// The owner's API: the key checks, every package and uploader action, bulk actions, settings, purges,
// quarantine, backups, and takedowns when the cache or D1 misbehave.

import { assert, assertEquals, assertMatch } from "./assert.js";
import { FAKE_ADMIN, advance, fakeKey, makeHub, publish, register, seedFile, seedPackages, seedUploader, time } from "./helpers.js";
import { goodBattle, goodPack, thumbB64 } from "./make-fixtures.js";
import { BACKUP_BUDGET } from "../src/admin.js";
import { sha256Hex } from "../src/ids.js";

const admin = (hub, method, path, json) => hub.call(method, "/v1/admin/" + path, { admin: true, json: method === "GET" ? undefined : json ?? {} });

Deno.test("admin: no key, a wrong key, a short or missing ADMIN_KEY, and too many tries", async () => {
  const hub = await makeHub();
  assertEquals((await hub.call("GET", "/v1/admin/overview")).status, 401);
  assertEquals((await hub.call("GET", "/v1/admin/overview", { admin: "wrong-key-wrong-key-wrong-key-wrong" })).body.error, "wrong_key");
  assertEquals((await admin(hub, "GET", "overview")).status, 200);
  const short = await makeHub({ env: { ADMIN_KEY: "too-short" } });
  assertEquals((await short.call("GET", "/v1/admin/overview", { admin: "too-short" })).body.error, "admin_off");
  const none = await makeHub({ drop: ["ADMIN_KEY"] });
  assertEquals((await none.call("GET", "/v1/admin/overview", { admin: FAKE_ADMIN })).body.error, "admin_off");
  const limited = await makeHub({ limits: { RL_WRITE: 3 } });
  for (let i = 0; i < 3; i++) await limited.call("GET", "/v1/admin/overview", { admin: "nope" });
  assertEquals((await limited.call("GET", "/v1/admin/overview", { admin: FAKE_ADMIN })).status, 429);
});

Deno.test("admin: hide, then restore; each purges the entry and the lists", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  await register(hub, key);
  const id = await publish(hub, key, await goodBattle());
  const hide = await admin(hub, "POST", `packages/${id}/hide`, { note: "checking" });
  assertEquals([hide.body.changed, hide.body.purge], [1, "done"]);
  assertEquals((await hub.call("GET", `/v1/packages/${id}`)).body.error, "unavailable");
  assertEquals((await hub.call("GET", `/v1/files/${id}/1/package`)).status, 410);
  assertEquals((await hub.call("GET", "/v1/packages")).body.items.length, 0);
  assertEquals(hub.cache.purged[0].sort(), ["list", `pkg-${id}`]);
  const restore = await admin(hub, "POST", `packages/${id}/restore`);
  assertEquals(restore.body.changed, 1);
  assertEquals((await hub.call("GET", `/v1/packages/${id}`)).status, 200);
  assertEquals((await hub.call("GET", "/v1/packages?q=moonlit")).body.items.length, 1);
  const detail = await admin(hub, "GET", `packages/${id}`);
  assertEquals(detail.body.audit.map((a) => a.action), ["restore", "hide", "publish"]);
});

Deno.test("admin: remove for copyright with a strike; files kept 30 days; 3 strikes ban the key", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  await register(hub, key);
  const ids = [];
  for (let i = 0; i < 3; i++) {
    advance(3 * 86400);
    ids.push(await publish(hub, key, await goodPack({ title: "p" + i, songs: ["S" + i] }), { kind: "charts", songs: ["S" + i] }));
  }
  const r = await admin(hub, "POST", `packages/${ids[0]}/remove`, { reason: "copyright", note: "notice from the label", strike: true });
  assertEquals(r.body.changed, 1);
  const trash = hub.d1.one("SELECT delete_after - ? AS keep FROM trash", time());
  assertEquals(trash.keep, 30 * 86400);
  assertEquals(hub.d1.one("SELECT strikes, status FROM uploaders"), { strikes: 1, status: "ok" });
  await admin(hub, "POST", `packages/${ids[1]}/remove`, { reason: "copyright", strike: true });
  await admin(hub, "POST", `packages/${ids[2]}/remove`, { reason: "copyright", strike: true });
  assertEquals(hub.d1.one("SELECT strikes, status FROM uploaders"), { strikes: 3, status: "banned" });
  const d = await hub.call("GET", `/v1/packages/${ids[0]}`);
  assertEquals([d.status, d.body.error, d.body.reason], [410, "removed", "copyright"]);
  assertEquals((await admin(hub, "POST", `packages/${ids[0]}/remove`, { reason: "nonsense" })).body.error, "bad_reason");
});

Deno.test("admin: remove for other reasons keeps the files a day and gives no strike", async () => {
  const hub = await makeHub();
  const [id] = seedPackages(hub, 1);
  await admin(hub, "POST", `packages/${id}/remove`, { reason: "spam", strike: true });
  assertEquals(hub.d1.one("SELECT delete_after - ? AS keep FROM trash", time()).keep, 86400);
  assertEquals(hub.d1.one("SELECT sum(strikes) AS s FROM uploaders").s, 0);
  assertEquals(hub.d1.one("SELECT count(*) AS n FROM battle_blocks").n, 0);
});

Deno.test("admin: quarantine moves the file to a private place no cron deletes; the owner can still download it", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  await register(hub, key);
  const bytes = await goodBattle();
  const id = await publish(hub, key, bytes);
  const q = await admin(hub, "POST", `packages/${id}/quarantine`, { note: "reported to the authorities" });
  assertEquals([q.body.changed, q.body.moved], [1, true]);
  assertEquals([...hub.r2.objects.keys()], [`quarantine/${id}/1/package`]);
  assertEquals((await hub.call("GET", `/v1/packages/${id}`)).body.error, "removed");
  assertEquals((await hub.call("GET", `/v1/files/${id}/1/package`)).status, 410);
  advance(400 * 86400);
  for (const cron of ["7 * * * *", "17 3 * * *"]) await hub.cron(cron);
  assertEquals([...hub.r2.objects.keys()], [`quarantine/${id}/1/package`]);
  const file = await hub.call("GET", `/v1/admin/files/${id}/1`, { admin: true });
  assertEquals(file.status, 200);
  assertEquals(file.body.length, bytes.length);
  assertEquals(file.headers.get("Cache-Control"), "no-store");
  assertEquals(file.headers.get("Content-Security-Policy"), "default-src 'none'; sandbox");
  const mine = await hub.call("GET", "/v1/me/packages", { key });
  assertEquals(mine.body.items[0].removedNote, null);
  const back = await admin(hub, "POST", `packages/${id}/restore`);
  assertEquals(back.body.changed, 1);
  const stored = hub.d1.one("SELECT r2_key FROM packages").r2_key;
  assertMatch(stored, new RegExp(`^pkg/${id}/1/up[0-9a-z]{16}$`));
  assertEquals([...hub.r2.objects.keys()], [stored]);
  assertEquals((await hub.call("GET", `/v1/files/${id}/1/package`)).status, 200);
});

Deno.test("admin: restore refuses when the files are gone or another live entry took the battle id", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  await register(hub, key);
  const id = await publish(hub, key, await goodBattle());
  await admin(hub, "POST", `packages/${id}/remove`, { reason: "other" });
  await register(hub, fakeKey(2), "Two");
  const other = await publish(hub, fakeKey(2), await goodBattle({ files: { "x.json": { data: "{}" } } }));
  assertEquals((await admin(hub, "POST", `packages/${id}/restore`)).body.error, "battle_taken");
  await admin(hub, "POST", `packages/${other}/remove`, { reason: "other" });
  advance(86401);
  await hub.cron("7 * * * *");
  assertEquals((await admin(hub, "POST", `packages/${id}/restore`)).body.error, "files_gone");
});

Deno.test("admin: the admin file route serves hidden entries for review", async () => {
  const hub = await makeHub();
  const [id] = seedPackages(hub, 1, () => ({ status: "hidden" }));
  await seedFile(hub, id);
  assertEquals((await hub.call("GET", `/v1/files/${id}/1/package`)).status, 410);
  assertEquals((await hub.call("GET", `/v1/admin/files/${id}/1`, { admin: true })).status, 200);
  assertEquals((await hub.call("GET", `/v1/admin/files/${id}/1`)).status, 401);
});

Deno.test("admin: pictures waiting, approved or refused", async () => {
  const hub = await makeHub();
  const [a, b] = seedPackages(hub, 2, () => ({ thumb: thumbB64(), picture: "waiting" }));
  const waiting = await admin(hub, "GET", "pictures");
  assertEquals(waiting.body.items.length, 2);
  await admin(hub, "POST", `packages/${a}/picture`, { show: true });
  await admin(hub, "POST", `packages/${b}/picture`, { show: false });
  assertEquals(hub.d1.q("SELECT picture_state FROM packages ORDER BY seq").map((r) => r.picture_state), ["shown", "refused"]);
  assertEquals((await admin(hub, "POST", `packages/${a}/picture`, { show: "yes" })).status, 400);
});

Deno.test("admin: resolving reports", async () => {
  const hub = await makeHub();
  const [id] = seedPackages(hub, 1);
  for (let i = 0; i < 3; i++) await hub.call("POST", `/v1/packages/${id}/report`, { key: fakeKey(i), json: { reason: "broken" } });
  const detail = await admin(hub, "GET", `packages/${id}`);
  const one = await admin(hub, "POST", "reports/resolve", { packageId: id, reporterHash: detail.body.reports[0].reporterHash, resolution: "fixed" });
  assertEquals(one.body.resolved, 1);
  assertEquals(hub.d1.one("SELECT reports_open FROM packages").reports_open, 2);
  const all = await admin(hub, "POST", "reports/resolve", { packageId: id });
  assertEquals(all.body.resolved, 2);
  assertEquals((await admin(hub, "GET", "reports")).body.items.length, 0);
});

Deno.test("admin: uploaders: ban, unban, strikes, revoke, remove everything, and transfer", async () => {
  const hub = await makeHub();
  await register(hub, fakeKey(1), "One");
  await register(hub, fakeKey(2), "Two");
  const [u1, u2] = hub.d1.q("SELECT id FROM uploaders ORDER BY seq").map((r) => r.id);
  const p = await publish(hub, fakeKey(1), await goodPack(), { kind: "charts" });
  assertEquals((await admin(hub, "POST", `uploaders/${u1}/ban`, { note: "spam" })).body.status, "banned");
  assertEquals((await hub.call("GET", "/v1/me", { key: fakeKey(1) })).body.uploader.status, "banned");
  assertEquals((await admin(hub, "POST", `uploaders/${u1}/unban`)).body.status, "ok");
  assertEquals((await admin(hub, "POST", `uploaders/${u1}/strikes`, { value: 2 })).body.strikes, 2);
  assertEquals((await admin(hub, "POST", `uploaders/${u1}/transfer`, { to: u2 })).body.moved, 1);
  assertEquals((await hub.call("GET", "/v1/me/packages", { key: fakeKey(2) })).body.items.map((i) => i.id), [p]);
  assertEquals((await admin(hub, "POST", `uploaders/${u2}/remove-all`, { reason: "spam" })).body.done, 1);
  assertEquals((await hub.call("GET", `/v1/packages/${p}`)).body.error, "removed");
  assertEquals((await admin(hub, "POST", `uploaders/${u2}/revoke-key`)).body.status, "revoked");
  assertEquals((await hub.call("GET", "/v1/me", { key: fakeKey(2) })).body.error, "revoked");
  assertEquals((await admin(hub, "POST", `uploaders/${u2}/unban`)).body.error, "revoked");
  const info = await admin(hub, "GET", `uploaders/${u2}`);
  assertEquals(info.body.uploader.status, "revoked");
  assertEquals((await admin(hub, "POST", "uploaders/unot-an-id/ban")).status, 404);
});

Deno.test("admin: bulk actions for spam waves, 30 at a time, by upload time or key age; and the new-key gate", async () => {
  const hub = await makeHub();
  const oldIds = seedPackages(hub, 5, (i) => ({ created: time() - 10 * 86400 + i }));
  const wave = seedUploader(hub, { created: time() - 3600 });
  const waveIds = seedPackages(hub, 35, (i) => ({ uploader: wave, created: time() - 600 + i }));
  const first = await admin(hub, "POST", "bulk", { action: "hide", keysCreatedSince: time() - 7200 });
  assertEquals([first.body.done, first.body.more], [30, true]);
  const second = await admin(hub, "POST", "bulk", { action: "hide", keysCreatedSince: time() - 7200 });
  assertEquals([second.body.done, second.body.more], [5, false]);
  assertEquals(hub.d1.one("SELECT count(*) AS n FROM packages WHERE status = 'hidden'").n, 35);
  const removed = await admin(hub, "POST", "bulk", { action: "remove", uploadedSince: time() - 3600 });
  assertEquals(removed.body.done, 30);
  assertEquals(hub.d1.one("SELECT count(*) AS n FROM packages WHERE status = 'live'").n, oldIds.length);
  assertEquals((await admin(hub, "POST", "bulk", { action: "hide" })).status, 400);
  assertEquals((await admin(hub, "POST", "bulk", { action: "delete", uploadedSince: 1 })).status, 400);
  assert(waveIds.length === 35);
  assertEquals((await admin(hub, "PUT", "gates", { closeUploadsForKeysYoungerThanDays: 2 })).body.closeUploadsForKeysYoungerThanDays, 2);
  assertEquals(hub.d1.one("SELECT v FROM settings WHERE k = 'close_uploads_key_days'").v, "2");
});

Deno.test("admin: settings are validated; salts are never shown; the takedown contact reaches /legal", async () => {
  const hub = await makeHub();
  const s = await admin(hub, "GET", "settings");
  assert(!("stats_salt" in s.body.settings));
  assertEquals(s.body.settings.hub_name, "nocturne but better hub");
  const bad = await admin(hub, "PUT", "settings", { max_entries: "lots", stats_salt: "x", uploads_open: "maybe" });
  assertEquals(bad.status, 400);
  assertEquals(bad.body.problems.length, 3);
  const ok = await admin(hub, "PUT", "settings", { takedown_contact: "takedowns@example.org <b>", message: "hello players" });
  assertEquals(ok.body.changed.sort(), ["message", "takedown_contact"]);
  assertEquals(hub.cache.purged.at(-1).sort(), ["info", "legal"]);
  const legal = await hub.call("GET", "/legal");
  assertMatch(legal.body, /takedowns@example\.org &lt;b&gt;/);
  assertEquals((await hub.call("GET", "/v1/info")).body.message, "hello players");
});

Deno.test("takedowns: a failing purge stays queued and the cron retries it; purges are merged, 100 tags at most", async () => {
  const hub = await makeHub();
  const ids = seedPackages(hub, 3);
  hub.cache.failing = true;
  const r = await admin(hub, "POST", `packages/${ids[0]}/remove`, { reason: "copyright" });
  assertEquals(r.body.purge, "pending");
  const q = hub.d1.one("SELECT tries, done_at, last_error FROM purge_queue");
  assertEquals(q, { tries: 1, done_at: null, last_error: "purge_failed" });
  await admin(hub, "POST", `packages/${ids[1]}/hide`);
  assertEquals((await admin(hub, "GET", "overview")).body.purgesFailing, 2);
  hub.cache.failing = false;
  await hub.cron("7 * * * *");
  assertEquals(hub.d1.one("SELECT count(*) AS n FROM purge_queue WHERE done_at IS NULL").n, 0);
  assertEquals(hub.cache.purged.length, 1);
  assertEquals(hub.cache.purged[0].sort(), ["list", `pkg-${ids[0]}`, `pkg-${ids[1]}`].sort());
  const many = seedPackages(hub, 120);
  hub.cache.failing = true;
  await admin(hub, "POST", "bulk", { action: "hide", uploadedSince: 0 });
  await admin(hub, "POST", "bulk", { action: "hide", uploadedSince: 0 });
  await admin(hub, "POST", "bulk", { action: "hide", uploadedSince: 0 });
  await admin(hub, "POST", "bulk", { action: "hide", uploadedSince: 0 });
  hub.cache.failing = false;
  const retry = await admin(hub, "POST", "purges/retry");
  assert(retry.body.done >= 1 && hub.cache.purged.at(-1).length <= 100, JSON.stringify(retry.body));
  assert(many.length === 120);
  const list = await admin(hub, "GET", "purges");
  assert(list.body.items.length >= 4);
});

Deno.test("takedowns: the free-tier purge limit (5 a minute, a bucket of 25) is survived by the queue", async () => {
  const hub = await makeHub();
  const ids = seedPackages(hub, 30);
  let pending = 0;
  for (const id of ids) if ((await admin(hub, "POST", `packages/${id}/hide`)).body.purge === "pending") pending++;
  assert(pending >= 1, "some purges must wait");
  advance(10 * 60);
  await hub.cron("7 * * * *");
  assertEquals(hub.d1.one("SELECT count(*) AS n FROM purge_queue WHERE done_at IS NULL").n, 0);
});

Deno.test("takedowns: with D1 out of its daily limit, owner actions fail but BLOCKED_IDS still works", async () => {
  const hub = await makeHub();
  const [id] = seedPackages(hub, 1);
  await seedFile(hub, id);
  hub.d1.limitSpent = true;
  assertEquals((await admin(hub, "POST", `packages/${id}/hide`)).body.error, "busy_today");
  hub.vars.BLOCKED_IDS = id;
  assertEquals((await hub.call("GET", `/v1/files/${id}/1/package`)).status, 410);
  hub.d1.limitSpent = false;
  const o = await admin(hub, "GET", "overview");
  assertEquals(o.body.switches.blockedIds, [id]);
});

Deno.test("admin: backups write the base tables as JSON pages into R2, and reindex rebuilds search", async () => {
  const hub = await makeHub();
  seedPackages(hub, 450, (i) => ({ title: `Backup test ${i}` }));
  let cursor = null, files = 0, calls = 0;
  do {
    const r = await admin(hub, "POST", "backup", { cursor });
    assertEquals(r.status, 200, JSON.stringify(r.body));
    files += r.body.written.length;
    cursor = r.body.cursor;
    calls++;
  } while (cursor);
  const keys = [...hub.r2.objects.keys()].filter((k) => k.startsWith("backup/2026-09-26/"));
  assertEquals(keys.length, files);
  assert(keys.includes("backup/2026-09-26/packages-0002.json"), keys.join(","));
  const pages = await Promise.all(keys.filter((k) => k.includes("/packages-")).map(async (k) => JSON.parse(await (await hub.r2.get(k)).text()).rows.length));
  assertEquals(pages.reduce((a, b) => a + b, 0), 450);
  assert(calls >= 2);
  hub.d1.sqlite.exec("DELETE FROM packages_fts");
  assertEquals((await hub.call("GET", "/v1/packages?q=backup")).body.items.length, 0);
  let after = 0, done = false;
  while (!done) {
    const r = await admin(hub, "POST", "reindex", { after });
    after = r.body.after;
    done = r.body.done;
  }
  assertEquals(hub.d1.one("SELECT count(*) AS n FROM packages_fts").n, 450);
  assertEquals((await hub.call("GET", "/v1/packages?q=backup")).body.items.length, 24);
});

Deno.test("admin: the owner's lists filter by status, time, key age and text", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  await register(hub, key);
  await publish(hub, key, await goodBattle());
  seedPackages(hub, 3, (i) => ({ status: ["live", "hidden", "removed"][i], title: "Other " + i, created: time() - 20 * 86400 }));
  assertEquals((await admin(hub, "GET", "packages")).body.items.length, 4);
  assertEquals((await admin(hub, "GET", "packages?status=hidden")).body.items.length, 1);
  assertEquals((await admin(hub, "GET", "packages?keyAge=1")).body.items.length, 1);
  assertEquals((await admin(hub, "GET", `packages?since=${time() - 86400}`)).body.items.length, 1);
  assertEquals((await admin(hub, "GET", "packages?q=Moon")).body.items.length, 1);
  assertEquals((await admin(hub, "GET", "packages?q=3f2b8c1e-7d6a-4b5c-9e8f-0a1b2c3d4e5f")).body.items.length, 1);
  assertEquals((await admin(hub, "GET", "packages?q=%25")).body.items.length, 0);
  const o = await admin(hub, "GET", "overview");
  assertEquals(o.body.packages, { live: 2, hidden: 1, removed: 1 });
  assertEquals(o.body.uploadsToday, 1);
});

// ---- quarantine keeps every version; backups (the review's SEC-05, SEC-06, CF-3) --------------------------

Deno.test("admin: quarantine keeps every version still held, since the reported one may be older; restore brings back the current one", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  await register(hub, key);
  const v1 = await goodBattle({ title: "Reported upload" });
  const id = await publish(hub, key, v1);
  assertEquals((await hub.call("POST", `/v1/packages/${id}/report`, { key: fakeKey(2), json: { reason: "other", note: "illegal picture in v1" } })).status, 201);
  // The uploader swaps in a clean v2 before the owner looks: v1 waits 24 h in the trash.
  advance(600);
  const v2 = await goodBattle({ title: "Reported upload (clean)" });
  await publish(hub, key, v2, { packageId: id });
  advance(600);
  const q = await admin(hub, "POST", `packages/${id}/quarantine`, { note: "kept for the report" });
  assertEquals([q.status, q.body.versions, q.body.moved, q.body.more], [200, [1, 2], true, false]);
  assertEquals([...hub.r2.objects.keys()].sort(), [`quarantine/${id}/1/package`, `quarantine/${id}/2/package`]);
  assertEquals(hub.d1.one("SELECT count(*) AS n FROM trash").n, 0);
  assertMatch(hub.d1.one("SELECT detail FROM audit WHERE action = 'quarantine'").detail, /"versions":\[1,2\]/);
  // A day (and a year) of crons later, both are still there, and the owner can download either.
  advance(25 * 3600);
  await hub.cron("7 * * * *");
  advance(400 * 86400);
  await hub.cron("7 * * * *");
  await hub.cron("17 3 * * *");
  assertEquals([...hub.r2.objects.keys()].length, 2);
  const f1 = await hub.call("GET", `/v1/admin/files/${id}/1`, { admin: true });
  assertEquals([f1.status, await sha256Hex(f1.body)], [200, await sha256Hex(v1)]);
  assertEquals((await hub.call("GET", `/v1/admin/files/${id}/2`, { admin: true })).status, 200);
  assertEquals((await hub.call("GET", `/v1/admin/files/${id}/3`, { admin: true })).status, 404);
  // Everything in quarantine counts toward the storage cap.
  assertEquals(hub.d1.one("SELECT v FROM settings WHERE k = 'storage_used'").v, String(v1.length + v2.length));
  // Pressing it again finds nothing left to move.
  assertEquals((await admin(hub, "POST", `packages/${id}/quarantine`, {})).body.changed, 0);
  // Restore: the current version is live again; the older one stays in quarantine.
  const back = await admin(hub, "POST", `packages/${id}/restore`);
  assertEquals(back.body.changed, 1);
  const current = hub.d1.one("SELECT r2_key FROM packages").r2_key;
  assertEquals([...hub.r2.objects.keys()].sort(), [current, `quarantine/${id}/1/package`].sort());
  const served = await hub.call("GET", `/v1/files/${id}/2/package`);
  assertEquals(await sha256Hex(served.body), await sha256Hex(v2));
  assertEquals((await hub.call("GET", `/v1/admin/files/${id}/1`, { admin: true })).status, 200);
});

Deno.test("admin: quarantine after a copyright removal takes the file from the trash too", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  await register(hub, key);
  const id = await publish(hub, key, await goodBattle());
  await admin(hub, "POST", `packages/${id}/remove`, { reason: "copyright", note: "" });
  assertEquals(hub.d1.one("SELECT count(*) AS n FROM trash").n, 1);
  const q = await admin(hub, "POST", `packages/${id}/quarantine`, {});
  assertEquals(q.body.versions, [1]);
  assertEquals(hub.d1.one("SELECT count(*) AS n FROM trash").n, 0);
  assertEquals([...hub.r2.objects.keys()], [`quarantine/${id}/1/package`]);
  // The owner's entry view lists what's still held.
  const d = await admin(hub, "GET", `packages/${id}`);
  assertEquals([d.body.status, d.body.oldVersions], ["quarantined", []]);
});

Deno.test("admin: backups leave out the download-count salts and the cursor key", async () => {
  const hub = await makeHub();
  const [id] = seedPackages(hub, 1);
  assertEquals((await hub.call("POST", `/v1/packages/${id}/installed`, { json: { version: 1 }, ip: "198.51.100.23" })).status, 204);
  const salt = hub.d1.one("SELECT v FROM settings WHERE k = 'stats_salt'").v;
  const cursorKey = hub.d1.one("SELECT v FROM settings WHERE k = 'cursor_key'").v;
  assertEquals([salt.length, cursorKey.length], [64, 64]);
  hub.d1.sqlite.prepare("UPDATE settings SET v = 'an-older-salt' WHERE k = 'stats_salt_prev'").run();
  let cursor = null;
  const keys = [];
  for (let i = 0; i < 20; i++) {
    const r = await admin(hub, "POST", "backup", { cursor });
    keys.push(...r.body.written);
    if (r.body.done) break;
    cursor = r.body.cursor;
  }
  const settingsFile = keys.find((k) => k.includes("/settings-"));
  const text = new TextDecoder().decode(hub.r2.objects.get(settingsFile).bytes);
  const rows = JSON.parse(text).rows.map((r) => r.k);
  assert(rows.includes("takedown_contact") && rows.includes("storage_used"), "the other settings are kept");
  for (const secret of [salt, cursorKey, "an-older-salt"]) assert(!keys.some((k) => new TextDecoder().decode(hub.r2.objects.get(k).bytes).includes(secret)), "a secret was backed up");
  assert(!rows.includes("stats_salt") && !rows.includes("stats_salt_prev") && !rows.includes("cursor_key"));
});

Deno.test("admin: one backup call writes at most its budget of JSON, even over 1000 thumbnails of 16 KB", async () => {
  const hub = await makeHub();
  const thumb = "A".repeat(16380) + "AAA=";
  seedPackages(hub, 1000, () => ({ thumb }));
  let cursor = null, calls = 0, thumbRows = 0;
  for (; calls < 200; calls++) {
    const before = new Set(hub.r2.objects.keys());
    const r = await admin(hub, "POST", "backup", { cursor });
    assertEquals(r.status, 200);
    let bytes = 0;
    for (const k of r.body.written) {
      const size = hub.r2.objects.get(k).bytes.length;
      assert(!before.has(k), "a page was written twice: " + k);
      bytes += size;
      if (k.includes("/thumbs-")) thumbRows += JSON.parse(new TextDecoder().decode(hub.r2.objects.get(k).bytes)).rows.length;
    }
    assert(bytes <= BACKUP_BUDGET, `call ${calls} wrote ${bytes} bytes`);
    assertEquals(r.body.bytes, bytes);
    if (r.body.done) break;
    cursor = r.body.cursor;
  }
  assertEquals(thumbRows, 1000);
  assert(calls >= 32 && calls < 60, "calls: " + calls);
});
