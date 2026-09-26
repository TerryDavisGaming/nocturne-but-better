// Browse, search, lookup, detail and files: filters, sorts, paging, strict query strings, cache and file headers.

import { assert, assertEquals, assertMatch } from "./assert.js";
import { BASE_TIME, fakeKey, makeHub, publish, register, seedFile, seedPackages } from "./helpers.js";
import { goodBattle, thumbB64 } from "./make-fixtures.js";

const ids = (r) => r.body.items.map((i) => i.id);

Deno.test("browse: newest first across the four kind and lanes facets, with filters", async () => {
  const hub = await makeHub();
  const seeded = seedPackages(hub, 40);
  const all = await hub.call("GET", "/v1/packages");
  assertEquals(all.status, 200);
  assertEquals(all.body.items.length, 24);
  assertEquals(ids(all), [...seeded].reverse().slice(0, 24));
  const battles5 = await hub.call("GET", "/v1/packages?kind=battle&lanes=5");
  assert(battles5.body.items.every((i) => i.kind === "battle" && i.lanes === 5));
  assertEquals(battles5.body.items.length, 10);
  assertEquals(battles5.body.next, null);
});

Deno.test("browse: keyset paging returns every entry once, even with equal times", async () => {
  const hub = await makeHub();
  const seeded = seedPackages(hub, 100, (i) => ({ created: BASE_TIME - 1000 + Math.floor(i / 7) }));
  const seen = [];
  let cursor = null;
  for (let page = 0; page < 10; page++) {
    const r = await hub.call("GET", "/v1/packages" + (cursor ? "?cursor=" + cursor : ""));
    assertEquals(r.status, 200, JSON.stringify(r.body));
    seen.push(...ids(r));
    cursor = r.body.next;
    if (!cursor) break;
  }
  assertEquals(seen.length, 100);
  assertEquals(new Set(seen).size, 100);
  assertEquals(new Set(seen), new Set(seeded));
});

Deno.test("browse: most downloaded and title (A-Z), each paged", async () => {
  const hub = await makeHub();
  seedPackages(hub, 30, (i) => ({ downloads: (i * 7) % 11, title: ["b", "A", "\u00C9", "c"][i % 4] + " song " + i }));
  const pop = await hub.call("GET", "/v1/packages?sort=popular");
  const downloads = pop.body.items.map((i) => i.downloads);
  assert(downloads.every((d) => d === null), "counts are off");
  const rows = hub.d1.q("SELECT id FROM packages ORDER BY downloads DESC, seq DESC LIMIT 24").map((r) => r.id);
  assertEquals(ids(pop), rows);
  const byTitle = [];
  let cursor = null;
  do {
    const r = await hub.call("GET", "/v1/packages?sort=title" + (cursor ? "&cursor=" + cursor : ""));
    byTitle.push(...r.body.items.map((i) => i.title));
    cursor = r.body.next;
  } while (cursor);
  assertEquals(byTitle.length, 30);
  assert(byTitle[0].startsWith("A") && byTitle[byTitle.length - 1].startsWith("\u00C9"), byTitle.join(","));
});

Deno.test("browse: more by this uploader, newest first", async () => {
  const hub = await makeHub();
  const a = seedPackages(hub, 30);
  seedPackages(hub, 5);
  const uploader = hub.d1.one("SELECT uploader_id FROM packages WHERE id = ?", a[0]).uploader_id;
  const r = await hub.call("GET", `/v1/packages?uploader=${uploader}`);
  assertEquals(r.body.items.length, 24);
  assert(r.body.items.every((i) => i.uploader.id === uploader));
  const r2 = await hub.call("GET", `/v1/packages?uploader=${uploader}&cursor=${r.body.next}`);
  assertEquals(r2.body.items.length, 6);
  assertEquals((await hub.call("GET", `/v1/packages?uploader=${uploader}&sort=title`)).status, 400);
  const kind = await hub.call("GET", `/v1/packages?kind=charts&uploader=${uploader}`);
  assert(kind.body.items.every((i) => i.kind === "charts"));
});

Deno.test("search: title, artist, charter, songs and description; prefix only from 3 letters; accents ignored", async () => {
  const hub = await makeHub();
  seedPackages(hub, 1, () => ({ title: "Moonlit Duel", artist: "Kirara", author: "Mika", kind: "battle" }));
  seedPackages(hub, 1, () => ({ title: "Firefly remix", kind: "charts", songs: ["Firefly - 1"], description: "harder streams" }));
  seedPackages(hub, 1, () => ({ title: "M\u00F6\u00F6nlight", kind: "battle" }));
  seedPackages(hub, 1, () => ({ title: "mo", kind: "battle" }));
  const q = async (text, extra = "") => (await hub.call("GET", `/v1/packages?${extra}q=${encodeURIComponent(text)}`)).body.items.map((i) => i.title);
  assertEquals(await q("moonlit"), ["Moonlit Duel"]);
  assertEquals((await q("moo")).sort(), ["Moonlit Duel", "M\u00F6\u00F6nlight"].sort());
  assertEquals(await q("mo"), ["mo"]);
  assertEquals(await q("kirara"), ["Moonlit Duel"]);
  assertEquals(await q("mika duel"), ["Moonlit Duel"]);
  assertEquals(await q("firefly"), ["Firefly remix"]);
  assertEquals(await q("streams"), ["Firefly remix"]);
  assertEquals(await q("moonlight"), ["M\u00F6\u00F6nlight"]);
  assertEquals(await q("moo", "kind=charts&"), []);
});

Deno.test("search: at most 200 hits, 24 a page, 8 pages; other sorts work on the hits", async () => {
  const hub = await makeHub();
  seedPackages(hub, 260, (i) => ({ title: `Common tune ${i}`, downloads: i }));
  const seen = [];
  let cursor = null, pages = 0;
  do {
    const r = await hub.call("GET", "/v1/packages?q=common" + (cursor ? "&cursor=" + cursor : ""));
    assertEquals(r.status, 200, JSON.stringify(r.body));
    seen.push(...ids(r));
    cursor = r.body.next;
    pages++;
  } while (cursor);
  assertEquals(pages, 8);
  assertEquals(new Set(seen).size, seen.length);
  assert(seen.length <= 200);
  const popular = await hub.call("GET", "/v1/packages?q=common&sort=popular");
  assertEquals(popular.status, 200);
  assertEquals((await hub.call("GET", "/v1/packages?q=common&cursor=" + btoa('["o",200]').replace(/=+$/, ""))).status, 400);
});

Deno.test("search: injection strings and non-normalized text are refused before the database", async () => {
  const hub = await makeHub();
  seedPackages(hub, 3);
  const before = hub.d1.statements.length;
  for (const q of ['"', "*", "NEAR(a", "title:moon", "moon OR duel", '"unbalanced', "one two three four five", "Moon", "a", "moon  duel", "moon%20", " moon"]) {
    const r = await hub.call("GET", "/v1/packages?q=" + encodeURIComponent(q));
    assertEquals(r.status, 400, q);
    assertEquals(r.body.error, "bad_query");
  }
  assertEquals(hub.d1.statements.length, before);
  const ok = await hub.call("GET", "/v1/packages?q=" + encodeURIComponent("near title or"));
  assertEquals(ok.status, 200);
});

Deno.test("strict query strings: unknown, repeated, reordered, empty, default or mis-encoded parameters are 400 with no D1 call", async () => {
  const hub = await makeHub();
  seedPackages(hub, 3);
  const id = hub.d1.one("SELECT id FROM packages").id;
  const before = hub.d1.statements.length;
  for (const qs of ["x=1", "kind=battle&kind=battle", "lanes=4&kind=battle", "kind=", "sort=new", "q=moon&sort=best", "kind=Battle", "lanes=6",
    "q=moon+duel", "cursor=%%%", "uploader=u123", "sort=popular&x=1", "&", "kind=battle&"]) {
    const r = await hub.call("GET", "/v1/packages?" + qs);
    assertEquals(r.status, 400, qs);
  }
  assertEquals((await hub.call("GET", `/v1/packages/${id}?x=1`)).status, 400);
  assertEquals((await hub.call("GET", `/v1/packages/${id}?`)).status, 400);
  assertEquals((await hub.call("GET", `/v1/files/${id}/1/package?x=1`)).status, 400);
  assertEquals((await hub.call("GET", "/v1/packages/lookup?ids=b,a")).status, 400);
  assertEquals(hub.d1.statements.length, before);
});

Deno.test("lookup: sorted unique ids, every status, missing ids", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  await register(hub, key);
  const a = await publish(hub, key, await goodBattle());
  const [b, c, d] = seedPackages(hub, 3, (i) => ({ status: ["removed", "deleted", "hidden"][i] }));
  hub.d1.sqlite.prepare("UPDATE packages SET removed_reason = 'copyright' WHERE id = ?").run(b);
  const list = [a, b, c, d, "zzzzzzzzzz"].sort();
  const r = await hub.call("GET", "/v1/packages/lookup?ids=" + list.join(","));
  assertEquals(r.status, 200);
  const by = Object.fromEntries(r.body.items.map((i) => [i.id, i]));
  assertEquals(by[a].status, "live");
  assertEquals(by[a].version, 1);
  assertEquals(by[a].sha256.length, 64);
  assertEquals(by[b].status, "removed");
  assertEquals(by[b].reason, "copyright");
  assertEquals(by[c].status, "deleted");
  assertEquals(by[d].status, "unavailable");
  assertEquals(by.zzzzzzzzzz.status, "missing");
  assertEquals(r.headers.get("Cache-Tag"), "list");
});

Deno.test("detail and file: 404, and 410 removed, deleted or under review", async () => {
  const hub = await makeHub();
  const [live, removed, deleted, hidden] = seedPackages(hub, 4, (i) => ({ status: ["live", "removed", "deleted", "hidden"][i] }));
  hub.d1.sqlite.prepare("UPDATE packages SET removed_reason = 'offensive' WHERE id = ?").run(removed);
  assertEquals((await hub.call("GET", "/v1/packages/zzzzzzzzzz")).status, 404);
  assertEquals((await hub.call("GET", "/v1/packages/not-an-id")).status, 404);
  const r = await hub.call("GET", `/v1/packages/${removed}`);
  assertEquals([r.status, r.body.error, r.body.reason], [410, "removed", "offensive"]);
  assertEquals((await hub.call("GET", `/v1/packages/${deleted}`)).body.error, "deleted");
  assertEquals((await hub.call("GET", `/v1/packages/${hidden}`)).body.error, "unavailable");
  assertEquals((await hub.call("GET", `/v1/files/${removed}/1/package`)).status, 410);
  assertEquals((await hub.call("GET", `/v1/files/${live}/1/package`)).status, 404); // no object in R2
  await seedFile(hub, live);
  assertEquals((await hub.call("GET", `/v1/files/${live}/1/package`)).status, 200);
});

Deno.test("headers: files are attachments in a sandbox, cached for a day; lists 60 s with stale-while-revalidate; errors never cached", async () => {
  const hub = await makeHub();
  const [id] = seedPackages(hub, 1, () => ({ kind: "battle" }));
  await seedFile(hub, id);
  const f = await hub.call("GET", `/v1/files/${id}/1/package`);
  assertEquals(f.headers.get("Content-Type"), "application/octet-stream");
  assertEquals(f.headers.get("Content-Disposition"), `attachment; filename="${id}.nbbbattle"`);
  assertEquals(f.headers.get("Content-Security-Policy"), "default-src 'none'; sandbox");
  assertEquals(f.headers.get("Cross-Origin-Resource-Policy"), "same-origin");
  assertEquals(f.headers.get("Cache-Control"), "public, s-maxage=86400");
  assertEquals(f.headers.get("Cache-Tag"), `pkg-${id}`);
  const list = await hub.call("GET", "/v1/packages");
  assertEquals(list.headers.get("Cache-Control"), "public, max-age=60, stale-while-revalidate=300");
  assert(!/s-maxage|must-revalidate/.test(list.headers.get("Cache-Control")));
  const detail = await hub.call("GET", `/v1/packages/${id}`);
  assertEquals(detail.headers.get("Cache-Tag"), `pkg-${id}`);
  assertEquals(detail.headers.get("Cache-Control"), "public, max-age=60, stale-while-revalidate=300");
  assertEquals((await hub.call("GET", "/v1/info")).headers.get("Cache-Control"), "public, max-age=300");
  const err = await hub.call("GET", "/v1/packages/zzzzzzzzzz");
  assertEquals(err.headers.get("Cache-Control"), "no-store");
  assertEquals(Object.keys(err.body).sort(), ["error", "message"]);
  const me = await hub.call("GET", "/v1/me", { key: fakeKey(1) });
  assertEquals(me.headers.get("Cache-Control"), "no-store");
  for (const r of [f, list, detail, err, me]) {
    assertEquals(r.headers.get("X-Content-Type-Options"), "nosniff");
    assertEquals(r.headers.get("Referrer-Policy"), "no-referrer");
    for (const [k] of r.headers) assert(!k.toLowerCase().startsWith("access-control-"), k);
  }
});

Deno.test("cards: flags, source, thumbnails shown only when allowed, and downloads null while counts are off", async () => {
  const hub = await makeHub();
  const thumb = thumbB64();
  seedPackages(hub, 1, () => ({ kind: "battle", thumb, picture: "shown" }));
  seedPackages(hub, 1, () => ({ kind: "battle", thumb, picture: "waiting" }));
  seedPackages(hub, 1, () => ({ kind: "battle", thumb, picture: "refused" }));
  const r = await hub.call("GET", "/v1/packages?kind=battle");
  assertEquals(r.body.items.map((i) => Boolean(i.thumb)), [false, false, true]);
  assert(r.body.items.every((i) => i.downloads === null));
  const card = r.body.items[0];
  for (const k of ["id", "kind", "version", "status", "title", "artist", "author", "uploader", "lanes", "difficulties", "songs", "battleId", "size", "downloads",
    "createdAt", "updatedAt", "lengthSeconds", "bpm", "flags", "source", "format", "requires"]) assert(k in card, k);
  assertEquals(card.uploader.tag.length, 4);
});

Deno.test("installed: one Analytics Engine data point and no database write; limited per address", async () => {
  const hub = await makeHub();
  const [id] = seedPackages(hub, 1);
  hub.d1.sqlite.prepare("UPDATE settings SET v = 'salt-for-tests' WHERE k = 'stats_salt'").run();
  const before = hub.d1.statements.filter((s) => s.changes > 0).length;
  const r = await hub.call("POST", `/v1/packages/${id}/installed`, { json: { version: 1 } });
  assertEquals(r.status, 204);
  assertEquals(hub.ae.points.length, 1);
  assertEquals(hub.ae.points[0].indexes, [id]);
  assertEquals(hub.ae.points[0].blobs[0], id);
  assert(!hub.ae.points[0].blobs[1].includes("203.0.113.7"));
  assertEquals(hub.d1.statements.filter((s) => s.changes > 0).length, before);
  for (let i = 0; i < 4; i++) await hub.call("POST", `/v1/packages/${id}/installed`, { json: { version: 1 } });
  const limited = await hub.call("POST", `/v1/packages/${id}/installed`, { json: { version: 1 } });
  assertEquals(limited.status, 429);
  assertEquals(limited.headers.get("Retry-After"), "30");
  assertEquals((await hub.call("POST", `/v1/packages/${id}/installed`, { json: { version: 0 }, ip: "198.51.100.1" })).status, 400);
  // The same address counts once per package per salt.
  assertEquals(hub.ae.points[0].blobs[1], hub.ae.points[1].blobs[1]);
});

Deno.test("download counts: folded from the Analytics Engine SQL API into D1 every 6 hours, only with a token", async () => {
  const hub = await makeHub({ env: { STATS_TOKEN: "fake-token", STATS_ACCOUNT_ID: "fake-account" } });
  const [a, b] = seedPackages(hub, 2);
  hub.d1.sqlite.prepare("UPDATE settings SET v = ? WHERE k = 'stats_folded_until'").run(String(BASE_TIME - 1000));
  await hub.call("POST", `/v1/packages/${a}/installed`, { json: { version: 1 }, ip: "198.51.100.1" });
  await hub.call("POST", `/v1/packages/${a}/installed`, { json: { version: 1 }, ip: "198.51.100.2" });
  await hub.call("POST", `/v1/packages/${a}/installed`, { json: { version: 1 }, ip: "198.51.100.2" });
  await hub.call("POST", `/v1/packages/${b}/installed`, { json: { version: 1 }, ip: "2001:db8:1:2:3:4:5:6" });
  const { foldDownloads } = await import("../src/stats.js");
  const { setTime } = await import("./helpers.js");
  setTime(BASE_TIME + 3600);
  const before = hub.d1.statements.length;
  const r = await foldDownloads(hub.env(), hub.ae.sqlApi({ token: "fake-token" }));
  assertEquals(r.folded, 2);
  assertEquals(hub.d1.statements.slice(before).filter((s) => /downloads = downloads/.test(s.sql)).length, 1);
  assertEquals(hub.d1.one("SELECT downloads FROM packages WHERE id = ?", a).downloads, 2);
  assertEquals(hub.d1.one("SELECT downloads FROM packages WHERE id = ?", b).downloads, 1);
  const info = await hub.call("GET", "/v1/info");
  assertEquals(info.body.counts, true);
  const list = await hub.call("GET", "/v1/packages?sort=popular");
  assertEquals(list.body.items[0].downloads, 2);
  const off = await makeHub();
  assertEquals((await foldDownloads(off.env(), () => { throw new Error("no call expected"); })).skipped, true);
  assertEquals((await off.call("GET", "/v1/info")).body.counts, false);
});

Deno.test("BLOCKED_IDS: the file and detail routes refuse before touching D1, even with D1 down", async () => {
  const hub = await makeHub();
  const [id] = seedPackages(hub, 1);
  await seedFile(hub, id);
  hub.vars.BLOCKED_IDS = ` other1234 , ${id.toUpperCase()} `;
  hub.d1.down = true;
  const f = await hub.call("GET", `/v1/files/${id}/1/package`);
  assertEquals([f.status, f.body.error], [410, "unavailable"]);
  const d = await hub.call("GET", `/v1/packages/${id}`);
  assertEquals([d.status, d.body.error], [410, "unavailable"]);
  const l = await hub.call("GET", "/v1/packages");
  assertEquals([l.status, l.body.error], [503, "db_unavailable"]);
});

Deno.test("free-plan limits: D1's daily limit spent is 503 busy_today; a hub not set up is 503 not_set_up", async () => {
  const hub = await makeHub();
  hub.d1.limitSpent = true;
  const r = await hub.call("GET", "/v1/info");
  assertEquals([r.status, r.body.error], [503, "busy_today"]);
  const bare = await makeHub({ drop: ["DB"] });
  assertEquals((await bare.call("GET", "/v1/info")).body.error, "not_set_up");
  const empty = await makeHub();
  empty.d1.sqlite.exec("DROP TABLE settings");
  assertEquals((await empty.call("GET", "/v1/info")).body.error, "not_set_up");
  assertEquals((await bare.call("GET", "/robots.txt")).status, 200);
});

Deno.test("versioning: everything is under /v1/; other versions and unknown paths are 404; info says api 1", async () => {
  const hub = await makeHub();
  const info = await hub.call("GET", "/v1/info");
  assertEquals(info.body.api, 1);
  assertEquals(info.body.minClient, "2.8.0");
  assertEquals(info.body.hub, "nocturne but better hub");
  assertEquals(info.body.partSize, 8388608);
  assertEquals(info.body.maxPackageBytes, 104857600);
  assertEquals(info.body.media.video, ["webm-vp8"]);
  assertEquals(info.body.legalUrl, "https://hub.nocturnbutbetter.com/legal");
  assertEquals(info.headers.get("Cache-Tag"), "info");
  const v2 = await hub.call("GET", "/v2/info");
  assertEquals(v2.status, 404);
  assertMatch(v2.body.message, /API v1/);
  assertEquals((await hub.call("GET", "/v1/nothing")).status, 404);
  assertEquals((await hub.call("POST", "/v1/info", { json: {} })).status, 405);
  assertEquals((await hub.call("GET", "/v1/packages/zzzzzzzzzz/report")).status, 405);
});
