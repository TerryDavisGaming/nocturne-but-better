using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace NocturneFlatScroll;

/// <summary>
/// Plays the player's chosen custom difficulty in a song's battle, and a custom battle's own
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

    /// <summary>The custom battle being played, or null.</summary>
    internal static CustomBattles.Battle? PlayingBattle { get; private set; }

    // The custom battle's conductor.
    private static WwiseConductor? battleConductor;

    /// <summary>The custom battle running now, or null.</summary>
    internal static CustomBattles.Battle? CurrentBattle
    {
        get
        {
            var conductor = battleConductor;
            try { return PlayingBattle != null && conductor != null && conductor && conductor.initializedSong && !conductor.endedSong ? PlayingBattle : null; }
            catch { return null; }
        }
    }

    // A custom difficulty's scores go under a key of their own, so the song's high scores and the
    // melodies they unlock stay the game's. The song's own score key is pointed at that key for
    // the battle (SongData.HighScoreKey reads highScoreKey when overrideHighScoreKey is set) and
    // put back afterwards, so the game records the score as usual. The score methods themselves
    // aren't patched: TryRecordScore returns a small struct (ValueTuple<bool, int>), which
    // Il2CppInterop's patch trampoline returns as a pointer, and the results screen then read a
    // garbage "new high score" and previous score in every battle.
    // The game's end-of-battle achievement check reads every arcade song's scores through the same
    // key, so the song has its own key for that check (SuspendScoreKey): a custom difficulty's
    // score never counts as the song's own there.
    private static SongData? keyedSong;
    private static WwiseConductor? keyedConductor;
    private static bool keyedOverride;
    private static string? keyedKey;
    private static string? keyedCustomKey;
    private static bool keySuspended;

    /// <summary>Whether the battle's chart and clock can be swapped (test play needs it).</summary>
    internal static bool Installed { get; private set; }

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(Method(typeof(WwiseConductor), "Initialize"), prefix: new HarmonyMethod(typeof(ChartSwap), nameof(InitializePrefix)));
        harmony.Patch(Method(typeof(WwiseConductor), "CreateBeatmap"), prefix: new HarmonyMethod(typeof(ChartSwap), nameof(CreateBeatmapPrefix)));
        // The song's score key goes back once the battle is left (after its score is recorded).
        harmony.Patch(Method(typeof(CombatManagerV3), "ExitCombat"), postfix: new HarmonyMethod(typeof(ChartSwap), nameof(ExitCombatPostfix)));
        Installed = true;
        // Only custom battles need this, so custom charts keep working if it can't be installed.
        try { harmony.Patch(Method(typeof(WwiseConductor), "InitializeNoteField"), postfix: new HarmonyMethod(typeof(ChartSwap), nameof(InitializeNoteFieldPostfix))); }
        catch (Exception ex) { ModLog.Error("Custom battles' full-length songs could not be installed: " + ex); }
    }

    private static System.Reflection.MethodInfo Method(Type type, string name) =>
        AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);

    /// <summary>
    /// Starts every battle: decides the chart, and fixes the melody the chart was written for. A
    /// test play's battle can start its clock partway into the song: the game passes the start as
    /// a plain float (always 0 in battles), and a negative delay starts the clock that far in.
    /// </summary>
    private static void InitializePrefix(WwiseConductor __instance, SongData songData, ref Il2CppStructArray<int> melodies, ref float startDelay)
    {
        Playing = null;
        RestoreScoreKey(__instance);
        ScrollSpeedHooks.Prepare(null);
        CustomMusic.Reset(__instance);
        try
        {
            var test = TestPlay.Claim(__instance, songData);
            if (TakeCustomBattle(__instance, songData))
            {
                // A custom battle has one melody, and its chart and score key are its own.
                melodies = new Il2CppStructArray<int>(new[] { 0, 0 });
                if (test != null) StartTestClock(test, ref startDelay);
                // Its dialogue, before the game shows the ready prompt (which its first lines hold back).
                BattleDialogue.Begin(PlayingBattle!, test, __instance);
                return;
            }
            if (test != null)
            {
                // A game song's test plays the editor's chart on one melody; it records nothing,
                // so no custom chart and no score key of its own.
                melodies = new Il2CppStructArray<int>(new[] { test.Melody, test.Melody });
                StartTestClock(test, ref startDelay);
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
            SwapScoreKey(__instance, songData, ScoreKeyPrefix + chart.Key);
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    private static void StartTestClock(TestPlay.Run test, ref float startDelay)
    {
        if (test.T0 > 0) startDelay = -(float)test.T0;
    }

    private static void SwapScoreKey(WwiseConductor conductor, SongData song, string key)
    {
        RestoreScoreKey(null);
        keyedOverride = song.overrideHighScoreKey;
        keyedKey = song.highScoreKey;
        keyedSong = song;
        keyedConductor = conductor;
        keyedCustomKey = key;
        keySuspended = false;
        song.overrideHighScoreKey = true;
        song.highScoreKey = key;
    }

    /// <summary>
    /// Gives the song its own score key back for a moment, while a custom difficulty's key is in.
    /// True when it did; ResumeScoreKey puts the custom key back.
    /// </summary>
    internal static bool SuspendScoreKey()
    {
        var song = keyedSong;
        if (song == null || keySuspended) return false;
        try
        {
            if (!song) return false;
            song.overrideHighScoreKey = keyedOverride;
            song.highScoreKey = keyedKey;
            keySuspended = true;
            return true;
        }
        catch (Exception ex)
        {
            ReportOnce(ex);
            return false;
        }
    }

    /// <summary>Puts the custom difficulty's key back after SuspendScoreKey. Does nothing otherwise.</summary>
    internal static void ResumeScoreKey()
    {
        if (!keySuspended) return;
        keySuspended = false;
        var song = keyedSong;
        if (song == null) return;
        try
        {
            if (!song) return;
            song.overrideHighScoreKey = true;
            song.highScoreKey = keyedCustomKey;
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    /// <summary>
    /// Puts the song's own score key back. With a conductor, only when that conductor is the one
    /// whose battle took the key, or that battle is over (a menu's conductor starting during a
    /// battle leaves the battle's key alone).
    /// </summary>
    private static void RestoreScoreKey(WwiseConductor? starting)
    {
        var song = keyedSong;
        if (song == null) return;
        try
        {
            if (starting != null)
            {
                var battle = keyedConductor;
                bool running = battle != null && battle && battle.Pointer != starting.Pointer && battle.initializedSong && !battle.endedSong;
                if (running) return;
            }
            if (song)
            {
                song.overrideHighScoreKey = keyedOverride;
                song.highScoreKey = keyedKey;
            }
        }
        catch (Exception ex) { ReportOnce(ex); }
        keyedSong = null;
        keyedConductor = null;
        keyedKey = null;
        keyedCustomKey = null;
        keySuspended = false;
    }

    private static void ExitCombatPostfix() => RestoreScoreKey(null);

    /// <summary>
    /// Notes the custom battle a conductor starts, if it is one. Another conductor starting (a
    /// menu's, say) leaves a running custom-song battle as it is.
    /// </summary>
    private static bool TakeCustomBattle(WwiseConductor conductor, SongData songData)
    {
        var custom = songData ? CustomBattles.Find(songData) : null;
        if (custom != null)
        {
            PlayingBattle = custom;
            battleConductor = conductor;
            ModLog.Info($"Custom battle: {custom.Title}.");
            return true;
        }
        var current = battleConductor;
        bool running = current != null && current && current.Pointer != conductor.Pointer && current.initializedSong && !current.endedSong;
        if (!running)
        {
            PlayingBattle = null;
            battleConductor = null;
        }
        return false;
    }

    private static bool IsBattleConductor(WwiseConductor conductor) =>
        battleConductor != null && battleConductor && conductor && battleConductor.Pointer == conductor.Pointer;

    private static bool CreateBeatmapPrefix(WwiseConductor __instance, SongData song, ref SmSongData __result)
    {
        var custom = PlayingBattle;
        if (custom != null && song && custom.Data && song.Pointer == custom.Data.Pointer && IsBattleConductor(__instance))
            return CreateBattleBeatmap(__instance, custom, ref __result);

        // A test of a game song's chart; a custom battle's test is the custom battle above.
        var test = TestPlay.ChartFor(__instance, song);
        if (test != null) return CreateTestBeatmap(__instance, song, test, ref __result);

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
            CustomMusic.Prepare(CustomMusic.SourceFor(chart), __instance, customBattle: false);
            ModLog.Info($"Playing custom chart {chart.DisplayName} for {song.name}.");
            return false;
        }
        catch (Exception ex)
        {
            // A broken chart must not stop the battle: the game's own chart plays instead.
            ModLog.Error($"Custom chart {chart.DisplayName} could not be loaded, so the game's chart plays: {ex}");
            Playing = null;
            RestoreScoreKey(null);
            return true;
        }
    }

    /// <summary>A custom battle's chart: its six difficulty slots, read by the game's own reader.</summary>
    private static bool CreateBattleBeatmap(WwiseConductor conductor, CustomBattles.Battle custom, ref SmSongData __result)
    {
        CustomMusic.Prepare(custom.Music, conductor, customBattle: true);
        try
        {
            var built = NotesLoaderSM.Instance.LoadFromText(custom.PlayableText);
            if (built == null || built.steps == null || built.steps.Count == 0 || built.timingData == null)
                throw new InvalidDataException("the game's reader found no playable chart in it");
            __result = built;
            ScrollSpeedHooks.Prepare(custom.Chart);
            ModLog.Info($"Playing custom battle {custom.Title} ({custom.Lanes} lanes).");
            return false;
        }
        catch (Exception ex)
        {
            // The song's beatmap holds the same chart, so the game's own read of it is the fallback.
            ModLog.Error($"Custom battle {custom.Title} could not be built, so the game reads its chart: {ex}");
            return true;
        }
    }

    /// <summary>A test play's chart for a game song, built by the chart editor from its unsaved chart.</summary>
    private static bool CreateTestBeatmap(WwiseConductor conductor, SongData song, TestPlay.Run test, ref SmSongData __result)
    {
        try
        {
            // First, so a test from partway in never falls back to the game's music from its start.
            if (test.Music != null) CustomMusic.Prepare(test.Music, conductor, customBattle: !test.MusicFallsBackToWwise);
            var built = NotesLoaderSM.Instance.LoadFromText(test.PlayableText);
            if (built == null || built.steps == null || built.steps.Count == 0 || built.timingData == null)
                throw new InvalidDataException("the game's reader found no playable chart in it");
            __result = built;
            ScrollSpeedHooks.Prepare(test.Chart);
            ModLog.Info($"Test play: playing the test chart for {song.name}.");
            return false;
        }
        catch (Exception ex)
        {
            // The editor checked the chart with the same reader, so this isn't expected.
            ModLog.Error($"Test play: the test chart for {song.name} could not be loaded, so the game's chart plays: {ex}");
            return true;
        }
    }

    // The battle ends a beat after the chart's last note (the note field's maxTapRow + 48 rows).
    // An easier difficulty's last note comes sooner, so a custom battle runs to the last
    // note of its longest difficulty whichever one plays.
    private static void InitializeNoteFieldPostfix(WwiseConductor __instance)
    {
        var custom = PlayingBattle;
        if (custom == null || !IsBattleConductor(__instance)) return;
        try
        {
            var field = __instance.noteField;
            if (field == null) return;
            int last = custom.LastNoteRow;
            if (last <= field.maxTapRow) return;
            ModLog.Info($"Custom battle {custom.Title}: the battle runs to row {last} (this difficulty ends at {field.maxTapRow}).");
            field.maxTapRow = last;
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    private static void ReportOnce(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        ModLog.Error("Custom charts failed: " + ex);
    }
}
