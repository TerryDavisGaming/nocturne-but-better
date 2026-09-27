// The whole server-side package check at complete (DESIGN-HUB 2.6 steps 4-5, 2.7): the zip structure, the
// facts the listing takes from battle.json / manifest.json, and media by decoder from the files' bytes.
// Everything is read with ranged gets inside fixed budgets.

import {
  AUDIO_EXT, PICTURE_EXT, VIDEO_EXT, ZipProblem, checkEntries, checkMagic, contentsSummary, fingerprint, parseCentral, parseEnd,
} from "./zipcheck.js";
import { Budget, EntryReader, LOCAL_SLACK, OutOfBudget, localProblem, parseLocal, readRange } from "./zipread.js";
import { audioProblem, pictureProblem, textProblem, videoProblem } from "./media.js";
import { crc32, inflateAll } from "./inflate.js";
import { LIMITS, cleanLine } from "./names.js";
import { BATTLE_ID } from "./ids.js";

export const MAX_ROOT_JSON = 256 * 1024;
export const MAX_HEAVY_MEDIA = 16; // song and video files a battle may have
export const TEXT_EXT = new Set(["json", "sm"]);
const TEXT_HEAD = 64; // bytes of each .sm and .json file looked at
const TEXT_INFLATE = 1024; // compressed bytes read to get them
export const BATTLE_FORMAT = 2;
export const PACK_FORMAT = 1;

const KiB = 1024, MiB = 1024 * 1024;

/**
 * Checks the uploaded object and reads its facts. Throws a ZipProblem (plain-word problems) when refused.
 * `limits`: { maxEntries, maxUnpacked, maxSongs }.
 */
export async function checkPackage(files, key, size, kind, limits) {
  checkMagic(await readRange(files, key, 0, 4));
  const end = parseEnd(await readRange(files, key, size - 22, 22), size, limits.maxEntries);
  const entries = parseCentral(await readRange(files, key, end.cdOffset, end.cdSize), end);
  const { root, files: list, unpacked } = checkEntries(entries, kind, { cdOffset: end.cdOffset, maxUnpacked: limits.maxUnpacked });
  for (const e of list) e.cdOffset = end.cdOffset;

  const json = await readRootJson(files, key, root, end.cdOffset);
  const facts = kind === "battle" ? battleFacts(json, list) : packFacts(json, list, limits.maxSongs);
  await checkText(files, key, list, root, end.cdOffset);
  const media = await checkMedia(files, key, list, end.cdOffset);

  facts.fingerprint = await fingerprint(entries);
  facts.contents = { ...contentsSummary(list, unpacked), mediaChecked: media.checked, mediaTotal: media.total };
  facts.entries = entries.length;
  return facts;
}

async function readRootJson(files, key, entry, cdOffset) {
  const name = entry.relative;
  if (entry.usize > MAX_ROOT_JSON || entry.csize > MAX_ROOT_JSON) throw new ZipProblem(`${name} is over 256 KB.`);
  const buf = await readRange(files, key, entry.offset, Math.min(30 + entry.nameLength + LOCAL_SLACK + entry.csize, cdOffset - entry.offset));
  const local = parseLocal(buf, 0);
  const why = localProblem(local, entry, buf.subarray(30, 30 + entry.nameLength));
  if (why) throw new ZipProblem(`${name} ${why}.`);
  const dataStart = 30 + local.nameLength + local.extraLength;
  if (entry.offset + dataStart + entry.csize > cdOffset) throw new ZipProblem(`${name} runs into the zip's file list.`);
  let data = buf.subarray(dataStart, dataStart + entry.csize);
  if (data.length < entry.csize) data = await readRange(files, key, entry.offset + dataStart, entry.csize);
  let bytes;
  if (entry.method === 0) bytes = data;
  else {
    try {
      bytes = await inflateAll(data, entry.usize);
    } catch {
      throw new ZipProblem(`${name} doesn't unpack to its declared size.`);
    }
  }
  if (bytes.length !== entry.usize) throw new ZipProblem(`${name} doesn't unpack to its declared size.`);
  if (crc32(bytes) !== entry.crc) throw new ZipProblem(`${name} doesn't match its checksum (CRC-32).`);
  let text, parsed;
  try {
    text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
    parsed = JSON.parse(text);
  } catch {
    throw new ZipProblem(`${name} must be plain JSON as the mod's upload writes it (no comments, no trailing commas).`);
  }
  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) throw new ZipProblem(`${name} must hold a JSON object.`);
  // The mod's reader ignores the keys' case and takes the last of two; field() takes the exact one first. A key
  // twice would let the check and the game read different files (a song that was never checked), so it's refused.
  const twice = duplicateKey(text);
  if (twice !== null) throw new ZipProblem(`${name} has the key "${cleanLine(twice, 40) || ""}" twice (keys count as the same whatever their capitals).`);
  return parsed;
}

/**
 * The first key that an object in this JSON text (at any depth) has twice, ignoring case, or null. The text
 * already parsed, so it's well formed. JSON.parse keeps only the last of two equal keys, so the text is walked.
 * Keys count as the same when they match lower-cased, or upper-cased letter by letter: at least every pair the
 * mod's reader (.NET's OrdinalIgnoreCase) takes as one key.
 */
export function duplicateKey(text) {
  const open = []; // per open object its seen keys, per open array null
  let wantKey = false;
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    if (c === 0x22) {
      let j = i + 1;
      while (text.charCodeAt(j) !== 0x22) j += text.charCodeAt(j) === 0x5c ? 2 : 1;
      const keys = open.length > 0 ? open[open.length - 1] : null;
      if (wantKey && keys) {
        const key = JSON.parse(text.slice(i, j + 1));
        const lower = "l" + key.toLowerCase();
        let upper = "u";
        for (const ch of key.split("")) {
          const u = ch.toUpperCase();
          upper += u.length === 1 ? u : ch;
        }
        if (keys.has(lower) || keys.has(upper)) return key;
        keys.add(lower);
        keys.add(upper);
        wantKey = false;
      }
      i = j;
    } else if (c === 0x7b) {
      open.push(new Set());
      wantKey = true;
    } else if (c === 0x5b) {
      open.push(null);
      wantKey = false;
    } else if (c === 0x7d || c === 0x5d) {
      open.pop();
      wantKey = false;
    } else if (c === 0x2c) {
      wantKey = open.length > 0 && open[open.length - 1] !== null;
    }
  }
  return null;
}

// A-Z lower-cased, nothing else: the mod's reader matches its key names that way (.NET's OrdinalIgnoreCase maps no
// other letter to a-z, so "kind" with a Kelvin sign for its k, or "audio" with a dotless i, isn't that key to it).
const asciiLower = (s) => s.replace(/[A-Z]+/g, (m) => m.toLowerCase());

/** Looks a key up ignoring the case of a-z, like the mod's reader (an exact match first). */
export function field(obj, name) {
  if (obj === null || typeof obj !== "object" || Array.isArray(obj)) return undefined;
  if (Object.hasOwn(obj, name)) return obj[name];
  const lower = asciiLower(name);
  for (const k of Object.keys(obj)) if (asciiLower(k) === lower) return obj[k];
  return undefined;
}

const isObject = (v) => v !== null && typeof v === "object" && !Array.isArray(v);
const modeIs = (obj, mode) => typeof field(obj, "mode") === "string" && field(obj, "mode").trim().toLowerCase() === mode;

export function battleFacts(j, list) {
  const problems = [];
  const format = field(j, "format");
  if (format !== BATTLE_FORMAT) {
    problems.push(typeof format === "number" && format > BATTLE_FORMAT ? "battle.json was made for a newer version of the mod." : "battle.json isn't format 2.");
  }
  const kind = field(j, "kind");
  if (typeof kind !== "string" || kind.trim().toLowerCase() !== "battle") problems.push('battle.json\'s "kind" isn\'t "battle".');
  const rawId = field(j, "id");
  const battleId = typeof rawId === "string" ? rawId.trim().toLowerCase() : "";
  if (!BATTLE_ID.test(battleId)) problems.push("battle.json's id isn't a GUID.");
  const title = cleanLine(field(j, "title"), LIMITS.title);
  if (!title) problems.push("The battle has no title.");
  const artist = cleanLine(field(j, "artist") ?? "", LIMITS.artist);
  const author = cleanLine(field(j, "author") ?? "", LIMITS.author);
  if (artist === null || author === null) problems.push("battle.json's artist and author must be text.");
  const lanes = field(j, "lanes");
  if (lanes !== 4 && lanes !== 5) problems.push("battle.json must say its lanes (4 or 5).");
  // The mod plays the file "audio" names, whatever its name ends with (and falls back to the chart's #MUSIC
  // without it), so it must be one of the song files whose bytes are checked below. Likewise the card.
  const entries = new Map(list.map((e) => [e.relative.toLowerCase(), e]));
  const entryFor = (v) => (typeof v === "string" ? entries.get(v.replace(/\\/g, "/").trim().toLowerCase()) : undefined);
  const song = entryFor(field(j, "audio"));
  if (!song || !AUDIO_EXT.has(song.ext)) problems.push('battle.json\'s "audio" must name the song file in the package (an .ogg, .wav or .mp3).');
  const card = field(j, "card");
  if (card !== undefined && card !== null && card !== "") {
    const picture = entryFor(card);
    if (!picture || !PICTURE_EXT.has(picture.ext)) problems.push('battle.json\'s "card" must name a picture in the package (a .png, .jpg or .gif).');
  }
  if (problems.length) throw new ZipProblem(problems);

  const rawSource = field(j, "source");
  let source = null;
  if (isObject(rawSource)) {
    const sk = cleanLine(field(rawSource, "kind"), 32);
    if (sk) source = { kind: sk, mapper: cleanLine(field(rawSource, "mapper") ?? "", LIMITS.author) || "" };
  }
  const gear = field(j, "gear");
  const level = field(j, "level");
  const dialogue = field(j, "dialogue");
  const flags = {
    gear: isObject(gear) && modeIs(gear, "set"),
    level: typeof level === "number" || (isObject(level) && modeIs(level, "set") && field(level, "value") != null),
    dialogue: dialogue !== undefined && dialogue !== null,
    video: list.some((e) => e.relative.toLowerCase().startsWith("art/") && e.ext === "webm"),
  };
  return { kind: "battle", format, battleId, title, artist, author, lanes, source, flags, songs: null };
}

export function packFacts(j, list, maxSongs) {
  const problems = [];
  const format = field(j, "format");
  if (typeof format !== "number" || !Number.isInteger(format) || format < 1 || format > PACK_FORMAT) {
    problems.push(typeof format === "number" && format > PACK_FORMAT ? "manifest.json was made for a newer version of the mod." : "manifest.json isn't format 1.");
  }
  const title = cleanLine(field(j, "title"), LIMITS.packTitle);
  if (!title) problems.push("The difficulty pack has no title.");
  const author = cleanLine(field(j, "author") ?? "", LIMITS.author);
  if (author === null) problems.push("manifest.json's author must be text.");
  const lanes = field(j, "lanes");
  if (lanes !== 4 && lanes !== 5) problems.push("manifest.json must say its lanes (4 or 5).");
  const charts = field(j, "charts");
  const songs = [];
  if (!Array.isArray(charts) || charts.length === 0) problems.push("manifest.json lists no charts.");
  else {
    const names = new Set(list.map((e) => e.relative.toLowerCase()));
    for (const c of charts) {
      const source = field(c, "source") ?? "game";
      const song = cleanLine(field(c, "song"), LIMITS.song);
      const file = field(c, "file");
      if (typeof source !== "string" || source.trim().toLowerCase() !== "game") {
        problems.push("A difficulty pack on the hub can only have charts for the game's own songs.");
        break;
      }
      if (!song) {
        problems.push("A chart in manifest.json has no song.");
        break;
      }
      if (typeof file !== "string" || !file.toLowerCase().endsWith(".sm") || !names.has(file.replace(/\\/g, "/").toLowerCase())) {
        problems.push(`The chart for ${song} names a file that isn't in the pack.`);
        break;
      }
      if (!songs.includes(song)) songs.push(song);
    }
    if (songs.length > maxSongs) problems.push(`The pack covers ${songs.length} songs; the hub takes up to ${maxSongs}.`);
  }
  if (problems.length) throw new ZipProblem(problems);
  return {
    kind: "charts", format, battleId: null, title, artist: "", author, lanes, source: null,
    flags: { gear: false, level: false, dialogue: false, video: false }, songs,
  };
}

/**
 * Every .sm and .json file (besides battle.json / manifest.json, read above) must hold text. A chart's #MUSIC,
 * battle.json's references and enemy art can name any file, and the mod reads a file by its bytes, so a song
 * or video stored under a text name would otherwise reach a decoder without the media check below. Local
 * headers close together are read in one window; a package whose text files can't all be read within the
 * budget is refused (the mod's builder writes them together).
 */
async function checkText(files, key, list, root, cdOffset) {
  const texts = list.filter((e) => TEXT_EXT.has(e.ext) && e !== root).sort((a, b) => a.offset - b.offset);
  if (!texts.length) return;
  const groups = [];
  for (const e of texts) {
    const start = e.offset, end = Math.min(e.offset + 30 + e.nameLength + Math.min(e.csize, e.method === 0 ? TEXT_HEAD : TEXT_INFLATE), cdOffset);
    const last = groups[groups.length - 1];
    if (last && start - last.end <= 128 * KiB && end - last.start <= 1 * MiB) {
      last.end = Math.max(last.end, end);
      last.items.push(e);
    } else groups.push({ start, end, items: [e] });
  }
  const budget = new Budget(96, 16 * MiB);
  const problems = [];
  try {
    for (const g of groups) {
      const seed = await readRange(files, key, g.start, g.end - g.start, budget);
      for (const e of g.items) {
        const reader = await new EntryReader(files, key, e, budget, { seed, seedOffset: g.start, window: TEXT_HEAD, inflateCap: TEXT_INFLATE }).open();
        const head = await reader.read(0, TEXT_HEAD);
        // A deflate stream that yields less than it must from its first bytes can't be judged: refused.
        const why = head.length < Math.min(TEXT_HEAD, e.usize) ? "is compressed in a way the hub can't look into" : textProblem(head);
        if (why && problems.length < 20) problems.push(`${e.relative} ${why}.`);
      }
    }
  } catch (err) {
    if (err instanceof OutOfBudget) throw new ZipProblem("The package's chart and .json files are too many, or too spread out, for the hub to check them all.");
    throw err;
  }
  if (problems.length) throw new ZipProblem(problems);
}

/**
 * Media by decoder from the bytes: every song and video file, and pictures as far as a small budget of
 * reads goes (the mod checks every file again before it installs a download).
 */
async function checkMedia(files, key, list, cdOffset) {
  const heavy = list.filter((e) => AUDIO_EXT.has(e.ext) || VIDEO_EXT.has(e.ext));
  const pictures = list.filter((e) => PICTURE_EXT.has(e.ext)).sort((a, b) => a.offset - b.offset);
  if (heavy.length > MAX_HEAVY_MEDIA) throw new ZipProblem(`The battle has ${heavy.length} song and video files; the hub takes up to ${MAX_HEAVY_MEDIA}.`);
  const problems = [];
  const heavyBudget = new Budget(3 * heavy.length + 4, 16 * MiB);
  try {
    for (const e of heavy) {
      const first = Math.min(30 + e.nameLength + LOCAL_SLACK + Math.min(e.csize, 16 * KiB), cdOffset - e.offset);
      const seed = await readRange(files, key, e.offset, first, heavyBudget);
      const reader = await new EntryReader(files, key, e, heavyBudget, { seed, seedOffset: e.offset }).open();
      const read = (o, l) => reader.read(o, l);
      const why = AUDIO_EXT.has(e.ext) ? await audioProblem(read, e.usize) : await videoProblem(read, e.usize);
      if (why) problems.push(`${e.relative} ${why}.`);
    }
  } catch (err) {
    if (err instanceof OutOfBudget) throw new ZipProblem("The song and video files couldn't all be checked; store them without compression.");
    throw err;
  }

  // Pictures: windows around nearby local headers are read together.
  const groups = [];
  for (const e of pictures) {
    const start = e.offset, end = Math.min(e.offset + 30 + e.nameLength + LOCAL_SLACK + 16, cdOffset);
    const last = groups[groups.length - 1];
    if (last && start - last.end <= 32 * KiB && end - last.start <= 256 * KiB) {
      last.end = Math.max(last.end, end);
      last.items.push(e);
    } else groups.push({ start, end, items: [e] });
  }
  const pictureBudget = new Budget(24, 4 * MiB);
  let checked = heavy.length;
  for (const g of groups) {
    if (!pictureBudget.canTake(g.end - g.start)) break;
    const seed = await readRange(files, key, g.start, g.end - g.start, pictureBudget);
    let done = true;
    for (const e of g.items) {
      try {
        const reader = await new EntryReader(files, key, e, pictureBudget, { seed, seedOffset: g.start, window: 64 }).open();
        const why = await pictureProblem((o, l) => reader.read(o, l));
        if (why) problems.push(`${e.relative} ${why}.`);
        checked++;
      } catch (err) {
        if (!(err instanceof OutOfBudget)) throw err;
        done = false;
        break;
      }
    }
    if (!done) break;
  }
  if (problems.length) throw new ZipProblem(problems.slice(0, 20));
  return { checked, total: heavy.length + pictures.length };
}
