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
    Format: 2, KIND: "Battle", Id: "3F2B8C1E-7D6A-4B5C-9E8F-0A1B2C3D4E5F", Title: "  Boss\u200B Rush ", artist: "A", author: "B", Lanes: 4, Audio: "audio/song.ogg",
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

// ---- what the loader will decode, whatever its name (the review's SEC-02) ----------------------------------

Deno.test("zip: battle.json's audio must name one of the song files, and its card a picture", async () => {
  // The mod plays the file "audio" names, whatever its name ends with.
  await refused(await goodBattle({ extra: { audio: "charts/track.sm" }, files: { "charts/track.sm": { data: CHART, method: 8 }, "audio/song.ogg": null } }),
    /"audio" must name the song file/);
  await refused(await goodBattle({ extra: { audio: "images/card.png" } }), /"audio" must name the song file/);
  await refused(await goodBattle({ extra: { audio: "audio/missing.ogg" } }), /"audio" must name the song file/);
  await refused(await goodBattle({ json: battleJson().replace('"audio": "audio/song.ogg",', "") }), /"audio" must name the song file/);
  await refused(await goodBattle({ extra: { card: "charts/song.sm" } }), /"card" must name a picture/);
  await refused(await goodBattle({ extra: { card: 5 } }), /"card" must name a picture/);
  // Case and backslashes as the mod's reader takes them; no card at all is fine.
  const ok = await check(await goodBattle({ extra: { audio: "Audio\\Song.OGG", card: "" } }));
  assert(ok.facts, JSON.stringify(ok.problems));
  const noCard = await check(await goodBattle({ json: battleJson().replace('"card": "images/card.png"', '"cardFit": "fill"') }));
  assert(noCard.facts, JSON.stringify(noCard.problems));
});

Deno.test("zip: every .sm and .json file must hold text, stored or deflated (a chart's #MUSIC or enemy art can name any file)", async () => {
  const disguised = {
    "MP4/M4A": media.mp4(), "FLAC": media.flac(), "ADPCM WAV": media.wav({ tag: 2 }), "Ogg": media.ogg("opus"), "MP3": media.mp3(),
    "ID3": media.mp3({ id3: 20 }), "ADTS": media.adts(), "WebM": media.webm(), "PNG": media.png(), "JPEG": media.jpeg(), "GIF": media.gif(),
    "WMA": new Uint8Array([0x30, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11, 0xa6, 0xd9, 0, 0xaa, 0, 0x62, 0xce, 0x6c]),
  };
  for (const [what, data] of Object.entries(disguised)) {
    for (const method of [0, 8]) {
      await refused(await goodBattle({ files: { "charts/extra.sm": { data, method } } }), /charts\/extra\.sm holds a sound, picture or video file/, "battle");
      await refused(await goodBattle({ files: { "enemy.json": { data, method } } }), /enemy\.json holds a sound, picture or video file/, "battle");
    }
    void what;
  }
  await refused(await goodBattle({ files: { "dialogue.json": { data: '{"a":\u0000}' } } }), /dialogue\.json isn't a text file/);
  // Text with a BOM, CRLF line ends, tabs and any non-ASCII letters is fine.
  const fine = await check(await goodBattle({ files: { "enemy.json": { data: "\uFEFF{\r\n\t\"name\": \"\u041D\u043E\u0447\u044C\"\r\n}", method: 8 }, "charts/two.sm": { data: CHART, method: 0 } } }));
  assert(fine.facts, JSON.stringify(fine.problems));
  // A deflate stream whose first 1 KB yields nothing (empty stored blocks) can't be judged, so it's refused.
  const empties = [];
  for (let i = 0; i < 250; i++) empties.push(0x00, 0x00, 0x00, 0xff, 0xff);
  const mp4 = media.mp4();
  const packed = new Uint8Array([...empties, 0x01, mp4.length & 255, mp4.length >> 8, ~mp4.length & 255, (~mp4.length >> 8) & 255, ...mp4]);
  await refused(await goodBattle({ files: { "charts/deep.sm": { data: mp4, method: 8, packed } } }), /compressed in a way the hub can't look into/);
});

Deno.test("packs: a chart file holding a song (a chart's #MUSIC can name it) is refused; 40 songs' charts pass in a few reads", async () => {
  await refused(await goodPack({ files: { "charts/beat.sm": { data: media.mp4(), method: 0 } } }), /charts\/beat\.sm holds a sound/, "charts");
  const songs = Array.from({ length: 40 }, (_, i) => `Song ${i}`);
  const r = await check(await goodPack({ songs }), "charts");
  assert(r.facts, JSON.stringify(r.problems));
  assert(r.r2.ops.get <= 12, "gets: " + r.r2.ops.get);
});

Deno.test("zip: text files too spread out for the read budget are refused, not skipped", async () => {
  const files = {};
  // 120 small .json files, each followed by a 200 KB stored picture: one read each is past the budget.
  for (let i = 0; i < 120; i++) {
    const n = String(i).padStart(3, "0");
    files[`art/f${n}.json`] = { data: "{}", method: 0 };
    const pic = new Uint8Array(200 * 1024);
    pic.set(media.png());
    files[`art/f${n}.png`] = { data: pic, method: 0 };
  }
  await refused(await goodBattle({ files }), /too many, or too spread out/);
});

// ---- local extra fields (the review's SEC-09) ---------------------------------------------------------------

Deno.test("zip: a local extra field is refused, so it can't move an entry's data onto the next one (extra-field quoting)", async () => {
  const base = [
    { name: "T/battle.json", data: battleJson(), method: 8 },
    { name: "T/audio/song.ogg", data: media.ogg("vorbis", 4000) },
    { name: "T/images/card.png", data: media.png() },
    { name: "T/charts/song.sm", data: CHART },
  ];
  // A's declared data is exactly B's local header, name and data; A's local extra field pushes A's real data
  // start onto B, so an overlap rule that ignores the local extra would pass it.
  const probe = await buildZip([...base.slice(0, 3), { name: "T/charts/a.sm", data: "" }, base[3]]);
  const enc = new TextEncoder();
  let bStart = -1;
  for (let i = 0; i + 30 < probe.length; i++) {
    if (probe[i] === 0x50 && probe[i + 1] === 0x4b && probe[i + 2] === 3 && probe[i + 3] === 4) {
      const n = probe[i + 26] | (probe[i + 27] << 8);
      if (new TextDecoder().decode(probe.subarray(i + 30, i + 30 + n)) === "T/charts/song.sm") bStart = i;
    }
  }
  const quoted = probe.slice(bStart, bStart + 30 + enc.encode("T/charts/song.sm").length + enc.encode(CHART).length);
  const zip = await buildZip([
    ...base.slice(0, 3),
    { name: "T/charts/a.sm", data: "", csize: quoted.length, usize: quoted.length, crc: crc32(quoted), localExtra: new Uint8Array(quoted.length) },
    base[3],
  ]);
  await refused(zip, /a\.sm has an extra field in its local header/);
  // Anywhere else the server reads a local header too.
  await refused(await goodBattle({ files: { "audio/song.ogg": { data: media.ogg("vorbis", 4000), localExtra: new Uint8Array([0x0a, 0, 4, 0, 1, 2, 3, 4]) } } }), /extra field in its local header/);
  await refused(await goodBattle({ files: { "battle.json": { data: battleJson(), method: 8, localExtra: new Uint8Array([0x0a, 0, 0, 0]) } } }), /extra field in its local header/);
});
