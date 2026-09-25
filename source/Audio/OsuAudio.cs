using System.Globalization;

namespace NocturneFlatScroll;

/// <summary>
/// Where osu! starts an MP3 compared with the mod. osu! plays songs through BASS with
/// BASS_CONFIG_MP3_OLDGAPS on, and mappers time their notes against what it plays; the mod
/// decodes MP3s with Media Foundation and trims them the way ffmpeg's gapless decode does
/// (<see cref="AudioFile"/>). Measured with click files (a click at exactly 1 s): BASS trims the
/// encoder delay and its decoder's 529 samples only when the Xing/Info header's LAME part gives a
/// delay, whatever encoder name it has, and trims nothing otherwise. So an MP3 without that
/// header starts later in osu! (about 25 ms), and its notes must move that much earlier.
/// Other formats start at the same sample in both. This file has no Unity or game dependencies.
/// </summary>
internal static class OsuAudio
{
    /// <summary>The MP3 decoder's own delay, which both trim (AudioFile's MfMp3Lead + MfMp3Hidden).</summary>
    internal const int DecoderDelay = 529;
    /// <summary>The encoder delay the mod assumes without gapless info (AudioFile.LameDelay).</summary>
    internal const int AssumedDelay = 576;

    /// <summary>Seconds to take off osu!'s times for this song file; 0 for anything but an MP3.</summary>
    internal static double Shift(byte[] bytes, out string how)
    {
        how = "not an MP3, so osu! and the game start it at the same sample";
        if (!AudioFile.IsMp3(bytes)) return 0;
        var info = Mp3Info.Parse(bytes);
        if (info == null || info.Layer != 3 || info.SampleRate <= 0) return 0;
        int modDelay = info.HasLame ? info.Delay : info.SmpbPriming >= 0 ? (int)info.SmpbPriming : AssumedDelay;
        int raw = info.Tag is "Xing" or "Info" ? info.RawDelay : 0;
        int osuTrim = raw > 0 ? raw + DecoderDelay : 0;
        int samples = DecoderDelay + modDelay - osuTrim;
        double seconds = samples / (double)info.SampleRate;
        how = raw > 0
            ? $"MP3 with an encoder delay of {raw} in its header, which osu! and the game both trim" + (samples != 0 ? $" (the game reads {modDelay}, so they're {samples} samples apart)" : "")
            : $"MP3 without gapless info: osu! plays {samples} samples ({(seconds * 1000).ToString("0.0", CultureInfo.InvariantCulture)} ms) of delay that the game trims";
        return seconds;
    }
}
