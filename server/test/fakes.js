// Stand-ins for the Cloudflare bindings (DESIGN-HUB 6.1), so the real worker runs under Deno with nothing
// installed: FakeD1 on node:sqlite with D1's API and limits, FakeR2 with ranged gets and multipart,
// FakeRateLimiter, FakeCache (the purge API with the free-tier purge limits), FakeAnalytics with a fake SQL
// API, and a console catcher.

import { DatabaseSync } from "node:sqlite";
import { now, sha256Hex } from "../src/ids.js";

const MIGRATIONS = new URL("../migrations/", import.meta.url);

export async function migrationFiles() {
  const names = [];
  for await (const e of Deno.readDir(MIGRATIONS)) if (e.isFile && e.name.endsWith(".sql")) names.push(e.name);
  names.sort();
  return Promise.all(names.map((n) => Deno.readTextFile(new URL(n, MIGRATIONS))));
}

let migrationCache = null;

// ---- D1 --------------------------------------------------------------------------------------------------

const FORBIDDEN = /^\s*(BEGIN|COMMIT|END|ROLLBACK|SAVEPOINT|RELEASE|ATTACH|DETACH|VACUUM)\b/i;
const WRITE_PRAGMA = /^\s*PRAGMA\s+[\w.]+\s*=/i;

export class FakeD1 {
  constructor(path = ":memory:") {
    this.sqlite = new DatabaseSync(path);
    this.limitSpent = false;
    this.down = false;
    this.failNextBatch = null; // an Error to throw from the next batch, once
    this.beforeBatch = null; // a function run once just before the next batch (to stage a race)
    this.statements = []; // every statement run: { sql, changes }
    this.maxQueries = 50;
  }

  static async create(path = ":memory:") {
    const d1 = new FakeD1(path);
    migrationCache ??= await migrationFiles();
    const has = d1.sqlite.prepare("SELECT count(*) AS n FROM sqlite_master WHERE name = 'packages'").get().n;
    if (!has) for (const sql of migrationCache) d1.sqlite.exec(sql);
    return d1;
  }

  /** The DB binding for one Worker invocation (queries are counted per invocation). */
  forInvocation() {
    return new D1Binding(this);
  }

  sizeAfter() {
    const pages = this.sqlite.prepare("PRAGMA page_count").get().page_count;
    const size = this.sqlite.prepare("PRAGMA page_size").get().page_size;
    return pages * size;
  }

  /** Test helper: run SQL directly (not counted). */
  q(sql, ...params) {
    return this.sqlite.prepare(sql).all(...params).map((r) => ({ ...r }));
  }
  one(sql, ...params) {
    const r = this.sqlite.prepare(sql).get(...params);
    return r ? { ...r } : null;
  }
}

class D1Binding {
  constructor(fake) {
    this.fake = fake;
    this.count = 0;
  }
  prepare(sql) {
    return new D1Statement(this, sql, []);
  }
  countQueries(n) {
    this.count += n;
    if (this.count > this.fake.maxQueries) throw new Error("D1_ERROR: Too many API requests by single worker invocation. (more than 50 queries)");
  }
  checkState() {
    if (this.fake.down) throw new Error("D1_ERROR: Network connection lost.");
    if (this.fake.limitSpent) throw new Error("D1_ERROR: Exceeded the daily rows read limit of the free plan. The limit resets at 00:00 UTC.");
  }
  execute(stmt) {
    const sql = stmt.sql;
    if (new TextEncoder().encode(sql).length > 100000) throw new Error("D1_ERROR: statement too long: SQLITE_TOOBIG");
    if (stmt.params.length > 100) throw new Error("D1_ERROR: too many SQL variables: SQLITE_ERROR");
    if (FORBIDDEN.test(sql) || WRITE_PRAGMA.test(sql)) throw new Error("D1_ERROR: not authorized: SQLITE_AUTH");
    let prepared;
    try {
      prepared = this.fake.sqlite.prepare(sql);
    } catch (e) {
      throw new Error("D1_ERROR: " + e.message + ": SQLITE_ERROR");
    }
    const read = /^\s*(SELECT|WITH|EXPLAIN|PRAGMA|VALUES)\b/i.test(sql);
    let results = [], changes = 0, lastRow = 0;
    try {
      if (read) results = prepared.all(...stmt.params).map((r) => ({ ...r }));
      else {
        const r = prepared.run(...stmt.params);
        changes = Number(r.changes);
        lastRow = Number(r.lastInsertRowid);
      }
    } catch (e) {
      throw new Error("D1_ERROR: " + e.message + ": SQLITE_CONSTRAINT");
    }
    this.fake.statements.push({ sql, changes });
    return {
      success: true,
      results,
      meta: { changes, last_row_id: lastRow, changed_db: changes > 0, size_after: this.fake.sizeAfter(), rows_read: 0, rows_written: changes, duration: 0 },
    };
  }
  async batch(statements) {
    this.checkState();
    this.countQueries(statements.length);
    if (this.fake.beforeBatch) {
      const f = this.fake.beforeBatch;
      this.fake.beforeBatch = null;
      f(statements);
    }
    if (this.fake.failNextBatch) {
      const e = this.fake.failNextBatch;
      this.fake.failNextBatch = null;
      throw e;
    }
    const db = this.fake.sqlite;
    db.exec("BEGIN");
    try {
      const out = statements.map((s) => this.execute(s));
      db.exec("COMMIT");
      return out;
    } catch (e) {
      db.exec("ROLLBACK");
      throw e;
    }
  }
  async exec(sql) {
    this.checkState();
    this.countQueries(1);
    this.fake.sqlite.exec(sql);
    return { count: 1, duration: 0 };
  }
}

class D1Statement {
  constructor(binding, sql, params) {
    this.binding = binding;
    this.sql = sql;
    this.params = params;
  }
  bind(...values) {
    const params = values.map((v, i) => {
      if (v === undefined) throw new Error(`D1_TYPE_ERROR: Type 'undefined' not supported for value 'undefined' (parameter ${i + 1})`);
      if (v === null || typeof v === "string" || typeof v === "bigint") return v;
      if (typeof v === "number") {
        if (!Number.isFinite(v)) throw new Error("D1_TYPE_ERROR: number not finite");
        return v;
      }
      if (typeof v === "boolean") return v ? 1 : 0;
      if (v instanceof ArrayBuffer) return new Uint8Array(v);
      if (ArrayBuffer.isView(v)) return new Uint8Array(v.buffer, v.byteOffset, v.byteLength);
      throw new Error(`D1_TYPE_ERROR: Type '${typeof v}' not supported`);
    });
    return new D1Statement(this.binding, this.sql, params);
  }
  run1() {
    this.binding.checkState();
    this.binding.countQueries(1);
    return this.binding.execute(this);
  }
  async first(column) {
    const row = this.run1().results[0] ?? null;
    if (column === undefined) return row;
    if (row === null) return null;
    if (!(column in row)) throw new Error(`D1_COLUMN_NOTFOUND: Column not found (${column})`);
    return row[column];
  }
  async all() {
    return this.run1();
  }
  async run() {
    return this.run1();
  }
  async raw(options = {}) {
    const r = this.run1().results;
    const rows = r.map((row) => Object.values(row));
    if (options.columnNames) rows.unshift(r.length ? Object.keys(r[0]) : []);
    return rows;
  }
}

// ---- R2 ---------------------------------------------------------------------------------------------------

export async function toBytes(value) {
  if (value === null || value === undefined) return new Uint8Array(0);
  if (typeof value === "string") return new TextEncoder().encode(value);
  if (value instanceof ArrayBuffer) return new Uint8Array(value.slice(0));
  if (ArrayBuffer.isView(value)) return new Uint8Array(value.buffer.slice(value.byteOffset, value.byteOffset + value.byteLength));
  if (value instanceof Blob) return new Uint8Array(await value.arrayBuffer());
  if (value instanceof ReadableStream) return new Uint8Array(await new Response(value).arrayBuffer());
  throw new Error("FakeR2: unsupported value");
}

const MiB = 1024 * 1024;
let etagCounter = 0;

function r2Object(key, bytes, extra = {}) {
  const etag = (++etagCounter).toString(16).padStart(32, "0");
  return { key, size: bytes.length, etag, httpEtag: `"${etag}"`, uploaded: new Date(now() * 1000), httpMetadata: {}, customMetadata: {}, version: etag, ...extra };
}

function withBody(obj, bytes, range) {
  return {
    ...obj,
    range,
    get body() {
      return new Blob([bytes]).stream();
    },
    bodyUsed: false,
    arrayBuffer: async () => bytes.slice().buffer,
    bytes: async () => bytes.slice(),
    text: async () => new TextDecoder().decode(bytes),
    json: async () => JSON.parse(new TextDecoder().decode(bytes)),
    blob: async () => new Blob([bytes]),
  };
}

export class FakeR2 {
  constructor() {
    this.objects = new Map(); // key -> { meta, bytes }
    this.multipart = new Map(); // uploadId -> { key, parts: Map(n -> { etag, bytes }) }
    this.ops = { get: 0, head: 0, put: 0, delete: 0, list: 0, uploadPart: 0, complete: 0, abort: 0, create: 0 };
    this.fail = {}; // name -> true: the next call of that operation throws
  }

  maybeFail(op) {
    if (this.fail[op]) {
      delete this.fail[op];
      throw new Error(`FakeR2: injected ${op} failure`);
    }
  }

  async put(key, value, options = {}) {
    this.ops.put++;
    this.maybeFail("put");
    const bytes = await toBytes(value);
    const hashes = ["md5", "sha1", "sha256", "sha384", "sha512"].filter((h) => options[h] !== undefined);
    if (hashes.length > 1) throw new Error("put: You can only specify one hashing algorithm. (10029)");
    if (options.sha256 !== undefined) {
      const want = typeof options.sha256 === "string" ? options.sha256.toLowerCase() : [...new Uint8Array(options.sha256)].map((b) => b.toString(16).padStart(2, "0")).join("");
      if ((await sha256Hex(bytes)) !== want) throw new Error("put: The SHA-256 checksum you specified did not match what we received. (10037)");
    }
    const meta = r2Object(key, bytes);
    this.objects.set(key, { meta, bytes });
    return meta;
  }

  async get(key, options = {}) {
    this.ops.get++;
    this.maybeFail("get");
    const o = this.objects.get(key);
    if (!o) return null;
    let offset = 0, length = o.bytes.length;
    const r = options.range;
    if (r) {
      if (r.suffix !== undefined) {
        offset = Math.max(0, o.bytes.length - r.suffix);
        length = o.bytes.length - offset;
      } else {
        offset = r.offset ?? 0;
        if (offset > o.bytes.length) throw new Error("get: The requested range is not satisfiable. (10039)");
        length = Math.min(r.length ?? o.bytes.length - offset, o.bytes.length - offset);
      }
    }
    const bytes = o.bytes.subarray(offset, offset + length);
    return withBody(o.meta, bytes, r ? { offset, length } : undefined);
  }

  async head(key) {
    this.ops.head++;
    this.maybeFail("head");
    const o = this.objects.get(key);
    return o ? { ...o.meta } : null;
  }

  async delete(keys) {
    this.ops.delete++;
    this.maybeFail("delete");
    const list = Array.isArray(keys) ? keys : [keys];
    if (list.length > 1000) throw new Error("delete: Too many keys (max 1000).");
    for (const k of list) this.objects.delete(k);
  }

  async list(options = {}) {
    this.ops.list++;
    const prefix = options.prefix ?? "";
    const limit = Math.min(options.limit ?? 1000, 1000);
    const keys = [...this.objects.keys()].filter((k) => k.startsWith(prefix)).sort();
    let start = 0;
    if (options.cursor) start = keys.findIndex((k) => k > atob(options.cursor));
    if (start < 0) start = keys.length;
    const page = keys.slice(start, start + limit);
    const truncated = start + limit < keys.length;
    return {
      objects: page.map((k) => ({ ...this.objects.get(k).meta })),
      truncated,
      cursor: truncated ? btoa(page[page.length - 1]) : undefined,
      delimitedPrefixes: [],
    };
  }

  async createMultipartUpload(key) {
    this.ops.create++;
    this.maybeFail("create");
    const uploadId = "mpu-" + crypto.randomUUID();
    this.multipart.set(uploadId, { key, parts: new Map() });
    return this.resumeMultipartUpload(key, uploadId);
  }

  resumeMultipartUpload(key, uploadId) {
    const r2 = this;
    const state = () => {
      const s = r2.multipart.get(uploadId);
      if (!s || s.key !== key) throw new Error("uploadPart: The specified multipart upload does not exist. (10024)");
      return s;
    };
    return {
      key,
      uploadId,
      async uploadPart(n, value, options = {}) {
        r2.ops.uploadPart++;
        r2.maybeFail("uploadPart");
        if (Object.keys(options).some((k) => !["httpMetadata", "customMetadata", "storageClass", "ssecKey"].includes(k))) throw new Error("uploadPart: bad option");
        if (!(n >= 1 && n <= 10000)) throw new Error("uploadPart: part number out of range");
        const bytes = await toBytes(value);
        const etag = "part-" + (++etagCounter);
        state().parts.set(n, { etag, bytes });
        return { partNumber: n, etag };
      },
      async abort() {
        r2.ops.abort++;
        state();
        r2.multipart.delete(uploadId);
      },
      async complete(parts) {
        r2.ops.complete++;
        r2.maybeFail("complete");
        const s = state();
        const sorted = [...parts].sort((a, b) => a.partNumber - b.partNumber);
        const chunks = [];
        for (let i = 0; i < sorted.length; i++) {
          const p = s.parts.get(sorted[i].partNumber);
          if (!p || p.etag !== sorted[i].etag) throw new Error("complete: One or more of the specified parts could not be found. (10025)");
          const last = i === sorted.length - 1;
          if (!last && p.bytes.length < 5 * MiB) throw new Error("complete: Your proposed upload is smaller than the minimum allowed object size. (10011)");
          if (!last && p.bytes.length !== s.parts.get(sorted[0].partNumber).bytes.length) throw new Error("complete: All non-trailing parts must have the same length. (10048)");
          chunks.push(p.bytes);
        }
        const total = chunks.reduce((n, c) => n + c.length, 0);
        const bytes = new Uint8Array(total);
        let at = 0;
        for (const c of chunks) {
          bytes.set(c, at);
          at += c.length;
        }
        const meta = r2Object(key, bytes);
        r2.objects.set(key, { meta, bytes });
        r2.multipart.delete(uploadId);
        return meta;
      },
    };
  }
}

// ---- rate limits, cache purges, Analytics Engine ----------------------------------------------------------

export class FakeRateLimiter {
  constructor(limit, period = 60) {
    this.limit_ = limit;
    this.period = period;
    this.counts = new Map();
    this.calls = 0;
  }
  async limit({ key }) {
    this.calls++;
    const window = Math.floor(now() / this.period);
    const k = `${window}|${key}`;
    const n = (this.counts.get(k) ?? 0) + 1;
    this.counts.set(k, n);
    return { success: n <= this.limit_ };
  }
}

export class FakeCache {
  constructor() {
    this.purged = []; // arrays of tags
    this.failing = false;
    this.tokens = 25;
    this.at = now();
  }
  async purge({ tags }) {
    const t = now();
    this.tokens = Math.min(25, this.tokens + ((t - this.at) / 60) * 5);
    this.at = t;
    if (!Array.isArray(tags) || tags.length === 0 || tags.length > 100) throw new Error("purge: 1 to 100 tags");
    if (this.failing) throw new Error("purge failed: internal error");
    if (this.tokens < 1) throw new Error("purge: rate limited (429)");
    this.tokens -= 1;
    this.purged.push([...tags]);
    return { success: true };
  }
}

export class FakeAnalytics {
  constructor() {
    this.points = [];
  }
  writeDataPoint(point) {
    if ((point.indexes || []).length > 1) throw new Error("writeDataPoint: at most one index");
    for (const b of point.blobs || []) if (new TextEncoder().encode(String(b)).length > 16384) throw new Error("writeDataPoint: blob too big");
    this.points.push({ ...point, timestamp: now() });
  }
  /** A fetch function that answers the Analytics Engine SQL API from the recorded points. */
  sqlApi(expect = {}) {
    return async (_url, init) => {
      if (expect.token && init.headers.Authorization !== `Bearer ${expect.token}`) return new Response("{}", { status: 403 });
      const sql = String(init.body);
      const m = /timestamp > toDateTime\((\d+)\) AND timestamp <= toDateTime\((\d+)\)/.exec(sql);
      const from = Number(m[1]), to = Number(m[2]);
      const counts = new Map();
      for (const p of this.points) {
        if (p.timestamp <= from || p.timestamp > to) continue;
        const [id, who] = p.blobs;
        if (!counts.has(id)) counts.set(id, new Set());
        counts.get(id).add(who);
      }
      const data = [...counts].map(([id, set]) => ({ id, n: String(set.size) }));
      return new Response(JSON.stringify({ meta: [{ name: "id" }, { name: "n" }], data, rows: data.length }), { status: 200 });
    };
  }
}

/** Catches console.error and console.log lines while `fn` runs. */
export async function captureConsole(fn) {
  const lines = [];
  const saved = { error: console.error, log: console.log, warn: console.warn, info: console.info };
  for (const k of Object.keys(saved)) console[k] = (...args) => lines.push(args.map(String).join(" "));
  try {
    await fn();
  } finally {
    Object.assign(console, saved);
  }
  return lines;
}
