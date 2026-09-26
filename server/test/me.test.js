// The per-player key: registering at the first upload, names and renames, status, rotation, revoking,
// and the player's own list.

import { assert, assertEquals } from "./assert.js";
import { advance, fakeKey, makeHub, publish, register, time } from "./helpers.js";
import { goodBattle, goodPack } from "./make-fixtures.js";
import { sha256Hex } from "../src/ids.js";

Deno.test("key: the server stores only the key's SHA-256", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  const r = await register(hub, key, "  Bryce  ");
  assertEquals(r.status, 200);
  assertEquals(r.body.created, true);
  assertEquals(r.body.uploader.name, "Bryce");
  assertEquals(r.body.uploader.tag, r.body.uploader.id.slice(1, 5).toUpperCase());
  const row = hub.d1.one("SELECT * FROM uploaders");
  assertEquals(row.key_hash, await sha256Hex(key));
  const dump = JSON.stringify(hub.d1.q("SELECT * FROM uploaders")) + JSON.stringify(hub.d1.q("SELECT * FROM audit"));
  assert(!dump.includes(key), "the key itself is stored somewhere");
  assert(!dump.includes("203.0.113.7"), "an address is stored somewhere");
});

Deno.test("key: malformed and unknown keys", async () => {
  const hub = await makeHub();
  for (const bad of ["nbbk1_", "nbbk2_" + "a".repeat(43), "nbbk1_" + "a".repeat(42), "nbbk1_" + "a".repeat(42) + "!", "Basic abc"]) {
    const r = await hub.call("GET", "/v1/me", { headers: { Authorization: bad.startsWith("Basic") ? bad : "Bearer " + bad } });
    assertEquals([r.status, r.body.error], [401, "bad_key"], bad);
  }
  assertEquals((await hub.call("GET", "/v1/me", { key: fakeKey(7) })).body.error, "unknown_key");
  assertEquals((await hub.call("GET", "/v1/me")).body.error, "no_key");
});

Deno.test("names: refused names; a rename once a day; the same name is a no-op writing nothing", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  for (const bad of ["", "!!!", "admin", "\u3164", "x".repeat(0)]) assertEquals((await register(hub, key, bad)).body.error, "bad_name", bad);
  await register(hub, key, "Bryce");
  let before = hub.d1.statements.length;
  assertEquals((await register(hub, key, "Bryce")).status, 200);
  assertEquals(hub.d1.statements.slice(before).filter((s) => s.changes > 0).length, 0);
  before = hub.d1.statements.length;
  const renamed = await register(hub, key, "Bryce W");
  assertEquals(renamed.body.uploader.name, "Bryce W");
  const writes = hub.d1.statements.slice(before).filter((s) => s.changes > 0);
  assertEquals(writes.length, 1);
  assertEquals(writes[0].changes, 1);
  const again = await register(hub, key, "Bryce X");
  assertEquals([again.status, again.body.error], [429, "rename_limit"]);
  advance(86401);
  assertEquals((await register(hub, key, "Bryce X")).status, 200);
});

Deno.test("registering: limited per address block (/56 for IPv6), a whole-hub cap, and the new-keys switch", async () => {
  const hub = await makeHub();
  for (let i = 0; i < 3; i++) assertEquals((await register(hub, fakeKey(i), "P" + i, `2001:db8:0:${i}::1`)).status, 200);
  const fourth = await register(hub, fakeKey(3), "P3", "2001:db8:0:ff::2");
  assertEquals([fourth.status, fourth.body.error], [429, "slow_down"]);
  assertEquals((await register(hub, fakeKey(4), "P4", "2001:db8:1:0::1")).status, 200);
  await hub.call("PUT", "/v1/admin/settings", { admin: true, json: { new_keys_global_day: "4" } });
  assertEquals((await register(hub, fakeKey(5), "P5", "198.51.100.9")).body.error, "daily_limit");
  await hub.call("PUT", "/v1/admin/settings", { admin: true, json: { new_keys_global_day: "2000", new_keys_open: "0" } });
  assertEquals((await register(hub, fakeKey(6), "P6", "198.51.100.10")).body.error, "closed");
  // An existing key can still rename while new keys are closed.
  advance(86401);
  assertEquals((await register(hub, fakeKey(0), "P0 new", "198.51.100.11")).status, 200);
});

Deno.test("me: status and today's limits", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  await register(hub, key);
  await publish(hub, key, await goodPack(), { kind: "charts" });
  const r = await hub.call("GET", "/v1/me", { key });
  assertEquals(r.status, 200);
  assertEquals(r.body.limits.probation, true);
  assertEquals(r.body.limits.uploadsToday, 1);
  assertEquals(r.body.limits.uploadsPerDay, 2);
  assertEquals(r.body.limits.livePackages, 1);
  assertEquals(r.body.uploader.trusted, false);
});

Deno.test("rotate: only the new key's hash travels; the old key stops working at once", async () => {
  const hub = await makeHub();
  const oldKey = fakeKey(1), newKey = fakeKey(2);
  await register(hub, oldKey);
  const id = await publish(hub, oldKey, await goodBattle());
  const r = await hub.call("POST", "/v1/me/rotate", { key: oldKey, json: { newKeyHash: await sha256Hex(newKey) } });
  assertEquals(r.status, 200);
  assertEquals((await hub.call("GET", "/v1/me", { key: oldKey })).body.error, "unknown_key");
  assertEquals((await hub.call("GET", "/v1/me", { key: newKey })).status, 200);
  const mine = await hub.call("GET", "/v1/me/packages", { key: newKey });
  assertEquals(mine.body.items.map((i) => i.id), [id]);
  await register(hub, fakeKey(3), "Other");
  const clash = await hub.call("POST", "/v1/me/rotate", { key: newKey, json: { newKeyHash: await sha256Hex(fakeKey(3)) } });
  assertEquals(clash.body.error, "key_in_use");
  assertEquals((await hub.call("POST", "/v1/me/rotate", { key: newKey, json: { newKeyHash: "xyz" } })).status, 400);
});

Deno.test("my uploads: every status, with the owner's reason and note for removals", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  await register(hub, key);
  const a = await publish(hub, key, await goodBattle(), {});
  const b = await publish(hub, key, await goodPack(), { kind: "charts" });
  await hub.call("POST", `/v1/admin/packages/${a}/remove`, { admin: true, json: { reason: "copyright", note: "a notice from the label" } });
  await hub.call("POST", `/v1/admin/packages/${b}/hide`, { admin: true, json: { note: "private owner note" } });
  const r = await hub.call("GET", "/v1/me/packages", { key });
  const by = Object.fromEntries(r.body.items.map((i) => [i.id, i]));
  assertEquals([by[a].status, by[a].removedReason, by[a].removedNote], ["removed", "copyright", "a notice from the label"]);
  assertEquals([by[b].status, by[b].removedNote], ["hidden", null]);
  assert("pictureState" in by[b]);
});

Deno.test("delete: the uploader removes their own entry for everyone; others can't", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  await register(hub, key);
  const id = await publish(hub, key, await goodBattle());
  await register(hub, fakeKey(2), "Other");
  assertEquals((await hub.call("DELETE", `/v1/packages/${id}`, { key: fakeKey(2) })).body.error, "not_yours");
  const r = await hub.call("DELETE", `/v1/packages/${id}`, { key });
  assertEquals(r.status, 204);
  assertEquals((await hub.call("GET", `/v1/packages/${id}`)).body.error, "deleted");
  assertEquals((await hub.call("GET", "/v1/packages?q=moonlit")).body.items.length, 0);
  assertEquals(hub.cache.purged.flat().sort(), ["list", `pkg-${id}`].sort());
  assertEquals((await hub.call("DELETE", `/v1/packages/${id}`, { key })).status, 410);
  assertEquals(hub.d1.one("SELECT delete_after - ? AS d, why FROM trash", time()), { d: 86400, why: "deleted by uploader" });
});

Deno.test("my uploads: a quarantined entry shows as removed for the rules, like anywhere else (the review's C6)", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  await register(hub, key, "Bryce");
  const id = await publish(hub, key, await goodBattle());
  assertEquals((await hub.call("POST", `/v1/admin/packages/${id}/quarantine`, { admin: true, json: { note: "x" } })).status, 200);
  const item = (await hub.call("GET", "/v1/me/packages", { key })).body.items[0];
  assertEquals([item.status, item.removedReason, item.removedNote], ["removed", "rules", null]);
  assertEquals((await hub.call("GET", `/v1/packages/lookup?ids=${id}`)).body.items[0].status, "removed");
});

Deno.test("registering: one address makes at most 20 new keys a day, so it can't fill the whole hub's cap (the review's SEC-07)", async () => {
  const hub = await makeHub();
  for (let i = 0; i < 20; i++) {
    if (i && i % 3 === 0) advance(61); // RL_NEWKEY: 3 a minute
    assertEquals((await register(hub, fakeKey(i), "P" + i, "198.51.100.9")).status, 200, "key " + i);
  }
  advance(61);
  const over = await register(hub, fakeKey(20), "P20", "198.51.100.9");
  assertEquals([over.status, over.body.error], [429, "daily_limit"]);
  // Another address can, and the key that was refused isn't registered.
  assertEquals((await register(hub, fakeKey(21), "P21", "203.0.113.50")).status, 200);
  assertEquals(hub.d1.one("SELECT count(*) AS n FROM uploaders").n, 21);
  // The address is kept only as a salted hash, and the daily salt change deletes the counts.
  const rows = hub.d1.q("SELECT h, what, n FROM address_day");
  assert(rows.every((r) => /^[0-9a-f]{32}$/.test(r.h) && !r.h.includes("198")), JSON.stringify(rows));
  await hub.cron("17 3 * * *");
  assertEquals(hub.d1.one("SELECT count(*) AS n FROM address_day").n, 0);
  assertEquals((await register(hub, fakeKey(20), "P20", "198.51.100.9")).status, 200);
});
