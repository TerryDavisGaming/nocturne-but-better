using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace NocturneFlatScroll;

/// <summary>
/// Plays the player's chosen custom difficulty in a song's battle. The game builds each battle's
/// chart in WwiseConductor.CreateBeatmap from the song's .sm text; the mod hands it the custom
/// chart there instead, so everything after that (judging, the note field, the enemy's events)
/// works as usual.
/// </summary>
internal static class ChartSwap
{
    private const string ScoreKeyPrefix = "NocturneButBetter/";
    private static bool reportedError;

    /// <summary>The custom difficulty in the current battle, or null for the game's own chart.</summary>
    internal static CustomCharts.CustomChart? Playing { get; private set; }

    // The score key the playing song records under; only that key is redirected.
    private static string? playingScoreKey;

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(WwiseConductor), "Initialize")
                ?? throw new MissingMethodException(typeof(WwiseConductor).FullName, "Initialize"),
            prefix: new HarmonyMethod(typeof(ChartSwap), nameof(InitializePrefix)));
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(WwiseConductor), "CreateBeatmap")
                ?? throw new MissingMethodException(typeof(WwiseConductor).FullName, "CreateBeatmap"),
            prefix: new HarmonyMethod(typeof(ChartSwap), nameof(CreateBeatmapPrefix)));
        // A custom difficulty's scores go under a key of their own, so the song's high scores
        // and the melodies they unlock stay the game's.
        foreach (var name in new[] { "TryRecordScore", "IsNewHighScore" })
            harmony.Patch(
                AccessTools.DeclaredMethod(typeof(ScoreManager), name)
                    ?? throw new MissingMethodException(typeof(ScoreManager).FullName, name),
                prefix: new HarmonyMethod(typeof(ChartSwap), nameof(ScoreKeyPrefix_)));
    }

    /// <summary>Starts every battle: decides the chart, and fixes the melody the chart was written for.</summary>
    private static void InitializePrefix(SongData songData, ref Il2CppStructArray<int> melodies)
    {
        Playing = null;
        playingScoreKey = null;
        ScrollSpeedHooks.Prepare(null);
        CustomMusic.Prepare(null);
        try
        {
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

    private static bool CreateBeatmapPrefix(SongData song, ref SmSongData __result)
    {
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
            CustomMusic.Prepare(chart);
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
