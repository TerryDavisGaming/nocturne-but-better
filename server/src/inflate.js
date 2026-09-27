// A small raw-deflate decoder for the START of an entry: it stops after `want` output bytes, and a cut-off
// input just ends the output early. Used to look at the first bytes of a compressed media file without
// reading the whole entry (DecompressionStream can't hand back the output of a truncated input reliably).
// Whole small files (battle.json) use the platform's DecompressionStream instead.

class Bits {
  constructor(data) {
    this.data = data;
    this.pos = 0;
    this.bit = 0;
    this.bits = 0;
  }
  need(n) {
    while (this.bits < n) {
      if (this.pos >= this.data.length) throw TRUNCATED;
      this.bit |= this.data[this.pos++] << this.bits;
      this.bits += 8;
    }
  }
  read(n) {
    if (n === 0) return 0;
    this.need(n);
    const v = this.bit & ((1 << n) - 1);
    this.bit >>>= n;
    this.bits -= n;
    return v;
  }
  align() {
    this.bit = 0;
    this.bits = 0;
  }
}

const TRUNCATED = new Error("truncated");
const BAD = () => new Error("bad deflate data");

function huffman(lengths) {
  const counts = new Uint16Array(16);
  for (const l of lengths) counts[l]++;
  counts[0] = 0;
  const offs = new Uint16Array(16);
  for (let i = 1; i < 16; i++) offs[i] = offs[i - 1] + counts[i - 1];
  const symbols = new Uint16Array(lengths.length);
  for (let s = 0; s < lengths.length; s++) if (lengths[s]) symbols[offs[lengths[s]]++] = s;
  return { counts, symbols };
}

function decodeSymbol(bits, h) {
  let code = 0, first = 0, index = 0;
  for (let len = 1; len < 16; len++) {
    code |= bits.read(1);
    const count = h.counts[len];
    if (code - count < first) return h.symbols[index + (code - first)];
    index += count;
    first += count;
    first <<= 1;
    code <<= 1;
  }
  throw BAD();
}

const LEN_BASE = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
const LEN_EXTRA = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
const DIST_BASE = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
const DIST_EXTRA = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
const ORDER = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

let fixed = null;
function fixedTables() {
  if (!fixed) {
    const l = new Uint8Array(288);
    l.fill(8, 0, 144);
    l.fill(9, 144, 256);
    l.fill(7, 256, 280);
    l.fill(8, 280, 288);
    fixed = { lit: huffman(l), dist: huffman(new Uint8Array(30).fill(5)) };
  }
  return fixed;
}

/** Inflates raw deflate data until `want` bytes are out, the stream ends, or the input runs out. */
export function inflatePrefix(data, want) {
  const out = new Uint8Array(want);
  let n = 0;
  const bits = new Bits(data);
  try {
    for (;;) {
      const last = bits.read(1);
      const type = bits.read(2);
      if (type === 0) {
        bits.align();
        if (bits.pos + 4 > data.length) throw TRUNCATED;
        const len = data[bits.pos] | (data[bits.pos + 1] << 8);
        const nlen = data[bits.pos + 2] | (data[bits.pos + 3] << 8);
        if ((len ^ 0xffff) !== nlen) throw BAD();
        bits.pos += 4;
        for (let i = 0; i < len; i++) {
          if (bits.pos >= data.length) throw TRUNCATED;
          out[n++] = data[bits.pos++];
          if (n >= want) return out;
        }
      } else if (type === 1 || type === 2) {
        let lit, dist;
        if (type === 1) ({ lit, dist } = fixedTables());
        else {
          const hlit = bits.read(5) + 257, hdist = bits.read(5) + 1, hclen = bits.read(4) + 4;
          const cl = new Uint8Array(19);
          for (let i = 0; i < hclen; i++) cl[ORDER[i]] = bits.read(3);
          const clh = huffman(cl);
          const lengths = new Uint8Array(hlit + hdist);
          for (let i = 0; i < hlit + hdist;) {
            const sym = decodeSymbol(bits, clh);
            if (sym < 16) lengths[i++] = sym;
            else {
              let rep = 0, val = 0;
              if (sym === 16) {
                if (i === 0) throw BAD();
                val = lengths[i - 1];
                rep = 3 + bits.read(2);
              } else if (sym === 17) rep = 3 + bits.read(3);
              else rep = 11 + bits.read(7);
              if (i + rep > hlit + hdist) throw BAD();
              while (rep--) lengths[i++] = val;
            }
          }
          lit = huffman(lengths.subarray(0, hlit));
          dist = huffman(lengths.subarray(hlit));
        }
        for (;;) {
          const sym = decodeSymbol(bits, lit);
          if (sym < 256) {
            out[n++] = sym;
            if (n >= want) return out;
          } else if (sym === 256) break;
          else {
            const li = sym - 257;
            if (li >= 29) throw BAD();
            const length = LEN_BASE[li] + bits.read(LEN_EXTRA[li]);
            const di = decodeSymbol(bits, dist);
            if (di >= 30) throw BAD();
            const distance = DIST_BASE[di] + bits.read(DIST_EXTRA[di]);
            if (distance > n) throw BAD();
            for (let i = 0; i < length; i++) {
              out[n] = out[n - distance];
              n++;
              if (n >= want) return out;
            }
          }
        }
      } else throw BAD();
      if (last) break;
    }
  } catch (e) {
    if (e !== TRUNCATED) throw e;
  }
  return out.subarray(0, n);
}

/** Inflates a whole small entry with the platform's DecompressionStream, refusing more than `cap` bytes. */
export async function inflateAll(data, cap) {
  const stream = new Blob([data]).stream().pipeThrough(new DecompressionStream("deflate-raw"));
  const reader = stream.getReader();
  const chunks = [];
  let size = 0;
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    size += value.byteLength;
    if (size > cap) {
      await reader.cancel().catch(() => {});
      throw new Error("inflates past its size");
    }
    chunks.push(value);
  }
  const out = new Uint8Array(size);
  let at = 0;
  for (const c of chunks) {
    out.set(c, at);
    at += c.byteLength;
  }
  return out;
}

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

export function crc32(bytes) {
  let c = 0xffffffff;
  for (let i = 0; i < bytes.length; i++) c = CRC_TABLE[(c ^ bytes[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}
