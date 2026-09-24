using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>
/// An osu!mania-style chart editor on top of the game: pick a song and melody, start from a blank
/// chart, a copy of one of the game's difficulties, or a custom chart, then place taps, holds and
/// mines on a snapped grid while the music plays (at 25% to 100% speed), and save it as a custom
/// difficulty. It draws its own screen and reads the keyboard and mouse itself; the game's menus
/// underneath are locked while it is open.
/// </summary>
internal static partial class ChartEditor
{
    private static readonly int[] Snaps = { 1, 2, 3, 4, 6, 8, 12, 16 };
    private static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1.0 };

    private enum Screen { Songs, Melody, Source, Edit }
    private enum Tool { Select, Note, Hold, Mine }

    internal static bool IsOpen => root;

    private static GameObject? root;
    private static RectTransform? canvasRect;
    private static Screen screen;

    // ---- opening and closing ----------------------------------------------------------------

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        // While the editor is open, Back (Escape, the pad's B) must not close the menus underneath;
        // LockGameInput also turns off the open panels' own back setting.
        var back = AccessTools.DeclaredMethod(typeof(MenuPanel), "TryBackAction")
            ?? throw new MissingMethodException(typeof(MenuPanel).FullName, "TryBackAction");
        harmony.Patch(back, prefix: new HarmonyMethod(typeof(ChartEditor), nameof(BackPrefix)));
    }

    // The Escape that closes the editor is still down for a few frames; the menus mustn't see it.
    private static int blockBackUntilFrame = -1;
    private static bool pendingUnlock;

    private static bool BlockBack
    {
        get
        {
            if (IsOpen || Time.frameCount <= blockBackUntilFrame) return true;
            var keyboard = InputKeyboard.current;
            return pendingUnlock && keyboard != null && keyboard[Key.Escape].isPressed;
        }
    }

    private static bool BackPrefix(ref bool __result)
    {
        if (!BlockBack) return true;
        __result = false;
        return false;
    }

    /// <summary>Opens the editor on its song list, or on a song when one is given.</summary>
    internal static void Open(string? song = null)
    {
        if (IsOpen) return;
        try
        {
            pendingUnlock = false;
            BuildCanvas();
            LockGameInput(true);
            cursorWas = (Cursor.lockState, Cursor.visible);
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
        if (root) Object.Destroy(root);
        root = null;
        if (cursorWas is { } was)
        {
            Cursor.lockState = was.Lock;
            Cursor.visible = was.Visible;
            cursorWas = null;
        }
        // Menus come back once the key that closed the editor is let go.
        blockBackUntilFrame = Time.frameCount + 3;
        pendingUnlock = true;
        exportDialog = null;
        typing = TextField.None;
        rebinding = null;
        notePool.Clear();
        linePool.Clear();
        wavePool.Clear();
        markerPool.Clear();
        labelPool.Clear();
        uiButtons.Clear();
        listRows.Clear();
        eventRows.Clear();
        ModLog.Info("Chart editor closed.");
    }

    /// <summary>
    /// Stops the menu underneath from reacting to the editor's keys: its navigation, select and
    /// back events. (The game's own input lock also holds keyboard events back, which the editor
    /// needs.) Back is also blocked by <see cref="BackPrefix"/>.
    /// </summary>
    private static void LockGameInput(bool locked)
    {
        try
        {
            var events = UnityEngine.EventSystems.EventSystem.current;
            if (locked)
            {
                if (events && lockedEvents == null)
                {
                    lockedEvents = events;
                    navigationWas = events.sendNavigationEvents;
                    events.sendNavigationEvents = false;
                }
                // The game only pops a panel on Back when the panel allows it.
                foreach (var panel in Resources.FindObjectsOfTypeAll<MenuPanel>())
                {
                    if (!panel || !panel.gameObject.activeInHierarchy || !panel.allowBacktrack) continue;
                    panel.allowBacktrack = false;
                    noBackPanels.Add(panel);
                }
            }
            else
            {
                if (lockedEvents != null)
                {
                    if (lockedEvents) lockedEvents.sendNavigationEvents = navigationWas;
                    lockedEvents = null;
                }
                foreach (var panel in noBackPanels)
                    if (panel) panel.allowBacktrack = true;
                noBackPanels.Clear();
            }
        }
        catch (Exception ex) { ModLog.Error("Locking the menus for the editor failed: " + ex.Message); }
    }

    // The game can lock and hide the cursor; the editor needs it free while it's open.
    private static (CursorLockMode Lock, bool Visible)? cursorWas;

    private static void FreeCursor()
    {
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
        if (!Cursor.visible) Cursor.visible = true;
    }

    private static UnityEngine.EventSystems.EventSystem? lockedEvents;
    private static readonly List<MenuPanel> noBackPanels = new();
    private static bool navigationWas = true;

    /// <summary>Called every frame.</summary>
    internal static void Update()
    {
        if (pendingUnlock && !BlockBack)
        {
            pendingUnlock = false;
            LockGameInput(false);
        }
        if (!IsOpen) return;
        try
        {
            FreeCursor();
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

    // A row clicked with the mouse, chosen on the next update.
    private static int listClick = -1;

    private static void ClickListRow(int row) => listClick = listFirst + row;

    private static bool Chosen(InputKeyboard keyboard, int count)
    {
        if (listClick >= 0 && listClick < count)
        {
            listIndex = listClick;
            listClick = -1;
            return true;
        }
        listClick = -1;
        return count > 0 && (Pressed(keyboard, Key.Enter) || Pressed(keyboard, Key.NumpadEnter));
    }

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
        DrawList("Chart editor: pick a song",
            (filter.Length > 0 ? $"Filter: {filter}_" : "Type to filter") + "     Click a song, or Up/Down and Enter.  Esc leaves",
            lines, listIndex);
    }

    private static void UpdateMelodyList(InputKeyboard keyboard)
    {
        if (Pressed(keyboard, Key.Escape) || Pressed(keyboard, Key.Backspace)) { listIndex = 0; ShowScreen(Screen.Songs); return; }
        int count = song!.beatmaps.Length;
        listIndex = MoveInList(keyboard, listIndex, count);
        if (Chosen(keyboard, count)) { melody = listIndex + 1; ShowSourceScreen(); return; }
        DrawList($"{song.name}: pick a melody", "Each melody has its own music.  Esc goes back",
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
        DrawList($"{song!.name}, melody {melody}: start from", "Click one, or Up/Down and Enter.  Esc goes back",
            sources.Select(s => s.Label).ToList(), listIndex);
    }

    /// <summary>The song's own chart for the chosen melody: its timing, events and difficulties.</summary>
    private static ChartText GameChart()
    {
        var maps = song!.beatmaps;
        var map = maps[Math.Clamp(melody - 1, 0, maps.Length - 1)];
        return ChartText.Parse(map ? map.text : "");
    }

    // ---- helpers shared by the screens ------------------------------------------------------

    private static bool Pressed(InputKeyboard keyboard, Key key) => keyboard[key].wasPressedThisFrame;
    private static bool Held(InputKeyboard keyboard, Key key) => keyboard[key].isPressed;
    private static bool Ctrl(InputKeyboard keyboard) => Held(keyboard, Key.LeftCtrl) || Held(keyboard, Key.RightCtrl);
    private static bool Shift(InputKeyboard keyboard) => Held(keyboard, Key.LeftShift) || Held(keyboard, Key.RightShift);
    private static bool Alt(InputKeyboard keyboard) => Held(keyboard, Key.LeftAlt) || Held(keyboard, Key.RightAlt);

    private static int MoveInList(InputKeyboard keyboard, int index, int count)
    {
        if (count == 0) return 0;
        if (Pressed(keyboard, Key.DownArrow)) index++;
        if (Pressed(keyboard, Key.UpArrow)) index--;
        if (Pressed(keyboard, Key.PageDown)) index += 10;
        if (Pressed(keyboard, Key.PageUp)) index -= 10;
        return Math.Clamp(index, 0, count - 1);
    }

    private static readonly (Key Key, char Lower, char Upper)[] TypingKeys = BuildTypingKeys();

    private static (Key, char, char)[] BuildTypingKeys()
    {
        var keys = new List<(Key, char, char)>();
        for (int i = 0; i < 26; i++) keys.Add((Key.A + i, (char)('a' + i), (char)('A' + i)));
        // The digit keys aren't numbered in order (Digit0 comes after Digit9), so list them.
        Key[] digits = { Key.Digit0, Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9 };
        for (int i = 0; i < 10; i++) keys.Add((digits[i], (char)('0' + i), ")!@#$%^&*("[i]));
        keys.Add((Key.Space, ' ', ' '));
        keys.Add((Key.Minus, '-', '_'));
        keys.Add((Key.Period, '.', '>'));
        keys.Add((Key.Comma, ',', '<'));
        keys.Add((Key.Quote, '\'', '"'));
        keys.Add((Key.Equals, '=', '+'));
        keys.Add((Key.Slash, '/', '?'));
        keys.Add((Key.NumpadPeriod, '.', '.'));
        keys.Add((Key.NumpadMinus, '-', '-'));
        Key[] pad = { Key.Numpad0, Key.Numpad1, Key.Numpad2, Key.Numpad3, Key.Numpad4, Key.Numpad5, Key.Numpad6, Key.Numpad7, Key.Numpad8, Key.Numpad9 };
        for (int i = 0; i < 10; i++) keys.Add((pad[i], (char)('0' + i), (char)('0' + i)));
        return keys.ToArray();
    }

    /// <summary>Adds typed characters to <paramref name="text"/>; Backspace deletes. True when it changed.</summary>
    private static bool TypeInto(InputKeyboard keyboard, ref string text, int max = 40)
    {
        bool changed = false;
        if (Ctrl(keyboard) || Alt(keyboard)) return false;
        foreach (var (key, lower, upper) in TypingKeys)
        {
            if (!Pressed(keyboard, key) || text.Length >= max) continue;
            text += Shift(keyboard) ? upper : lower;
            changed = true;
        }
        if (Pressed(keyboard, Key.Backspace) && text.Length > 0) { text = text.Substring(0, text.Length - 1); changed = true; }
        return changed;
    }
}
