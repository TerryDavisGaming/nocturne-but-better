using System.Runtime.CompilerServices;

namespace NocturneFlatScroll;

/// <summary>
/// Reads bits LSB first, the Vorbis and Ogg packing order. A read that runs past the end of the
/// packet fails and so does every later one, like libvorbis' oggpack: audio decoding treats that
/// as "the rest of this packet is zero", header parsing as an error.
/// </summary>
internal sealed class VorbisBitReader
{
    private byte[] data = Array.Empty<byte>();
    private int pos;      // next byte to load into buf
    private int end;      // end of the readable range (exclusive)
    private ulong buf;    // pending bits, LSB = next bit
    private int count;    // valid bits in buf

    /// <summary>True once any read went past the end of the range.</summary>
    internal bool EndOfPacket;

    internal VorbisBitReader() { }

    internal VorbisBitReader(byte[] bytes, int offset, int length) { Init(bytes, offset, length); }

    internal void Init(byte[] bytes, int offset, int length)
    {
        if ((uint)offset > (uint)bytes.Length || (uint)length > (uint)(bytes.Length - offset))
            throw new ArgumentOutOfRangeException(nameof(length));
        data = bytes;
        pos = offset;
        end = offset + length;
        buf = 0;
        count = 0;
        EndOfPacket = false;
    }

    /// <summary>Bits not yet read.</summary>
    internal long BitsLeft => EndOfPacket ? 0 : (long)(end - pos) * 8 + count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Fill()
    {
        while (count <= 56 && pos < end)
        {
            buf |= (ulong)data[pos++] << count;
            count += 8;
        }
    }

    /// <summary>Reads n (0 to 31) bits, or returns -1 at the end of the packet.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Read(int n)
    {
        if (count < n)
        {
            Fill();
            if (count < n) { SetEndOfPacket(); return -1; }
        }
        int v = (int)(buf & ((1UL << n) - 1));
        buf >>= n;
        count -= n;
        return v;
    }

    /// <summary>Reads n (0 to 32) bits of a header; running out is an error.</summary>
    internal uint ReadHeader(int n)
    {
        if (n == 0) return 0;
        if (count < n)
        {
            Fill();
            if (count < n) throw new InvalidDataException("the Vorbis header ends early");
        }
        uint v = (uint)(buf & ((1UL << n) - 1));
        buf >>= n;
        count -= n;
        return v;
    }

    /// <summary>
    /// The next 32 bits (fewer at the end; missing bits read as zero) without consuming them;
    /// <paramref name="available"/> says how many are real.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint Peek32(out int available)
    {
        if (count < 32) Fill();
        available = count;
        return (uint)buf;
    }

    /// <summary>Consumes n bits already checked with <see cref="Peek32"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Skip(int n)
    {
        buf >>= n;
        count -= n;
    }

    internal void SetEndOfPacket()
    {
        EndOfPacket = true;
        pos = end;
        buf = 0;
        count = 0;
    }
}
