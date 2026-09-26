// Test packages written byte by byte (DESIGN-HUB 6.1): good battles and difficulty packs, evil zips, and media
// samples. The tests build them in memory; run this file to write them into test/fixtures/ for the mod's own
// client tests:
//   deno run --no-remote --no-npm --allow-write=server/test/fixtures server/test/make-fixtures.js

import { crc32 } from "../src/inflate.js";

const enc = new TextEncoder();
export const bytesOf = (v) => (typeof v === "string" ? enc.encode(v) : v instanceof Uint8Array ? v : new Uint8Array(v));

export async function deflateRaw(data) {
  const stream = new Blob([data]).stream().pipeThrough(new CompressionStream("deflate-raw"));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

function concat(parts) {
  const total = parts.reduce((n, p) => n + p.length, 0);
  const out = new Uint8Array(total);
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
}

class Writer {
  constructor() {
    this.parts = [];
    this.length = 0;
  }
  u8(v) {
    this.raw(new Uint8Array([v & 0xff]));
  }
  u16(v) {
    this.raw(new Uint8Array([v & 0xff, (v >>> 8) & 0xff]));
  }
  u32(v) {
    this.raw(new Uint8Array([v & 0xff, (v >>> 8) & 0xff, (v >>> 16) & 0xff, (v >>> 24) & 0xff]));
  }
  raw(b) {
    this.parts.push(b);
    this.length += b.length;
  }
  bytes() {
    return concat(this.parts);
  }
}

/**
 * Builds a zip. files: [{ name, data, method (0 or 8), and overrides for evil zips: flags, crc, csize, usize,
 * versionNeeded, centralOffset (point this entry's central record elsewhere), extra, localExtra, nameBytes,
 * comment, localName, dataDescriptor }]. opts: { prefix (bytes before the zip), comment, entries (EOCD
 * count), cdOffsetDelta, trailing (bytes after the end record), zip64 }.
 */
export async function buildZip(files, opts = {}) {
  const w = new Writer();
  if (opts.prefix) w.raw(bytesOf(opts.prefix));
  const records = [];
  for (const f of files) {
    const data = bytesOf(f.data ?? "");
    const method = f.method ?? 0;
    const packed = f.packed ? bytesOf(f.packed) : method === 8 ? await deflateRaw(data) : data;
    const nameBytes = f.nameBytes ?? enc.encode(f.name);
    const utf8 = f.nameBytes ? false : /[^\x00-\x7f]/.test(f.name);
    const flags = f.flags ?? (utf8 ? 0x800 : 0) | (f.dataDescriptor ? 0x8 : 0);
    const crc = f.crc ?? crc32(data);
    const csize = f.csize ?? packed.length;
    const usize = f.usize ?? data.length;
    const offset = w.length;
    const localName = f.localName ? enc.encode(f.localName) : nameBytes;
    const localExtra = f.localExtra ? bytesOf(f.localExtra) : new Uint8Array(0);
    w.u32(0x04034b50);
    w.u16(f.versionNeeded ?? 20);
    w.u16(flags);
    w.u16(f.localMethod ?? method);
    w.u16(0); // time 00:00
    w.u16(0x0021); // date 1980-01-01
    w.u32(f.dataDescriptor ? 0 : crc);
    w.u32(f.dataDescriptor ? 0 : f.localCsize ?? csize);
    w.u32(f.dataDescriptor ? 0 : usize);
    w.u16(localName.length);
    w.u16(localExtra.length);
    w.raw(localName);
    w.raw(localExtra);
    w.raw(packed);
    if (f.dataDescriptor) {
      w.u32(0x08074b50);
      w.u32(crc);
      w.u32(csize);
      w.u32(usize);
    }
    records.push({ f, nameBytes, flags, method, crc, csize, usize, offset: f.centralOffset ?? offset });
  }
  const cdStart = w.length;
  for (const r of records) {
    const extra = r.f.extra ? bytesOf(r.f.extra) : new Uint8Array(0);
    const comment = r.f.comment ? enc.encode(r.f.comment) : new Uint8Array(0);
    w.u32(0x02014b50);
    w.u16(0x033f); // made by: Unix, 6.3
    w.u16(r.f.versionNeeded ?? 20);
    w.u16(r.flags);
    w.u16(r.method);
    w.u16(0);
    w.u16(0x0021);
    w.u32(r.crc);
    w.u32(r.csize);
    w.u32(r.usize);
    w.u16(r.nameBytes.length);
    w.u16(extra.length);
    w.u16(comment.length);
    w.u16(0);
    w.u16(0);
    w.u32(0);
    w.u32(r.offset);
    w.raw(r.nameBytes);
    w.raw(extra);
    w.raw(comment);
  }
  const cdSize = w.length - cdStart;
  const comment = opts.comment ? enc.encode(opts.comment) : new Uint8Array(0);
  w.u32(0x06054b50);
  w.u16(0);
  w.u16(0);
  const n = opts.entries ?? records.length;
  w.u16(opts.zip64 ? 0xffff : n);
  w.u16(opts.zip64 ? 0xffff : n);
  w.u32(cdSize);
  w.u32(cdStart + (opts.cdOffsetDelta ?? 0));
  w.u16(comment.length);
  w.raw(comment);
  if (opts.trailing) w.raw(bytesOf(opts.trailing));
  return w.bytes();
}

// ---- media samples (only as much of each format as the checks read) ------------------------------------

const u32le = (v) => [v & 0xff, (v >>> 8) & 0xff, (v >>> 16) & 0xff, (v >>> 24) & 0xff];
const u16le = (v) => [v & 0xff, (v >>> 8) & 0xff];
const ascii = (s) => [...s].map((c) => c.charCodeAt(0));
const pad = (n, v = 0) => new Array(n).fill(v);

export const media = {
  png: () => new Uint8Array([0x89, ...ascii("PNG\r\n\x1a\n"), ...u32le(0x0d000000), ...ascii("IHDR"), ...pad(17), 0xae, 0x42, 0x60, 0x82]),
  gif: () => new Uint8Array([...ascii("GIF89a"), 1, 0, 1, 0, 0, 0, 0, ...ascii(";")]),
  /** A JPEG's marker structure: SOI, APP0, DQT, SOFn, DHT, SOS, a little data, EOI. */
  jpeg: ({ width = 128, height = 128, progressive = false, extraSegments = 0, noEoi = false } = {}) => {
    const b = [0xff, 0xd8, 0xff, 0xe0, 0, 16, ...ascii("JFIF"), 0, 1, 1, 0, 0, 1, 0, 1, 0, 0];
    for (let i = 0; i < extraSegments; i++) b.push(0xff, 0xe1, 0, 4, 0, 0);
    b.push(0xff, 0xdb, 0, 67, 0, ...pad(64, 1));
    b.push(0xff, progressive ? 0xc2 : 0xc0, 0, 17, 8, height >> 8, height & 255, width >> 8, width & 255, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1);
    b.push(0xff, 0xc4, 0, 20, 0, 1, ...pad(15), 0);
    b.push(0xff, 0xda, 0, 12, 3, 1, 0, 2, 0x11, 3, 0x11, 0, 63, 0);
    b.push(...pad(200, 0x55));
    if (!noEoi) b.push(0xff, 0xd9);
    return new Uint8Array(b);
  },
  /** A WAV header: format tag (1 PCM, 3 float, 2 ADPCM, 0x55 MP3, 0xFFFE extensible with `sub`). */
  wav: ({ tag = 1, sub = 1, junk = 0, frames = 64 } = {}) => {
    const fmt = tag === 0xfffe
      ? [...u16le(0xfffe), ...u16le(2), ...u32le(44100), ...u32le(176400), ...u16le(4), ...u16le(16), ...u16le(22), ...u16le(16), ...u32le(3), ...u16le(sub), ...pad(14)]
      : [...u16le(tag), ...u16le(2), ...u32le(44100), ...u32le(176400), ...u16le(4), ...u16le(16)];
    const body = [
      ...ascii("WAVE"),
      ...(junk ? [...ascii("JUNK"), ...u32le(junk), ...pad(junk)] : []),
      ...ascii("fmt "), ...u32le(fmt.length), ...fmt,
      ...ascii("data"), ...u32le(frames * 4), ...pad(frames * 4),
    ];
    return new Uint8Array([...ascii("RIFF"), ...u32le(body.length), ...body]);
  },
  /** The first Ogg page with one packet: Vorbis, Opus or FLAC. */
  ogg: (codec = "vorbis", extra = 0) => {
    const packet = codec === "vorbis" ? [1, ...ascii("vorbis"), ...pad(23)] : codec === "opus" ? [...ascii("OpusHead"), ...pad(11)] : [0x7f, ...ascii("FLAC"), ...pad(30)];
    return new Uint8Array([...ascii("OggS"), 0, 2, ...pad(8), ...u32le(1234), ...u32le(0), ...u32le(0), 1, packet.length, ...packet, ...pad(extra, 7)]);
  },
  flac: () => new Uint8Array([...ascii("fLaC"), 0, 0, 0, 34, ...pad(34)]),
  /** MPEG-1 layer III frames at 128 kbps, 44.1 kHz (417 bytes each), after an optional ID3v2 tag. */
  mp3: ({ id3 = 0, frames = 3, junk = 0 } = {}) => {
    const out = new Uint8Array((id3 ? 10 + id3 : 0) + junk + frames * 417);
    let at = 0;
    if (id3) {
      out.set([...ascii("ID3"), 4, 0, 0, (id3 >> 21) & 0x7f, (id3 >> 14) & 0x7f, (id3 >> 7) & 0x7f, id3 & 0x7f]);
      at = 10 + id3;
    }
    out.fill(0x11, at, at + junk);
    at += junk;
    for (let i = 0; i < frames; i++, at += 417) out.set([0xff, 0xfb, 0x90, 0x64], at);
    return out;
  },
  adts: () => new Uint8Array([0xff, 0xf1, 0x50, 0x80, 0x02, 0x1f, 0xfc, ...pad(100)]),
  mp4: () => new Uint8Array([0, 0, 0, 0x18, ...ascii("ftypisom"), 0, 0, 2, 0, ...ascii("isomiso2"), ...pad(64)]),
  /** A WebM (or Matroska) header with one video track using `codec`. */
  webm: ({ codec = "V_VP8", docType = "webm", voidBytes = 0 } = {}) => {
    const el = (id, body) => {
      const n = body.length;
      return [...id, 0x10 | ((n >>> 24) & 0x0f), (n >>> 16) & 255, (n >>> 8) & 255, n & 255, ...body];
    };
    const header = el([0x1a, 0x45, 0xdf, 0xa3], [...el([0x42, 0x86], [1]), ...el([0x42, 0x82], ascii(docType))]);
    const track = el([0xae], [...el([0xd7], [1]), ...el([0x83], [1]), ...el([0x86], ascii(codec))]);
    const tracks = el([0x16, 0x54, 0xae, 0x6b], track);
    const voidEl = voidBytes ? el([0xec], pad(voidBytes)) : [];
    const info = el([0x15, 0x49, 0xa9, 0x66], el([0x2a, 0xd7, 0xb1], [0x0f, 0x42, 0x40]));
    const cluster = [0x1f, 0x43, 0xb6, 0x75, 0x01, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, ...pad(32)];
    const segment = [0x18, 0x53, 0x80, 0x67, 0x01, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, ...info, ...voidEl, ...tracks, ...cluster];
    return new Uint8Array([...header, ...segment]);
  },
};

export const thumbB64 = (opts) => btoa(String.fromCharCode(...media.jpeg(opts)));

// ---- packages -------------------------------------------------------------------------------------------

export const CHART = "#TITLE:Moonlit Duel;\n#ARTIST:Kirara;\n#OFFSET:0;\n#BPMS:0=128;\n#NOTES:\n dance-single:\n Mika:\n Hard:\n 8:\n 0,0,0,0,0:\n1000\n0100\n0010\n0001\n;\n";

export function battleJson(o = {}) {
  const j = {
    format: 2, kind: "battle", id: o.id ?? "3f2b8c1e-7d6a-4b5c-9e8f-0a1b2c3d4e5f", title: o.title ?? "Moonlit Duel", artist: o.artist ?? "Kirara",
    author: o.author ?? "Mika", lanes: o.lanes ?? 4, chart: "charts/song.sm", audio: "audio/song.ogg", card: "images/card.png",
  };
  if (o.gear) j.gear = o.gear;
  if (o.level !== undefined) j.level = o.level;
  if (o.dialogue) j.dialogue = o.dialogue;
  if (o.source) j.source = o.source;
  Object.assign(j, o.extra || {});
  return JSON.stringify(j, null, 2);
}

/**
 * A battle as the mod's export writes it: one top folder, fixed timestamps, compressed media stored,
 * json and charts deflated. `files` adds or replaces entries by name (inside the folder).
 */
export async function goodBattle(o = {}) {
  const top = o.top ?? "Moonlit Duel";
  const entries = new Map([
    ["battle.json", { data: o.json ?? battleJson(o), method: 8 }],
    ["charts/song.sm", { data: CHART, method: 8 }],
    ["audio/song.ogg", { data: o.song ?? media.ogg("vorbis", 4000), method: 0 }],
    ["images/card.png", { data: media.png(), method: 0 }],
  ]);
  if (o.video) entries.set("art/idle.webm", { data: media.webm(), method: 0 });
  for (const [name, f] of Object.entries(o.files || {})) {
    if (f === null) entries.delete(name);
    else entries.set(name, f);
  }
  const files = [...entries].sort(([a], [b]) => (a < b ? -1 : 1)).map(([name, f]) => ({ ...f, name: top ? `${top}/${name}` : name }));
  return buildZip(files, o.zip || {});
}

export function manifestJson(o = {}) {
  const songs = o.songs ?? ["Firefly - 1"];
  return JSON.stringify({
    format: o.format ?? 1, title: o.title ?? "Firefly remix difficulties", author: o.author ?? "Rin", lanes: o.lanes ?? 4,
    charts: songs.map((song, i) => ({ source: o.source ?? "game", song, melody: 1, events: "song", file: `charts/${song} M1${i ? " " + (i + 1) : ""}.sm` })),
  }, null, 2);
}

export async function goodPack(o = {}) {
  const songs = o.songs ?? ["Firefly - 1"];
  const files = songs.map((song, i) => ({ name: `charts/${song} M1${i ? " " + (i + 1) : ""}.sm`, data: CHART, method: 8 }));
  files.push({ name: "manifest.json", data: o.json ?? manifestJson(o), method: 8 });
  for (const [name, f] of Object.entries(o.files || {})) files.push({ ...f, name });
  return buildZip(files, o.zip || {});
}

/**
 * Search texts and the hub's normalized form of each (the server is the reference), for the client's own
 * normalization test: capitals outside A-Z, marks, invisible characters, astral letters, long words.
 */
export const SEARCH_TEXTS = [
  "  Moonlit   DUEL!! ", "\u00C9milie", "\u041D\u043E\u0447\u044C", "\u039F\u0394\u039F\u03A3 Remix", "\u0130stanbul Nights", "Stra\u00DFe BEAT",
  "E\u0301toile", "Love\u2665Song (Remix)", "zero\u200Bwidth", "\u{1D400}\u{1D401}\u{1D402} astral", "a b c", "one two three four five",
  "x".repeat(40) + " tail", "\uFF21\uFF22\uFF23 fullwidth",
];

/** Every fixture by name, for the client harness. */
export async function allFixtures() {
  const big = {};
  for (let i = 0; i < 996; i++) big[`art/frames/f${String(i).padStart(4, "0")}.png`] = { data: media.png(), method: 0 };
  return {
    "good-battle.nbbbattle": await goodBattle(),
    "good-battle-video.nbbbattle": await goodBattle({ video: true }),
    "good-battle-1000-entries.nbbbattle": await goodBattle({ files: big }),
    "good-pack.nbbchart": await goodPack({ songs: ["Firefly - 1", "Firefly - 2"] }),
    "bad-prefix-bytes.nbbbattle": await goodBattle({ zip: { prefix: "MZ\x90\x00this is not a zip" } }),
    "bad-comment.nbbbattle": await goodBattle({ zip: { comment: "hello" } }),
    "bad-zip64.nbbbattle": await goodBattle({ zip: { zip64: true } }),
    "bad-zip-slip.nbbbattle": await goodBattle({ files: { "../../evil.json": { data: "{}", method: 0 } } }),
    "bad-absolute.nbbbattle": await goodBattle({ top: "", files: { "/etc/evil.json": { data: "{}" } } }),
    "bad-drive.nbbbattle": await goodBattle({ files: { "C:x.json": { data: "{}" } } }),
    "bad-backslash.nbbbattle": await goodBattle({ files: { "a\\b.json": { data: "{}" } } }),
    "bad-device.nbbbattle": await goodBattle({ files: { "images/CON.png": { data: media.png() } } }),
    "bad-trailing-dot.nbbbattle": await goodBattle({ files: { "images/x.": { data: "x" } } }),
    "bad-duplicate-case.nbbbattle": await goodBattle({ files: { "images/Card.PNG": { data: media.png() } } }),
    "bad-exe.nbbbattle": await goodBattle({ files: { "audio/setup.exe": { data: "MZ" } } }),
    "bad-nested-zip.nbbbattle": await goodBattle({ files: { "art/inner.zip": { data: "PK\x03\x04" } } }),
    "bad-encrypted.nbbbattle": await goodBattle({ files: { "images/card.png": { data: media.png(), flags: 1 } } }),
    "bad-method-12.nbbbattle": await goodBattle({ files: { "charts/song.sm": { data: CHART, method: 12 } } }),
    "bad-huge-declared.nbbbattle": await goodBattle({ files: { "audio/song.ogg": { data: media.ogg(), usize: 0x7fffffff, method: 0, csize: undefined } } }),
  };
}

if (import.meta.main) {
  const dir = new URL("./fixtures/", import.meta.url);
  await Deno.mkdir(dir, { recursive: true });
  for (const [name, bytes] of Object.entries(await allFixtures())) await Deno.writeFile(new URL(name, dir), bytes);
  const { normalizeSearch } = await import("../src/names.js");
  const search = SEARCH_TEXTS.map((q) => ({ q, normalized: normalizeSearch(q) }));
  await Deno.writeTextFile(new URL("search-normalization.json", dir), JSON.stringify(search, null, 2) + "\n");
  console.log("fixtures written to server/test/fixtures/");
}
