using System.Buffers.Binary;
using System.Text;

namespace NocturneFlatScroll;

/// <summary>What a video file's container says, and whether the game can play it.</summary>
internal sealed class VideoFacts
{
    /// <summary>"mp4" (also .m4v and .mov) or "webm" (also Matroska).</summary>
    internal string Container = "?";
    internal string Codec = "";
    internal string DocType = "";
    internal int Width, Height;
    /// <summary>H.264's profile from avcC; -1 when not known.</summary>
    internal int Profile = -1;
    /// <summary>How long it plays; 0 when the file doesn't say (browser recordings, some fragmented MP4s).</summary>
    internal double Seconds;
    internal bool HasVideo, Alpha;
    /// <summary>The part that describes the tracks was read (an MP4's moov, a WebM's Tracks).</summary>
    internal bool IndexFound;
    /// <summary>
    /// How far an MP4 asks to be turned when it's shown (0, 90, 180 or 270 degrees), from its video
    /// track's matrix: a phone video recorded upright is stored sideways with 90. The game's video
    /// player shows the stored frames as they are.
    /// </summary>
    internal int Rotation;
    /// <summary>An MP4's movie time scale, for a fragmented file's length (mehd).</summary>
    internal double TimeScale;
    /// <summary>A fragmented MP4 (its moov has mvex), and whether it said its whole length (mehd).</summary>
    internal bool Fragmented, FragmentsLength;

    internal string CodecName => Codec switch
    {
        "avc1" or "avc3" => "H.264",
        "hvc1" or "hev1" => "H.265",
        "av01" or "V_AV1" => "AV1",
        "vp09" or "V_VP9" => "VP9",
        "vp08" or "V_VP8" => "VP8",
        "mp4v" => "MPEG-4",
        "apcn" or "apch" or "apcs" or "apco" or "ap4h" or "ap4x" => "ProRes",
        "" => "an unknown codec",
        _ => Codec,
    };

    /// <summary>
    /// Null when the game can play it on any Windows PC; otherwise why not, written to follow the
    /// file's name ("art/x.mp4 uses H.265, ...").
    /// </summary>
    internal string? Problem()
    {
        if (!IndexFound) return "isn't a video the game can play";
        if (!HasVideo) return "has no video in it";
        string? codec = CodecProblem();
        if (codec != null) return codec;
        if (Rotation == 180) return "is stored upside down with a note to turn it; the game can't turn videos, so save it the right way up first (for example with ffmpeg)";
        if (Rotation != 0) return "is stored sideways with a note to turn it (as upright phone videos are); the game can't turn videos, so save it upright first (for example with ffmpeg)";
        return null;
    }

    private string? CodecProblem()
    {
        // Unity plays .webm with its own decoder, which only knows VP8.
        if (Container == "webm")
            return Codec == "V_VP8" ? null : $"uses {CodecName}, which the game can't play (WebM videos must use VP8, or use an H.264 MP4)";
        if (Codec is "avc1" or "avc3")
            return Profile is 66 or 77 or 88 or 100 or -1 ? null
                : $"is a {(Profile == 110 ? "10-bit" : Profile == 122 ? "4:2:2" : Profile == 244 ? "4:4:4" : "profile " + Profile)} H.264 video, which Windows can't play (re-save it as 8-bit 4:2:0, yuv420p)";
        // MPEG-4 part 2 plays through Windows' own decoder.
        if (Codec == "mp4v") return null;
        return $"uses {CodecName}, which the game can't play (use an H.264 MP4 or a VP8 WebM)";
    }
}

/// <summary>
/// Reads a video's container headers without decoding, to tell the player before Unity tries it
/// whether the game can play it. Unity picks its video backend by extension: .webm goes through
/// its own decoder, which only accepts VP8; .mp4/.m4v/.mov go through Windows Media Foundation,
/// whose built-in H.264 decoder takes 8-bit 4:2:0 only (HEVC, AV1 and VP9 need Store extensions
/// that many PCs lack). Bounded: boxes and elements nest at most 8 deep, and a stream read keeps
/// only an MP4's index (at most 16 MB) or a WebM's first 1 MB.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class VideoProbe
{
    private const int MaxIndexBytes = 16 * 1024 * 1024;
    private const int WebmHeadBytes = 1024 * 1024;
    private const int MaxTopBoxes = 10000;

    internal static bool IsMp4(ReadOnlySpan<byte> d) =>
        d.Length >= 8 && (Ascii(d, 4) is "ftyp" or "moov" or "mdat" or "wide" or "free" or "skip");

    internal static bool IsEbml(ReadOnlySpan<byte> d) =>
        d.Length >= 4 && d[0] == 0x1A && d[1] == 0x45 && d[2] == 0xDF && d[3] == 0xA3;

    private static string Ascii(ReadOnlySpan<byte> d, int at) =>
        at + 4 <= d.Length ? Encoding.ASCII.GetString(d.Slice(at, 4)) : "";

    /// <summary>Reads the bytes given (a whole file, or only its start).</summary>
    internal static VideoFacts Read(byte[] d)
    {
        var f = new VideoFacts();
        if (IsMp4(d))
        {
            f.Container = "mp4";
            WalkBoxes(d, 0, d.Length, 0, f, false);
        }
        else if (IsEbml(d))
        {
            f.Container = "webm";
            WalkEbml(d, 0, d.Length, 0, f, 1_000_000);
            f.IndexFound = f.HasVideo;
        }
        return f;
    }

    /// <summary>
    /// Reads a whole file through a seekable stream: an MP4's top-level boxes by their sizes, with
    /// only moov read in full, wherever it is in the file.
    /// </summary>
    internal static VideoFacts Read(Stream s)
    {
        var head = new byte[16];
        int got = ReadFully(s, head, 16);
        s.Position = 0;
        if (IsEbml(head.AsSpan(0, got)))
        {
            var bytes = new byte[(int)Math.Min(s.Length, WebmHeadBytes)];
            ReadFully(s, bytes, bytes.Length);
            return Read(bytes);
        }
        var f = new VideoFacts();
        if (!IsMp4(head.AsSpan(0, got))) return f;
        f.Container = "mp4";
        long at = 0, length = s.Length;
        for (int i = 0; i < MaxTopBoxes && at + 8 <= length; i++)
        {
            s.Position = at;
            if (ReadFully(s, head, 16) < 8) break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(head);
            string type = Encoding.ASCII.GetString(head, 4, 4);
            int header = 8;
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(8)); header = 16; }
            else if (size == 0) size = length - at;
            if (size < header || size > length - at) break;   // cut off or broken: what was read stands
            if (type == "moov")
            {
                if (size > MaxIndexBytes) return f;           // not found, as far as the game is concerned
                var moov = new byte[(int)size];
                s.Position = at;
                if (ReadFully(s, moov, moov.Length) < moov.Length) break;
                WalkBoxes(moov, 0, moov.Length, 0, f, false);
                return f;
            }
            at += size;
        }
        return f;
    }

    private static int ReadFully(Stream s, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buffer, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    // ---- ISO BMFF (mp4, m4v, mov) ----

    private static uint U32(byte[] d, int p) => (uint)(d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]);

    private static int I32(byte[] d, int p) => (int)U32(d, p);

    /// <summary>The turn a track matrix asks for, from its first row (a = cos, b = sin), to the nearest quarter.</summary>
    internal static int RotationOf(int a, int b)
    {
        if (a == 0 && b == 0) return 0;
        double degrees = Math.Atan2(b, a) * 180 / Math.PI;
        return ((int)Math.Round(degrees / 90) * 90 % 360 + 360) % 360;
    }

    private static void WalkBoxes(byte[] d, int start, int end, int depth, VideoFacts f, bool videoTrack)
    {
        int p = start;
        while (p + 8 <= end && depth < 8)
        {
            long size = U32(d, p);
            string type = Encoding.ASCII.GetString(d, p + 4, 4);
            int header = 8;
            if (size == 1) { if (p + 16 > end) return; size = (long)((ulong)U32(d, p + 8) << 32 | U32(d, p + 12)); header = 16; }
            else if (size == 0) size = end - p;
            bool whole = size >= header && p + size <= end;
            if (!whole) size = end - p;                        // cut off: read what's there
            int body = p + header, next = (int)Math.Min(end, p + size);
            if (body > next) return;
            switch (type)
            {
                case "moov":
                    if (whole) f.IndexFound = true;
                    WalkBoxes(d, body, next, depth + 1, f, videoTrack);
                    // A fragmented file's mvhd covers only what's in moov (often nothing, or the first
                    // fragment): without mehd its whole length isn't known.
                    if (f.Fragmented && !f.FragmentsLength) f.Seconds = 0;
                    break;
                case "mdia": case "minf": case "stbl": case "edts":
                    WalkBoxes(d, body, next, depth + 1, f, videoTrack);
                    break;
                case "mvex":
                    f.Fragmented = true;
                    WalkBoxes(d, body, next, depth + 1, f, videoTrack);
                    break;
                case "trak":
                    // A track is video when its handler says "vide".
                    if (!f.HasVideo && FindHandler(d, body, next, depth + 1) == "vide") WalkBoxes(d, body, next, depth + 1, f, true);
                    break;
                case "mvhd":
                    {
                        int v = d[body];
                        if (v == 1 && body + 32 <= next)
                        {
                            double scale = U32(d, body + 20);
                            double duration = (double)((ulong)U32(d, body + 24) << 32 | U32(d, body + 28));
                            f.TimeScale = scale;
                            if (scale > 0) f.Seconds = duration / scale;
                        }
                        else if (v == 0 && body + 20 <= next)
                        {
                            double scale = U32(d, body + 12);
                            f.TimeScale = scale;
                            if (scale > 0) f.Seconds = U32(d, body + 16) / scale;
                        }
                        break;
                    }
                case "mehd":
                    {
                        // A fragmented MP4's whole length (mvhd, which comes first, gives its time scale).
                        int v = d[body];
                        double duration = v == 1 && body + 12 <= next ? (double)((ulong)U32(d, body + 4) << 32 | U32(d, body + 8))
                            : v == 0 && body + 8 <= next ? U32(d, body + 4) : 0;
                        if (f.TimeScale > 0 && duration > 0)
                        {
                            f.Seconds = duration / f.TimeScale;
                            f.FragmentsLength = true;
                        }
                        break;
                    }
                case "tkhd":
                    if (videoTrack)
                    {
                        // Version and flags, the times (v1: 32 bytes, v0: 20), 16 more, then the 3 x 3
                        // matrix of 16.16 numbers: a b u / c d v / x y w.
                        int m = body + 4 + (d[body] == 1 ? 32 : 20) + 16;
                        if (m + 20 <= next) f.Rotation = RotationOf(I32(d, m), I32(d, m + 4));
                    }
                    break;
                case "stsd":
                    if (videoTrack && body + 16 + 78 <= next)
                    {
                        int e = body + 8;                           // version, flags and entry count come first
                        f.HasVideo = true;
                        f.Codec = Encoding.ASCII.GetString(d, e + 4, 4);
                        f.Width = d[e + 32] << 8 | d[e + 33];
                        f.Height = d[e + 34] << 8 | d[e + 35];
                        int entryEnd = (int)Math.Min(next, e + (long)U32(d, e));
                        // The sample entry's own boxes start after its 86-byte fixed part.
                        for (int q = e + 86; q + 8 <= entryEnd;)
                        {
                            uint bs = U32(d, q);
                            if (Encoding.ASCII.GetString(d, q + 4, 4) == "avcC" && q + 10 <= entryEnd) f.Profile = d[q + 9];
                            if (bs < 8) break;
                            q += (int)Math.Min(bs, (uint)(entryEnd - q + 8));
                        }
                    }
                    break;
            }
            p = next;
        }
    }

    private static string? FindHandler(byte[] d, int start, int end, int depth)
    {
        int p = start;
        while (p + 8 <= end && depth < 8)
        {
            long size = U32(d, p);
            string type = Encoding.ASCII.GetString(d, p + 4, 4);
            if (size < 8 || p + size > end) size = end - p;
            if (type == "hdlr" && p + 20 <= end) return Encoding.ASCII.GetString(d, p + 16, 4);
            if (type == "mdia")
            {
                var found = FindHandler(d, p + 8, (int)(p + size), depth + 1);
                if (found != null) return found;
            }
            p += (int)size;
        }
        return null;
    }

    // ---- EBML (webm, mkv) ----

    private static bool Vint(byte[] d, ref int p, int end, bool keepMarker, out long value, out bool unknown)
    {
        value = 0;
        unknown = false;
        if (p >= end) return false;
        int first = d[p], len = 1;
        while (len <= 8 && (first & (0x80 >> (len - 1))) == 0) len++;
        if (len > 8 || p + len > end) return false;
        long v = keepMarker ? first : first & (0xFF >> len);
        bool allOnes = (first & (0xFF >> len)) == (0xFF >> len);
        for (int i = 1; i < len; i++) { v = v << 8 | d[p + i]; allOnes &= d[p + i] == 0xFF; }
        p += len;
        value = v;
        unknown = !keepMarker && allOnes;
        return true;
    }

    private static void WalkEbml(byte[] d, int start, int end, int depth, VideoFacts f, int budget)
    {
        int p = start;
        long trackType = 0;
        string? codec = null;
        int w = 0, h = 0;
        bool alpha = false;
        double scale = 1_000_000, duration = 0;
        while (p < end && depth < 8 && budget-- > 0)
        {
            if (!Vint(d, ref p, end, true, out long id, out _)) return;
            if (!Vint(d, ref p, end, false, out long size, out bool unknown)) return;
            int body = p;
            int next = unknown || size < 0 || size > end - p ? end : (int)(p + size);
            switch (id)
            {
                case 0x1A45DFA3: // EBML header
                    WalkEbml(d, body, next, depth + 1, f, budget);
                    break;
                case 0x4282: // DocType
                    f.DocType = Encoding.ASCII.GetString(d, body, Math.Min(16, next - body)).TrimEnd('\0');
                    break;
                case 0x18538067: case 0x1654AE6B: case 0x1549A966: // Segment, Tracks, Info
                    WalkEbml(d, body, next, depth + 1, f, budget);
                    if (f.HasVideo && id == 0x1654AE6B) return;
                    break;
                case 0x1F43B675: // Cluster: in files that play, the tracks come first
                    if (f.HasVideo) return;
                    break;
                case 0xAE: // TrackEntry
                    WalkEbml(d, body, next, depth + 1, f, budget);
                    break;
                case 0x83: trackType = Uint(d, body, next); break;
                case 0x86: codec = Encoding.ASCII.GetString(d, body, Math.Min(32, next - body)).TrimEnd('\0'); break;
                case 0xE0: // Video
                    for (int q = body; q < next;)
                    {
                        if (!Vint(d, ref q, next, true, out long cid, out _) || !Vint(d, ref q, next, false, out long cs, out _)) break;
                        int ce = cs < 0 || cs > next - q ? next : (int)(q + cs);
                        if (cid == 0xB0) w = (int)Math.Min(int.MaxValue, Uint(d, q, ce));
                        else if (cid == 0xBA) h = (int)Math.Min(int.MaxValue, Uint(d, q, ce));
                        else if (cid == 0x53C0) alpha = Uint(d, q, ce) == 1;
                        q = ce;
                    }
                    break;
                case 0x2AD7B1: scale = Uint(d, body, next); break;
                case 0x4489:
                    duration = next - body == 4 ? BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(body, 4))
                        : next - body == 8 ? BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(body, 8)) : 0;
                    break;
            }
            p = next;
        }
        if (trackType == 1 && codec != null && !f.HasVideo)
        {
            f.HasVideo = true;
            f.Codec = codec;
            f.Width = w;
            f.Height = h;
            f.Alpha = alpha;
        }
        if (duration > 0 && double.IsFinite(duration) && scale > 0) f.Seconds = duration * scale / 1e9;
    }

    private static long Uint(byte[] d, int s, int e)
    {
        long v = 0;
        for (int i = s; i < e && i < s + 8; i++) v = v << 8 | d[i];
        return v;
    }
}
