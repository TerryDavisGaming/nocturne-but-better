// The server's package check against every fixture: good packages, malicious zips, and the facts it reads.

import { assert, assertEquals, assertMatch } from "./assert.js";
import { FakeR2 } from "./fakes.js";
import { checkPackage } from "../src/facts.js";
import { ZipProblem, fingerprint, nameProblem, parseCentral, parseEnd } from "../src/zipcheck.js";
import { sha256Hex } from "../src/ids.js";
import { crc32 } from "../src/inflate.js";
import { CHART, allFixtures, battleJson, buildZip, goodBattle, goodPack, manifestJson, media } from "./make-fixtures.js";

const LIMITS = { maxEntries: 1000, maxUnpacked: 200 * 1024 * 1024, maxSongs: 40 };

async function check(bytes, kind = "battle", limits = LIMITS) {
  const r2 = new FakeR2();
  await r2.put("pkg/x/1/package", bytes);
  try {
    return { facts: await checkPackage(r2, "pkg/x/1/package", bytes.length, kind, limits), r2 };
  } catch (e) {
    if (e instanceof ZipProblem) return { problems: e.problems, r2 };
    throw e;
  }
}

async function refused(bytes, re, kind = "battle", limits = LIMITS) {
  const r = await check(bytes, kind, limits);
  assert(r.problems, "expected a refusal, got facts: " + JSON.stringify(r.facts));
  assertMatch(r.problems.join(" | "), re);
  return r.problems;
}

Deno.test("zip: the good battle passes and its facts come from battle.json", async () => {
  const { facts, r2 } = await check(await goodBattle({ title: "Moonlit Duel", lanes: 5 }));
  assertEquals(facts.title, "Moonlit Duel");
  assertEquals(facts.artist, "Kirara");
  assertEquals(facts.author, "Mika");
  assertEquals(facts.lanes, 5);
  assertEquals(facts.battleId, "3f2b8c1e-7d6a-4b5c-9e8f-0a1b2c3d4e5f");
  assertEquals(facts.flags, { gear: false, level: false, dialogue: false, video: false });
  assertEquals(facts.contents.files, 4);
  assertEquals(facts.contents.songs, 1);
  assertEquals(facts.contents.pictures, 1);
  assertEquals(facts.contents.charts, 1);
  assertEquals(facts.contents.mediaChecked, 2);
  assert(r2.ops.get <= 12, "gets: " + r2.ops.get);
});

Deno.test("zip: flags for gear, level, dialogue and video; keys read ignoring case; the source's mapper", async () => {
  const json = JSON.stringify({
    Format: 2, KIND: "Battle", Id: "3F2B8C1E-7D6A-4B5C-9E8F-0A1B2C3D4E5F", Title: "  Boss\u200B Rush ", artist: "A", author: "B", Lanes: 4,
    gear: { MODE: "set", mainHand: "DBA1" }, level: { mode: "set", value: 12 }, dialogue: "dialogue.json",
    source: { kind: "osu!mania", mapper: "Someone", file: "x.osz" },
  });
  const { facts } = await check(await goodBattle({ json, video: true }));
  assertEquals(facts.title, "Boss Rush");
  assertEquals(facts.battleId, "3f2b8c1e-7d6a-4b5c-9e8f-0a1b2c3d4e5f");
  assertEquals(facts.flags, { gear: true, level: true, dialogue: true, video: true });
  assertEquals(facts.source, { kind: "osu!mania", mapper: "Someone" });
  const plain = await check(await goodBattle({ gear: { mode: "player" }, level: 12 }));
  assertEquals(plain.facts.flags.gear, false);
  assertEquals(plain.facts.flags.level, true);
});

Deno.test("zip: a stored battle.json and a battle.json at the root both work", async () => {
  const stored = await goodBattle({ files: { "battle.json": { data: battleJson(), method: 0 } } });
  assertEquals((await check(stored)).facts.title, "Moonlit Duel");
  const root = await goodBattle({ top: "" });
  assertEquals((await check(root)).facts.title, "Moonlit Duel");
});

Deno.test("zip: battle.json must be strict JSON, at most 256 KB, format 2, kind battle, a GUID id, 4 or 5 lanes", async () => {
  await refused(await goodBattle({ json: '{ "format": 2, // a comment\n "kind": "battle" }' }), /plain JSON/);
  await refused(await goodBattle({ json: battleJson().replace(/}\s*$/, ",}") }), /plain JSON/);
  assert((await check(await goodBattle({ json: "\uFEFF" + battleJson() }))).facts, "a byte-order mark is fine, as in the mod's reader");
  await refused(await goodBattle({ json: battleJson({ extra: { pad: "x".repeat(300 * 1024) } }) }), /over 256 KB/);
  await refused(await goodBattle({ json: battleJson({ extra: { format: 3 } }) }), /newer version/);
  await refused(await goodBattle({ json: battleJson({ extra: { kind: "chart" } }) }), /kind/);
  await refused(await goodBattle({ json: battleJson({ id: "not-a-guid" }) }), /GUID/);
  await refused(await goodBattle({ json: battleJson({ extra: { lanes: 6 } }) }), /lanes/);
  await refused(await goodBattle({ json: battleJson({ title: " \u200B " }) }), /no title/);
  await refused(await goodBattle({ json: "[1,2]" }), /JSON object/);
});

Deno.test("zip: battle.json must unpack to its size and match its CRC-32", async () => {
  await refused(await goodBattle({ files: { "battle.json": { data: battleJson(), method: 8, crc: 12345 } } }), /CRC-32/);
  await refused(await goodBattle({ files: { "battle.json": { data: battleJson(), method: 8, usize: 10 } } }), /declared size/);
  await refused(await goodBattle({ files: { "battle.json": { data: battleJson(), method: 8, usize: 5000 } } }), /declared size/);
});

Deno.test("zip: battle.json missing, or in two folders, or files outside the battle's folder", async () => {
  await refused(await goodBattle({ files: { "battle.json": null } }), /no battle\.json/);
  const two = await buildZip([
    { name: "A/battle.json", data: battleJson() }, { name: "B/battle.json", data: battleJson() }, { name: "A/charts/song.sm", data: CHART },
  ]);
  await refused(two, /more than one battle\.json/);
  const outside = await buildZip([{ name: "A/battle.json", data: battleJson() }, { name: "B/x.json", data: "{}" }]);
  await refused(outside, /inside its one folder/);
  await refused(await goodBattle({ files: {}, zip: {} }).then(async () => buildZip([{ name: "A/battle.json", data: battleJson() }, { name: "__MACOSX/A/._x", data: "x" }])), /inside its one folder/);
});

Deno.test("zip: every malicious fixture is refused", async () => {
  const fx = await allFixtures();
  const expect = {
    "bad-prefix-bytes.nbbbattle": /doesn't start like a zip/,
    "bad-comment.nbbbattle": /end record|comment/,
    "bad-zip64.nbbbattle": /ZIP64/,
    "bad-zip-slip.nbbbattle": /\.\. in its path/,
    "bad-absolute.nbbbattle": /absolute path/,
    "bad-drive.nbbbattle": /character Windows doesn't allow/,
    "bad-backslash.nbbbattle": /character Windows doesn't allow/,
    "bad-device.nbbbattle": /device name/,
    "bad-trailing-dot.nbbbattle": /ending in a dot/,
    "bad-duplicate-case.nbbbattle": /twice/,
    "bad-exe.nbbbattle": /\.exe file/,
    "bad-nested-zip.nbbbattle": /\.zip file/,
    "bad-encrypted.nbbbattle": /encrypted/,
    "bad-method-12.nbbbattle": /compression method/,
    "bad-huge-declared.nbbbattle": /sizes differ|too big/,
  };
  for (const [name, re] of Object.entries(expect)) await refused(fx[name], re);
  for (const name of ["good-battle.nbbbattle", "good-battle-video.nbbbattle", "good-battle-1000-entries.nbbbattle"]) {
    const r = await check(fx[name]);
    assert(r.facts, name + ": " + JSON.stringify(r.problems));
  }
  assert((await check(fx["good-pack.nbbchart"], "charts")).facts);
});

Deno.test("zip: bytes after the end record, a moved directory, and the end record's counts", async () => {
  await refused(await goodBattle({ zip: { trailing: "junk" } }), /end record/);
  await refused(await goodBattle({ zip: { cdOffsetDelta: -1 } }), /doesn't end where/);
  await refused(await goodBattle({ zip: { entries: 3 } }), /damaged|bytes after/);
});

Deno.test("zip: overlapping entries (the overlapping-file zip bomb) are refused", async () => {
  const kernel = "A".repeat(1000);
  const bomb = await buildZip([
    { name: "T/battle.json", data: battleJson() },
    { name: "T/charts/a.sm", data: kernel, method: 8 },
    { name: "T/charts/b.sm", data: kernel, method: 8, centralOffset: 0 },
  ]);
  await refused(bomb, /overlap|first file/);
  const zip = await goodBattle();
  // Point a second central record at the first entry's local header (the Fifield shape).
  const end = parseEnd(zip.subarray(zip.length - 22), zip.length, 1000);
  const entries = parseCentral(zip.subarray(end.cdOffset, end.cdOffset + end.cdSize), end);
  const sameStart = await buildZip([
    { name: "T/battle.json", data: battleJson() },
    { name: "T/charts/song.sm", data: CHART, method: 8 },
    { name: "T/charts/copy.sm", data: CHART, method: 8, centralOffset: 50 },
  ]);
  await refused(sameStart, /overlap/);
  assert(entries.length === 4);
});

Deno.test("zip: huge declared sizes, impossible ratios and ZIP64 extra fields are refused", async () => {
  await refused(await goodBattle({ files: { "charts/song.sm": { data: CHART, method: 8, usize: 0xffffffff } } }), /ZIP64/);
  await refused(await goodBattle({ files: { "charts/song.sm": { data: CHART, method: 8, usize: 400 * 1024 * 1024 } } }), /can't have|too big|unpacks to/);
  await refused(await goodBattle({ files: { "charts/big.sm": { data: CHART, method: 8, usize: 9 * 1024 * 1024 } } }), /too big|can't have/);
  await refused(await goodBattle({ files: { "images/x.png": { data: media.png(), extra: new Uint8Array([1, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0]) } } }), /ZIP64/);
  await refused(await goodBattle({ files: { "images/x.png": { data: media.png(), extra: new Uint8Array([9, 0, 50, 0, 1]) } } }), /damaged extra/);
  await refused(await goodBattle({ files: { "images/x.png": { data: media.png(), versionNeeded: 45 } } }), /zip features/);
});

Deno.test("zip: too many entries, a file list over 1 MB, and too much unpacked data", async () => {
  const many = {};
  for (let i = 0; i < 1000; i++) many[`art/f${i}.png`] = { data: media.png() };
  await refused(await goodBattle({ files: many }), /1004 files; the hub takes up to 1000/);
  const bigExtra = {};
  const extra = new Uint8Array(4 + 60000);
  extra.set([0x55, 0x54, 60000 & 255, 60000 >> 8]);
  for (let i = 0; i < 20; i++) bigExtra[`images/x${i}.png`] = { data: media.png(), extra };
  await refused(await goodBattle({ files: bigExtra }), /over 1 MB/);
  await refused(await goodBattle(), /unpacks to/, "battle", { ...LIMITS, maxUnpacked: 1000 });
});

Deno.test("zip: names: non-UTF-8, non-ASCII without the UTF-8 flag, control and direction characters, depth, length", async () => {
  await refused(await goodBattle({ files: { x: { nameBytes: new Uint8Array([0x54, 0x2f, 0xe9, 0x2e, 0x70, 0x6e, 0x67]), data: media.png(), flags: 0 } } }), /UTF-8/);
  await refused(await goodBattle({ files: { x: { nameBytes: new Uint8Array([0x4d, 0x6f, 0x6f, 0x6e, 0x6c, 0x69, 0x74, 0x20, 0x44, 0x75, 0x65, 0x6c, 0x2f, 0xff, 0x2e, 0x70, 0x6e, 0x67]), data: media.png(), flags: 0x800 } } }), /UTF-8/);
  await refused(await goodBattle({ files: { "images/a\u0001.png": { data: media.png() } } }), /character Windows/);
  await refused(await goodBattle({ files: { "images/gpj.\u202Eexe.png": { data: media.png() } } }), /text-direction/);
  await refused(await goodBattle({ files: { "a/b/c/d/e/f/g.png": { data: media.png() } } }), /folders deep/);
  await refused(await goodBattle({ files: { ["images/" + "n".repeat(200) + ".png"]: { data: media.png() } } }), /over 200/);
  await refused(await goodBattle({ files: { "images//x.png": { data: media.png() } } }), /empty folder/);
  await refused(await goodBattle({ files: { "images/LPT\u00B9.png": { data: media.png() } } }), /device name/);
  await refused(await goodBattle({ files: { "images/x.png ": { data: media.png() } } }), /ending in a dot or a space/);
  await refused(await goodBattle({ files: { "images/noext": { data: "x" } } }), /no file type/);
  await refused(await goodBattle({ files: { "art/intro.mp4": { data: media.mp4() } } }), /MP4 video; the hub takes WebM/);
  await refused(await goodBattle({ files: { "images/x.png": { data: media.png(), comment: "hi" } } }), /comment/);
  assertEquals(nameProblem("Top/art/boss/idle/frame.png"), null);
  assertEquals(nameProblem("Top/\u6708\u5149.png"), null);
});

Deno.test("zip: a stored entry must have equal sizes; local headers must agree with the directory", async () => {
  await refused(await goodBattle({ files: { "images/x.png": { data: media.png(), csize: 30 } } }), /sizes differ|overlap/);
  await refused(await goodBattle({ files: { "images/card.png": { data: media.png(), localName: "Moonlit Duel/images/cxrd.png" } } }), /names that disagree/);
  await refused(await goodBattle({ files: { "audio/song.ogg": { data: media.ogg("vorbis", 100), localMethod: 8 } } }), /disagree/);
});

Deno.test("zip: nested packages and executables by extension", async () => {
  for (const ext of ["zip", "nbbbattle", "nbbchart", "exe", "dll", "bat", "ps1", "js", "lnk", "url", "scr", "msi", "reg", "html", "svg", "osz"]) {
    await refused(await goodBattle({ files: { ["audio/x." + ext]: { data: "PK\x03\x04" } } }), new RegExp(`\\.${ext} file`));
  }
});

Deno.test("zip: the directory fingerprint is SHA-256 over sorted name, size and CRC-32 lines", async () => {
  const zip = await buildZip([{ name: "B.json", data: "{}" }, { name: "a.sm", data: "x" }]);
  const end = parseEnd(zip.subarray(zip.length - 22), zip.length, 10);
  const entries = parseCentral(zip.subarray(end.cdOffset, end.cdOffset + end.cdSize), end);
  // "a.sm" sorts before "B.json" (only A-Z are lowered for sorting). crc32("x") = 8cdc1683.
  const crc = crc32(new TextEncoder().encode("{}")).toString(16).padStart(8, "0");
  const expected = await sha256Hex("a.sm\u00001\u00008cdc1683\nB.json\u00002\u0000" + crc + "\n");
  assertEquals(await fingerprint(entries), expected);
});

Deno.test("packs: facts from manifest.json; songs, lanes, files, sources and counts are checked", async () => {
  const { facts } = await check(await goodPack({ songs: ["Firefly - 1", "Firefly - 2"], lanes: 5 }), "charts");
  assertEquals(facts.title, "Firefly remix difficulties");
  assertEquals(facts.songs, ["Firefly - 1", "Firefly - 2"]);
  assertEquals(facts.lanes, 5);
  assertEquals(facts.battleId, null);
  await refused(await goodPack({ json: manifestJson({ format: 2 }) }), /newer version/, "charts");
  await refused(await goodPack({ json: manifestJson({ lanes: 3 }) }), /lanes/, "charts");
  await refused(await goodPack({ json: manifestJson({ source: "file" }) }), /game's own songs/, "charts");
  await refused(await goodPack({ json: JSON.stringify({ format: 1, title: "x", lanes: 4, charts: [{ song: "Firefly - 1", file: "charts/missing.sm" }] }) }), /isn't in the pack/, "charts");
  await refused(await goodPack({ files: { "audio/song.ogg": { data: media.ogg() } } }), /\.ogg file, which the hub doesn't take in a difficulty pack/, "charts");
  const songs = Array.from({ length: 41 }, (_, i) => `Song ${i}`);
  await refused(await goodPack({ songs }), /41 songs; the hub takes up to 40/, "charts");
  await refused(await goodBattle(), /no manifest\.json/, "charts");
});
