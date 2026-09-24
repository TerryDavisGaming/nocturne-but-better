using System.Text;
using UnityEngine;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using Key = UnityEngine.InputSystem.Key;

namespace NocturneFlatScroll;

// The editor's key bindings: every action has default keys, and the Keys tab changes them.
// They are saved per PC in playerprefs.
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
    }

    private readonly struct Binding
    {
        internal readonly Key Key;
        internal readonly bool Ctrl, Shift, Alt;

        internal Binding(Key key, bool ctrl = false, bool shift = false, bool alt = false)
        {
            Key = key;
            Ctrl = ctrl;
            Shift = shift;
            Alt = alt;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            if (Ctrl) sb.Append("Ctrl+");
            if (Shift) sb.Append("Shift+");
            if (Alt) sb.Append("Alt+");
            sb.Append(KeyName(Key));
            return sb.ToString();
        }

        internal string Save() => $"{(Ctrl ? "C" : "")}{(Shift ? "S" : "")}{(Alt ? "A" : "")}:{Key}";

        internal static bool TryLoad(string text, out Binding binding)
        {
            binding = default;
            int colon = text.IndexOf(':');
            if (colon < 0 || !Enum.TryParse(text.Substring(colon + 1), out Key key) || key == Key.None) return false;
            string mods = text.Substring(0, colon);
            binding = new Binding(key, mods.Contains('C'), mods.Contains('S'), mods.Contains('A'));
            return true;
        }
    }

    private static readonly (EditorAction Action, string Label, Binding[] Keys)[] DefaultBindings =
    {
        (EditorAction.PlayPause, "Play / pause", new[] { new Binding(Key.Space) }),
        (EditorAction.SnapForward, "Forward one snap", new[] { new Binding(Key.UpArrow) }),
        (EditorAction.SnapBack, "Back one snap", new[] { new Binding(Key.DownArrow) }),
        (EditorAction.BeatForward, "Forward one beat", new[] { new Binding(Key.RightArrow) }),
        (EditorAction.BeatBack, "Back one beat", new[] { new Binding(Key.LeftArrow) }),
        (EditorAction.MeasureForward, "Forward one measure", new[] { new Binding(Key.PageUp) }),
        (EditorAction.MeasureBack, "Back one measure", new[] { new Binding(Key.PageDown) }),
        (EditorAction.GoStart, "Go to the start", new[] { new Binding(Key.Home) }),
        (EditorAction.GoEnd, "Go to the last note", new[] { new Binding(Key.End) }),
        (EditorAction.SpeedDown, "Playback slower", new[] { new Binding(Key.Comma) }),
        (EditorAction.SpeedUp, "Playback faster", new[] { new Binding(Key.Period) }),
        (EditorAction.SnapFiner, "Finer snap", new[] { new Binding(Key.Tab) }),
        (EditorAction.SnapCoarser, "Coarser snap", new[] { new Binding(Key.Tab, shift: true) }),
        (EditorAction.ZoomIn, "Zoom in", new[] { new Binding(Key.Equals) }),
        (EditorAction.ZoomOut, "Zoom out", new[] { new Binding(Key.Minus) }),
        (EditorAction.ToolSelect, "Select tool", new[] { new Binding(Key.Digit1), new Binding(Key.Q) }),
        (EditorAction.ToolNote, "Note tool", new[] { new Binding(Key.Digit2), new Binding(Key.W) }),
        (EditorAction.ToolHold, "Hold tool", new[] { new Binding(Key.Digit3), new Binding(Key.E) }),
        (EditorAction.ToolMine, "Mine tool", new[] { new Binding(Key.Digit4), new Binding(Key.R) }),
        (EditorAction.NoteTicks, "Note ticks on/off", new[] { new Binding(Key.T) }),
        (EditorAction.Metronome, "Metronome on/off", new[] { new Binding(Key.M) }),
        (EditorAction.AddBookmark, "Add/remove bookmark", new[] { new Binding(Key.B, ctrl: true) }),
        (EditorAction.NextBookmark, "Next bookmark", new[] { new Binding(Key.B) }),
        (EditorAction.PrevBookmark, "Previous bookmark", new[] { new Binding(Key.B, shift: true) }),
        (EditorAction.Undo, "Undo", new[] { new Binding(Key.Z, ctrl: true) }),
        (EditorAction.Redo, "Redo", new[] { new Binding(Key.Y, ctrl: true), new Binding(Key.Z, ctrl: true, shift: true) }),
        (EditorAction.SelectAll, "Select all", new[] { new Binding(Key.A, ctrl: true) }),
        (EditorAction.Copy, "Copy", new[] { new Binding(Key.C, ctrl: true) }),
        (EditorAction.Cut, "Cut", new[] { new Binding(Key.X, ctrl: true) }),
        (EditorAction.Paste, "Paste at the current time", new[] { new Binding(Key.V, ctrl: true) }),
        (EditorAction.Delete, "Delete selection", new[] { new Binding(Key.Delete), new Binding(Key.Backspace) }),
        (EditorAction.Mirror, "Mirror left/right", new[] { new Binding(Key.H, ctrl: true) }),
        (EditorAction.Reverse, "Reverse in time", new[] { new Binding(Key.J, ctrl: true) }),
        (EditorAction.NudgeLater, "Move selection later", new[] { new Binding(Key.UpArrow, alt: true) }),
        (EditorAction.NudgeEarlier, "Move selection earlier", new[] { new Binding(Key.DownArrow, alt: true) }),
        (EditorAction.NudgeLeft, "Move selection left", new[] { new Binding(Key.LeftArrow, alt: true) }),
        (EditorAction.NudgeRight, "Move selection right", new[] { new Binding(Key.RightArrow, alt: true) }),
        (EditorAction.Resnap, "Snap selection to the grid", new[] { new Binding(Key.G, ctrl: true) }),
        (EditorAction.Save, "Save", new[] { new Binding(Key.S, ctrl: true) }),
        (EditorAction.Export, "Export as a pack", new[] { new Binding(Key.E, ctrl: true) }),
        (EditorAction.Rename, "Name the chart", new[] { new Binding(Key.F2) }),
        (EditorAction.SetAuthor, "Set the author", new[] { new Binding(Key.F3) }),
    };

    private const string KeysPref = "NocturneFlatScroll.EditorKeys.v1";
    private static Dictionary<EditorAction, Binding[]>? bindings;

    private static Dictionary<EditorAction, Binding[]> Bindings
    {
        get
        {
            if (bindings != null) return bindings;
            bindings = DefaultBindings.ToDictionary(d => d.Action, d => d.Keys);
            // Saved as "Action=mods:Key|mods:Key;..."; anything unreadable keeps its default.
            foreach (var entry in PlayerPrefs.GetString(KeysPref, "").Split(';'))
            {
                int eq = entry.IndexOf('=');
                if (eq < 0 || !Enum.TryParse(entry.Substring(0, eq), out EditorAction action)) continue;
                var list = new List<Binding>();
                foreach (var part in entry.Substring(eq + 1).Split('|'))
                    if (Binding.TryLoad(part, out var b)) list.Add(b);
                bindings[action] = list.ToArray();
            }
            return bindings;
        }
    }

    private static void SaveBindings()
    {
        var sb = new StringBuilder();
        foreach (var (action, keys) in Bindings)
            sb.Append(action).Append('=').Append(string.Join("|", keys.Select(k => k.Save()))).Append(';');
        PlayerPrefs.SetString(KeysPref, sb.ToString());
        PlayerPrefs.Save();
    }

    private static void ResetBindings()
    {
        PlayerPrefs.DeleteKey(KeysPref);
        PlayerPrefs.Save();
        bindings = null;
    }

    private static string KeysFor(EditorAction action) =>
        Bindings.TryGetValue(action, out var keys) && keys.Length > 0 ? string.Join(" / ", keys.Select(k => k.ToString())) : "(none)";

    private static string ShortKey(EditorAction action) =>
        Bindings.TryGetValue(action, out var keys) && keys.Length > 0 ? keys[0].ToString() : "";

    /// <summary>Whether one of the action's keys went down this frame with exactly its modifiers.</summary>
    private static bool Triggered(InputKeyboard k, EditorAction action)
    {
        if (!Bindings.TryGetValue(action, out var keys)) return false;
        bool ctrl = Ctrl(k), shift = Shift(k), alt = Alt(k);
        foreach (var b in keys)
            if (b.Key != Key.None && k[b.Key].wasPressedThisFrame && b.Ctrl == ctrl && b.Shift == shift && b.Alt == alt) return true;
        return false;
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.Digit1 and <= Key.Digit9 => ((int)(key - Key.Digit1) + 1).ToString(),
        Key.Digit0 => "0",
        Key.UpArrow => "Up",
        Key.DownArrow => "Down",
        Key.LeftArrow => "Left",
        Key.RightArrow => "Right",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        Key.Comma => ",",
        Key.Period => ".",
        Key.Minus => "-",
        Key.Equals => "=",
        Key.Backspace => "Bksp",
        Key.Delete => "Del",
        _ => key.ToString(),
    };

    // ---- the Keys tab: click an action, then press its new key ------------------------------

    private static EditorAction? rebinding;

    private static readonly Key[] ModifierKeys = { Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift, Key.LeftAlt, Key.RightAlt, Key.LeftMeta, Key.RightMeta };

    /// <summary>While waiting for a new key: the next non-modifier key becomes the action's key.</summary>
    private static void UpdateRebinding(InputKeyboard k)
    {
        if (rebinding == null) return;
        if (Pressed(k, Key.Escape)) { rebinding = null; Say("Kept the old key", 2f); return; }
        foreach (var control in k.allKeys)
        {
            if (control == null || !control.wasPressedThisFrame) continue;
            var key = control.keyCode;
            if (ModifierKeys.Contains(key) || key == Key.Escape) continue;
            var action = rebinding.Value;
            var binding = new Binding(key, Ctrl(k), Shift(k), Alt(k));
            // One key does one thing: take it off any other action first.
            foreach (var other in Bindings.Keys.ToList())
                Bindings[other] = Bindings[other].Where(b => !(b.Key == binding.Key && b.Ctrl == binding.Ctrl && b.Shift == binding.Shift && b.Alt == binding.Alt)).ToArray();
            Bindings[action] = new[] { binding };
            SaveBindings();
            rebinding = null;
            Say($"{LabelOf(action)}: {binding}", 3f);
            return;
        }
    }

    private static string LabelOf(EditorAction action) => DefaultBindings.First(d => d.Action == action).Label;
}
