using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace NocturneFlatScroll;

/// <summary>
/// Writes RGBA pixels (top row first) as a PNG file: 8 bits a channel with alpha, each row
/// filtered the way that packs it best (the usual smallest-sum rule), deflated with .NET's own
/// zlib. The battle creator saves the sprite sheets it makes from videos with it, on a worker
/// thread, so no game call is needed.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    // Data chunks of at most this many bytes, as most encoders write them.
    private const int ChunkBytes = 1 << 20;
    private static readonly uint[] CrcTable = MakeCrcTable();

    internal static byte[] Encode(byte[] rgba, int width, int height, CancellationToken cancel = default)
    {
        if (width < 1 || height < 1 || (long)width * height * 4 > rgba.LongLength) throw new ArgumentException("the pixels don't match the size");
        byte[] data;
        using (var packed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            {
                int stride = width * 4;
                var row = new byte[stride + 1];
                var best = new byte[stride + 1];
                for (int y = 0; y < height; y++)
                {
                    if ((y & 63) == 0) cancel.ThrowIfCancellationRequested();
                    long bestSum = long.MaxValue;
                    for (byte filter = 0; filter <= 4; filter++)
                    {
                        long sum = Filter(rgba, stride, y, filter, row);
                        if (sum >= bestSum) continue;
                        bestSum = sum;
                        (best, row) = (row, best);
                    }
                    zlib.Write(best, 0, best.Length);
                }
            }
            data = packed.ToArray();
        }
        cancel.ThrowIfCancellationRequested();
        using var file = new MemoryStream(data.Length + 1024);
        file.Write(Signature, 0, Signature.Length);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;   // bits a channel
        header[9] = 6;   // RGBA
        Chunk(file, "IHDR", header, 0, header.Length);
        for (int at = 0; at < data.Length; at += ChunkBytes) Chunk(file, "IDAT", data, at, Math.Min(ChunkBytes, data.Length - at));
        Chunk(file, "IEND", data, 0, 0);
        return file.ToArray();
    }

    // Row y with one of the five filters into row (the filter's number first); the sum of the
    // bytes as signed values, which is smallest for the row that packs best.
    private static long Filter(byte[] rgba, int stride, int y, byte filter, byte[] row)
    {
        int at = y * stride, up = at - stride;
        bool first = y == 0;
        row[0] = filter;
        long sum = 0;
        for (int i = 0; i < stride; i++)
        {
            int x = rgba[at + i];
            int a = i >= 4 ? rgba[at + i - 4] : 0;
            int b = first ? 0 : rgba[up + i];
            int c = i >= 4 && !first ? rgba[up + i - 4] : 0;
            int v = filter switch
            {
                1 => x - a,
                2 => x - b,
                3 => x - ((a + b) >> 1),
                4 => x - Paeth(a, b, c),
                _ => x,
            };
            byte out8 = (byte)v;
            row[i + 1] = out8;
            // As a signed byte: -128 counts 128 (Math.Abs would throw on it).
            int signed = (sbyte)out8;
            sum += signed < 0 ? -signed : signed;
        }
        return sum;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream output, string type, byte[] data, int offset, int count)
    {
        var head = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, count);
        Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
        output.Write(head, 0, 8);
        output.Write(data, offset, count);
        uint crc = Crc(0xFFFFFFFFu, head, 4, 4);
        crc = Crc(crc, data, offset, count) ^ 0xFFFFFFFFu;
        var tail = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        output.Write(tail, 0, 4);
    }

    private static uint Crc(uint crc, byte[] data, int offset, int count)
    {
        for (int i = offset; i < offset + count; i++) crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
