using System.Text;
using UnityEngine;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using Key = UnityEngine.InputSystem.Key;

namespace NocturneFlatScroll;

/// <summary>
/// Keyboard reading shared by the editor screens: keys and modifiers, list movement, typing into
/// text fields (a plain key table for .sm fields, or full Unicode text for things like dialogue),
/// and <see cref="KeyMap{TAction}"/> for rebindable actions.
/// </summary>
internal static class EditorInput
{
    internal static bool Pressed(InputKeyboard keyboard, Key key) => keyboard[key].wasPressedThisFrame;
    internal static bool Held(InputKeyboard keyboard, Key key) => keyboard[key].isPressed;
    internal static bool Ctrl(InputKeyboard keyboard) => Held(keyboard, Key.LeftCtrl) || Held(keyboard, Key.RightCtrl);
    internal static bool Shift(InputKeyboard keyboard) => Held(keyboard, Key.LeftShift) || Held(keyboard, Key.RightShift);
    internal static bool Alt(InputKeyboard keyboard) => Held(keyboard, Key.LeftAlt) || Held(keyboard, Key.RightAlt);

    internal static readonly Key[] ModifierKeys = { Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift, Key.LeftAlt, Key.RightAlt, Key.LeftMeta, Key.RightMeta };

    /// <summary>Up/Down move by one, Page Up/Down by ten; the result stays in the list.</summary>
    internal static int MoveInList(InputKeyboard keyboard, int index, int count)
    {
        if (count == 0) return 0;
        if (Pressed(keyboard, Key.DownArrow)) index++;
        if (Pressed(keyboard, Key.UpArrow)) index--;
        if (Pressed(keyboard, Key.PageDown)) index += 10;
        if (Pressed(keyboard, Key.PageUp)) index -= 10;
        return Math.Clamp(index, 0, count - 1);
    }

    internal static string KeyName(Key key) => key switch
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
        Key.LeftBracket => "[",
        Key.RightBracket => "]",
        Key.Backspace => "Bksp",
        Key.Delete => "Del",
        _ => key.ToString(),
    };

    // ---- typing from a key table: US letters, digits and a little punctuation ----------------

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

    /// <summary>
    /// Adds typed characters to <paramref name="text"/> from the key table; Backspace deletes.
    /// True when it changed. Fine for .sm fields (see <see cref="CleanText"/>); dialogue and other
    /// free text should use <see cref="TypeText"/>, which follows the keyboard layout.
    /// </summary>
    internal static bool TypeInto(InputKeyboard keyboard, ref string text, int max = 40)
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

    /// <summary>
    /// Typed text goes into the .sm file, where ':' and ';' end a value, '//' starts a comment and
    /// a '#' at the start of a line starts a tag, so those are left out.
    /// </summary>
    internal static string CleanText(string text)
    {
        var clean = text.Replace(":", " ").Replace(";", " ");
        while (clean.Contains("//")) clean = clean.Replace("//", "/");
        return clean.Trim().TrimStart('#').Trim();
    }

    // ---- Unicode text entry -------------------------------------------------------------------
    //
    // The keyboard's onTextInput event gives the characters Windows makes from the key presses:
    // the player's keyboard layout, Shift and Caps Lock, dead keys and accents, and every
    // punctuation mark. A screen calls BeginText when a field starts taking text, TypeText each
    // frame while it does, and EndText when it's done. Until the first character arrives through
    // the event, the key table above stands in, one frame late, so a key is never typed twice.

    private static InputKeyboard? textKeyboard;
    private static Il2CppSystem.Action<char>? textHandler;
    private static readonly StringBuilder textQueue = new();
    private static bool textListening, textEventsSeen, textEventsBroken, reportedTextError;
    private static int textBeganFrame = -1;
    private static string tableFallback = "";

    /// <summary>Starts taking typed characters; anything typed before now is dropped.</summary>
    internal static void BeginText()
    {
        textQueue.Clear();
        tableFallback = "";
        textListening = true;
        textBeganFrame = Time.frameCount;
        Attach(InputKeyboard.current);
    }

    /// <summary>Stops taking typed characters.</summary>
    internal static void EndText()
    {
        textListening = false;
        textQueue.Clear();
        tableFallback = "";
        Detach();
    }

    /// <summary>
    /// Adds the characters typed since the last call to <paramref name="text"/>, up to
    /// <paramref name="max"/> characters. Backspace deletes a character, Ctrl+Backspace a word.
    /// Enter, Tab, Escape and other control keys are left to the screen. True when the text changed.
    /// </summary>
    internal static bool TypeText(InputKeyboard keyboard, ref string text, int max = 500)
    {
        if (!textListening) BeginText();
        Attach(keyboard);
        string before = text;
        string typed = textQueue.ToString();
        textQueue.Clear();
        if (typed.Length > 0) textEventsSeen = true;
        if (!textEventsSeen)
        {
            // No characters from the event yet: use last frame's keys, then collect this frame's.
            // The key that started the field doesn't count.
            if (typed.Length == 0) typed = tableFallback;
            tableFallback = "";
            if (Time.frameCount != textBeganFrame && !Ctrl(keyboard) && !Alt(keyboard))
                foreach (var (key, lower, upper) in TypingKeys)
                    if (Pressed(keyboard, key)) tableFallback += Shift(keyboard) ? upper : lower;
        }
        text = TextEditing.Append(text, typed, max);
        if (Pressed(keyboard, Key.Backspace) && text.Length > 0) text = TextEditing.DeleteBack(text, word: Ctrl(keyboard));
        return text != before;
    }

    // The interop hands out a new wrapper object for the same keyboard, so compare the game's own.
    private static bool SameKeyboard(InputKeyboard? a, InputKeyboard? b) => a != null && b != null && a.Pointer == b.Pointer;

    private static void Attach(InputKeyboard? keyboard)
    {
        if (keyboard == null || textEventsBroken || SameKeyboard(keyboard, textKeyboard)) return;
        Detach();
        try
        {
            // Kept in a field so the delegate stays alive while the game holds it.
            textHandler ??= (Il2CppSystem.Action<char>)(Action<char>)OnTextInput;
            keyboard.add_onTextInput(textHandler);
            textKeyboard = keyboard;
        }
        catch (Exception ex)
        {
            // Typing still works from the key table; don't try again every frame.
            textEventsBroken = true;
            ModLog.Error("Listening for typed text failed; typing uses the plain key table: " + ex.Message);
        }
    }

    private static void Detach()
    {
        var keyboard = textKeyboard;
        textKeyboard = null;
        if (keyboard == null || textHandler == null) return;
        try { keyboard.remove_onTextInput(textHandler); }
        catch (Exception ex)
        {
            if (!reportedTextError) ModLog.Error("Removing the typed text listener failed: " + ex.Message);
            reportedTextError = true;
        }
    }

    private static void OnTextInput(char c)
    {
        // Runs inside the input system's update; nothing here may throw back into the game.
        try
        {
            if (textListening && textQueue.Length < 4096) textQueue.Append(c);
        }
        catch { }
    }
}

/// <summary>The string edits behind <see cref="EditorInput.TypeText"/>, apart from the keyboard.</summary>
internal static class TextEditing
{
    /// <summary>
    /// Adds the typed characters that are text, while the result stays within
    /// <paramref name="max"/> characters. Control characters (Backspace, Enter, Tab, Escape,
    /// Ctrl+letters) are skipped, and a surrogate pair goes in whole or not at all.
    /// </summary>
    internal static string Append(string text, string typed, int max)
    {
        if (typed.Length == 0) return text;
        var sb = new StringBuilder(text);
        for (int i = 0; i < typed.Length; i++)
        {
            char c = typed[i];
            if (char.IsControl(c)) continue;
            bool pair = char.IsHighSurrogate(c) && i + 1 < typed.Length && char.IsLowSurrogate(typed[i + 1]);
            if (!pair && char.IsSurrogate(c)) continue;   // half a pair isn't text
            int size = pair ? 2 : 1;
            if (sb.Length + size <= max)
            {
                sb.Append(c);
                if (pair) sb.Append(typed[i + 1]);
            }
            if (pair) i++;
        }
        return sb.ToString();
    }

    /// <summary>Removes the last character (both halves of a surrogate pair), or with <paramref name="word"/> the last word and the spaces after it.</summary>
    internal static string DeleteBack(string text, bool word)
    {
        if (text.Length == 0) return text;
        int i = text.Length;
        if (word)
        {
            while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
            while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
        }
        else
        {
            i--;
            if (i > 0 && char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1])) i--;
        }
        return text.Substring(0, i);
    }
}

/// <summary>A key with the modifiers that must be held with it.</summary>
internal readonly struct KeyBinding
{
    internal readonly Key Key;
    internal readonly bool Ctrl, Shift, Alt;

    internal KeyBinding(Key key, bool ctrl = false, bool shift = false, bool alt = false)
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
        sb.Append(EditorInput.KeyName(Key));
        return sb.ToString();
    }

    internal bool SameAs(KeyBinding other) => Key == other.Key && Ctrl == other.Ctrl && Shift == other.Shift && Alt == other.Alt;

    internal string Save() => $"{(Ctrl ? "C" : "")}{(Shift ? "S" : "")}{(Alt ? "A" : "")}:{Key}";

    internal static bool TryLoad(string text, out KeyBinding binding)
    {
        binding = default;
        int colon = text.IndexOf(':');
        if (colon < 0 || !Enum.TryParse(text.Substring(colon + 1), out Key key) || key == Key.None) return false;
        string mods = text.Substring(0, colon);
        binding = new KeyBinding(key, mods.Contains('C'), mods.Contains('S'), mods.Contains('A'));
        return true;
    }
}

/// <summary>
/// One screen's rebindable actions: each has a label and default keys, and the player's own keys
/// are saved per PC in the player prefs under the screen's own key, as
/// "Action=mods:Key|mods:Key;...". Anything unreadable keeps its default.
/// </summary>
internal sealed class KeyMap<TAction> where TAction : struct, Enum
{
    private readonly string pref;
    private Dictionary<TAction, KeyBinding[]>? bindings;

    internal KeyMap(string pref, (TAction Action, string Label, KeyBinding[] Keys)[] defaults)
    {
        this.pref = pref;
        Defaults = defaults;
    }

    internal (TAction Action, string Label, KeyBinding[] Keys)[] Defaults { get; }

    /// <summary>The action waiting for a new key on a Keys screen, if any.</summary>
    internal TAction? Rebinding { get; set; }

    internal Dictionary<TAction, KeyBinding[]> Bindings
    {
        get
        {
            if (bindings != null) return bindings;
            bindings = Defaults.ToDictionary(d => d.Action, d => d.Keys);
            var saved = new HashSet<TAction>();
            foreach (var entry in PlayerPrefs.GetString(pref, "").Split(';'))
            {
                int eq = entry.IndexOf('=');
                if (eq < 0 || !Enum.TryParse(entry.Substring(0, eq), out TAction action)) continue;
                var list = new List<KeyBinding>();
                foreach (var part in entry.Substring(eq + 1).Split('|'))
                    if (KeyBinding.TryLoad(part, out var b)) list.Add(b);
                bindings[action] = list.ToArray();
                saved.Add(action);
            }
            // An action added since the keys were saved starts with its default keys, less any the
            // player already gave to another action: one key does one thing.
            var loaded = bindings;
            if (saved.Count > 0)
                foreach (var (action, _, keys) in Defaults)
                    if (!saved.Contains(action))
                        loaded[action] = keys.Where(k => !saved.Any(other => loaded[other].Any(b => b.SameAs(k)))).ToArray();
            return loaded;
        }
    }

    internal void Save()
    {
        var sb = new StringBuilder();
        foreach (var (action, keys) in Bindings)
            sb.Append(action).Append('=').Append(string.Join("|", keys.Select(k => k.Save()))).Append(';');
        PlayerPrefs.SetString(pref, sb.ToString());
        PlayerPrefs.Save();
    }

    /// <summary>Back to the default keys.</summary>
    internal void Reset()
    {
        PlayerPrefs.DeleteKey(pref);
        PlayerPrefs.Save();
        bindings = null;
    }

    internal string KeysFor(TAction action) =>
        Bindings.TryGetValue(action, out var keys) && keys.Length > 0 ? string.Join(" / ", keys.Select(k => k.ToString())) : "(none)";

    internal string ShortKey(TAction action) =>
        Bindings.TryGetValue(action, out var keys) && keys.Length > 0 ? keys[0].ToString() : "";

    internal string LabelOf(TAction action) =>
        Defaults.First(d => EqualityComparer<TAction>.Default.Equals(d.Action, action)).Label;

    /// <summary>Whether one of the action's keys went down this frame with exactly its modifiers.</summary>
    internal bool Triggered(InputKeyboard k, TAction action)
    {
        if (!Bindings.TryGetValue(action, out var keys)) return false;
        bool ctrl = EditorInput.Ctrl(k), shift = EditorInput.Shift(k), alt = EditorInput.Alt(k);
        foreach (var b in keys)
            if (b.Key != Key.None && k[b.Key].wasPressedThisFrame && b.Ctrl == ctrl && b.Shift == shift && b.Alt == alt) return true;
        return false;
    }

    /// <summary>
    /// While <see cref="Rebinding"/> waits for a new key: the next non-modifier key (with the
    /// modifiers held) becomes the action's only key, and is taken off any other action. Escape
    /// keeps the old key. <paramref name="say"/> shows the outcome.
    /// </summary>
    internal void UpdateRebinding(InputKeyboard k, Action<string, float> say)
    {
        if (Rebinding == null) return;
        if (EditorInput.Pressed(k, Key.Escape)) { Rebinding = null; say("Kept the old key", 2f); return; }
        foreach (var control in k.allKeys)
        {
            if (control == null || !control.wasPressedThisFrame) continue;
            var key = control.keyCode;
            if (EditorInput.ModifierKeys.Contains(key) || key == Key.Escape) continue;
            var action = Rebinding.Value;
            var binding = new KeyBinding(key, EditorInput.Ctrl(k), EditorInput.Shift(k), EditorInput.Alt(k));
            // One key does one thing: take it off any other action first.
            foreach (var other in Bindings.Keys.ToList())
                Bindings[other] = Bindings[other].Where(b => !b.SameAs(binding)).ToArray();
            Bindings[action] = new[] { binding };
            Save();
            Rebinding = null;
            say($"{LabelOf(action)}: {binding}", 3f);
            return;
        }
    }
}
