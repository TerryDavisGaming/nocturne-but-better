// Cache purges (DESIGN-HUB 2.12). Workers Cache purges always use the free-tier limits (5 a minute, a bucket
// of 25, 100 tags a request), so purges happen only for owner actions and uploader deletes. Each purge is
// written to purge_queue first, then tried; pending tags are merged up to 100 a call, and the hourly cron
// and the owner's "retry purges" button try again.

import { now } from "./ids.js";

export const MAX_TAGS = 100;

/** The purge function for this invocation, or null when there's no cache API to call. */
export async function purger(ctx, inScheduled = false) {
  if (ctx && ctx.cache && typeof ctx.cache.purge === "function") return (tags) => ctx.cache.purge({ tags });
  if (inScheduled) {
    try {
      const name = "cloudflare:" + "workers";
      const mod = await import(name);
      if (mod && mod.cache && typeof mod.cache.purge === "function") return (tags) => mod.cache.purge({ tags });
    } catch {
      // not in the Workers runtime, or no cache module: purges stay queued
    }
  }
  return null;
}

/** Queues tags (in the same batch as the change when `into` is given) and returns the statement. */
export function queueStatement(db, tags) {
  return db.prepare("INSERT INTO purge_queue (tags, created_at) VALUES (?1, ?2)").bind(JSON.stringify([...new Set(tags)]), now());
}

/**
 * Tries the pending purges, oldest first, merging their tags up to 100. Returns
 * { attempted, done, failed, pending } counts.
 */
export async function runPurges(env, ctx, { inScheduled = false, limit = 30 } = {}) {
  const { results } = await env.DB.prepare("SELECT id, tags FROM purge_queue WHERE done_at IS NULL ORDER BY id LIMIT ?1").bind(limit).all();
  if (results.length === 0) return { attempted: 0, done: 0, failed: 0 };
  const ids = [];
  const tags = new Set();
  for (const r of results) {
    let list = [];
    try {
      list = JSON.parse(r.tags);
    } catch {
      list = [];
    }
    const merged = new Set([...tags, ...list]);
    if (merged.size > MAX_TAGS && ids.length > 0) break;
    for (const t of list) tags.add(t);
    ids.push(r.id);
  }
  const purge = await purger(ctx, inScheduled);
  let error = null;
  if (!purge) error = "no_cache_api";
  else {
    try {
      const result = await purge([...tags].slice(0, MAX_TAGS));
      if (result && result.success === false) error = "purge_refused";
    } catch (e) {
      error = /rate|limit|429/i.test(String(e && e.message)) ? "rate_limited" : "purge_failed";
    }
  }
  const list = JSON.stringify(ids);
  if (!error) {
    await env.DB.prepare("UPDATE purge_queue SET done_at = ?1, tries = tries + 1, last_error = NULL WHERE id IN (SELECT value FROM json_each(?2))")
      .bind(now(), list).run();
    return { attempted: ids.length, done: ids.length, failed: 0 };
  }
  await env.DB.prepare("UPDATE purge_queue SET tries = tries + 1, last_error = ?1 WHERE id IN (SELECT value FROM json_each(?2))").bind(error, list).run();
  if (error !== "no_cache_api") console.error(`route=purge code=${error}`);
  return { attempted: ids.length, done: 0, failed: ids.length, error };
}

export const listTags = (ids) => ["list", ...ids.map((id) => `pkg-${id}`)];
