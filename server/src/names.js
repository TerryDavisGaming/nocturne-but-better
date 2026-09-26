// Text cleaning, shared with the mod (DESIGN-HUB 2.2). Pure functions.
//
// Every free-text field: NFC; control characters, bidi overrides and isolates, U+061C and invisible
// characters removed; whitespace collapsed and trimmed (descriptions keep single line breaks); then capped
// at a number of Unicode code points.

export const LIMITS = { title: 100, artist: 100, author: 64, packTitle: 100, description: 1000, name: 32, note: 500, song: 100, difficulty: 32 };

// C0 (except tab, LF, CR, which become whitespace), DEL and C1; soft hyphen; combining grapheme joiner;
// Arabic letter mark; Hangul fillers; Mongolian vowel separator; zero-width and directional marks;
// bidi embeddings, overrides and isolates; word joiner and invisible operators; BOM; tag characters.
export const INVISIBLE =
  /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F\u00AD\u034F\u061C\u115F\u1160\u180E\u200B-\u200F\u202A-\u202E\u2060-\u2064\u2066-\u2069\u3164\uFEFF\uFFA0\u{E0000}-\u{E007F}]/gu;

export function capPoints(text, max) {
  const points = [...text];
  return points.length <= max ? text : points.slice(0, max).join("").trimEnd();
}

/** One line of text: null when it isn't a string. */
export function cleanLine(value, max) {
  if (typeof value !== "string") return null;
  const text = value.normalize("NFC").replace(INVISIBLE, "").replace(/\s+/gu, " ").trim();
  return capPoints(text, max);
}

/** Multi-line text (descriptions): single line breaks kept, at most one empty line in a row. */
export function cleanText(value, max) {
  if (typeof value !== "string") return null;
  const lines = value
    .normalize("NFC")
    .replace(/\r\n?/g, "\n")
    .replace(INVISIBLE, "")
    .split("\n")
    .map((line) => line.replace(/\s+/gu, " ").trim());
  const text = lines.join("\n").replace(/\n{3,}/g, "\n\n").trim();
  return capPoints(text, max);
}

const RESERVED_NAMES = new Set(["admin", "owner", "moderator", "hub"]);

/** A display name, or null when it's refused (empty, only punctuation or symbols, or reserved). */
export function cleanName(value) {
  const name = cleanLine(value, LIMITS.name);
  if (!name) return null;
  if (!/[\p{L}\p{N}]/u.test(name)) return null;
  if (RESERVED_NAMES.has(name.toLowerCase().replace(/[^\p{L}\p{N}]/gu, ""))) return null;
  return name;
}

export const TITLE_KEY_POINTS = 64;

/**
 * The Title sort key: NFKD, marks removed, lower case, cut to its first 64 code points. NFKD can make a title
 * much longer (one ligature becomes 18 letters), and the key travels in page cursors; the sort only needs a
 * prefix, since the row's seq breaks ties.
 */
export function titleKey(title) {
  const key = String(title).normalize("NFKD").replace(/\p{M}/gu, "").toLowerCase();
  const points = [...key];
  return points.length <= TITLE_KEY_POINTS ? key : points.slice(0, TITLE_KEY_POINTS).join("");
}

// ---- search text (DESIGN-HUB 1.3) -------------------------------------------------------------------

export const SEARCH_MAX_WORDS = 4;
export const SEARCH_MIN_WORD = 2;
export const SEARCH_PREFIX_MIN = 3;
export const SEARCH_MAX_WORD = 32;

/** Words of a text the way the hub indexes and searches it: lower case; letters, digits and marks. */
export function searchWords(text) {
  return String(text)
    .normalize("NFC")
    .replace(INVISIBLE, "")
    .toLowerCase()
    .split(/[^\p{L}\p{N}\p{M}]+/u)
    .filter((w) => w.length > 0);
}

/** The indexed form of a field: its words joined by single spaces. */
export function indexText(text) {
  return searchWords(text || "").join(" ");
}

/**
 * The normalized search the mod sends as q=: at most 4 words, each at least 2 characters (shorter ones
 * dropped), at most 32 characters each, joined by single spaces. Returns "" when nothing is left.
 */
export function normalizeSearch(text) {
  const words = searchWords(text)
    .filter((w) => [...w].length >= SEARCH_MIN_WORD)
    .map((w) => [...w].slice(0, SEARCH_MAX_WORD).join(""))
    .slice(0, SEARCH_MAX_WORDS);
  return words.join(" ");
}

/** The FTS5 MATCH string for a normalized search: each word quoted, the last one a prefix at 3+ chars. */
export function matchString(normalized) {
  const words = normalized.split(" ").filter(Boolean);
  return words
    .map((w, i) => (i === words.length - 1 && [...w].length >= SEARCH_PREFIX_MIN ? `"${w}"*` : `"${w}"`))
    .join(" ");
}
