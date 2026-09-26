// Reports: from any well-formed key, once per key per entry, capped, and never hiding anything by themselves.

import { assert, assertEquals } from "./assert.js";
import { fakeKey, makeHub, register, seedPackages } from "./helpers.js";
import { sha256Hex } from "../src/ids.js";

Deno.test("reports: a key that never registered can report; the second report is 'already'", async () => {
  const hub = await makeHub();
  const [id] = seedPackages(hub, 1);
  const key = fakeKey(1);
  const r = await hub.call("POST", `/v1/packages/${id}/report`, { key, json: { reason: "copyright", note: "  this is mine\u200B " } });
  assertEquals([r.status, r.body.status], [201, "received"]);
  const again = await hub.call("POST", `/v1/packages/${id}/report`, { key, json: { reason: "spam" } });
  assertEquals([again.status, again.body.status], [200, "already"]);
  const row = hub.d1.one("SELECT * FROM reports");
  assertEquals(row.reporter_hash, await sha256Hex(key));
  assertEquals(row.note, "this is mine");
  assertEquals(hub.d1.one("SELECT count(*) AS n FROM uploaders").n, 1); // only the seeder: reporting registers nothing
  assertEquals(hub.d1.one("SELECT reports_open FROM packages").reports_open, 1);
});

Deno.test("reports: reasons, notes, a key, and a live entry are required", async () => {
  const hub = await makeHub();
  const [id, gone] = seedPackages(hub, 2, (i) => ({ status: i ? "removed" : "live" }));
  const key = fakeKey(1);
  assertEquals((await hub.call("POST", `/v1/packages/${id}/report`, { key, json: { reason: "boring" } })).body.error, "bad_reason");
  assertEquals((await hub.call("POST", `/v1/packages/${id}/report`, { json: { reason: "spam" } })).body.error, "no_key");
  assertEquals((await hub.call("POST", `/v1/packages/${gone}/report`, { key, json: { reason: "spam" } })).status, 404);
  assertEquals((await hub.call("POST", "/v1/packages/zzzzzzzzzz/report", { key, json: { reason: "spam" } })).status, 404);
  const long = await hub.call("POST", `/v1/packages/${id}/report`, { key, json: { reason: "other", note: "x".repeat(900) } });
  assertEquals(long.status, 201);
  assertEquals(hub.d1.one("SELECT length(note) AS n FROM reports").n, 500);
});

Deno.test("reports: 5 a minute per address, 20 a day per key, and a whole-hub cap", async () => {
  const hub = await makeHub();
  const pkgs = seedPackages(hub, 30);
  const key = fakeKey(1);
  for (let i = 0; i < 5; i++) assertEquals((await hub.call("POST", `/v1/packages/${pkgs[i]}/report`, { key, json: { reason: "spam" } })).status, 201);
  assertEquals((await hub.call("POST", `/v1/packages/${pkgs[5]}/report`, { key, json: { reason: "spam" } })).body.error, "slow_down");
  for (let i = 5; i < 20; i++) {
    const r = await hub.call("POST", `/v1/packages/${pkgs[i]}/report`, { key, json: { reason: "spam" }, ip: `198.51.100.${i}` });
    assertEquals(r.status, 201);
  }
  const over = await hub.call("POST", `/v1/packages/${pkgs[20]}/report`, { key, json: { reason: "spam" }, ip: "198.51.100.200" });
  assertEquals(over.body.error, "daily_limit");
  await hub.call("PUT", "/v1/admin/settings", { admin: true, json: { reports_global_day: "20" } });
  const other = await hub.call("POST", `/v1/packages/${pkgs[21]}/report`, { key: fakeKey(2), json: { reason: "spam" }, ip: "198.51.100.201" });
  assertEquals(other.body.error, "daily_limit");
});

Deno.test("reports: nothing is hidden automatically, however many reports come in", async () => {
  const hub = await makeHub();
  const [id] = seedPackages(hub, 1);
  for (let i = 0; i < 40; i++) await hub.call("POST", `/v1/packages/${id}/report`, { key: fakeKey(i), json: { reason: "offensive" }, ip: `198.51.100.${i}` });
  assertEquals(hub.d1.one("SELECT status, reports_open FROM packages"), { status: "live", reports_open: 40 });
  const grouped = await hub.call("GET", "/v1/admin/reports", { admin: true });
  assertEquals(grouped.body.items.length, 1);
  assertEquals(grouped.body.items[0].count, 40);
  assertEquals(grouped.body.items[0].reasons, { offensive: 40 });
});

Deno.test("reports: a banned key's reports are ignored", async () => {
  const hub = await makeHub();
  const [id] = seedPackages(hub, 1);
  const key = fakeKey(1);
  await register(hub, key, "Troll");
  const uid = hub.d1.one("SELECT id FROM uploaders WHERE name = 'Troll'").id;
  await hub.call("POST", `/v1/admin/uploaders/${uid}/ban`, { admin: true, json: {} });
  const r = await hub.call("POST", `/v1/packages/${id}/report`, { key, json: { reason: "spam" } });
  assertEquals(r.status, 201);
  assertEquals(hub.d1.one("SELECT count(*) AS n FROM reports").n, 0);
});

Deno.test("reports and uploads reach the owner's Discord webhook without pinging anyone", async () => {
  const hub = await makeHub({ env: { NOTIFY_WEBHOOK: "https://discord.example/api/webhooks/fake" } });
  const [id] = seedPackages(hub, 1, () => ({ title: "@everyone `look` <@123>" }));
  const sent = [];
  const saved = globalThis.fetch;
  globalThis.fetch = async (url, init) => {
    sent.push({ url: String(url), body: typeof init.body === "string" ? init.body : null });
    return new Response(null, { status: 204 });
  };
  try {
    await hub.call("POST", `/v1/packages/${id}/report`, { key: fakeKey(1), json: { reason: "spam" } });
  } finally {
    globalThis.fetch = saved;
  }
  assertEquals(sent.length, 1);
  const body = JSON.parse(sent[0].body);
  assertEquals(body.allowed_mentions, { parse: [] });
  assert(body.content.includes("`@everyone 'look' <@123>`"), body.content);
});
