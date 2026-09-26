// Reading parts of an uploaded zip from R2 with ranged gets, within a fixed budget of gets and bytes, so a
// complete stays inside the free plan's CPU time and subrequest limits.

import { SIG_LOCAL, ZipProblem } from "./zipcheck.js";
import { inflatePrefix } from "./inflate.js";

const u16 = (b, at) => b[at] | (b[at + 1] << 8);
const u32 = (b, at) => (b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24)) >>> 0;

export class Budget {
  constructor(gets, bytes) {
    this.gets = gets;
    this.bytes = bytes;
    this.usedGets = 0;
    this.usedBytes = 0;
  }
  canTake(length) {
    return this.usedGets < this.gets && this.usedBytes + length <= this.bytes;
  }
}

export class OutOfBudget extends Error {}

/** One ranged get. Throws OutOfBudget when the budget is spent. */
export async function readRange(files, key, offset, length, budget) {
  if (length <= 0) return new Uint8Array(0);
  if (budget) {
    if (!budget.canTake(length)) throw new OutOfBudget("budget");
    budget.usedGets++;
    budget.usedBytes += length;
  }
  const obj = await files.get(key, { range: { offset, length } });
  if (!obj) throw new ZipProblem("The uploaded file went missing while it was checked.");
  return new Uint8Array(await obj.arrayBuffer());
}

/** Parses a local file header at `at` in `buf`, or null when it isn't one. */
export function parseLocal(buf, at) {
  if (at + 30 > buf.length || u32(buf, at) !== SIG_LOCAL) return null;
  return {
    flags: u16(buf, at + 6), method: u16(buf, at + 8), crc: u32(buf, at + 14), csize: u32(buf, at + 18), usize: u32(buf, at + 22),
    nameLength: u16(buf, at + 26), extraLength: u16(buf, at + 28),
  };
}

/**
 * Why a local header disagrees with its central directory entry, or null. A local extra field is refused
 * outright: readers start an entry's data after it, but the directory doesn't say how long it is, so it
 * could move the data onto the next entry and get past the overlap rule (extra-field quoting). The mod's
 * zip writer puts none there.
 */
export function localProblem(local, entry, nameBytes) {
  if (!local) return "has a damaged local header";
  if (local.extraLength !== 0) return "has an extra field in its local header; hub packages have none";
  if (local.method !== entry.method) return "has local and central headers that disagree";
  if ((local.flags & 0x1) !== (entry.flags & 0x1) || (local.flags & 0x8) !== (entry.flags & 0x8)) return "has local and central headers that disagree";
  if (!(local.flags & 0x8) && (local.crc !== entry.crc || local.csize !== entry.csize || local.usize !== entry.usize)) {
    return "has local and central headers that disagree";
  }
  if (local.nameLength !== entry.nameLength) return "has local and central names that disagree";
  for (let i = 0; i < entry.nameLength; i++) if (nameBytes[i] !== entry.nameBytes[i]) return "has local and central names that disagree";
  return null;
}

export const LOCAL_SLACK = 256; // room for a local extra field in a first read

/**
 * Reads an entry's unpacked bytes for the media checks. It starts from a window already fetched (`seed`,
 * holding the entry's local header at `seedOffset`), fetches more windows as the checks ask for them,
 * and inflates the start of compressed entries (at most `inflateCap` bytes out).
 */
export class EntryReader {
  constructor(files, key, entry, budget, { seed = null, seedOffset = 0, window = 64 * 1024, inflateCap = 64 * 1024 } = {}) {
    this.files = files;
    this.key = key;
    this.entry = entry;
    this.budget = budget;
    this.window = window;
    this.inflateCap = inflateCap;
    this.seed = seed;
    this.seedOffset = seedOffset;
    this.dataStart = null;
    this.cache = null; // { start (relative to the entry's data), bytes }
    this.inflated = null;
  }

  async open() {
    const e = this.entry;
    let buf = this.seed, base = this.seedOffset;
    const headerEnd = e.offset + 30 + e.nameLength;
    if (!buf || e.offset < base || headerEnd > base + buf.length) {
      buf = await readRange(this.files, this.key, e.offset, 30 + e.nameLength + LOCAL_SLACK + 64, this.budget);
      base = e.offset;
    }
    const local = parseLocal(buf, e.offset - base);
    const nameAt = e.offset - base + 30;
    const why = localProblem(local, e, buf.subarray(nameAt, nameAt + e.nameLength));
    if (why) throw new ZipProblem(`${e.relative} ${why}.`);
    this.dataStart = e.offset + 30 + local.nameLength + local.extraLength;
    if (this.dataStart + e.csize > e.cdOffset) throw new ZipProblem(`${e.relative} runs into the zip's file list.`);
    const dataInBuf = this.dataStart - base;
    if (dataInBuf >= 0 && dataInBuf < buf.length) this.cache = { start: 0, bytes: buf.subarray(dataInBuf, Math.min(buf.length, dataInBuf + e.csize)) };
    return this;
  }

  /** Up to `length` unpacked bytes at `offset` (fewer at the end of the entry). */
  async read(offset, length) {
    const e = this.entry;
    if (offset >= e.usize || length <= 0) return new Uint8Array(0);
    length = Math.min(length, e.usize - offset);
    if (e.method === 0) {
      const c = this.cache;
      if (c && offset >= c.start && offset + length <= c.start + c.bytes.length) return c.bytes.subarray(offset - c.start, offset - c.start + length);
      const size = Math.min(Math.max(length, this.window), e.csize - offset);
      const bytes = await readRange(this.files, this.key, this.dataStart + offset, size, this.budget);
      this.cache = { start: offset, bytes };
      return bytes.subarray(0, length);
    }
    // Deflated: inflate the start once, from at most inflateCap bytes of compressed data.
    if (offset + length > this.inflateCap) throw new ZipProblem(`${e.relative} is compressed, so the hub can't look far enough into it; store it without compression.`);
    if (!this.inflated) {
      let compressed;
      const want = Math.min(e.csize, this.inflateCap);
      if (this.cache && this.cache.start === 0 && this.cache.bytes.length >= want) compressed = this.cache.bytes.subarray(0, want);
      else compressed = await readRange(this.files, this.key, this.dataStart, want, this.budget);
      try {
        this.inflated = inflatePrefix(compressed, this.inflateCap);
      } catch {
        throw new ZipProblem(`${e.relative} has damaged compressed data.`);
      }
    }
    return this.inflated.subarray(Math.min(offset, this.inflated.length), Math.min(offset + length, this.inflated.length));
  }
}
