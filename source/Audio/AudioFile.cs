using System.Diagnostics;

namespace NocturneFlatScroll;

/// <summary>
/// Reads song files into 16-bit stereo PCM at the file's own sample rate. The format comes from
/// the file's first bytes, not its name. WAV and Ogg Vorbis are decoded by the mod itself; FLAC,
/// MP3, M4A/MP4, WMA and a few more go through Windows Media Foundation, and the start and end of
/// MP3 and AAC are corrected so the song lines up the way ffmpeg's gapless decode does (MF plays
/// the MP3 header frame, the encoder delay and the AAC priming as extra audio at the start).
/// Runs on any thread and never touches Unity.
/// </summary>
internal static class AudioFile
{
    private enum Kind { Unknown, Wav, Ogg, Flac, Mp3, Adts, Mp4, Asf, Matroska, WebM, Aiff }

    internal static (short[] Stereo, int Rate) Decode(byte[] bytes, string name)
    {
        string file = Path.GetFileName(name);
        var watch = Stopwatch.StartNew();
        try
        {
            var (stereo, rate, how) = DecodeAny(bytes, file);
            int frames = stereo.Length / 2;
            ModLog.Info($"Decoded {file}: {how}; {frames} frames at {rate} Hz ({frames / (double)rate:0.00} s) in {watch.ElapsedMilliseconds} ms.");
            return (stereo, rate);
        }
        catch (NotSupportedException ex) { throw new NotSupportedException($"{file}: {ex.Message}", ex); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{file}: {ex.Message}", ex); }
        catch (OutOfMemoryException) { throw; }
        catch (Exception ex) { throw new InvalidDataException($"{file} couldn't be decoded ({ex.GetType().Name}: {ex.Message})", ex); }
    }

    /// <summary>Whether <see cref="Decode"/> reads these bytes as an MP3 (the first bytes decide, as there).</summary>
    internal static bool IsMp3(byte[] bytes) => Detect(bytes) == Kind.Mp3;

    private static Kind Detect(byte[] b)
    {
        bool At(int p, string s)
        {
            if (p + s.Length > b.Length) return false;
            for (int i = 0; i < s.Length; i++) if (b[p + i] != s[i]) return false;
            return true;
        }
        if (b.Length >= 12 && At(0, "RIFF") && b[8] == 'W' && b[9] == 'A') return Kind.Wav;
        if (At(0, "OggS")) return Kind.Ogg;
        if (At(0, "fLaC")) return Kind.Flac;
        if (At(4, "ftyp") || At(4, "moov") || At(4, "mdat") || At(4, "wide") || At(4, "free") || At(4, "skip")) return Kind.Mp4;
        if (b.Length >= 16 && b[0] == 0x30 && b[1] == 0x26 && b[2] == 0xB2 && b[3] == 0x75 && b[4] == 0x8E && b[5] == 0x66
            && b[6] == 0xCF && b[7] == 0x11 && b[8] == 0xA6 && b[9] == 0xD9) return Kind.Asf;
        if (b.Length >= 4 && b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3)
        {
            // Matroska and WebM share the EBML header; the doctype tells them apart.
            int span = Math.Min(b.Length, 64);
            for (int i = 4; i + 4 <= span; i++) if (At(i, "webm")) return Kind.WebM;
            return Kind.Matroska;
        }
        if (At(0, "FORM") && (At(8, "AIFF") || At(8, "AIFC"))) return Kind.Aiff;

        int p = Mp3Info.SkipId3(b);
        if (At(p, "fLaC")) return Kind.Flac;
        if (p + 2 <= b.Length && b[p] == 0xFF && (b[p + 1] & 0xF6) == 0xF0) return Kind.Adts;
        if (Mp3Info.IsFrameHeader(b, p, out _, out _, out _, out _)) return Kind.Mp3;
        // Junk before the first frame, or an ID3 tag followed by something odd: look further in.
        if (Mp3Info.Parse(b) != null) return Kind.Mp3;
        return Kind.Unknown;
    }

    private static (short[] Stereo, int Rate, string How) DecodeAny(byte[] bytes, string file)
    {
        switch (Detect(bytes))
        {
            case Kind.Wav: return DecodeWav(bytes);
            case Kind.Ogg: return DecodeOgg(bytes);
            case Kind.Flac: return Plain(bytes, "FLAC", "audio/flac", "song.flac");
            case Kind.Mp3: return DecodeMp3(bytes);
            case Kind.Adts: return Plain(bytes, "AAC (ADTS)", "audio/aac", "song.aac");
            case Kind.Mp4: return DecodeMp4(bytes);
            case Kind.Asf: return Plain(bytes, "WMA", "audio/x-ms-wma", "song.wma");
            case Kind.Matroska: return Plain(bytes, "Matroska audio", "audio/x-matroska", "song.mka");
            case Kind.WebM: return Plain(bytes, "WebM audio", "audio/webm", "song.weba");
            case Kind.Aiff: throw new NotSupportedException("AIFF files aren't supported; convert the song to .wav, .ogg or .flac");
            default:
                throw new NotSupportedException("isn't a song file the mod can read (it reads WAV, OGG Vorbis, MP3, FLAC, M4A and WMA)");
        }
    }

    // ---- WAV ------------------------------------------------------------------------------------

    private static (short[], int, string) DecodeWav(byte[] bytes)
    {
        int tag = WavFile.FormatTag(bytes);
        if (tag == 1 || tag == 3 || tag == -1)
        {
            var (stereo, rate, channels) = WavFile.Decode(bytes);
            // The WAV reader keeps the first two channels as they are (as it always has).
            return (stereo, rate, channels > 2 ? $"WAV, {channels} channels (the first two are used)" : $"WAV, {Describe(channels)}");
        }
        // Compressed WAV (ADPCM, MP3 in WAV and so on): Windows may have a decoder for it.
        try
        {
            if (tag != 0x55) return Plain(bytes, $"WAV format {tag}", "audio/wav", "song.wav");
            // MP3 in WAV decodes with the same offsets as a plain MP3.
            var (data, size) = WavFile.Chunk(bytes, "data");
            var info = data >= 0 ? Mp3Info.Parse(bytes.AsSpan(data, size).ToArray()) : null;
            return TrimMp3(MediaFoundationAudio.Decode(bytes, "audio/wav", "song.wav"), info, " in WAV");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new NotSupportedException($"WAV format {tag} isn't supported ({ex.Message}); use plain PCM", ex);
        }
    }

    // ---- Ogg Vorbis -----------------------------------------------------------------------------

    private static (short[], int, string) DecodeOgg(byte[] bytes)
    {
        var ogg = OggVorbis.Open(bytes);
        // The last granule position is the length after trimming, so the buffer fills exactly;
        // the packet count bounds it when the granule positions don't start at zero.
        long frames = ogg.CountFrames();
        if (ogg.LastGranule > 0) frames = Math.Min(frames, ogg.LastGranule);
        var pcm = new StereoPcm(ogg.Channels, SpeakerLayouts.Vorbis(ogg.Channels), StereoPcm.Plausible(frames, bytes.Length));
        ogg.DecodeTo(pcm);
        var how = $"Ogg Vorbis, {Describe(ogg.Channels)}";
        if (ogg.Vendor.Length > 0) how += $", encoder \"{ogg.Vendor}\"";
        if (ogg.TrimmedStart > 0 || ogg.TrimmedEnd > 0) how += $", granule trim {ogg.TrimmedStart} at the start and {ogg.TrimmedEnd} at the end";
        if (ogg.DamagedPages > 0 || ogg.Gaps > 0 || ogg.FilledGaps > 0)
            how += $", {ogg.DamagedPages} damaged pages and {ogg.Gaps} gaps in the page sequence ({ogg.FilledGaps} frames of silence put in their place)";
        if (ogg.SkippedPackets > 0) how += $", {ogg.SkippedPackets} bad packets skipped";
        if (ogg.Truncated) how += ", the file is cut short";
        return (pcm.ToArray(), ogg.Rate, how);
    }

    // ---- Media Foundation -------------------------------------------------------------------------

    private static (short[], int, string) Plain(byte[] bytes, string format, string contentType, string fileName)
    {
        var d = MediaFoundationAudio.Decode(bytes, contentType, fileName);
        return (d.Pcm.ToArray(), d.Rate, $"{format} through Media Foundation, {Describe(d.Channels)}");
    }

    // How MF's MP3 decoder lines up (measured on Windows 11 with LAME files and a click at 44100):
    //  - it hides 528 of the decoder's 529-sample delay, so one extra sample leads the audio;
    //  - it plays a CBR "Info" header frame as a frame of silence, but skips a VBR "Xing" one;
    //  - the encoder delay (576 for LAME) stays in, and the padding at the end too.
    // ffmpeg's gapless decode starts after the header frame, the delay from LAME's tag and its own
    // decoder delay, and stops before the padding, so this trims the same and matches it to the
    // sample. Apple's and Windows' encoders write iTunes' iTunSMPB comment instead, with the delay
    // and the true length (ffmpeg ignores it; this uses it). With neither, LAME's usual 576-sample
    // delay is assumed (ffmpeg trims nothing then).
    private const int LameDelay = 576;
    private const int MfMp3Lead = 1;
    private const int MfMp3Hidden = 528;

    private static (short[], int, string) DecodeMp3(byte[] bytes)
    {
        var info = Mp3Info.Parse(bytes);
        var d = MediaFoundationAudio.Decode(bytes, "audio/mpeg", "song.mp3");
        return TrimMp3(d, info, "");
    }

    private static (short[], int, string) TrimMp3(MediaFoundationAudio.Decoded d, Mp3Info? info, string container)
    {
        long total = d.Pcm.Frames;
        if (info == null) return (d.Pcm.ToArray(), d.Rate, $"MP3{container} through Media Foundation, {Describe(d.Channels)}, no MPEG frame header found so nothing trimmed");

        string how = $"MPEG-{(info.Version == 25 ? "2.5" : info.Version.ToString())} layer {info.Layer}{container} through Media Foundation, {Describe(d.Channels)}";
        if (info.Tag.Length > 0) how += $", {info.Tag} header" + (info.Encoder.Length > 0 ? $" from \"{info.Encoder}\"" : "");
        int spf = info.SamplesPerFrame;
        if (info.Layer != 3) return Trim(d, 0, -1, how + " (not layer 3, so nothing trimmed)");

        // Whether MF played the header frame: the stream's length tells, when the tag has a frame count.
        bool headerPlayed = info.Tag == "Info";
        long length = -1;
        if (info.Tag.Length > 0 && info.Frames > 0)
        {
            long played = (info.Frames + 1) * spf - MfMp3Hidden, skipped = info.Frames * spf - MfMp3Hidden;
            if (Math.Abs(total - played) <= spf / 4) headerPlayed = true;
            else if (Math.Abs(total - skipped) <= spf / 4) headerPlayed = false;
            else if (info.HasLame && Math.Abs(total - (info.Frames * spf - info.Delay - info.Padding)) <= spf / 4)
                return (d.Pcm.ToArray(), d.Rate, how + $", LAME delay {info.Delay} and padding {info.Padding} already applied by Windows");
            else how += $", unexpected length {total} (expected {played} or {skipped})";
        }
        long start = (info.Tag.Length > 0 && headerPlayed ? spf : 0) + MfMp3Lead;
        if (info.HasLame)
        {
            start += info.Delay;
            if (info.Frames > 0) length = info.Frames * spf - info.Delay - info.Padding;
            how += $", LAME delay {info.Delay} and padding {info.Padding}";
        }
        else if (info.SmpbPriming >= 0)
        {
            // Same convention as LAME's delay: the decoder's own delay isn't included.
            start += info.SmpbPriming;
            if (info.SmpbLength > 0) length = info.SmpbLength;
            how += $", iTunSMPB delay {info.SmpbPriming} and length {info.SmpbLength}";
        }
        else
        {
            start += LameDelay;
            how += $", no LAME tag so a {LameDelay}-sample encoder delay is assumed";
        }
        return Trim(d, start, length, how);
    }

    // MF's MPEG-4 source doesn't apply the edit list: AAC's priming samples (usually 1024, 2112
    // from Apple's encoder) come out first and the last frame's padding at the end. Windows' own
    // AAC encoder writes no edit list and no priming, so those files need nothing.
    private static (short[], int, string) DecodeMp4(byte[] bytes)
    {
        var info = Mp4Info.Parse(bytes);
        var d = MediaFoundationAudio.Decode(bytes, "audio/mp4", "song.m4a");
        string codec = info.Codec.Trim().Length > 0 ? info.Codec.Trim() : "MP4";
        string how = $"{(codec == "mp4a" ? "AAC" : codec)} in MP4 through Media Foundation, {Describe(d.Channels)}";
        long start = 0, length = -1;
        if (info.MediaTime >= 0)
        {
            double mediaScale = info.MediaTimescale > 0 ? d.Rate / (double)info.MediaTimescale : 1;
            start = (long)Math.Round(info.MediaTime * mediaScale);
            if (info.SegmentDuration > 0 && info.MovieTimescale > 0)
                length = (long)Math.Round(info.SegmentDuration * (d.Rate / (double)info.MovieTimescale));
            how += $", edit list starts at {info.MediaTime}/{info.MediaTimescale}";
            if (info.Edits > 1) how += $" (only the first of {info.Edits} edits is used)";
            // In case a later Windows applies the edit list itself: then the output is already the
            // edit's length rather than the whole track's.
            if (length > 0 && info.MediaDuration > 0 && info.MediaTimescale > 0)
            {
                long whole = (long)Math.Round(info.MediaDuration * mediaScale);
                long total = d.Pcm.Frames;
                if (Math.Abs(total - length) <= 64 && Math.Abs(total - length) < Math.Abs(total - whole))
                    return (d.Pcm.ToArray(), d.Rate, how + ", already applied by Windows");
            }
        }
        else if (info.SmpbPriming >= 0)
        {
            start = info.SmpbPriming;
            if (info.SmpbLength > 0) length = info.SmpbLength;
            how += $", iTunSMPB priming {info.SmpbPriming} and padding {info.SmpbPadding}";
        }
        return Trim(d, start, length, how);
    }

    private static (short[], int, string) Trim(MediaFoundationAudio.Decoded d, long start, long length, string how)
    {
        var pcm = d.Pcm;
        long total = pcm.Frames;
        start = Math.Clamp(start, 0, total);
        long keep = length < 0 ? total - start : Math.Clamp(length, 0, total - start);
        pcm.TrimStart(start);
        pcm.TrimEnd(total - start - keep);
        how += $"; dropped {start} frames at the start and {total - start - keep} at the end";
        return (pcm.ToArray(), d.Rate, how);
    }

    private static string Describe(int channels) => channels switch
    {
        1 => "mono",
        2 => "stereo",
        _ => $"{channels} channels folded to stereo",
    };
}
