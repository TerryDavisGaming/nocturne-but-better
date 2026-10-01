using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;
using Object = UnityEngine.Object;

namespace NocturnePlus;

/// <summary>
/// Held with a slot's key, it quick saves; the key alone quick loads that slot. No Alt: Alt+F4
/// closes the game, and overlays take Alt with the function keys.
/// </summary>
internal enum SaveModifier { Ctrl, Shift }

/// <summary>
/// Quick save and quick load, in up to 12 slots, each its own file (ProdSaveF1.sav to ProdSaveF12.sav,
/// beside the game's saves; the game's own save list never shows them). With "Quick save &amp; load"
/// on, holding the save modifier (Ctrl unless changed) with a slot's key (F1 to F11 unless changed)
/// quick saves: the game's own autosave, then the file it wrote is copied into the slot. The key
/// alone quick loads that slot's file through the game's own Load Game (the game's quick load
/// can't: it goes back to the save it keeps in memory, the last one made). Loading never writes a
/// file, so the player's own autosave and save slots stay as they were. Each slot's key is
/// rebindable, and the switch, the save modifier and the slot stepper are the last rows of
/// Options > Gameplay.
/// Neither works on the title, in a battle, in a cutscene or dialogue, between screens, while the
/// game is paused, in the arcade, or during a chart editor test.
/// The game's save and load calls are looked up when the mod starts: a missing one only turns its
/// half off (and says why), and the rest of the mod is unaffected.
/// </summary>
internal static class QuickSaveLoad
{
    internal const int SlotCount = 12;
    private const string ModifierPref = "NocturnePlus.QuickSaveModifier.v1";
    private const string SlotKeysPref = "NocturnePlus.QuickSlotKeys.v1";
    private const string AutoSaveFile = "ProdAutoSave.sav";
    private const float Cooldown = 1.5f;
    private const float RebindTimeout = 10f;
    // After a quick save, the game's autosave file is copied into the slot once it has stopped
    // changing for WriteSettle seconds; WriteTimeout after the save without that, the slot is left alone.
    private const float WriteSettle = 0.25f;
    private const float WriteTimeout = 4f;

    // Slot 12 starts with no key: F12 is Steam's screenshot key, and every screenshot would load it.
    private static readonly Key[] DefaultSlotKeys =
    {
        Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.None,
    };

    // Keys a slot can take: none of them is one of the game's own keyboard controls.
    private static readonly HashSet<Key> SlotKeysAllowed = new()
    {
        Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12,
        Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9, Key.Digit0,
        Key.Numpad1, Key.Numpad2, Key.Numpad3, Key.Numpad4, Key.Numpad5, Key.Numpad6, Key.Numpad7, Key.Numpad8, Key.Numpad9, Key.Numpad0,
        Key.Insert, Key.Delete, Key.Home, Key.End, Key.PageUp, Key.PageDown,
    };

    // Ctrl, not Shift: Shift is the game's run key, so a player running with it would save over a slot
    // when they meant to load it.
    private const SaveModifier DefaultModifier = SaveModifier.Ctrl;

    private static Key[] slotKeys = (Key[])DefaultSlotKeys.Clone();
    private static SaveModifier modifier = DefaultModifier;
    // Which slot the stepper row is showing; changing it doesn't touch any save.
    private static int currentSlot;

    /// <summary>Slot <paramref name="slot"/>'s file (0-based), beside ProdAutoSave.sav and ProdSlotNNNN.sav.</summary>
    private static string SlotFileName(int slot) => $"ProdSaveF{slot + 1}.sav";

    // The game's calls, looked up once (see Install). Null means that half can't run.
    private static MethodInfo? saveCall, loadCall, canPauseCall;
    private static PropertyInfo? guiInstance;
    private static string? saveMissing, loadMissing;
    private static bool installed;

    private static float nextActionAt;
    private static readonly object RebindOwner = new();
    // The slot waiting for a new key, or -1.
    private static int rebindSlot = -1;
    private static int rebindFrame = -1;
    private static float rebindStartedAt;
    private static bool rebindLocked;

    // A quick save waiting for the game's write: the folder, when the save was asked for, and the slot
    // the file goes to. The write is watched every frame (WatchWrite).
    private static string? pendingFolder;
    private static DateTime pendingSince;
    private static float pendingStartedAt;
    private static int pendingSlot = -1;
    private static (DateTime Time, long Size) pendingSeen;
    private static float pendingStableSince = -1f;

    // ---- the options rows -----------------------------------------------------------------------

    // The last rows of the gameplay page, in this order (OptionsMenuIntegration puts them there).
    internal static readonly OptionsMenuIntegration.RowSpec[] Rows =
    {
        new("StateToggle_PlusQuickSaveLoad",
            "Quick save & load",
            "Hold the save modifier with a slot's key to quick save there, anywhere you can walk around (it is also the game's autosave); the slot's key alone quick loads it. Your own save slots are never written over.",
            new[] { "Off", "On" },
            () => SettingsState.QuickSaveLoad ? 1 : 0,
            (direction, click) => SettingsState.SetQuickSaveLoad(!SettingsState.QuickSaveLoad)),
        new("StateToggle_PlusQuickSaveModifier",
            "Quick save modifier",
            "Hold this with a slot's key (below) to quick save to that slot; the key alone quick loads it. Shift is also the game's run key, so with Shift a slot key pressed while running saves.",
            () => new[] { modifier.ToString() },
            () => 0,
            (direction, click) => ChangeModifier(direction)),
        new("StateToggle_PlusQuickSaveSlot",
            "Quick save slot",
            "Left and right pick a slot. Click, then press its new key: a function key, a number, or Insert, Delete, Home, End, Page Up or Page Down (Esc keeps the old one).",
            () => new[] { SlotLabel() },
            () => 0,
            (direction, click) => ChangeSlot(direction, click)),
    };

    private static string SlotLabel()
    {
        if (rebindSlot == currentSlot) return $"{currentSlot + 1}: press a key...";
        string key = slotKeys[currentSlot] == Key.None ? "(none)" : EditorInput.KeyName(slotKeys[currentSlot]);
        string info = SlotInfo(currentSlot);
        return info.Length > 0 ? $"{currentSlot + 1}: {key}  ({info})" : $"{currentSlot + 1}: {key}";
    }

    /// <summary>"empty", "saved 3m ago", or "" when the save folder can't be found yet.</summary>
    private static string SlotInfo(int slot)
    {
        try
        {
            var saveFile = ArcadeSession.SaveFile();
            if (saveFile == null) return "";
            string path = Path.Combine(ArcadeSession.SaveFolder(saveFile), SlotFileName(slot));
            if (!File.Exists(path)) return "empty";
            return "saved " + AgeText(DateTime.UtcNow - File.GetLastWriteTimeUtc(path));
        }
        catch (Exception ex)
        {
            if (!reportedSlotInfo) ModLog.Error("Quick save: reading a slot's file for the options row failed: " + ex.Message);
            reportedSlotInfo = true;
            return "";
        }
    }

    private static bool reportedSlotInfo;

    private static string AgeText(TimeSpan age)
    {
        if (age.TotalSeconds < 60) return "just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    private static void ChangeModifier(int direction)
    {
        var values = (SaveModifier[])Enum.GetValues(typeof(SaveModifier));
        int step = direction == 0 ? 1 : Math.Sign(direction);
        int index = ((Array.IndexOf(values, modifier) + step) % values.Length + values.Length) % values.Length;
        modifier = values[index];
        SavePrefs();
        ModLog.Info("Quick save modifier: " + modifier);
    }

    private static void ChangeSlot(int direction, bool click)
    {
        if (click)
        {
            StartRebind(currentSlot);
            return;
        }
        StopRebind(null);
        int step = direction == 0 ? 1 : Math.Sign(direction);
        currentSlot = ((currentSlot + step) % SlotCount + SlotCount) % SlotCount;
    }

    /// <summary>Every slot key back to F1 through F11 (slot 12 none), and the save modifier back to Ctrl.</summary>
    internal static void ResetKeys()
    {
        StopRebind(null);
        slotKeys = (Key[])DefaultSlotKeys.Clone();
        modifier = DefaultModifier;
        currentSlot = 0;
        SavePrefs();
    }

    // ---- rebinding a slot's key --------------------------------------------------------------------

    /// <summary>Whether a slot's new key is being awaited.</summary>
    internal static bool Rebinding => rebindSlot >= 0;

    private static void StartRebind(int slot)
    {
        if (rebindSlot == slot) return;
        rebindSlot = slot;
        rebindFrame = Time.frameCount;
        rebindStartedAt = Time.unscaledTime;
        // The menus underneath don't move, select or go back while the key is awaited.
        if (!rebindLocked)
        {
            EditorOverlay.Enter(RebindOwner);
            rebindLocked = true;
        }
        string current = slotKeys[slot] == Key.None ? "(none)" : EditorInput.KeyName(slotKeys[slot]);
        Toast.Show($"Slot {slot + 1}: press a key (Esc keeps {current})", RebindTimeout);
        // A click gives up (UpdateRebind reads the mouse itself); it mustn't also press the row under it.
        Toast.BlockClicks(true);
    }

    private static void StopRebind(string? message)
    {
        bool was = rebindSlot >= 0;
        rebindSlot = -1;
        Toast.BlockClicks(false);
        if (rebindLocked)
        {
            EditorOverlay.Leave(RebindOwner);
            rebindLocked = false;
        }
        if (message != null) Toast.Show(message, 2.5f);
        else if (was) Toast.Hide();
        if (was) OptionsMenuIntegration.RefreshAll();
    }

    private static void UpdateRebind(InputKeyboard keyboard)
    {
        // The press that started it isn't the new key.
        if (Time.frameCount <= rebindFrame) return;
        if (Time.unscaledTime - rebindStartedAt > RebindTimeout || !Application.isFocused)
        {
            StopRebind("Kept the old key");
            return;
        }
        var mouse = InputMouse.current;
        if (mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
        {
            StopRebind("Kept the old key");
            return;
        }
        if (EditorInput.Pressed(keyboard, Key.Escape) || PadPressed())
        {
            StopRebind("Kept the old key");
            return;
        }
        foreach (var control in keyboard.allKeys)
        {
            if (control == null || !control.wasPressedThisFrame) continue;
            var key = control.keyCode;
            if (EditorInput.ModifierKeys.Contains(key) || key == Key.Escape) continue;
            int slot = rebindSlot;
            if (!SlotKeysAllowed.Contains(key))
            {
                // The game's own keys (walking, confirm, the lanes) would load a slot on every press.
                rebindStartedAt = Time.unscaledTime;
                Toast.Show($"{EditorInput.KeyName(key)} is the game's; slot {slot + 1} takes a function key, a number, or Insert/Delete/Home/End/Page Up/Page Down", RebindTimeout);
                return;
            }
            // One key does one thing: a slot that had it gets this slot's old key instead.
            var old = slotKeys[slot];
            int other = Array.IndexOf(slotKeys, key);
            string also = "";
            if (other >= 0 && other != slot)
            {
                slotKeys[other] = old;
                also = old == Key.None ? $" (slot {other + 1} now has no key)" : $" (slot {other + 1} now has {EditorInput.KeyName(old)})";
            }
            slotKeys[slot] = key;
            SavePrefs();
            StopRebind($"Slot {slot + 1}: {EditorInput.KeyName(key)}{also}");
            return;
        }
    }

    // A pad can't give a slot a key, so any of its buttons gives up (the rebind holds the menus, Back included).
    private static bool PadPressed()
    {
        var pad = UnityEngine.InputSystem.Gamepad.current;
        return pad != null && (pad.buttonSouth.wasPressedThisFrame || pad.buttonEast.wasPressedThisFrame || pad.buttonWest.wasPressedThisFrame ||
                               pad.buttonNorth.wasPressedThisFrame || pad.startButton.wasPressedThisFrame || pad.selectButton.wasPressedThisFrame);
    }

    // ---- prefs ---------------------------------------------------------------------------------------

    private static void LoadPrefs()
    {
        try
        {
            string saved = PlayerPrefs.GetString(SlotKeysPref, "");
            var parts = saved.Length > 0 ? saved.Split(',') : Array.Empty<string>();
            if (parts.Length == SlotCount)
            {
                var keys = new Key[SlotCount];
                bool ok = true;
                for (int i = 0; i < SlotCount && ok; i++) ok = Enum.TryParse(parts[i], out keys[i]);
                if (ok) slotKeys = keys;
            }
        }
        catch (Exception ex) { ModLog.Error("Quick save: reading the slot keys failed, so they're the defaults: " + ex.Message); }
        try
        {
            int saved = PlayerPrefs.GetInt(ModifierPref, (int)DefaultModifier);
            if (Enum.IsDefined(typeof(SaveModifier), saved)) modifier = (SaveModifier)saved;
        }
        catch (Exception ex) { ModLog.Error("Quick save: reading the save modifier failed, so it's Ctrl: " + ex.Message); }
    }

    private static void SavePrefs()
    {
        try
        {
            PlayerPrefs.SetString(SlotKeysPref, string.Join(",", slotKeys.Select(k => k.ToString())));
            PlayerPrefs.SetInt(ModifierPref, (int)modifier);
            PlayerPrefs.Save();
        }
        catch (Exception ex) { ModLog.Error("Quick save: saving the slot keys or the save modifier failed: " + ex.Message); }
    }

    // ---- start-up ---------------------------------------------------------------------------------

    /// <summary>
    /// Looks up the game's calls. Quick save uses the save manager's AutoSave, called with no
    /// arguments or only default ones. Quick load uses the Load Game menu's LoadGame(file, slot), the
    /// call behind picking a save there.
    /// </summary>
    internal static void Install()
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var saves = typeof(SaveFileManager).GetMethods(All);
        ModLog.Info("Quick save: the save manager's save calls: " + string.Join(", ",
            saves.Where(m => m.Name.Contains("Save", StringComparison.Ordinal) && !m.IsSpecialName).Select(Describe)) + ".");
        // Only the autosave: the save slots the player saved by hand are never written over.
        saveCall = saves.Where(m => m.Name == "AutoSave" && Callable(m)).OrderBy(m => m.GetParameters().Length).FirstOrDefault();
        saveMissing = saveCall == null ? "the game's autosave can't be called" : null;

        loadCall = typeof(LoadGameMenu).GetMethods(All).FirstOrDefault(m => m.Name == "LoadGame" && !m.IsStatic &&
            m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(string), typeof(int) }));
        loadMissing = loadCall == null ? "the game's Load Game call isn't there" : null;

        canPauseCall = typeof(NocturneGui).GetMethods(All).FirstOrDefault(m => m.Name == "CanPauseGameState" && m.ReturnType == typeof(bool) &&
            m.GetParameters().All(p => p.HasDefaultValue || p.ParameterType == typeof(GameStates)));
        if (canPauseCall != null && !canPauseCall.IsStatic)
        {
            guiInstance = typeof(NocturneGui).GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (guiInstance == null || !typeof(NocturneGui).IsAssignableFrom(guiInstance.PropertyType)) canPauseCall = null;
        }

        LoadPrefs();
        ModLog.Info($"Quick save: {(saveCall != null ? "uses SaveFileManager." + Describe(saveCall) : "off, " + saveMissing)}; " +
                    $"quick load: {(loadCall != null ? "uses LoadGameMenu." + Describe(loadCall) : "off, " + loadMissing)}. " +
                    $"{SlotCount} slots, save modifier {modifier}. Game states: {string.Join(", ", Enum.GetNames(typeof(GameStates)))}.");
        installed = true;
    }

    private static bool Callable(MethodInfo method) => !method.ContainsGenericParameters && method.GetParameters().All(p => p.HasDefaultValue);

    private static object?[] Arguments(MethodInfo method) => method.GetParameters()
        .Select(p => p.DefaultValue is DBNull || p.DefaultValue == Missing.Value ? null : p.DefaultValue).ToArray();

    private static string Describe(MethodInfo method) =>
        $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";

    // ---- every frame ------------------------------------------------------------------------------

    /// <summary>Whether the mod's quick save message is on screen and starts fading out within <paramref name="seconds"/>; until then it doesn't move.</summary>
    internal static bool MessageFadesWithin(float seconds) => Toast.FadesWithin(seconds);

    /// <summary>Called every frame: the slot keys, a key being rebound, and the message on screen.</summary>
    internal static void Update()
    {
        try
        {
            Toast.Update();
            WatchWrite();
            if (rebindSlot >= 0)
            {
                var pressed = InputKeyboard.current;
                if (pressed == null) StopRebind(null);
                else UpdateRebind(pressed);
                return;
            }
            // Checked first: it's the cheapest, and off is the usual case.
            if (!installed || !SettingsState.QuickSaveLoad) return;
            var keyboard = InputKeyboard.current;
            if (keyboard == null || !Application.isFocused) return;
            // The mod's own pages use these keys for themselves (F5 tests in the chart editor),
            // and the title has nothing to save.
            if (EditorOverlay.IsOpen || GameManager.GameState == GameStates.MainMenu) return;
            if (Time.unscaledTime < nextActionAt) return;
            int slot = -1;
            for (int i = 0; i < SlotCount && slot < 0; i++)
                if (slotKeys[i] != Key.None && keyboard[slotKeys[i]].wasPressedThisFrame) slot = i;
            if (slot < 0) return;
            bool? save = Intent(keyboard);
            if (save == null) return;
            nextActionAt = Time.unscaledTime + Cooldown;
            // Nothing new until the last quick save is in its slot: a load now would change the
            // autosave file before it's copied.
            if (pendingFolder != null)
            {
                Toast.Show($"Still saving slot {pendingSlot + 1}", 1.5f);
                return;
            }
            if (save == true) Save(slot); else Load(slot);
        }
        catch (Exception ex)
        {
            if (!reportedUpdate) ModLog.Error("Quick save and load failed: " + ex);
            reportedUpdate = true;
        }
    }

    private static bool reportedUpdate;

    /// <summary>
    /// True to save (exactly the save modifier held), false to load (no modifier; Shift allowed when it
    /// isn't the save modifier, since it's the game's run key), null for anything else: Alt+F4, an
    /// overlay's Alt or Windows-key shortcut, Ctrl+Shift, and a modifier let go just as the key came.
    /// </summary>
    private static bool? Intent(InputKeyboard keyboard)
    {
        var ctrl = Held(keyboard.leftCtrlKey, keyboard.rightCtrlKey, VkControl);
        var shift = Held(keyboard.leftShiftKey, keyboard.rightShiftKey, VkShift);
        if (Held(keyboard.leftAltKey, keyboard.rightAltKey, VkAlt) != Hold.Up ||
            Held(keyboard.leftMetaKey, keyboard.rightMetaKey, VkLeftWin) != Hold.Up || WindowsHeld(VkRightWin)) return null;
        var save = modifier == SaveModifier.Ctrl ? ctrl : shift;
        var other = modifier == SaveModifier.Ctrl ? shift : ctrl;
        if (save == Hold.Unsure)
        {
            // A missed modifier must never turn a save into a load, nor a stale one a load into a save.
            Toast.Show($"Hold {modifier} until the slot key is down to save, or let go of it first to load", 2.5f);
            return null;
        }
        if (save == Hold.Down) return other == Hold.Up ? true : null;
        return ctrl == Hold.Up ? false : null;
    }

    private enum Hold { Up, Down, Unsure }

    /// <summary>
    /// A modifier's state as the slot key comes in. Windows is asked too, since Unity has missed a held
    /// Ctrl. Let go in the same input update as the slot key is unsure: it may or may not have been held
    /// for it. The game's pause menu gets its keys in such bunches (qa saw Ctrl+F4 come in as F4 with Ctrl
    /// up), and a slow frame could too.
    /// </summary>
    private static Hold Held(UnityEngine.InputSystem.Controls.KeyControl left, UnityEngine.InputSystem.Controls.KeyControl right, int windowsKey)
    {
        if (left.isPressed || right.isPressed || WindowsHeld(windowsKey)) return Hold.Down;
        return left.wasReleasedThisFrame || right.wasReleasedThisFrame ? Hold.Unsure : Hold.Up;
    }

    private const int VkShift = 0x10, VkControl = 0x11, VkAlt = 0x12, VkLeftWin = 0x5B, VkRightWin = 0x5C;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private static bool windowsKeysFailed;

    private static bool WindowsHeld(int key)
    {
        if (windowsKeysFailed) return false;
        try { return (GetAsyncKeyState(key) & 0x8000) != 0; }
        catch (Exception ex)
        {
            windowsKeysFailed = true;
            ModLog.Error("Quick save: asking Windows which keys are held failed, so only the game's input is used: " + ex.Message);
            return false;
        }
    }

    private static void Save(int slot)
    {
        if (saveCall == null)
        {
            Toast.Show("Quick save isn't available: " + saveMissing, 3f);
            return;
        }
        string? why = WhyNotNow();
        if (why != null)
        {
            Toast.Show("Can't quick save " + why, 2.5f);
            ModLog.Info("Quick save: refused, " + why + ".");
            return;
        }
        var saveFile = ArcadeSession.SaveFile();
        if (saveFile == null)
        {
            Toast.Show("Can't quick save: the game's saves aren't ready", 2.5f);
            return;
        }
        try
        {
            string folder = ArcadeSession.SaveFolder(saveFile);
            // A second of slack: the file system's write times can be a little behind the clock.
            var since = DateTime.UtcNow.AddSeconds(-1);
            string? how = null;
            try { how = SaveAsTheGameDoes(saveFile); }
            catch (Exception ex)
            {
                if (!reportedFullSave) ModLog.Error("Quick save: the game's own quick save call failed, so the plain autosave is used: " + Inner(ex));
                reportedFullSave = true;
            }
            if (how == null)
            {
                var result = saveCall.Invoke(saveCall.IsStatic ? null : saveFile, Arguments(saveCall));
                if (result is bool ok && !ok)
                {
                    Toast.Show("Quick save didn't work (the game said no)", 3f);
                    ModLog.Error("Quick save: SaveFileManager." + Describe(saveCall) + " returned false.");
                    return;
                }
                how = "SaveFileManager." + Describe(saveCall);
            }
            pendingFolder = folder;
            pendingSince = since;
            pendingStartedAt = Time.unscaledTime;
            pendingSlot = slot;
            pendingSeen = default;
            pendingStableSince = -1f;
            ModLog.Info($"Quick save: slot {slot + 1}, saved with {how}.");
            // The game writes its autosave before AutoSave returns, so the slot is normally made right here.
            if (!CopyWhenWritten(settled: true)) Toast.Show($"Quick saving to slot {slot + 1}...", WriteTimeout);
        }
        catch (Exception ex)
        {
            pendingFolder = null;
            pendingSlot = -1;
            Toast.Show("Quick save failed (see the log)", 3f);
            ModLog.Error("Quick save: the game's save failed: " + Inner(ex));
        }
    }

    private static bool reportedFullSave;

    /// <summary>
    /// The save the game's own quick save points make (its QuickSaveClip): AutoSave with the scene, where
    /// the player stands, their facing, their floor (elevation layer) and the enemies beaten so far. The
    /// plain AutoSave() keeps the facing and floor of the last manual save or load. Null when the player
    /// or the game's enemy list isn't there; kept apart so a game update that drops a member only
    /// fails this call.
    /// </summary>
    private static string? SaveAsTheGameDoes(SaveFileManager saveFile)
    {
        var player = PlayerController.Instance;
        var enemies = EnemyManager.Instance;
        string scene = SceneTransitionController.CurrentSceneName;
        if (player == null || !player || enemies == null || !enemies || string.IsNullOrEmpty(scene)) return null;
        var at = player.transform.position;
        var facing = player.CurrentDirection;
        int floor = player.ElevationLayerIndex;
        saveFile.AutoSave(scene, new Vector2(at.x, at.y), facing, floor, enemies.DefeatedEnemies);
        return $"SaveFileManager.AutoSave in {scene} at ({at.x:0.00}, {at.y:0.00}), facing {facing}, floor {floor}";
    }

    private static void Load(int slot)
    {
        if (loadCall == null)
        {
            Toast.Show("Quick load isn't available: " + loadMissing, 3f);
            return;
        }
        string? why = WhyNotNow();
        if (why != null)
        {
            Toast.Show("Can't quick load " + why, 2.5f);
            ModLog.Info("Quick load: refused, " + why + ".");
            return;
        }
        var saveFile = ArcadeSession.SaveFile();
        if (saveFile == null)
        {
            Toast.Show("Can't quick load: the game's saves aren't ready", 2.5f);
            return;
        }
        string name = SlotFileName(slot);
        if (!File.Exists(Path.Combine(ArcadeSession.SaveFolder(saveFile), name)))
        {
            Toast.Show($"Nothing to quick load in slot {slot + 1}", 2.5f);
            return;
        }
        // Read first, so a damaged file is turned away here instead of in the middle of the game's load.
        int storySlot = GameDataManager.CurrentSaveSlot;
        string stamp;
        int fileSlot;
        SavedGameDataV105 data;
        try
        {
            data = saveFile.LoadAndGetSaveGameData(name);
            if (data == null) throw new InvalidDataException("the game read nothing from it");
            stamp = data.meta != null ? data.meta.timeSaved.ToString() : "no time";
            fileSlot = data.meta != null ? data.meta.slotIndex : storySlot;
        }
        catch (Exception ex)
        {
            Toast.Show($"Slot {slot + 1}'s save can't be read (see the log)", 3f);
            ModLog.Error($"Quick load: {name} can't be read, so nothing is loaded: {Inner(ex)}");
            return;
        }
        // Quick save slots are shared by the game's save slots; one from another playthrough would carry
        // that playthrough's progress into this one's saves.
        if (fileSlot != storySlot)
        {
            Toast.Show($"Slot {slot + 1} is from another playthrough (the game's save slot {fileSlot})", 3.5f);
            ModLog.Info($"Quick load: refused, {name} was saved in the game's save slot {fileSlot}, and slot {storySlot} is being played.");
            return;
        }
        LoadGameMenu? menu = null;
        foreach (var candidate in Resources.FindObjectsOfTypeAll<LoadGameMenu>())
            if (candidate && candidate.gameObject.scene.IsValid()) { menu = candidate; break; }
        if (menu == null)
        {
            Toast.Show("Quick load isn't available here", 2.5f);
            ModLog.Error("Quick load: the game's Load Game menu isn't loaded, so it can't load a save.");
            return;
        }
        try
        {
            // A set-gear battle's gear is never carried into a load.
            BattleGear.Backstop("quick load");
            Toast.Show($"Quick loading slot {slot + 1}...", 2f);
            // The player's own save slot stays the one they play in; only the file read is the slot's.
            loadCall.Invoke(menu, new object[] { name, storySlot });
            ModLog.Info($"Quick load: slot {slot + 1} ({name}, saved {stamp}) into story slot {storySlot}, with LoadGameMenu.{Describe(loadCall)}.");
        }
        catch (Exception ex)
        {
            Toast.Show("Quick load failed (see the log)", 3f);
            ModLog.Error("Quick load: the game's Load Game failed: " + Inner(ex));
            return;
        }
        try { RememberAsLatest(saveFile, data); }
        catch (Exception ex)
        {
            if (!reportedRemember) ModLog.Error("Quick load: the game's own quick load couldn't be pointed at the loaded slot: " + Inner(ex));
            reportedRemember = true;
        }
    }

    private static bool reportedRemember;

    /// <summary>
    /// After a slot load, the game's own quick load (the game over screen's) goes back to that slot, as it
    /// would after a save there: the game keeps its last save in memory for it, which becomes the slot's
    /// save, stamped now so the game counts it as the latest. Nothing is written to disk.
    /// </summary>
    private static void RememberAsLatest(SaveFileManager saveFile, SavedGameDataV105 data)
    {
        if (data.meta != null) data.meta.timeSaved = Il2CppSystem.DateTime.Now;
        saveFile.currentAutoSave = data;
    }

    private static string Inner(Exception ex) => (ex is TargetInvocationException { InnerException: { } inner } ? inner : ex).ToString();

    /// <summary>
    /// Why the story can't be saved or loaded right now, or null when it can: the player walks free,
    /// with nothing else going on.
    /// </summary>
    private static string? WhyNotNow()
    {
        if (ArcadeSession.Active || ArcadeUtility.IsRunning) return "in the arcade";
        if (TestPlay.Active) return "during a test";
        var state = GameManager.GameState;
        if (state == GameStates.MainMenu) return "on the title";
        if (state == GameStates.Combat) return "during a battle";
        if (BusyState(state.ToString())) return "right now";
        var transitions = SceneTransitionController.Instance?.TryCast<SceneTransitionController>();
        if (transitions != null && transitions && (transitions.Loading || transitions.NeedsTempSave)) return "between screens";
        var cutscenes = CutsceneManager.Instance;
        if (cutscenes && cutscenes.Busy) return "during a cutscene";
        if (Time.timeScale <= 0f) return "while the game is paused";
        if (!CanPause()) return "right now";
        return null;
    }

    // Game states that aren't the player walking around, by name, since their list can grow.
    private static readonly string[] BusyStateWords =
    {
        "Menu", "Title", "Combat", "Battle", "Cutscene", "Cinematic", "Dialog", "Pause", "Load", "Transition",
        "Intro", "Credit", "GameOver", "Death", "Arcade", "Shop", "Sequence", "Splash",
    };

    private static bool BusyState(string name)
    {
        foreach (var word in BusyStateWords)
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // The game's own "can the pause menu open now", which is no during cutscenes and fades. When
    // it can't be asked, the other checks decide.
    private static bool CanPause()
    {
        if (canPauseCall == null) return true;
        try
        {
            object? target = null;
            if (!canPauseCall.IsStatic)
            {
                // The game's GUI is a singleton; its instance is looked up the same way.
                target = guiInstance?.GetValue(null);
                if (target == null) return true;
            }
            var args = canPauseCall.GetParameters()
                .Select(p => p.ParameterType == typeof(GameStates) ? (object?)GameManager.GameState : p.DefaultValue is DBNull || p.DefaultValue == Missing.Value ? null : p.DefaultValue)
                .ToArray();
            return canPauseCall.Invoke(target, args) is not false;
        }
        catch (Exception ex)
        {
            if (!reportedPause) ModLog.Error("Quick save: asking whether the game can pause failed, so it isn't asked again: " + Inner(ex));
            reportedPause = true;
            canPauseCall = null;
            return true;
        }
    }

    private static bool reportedPause;

    /// <summary>
    /// Every frame while a quick save is pending (the game didn't write before AutoSave returned): the
    /// slot is made once the autosave file has kept the same time and size for WriteSettle seconds.
    /// Nothing by WriteTimeout leaves the slot as it was.
    /// </summary>
    private static void WatchWrite()
    {
        if (pendingFolder == null || CopyWhenWritten(settled: false)) return;
        if (Time.unscaledTime - pendingStartedAt < WriteTimeout) return;
        int slot = pendingSlot;
        var info = new FileInfo(Path.Combine(pendingFolder, AutoSaveFile));
        bool written = info.Exists && info.LastWriteTimeUtc >= pendingSince;
        EndPending();
        Toast.Show($"Quick save to slot {slot + 1} didn't finish (see the log)", 3f);
        ModLog.Error($"Quick save: {(written ? AutoSaveFile + " kept changing" : "the game didn't write " + AutoSaveFile)} within {WriteTimeout:0} s, " +
                     $"so slot {slot + 1} was left as it was.");
    }

    /// <summary>
    /// Copies the game's autosave file into the pending slot once the game has written it: at once when
    /// <paramref name="settled"/> (its write already returned), or else after it stops changing. The copy
    /// goes through a temporary file, so a failed copy never leaves the slot half-written. False while
    /// still waiting; true once the pending save is over, made or failed.
    /// </summary>
    private static bool CopyWhenWritten(bool settled)
    {
        int slot = pendingSlot;
        string folder = pendingFolder!;
        try
        {
            string autoPath = Path.Combine(folder, AutoSaveFile);
            var info = new FileInfo(autoPath);
            if (!info.Exists || info.LastWriteTimeUtc < pendingSince) return false;
            if (!settled)
            {
                var seen = (info.LastWriteTimeUtc, info.Length);
                if (seen != pendingSeen || pendingStableSince < 0f)
                {
                    pendingSeen = seen;
                    pendingStableSince = Time.unscaledTime;
                    return false;
                }
                if (Time.unscaledTime - pendingStableSince < WriteSettle) return false;
            }
            string slotPath = Path.Combine(folder, SlotFileName(slot));
            string part = slotPath + ".part";
            File.Copy(autoPath, part, overwrite: true);
            File.Move(part, slotPath, overwrite: true);
            float after = Time.unscaledTime - pendingStartedAt;
            EndPending();
            Toast.Show($"Quick saved to slot {slot + 1}", 2f);
            ModLog.Info($"Quick save: slot {slot + 1} now holds this save ({SlotFileName(slot)}, {info.Length} bytes, {after:0.00} s after the save).");
            OptionsMenuIntegration.RefreshAll();
            return true;
        }
        catch (Exception ex)
        {
            EndPending();
            Toast.Show($"Quick save to slot {slot + 1} failed (see the log)", 3f);
            ModLog.Error($"Quick save: copying the save into slot {slot + 1} failed: " + ex.Message);
            return true;
        }
    }

    private static void EndPending()
    {
        pendingFolder = null;
        pendingSlot = -1;
        pendingStableSince = -1f;
    }

    // ---- the message on screen ----------------------------------------------------------------------

    /// <summary>A short line at the top of the screen, over the game, that fades out.</summary>
    private static class Toast
    {
        private const float Fade = 0.4f;
        private const float FontSize = 36f;
        private static GameObject? root;
        private static TextMeshProUGUI? label;
        private static Image? back;
        private static Image? blocker;
        private static float until = -1f;
        private static bool failed;

        /// <summary>Whether a message is on screen and starts fading within <paramref name="seconds"/> (it's drawn over the loading curtain too).</summary>
        internal static bool FadesWithin(float seconds) => until > Time.unscaledTime && until - Fade - Time.unscaledTime < seconds;

        internal static void Show(string text, float seconds)
        {
            if (failed) return;
            try
            {
                if (!Make()) return;
                label!.text = text;
                var size = label.GetPreferredValues(text);
                back!.rectTransform.sizeDelta = new Vector2(size.x + 48f, size.y + 20f);
                until = Time.unscaledTime + seconds;
                SetAlpha(1f);
                if (!root!.activeSelf) root.SetActive(true);
            }
            catch (Exception ex)
            {
                failed = true;
                ModLog.Error("Quick save: showing its message failed, so messages are only logged: " + ex.Message);
            }
        }

        internal static void Hide()
        {
            until = -1f;
            if (root != null && root && root.activeSelf) root.SetActive(false);
        }

        /// <summary>
        /// While a slot's new key is awaited, a see-through layer over the whole screen takes the
        /// mouse, so the click that gives up doesn't also press the game's row under the pointer.
        /// It shows with the message.
        /// </summary>
        internal static void BlockClicks(bool on)
        {
            // Nothing built yet means nothing to turn off.
            if (failed || (!on && (blocker == null || !blocker))) return;
            try
            {
                if (on && !Make()) return;
                if (blocker != null && blocker && blocker.gameObject.activeSelf != on) blocker.gameObject.SetActive(on);
            }
            catch (Exception ex) { ModLog.Error("Quick save: blocking clicks while a key is awaited failed: " + ex.Message); }
        }

        internal static void Update()
        {
            if (root == null || !root || !root.activeSelf) return;
            float left = until - Time.unscaledTime;
            if (left <= 0f) Hide();
            else SetAlpha(Math.Min(1f, left / Fade));
        }

        private static void SetAlpha(float alpha)
        {
            if (label != null && label) label.color = new Color(1f, 1f, 1f, alpha);
            if (back != null && back) back.color = new Color(0f, 0f, 0f, 0.65f * alpha);
        }

        private static bool Make()
        {
            if (root != null && root && label != null && label && back != null && back && blocker != null && blocker) return true;
            if (root != null && root) Object.Destroy(root);
            root = new GameObject("NocturnePlus_QuickSaveMessage");
            Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Over the game's menus, under the mod's own pages.
            canvas.sortingOrder = 31000;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            // Only the click blocker takes the ray; while it's off, the message lets every click through.
            root.AddComponent<GraphicRaycaster>();

            var block = new GameObject("ClickBlock");
            var blockRect = block.AddComponent<RectTransform>();
            blockRect.SetParent(root.transform, false);
            blockRect.anchorMin = Vector2.zero;
            blockRect.anchorMax = Vector2.one;
            blockRect.sizeDelta = Vector2.zero;
            blocker = block.AddComponent<Image>();
            blocker.color = new Color(0f, 0f, 0f, 0f);
            blocker.raycastTarget = true;
            // A see-through graphic whose mesh is culled isn't raycast; this one must be.
            blocker.canvasRenderer.cullTransparentMesh = false;
            block.SetActive(false);

            var box = new GameObject("Box");
            var boxRect = box.AddComponent<RectTransform>();
            boxRect.SetParent(root.transform, false);
            boxRect.anchorMin = boxRect.anchorMax = new Vector2(0.5f, 1f);
            boxRect.pivot = new Vector2(0.5f, 1f);
            boxRect.anchoredPosition = new Vector2(0f, -48f);
            back = box.AddComponent<Image>();
            back.raycastTarget = false;

            var text = new GameObject("Text");
            var textRect = text.AddComponent<RectTransform>();
            textRect.SetParent(boxRect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            label = text.AddComponent<TextMeshProUGUI>();
            var font = TitleBranding.FindFont();
            if (font != null && font)
            {
                label.font = font.font;
                label.fontSharedMaterial = font.fontSharedMaterial;
            }
            label.fontSize = FontSize;
            label.alignment = TextAlignmentOptions.Center;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Overflow;
            label.raycastTarget = false;
            // Plain text only: the words are the mod's own.
            label.richText = false;
            root.SetActive(false);
            return true;
        }
    }
}
