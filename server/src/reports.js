// Reports (DESIGN-HUB 2.11): one per key hash per package, from any well-formed key (it needn't be
// registered, so a player never has to pick a name to report). Nothing is ever hidden automatically.

import { HubError, checkWrite, json, readJson } from "./http.js";
import { now } from "./ids.js";
import { LIMITS, cleanText } from "./names.js";
import { bearerKey, keyHash, uploaderByHash } from "./auth.js";
import { clientIp, ipKey, rateLimit, refuseReadOnly } from "./guard.js";
import { loadSettings } from "./settings.js";
import { notify, reportText } from "./notify.js";
import { countForAddress } from "./stats.js";

export const REASONS = ["copyright", "offensive", "picture", "broken", "malicious", "spam", "other"];

export async function reportPackage(env, request, ctx, id) {
  checkWrite(request, "json");
  const address = ipKey(clientIp(request));
  await rateLimit(env, "RL_REPORT", address);
  refuseReadOnly(env);
  const key = bearerKey(request);
  const body = await readJson(request, 4096);
  if (!REASONS.includes(body.reason)) throw new HubError(400, "bad_reason", `The reason must be one of: ${REASONS.join(", ")}.`);
  const note = body.note === undefined || body.note === null ? "" : cleanText(body.note, LIMITS.note);
  if (note === null) throw new HubError(400, "bad_request", "The note must be text.");
  const db = env.DB;
  const hash = await keyHash(key);
  const pkg = await db.prepare("SELECT id, status, version, title FROM packages WHERE id = ?1").bind(id).first();
  if (!pkg || (pkg.status !== "live" && pkg.status !== "hidden")) throw new HubError(404, "not_found", "There's no such entry.");
  const reporter = await uploaderByHash(db, hash);
  if (reporter && reporter.status !== "ok") return json({ status: "received" }, 201); // banned or revoked keys are ignored
  const t = now();
  const already = await db.prepare("SELECT 1 AS x FROM reports WHERE package_id = ?1 AND reporter_hash = ?2").bind(id, hash).first();
  if (already) return json({ status: "already" }, 200);
  const settings = await loadSettings(db);
  const mine = await db.prepare("SELECT count(*) AS n FROM reports WHERE reporter_hash = ?1 AND created_at > ?2").bind(hash, t - 86400).first("n");
  if (mine >= settings.num("reports_per_key_day")) throw new HubError(429, "daily_limit", "You've sent as many reports as the hub takes in a day.", { retryAfter: 3600 });
  const all = await db.prepare("SELECT count(*) AS n FROM reports WHERE created_at > ?1").bind(t - 86400).first("n");
  if (all >= settings.num("reports_global_day")) throw new HubError(429, "daily_limit", "The hub has taken all the reports it can today.", { retryAfter: 3600 });
  // Keys cost nothing, so one address with many keys could otherwise fill the whole hub's cap by itself.
  if (!(await countForAddress(env, "report", address, settings.num("reports_per_address_day")))) {
    throw new HubError(429, "daily_limit", "This network has sent as many reports as the hub takes in a day.", { retryAfter: 3600 });
  }
  // The owner hears of an entry's reports at most once an hour (the webhook isn't a way to flood them).
  const recent = await db.prepare("SELECT 1 AS x FROM reports WHERE package_id = ?1 AND created_at > ?2 LIMIT 1").bind(id, t - 3600).first();
  const [inserted] = await db.batch([
    db.prepare("INSERT OR IGNORE INTO reports (package_id, reporter_hash, version, reason, note, created_at) VALUES (?1, ?2, ?3, ?4, ?5, ?6)")
      .bind(id, hash, pkg.version, body.reason, note, t),
    db.prepare("UPDATE packages SET reports_open = (SELECT count(*) FROM reports WHERE package_id = ?1 AND resolved_at IS NULL) WHERE id = ?1").bind(id),
  ]);
  if (inserted.meta.changes !== 1) return json({ status: "already" }, 200);
  if (!recent) notify(env, ctx, reportText(pkg, body.reason));
  return json({ status: "received" }, 201);
}
