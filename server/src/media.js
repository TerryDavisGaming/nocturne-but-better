// Media by decoder (DESIGN-HUB 2.7): which decoder a file's bytes would reach in the mod, judged from the
// bytes the way the mod's own AudioFile.Detect, WavFile.FormatTag and VideoProbe do. Pure: each check takes
// `read(offset, length)`, an async function returning up to `length` bytes of the entry's unpacked data.
// Allowed: WAV with PCM or float samples, Ogg Vorbis, MP3; PNG, JPEG, GIF; WebM with VP8 video.

const ascii = (b, at, s) => {
  if (at < 0 || at + s.length > b.length) return false;
  for (let i = 0; i < s.length; i++) if (b[at + i] !== s.charCodeAt(i)) return false;
  return true;
};
const u16 = (b, at) => b[at] | (b[at + 1] << 8);
const u32 = (b, at) => (b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24)) >>> 0;

const CONVERT = "convert the song to .ogg (Vorbis) or .mp3";

/** Returns null when the audio is allowed, else a plain-words reason. */
export async function audioProblem(read, size) {
  const head = await read(0, 64);
  if (head.length >= 12 && ascii(head, 0, "RIFF") && head[8] === 0x57 && head[9] === 0x41) return wavProblem(read, size);
  if (ascii(head, 0, "OggS")) return oggProblem(read);
  if (ascii(head, 0, "fLaC")) return `is a FLAC file; ${CONVERT}`;
  if (["ftyp", "moov", "mdat", "wide", "free", "skip"].some((box) => ascii(head, 4, box))) return `is an MP4/M4A file; ${CONVERT}`;
  if (head.length >= 10 && head[0] === 0x30 && head[1] === 0x26 && head[2] === 0xb2 && head[3] === 0x75 && head[4] === 0x8e && head[5] === 0x66) {
    return `is a WMA file; ${CONVERT}`;
  }
  if (head.length >= 4 && head[0] === 0x1a && head[1] === 0x45 && head[2] === 0xdf && head[3] === 0xa3) return `is Matroska or WebM audio; ${CONVERT}`;
  if (ascii(head, 0, "FORM")) return `is an AIFF file; ${CONVERT}`;
  return mp3Problem(read, size);
}

async function wavProblem(read, size) {
  // Walk the chunks from byte 12 to "fmt " (WavFile.Chunk).
  let pos = 12;
  for (let i = 0; i < 16 && pos + 8 <= size; i++) {
    const h = await read(pos, 8);
    if (h.length < 8) break;
    const chunkSize = u32(h, 4);
    if (ascii(h, 0, "fmt ")) {
      const fmt = await read(pos + 8, Math.min(chunkSize, 40));
      if (fmt.length < 2) return "is a WAV file with a damaged format chunk";
      let tag = u16(fmt, 0);
      if (tag === 0xfffe && chunkSize >= 26 && fmt.length >= 26) tag = u16(fmt, 24);
      if (tag === 1 || tag === 3) return null;
      return `is a WAV file in format ${tag}, which goes to Windows' own decoders; ${CONVERT}, or save it as plain PCM WAV`;
    }
    if (ascii(h, 0, "data")) break;
    pos += 8 + chunkSize + (chunkSize & 1);
  }
  return "is a WAV file without a format chunk before its sound data";
}

async function oggProblem(read) {
  const page = await read(0, 27 + 255);
  if (page.length < 28) return "is a damaged Ogg file";
  const segments = page[26];
  const start = 27 + segments;
  const packet = await read(start, 7);
  if (packet.length === 7 && packet[0] === 1 && ascii(packet, 1, "vorbis")) return null;
  if (ascii(packet, 0, "OpusHea")) return `is Ogg Opus; ${CONVERT}`;
  if (ascii(packet, 0, "\x7fFLAC")) return `is Ogg FLAC; ${CONVERT}`;
  return `is an Ogg file that isn't Ogg Vorbis; ${CONVERT}`;
}

const BITRATES = [
  [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448],
  [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],
  [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],
  [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],
  [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
  [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
];

/** Mp3Info.IsFrameHeader: version, layer, rate and frame length, or null. */
export function frameHeader(b, p) {
  if (p < 0 || p + 4 > b.length || b[p] !== 0xff || (b[p + 1] & 0xe0) !== 0xe0) return null;
  const v = (b[p + 1] >> 3) & 3, l = (b[p + 1] >> 1) & 3, br = b[p + 2] >> 4, sr = (b[p + 2] >> 2) & 3;
  if (v === 1 || l === 0 || br === 15 || sr === 3) return null;
  const version = v === 3 ? 1 : v === 2 ? 2 : 25;
  const layer = 4 - l;
  const rate = [44100, 48000, 32000][sr] / (version === 1 ? 1 : version === 2 ? 2 : 4);
  const kbps = BITRATES[(version === 1 ? 0 : 3) + layer - 1][br];
  const pad = (b[p + 2] >> 1) & 1;
  let length = 0;
  if (kbps === 0) length = 0;
  else if (layer === 1) length = (Math.floor((12 * kbps * 1000) / rate) + pad) * 4;
  else length = Math.floor(((layer === 3 && version !== 1 ? 72 : 144) * kbps * 1000) / rate) + pad;
  return { version, layer, rate, length };
}

async function mp3Problem(read, size) {
  // Skip ID3v2 tags (Mp3Info.SkipId3), which can hold a big cover picture.
  let p = 0;
  for (let i = 0; i < 4 && p + 10 <= size; i++) {
    const h = await read(p, 10);
    if (!(h.length === 10 && ascii(h, 0, "ID3") && h[3] < 0xff && h[4] < 0xff && (h[6] | h[7] | h[8] | h[9]) < 0x80)) break;
    const tag = (h[6] << 21) | (h[7] << 14) | (h[8] << 7) | h[9];
    p += 10 + tag + (h[5] & 0x10 ? 10 : 0);
  }
  if (p >= size) return "isn't a song file the hub takes";
  const at = await read(p, 4);
  if (ascii(at, 0, "fLaC")) return `is a FLAC file; ${CONVERT}`;
  if (at.length >= 2 && at[0] === 0xff && (at[1] & 0xf6) === 0xf0) return `is AAC (ADTS); ${CONVERT}`;
  if (frameHeader(at, 0)) return null;
  // Junk before the first frame: a frame followed by another like it, near the start (Mp3Info.Parse).
  const window = await read(p, 16 * 1024);
  for (let i = 0; i + 4 <= window.length; i++) {
    if (window[i] !== 0xff) continue;
    const f = frameHeader(window, i);
    if (!f) continue;
    if (f.length > 0 && i + f.length + 4 <= window.length) {
      const g = frameHeader(window, i + f.length);
      if (!g || g.version !== f.version || g.layer !== f.layer || g.rate !== f.rate) continue;
    }
    return null;
  }
  return "isn't a song file the hub takes (it takes WAV, Ogg Vorbis and MP3)";
}

// ---- text files (DESIGN-HUB 2.7): the mod picks decoders by bytes, not names --------------------------------

/**
 * Whether the first bytes are something one of the mod's sniffers (AudioFile.Detect, MediaSniff.TypeOf,
 * VideoProbe) would take for a sound, picture or video: the name of a file doesn't decide how the mod reads
 * it. battle.json's "audio", a chart's #MUSIC and enemy art can name any file in the package.
 */
export function mediaSignature(b) {
  if (ascii(b, 0, "RIFF") || ascii(b, 0, "OggS") || ascii(b, 0, "fLaC") || ascii(b, 0, "FORM") || ascii(b, 0, "ID3")) return true;
  if (["ftyp", "moov", "mdat", "wide", "free", "skip"].some((box) => ascii(b, 4, box))) return true;
  if (b.length >= 4 && b[0] === 0x30 && b[1] === 0x26 && b[2] === 0xb2 && b[3] === 0x75) return true; // ASF (WMA)
  if (b.length >= 4 && b[0] === 0x1a && b[1] === 0x45 && b[2] === 0xdf && b[3] === 0xa3) return true; // EBML (WebM, Matroska)
  if (b.length >= 2 && b[0] === 0xff && (b[1] & 0xe0) === 0xe0) return true; // MPEG audio or ADTS frame sync
  if (b.length >= 2 && ((b[0] === 0x89 && b[1] === 0x50) || (b[0] === 0xff && b[1] === 0xd8))) return true; // PNG, JPEG
  if (ascii(b, 0, "GIF8")) return true;
  return false; // (WebP, BMP, TIFF and HEIC are refused by the mod before any decoder; HEIC has "ftyp" anyway)
}

/**
 * A .sm or .json file must hold text: no media signature at its start (above) and no NUL or other control
 * characters (tab, line feed and carriage return are fine) in its first bytes. Returns null or the reason.
 */
export function textProblem(head) {
  if (mediaSignature(head)) return "holds a sound, picture or video file, not text";
  for (const x of head) if ((x < 0x20 && x !== 0x09 && x !== 0x0a && x !== 0x0d) || x === 0x7f) return "isn't a text file";
  return null;
}

/** PNG, JPEG or GIF by the first bytes. */
export async function pictureProblem(read) {
  const b = await read(0, 8);
  if (b.length >= 8 && b[0] === 0x89 && ascii(b, 1, "PNG\r\n\x1a\n")) return null;
  if (b.length >= 3 && b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff) return null;
  if (ascii(b, 0, "GIF87a") || ascii(b, 0, "GIF89a")) return null;
  return "isn't a PNG, JPEG or GIF picture";
}

// ---- WebM (VideoProbe: Unity plays .webm with its own decoder, which only knows VP8) -------------------

function vint(b, at, keepMarker) {
  if (at >= b.length) return null;
  const first = b[at];
  if (first === 0) return null;
  let length = 1;
  while (length <= 8 && !(first & (0x80 >> (length - 1)))) length++;
  if (length > 8 || at + length > b.length) return null;
  let value = keepMarker ? first : first & (0xff >> length);
  let allOnes = (first & (0xff >> length)) === 0xff >> length;
  for (let i = 1; i < length; i++) {
    value = value * 256 + b[at + i];
    if (b[at + i] !== 0xff) allOnes = false;
  }
  return { value, length, unknown: !keepMarker && allOnes };
}

const EBML = 0x1a45dfa3, DOCTYPE = 0x4282, SEGMENT = 0x18538067, TRACKS = 0x1654ae6b, TRACK_ENTRY = 0xae,
  TRACK_TYPE = 0x83, CODEC_ID = 0x86, CLUSTER = 0x1f43b675;

async function element(read, at) {
  const h = await read(at, 12);
  const id = vint(h, 0, true);
  if (!id) return null;
  const size = vint(h, id.length, false);
  if (!size) return null;
  return { id: id.value, start: at + id.length + size.length, size: size.value, unknown: size.unknown };
}

export async function videoProblem(read, size) {
  const top = await element(read, 0);
  if (!top || top.id !== EBML) return "isn't a WebM video (the hub takes WebM with VP8 video)";
  const header = await read(top.start, Math.min(top.size, 256));
  let docType = null;
  for (let at = 0; at < header.length;) {
    const id = vint(header, at, true), len = id && vint(header, at + id.length, false);
    if (!id || !len) break;
    const body = at + id.length + len.length;
    if (id.value === DOCTYPE) docType = new TextDecoder().decode(header.subarray(body, body + len.value));
    at = body + len.value;
  }
  if (docType !== "webm") return "isn't a WebM video (a Matroska file, or damaged)";
  const segment = await element(read, top.start + top.size);
  if (!segment || segment.id !== SEGMENT) return "is a WebM file without its video";
  const segmentEnd = segment.unknown ? size : Math.min(size, segment.start + segment.size);
  let at = segment.start;
  for (let i = 0; i < 64 && at < segmentEnd; i++) {
    const el = await element(read, at);
    if (!el) break;
    if (el.id === CLUSTER) break;
    if (el.id === TRACKS) return tracksProblem(read, el);
    if (el.unknown) break;
    at = el.start + el.size;
  }
  return "is a WebM video whose track list isn't before its first frames";
}

async function tracksProblem(read, tracks) {
  const body = await read(tracks.start, Math.min(tracks.size, 64 * 1024));
  const codecs = [];
  for (let at = 0; at < body.length;) {
    const id = vint(body, at, true), len = id && vint(body, at + id.length, false);
    if (!id || !len) break;
    const start = at + id.length + len.length;
    if (id.value === TRACK_ENTRY) {
      let type = 0, codec = "";
      for (let p = start; p < Math.min(start + len.value, body.length);) {
        const cid = vint(body, p, true), clen = cid && vint(body, p + cid.length, false);
        if (!cid || !clen) break;
        const cstart = p + cid.length + clen.length;
        if (cid.value === TRACK_TYPE) type = body[cstart];
        if (cid.value === CODEC_ID) codec = new TextDecoder().decode(body.subarray(cstart, cstart + clen.value)).replace(/\0+$/, "");
        p = cstart + clen.value;
      }
      if (type === 1) codecs.push(codec);
    }
    at = start + len.value;
  }
  if (codecs.length === 0) return "is a WebM file with no video in it";
  const other = codecs.find((c) => c !== "V_VP8");
  return other ? `is a ${other === "V_VP9" ? "VP9" : other === "V_AV1" ? "AV1" : other} WebM video; the game plays only VP8 in WebM` : null;
}

// ---- the listing thumbnail (DESIGN-HUB 2.7, 3.7) --------------------------------------------------------

export const MAX_THUMB_B64 = 16384;

/**
 * A baseline JPEG (SOF0) by its markers: at most 20 segments before the scan, at most 256 px each way.
 * Reads the markers only, never decodes. Returns null when fine, else the reason.
 */
export function thumbProblem(b64) {
  if (typeof b64 !== "string" || b64.length === 0) return "no thumbnail";
  if (b64.length > MAX_THUMB_B64) return "the thumbnail is over 16 KB of base64";
  if (!/^[A-Za-z0-9+/]+={0,2}$/.test(b64) || b64.length % 4 !== 0) return "the thumbnail isn't base64";
  let b;
  try {
    const bin = atob(b64);
    b = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) b[i] = bin.charCodeAt(i);
  } catch {
    return "the thumbnail isn't base64";
  }
  if (b.length < 4 || b[0] !== 0xff || b[1] !== 0xd8) return "the thumbnail isn't a JPEG";
  if (b[b.length - 2] !== 0xff || b[b.length - 1] !== 0xd9) return "the thumbnail JPEG is cut short";
  let at = 2, segments = 0, frame = null;
  for (;;) {
    if (at + 4 > b.length || b[at] !== 0xff) return "the thumbnail JPEG is damaged";
    let marker = b[at + 1];
    while (marker === 0xff && at + 2 < b.length) marker = b[++at + 1];
    if (marker === 0xd9) return "the thumbnail JPEG has no picture data";
    if (marker >= 0xd0 && marker <= 0xd7) return "the thumbnail JPEG is damaged";
    const length = (b[at + 2] << 8) | b[at + 3];
    if (length < 2 || at + 2 + length > b.length) return "the thumbnail JPEG is damaged";
    if (++segments > 20) return "the thumbnail JPEG has too many segments";
    if (marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc) {
      if (marker !== 0xc0) return "the thumbnail must be a baseline JPEG (not progressive)";
      if (length < 8) return "the thumbnail JPEG is damaged";
      const height = (b[at + 5] << 8) | b[at + 6], width = (b[at + 7] << 8) | b[at + 8];
      if (!width || !height || width > 256 || height > 256) return "the thumbnail must be at most 256 by 256 pixels";
      frame = { width, height };
    }
    if (marker === 0xda) return frame ? null : "the thumbnail JPEG has no frame header";
    at += 2 + length;
  }
}
