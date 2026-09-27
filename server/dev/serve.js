// A local stand-in for the hub (DESIGN-HUB 6.3): the REAL worker with the test fakes, on 127.0.0.1 only, with
// its database and files in a data folder. For in-game QA with NFS_QA_HUB_URL=http://127.0.0.1:8787.
//
//   deno run --no-remote --no-npm --allow-net=127.0.0.1:8787 --allow-read=<server>,<data> --allow-write=<data> \
//     server/dev/serve.js <data> [--seed] [--port 8787]
//
// It prints a throwaway owner key for /admin (made fresh every start). <data>/faults.json, read on every
// request, can make routes misbehave for QA (see the README).

import worker from "../src/worker.js";
import { FakeAnalytics, FakeCache, FakeD1, FakeR2, FakeRateLimiter } from "../test/fakes.js";
import { sha256Hex } from "../src/ids.js";
import { fingerprint, parseCentral, parseEnd } from "../src/zipcheck.js";
import { battleJson, goodBattle, goodPack, media, thumbB64 } from "../test/make-fixtures.js";

const args = [...Deno.args];
const seed = args.includes("--seed");
const portAt = args.indexOf("--port");
const port = portAt >= 0 ? Number(args[portAt + 1]) : 8787;
const dataDir = args.find((a, i) => !a.startsWith("--") && args[i - 1] !== "--port");
if (!dataDir) {
  console.error("usage: serve.js <data folder> [--seed] [--port 8787]");
  Deno.exit(2);
}
await Deno.mkdir(`${dataDir}/r2`, { recursive: true });

/** R2 kept in a folder, one file per object. Multipart parts stay in memory. */
class FolderR2 extends FakeR2 {
  constructor(dir) {
    super();
    this.dir = dir;
  }
  file(key) {
    return `${this.dir}/${encodeURIComponent(key)}`;
  }
  async load() {
    for await (const e of Deno.readDir(this.dir)) {
      if (!e.isFile) continue;
      const bytes = await Deno.readFile(`${this.dir}/${e.name}`);
      await super.put(decodeURIComponent(e.name), bytes);
    }
  }
  async put(key, value, options) {
    const meta = await super.put(key, value, options);
    await Deno.writeFile(this.file(key), this.objects.get(key).bytes);
    return meta;
  }
  async delete(keys) {
    await super.delete(keys);
    for (const k of Array.isArray(keys) ? keys : [keys]) await Deno.remove(this.file(k)).catch(() => {});
  }
  resumeMultipartUpload(key, uploadId) {
    const upload = super.resumeMultipartUpload(key, uploadId);
    const complete = upload.complete.bind(upload);
    upload.complete = async (parts) => {
      const meta = await complete(parts);
      await Deno.writeFile(this.file(key), this.objects.get(key).bytes);
      return meta;
    };
    return upload;
  }
}

const d1 = await FakeD1.create(`${dataDir}/hub.sqlite`);
d1.maxQueries = 50;
const r2 = new FolderR2(`${dataDir}/r2`);
await r2.load();
const ae = new FakeAnalytics();
const cache = new FakeCache();
const adminKey = [...crypto.getRandomValues(new Uint8Array(24))].map((b) => b.toString(16).padStart(2, "0")).join("");
const limiters = {
  RL_IP: new FakeRateLimiter(300), RL_SEARCH: new FakeRateLimiter(10), RL_NEWKEY: new FakeRateLimiter(3),
  RL_WRITE: new FakeRateLimiter(60), RL_INSTALL: new FakeRateLimiter(5), RL_REPORT: new FakeRateLimiter(5),
};
const env = () => ({ DB: d1.forInvocation(), FILES: r2, DL: ae, ...limiters, ADMIN_KEY: adminKey });
const ctx = () => ({ waitUntil() {}, passThroughOnException() {}, cache });

// ---- faults (QA): <data>/faults.json = { "rules": [ { "match": "GET /v1/files/", ... } ] } ----------------

async function readFaults() {
  try {
    return JSON.parse(await Deno.readTextFile(`${dataDir}/faults.json`));
  } catch {
    return { rules: [] };
  }
}

// Cloudflare's own error pages, as the mod may meet them: 1027 (the free plan's daily allowance is spent: "too
// busy today") and 1102 (one request went over its CPU or memory limit: "the hub had a problem").
const PAGE_1027 = "<!DOCTYPE html><html><head><title>This website has been temporarily rate limited | hub.nocturnbutbetter.com | Cloudflare</title></head>" +
  "<body><h1>Error 1027</h1><p>This website has been temporarily rate limited</p></body></html>";
const PAGE_1102 = "<!DOCTYPE html><html><head><title>Worker exceeded resource limits | hub.nocturnbutbetter.com | Cloudflare</title></head>" +
  "<body><h1>Error 1102</h1><p>Worker exceeded resource limits</p></body></html>";

function setPath(obj, path, value) {
  const parts = path.split(".");
  let at = obj;
  for (const p of parts.slice(0, -1)) at = at?.[p];
  if (at && typeof at === "object") at[parts[parts.length - 1]] = value;
}

async function applyFault(rule, run) {
  if (rule.delayMs) await new Promise((r) => setTimeout(r, rule.delayMs));
  if (rule.html1027) return new Response(PAGE_1027, { status: 503, headers: { "Content-Type": "text/html" } });
  if (rule.html1102) return new Response(PAGE_1102, { status: 503, headers: { "Content-Type": "text/html" } });
  if (rule.status) {
    const body = { error: rule.error ?? "fault", message: rule.message ?? "An injected fault." };
    const headers = { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" };
    if (rule.retryAfter) headers["Retry-After"] = String(rule.retryAfter);
    return new Response(JSON.stringify(body), { status: rule.status, headers });
  }
  const response = await run();
  if (rule.patchJson && (response.headers.get("Content-Type") || "").includes("application/json")) {
    const body = await response.json();
    for (const [path, value] of Object.entries(rule.patchJson)) {
      if (Array.isArray(body.items)) for (const item of body.items) setPath(item, path, value);
      else setPath(body, path, value);
    }
    return new Response(JSON.stringify(body), { status: response.status, headers: response.headers });
  }
  if (rule.truncateAt !== undefined || rule.flipByte !== undefined || rule.stallAfter !== undefined) {
    const bytes = new Uint8Array(await response.arrayBuffer());
    if (rule.flipByte !== undefined && rule.flipByte < bytes.length) bytes[rule.flipByte] ^= 0xff;
    const cut = rule.truncateAt !== undefined ? bytes.subarray(0, rule.truncateAt) : bytes;
    if (rule.stallAfter === undefined) return new Response(cut, { status: response.status, headers: response.headers });
    const stream = new ReadableStream({
      async start(c) {
        c.enqueue(cut.subarray(0, rule.stallAfter));
        await new Promise((r) => setTimeout(r, rule.stallMs ?? 120000));
        c.close();
      },
    });
    return new Response(stream, { status: response.status, headers: response.headers });
  }
  return response;
}

async function handle(request, info) {
  // The stand-in plays Cloudflare's part: the client's address, and nothing forwarded from the client.
  const headers = new Headers(request.headers);
  headers.set("CF-Connecting-IP", info.remoteAddr.hostname);
  const forwarded = new Request(request.url.replace(/^http:\/\/[^/]+/, `http://127.0.0.1:${port}`), {
    method: request.method, headers, body: request.body, duplex: "half",
  });
  const run = () => worker.fetch(forwarded, env(), ctx());
  const faults = await readFaults();
  const line = `${request.method} ${new URL(request.url).pathname}`;
  for (const rule of faults.rules || []) {
    if (!rule.match || !new RegExp("^" + rule.match.replace(/[.+?^${}()|[\]\\]/g, "\\$&").replace(/\*/g, "[^/]+")).test(line)) continue;
    if (rule.times !== undefined) {
      rule.used = (rule.used || 0) + 1;
      if (rule.used > rule.times) continue;
      await Deno.writeTextFile(`${dataDir}/faults.json`, JSON.stringify(faults, null, 2));
    }
    console.log(`fault: ${line}`);
    return applyFault(rule, run);
  }
  const response = await run();
  console.log(`${response.status} ${line}`);
  return response;
}

// ---- seeding: 60 sample entries through the real upload path -----------------------------------------------

async function call(method, path, key, json, bytes) {
  const headers = new Headers({ "X-NBB-Client": "1", "CF-Connecting-IP": "127.0.0.1" });
  if (key) headers.set("Authorization", `Bearer ${key}`);
  let body;
  if (bytes) {
    headers.set("Content-Type", "application/octet-stream");
    headers.set("Content-Length", String(bytes.length));
    body = bytes;
  } else if (method !== "GET") {
    headers.set("Content-Type", "application/json");
    body = JSON.stringify(json ?? {});
  }
  const seedLimits = {};
  for (const k of Object.keys(limiters)) seedLimits[k] = new FakeRateLimiter(1e9);
  const res = await worker.fetch(new Request(`http://127.0.0.1:${port}${path}`, { method, headers, body }), { ...env(), ...seedLimits }, ctx());
  return { status: res.status, body: await res.json().catch(() => null) };
}

const TITLES = [
  "Moonlit Duel", "\u6708\u5149\u306E\u30C7\u30E5\u30A8\u30EB", "\uB2EC\uBE5B \uACB0\uD22C", "Cafe\u0301 Showdown", "</noparse><size=500>BIG TITLE",
  "<color=#00000000>invisible</color>", "</NOPARSE><sprite=0>", "A very long title that goes on and on to test how the hub row cuts it off nicely",
  "\u202Eevil\u202C reversed", "\u3164\u3164 Filler", "Boss Rush", "Neon Rain", "Firefly Remix", "Crimson Waltz", "Last Light",
];

async function seedAll() {
  // Generous limits while seeding; only these keys are put back afterwards.
  const generous = [["probation_uploads_day", "100000"], ["uploads_per_key_day", "100000"], ["uploads_global_day", "100000"], ["attempts_global_day", "100000"],
    ["live_per_key", "100000"], ["probation_bytes_day", "100000000000"], ["bytes_per_key_day", "100000000000"]];
  const saved = generous.map(([k]) => d1.one("SELECT k, v FROM settings WHERE k = ?", k)).filter(Boolean);
  for (const [k, v] of generous) d1.sqlite.prepare("UPDATE settings SET v = ? WHERE k = ?").run(v, k);
  let made = 0;
  for (let u = 0; u < 6; u++) {
    const key = "nbbk1_" + `stand-in-seed-key-${u}`.padEnd(43, "_");
    await call("PUT", "/v1/me", key, { name: ["Mika", "Rin", "Kirara", "\u6708\u5149", "Seed Five", "</noparse>Six"][u] });
    d1.sqlite.prepare("UPDATE uploaders SET trusted_at = 1 WHERE key_hash = ?").run(await sha256Hex(key));
    for (let i = 0; i < 10; i++) {
      const n = u * 10 + i;
      const title = TITLES[n % TITLES.length] + (n >= TITLES.length ? ` ${n}` : "");
      const lanes = n % 3 === 0 ? 5 : 4;
      let bytes, kind = "battle", meta;
      if (n % 4 === 3) {
        kind = "charts";
        // Real song names; each pack's own title keeps its files (and so its fingerprint) unique.
        const songs = n % 8 === 7 ? ["Firefly - 1", "Firefly - 2"] : [`Firefly - ${1 + (n % 2)}`];
        bytes = await goodPack({ title, songs, lanes });
        meta = { description: "seeded difficulty pack", songs: songs.map((song) => ({ song, difficulties: [{ name: "Hard", level: 5 + (n % 9), notes: 300 + n }] })), requires: n === 7 ? ["future-feature"] : [] };
      } else {
        const id = crypto.randomUUID();
        bytes = await goodBattle({
          id, title, lanes, video: n % 5 === 0, gear: n % 6 === 0 ? { mode: "set", mainHand: "DBA1" } : undefined, level: n % 7 === 0 ? 12 : undefined,
          dialogue: n % 9 === 0 ? { lines: [] } : undefined, files: n % 10 === 0 ? { "art/idle.png": { data: media.png() } } : {},
          json: n === 1 ? battleJson({ id, title, lanes, extra: { source: { kind: "osu!mania", mapper: "Someone" } } }) : undefined,
        });
        meta = { description: n % 2 ? "seeded battle\nwith two lines" : "", difficulties: [{ name: "Easy", level: 2, notes: 200 }, { name: "Hard", level: 8 + (n % 5), notes: 800 + n }], lengthSeconds: 120 + n, bpm: [120, 120 + n], requires: [] };
      }
      const end = parseEnd(bytes.subarray(bytes.length - 22), bytes.length, 100000);
      const entries = parseCentral(bytes.subarray(end.cdOffset, end.cdOffset + end.cdSize), end);
      const start = await call("POST", "/v1/uploads", key, {
        clientUploadId: `seed-${n}-${crypto.randomUUID()}`, kind, packageId: null,
        file: { size: bytes.length, sha256: await sha256Hex(bytes), entriesSha256: await fingerprint(entries) },
        meta, thumb: n % 3 === 1 ? undefined : thumbB64({ width: 128, height: 128 }), rightsConfirmed: true, client: "2.7.0",
      });
      if (start.status !== 201) {
        console.error("seed start failed", n, JSON.stringify(start.body));
        continue;
      }
      await call("PUT", `/v1/uploads/${start.body.uploadId}/parts/1`, key, null, bytes);
      const done = await call("POST", `/v1/uploads/${start.body.uploadId}/complete`, key, {});
      if (done.status === 200) made++;
      else console.error("seed complete failed", n, JSON.stringify(done.body));
    }
  }
  for (const r of saved) d1.sqlite.prepare("UPDATE settings SET v = ? WHERE k = ?").run(r.v, r.k);
  console.log(`seeded ${made} entries`);
}

if (seed) await seedAll();

console.log(`nocturne but better hub (local stand-in) on http://127.0.0.1:${port}`);
console.log(`owner key for http://127.0.0.1:${port}/admin (this run only): ${adminKey}`);
Deno.serve({ hostname: "127.0.0.1", port, onListen() {} }, handle);
