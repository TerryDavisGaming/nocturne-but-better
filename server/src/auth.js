// The per-player key and the owner's key (DESIGN-HUB 2.3, 2.8). The server stores only the SHA-256 of a
// player's key; the owner's key is compared as SHA-256 digests with a timing-safe comparison.

import { HubError } from "./http.js";
import { sha256Hex } from "./ids.js";
import { clientIp, ipKey, rateLimit } from "./guard.js";

export const KEY_PATTERN = /^nbbk1_[A-Za-z0-9_-]{43}$/;

/** The player's key from Authorization: Bearer, or null when there's none. A malformed key is 401. */
export function bearerKey(request, required = true) {
  const header = request.headers.get("Authorization");
  if (!header) {
    if (required) throw new HubError(401, "no_key", "This needs your hub key.");
    return null;
  }
  const m = /^Bearer (\S+)$/.exec(header);
  if (!m || !KEY_PATTERN.test(m[1])) throw new HubError(401, "bad_key", "That isn't a hub key.");
  return m[1];
}

export const keyHash = (key) => sha256Hex(key);

/** The uploader for a key hash, or null. */
export async function uploaderByHash(db, hash) {
  return db.prepare("SELECT * FROM uploaders WHERE key_hash = ?1").bind(hash).first();
}

/** A registered, usable uploader for the request's key; 401/403 otherwise. */
export async function requireUploader(env, request, { allowBanned = false } = {}) {
  const key = bearerKey(request);
  const hash = await keyHash(key);
  const uploader = await uploaderByHash(env.DB, hash);
  if (!uploader) throw new HubError(401, "unknown_key", "This hub key isn't registered yet.");
  if (uploader.status === "revoked") throw new HubError(403, "revoked", "This hub key was turned off by the hub's owner.");
  if (uploader.status === "banned" && !allowBanned) throw new HubError(403, "banned", "This hub key can't upload any more.");
  return { key, hash, uploader };
}

function timingSafeEqual(a, b) {
  if (crypto.subtle && typeof crypto.subtle.timingSafeEqual === "function") return crypto.subtle.timingSafeEqual(a, b);
  const x = new Uint8Array(a), y = new Uint8Array(b);
  if (x.length !== y.length) return false;
  let diff = 0;
  for (let i = 0; i < x.length; i++) diff |= x[i] ^ y[i];
  return diff === 0;
}

/**
 * The owner's routes: 503 admin_off without a usable ADMIN_KEY (missing or under 32 characters), 429 on
 * too many tries, 401 on a wrong key. Both refusals are rate limited per address.
 */
export async function requireAdmin(env, request) {
  const secret = typeof env.ADMIN_KEY === "string" ? env.ADMIN_KEY : "";
  await rateLimit(env, "RL_WRITE", "admin:" + ipKey(clientIp(request)));
  if (secret.length < 32) throw new HubError(503, "admin_off", "The owner's key isn't set up (ADMIN_KEY must be at least 32 characters).");
  const header = request.headers.get("Authorization") || "";
  const m = /^Bearer (.{1,512})$/.exec(header);
  const enc = new TextEncoder();
  const given = await crypto.subtle.digest("SHA-256", enc.encode(m ? m[1] : ""));
  const wanted = await crypto.subtle.digest("SHA-256", enc.encode(secret));
  if (!m || !timingSafeEqual(given, wanted)) throw new HubError(401, "wrong_key", "Wrong key.");
}
