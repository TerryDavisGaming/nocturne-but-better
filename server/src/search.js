// Listing and search SQL (DESIGN-HUB 2.12). Pure builders: every browse reads index-ordered facets, and every
// search is bounded to its best 200 FTS hits before the join.

import { base64urlDecode, base64urlEncode } from "./ids.js";
import { CARD_COLUMNS } from "./cards.js";
import { matchString } from "./names.js";

export const PAGE = 24;
export const SEARCH_HITS = 200;
export const MAX_OFFSET = SEARCH_HITS - PAGE - 1; // 175

export const SORTS = {
  new: { col: "created_at", dir: "DESC", index: "pk_new" },
  popular: { col: "downloads", dir: "DESC", index: "pk_pop" },
  title: { col: "title_key", dir: "ASC", index: "pk_title" },
};

export function encodeCursor(values) {
  return base64urlEncode(JSON.stringify(values));
}

/** Decodes and checks a cursor: browse [sortValue, seq], search ["o", offset]. Throws on anything else. */
export function decodeCursor(text, sort, searching) {
  if (text.length > 400) throw new Error("cursor");
  const v = JSON.parse(base64urlDecode(text));
  if (!Array.isArray(v) || v.length !== 2) throw new Error("cursor");
  if (searching) {
    if (v[0] !== "o" || !Number.isInteger(v[1]) || v[1] <= 0 || v[1] > MAX_OFFSET || v[1] % PAGE !== 0) throw new Error("cursor");
    return { offset: v[1] };
  }
  const [value, seq] = v;
  if (!Number.isInteger(seq) || seq < 0) throw new Error("cursor");
  if (sort === "title" ? typeof value !== "string" || value.length > 400 : !Number.isInteger(value)) throw new Error("cursor");
  return { value, seq };
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
export function nextBrowseCursor(row, sort) {
  return encodeCursor([row[SORTS[sort].col], row.seq]);
}
