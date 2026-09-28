using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;
using Object = UnityEngine.Object;

namespace NocturnePlus;

internal enum QuickAction { Save, Load }

/// <summary>
/// Quick save and quick load. With quick save on, its key (F5 unless changed) saves the story
/// wherever the player walks free, through the game's own autosave, so the save slots the player
/// saved by hand are never written over. With quick load on, its key (F9 unless changed) does what
/// the game over screen's quick load does: it goes back to the latest save, which is the last
/// quick save unless the game has saved since. Both switches and both keys are rows in
/// Options > Gameplay; a key row takes the next key pressed after it's clicked.
/// Neither works on the title, in a battle, in a cutscene or dialogue, between screens, while the
/// game is paused, in the arcade, or during a chart editor test.
/// The game's save and load calls are looked up when the mod starts: a missing one only turns its
/// key off (and says why), and the rest of the mod is unaffected.
/// </summary>
internal static class QuickSaveLoad
{
    private const string KeysPref = "NocturnePlus.QuickKeys.v1";
    private const float Cooldown = 1.5f;
    private const float RebindTimeout = 10f;
    // Checked a second after a quick save, for the log: which save file the game wrote.
    private const float WriteCheckDelay = 1f;

    private static readonly KeyMap<QuickAction> keyMap = new(KeysPref, new[]
    {
        (QuickAction.Save, "Quick save key", new[] { new KeyBinding(Key.F5) }),
        (QuickAction.Load, "Quick load key", new[] { new KeyBinding(Key.F9) }),
    });

    // Left and right on a key row step through these; clicking the row takes any key.
    private static readonly Key[] StepKeys =
    {
        Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12,
    };

    // The game's calls, looked up once (see Install). Null means that half can't run.
    private static MethodInfo? saveCall, loadCall, canPauseCall;
    private static PropertyInfo? guiInstance;
    private static string? saveMissing, loadMissing;
    private static bool installed;

    private static float nextActionAt;
    private static readonly object RebindOwner = new();
    private static int rebindFrame = -1;
    private static float rebindStartedAt;
    private static bool rebindLocked;

    private static string? pendingCheckFolder;
    private static DateTime pendingCheckSince;
    private static float pendingCheckAt = -1f;

    // ---- the options rows -----------------------------------------------------------------------

    internal static readonly OptionsMenuIntegration.RowSpec[] Rows =
    {
        new("StateToggle_PlusQuickSave",
            "Quick save",
            "Lets the quick save key save the story anywhere you can walk around. It uses the game's autosave, so your own save slots are never written over.",
            new[] { "Off", "On" },
            () => SettingsState.QuickSave ? 1 : 0,
            (direction, click) => SettingsState.SetQuickSave(!SettingsState.QuickSave)),
        new("StateToggle_PlusQuickSaveKey",
            "Quick save key",
            "The key that quick saves. Click, then press the new key (Esc keeps the old one). Left and right pick F1 to F12.",
            () => new[] { KeyLabel(QuickAction.Save) },
            () => 0,
            (direction, click) => ChangeKey(QuickAction.Save, direction, click)),
        new("StateToggle_PlusQuickLoad",
            "Quick load",
            "Lets the quick load key go back to your latest save, like the game over screen's quick load. That's your last quick save unless the game has saved since.",
            new[] { "Off", "On" },
            () => SettingsState.QuickLoad ? 1 : 0,
            (direction, click) => SettingsState.SetQuickLoad(!SettingsState.QuickLoad)),
        new("StateToggle_PlusQuickLoadKey",
            "Quick load key",
            "The key that quick loads. Click, then press the new key (Esc keeps the old one). Left and right pick F1 to F12.",
            () => new[] { KeyLabel(QuickAction.Load) },
            () => 0,
            (direction, click) => ChangeKey(QuickAction.Load, direction, click)),
    };

    private static string KeyLabel(QuickAction action) =>
        keyMap.Rebinding is { } waiting && waiting == action ? "Press a key..." : keyMap.KeysFor(action);

    private static void ChangeKey(QuickAction action, int direction, bool click)
    {
        if (click)
        {
            StartRebind(action);
            return;
        }
        StopRebind(null);
        var current = keyMap.Bindings.TryGetValue(action, out var keys) && keys.Length > 0 ? keys[0] : default;
        int index = Array.IndexOf(StepKeys, current.Key);
        int next = index < 0 ? (direction > 0 ? 0 : StepKeys.Length - 1)
            : ((index + direction) % StepKeys.Length + StepKeys.Length) % StepKeys.Length;
        var binding = new KeyBinding(StepKeys[next]);
        keyMap.Set(action, binding);
        ModLog.Info($"{keyMap.LabelOf(action)}: {binding}");
    }

    /// <summary>Back to F5 and F9 (the gameplay page's reset).</summary>
    internal static void ResetKeys()
    {
        StopRebind(null);
        keyMap.Reset();
    }

    // ---- rebinding a key --------------------------------------------------------------------------

    private static void StartRebind(QuickAction action)
    {
        if (keyMap.Rebinding is { } waiting && waiting == action) return;
        keyMap.Rebinding = action;
        rebindFrame = Time.frameCount;
        rebindStartedAt = Time.unscaledTime;
        // The menus underneath don't move, select or go back while the key is awaited.
        if (!rebindLocked)
        {
            EditorOverlay.Enter(RebindOwner);
            rebindLocked = true;
        }
        Toast.Show($"{keyMap.LabelOf(action)}: press a key (Esc keeps {keyMap.KeysFor(action)})", RebindTimeout);
    }

    private static void StopRebind(string? message)
    {
        bool was = keyMap.Rebinding != null;
        keyMap.Rebinding = null;
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
        // The press that started it (Enter, say) isn't the new key.
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
        string? said = null;
        keyMap.UpdateRebinding(keyboard, (text, _) => said = text);
        if (keyMap.Rebinding == null) StopRebind(said ?? "Kept the old key");
    }

    // ---- start-up ---------------------------------------------------------------------------------

    /// <summary>
    /// Looks up the game's calls. Quick save uses the save manager's AutoSave, called with no
    /// arguments or only default ones. Quick load uses the game over screen's
    /// ContinueFromLatestSave, the call behind its quick load button.
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

        loadCall = typeof(GameOverMenu).GetMethods(All).Where(m => m.Name == "ContinueFromLatestSave" && Callable(m))
            .OrderBy(m => m.GetParameters().Length).FirstOrDefault();
        loadMissing = loadCall == null ? "the game's quick load isn't there" : null;

        canPauseCall = typeof(NocturneGui).GetMethods(All).FirstOrDefault(m => m.Name == "CanPauseGameState" && m.ReturnType == typeof(bool) &&
            m.GetParameters().All(p => p.HasDefaultValue || p.ParameterType == typeof(GameStates)));
        if (canPauseCall != null && !canPauseCall.IsStatic)
        {
            guiInstance = typeof(NocturneGui).GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (guiInstance == null || !typeof(NocturneGui).IsAssignableFrom(guiInstance.PropertyType)) canPauseCall = null;
        }

        ModLog.Info($"Quick save: {(saveCall != null ? "uses SaveFileManager." + Describe(saveCall) : "off, " + saveMissing)}; " +
                    $"quick load: {(loadCall != null ? "uses GameOverMenu." + Describe(loadCall) : "off, " + loadMissing)}. " +
                    $"Game states: {string.Join(", ", Enum.GetNames(typeof(GameStates)))}.");
        installed = true;
    }

    private static bool Callable(MethodInfo method) => !method.ContainsGenericParameters && method.GetParameters().All(p => p.HasDefaultValue);

    private static object?[] Arguments(MethodInfo method) => method.GetParameters()
        .Select(p => p.DefaultValue is DBNull || p.DefaultValue == Missing.Value ? null : p.DefaultValue).ToArray();

    private static string Describe(MethodInfo method) =>
        $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";

    // ---- every frame ------------------------------------------------------------------------------

    /// <summary>Called every frame: the keys, a key being rebound, and the message on screen.</summary>
    internal static void Update()
    {
        try
        {
            Toast.Update();
            CheckWrite();
            var keyboard = InputKeyboard.current;
            if (keyMap.Rebinding != null)
            {
                if (keyboard == null) StopRebind(null);
                else UpdateRebind(keyboard);
                return;
            }
            if (!installed || keyboard == null || !Application.isFocused) return;
            bool save = SettingsState.QuickSave && keyMap.Triggered(keyboard, QuickAction.Save);
            bool load = SettingsState.QuickLoad && keyMap.Triggered(keyboard, QuickAction.Load);
            if (!save && !load) return;
            // The mod's own pages use these keys for themselves (F5 tests in the chart editor),
            // and the title has nothing to save.
            if (EditorOverlay.IsOpen || GameManager.GameState == GameStates.MainMenu) return;
            if (Time.unscaledTime < nextActionAt) return;
            nextActionAt = Time.unscaledTime + Cooldown;
            if (save) Save();
            else Load();
        }
        catch (Exception ex)
        {
            if (!reportedUpdate) ModLog.Error("Quick save and load failed: " + ex);
            reportedUpdate = true;
        }
    }

    private static bool reportedUpdate;

    private static void Save()
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
            pendingCheckFolder = ArcadeSession.SaveFolder(saveFile);
            pendingCheckSince = DateTime.UtcNow.AddSeconds(-1);
            var result = saveCall.Invoke(saveCall.IsStatic ? null : saveFile, Arguments(saveCall));
            if (result is bool ok && !ok)
            {
                pendingCheckFolder = null;
                Toast.Show("Quick save didn't work (the game said no)", 3f);
                ModLog.Error("Quick save: SaveFileManager." + Describe(saveCall) + " returned false.");
                return;
            }
            pendingCheckAt = Time.unscaledTime + WriteCheckDelay;
            Toast.Show("Quick saved", 2f);
            ModLog.Info("Quick save: saved with SaveFileManager." + Describe(saveCall) + ".");
        }
        catch (Exception ex)
        {
            pendingCheckFolder = null;
            Toast.Show("Quick save failed (see the log)", 3f);
            ModLog.Error("Quick save: the game's save failed: " + Inner(ex));
        }
    }

    private static void Load()
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
        if (saveFile == null || !saveFile.HasContinueGameData())
        {
            Toast.Show("Nothing to quick load: there's no save yet", 2.5f);
            return;
        }
        GameOverMenu? menu = null;
        if (!loadCall.IsStatic)
        {
            foreach (var candidate in Resources.FindObjectsOfTypeAll<GameOverMenu>())
                if (candidate && candidate.gameObject.scene.IsValid()) { menu = candidate; break; }
            if (menu == null)
            {
                Toast.Show("Quick load isn't available here", 2.5f);
                ModLog.Error("Quick load: the game over screen isn't loaded, so its quick load can't run.");
                return;
            }
        }
        try
        {
            // A set-gear battle's gear is never carried into a load.
            BattleGear.Backstop("quick load");
            Toast.Show("Quick loading...", 2f);
            loadCall.Invoke(menu, Arguments(loadCall));
            ModLog.Info("Quick load: loading the latest save with GameOverMenu." + Describe(loadCall) + ".");
        }
        catch (Exception ex)
        {
            Toast.Show("Quick load failed (see the log)", 3f);
            ModLog.Error("Quick load: the game's quick load failed: " + Inner(ex));
        }
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

    // For the log: which of the story's save files the quick save wrote.
    private static void CheckWrite()
    {
        if (pendingCheckFolder == null || Time.unscaledTime < pendingCheckAt) return;
        string folder = pendingCheckFolder;
        pendingCheckFolder = null;
        try
        {
            var written = Directory.Exists(folder)
                ? Directory.GetFiles(folder, "Prod*.sav").Where(f => File.GetLastWriteTimeUtc(f) >= pendingCheckSince).Select(Path.GetFileName).ToList()
                : new List<string?>();
            if (written.Count > 0) ModLog.Info("Quick save: the game wrote " + string.Join(", ", written) + ".");
            else ModLog.Error("Quick save: no save file in " + folder + " changed; the game may not have saved.");
        }
        catch (Exception ex) { ModLog.Error("Quick save: checking the save folder failed: " + ex.Message); }
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
        private static float until = -1f;
        private static bool failed;

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
            if (root != null && root && label != null && label && back != null && back) return true;
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
