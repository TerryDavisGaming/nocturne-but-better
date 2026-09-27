// The upload flow (DESIGN-HUB 2.6): start, parts (8 MiB, R2 multipart for more than one), complete (a
// guarded state machine that a retry can pick up), and abort. Uploads go live the moment they complete.

import { HubError, checkWrite, json, noContent, readJson } from "./http.js";
import { CLIENT_UPLOAD_ID, HEX64, PKG_ID, newPackageId, newUploadId, now, objectKey } from "./ids.js";
import { LIMITS, cleanLine, cleanText, indexText, titleKey } from "./names.js";
import { loadSettings } from "./settings.js";
import { requireUploader } from "./auth.js";
import { classifyDbError, isUniqueError, rateLimit, refuseReadOnly, uploadsVarClosed } from "./guard.js";
import { thumbProblem } from "./media.js";
import { checkPackage } from "./facts.js";
import { ZipProblem } from "./zipcheck.js";
import { dropStopped, dropUploadObject } from "./packages.js";
import { notify, uploadText } from "./notify.js";

export const PART_SIZE = 8 * 1024 * 1024;
export const IDLE_FIRST_PART = 15 * 60; // no part for 15 minutes after the start
export const IDLE_NEXT_PART = 60 * 60; // no new part for an hour
export const COMPLETE_RETRY = 60; // a complete that stopped can be picked up after 60 s
export const TRIES_PER_UPLOAD = 3; // starts a key may make a day, for each upload it may finish
const FEATURE = /^[a-z0-9][a-z0-9-]{0,31}$/;

function uploadsClosed(env, settings) {
  if (uploadsVarClosed(env) || !settings.on("uploads_open")) throw new HubError(503, "uploads_closed", "Uploads are closed right now. Downloads still work.");
}

export function isExpired(u, t) {
  if (u.state === "open") return u.last_part_at === null ? u.created_at < t - IDLE_FIRST_PART : u.last_part_at < t - IDLE_NEXT_PART;
  if (u.state === "completing") return u.completing_at < t - IDLE_NEXT_PART;
  return false;
}

/** SQL for "still idle", with ?3 = now - 15 minutes and ?4 = now - 1 hour. */
export const IDLE_WHERE =
  "((state = 'open' AND ((last_part_at IS NULL AND created_at < ?3) OR last_part_at < ?4)) OR (state = 'completing' AND completing_at < ?4))";

/**
 * Expires an idle upload: marked expired first (only while it's still idle), then the multipart upload
 * aborted and the object deleted. Leaving 'open' releases its reserved bytes.
 */
export async function expireUpload(env, u, t) {
  const r = await env.DB.prepare(`UPDATE uploads SET state = 'expired', meta = NULL, updated_at = ?2 WHERE id = ?1 AND ${IDLE_WHERE}`)
    .bind(u.id, t, t - IDLE_FIRST_PART, t - IDLE_NEXT_PART).run();
  if (r.meta.changes !== 1) return false;
  await env.DB.prepare("DELETE FROM upload_parts WHERE upload_id = ?1").bind(u.id).run();
  await dropUploadObject(env, u).catch(() => {});
  return true;
}

const uploadBody = (u) => ({
  uploadId: u.id, packageId: u.package_id, version: u.version, partSize: u.part_size, parts: u.parts,
  expiresAt: u.last_part_at ? u.last_part_at + IDLE_NEXT_PART : u.created_at + IDLE_FIRST_PART,
});

function compareVersions(a, b) {
  const x = String(a).split(".").map(Number), y = String(b).split(".").map(Number);
  for (let i = 0; i < 3; i++) if ((x[i] || 0) !== (y[i] || 0)) return (x[i] || 0) - (y[i] || 0);
  return 0;
}

function difficultyList(value, problems, where) {
  if (!Array.isArray(value) || value.length === 0 || value.length > 20) {
    problems.push(`${where} needs 1 to 20 difficulties`);
    return [];
  }
  const out = [];
  for (const d of value) {
    const name = cleanLine(d && d.name, LIMITS.difficulty);
    const level = d && d.level, notes = d && d.notes;
    if (!name || !Number.isInteger(level) || level < 0 || level > 99 || !Number.isInteger(notes) || notes < 0 || notes > 1000000) {
      problems.push(`${where} has a difficulty without a name, level (0-99) or note count`);
      return [];
    }
    out.push({ name, level, notes });
  }
  return out;
}

/** Checks the start body; returns the cleaned request. 400 with every problem, 413 when too big. */
export function checkStart(body, settings) {
  const problems = [];
  const kind = body.kind;
  if (kind !== "battle" && kind !== "charts") problems.push('kind must be "battle" or "charts"');
  if (typeof body.clientUploadId !== "string" || !CLIENT_UPLOAD_ID.test(body.clientUploadId)) problems.push("clientUploadId is missing");
  const packageId = body.packageId ?? null;
  if (packageId !== null && (typeof packageId !== "string" || !PKG_ID.test(packageId))) problems.push("packageId isn't a package id");
  const file = body.file || {};
  const max = settings.num("max_package_bytes");
  if (!Number.isInteger(file.size) || file.size < 22 + 30) problems.push("file.size is missing");
  else if (file.size > max) {
    throw new HubError(413, "too_big", `The package is ${Math.ceil(file.size / 1048576)} MB and the hub takes up to ${Math.floor(max / 1048576)} MB.`, { maxPackageBytes: max });
  }
  if (typeof file.sha256 !== "string" || !HEX64.test(file.sha256)) problems.push("file.sha256 must be 64 lower-case hex characters");
  if (typeof file.entriesSha256 !== "string" || !HEX64.test(file.entriesSha256)) problems.push("file.entriesSha256 must be 64 lower-case hex characters");
  const meta = body.meta && typeof body.meta === "object" && !Array.isArray(body.meta) ? body.meta : null;
  if (!meta) problems.push("meta is missing");
  const out = { kind, clientUploadId: body.clientUploadId, packageId, file, meta: {} };
  if (meta) {
    const description = meta.description === undefined || meta.description === null ? "" : cleanText(meta.description, LIMITS.description);
    if (description === null) problems.push("meta.description must be text");
    out.meta.description = description ?? "";
    if (kind === "battle") out.meta.difficulties = difficultyList(meta.difficulties, problems, "The battle");
    if (kind === "charts") {
      const maxSongs = settings.num("max_songs_per_pack");
      if (!Array.isArray(meta.songs) || meta.songs.length === 0 || meta.songs.length > maxSongs) problems.push(`meta.songs needs 1 to ${maxSongs} songs`);
      else {
        out.meta.songs = [];
        for (const s of meta.songs) {
          const song = cleanLine(s && s.song, LIMITS.song);
          if (!song) {
            problems.push("A song in meta.songs has no name");
            break;
          }
          out.meta.songs.push({ song, difficulties: difficultyList(s.difficulties, problems, song) });
        }
      }
    }
    if (meta.lengthSeconds !== undefined && meta.lengthSeconds !== null) {
      if (typeof meta.lengthSeconds !== "number" || !(meta.lengthSeconds > 0 && meta.lengthSeconds <= 7200)) problems.push("meta.lengthSeconds is out of range");
      else out.meta.lengthSeconds = Math.round(meta.lengthSeconds * 10) / 10;
    }
    if (meta.bpm !== undefined && meta.bpm !== null) {
      const b = meta.bpm;
      if (!Array.isArray(b) || b.length !== 2 || b.some((x) => typeof x !== "number" || !(x > 0 && x <= 2000)) || b[0] > b[1]) problems.push("meta.bpm must be [lowest, highest]");
      else out.meta.bpm = b.map((x) => Math.round(x * 100) / 100);
    }
    const requires = meta.requires ?? [];
    if (!Array.isArray(requires) || requires.length > 16 || requires.some((r) => typeof r !== "string" || !FEATURE.test(r))) problems.push("meta.requires must be a list of feature names");
    else out.meta.requires = [...new Set(requires)].sort();
  }
  if (body.thumb !== undefined && body.thumb !== null) {
    const why = thumbProblem(body.thumb);
    if (why) problems.push(why);
    else out.meta.thumb = body.thumb;
  }
  if (body.rightsConfirmed !== true) problems.push("the rules must be confirmed (rightsConfirmed)");
  if (typeof body.client !== "string" || !/^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}$/.test(body.client)) problems.push("client must be the mod's version");
  out.client = body.client;
  if (problems.length) throw new HubError(400, "bad_request", problems[0], { problems });
  return out;
}

export async function startUpload(env, request) {
  checkWrite(request, "json");
  refuseReadOnly(env);
  const { uploader } = await requireUploader(env, request);
  await rateLimit(env, "RL_WRITE", "key:" + uploader.id);
  const body = await readJson(request, 32 * 1024);
  const db = env.DB;
  const settings = await loadSettings(db);
  const t = now();
  uploadsClosed(env, settings);
  const gate = settings.num("close_uploads_key_days");
  if (gate > 0 && uploader.created_at > t - gate * 86400) throw new HubError(403, "key_too_new", "Uploads from new hub keys are paused right now. Try again in a few days.");
  const req = checkStart(body, settings);
  if (compareVersions(req.client, settings.str("min_client")) < 0) throw new HubError(403, "client_too_old", "This version of the mod is too old for the hub. Update the mod.");

  const existing = await db.prepare("SELECT * FROM uploads WHERE uploader_id = ?1 AND client_upload_id = ?2").bind(uploader.id, req.clientUploadId).first();
  if (existing) {
    if (existing.state === "open" && !isExpired(existing, t)) return json(uploadBody(existing), 200);
    throw new HubError(409, "upload_closed", "That upload is already finished or stopped.");
  }
  const open = await db.prepare("SELECT * FROM uploads WHERE uploader_id = ?1 AND state IN ('open','completing')").bind(uploader.id).first();
  if (open) {
    if (!isExpired(open, t) || !(await expireUpload(env, open, t))) {
      throw new HubError(409, "upload_in_progress", "Another upload with this hub key is still running.");
    }
  }

  const reserved = await db.prepare("SELECT coalesce(sum(received_bytes), 0) AS n FROM uploads WHERE state = 'open'").first("n");
  if (settings.num("storage_used") + reserved + req.file.size > settings.num("storage_cap_bytes") || settings.num("d1_size_bytes") >= settings.num("d1_close_bytes")) {
    throw new HubError(503, "storage_full", "Uploads are closed right now: the hub is full. Downloads still work.");
  }

  const day = t - 86400;
  const probation = uploader.created_at > t - settings.num("probation_hours") * 3600;
  const perDay = settings.num(probation ? "probation_uploads_day" : "uploads_per_key_day");
  const bytesPerDay = settings.num(probation ? "probation_bytes_day" : "bytes_per_key_day");
  const triesPerDay = perDay * TRIES_PER_UPLOAD;
  const mine = await keyDay(db, uploader.id, day);
  if (mine.n + 1 > perDay || mine.bytes + req.file.size > bytesPerDay) {
    const retryAfter = Math.max(60, (mine.first ?? t) + 86400 - t);
    throw new HubError(429, "daily_limit", "You've uploaded as much as the hub allows today. Try again tomorrow.", {
      retryAfter, probation, uploadsPerDay: perDay, bytesPerDay,
    });
  }
  // Every start counts here, stopped and refused ones too, so start-then-stop can't go on for ever.
  if (mine.tries + 1 > triesPerDay) {
    const retryAfter = Math.max(60, (mine.firstTry ?? t) + 86400 - t);
    throw new HubError(429, "daily_limit", "You've started as many uploads as the hub allows today. Try again tomorrow.", {
      retryAfter, probation, uploadsPerDay: perDay, bytesPerDay, attemptsPerDay: triesPerDay,
    });
  }
  // The whole-hub caps are for keys that haven't earned trust yet (DESIGN-HUB 2.9), so a flood from new keys
  // can't lock established uploaders out.
  if (uploader.trusted_at === null) {
    const attempts = await db.prepare("SELECT count(*) AS n FROM uploads WHERE created_at > ?1").bind(day).first("n");
    if (attempts >= settings.num("attempts_global_day")) throw new HubError(429, "daily_limit", "The hub has taken all the uploads it can today. Try again tomorrow.", { retryAfter: 3600 });
    const completed = await db.prepare(
      "SELECT count(*) AS n FROM uploads u JOIN uploaders k ON k.id = u.uploader_id WHERE u.state = 'live' AND u.updated_at > ?1 AND k.trusted_at IS NULL",
    ).bind(day).first("n");
    if (completed >= settings.num("uploads_global_day")) throw new HubError(429, "daily_limit", "The hub has taken all the uploads it can today. Try again tomorrow.", { retryAfter: 3600 });
  }

  let packageId, version;
  if (req.packageId) {
    const pkg = await db.prepare("SELECT id, uploader_id, kind, status, version FROM packages WHERE id = ?1").bind(req.packageId).first();
    if (!pkg) throw new HubError(404, "no_package", "There's no such entry to update.");
    if (pkg.uploader_id !== uploader.id) throw new HubError(403, "not_yours", "This entry was uploaded with another hub key.");
    if (pkg.kind !== req.kind) throw new HubError(400, "bad_request", "A new version must be the same kind of package.");
    if (pkg.status !== "live") throw new HubError(409, "not_live", "Only a live entry can get a new version.");
    const pending = await db.prepare("SELECT * FROM uploads WHERE package_id = ?1 AND state IN ('open','completing')").bind(pkg.id).first();
    if (pending) {
      if (!isExpired(pending, t) || !(await expireUpload(env, pending, t))) {
        throw new HubError(409, "upload_in_progress", "A new version of this entry is already being uploaded.");
      }
    }
    packageId = pkg.id;
    version = pkg.version + 1;
  } else {
    const live = await db.prepare("SELECT count(*) AS n FROM packages WHERE uploader_id = ?1 AND status = 'live'").bind(uploader.id).first("n");
    if (live >= settings.num("live_per_key")) throw new HubError(409, "too_many_live", `A hub key can have ${settings.num("live_per_key")} live entries. Delete one first.`);
    packageId = newPackageId();
    version = 1;
  }

  // The same files already on the hub. The fingerprint declared here must match the file at complete, so this
  // early answer only saves sending the parts. An entry's own new version may keep its files.
  const dup = await db.prepare("SELECT id FROM packages WHERE fingerprint = ?1 AND status IN ('live','hidden') AND id != ?2 LIMIT 1")
    .bind(req.file.entriesSha256, packageId).first();
  if (dup) throw new HubError(409, "duplicate", "The same files are already on the hub.", { packageId: dup.id });

  const id = newUploadId();
  const key = objectKey(packageId, version, id);
  const parts = Math.ceil(req.file.size / PART_SIZE);
  let r2UploadId = null;
  if (parts > 1) r2UploadId = (await env.FILES.createMultipartUpload(key)).uploadId;
  const meta = JSON.stringify(req.meta);
  const row = {
    id, uploader_id: uploader.id, client_upload_id: req.clientUploadId, package_id: packageId,
    version, is_update: req.packageId ? 1 : 0, kind: req.kind, r2_key: key, r2_upload_id: r2UploadId, size: req.file.size,
    sha256: req.file.sha256, entries_sha256: req.file.entriesSha256, part_size: PART_SIZE, parts, meta, state: "open", created_at: t,
    last_part_at: null, updated_at: t,
  };
  try {
    await db.prepare(
      "INSERT INTO uploads (id, uploader_id, client_upload_id, package_id, version, is_update, kind, r2_key, r2_upload_id, size, sha256, " +
        "entries_sha256, part_size, parts, meta, state, created_at, updated_at) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, 'open', ?16, ?16)",
    ).bind(row.id, row.uploader_id, row.client_upload_id, packageId, version, row.is_update, row.kind, key, r2UploadId, row.size, row.sha256,
      row.entries_sha256, PART_SIZE, parts, meta, t).run();
  } catch (e) {
    if (r2UploadId) await dropUploadObject(env, { r2_key: key, r2_upload_id: r2UploadId }).catch(() => {});
    if (isUniqueError(e)) throw new HubError(409, "upload_in_progress", "Another upload with this hub key is still running.");
    throw e;
  }
  return json(uploadBody(row), 201);
}

/** A key's uploads of the last day: the running or finished ones (count, bytes) and every start (tries). */
export async function keyDay(db, uploaderId, since) {
  const r = await db.prepare(
    "SELECT count(*) AS tries, min(created_at) AS firstTry, " +
      "coalesce(sum(CASE WHEN state IN ('open','completing','live') THEN 1 ELSE 0 END), 0) AS n, " +
      "coalesce(sum(CASE WHEN state IN ('open','completing','live') THEN size ELSE 0 END), 0) AS bytes, " +
      "min(CASE WHEN state IN ('open','completing','live') THEN created_at END) AS first " +
      "FROM uploads WHERE uploader_id = ?1 AND created_at > ?2",
  ).bind(uploaderId, since).first();
  return { tries: r.tries, firstTry: r.firstTry, n: r.n, bytes: r.bytes, first: r.first };
}

export async function putPart(env, request, uploadId, n) {
  checkWrite(request, "octet");
  refuseReadOnly(env);
  const { uploader } = await requireUploader(env, request);
  await rateLimit(env, "RL_WRITE", "key:" + uploader.id);
  const db = env.DB;
  const t = now();
  const u = await db.prepare("SELECT * FROM uploads WHERE id = ?1 AND uploader_id = ?2").bind(uploadId, uploader.id).first();
  if (!u) throw new HubError(404, "not_found", "There's no such upload.");
  if (u.state !== "open") throw new HubError(409, "upload_closed", "That upload is already finished or stopped.");
  if (isExpired(u, t)) {
    await expireUpload(env, u, t);
    throw new HubError(409, "upload_closed", "That upload waited too long and was stopped. Start it again.");
  }
  const settings = await loadSettings(db);
  uploadsClosed(env, settings);
  if (!(n >= 1 && n <= u.parts)) throw new HubError(400, "bad_part", `Part numbers go from 1 to ${u.parts}.`);
  const expected = n < u.parts ? u.part_size : u.size - u.part_size * (u.parts - 1);
  const declared = request.headers.get("Content-Length");
  if (declared === null) throw new HubError(411, "length_required", "A part needs its Content-Length.");
  if (!/^[0-9]{1,12}$/.test(declared)) throw new HubError(400, "bad_part", "Bad Content-Length.");
  const length = Number(declared);
  if (length > expected) throw new HubError(413, "too_big", `Part ${n} must be ${expected} bytes.`);
  if (length < expected || !request.body) throw new HubError(400, "bad_part", `Part ${n} must be ${expected} bytes.`);

  const prior = (await db.prepare("SELECT bytes FROM upload_parts WHERE upload_id = ?1 AND n = ?2").bind(u.id, n).first("bytes")) ?? 0;
  const reserved = await db.prepare("SELECT coalesce(sum(received_bytes), 0) AS n FROM uploads WHERE state = 'open'").first("n");
  if (settings.num("storage_used") + reserved - prior + length > settings.num("storage_cap_bytes")) {
    throw new HubError(503, "storage_full", "Uploads are closed right now: the hub is full. Downloads still work.");
  }
  let etag;
  try {
    if (u.parts === 1) {
      const obj = await env.FILES.put(u.r2_key, request.body, { sha256: u.sha256 });
      etag = obj.etag;
    } else {
      const part = await env.FILES.resumeMultipartUpload(u.r2_key, u.r2_upload_id).uploadPart(n, request.body);
      etag = part.etag;
    }
  } catch (e) {
    if (u.parts === 1 && /(checksum|sha-?256|digest|did not match)/i.test(String(e && e.message))) {
      throw new HubError(400, "bad_part", "The file doesn't match the SHA-256 the upload started with.");
    }
    // A multipart upload that was stopped while this part arrived is gone in R2 too.
    const state = await db.prepare("SELECT state FROM uploads WHERE id = ?1").bind(u.id).first("state");
    if (state !== "open") throw new HubError(409, "upload_closed", "That upload was stopped while this part arrived.");
    throw Object.assign(new Error("r2"), { code: "r2_part_failed" });
  }
  // The part counts only while the upload is still open: it may have been stopped (aborted, expired, refused)
  // while the bytes were arriving. Then the object just written belongs to nothing, and it goes again.
  const [, updated] = await db.batch([
    db.prepare("INSERT OR REPLACE INTO upload_parts (upload_id, n, etag, bytes) SELECT ?1, ?2, ?3, ?4 WHERE EXISTS (SELECT 1 FROM uploads WHERE id = ?1 AND state = 'open')")
      .bind(u.id, n, etag, length),
    db.prepare("UPDATE uploads SET received_bytes = (SELECT coalesce(sum(bytes), 0) FROM upload_parts WHERE upload_id = ?1), last_part_at = ?2, updated_at = ?2 WHERE id = ?1 AND state = 'open'")
      .bind(u.id, t),
  ]);
  if (updated.meta.changes !== 1) {
    await dropStopped(env, [u]);
    throw new HubError(409, "upload_closed", "That upload was stopped while this part arrived.");
  }
  return json({ n, etag });
}

export async function abortUpload(env, request, uploadId) {
  checkWrite(request, null);
  const { uploader } = await requireUploader(env, request, { allowBanned: true });
  await rateLimit(env, "RL_WRITE", "key:" + uploader.id);
  const t = now();
  const u = await env.DB.prepare("SELECT * FROM uploads WHERE id = ?1 AND uploader_id = ?2").bind(uploadId, uploader.id).first();
  if (!u) throw new HubError(404, "not_found", "There's no such upload.");
  if (u.state === "completing" && u.completing_at >= t - COMPLETE_RETRY) throw new HubError(409, "busy", "That upload is being finished right now.", { retryAfter: COMPLETE_RETRY });
  if (u.state === "open" || u.state === "completing") {
    const [r] = await env.DB.batch([
      env.DB.prepare("UPDATE uploads SET state = 'aborted', meta = NULL, updated_at = ?2 WHERE id = ?1 AND (state = 'open' OR (state = 'completing' AND completing_at < ?3))")
        .bind(u.id, t, t - COMPLETE_RETRY),
      env.DB.prepare("DELETE FROM upload_parts WHERE upload_id = ?1 AND (SELECT state FROM uploads WHERE id = ?1) = 'aborted'").bind(u.id),
    ]);
    if (r.meta.changes === 1) await dropUploadObject(env, u).catch(() => {});
  }
  return noContent();
}

// ---- complete -------------------------------------------------------------------------------------------

async function refuse(env, u, status, code, message, extra = {}) {
  const body = { error: code, message, ...extra };
  const [r] = await env.DB.batch([
    env.DB.prepare("UPDATE uploads SET state = 'refused', meta = NULL, result = ?2, updated_at = ?3 WHERE id = ?1 AND state = 'completing'")
      .bind(u.id, JSON.stringify({ status, body }), now()),
    env.DB.prepare("DELETE FROM upload_parts WHERE upload_id = ?1 AND (SELECT state FROM uploads WHERE id = ?1) = 'refused'").bind(u.id),
  ]);
  if (r.meta.changes !== 1) {
    // Another complete of the same upload finished first: its answer stands, and its file stays.
    const cur = await env.DB.prepare("SELECT state, result FROM uploads WHERE id = ?1").bind(u.id).first();
    if (cur && cur.result) {
      const stored = JSON.parse(cur.result);
      return json(stored.body, stored.status);
    }
    throw new HubError(409, "upload_closed", "That upload is already finished or stopped.");
  }
  await dropUploadObject(env, u).catch(() => {});
  return json(body, status);
}

const refused = (env, u, problems) =>
  refuse(env, u, 422, "package_refused", "The hub can't take this package: " + problems[0], { problems });

export async function completeUpload(env, request, ctx, uploadId) {
  checkWrite(request, "json");
  refuseReadOnly(env);
  const { uploader } = await requireUploader(env, request);
  await rateLimit(env, "RL_WRITE", "key:" + uploader.id);
  const db = env.DB;
  const settings = await loadSettings(db);
  uploadsClosed(env, settings);
  const t = now();

  // 1. Claim it. A complete that stopped more than 60 s ago can be picked up again.
  const claim = await db.prepare(
    "UPDATE uploads SET state = 'completing', completing_at = ?3, updated_at = ?3 WHERE id = ?1 AND uploader_id = ?2 " +
      "AND (state = 'open' OR (state = 'completing' AND completing_at < ?4))",
  ).bind(uploadId, uploader.id, t, t - COMPLETE_RETRY).run();
  if (claim.meta.changes !== 1) {
    const prev = await db.prepare("SELECT state, result FROM uploads WHERE id = ?1 AND uploader_id = ?2").bind(uploadId, uploader.id).first();
    if (!prev) throw new HubError(404, "not_found", "There's no such upload.");
    if ((prev.state === "live" || prev.state === "refused") && prev.result) {
      const stored = JSON.parse(prev.result);
      return json(stored.body, stored.status);
    }
    if (prev.state === "completing") throw new HubError(409, "busy", "That upload is being finished right now. Try again in a minute.", { retryAfter: COMPLETE_RETRY });
    throw new HubError(409, "upload_closed", "That upload is already finished or stopped.");
  }
  const u = await db.prepare("SELECT * FROM uploads WHERE id = ?1").bind(uploadId).first();

  // 2. Every part recorded.
  const { results: parts } = await db.prepare("SELECT n, etag, bytes FROM upload_parts WHERE upload_id = ?1 ORDER BY n").bind(u.id).all();
  const have = new Set(parts.map((p) => p.n));
  const missing = [];
  for (let i = 1; i <= u.parts && missing.length < 100; i++) if (!have.has(i)) missing.push(i);
  if (missing.length) {
    await db.prepare("UPDATE uploads SET state = 'open', updated_at = ?2 WHERE id = ?1 AND state = 'completing'").bind(u.id, t).run();
    throw new HubError(409, "missing_parts", `${missing.length} part(s) haven't arrived yet.`, { missing });
  }

  // 3. The object: head first, so a retry after R2 already joined the parts carries on.
  let head = await env.FILES.head(u.r2_key);
  if (!head || head.size !== u.size) {
    if (u.parts > 1) {
      try {
        await env.FILES.resumeMultipartUpload(u.r2_key, u.r2_upload_id).complete(parts.map((p) => ({ partNumber: p.n, etag: p.etag })));
      } catch {
        return refused(env, u, ["The parts didn't join up into one file. Upload it again."]);
      }
      head = await env.FILES.head(u.r2_key);
    }
    if (!head) {
      await db.batch([
        db.prepare("UPDATE uploads SET state = 'open', updated_at = ?2 WHERE id = ?1 AND state = 'completing'").bind(u.id, t),
        db.prepare("DELETE FROM upload_parts WHERE upload_id = ?1").bind(u.id),
      ]);
      throw new HubError(409, "missing_parts", "The file hasn't arrived yet.", { missing: [1] });
    }
    if (head.size !== u.size) return refused(env, u, ["The file isn't the size the upload started with."]);
  }

  // 4-5. The zip check and the facts, from the file itself.
  let facts;
  try {
    facts = await checkPackage(env.FILES, u.r2_key, u.size, u.kind, {
      maxEntries: settings.num("max_entries"), maxUnpacked: settings.num("max_unpacked_bytes"), maxSongs: settings.num("max_songs_per_pack"),
    });
  } catch (e) {
    if (e instanceof ZipProblem) return refused(env, u, e.problems);
    throw e;
  }
  if (facts.fingerprint !== u.entries_sha256) return refused(env, u, ["The zip's file list doesn't match the one the upload started with (entriesSha256)."]);
  const meta = JSON.parse(u.meta || "{}");
  let songs = null, difficulties = meta.difficulties || [];
  if (u.kind === "charts") {
    const declared = (meta.songs || []).map((s) => s.song);
    const same = declared.length === facts.songs.length && facts.songs.every((s) => declared.includes(s));
    if (!same) return refused(env, u, ["The difficulty list doesn't match the pack's songs."]);
    songs = facts.songs.map((s) => meta.songs.find((m) => m.song === s));
    difficulties = songs.flatMap((s) => s.difficulties);
  }

  // 6. The rules on the real facts.
  let pkg = null;
  if (u.is_update) {
    pkg = await db.prepare("SELECT * FROM packages WHERE id = ?1").bind(u.package_id).first();
    if (!pkg || pkg.uploader_id !== uploader.id || pkg.status !== "live" || pkg.version !== u.version - 1) {
      return refuse(env, u, 409, "not_live", "The entry changed while this version was uploading (it was hidden, removed or updated).");
    }
    if (u.kind === "battle" && pkg.battle_id !== facts.battleId) {
      return refuse(env, u, 409, "battle_changed", "A new version must keep its battle id. Upload it as a new entry instead.");
    }
  } else if (u.kind === "battle") {
    const taken = await db.prepare("SELECT id, uploader_id FROM packages WHERE battle_id = ?1 AND status IN ('live','hidden')").bind(facts.battleId).first();
    if (taken && taken.uploader_id === uploader.id) {
      return refuse(env, u, 409, "battle_yours", "You already uploaded this battle. Upload it as a new version of that entry.", { packageId: taken.id });
    }
    if (taken) {
      return refuse(env, u, 409, "battle_taken",
        "Another entry already has this battle's id. If this is your battle, report that entry with \"This is mine\" as the note; " +
          "if you made your own version, use \"Make it a separate battle\" in the creator first.", { packageId: taken.id });
    }
    const blocked = await db.prepare("SELECT 1 AS b FROM battle_blocks WHERE battle_id = ?1 AND uploader_id = ?2").bind(facts.battleId, uploader.id).first();
    if (blocked) return refuse(env, u, 409, "removed_before", "This battle was removed from the hub for copyright before.");
  }
  // An entry's own new version may keep the same files (to change its description or picture).
  const dup = await db.prepare("SELECT id FROM packages WHERE fingerprint = ?1 AND status IN ('live','hidden') AND id != ?2 LIMIT 1")
    .bind(facts.fingerprint, u.package_id).first();
  if (dup) return refuse(env, u, 409, "duplicate", "The same files are already on the hub.", { packageId: dup.id });

  // 7-8. Accepted: one batch (a transaction).
  const thumb = meta.thumb || null;
  let pictureState = "shown", pictureDue = null;
  const delay = settings.num("picture_delay_hours");
  if (thumb) {
    const old = pkg ? await db.prepare("SELECT b64 FROM thumbs WHERE package_id = ?1").bind(pkg.id).first("b64") : null;
    if (pkg && old === thumb) {
      pictureState = pkg.picture_state;
      pictureDue = pkg.picture_due;
    } else if ((uploader.trusted_at === null || uploader.picture_refused_at !== null) && delay > 0) {
      // Only a trusted key whose pictures the owner never refused gets its new ones shown at once.
      pictureState = "waiting";
      pictureDue = t + delay * 3600;
    }
  }
  const f = {
    id: u.package_id, version: u.version, title: facts.title, title_key: titleKey(facts.title), artist: facts.artist, author: facts.author,
    description: meta.description || "", lanes: facts.lanes, difficulties: JSON.stringify(difficulties), songs: songs ? JSON.stringify(songs) : null,
    battle_id: facts.battleId, flags: JSON.stringify(facts.flags), source: facts.source ? JSON.stringify(facts.source) : null,
    contents: JSON.stringify(facts.contents), format: facts.format, requires: JSON.stringify(meta.requires || []),
    length_s: meta.lengthSeconds ?? null, bpm_min: meta.bpm ? meta.bpm[0] : null, bpm_max: meta.bpm ? meta.bpm[1] : null,
    file_size: u.size, file_sha256: u.sha256, fingerprint: facts.fingerprint, entries: facts.entries, r2_key: u.r2_key,
  };
  const result = { packageId: u.package_id, version: u.version, status: "live" };
  const stmts = [
    db.prepare(
      "SELECT json((SELECT CASE WHEN (SELECT count(*) FROM uploads WHERE id = ?1 AND uploader_id = ?2 AND state = 'completing') = 1 " +
        "AND (?3 = 0 OR (SELECT count(*) FROM packages WHERE id = ?4 AND uploader_id = ?2 AND status = 'live' AND version = ?5) = 1) " +
        "THEN '1' ELSE 'guard: changed' END))",
    ).bind(u.id, uploader.id, u.is_update, u.package_id, u.version - 1),
  ];
  const common = [f.version, f.title, f.title_key, f.artist, f.author, f.description, f.lanes, f.difficulties, f.songs, f.flags, f.source, f.contents,
    f.format, f.requires, f.length_s, f.bpm_min, f.bpm_max, f.file_size, f.file_sha256, f.fingerprint, f.entries, pictureState, pictureDue, u.size, t, f.r2_key];
  if (pkg) {
    stmts.push(db.prepare(
      "UPDATE packages SET version = ?1, title = ?2, title_key = ?3, artist = ?4, author = ?5, description = ?6, lanes = ?7, difficulties = ?8, songs = ?9, " +
        "flags = ?10, source = ?11, contents = ?12, format = ?13, requires = ?14, length_s = ?15, bpm_min = ?16, bpm_max = ?17, file_size = ?18, " +
        "file_sha256 = ?19, fingerprint = ?20, entries = ?21, picture_state = ?22, picture_due = ?23, bytes_stored = ?24, updated_at = ?25, r2_key = ?26 WHERE id = ?27",
    ).bind(...common, f.id));
  } else {
    stmts.push(db.prepare(
      "INSERT INTO packages (version, title, title_key, artist, author, description, lanes, difficulties, songs, flags, source, contents, format, requires, " +
        "length_s, bpm_min, bpm_max, file_size, file_sha256, fingerprint, entries, picture_state, picture_due, bytes_stored, updated_at, r2_key, " +
        "id, kind, uploader_id, status, battle_id, created_at) " +
        "VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19, ?20, ?21, ?22, ?23, ?24, ?25, ?26, ?27, ?28, ?29, 'live', ?30, ?25)",
    ).bind(...common, f.id, u.kind, uploader.id, f.battle_id));
  }
  const songNames = songs ? songs.map((s) => s.song).join(" ") : "";
  stmts.push(
    db.prepare("DELETE FROM packages_fts WHERE rowid = (SELECT seq FROM packages WHERE id = ?1)").bind(f.id),
    db.prepare("INSERT INTO packages_fts (rowid, title, artist, author, songs, description) VALUES ((SELECT seq FROM packages WHERE id = ?1), ?2, ?3, ?4, ?5, ?6)")
      .bind(f.id, indexText(f.title), indexText(f.artist), indexText(f.author), indexText(songNames), indexText(f.description)),
    thumb
      ? db.prepare("INSERT OR REPLACE INTO thumbs (package_id, version, b64) VALUES (?1, ?2, ?3)").bind(f.id, f.version, thumb)
      : db.prepare("DELETE FROM thumbs WHERE package_id = ?1").bind(f.id),
    db.prepare("UPDATE settings SET v = CAST(CAST(v AS INTEGER) + CAST(?1 AS INTEGER) AS TEXT) WHERE k = 'storage_used'").bind(u.size),
  );
  if (pkg) {
    stmts.push(db.prepare("INSERT OR REPLACE INTO trash (r2_key, bytes, delete_after, why) VALUES (?1, ?2, ?3, 'old version')")
      .bind(pkg.r2_key, pkg.bytes_stored, t + 86400));
  }
  stmts.push(
    db.prepare("UPDATE uploads SET state = 'live', result = ?2, meta = NULL, updated_at = ?3 WHERE id = ?1").bind(u.id, JSON.stringify({ status: 200, body: result }), t),
    db.prepare("DELETE FROM upload_parts WHERE upload_id = ?1").bind(u.id),
    db.prepare("INSERT INTO audit (at, actor, action, package_id, uploader_id, detail) VALUES (?1, 'uploader', ?2, ?3, ?4, ?5)")
      .bind(t, pkg ? "new-version" : "publish", f.id, uploader.id, `v${f.version}`),
  );
  let results;
  try {
    results = await db.batch(stmts);
  } catch (e) {
    if (classifyDbError(e)) throw e;
    // A complete picked up after 60 s may overlap a slow first one that has just finished.
    const done = await db.prepare("SELECT state, result FROM uploads WHERE id = ?1").bind(u.id).first();
    if (done && done.state === "live" && done.result) {
      const stored = JSON.parse(done.result);
      return json(stored.body, stored.status);
    }
    if (isUniqueError(e, "battle_id")) {
      const other = await db.prepare("SELECT id FROM packages WHERE battle_id = ?1 AND status IN ('live','hidden')").bind(facts.battleId).first("id");
      return refuse(env, u, 409, "battle_taken", "Another entry already has this battle's id.", { packageId: other });
    }
    if (/malformed JSON|guard/i.test(String(e && e.message))) {
      const state = await db.prepare("SELECT state FROM uploads WHERE id = ?1").bind(u.id).first("state");
      if (state === "completing") return refuse(env, u, 409, "not_live", "The entry changed while this version was uploading.");
      throw new HubError(409, "upload_closed", "That upload is already finished or stopped.");
    }
    throw e;
  }
  await noteDbSize(env, settings, results);
  notify(env, ctx, uploadText({ ...f, kind: u.kind }, uploader.name, !pkg), thumb);
  return json(result, 200);
}

/** Saves D1's size from a write's meta.size_after, at most once a minute; uploads close at d1_close_bytes. */
export async function noteDbSize(env, settings, results) {
  const last = results && results[results.length - 1];
  const size = last && last.meta ? last.meta.size_after : null;
  if (!size || now() - settings.num("d1_size_at") < 60) return;
  await env.DB.batch([
    env.DB.prepare("UPDATE settings SET v = ?1 WHERE k = 'd1_size_bytes'").bind(String(size)),
    env.DB.prepare("UPDATE settings SET v = ?1 WHERE k = 'd1_size_at'").bind(String(now())),
  ]);
}
