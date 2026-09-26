// Media by decoder: which of the mod's decoders a file's bytes would reach (DESIGN-HUB 2.7).

import { assert, assertEquals, assertMatch } from "./assert.js";
import { audioProblem, pictureProblem, thumbProblem, videoProblem } from "../src/media.js";
import { inflatePrefix } from "../src/inflate.js";
import { FakeR2 } from "./fakes.js";
import { checkPackage } from "../src/facts.js";
import { ZipProblem } from "../src/zipcheck.js";
import { deflateRaw, goodBattle, media, thumbB64 } from "./make-fixtures.js";

const reader = (bytes) => async (offset, length) => bytes.subarray(offset, offset + length);
const audio = (b) => audioProblem(reader(b), b.length);
const video = (b) => videoProblem(reader(b), b.length);

Deno.test("audio: PCM, float and extensible-PCM WAV, Ogg Vorbis and MP3 are allowed", async () => {
  assertEquals(await audio(media.wav({ tag: 1 })), null);
  assertEquals(await audio(media.wav({ tag: 3 })), null);
  assertEquals(await audio(media.wav({ tag: 0xfffe, sub: 1 })), null);
  assertEquals(await audio(media.wav({ tag: 0xfffe, sub: 3 })), null);
  assertEquals(await audio(media.wav({ tag: 1, junk: 5000 })), null);
  assertEquals(await audio(media.ogg("vorbis")), null);
  assertEquals(await audio(media.mp3()), null);
  assertEquals(await audio(media.mp3({ id3: 300000 })), null);
  assertEquals(await audio(media.mp3({ junk: 100 })), null);
});

Deno.test("audio: files that would reach Windows' decoders are refused", async () => {
  assertMatch(await audio(media.wav({ tag: 2 })), /format 2/);
  assertMatch(await audio(media.wav({ tag: 0x55 })), /format 85/);
  assertMatch(await audio(media.wav({ tag: 0xfffe, sub: 2 })), /format 2/);
  assertMatch(await audio(media.ogg("opus")), /Opus/);
  assertMatch(await audio(media.ogg("flac")), /FLAC/);
  assertMatch(await audio(media.flac()), /FLAC/);
  assertMatch(await audio(media.adts()), /AAC/);
  assertMatch(await audio(media.mp4()), /MP4/);
  assertMatch(await audio(media.webm()), /Matroska or WebM/);
  assertMatch(await audio(new TextEncoder().encode("just some text that isn't audio at all")), /isn't a song file/);
  assertMatch(await audio(new Uint8Array([...new TextEncoder().encode("ID3"), 4, 0, 0, 0, 0, 0, 10, ...new Array(10).fill(0), ...new TextEncoder().encode("fLaC")])), /FLAC/);
});

Deno.test("pictures: PNG, JPEG and GIF by their bytes; anything else is refused", async () => {
  assertEquals(await pictureProblem(reader(media.png())), null);
  assertEquals(await pictureProblem(reader(media.jpeg())), null);
  assertEquals(await pictureProblem(reader(media.gif())), null);
  assertMatch(await pictureProblem(reader(media.mp4())), /isn't a PNG, JPEG or GIF/);
  assertMatch(await pictureProblem(reader(new TextEncoder().encode("<svg onload=alert(1)>"))), /isn't a PNG/);
});

Deno.test("video: only VP8 in WebM", async () => {
  assertEquals(await video(media.webm()), null);
  assertEquals(await video(media.webm({ voidBytes: 200000 })), null);
  assertMatch(await video(media.webm({ codec: "V_VP9" })), /VP9 WebM video/);
  assertMatch(await video(media.webm({ codec: "V_AV1" })), /AV1/);
  assertMatch(await video(media.webm({ docType: "matroska" })), /Matroska/);
  assertMatch(await video(media.mp4()), /isn't a WebM video/);
});

Deno.test("thumbnails: a baseline JPEG, at most 256 px, 20 segments and 16 KB of base64", () => {
  assertEquals(thumbProblem(thumbB64()), null);
  assertMatch(thumbProblem(thumbB64({ progressive: true })), /baseline/);
  assertMatch(thumbProblem(thumbB64({ width: 300 })), /256/);
  assertMatch(thumbProblem(thumbB64({ extraSegments: 20 })), /too many segments/);
  assertMatch(thumbProblem(thumbB64({ noEoi: true })), /cut short/);
  assertMatch(thumbProblem(btoa(String.fromCharCode(...media.png()))), /isn't a JPEG/);
  assertMatch(thumbProblem("not base64!"), /base64/);
  const big = new Uint8Array(13 * 1024);
  big.set(media.jpeg());
  assertMatch(thumbProblem(btoa(String.fromCharCode(...big))), /over 16 KB/);
});

Deno.test("inflate: the start of a deflated file, even from a cut-off input", async () => {
  let seed = 7;
  const noise = Array.from({ length: 50000 }, () => String.fromCharCode(97 + ((seed = (seed * 1103515245 + 12345) >>> 0) % 26))).join("");
  const text = new TextEncoder().encode("RIFF....WAVEfmt " + noise);
  const packed = await deflateRaw(text);
  assertEquals(new TextDecoder().decode(inflatePrefix(packed, 16)), "RIFF....WAVEfmt ");
  assertEquals(inflatePrefix(packed, 1000000).length, text.length);
  const cut = inflatePrefix(packed.subarray(0, packed.length >> 1), 1000000);
  assert(cut.length > 16 && cut.length < text.length);
  assertEquals(new TextDecoder().decode(cut.subarray(0, 16)), "RIFF....WAVEfmt ");
});

async function packageVerdict(files) {
  const bytes = await goodBattle({ files });
  const r2 = new FakeR2();
  await r2.put("k", bytes);
  try {
    await checkPackage(r2, "k", bytes.length, "battle", { maxEntries: 1000, maxUnpacked: 200 * 1024 * 1024, maxSongs: 40 });
    return null;
  } catch (e) {
    if (e instanceof ZipProblem) return e.problems.join(" | ");
    throw e;
  }
}

Deno.test("packages: renamed and disguised media are refused, stored or deflated", async () => {
  assertMatch(await packageVerdict({ "audio/song.ogg": { data: media.flac() } }), /audio\/song\.ogg is a FLAC file/);
  assertMatch(await packageVerdict({ "audio/song.mp3": { data: media.adts() } }), /AAC/);
  assertMatch(await packageVerdict({ "art/idle.webm": { data: media.mp4() } }), /isn't a WebM/);
  assertMatch(await packageVerdict({ "audio/voice.wav": { data: media.wav({ tag: 2 }), method: 8 } }), /format 2/);
  assertMatch(await packageVerdict({ "audio/voice.wav": { data: media.wav({ tag: 0x55 }) } }), /format 85/);
  assertMatch(await packageVerdict({ "audio/song.ogg": { data: media.ogg("opus", 50) } }), /Opus/);
  assertMatch(await packageVerdict({ "images/card.png": { data: new TextEncoder().encode("MZ this is an exe") } }), /isn't a PNG/);
  assertEquals(await packageVerdict({ "audio/voice.wav": { data: media.wav({ tag: 1, frames: 20000 }), method: 8 } }), null);
  assertEquals(await packageVerdict({ "audio/extra.wav": { data: media.wav({ tag: 0xfffe, sub: 1 }) } }), null);
  assertEquals(await packageVerdict({ "audio/song.mp3": { data: media.mp3({ id3: 200000 }) } }), null);
  assertEquals(await packageVerdict({ "art/idle.webm": { data: media.webm({ voidBytes: 100000 }) } }), null);
});

Deno.test("packages: at most 16 song and video files, and a small read budget for pictures", async () => {
  const many = {};
  for (let i = 0; i < 17; i++) many[`audio/s${i}.ogg`] = { data: media.ogg() };
  assertMatch(await packageVerdict(many), /18 song and video files/);
  // 900 pictures 40 KB apart: the budget covers some of them; the rest are left to the mod's own check.
  const pics = {};
  const filler = new Uint8Array(40 * 1024);
  filler.set(media.png());
  for (let i = 0; i < 900; i++) pics[`art/f${String(i).padStart(3, "0")}.png`] = { data: filler };
  const bytes = await goodBattle({ files: pics });
  const r2 = new FakeR2();
  await r2.put("k", bytes);
  const facts = await checkPackage(r2, "k", bytes.length, "battle", { maxEntries: 1000, maxUnpacked: 200 * 1024 * 1024, maxSongs: 40 });
  assert(facts.contents.mediaChecked < facts.contents.mediaTotal, JSON.stringify(facts.contents));
  assert(r2.ops.get <= 3 + 2 + 4 + 24 + 4, "gets: " + r2.ops.get);
});
