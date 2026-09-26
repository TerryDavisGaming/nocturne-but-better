// Writes need the client header and the exact content type; OPTIONS is 405; no CORS; errors log only the
// route and a code, never a key, an address or a header; the D1 per-invocation query limit holds.

import { assert, assertEquals } from "./assert.js";
import { FAKE_ADMIN, fakeKey, makeHub, publish, register, seedFile, seedPackages, startBody } from "./helpers.js";
import { captureConsole } from "./fakes.js";
import { goodBattle } from "./make-fixtures.js";

const WRITES = [
  ["PUT", "/v1/me"], ["POST", "/v1/me/rotate"], ["POST", "/v1/uploads"], ["PUT", "/v1/uploads/up0000000000000000/parts/1"],
  ["POST", "/v1/uploads/up0000000000000000/complete"], ["DELETE", "/v1/uploads/up0000000000000000"], ["DELETE", "/v1/packages/0000000000"],
  ["POST", "/v1/packages/0000000000/installed"], ["POST", "/v1/packages/0000000000/report"], ["POST", "/v1/admin/packages/0000000000/hide"],
  ["PUT", "/v1/admin/settings"],
];

Deno.test("writes: without X-NBB-Client every write is 400 before anything runs", async () => {
  const hub = await makeHub();
  const before = hub.d1.statements.length;
  const calls = hub.limiters.RL_IP.calls;
  for (const [method, path] of WRITES) {
    const r = await hub.call(method, path, { key: fakeKey(1), json: {}, noClient: true });
    assertEquals([r.status, r.body.error], [400, "bad_client"], method + " " + path);
  }
  assertEquals(hub.d1.statements.length, before);
  assertEquals(hub.limiters.RL_IP.calls, calls);
});

Deno.test("writes: text/plain (a browser's simple request) is 400; OPTIONS (a preflight) is 405", async () => {
  const hub = await makeHub();
  for (const type of ["text/plain", "application/x-www-form-urlencoded", "multipart/form-data; boundary=x", "application/json5"]) {
    const r = await hub.call("PUT", "/v1/me", { key: fakeKey(1), raw: '{"name":"x"}', headers: { "Content-Type": type, "X-NBB-Client": "1" } });
    assertEquals([r.status, r.body.error], [400, "bad_client"], type);
  }
  const ok = await hub.call("PUT", "/v1/me", { key: fakeKey(1), raw: '{"name":"x"}', headers: { "Content-Type": "application/json; charset=utf-8", "X-NBB-Client": "1" } });
  assertEquals(ok.status, 200);
  for (const path of ["/v1/info", "/v1/packages", "/v1/uploads", "/admin", "/"]) {
    const r = await hub.call("OPTIONS", path, { headers: { Origin: "https://evil.example", "Access-Control-Request-Method": "POST" } });
    assertEquals(r.status, 405, path);
    for (const [k] of r.headers) assert(!k.toLowerCase().startsWith("access-control-"), k);
  }
});

Deno.test("logs: a route that fails returns the 500 body and logs only its route and a code", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  await register(hub, key);
  const [id] = seedPackages(hub, 1);
  await seedFile(hub, id);
  const lines = await captureConsole(async () => {
    hub.r2.fail.get = true;
    const f = await hub.call("GET", `/v1/files/${id}/1/package`, { ip: "2001:db8:dead:beef::1", headers: { "User-Agent": "NocturneButBetter/2.8.0 (BepInEx)", Authorization: `Bearer ${key}` } });
    assertEquals([f.status, f.body.error], [500, "server_error"]);
    assertEquals(Object.keys(f.body).sort(), ["error", "message"]);
    hub.r2.fail.uploadPart = true;
    const bytes = await goodBattle({ song: new Uint8Array(9 * 1024 * 1024).fill(1) });
    const s = await hub.call("POST", "/v1/uploads", { key, json: { clientUploadId: "log-test-0001", kind: "battle", file: { size: bytes.length, sha256: "a".repeat(64), entriesSha256: "b".repeat(64) }, meta: { difficulties: [{ name: "H", level: 1, notes: 1 }] }, rightsConfirmed: true, client: "2.8.0" }, ip: "198.51.100.77" });
    const p = await hub.call("PUT", `/v1/uploads/${s.body.uploadId}/parts/1`, { key, bytes: bytes.subarray(0, 8388608), ip: "198.51.100.77" });
    assertEquals(p.status, 500);
    hub.d1.failNextBatch = new Error("D1_ERROR: something odd with nbbk1_ and 198.51.100.77 inside");
    await hub.call("POST", `/v1/admin/packages/${id}/hide`, { admin: true, json: { note: "x" }, ip: "198.51.100.78" });
  });
  assert(lines.includes("route=file code=exception"), lines.join("\n"));
  assert(lines.includes("route=upload-part code=r2_part_failed"), lines.join("\n"));
  assert(lines.includes("route=admin code=exception"), lines.join("\n"));
  const all = lines.join("\n");
  for (const secret of [key, "nbbk1_", "2001:db8", "198.51.100", "BepInEx", FAKE_ADMIN, "Bearer"]) assert(!all.includes(secret), `the log holds ${secret}: ${all}`);
});

Deno.test("limits: every route stays under D1's 50 queries per invocation (the fake enforces it)", async () => {
  const hub = await makeHub();
  hub.d1.maxQueries = 50;
  const key = fakeKey(1);
  await register(hub, key);
  const id = await publish(hub, key, await goodBattle());
  for (const path of ["overview", "packages", "reports", "pictures", "purges", "settings", `packages/${id}`]) {
    assertEquals((await hub.call("GET", "/v1/admin/" + path, { admin: true })).status, 200, path);
  }
  assertEquals((await hub.call("POST", `/v1/admin/packages/${id}/remove`, { admin: true, json: { reason: "copyright", strike: true } })).status, 200);
});

Deno.test("public routes ignore Authorization; keyed routes never answer from a shared cache", async () => {
  const hub = await makeHub();
  const [id] = seedPackages(hub, 1);
  const a = await hub.call("GET", `/v1/packages/${id}`, { key: fakeKey(1) });
  const b = await hub.call("GET", `/v1/packages/${id}`);
  assertEquals(a.body, b.body);
  assertEquals(a.headers.get("Cache-Control"), b.headers.get("Cache-Control"));
  const mine = await hub.call("GET", "/v1/me/packages", { key: fakeKey(1) });
  assertEquals(mine.headers.get("Cache-Control"), "no-store");
});

Deno.test("rate limits: per address for every request, for searches, and per key for writes; a missing binding is logged once", async () => {
  const hub = await makeHub({ limits: { RL_IP: 3, RL_SEARCH: 2, RL_WRITE: 2 } });
  for (let i = 0; i < 3; i++) assertEquals((await hub.call("GET", "/v1/info", { ip: "198.51.100.1" })).status, 200);
  const r = await hub.call("GET", "/v1/info", { ip: "198.51.100.1" });
  assertEquals([r.status, r.body.error, r.headers.get("Retry-After")], [429, "slow_down", "30"]);
  // An IPv6 client can't dodge the limit by changing the last 64 bits.
  for (let i = 0; i < 3; i++) await hub.call("GET", "/v1/info", { ip: `2001:db8:1:1::${i + 1}` });
  assertEquals((await hub.call("GET", "/v1/info", { ip: "2001:db8:1:1:ffff::9" })).status, 429);
  assertEquals((await hub.call("GET", "/v1/info", { ip: "2001:db8:1:2::1" })).status, 200);
  seedPackages(hub, 1);
  for (let i = 0; i < 2; i++) assertEquals((await hub.call("GET", "/v1/packages?q=seeded", { ip: "198.51.100.2" })).status, 200);
  assertEquals((await hub.call("GET", "/v1/packages?q=seeded", { ip: "198.51.100.2" })).body.error, "slow_down");
  const key = fakeKey(1);
  await register(hub, key, "Writer", "198.51.100.3");
  const { sha256Hex } = await import("../src/ids.js");
  for (let i = 0; i < 2; i++) await hub.call("POST", "/v1/me/rotate", { key: i ? fakeKey(10) : key, json: { newKeyHash: await sha256Hex(fakeKey(10 + i)) }, ip: `198.51.100.${10 + i}` });
  const third = await hub.call("POST", "/v1/me/rotate", { key: fakeKey(11), json: { newKeyHash: await sha256Hex(fakeKey(12)) }, ip: "198.51.100.20" });
  assertEquals(third.body.error, "slow_down");
  const bare = await makeHub({ drop: ["RL_IP", "RL_SEARCH"] });
  const lines = await captureConsole(async () => {
    for (let i = 0; i < 3; i++) assertEquals((await bare.call("GET", "/v1/info")).status, 200);
  });
  assert(lines.filter((l) => l.includes("binding=RL_IP")).length <= 1, lines.join("\n"));
});

Deno.test("429s: every one carries Retry-After as well as retryAfter (the review's C5)", async () => {
  const hub = await makeHub();
  const checked = [];
  const expect429 = (r, what) => {
    assertEquals(r.status, 429, what);
    assert(Number.isFinite(r.body.retryAfter) && r.body.retryAfter > 0, what + ": retryAfter");
    assertEquals(r.headers.get("Retry-After"), String(Math.ceil(r.body.retryAfter)), what);
    checked.push(r.body.error);
  };
  // rename_limit
  const key = fakeKey(1);
  await register(hub, key, "Bryce");
  await register(hub, key, "Bryce W");
  expect429(await register(hub, key, "Bryce X"), "rename");
  // daily_limit: reports per key, then per address
  const ids = seedPackages(hub, 30);
  await hub.call("PUT", "/v1/admin/settings", { admin: true, json: { reports_per_key_day: "1", reports_per_address_day: "2" } });
  await hub.call("POST", `/v1/packages/${ids[0]}/report`, { key: fakeKey(9), json: { reason: "spam" }, ip: "198.51.100.1" });
  expect429(await hub.call("POST", `/v1/packages/${ids[1]}/report`, { key: fakeKey(9), json: { reason: "spam" }, ip: "198.51.100.2" }), "reports per key");
  await hub.call("POST", `/v1/packages/${ids[2]}/report`, { key: fakeKey(10), json: { reason: "spam" }, ip: "198.51.100.1" });
  expect429(await hub.call("POST", `/v1/packages/${ids[3]}/report`, { key: fakeKey(11), json: { reason: "spam" }, ip: "198.51.100.1" }), "reports per address");
  // daily_limit: uploads per key
  await hub.call("PUT", "/v1/admin/settings", { admin: true, json: { probation_uploads_day: "0" } });
  const bytes = await goodBattle();
  expect429(await hub.call("POST", "/v1/uploads", { key, json: startBody(bytes, { sha256: "0".repeat(64), entriesSha256: "1".repeat(64) }) }), "uploads per key");
  // daily_limit: new keys for the whole hub
  await hub.call("PUT", "/v1/admin/settings", { admin: true, json: { new_keys_global_day: "1" } });
  expect429(await register(hub, fakeKey(2), "Two", "198.51.100.77"), "new keys");
  // slow_down from a rate limiter
  let r;
  for (let i = 0; i < 12; i++) r = await hub.call("GET", "/v1/packages?q=moon" + "x".repeat(i));
  expect429(r, "searches");
  assertEquals(checked, ["rename_limit", "daily_limit", "daily_limit", "daily_limit", "daily_limit", "slow_down"]);
});
