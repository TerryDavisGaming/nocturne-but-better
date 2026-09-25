using UnityEngine;
using static NocturneFlatScroll.EditorInput;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;

namespace NocturneFlatScroll;

/// <summary>
/// An osu!mania-style chart editor on top of the game: pick a song and melody, start from a blank
/// chart, a copy of one of the game's difficulties, or a custom chart, then place taps, holds and
/// mines on a snapped grid while the music plays (at 25% to 100% speed), and save it as a custom
/// difficulty. It draws its own screen (an <see cref="EditorUi"/>) and reads the keyboard and
/// mouse itself; the game's menus underneath are locked while it is open (<see cref="EditorOverlay"/>).
/// It also edits a custom battle's chart, every difficulty in one file (<see cref="OpenBattle"/>).
/// </summary>
internal static partial class ChartEditor
{
    private static readonly int[] Snaps = { 1, 2, 3, 4, 6, 8, 12, 16 };
    private static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1.0 };

    private enum Screen { Songs, Melody, Source, Edit }
    private enum Tool { Select, Note, Hold, Mine }

    internal static bool IsOpen => ui != null && ui.IsAlive;

    // The screen, built on open and destroyed on close.
    private static EditorUi? ui;
    private static EditorUi Ui => ui ?? throw new InvalidOperationException("the chart editor's screen isn't built");
    private static Screen screen;

    // Names the editor to EditorOverlay, which keeps the menus locked while any editor is open.
    private static readonly object OverlayOwner = "chart editor";

    // ---- opening and closing ----------------------------------------------------------------

    // The Back blocker that keeps the menus underneath closed is shared by the editor screens.
    internal static void Install(HarmonyLib.Harmony harmony) => EditorOverlay.Install(harmony);

    /// <summary>Opens the editor on its song list, or on a song when one is given.</summary>
    internal static void Open(string? song = null)
    {
        if (IsOpen) return;
        try
        {
            BuildCanvas();
            EditorOverlay.Enter(OverlayOwner);
            songs = AllSongs();
            filter = "";
            listIndex = song == null ? 0 : Math.Max(0, songs.FindIndex(s => s.name == song));
            ShowScreen(Screen.Songs);
            ModLog.Info("Chart editor opened.");
        }
        catch (Exception ex)
        {
            ModLog.Error("Opening the chart editor failed: " + ex);
            Close();
        }
    }

    private static void Close()
    {
        audio?.Dispose();
        audio = null;
        loading = null;
        ui?.Destroy();
        ui = null;
        // Gives the cursor back; the menus come back once the key that closed the editor is let go.
        EditorOverlay.Leave(OverlayOwner);
        exportDialog = null;
        typing = TextField.None;
        keyMap.Rebinding = null;
        notePool.Clear();
        linePool.Clear();
        wavePool.Clear();
        markerPool.Clear();
        labelPool.Clear();
        eventRows.Clear();
        ClearBattleWidgets();
        ModLog.Info("Chart editor closed.");
        // A battle's screen (the battle creator) shows again once the editor is gone, on the
        // next frame (see RunClosedBattles).
        var closedBattle = battle;
        if (closedBattle == null) return;
        battle = null;
        closePrompt = false;
        looping = false;
        taps.Clear();
        Array.Clear(tabNotes);
        QueueClosed(closedBattle);
    }

    /// <summary>Called every frame.</summary>
    internal static void Update()
    {
        // Unlocks the menus after a close, and keeps the cursor free while an editor is open.
        EditorOverlay.Update();
        RunClosedBattles();
        // QA only (NFS_QA_BATTLECHART): remove with BattleChartQa.cs once the battle creator opens battles.
        BattleChartQa.Update();
        if (ui == null) return;
        try
        {
            // Only something else destroying the canvas gets here with the screen still set. Close
            // properly, or EditorOverlay would keep the menus and their Back locked for good.
            if (!ui.IsAlive)
            {
                ModLog.Error("The chart editor's screen was destroyed from outside; closing it.");
                Close();
                return;
            }
            var keyboard = InputKeyboard.current;
            if (keyboard == null) return;
            switch (screen)
            {
                case Screen.Songs: UpdateSongList(keyboard); break;
                case Screen.Melody: UpdateMelodyList(keyboard); break;
                case Screen.Source: UpdateSourceList(keyboard); break;
                case Screen.Edit: UpdateEdit(keyboard, InputMouse.current); break;
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("The chart editor failed: " + ex);
            Close();
        }
    }

    // ---- the picker screens -----------------------------------------------------------------

    private static List<SongData> songs = new();
    private static string filter = "";
    private static int listIndex;
    private static SongData? song;
    private static int melody = 1;
    private static readonly List<(string Label, Action Choose)> sources = new();

    private static List<SongData> AllSongs()
    {
        var seen = new HashSet<string>();
        var list = new List<SongData>();
        foreach (var s in Resources.FindObjectsOfTypeAll<SongData>())
        {
            if (!s || !seen.Add(s.name) || s.beatmaps == null || s.beatmaps.Length == 0) continue;
            if (CustomCharts.SharesScore(s)) continue;
            // The latency calibration and fishing test tracks aren't battles.
            if (s.name.StartsWith("Calibration", StringComparison.OrdinalIgnoreCase) || s.name.StartsWith("FishingTest", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(s);
        }
        list.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static List<SongData> FilteredSongs() =>
        filter.Length == 0 ? songs : songs.Where(s => s.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

    // A row clicked with the mouse is chosen on the next update.
    private static bool Chosen(InputKeyboard keyboard, int count) => Ui.Chosen(keyboard, count, ref listIndex);

    private static void UpdateSongList(InputKeyboard keyboard)
    {
        if (Pressed(keyboard, Key.Escape)) { Close(); return; }
        var list = FilteredSongs();
        if (TypeInto(keyboard, ref filter)) listIndex = 0;
        listIndex = MoveInList(keyboard, listIndex, list.Count);
        if (Chosen(keyboard, list.Count))
        {
            song = list[listIndex];
            int count = song.beatmaps.Length;
            if (count > 1) { melody = 1; listIndex = 0; ShowScreen(Screen.Melody); }
            else { melody = 1; ShowSourceScreen(); }
            return;
        }
        var lines = list.Select(s => $"{s.name}  ({CustomCharts.LanesOf(s)} lanes{(s.beatmaps.Length > 1 ? $", {s.beatmaps.Length} melodies" : "")})").ToList();
        Ui.DrawList("Chart editor: pick a song",
            (filter.Length > 0 ? $"Filter: {filter}_" : "Type to filter") + "     Click a song, or Up/Down and Enter.  Esc leaves",
            lines, listIndex);
    }

    private static void UpdateMelodyList(InputKeyboard keyboard)
    {
        if (Pressed(keyboard, Key.Escape) || Pressed(keyboard, Key.Backspace)) { listIndex = 0; ShowScreen(Screen.Songs); return; }
        int count = song!.beatmaps.Length;
        listIndex = MoveInList(keyboard, listIndex, count);
        if (Chosen(keyboard, count)) { melody = listIndex + 1; ShowSourceScreen(); return; }
        Ui.DrawList($"{song.name}: pick a melody", "Each melody has its own music.  Esc goes back",
            Enumerable.Range(1, count).Select(i => $"Melody {i}").ToList(), listIndex);
    }

    private static void ShowSourceScreen()
    {
        sources.Clear();
        var own = GameChart();
        sources.Add(("New empty chart", () => StartEditing(own, -1, null)));
        for (int i = 0; i < own.Blocks.Count; i++)
        {
            int block = i;
            sources.Add(($"Copy of the game's {own.Blocks[i].DisplayName}", () => StartEditing(own, block, null)));
        }
        foreach (var chart in CustomCharts.ForSong(song!.name).Where(c => c.Melody == melody))
        {
            var custom = chart;
            sources.Add(($"Custom: {custom.DisplayName}", () => StartEditing(custom.Chart, custom.BlockIndex, custom)));
        }
        listIndex = 0;
        ShowScreen(Screen.Source);
    }

    private static void UpdateSourceList(InputKeyboard keyboard)
    {
        if (Pressed(keyboard, Key.Escape) || Pressed(keyboard, Key.Backspace))
        {
            listIndex = 0;
            ShowScreen(song!.beatmaps.Length > 1 ? Screen.Melody : Screen.Songs);
            return;
        }
        listIndex = MoveInList(keyboard, listIndex, sources.Count);
        if (Chosen(keyboard, sources.Count)) { sources[listIndex].Choose(); return; }
        Ui.DrawList($"{song!.name}, melody {melody}: start from", "Click one, or Up/Down and Enter.  Esc goes back",
            sources.Select(s => s.Label).ToList(), listIndex);
    }

    /// <summary>The song's own chart for the chosen melody: its timing, events and difficulties.</summary>
    private static ChartText GameChart()
    {
        var maps = song!.beatmaps;
        var map = maps[Math.Clamp(melody - 1, 0, maps.Length - 1)];
        return ChartText.Parse(map ? map.text : "");
    }
}
