using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace NocturneFlatScroll;

/// <summary>
/// Plays the player's chosen custom difficulty in a song's battle, and a custom song's own
/// chart in its battle. The game builds each battle's chart in WwiseConductor.CreateBeatmap from
/// the song's .sm text; the mod hands it the custom chart there instead, so everything after
/// that (judging, the note field, the enemy's events) works as usual.
/// </summary>
internal static class ChartSwap
{
    private const string ScoreKeyPrefix = "NocturneButBetter/";
    private static bool reportedError;

    /// <summary>The custom difficulty in the current battle, or null for the game's own chart.</summary>
    internal static CustomCharts.CustomChart? Playing { get; private set; }

    /// <summary>The custom song in the current battle, or null.</summary>
    internal static CustomSongs.Song? PlayingSong { get; private set; }

    // The conductor playing the custom song.
    private static WwiseConductor? songConductor;

    /// <summary>The custom song whose battle is running now, or null.</summary>
    internal static CustomSongs.Song? CurrentSong
    {
        get
        {
            var conductor = songConductor;
            try { return PlayingSong != null && conductor != null && conductor && conductor.initializedSong && !conductor.endedSong ? PlayingSong : null; }
            catch { return null; }
        }
    }

    // The score key the playing song records under; only that key is redirected.
    private static string? playingScoreKey;

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(Method(typeof(WwiseConductor), "Initialize"), prefix: new HarmonyMethod(typeof(ChartSwap), nameof(InitializePrefix)));
        harmony.Patch(Method(typeof(WwiseConductor), "CreateBeatmap"), prefix: new HarmonyMethod(typeof(ChartSwap), nameof(CreateBeatmapPrefix)));
        // A custom difficulty's scores go under a key of their own, so the song's high scores
        // and the melodies they unlock stay the game's.
        foreach (var name in new[] { "TryRecordScore", "IsNewHighScore" })
            harmony.Patch(Method(typeof(ScoreManager), name), prefix: new HarmonyMethod(typeof(ChartSwap), nameof(ScoreKeyPrefix_)));
        // Only custom songs need this, so custom charts keep working if it can't be installed.
        try { harmony.Patch(Method(typeof(WwiseConductor), "InitializeNoteField"), postfix: new HarmonyMethod(typeof(ChartSwap), nameof(InitializeNoteFieldPostfix))); }
        catch (Exception ex) { ModLog.Error("Custom songs' full-length battles could not be installed: " + ex); }
    }

    private static System.Reflection.MethodInfo Method(Type type, string name) =>
        AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);

    /// <summary>Starts every battle: decides the chart, and fixes the melody the chart was written for.</summary>
    private static void InitializePrefix(WwiseConductor __instance, SongData songData, ref Il2CppStructArray<int> melodies)
    {
        Playing = null;
        playingScoreKey = null;
        ScrollSpeedHooks.Prepare(null);
        CustomMusic.Reset(__instance);
        try
        {
            if (TakeCustomSong(__instance, songData))
            {
                // A custom song has one melody, and its chart and score key are its own.
                melodies = new Il2CppStructArray<int>(new[] { 0, 0 });
                return;
            }
            if (!songData) return;
            var chart = CustomCharts.Selected(songData.name);
            if (chart == null) return;
            if (CustomCharts.SharesScore(songData))
            {
                ModLog.Error($"{songData.name} shares its score with other parts of its fight, which custom charts don't support yet; playing the game's chart.");
                return;
            }
            int lanes = CustomCharts.LanesOf(songData);
            if (lanes > 0 && chart.Lanes != lanes)
            {
                ModLog.Error($"Custom chart {chart.DisplayName} has {chart.Lanes} lanes but {songData.name} has {lanes}; playing the game's chart.");
                return;
            }
            var maps = songData.beatmaps;
            int count = maps != null ? maps.Length : 0;
            if (count > 1)
            {
                // The chart follows one melody's music, so that melody plays from start to end.
                int melody = Math.Clamp(chart.Melody, 1, count) - 1;
                melodies = new Il2CppStructArray<int>(new[] { melody, melody });
            }
            Playing = chart;
            playingScoreKey = songData.HighScoreKey;
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    /// <summary>
    /// Notes the custom song a conductor starts, if it is one. Another conductor starting (a
    /// menu's, say) leaves a running custom-song battle as it is.
    /// </summary>
    private static bool TakeCustomSong(WwiseConductor conductor, SongData songData)
    {
        var custom = songData ? CustomSongs.Find(songData) : null;
        if (custom != null)
        {
            PlayingSong = custom;
            songConductor = conductor;
            ModLog.Info($"Custom song battle: {custom.Title}.");
            return true;
        }
        var current = songConductor;
        bool running = current != null && current && current.Pointer != conductor.Pointer && current.initializedSong && !current.endedSong;
        if (!running)
        {
            PlayingSong = null;
            songConductor = null;
        }
        return false;
    }

    private static bool IsSongConductor(WwiseConductor conductor) =>
        songConductor != null && songConductor && conductor && songConductor.Pointer == conductor.Pointer;

    private static bool CreateBeatmapPrefix(WwiseConductor __instance, SongData song, ref SmSongData __result)
    {
        var custom = PlayingSong;
        if (custom != null && song && custom.Data && song.Pointer == custom.Data.Pointer && IsSongConductor(__instance))
            return CreateSongBeatmap(__instance, custom, ref __result);

        var chart = Playing;
        if (chart == null || !song || !chart.Song.Equals(song.name, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            ChartText? events = null;
            if (chart.KeepSongEvents)
            {
                var maps = song.beatmaps;
                int index = Math.Clamp(chart.Melody, 1, Math.Max(1, maps != null ? maps.Length : 1)) - 1;
                if (maps != null && index < maps.Length && maps[index]) events = ChartText.Parse(maps[index].text);
            }
            chart.Chart.Validate(chart.BlockIndex);
            var built = NotesLoaderSM.Instance.LoadFromText(chart.Chart.BuildPlayable(chart.BlockIndex, events));
            if (built == null || built.steps == null || built.steps.Count == 0 || built.timingData == null)
                throw new InvalidDataException("the game's reader found no playable chart in it");
            __result = built;
            ScrollSpeedHooks.Prepare(chart.Chart);
            CustomMusic.Prepare(CustomMusic.SourceFor(chart), __instance, customSong: false);
            ModLog.Info($"Playing custom chart {chart.DisplayName} for {song.name}.");
            return false;
        }
        catch (Exception ex)
        {
            // A broken chart must not stop the battle: the game's own chart plays instead.
            ModLog.Error($"Custom chart {chart.DisplayName} could not be loaded, so the game's chart plays: {ex}");
            Playing = null;
            playingScoreKey = null;
            return true;
        }
    }

    /// <summary>A custom song's chart: its six difficulty slots, read by the game's own reader.</summary>
    private static bool CreateSongBeatmap(WwiseConductor conductor, CustomSongs.Song custom, ref SmSongData __result)
    {
        CustomMusic.Prepare(custom.Music, conductor, customSong: true);
        try
        {
            var built = NotesLoaderSM.Instance.LoadFromText(custom.PlayableText);
            if (built == null || built.steps == null || built.steps.Count == 0 || built.timingData == null)
                throw new InvalidDataException("the game's reader found no playable chart in it");
            __result = built;
            ScrollSpeedHooks.Prepare(custom.Chart);
            ModLog.Info($"Playing custom song {custom.Title} ({custom.Lanes} lanes).");
            return false;
        }
        catch (Exception ex)
        {
            // The song's beatmap holds the same chart, so the game's own read of it is the fallback.
            ModLog.Error($"Custom song {custom.Title} could not be built, so the game reads its chart: {ex}");
            return true;
        }
    }

    // The battle ends a beat after the chart's last note (the note field's maxTapRow + 48 rows).
    // An easier difficulty's last note comes sooner, so a custom song's battle runs to the last
    // note of its longest difficulty whichever one plays.
    private static void InitializeNoteFieldPostfix(WwiseConductor __instance)
    {
        var custom = PlayingSong;
        if (custom == null || !IsSongConductor(__instance)) return;
        try
        {
            var field = __instance.noteField;
            if (field == null) return;
            int last = custom.LastNoteRow;
            if (last <= field.maxTapRow) return;
            ModLog.Info($"Custom song {custom.Title}: the battle runs to row {last} (this difficulty ends at {field.maxTapRow}).");
            field.maxTapRow = last;
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    private static void ScoreKeyPrefix_(ref string songId)
    {
        var chart = Playing;
        if (chart != null && playingScoreKey != null && songId == playingScoreKey) songId = ScoreKeyPrefix + chart.Key;
    }

    private static void ReportOnce(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        ModLog.Error("Custom charts failed: " + ex);
    }
}
