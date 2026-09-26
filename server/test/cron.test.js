// Cron jobs: at most 30 items a job a run, trash, pictures, salts, trusted keys, storage recount, clean-up,
// the orphan sweep, and the download-count fold every sixth hour.

import { assert, assertEquals } from "./assert.js";
import { BASE_TIME, advance, fakeKey, makeHub, publish, register, seedFile, seedKey, seedPackages, setTime, time, upload } from "./helpers.js";
import { CRONS, sweepOrphans } from "../src/cron.js";
import { goodBattle } from "./make-fixtures.js";
import { captureConsole } from "./fakes.js";

const HOURLY = "7 * * * *", DAILY = "17 3 * * *";

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

Deno.test("cron: the daily orphan sweep deletes only objects nothing accounts for", async () => {
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
    const r = await hub.cron(DAILY);
    assertEquals(r.orphans.deleted, 2);
  });
  assert(lines.some((l) => /route=cron-orphans code=deleted count=2/.test(l)));
  const liveKey = hub.d1.one("SELECT r2_key FROM packages WHERE id = ?", live).r2_key;
  assertEquals([...hub.r2.objects.keys()].sort(), ["backup/2026-09-01/settings-0000.json", "pkg/bbbbbbbbbb/3/package", liveKey, "quarantine/cccccccccc/1/package"].sort());
});

Deno.test("cron: the orphan sweep pages through big buckets across runs", async () => {
  const hub = await makeHub();
  for (let i = 0; i < 2500; i++) await hub.r2.put(`pkg/${String(i).padStart(10, "0")}/1/package`, "x");
  const kept = seedPackages(hub, 1, () => ({ id: "0000000005" }));
  assert(kept[0] === "0000000005");
  await seedFile(hub, "0000000005");
  const runs = [];
  for (let run = 0; run < 10 && hub.r2.objects.size > 1; run++) runs.push((await hub.cron(DAILY)).orphans.deleted);
  // Up to 1000 a run (one listed page, one R2 delete call); the kept file is on the first page.
  assertEquals(runs, [999, 1000, 501]);
  assertEquals(hub.r2.ops.delete, 3);
  assertEquals([...hub.r2.objects.keys()], [seedKey("0000000005")]);
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

// ---- the cron config, and the sweep against a publish (the review's CF-1, CF-6) ----------------------------

/** wrangler.jsonc without its comments (outside strings), parsed. */
async function wranglerConfig() {
  const text = await Deno.readTextFile(new URL("../wrangler.jsonc", import.meta.url));
  let out = "", inString = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (inString) {
      out += c;
      if (c === "\\") out += text[++i];
      else if (c === '"') inString = false;
    } else if (c === '"') {
      inString = true;
      out += c;
    } else if (c === "/" && text[i + 1] === "/") {
      while (i < text.length && text[i] !== "\n") i++;
      out += "\n";
    } else out += c;
  }
  return JSON.parse(out);
}

/** Cloudflare's cron syntax: minute 0-59, hour 0-23, day 1-31, month 1-12 or JAN-DEC, weekday 1-7 or SUN-SAT. */
function cronFieldOk(field, min, max, names = []) {
  return field.split(",").every((part) => {
    const [range, step] = part.split("/");
    if (step !== undefined && !/^[1-9][0-9]*$/.test(step)) return false;
    if (range === "*") return true;
    return range.split("-").every((v) => (/^[0-9]+$/.test(v) ? Number(v) >= min && Number(v) <= max : names.includes(v.toUpperCase())));
  });
}

Deno.test("cron: wrangler.jsonc's triggers are exactly the jobs' cron strings, in Cloudflare's syntax (weekdays 1-7 or SUN-SAT)", async () => {
  const config = await wranglerConfig();
  assertEquals(config.triggers.crons, CRONS);
  const days = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];
  const months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
  for (const cron of config.triggers.crons) {
    const f = cron.split(" ");
    assertEquals(f.length, 5, cron);
    assert(cronFieldOk(f[0], 0, 59) && cronFieldOk(f[1], 0, 23) && cronFieldOk(f[2], 1, 31) && cronFieldOk(f[3], 1, 12, months), cron);
    assert(cronFieldOk(f[4], 1, 7, days), `weekday field of "${cron}"`);
  }
  // The check itself catches the mistake the review found.
  assert(!cronFieldOk("0", 1, 7, days));
  assert(cronFieldOk("SUN", 1, 7, days) && cronFieldOk("1-7", 1, 7, days));
  // And the other bindings the code expects are there.
  assertEquals(config.routes, [{ pattern: "hub.nocturnbutbetter.com", zone_name: "nocturnbutbetter.com", custom_domain: true }]);
  assertEquals([config.workers_dev, config.preview_urls], [false, false]);
});

Deno.test("cron: a trigger the code doesn't know does nothing but log unknown_cron", async () => {
  const hub = await makeHub();
  let report;
  const lines = await captureConsole(async () => {
    report = await hub.cron("23 4 * * SUN");
  });
  assertEquals(report, {});
  assert(lines.some((l) => l === "route=cron code=unknown_cron"), lines.join("\n"));
});

async function sweepWithPublishAt(pattern) {
  const hub = await makeHub();
  const key = fakeKey(1);
  await register(hub, key);
  const r = await upload(hub, key, await goodBattle(), { noComplete: true }); // one part: its object is in R2, the upload 'open'
  const { uploadId, packageId } = r.start.body;
  const objKey = hub.d1.one("SELECT r2_key FROM uploads WHERE id = ?", uploadId).r2_key;
  assert(hub.r2.objects.has(objKey), "setup");
  const env = hub.env();
  const prepare = env.DB.prepare.bind(env.DB);
  let staged = false;
  env.DB.prepare = (sql) => {
    if (!staged && pattern.test(sql)) {
      // The upload's complete commits right here: it's live, and its package names its object.
      staged = true;
      hub.d1.sqlite.prepare("UPDATE uploads SET state = 'live' WHERE id = ?").run(uploadId);
      seedPackages(hub, 1, () => ({ id: packageId, kind: "battle" }));
      hub.d1.sqlite.prepare("UPDATE packages SET r2_key = ? WHERE id = ?").run(objKey, packageId);
    }
    return prepare(sql);
  };
  const out = await sweepOrphans(env);
  assert(staged, "the publish was staged");
  return { out, kept: hub.r2.objects.has(objKey) };
}

Deno.test("cron: the orphan sweep never deletes a file that an upload publishes while the sweep runs", async () => {
  // Just before its first query and between its two queries: the live file is kept each time.
  for (const pattern of [/FROM uploads WHERE state IN/, /FROM json_each\(\?1\) j WHERE EXISTS/]) {
    const { out, kept } = await sweepWithPublishAt(pattern);
    assert(kept, `the live entry's file was deleted (published at ${pattern})`);
    assertEquals(out.deleted, 0);
  }
});
