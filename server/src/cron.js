// Cron jobs (DESIGN-HUB 2.13). Each job handles at most 30 items a run and leaves the rest for the next run
// (the orphan sweep deletes up to 1000 keys in its one R2 call), and each runs in its own try/catch so one
// failure doesn't stop the others.
//
// The cron strings here must equal wrangler.jsonc's triggers exactly: runCron picks the jobs by the string.
// Cloudflare's weekday field is 1-7 (1 = Sunday) or SUN-SAT; a 0 there makes every deploy fail.

import { now } from "./ids.js";
import { getSetting, upsertSetting } from "./settings.js";
import { dropStopped } from "./packages.js";
import { runPurges } from "./purge.js";
import { foldDownloads, rotateSaltStatements } from "./stats.js";
import { IDLE_FIRST_PART, IDLE_NEXT_PART, IDLE_WHERE } from "./uploads.js";

export const HOURLY = "7 * * * *";
export const DAILY = "17 3 * * *";
export const CRONS = [HOURLY, DAILY];
export const PER_RUN = 30;
export const SWEEP_MAX = 1000; // one R2 delete call
const DAY = 86400;

async function job(name, fn, report) {
  try {
    report[name] = await fn();
  } catch (e) {
    report[name] = { error: true };
    console.error(`route=cron-${name} code=${(e && e.code) || "exception"}`);
  }
}

/** Idle uploads: aborted, their objects deleted, their reserved bytes released. */
export async function expireIdleUploads(env, t = now()) {
  const { results } = await env.DB.prepare(
    "SELECT id, r2_key, r2_upload_id FROM uploads WHERE (state = 'open' AND ((last_part_at IS NULL AND created_at < ?1) OR last_part_at < ?2)) " +
      "OR (state = 'completing' AND completing_at < ?2) LIMIT ?3",
  ).bind(t - IDLE_FIRST_PART, t - IDLE_NEXT_PART, PER_RUN).all();
  if (!results.length) return { expired: 0 };
  const ids = JSON.stringify(results.map((u) => u.id));
  const [r] = await env.DB.batch([
    env.DB.prepare(`UPDATE uploads SET state = 'expired', meta = NULL, updated_at = ?2 WHERE id IN (SELECT value FROM json_each(?1)) AND ${IDLE_WHERE}`)
      .bind(ids, t, t - IDLE_FIRST_PART, t - IDLE_NEXT_PART),
    env.DB.prepare("DELETE FROM upload_parts WHERE upload_id IN (SELECT id FROM uploads WHERE id IN (SELECT value FROM json_each(?1)) AND state = 'expired')").bind(ids),
  ]);
  await dropStopped(env, results);
  return { expired: r.meta.changes };
}

/** Deletes trash objects whose time is up and takes their bytes off storage_used. */
export async function emptyTrash(env, t = now()) {
  const { results } = await env.DB.prepare("SELECT r2_key, bytes FROM trash WHERE delete_after <= ?1 ORDER BY delete_after LIMIT ?2").bind(t, PER_RUN).all();
  if (!results.length) return { deleted: 0 };
  await env.FILES.delete(results.map((r) => r.r2_key));
  const bytes = results.reduce((s, r) => s + r.bytes, 0);
  await env.DB.batch([
    env.DB.prepare("DELETE FROM trash WHERE r2_key IN (SELECT value FROM json_each(?1))").bind(JSON.stringify(results.map((r) => r.r2_key))),
    env.DB.prepare("UPDATE settings SET v = CAST(max(0, CAST(v AS INTEGER) - CAST(?1 AS INTEGER)) AS TEXT) WHERE k = 'storage_used'").bind(bytes),
  ]);
  return { deleted: results.length, bytes };
}

/** Uploaders' pictures whose delay is over are shown (unless the owner refused them). */
export async function showDuePictures(env, t = now()) {
  const r = await env.DB.prepare(
    "UPDATE packages SET picture_state = 'shown', picture_due = NULL WHERE seq IN " +
      "(SELECT seq FROM packages WHERE picture_state = 'waiting' AND picture_due IS NOT NULL AND picture_due <= ?1 LIMIT ?2)",
  ).bind(t, PER_RUN).run();
  return { shown: r.meta.changes };
}

/** Keys with a live entry older than 7 days and no strikes become trusted (outside the whole-hub caps). */
export async function markTrusted(env, t = now()) {
  const r = await env.DB.prepare(
    "UPDATE uploaders SET trusted_at = ?1 WHERE seq IN (SELECT u.seq FROM uploaders u WHERE u.trusted_at IS NULL AND u.strikes = 0 AND u.status = 'ok' " +
      "AND EXISTS (SELECT 1 FROM packages p WHERE p.uploader_id = u.id AND p.status = 'live' AND p.created_at < ?2) LIMIT ?3)",
  ).bind(t, t - 7 * DAY, PER_RUN).run();
  return { trusted: r.meta.changes };
}

/**
 * storage_used from what's really stored: live and hidden entries, the trash, and everything in quarantine
 * (every version of a quarantined entry, listed from R2, so files the owner deleted there by hand drop out).
 */
export async function recountStorage(env) {
  let quarantined = 0, cursor;
  for (let page = 0; page < 10; page++) {
    const listing = await env.FILES.list({ prefix: "quarantine/", cursor, limit: 1000 });
    for (const o of listing.objects) quarantined += o.size;
    if (!listing.truncated) break;
    cursor = listing.cursor;
  }
  const used = await env.DB.prepare(
    "SELECT (SELECT coalesce(sum(bytes_stored), 0) FROM packages WHERE status IN ('live','hidden')) + " +
      "(SELECT coalesce(sum(bytes), 0) FROM trash) AS n",
  ).first("n");
  await upsertSetting(env.DB, "storage_used", used + quarantined).run();
  return { storageUsed: used + quarantined };
}

export async function cleanOldRows(env, t = now()) {
  const db = env.DB;
  const [a, b, c, d, e] = await db.batch([
    db.prepare("DELETE FROM reports WHERE (package_id, reporter_hash) IN (SELECT package_id, reporter_hash FROM reports WHERE resolved_at IS NOT NULL AND resolved_at < ?1 LIMIT 500)")
      .bind(t - 90 * DAY),
    db.prepare("DELETE FROM audit WHERE id IN (SELECT id FROM audit WHERE at < ?1 LIMIT 500)").bind(t - 365 * DAY),
    db.prepare("DELETE FROM uploads WHERE id IN (SELECT id FROM uploads WHERE state IN ('refused','aborted','expired','live') AND updated_at < ?1 LIMIT 500)")
      .bind(t - 30 * DAY),
    db.prepare("DELETE FROM thumbs WHERE package_id IN (SELECT id FROM packages WHERE status IN ('removed','deleted') AND removed_at < ?1 LIMIT 500)")
      .bind(t - 30 * DAY),
    db.prepare("DELETE FROM purge_queue WHERE id IN (SELECT id FROM purge_queue WHERE done_at IS NOT NULL AND done_at < ?1 LIMIT 500)").bind(t - 30 * DAY),
  ]);
  return { reports: a.meta.changes, audit: b.meta.changes, uploads: c.meta.changes, thumbs: d.meta.changes, purges: e.meta.changes };
}

/**
 * The daily orphan sweep of pkg/: an object that no package, trash row or running upload names is deleted
 * (one page of up to 1000 keys a run; the listing cursor is kept in settings across runs). Objects come from
 * crashes and from parts that arrived after their upload was stopped.
 *
 * The running uploads are read BEFORE the packages and the trash: an upload whose complete commits in
 * between is then seen by one query or the other, so a file that was just published is never taken for an
 * orphan.
 */
export async function sweepOrphans(env) {
  const db = env.DB;
  const cursor = (await getSetting(db, "orphan_cursor")) || undefined;
  const listing = await env.FILES.list({ prefix: "pkg/", cursor, limit: SWEEP_MAX });
  const keys = listing.objects.map((o) => o.key);
  const { results: open } = await db.prepare("SELECT r2_key FROM uploads WHERE state IN ('open','completing')").all();
  const { results: known } = await db.prepare(
    "SELECT j.value AS k FROM json_each(?1) j WHERE EXISTS (SELECT 1 FROM trash t WHERE t.r2_key = j.value) " +
      "OR EXISTS (SELECT 1 FROM packages p WHERE p.r2_key = j.value)",
  ).bind(JSON.stringify(keys)).all();
  const keep = new Set([...known.map((r) => r.k), ...open.map((r) => r.r2_key)]);
  const orphans = keys.filter((k) => !keep.has(k)).slice(0, SWEEP_MAX);
  if (orphans.length) await env.FILES.delete(orphans);
  await upsertSetting(db, "orphan_cursor", listing.truncated ? listing.cursor : "").run();
  if (orphans.length) console.error(`route=cron-orphans code=deleted count=${orphans.length}`);
  return { listed: keys.length, deleted: orphans.length };
}

export async function runCron(event, env, ctx) {
  const t = Math.floor((event && event.scheduledTime ? event.scheduledTime : Date.now()) / 1000);
  const cron = event && event.cron;
  const report = {};
  if (cron === HOURLY) {
    await job("expire", () => expireIdleUploads(env), report);
    await job("trash", () => emptyTrash(env), report);
    await job("purges", () => runPurges(env, ctx, { inScheduled: true }), report);
    await job("pictures", () => showDuePictures(env), report);
    if (Math.floor(t / 3600) % 6 === 0) await job("fold", () => foldDownloads(env), report);
  } else if (cron === DAILY) {
    await job("salt", async () => {
      const current = await getSetting(env.DB, "stats_salt");
      await env.DB.batch(rotateSaltStatements(env.DB, current));
      return { rotated: true };
    }, report);
    await job("trusted", () => markTrusted(env), report);
    await job("cleanup", () => cleanOldRows(env), report);
    await job("orphans", () => sweepOrphans(env), report);
    await job("storage", () => recountStorage(env), report);
  } else {
    // A trigger this code doesn't know (the dashboard's, or a config that drifted from CRONS).
    console.error("route=cron code=unknown_cron");
  }
  return report;
}
