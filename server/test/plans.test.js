// Rows-read guards (DESIGN-HUB 6.1): every browse, uploader and search query walks an index, never the whole
// packages table; a search reads at most its best 200 hits; the search index survives VACUUM and a restore.

import { assert, assertEquals } from "./assert.js";
import { makeHub, seedPackages } from "./helpers.js";
import { browseQuery, facets, searchQuery, uploaderQuery } from "../src/search.js";
import { matchString } from "../src/names.js";

function plan(hub, sql, params) {
  return hub.d1.q("EXPLAIN QUERY PLAN " + sql, ...params).map((r) => r.detail);
}

function assertNoTableScan(lines, what) {
  for (const l of lines) {
    assert(!/^SCAN (packages|p)\b/.test(l) && !/SCAN packages\b/.test(l), `${what}: ${l}\n${lines.join("\n")}`);
  }
}

Deno.test("plans: each browse facet reads its sort's index in order, for every filter, sort and cursor", async () => {
  const hub = await makeHub();
  seedPackages(hub, 50);
  const cursors = { new: { value: 1, seq: 5 }, popular: { value: 3, seq: 5 }, title: { value: "m", seq: 5 } };
  for (const sort of ["new", "popular", "title"]) {
    for (const kind of [null, "battle", "charts"]) {
      for (const lanes of [null, 4, 5]) {
        for (const cursor of [null, cursors[sort]]) {
          const { sql, params } = browseQuery({ kind, lanes, sort, cursor });
          const lines = plan(hub, sql, params);
          assertNoTableScan(lines, `${sort} ${kind} ${lanes}`);
          const index = { new: "pk_new", popular: "pk_pop", title: "pk_title" }[sort];
          const uses = lines.filter((l) => l.includes(`USING INDEX ${index}`) || l.includes(`USING COVERING INDEX ${index}`)).length;
          assertEquals(uses, facets(kind, lanes).length, lines.join("\n"));
          // The only sorting left is merging at most 4 x 25 rows, never over the table.
          for (const l of lines) if (/TEMP B-TREE/.test(l)) assert(/ORDER BY|UNION|RIGHT PART/.test(l), l);
        }
      }
    }
  }
});

Deno.test("plans: more by this uploader walks pk_owner; lookups and details use the id index", async () => {
  const hub = await makeHub();
  seedPackages(hub, 20);
  const u = hub.d1.one("SELECT uploader_id FROM packages").uploader_id;
  for (const q of [uploaderQuery({ uploader: u }), uploaderQuery({ uploader: u, kind: "battle", lanes: 4, cursor: { value: 5, seq: 9 } })]) {
    const lines = plan(hub, q.sql, q.params);
    assertNoTableScan(lines, "uploader");
    assert(lines.some((l) => /pk_owner/.test(l)), lines.join("\n"));
  }
  const lookup = plan(hub, "SELECT id, status FROM packages WHERE id IN (SELECT value FROM json_each(?1))", ['["a","b"]']);
  assertNoTableScan(lookup, "lookup");
  const detail = plan(hub, "SELECT p.*, u.name FROM packages p LEFT JOIN uploaders u ON u.id = p.uploader_id LEFT JOIN thumbs t ON t.package_id = p.id WHERE p.id = ?1", ["a"]);
  assertNoTableScan(detail, "detail");
});

Deno.test("plans: a search is bounded to its best 200 hits before the join, even in a catalogue of 10,000", async () => {
  const hub = await makeHub();
  seedPackages(hub, 10000, (i) => ({ title: `Song number ${i} ${i % 3 ? "remix" : "original"}`, artist: i % 2 ? "Common Artist" : "Other" }));
  const inner = hub.d1.q("SELECT rowid AS seq, rank FROM packages_fts WHERE packages_fts MATCH ?1 ORDER BY rank LIMIT 200", matchString("common"));
  assertEquals(inner.length, 200);
  for (const sort of ["best", "new", "popular", "title"]) {
    const q = searchQuery({ q: "common remix", kind: "battle", lanes: 4, sort, offset: 24 });
    const lines = plan(hub, q.sql, q.params);
    assertNoTableScan(lines, "search " + sort);
    assert(lines.some((l) => /packages_fts VIRTUAL TABLE/.test(l)), lines.join("\n"));
    assert(lines.some((l) => /SEARCH p USING INTEGER PRIMARY KEY/.test(l)), lines.join("\n"));
  }
  const r = await hub.call("GET", "/v1/packages?q=common");
  assertEquals(r.body.items.length, 24);
  const page = await hub.call("GET", "/v1/packages?kind=battle&lanes=5");
  assertEquals(page.body.items.length, 24);
});

Deno.test("search index: stable across VACUUM, and rebuilt the same after a restore into a fresh database", async () => {
  const hub = await makeHub();
  seedPackages(hub, 200, (i) => ({ title: `Track ${i} ${["alpha", "beta", "gamma"][i % 3]}`, status: i % 10 === 0 ? "removed" : "live" }));
  hub.d1.sqlite.prepare("DELETE FROM packages WHERE id IN (SELECT id FROM packages WHERE status = 'removed' LIMIT 5)").run();
  const search = async (h, q) => (await h.call("GET", "/v1/packages?q=" + q)).body.items.map((i) => i.id).sort();
  const before = { alpha: await search(hub, "alpha"), beta: await search(hub, "beta"), track: await search(hub, "track") };
  // packages.seq is an INTEGER PRIMARY KEY (a rowid alias), so VACUUM keeps it and the FTS rows keyed on it.
  const seq = hub.d1.q("PRAGMA table_info(packages)").find((c) => c.name === "seq");
  assertEquals([seq.type, seq.pk], ["INTEGER", 1]);
  try {
    hub.d1.sqlite.exec("VACUUM");
    for (const q of Object.keys(before)) assertEquals(await search(hub, q), before[q], "after VACUUM: " + q);
  } catch (e) {
    // Deno's built-in SQLite allows no attached databases, which VACUUM needs; the schema check above stands in.
    assert(/attached/.test(e.message), e.message);
  }

  // Export the base tables (as the backup does), import them into a fresh database, rebuild the index.
  const fresh = await makeHub();
  const db = fresh.d1.sqlite;
  db.exec("DELETE FROM settings");
  for (const table of ["uploaders", "packages", "thumbs", "settings"]) {
    const rows = hub.d1.q(`SELECT * FROM ${table}`);
    if (!rows.length) continue;
    const cols = Object.keys(rows[0]);
    const ins = db.prepare(`INSERT INTO ${table} (${cols.join(", ")}) VALUES (${cols.map(() => "?").join(", ")})`);
    db.exec("BEGIN");
    for (const r of rows) ins.run(...cols.map((c) => r[c]));
    db.exec("COMMIT");
  }
  assertEquals(fresh.d1.one("SELECT count(*) AS n FROM packages_fts").n, 0);
  let after = 0, done = false;
  while (!done) {
    const r = await fresh.call("POST", "/v1/admin/reindex", { admin: true, json: { after } });
    after = r.body.after;
    done = r.body.done;
  }
  for (const q of Object.keys(before)) assertEquals(await search(fresh, q), before[q], "after restore: " + q);
});

Deno.test("plans: the review's new queries use their indexes (trash by key range, the sweep's packages by key, a key's day of uploads)", async () => {
  const hub = await makeHub();
  seedPackages(hub, 20);
  const trash = plan(hub, "SELECT r2_key FROM trash WHERE r2_key >= ?1 AND r2_key < ?2 LIMIT 1", ["pkg/a/1/", "pkg/a/10"]);
  assert(trash.some((l) => /SEARCH trash USING PRIMARY KEY \(r2_key>\? AND r2_key<\?\)/.test(l)), trash.join("\n"));
  const sweep = plan(hub, "SELECT j.value AS k FROM json_each(?1) j WHERE EXISTS (SELECT 1 FROM trash t WHERE t.r2_key = j.value) " +
    "OR EXISTS (SELECT 1 FROM packages p WHERE p.r2_key = j.value)", ['["a"]']);
  assertNoTableScan(sweep, "sweep");
  assert(sweep.some((l) => /pk_key/.test(l)), sweep.join("\n"));
  const day = plan(hub, "SELECT count(*) AS tries FROM uploads WHERE uploader_id = ?1 AND created_at > ?2", ["u1", 0]);
  assert(day.some((l) => /up_owner/.test(l)), day.join("\n"));
  const address = plan(hub, "SELECT n FROM address_day WHERE h = ?1 AND what = ?2", ["h", "report"]);
  assert(address.some((l) => /PRIMARY KEY/.test(l)), address.join("\n"));
});
