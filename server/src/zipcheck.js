// The server's zip check (DESIGN-HUB 2.7, "server at complete"): the end record, the central directory and
// the rules on every entry, from at most three reads of the object. Pure: bytes in, verdicts out.
// Problems are plain words the mod shows to the uploader.

import { sha256Hex } from "./ids.js";
import { INVISIBLE } from "./names.js";

export const MiB = 1024 * 1024;
export const MAX_CD_BYTES = 1 * MiB;
export const MAX_PATH = 200;
export const MAX_FOLDERS = 6; // at most 6 folders above a file (7 parts)

export const SIG_LOCAL = 0x04034b50;
export const SIG_CENTRAL = 0x02014b50;
export const SIG_END = 0x06054b50;

export const EXTENSIONS = {
  battle: new Set(["json", "sm", "ogg", "wav", "mp3", "png", "jpg", "jpeg", "gif", "webm"]),
  charts: new Set(["json", "sm"]),
};
export const AUDIO_EXT = new Set(["ogg", "wav", "mp3"]);
export const PICTURE_EXT = new Set(["png", "jpg", "jpeg", "gif"]);
export const VIDEO_EXT = new Set(["webm"]);

/** A refusal of the whole package; `problems` are plain words. */
export class ZipProblem extends Error {
  constructor(problems) {
    super("zip refused");
    this.problems = Array.isArray(problems) ? problems : [problems];
  }
}

const u16 = (b, at) => b[at] | (b[at + 1] << 8);
const u32 = (b, at) => (b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24)) >>> 0;

export function checkMagic(first4) {
  if (first4.length < 4 || u32(first4, 0) !== SIG_LOCAL) {
    throw new ZipProblem("The file doesn't start like a zip (something is stuck in front of it, or it isn't a zip).");
  }
}

/** The end-of-central-directory record, which must be the last 22 bytes (no comment, no ZIP64). */
export function parseEnd(tail, size, maxEntries) {
  if (size < 22 + 30 || tail.length !== 22 || u32(tail, 0) !== SIG_END) {
    throw new ZipProblem("The zip's end record isn't where it must be (a zip comment, or bytes stuck after the zip).");
  }
  const disk = u16(tail, 4), cdDisk = u16(tail, 6), onDisk = u16(tail, 8), total = u16(tail, 10);
  const cdSize = u32(tail, 12), cdOffset = u32(tail, 16), commentLength = u16(tail, 20);
  if (commentLength !== 0) throw new ZipProblem("The zip has a comment; hub packages have none.");
  if (total === 0xffff || cdSize === 0xffffffff || cdOffset === 0xffffffff) throw new ZipProblem("The zip uses ZIP64, which the hub doesn't take.");
  if (disk !== 0 || cdDisk !== 0 || onDisk !== total) throw new ZipProblem("The zip is split over several disks.");
  if (total === 0) throw new ZipProblem("The zip is empty.");
  if (total > maxEntries) throw new ZipProblem(`The zip has ${total} files; the hub takes up to ${maxEntries}.`);
  if (cdSize > MAX_CD_BYTES) throw new ZipProblem("The zip's file list is over 1 MB.");
  if (cdOffset + cdSize !== size - 22) throw new ZipProblem("The zip's file list doesn't end where its end record starts.");
  if (cdOffset < 30) throw new ZipProblem("The zip's file list starts before any file.");
  return { entries: total, cdSize, cdOffset };
}

const utf8 = new TextDecoder("utf-8", { fatal: true });

/** Parses the central directory into entries. Structural problems throw; name rules come later. */
export function parseCentral(cd, end) {
  const entries = [];
  let at = 0;
  for (let i = 0; i < end.entries; i++) {
    if (at + 46 > cd.length || u32(cd, at) !== SIG_CENTRAL) throw new ZipProblem("The zip's file list is damaged.");
    const versionNeeded = u16(cd, at + 6);
    const flags = u16(cd, at + 8);
    const method = u16(cd, at + 10);
    const crc = u32(cd, at + 16);
    const csize = u32(cd, at + 20);
    const usize = u32(cd, at + 24);
    const nameLength = u16(cd, at + 28);
    const extraLength = u16(cd, at + 30);
    const commentLength = u16(cd, at + 32);
    const diskStart = u16(cd, at + 34);
    const offset = u32(cd, at + 42);
    const nameStart = at + 46;
    const next = nameStart + nameLength + extraLength + commentLength;
    if (next > cd.length) throw new ZipProblem("The zip's file list is damaged.");
    // Views into the directory, not copies: this runs for up to 1000 entries inside a complete's CPU budget.
    const nameBytes = cd.subarray(nameStart, nameStart + nameLength);
    const extra = cd.subarray(nameStart + nameLength, nameStart + nameLength + extraLength);
    let ascii = true;
    for (let k = 0; k < nameLength; k++) {
      if (nameBytes[k] > 0x7f) {
        ascii = false;
        break;
      }
    }
    let name = null;
    if (ascii) name = String.fromCharCode.apply(null, nameBytes);
    else if (flags & 0x800) {
      try {
        name = utf8.decode(nameBytes);
      } catch {
        name = null;
      }
    }
    entries.push({
      index: i, versionNeeded, flags, method, crc, csize, usize, nameLength, extraLength, commentLength, diskStart, offset, nameBytes, extra, name,
    });
    at = next;
  }
  if (at !== cd.length) throw new ZipProblem("The zip's file list has bytes after its last entry.");
  return entries;
}

function extraHasZip64(extra) {
  let at = 0;
  while (at < extra.length) {
    if (at + 4 > extra.length) return { bad: true };
    const id = u16(extra, at), len = u16(extra, at + 2);
    if (at + 4 + len > extra.length) return { bad: true };
    if (id === 0x0001) return { zip64: true };
    at += 4 + len;
  }
  return {};
}

const DEVICE = /^(con|prn|aux|nul|com[0-9\u00B9\u00B2\u00B3]|lpt[0-9\u00B9\u00B2\u00B3])(\..*)?$/i;
const BAD_CHARS = /[\u0000-\u001f\u007f-\u009f\\:<>"|?*]/;
const HIDDEN_CHARS = new RegExp(INVISIBLE.source, "u"); // the same set, without the global flag

/** Why a zip entry name is refused, or null. Names use "/" and may end with "/" for a folder. */
export function nameProblem(name) {
  if (name === null) return "has a name that isn't UTF-8 (or plain ASCII)";
  const path = name.endsWith("/") ? name.slice(0, -1) : name;
  if (path.length === 0) return "has an empty name";
  if (name.length > MAX_PATH && [...name].length > MAX_PATH) return `has a path over ${MAX_PATH} characters`;
  if (path.charCodeAt(0) === 0x2f) return "starts with / (an absolute path)";
  if (BAD_CHARS.test(path)) return "has a character Windows doesn't allow in names (\\ : < > \" | ? * or a control character)";
  if (HIDDEN_CHARS.test(path)) return "has invisible or text-direction characters in its name";
  const parts = path.split("/");
  if (parts.length > MAX_FOLDERS + 1) return `is more than ${MAX_FOLDERS} folders deep`;
  for (const part of parts) {
    if (part.length === 0) return "has an empty folder name (//)";
    if (part === "." || part === "..") return "has a . or .. in its path";
    const last = part.charCodeAt(part.length - 1);
    if (last === 0x20 || last === 0x2e) return "has a name part ending in a dot or a space";
    // Device names start with c, p, a, n or l; only those parts need the full check.
    if (part.length >= 3 && "cpanlCPANL".includes(part[0]) && DEVICE.test(part)) return "uses a Windows device name (like CON or COM1)";
  }
  return null;
}

/** The lower-case extension of a file name, or "". */
export function extensionOf(name) {
  const last = name.slice(name.lastIndexOf("/") + 1);
  const dot = last.lastIndexOf(".");
  return dot <= 0 ? "" : last.slice(dot + 1).toLowerCase();
}

/** The mod's per-file unpacked limit (BattleFiles.LimitFor), by the name inside the battle's folder. */
export function limitFor(relative, ext = extensionOf(relative)) {
  if (ext === "json") return 1 * MiB;
  if (ext === "sm") return 8 * MiB;
  if (PICTURE_EXT.has(ext)) {
    const lower = relative.slice(0, 11).toLowerCase();
    return lower.startsWith("art/") ? 32 * MiB : lower.startsWith("portraits/") ? 4 * MiB : 16 * MiB;
  }
  return 512 * MiB;
}

/** Deflate can't expand more than about 1032 to 1, so a bigger declared size is a lie. */
export const impossibleSize = (e) => e.method === 8 && e.usize > e.csize * 1032 + 1024;

/**
 * Every rule on the entry list. Returns { prefix, root, files } where root is the battle.json or
 * manifest.json entry and files the non-folder entries with their names inside the prefix, or throws a
 * ZipProblem listing the problems (at most 20).
 */
export function checkEntries(entries, kind, { cdOffset, maxUnpacked }) {
  const problems = [];
  const add = (p) => {
    if (problems.length < 20) problems.push(p);
  };
  const seen = new Map();
  for (const e of entries) {
    // (Labels are cheap string slices; entries are checked in a tight loop for up to 1000 entries.)
    const label = e.name === null ? `File ${e.index + 1}` : e.name.length > 120 ? e.name.slice(0, 120) : e.name;
    if (e.flags & 0x1 || e.flags & 0x40 || e.flags & 0x2000) add(`${label} is encrypted.`);
    if (e.method !== 0 && e.method !== 8) add(`${label} uses a compression method other than store or deflate.`);
    if (e.versionNeeded > 20) add(`${label} needs zip features the hub doesn't take.`);
    if (e.csize === 0xffffffff || e.usize === 0xffffffff || e.offset === 0xffffffff) add(`${label} uses ZIP64.`);
    if (e.extraLength) {
      const extra = extraHasZip64(e.extra);
      if (extra.zip64) add(`${label} uses ZIP64.`);
      if (extra.bad) add(`${label} has a damaged extra field.`);
    }
    if (e.commentLength) add(`${label} has a comment; hub packages have none.`);
    if (e.diskStart !== 0) add(`${label} is on another disk.`);
    if (e.method === 0 && e.csize !== e.usize) add(`${label} is stored but its sizes differ.`);
    if (impossibleSize(e)) add(`${label} declares a size its compressed data can't have.`);
    const why = nameProblem(e.name);
    if (why) {
      add(`${label} ${why}.`);
      continue;
    }
    e.isDir = e.name.endsWith("/");
    if (e.isDir && e.usize !== 0) add(`${label} is a folder with data in it.`);
    const clean = e.isDir ? e.name.slice(0, -1) : e.name;
    const slash = clean.indexOf("/");
    e.first = slash < 0 ? clean : clean.slice(0, slash); // the top-level part of the path
    e.oneDeep = slash >= 0 && clean.indexOf("/", slash + 1) < 0; // exactly "Top/name"
    const key = clean.toUpperCase();
    if (seen.has(key)) add(`${label} is in the zip twice (names are compared ignoring case).`);
    seen.set(key, e);
  }
  if (problems.length) throw new ZipProblem(problems);

  // Overlapping entries (the overlapping-file zip bomb): sorted by local header offset, each entry's
  // header, name and data must end before the next entry and before the file list.
  const byOffset = [...entries].sort((a, b) => a.offset - b.offset);
  if (byOffset[0].offset !== 0) throw new ZipProblem("The zip's first file doesn't start at the beginning.");
  for (let i = 0; i < byOffset.length; i++) {
    const e = byOffset[i];
    const end = e.offset + 30 + e.nameLength + e.csize + (e.flags & 0x8 ? 12 : 0);
    const limit = i + 1 < byOffset.length ? byOffset[i + 1].offset : cdOffset;
    if (end > limit) throw new ZipProblem("Files in the zip overlap each other (the shape of a zip bomb).");
  }

  // Where battle.json / manifest.json is, and the prefix every entry must be inside.
  const rootName = kind === "battle" ? "battle.json" : "manifest.json";
  let prefix = null;
  const atRoot = entries.find((e) => !e.isDir && e.name.toLowerCase() === rootName);
  if (atRoot) prefix = "";
  else if (kind === "battle") {
    const tops = new Set(entries.map((e) => e.first));
    const tail = "/" + rootName;
    const candidates = entries.filter((e) => !e.isDir && e.oneDeep && e.name.length === e.first.length + tail.length && e.name.toLowerCase().endsWith(tail));
    if (candidates.length === 1 && tops.size === 1) prefix = candidates[0].first + "/";
    else if (candidates.length > 1) throw new ZipProblem("The zip has more than one battle.json.");
    else if (candidates.length === 1) throw new ZipProblem("Everything in a battle must be inside its one folder.");
  }
  if (prefix === null) throw new ZipProblem(`The zip has no ${rootName}${kind === "battle" ? " at its root or in its one folder" : " at its root"}.`);

  let total = 0;
  const files = [];
  for (const e of entries) {
    if (!e.name.startsWith(prefix)) {
      add(`${e.name.slice(0, 120)} is outside the battle's folder.`);
      continue;
    }
    total += e.usize;
    if (e.isDir) continue;
    const relative = e.name.slice(prefix.length);
    const ext = extensionOf(relative);
    if (!EXTENSIONS[kind].has(ext)) {
      add(
        ext === "mp4" ? `${relative} is an MP4 video; the hub takes WebM (VP8) videos or frames.`
          : ext === "" ? `${relative} has no file type.`
          : `${relative} is a .${ext} file, which the hub doesn't take${kind === "charts" ? " in a difficulty pack" : ""}.`,
      );
      continue;
    }
    if (e.usize > limitFor(relative, ext)) add(`${relative} is too big (${Math.ceil(e.usize / MiB)} MB).`);
    e.relative = relative;
    e.ext = ext;
    files.push(e);
  }
  if (total > maxUnpacked) add(`The package unpacks to ${Math.ceil(total / MiB)} MB; the hub takes up to ${Math.floor(maxUnpacked / MiB)} MB.`);
  if (problems.length) throw new ZipProblem(problems);
  const root = files.find((e) => e.relative.toLowerCase() === rootName);
  return { prefix, root, files, unpacked: total };
}

/**
 * The directory fingerprint: SHA-256 (hex) of one line per entry, `name \0 size \0 crc32-hex \n`, sorted by
 * the name with only A-Z lowered, compared by UTF-16 code units. The mod computes the same.
 */
export async function fingerprint(entries) {
  const lines = entries
    .map((e) => ({ key: e.name.replace(/[A-Z]/g, (c) => c.toLowerCase()), line: `${e.name}\0${e.usize}\0${e.crc.toString(16).padStart(8, "0")}\n` }))
    .sort((a, b) => (a.key < b.key ? -1 : a.key > b.key ? 1 : 0));
  return sha256Hex(lines.map((l) => l.line).join(""));
}

/** Counts from the directory: what's inside, for the detail panel. */
export function contentsSummary(files, unpacked) {
  const c = { files: files.length, unpacked, songs: 0, charts: 0, pictures: 0, videos: 0, json: 0, other: 0 };
  for (const e of files) {
    if (AUDIO_EXT.has(e.ext)) c.songs++;
    else if (e.ext === "sm") c.charts++;
    else if (PICTURE_EXT.has(e.ext)) c.pictures++;
    else if (VIDEO_EXT.has(e.ext)) c.videos++;
    else if (e.ext === "json") c.json++;
    else c.other++;
  }
  return c;
}
