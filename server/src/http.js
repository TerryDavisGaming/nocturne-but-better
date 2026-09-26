// Responses, errors, strict query strings and the write-request checks (DESIGN-HUB 2.2).

export class HubError extends Error {
  constructor(status, code, message, extra = {}, headers = {}) {
    super(code);
    this.status = status;
    this.code = code;
    this.userMessage = message;
    this.extra = extra;
    this.headers = headers;
  }
}

export const fail = (status, code, message, extra, headers) => {
  throw new HubError(status, code, message, extra, headers);
};

const COMMON = {
  "X-Content-Type-Options": "nosniff",
  "Referrer-Policy": "no-referrer",
};

export const CACHE_LIST = "public, max-age=60, stale-while-revalidate=300";
export const CACHE_FILE = "public, s-maxage=86400";
export const CACHE_INFO = "public, max-age=300";
export const NO_STORE = "no-store";

export const PAGE_CSP =
  "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
  "frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
export const FILE_CSP = "default-src 'none'; sandbox";

/** Adds the headers every response carries, and makes sure there's an explicit Cache-Control. */
export function finish(response) {
  const headers = new Headers(response.headers);
  for (const [k, v] of Object.entries(COMMON)) headers.set(k, v);
  if (!headers.has("Cache-Control")) headers.set("Cache-Control", NO_STORE);
  for (const name of [...headers.keys()]) if (name.toLowerCase().startsWith("access-control-")) headers.delete(name);
  return new Response(response.body, { status: response.status, statusText: response.statusText, headers });
}

export function json(body, status = 200, cache = NO_STORE, headers = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": cache, ...headers },
  });
}

export function noContent() {
  return new Response(null, { status: 204, headers: { "Cache-Control": NO_STORE } });
}

export function errorResponse(err) {
  const extra = err.extra || {};
  const body = { error: err.code, message: err.userMessage || "Something went wrong.", ...extra };
  const headers = { ...(err.headers || {}) };
  // Every 429 says when to try again, in the header as well as the body.
  if (err.status === 429 && Number.isFinite(extra.retryAfter) && !Object.keys(headers).some((k) => k.toLowerCase() === "retry-after")) {
    headers["Retry-After"] = String(Math.max(1, Math.ceil(extra.retryAfter)));
  }
  return json(body, err.status, NO_STORE, headers);
}

// ---- strict query strings ---------------------------------------------------------------------------

/**
 * Checks a query string against a fixed list of parameters in a fixed order, each at most once, with
 * canonical percent-encoding and normalized values; anything else is 400 bad_query before any D1 work.
 * `spec` is an ordered array of [name, normalize, options] where normalize(value) returns the canonical value
 * or throws. With `{ loose: true }` any value that normalizes is taken as its normalized form (the search
 * text: a client's own lower-casing may differ from JavaScript's). Returns an object of the values.
 */
export function strictQuery(rawUrl, spec) {
  const q = rawUrl.indexOf("?");
  const out = {};
  if (q < 0) return out;
  const hash = rawUrl.indexOf("#");
  const raw = rawUrl.slice(q + 1, hash < 0 ? undefined : hash);
  if (spec.length === 0 || raw.length === 0) badQuery("This address takes no query string.");
  let at = -1;
  for (const part of raw.split("&")) {
    const eq = part.indexOf("=");
    if (eq <= 0) badQuery();
    const name = part.slice(0, eq);
    const rawValue = part.slice(eq + 1);
    const index = spec.findIndex(([n]) => n === name);
    const loose = index >= 0 && Boolean(spec[index][2] && spec[index][2].loose);
    if (index < 0) badQuery(`Unknown parameter "${name.slice(0, 20)}".`);
    if (index <= at) badQuery("Parameters must come once each, in the documented order.");
    at = index;
    if (rawValue.length === 0) badQuery("Leave a parameter out instead of sending it empty.");
    let value;
    try {
      value = decodeURIComponent(rawValue);
    } catch {
      badQuery();
    }
    const canonical = name === "ids" ? value.split(",").map(encodeURIComponent).join(",") : encodeURIComponent(value);
    if (!loose && canonical !== rawValue) badQuery("A parameter isn't in its canonical encoding.");
    let normalized;
    try {
      normalized = spec[index][1](value);
    } catch (e) {
      badQuery(e instanceof HubError ? e.userMessage : `Bad value for "${name}".`);
    }
    if (!loose && normalized !== value) badQuery(`"${name}" isn't in its normalized form.`);
    out[name] = loose ? normalized : value;
  }
  return out;
}

function badQuery(message = "The query string isn't one this hub accepts.") {
  fail(400, "bad_query", message);
}

// ---- writes -----------------------------------------------------------------------------------------

const JSON_TYPES = new Set(["application/json", "application/json;charset=utf-8"]);

/**
 * Every POST, PUT, PATCH and DELETE must carry X-NBB-Client: 1, and a body-carrying write the exact
 * Content-Type. A browser has to preflight such a request, and OPTIONS answers 405, so a web page can't
 * make a browser send a write.
 */
export function checkWrite(request, contentType /* "json" | "octet" | null */) {
  if (request.headers.get("X-NBB-Client") !== "1") fail(400, "bad_client", "Writes need the X-NBB-Client header.");
  const type = (request.headers.get("Content-Type") || "").toLowerCase().replace(/\s+/g, "");
  if (contentType === "json" && !JSON_TYPES.has(type)) fail(400, "bad_client", "Writes need Content-Type: application/json.");
  if (contentType === "octet" && type !== "application/octet-stream") fail(400, "bad_client", "Parts need Content-Type: application/octet-stream.");
  if (contentType === null && type && !JSON_TYPES.has(type)) fail(400, "bad_client", "Unexpected Content-Type.");
}

/** Reads a JSON object body, refusing anything past `cap` bytes while reading. An empty body is {}. */
export async function readJson(request, cap = 8192) {
  const declared = request.headers.get("Content-Length");
  if (declared !== null && Number(declared) > cap) fail(413, "too_big", "The request body is too big.");
  if (!request.body) return {};
  const reader = request.body.getReader();
  const chunks = [];
  let size = 0;
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    size += value.byteLength;
    if (size > cap) {
      await reader.cancel().catch(() => {});
      fail(413, "too_big", "The request body is too big.");
    }
    chunks.push(value);
  }
  if (size === 0) return {};
  const bytes = new Uint8Array(size);
  let at = 0;
  for (const c of chunks) {
    bytes.set(c, at);
    at += c.byteLength;
  }
  let body;
  try {
    body = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
  } catch {
    fail(400, "bad_request", "The body isn't valid JSON.");
  }
  if (body === null || typeof body !== "object" || Array.isArray(body)) fail(400, "bad_request", "The body must be a JSON object.");
  return body;
}
