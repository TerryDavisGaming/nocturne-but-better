using System.Runtime.CompilerServices;

namespace NocturneFlatScroll;

/// <summary>
/// A Vorbis codebook: a canonical Huffman code over <see cref="Entries"/> entries, plus the VQ
/// vectors for lookup types 1 and 2. Decoding uses a 10-bit table for short codes and a binary
/// search over the sorted codewords for long ones.
/// </summary>
internal sealed class VorbisCodebook
{
    private const int FastBits = 10;
    private const int FastSize = 1 << FastBits;
    internal const int MaxEntries = 1 << 20;

    internal int Dimensions;
    internal int Entries;
    internal int LookupType;
    /// <summary>Unpacked vectors, Entries * Dimensions floats; null for lookup type 0.</summary>
    internal float[]? Vq;

    private byte[] lengths = Array.Empty<byte>();       // codeword length per entry, 0 = unused
    private int[] fast = Array.Empty<int>();            // first FastBits bits (LSB first) -> entry, -1 = slow path
    private uint[] sortedCodes = Array.Empty<uint>();   // codewords MSB first, left-aligned to 32 bits, ascending
    private byte[] sortedLengths = Array.Empty<byte>();
    private int[] sortedEntries = Array.Empty<int>();

    internal static int ILog(long v)
    {
        int r = 0;
        while (v > 0) { r++; v >>= 1; }
        return r;
    }

    /// <summary>The number of scalars stored for lookup type 1 (the spec's lookup1_values).</summary>
    internal static int Lookup1Values(int entries, int dim)
    {
        int r = (int)Math.Floor(Math.Exp(Math.Log(entries) / dim));
        // Correct floating point rounding in either direction.
        while (Pow(r + 1, dim) <= entries) r++;
        while (r > 0 && Pow(r, dim) > entries) r--;
        return r;
    }

    private static long Pow(long b, int e)
    {
        long r = 1;
        for (int i = 0; i < e; i++) { r *= b; if (r > int.MaxValue) return r; }
        return r;
    }

    /// <summary>Parses a standard Vorbis I codebook from a setup header.</summary>
    /// <param name="vectorRoom">How many vector values the setup still allows; a bigger VQ table is refused before it's made.</param>
    internal static VorbisCodebook Read(VorbisBitReader br, long vectorRoom)
    {
        if (br.ReadHeader(24) != 0x564342) throw new InvalidDataException("a Vorbis codebook has a bad sync pattern");
        var cb = new VorbisCodebook
        {
            Dimensions = (int)br.ReadHeader(16),
            Entries = (int)br.ReadHeader(24),
        };
        if (cb.Dimensions == 0 || cb.Entries == 0) throw new InvalidDataException("a Vorbis codebook is empty");
        // libvorbis' limit, which also keeps the VQ table below 16M values, and a cap on the Huffman
        // table far above what any encoder writes, so a damaged header can't ask for gigabytes.
        if (ILog(cb.Dimensions) + ILog(cb.Entries) > 24 || cb.Entries > MaxEntries)
            throw new InvalidDataException("a Vorbis codebook is too large");
        var lengths = new byte[cb.Entries];
        if (br.ReadHeader(1) != 0)
        {
            ReadOrderedLengths(br, lengths);
        }
        else
        {
            bool sparse = br.ReadHeader(1) != 0;
            for (int i = 0; i < cb.Entries; i++)
            {
                bool present = !sparse || br.ReadHeader(1) != 0;
                lengths[i] = present ? (byte)(br.ReadHeader(5) + 1) : (byte)0;
            }
        }
        cb.LookupType = (int)br.ReadHeader(4);
        if ((cb.LookupType == 1 || cb.LookupType == 2) && (long)cb.Entries * cb.Dimensions > vectorRoom)
            throw new InvalidDataException("the Vorbis setup's tables are too large");
        if (cb.LookupType == 1 || cb.LookupType == 2) cb.ReadLookup(br);
        else if (cb.LookupType != 0) throw new InvalidDataException("a Vorbis codebook has a bad lookup type");
        cb.Build(lengths);
        return cb;
    }

    private static void ReadOrderedLengths(VorbisBitReader br, byte[] lengths)
    {
        int entries = lengths.Length;
        int len = (int)br.ReadHeader(5) + 1;
        int cur = 0;
        while (cur < entries)
        {
            int number = (int)br.ReadHeader(ILog(entries - cur));
            if (cur + number > entries || len > 32) throw new InvalidDataException("a Vorbis codebook's lengths overflow");
            for (int i = 0; i < number; i++) lengths[cur + i] = (byte)len;
            cur += number;
            len++;
        }
    }

    /// <summary>The Vorbis float format: 21-bit mantissa, 10-bit exponent (bias 788), sign.</summary>
    private static float Float32Unpack(uint x)
    {
        double mant = x & 0x1FFFFF;
        int exp = (int)((x & 0x7FE00000) >> 21);
        if ((x & 0x80000000) != 0) mant = -mant;
        return (float)(mant * Math.Pow(2, exp - 788));
    }

    private void ReadLookup(VorbisBitReader br)
    {
        float min = Float32Unpack(br.ReadHeader(32));
        float delta = Float32Unpack(br.ReadHeader(32));
        int valueBits = (int)br.ReadHeader(4) + 1;
        bool sequenceP = br.ReadHeader(1) != 0;
        int count = LookupType == 1 ? Lookup1Values(Entries, Dimensions) : Entries * Dimensions;
        if (count <= 0 || (long)count * valueBits > br.BitsLeft) throw new InvalidDataException("a Vorbis codebook's lookup table ends early");
        var mult = new int[count];
        for (int i = 0; i < count; i++) mult[i] = (int)br.ReadHeader(valueBits);

        // Every vector up front, in libvorbis' evaluation order: mult * delta + min + last.
        Vq = new float[Entries * Dimensions];
        for (int e = 0; e < Entries; e++)
        {
            float last = 0;
            int divisor = 1;
            for (int k = 0; k < Dimensions; k++)
            {
                int idx = LookupType == 1 ? (e / divisor) % count : e * Dimensions + k;
                float val = mult[idx] * delta + min + last;
                Vq[e * Dimensions + k] = val;
                if (sequenceP) last = val;
                divisor *= count;
            }
        }
    }

    /// <summary>Assigns the canonical codewords (the spec's algorithm) and builds the decode tables.</summary>
    private void Build(byte[] codeLengths)
    {
        lengths = codeLengths;
        var codes = new uint[Entries];
        var available = new uint[33];
        int used = 0;
        bool first = true;
        for (int i = 0; i < Entries; i++)
        {
            int len = codeLengths[i];
            if (len == 0) continue;
            used++;
            if (first)
            {
                first = false;
                codes[i] = 0;
                for (int j = 1; j <= len; j++) available[j] = 1u << (32 - j);
                continue;
            }
            int z = len;
            while (z > 0 && available[z] == 0) z--;
            if (z == 0) throw new InvalidDataException("a Vorbis codebook's Huffman tree is overfull");
            uint res = available[z];
            available[z] = 0;
            codes[i] = res;
            for (int y = len; y > z; y--) available[y] = res + (1u << (32 - y));
        }

        fast = new int[FastSize];
        fast.AsSpan().Fill(-1);
        sortedCodes = new uint[used];
        sortedLengths = new byte[used];
        sortedEntries = new int[used];
        int n = 0;
        for (int i = 0; i < Entries; i++)
        {
            int len = codeLengths[i];
            if (len == 0) continue;
            sortedCodes[n] = codes[i];
            sortedEntries[n] = i;
            n++;
            if (len <= FastBits)
            {
                uint rev = BitReverse(codes[i]); // first bit of the codeword in bit 0
                for (uint j = rev; j < FastSize; j += 1u << len) fast[j] = i;
            }
        }
        Array.Sort(sortedCodes, sortedEntries);
        for (int i = 0; i < used; i++) sortedLengths[i] = codeLengths[sortedEntries[i]];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint BitReverse(uint n)
    {
        n = ((n & 0xAAAAAAAA) >> 1) | ((n & 0x55555555) << 1);
        n = ((n & 0xCCCCCCCC) >> 2) | ((n & 0x33333333) << 2);
        n = ((n & 0xF0F0F0F0) >> 4) | ((n & 0x0F0F0F0F) << 4);
        n = ((n & 0xFF00FF00) >> 8) | ((n & 0x00FF00FF) << 8);
        return (n >> 16) | (n << 16);
    }

    /// <summary>Decodes one entry number, or -1 at the end of the packet or on an invalid code.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int DecodeScalar(VorbisBitReader br)
    {
        uint bits = br.Peek32(out int avail);
        int e = fast[bits & (FastSize - 1)];
        int len;
        if (e >= 0)
        {
            len = lengths[e];
        }
        else
        {
            e = DecodeSlow(bits, out len);
            if (e < 0) { br.SetEndOfPacket(); return -1; }
        }
        if (len > avail) { br.SetEndOfPacket(); return -1; }
        br.Skip(len);
        return e;
    }

    private int DecodeSlow(uint bits, out int len)
    {
        uint v = BitReverse(bits); // stream order, first bit in the MSB
        // The greatest left-aligned codeword <= v; in a prefix code it's the only candidate.
        int lo = 0, hi = sortedCodes.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (sortedCodes[mid] <= v) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        len = 0;
        if (found < 0) return -1;
        len = sortedLengths[found];
        if (((ulong)(v ^ sortedCodes[found]) >> (32 - len)) != 0) return -1;
        return sortedEntries[found];
    }
}
