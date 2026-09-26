// The test harness: the real worker with the fakes, a movable clock, fake keys, and an upload helper.
// Every key here is a fake made from a counter; no real secret is ever used.

import worker from "../src/worker.js";
import { FakeAnalytics, FakeCache, FakeD1, FakeR2, FakeRateLimiter } from "./fakes.js";
import { setClock, sha256Hex } from "../src/ids.js";
import { fingerprint, parseCentral, parseEnd } from "../src/zipcheck.js";

export const BASE_TIME = 1790380800; // 2026-09-26 00:00 UTC
export const FAKE_ADMIN = "fake-admin-key-for-tests-only-0123456789abcdef";
export const ORIGIN = "https://hub.nocturnbutbetter.com";

let clockSeconds = BASE_TIME;
setClock(() => clockSeconds * 1000);
export const setTime = (t) => (clockSeconds = t);
export const advance = (s) => (clockSeconds += s);
export const time = () => clockSeconds;

/** A fake player key (never a real one): nbbk1_ + 43 base64url characters. */
export function fakeKey(n) {
  const seed = `fake-key-${n}-`.padEnd(43, "x").slice(0, 43).replace(/[^A-Za-z0-9_-]/g, "_");
  return "nbbk1_" + seed;
}

export const LIMITS = { RL_IP: 300, RL_SEARCH: 10, RL_NEWKEY: 3, RL_WRITE: 60, RL_INSTALL: 5, RL_REPORT: 5 };

export async function makeHub(opts = {}) {
  setTime(opts.time ?? BASE_TIME);
  const d1 = await FakeD1.create();
  const r2 = new FakeR2();
  const ae = new FakeAnalytics();
  const cache = new FakeCache();
  const limiters = {};
  for (const [k, v] of Object.entries(LIMITS)) limiters[k] = new FakeRateLimiter(opts.limits?.[k] ?? v);
  const hub = {
    d1, r2, ae, cache, limiters, waits: [],
    vars: { ADMIN_KEY: FAKE_ADMIN, ...(opts.env || {}) },
    lastEnv: null,
    env() {
      const env = { DB: d1.forInvocation(), FILES: r2, DL: ae, ...limiters, ...hub.vars };
      for (const k of opts.drop || []) delete env[k];
      hub.lastEnv = env;
      return env;
    },
    ctx() {
      return { waitUntil: (p) => hub.waits.push(p), passThroughOnException() {}, cache };
    },
    /** Sends a request; returns { status, headers, body (JSON or bytes or text), response }. */
    async call(method, path, o = {}) {
      const headers = new Headers(o.headers || {});
      if (o.key) headers.set("Authorization", `Bearer ${o.key}`);
      if (o.admin) headers.set("Authorization", `Bearer ${o.admin === true ? FAKE_ADMIN : o.admin}`);
      if (!headers.has("CF-Connecting-IP") && o.ip !== null) headers.set("CF-Connecting-IP", o.ip ?? "203.0.113.7");
      let body;
      if (o.bytes !== undefined) {
        body = o.bytes;
        if (!o.noClient) headers.set("X-NBB-Client", "1");
        if (!headers.has("Content-Type")) headers.set("Content-Type", "application/octet-stream");
        if (!o.noLength) headers.set("Content-Length", String(o.length ?? o.bytes.length));
      } else if (o.json !== undefined || (method !== "GET" && method !== "DELETE" && method !== "OPTIONS" && !o.raw)) {
        body = JSON.stringify(o.json ?? {});
        if (!o.noClient) headers.set("X-NBB-Client", "1");
        if (!headers.has("Content-Type")) headers.set("Content-Type", "application/json");
      } else if (method === "DELETE" && !o.noClient) headers.set("X-NBB-Client", "1");
      if (o.raw !== undefined) body = o.raw;
      const request = new Request(ORIGIN + path, { method, headers, body });
      const env = hub.env();
      const response = await worker.fetch(request, env, hub.ctx());
      await Promise.all(hub.waits.splice(0));
      const type = response.headers.get("Content-Type") || "";
      let parsed;
      if (type.includes("application/json")) parsed = await response.json();
      else if (type.includes("text/")) parsed = await response.text();
      else parsed = response.body ? new Uint8Array(await response.arrayBuffer()) : null;
      return { status: response.status, headers: response.headers, body: parsed, response, env };
    },
    async cron(which) {
      const env = hub.env();
      const { runCron } = await import("../src/cron.js");
      const report = await runCron({ cron: which, scheduledTime: clockSeconds * 1000 }, env, hub.ctx());
      await Promise.all(hub.waits.splice(0));
      return report;
    },
    async scheduled(which) {
      await worker.scheduled({ cron: which, scheduledTime: clockSeconds * 1000 }, hub.env(), hub.ctx());
    },
  };
  return hub;
}

/** The directory fingerprint of zip bytes, the way the mod computes it before an upload. */
export async function zipFingerprint(bytes) {
  const size = bytes.length;
  const end = parseEnd(bytes.subarray(size - 22), size, 100000);
  const entries = parseCentral(bytes.subarray(end.cdOffset, end.cdOffset + end.cdSize), end);
  return fingerprint(entries);
}

export const difficulties = [{ name: "Hard", level: 8, notes: 812 }];

let uploadCounter = 0;

export function startBody(bytes, o = {}) {
  return {
    clientUploadId: o.clientUploadId ?? `b1f6-${++uploadCounter}-${"0".repeat(8)}`,
    kind: o.kind ?? "battle",
    packageId: o.packageId ?? null,
    file: { size: bytes.length, sha256: o.sha256, entriesSha256: o.entriesSha256 },
    meta: o.meta ?? (o.kind === "charts" ? { description: "", songs: (o.songs ?? ["Firefly - 1"]).map((song) => ({ song, difficulties })) } : { description: o.description ?? "a test battle", difficulties, lengthSeconds: 192.4, bpm: [128, 172], requires: [] }),
    thumb: o.thumb,
    rightsConfirmed: o.rightsConfirmed ?? true,
    client: o.client ?? "2.7.0",
  };
}

/** Registers a key with a name (PUT /v1/me). */
export async function register(hub, key, name = "Bryce", ip) {
  return hub.call("PUT", "/v1/me", { key, json: { name }, ip });
}

/** start + every part + complete; returns { start, parts, complete }. */
export async function upload(hub, key, bytes, o = {}) {
  const sha256 = o.sha256 ?? (await sha256Hex(bytes));
  const entriesSha256 = o.entriesSha256 ?? (await zipFingerprint(bytes));
  const start = await hub.call("POST", "/v1/uploads", { key, json: startBody(bytes, { ...o, sha256, entriesSha256 }) });
  if (start.status !== 201 && start.status !== 200) return { start };
  const { uploadId, partSize, parts } = start.body;
  const results = [];
  for (let n = 1; n <= parts; n++) {
    if (o.skipParts && o.skipParts.includes(n)) continue;
    const slice = bytes.subarray((n - 1) * partSize, Math.min(bytes.length, n * partSize));
    results.push(await hub.call("PUT", `/v1/uploads/${uploadId}/parts/${n}`, { key, bytes: slice }));
  }
  if (o.noComplete) return { start, parts: results };
  const complete = await hub.call("POST", `/v1/uploads/${uploadId}/complete`, { key, json: {} });
  return { start, parts: results, complete };
}

/** Registers a key and publishes a battle; returns the package id. */
export async function publish(hub, key, bytes, o = {}) {
  const r = await upload(hub, key, bytes, o);
  if (!r.complete || r.complete.status !== 200) throw new Error("publish failed: " + JSON.stringify((r.complete || r.start).body));
  return r.complete.body.packageId;
}

// ---- seeding the catalogue directly (fast, for listing and query-plan tests) ----------------------------

import { indexText, titleKey } from "../src/names.js";
import { objectKey } from "../src/ids.js";

/** The object key a seeded package's version lives at. */
export const seedKey = (id, version = 1) => objectKey(id, version, "upseed" + "0".repeat(10));

let seedCounter = 0;

/** Inserts an uploader row directly; returns its id. */
export function seedUploader(hub, o = {}) {
  const n = ++seedCounter;
  const id = o.id ?? "u" + String(n).padStart(12, "0").replace(/[ilou]/g, "0");
  hub.d1.sqlite.prepare("INSERT INTO uploaders (id, key_hash, name, created_at, status, strikes, trusted_at) VALUES (?, ?, ?, ?, ?, ?, ?)")
    .run(id, o.keyHash ?? "seed-" + n, o.name ?? `Seeder ${n}`, o.created ?? BASE_TIME - 30 * 86400, o.status ?? "ok", o.strikes ?? 0, o.trusted ?? null);
  return id;
}

const ALPH = "0123456789abcdefghjkmnpqrstvwxyz";
export function seedId(n) {
  let s = "";
  for (let i = 0; i < 10; i++) {
    s = ALPH[n % 32] + s;
    n = Math.floor(n / 32);
  }
  return s;
}

/**
 * Inserts packages directly with their search rows. fields(i) returns overrides: kind, lanes, title, artist,
 * author, description, songs, created, downloads, status, uploader, picture, thumb.
 */
export function seedPackages(hub, count, fields = () => ({})) {
  const db = hub.d1.sqlite;
  const uploader = seedUploader(hub);
  const ins = db.prepare(
    "INSERT INTO packages (id, kind, uploader_id, status, version, title, title_key, artist, author, description, lanes, difficulties, songs, battle_id, " +
      "flags, contents, format, file_size, file_sha256, fingerprint, entries, picture_state, bytes_stored, created_at, updated_at, downloads, r2_key) " +
      "VALUES (?, ?, ?, ?, 1, ?, ?, ?, ?, ?, ?, '[]', ?, ?, '{}', '{}', 2, 1000, ?, ?, 4, ?, 1000, ?, ?, ?, ?)",
  );
  const fts = db.prepare("INSERT INTO packages_fts (rowid, title, artist, author, songs, description) VALUES (?, ?, ?, ?, ?, ?)");
  const thumb = db.prepare("INSERT INTO thumbs (package_id, version, b64) VALUES (?, 1, ?)");
  const ids = [];
  db.exec("BEGIN");
  for (let i = 0; i < count; i++) {
    const f = fields(i);
    const n = ++seedCounter;
    const id = f.id ?? seedId(1000000 + n);
    const kind = f.kind ?? (i % 2 ? "charts" : "battle");
    const title = f.title ?? `Seeded ${i}`;
    const songs = kind === "charts" ? JSON.stringify((f.songs ?? ["Firefly - 1"]).map((song) => ({ song, difficulties: [] }))) : null;
    const status = f.status ?? "live";
    const r = ins.run(id, kind, f.uploader ?? uploader, status, title, titleKey(title), f.artist ?? "Artist", f.author ?? "Charter", f.description ?? "",
      f.lanes ?? (i % 4 < 2 ? 4 : 5), songs, kind === "battle" ? f.battleId ?? crypto.randomUUID() : null, "0".repeat(64), f.fingerprint ?? "f" + n,
      f.picture ?? "shown", f.created ?? BASE_TIME - 1000 + i, f.created ?? BASE_TIME - 1000 + i, f.downloads ?? 0, seedKey(id));
    const seq = Number(r.lastInsertRowid);
    if (status === "live" || status === "hidden") {
      const songText = songs ? JSON.parse(songs).map((s) => s.song).join(" ") : "";
      fts.run(seq, indexText(title), indexText(f.artist ?? "Artist"), indexText(f.author ?? "Charter"), indexText(songText), indexText(f.description ?? ""));
    }
    if (f.thumb) thumb.run(id, f.thumb);
    ids.push(id);
  }
  db.exec("COMMIT");
  return ids;
}

/** Puts a fake object for a seeded package into R2. */
export async function seedFile(hub, id, version = 1, bytes = new Uint8Array(1000)) {
  await hub.r2.put(seedKey(id, version), bytes);
}
