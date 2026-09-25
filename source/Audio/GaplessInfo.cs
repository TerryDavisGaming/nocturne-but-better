using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace NocturneFlatScroll;

/// <summary>
/// What an MP3 file's first frame says about its timing: the Xing/Info (or VBRI) header frame,
/// which decoders play as a frame of silence, and LAME's encoder delay and padding.
/// </summary>
internal sealed class Mp3Info
{
    internal int Version;           // 1 = MPEG-1, 2 = MPEG-2, 25 = MPEG-2.5
    internal int Layer;
    internal int SampleRate;
    internal int SamplesPerFrame;
    internal string Tag = "";       // "Xing", "Info", "VBRI" or "" (no header frame)
    internal string Encoder = "";
    internal bool HasLame;          // encoder delay and padding are known
    internal long Frames = -1;      // audio frames after the header frame, if the tag says
    internal int Delay, Padding;
    // The delay field where LAME's part of a Xing/Info header keeps it, whatever encoder name comes
    // before it (osu!'s audio library reads it that way; see OsuAudio). 0 without that part.
    internal int RawDelay;
    // iTunes' gapless comment in the ID3 tag (Apple's and Windows' encoders write it instead of a
    // LAME tag): the encoder delay and the original length.
    internal long SmpbPriming = -1, SmpbLength = -1;

    private static readonly int[,] Bitrates =
    {
        // MPEG-1 layers I, II, III, then MPEG-2/2.5 layers I, II, III (kbit/s)
        { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 },
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },
        { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 },
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 },
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
    };

    /// <summary>Where the audio starts after any ID3v2 tags.</summary>
    internal static int SkipId3(byte[] b)
    {
        int p = 0;
        while (p + 10 <= b.Length && b[p] == 'I' && b[p + 1] == 'D' && b[p + 2] == '3' && b[p + 3] < 0xFF && b[p + 4] < 0xFF
               && (b[p + 6] | b[p + 7] | b[p + 8] | b[p + 9]) < 0x80)
        {
            int size = (b[p + 6] << 21) | (b[p + 7] << 14) | (b[p + 8] << 7) | b[p + 9];
            p += 10 + size + ((b[p + 5] & 0x10) != 0 ? 10 : 0); // a footer adds 10 bytes
        }
        return Math.Min(p, b.Length);
    }

    /// <summary>Whether the four bytes at <paramref name="p"/> are a valid MPEG audio frame header.</summary>
    internal static bool IsFrameHeader(byte[] b, int p, out int version, out int layer, out int rate, out int length)
    {
        version = layer = rate = length = 0;
        if (p < 0 || p + 4 > b.Length || b[p] != 0xFF || (b[p + 1] & 0xE0) != 0xE0) return false;
        int v = (b[p + 1] >> 3) & 3, l = (b[p + 1] >> 1) & 3, br = b[p + 2] >> 4, sr = (b[p + 2] >> 2) & 3;
        if (v == 1 || l == 0 || br == 15 || sr == 3) return false;
        version = v == 3 ? 1 : v == 2 ? 2 : 25;
        layer = 4 - l;
        rate = new[] { 44100, 48000, 32000 }[sr] / (version == 1 ? 1 : version == 2 ? 2 : 4);
        int kbps = Bitrates[(version == 1 ? 0 : 3) + layer - 1, br];
        int pad = (b[p + 2] >> 1) & 1;
        if (kbps == 0) length = 0; // free format
        else if (layer == 1) length = (12 * kbps * 1000 / rate + pad) * 4;
        else length = (layer == 3 && version != 1 ? 72 : 144) * kbps * 1000 / rate + pad;
        return true;
    }

    /// <summary>Reads the first MPEG audio frame, or returns null if none is found near the start.</summary>
    internal static Mp3Info? Parse(byte[] b)
    {
        int start = SkipId3(b);
        int limit = Math.Min(b.Length - 4, start + 256 * 1024);
        for (int p = start; p <= limit; p++)
        {
            if (b[p] != 0xFF || !IsFrameHeader(b, p, out int version, out int layer, out int rate, out int length)) continue;
            // A real frame is followed by another like it (or the end of the file).
            if (length > 0 && p + length + 4 <= b.Length)
            {
                if (!IsFrameHeader(b, p + length, out int v2, out int l2, out int r2, out _) || v2 != version || l2 != layer || r2 != rate)
                    continue;
            }
            var info = new Mp3Info
            {
                Version = version,
                Layer = layer,
                SampleRate = rate,
                SamplesPerFrame = layer == 1 ? 384 : layer == 3 && version != 1 ? 576 : 1152,
            };
            info.ReadTag(b, p, length);
            info.ReadSmpb(b, start);
            return info;
        }
        return null;
    }

    /// <summary>Finds "iTunSMPB" in the ID3 tag (a COMM or TXXX frame, Latin-1 or UTF-16) and reads its words.</summary>
    private void ReadSmpb(byte[] b, int tagEnd)
    {
        foreach (bool wide in new[] { false, true })
        {
            byte[] key = wide ? Encoding.Unicode.GetBytes("iTunSMPB") : Encoding.ASCII.GetBytes("iTunSMPB");
            int at = b.AsSpan(0, tagEnd).IndexOf(key);
            if (at < 0) continue;
            // The value follows the description: hex words separated by spaces, within a few hundred bytes.
            int from = at + key.Length, to = Math.Min(tagEnd, from + (wide ? 400 : 200));
            var text = new StringBuilder();
            for (int i = from; i < to; i++)
            {
                char c = (char)b[i];
                if (c == 0) continue; // terminators and the high bytes of UTF-16
                if (Uri.IsHexDigit(c) || c == ' ') text.Append(c);
                else if (text.Length > 0 && text.ToString().Trim().Length > 0) break;
            }
            var words = text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 4
                && long.TryParse(words[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long priming)
                && long.TryParse(words[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long length)
                && priming >= 0 && priming < 1 << 16)
            {
                SmpbPriming = priming;
                SmpbLength = length;
            }
            return;
        }
    }

    private void ReadTag(byte[] b, int frame, int frameLength)
    {
        int end = frameLength > 0 ? Math.Min(b.Length, frame + frameLength) : b.Length;
        bool mono = (b[frame + 3] >> 6) == 3;
        int side = Version == 1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
        // The Xing tag follows the side information; allow for a CRC before it.
        foreach (int at in new[] { frame + 4 + side, frame + 4 + side + 2 })
        {
            if (at + 8 > end) continue;
            string id = Encoding.ASCII.GetString(b, at, 4);
            if (id != "Xing" && id != "Info") continue;
            Tag = id;
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at + 4));
            int q = at + 8;
            if ((flags & 1) != 0 && q + 4 <= end) { Frames = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(q)); q += 4; }
            if ((flags & 2) != 0) q += 4;
            if ((flags & 4) != 0) q += 100;
            if ((flags & 8) != 0) q += 4;
            // LAME's extension: a 9-byte encoder name, then the delay and padding 21 bytes in (12 bits each).
            if (q + 24 <= end)
            {
                RawDelay = ((b[q + 21] << 16) | (b[q + 22] << 8) | b[q + 23]) >> 12;
                Encoder = Encoding.ASCII.GetString(b, q, 9).TrimEnd('\0', ' ');
                if (Encoder.StartsWith("LAME") || Encoder.StartsWith("Lavf") || Encoder.StartsWith("Lavc"))
                {
                    int v = (b[q + 21] << 16) | (b[q + 22] << 8) | b[q + 23];
                    Delay = v >> 12;
                    Padding = v & 0xFFF;
                    HasLame = true;
                }
            }
            return;
        }
        // Fraunhofer's VBRI header sits 32 bytes after the frame header, whatever the mode.
        int vbri = frame + 36;
        if (vbri + 18 <= end && Encoding.ASCII.GetString(b, vbri, 4) == "VBRI")
        {
            Tag = "VBRI";
            Frames = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(vbri + 14));
        }
    }
}

/// <summary>
/// The timing boxes of an MP4/M4A file's first audio track: the edit list, whose first entry says
/// how many priming samples to skip (AAC encoders add 1024 or 2112) and how long the audio really
/// is, and iTunes' iTunSMPB tag, which says the same for files without an edit list.
/// </summary>
internal sealed class Mp4Info
{
    internal uint MovieTimescale, MediaTimescale;
    internal long MediaDuration = -1;    // the audio track's length, media timescale
    internal string Codec = "";
    internal bool HasEdit;
    internal long MediaTime = -1;        // first non-empty edit, media timescale
    internal long SegmentDuration = -1;  // its length, movie timescale
    internal int Edits;                  // non-empty edits
    internal long SmpbPriming = -1, SmpbPadding = -1, SmpbLength = -1;
    private bool audioTrackDone;

    internal static Mp4Info Parse(byte[] b)
    {
        var info = new Mp4Info();
        try { info.Walk(b, 0, b.Length, ""); }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException)
        {
            // A damaged box tree: use whatever was read before it.
        }
        return info;
    }

    private static (long Size, int Header, string Type) Box(byte[] b, int p, int end)
    {
        if (p + 8 > end) return (0, 0, "");
        long size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
        string type = Encoding.ASCII.GetString(b, p + 4, 4);
        int header = 8;
        if (size == 1)
        {
            if (p + 16 > end) return (0, 0, "");
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p + 8));
            header = 16;
        }
        else if (size == 0) size = end - p;
        if (size < header || p + size > end) return (0, 0, "");
        return (size, header, type);
    }

    private void Walk(byte[] b, int p, int end, string path)
    {
        // Real files nest a handful of levels; a damaged one mustn't recurse without end.
        if (path.Length > 64) return;
        while (p + 8 <= end)
        {
            var (size, header, type) = Box(b, p, end);
            if (size == 0) return;
            int body = p + header, bodyEnd = (int)(p + size);
            switch (type)
            {
                case "moov":
                case "udta":
                case "ilst":
                case "edts":
                case "mdia":
                case "minf":
                case "stbl":
                    Walk(b, body, bodyEnd, path + "/" + type);
                    break;
                case "trak":
                    if (!audioTrackDone && IsSoundTrack(b, body, bodyEnd))
                    {
                        Walk(b, body, bodyEnd, path + "/trak");
                        audioTrackDone = true;
                    }
                    break;
                case "meta":
                    // ISO meta boxes carry a version and flags; QuickTime's don't.
                    Walk(b, body + 8 <= bodyEnd && Encoding.ASCII.GetString(b, body + 4, 4) == "hdlr" ? body : body + 4, bodyEnd, path + "/meta");
                    break;
                case "mvhd":
                    MovieTimescale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(body + (b[body] == 1 ? 20 : 12)));
                    break;
                case "mdhd":
                    if (path.EndsWith("/trak/mdia"))
                    {
                        bool v1 = b[body] == 1;
                        MediaTimescale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(body + (v1 ? 20 : 12)));
                        MediaDuration = v1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(body + 24)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(body + 16));
                    }
                    break;
                case "stsd":
                    if (path.EndsWith("/stbl") && body + 16 <= bodyEnd) Codec = Encoding.ASCII.GetString(b, body + 12, 4);
                    break;
                case "elst":
                    if (path.EndsWith("/trak/edts")) ReadEditList(b, body, bodyEnd);
                    break;
                case "----":
                    ReadFreeformTag(b, body, bodyEnd);
                    break;
            }
            p = bodyEnd;
        }
    }

    private static bool IsSoundTrack(byte[] b, int p, int end)
    {
        // trak/mdia/hdlr: version and flags, pre_defined, then the handler type.
        for (int q = p; q + 8 <= end;)
        {
            var (size, header, type) = Box(b, q, end);
            if (size == 0) return false;
            if (type == "mdia")
            {
                for (int r = q + header; r + 8 <= q + size;)
                {
                    var (s2, h2, t2) = Box(b, r, (int)(q + size));
                    if (s2 == 0) return false;
                    if (t2 == "hdlr" && r + h2 + 12 <= r + s2) return Encoding.ASCII.GetString(b, r + h2 + 8, 4) == "soun";
                    r += (int)s2;
                }
                return false;
            }
            q += (int)size;
        }
        return false;
    }

    private void ReadEditList(byte[] b, int p, int end)
    {
        int version = b[p];
        uint count = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p + 4));
        int q = p + 8;
        int entry = version == 1 ? 20 : 12;
        HasEdit = true;
        for (uint i = 0; i < count && q + entry <= end; i++, q += entry)
        {
            long duration = version == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(q)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(q));
            long mediaTime = version == 1 ? BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(q + 8)) : BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(q + 4));
            if (mediaTime < 0) continue; // an empty edit (a delay before the audio)
            Edits++;
            if (MediaTime < 0)
            {
                MediaTime = mediaTime;
                SegmentDuration = duration;
            }
        }
    }

    // iTunes' gapless tag: "----" with mean "com.apple.iTunes", name "iTunSMPB", and a data value of
    // hex words: 0, priming, padding, original length.
    private void ReadFreeformTag(byte[] b, int p, int end)
    {
        string name = "", value = "";
        for (int q = p; q + 8 <= end;)
        {
            var (size, header, type) = Box(b, q, end);
            if (size == 0) return;
            int body = q + header, bodyEnd = (int)(q + size);
            if (type == "name" && body + 4 <= bodyEnd) name = Encoding.UTF8.GetString(b, body + 4, bodyEnd - body - 4);
            if (type == "data" && body + 8 <= bodyEnd) value = Encoding.UTF8.GetString(b, body + 8, bodyEnd - body - 8);
            q = bodyEnd;
        }
        if (name != "iTunSMPB") return;
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 4) return;
        if (long.TryParse(words[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long priming)
            && long.TryParse(words[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long padding)
            && long.TryParse(words[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long length))
        {
            SmpbPriming = priming;
            SmpbPadding = padding;
            SmpbLength = length;
        }
    }
}
