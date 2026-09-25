using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using Key = UnityEngine.InputSystem.Key;

namespace NocturneFlatScroll;

// The editor's key bindings: every action has default keys, and the Keys tab changes them.
// They are saved per PC in playerprefs (see KeyMap).
internal static partial class ChartEditor
{
    private enum EditorAction
    {
        PlayPause, SnapForward, SnapBack, BeatForward, BeatBack, MeasureForward, MeasureBack, GoStart, GoEnd,
        SpeedDown, SpeedUp, SnapFiner, SnapCoarser, ZoomIn, ZoomOut,
        ToolSelect, ToolNote, ToolHold, ToolMine,
        NoteTicks, Metronome, AddBookmark, NextBookmark, PrevBookmark,
        Undo, Redo, SelectAll, Copy, Cut, Paste, Delete, Mirror, Reverse,
        NudgeLater, NudgeEarlier, NudgeLeft, NudgeRight, Resnap,
        Save, Export, Rename, SetAuthor,
        TapTempo, NextDifficulty, PrevDifficulty,
    }

    private static readonly (EditorAction Action, string Label, KeyBinding[] Keys)[] DefaultBindings =
    {
        (EditorAction.PlayPause, "Play / pause", new[] { new KeyBinding(Key.Space) }),
        (EditorAction.SnapForward, "Forward one snap", new[] { new KeyBinding(Key.UpArrow) }),
        (EditorAction.SnapBack, "Back one snap", new[] { new KeyBinding(Key.DownArrow) }),
        (EditorAction.BeatForward, "Forward one beat", new[] { new KeyBinding(Key.RightArrow) }),
        (EditorAction.BeatBack, "Back one beat", new[] { new KeyBinding(Key.LeftArrow) }),
        (EditorAction.MeasureForward, "Forward one measure", new[] { new KeyBinding(Key.PageUp) }),
        (EditorAction.MeasureBack, "Back one measure", new[] { new KeyBinding(Key.PageDown) }),
        (EditorAction.GoStart, "Go to the start", new[] { new KeyBinding(Key.Home) }),
        (EditorAction.GoEnd, "Go to the last note", new[] { new KeyBinding(Key.End) }),
        (EditorAction.SpeedDown, "Playback slower", new[] { new KeyBinding(Key.Comma) }),
        (EditorAction.SpeedUp, "Playback faster", new[] { new KeyBinding(Key.Period) }),
        (EditorAction.SnapFiner, "Finer snap", new[] { new KeyBinding(Key.Tab) }),
        (EditorAction.SnapCoarser, "Coarser snap", new[] { new KeyBinding(Key.Tab, shift: true) }),
        (EditorAction.ZoomIn, "Zoom in", new[] { new KeyBinding(Key.Equals) }),
        (EditorAction.ZoomOut, "Zoom out", new[] { new KeyBinding(Key.Minus) }),
        (EditorAction.ToolSelect, "Select tool", new[] { new KeyBinding(Key.Digit1), new KeyBinding(Key.Q) }),
        (EditorAction.ToolNote, "Note tool", new[] { new KeyBinding(Key.Digit2), new KeyBinding(Key.W) }),
        (EditorAction.ToolHold, "Hold tool", new[] { new KeyBinding(Key.Digit3), new KeyBinding(Key.E) }),
        (EditorAction.ToolMine, "Mine tool", new[] { new KeyBinding(Key.Digit4), new KeyBinding(Key.R) }),
        (EditorAction.NoteTicks, "Note ticks on/off", new[] { new KeyBinding(Key.T) }),
        (EditorAction.Metronome, "Metronome on/off", new[] { new KeyBinding(Key.M) }),
        (EditorAction.AddBookmark, "Add/remove bookmark", new[] { new KeyBinding(Key.B, ctrl: true) }),
        (EditorAction.NextBookmark, "Next bookmark", new[] { new KeyBinding(Key.B) }),
        (EditorAction.PrevBookmark, "Previous bookmark", new[] { new KeyBinding(Key.B, shift: true) }),
        (EditorAction.Undo, "Undo", new[] { new KeyBinding(Key.Z, ctrl: true) }),
        (EditorAction.Redo, "Redo", new[] { new KeyBinding(Key.Y, ctrl: true), new KeyBinding(Key.Z, ctrl: true, shift: true) }),
        (EditorAction.SelectAll, "Select all", new[] { new KeyBinding(Key.A, ctrl: true) }),
        (EditorAction.Copy, "Copy", new[] { new KeyBinding(Key.C, ctrl: true) }),
        (EditorAction.Cut, "Cut", new[] { new KeyBinding(Key.X, ctrl: true) }),
        (EditorAction.Paste, "Paste at the current time", new[] { new KeyBinding(Key.V, ctrl: true) }),
        (EditorAction.Delete, "Delete selection", new[] { new KeyBinding(Key.Delete), new KeyBinding(Key.Backspace) }),
        (EditorAction.Mirror, "Mirror left/right", new[] { new KeyBinding(Key.H, ctrl: true) }),
        (EditorAction.Reverse, "Reverse in time", new[] { new KeyBinding(Key.J, ctrl: true) }),
        (EditorAction.NudgeLater, "Move selection later", new[] { new KeyBinding(Key.UpArrow, alt: true) }),
        (EditorAction.NudgeEarlier, "Move selection earlier", new[] { new KeyBinding(Key.DownArrow, alt: true) }),
        (EditorAction.NudgeLeft, "Move selection left", new[] { new KeyBinding(Key.LeftArrow, alt: true) }),
        (EditorAction.NudgeRight, "Move selection right", new[] { new KeyBinding(Key.RightArrow, alt: true) }),
        (EditorAction.Resnap, "Snap selection to the grid", new[] { new KeyBinding(Key.G, ctrl: true) }),
        (EditorAction.Save, "Save", new[] { new KeyBinding(Key.S, ctrl: true) }),
        (EditorAction.Export, "Export as a pack", new[] { new KeyBinding(Key.E, ctrl: true) }),
        (EditorAction.Rename, "Name the chart", new[] { new KeyBinding(Key.F2) }),
        (EditorAction.SetAuthor, "Set the author", new[] { new KeyBinding(Key.F3) }),
        // Battles only. On a battle's Timing tab the tap takes T from the note ticks.
        (EditorAction.TapTempo, "Tap tempo (battle Timing tab)", new[] { new KeyBinding(Key.T) }),
        (EditorAction.NextDifficulty, "Next difficulty (battles)", new[] { new KeyBinding(Key.PageDown, ctrl: true) }),
        (EditorAction.PrevDifficulty, "Previous difficulty (battles)", new[] { new KeyBinding(Key.PageUp, ctrl: true) }),
    };

    // The chart editor's own keys; other editor screens use their own KeyMap and player prefs key.
    private const string KeysPref = "NocturneFlatScroll.EditorKeys.v1";
    private static readonly KeyMap<EditorAction> keyMap = new(KeysPref, DefaultBindings);

    private static void ResetBindings() => keyMap.Reset();

    private static string KeysFor(EditorAction action) => keyMap.KeysFor(action);

    private static string ShortKey(EditorAction action) => keyMap.ShortKey(action);

    /// <summary>Whether one of the action's keys went down this frame with exactly its modifiers.</summary>
    private static bool Triggered(InputKeyboard k, EditorAction action) => keyMap.Triggered(k, action);

    private static string LabelOf(EditorAction action) => keyMap.LabelOf(action);
}
