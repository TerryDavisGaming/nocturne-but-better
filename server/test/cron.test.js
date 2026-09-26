// Cron jobs: at most 30 items a job a run, trash, pictures, salts, trusted keys, storage recount, clean-up,
// the orphan sweep, and the download-count fold every sixth hour.

import { assert, assertEquals } from "./assert.js";
import { BASE_TIME, advance, fakeKey, makeHub, publish, register, seedFile, seedPackages, setTime, time } from "./helpers.js";
import { goodBattle } from "./make-fixtures.js";
import { captureConsole } from "./fakes.js";

const HOURLY = "7 * * * *", DAILY = "17 3 * * *", WEEKLY = "23 4 * * 0";

Deno.test("cron: idle uploads expire at most 30 a run", async () => {
  const hub = await makeHub();
  const db = hub.d1.sqlite;
  db.prepare("INSERT INTO uploaders (id, key_hash, created_at) VALUES ('u000000000001', 'h', 0)").run();
  for (let i = 0; i < 40; i++) {
    // Rows only: each uploader may have one open upload, so these get their own uploader ids.
    db.prepare("INSERT INTO uploads (id, uploader_id, client_upload_id, package_id, version, is_update, kind, r2_key, size, sha256, entries_sha256, part_size, parts, state, created_at, updated_at) " +
      "VALUES (?, ?, ?, ?, 1, 0, 'battle', ?, 100, 'x', 'y', 8388608, 1, 'open', ?, ?)").run(`up${String(i).padStart(16, "0")}`, `u${String(i).padStart(12, "0")}`, "c" + i, "p" + i, `pkg/p${i}/1/package`, time() - 3600, time() - 3600);
    await hub.r2.put(`pkg/p${i}/1/package`, "x");
  }
  const one = await hub.cron(HOURLY);
  assertEquals(one.expire.expired, 30);
  const two = await hub.cron(HOURLY);
  assertEquals(two.expire.expired, 10);
  assertEquals(hub.r2.objects.size, 0);
});

Deno.test("cron: due trash is deleted (30 a run) and its bytes come off storage_used", async () => {
  const hub = await makeHub();
  const db = hub.d1.sqlite;
  for (let i = 0; i < 35; i++) {
    db.prepare("INSERT INTO trash (r2_key, bytes, delete_after, why) VALUES (?, 100, ?, 'test')").run(`pkg/t${i}/1/package`, time() + (i < 33 ? -1 : 1000));
    await hub.r2.put(`pkg/t${i}/1/package`, "x");
  }
  db.prepare("UPDATE settings SET v = '5000' WHERE k = 'storage_used'").run();
  assertEquals((await hub.cron(HOURLY)).trash.deleted, 30);
  assertEquals((await hub.cron(HOURLY)).trash.deleted, 3);
  assertEquals(hub.r2.objects.size, 2);
  assertEquals(hub.d1.one("SELECT v FROM settings WHERE k = 'storage_used'").v, "1700");
});

Deno.test("cron: pictures whose delay is over are shown, refused ones never", async () => {
  const hub = await makeHub();
  seedPackages(hub, 3, (i) => ({ picture: ["waiting", "waiting", "refused"][i] }));
  hub.d1.sqlite.prepare("UPDATE packages SET picture_due = ? WHERE picture_state = 'waiting'").run(time() + 100);
  assertEquals((await hub.cron(HOURLY)).pictures.shown, 0);
  advance(101);
  assertEquals((await hub.cron(HOURLY)).pictures.shown, 2);
  assertEquals(hub.d1.q("SELECT picture_state FROM packages ORDER BY seq").map((r) => r.picture_state), ["shown", "shown", "refused"]);
});

Deno.test("cron: the daily salt change keeps one old salt; trusted keys; storage recount; old rows cleaned", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  await register(hub, key);
  const bytes = await goodBattle();
  await publish(hub, key, bytes);
  hub.d1.sqlite.prepare("UPDATE settings SET v = 'first' WHERE k = 'stats_salt'").run();
  hub.d1.sqlite.prepare("UPDATE settings SET v = '999' WHERE k = 'storage_used'").run();
  hub.d1.sqlite.prepare("INSERT INTO audit (at, actor, action) VALUES (1, 'owner', 'ancient')").run();
  let r = await hub.cron(DAILY);
  const salts = () => Object.fromEntries(hub.d1.q("SELECT k, v FROM settings WHERE k LIKE 'stats_salt%'").map((x) => [x.k, x.v]));
  assertEquals(salts().stats_salt_prev, "first");
  assert(salts().stats_salt.length === 64);
  const second = salts().stats_salt;
  await hub.cron(DAILY);
  assertEquals(salts().stats_salt_prev, second);
  assert(!Object.values(salts()).includes("first"), "the salt before is gone");
  assertEquals(r.trusted.trusted, 0);
  assertEquals(hub.d1.one("SELECT v FROM settings WHERE k = 'storage_used'").v, String(bytes.length));
  assertEquals(r.cleanup.audit, 1);
  advance(8 * 86400);
  r = await hub.cron(DAILY);
  assertEquals(r.trusted.trusted, 1);
  assert(hub.d1.one("SELECT trusted_at FROM uploaders").trusted_at !== null);
});

Deno.test("cron: the weekly orphan sweep deletes only objects nothing accounts for", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  await register(hub, key);
  const live = await publish(hub, key, await goodBattle());
  await hub.r2.put("pkg/aaaaaaaaaa/1/package", "orphan");
  await hub.r2.put("pkg/bbbbbbbbbb/3/package", "old, in the trash");
  hub.d1.sqlite.prepare("INSERT INTO trash (r2_key, bytes, delete_after) VALUES ('pkg/bbbbbbbbbb/3/package', 5, ?)").run(time() + 9999);
  await hub.r2.put("quarantine/cccccccccc/1/package", "kept");
  await hub.r2.put("backup/2026-09-01/settings-0000.json", "{}");
  await hub.r2.put("pkg/not-an-id/x/package", "junk");
  const lines = await captureConsole(async () => {
    const r = await hub.cron(WEEKLY);
    assertEquals(r.orphans.deleted, 2);
  });
  assert(lines.some((l) => /route=cron-orphans code=deleted count=2/.test(l)));
  assertEquals([...hub.r2.objects.keys()].sort(), ["backup/2026-09-01/settings-0000.json", "pkg/bbbbbbbbbb/3/package", `pkg/${live}/1/package`, "quarantine/cccccccccc/1/package"].sort());
});

Deno.test("cron: the orphan sweep pages through big buckets across runs", async () => {
  const hub = await makeHub();
  for (let i = 0; i < 1100; i++) await hub.r2.put(`pkg/${String(i).padStart(10, "0")}/1/package`, "x");
  const kept = seedPackages(hub, 1, () => ({ id: "0000000005" }));
  assert(kept[0] === "0000000005");
  let deleted = 0;
  for (let run = 0; run < 60 && hub.r2.objects.size > 1; run++) deleted += (await hub.cron(WEEKLY)).orphans.deleted;
  assertEquals(deleted, 1099);
  assertEquals([...hub.r2.objects.keys()], ["pkg/0000000005/1/package"]);
});

Deno.test("cron: download counts fold only on every sixth hour; failures in one job don't stop the others", async () => {
  const hub = await makeHub({ env: { STATS_TOKEN: "fake", STATS_ACCOUNT_ID: "acct" } });
  const calls = [];
  const saved = globalThis.fetch;
  globalThis.fetch = async (url) => {
    calls.push(String(url));
    return new Response("nope", { status: 500 });
  };
  try {
    setTime(BASE_TIME + 3600); // 01:00 UTC
    let r = await hub.cron(HOURLY);
    assertEquals(r.fold, undefined);
    setTime(BASE_TIME + 6 * 3600); // 06:00 UTC
    const lines = await captureConsole(async () => {
      r = await hub.cron(HOURLY);
    });
    assertEquals(r.fold, { error: true });
    assert(r.trash && !r.trash.error);
    assert(lines.some((l) => l === "route=cron-fold code=stats_http_500"), lines.join("\n"));
    assert(calls[0].startsWith("https://api.cloudflare.com/client/v4/accounts/acct/analytics_engine/sql"));
  } finally {
    globalThis.fetch = saved;
  }
});

Deno.test("cron: scheduled() without a database does nothing; with one it runs the jobs", async () => {
  const bare = await makeHub({ drop: ["DB"] });
  await bare.scheduled(HOURLY);
  const hub = await makeHub();
  const [id] = seedPackages(hub, 1);
  await seedFile(hub, id);
  await hub.scheduled(DAILY);
  assertEquals(hub.d1.one("SELECT v FROM settings WHERE k = 'storage_used'").v, "1000");
});
