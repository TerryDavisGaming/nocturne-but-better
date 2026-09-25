using System.Buffers.Binary;
using System.Text;

namespace NocturneFlatScroll;

internal enum MediaType { Unknown, Png, Jpeg, Gif, WebP, Bmp, Tiff, Heic, Mp4, WebM }

/// <summary>What an art file is and says about itself, from its bytes.</summary>
internal sealed class MediaInfo
{
    internal MediaType Type;
    internal int Width, Height;
    /// <summary>A GIF's whole frames; 0 when only the file's start was read. Other pictures: 1.</summary>
    internal int Frames;
    /// <summary>A GIF's own delays in ms, as browsers play them.</summary>
    internal int[] FrameMs = Array.Empty<int>();
    /// <summary>Whether it may have see-through pixels (as far as its header says).</summary>
    internal bool Alpha;
    /// <summary>The whole file was read, so a GIF's frames and a video's facts are final.</summary>
    internal bool Complete;
    internal VideoFacts? Video;
    internal readonly List<string> Notes = new();

    internal bool IsVideo => Type is MediaType.Mp4 or MediaType.WebM;
    internal double Seconds => Video?.Seconds ?? FrameMs.Sum() / 1000.0;
    internal string TypeName => MediaSniff.Name(Type);
}

/// <summary>
/// What a picked or packaged art file is, from its first bytes (never its extension), without
/// decoding pixels. Every walk is bounded; a file that can't be used ends in an
/// InvalidDataException whose message follows the file's name ("art/x.webp is a WebP picture...").
/// This file has no Unity or game dependencies.
/// </summary>
internal static class MediaSniff
{
    internal static readonly GifDecoder.Limits GifLimits = new();

    internal static MediaType TypeOf(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G' && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return MediaType.Png;
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return MediaType.Jpeg;
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8' && (b[4] == '7' || b[4] == '9') && b[5] == 'a') return MediaType.Gif;
        if (b.Length >= 12 && Ascii(b, 0) == "RIFF" && Ascii(b, 8) == "WEBP") return MediaType.WebP;
        if (b.Length >= 26 && b[0] == 'B' && b[1] == 'M') return MediaType.Bmp;
        if (b.Length >= 4 && ((b[0] == 'I' && b[1] == 'I' && b[2] == 42 && b[3] == 0) || (b[0] == 'M' && b[1] == 'M' && b[2] == 0 && b[3] == 42))) return MediaType.Tiff;
        if (b.Length >= 12 && Ascii(b, 4) == "ftyp" && Ascii(b, 8) is "heic" or "heix" or "hevc" or "heim" or "heis" or "mif1" or "msf1" or "avif" or "avis") return MediaType.Heic;
        if (VideoProbe.IsMp4(b)) return MediaType.Mp4;
        if (VideoProbe.IsEbml(b)) return MediaType.WebM;
        return MediaType.Unknown;
    }

    internal static string Name(MediaType type) => type switch
    {
        MediaType.Png => "PNG",
        MediaType.Jpeg => "JPEG",
        MediaType.Gif => "GIF",
        MediaType.Mp4 => "MP4",
        MediaType.WebM => "WebM",
        MediaType.WebP => "WebP",
        MediaType.Bmp => "BMP",
        MediaType.Tiff => "TIFF",
        MediaType.Heic => "HEIC",
        _ => "file",
    };

    private static string Ascii(ReadOnlySpan<byte> b, int at) => at + 4 <= b.Length ? Encoding.ASCII.GetString(b.Slice(at, 4)) : "";

    /// <summary>
    /// Reads what the bytes say. <paramref name="complete"/> is false when only the file's start
    /// was read: a GIF's frames and a video whose index comes later are then left for the load.
    /// </summary>
    internal static MediaInfo Probe(byte[] bytes, bool complete)
    {
        var info = new MediaInfo { Type = TypeOf(bytes), Complete = complete };
        try
        {
            switch (info.Type)
            {
                case MediaType.Png: Png(bytes, info); break;
                case MediaType.Jpeg: Jpeg(bytes, info); break;
                case MediaType.Gif: Gif(bytes, info, complete); break;
                case MediaType.Mp4:
                case MediaType.WebM: Video(bytes, info, complete); break;
                case MediaType.WebP:
                case MediaType.Bmp:
                case MediaType.Tiff:
                case MediaType.Heic:
                    throw new InvalidDataException($"is a {Name(info.Type)} picture, which can't be read yet (use PNG, JPEG, GIF or a sprite sheet)");
                default:
                    throw new InvalidDataException("isn't a picture, GIF or video the game can show");
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("is cut off or broken");
        }
        // Sizes over the limits are for the caller to refuse, with its own limit in the message.
        if (!info.IsVideo && (info.Width < 1 || info.Height < 1))
            throw new InvalidDataException($"says it is {info.Width} x {info.Height}, which isn't a real picture");
        return info;
    }

    private static uint U32BE(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at, 4));

    private static void Png(byte[] b, MediaInfo info)
    {
        if (b.Length < 26 || Ascii(b, 12) != "IHDR") throw new InvalidDataException("is a broken PNG (it has no header)");
        info.Width = (int)Math.Min(int.MaxValue, U32BE(b, 16));
        info.Height = (int)Math.Min(int.MaxValue, U32BE(b, 20));
        int colorType = b[25];
        info.Alpha = colorType is 4 or 6;
        info.Frames = 1;
        // The chunks before the pixels: tRNS means see-through pixels, acTL an animated PNG.
        int at = 8;
        for (int i = 0; i < 64 && at + 8 <= b.Length; i++)
        {
            uint length = U32BE(b, at);
            string type = Ascii(b, at + 4);
            if (type is "IDAT" or "IEND") break;
            if (type == "tRNS") info.Alpha = true;
            if (type == "acTL") info.Notes.Add("is an animated PNG, and only its first frame shows (use a GIF or a sprite sheet)");
            if (length > int.MaxValue - 12 - at) break;
            at += 12 + (int)length;
        }
    }

    private static void Jpeg(byte[] d, MediaInfo info)
    {
        int p = 2;
        while (p + 4 <= d.Length)
        {
            if (d[p] != 0xFF) { p++; continue; }
            int m = d[p + 1];
            if (m == 0xFF) { p++; continue; }                 // fill bytes
            if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { p += 2; continue; }
            if (m == 0xD9 || m == 0xDA) break;                 // the end, or pixels before any size
            int length = d[p + 2] << 8 | d[p + 3];
            bool frame = m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC;
            if (frame && p + 10 <= d.Length)
            {
                info.Height = d[p + 5] << 8 | d[p + 6];
                info.Width = d[p + 7] << 8 | d[p + 8];
                info.Frames = 1;
                if (d[p + 9] == 4) info.Notes.Add("is a CMYK JPEG, so its colours may come out wrong");
                return;
            }
            if (length < 2) break;
            p += 2 + length;
        }
        throw new InvalidDataException("is a broken JPEG (it has no size)");
    }

    private static void Gif(byte[] b, MediaInfo info, bool complete)
    {
        var gif = GifDecoder.Scan(b, GifLimits, partial: !complete);
        info.Width = gif.Width;
        info.Height = gif.Height;
        info.Alpha = gif.Transparent;
        if (gif.Partial) return;                             // counted when the battle loads
        info.Frames = gif.Frames;
        info.FrameMs = gif.DelaysMs;
        string? note = GifNote(gif);
        if (note != null) info.Notes.Add(note);
    }

    /// <summary>What to tell the player about a GIF that wasn't read to its end.</summary>
    internal static string? GifNote(GifDecoder.Info gif) => gif.Stopped switch
    {
        GifDecoder.Stop.Cut => $"ends early, so only the {gif.Frames} whole frame{(gif.Frames == 1 ? "" : "s")} before the break {(gif.Frames == 1 ? "shows" : "play")}",
        GifDecoder.Stop.Frames => $"has more than {GifLimits.MaxFrames} frames, so only the first {gif.Frames} play",
        GifDecoder.Stop.Pixels => $"is too big to read in full, so only its first {gif.Frames} frames play",
        _ => null,
    };

    private static void Video(byte[] b, MediaInfo info, bool complete)
    {
        var facts = VideoProbe.Read(b);
        info.Video = facts;
        info.Width = facts.Width;
        info.Height = facts.Height;
        info.Frames = 1;
        // An MP4 whose index comes after the part read is checked when the battle loads.
        if (!facts.IndexFound && !complete) return;
        string? problem = facts.Problem();
        if (problem != null) throw new InvalidDataException(problem);
    }
}
