// Public package routes: list and search, lookup, detail, the file itself, and the verified-install count
// (DESIGN-HUB 2.3, 2.12). Plus the uploader's own delete.

import { CACHE_FILE, CACHE_LIST, FILE_CSP, HubError, checkWrite, json, noContent, readJson, strictQuery } from "./http.js";
import { PKG_ID, UPLOADER_ID, hex, keyRange, now } from "./ids.js";
import { card, detail, goneStatus } from "./cards.js";
import { normalizeSearch } from "./names.js";
import { MAX_OFFSET, PAGE, browseQuery, decodeCursor, encodeCursor, nextBrowseCursor, searchQuery, uploaderQuery } from "./search.js";
import { blockedIds, classifyDbError, clientIp, countsOn, ipKey, rateLimit, readOnly, refuseReadOnly } from "./guard.js";
import { recordInstall } from "./stats.js";
import { getSetting } from "./settings.js";
import { requireUploader } from "./auth.js";
import { listTags, queueStatement, runPurges } from "./purge.js";

const oneOf = (...values) => (v) => {
  if (!values.includes(v)) throw new Error("bad");
  return v;
};

const LIST_SPEC = [
  ["kind", oneOf("battle", "charts")],
  ["lanes", oneOf("4", "5")],
  ["uploader", (v) => {
    if (!UPLOADER_ID.test(v)) throw new Error("bad");
    return v;
  }],
  // Any text is taken and searched in its normalized form; sending exactly that form keeps the cache useful.
  ["q", (v) => {
    if (v.length > MAX_RAW_SEARCH) throw new HubError(400, "bad_query", "The search is too long.");
    const n = normalizeSearch(v);
    if (!n) throw new HubError(400, "bad_query", "Search for words of at least 2 letters.");
    return n;
  }, { loose: true }],
  ["sort", oneOf("popular", "title", "new", "best")],
  ["cursor", (v) => v],
];

const MAX_RAW_SEARCH = 200;

/** The key that signs page cursors (made by the migration; made here if a database lacks it). */
export async function cursorSecret(env) {
  let secret = await getSetting(env.DB, "cursor_key");
  if (!secret) {
    await env.DB.prepare("INSERT INTO settings (k, v) VALUES ('cursor_key', ?1) ON CONFLICT(k) DO UPDATE SET v = excluded.v WHERE settings.v = ''")
      .bind(hex(crypto.getRandomValues(new Uint8Array(32)))).run();
    secret = await getSetting(env.DB, "cursor_key");
  }
  return secret;
}

export async function listPackages(env, request) {
  const p = strictQuery(request.url, LIST_SPEC);
  const searching = p.q !== undefined;
  // Defaults are left out, so each list has exactly one address: sort=new without text, best with text.
  if ((!searching && (p.sort === "new" || p.sort === "best")) || (searching && p.sort === "best")) {
    throw new HubError(400, "bad_query", "Leave out the default sort.");
  }
  const sort = p.sort ?? (searching ? "best" : "new");
  if (p.uploader && !searching && sort !== "new") throw new HubError(400, "bad_query", "An uploader's list is sorted newest first.");
  // A cursor is signed for exactly this list, so only pages the hub handed out can be asked for.
  const scope = [p.kind ?? "", p.lanes ?? "", p.uploader ?? "", p.q ?? "", sort].join("|");
  let secret = null;
  const getSecret = async () => (secret ??= await cursorSecret(env));
  let cursor = null;
  if (p.cursor !== undefined) {
    try {
      cursor = await decodeCursor(p.cursor, sort, searching, getSecret, scope);
    } catch (e) {
      if (classifyDbError(e)) throw e;
      throw new HubError(400, "bad_query", "That page cursor isn't valid.");
    }
  }
  if (searching) await rateLimit(env, "RL_SEARCH", ipKey(clientIp(request)));
  const lanes = p.lanes ? Number(p.lanes) : null;
  const counts = countsOn(env);
  let rows, next = null;
  if (searching) {
    const offset = cursor ? cursor.offset : 0;
    const { sql, params } = searchQuery({ q: p.q, kind: p.kind, lanes, uploader: p.uploader, sort, offset });
    rows = (await env.DB.prepare(sql).bind(...params).all()).results;
    if (rows.length > PAGE && offset + PAGE <= MAX_OFFSET) next = await encodeCursor(["o", offset + PAGE], await getSecret(), scope);
  } else if (p.uploader) {
    const { sql, params } = uploaderQuery({ uploader: p.uploader, kind: p.kind, lanes, cursor });
    rows = (await env.DB.prepare(sql).bind(...params).all()).results;
    if (rows.length > PAGE) next = await nextBrowseCursor(rows[PAGE - 1], "new", await getSecret(), scope);
  } else {
    const { sql, params } = browseQuery({ kind: p.kind, lanes, sort, cursor });
    rows = (await env.DB.prepare(sql).bind(...params).all()).results;
    if (rows.length > PAGE) next = await nextBrowseCursor(rows[PAGE - 1], sort, await getSecret(), scope);
  }
  const items = rows.slice(0, PAGE).map((r) => card(r, counts));
  return json({ items, next }, 200, CACHE_LIST, { "Cache-Tag": "list" });
}

const LOOKUP_SPEC = [
  ["ids", (v) => {
    const ids = v.split(",");
    if (ids.length === 0 || ids.length > 50 || ids.some((id) => !PKG_ID.test(id))) throw new Error("bad");
    const sorted = [...new Set(ids)].sort();
    return sorted.join(",");
  }],
];

export async function lookupPackages(env, request) {
  const p = strictQuery(request.url, LOOKUP_SPEC);
  if (!p.ids) throw new HubError(400, "bad_query", "Give ids=a,b,c (sorted, at most 50).");
  const ids = p.ids.split(",");
  const { results } = await env.DB.prepare(
    "SELECT id, status, version, updated_at, title, file_sha256, removed_reason FROM packages WHERE id IN (SELECT value FROM json_each(?1))",
  ).bind(JSON.stringify(ids)).all();
  const found = new Map(results.map((r) => [r.id, r]));
  const items = ids.map((id) => {
    const r = found.get(id);
    if (!r) return { id, status: "missing" };
    if (r.status === "live") return { id, status: "live", version: r.version, updatedAt: r.updated_at, title: r.title, sha256: r.file_sha256 };
    const status = goneStatus(r.status);
    const item = { id, status, version: r.version, updatedAt: r.updated_at, title: r.title };
    if (status === "removed" && r.status === "removed") item.reason = r.removed_reason;
    return item;
  });
  return json({ items }, 200, CACHE_LIST, { "Cache-Tag": "list" });
}

function refuseBlocked(env, id) {
  if (blockedIds(env).has(id)) throw new HubError(410, "unavailable", "This entry isn't available.", { reason: null });
}

function gone(row) {
  const status = goneStatus(row.status);
  const message = status === "deleted" ? "Its uploader deleted this entry." : status === "unavailable" ? "This entry is under review." : "The hub's owner removed this entry.";
  throw new HubError(410, status, message, { reason: row.status === "removed" ? row.removed_reason : null });
}

export async function packageDetail(env, request, id) {
  strictQuery(request.url, []);
  refuseBlocked(env, id);
  const row = await env.DB.prepare(
    "SELECT p.*, u.name AS uploader_name, CASE WHEN p.picture_state = 'shown' THEN t.b64 END AS thumb " +
      "FROM packages p LEFT JOIN uploaders u ON u.id = p.uploader_id LEFT JOIN thumbs t ON t.package_id = p.id WHERE p.id = ?1",
  ).bind(id).first();
  if (!row) throw new HubError(404, "not_found", "There's no such entry.");
  if (row.status !== "live") gone(row);
  return json(detail(row, countsOn(env)), 200, CACHE_LIST, { "Cache-Tag": `pkg-${id}` });
}

export function fileHeaders(id, kind, cache) {
  const ext = kind === "charts" ? "nbbchart" : "nbbbattle";
  return {
    "Content-Type": "application/octet-stream",
    "Content-Disposition": `attachment; filename="${id}.${ext}"`,
    "Content-Security-Policy": FILE_CSP,
    "Cross-Origin-Resource-Policy": "same-origin",
    "Cache-Control": cache,
  };
}

export async function packageFile(env, request, id, version) {
  strictQuery(request.url, []);
  refuseBlocked(env, id);
  const row = await env.DB.prepare("SELECT id, kind, status, version, file_sha256, removed_reason, r2_key FROM packages WHERE id = ?1").bind(id).first();
  if (!row) throw new HubError(404, "not_found", "There's no such entry.");
  if (row.status !== "live") gone(row);
  if (version > row.version) throw new HubError(404, "not_found", "There's no such version.");
  // Old versions stay downloadable while they wait in the trash (24 h), so running downloads finish.
  const key = version === row.version ? row.r2_key : await trashKeyOf(env.DB, id, version);
  const obj = key ? await env.FILES.get(key) : null;
  if (!obj) throw new HubError(404, "not_found", "There's no such version.");
  const headers = fileHeaders(id, row.kind, CACHE_FILE);
  headers["Cache-Tag"] = `pkg-${id}`;
  if (version === row.version) headers.ETag = `"${row.file_sha256}"`;
  return new Response(obj.body, { status: 200, headers });
}

/** The trash row's object key for an old version of a package, or null. */
export async function trashKeyOf(db, id, version) {
  const [from, to] = keyRange(id, version);
  return db.prepare("SELECT r2_key FROM trash WHERE r2_key >= ?1 AND r2_key < ?2 LIMIT 1").bind(from, to).first("r2_key");
}

export async function packageInstalled(env, request, id) {
  checkWrite(request, "json");
  const ip = ipKey(clientIp(request));
  await rateLimit(env, "RL_INSTALL", ip);
  const body = await readJson(request, 1024);
  if (!Number.isInteger(body.version) || body.version < 1 || body.version > 2147483647) throw new HubError(400, "bad_request", "Give the version installed.");
  await recordInstall(env, id, ip, !readOnly(env));
  return noContent();
}

/** DELETE /v1/packages/:id: the uploader removes their own entry for everyone (the files wait 24 h). */
export async function deleteOwnPackage(env, request, ctx, id) {
  checkWrite(request, null);
  refuseReadOnly(env);
  const { uploader } = await requireUploader(env, request, { allowBanned: true });
  await rateLimit(env, "RL_WRITE", "key:" + uploader.id);
  const row = await env.DB.prepare("SELECT id, uploader_id, status, version, bytes_stored, seq, r2_key FROM packages WHERE id = ?1").bind(id).first();
  if (!row) throw new HubError(404, "not_found", "There's no such entry.");
  if (row.uploader_id !== uploader.id) throw new HubError(403, "not_yours", "This entry was uploaded with another hub key.");
  if (row.status !== "live" && row.status !== "hidden") gone(row);
  const t = now();
  const db = env.DB;
  const open = await db.prepare("SELECT id, r2_key, r2_upload_id FROM uploads WHERE package_id = ?1 AND state IN ('open','completing')").bind(id).first();
  await db.batch([
    db.prepare("SELECT json((SELECT CASE WHEN count(*) = 1 THEN '1' ELSE 'guard: package changed' END FROM packages WHERE id = ?1 AND uploader_id = ?2 AND status IN ('live','hidden')))")
      .bind(id, uploader.id),
    db.prepare("UPDATE packages SET status = 'deleted', removed_at = ?2 WHERE id = ?1").bind(id, t),
    db.prepare("DELETE FROM packages_fts WHERE rowid = ?1").bind(row.seq),
    db.prepare("INSERT OR REPLACE INTO trash (r2_key, bytes, delete_after, why) VALUES (?1, ?2, ?3, 'deleted by uploader')")
      .bind(row.r2_key, row.bytes_stored, t + 86400),
    db.prepare("UPDATE uploads SET state = 'aborted', meta = NULL, updated_at = ?2 WHERE package_id = ?1 AND state IN ('open','completing')").bind(id, t),
    db.prepare("INSERT INTO audit (at, actor, action, package_id, uploader_id) VALUES (?1, 'uploader', 'delete', ?2, ?3)").bind(t, id, uploader.id),
    queueStatement(db, listTags([id])),
  ]);
  if (open) await dropStopped(env, [open]);
  await runPurges(env, ctx);
  return noContent();
}

/**
 * Drops the objects of uploads that the database now shows as stopped (aborted, expired or refused).
 * Always change the state first and drop second, so an upload that went live meanwhile keeps its file.
 */
export async function dropStopped(env, uploads) {
  if (!uploads.length) return;
  const { results } = await env.DB.prepare(
    "SELECT id, r2_key, r2_upload_id FROM uploads WHERE id IN (SELECT value FROM json_each(?1)) AND state IN ('aborted','expired','refused')",
  ).bind(JSON.stringify(uploads.map((u) => u.id))).all();
  for (const u of results) await dropUploadObject(env, u).catch(() => {});
}

/** Aborts an upload's multipart upload and deletes its object (both free in R2). */
export async function dropUploadObject(env, upload) {
  if (upload.r2_upload_id) {
    try {
      await env.FILES.resumeMultipartUpload(upload.r2_key, upload.r2_upload_id).abort();
    } catch {
      // already completed or aborted
    }
  }
  await env.FILES.delete(upload.r2_key);
}
