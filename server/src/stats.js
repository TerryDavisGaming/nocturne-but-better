// Download counts without D1 writes (DESIGN-HUB 2.12): a verified install writes one Analytics Engine data
// point with a daily-salted hash of the address; every 6 hours the cron asks the Analytics Engine SQL API
// for distinct counts and folds them into D1 in one statement per 1000 packages.

import { hex, now, PKG_ID } from "./ids.js";
import { getSetting, upsertSetting } from "./settings.js";
import { countsOn } from "./guard.js";

export const DATASET = "nbb_downloads";

function randomSalt() {
  return hex(crypto.getRandomValues(new Uint8Array(32)));
}

async function hmacHex(secret, text) {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return hex(await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(text))).slice(0, 32);
}

/** The day's salt, made on first use when `canWrite` (the daily cron replaces it). "" when there's none. */
export async function dailySalt(env, canWrite) {
  let salt = await getSetting(env.DB, "stats_salt");
  if (!salt && canWrite) {
    await env.DB.prepare("UPDATE settings SET v = ?1 WHERE k = 'stats_salt' AND v = ''").bind(randomSalt()).run();
    salt = await getSetting(env.DB, "stats_salt");
  }
  return salt;
}

/** One data point for a verified install. Skips counting when D1 or Analytics Engine can't be used. */
export async function recordInstall(env, packageId, addressKey, canWrite) {
  if (!env.DL || typeof env.DL.writeDataPoint !== "function") return false;
  let salt;
  try {
    salt = await dailySalt(env, canWrite);
  } catch {
    return false;
  }
  if (!salt) return false;
  env.DL.writeDataPoint({ indexes: [packageId], blobs: [packageId, await hmacHex(salt, addressKey + "|" + packageId)] });
  return true;
}

/**
 * Counts one more `what` (a report, a new key) for an address today, and refuses past `cap` (DESIGN-HUB 2.9,
 * so one address can't fill a whole-hub daily cap by itself). The address is kept only as an HMAC with the
 * day's salt, in rows the daily cron deletes when it replaces the salt.
 */
export async function countForAddress(env, what, addressKey, cap) {
  const salt = await dailySalt(env, true);
  const h = await hmacHex(salt, `${what}|${addressKey}`);
  const n = await env.DB.prepare("SELECT n FROM address_day WHERE h = ?1 AND what = ?2").bind(h, what).first("n");
  if ((n ?? 0) >= cap) return false;
  await env.DB.prepare("INSERT INTO address_day (h, what, n) VALUES (?1, ?2, 1) ON CONFLICT(h, what) DO UPDATE SET n = n + 1").bind(h, what).run();
  return true;
}

/**
 * The daily salt change: the old salt is kept one more day, the one before it is gone for good. The
 * per-address counts go with the old salt.
 */
export function rotateSaltStatements(db, current) {
  return [upsertSetting(db, "stats_salt_prev", current), upsertSetting(db, "stats_salt", randomSalt()), db.prepare("DELETE FROM address_day")];
}

/**
 * Folds the counts since the last fold into packages.downloads. Needs STATS_TOKEN (an API token with
 * "Account Analytics: Read") and STATS_ACCOUNT_ID; does nothing without them.
 */
export async function foldDownloads(env, fetchFn = fetch) {
  if (!countsOn(env)) return { skipped: true };
  const until = Number(await getSetting(env.DB, "stats_folded_until")) || 0;
  const to = now() - 300; // Analytics Engine takes a little while to show new points
  const from = until > 0 ? until : to - 90 * 86400; // it keeps 3 months
  if (to <= from) return { folded: 0 };
  const sql = `SELECT blob1 AS id, count(DISTINCT blob2) AS n FROM ${DATASET} ` +
    `WHERE timestamp > toDateTime(${from}) AND timestamp <= toDateTime(${to}) GROUP BY blob1`;
  const response = await fetchFn(`https://api.cloudflare.com/client/v4/accounts/${encodeURIComponent(env.STATS_ACCOUNT_ID)}/analytics_engine/sql`, {
    method: "POST",
    headers: { Authorization: `Bearer ${env.STATS_TOKEN}` },
    body: sql,
  });
  if (!response.ok) throw Object.assign(new Error("stats"), { code: `stats_http_${response.status}` });
  const body = await response.json();
  const counts = {};
  let rows = 0;
  for (const row of Array.isArray(body.data) ? body.data : []) {
    const id = String(row.id ?? "");
    const n = Math.floor(Number(row.n));
    if (!PKG_ID.test(id) || !Number.isFinite(n) || n <= 0 || n > 10000000) continue;
    counts[id] = n;
    rows++;
  }
  const ids = Object.keys(counts);
  const statements = [];
  for (let i = 0; i < ids.length && statements.length < 10; i += 1000) {
    const chunk = {};
    for (const id of ids.slice(i, i + 1000)) chunk[id] = counts[id];
    statements.push(
      env.DB.prepare("UPDATE packages SET downloads = downloads + j.value FROM json_each(?1) AS j WHERE packages.id = j.key").bind(JSON.stringify(chunk)),
    );
  }
  statements.push(upsertSetting(env.DB, "stats_folded_until", to));
  await env.DB.batch(statements);
  return { folded: rows };
}
