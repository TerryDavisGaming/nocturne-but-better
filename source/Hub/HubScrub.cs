using System.Text;
using System.Text.RegularExpressions;

namespace NocturneFlatScroll;

/// <summary>
/// The privacy part of building an upload (DESIGN-HUB 3.6 step 1): picture metadata is dropped
/// from the copies that go into the package (every JPEG APPn but JFIF, ICC and Adobe, comments, and
/// anything after the picture's end; PNG eXIf/tEXt/iTXt/zTXt and anything after IEND; GIF comments,
/// XMP and anything after the trailer), without ever re-encoding the picture, and the text files,
/// the songs' tags and the rest of the songs outside their sound are searched for Windows user
/// paths and the Windows user name. The player's own files are never changed.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class HubScrub
{
    /// <summary>
    /// A picture without its metadata: the same bytes otherwise, or the bytes as they are when there
    /// was none. Null when the file's structure can't be walked (it isn't sent then).
    /// </summary>
    internal static byte[]? Picture(byte[] bytes, out bool changed)
    {
        changed = false;
        return MediaSniff.TypeOf(bytes) switch
        {
            MediaType.Jpeg => Jpeg(bytes, out changed),
            MediaType.Png => Png(bytes, out changed),
            MediaType.Gif => Gif(bytes, out changed),
            _ => null,
        };
    }

    /// <summary>
    /// A JPEG with only what draws it: its frame, tables and scans byte for byte, JFIF (APP0), an ICC
    /// colour profile (APP2 "ICC_PROFILE") and Adobe's colour marker (APP14). Every other APPn (EXIF,
    /// XMP, IPTC, MPF's list of extra pictures, maker notes) and comments are dropped, between scans
    /// too, and so is everything after the picture's end (EOI): a phone's motion-photo video, extra
    /// pictures, vendor trailers. A JPEG cut short before its EOI is kept as far as it goes.
    /// </summary>
    internal static byte[]? Jpeg(byte[] b, out bool changed)
    {
        changed = false;
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return null;
        using var output = new MemoryStream(b.Length);
        output.Write(b, 0, 2);
        int at = 2;
        while (true)
        {
            if (at + 2 > b.Length || b[at] != 0xFF) return null;
            int marker = b[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }
            if (marker == 0xD9)
            {
                // The picture's end: anything after it isn't part of the picture.
                output.Write(b, at, 2);
                if (at + 2 < b.Length) changed = true;
                return output.ToArray();
            }
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                output.Write(b, at, 2);
                at += 2;
                continue;
            }
            if (at + 4 > b.Length) return null;
            int length = b[at + 2] << 8 | b[at + 3];
            if (length < 2 || at + 2 + length > b.Length) return null;
            if (marker == 0xFE || (marker >= 0xE0 && marker <= 0xEF && !KeptApp(b, at, marker, length))) changed = true;
            else output.Write(b, at, 2 + length);
            at += 2 + length;
            if (marker != 0xDA) continue;
            // The scan's picture data, byte for byte, up to the next marker (a stuffed 00, a restart marker
            // and fill bytes are part of it); then segments again (tables, the next scan, or the end).
            int scan = at;
            while (true)
            {
                int ff = Array.IndexOf(b, (byte)0xFF, at);
                if (ff < 0 || ff + 1 >= b.Length)
                {
                    // Cut short without its end: kept as it is.
                    output.Write(b, scan, b.Length - scan);
                    return output.ToArray();
                }
                int next = b[ff + 1];
                if (next == 0x00 || (next >= 0xD0 && next <= 0xD7)) at = ff + 2;
                else if (next == 0xFF) at = ff + 1;
                else
                {
                    output.Write(b, scan, ff - scan);
                    at = ff;
                    break;
                }
            }
        }
    }

    // The APPn segments a JPEG keeps: JFIF, an ICC colour profile, Adobe's colour marker.
    private static bool KeptApp(byte[] b, int at, int marker, int length) => marker switch
    {
        0xE0 => true,
        0xE2 => length >= 2 + 12 && Ascii(b, at + 4, "ICC_PROFILE\0"),
        0xEE => length >= 2 + 5 && Ascii(b, at + 4, "Adobe"),
        _ => false,
    };

    private static readonly HashSet<string> PngDropped = new(StringComparer.Ordinal) { "eXIf", "tEXt", "iTXt", "zTXt" };

    /// <summary>A PNG without its eXIf, tEXt, iTXt and zTXt chunks (and nothing after IEND).</summary>
    internal static byte[]? Png(byte[] b, out bool changed)
    {
        changed = false;
        if (b.Length < 8) return null;
        using var output = new MemoryStream(b.Length);
        output.Write(b, 0, 8);
        int at = 8;
        while (true)
        {
            if (at + 12 > b.Length) return null;
            long length = (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
            string type = Encoding.ASCII.GetString(b, at + 4, 4);
            if (length > int.MaxValue || at + 12 + length > b.Length) return null;
            int whole = 12 + (int)length;
            if (PngDropped.Contains(type)) changed = true;
            else output.Write(b, at, whole);
            at += whole;
            if (type == "IEND")
            {
                if (at < b.Length) changed = true;
                return output.ToArray();
            }
        }
    }

    /// <summary>A GIF without comment extensions and XMP application extensions; its frames as they are.</summary>
    internal static byte[]? Gif(byte[] b, out bool changed)
    {
        changed = false;
        if (b.Length < 13) return null;
        using var output = new MemoryStream(b.Length);
        int at = 13;
        if ((b[10] & 0x80) != 0) at += 3 << ((b[10] & 7) + 1);
        if (at > b.Length) return null;
        output.Write(b, 0, at);
        while (at < b.Length)
        {
            int start = at;
            byte kind = b[at];
            if (kind == 0x3B)
            {
                output.WriteByte(0x3B);
                if (at + 1 < b.Length) changed = true;
                return output.ToArray();
            }
            if (kind == 0x21)
            {
                if (at + 2 > b.Length) return null;
                byte label = b[at + 1];
                at += 2;
                bool drop = label == 0xFE;
                if (label == 0xFF && at + 12 <= b.Length && b[at] == 11 && Encoding.ASCII.GetString(b, at + 1, 11) == "XMP DataXMP") drop = true;
                int end = SkipBlocks(b, at);
                if (end < 0) return null;
                if (drop) changed = true;
                else output.Write(b, start, end - start);
                at = end;
            }
            else if (kind == 0x2C)
            {
                if (at + 10 > b.Length) return null;
                int packed = b[at + 9];
                at += 10;
                if ((packed & 0x80) != 0) at += 3 << ((packed & 7) + 1);
                at += 1;   // LZW minimum code size
                if (at > b.Length) return null;
                int end = SkipBlocks(b, at);
                if (end < 0) return null;
                output.Write(b, start, end - start);
                at = end;
            }
            else return null;
        }
        return null;
    }

    // Past a chain of sub-blocks (each a length byte and that many bytes, ending with 0); -1 when cut short.
    private static int SkipBlocks(byte[] b, int at)
    {
        while (at < b.Length)
        {
            int n = b[at];
            at += 1 + n;
            if (n == 0) return at;
        }
        return -1;
    }

    // ---- what might say who the player is ----------------------------------------------------------

    /// <summary>A Windows user path ("C:\Users\", "C:\\Users\\" in JSON, or with slashes).</summary>
    private static readonly Regex UsersPath = new(@"[A-Za-z]:(\\\\|\\|/)+Users(\\\\|\\|/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The first line of a text file (a .json or .sm going into the package) that holds a Windows
    /// user path or "\Users\&lt;user name&gt;", as "line N", or null.
    /// </summary>
    internal static string? TextProblem(string text, string userName)
    {
        var named = UserPathPattern(userName);
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (UsersPath.IsMatch(lines[i]) || (named != null && named.IsMatch(lines[i]))) return $"line {i + 1}";
        return null;
    }

    private static Regex? UserPathPattern(string userName) =>
        string.IsNullOrWhiteSpace(userName) ? null
            : new Regex(@"(\\\\|\\|/)Users(\\\\|\\|/)+" + Regex.Escape(userName.Trim()), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Whether a tag's text holds a user path or the Windows user name (a whole word, when it has 3 or more letters).</summary>
    internal static bool Names(string text, string userName)
    {
        if (UsersPath.IsMatch(text)) return true;
        string name = userName.Trim();
        if (name.Length < 3) return false;
        return Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Whether a song file holds a Windows user path (or "\Users\&lt;user name&gt;") anywhere but its
    /// sound, where the tags above don't look: every WAV chunk but "data" (XMP in "_PMX", "bext",
    /// "iXML", "id3 ", LIST), the whole ID3v2 tag at the start (an XMP PRIV frame, GEOB, APIC and the
    /// rest) and the tags at the end (ID3v1, APE, Lyrics3), or an Ogg file's three header packets.
    /// Editors such as Premiere and Audition write the project's and the source's paths there. Each
    /// part is searched as Latin-1 (which covers UTF-8's ASCII) and as UTF-16 either way round. The
    /// user name alone isn't searched here: a short one would turn up in random bytes.
    /// </summary>
    internal static bool SongHasUserPath(byte[] b, string userName)
    {
        var named = UserPathPattern(userName);
        try
        {
            if (b.Length >= 12 && Ascii(b, 0, "RIFF") && Ascii(b, 8, "WAVE"))
            {
                int pos = 12;
                for (int i = 0; i < 1000 && pos + 8 <= b.Length; i++)
                {
                    string id = Encoding.ASCII.GetString(b, pos, 4);
                    long size = BitConverter.ToUInt32(b, pos + 4);
                    int body = pos + 8;
                    int end = (int)Math.Min(b.Length, body + size);
                    if (id != "data" && HasPath(b, body, end - body, named)) return true;
                    pos = (int)Math.Min(b.Length, (long)end + (size & 1));
                }
                return false;
            }
            if (Ascii(b, 0, "OggS"))
            {
                for (int i = 0; i < 3; i++)
                    if (OggPacket(b, i, 16 * 1024 * 1024) is { } packet && HasPath(packet, 0, packet.Length, named)) return true;
                return false;
            }
            if (b.Length >= 10 && Ascii(b, 0, "ID3"))
            {
                long size = b[6] << 21 | b[7] << 14 | b[8] << 7 | b[9];
                int tag = (int)Math.Min(b.Length, 10 + size + ((b[5] & 0x10) != 0 ? 10 : 0));
                if (HasPath(b, 0, tag, named)) return true;
            }
            int tail = Math.Min(b.Length, 256 * 1024);
            return HasPath(b, b.Length - tail, tail, named);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException) { return false; }
    }

    // A part of a file searched for a user path, a megabyte at a time (overlapping, so a path across two isn't missed).
    private static bool HasPath(byte[] b, int start, int length, Regex? named)
    {
        const int Window = 1 << 20, Overlap = 1024;
        bool Found(string text) => UsersPath.IsMatch(text) || (named != null && named.IsMatch(text));
        int end = start + length;
        for (int at = start; at < end; at += Window - Overlap)
        {
            int n = Math.Min(Window, end - at);
            if (Found(Encoding.Latin1.GetString(b, at, n)) || Found(Encoding.Unicode.GetString(b, at, n & ~1))
                || (n > 2 && Found(Encoding.Unicode.GetString(b, at + 1, (n - 1) & ~1)))) return true;
            if (at + n >= end) break;
        }
        return false;
    }

    /// <summary>
    /// A song file's tags: ID3v2 and ID3v1 (MP3, and an "id3 " chunk in a WAV), Vorbis comments
    /// (Ogg) and a WAV's LIST INFO, as (name, text) pairs. Only reads; bounded.
    /// </summary>
    internal static List<(string Name, string Text)> AudioTags(byte[] b)
    {
        var tags = new List<(string, string)>();
        try
        {
            if (b.Length >= 12 && Ascii(b, 0, "RIFF") && Ascii(b, 8, "WAVE")) WavTags(b, tags);
            else if (Ascii(b, 0, "OggS")) VorbisComments(b, tags);
            else
            {
                Id3v2(b, 0, tags);
                if (b.Length >= 128 && Ascii(b, b.Length - 128, "TAG")) Id3v1(b, b.Length - 128, tags);
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException or DecoderFallbackException) { }
        return tags;
    }

    private static bool Ascii(byte[] b, int at, string s)
    {
        if (at < 0 || at + s.Length > b.Length) return false;
        for (int i = 0; i < s.Length; i++)
            if (b[at + i] != s[i]) return false;
        return true;
    }

    private static void Id3v2(byte[] b, int at, List<(string, string)> tags)
    {
        if (!Ascii(b, at, "ID3") || at + 10 > b.Length) return;
        int version = b[at + 3];
        int size = b[at + 6] << 21 | b[at + 7] << 14 | b[at + 8] << 7 | b[at + 9];
        int end = Math.Min(b.Length, at + 10 + size);
        int p = at + 10;
        if ((b[at + 5] & 0x40) != 0 && p + 4 <= end)
            p += version == 4 ? (b[p] << 21 | b[p + 1] << 14 | b[p + 2] << 7 | b[p + 3]) : 4 + (b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]);
        if (version < 3)
        {
            // ID3v2.2: three-letter frame ids and three-byte sizes.
            while (p + 6 <= end && b[p] != 0)
            {
                string id = Encoding.ASCII.GetString(b, p, 3);
                int length = b[p + 3] << 16 | b[p + 4] << 8 | b[p + 5];
                if (length < 0 || p + 6 + length > end) break;
                if (id[0] == 'T' || id == "COM") tags.Add((id, FrameText(b, p + 6, length, id == "COM")));
                p += 6 + length;
            }
            return;
        }
        while (p + 10 <= end && b[p] != 0)
        {
            string id = Encoding.ASCII.GetString(b, p, 4);
            int length = version == 4 ? (b[p + 4] << 21 | b[p + 5] << 14 | b[p + 6] << 7 | b[p + 7]) : (b[p + 4] << 24 | b[p + 5] << 16 | b[p + 6] << 8 | b[p + 7]);
            if (length < 0 || p + 10 + length > end) break;
            if (id[0] == 'T' || id == "COMM" || id == "USLT" || id[0] == 'W') tags.Add((id, id[0] == 'W' && id != "WXXX"
                ? Encoding.Latin1.GetString(b, p + 10, length) : FrameText(b, p + 10, length, id is "COMM" or "USLT")));
            p += 10 + length;
        }
    }

    // A text frame: an encoding byte (Latin-1, UTF-16 with BOM, UTF-16BE, UTF-8), then the text (a
    // comment first has a language and a short description).
    private static string FrameText(byte[] b, int at, int length, bool comment)
    {
        if (length < 1) return "";
        int encoding = b[at];
        int start = at + 1 + (comment ? 3 : 0);
        int count = Math.Max(0, at + length - start);
        var text = encoding switch
        {
            1 => Encoding.Unicode.GetString(b, start, count),
            2 => Encoding.BigEndianUnicode.GetString(b, start, count),
            3 => Encoding.UTF8.GetString(b, start, count),
            _ => Encoding.Latin1.GetString(b, start, count),
        };
        return text.Replace('\0', ' ').Replace("\uFEFF", "").Replace("\uFFFE", "");
    }

    private static void Id3v1(byte[] b, int at, List<(string, string)> tags)
    {
        string Field(int offset, int length) => Encoding.Latin1.GetString(b, at + offset, length).TrimEnd('\0', ' ');
        tags.Add(("ID3v1 title", Field(3, 30)));
        tags.Add(("ID3v1 artist", Field(33, 30)));
        tags.Add(("ID3v1 album", Field(63, 30)));
        tags.Add(("ID3v1 comment", Field(97, 30)));
    }

    private static void WavTags(byte[] b, List<(string, string)> tags)
    {
        int pos = 12;
        for (int i = 0; i < 64 && pos + 8 <= b.Length; i++)
        {
            string id = Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4);
            int body = pos + 8;
            if (size < 0 || (long)body + size > b.Length) size = b.Length - body;
            if (id == "LIST" && size >= 4 && Ascii(b, body, "INFO"))
            {
                int p = body + 4, end = body + size;
                while (p + 8 <= end)
                {
                    string key = Encoding.ASCII.GetString(b, p, 4);
                    int length = BitConverter.ToInt32(b, p + 4);
                    if (length < 0 || p + 8 + length > end) break;
                    tags.Add((key, Encoding.UTF8.GetString(b, p + 8, length).TrimEnd('\0')));
                    p += 8 + length + (length & 1);
                }
            }
            else if (id is "id3 " or "ID3 ") Id3v2(b, body, tags);
            pos = body + size + (size & 1);
        }
    }

    // The second packet of the first Ogg stream: "\x03vorbis", the vendor, then "KEY=value" comments.
    private static void VorbisComments(byte[] b, List<(string, string)> tags)
    {
        var packet = OggPacket(b, 1, 1024 * 1024);
        if (packet == null || packet.Length < 7 || packet[0] != 3 || Encoding.ASCII.GetString(packet, 1, 6) != "vorbis") return;
        int p = 7;
        int vendor = BitConverter.ToInt32(packet, p);
        p += 4 + vendor;
        int count = BitConverter.ToInt32(packet, p);
        p += 4;
        for (int i = 0; i < count && i < 1000 && p + 4 <= packet.Length; i++)
        {
            int length = BitConverter.ToInt32(packet, p);
            p += 4;
            if (length < 0 || p + length > packet.Length) break;
            string comment = Encoding.UTF8.GetString(packet, p, length);
            int eq = comment.IndexOf('=');
            tags.Add(eq > 0 ? (comment.Substring(0, eq).ToUpperInvariant(), comment.Substring(eq + 1)) : ("COMMENT", comment));
            p += length;
        }
    }

    /// <summary>Packet <paramref name="index"/> of an Ogg file, put together from its pages' segments.</summary>
    private static byte[]? OggPacket(byte[] b, int index, int max)
    {
        var packet = new MemoryStream();
        int found = 0, pos = 0;
        while (pos + 27 <= b.Length && Ascii(b, pos, "OggS"))
        {
            int segments = b[pos + 26];
            int data = pos + 27 + segments;
            if (data > b.Length) return null;
            for (int s = 0; s < segments; s++)
            {
                int lace = b[pos + 27 + s];
                if (data + lace > b.Length) return null;
                if (found == index)
                {
                    packet.Write(b, data, lace);
                    if (packet.Length > max) return null;
                }
                data += lace;
                if (lace < 255)
                {
                    if (found == index) return packet.ToArray();
                    found++;
                }
            }
            pos = data;
        }
        return null;
    }
}
