// Ids, patterns and the clock. Ids are random lower-case Crockford base32; no user text is ever part of
// an id or an R2 key (DESIGN-HUB 2.2).

export const ALPHABET = "0123456789abcdefghjkmnpqrstvwxyz";

export const PKG_ID = /^[0-9a-hjkmnp-tv-z]{10}$/;
export const UPLOADER_ID = /^u[0-9a-hjkmnp-tv-z]{12}$/;
export const UPLOAD_ID = /^up[0-9a-hjkmnp-tv-z]{16}$/;
export const BATTLE_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
export const HEX64 = /^[0-9a-f]{64}$/;
export const VERSION = /^[1-9][0-9]{0,8}$/;
export const CLIENT_UPLOAD_ID = /^[A-Za-z0-9-]{8,64}$/;

export function randomId(length) {
  // 256 is a multiple of 32, so masking keeps every character equally likely.
  const bytes = crypto.getRandomValues(new Uint8Array(length));
  let out = "";
  for (const b of bytes) out += ALPHABET[b & 31];
  return out;
}

export const newPackageId = () => randomId(10);
export const newUploaderId = () => "u" + randomId(12);
export const newUploadId = () => "up" + randomId(16);

/** The 4-character tag shown after an uploader's name ("Bryce #7K2M"). */
export const tagOf = (uploaderId) => String(uploaderId).slice(1, 5).toUpperCase();

/**
 * Where an upload's bytes live in R2: one key per upload, never shared. A part that is still arriving after
 * its upload was stopped can then only land on that dead upload's own key, never on a file another upload
 * has already checked and published. The key is stored on the upload, the package and any trash row.
 */
export const objectKey = (packageId, version, uploadId) => `pkg/${packageId}/${version}/${uploadId}`;
export const quarantineKey = (packageId, version) => `quarantine/${packageId}/${version}/package`;

/** The package id and version an object key belongs to, or null. */
export function keyParts(key) {
  const m = /^pkg\/([0-9a-hjkmnp-tv-z]{10})\/([1-9][0-9]{0,8})\/[^/]+$/.exec(String(key));
  return m ? { packageId: m[1], version: Number(m[2]) } : null;
}

/** The [from, to) key range that holds every object of one version (or, without a version, of one package). */
export function keyRange(packageId, version) {
  const prefix = version === undefined ? `pkg/${packageId}/` : `pkg/${packageId}/${version}/`;
  return [prefix, prefix.slice(0, -1) + "0"]; // "0" sorts right after "/"
}

export function hex(bytes) {
  const b = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  let out = "";
  for (const x of b) out += x.toString(16).padStart(2, "0");
  return out;
}

export async function sha256Hex(data) {
  const bytes = typeof data === "string" ? new TextEncoder().encode(data) : data;
  return hex(await crypto.subtle.digest("SHA-256", bytes));
}

export function base64urlEncode(text) {
  const bytes = new TextEncoder().encode(text);
  let bin = "";
  for (const b of bytes) bin += String.fromCharCode(b);
  return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function base64urlDecode(text) {
  if (!/^[A-Za-z0-9_-]*$/.test(text)) throw new Error("bad base64url");
  const pad = text.length % 4 === 2 ? "==" : text.length % 4 === 3 ? "=" : text.length % 4 === 1 ? null : "";
  if (pad === null) throw new Error("bad base64url");
  const bin = atob(text.replace(/-/g, "+").replace(/_/g, "/") + pad);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
}

export function base64ToBytes(b64) {
  const bin = atob(b64);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return bytes;
}

// ---- the clock (tests move it) ----------------------------------------------------------------------

let clock = () => Date.now();

/** Unix seconds. */
export const now = () => Math.floor(clock() / 1000);

/** Tests only: replace the clock with a function returning milliseconds. */
export function setClock(fn) {
  clock = fn || (() => Date.now());
}
