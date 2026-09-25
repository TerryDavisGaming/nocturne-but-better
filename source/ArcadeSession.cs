using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// The arcade on the main menu. The game already has a hidden Arcade button there; it is shown,
/// and clicking it starts an arcade session instead of running the game's handler, which would
/// move the story save into the arcade's overworld scene. The session opens the arcade menu over
/// the title the way the game's debug Song Testing button does, on the latest save.
/// Arcade progress is the save's own: scores are recorded and written to the save's slot .score
/// file by the game, exactly as the in-story arcade cabinet does, so they also sync through Steam
/// Cloud. Where the player is and what they're doing is not touched: while the session runs,
/// every write to the story's .sav files is refused and logged, and leaving the arcade reads the
/// latest save from disk again, which drops anything else the arcade changed in memory.
/// </summary>
internal static class ArcadeSession
{
    private const string HelpText = "Play the songs you've unlocked. Scores go to your latest save; your place in the story doesn't change.";

    /// <summary>True while the main-menu arcade is open.</summary>
    internal static bool Active { get; private set; }

    private static string? saveFolder;
    private static Dictionary<string, Fingerprint>? fingerprints;
    private static bool gameOverContinueOff, tempSaveBefore;
    // Set while MainMenu.WillShow refreshes the title under the open arcade.
    private static bool refreshUnderArcade;

    // The story's save files that hold where the player is: ProdAutoSave.sav, ProdSlot0001.sav and
    // so on. The slot .score files are arcade progress and are written as usual.
    private static readonly Regex StoryFile = new(@"^Prod(AutoSave|Slot\d+)\.sav$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly record struct Fingerprint(long Size, DateTime Modified, string Hash);

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        // The firewall. SaveGameData is where SaveGame (both), UpdateSave and the slot saves end
        // up (it writes the .sav and the .score); the SaveLoadManager hooks catch any other way to
        // the story's .sav files. SaveScores, which writes only the .score, is left alone.
        Patch(harmony, typeof(SaveFileManager), "SaveGameData", prefix: nameof(SaveGameDataPrefix));
        PatchOverloads(harmony, typeof(SaveFileManager), "AutoSave", 2, nameof(BlockPrefix));
        Patch(harmony, typeof(SaveFileManager), "ClearAutoSave", prefix: nameof(BlockPrefix));
        Patch(harmony, typeof(SaveLoadManager), "Save", prefix: nameof(WriteFilePrefix));
        Patch(harmony, typeof(SaveLoadManager), "SaveWithMethod", prefix: nameof(WriteFilePrefix));
        Patch(harmony, typeof(SaveLoadManager), "DeleteSave", prefix: nameof(DeleteFilePrefix));

        // Both load the story save and drop the player into the story. Neither is expected in an
        // arcade battle, so these are extra safety rather than required.
        Patch(harmony, typeof(CombatPauseMenu), "Retry", prefix: nameof(BlockPrefix), required: false);
        Patch(harmony, typeof(GameOverMenu), "ContinueFromLatestSave", prefix: nameof(BlockPrefix), required: false);
        Patch(harmony, typeof(GameOverMenu), "Activate", postfix: nameof(GameOverShownPostfix), required: false);

        // Where a session ends: leaving the arcade menu, and before anything loads or starts the
        // story, so the story starts from what is on disk.
        Patch(harmony, typeof(ArcadeMenuV2), "Deactivate", postfix: nameof(ArcadeClosedPostfix));
        foreach (var name in new[] { "WillShow", "Continue", "LoadGame", "NewGame", "Gauntlet" })
            Patch(harmony, typeof(MainMenu), name, prefix: nameof(MainMenuPrefix),
                  postfix: name == "WillShow" ? nameof(WillShowPostfix) : null, required: false);
        foreach (var name in new[] { "CreateNewData", "LoadFilename", "EraseAllData", "SetCurrentGameData" })
            Patch(harmony, typeof(SaveFileManager), name, prefix: nameof(StoryDataPrefix), required: false);

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

    // The game makes the button clickable only with a save to continue, like Continue; arcade
    // progress goes to that save, so that rule is kept.
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
        // the scores, and its slot's .score file is where arcade progress is written.
        if (!saveFile.HasContinueGameData())
        {
            ModLog.Info("Arcade: there is no save yet, so the arcade stays closed (its progress is kept in the save).");
            return false;
        }
        if (!saveFile.LoadLatestGameData(-1))
        {
            ModLog.Error("Arcade: the latest save could not be read, so the arcade stays closed.");
            return false;
        }
        if (!saveFile.currentSaveData)
        {
            ModLog.Error("Arcade: there is no save data in memory, so the arcade stays closed.");
            return false;
        }
        if (!ReadSlotScores(saveFile))
        {
            ModLog.Error("Arcade: the latest save's scores could not be read, so the arcade stays closed.");
            return false;
        }
        saveFolder = SaveFolder(saveFile);
        fingerprints = TakeFingerprints(saveFolder);
        tempSaveBefore = PendingTempSave(clear: false);
        Active = true;
        ModLog.Info($"Arcade: session started on the latest save; {fingerprints.Count} story save files noted in {saveFolder}.");
        return true;
    }

    /// <summary>
    /// Puts the loaded save's slot scores, read from its .score file, in memory. When the
    /// autosave is newer than every slot save, the game loads it with whatever scores are
    /// already in memory rather than its slot's .score file. After a fresh start those are the
    /// empty scores of a new game, and the first arcade battle would write them over the slot's
    /// .score file. Scores an older save carried inside its .sav are what the game loaded and
    /// are kept.
    /// </summary>
    private static bool ReadSlotScores(SaveFileManager saveFile)
    {
        try
        {
            var data = saveFile.currentSaveData!;
            var save = data.saveData;
            var meta = save?.meta;
            if (meta == null) return false;
            if (save!.MigratedScoresData != null) return true;
            // SaveScores writes a save without a slot to slot 1.
            int slot = meta.slotIndex > 0 ? meta.slotIndex : 1;
            var scores = saveFile.GetSavedScoresData(slot);
            if (scores == null) return false;
            if (scores.meta != null) scores.meta.slotIndex = meta.slotIndex;
            data.scoresData = scores;
            ModLog.Info($"Arcade: read the scores of save slot {slot} ({scores.songScores?.Count ?? 0} songs).");
            return true;
        }
        catch (Exception ex)
        {
            ReportOnce("reading the latest save's scores", ex);
            return false;
        }
    }

    /// <summary>
    /// Ends the session. With <paramref name="reload"/> the latest save is read from disk again,
    /// which drops anything the arcade changed in memory (play time, stagger time, items used)
    /// while keeping the arcade scores, which are already in the save's .score file.
    /// </summary>
    internal static void End(string reason, bool reload)
    {
        if (!Active) return;
        Active = false;
        ModLog.Info($"Arcade: session ending ({reason}).");
        // A set-gear battle's inventory is never left in after the arcade.
        BattleGear.Backstop("the arcade session ended");
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
        refreshUnderArcade = false;
        if (!Active) return;
        try
        {
            string name = __originalMethod?.Name ?? "?";
            // The title only shows again once the arcade menu is gone; anything else is the game
            // refreshing the menus under it, and the session goes on.
            if (name == "WillShow" && (ArcadeUtility.IsRunning || ArcadeMenuOnStack()))
            {
                // WillShow reads the latest save again, which would otherwise end the session.
                refreshUnderArcade = true;
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

    private static void WillShowPostfix() => refreshUnderArcade = false;

    // A story load or a new game while the session is open ends it first; the load then reads
    // the story from disk as usual.
    private static void StoryDataPrefix(MethodBase __originalMethod)
    {
        if (!Active) return;
        string name = __originalMethod?.Name ?? "?";
        if (refreshUnderArcade && name == "SetCurrentGameData") return;
        try { End("SaveFileManager." + name, reload: false); }
        catch (Exception ex) { ReportOnce("ending the arcade session", ex); }
    }

    // ---- the firewall ----------------------------------------------------------------------

    private static bool BlockPrefix(MethodBase __originalMethod)
    {
        if (!Active) return true;
        Blocked($"{__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name}");
        return false;
    }

    // SaveGameData writes the slot's .sav with the player's place in it (and the .score). During
    // a session only the scores are saved, the way every battle's end saves them.
    private static bool SaveGameDataPrefix(SaveFileManager __instance)
    {
        if (!Active) return true;
        Blocked("SaveFileManager.SaveGameData (the arcade scores are saved on their own)");
        try { __instance.SaveScores(); }
        catch (Exception ex) { ReportOnce("saving the arcade scores in place of a full save", ex); }
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
        return StoryFile.IsMatch(Path.GetFileName(filename));
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

    /// <summary>Size, time and SHA-256 of each story .sav file. Only reads them.</summary>
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
