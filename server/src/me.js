// The player's own routes (DESIGN-HUB 2.3, 2.8): register or rename, status and limits, rotate the key,
// and every one of their uploads.

import { HubError, checkWrite, json, readJson } from "./http.js";
import { HEX64, newUploaderId, now, tagOf } from "./ids.js";
import { cleanName } from "./names.js";
import { bearerKey, keyHash, requireUploader, uploaderByHash } from "./auth.js";
import { clientIp, countsOn, ipKey, isUniqueError, rateLimit, refuseReadOnly, uploadsVarClosed } from "./guard.js";
import { loadSettings } from "./settings.js";
import { myCard } from "./cards.js";
import { countForAddress } from "./stats.js";
import { TRIES_PER_UPLOAD, keyDay } from "./uploads.js";

export const publicUploader = (u) => ({
  id: u.id, name: u.name, tag: tagOf(u.id), createdAt: u.created_at, status: u.status, strikes: u.strikes, trusted: u.trusted_at !== null,
});

export async function putMe(env, request) {
  checkWrite(request, "json");
  refuseReadOnly(env);
  const key = bearerKey(request);
  const hash = await keyHash(key);
  const body = await readJson(request, 2048);
  const name = cleanName(body.name);
  if (!name) throw new HubError(400, "bad_name", "Pick a name of 1 to 32 characters with a letter or digit in it (not admin, owner, moderator or hub).");
  const db = env.DB;
  const t = now();
  const existing = await uploaderByHash(db, hash);
  if (!existing) {
    const address = ipKey(clientIp(request), 56);
    await rateLimit(env, "RL_NEWKEY", address);
    const settings = await loadSettings(db);
    if (!settings.on("new_keys_open") || !settings.on("uploads_open") || uploadsVarClosed(env)) {
      throw new HubError(503, "closed", "The hub isn't taking new uploaders right now.");
    }
    const recent = await db.prepare("SELECT count(*) AS n FROM uploaders WHERE created_at > ?1").bind(t - 86400).first("n");
    if (recent >= settings.num("new_keys_global_day")) throw new HubError(429, "daily_limit", "The hub has taken all the new uploaders it can today. Try again tomorrow.", { retryAfter: 3600 });
    // One address can't fill the whole hub's daily cap on its own.
    if (!(await countForAddress(env, "newkey", address, settings.num("new_keys_per_address_day")))) {
      throw new HubError(429, "daily_limit", "This network has made as many new hub keys as the hub takes in a day. Try again tomorrow.", { retryAfter: 3600 });
    }
    const id = newUploaderId();
    try {
      await db.prepare("INSERT INTO uploaders (id, key_hash, name, created_at) VALUES (?1, ?2, ?3, ?4)").bind(id, hash, name, t).run();
    } catch (e) {
      if (!isUniqueError(e)) throw e;
    }
    const row = await uploaderByHash(db, hash);
    return json({ uploader: publicUploader(row), created: row.id === id });
  }
  if (existing.status === "revoked") throw new HubError(403, "revoked", "This hub key was turned off by the hub's owner.");
  if (existing.status === "banned") throw new HubError(403, "banned", "This hub key can't upload any more.");
  if (existing.name === name) return json({ uploader: publicUploader(existing), created: false });
  if (existing.name_changed_at !== null && existing.name_changed_at > t - 86400) {
    throw new HubError(429, "rename_limit", "You can change your name once a day.", { retryAfter: existing.name_changed_at + 86400 - t });
  }
  await db.prepare("UPDATE uploaders SET name = ?2, name_changed_at = ?3 WHERE id = ?1").bind(existing.id, name, t).run();
  return json({ uploader: publicUploader({ ...existing, name, name_changed_at: t }), created: false });
}

export async function getMe(env, request) {
  const { uploader } = await requireUploader(env, request, { allowBanned: true });
  const db = env.DB;
  const settings = await loadSettings(db);
  const t = now();
  const probationUntil = uploader.created_at + settings.num("probation_hours") * 3600;
  const probation = probationUntil > t;
  const mine = await keyDay(db, uploader.id, t - 86400);
  const perDay = settings.num(probation ? "probation_uploads_day" : "uploads_per_key_day");
  const live = await db.prepare("SELECT count(*) AS n FROM packages WHERE uploader_id = ?1 AND status = 'live'").bind(uploader.id).first("n");
  return json({
    uploader: publicUploader(uploader),
    limits: {
      probation, probationUntil: probation ? probationUntil : null,
      uploadsToday: mine.n, uploadsPerDay: perDay, attemptsToday: mine.tries, attemptsPerDay: perDay * TRIES_PER_UPLOAD,
      bytesToday: mine.bytes, bytesPerDay: settings.num(probation ? "probation_bytes_day" : "bytes_per_key_day"),
      livePackages: live, livePerKey: settings.num("live_per_key"),
    },
  });
}

/** Only the new key's SHA-256 travels; the old key stops working at once. */
export async function rotateKey(env, request) {
  checkWrite(request, "json");
  refuseReadOnly(env);
  const { uploader, hash } = await requireUploader(env, request, { allowBanned: true });
  await rateLimit(env, "RL_WRITE", "key:" + uploader.id);
  const body = await readJson(request, 1024);
  const next = body.newKeyHash;
  if (typeof next !== "string" || !HEX64.test(next)) throw new HubError(400, "bad_request", "newKeyHash must be the new key's SHA-256 (64 lower-case hex characters).");
  if (next === hash) throw new HubError(400, "bad_request", "That's the key you already have.");
  try {
    const r = await env.DB.prepare("UPDATE uploaders SET key_hash = ?3 WHERE id = ?1 AND key_hash = ?2").bind(uploader.id, hash, next).run();
    if (r.meta.changes !== 1) throw new HubError(409, "busy", "The key changed at the same time. Check which key works.");
  } catch (e) {
    if (isUniqueError(e)) throw new HubError(409, "key_in_use", "That key is already in use.");
    throw e;
  }
  await env.DB.prepare("INSERT INTO audit (at, actor, action, uploader_id) VALUES (?1, 'uploader', 'rotate-key', ?2)").bind(now(), uploader.id).run();
  return json({ uploader: publicUploader(uploader) });
}

export async function myPackages(env, request) {
  const { uploader } = await requireUploader(env, request, { allowBanned: true });
  const { results } = await env.DB.prepare(
    "SELECT p.*, ?2 AS uploader_name, t.b64 AS thumb FROM packages p LEFT JOIN thumbs t ON t.package_id = p.id " +
      "WHERE p.uploader_id = ?1 ORDER BY p.created_at DESC, p.seq DESC LIMIT 200",
  ).bind(uploader.id, uploader.name).all();
  return json({ items: results.map((r) => myCard(r, countsOn(env))) });
}
