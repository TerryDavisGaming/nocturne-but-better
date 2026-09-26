// The owner's API under /v1/admin/ (DESIGN-HUB 2.3, 2.11). Every route needs ADMIN_KEY; every action writes
// an audit row; actions that change what players see queue a cache purge and try it at once.

import { HubError, NO_STORE, checkWrite, json, readJson } from "./http.js";
import { BATTLE_ID, PKG_ID, UPLOADER_ID, fileKey, now, quarantineKey } from "./ids.js";
import { LIMITS, cleanText, indexText } from "./names.js";
import { DEFAULTS, EDITABLE, READ_ONLY_KEYS, checkSettingChanges, loadSettings, upsertSetting } from "./settings.js";
import { blockedIds, countsOn, isUniqueError, readOnly, uploadsVarClosed } from "./guard.js";
import { listTags, queueStatement, runPurges } from "./purge.js";
import { dropUploadObject, fileHeaders } from "./packages.js";
import { REASONS } from "./reports.js";
import { tagOf } from "./ids.js";

export const REMOVE_REASONS = ["copyright", "offensive", "malicious", "spam", "rules", "other"];
export const BULK_MAX = 30;
const DAY = 86400;

const audit = (db, action, packageId, uploaderId, detail) =>
  db.prepare("INSERT INTO audit (at, actor, action, package_id, uploader_id, detail) VALUES (?1, 'owner', ?2, ?3, ?4, ?5)")
    .bind(now(), action, packageId ?? null, uploaderId ?? null, detail ?? null);

const note = (v) => (v === undefined || v === null ? "" : cleanText(String(v), LIMITS.note) ?? "");

async function afterChange(env, ctx) {
  const purge = await runPurges(env, ctx);
  return purge.failed ? "pending" : purge.done ? "done" : "none";
}

async function openUploadsFor(db, ids) {
  const { results } = await db.prepare("SELECT id, r2_key, r2_upload_id FROM uploads WHERE package_id IN (SELECT value FROM json_each(?1)) AND state IN ('open','completing')")
    .bind(JSON.stringify(ids)).all();
  return results;
}

// ---- package actions (each works on a list, so bulk actions stay within D1's per-call query limit) -----

export async function hidePackages(env, ctx, ids, text) {
  const db = env.DB;
  const list = JSON.stringify(ids);
  const t = now();
  const [r] = await db.batch([
    db.prepare("UPDATE packages SET status = 'hidden' WHERE id IN (SELECT value FROM json_each(?1)) AND status = 'live'").bind(list),
    db.prepare("INSERT INTO audit (at, actor, action, package_id, detail) SELECT ?2, 'owner', 'hide', value, ?3 FROM json_each(?1)").bind(list, t, text),
    queueStatement(db, listTags(ids)),
  ]);
  return { changed: r.meta.changes, purge: await afterChange(env, ctx) };
}

export async function removePackages(env, ctx, ids, reason, text, strike) {
  const db = env.DB;
  const settings = await loadSettings(db);
  const list = JSON.stringify(ids);
  const t = now();
  const keep = reason === "copyright" ? 30 * DAY : DAY;
  const { results: rows } = await db.prepare(
    "SELECT id, seq, uploader_id, battle_id FROM packages WHERE id IN (SELECT value FROM json_each(?1)) AND status IN ('live','hidden')",
  ).bind(list).all();
  if (rows.length === 0) return { changed: 0, purge: "none" };
  const live = JSON.stringify(rows.map((r) => r.id));
  const uploads = await openUploadsFor(db, rows.map((r) => r.id));
  const stmts = [
    db.prepare("INSERT OR REPLACE INTO trash (r2_key, bytes, delete_after, why) SELECT 'pkg/' || id || '/' || version || '/package', bytes_stored, ?2, ?3 " +
      "FROM packages WHERE id IN (SELECT value FROM json_each(?1))").bind(live, t + keep, "removed: " + reason),
    db.prepare("UPDATE packages SET status = 'removed', removed_reason = ?2, removed_note = ?3, removed_at = ?4 WHERE id IN (SELECT value FROM json_each(?1))")
      .bind(live, reason, text, t),
    db.prepare("DELETE FROM packages_fts WHERE rowid IN (SELECT value FROM json_each(?1))").bind(JSON.stringify(rows.map((r) => r.seq))),
    db.prepare("UPDATE uploads SET state = 'aborted', meta = NULL, updated_at = ?2 WHERE package_id IN (SELECT value FROM json_each(?1)) AND state IN ('open','completing')")
      .bind(live, t),
    db.prepare("INSERT INTO audit (at, actor, action, package_id, detail) SELECT ?2, 'owner', 'remove', value, ?3 FROM json_each(?1)")
      .bind(live, t, JSON.stringify({ reason, note: text, strike: Boolean(strike) })),
  ];
  if (reason === "copyright") {
    stmts.push(db.prepare("INSERT OR IGNORE INTO battle_blocks (battle_id, uploader_id, package_id, at) SELECT battle_id, uploader_id, id, ?2 " +
      "FROM packages WHERE id IN (SELECT value FROM json_each(?1)) AND battle_id IS NOT NULL").bind(live, t));
  }
  if (strike) {
    stmts.push(
      db.prepare("UPDATE uploaders SET strikes = strikes + (SELECT count(*) FROM packages p WHERE p.uploader_id = uploaders.id AND p.id IN " +
        "(SELECT value FROM json_each(?1))), trusted_at = NULL WHERE id IN (SELECT uploader_id FROM packages WHERE id IN (SELECT value FROM json_each(?1)))").bind(live),
      db.prepare("UPDATE uploaders SET status = 'banned' WHERE status = 'ok' AND strikes >= ?2 AND id IN " +
        "(SELECT uploader_id FROM packages WHERE id IN (SELECT value FROM json_each(?1)))").bind(live, settings.num("strikes_to_ban")),
    );
  }
  stmts.push(queueStatement(db, listTags(rows.map((r) => r.id))));
  await db.batch(stmts);
  for (const u of uploads) await dropUploadObject(env, u).catch(() => {});
  return { changed: rows.length, purge: await afterChange(env, ctx) };
}

async function moveObject(env, from, to) {
  const obj = await env.FILES.get(from);
  if (!obj) return false;
  await env.FILES.put(to, obj.body);
  await env.FILES.delete(from);
  return true;
}

export async function quarantinePackage(env, ctx, id, text) {
  const db = env.DB;
  const row = await db.prepare("SELECT id, seq, status, version FROM packages WHERE id = ?1").bind(id).first();
  if (!row) throw new HubError(404, "not_found", "There's no such entry.");
  if (row.status === "quarantined") return { changed: 0, purge: "none" };
  const src = fileKey(id, row.version), dst = quarantineKey(id, row.version);
  const moved = await moveObject(env, src, dst);
  const t = now();
  const uploads = await openUploadsFor(db, [id]);
  await db.batch([
    db.prepare("UPDATE packages SET status = 'quarantined', removed_reason = 'rules', removed_note = NULL, removed_at = ?2 WHERE id = ?1").bind(id, t),
    db.prepare("DELETE FROM packages_fts WHERE rowid = ?1").bind(row.seq),
    db.prepare("DELETE FROM trash WHERE r2_key = ?1").bind(src),
    db.prepare("UPDATE uploads SET state = 'aborted', meta = NULL, updated_at = ?2 WHERE package_id = ?1 AND state IN ('open','completing')").bind(id, t),
    audit(db, "quarantine", id, null, JSON.stringify({ note: text, moved })),
    queueStatement(db, listTags([id])),
  ]);
  for (const u of uploads) await dropUploadObject(env, u).catch(() => {});
  return { changed: 1, moved, purge: await afterChange(env, ctx) };
}

export async function restorePackage(env, ctx, id) {
  const db = env.DB;
  const row = await db.prepare("SELECT * FROM packages WHERE id = ?1").bind(id).first();
  if (!row) throw new HubError(404, "not_found", "There's no such entry.");
  if (row.status === "live") return { changed: 0, purge: "none" };
  const key = fileKey(id, row.version);
  if (row.status === "quarantined") {
    if (!(await moveObject(env, quarantineKey(id, row.version), key))) throw new HubError(409, "files_gone", "The entry's file is gone.");
  } else if (row.status !== "hidden" && !(await env.FILES.head(key))) {
    throw new HubError(409, "files_gone", "The entry's file is gone (its time in the trash ran out).");
  }
  const t = now();
  const stmts = [
    db.prepare("UPDATE packages SET status = 'live', removed_reason = NULL, removed_note = NULL, removed_at = NULL WHERE id = ?1").bind(id),
    db.prepare("DELETE FROM trash WHERE r2_key = ?1").bind(key),
    audit(db, "restore", id, null, `was ${row.status}`),
    queueStatement(db, listTags([id])),
  ];
  if (row.status !== "hidden") {
    const songs = row.songs ? JSON.parse(row.songs).map((s) => s.song).join(" ") : "";
    stmts.splice(1, 0,
      db.prepare("DELETE FROM packages_fts WHERE rowid = ?1").bind(row.seq),
      db.prepare("INSERT INTO packages_fts (rowid, title, artist, author, songs, description) VALUES (?1, ?2, ?3, ?4, ?5, ?6)")
        .bind(row.seq, indexText(row.title), indexText(row.artist), indexText(row.author), indexText(songs), indexText(row.description)));
  }
  try {
    await db.batch(stmts);
  } catch (e) {
    if (row.status === "quarantined") await moveObject(env, key, quarantineKey(id, row.version)).catch(() => {});
    if (isUniqueError(e, "battle_id")) throw new HubError(409, "battle_taken", "Another live entry now has this battle's id.");
    throw e;
  }
  return { changed: 1, purge: await afterChange(env, ctx) };
}

// ---- reads ------------------------------------------------------------------------------------------------

async function overview(env) {
  const db = env.DB;
  const settings = await loadSettings(db);
  const t = now();
  const { results: statuses } = await db.prepare("SELECT status, count(*) AS n FROM packages GROUP BY status").all();
  const one = (sql, ...b) => db.prepare(sql).bind(...b).first("n");
  return {
    packages: Object.fromEntries(statuses.map((r) => [r.status, r.n])),
    storageUsed: settings.num("storage_used"),
    storageReserved: await one("SELECT coalesce(sum(received_bytes), 0) AS n FROM uploads WHERE state = 'open'"),
    storageCap: settings.num("storage_cap_bytes"),
    d1Size: settings.num("d1_size_bytes"),
    d1Close: settings.num("d1_close_bytes"),
    uploadsToday: await one("SELECT count(*) AS n FROM uploads WHERE state = 'live' AND updated_at > ?1", t - DAY),
    attemptsToday: await one("SELECT count(*) AS n FROM uploads WHERE created_at > ?1", t - DAY),
    newKeysToday: await one("SELECT count(*) AS n FROM uploaders WHERE created_at > ?1", t - DAY),
    openReports: await one("SELECT count(*) AS n FROM reports WHERE resolved_at IS NULL"),
    picturesWaiting: await one("SELECT count(*) AS n FROM packages WHERE picture_state = 'waiting' AND status IN ('live','hidden')"),
    purgesPending: await one("SELECT count(*) AS n FROM purge_queue WHERE done_at IS NULL"),
    purgesFailing: await one("SELECT count(*) AS n FROM purge_queue WHERE done_at IS NULL AND tries > 0"),
    counts: countsOn(env),
    switches: {
      uploadsOpen: settings.on("uploads_open"), newKeysOpen: settings.on("new_keys_open"), closeUploadsForKeysYoungerThanDays: settings.num("close_uploads_key_days"),
      varUploadsClosed: uploadsVarClosed(env), varReadOnly: readOnly(env), blockedIds: [...blockedIds(env)],
      webhook: typeof env.NOTIFY_WEBHOOK === "string" && env.NOTIFY_WEBHOOK.startsWith("https://"),
    },
    time: t,
  };
}

const ADMIN_ROW = "p.id, p.kind, p.status, p.version, p.title, p.artist, p.author, p.uploader_id, p.lanes, p.battle_id, p.file_size, p.created_at, " +
  "p.updated_at, p.downloads, p.reports_open, p.picture_state, p.removed_reason, p.removed_at, p.seq, u.name AS uploader_name, u.created_at AS key_created, " +
  "u.status AS uploader_status, u.strikes AS uploader_strikes, u.trusted_at AS uploader_trusted";

const adminRow = (r) => ({
  id: r.id, kind: r.kind, status: r.status, version: r.version, title: r.title, artist: r.artist, author: r.author, lanes: r.lanes,
  battleId: r.battle_id, size: r.file_size, createdAt: r.created_at, updatedAt: r.updated_at, downloads: r.downloads, reportsOpen: r.reports_open,
  pictureState: r.picture_state, removedReason: r.removed_reason, removedAt: r.removed_at,
  uploader: { id: r.uploader_id, name: r.uploader_name, tag: tagOf(r.uploader_id), keyCreated: r.key_created, status: r.uploader_status,
    strikes: r.uploader_strikes, trusted: r.uploader_trusted !== null },
});

function intParam(url, name, min, max) {
  const v = url.searchParams.get(name);
  if (v === null || v === "") return null;
  if (!/^[0-9]{1,12}$/.test(v) || Number(v) < min || Number(v) > max) throw new HubError(400, "bad_query", `${name} must be a whole number.`);
  return Number(v);
}

async function listAdminPackages(env, url) {
  const status = url.searchParams.get("status");
  if (status && !["live", "hidden", "removed", "deleted", "quarantined"].includes(status)) throw new HubError(400, "bad_query", "Unknown status.");
  const since = intParam(url, "since", 0, 1e11);
  const keyAge = intParam(url, "keyAge", 0, 36500);
  const cursor = intParam(url, "cursor", 0, 1e12);
  const q = (url.searchParams.get("q") || "").trim().slice(0, 100);
  const where = ["1 = 1"], binds = [];
  const bind = (v) => {
    binds.push(v);
    return `?${binds.length}`;
  };
  if (status) where.push(`p.status = ${bind(status)}`);
  if (since !== null) where.push(`p.updated_at >= ${bind(since)}`);
  if (keyAge !== null) where.push(`u.created_at >= ${bind(now() - keyAge * DAY)}`);
  if (cursor !== null) where.push(`p.seq < ${bind(cursor)}`);
  if (q) {
    if (PKG_ID.test(q)) where.push(`p.id = ${bind(q)}`);
    else if (UPLOADER_ID.test(q)) where.push(`p.uploader_id = ${bind(q)}`);
    else if (BATTLE_ID.test(q.toLowerCase())) where.push(`p.battle_id = ${bind(q.toLowerCase())}`);
    else where.push(`p.title LIKE ${bind("%" + q.replace(/[\\%_]/g, (c) => "\\" + c) + "%")} ESCAPE '\\'`);
  }
  const { results } = await env.DB.prepare(
    `SELECT ${ADMIN_ROW} FROM packages p JOIN uploaders u ON u.id = p.uploader_id WHERE ${where.join(" AND ")} ORDER BY p.seq DESC LIMIT 50`,
  ).bind(...binds).all();
  return { items: results.map(adminRow), next: results.length === 50 ? results[49].seq : null };
}

async function adminPackage(env, id) {
  const db = env.DB;
  const r = await db.prepare(`SELECT ${ADMIN_ROW}, p.description, p.contents, p.flags, p.difficulties, p.songs, p.file_sha256, p.fingerprint, ` +
    "p.removed_note, t.b64 AS thumb FROM packages p JOIN uploaders u ON u.id = p.uploader_id LEFT JOIN thumbs t ON t.package_id = p.id WHERE p.id = ?1").bind(id).first();
  if (!r) throw new HubError(404, "not_found", "There's no such entry.");
  const { results: reports } = await db.prepare(
    "SELECT reporter_hash, version, reason, note, created_at, resolved_at, resolution FROM reports WHERE package_id = ?1 ORDER BY created_at DESC LIMIT 100",
  ).bind(id).all();
  const { results: log } = await db.prepare("SELECT at, actor, action, detail FROM audit WHERE package_id = ?1 ORDER BY at DESC, id DESC LIMIT 50").bind(id).all();
  const blocked = r.battle_id ? await db.prepare("SELECT uploader_id, package_id, at FROM battle_blocks WHERE battle_id = ?1").bind(r.battle_id).all() : { results: [] };
  return {
    ...adminRow(r), description: r.description, contents: JSON.parse(r.contents || "{}"), flags: JSON.parse(r.flags || "{}"),
    difficulties: JSON.parse(r.difficulties || "[]"), songs: r.songs ? JSON.parse(r.songs) : null, sha256: r.file_sha256, fingerprint: r.fingerprint,
    removedNote: r.removed_note, thumb: r.thumb ?? null,
    reports: reports.map((x) => ({ reporter: x.reporter_hash.slice(0, 12), reporterHash: x.reporter_hash, version: x.version, reason: x.reason, note: x.note,
      createdAt: x.created_at, resolvedAt: x.resolved_at, resolution: x.resolution })),
    audit: log, battleBlocks: blocked.results,
  };
}

async function adminUploader(env, id) {
  const db = env.DB;
  const u = await db.prepare("SELECT id, name, created_at, status, strikes, trusted_at, name_changed_at FROM uploaders WHERE id = ?1").bind(id).first();
  if (!u) throw new HubError(404, "not_found", "There's no such uploader.");
  const { results } = await db.prepare(`SELECT ${ADMIN_ROW} FROM packages p JOIN uploaders u ON u.id = p.uploader_id WHERE p.uploader_id = ?1 ORDER BY p.seq DESC LIMIT 100`)
    .bind(id).all();
  return { uploader: { ...u, tag: tagOf(u.id) }, packages: results.map(adminRow) };
}

async function listReports(env, url) {
  const open = url.searchParams.get("open") !== "0";
  const { results } = await env.DB.prepare(
    "SELECT r.package_id, p.title, p.status, p.uploader_id, count(*) AS n, max(r.created_at) AS last, " +
      REASONS.map((x) => `sum(r.reason = '${x}') AS ${x}`).join(", ") +
      ` FROM reports r LEFT JOIN packages p ON p.id = r.package_id WHERE ${open ? "r.resolved_at IS NULL" : "1 = 1"} ` +
      "GROUP BY r.package_id ORDER BY last DESC LIMIT 100",
  ).all();
  return {
    items: results.map((r) => ({
      packageId: r.package_id, title: r.title, status: r.status, uploaderId: r.uploader_id, count: r.n, last: r.last,
      reasons: Object.fromEntries(REASONS.map((x) => [x, r[x]]).filter(([, n]) => n > 0)),
    })),
  };
}

async function waitingPictures(env) {
  const { results } = await env.DB.prepare(
    "SELECT p.id, p.title, p.status, p.picture_due, p.uploader_id, t.b64 FROM packages p JOIN thumbs t ON t.package_id = p.id " +
      "WHERE p.picture_state = 'waiting' AND p.status IN ('live','hidden') ORDER BY p.picture_due LIMIT 60",
  ).all();
  return { items: results.map((r) => ({ id: r.id, title: r.title, status: r.status, due: r.picture_due, uploaderId: r.uploader_id, thumb: r.b64 })) };
}

async function listPurges(env) {
  const { results } = await env.DB.prepare("SELECT id, tags, created_at, tries, done_at, last_error FROM purge_queue ORDER BY id DESC LIMIT 50").all();
  return { items: results.map((r) => ({ ...r, tags: JSON.parse(r.tags) })) };
}

async function adminFile(env, id, version) {
  const row = await env.DB.prepare("SELECT kind, status FROM packages WHERE id = ?1").bind(id).first();
  if (!row) throw new HubError(404, "not_found", "There's no such entry.");
  const obj = (await env.FILES.get(fileKey(id, version))) || (await env.FILES.get(quarantineKey(id, version)));
  if (!obj) throw new HubError(404, "not_found", "That version's file is gone.");
  return new Response(obj.body, { status: 200, headers: fileHeaders(id, row.kind, NO_STORE) });
}

// ---- backups and the search index ------------------------------------------------------------------------

export const BACKUP_TABLES = [
  ["uploaders", ["seq"], 500], ["packages", ["seq"], 200], ["battle_blocks", ["battle_id", "uploader_id"], 500], ["thumbs", ["package_id"], 100],
  ["uploads", ["id"], 500], ["upload_parts", ["upload_id", "n"], 500], ["reports", ["package_id", "reporter_hash"], 500],
  ["purge_queue", ["id"], 500], ["settings", ["k"], 500], ["audit", ["id"], 500], ["trash", ["r2_key"], 500],
];

/**
 * Writes the next pages of the base tables (not the search index) as JSON into backup/<date>/ in R2.
 * The /admin page repeats the call with the returned cursor until done.
 */
async function backup(env, body) {
  const c = body.cursor && typeof body.cursor === "object" ? body.cursor : { date: new Date(now() * 1000).toISOString().slice(0, 10), table: 0, after: null, page: 0 };
  if (!/^\d{4}-\d{2}-\d{2}$/.test(String(c.date)) || !Number.isInteger(c.table) || c.table < 0 || !Number.isInteger(c.page)) {
    throw new HubError(400, "bad_request", "Bad backup cursor.");
  }
  const written = [];
  let { table, after, page } = c;
  for (let step = 0; step < 8 && table < BACKUP_TABLES.length; step++) {
    const [name, keys, size] = BACKUP_TABLES[table];
    let where = "", binds = [];
    if (after) {
      if (!Array.isArray(after) || after.length !== keys.length) throw new HubError(400, "bad_request", "Bad backup cursor.");
      where = `WHERE (${keys.join(", ")}) > (${keys.map((_, i) => `?${i + 1}`).join(", ")})`;
      binds = after;
    }
    const { results } = await env.DB.prepare(`SELECT * FROM ${name} ${where} ORDER BY ${keys.join(", ")} LIMIT ${size}`).bind(...binds).all();
    if (results.length) {
      const key = `backup/${c.date}/${name}-${String(page).padStart(4, "0")}.json`;
      await env.FILES.put(key, JSON.stringify({ table: name, rows: results }));
      written.push(key);
    }
    if (results.length === size) {
      after = keys.map((k) => results[results.length - 1][k]);
      page++;
    } else {
      table++;
      after = null;
      page = 0;
    }
  }
  const done = table >= BACKUP_TABLES.length;
  if (done) await audit(env.DB, "backup", null, null, c.date).run();
  return { done, written, cursor: done ? null : { date: c.date, table, after, page } };
}

/** Rebuilds the search rows for live and hidden entries, 40 at a time (after a restore). */
async function reindex(env, body) {
  const after = Number.isInteger(body.after) ? body.after : 0;
  const db = env.DB;
  const { results } = await db.prepare("SELECT seq, title, artist, author, songs, description FROM packages WHERE seq > ?1 AND status IN ('live','hidden') ORDER BY seq LIMIT 40")
    .bind(after).all();
  const stmts = [];
  for (let i = 0; i < results.length; i += 16) {
    const rows = results.slice(i, i + 16);
    const values = [], binds = [];
    for (const r of rows) {
      const songs = r.songs ? JSON.parse(r.songs).map((s) => s.song).join(" ") : "";
      const at = binds.length;
      values.push(`(?${at + 1}, ?${at + 2}, ?${at + 3}, ?${at + 4}, ?${at + 5}, ?${at + 6})`);
      binds.push(r.seq, indexText(r.title), indexText(r.artist), indexText(r.author), indexText(songs), indexText(r.description));
    }
    stmts.push(db.prepare(`DELETE FROM packages_fts WHERE rowid IN (SELECT value FROM json_each(?1))`).bind(JSON.stringify(rows.map((r) => r.seq))));
    stmts.push(db.prepare(`INSERT INTO packages_fts (rowid, title, artist, author, songs, description) VALUES ${values.join(", ")}`).bind(...binds));
  }
  if (stmts.length) await db.batch(stmts);
  const last = results.length ? results[results.length - 1].seq : after;
  return { done: results.length < 40, after: last, indexed: results.length };
}

// ---- the router -------------------------------------------------------------------------------------------

function pkgId(id) {
  if (!PKG_ID.test(id)) throw new HubError(404, "not_found", "There's no such entry.");
  return id;
}

async function packagesSince(env, body, statuses) {
  const since = body.uploadedSince ?? null, keys = body.keysCreatedSince ?? null;
  if (since === null && keys === null) throw new HubError(400, "bad_request", "Give uploadedSince or keysCreatedSince (Unix seconds).");
  for (const v of [since, keys]) if (v !== null && (!Number.isInteger(v) || v < 0)) throw new HubError(400, "bad_request", "Times are Unix seconds.");
  const { results } = await env.DB.prepare(
    `SELECT p.id FROM packages p JOIN uploaders u ON u.id = p.uploader_id WHERE p.status IN (${statuses.map((s) => `'${s}'`).join(", ")}) ` +
      "AND (?1 IS NULL OR p.updated_at >= ?1) AND (?2 IS NULL OR u.created_at >= ?2) ORDER BY p.seq LIMIT ?3",
  ).bind(since, keys, BULK_MAX).all();
  return results.map((r) => r.id);
}

export async function adminRoute(env, request, ctx, parts) {
  const url = new URL(request.url);
  const method = request.method;
  const db = env.DB;
  const path = parts.join("/");
  const write = method !== "GET";
  if (write) checkWrite(request, method === "DELETE" ? null : "json");
  const body = write && method !== "DELETE" ? await readJson(request, 16 * 1024) : {};

  if (method === "GET" && path === "overview") return json(await overview(env));
  if (method === "GET" && path === "reports") return json(await listReports(env, url));
  if (method === "GET" && path === "packages") return json(await listAdminPackages(env, url));
  if (method === "GET" && path === "pictures") return json(await waitingPictures(env));
  if (method === "GET" && path === "purges") return json(await listPurges(env));
  if (method === "POST" && path === "purges/retry") return json(await runPurges(env, ctx));
  if (method === "POST" && path === "backup") return json(await backup(env, body));
  if (method === "POST" && path === "reindex") return json(await reindex(env, body));
  if (method === "GET" && path === "settings") {
    const s = await loadSettings(db);
    const out = {};
    for (const k of [...Object.keys(EDITABLE), ...READ_ONLY_KEYS]) out[k] = s.str(k);
    return json({ settings: out, editable: Object.keys(EDITABLE), defaults: Object.fromEntries(Object.keys(EDITABLE).map((k) => [k, DEFAULTS[k]])) });
  }
  if (method === "PUT" && path === "settings") {
    const changes = checkSettingChanges(body);
    await db.batch([
      ...changes.map(([k, v]) => upsertSetting(db, k, v)),
      audit(db, "settings", null, null, JSON.stringify(Object.fromEntries(changes))),
      queueStatement(db, ["info", "legal"]),
    ]);
    return json({ changed: changes.map(([k]) => k), purge: await afterChange(env, ctx) });
  }
  if (method === "PUT" && path === "gates") {
    const days = body.closeUploadsForKeysYoungerThanDays;
    if (!Number.isInteger(days) || days < 0 || days > 3650) throw new HubError(400, "bad_request", "closeUploadsForKeysYoungerThanDays must be 0 or more days.");
    await db.batch([upsertSetting(db, "close_uploads_key_days", days), audit(db, "gates", null, null, String(days))]);
    return json({ closeUploadsForKeysYoungerThanDays: days });
  }
  if (method === "POST" && path === "bulk") {
    if (body.action !== "hide" && body.action !== "remove") throw new HubError(400, "bad_request", 'action must be "hide" or "remove".');
    const ids = await packagesSince(env, body, body.action === "hide" ? ["live"] : ["live", "hidden"]);
    if (ids.length === 0) return json({ done: 0, more: false });
    const r = body.action === "hide" ? await hidePackages(env, ctx, ids, "bulk") : await removePackages(env, ctx, ids, "spam", "", false);
    return json({ done: r.changed, more: ids.length === BULK_MAX, purge: r.purge });
  }
  if (method === "POST" && path === "reports/resolve") {
    const id = pkgId(String(body.packageId || ""));
    const resolution = String(body.resolution || "").slice(0, 32) || "resolved";
    const hash = body.reporterHash === undefined || body.reporterHash === null ? null : String(body.reporterHash);
    const t = now();
    const [r] = await db.batch([
      db.prepare("UPDATE reports SET resolved_at = ?3, resolution = ?4 WHERE package_id = ?1 AND (?2 IS NULL OR reporter_hash = ?2) AND resolved_at IS NULL")
        .bind(id, hash, t, resolution),
      db.prepare("UPDATE packages SET reports_open = (SELECT count(*) FROM reports WHERE package_id = ?1 AND resolved_at IS NULL) WHERE id = ?1").bind(id),
      audit(db, "resolve-reports", id, null, resolution),
    ]);
    return json({ resolved: r.meta.changes });
  }

  let m;
  if ((m = /^packages\/([^/]+)$/.exec(path)) && method === "GET") return json(await adminPackage(env, pkgId(m[1])));
  if ((m = /^files\/([^/]+)\/([^/]+)$/.exec(path)) && method === "GET") {
    if (!/^[1-9][0-9]{0,8}$/.test(m[2])) throw new HubError(404, "not_found", "There's no such version.");
    return adminFile(env, pkgId(m[1]), Number(m[2]));
  }
  if ((m = /^packages\/([^/]+)\/(hide|restore|remove|quarantine|picture)$/.exec(path)) && method === "POST") {
    const id = pkgId(m[1]);
    const exists = await db.prepare("SELECT status FROM packages WHERE id = ?1").bind(id).first("status");
    if (!exists) throw new HubError(404, "not_found", "There's no such entry.");
    if (m[2] === "hide") return json(await hidePackages(env, ctx, [id], note(body.note)));
    if (m[2] === "restore") return json(await restorePackage(env, ctx, id));
    if (m[2] === "quarantine") return json(await quarantinePackage(env, ctx, id, note(body.note)));
    if (m[2] === "remove") {
      if (!REMOVE_REASONS.includes(body.reason)) throw new HubError(400, "bad_reason", `The reason must be one of: ${REMOVE_REASONS.join(", ")}.`);
      return json(await removePackages(env, ctx, [id], body.reason, note(body.note), body.strike === true && body.reason === "copyright"));
    }
    if (typeof body.show !== "boolean") throw new HubError(400, "bad_request", "Give show: true or false.");
    await db.batch([
      db.prepare("UPDATE packages SET picture_state = ?2, picture_due = NULL WHERE id = ?1").bind(id, body.show ? "shown" : "refused"),
      audit(db, body.show ? "picture-show" : "picture-refuse", id, null, null),
      queueStatement(db, listTags([id])),
    ]);
    return json({ pictureState: body.show ? "shown" : "refused", purge: await afterChange(env, ctx) });
  }
  if ((m = /^battle-ids\/([^/]+)\/release$/.exec(path)) && method === "POST") {
    const battleId = m[1].toLowerCase();
    if (!BATTLE_ID.test(battleId)) throw new HubError(404, "not_found", "That isn't a battle id.");
    const [r] = await db.batch([db.prepare("DELETE FROM battle_blocks WHERE battle_id = ?1").bind(battleId), audit(db, "release-battle-id", null, null, battleId)]);
    return json({ released: r.meta.changes });
  }
  if ((m = /^uploaders\/([^/]+)$/.exec(path)) && method === "GET") {
    if (!UPLOADER_ID.test(m[1])) throw new HubError(404, "not_found", "There's no such uploader.");
    return json(await adminUploader(env, m[1]));
  }
  if ((m = /^uploaders\/([^/]+)\/(ban|unban|strikes|remove-all|revoke-key|transfer)$/.exec(path)) && method === "POST") {
    const uid = m[1];
    if (!UPLOADER_ID.test(uid)) throw new HubError(404, "not_found", "There's no such uploader.");
    const u = await db.prepare("SELECT id, status, strikes FROM uploaders WHERE id = ?1").bind(uid).first();
    if (!u) throw new HubError(404, "not_found", "There's no such uploader.");
    const action = m[2];
    if (action === "ban" || action === "unban" || action === "revoke-key") {
      const status = action === "ban" ? "banned" : action === "unban" ? "ok" : "revoked";
      if (action === "unban" && u.status === "revoked") throw new HubError(409, "revoked", "A revoked key stays revoked.");
      await db.batch([db.prepare("UPDATE uploaders SET status = ?2 WHERE id = ?1").bind(uid, status), audit(db, action, null, uid, note(body.note))]);
      return json({ status });
    }
    if (action === "strikes") {
      if (!Number.isInteger(body.value) || body.value < 0 || body.value > 100) throw new HubError(400, "bad_request", "value must be 0 to 100.");
      await db.batch([
        db.prepare("UPDATE uploaders SET strikes = ?2, trusted_at = CASE WHEN ?2 > 0 THEN NULL ELSE trusted_at END WHERE id = ?1").bind(uid, body.value),
        audit(db, "strikes", null, uid, String(body.value)),
      ]);
      return json({ strikes: body.value });
    }
    if (action === "remove-all") {
      const reason = REMOVE_REASONS.includes(body.reason) ? body.reason : "other";
      const { results } = await db.prepare("SELECT id FROM packages WHERE uploader_id = ?1 AND status IN ('live','hidden') ORDER BY seq LIMIT ?2").bind(uid, BULK_MAX).all();
      const ids = results.map((r) => r.id);
      if (ids.length === 0) return json({ done: 0, more: false });
      const r = await removePackages(env, ctx, ids, reason, note(body.note), false);
      return json({ done: r.changed, more: ids.length === BULK_MAX, purge: r.purge });
    }
    // transfer: only on a request the owner can verify (the runbook says how).
    const to = String(body.to || "");
    if (!UPLOADER_ID.test(to) || to === uid) throw new HubError(400, "bad_request", "Give the uploader id to move the entries to.");
    const target = await db.prepare("SELECT id FROM uploaders WHERE id = ?1").bind(to).first();
    if (!target) throw new HubError(404, "not_found", "There's no such uploader to move them to.");
    const [r] = await db.batch([
      db.prepare("UPDATE packages SET uploader_id = ?2 WHERE uploader_id = ?1").bind(uid, to),
      audit(db, "transfer", null, uid, to),
      queueStatement(db, ["list"]),
    ]);
    return json({ moved: r.meta.changes, purge: await afterChange(env, ctx) });
  }
  throw new HubError(404, "not_found", "There's no such owner route.");
}
