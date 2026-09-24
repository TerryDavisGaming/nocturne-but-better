using System.Globalization;
using System.IO.Compression;
using HarmonyLib;
using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// Plays a custom chart's own song file (#MUSIC, StepMania's tag) in battle instead of the game's
/// Wwise music. The battle conductor normally sets its clock from Wwise
/// (<c>AudioController.TryGetSongPosition</c>: the position in the playing music segment), so while
/// a custom song plays that call is answered with the mod's player instead, and the notes follow
/// the file. Wwise still runs underneath with the combat music turned down, because its start cue
/// is what starts the battle; the battle ends on the chart's last row, not on a cue. The chart's
/// #OFFSET places the file: chart time t is file time t - OFFSET, as in StepMania.
/// </summary>
internal static class CustomMusic
{
    private sealed class Song
    {
        internal short[] Stereo = Array.Empty<short>();
        internal int Rate;
        internal double Origin;   // chart time of the first sample
    }

    private static Task<Song>? loading;
    private static string pendingName = "";
    private static EditorAudio? player;
    private static double origin;
    private static WwiseConductor? conductor;
    private static bool muted, paused;
    private static bool reportedError;

    internal static bool Active => player != null;

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        var position = AccessTools.DeclaredMethod(typeof(AudioController), "TryGetSongPosition")
            ?? throw new MissingMethodException(typeof(AudioController).FullName, "TryGetSongPosition");
        harmony.Patch(position, prefix: new HarmonyMethod(typeof(CustomMusic), nameof(PositionPrefix)));
        var cue = AccessTools.DeclaredMethod(typeof(WwiseConductor), "OnAudioCue")
            ?? throw new MissingMethodException(typeof(WwiseConductor).FullName, "OnAudioCue");
        harmony.Patch(cue, postfix: new HarmonyMethod(typeof(CustomMusic), nameof(CuePostfix)));
        var beat = AccessTools.DeclaredMethod(typeof(WwiseConductor), "OnSyncBeat")
            ?? throw new MissingMethodException(typeof(WwiseConductor).FullName, "OnSyncBeat");
        harmony.Patch(beat, prefix: new HarmonyMethod(typeof(CustomMusic), nameof(BeatPrefix)));
    }

    /// <summary>
    /// How far Wwise's reported position runs ahead of what's heard, less the same for the mod's
    /// player: measured from a loopback recording as 45.5 ms for Wwise and -1.3 ms for the player
    /// (steady to a few ms). Reporting the player this much ahead keeps the game's latency
    /// calibration, which was made against Wwise, right for custom songs.
    /// </summary>
    private const double ClockLead = 0.0468;

    /// <summary>The song's chart time as the conductor's segment time (it adds the finished segments).</summary>
    private static double SegmentTime(EditorAudio p) => p.Time + ClockLead + origin - conductor!.previousSongSegmentTime;

    // Wwise's beat callback also sets the conductor's track time, from Wwise's position; it gets
    // the song's instead.
    private static void BeatPrefix(uint playingId, ref int currentPosition)
    {
        var p = player;
        if (p == null || !conductor || conductor!.currentPlayingId != playingId) return;
        try { currentPosition = (int)Math.Round(SegmentTime(p) * 1000); }
        catch (Exception ex) { Report(ex); }
    }

    /// <summary>Called by ChartSwap for each battle: the custom chart, or none.</summary>
    internal static void Prepare(CustomCharts.CustomChart? chart)
    {
        Stop();
        loading = null;
        var music = chart?.Chart.GetTag("MUSIC")?.Trim();
        if (chart == null || string.IsNullOrEmpty(music)) return;
        // Charts copied from the game name its own source files, which aren't there; those keep
        // the game's music.
        try { if (!MusicFileExists(chart, music!)) return; }
        catch { return; }
        double offset = double.TryParse(chart.Chart.GetTag("OFFSET"), NumberStyles.Float, CultureInfo.InvariantCulture, out double o) ? o : 0;
        pendingName = music!;
        var source = chart;
        loading = Task.Run(() => Load(source, music!, offset));
        ModLog.Info($"Custom song for {chart.DisplayName}: loading {music}.");
    }

    // ---- loading ------------------------------------------------------------------------------

    private static Song Load(CustomCharts.CustomChart chart, string music, double offset)
    {
        byte[] bytes = ReadMusicFile(chart, music);
        var (stereo, rate) = AudioFile.Decode(bytes, music);
        return new Song { Stereo = stereo, Rate = rate, Origin = offset };
    }

    /// <summary>The #MUSIC name as a path inside the chart's folder or pack folder, or null if it points elsewhere.</summary>
    private static string? SafeName(string music)
    {
        string name = music.Replace('\\', '/').Trim();
        if (name.Length == 0 || name.Contains("..") || name.StartsWith("/") || name.Contains(':')) return null;
        return name;
    }

    private static bool MusicFileExists(CustomCharts.CustomChart chart, string music)
    {
        var name = SafeName(music);
        if (name == null) return false;
        if (chart.PackEntry == null) return File.Exists(LoosePath(chart, name));
        using var zip = ZipFile.OpenRead(chart.SourceFile);
        return PackEntry(zip, chart, name) != null;
    }

    private static string LoosePath(CustomCharts.CustomChart chart, string name) =>
        Path.Combine(Path.GetDirectoryName(chart.SourceFile) ?? CustomCharts.Folder, name.Replace('/', Path.DirectorySeparatorChar));

    private static ZipArchiveEntry? PackEntry(ZipArchive zip, CustomCharts.CustomChart chart, string name)
    {
        string entry = chart.PackEntry ?? "";
        string folderInPack = entry.Contains('/') ? entry.Substring(0, entry.LastIndexOf('/') + 1) : "";
        return zip.GetEntry(folderInPack + name) ?? zip.GetEntry(name);
    }

    /// <summary>The #MUSIC file next to the chart: in its folder, or in the same pack.</summary>
    private static byte[] ReadMusicFile(CustomCharts.CustomChart chart, string music)
    {
        var name = SafeName(music) ?? throw new InvalidDataException("the #MUSIC file must be next to the chart");
        if (chart.PackEntry == null) return File.ReadAllBytes(LoosePath(chart, name));
        using var zip = ZipFile.OpenRead(chart.SourceFile);
        var entry = PackEntry(zip, chart, name) ?? throw new FileNotFoundException("the pack has no " + name);
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // ---- starting, following, stopping ---------------------------------------------------------

    // The song starts where the game's clock starts: on the music's start cue.
    private static void CuePostfix(WwiseConductor __instance)
    {
        if (player != null || loading == null || !__instance || __instance.waitForStartCue || !__instance.playing) return;
        try
        {
            if (!loading.IsCompleted) loading.Wait(TimeSpan.FromSeconds(3));
            if (!loading.IsCompleted) throw new TimeoutException("the song file took too long to load");
            var song = loading.Result;
            loading = null;
            player = new EditorAudio(song.Stereo, song.Rate);
            origin = song.Origin;
            conductor = __instance;
            double now = __instance.songPosition != null ? __instance.songPosition.RawTime : 0;
            player.SetMusicVolume(Volume());
            player.Seek(Math.Max(0, now - origin));
            player.Play();
            MuteWwise(true);
            ModLog.Info($"Custom song {pendingName} started at chart time {now:0.000} ({player.Length:0.0}s).");
        }
        catch (Exception ex)
        {
            var reason = ex is AggregateException agg && agg.InnerException != null ? agg.InnerException : ex;
            ModLog.Error($"The custom song {pendingName} couldn't play, so the game's music plays: {reason}");
            loading = null;
            Stop();
        }
    }

    // The conductor adds the time of the segments Wwise already finished, so the answer is the
    // song's chart time minus that.
    private static bool PositionPrefix(uint playingId, ref double songPosition, ref bool __result)
    {
        var p = player;
        if (p == null || !conductor || conductor!.currentPlayingId != playingId) return true;
        try
        {
            songPosition = SegmentTime(p);
            __result = true;
            return false;
        }
        catch (Exception ex) { Report(ex); return true; }
    }

    /// <summary>Called every frame: pause with the game, follow the volume, and end with the song.</summary>
    internal static void Update()
    {
        var p = player;
        if (p == null) return;
        try
        {
            if (!conductor || !conductor!.initializedSong || conductor.endedSong || !conductor.playing)
            {
                Stop();
                return;
            }
            bool pause = AudioController.IsPausedCombat || conductor.Paused;
            if (pause != paused)
            {
                paused = pause;
                if (pause) p.Pause(); else p.Play();
            }
            p.SetMusicVolume(Volume());
            if (p.Failed != null) throw new InvalidOperationException(p.Failed);
        }
        catch (Exception ex)
        {
            Report(ex);
            Stop();
        }
    }

    private static void Stop()
    {
        if (player != null)
        {
            try { player.Dispose(); } catch { }
            player = null;
            ModLog.Info("Custom song stopped.");
        }
        conductor = null;
        paused = false;
        MuteWwise(false);
    }

    /// <summary>The player's volume from the game's master, music and combat music settings.</summary>
    private static float Volume()
    {
        try { return Mathf.Clamp01(AudioController.MasterVolume) * Mathf.Clamp01(AudioController.MusicVolume) * Mathf.Clamp01(AudioController.CombatMusicVolume); }
        catch { return 1f; }
    }

    // Turns the game's combat music down through its own volume control, without changing the
    // saved setting, and back to the setting afterwards.
    private static void MuteWwise(bool mute)
    {
        if (mute == muted) return;
        try
        {
            var controller = AudioController.Instance;
            var rtpc = controller ? controller.CombatMusicVolumeProperty : null;
            if (rtpc == null) return;
            if (mute) rtpc.SetGlobalValue(0f);
            else AudioController.CombatMusicVolume = AudioController.CombatMusicVolume; // re-applies the saved value
            muted = mute;
        }
        catch (Exception ex) { Report(ex); }
    }

    private static void Report(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        ModLog.Error("Custom song playback failed: " + ex);
    }
}

/// <summary>Reads song files into 16-bit stereo PCM.</summary>
internal static class AudioFile
{
    internal static (short[] Stereo, int Rate) Decode(byte[] bytes, string name)
    {
        if (bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' && bytes[8] == 'W' && bytes[9] == 'A')
            return DecodeWav(bytes);
        throw new NotSupportedException($"{Path.GetFileName(name)} isn't a WAV file; only WAV songs work so far");
    }

    /// <summary>PCM WAV, 8/16/24/32-bit integer or 32-bit float, mono or stereo (or more; the first two channels).</summary>
    private static (short[], int) DecodeWav(byte[] b)
    {
        int pos = 12, channels = 0, rate = 0, bits = 0, format = 0;
        int dataStart = -1, dataLength = 0;
        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4);
            int body = pos + 8;
            if (size < 0 || body + size > b.Length) size = b.Length - body;
            if (id == "fmt ")
            {
                format = BitConverter.ToUInt16(b, body);
                channels = BitConverter.ToUInt16(b, body + 2);
                rate = BitConverter.ToInt32(b, body + 4);
                bits = BitConverter.ToUInt16(b, body + 14);
                if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(b, body + 24); // extensible: sub-format
            }
            else if (id == "data") { dataStart = body; dataLength = size; }
            pos = body + size + (size & 1);
        }
        if (dataStart < 0 || channels <= 0 || rate <= 0) throw new InvalidDataException("the WAV file has no audio");
        bool isFloat = format == 3;
        if (!isFloat && format != 1) throw new NotSupportedException($"WAV format {format} isn't supported; use plain PCM");
        int bytesPer = bits / 8;
        if (bytesPer <= 0 || (isFloat && bits != 32)) throw new NotSupportedException($"{bits}-bit WAV isn't supported");
        int frames = dataLength / (bytesPer * channels);
        var stereo = new short[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            int at = dataStart + f * bytesPer * channels;
            short l = Sample(b, at, bits, isFloat);
            short r = channels > 1 ? Sample(b, at + bytesPer, bits, isFloat) : l;
            stereo[f * 2] = l;
            stereo[f * 2 + 1] = r;
        }
        return (stereo, rate);
    }

    private static short Sample(byte[] b, int at, int bits, bool isFloat)
    {
        if (isFloat) return (short)Math.Clamp(BitConverter.ToSingle(b, at) * 32767f, -32768f, 32767f);
        return bits switch
        {
            8 => (short)((b[at] - 128) << 8),
            16 => BitConverter.ToInt16(b, at),
            24 => (short)((b[at + 1]) | (b[at + 2] << 8)),
            32 => (short)(BitConverter.ToInt32(b, at) >> 16),
            _ => 0,
        };
    }
}
