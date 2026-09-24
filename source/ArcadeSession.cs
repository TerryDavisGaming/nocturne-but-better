using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// The arcade on the main menu, with progress of its own. The game already has a hidden Arcade
/// button there; it is shown, and clicking it starts an arcade session instead of running the
/// game's handler, which would move the story save into the arcade's overworld scene. The session
/// opens the arcade menu over the title the way the game's debug Song Testing button does.
/// While it runs, the story save is read-only: the arcade shows the story's scores merged with
/// ArcadeScores.json, battle results go only to that file, and every write to the story's save
/// files is refused and logged. Leaving the arcade puts the story's own scores back and reloads
/// the latest save from disk.
/// </summary>
internal static class ArcadeSession
{
    private const string HelpText = "Play the songs you've unlocked. Arcade scores are kept on their own; your story save isn't changed.";

    /// <summary>True while the main-menu arcade is open.</summary>
    internal static bool Active { get; private set; }

    private static GameDataScriptableObject? data;
    private static SavedScoresData? storyScores, view;
    // Kept alive while the game holds it as SceneTransitionController.SaveScoresOverride.
    private static Il2CppSystem.Action? saveScoresHook;
    private static string? saveFolder;
    private static Dictionary<string, Fingerprint>? fingerprints;
    private static bool gameOverContinueOff, tempSaveBefore;

    // The story's files: ProdAutoSave.sav, ProdSlot0001.sav, ProdSlot0001.score and so on.
    private static readonly Regex StoryFile = new(@"^Prod(AutoSave\.sav|Slot\d+\.(sav|score))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly record struct Fingerprint(long Size, DateTime Modified, string Hash);

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        ArcadeScoreStore.Install(harmony);

        // The firewall. SaveGameData is where SaveGame (both), UpdateSave and the slot saves end
        // up; the SaveLoadManager hooks catch any other way to the story's files.
        Patch(harmony, typeof(SaveFileManager), "SaveGameData", prefix: nameof(BlockPrefix));
        PatchOverloads(harmony, typeof(SaveFileManager), "AutoSave", 2, nameof(BlockPrefix));
        Patch(harmony, typeof(SaveFileManager), "ClearAutoSave", prefix: nameof(BlockPrefix));
        Patch(harmony, typeof(SaveFileManager), "SaveScores", prefix: nameof(SaveScoresPrefix));
        Patch(harmony, typeof(SaveLoadManager), "Save", prefix: nameof(WriteFilePrefix));
        Patch(harmony, typeof(SaveLoadManager), "SaveWithMethod", prefix: nameof(WriteFilePrefix));
        Patch(harmony, typeof(SaveLoadManager), "DeleteSave", prefix: nameof(DeleteFilePrefix));

        // Both load the story save and drop the player into the story. Neither is expected in an
        // arcade battle, so these are extra safety rather than required.
        Patch(harmony, typeof(CombatPauseMenu), "Retry", prefix: nameof(BlockPrefix), required: false);
        Patch(harmony, typeof(GameOverMenu), "ContinueFromLatestSave", prefix: nameof(BlockPrefix), required: false);
        Patch(harmony, typeof(GameOverMenu), "Activate", postfix: nameof(GameOverShownPostfix), required: false);

        // Where a session ends: leaving the arcade menu, and before anything loads or starts the
        // story, so the story never sees the arcade's scores.
        Patch(harmony, typeof(ArcadeMenuV2), "Deactivate", postfix: nameof(ArcadeClosedPostfix));
        foreach (var name in new[] { "WillShow", "Continue", "LoadGame", "NewGame", "Gauntlet" })
            Patch(harmony, typeof(MainMenu), name, prefix: nameof(MainMenuPrefix), required: false);
        foreach (var name in new[] { "CreateNewData", "LoadFilename", "EraseAllData" })
            Patch(harmony, typeof(SaveFileManager), name, prefix: nameof(StoryDataPrefix), required: false);
        Patch(harmony, typeof(SaveFileManager), "SetCurrentGameData", prefix: nameof(SetCurrentGameDataPrefix));

        Patch(harmony, typeof(MainMenu), "Arcade", prefix: nameof(ArcadePrefix));
        // The button comes last: it only shows when everything required above is in place.
        Patch(harmony, typeof(MainMenu), "Awake", postfix: nameof(MainMenuAwakePostfix), required: false);
        Patch(harmony, typeof(MainMenu), "Activate", postfix: nameof(ShowButtonPostfix));
        Patch(harmony, typeof(MainMenu), "UpdateButtonState", postfix: nameof(ShowButtonPostfix), required: false);
    }

    /// <summary>Patches a method; a required one that fails stops the install, others are logged.</summary>
    private static void Patch(HarmonyLib.Harmony harmony, Type type, string method, string? prefix = null, string? postfix = null,
                              bool required = true)
    {
        try
        {
            var target = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.FullName, method);
            harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(ArcadeSession), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(ArcadeSession), postfix));
        }
        catch (Exception ex) when (!required)
        {
            ModLog.Error($"Arcade: {type.Name}.{method} could not be patched; the arcade works without it: {ex}");
        }
    }

    private static void PatchOverloads(HarmonyLib.Harmony harmony, Type type, string method, int expected, string prefix)
    {
        var targets = AccessTools.GetDeclaredMethods(type).Where(m => m.Name == method).ToList();
        if (targets.Count != expected)
            throw new MissingMethodException(type.FullName, $"{method} ({targets.Count} of {expected} overloads)");
        foreach (var target in targets) harmony.Patch(target, prefix: new HarmonyMethod(typeof(ArcadeSession), prefix));
    }

    // ---- the main menu button --------------------------------------------------------------

    private static void MainMenuAwakePostfix(MainMenu __instance)
    {
        try
        {
            var button = __instance ? __instance.arcadeButton : null;
            if (!button)
            {
                ReportOnceMessage("no button", "Arcade: the main menu has no Arcade button, so the arcade can't open from it.");
                return;
            }
            // In standard builds the button's BuildFlagObject switches it off when it wakes up.
            var flag = button!.GetComponent<BuildFlagObject>();
            if (flag) flag.ExistsInStandardBuilds = true;
        }
        catch (Exception ex) { ReportOnce("showing the Arcade button", ex); }
    }

    private static void ShowButtonPostfix(MainMenu __instance)
    {
        try
        {
            var button = __instance ? __instance.arcadeButton : null;
            if (!button) return;
            // Covers a menu that woke before the mod was loaded, if its buttons haven't yet.
            var flag = button!.GetComponent<BuildFlagObject>();
            if (flag && !flag.ExistsInStandardBuilds) flag.ExistsInStandardBuilds = true;
            if (!button.gameObject.activeSelf) button.gameObject.SetActive(true);
            // The game only enables it with a save to continue; the arcade keeps its own progress.
            if (!button.interactable) button.interactable = true;
            if (button.ButtonHelpText != HelpText)
            {
                // It came with New Game's help text.
                var help = button.LocalizedHelpKey;
                help.mTerm = string.Empty;
                button.LocalizedHelpKey = help;
                button.ButtonHelpText = HelpText;
            }
        }
        catch (Exception ex) { ReportOnce("showing the Arcade button", ex); }
    }

    // ---- starting and ending ---------------------------------------------------------------

    private static bool ArcadePrefix(MainMenu __instance)
    {
        // Never the game's own handler: it moves the story save to Slums_Arcade and reloads it.
        try
        {
            if (Active) End("the arcade was opened again", reload: false);
            if (!Start()) return false;
            // Pushes the arcade menu over the title, like the debug Song Testing button.
            __instance.SongTesting();
            ModLog.Info("Arcade: opened from the main menu.");
        }
        catch (Exception ex)
        {
            ModLog.Error("Arcade: the arcade could not open: " + ex);
            End("the arcade could not open", reload: true);
        }
        return false;
    }

    private static bool Start()
    {
        var saveFile = SaveFile();
        if (saveFile == null)
        {
            ModLog.Error("Arcade: the game's save manager isn't ready, so the arcade stays closed.");
            return false;
        }
        // The latest save, read from disk, supplies the unlocked chapters, the player's stats and
        // the story's scores. With no save there is the new-game data the game made at startup.
        if (saveFile.HasContinueGameData() && !saveFile.LoadLatestGameData(-1))
            ModLog.Info("Arcade: the latest save could not be read again; using the one already loaded.");
        var gameData = saveFile.currentSaveData;
        if (!gameData)
        {
            ModLog.Error("Arcade: there is no save data in memory, so the arcade stays closed.");
            return false;
        }
        saveFolder = SaveFolder(saveFile);
        fingerprints = TakeFingerprints(saveFolder);
        var story = gameData!.scoresData;
        var merged = ArcadeScoreStore.BuildView(story, out int storySongs, out int arcadeEntries);
        saveScoresHook ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)OnSaveScores)
            ?? throw new InvalidOperationException("Could not create the score save hook.");

        tempSaveBefore = PendingTempSave(clear: false);
        data = gameData;
        storyScores = story;
        view = merged;
        gameData.scoresData = merged;
        Active = true;
        try
        {
            // The battle's end saves scores through this hook when it's set, not through the save file.
            SceneTransitionController.SaveScoresOverride = saveScoresHook;
        }
        catch (Exception ex) { ReportOnce("setting the score save hook (SaveScores is still redirected)", ex); }
        ModLog.Info($"Arcade: session started with the story's scores for {storySongs} songs and {arcadeEntries} arcade scores; " +
                    $"{fingerprints.Count} story save files noted in {saveFolder}.");
        return true;
    }

    /// <summary>
    /// Ends the session: saves the arcade scores, gives the story its own scores back, and with
    /// <paramref name="reload"/> reads the latest save from disk again, which drops anything the
    /// arcade changed in memory (play time, stagger time, items used).
    /// </summary>
    internal static void End(string reason, bool reload)
    {
        if (!Active) return;
        Active = false;
        ModLog.Info($"Arcade: session ending ({reason}).");
        ArcadeScoreStore.Flush();
        try
        {
            if (data)
            {
                var current = data!.scoresData;
                if (view != null && current != null && current.Pointer == view.Pointer) data.scoresData = storyScores;
                else if (storyScores == null || current == null || current.Pointer != storyScores.Pointer)
                    ModLog.Info("Arcade: the scores in memory were replaced during the session and are left as they are.");
            }
        }
        catch (Exception ex) { ReportOnce("giving the story its scores back", ex); }
        try
        {
            var hook = SceneTransitionController.SaveScoresOverride;
            if (hook != null && saveScoresHook != null && hook.Pointer == saveScoresHook.Pointer)
                SceneTransitionController.SaveScoresOverride = null;
        }
        catch (Exception ex) { ReportOnce("removing the score save hook", ex); }
        try
        {
            // The arcade menu turns this on and nothing turns it off when it's opened from the title.
            var cutscenes = CutsceneManager.Instance;
            if (cutscenes) cutscenes.AllowMenuInputDuringCutscene = false;
        }
        catch (Exception ex) { ReportOnce("resetting cutscene menu input", ex); }
        // A cutscene's temporary save waits for the menus to close, which would be after the session.
        if (!tempSaveBefore && PendingTempSave(clear: true))
            ModLog.Info("Arcade: dropped a temporary save that arcade play had asked for.");
        data = null;
        storyScores = null;
        view = null;
        if (ArcadeScoreStore.RouteDepth != 0)
            ModLog.Error($"Arcade: {ArcadeScoreStore.RouteDepth} custom song score calls were still open when the session ended.");
        if (reload) ReloadLatest();
        CheckFingerprints();
    }

    /// <summary>Whether a cutscene has asked for a temporary save that hasn't happened yet.</summary>
    private static bool PendingTempSave(bool clear)
    {
        try
        {
            var transitions = SceneTransitionController.Instance?.TryCast<SceneTransitionController>();
            if (!transitions || !transitions!.NeedsTempSave) return false;
            if (clear) transitions.NeedsTempSave = false;
            return true;
        }
        catch (Exception ex)
        {
            ReportOnce("checking for a pending temporary save", ex);
            return false;
        }
    }

    private static void ReloadLatest()
    {
        try
        {
            var saveFile = SaveFile();
            if (saveFile == null || !saveFile.HasContinueGameData()) return;
            ModLog.Info(saveFile.LoadLatestGameData(-1)
                ? "Arcade: reloaded the latest save from disk."
                : "Arcade: reloading the latest save failed.");
        }
        catch (Exception ex) { ReportOnce("reloading the latest save", ex); }
    }

    private static void OnSaveScores()
    {
        try
        {
            ModLog.Info("Arcade: the battle's scores go to the arcade scores file, not the story save.");
            ArcadeScoreStore.Flush();
        }
        catch (Exception ex) { ReportOnce("saving arcade scores", ex); }
    }

    private static void ArcadeClosedPostfix(bool exiting)
    {
        if (!Active || !exiting) return;
        try
        {
            // Starting a song also takes the menu away; the song is running by then.
            if (ArcadeUtility.IsRunning) return;
            End("the arcade menu closed", reload: true);
        }
        catch (Exception ex) { ReportOnce("closing the arcade", ex); }
    }

    private static void MainMenuPrefix(MethodBase __originalMethod)
    {
        if (!Active) return;
        try
        {
            string name = __originalMethod?.Name ?? "?";
            // The title only shows again once the arcade menu is gone; anything else is the game
            // refreshing the menus under it, and the session goes on.
            if (name == "WillShow" && (ArcadeUtility.IsRunning || ArcadeMenuOnStack()))
            {
                ModLog.Info("Arcade: the title menu was refreshed under the arcade; the session goes on.");
                return;
            }
            // WillShow and Continue read the latest save themselves.
            End("MainMenu." + name, reload: name != "WillShow" && name != "Continue");
        }
        catch (Exception ex) { ReportOnce("ending the arcade session", ex); }
    }

    /// <summary>Whether the arcade menu is on the game's menu stack.</summary>
    private static bool ArcadeMenuOnStack()
    {
        foreach (var presenter in Resources.FindObjectsOfTypeAll<PanelStackPresenter>())
        {
            if (!presenter) continue;
            var stack = (presenter._menuSystem ?? presenter._menuSystemProvider?.MenuSystem)?.CurrentStack;
            if (stack == null) continue;
            for (int i = 0; i < stack.Count; i++)
                if (stack.Get(i)?.Controller?.TryCast<ArcadeMenuV2>() != null) return true;
        }
        return false;
    }

    private static void StoryDataPrefix(MethodBase __originalMethod)
    {
        if (!Active) return;
        try { End("SaveFileManager." + (__originalMethod?.Name ?? "?"), reload: false); }
        catch (Exception ex) { ReportOnce("ending the arcade session", ex); }
    }

    // A story load can be handed the scores in memory (LoadMostRecentSave does this with a newer
    // autosave); it gets the story's own, not the arcade's.
    private static void SetCurrentGameDataPrefix(ref SavedScoresData gameDataScores)
    {
        if (!Active) return;
        try
        {
            if (view != null && storyScores != null && gameDataScores != null && gameDataScores.Pointer == view.Pointer)
            {
                gameDataScores = storyScores;
                ModLog.Info("Arcade: a story load was handed the arcade's scores and gets the story's own instead.");
            }
            End("SaveFileManager.SetCurrentGameData", reload: false);
        }
        catch (Exception ex) { ReportOnce("ending the arcade session", ex); }
    }

    // ---- the firewall ----------------------------------------------------------------------

    private static bool BlockPrefix(MethodBase __originalMethod)
    {
        if (!Active) return true;
        Blocked($"{__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name}");
        return false;
    }

    private static bool SaveScoresPrefix()
    {
        if (!Active)
        {
            ArcadeScoreStore.Flush();
            return true;
        }
        Blocked("SaveFileManager.SaveScores (arcade scores go to ArcadeScores.json)");
        ArcadeScoreStore.Flush();
        return false;
    }

    private static bool WriteFilePrefix(string filename)
    {
        if (!Active || !IsStoryFile(filename)) return true;
        Blocked("writing " + filename);
        return false;
    }

    private static bool DeleteFilePrefix(string filename)
    {
        if (!Active || !IsStoryFile(filename)) return true;
        Blocked("deleting " + filename);
        return false;
    }

    private static bool IsStoryFile(string? filename)
    {
        if (string.IsNullOrEmpty(filename)) return false;
        return Path.GetFileName(filename).StartsWith("Prod", StringComparison.OrdinalIgnoreCase);
    }

    private static void Blocked(string what) =>
        ModLog.Info($"Arcade: blocked {what} during the arcade session; the story save stays as it was.");

    // The game over screen isn't expected in the arcade (a lost arcade song goes straight back to
    // the menu), but if it shows, its Continue would load the story; it's switched off.
    private static void GameOverShownPostfix(GameOverMenu __instance)
    {
        try
        {
            var button = __instance ? __instance.quickLoadButton : null;
            if (!button) return;
            if (Active)
            {
                if (!button!.interactable) return;
                button.interactable = false;
                gameOverContinueOff = true;
                ModLog.Info("Arcade: the game over screen's Continue is off during the arcade session.");
            }
            else if (gameOverContinueOff)
            {
                button!.interactable = true;
                gameOverContinueOff = false;
            }
        }
        catch (Exception ex) { ReportOnce("the game over screen", ex); }
    }

    // ---- story file fingerprints -----------------------------------------------------------

    private static SaveFileManager? SaveFile()
    {
        try { return GameDataManager.SaveFile?.TryCast<SaveFileManager>(); }
        catch (Exception ex)
        {
            ReportOnce("finding the save manager", ex);
            return null;
        }
    }

    private static string SaveFolder(SaveFileManager saveFile)
    {
        try
        {
            var manager = saveFile.saveLoadManager;
            if (manager)
            {
                string? folder = Path.GetDirectoryName(manager!.GetPath("ProdAutoSave.sav", null, false));
                if (!string.IsNullOrEmpty(folder)) return Path.GetFullPath(folder);
            }
        }
        catch (Exception ex) { ReportOnce("finding the save folder", ex); }
        return Path.Combine(Application.persistentDataPath, "GameData", "SaveData");
    }

    /// <summary>Size, time and SHA-256 of each story file. Only reads them.</summary>
    private static Dictionary<string, Fingerprint> TakeFingerprints(string folder)
    {
        var result = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(folder)) return result;
            foreach (var file in Directory.GetFiles(folder, "Prod*"))
            {
                string name = Path.GetFileName(file);
                if (!StoryFile.IsMatch(name)) continue;
                try
                {
                    var info = new FileInfo(file);
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sha = SHA256.Create();
                    result[name] = new Fingerprint(info.Length, info.LastWriteTimeUtc, Convert.ToHexString(sha.ComputeHash(stream)));
                }
                catch (Exception ex) { result[name] = new Fingerprint(-1, default, "unreadable: " + ex.Message); }
            }
        }
        catch (Exception ex) { ReportOnce("listing the story save files", ex); }
        return result;
    }

    private static void CheckFingerprints()
    {
        var before = fingerprints;
        fingerprints = null;
        if (before == null || saveFolder == null) return;
        var after = TakeFingerprints(saveFolder);
        var changed = new List<string>();
        foreach (var (name, was) in before)
        {
            if (!after.TryGetValue(name, out var now)) changed.Add(name + " was deleted");
            else if (now != was) changed.Add($"{name} ({was.Size} -> {now.Size} bytes)");
        }
        foreach (var name in after.Keys)
            if (!before.ContainsKey(name)) changed.Add(name + " is new");
        if (changed.Count == 0) ModLog.Info($"Arcade: story save files unchanged ({after.Count} checked).");
        else ModLog.Error("Arcade: WARNING: story save files changed during the arcade session: " + string.Join(", ", changed));
    }

    private static readonly HashSet<string> Reported = new();

    private static void ReportOnce(string where, Exception ex)
    {
        if (Reported.Add(where)) ModLog.Error($"Arcade: {where} failed: {ex}");
    }

    private static void ReportOnceMessage(string key, string message)
    {
        if (Reported.Add(key)) ModLog.Error(message);
    }
}
