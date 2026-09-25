using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// Test play: the chart editor's Test button plays the chart being edited in a real battle and
/// comes back to the editor when the battle is left. The battle starts straight from the title
/// screen the way the arcade menu starts one (the menu itself never shows), as an arcade battle
/// the player can't lose. Its chart, clock start and music come from the editor through ChartSwap
/// and CustomMusic. Nothing reaches a save: the results and score step is skipped, score and save
/// writes are blocked, achievements are held back, and the latest save is read again afterwards.
/// The end is found by watching the game's state: it turns Combat once the battle is on, and back
/// when it is left, behind a black screen either way.
/// </summary>
internal static class TestPlay
{
    internal enum Kind { GameSong, Battle }
    internal enum Phase { Idle, Starting, Running }

    /// <summary>One test, as the editor built it.</summary>
    internal sealed class Run
    {
        internal Kind Kind;
        internal SongData Song = null!;           // the game's SongData, or the test battle's
        internal CustomBattles.Battle? Battle;     // Kind.Battle only
        internal int Melody;                       // 0-based, for the whole battle
        internal int Difficulty;                   // the arcade difficulty the battle plays
        internal double T0;                        // clock start in seconds; 0 is the usual start
        internal string? PlayableText;             // Kind.GameSong: the test chart in all six slots
        internal ChartText? Chart;                 // Kind.GameSong: its scroll speeds
        internal CustomMusic.Source? Music;        // Kind.GameSong: null plays the game's own music
        internal bool MusicFallsBackToWwise;       // Kind.GameSong from the start with a #MUSIC file
        internal string MusicName = "";
        internal string Label = "";
        internal bool SkipReady;                   // QA hook only: no Ready prompt
        internal int FirstRow, Notes, Events, Carried;   // for the log
        internal IntPtr Conductor;                 // the battle's conductor, once it takes the test
    }

    /// <summary>How a test ended, for the editor's status line.</summary>
    internal sealed record Outcome(bool Started, bool Finished, float? Percent, bool FullCombo, int Misses, string? Problem);

    private const string BackLabel = "Back to the editor";
    // A launch that hasn't reached the battle by then is dead. The conductor takes the test inside
    // the game's StartCombat, and the game turns Combat as soon as that returns (the same step of
    // its start routine), so a claimed test that still isn't Combat a moment later is dead too.
    private const float StartTimeout = 15f, ClaimGrace = 2f;
    // With the game's state unreadable, a start is only given up on this late.
    private const float ErrorTimeout = 60f;
    // The return's relock waits for the game's fade back in, at most this long.
    private const float RelockLimit = 10f;

    private static Phase phase;
    private static Run? run;
    private static float startedAt, claimedAt;
    private static int prevDifficulty;
    private static bool installed;
    private static (float Percent, bool FullCombo, int Misses)? score;
    private static Outcome? outcome;
    private static ArcadeMenuV2? bankMenu;
    private static ArcadeSongInfo? bankInfo;
    private static float returnedAt = -1f;
    private static bool loggedClaim, loggedSaveBlock, loggedRelabel, reportedUpdate;
    private static readonly HashSet<string> Reported = new();

    // The pause menu's exit button while it reads "Back to the editor".
    private static TMP_Text? relabeled;
    private static string? relabeledText;
    private static readonly List<Localize> mutedLocalizers = new();

    internal static Phase State => phase;

    /// <summary>True from the Test press until the editor is back.</summary>
    internal static bool Active => phase != Phase.Idle;

    /// <summary>Whether every hook a test needs is in: without the score, save and achievement guards a test would record things.</summary>
    internal static bool Available => installed && ChartSwap.Installed && BattleGear.AchievementsGuarded && CustomBattles.EndGuarded;

    // ---- installing ------------------------------------------------------------------------------

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        try
        {
            // Everything required is looked up first, so a missing method installs nothing.
            var results = Method(typeof(CombatManagerV3), "ProcessResults");
            var scores = Method(typeof(SaveFileManager), "SaveScores");
            harmony.Patch(results, prefix: Hook(nameof(ProcessResultsPrefix)));
            harmony.Patch(scores, prefix: Hook(nameof(SaveScoresPrefix)));
            installed = true;
        }
        catch (Exception ex)
        {
            ModLog.Error("Test play: not available: the results or score hook couldn't be installed: " + ex);
            return;
        }
        // Without these the pause menu still says "Exit to arcade" (it still comes back), and an
        // enemy that something makes defeatable could end a test early.
        Optional(harmony, typeof(CombatPauseMenu), "Activate", postfix: nameof(PauseMenuPostfix));
        Optional(harmony, typeof(CombatSimulator), "IsEnemyDead", postfix: nameof(IsEnemyDeadPostfix));
        if (Available) ModLog.Info("Test play: installed.");
        else ModLog.Error("Test play: not available: " + Missing());
    }

    private static string Missing()
    {
        var missing = new List<string>();
        if (!ChartSwap.Installed) missing.Add("the custom chart hooks");
        if (!BattleGear.AchievementsGuarded) missing.Add("the achievement guard");
        if (!CustomBattles.EndGuarded) missing.Add("the battle end guard");
        return string.Join(", ", missing) + " couldn't be installed";
    }

    private static System.Reflection.MethodInfo Method(Type type, string name) =>
        AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);

    private static HarmonyMethod Hook(string name) => new(typeof(TestPlay), name);

    private static void Optional(HarmonyLib.Harmony harmony, Type type, string method, string postfix)
    {
        try { harmony.Patch(Method(type, method), postfix: Hook(postfix)); }
        catch (Exception ex) { ModLog.Error($"Test play: {type.Name}.{method} could not be patched; tests work without it: {ex}"); }
    }

    // ---- starting --------------------------------------------------------------------------------

    /// <summary>Whether a test can start now, and if not, why (for the status line).</summary>
    internal static bool CanStart(out string why)
    {
        why = "";
        if (!Available)
        {
            why = "Test isn't available: some of its game hooks couldn't be installed (see the log).";
            return false;
        }
        if (phase != Phase.Idle)
        {
            why = "A test is already starting.";
            return false;
        }
        bool title;
        try { title = GameManager.GameState == GameStates.MainMenu && !ArcadeUtility.IsRunning && !ArcadeSession.Active; }
        catch (Exception ex)
        {
            Report("checking for the title screen", ex);
            title = false;
        }
        if (!title)
        {
            why = "Test works from the title screen. Open the chart editor from Options there.";
            return false;
        }
        // Right after a test the game is still fading back in; a battle started then would run
        // its fade against that one (the game's start doesn't check).
        if (Transitioning)
        {
            why = "The game is still fading back in. Test again in a moment.";
            return false;
        }
        return true;
    }

    // Whether the game is between screens (fading or loading), as far as it can be read.
    private static bool Transitioning
    {
        get
        {
            try
            {
                var transitions = SceneTransitionController.Instance?.TryCast<SceneTransitionController>();
                return transitions != null && transitions && transitions.Loading;
            }
            catch (Exception ex)
            {
                Report("checking the game's screen transition", ex);
                return false;
            }
        }
    }

    /// <summary>The difficulty a game song's test plays: the player's story difficulty, else the arcade's last one.</summary>
    internal static int PlayerDifficulty()
    {
        try
        {
            var player = GameDataManager.PlayerData;
            if (player != null) return Math.Clamp(player.Difficulty, 0, 5);
        }
        catch (Exception ex) { Report("reading the player's difficulty", ex); }
        try { return Math.Clamp(ArcadeUtility.Difficulty, 0, 5); }
        catch { return 2; }
    }

    /// <summary>Starts the battle for a test, as the arcade menu would start one. False with a reason when it can't.</summary>
    internal static bool Start(Run next, out string why)
    {
        if (!CanStart(out why)) return false;
        prevDifficulty = ArcadeUtility.Difficulty;
        try
        {
            // What the arcade menu does before its battles: arcade rules on (no XP or loot, a
            // defeat leaves, the pause menu's exit goes back), the difficulty, the music.
            ArcadeUtility.CutsceneState = null;
            ArcadeUtility.Difficulty = next.Difficulty;
            ArcadeUtility.IsRunning = true;
            var cutscenes = CutsceneManager.Instance;
            if (cutscenes) cutscenes.AllowMenuInputDuringCutscene = false;
            AudioController.ResumeOverworld(false);
            if (next.Kind == Kind.GameSong) RequireBanks(next.Song);
            var options = Options(next);
            var transitions = SceneTransitionController.Instance?.TryCast<SceneTransitionController>()
                ?? throw new InvalidOperationException("the game's scene transitions aren't ready");

            run = next;
            phase = Phase.Starting;
            startedAt = Time.unscaledTime;
            score = null;
            outcome = null;
            returnedAt = -1f;
            loggedClaim = loggedSaveBlock = false;
            ModLog.Info($"Test play: starting {next.Kind} \"{next.Label}\" from {next.T0:0.000} s (first note row {next.FirstRow}, " +
                        $"{next.Notes} notes, {next.Events} events, {next.Carried} carried), music: {next.MusicName}.");
            // The game's own start (private in the game): the sting, the fade to black, then the battle.
            transitions!.GoToCombat(options, new Il2CppSystem.Nullable<Color>(), false);
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Error("Test play: failed to start the battle: " + ex);
            RestoreArcade();
            phase = Phase.Idle;
            run = null;
            why = "The test couldn't start: " + ex.Message;
            return false;
        }
    }

    // An arcade battle the player can't lose or have ended early, with the player's own lanes.
    private static CombatOptions Options(Run next)
    {
        var options = new CombatOptions();
        options.CombatType = CombatType.Arcade;
        // Health stops at 1 instead of the battle ending.
        options.FailType = CombatFailType.NoFail;
        options.Song = next.Song;
        options.Enemies = null;          // the song's own
        options.SourceEnemy = null;      // no enemy in the world to mark defeated
        options.Melodies = new Il2CppStructArray<int>(new[] { next.Melody, next.Melody });
        // Also rules out the one item that makes the enemy defeatable.
        options.disableConsumables = true;
        options.enableEnemyDefeat = false;
        options.FullHealthStart = true;
        options.FullHealthEnd = true;
        options.automateInput = false;
        options.inputScript = null;
        options.WaitForPlayerReady = !next.SkipReady;
        return options;
    }

    // The arcade loads extra sound banks for a few songs (and runs their pre-battle sound hook);
    // the song's own bank loads with the battle.
    private static void RequireBanks(SongData song)
    {
        try
        {
            foreach (var menu in Resources.FindObjectsOfTypeAll<ArcadeMenuV2>())
            {
                if (!menu) continue;
                var database = menu.arcadeDatabase;
                if (database == null || !database) continue;
                var info = FindInfo(database, song.name);
                if (info == null) return;
                bool banks = info.soundBanks != null && info.soundBanks.Count > 0;
                bool hook = info.preCombatHook != null && info.preCombatHook.Hooks != AudioHookType.None;
                if (!banks && !hook) return;
                menu.RequireSoundBanks(info);
                bankMenu = menu;
                bankInfo = info;
                ModLog.Info($"Test play: loaded the arcade's extra sound banks for {song.name}.");
                return;
            }
        }
        catch (Exception ex) { ModLog.Error("Test play: the arcade's extra sound banks couldn't be loaded; the test goes on without them: " + ex); }
    }

    private static ArcadeSongInfo? FindInfo(ArcadeDatabase database, string songName)
    {
        var categories = database.songCategories;
        if (categories == null) return null;
        for (int i = 0; i < categories.Count; i++)
        {
            var songs = categories[i]?.arcadeSongs;
            if (songs == null) continue;
            for (int j = 0; j < songs.Count; j++)
            {
                var info = songs[j];
                var data = info?.songData;
                if (data != null && data && data.name == songName) return info;
            }
        }
        return null;
    }

    private static void ReleaseBanks()
    {
        var menu = bankMenu;
        var info = bankInfo;
        bankMenu = null;
        bankInfo = null;
        if (menu == null || info == null) return;
        try { if (menu) menu.ReleaseSoundBanks(info); }
        catch (Exception ex) { Report("releasing the arcade's extra sound banks", ex); }
    }

    // ---- the battle ------------------------------------------------------------------------------

    /// <summary>
    /// The battle's conductor starting a song: when it's the test's, the conductor is noted and
    /// the run returned, for ChartSwap to start the clock and swap the chart.
    /// </summary>
    internal static Run? Claim(WwiseConductor conductor, SongData songData)
    {
        var r = run;
        if (r == null || phase == Phase.Idle || conductor == null || !conductor || songData == null || !songData) return null;
        if (!r.Song || songData.Pointer != r.Song.Pointer) return null;
        r.Conductor = conductor.Pointer;
        claimedAt = Time.unscaledTime;
        if (!loggedClaim)
        {
            loggedClaim = true;
            ModLog.Info($"Test play: the battle's conductor took the test (clock starts at {r.T0:0.000} s).");
        }
        return r;
    }

    /// <summary>A game song's test, when this conductor took it and builds its chart now.</summary>
    internal static Run? ChartFor(WwiseConductor conductor, SongData song)
    {
        var r = run;
        if (r == null || phase == Phase.Idle || r.Kind != Kind.GameSong || conductor == null || !conductor || song == null || !song) return null;
        return r.Conductor == conductor.Pointer && r.Song && song.Pointer == r.Song.Pointer ? r : null;
    }

    /// <summary>Called every frame, editor open or not, so a test always ends.</summary>
    internal static void Update()
    {
        try
        {
            RunRelocks();
            if (phase == Phase.Idle) return;
            var state = GameManager.GameState;
            if (phase == Phase.Starting)
            {
                if (state == GameStates.Combat)
                {
                    // The screen is black: the editor hides now and the game's menus work again.
                    phase = Phase.Running;
                    EditorOverlay.Suspend();
                    ModLog.Info($"Test play: the battle is on (after {Time.unscaledTime - startedAt:0.0} s).");
                    return;
                }
                if (run != null && run.Conductor != IntPtr.Zero)
                {
                    // Taken but not Combat: the game's StartCombat threw after the conductor
                    // started, and its start routine died with it. Nothing more will come.
                    if (Time.unscaledTime - claimedAt > ClaimGrace) Fail("the battle didn't start: the game stopped partway, after the conductor took the test");
                }
                else if (Time.unscaledTime - startedAt > StartTimeout) Fail("the battle didn't start");
                return;
            }
            // Left, whichever way: black again, and the title's menus are back under the editor.
            if (state != GameStates.Combat) End();
        }
        catch (Exception ex)
        {
            if (!reportedUpdate) ModLog.Error("Test play: watching the battle failed: " + ex);
            reportedUpdate = true;
            try
            {
                if (phase == Phase.Running && GameManager.GameState != GameStates.Combat) End();
                else if (phase == Phase.Starting && Time.unscaledTime - startedAt > ErrorTimeout) Fail("the battle didn't start");
            }
            catch (Exception inner) { Report("ending the test", inner); }
        }
    }

    /// <summary>How the last test ended, once; null when there's nothing new.</summary>
    internal static Outcome? TakeOutcome()
    {
        var o = outcome;
        outcome = null;
        return o;
    }

    // Back from the battle: everything the start changed goes back, and the editor is shown.
    private static void End()
    {
        var result = score;
        float took = Time.unscaledTime - startedAt;
        RestoreArcade();
        Try("playing the title music", () =>
        {
            // Winning doesn't start the title music again; leaving from the pause menu does.
            AudioController.PlayTitle();
            AudioController.PauseOverworld(false);
        });
        ReloadLatestSave();
        EditorOverlay.Resume();
        returnedAt = Time.unscaledTime;
        outcome = new Outcome(true, result != null, result?.Percent, result?.FullCombo ?? false, result?.Misses ?? 0, null);
        phase = Phase.Idle;
        run = null;
        DropBattleIfEditorGone();
        ModLog.Info($"Test play: back in the chart editor after {took:0.0} s ({(result != null ? "finished" : "left early")}).");
        ModLog.Info($"Test play: arcade state restored (IsRunning off, difficulty {prevDifficulty}).");
    }

    // The battle never came: the same restore, without the save (nothing has touched it).
    private static void Fail(string reason)
    {
        ModLog.Error($"Test play: {reason} ({Time.unscaledTime - startedAt:0.0} s after Test); the editor is back.");
        RestoreArcade();
        if (EditorOverlay.Suspended) EditorOverlay.Resume();
        outcome = new Outcome(false, false, null, false, 0, reason);
        phase = Phase.Idle;
        run = null;
        DropBattleIfEditorGone();
    }

    // The test's battle is kept while a test uses it, even when the editor closes; with the
    // editor gone, nobody else drops it once the test is over.
    private static void DropBattleIfEditorGone()
    {
        if (!ChartEditor.IsOpen) Try("dropping the test battle", CustomBattles.DropTest);
    }

    private static void RestoreArcade()
    {
        Try("turning the arcade off", () => ArcadeUtility.IsRunning = false);
        Try("putting the arcade difficulty back", () => ArcadeUtility.Difficulty = prevDifficulty);
        ReleaseBanks();
        Try("resetting cutscene menu input", () =>
        {
            var cutscenes = CutsceneManager.Instance;
            if (cutscenes) cutscenes.AllowMenuInputDuringCutscene = false;
        });
    }

    // Drops anything the battle changed in the loaded save (play time, stats), as the main-menu
    // arcade does when it closes.
    private static void ReloadLatestSave()
    {
        try
        {
            var saveFile = GameDataManager.SaveFile?.TryCast<SaveFileManager>();
            if (saveFile == null || !saveFile.HasContinueGameData()) return;
            ModLog.Info(saveFile.LoadLatestGameData(-1)
                ? "Test play: reloaded the latest save from disk."
                : "Test play: reloading the latest save failed.");
        }
        catch (Exception ex) { Report("reloading the latest save", ex); }
    }

    // The game brings the title's panels back while it leaves the battle, behind its fade back
    // in; once that fade is over the menus are locked again, those panels included. (Navigation
    // itself is kept off every frame by EditorOverlay: the game turns it back on when the fade
    // lets go of its input lock.)
    private static void RunRelocks()
    {
        if (returnedAt < 0) return;
        if (Transitioning && Time.unscaledTime < returnedAt + RelockLimit) return;
        returnedAt = -1f;
        if (phase == Phase.Idle && EditorOverlay.IsOpen) EditorOverlay.Relock();
    }

    // ---- patches ---------------------------------------------------------------------------------

    // The score step at a win: recording the score, melody unlocks, XP, loot and the results
    // screen. A test skips all of it and goes straight on to leave the battle; the victory
    // coroutine only yields what this returns, and null waits a frame.
    private static bool ProcessResultsPrefix(CombatManagerV3 __instance, ref Il2CppSystem.Collections.IEnumerator __result)
    {
        if (phase == Phase.Idle) return true;
        try
        {
            var played = __instance.PlayerScore;
            int misses = __instance.Tracker?.TryCast<CombatPlayerState>()?.PlayerMisses ?? 0;
            float percent = played != null ? 100f * played.score / Math.Max(1, played.maxScore) : 0f;
            score = (percent, played != null && played.fullCombo, misses);
            ModLog.Info($"Test play: skipped the results and the score ({percent:0.0}%, {misses} misses).");
        }
        catch (Exception ex)
        {
            score = (0f, false, 0);
            Report("reading the test's score", ex);
        }
        __result = null!;
        return false;
    }

    // Leaving any battle saves the scores to the save's .score file; a test saves nothing.
    private static bool SaveScoresPrefix()
    {
        if (phase == Phase.Idle) return true;
        if (!loggedSaveBlock)
        {
            loggedSaveBlock = true;
            ModLog.Info("Test play: blocked SaveFileManager.SaveScores; nothing is saved during a test.");
        }
        return false;
    }

    // The pause menu's "Exit to arcade" leaves a test's battle; it says where it goes.
    private static void PauseMenuPostfix(CombatPauseMenu __instance)
    {
        try
        {
            if (phase == Phase.Idle)
            {
                PutLabelBack();
                return;
            }
            var button = __instance ? __instance.exitToArcadeButton : null;
            if (!button) return;
            var label = button!.GetComponentInChildren<TMP_Text>(true);
            if (!label) return;
            if (relabeled == null || !relabeled || relabeled.Pointer != label!.Pointer)
            {
                PutLabelBack();
                relabeled = label;
                relabeledText = label!.text;
                // The game's localization would put its own text back.
                foreach (var localizer in button.GetComponentsInChildren<Localize>(true))
                {
                    if (!localizer || !localizer.enabled) continue;
                    localizer.enabled = false;
                    mutedLocalizers.Add(localizer);
                }
            }
            label!.text = BackLabel;
            if (!loggedRelabel)
            {
                loggedRelabel = true;
                ModLog.Info($"Test play: the pause menu's Exit to arcade reads \"{BackLabel}\" during tests.");
            }
        }
        catch (Exception ex) { Report("relabelling the pause menu", ex); }
    }

    private static void PutLabelBack()
    {
        var label = relabeled;
        if (label == null) return;
        relabeled = null;
        if (label && relabeledText != null) label.text = relabeledText;
        relabeledText = null;
        foreach (var localizer in mutedLocalizers)
            if (localizer) localizer.enabled = true;
        mutedLocalizers.Clear();
    }

    // Nothing ends a test before its chart does: the enemy stays up at 0 health, as in the arcade.
    private static void IsEnemyDeadPostfix(ref bool __result)
    {
        if (phase != Phase.Idle) __result = false;
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static void Try(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Report(what, ex); }
    }

    private static void Report(string what, Exception ex)
    {
        if (Reported.Add(what)) ModLog.Error($"Test play: {what} failed: {ex}");
    }
}
