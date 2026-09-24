using System.Buffers.Binary;

namespace NocturneFlatScroll;

/// <summary>One packet of an Ogg logical stream.</summary>
internal readonly struct OggPacket
{
    internal readonly byte[] Data;
    internal readonly int Offset, Length;
    /// <summary>The page's granule position on the last packet that ends on a page, otherwise -1.</summary>
    internal readonly long Granule;
    /// <summary>The last packet of the stream (it ends on the end-of-stream page).</summary>
    internal readonly bool EndOfStream;
    /// <summary>Pages of this stream were lost or damaged just before this packet.</summary>
    internal readonly bool AfterGap;

    internal OggPacket(byte[] data, int offset, int length, long granule, bool endOfStream, bool afterGap)
    {
        Data = data; Offset = offset; Length = length; Granule = granule; EndOfStream = endOfStream; AfterGap = afterGap;
    }
}

/// <summary>
/// Splits an Ogg file into the packets of one logical stream, like libogg: pages are found by
/// their "OggS" capture pattern, and a page whose CRC doesn't match is skipped. The stream is the
/// first one whose first packet the caller accepts; other multiplexed streams (a video track, say)
/// are ignored, and so is anything chained after the stream's end-of-stream page.
/// </summary>
internal sealed class OggReader
{
    private static readonly uint[] CrcTable = MakeCrcTable();

    private readonly byte[] data;

    /// <summary>The first packet of every stream seen before one was accepted (for error messages).</summary>
    internal readonly List<byte[]> OtherStreams = new();
    internal readonly List<OggPacket> Packets = new();
    /// <summary>The granule position of the stream's last page that has one, or -1.</summary>
    internal long LastGranule = -1;
    internal bool SawEndOfStream;
    internal int DamagedPages, Gaps;

    internal OggReader(byte[] data, Func<byte[], int, int, bool> accept)
    {
        this.data = data;
        Read(accept);
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            table[i] = r;
        }
        return table;
    }

    private uint PageCrc(int at, int headerLength, int bodyLength)
    {
        uint crc = 0;
        int end = at + headerLength + bodyLength;
        for (int i = at; i < end; i++)
        {
            byte b = i >= at + 22 && i < at + 26 ? (byte)0 : data[i]; // the CRC field counts as zero
            crc = (crc << 8) ^ CrcTable[((crc >> 24) & 0xFF) ^ b];
        }
        return crc;
    }

    private int FindCapture(int from)
    {
        for (int i = from; i + 4 <= data.Length; i++)
        {
            i = Array.IndexOf(data, (byte)'O', i);
            if (i < 0 || i + 4 > data.Length) return -1;
            if (data[i + 1] == 'g' && data[i + 2] == 'g' && data[i + 3] == 'S') return i;
        }
        return -1;
    }

    private void Read(Func<byte[], int, int, bool> accept)
    {
        uint? serial = null;
        uint expectedSequence = 0;
        bool haveSequence = false;
        var pending = new List<(int Offset, int Length)>(); // segments of a packet that continues on the next page
        bool pendingAfterGap = false;
        bool gap = false;
        int pos = 0;

        while (true)
        {
            int at = FindCapture(pos);
            if (at < 0 || at + 27 > data.Length) break;
            int segments = data[at + 26];
            int headerLength = 27 + segments;
            if (data[at + 4] != 0 || at + headerLength > data.Length)
            {
                pos = at + 1;
                continue;
            }
            int bodyLength = 0;
            for (int i = 0; i < segments; i++) bodyLength += data[at + 27 + i];
            if (at + headerLength + bodyLength > data.Length)
            {
                // A page cut off by the end of the file: what came before still plays.
                break;
            }
            if (PageCrc(at, headerLength, bodyLength) != BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 22)))
            {
                DamagedPages++;
                pos = at + 1;
                continue;
            }
            pos = at + headerLength + bodyLength;

            int flags = data[at + 5];
            long granule = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(at + 6));
            uint pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 14));
            uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 18));
            bool continued = (flags & 1) != 0, bos = (flags & 2) != 0, eos = (flags & 4) != 0;

            if (serial == null)
            {
                // Streams are identified by their first packet, alone on their first page.
                if (!bos) continue;
                int firstLength = 0;
                for (int i = 0; i < segments; i++)
                {
                    firstLength += data[at + 27 + i];
                    if (data[at + 27 + i] < 255) break;
                }
                if (!accept(data, at + headerLength, firstLength))
                {
                    OtherStreams.Add(data.AsSpan(at + headerLength, firstLength).ToArray());
                    continue;
                }
                serial = pageSerial;
            }
            else if (pageSerial != serial)
            {
                continue;
            }

            if (haveSequence && sequence != expectedSequence)
            {
                // Pages went missing: the packet that was continuing is lost.
                Gaps++;
                gap = true;
                pending.Clear();
            }
            haveSequence = true;
            expectedSequence = sequence + 1;

            int body = at + headerLength;
            int seg = 0;
            if (continued && pending.Count == 0)
            {
                // The start of this packet was lost with an earlier page: skip its tail (which may
                // go on past this page).
                while (seg < segments && data[at + 27 + seg] == 255) body += data[at + 27 + seg++];
                if (seg < segments) body += data[at + 27 + seg++];
                gap = true;
            }
            else if (!continued && pending.Count > 0)
            {
                // The previous page promised more of a packet that never came.
                pending.Clear();
                gap = true;
            }

            // The last packet that ends on this page carries the page's granule position.
            int lastEnd = -1;
            for (int i = segments - 1; i >= seg; i--)
                if (data[at + 27 + i] < 255) { lastEnd = i; break; }

            int start = body, size = 0;
            for (int i = seg; i < segments; i++)
            {
                int lace = data[at + 27 + i];
                size += lace;
                if (lace == 255) continue;
                bool last = i == lastEnd;
                AddPacket(pending, start, size, last ? granule : -1, last && eos, gap || pendingAfterGap);
                gap = false;
                pendingAfterGap = false;
                pending.Clear();
                start += size;
                size = 0;
            }
            if (seg < segments && data[at + 27 + segments - 1] == 255)
            {
                // The page ends inside a packet that continues on the next page.
                if (pending.Count == 0) pendingAfterGap = gap;
                pending.Add((start, size));
                gap = false;
            }
            if (lastEnd >= 0 && granule != -1) LastGranule = granule;
            if (eos)
            {
                SawEndOfStream = true;
                break;
            }
        }
    }

    private void AddPacket(List<(int Offset, int Length)> pending, int start, int size, long granule, bool eos, bool afterGap)
    {
        if (pending.Count == 0)
        {
            Packets.Add(new OggPacket(data, start, size, granule, eos, afterGap));
            return;
        }
        // A packet spread over pages is copied into one piece.
        int total = size;
        foreach (var part in pending) total += part.Length;
        var joined = new byte[total];
        int o = 0;
        foreach (var part in pending)
        {
            Buffer.BlockCopy(data, part.Offset, joined, o, part.Length);
            o += part.Length;
        }
        Buffer.BlockCopy(data, start, joined, o, size);
        Packets.Add(new OggPacket(joined, 0, total, granule, eos, afterGap));
    }
}
