// Listing and search SQL (DESIGN-HUB 2.12). Pure builders: every browse reads index-ordered facets, and every
// search is bounded to its best 200 FTS hits before the join. Page cursors are signed, so only the pages the
// hub handed out can be asked for: a made-up cursor would otherwise be a fresh cache miss every time.

import { base64urlDecode, base64urlEncode } from "./ids.js";
import { CARD_COLUMNS } from "./cards.js";
import { TITLE_KEY_POINTS, matchString } from "./names.js";

export const PAGE = 24;
export const SEARCH_HITS = 200;
export const MAX_OFFSET = SEARCH_HITS - PAGE - 1; // 175

export const SORTS = {
  new: { col: "created_at", dir: "DESC", index: "pk_new" },
  popular: { col: "downloads", dir: "DESC", index: "pk_pop" },
  title: { col: "title_key", dir: "ASC", index: "pk_title" },
};

export const MAX_CURSOR = 1024;
const MAC_BYTES = 16;
let macCache = { secret: null, key: null };

async function macKey(secret) {
  if (!secret) throw new Error("no cursor key");
  if (macCache.secret !== secret) {
    const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
    macCache = { secret, key };
  }
  return macCache.key;
}

function bytesToBase64url(bytes) {
  let bin = "";
  for (const b of bytes) bin += String.fromCharCode(b);
  return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

async function mac(secret, scope, payload) {
  const sig = await crypto.subtle.sign("HMAC", await macKey(secret), new TextEncoder().encode(scope + "|" + payload));
  return bytesToBase64url(new Uint8Array(sig).subarray(0, MAC_BYTES));
}

/**
 * A page cursor: base64url(JSON) + "." + a MAC over the list it belongs to (`scope`: its filters, text and
 * sort), so a cursor only works on the list that handed it out.
 */
export async function encodeCursor(values, secret, scope) {
  const payload = base64urlEncode(JSON.stringify(values));
  return payload + "." + (await mac(secret, scope, payload));
}

/**
 * Decodes and checks a cursor: browse [sortValue, seq], search ["o", offset]. Its shape is checked first, and
 * only then its MAC, with the key from `getSecret()` (one small read). Throws on anything else.
 */
export async function decodeCursor(text, sort, searching, getSecret, scope) {
  if (text.length > MAX_CURSOR) throw new Error("cursor");
  const dot = text.indexOf(".");
  if (dot <= 0) throw new Error("cursor");
  const payload = text.slice(0, dot);
  const v = JSON.parse(base64urlDecode(payload));
  if (!Array.isArray(v) || v.length !== 2) throw new Error("cursor");
  let out;
  if (searching) {
    if (v[0] !== "o" || !Number.isInteger(v[1]) || v[1] <= 0 || v[1] > MAX_OFFSET || v[1] % PAGE !== 0) throw new Error("cursor");
    out = { offset: v[1] };
  } else {
    const [value, seq] = v;
    if (!Number.isInteger(seq) || seq < 0) throw new Error("cursor");
    if (sort === "title" ? typeof value !== "string" || [...value].length > TITLE_KEY_POINTS : !Number.isInteger(value)) throw new Error("cursor");
    out = { value, seq };
  }
  const want = await mac(await getSecret(), scope, payload);
  const given = text.slice(dot + 1);
  let diff = want.length ^ given.length;
  for (let i = 0; i < want.length; i++) diff |= want.charCodeAt(i) ^ (given.charCodeAt(i) || 0);
  if (diff !== 0) throw new Error("cursor");
  return out;
}

/** The (kind, lanes) facets a filter allows. */
export function facets(kind, lanes) {
  const kinds = kind ? [kind] : ["battle", "charts"];
  const laneList = lanes ? [lanes] : [4, 5];
  const out = [];
  for (const k of kinds) for (const l of laneList) out.push([k, l]);
  return out;
}

const OUTER = "SELECT page.*, u.name AS uploader_name, CASE WHEN page.picture_state = 'shown' THEN t.b64 END AS thumb " +
  "FROM page LEFT JOIN uploaders u ON u.id = page.uploader_id LEFT JOIN thumbs t ON t.package_id = page.id ";

/** Browse without text: one index-ordered subquery per facet, merged. */
export function browseQuery({ kind, lanes, sort, cursor }) {
  const s = SORTS[sort];
  const cmp = s.dir === "DESC" ? "<" : ">";
  const order = `${s.col} ${s.dir}, seq ${s.dir}`;
  const params = [];
  const arms = facets(kind, lanes).map(([k, l]) => {
    params.push(k, l);
    let where = `status = 'live' AND kind = ?${params.length - 1} AND lanes = ?${params.length}`;
    if (cursor) {
      params.push(cursor.value, cursor.seq);
      where += ` AND (${s.col}, seq) ${cmp} (?${params.length - 1}, ?${params.length})`;
    }
    const cols = CARD_COLUMNS.replace(/p\./g, "");
    return `SELECT * FROM (SELECT ${cols} FROM packages INDEXED BY ${s.index} WHERE ${where} ORDER BY ${order} LIMIT ${PAGE + 1})`;
  });
  const outerOrder = `page.${s.col} ${s.dir}, page.seq ${s.dir}`;
  const sql = `WITH page AS (${arms.join(" UNION ALL ")} ORDER BY ${order} LIMIT ${PAGE + 1}) ${OUTER}ORDER BY ${outerOrder}`;
  return { sql, params };
}

/** More by this uploader: their live entries, newest first. */
export function uploaderQuery({ uploader, kind, lanes, cursor }) {
  const params = [uploader];
  let where = "p.uploader_id = ?1 AND p.status = 'live'";
  if (kind) {
    params.push(kind);
    where += ` AND p.kind = ?${params.length}`;
  }
  if (lanes) {
    params.push(lanes);
    where += ` AND p.lanes = ?${params.length}`;
  }
  if (cursor) {
    params.push(cursor.value, cursor.seq);
    where += ` AND (p.created_at, p.seq) < (?${params.length - 1}, ?${params.length})`;
  }
  const sql = `WITH page AS (SELECT ${CARD_COLUMNS} FROM packages p INDEXED BY pk_owner WHERE ${where} ` +
    `ORDER BY p.created_at DESC, p.seq DESC LIMIT ${PAGE + 1}) ${OUTER}ORDER BY page.created_at DESC, page.seq DESC`;
  return { sql, params };
}

/**
 * Text search: the best 200 hits by rank first, then the join, filters and chosen order. CROSS JOIN keeps
 * SQLite from starting at a packages index instead, which would read every live entry of a facet.
 */
export function searchQuery({ q, kind, lanes, uploader, sort, offset }) {
  const params = [matchString(q)];
  let where = "p.status = 'live'";
  if (kind) {
    params.push(kind);
    where += ` AND p.kind = ?${params.length}`;
  }
  if (lanes) {
    params.push(lanes);
    where += ` AND p.lanes = ?${params.length}`;
  }
  if (uploader) {
    params.push(uploader);
    where += ` AND p.uploader_id = ?${params.length}`;
  }
  const order = sort === "best" ? "hits.rank, p.seq" : sort === "title" ? "p.title_key, p.seq" : `p.${SORTS[sort].col} DESC, p.seq DESC`;
  params.push(offset);
  const sql =
    `WITH hits AS (SELECT rowid AS seq, rank FROM packages_fts WHERE packages_fts MATCH ?1 ORDER BY rank LIMIT ${SEARCH_HITS}) ` +
    `SELECT ${CARD_COLUMNS}, u.name AS uploader_name, CASE WHEN p.picture_state = 'shown' THEN t.b64 END AS thumb ` +
    `FROM hits CROSS JOIN packages p ON p.seq = hits.seq LEFT JOIN uploaders u ON u.id = p.uploader_id ` +
    `LEFT JOIN thumbs t ON t.package_id = p.id WHERE ${where} ORDER BY ${order} LIMIT ${PAGE + 1} OFFSET ?${params.length}`;
  return { sql, params };
}

/** The next-page cursor for a browse page, from its last row. */
export function nextBrowseCursor(row, sort, secret, scope) {
  return encodeCursor([row[SORTS[sort].col], row.seq], secret, scope);
}
