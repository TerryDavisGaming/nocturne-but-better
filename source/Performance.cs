using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace NocturnePlus;

/// <summary>
/// The Performance setting on Options > Graphics (GraphicsOptionsIntegration).
///
/// Normal is the game as it ships. Optimized draws every frame exactly as Normal does and does less
/// work: the mod looks for the game's objects only when something new can have appeared, leaves
/// hidden menu previews alone, puts the chart scroll hooks in only for charts that use them
/// (LayoutDriver, MenuFieldLayout, ScrollSpeedHooks), and while a scene loads behind the fully black
/// curtain it gives the loading more time each frame. Potato (Performance.Potato.cs) adds changes you
/// can see, for weak PCs.
///
/// Everything but two of the game's own settings lasts only while the game runs and is put back
/// when the mode changes. Those two (Combat Backdrop and Corruption Effects) are saved by the game,
/// so the mod notes that it changed them and puts them back when Potato is left; a change the player
/// makes to them by hand while on Potato is theirs (whether or not Potato had turned it on), and is
/// kept, then and after a restart.
///
/// Normal does no Performance work each frame: the scene changes are only followed while a mode
/// needs them (or a QA build asks for their times).
/// </summary>
internal static partial class Performance
{
    /// <summary>Optimized or Potato: the mod's work-savers are on.</summary>
    internal static bool Optimizing => SettingsState.Performance != PerformanceMode.Normal;

    internal static bool Potato => SettingsState.Performance == PerformanceMode.Potato;

    // The first title screen has been shown, so the game has checked (and maybe reset) its saved settings.
    private static bool started;
    private static readonly HashSet<string> FailedParts = new();

    internal static void Install(HarmonyLib.Harmony harmony) =>
        harmony.Patch(AccessTools.DeclaredMethod(typeof(MainMenu), "Activate")
                ?? throw new MissingMethodException(typeof(MainMenu).FullName, "Activate"),
            postfix: new HarmonyMethod(typeof(Performance), nameof(MainMenuPostfix)));

    private static void MainMenuPostfix()
    {
        LayoutDriver.Rescan();
        if (started) return;
        started = true;
        Run("the startup check", () =>
        {
            // A new game version can have reset every setting just before (in NocturneSettings.Awake).
            Erased();
            Probe();
            SettleGameOptions();
        });
    }

    /// <summary>Changes the setting: the old mode's changes are put back first, then the new mode's are made.</summary>
    internal static void SetMode(PerformanceMode mode)
    {
        var old = SettingsState.Performance;
        if (mode == old) return;
        if (old == PerformanceMode.Potato) LeavePotato(giveOptions: true);
        if (mode == PerformanceMode.Normal)
        {
            EndBoost();
            ScrollSpeedHooks.EnsurePatched();
        }
        // Saved once the old mode is undone, so a crash in between can't leave its changes behind.
        SettingsState.SavePerformance(mode);
        if (mode == PerformanceMode.Potato) EnterPotato();
        LayoutDriver.Rescan();
    }

    // QA builds only: NFS_QA_PERF_CHANGES=1 follows the scene changes in every mode, so their times
    // are logged and kept (QaChanges) on Normal too.
    private static readonly bool QaWantsChanges = QaBuild.Env("NFS_QA_PERF_CHANGES") == "1";

    /// <summary>Called at the start of every LateUpdate.</summary>
    internal static void Tick()
    {
        if (Optimizing || boosting || frameCapped || QaWantsChanges) Run("scene change timing", TickTransitions, StopFollowingChanges);
        // Not followed: the next change is timed from its start.
        else lastState = 0;
        if (Potato && !(Skip("scale") && Skip("lights"))) FollowScreenHeight();
        if (Potato || frameCapped) Run("the background frame rate", TickFrameCap, RestoreFrameCap);
    }

    /// <summary>Called once a second.</summary>
    internal static void Slow()
    {
        if (Erased()) return;
        if (started) Run("watching the game's settings", WatchGameOptionsSlowly);
        if (Potato) KeepPotato();
        Run("the arcade curtain", PutBackCurtains);
        // Outside Optimized only to stop one that was planned before the mode changed.
        if (Optimizing || FirstBattleWarmup.Busy) Run("the first battle warm-up", FirstBattleWarmup.Slow, FirstBattleWarmup.Stop);
    }

    // Erase All Data (and the game's own settings reset on a new version) deletes every saved
    // setting, the mod's included: Performance is Normal again, and the game's two settings are
    // back to their defaults. A record written since (Potato took them after the erase) is still
    // put back.
    private static bool Erased()
    {
        if (SettingsState.Performance == PerformanceMode.Normal || SettingsState.PerformanceSaved) return false;
        ModLog.Info("Performance: the saved settings were erased, so Performance is back to Normal.");
        bool wasPotato = Potato;
        SettingsState.ForgetPerformance();
        if (wasPotato) LeavePotato(giveOptions: true);
        EndBoost();
        ScrollSpeedHooks.EnsurePatched();
        GraphicsOptionsIntegration.RefreshAll();
        return true;
    }

    /// <summary>
    /// Runs one part; a part that fails is reported once and stays off for the session, after
    /// <paramref name="undo"/> puts back whatever it had changed.
    /// </summary>
    private static void Run(string part, Action action, Action? undo = null)
    {
        if (FailedParts.Contains(part)) return;
        try { action(); }
        catch (Exception ex)
        {
            FailedParts.Add(part);
            ModLog.Error($"Performance: {part} failed, so it's off for this session: {ex}");
            if (undo != null) Undo(part, undo);
        }
    }

    /// <summary>Puts a part's changes back, even when the part itself failed (each undo only puts back what's still the mod's).</summary>
    private static void Undo(string part, Action undo)
    {
        try { undo(); }
        catch (Exception ex) { ModLog.Error($"Performance: putting back {part} failed: {ex}"); }
    }

    // ---- scene changes: timing, and the loading budget ------------------------------------------------

    // Unity integrates loaded scenes and uploads their textures on the main thread, a few milliseconds
    // a frame. While the curtain is fully black and nothing on it moves, Optimized raises that budget:
    // frames nobody can see take longer, and the load finishes sooner. The two settings aren't in the
    // game's scripting API (they were stripped), so they're set through the engine's own calls.
    private const int HighPriority = 4, BoostedSlice = 8;
    private const float MaxBoost = 30f;
    private static readonly string[] StateNames = { "done", "fading out", "unloading", "loading", "running the intro", "fading in" };
    private static SceneTransitionController? transitions;
    private static float nextTransitionsLookup;
    private static int lastState;
    private static float stateSince, changeStart, boostStart, boosted;
    private static readonly float[] StateTime = new float[6];
    private static string fromScene = "", notStill = "";
    private static bool boosting;
    private static int savedPriority, savedSlice;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntGetter();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void IntSetter(int value);
    private static IntGetter? getPriority, getSlice;
    private static IntSetter? setPriority, setSlice;
    private static bool budgetLooked;

    // Unity's own check that the controller is still there is an engine call, so it's made once a
    // second; the controller lives for the whole session (the game's main scene holds it).
    private static float nextTransitionsCheck;

    private static SceneTransitionController? Transitions()
    {
        float now = Time.unscaledTime;
        if (transitions is not null)
        {
            if (now < nextTransitionsCheck) return transitions;
            nextTransitionsCheck = now + 1f;
            if (transitions) return transitions;
            transitions = null;
        }
        if (now < nextTransitionsLookup) return null;
        nextTransitionsLookup = now + 1f;
        transitions = SceneTransitionController.Instance?.TryCast<SceneTransitionController>();
        nextTransitionsCheck = now + 1f;
        return transitions;
    }

    /// <summary>Optimized and Potato: a scene change is under way (its scenes may still change), as last seen.</summary>
    internal static bool SceneChangeUnderWay => transitions is not null && lastState != 0;

    /// <summary>The scene transition controller, once found (Optimized and Potato follow it).</summary>
    internal static SceneTransitionController? Controller => Transitions();

    /// <summary>
    /// Optimized and Potato: the controller's curtain is down or moving, as in a scene change or the
    /// fade into or out of a battle.
    /// </summary>
    internal static bool CurtainBusy
    {
        get
        {
            var curtain = transitions is { } stc ? stc.curtainController : null;
            return curtain != null && curtain && (curtain.IsShowingCurtain || curtain.IsAnimating);
        }
    }

    /// <summary>Optimized and Potato: the curtain is down while the old scene unloads and the new one loads.</summary>
    internal static bool CurtainDown => lastState == 2 || lastState == 3;

    /// <summary>The scene change state last seen (0 when none, or when they aren't followed), for QA logs.</summary>
    internal static int SceneState => lastState;

    private static void TickTransitions()
    {
        var stc = Transitions();
        if (stc == null)
        {
            EndBoost();
            return;
        }
        // The field itself: the getter only returns it, and reading it is no engine call.
        int state = (int)stc.state;
        float now = Time.unscaledTime;
        if (state != lastState)
        {
            if (lastState == 0)
            {
                Array.Clear(StateTime);
                boosted = 0f;
                notStill = "";
                changeStart = now;
                fromScene = SceneTransitionController.CurrentSceneName ?? "";
            }
            else if (lastState < StateTime.Length) StateTime[lastState] += now - stateSince;
            if (lastState == 5) EndQuickFadeIn();
            stateSince = now;
            lastState = state;
            if (state == 5) BeginQuickFadeIn();
            if (state == 0)
            {
                EndBoost();
                ReportChange(now - changeStart);
            }
        }
        // Optimized: the objects the layout looks for are looked for again once the new scene is in,
        // while the curtain is still black (LayoutDriver).
        if (state == 2 || state == 3)
        {
            if (Optimizing) LayoutDriver.PendingScenePass = true;
        }
        else if (LayoutDriver.PendingScenePass)
        {
            LayoutDriver.PendingScenePass = false;
            if (Optimizing) LayoutDriver.DiscoverNow();
        }
        bool want = Optimizing && (state == 2 || state == 3) && now - changeStart < MaxBoost && (Potato || ScreenIsStill(stc));
        if (want && !boosting) BeginBoost();
        else if (!want && boosting) EndBoost();
        if (state == 5 && Potato && !quickFadeDecided && !Skip("fadein") && !Skip("fades")) Run("the quicker fade-in", TickQuickFadeIn);
    }

    // The scene changes are no longer followed (the part failed): nothing waits on them any more.
    private static void StopFollowingChanges()
    {
        EndBoost();
        lastState = 0;
        if (!LayoutDriver.PendingScenePass) return;
        LayoutDriver.PendingScenePass = false;
        LayoutDriver.Rescan();
    }

    /// <summary>Whether the screen shows only the still, fully black curtain, so slower frames can't be seen.</summary>
    private static bool ScreenIsStill(SceneTransitionController stc)
    {
        string why = WhyNotStill(stc);
        if (why.Length == 0) return true;
        if (notStill.Length == 0) notStill = why;
        return false;
    }

    private static string WhyNotStill(SceneTransitionController stc)
    {
        if (SceneTransitionController.CutsceneManagerBusy) return "a cutscene is running";
        // The message stands still until it fades; a loading frame can take up to about 50 ms.
        if (QuickSaveLoad.MessageFadesWithin(0.25f)) return "the quick save message is fading";
        var curtain = stc.curtainController;
        if (curtain == null || !curtain || !curtain.IsShowingCurtain) return "the curtain isn't down";
        if (curtain.IsAnimating) return "the curtain is moving";
        var group = curtain.canvasGroup;
        if (group == null || !group || !group.gameObject.activeInHierarchy || group.alpha < 0.999f) return "the curtain isn't fully black";
        var follower = curtain.followerTransition;
        if (follower != null && follower && follower.gameObject.activeInHierarchy) return "the loading animation is showing";
        var image = curtain.transitionImage;
        if (image != null && image && image.isActiveAndEnabled && image.sprite != null && image.color.a > 0.01f) return "the curtain's picture is showing";
        var texts = curtain.transitionText;
        if (texts != null)
            foreach (var text in texts)
                if (text != null && text && text.isActiveAndEnabled && text.color.a > 0.01f && !string.IsNullOrWhiteSpace(text.text))
                    return "the curtain's text is showing";
        return "";
    }

    /// <summary>QA builds only: each scene change's times in seconds, as logged.</summary>
    internal static readonly List<(string To, float FadeOut, float Unload, float Load, float FadeIn, float Total, float Boosted, string NotStill)> QaChanges = new();
    private static readonly bool KeepQaChanges = QaBuild.On;

    private static void ReportChange(float total)
    {
        var parts = new List<string>();
        for (int i = 1; i < StateTime.Length; i++)
            if (StateTime[i] > 0f) parts.Add($"{StateNames[i]} {StateTime[i]:0.00} s");
        string budget = boosted > 0f ? $"; loading budget raised for {boosted:0.00} s"
            : Optimizing && !Potato && notStill.Length > 0 ? $"; loading budget not raised: {notStill}" : "";
        string to = SceneTransitionController.CurrentSceneName ?? "";
        ModLog.Info($"Scene change {fromScene} -> {to}: {string.Join(", ", parts)} ({total:0.00} s in all{budget}).");
        if (KeepQaChanges) QaChanges.Add((to, StateTime[1], StateTime[2], StateTime[3], StateTime[5], total, boosted, notStill));
    }

    private static T? Icall<T>(string name) where T : Delegate
    {
        IntPtr call = IL2CPP.il2cpp_resolve_icall(name);
        return call == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(call);
    }

    private static bool BudgetCalls()
    {
        if (!budgetLooked)
        {
            budgetLooked = true;
            getPriority = Icall<IntGetter>("UnityEngine.Application::get_backgroundLoadingPriority");
            setPriority = Icall<IntSetter>("UnityEngine.Application::set_backgroundLoadingPriority");
            getSlice = Icall<IntGetter>("UnityEngine.QualitySettings::get_asyncUploadTimeSlice");
            setSlice = Icall<IntSetter>("UnityEngine.QualitySettings::set_asyncUploadTimeSlice");
            if (getPriority == null || setPriority == null || getSlice == null || setSlice == null)
                ModLog.Info("Performance: the engine's loading budget can't be reached, so loading isn't sped up.");
        }
        return getPriority != null && setPriority != null && getSlice != null && setSlice != null;
    }

    private static void BeginBoost()
    {
        if (!BudgetCalls()) return;
        savedPriority = getPriority!();
        savedSlice = getSlice!();
        setPriority!(HighPriority);
        if (savedSlice < BoostedSlice) setSlice!(BoostedSlice);
        boosting = true;
        boostStart = Time.unscaledTime;
    }

    private static void EndBoost()
    {
        if (!boosting) return;
        boosting = false;
        boosted += Time.unscaledTime - boostStart;
        // Nothing else sets them, but only what's still the mod's is put back.
        if (getPriority!() == HighPriority) setPriority!(savedPriority);
        if (savedSlice < BoostedSlice && getSlice!() == BoostedSlice) setSlice!(savedSlice);
    }

    // ---- the game's own saved settings (Potato) ---------------------------------------------------------

    // The ownership record once the player has changed the setting by hand while on Potato.
    private const string PlayersRecord = "player";

    /// <summary>
    /// One of the game's saved graphics settings that Potato turns on. The mod notes that it turned it
    /// on (NocturnePlus.Owns.*, "had" when the game had a saved value, "none" when it used its default),
    /// and only then puts it back; "player" when the player changed it by hand since.
    /// </summary>
    private sealed class GameOption
    {
        internal readonly string Key, Name;
        private readonly Func<bool> get;
        private readonly Action<bool> set;

        internal GameOption(string key, string name, Func<bool> get, Action<bool> set)
        {
            Key = key;
            Name = name;
            this.get = get;
            this.set = set;
        }

        private string Record => "NocturnePlus.Owns." + Key + ".v1";
        internal bool Owned => PlayerPrefs.HasKey(Record);
        // On at the last look while on Potato without a record (Watch).
        private bool seenOn;

        internal void Take()
        {
            if (Skip("options")) return;
            string record = PlayerPrefs.GetString(Record, "");
            // The player set it by hand while on Potato: theirs until Potato is left.
            if (record == PlayersRecord) return;
            if (record.Length > 0)
            {
                // Noted before (last session, or a crash): still the mod's.
                if (!get()) Write(true);
                return;
            }
            // Already on is the player's choice (or the game's default on small graphics cards).
            seenOn = get();
            if (seenOn) return;
            PlayerPrefs.SetString(Record, PlayerPrefs.HasKey(Key) ? "had" : "none");
            Write(true);
            ModLog.Info($"Performance (Potato): {Name} is on its lighter setting until you leave Potato.");
        }

        internal void Give()
        {
            if (!Owned) return;
            string record = PlayerPrefs.GetString(Record, "had");
            PlayerPrefs.DeleteKey(Record);
            if (record == PlayersRecord || !get()) return;
            bool had = record != "none";
            Write(false);
            // The game had no saved value: it goes back to working out its default.
            if (!had) PlayerPrefs.DeleteKey(Key);
            ModLog.Info($"Performance: {Name} is back to how it was before Potato.");
        }

        /// <summary>
        /// The player turned it off by hand while on Potato: it's theirs from now on. The record says
        /// so, so a later start in Potato doesn't take it again; leaving Potato clears it. When it was
        /// already on before Potato (so Potato never noted it), only the game's own change event counts
        /// (<paramref name="fromEvent"/>): the once-a-second check can't tell a change by hand from a
        /// take that failed.
        /// </summary>
        internal void Watch(bool fromEvent)
        {
            if (writing) return;
            if (!Owned)
            {
                if (!Potato || !started || Skip("options"))
                {
                    seenOn = false;
                    return;
                }
                // Only a change from on: the event also comes when the other setting changes.
                bool was = seenOn;
                seenOn = get();
                if (!fromEvent || !was || seenOn) return;
                PlayerPrefs.SetString(Record, PlayersRecord);
                PlayerPrefs.Save();
                ModLog.Info($"Performance: you turned {Name} off yourself on Potato, so Potato leaves it as you set it.");
                return;
            }
            if (get() || PlayerPrefs.GetString(Record, "") == PlayersRecord) return;
            PlayerPrefs.SetString(Record, PlayersRecord);
            PlayerPrefs.Save();
            ModLog.Info($"Performance: you changed {Name} yourself, so Potato leaves it as you set it.");
        }

        private void Write(bool value)
        {
            writing = true;
            try { set(value); }
            finally { writing = false; }
        }
    }

    private static bool writing, subscribed;
    // Held so the game's event keeps a live delegate.
    private static Il2CppSystem.Action? graphicsChanged;

    private static readonly GameOption[] GameOptions =
    {
        // Named as the game's Graphics page names them.
        new("CachedCombatBackdrop", "Combat Backgrounds", () => NocturneSettings.CachedCombatBackdrop, value => NocturneSettings.CachedCombatBackdrop = value),
        new("LowCorruptionEffects", "Corruption Effects", () => NocturneSettings.LowCorruptionEffects, value => NocturneSettings.LowCorruptionEffects = value),
    };

    /// <summary>At the first title screen: Potato takes the settings, any other mode puts back what an earlier Potato left.</summary>
    private static void SettleGameOptions()
    {
        if (!subscribed)
        {
            subscribed = true;
            // The game raises this after either setting changes, so a change by hand is seen at once.
            graphicsChanged = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)(() => Run("watching the game's settings", WatchGameOptionsNow)));
            if (graphicsChanged != null) NocturneSettings.add_OnGraphicsSettingsChanged(graphicsChanged);
        }
        if (Potato) TakeGameOptions();
        else GiveGameOptions();
    }

    private static void TakeGameOptions()
    {
        foreach (var option in GameOptions) option.Take();
        PlayerPrefs.Save();
        GraphicsOptionsIntegration.RefreshGameRows();
    }

    private static void GiveGameOptions()
    {
        foreach (var option in GameOptions) option.Give();
        PlayerPrefs.Save();
        GraphicsOptionsIntegration.RefreshGameRows();
    }

    // From the game's change event: a change by hand, just made.
    private static void WatchGameOptionsNow() => WatchGameOptions(fromEvent: true);

    private static void WatchGameOptionsSlowly() => WatchGameOptions(fromEvent: false);

    private static void WatchGameOptions(bool fromEvent)
    {
        foreach (var option in GameOptions) option.Watch(fromEvent);
    }

    // ---- what the game was set to, once, for the log -----------------------------------------------------

    private static void Probe()
    {
        var parts = new List<string> { "Performance: " + SettingsState.Performance };
        void Add(Func<string> part)
        {
            try { parts.Add(part()); }
            catch (Exception ex) { parts.Add("(" + ex.GetType().Name + ")"); }
        }
        Add(() => $"screen {Screen.width}x{Screen.height}, vsync {QualitySettings.vSyncCount}, frame rate cap {Application.targetFrameRate}, runs in background {Application.runInBackground}");
        Add(() => $"{SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsMemorySize} MB), {SystemInfo.processorCount} CPU threads");
        Add(() =>
        {
            var asset = UnityEngine.Rendering.Universal.UniversalRenderPipeline.asset;
            return asset == null ? "no URP asset" : $"render scale {asset.renderScale:0.###} ({asset.upscalingFilter}), MSAA {asset.msaaSampleCount}";
        });
        Add(() =>
        {
            var data = LightsData(UnityEngine.Rendering.Universal.UniversalRenderPipeline.asset);
            return data == null ? "no 2D renderer" : $"2D light scale {data.m_LightRenderTextureScale:0.###}";
        });
        Add(() => BudgetCalls() ? $"loading priority {getPriority!()}, upload slice {getSlice!()} ms" : "no loading budget calls");
        Add(() =>
        {
            var stc = Transitions();
            return stc == null ? "no scene transitions yet" : $"scene fades {stc.SceneTransitionTime:0.##} s, room fades {stc.RoomTransitionTime:0.##} s";
        });
        foreach (var option in GameOptions)
            Add(() => $"{option.Key} {(PlayerPrefs.HasKey(option.Key) ? PlayerPrefs.GetInt(option.Key, 0).ToString() : "unset")}{(option.Owned ? " (Potato's)" : "")}");
        ModLog.Info(string.Join("; ", parts) + ".");
    }
}
