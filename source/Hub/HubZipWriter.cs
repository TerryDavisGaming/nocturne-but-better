using System.IO.Compression;
using System.Text;

namespace NocturnePlus;

/// <summary>
/// Writes the zips the hub takes, byte for byte as its rules want them (server/API.md "the package
/// rules"): every entry stored or deflated, version needed 2.0, no extra field in any header, no
/// data descriptors, no comments, no ZIP64, every time fixed at 1980-01-01, UTF-8 names flagged,
/// and the sizes and CRC-32 in each local header. System.IO.Compression isn't used for writing, so
/// what the hub reads can't change with the .NET version a loader brings (the offline check records
/// what it writes). Deflate output may still differ between runtimes; the hub's fingerprint doesn't
/// depend on it.
/// This file has no Unity or game dependencies.
/// </summary>
internal sealed class HubZipWriter : IDisposable
{
    private const ushort VersionNeeded = 20;
    private const ushort DosDate1980 = (0 << 9) | (1 << 5) | 1;   // 1980-01-01
    private const ushort DosTime0 = 0;

    private sealed class Written
    {
        internal byte[] Name = Array.Empty<byte>();
        internal ushort Flags, Method;
        internal uint Crc;
        internal long CompressedSize, Size, Offset;
    }

    private readonly FileStream file;
    private readonly List<Written> written = new();
    private bool finished;

    internal HubZipWriter(string path) =>
        file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);

    internal int Count => written.Count;

    /// <summary>Adds a file from bytes.</summary>
    internal void Add(string name, byte[] data, bool deflate)
    {
        using var input = new MemoryStream(data, writable: false);
        Add(name, input, deflate);
    }

    /// <summary>Adds a file from a stream read to its end (stored as it is, or deflated).</summary>
    internal void Add(string name, Stream input, bool deflate)
    {
        if (finished) throw new InvalidOperationException("the zip is finished");
        if (written.Count >= 65534) throw new InvalidDataException("too many files for a zip without ZIP64");
        var entry = new Written
        {
            Name = Encoding.UTF8.GetBytes(name),
            Method = (ushort)(deflate ? 8 : 0),
            Offset = file.Position,
        };
        if (entry.Name.Length > ushort.MaxValue) throw new InvalidDataException("the name is too long");
        if (entry.Name.Any(b => b >= 0x80)) entry.Flags |= 0x800;
        WriteLocalHeader(entry);
        long dataStart = file.Position;
        uint crc = Crc32.Start;
        long size = 0;
        var buffer = new byte[81920];
        if (deflate)
        {
            using var deflater = new DeflateStream(file, CompressionLevel.Optimal, leaveOpen: true);
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                crc = Crc32.Update(crc, buffer.AsSpan(0, n));
                size += n;
                deflater.Write(buffer, 0, n);
            }
        }
        else
        {
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                crc = Crc32.Update(crc, buffer.AsSpan(0, n));
                size += n;
                file.Write(buffer, 0, n);
            }
        }
        long end = file.Position;
        entry.Crc = Crc32.Finish(crc);
        entry.Size = size;
        entry.CompressedSize = end - dataStart;
        if (entry.Size > uint.MaxValue - 1 || entry.CompressedSize > uint.MaxValue - 1 || entry.Offset > uint.MaxValue - 1)
            throw new InvalidDataException("the package is too big for a zip without ZIP64");
        // The sizes and CRC go into the local header itself (no data descriptor).
        file.Position = entry.Offset + 14;
        WriteU32(entry.Crc);
        WriteU32((uint)entry.CompressedSize);
        WriteU32((uint)entry.Size);
        file.Position = end;
        written.Add(entry);
    }

    private void WriteLocalHeader(Written e)
    {
        WriteU32(0x04034b50);
        WriteU16(VersionNeeded);
        WriteU16(e.Flags);
        WriteU16(e.Method);
        WriteU16(DosTime0);
        WriteU16(DosDate1980);
        WriteU32(0);   // CRC-32, filled in after the data
        WriteU32(0);   // compressed size
        WriteU32(0);   // size
        WriteU16((ushort)e.Name.Length);
        WriteU16(0);   // no extra field
        file.Write(e.Name, 0, e.Name.Length);
    }

    /// <summary>Writes the file list and the end record (no comment).</summary>
    internal void Finish()
    {
        if (finished) return;
        finished = true;
        long cdStart = file.Position;
        foreach (var e in written)
        {
            WriteU32(0x02014b50);
            WriteU16(VersionNeeded);      // made by: MS-DOS, 2.0
            WriteU16(VersionNeeded);
            WriteU16(e.Flags);
            WriteU16(e.Method);
            WriteU16(DosTime0);
            WriteU16(DosDate1980);
            WriteU32(e.Crc);
            WriteU32((uint)e.CompressedSize);
            WriteU32((uint)e.Size);
            WriteU16((ushort)e.Name.Length);
            WriteU16(0);                  // extra
            WriteU16(0);                  // comment
            WriteU16(0);                  // disk
            WriteU16(0);                  // internal attributes
            WriteU32(0);                  // external attributes
            WriteU32((uint)e.Offset);
            file.Write(e.Name, 0, e.Name.Length);
        }
        long cdSize = file.Position - cdStart;
        if (cdStart > uint.MaxValue - 1 || cdSize > uint.MaxValue - 1) throw new InvalidDataException("the package is too big for a zip without ZIP64");
        WriteU32(0x06054b50);
        WriteU16(0);
        WriteU16(0);
        WriteU16((ushort)written.Count);
        WriteU16((ushort)written.Count);
        WriteU32((uint)cdSize);
        WriteU32((uint)cdStart);
        WriteU16(0);
        file.Flush(true);
    }

    private void WriteU16(ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        b[0] = (byte)v;
        b[1] = (byte)(v >> 8);
        file.Write(b);
    }

    private void WriteU32(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        b[0] = (byte)v;
        b[1] = (byte)(v >> 8);
        b[2] = (byte)(v >> 16);
        b[3] = (byte)(v >> 24);
        file.Write(b);
    }

    public void Dispose() => file.Dispose();
}
