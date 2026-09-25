using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// Boss-style dialogue in custom battles: lines before the fight, during the song, and after a
/// win or a loss (battle.json's "dialogue", read by DialogueReader). The battle starts as usual;
/// a director for each battle adds its lines at hold points the game already has. The ready prompt
/// waits for the lines before the fight, the song stops for a break the way the pause menu stops
/// it, and the end routine's own wait before the victory or defeat waits for the lines after it.
/// Lines that wait for a key run as the game's own data cutscene of Dialogue actions; a line during
/// the song that doesn't stop it is the game's box in its timed mode, which ignores keys and goes
/// by itself. Nothing reaches the save: every cutscene is repeatable (the game marks one that isn't
/// as seen when it ends), it holds only Dialogue actions, and the battle's own speakers live in
/// memory until the battle ends. Any failure drops the battle's dialogue, never the battle.
/// The speakers and their pictures are in BattleDialogue.Speakers.cs.
/// </summary>
internal static partial class BattleDialogue
{
    /// <summary>
    /// Whether lines that stop the song do. Off, each one shows as a live line instead (logged
    /// once a battle); battle.json keeps its "pause" either way.
    /// </summary>
    internal const bool BreaksEnabled = true;
    /// <summary>A live line's bubble height in the dialogue canvas's game pixels: near the top, clear of the receptors.</summary>
    private const float LiveBubbleHeight = 180f;
    /// <summary>How long the song waits after a break's last line (real seconds), so the player's hands can get back to the lanes.</summary>
    private const float BreakResumeDelay = 1f;
    /// <summary>How long the lines before the fight wait for the game characters' faces once the battle has faded in.</summary>
    private const float FacesWait = 5f;
    /// <summary>The lines before the fight start after this long even if the fade-in never lets go.</summary>
    private const float FadeInLimit = 15f;
    /// <summary>How long a box waits for the one before it to finish moving (real seconds).</summary>
    private const float SettleLimit = 1.5f;
    /// <summary>A live line's box closes this long after its time is up, unless the next live line comes within KeepBoxGap.</summary>
    private const double LiveHideLag = 0.2, KeepBoxGap = 0.5;
    /// <summary>A director whose battle never went on is let go after this long.</summary>
    private const float NeverStartedLimit = 60f;
    /// <summary>The caller the game's cutscene code is told. The game never marks it as seen, since every block is repeatable.</summary>
    private const string Caller = "NocturneButBetter";

    private enum Phase { Prepared, PreWaiting, PreRunning, Song, Break, EndDue, EndRunning, EndFinished, Released }

    private static Director? director;
    private static bool installed, passThrough, reportedFailure;
    // A QA test (SkipReady) that asked for the ready prompt only for its lines before the fight
    // starts its countdown there, even if those lines don't play after all.
    private static bool countdownInstead;

    /// <summary>Whether every hook is in; without them custom battles play without their dialogue (test play asks, see TestPlay.Run.HoldsForDialogue).</summary>
    internal static bool Installed => installed;

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        // Everything is looked up first, so a missing method installs nothing. The hooks do
        // nothing until every patch is in: Begin checks "installed", and the others only act for
        // a director Begin made.
        var ready = Method(typeof(ReadyCountdownView), "ShowGetReadyAndPressKeyToStartText");
        var endRoutine = Method(typeof(CombatManagerV3), "EndCombatRoutine");
        var endWait = Method(typeof(CombatManagerV3), "_EndCombatRoutine_b__99_0");
        var expressions = Method(typeof(PortraitManager), "GetExpressions");
        var bubble = Method(typeof(DialogueStyleNormal), "GetDialogueBubbleHeight");
        var canPause = Method(typeof(NocturneGui), "CanPauseGameState");
        var exit = Method(typeof(CombatManagerV3), "ExitCombat");
        harmony.Patch(ready, prefix: Hook(nameof(ReadyPrefix)));
        harmony.Patch(endRoutine, postfix: Hook(nameof(EndRoutinePostfix)));
        harmony.Patch(endWait, postfix: Hook(nameof(EndWaitPostfix)));
        harmony.Patch(expressions, prefix: Hook(nameof(ExpressionsPrefix)));
        harmony.Patch(bubble, postfix: Hook(nameof(BubbleHeightPostfix)));
        harmony.Patch(canPause, postfix: Hook(nameof(CanPausePostfix)));
        harmony.Patch(exit, postfix: Hook(nameof(ExitPostfix)));
        installed = true;
    }

    private static System.Reflection.MethodInfo Method(Type type, string name) =>
        AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);

    private static HarmonyMethod Hook(string name) => new(typeof(BattleDialogue), name);

    // ---- starting a battle -------------------------------------------------------------------------

    /// <summary>
    /// Called by ChartSwap when a custom battle's conductor starts, before the game shows its ready
    /// prompt: plans the battle's lines, makes its own speakers and loads the game characters'
    /// faces, behind the battle's black screen. A battle without dialogue (or a test with it off)
    /// does nothing. Whatever an earlier battle left is cleaned up first.
    /// </summary>
    internal static void Begin(CustomBattles.Battle custom, TestPlay.Run? test, WwiseConductor conductor)
    {
        var old = director;
        if (old != null) Cleanup(old, letGo: false);
        countdownInstead = false;
        if (!installed) return;
        try
        {
            var data = custom.Package.Dialogue;
            if (!data.HasAny) return;
            if (test != null && !test.DialogueOn)
            {
                ModLog.Info($"Battle dialogue: {custom.Title}: off for this test.");
                return;
            }
            countdownInstead = test is { SkipReady: true } && test.HoldsForDialogue;
            var d = new Director(custom, conductor, test);
            director = d;
            d.Prepare(test);
        }
        catch (Exception ex) { Fail(director, "starting the battle", ex); }
    }

    // ---- hooks ---------------------------------------------------------------------------------------

    // The ready prompt: held back while the lines before the fight play. Combat waits for the
    // player meanwhile (the game set waitingForPlayerReady before asking), so the enemy idles and
    // no song, notes or attacks start. The director shows the prompt itself afterwards.
    private static bool ReadyPrefix(ReadyCountdownView __instance, bool isAmbush)
    {
        if (passThrough) return true;
        try
        {
            if (director?.HoldPrompt(__instance, isAmbush) == true) return false;
            if (countdownInstead)
            {
                countdownInstead = false;
                ModLog.Info("Battle dialogue: no lines before the fight after all, so the test's countdown starts.");
                __instance.StartCountdown();
                return false;
            }
        }
        catch (Exception ex) { Fail(director, "holding the ready prompt", ex); }
        return true;
    }

    // How the battle ends (0 enemy defeated, 1 player defeated, 2 song completed); the last call wins.
    private static void EndRoutinePostfix(CombatManagerV3 __instance, CombatEndType combatEndType)
    {
        var d = director;
        if (d == null) return;
        try { d.Ended(__instance, (int)combatEndType); }
        catch (Exception ex) { Fail(d, "noting the battle's end", ex); }
    }

    // The end routine waits on this (the enemy's attack being over) before the victory or the
    // defeat; it also waits for the lines after a win or a loss.
    private static void EndWaitPostfix(CombatManagerV3 __instance, ref bool __result)
    {
        var d = director;
        if (d == null) return;
        try
        {
            if (d.HoldsEnd(__instance)) __result = false;
        }
        catch (Exception ex) { Fail(d, "holding the battle's end", ex); }
    }

    // A live line's bubble goes near the top of the screen, clear of the receptors, unless the
    // receptors are at the top (2D upscroll) and the box's usual place is clear.
    private static void BubbleHeightPostfix(DialogueStyleNormal __instance, ref float __result)
    {
        var d = director;
        if (d == null || d.BubbleStyle == IntPtr.Zero) return;
        try
        {
            if (__instance != null && __instance.Pointer == d.BubbleStyle && SettingsState.Mode != ScrollMode.Upscroll2D) __result = LiveBubbleHeight;
        }
        catch (Exception ex) { Fail(d, "raising a line's box", ex); }
    }

    // No pause menu during a break or the lines after the battle: its Resume would start the song
    // under them. Holding Confirm or Esc fast-forwards the lines instead.
    private static void CanPausePostfix(ref bool __result)
    {
        if (__result && director?.GatesPause == true) __result = false;
    }

    // Every way out of a battle: won, lost, quit, or back to the chart editor.
    private static void ExitPostfix()
    {
        countdownInstead = false;
        var d = director;
        if (d == null) return;
        Cleanup(d, letGo: false);
    }

    /// <summary>Called every frame: the director's lines, and a backstop for a battle left without its exit.</summary>
    internal static void Update()
    {
        var d = director;
        if (d == null) return;
        try { d.Update(); }
        catch (Exception ex) { Fail(d, "playing its lines", ex); }
    }

    // ---- failing and cleaning up ---------------------------------------------------------------------

    /// <summary>Drops the battle's dialogue after a failure, letting go of whatever it held, so the battle goes on without it.</summary>
    private static void Fail(Director? d, string what, Exception ex)
    {
        if (!reportedFailure)
        {
            reportedFailure = true;
            ModLog.Error($"Battle dialogue failed ({what}), so this battle goes on without it: {ex}");
        }
        else ModLog.Error($"Battle dialogue failed ({what}: {ex.Message}), so this battle goes on without it.");
        if (d != null && director == d) Cleanup(d, letGo: true);
    }

    /// <summary>
    /// Ends a director: a running block is stopped, the boxes are hidden, and the battle's own
    /// speakers, pictures and face scopes go. With <paramref name="letGo"/> (the battle goes on) the
    /// ready prompt, a break and the end are let go too.
    /// </summary>
    private static void Cleanup(Director d, bool letGo, string? why = null)
    {
        if (director == d) director = null;
        if (why != null) ModLog.Info($"Battle dialogue: cleaned up {why}.");
        bool running = d.Current is { Finished: false, State: not null };
        int removed = 0;
        Try("stopping its lines", d.StopAll);
        if (letGo) Try("letting the battle go on", d.LetGo);
        Try("letting go of the song", d.ReleaseSong);
        Try("removing its speakers", () => removed = d.RemoveSpeakers());
        ModLog.Info($"Battle dialogue: cleaned up (a block was still running: {(running ? "yes" : "no")}; speakers removed: {removed}).");
    }

    private static void Try(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { ModLog.Error($"Battle dialogue: {what} failed: {ex.Message}"); }
    }

    // ---- one battle ----------------------------------------------------------------------------------

    /// <summary>A line as the game is given it: who says it, the name tag, the face and the side.</summary>
    private sealed class Said
    {
        internal DialogueLine Line = null!;
        /// <summary>A game character's id, one of the battle's own speakers' ids, or "Narrator".</summary>
        internal string Id = DialogueReader.Narrator;
        internal string Name = "";
        internal Emotions Face = Emotions.Neutral1;
        internal DialogueSpeakerPosition Position = DialogueSpeakerPosition.Default;
        internal bool Narrator = true;
        /// <summary>Who says it, for the log.</summary>
        internal string Who => Narrator ? DialogueReader.Narrator : Name.Length > 0 ? Name : Id;
    }

    /// <summary>A moment during the song: a live line, or a break's lines (stopping lines at the same time).</summary>
    private sealed class Cue
    {
        internal double Time;
        internal DialogueLine? Live;
        internal List<DialogueLine>? Break;
    }

    /// <summary>
    /// Lines that wait for a key (or their own time): one data cutscene of Dialogue actions. The
    /// block first waits for the boxes to stop moving, shows its cast, then runs.
    /// </summary>
    private sealed class Block
    {
        internal DialogueSection Section;
        internal List<Said> Lines = new();
        internal int Step;
        internal float StepAt;
        internal float StartedAt;
        internal ICutsceneState? State;
        // Kept while the game holds it.
        internal Il2CppSystem.Action? OnFinish;
        internal bool Finished;
        internal int FinishedFrame;
    }

    private sealed partial class Director
    {
        internal readonly CustomBattles.Battle Battle;
        internal readonly BattleDialogueData Data;
        private readonly WwiseConductor conductor;
        private readonly IntPtr conductorPointer;
        private readonly bool skipReady;
        private readonly List<DialogueLine> before = new();
        private readonly List<Cue> cues = new();
        private List<DialogueLine> afterWin = new(), afterLoss = new();
        private Phase phase = Phase.Prepared;
        private int next;
        private readonly float begunAt = Time.unscaledTime;
        private bool seenCombat;
        private int outOfCombat;
        private CombatManagerV3? manager;
        // The styles a line used, hidden at the end.
        private readonly List<DialogueStyleBehaviour> styles = new();

        /// <summary>The block running now, if one.</summary>
        internal Block? Current;

        // The ready prompt, held while the lines before the fight play.
        private ReadyCountdownView? heldView;
        private bool heldAmbush;
        private float heldAt = -1, fadedInAt = -1;

        // A break.
        private bool pausedCombat;
        private float stoppedAt, resumeAt = -1;

        // The end: how it ended, and what was stopped for the lines after a loss.
        private int endType = -1;
        private bool pausedConductor, heldMusic;
        private float endStartedAt;

        // The live line.
        private DialogueStyleBehaviour? liveStyle;
        private Coroutine? liveRoutine;
        private DialogueOptions? liveWaiting;
        private Said? liveShowing;     // shown with the box once it has stopped moving, if it isn't up
        private float liveWaitingSince;
        private double liveSeconds, liveHideAt = double.NaN;

        /// <summary>The Normal style while a live line's bubble is raised (GetDialogueBubbleHeight's postfix).</summary>
        internal IntPtr BubbleStyle;

        internal Director(CustomBattles.Battle battle, WwiseConductor conductor, TestPlay.Run? test)
        {
            Battle = battle;
            Data = battle.Package.Dialogue;
            this.conductor = conductor;
            conductorPointer = conductor.Pointer;
            skipReady = test?.SkipReady == true;
        }

        private string Title => Battle.Title;

        /// <summary>No pause menu during a break or the lines after the battle.</summary>
        internal bool GatesPause => phase is Phase.Break or Phase.EndDue or Phase.EndRunning or Phase.EndFinished;

        /// <summary>Plans the lines (a test from partway in has none before the fight, and none during the song before its start) and makes the speakers.</summary>
        internal void Prepare(TestPlay.Run? test)
        {
            double t0 = test?.T0 ?? 0;
            if (t0 <= 0) before.AddRange(Data.Before);
            int stops = 0;
            foreach (var line in Data.During)
            {
                if (t0 > 0 && line.Time < t0) continue;
                if (line.Pause && !BreaksEnabled) stops++;
                if (line.Pause && BreaksEnabled)
                {
                    // Stopping lines at the same time, one after another, are one break.
                    var last = cues.Count > 0 ? cues[cues.Count - 1] : null;
                    if (last?.Break != null && last.Time == line.Time) last.Break.Add(line);
                    else cues.Add(new Cue { Time = line.Time, Break = new List<DialogueLine> { line } });
                }
                else cues.Add(new Cue { Time = line.Time, Live = line });
            }
            afterWin = Data.AfterWin;
            afterLoss = Data.AfterLoss;

            var notes = new List<string>();
            var planned = before.Concat(cues.SelectMany(c => c.Break ?? new List<DialogueLine> { c.Live! })).Concat(afterWin).Concat(afterLoss).ToList();
            string speakers = PrepareSpeakers(planned, notes);
            ModLog.Info($"Battle dialogue: {Title}: {Data.Counts()}; speakers {speakers}.");
            if (t0 > 0) ModLog.Info($"Battle dialogue: the test starts at {DialogueReader.Clock(t0)}, so no lines before the fight and {cues.Count} moments during the song play.");
            if (stops > 0) ModLog.Info($"Battle dialogue: lines don't stop the song in this version, so {stops} such lines show like the others.");
            foreach (var note in notes) ModLog.Info("Battle dialogue: " + note);
            foreach (var problem in Data.Problems) NoteOnce($"{Title}: {problem.Text}");
        }

        // ---- every frame ----

        internal void Update()
        {
            if (!Watch()) return;
            PumpBlock();
            switch (phase)
            {
                case Phase.Prepared:
                    // No lines before the fight (or they're over): the song's moments start. A
                    // battle whose song started without asking for the ready prompt plays without
                    // its lines before the fight.
                    if (before.Count == 0) phase = Phase.Song;
                    else if (Manager() is { } m && m.initializedSong && !m.waitingForPlayerReady)
                    {
                        ModLog.Info("Battle dialogue: the song started without a ready prompt, so the lines before the fight are left out.");
                        before.Clear();
                        phase = Phase.Song;
                    }
                    break;
                case Phase.PreWaiting:
                    WaitForFadeIn();
                    break;
                case Phase.PreRunning:
                    if (Current is { Finished: true } b && Time.frameCount > b.FinishedFrame)
                    {
                        Current = null;
                        ReleasePrompt();
                        phase = Phase.Song;
                        ModLog.Info($"Battle dialogue: before the fight done after {Time.unscaledTime - b.StartedAt:0.0} s; {(skipReady ? "the countdown starts" : "the ready prompt is back")}.");
                    }
                    break;
                case Phase.Song:
                    UpdateSong();
                    break;
                case Phase.Break:
                    UpdateBreak();
                    break;
                case Phase.EndDue:
                    StartEnd();
                    break;
                case Phase.EndRunning:
                    if (Current is { Finished: true })
                    {
                        phase = Phase.EndFinished;
                        ModLog.Info($"Battle dialogue: {(endType == 1 ? "the lines after a loss" : "the lines after a win")} done after {Time.unscaledTime - endStartedAt:0.0} s; the battle ends.");
                    }
                    break;
            }
        }

        // Whether the battle is still on. A battle left without its exit (the game's state out of
        // combat for two frames), or one that never went on, is cleaned up here.
        private bool Watch()
        {
            bool combat = GameManager.GameState == GameStates.Combat;
            if (combat)
            {
                seenCombat = true;
                outOfCombat = 0;
                return true;
            }
            if (seenCombat ? ++outOfCombat < 2 : Time.unscaledTime - begunAt < NeverStartedLimit) return true;
            Cleanup(this, letGo: false, seenCombat ? "after a missed exit" : "(the battle never went on)");
            return false;
        }

        /// <summary>This battle's combat manager: the game's current one, checked to be this conductor's.</summary>
        private CombatManagerV3? Manager()
        {
            if (manager != null && manager) return manager;
            var m = CombatManager.Instance?.TryCast<CombatManagerV3>();
            return m != null && m && Owns(m) ? manager = m : null;
        }

        private bool Owns(CombatManagerV3 m)
        {
            var c = m.conductor;
            return c != null && c.Pointer == conductorPointer;
        }

        // ---- before the fight ----

        /// <summary>The game asks for the ready prompt: held while there are lines before the fight to play.</summary>
        internal bool HoldPrompt(ReadyCountdownView view, bool isAmbush)
        {
            // The game can ask again before the lines are over (it also asks from PrepareSongStart);
            // the last ask is the one shown afterwards.
            bool holding = phase is Phase.PreWaiting or Phase.PreRunning;
            if (!holding && (phase != Phase.Prepared || before.Count == 0)) return false;
            heldView = view;
            heldAmbush = isAmbush;
            if (holding) return true;
            heldAt = Time.unscaledTime;
            phase = Phase.PreWaiting;
            ModLog.Info($"Battle dialogue: holding the ready prompt for {Lines(before.Count)} before the fight.");
            return true;
        }

        // The lines start once the battle has faded in and the game characters' faces are loaded
        // (or FacesWait has passed; each line also waits for its own faces).
        private void WaitForFadeIn()
        {
            float now = Time.unscaledTime;
            if (fadedInAt < 0)
            {
                if (Loading() && now - heldAt < FadeInLimit) return;
                fadedInAt = now;
            }
            if (now - fadedInAt < FacesWait && !FacesReady(before)) return;
            StartBlock(DialogueSection.Before, before);
            phase = Phase.PreRunning;
        }

        private static bool Loading()
        {
            var transitions = SceneTransitionController.Instance?.TryCast<SceneTransitionController>();
            return transitions != null && transitions && transitions.Loading;
        }

        // The ready prompt comes back (a frame after the last line, so the key that closed it
        // doesn't also count as "any key"), or a QA test's countdown starts.
        private void ReleasePrompt()
        {
            var view = heldView;
            heldView = null;
            if (view == null || !view) return;
            passThrough = true;
            try
            {
                if (skipReady || countdownInstead) view.StartCountdown();
                else view.ShowGetReadyAndPressKeyToStartText(heldAmbush);
            }
            finally
            {
                passThrough = false;
                countdownInstead = false;
            }
        }

        // ---- during the song ----

        private void UpdateSong()
        {
            var m = Manager();
            if (m == null || !m.initializedSong || m.waitingForPlayerReady || m.endedSong || !conductor) return;
            if (conductor.Paused || AudioController.IsPausedCombat) return;
            var song = conductor.songPosition;
            if (song == null) return;
            double now = song.RawTime;
            StartLive(now);
            if (!double.IsNaN(liveHideAt) && now >= liveHideAt && !LiveComing(now)) HideLive();
            // Every moment that's due, in order. After a hitch only the last live line shows, and
            // a break wins over the live lines before it; the moments after it wait for the song.
            DialogueLine? show = null;
            while (next < cues.Count && cues[next].Time <= now)
            {
                var cue = cues[next++];
                if (show != null) LeftOut(show);
                show = null;
                if (cue.Break != null)
                {
                    StartBreak(cue, m);
                    return;
                }
                show = cue.Live;
            }
            if (show != null) ShowLive(show, now);
        }

        // Whether the next moment is a live line starting within KeepBoxGap, which keeps the box up.
        private bool LiveComing(double now) => next < cues.Count && cues[next].Live != null && cues[next].Time - now <= KeepBoxGap;

        private void LeftOut(DialogueLine line) =>
            ModLog.Info($"Battle dialogue: {DialogueReader.Clock(line.Time)} {Data.NameOf(line) ?? line.Speaker}: left out, a later line came at the same moment.");

        /// <summary>
        /// A live line: the game's box in its timed mode, straight on its style (no cutscene, so the
        /// side panels don't move for every line). It ignores keys, freezes with the pause menu, and
        /// the next live line cuts it short.
        /// </summary>
        private void ShowLive(DialogueLine line, double now)
        {
            var said = Resolve(line);
            var style = Style(said.Narrator);
            // The box of the line before stays up for this one when it's the same box.
            bool up = liveStyle != null && liveStyle && liveStyle.Pointer == style.Pointer;
            if (up) StopLiveRoutine();
            else HideLive();
            Use(style);
            liveStyle = style;
            BubbleStyle = said.Narrator ? IntPtr.Zero : style.Pointer;
            liveSeconds = line.ShowSeconds;
            liveWaiting = new DialogueOptions
            {
                speakerCharacterId = said.Id,
                speakerPosition = said.Position,
                position = DialogueWindowPosition.Center,
                expression = said.Face,
                localizedText = line.Text,
                localizedName = said.Name,
                timedDialogue = true,
                forcedTime = (float)liveSeconds,
            };
            liveShowing = up ? null : said;
            liveWaitingSince = Time.unscaledTime;
            ModLog.Info($"Battle dialogue: {DialogueReader.Clock(line.Time)} {said.Who}: shown for {liveSeconds:0.0} s.");
            StartLive(now);
        }

        // As the game's own Dialogue action does: once the box has stopped moving (it may still be
        // closing from a line before), it shows with its speaker if it isn't up, and when it has
        // stopped moving again the line runs.
        private void StartLive(double now)
        {
            var style = liveStyle;
            var options = liveWaiting;
            if (style == null || options == null) return;
            if (style.IsAnimating && Time.unscaledTime - liveWaitingSince < SettleLimit) return;
            var showing = liveShowing;
            liveShowing = null;
            if (showing != null && !style.IsShowing)
            {
                style.Show(ShowOptions(new[] { showing }, cast: false));
                liveWaitingSince = Time.unscaledTime;
                return;
            }
            liveWaiting = null;
            liveRoutine = style.StartCoroutine(style.RunDialogue(options));
            liveHideAt = now + liveSeconds + LiveHideLag;
        }

        private void StopLiveRoutine()
        {
            var routine = liveRoutine;
            liveRoutine = null;
            if (routine != null && liveStyle != null && liveStyle) liveStyle.StopCoroutine(routine);
        }

        private void HideLive()
        {
            liveWaiting = null;
            liveShowing = null;
            liveHideAt = double.NaN;
            BubbleStyle = IntPtr.Zero;
            StopLiveRoutine();
            var style = liveStyle;
            liveStyle = null;
            if (style != null && style) style.Hide(DialogueHideOptions.HideAll);
        }

        // ---- breaks ----

        // The song and notes stop the way the pause menu stops them (the combat HUD hides, and the
        // mod's song follows the conductor's pause), and the break's lines play.
        private void StartBreak(Cue cue, CombatManagerV3 m)
        {
            HideLive();
            m.Pause(true);
            pausedCombat = true;
            stoppedAt = Time.unscaledTime;
            resumeAt = -1;
            phase = Phase.Break;
            ModLog.Info($"Battle dialogue: the song stopped at {DialogueReader.Clock(cue.Time)} for {Lines(cue.Break!.Count)}.");
            StartBlock(DialogueSection.During, cue.Break!);
        }

        // A second after the break's last line, the song goes on from where it stopped.
        private void UpdateBreak()
        {
            if (Current is { Finished: false }) return;
            float now = Time.unscaledTime;
            if (resumeAt < 0) resumeAt = now + BreakResumeDelay;
            if (now < resumeAt) return;
            Current = null;
            Resume();
            phase = Phase.Song;
            ModLog.Info($"Battle dialogue: the song goes on (stopped for {now - stoppedAt:0.0} s).");
        }

        private void Resume()
        {
            if (!pausedCombat) return;
            pausedCombat = false;
            Manager()?.Pause(false);
        }

        // ---- the end ----

        internal void Ended(CombatManagerV3 m, int type)
        {
            if (!Owns(m)) return;
            manager = m;
            endType = type;
        }

        /// <summary>
        /// The end routine's wait before the victory or the defeat: true (hold) from the first time
        /// it asks with lines to play, until a frame after the last one.
        /// </summary>
        internal bool HoldsEnd(CombatManagerV3 m)
        {
            switch (phase)
            {
                case Phase.Released:
                    return false;
                case Phase.EndDue:
                case Phase.EndRunning:
                    return true;
                case Phase.EndFinished:
                    if (Current != null && Time.frameCount <= Current.FinishedFrame) return true;
                    Current = null;
                    phase = Phase.Released;
                    return false;
            }
            if (!Owns(m)) return false;
            var lines = endType switch { 0 or 2 => afterWin, 1 => afterLoss, _ => null };
            if (lines == null || lines.Count == 0)
            {
                phase = Phase.Released;
                return false;
            }
            phase = Phase.EndDue;
            string how = endType switch { 0 => "enemy defeated", 1 => "player defeated", _ => "song completed" };
            ModLog.Info($"Battle dialogue: holding the end ({how}) for {Lines(lines.Count)} {(endType == 1 ? "after a loss" : "after a win")}.");
            return true;
        }

        // The lines after a win play over the song's tail (the game fades it at the victory). After
        // a loss the notes and the song stop first.
        private void StartEnd()
        {
            if (Current is { Finished: false, State: not null } running) CutsceneManager.Abort(running.State);
            Current = null;
            Resume();
            HideLive();
            if (endType == 1)
            {
                if (conductor && !conductor.Paused)
                {
                    conductor.Paused = true;
                    pausedConductor = true;
                }
                CustomMusic.Hold(true);
                heldMusic = true;
            }
            endStartedAt = Time.unscaledTime;
            phase = Phase.EndRunning;
            StartBlock(endType == 1 ? DialogueSection.AfterLoss : DialogueSection.AfterWin, endType == 1 ? afterLoss : afterWin);
        }

        // ---- blocks ----

        private void StartBlock(DialogueSection section, List<DialogueLine> lines)
        {
            HideLive();
            Current = new Block { Section = section, Lines = lines.Select(Resolve).ToList(), StepAt = Time.unscaledTime };
            PumpBlock();
        }

        // A block waits for the boxes to stop moving (a live line may be closing), shows its cast
        // like a boss's opening (the listeners dimmed from the first line), waits again, then runs.
        private void PumpBlock()
        {
            var b = Current;
            if (b == null || b.Step >= 2) return;
            float now = Time.unscaledTime;
            bool waited = now - b.StepAt >= SettleLimit;
            if (b.Step == 0)
            {
                if (!waited && Moving()) return;
                b.Step = 1;
                b.StepAt = now;
                if (ShowCast(b)) return;
            }
            if (!waited && Moving()) return;
            b.Step = 2;
            Run(b);
        }

        private bool Moving()
        {
            foreach (var style in styles)
                if (style && style.IsAnimating) return true;
            return false;
        }

        // The block's speakers before its first Narrator line, up to two a side as the box holds them.
        private bool ShowCast(Block b)
        {
            var cast = new List<Said>();
            int left = 0, right = 0;
            foreach (var said in b.Lines)
            {
                if (said.Narrator) break;
                if (cast.Any(c => c.Id == said.Id)) continue;
                if (said.Position == DialogueSpeakerPosition.Left ? left++ >= 2 : right++ >= 2) continue;
                cast.Add(said);
            }
            if (cast.Count == 0) return false;
            var style = Style(narrator: false);
            Use(style);
            style.Show(ShowOptions(cast, cast: true));
            return true;
        }

        private void Run(Block b)
        {
            var actions = new List<SceneAction>();
            for (int i = 0; i < b.Lines.Count; i++)
            {
                // The box closes after the block's last line, and between a character's line and
                // the Narrator's (they are different boxes).
                bool last = i == b.Lines.Count - 1;
                actions.Add(Action(b.Lines[i], last || b.Lines[i + 1].Narrator != b.Lines[i].Narrator));
            }
            var block = b;
            b.OnFinish = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() =>
            {
                if (block.Finished) return;
                block.Finished = true;
                block.FinishedFrame = Time.frameCount;
            }));
            Use(Style(narrator: false));
            if (b.Lines.Any(l => l.Narrator)) Use(Style(narrator: true));
            b.StartedAt = Time.unscaledTime;
            b.State = CutsceneManager.RunCutscene(new ICutscene(NewBlock(actions).Pointer), Caller, b.OnFinish, null);
            if (b.State == null && !b.Finished)
            {
                ModLog.Error("Battle dialogue: the game didn't run the lines, so they're left out.");
                b.Finished = true;
                b.FinishedFrame = Time.frameCount;
            }
        }

        /// <summary>One line as the game's Dialogue action, with its name and text given as they are (no translation terms).</summary>
        private SceneAction Action(Said said, bool hideAfter)
        {
            double? seconds = said.Line.Duration;
            return new SceneAction
            {
                action = ActionTypes.Dialogue,
                name = said.Id,
                overrideName = true,
                // Empty terms, so the name and text below show as they are (the constructor sets "???").
                displayName = new LocalizedString(),
                displayNameFallback = said.Name,
                expression = said.Face,
                localizationKey = new LocalizedString(),
                text = said.Line.Text,
                timedDialogue = seconds != null,
                forcedTime = (float)(seconds ?? 0),
                dialoguePosition = DialogueWindowPosition.Center,
                dialogueSpeakerPosition = said.Position,
                dialogueHideOnComplete = hideAfter,
            };
        }

        private static DialogueShowOptions ShowOptions(IEnumerable<Said> lines, bool cast)
        {
            var characters = new Il2CppSystem.Collections.Generic.List<DialogueCharacterOptions>();
            foreach (var said in lines)
                characters.Add(new DialogueCharacterOptions { CharacterName = said.Id, Position = said.Position, Expression = said.Face });
            var options = new DialogueShowOptions { characters = characters, windowPosition = DialogueWindowPosition.Center };
            if (cast) options.speaker = lines.First().Id;
            return options;
        }

        private static DialogueStyleBehaviour Style(bool narrator)
        {
            var manager = DialogueManager.GetInstance();
            var style = manager == null ? null : narrator ? manager.GetStyle(DialogueStyles.Narrator) : manager.Default;
            if (style == null || !style) throw new InvalidOperationException("the game's dialogue box isn't there");
            return style;
        }

        private void Use(DialogueStyleBehaviour style)
        {
            if (!styles.Any(s => s.Pointer == style.Pointer)) styles.Add(style);
        }

        // ---- the end of the battle ----

        /// <summary>Stops a running block and a live line, and hides every box a line used.</summary>
        internal void StopAll()
        {
            BubbleStyle = IntPtr.Zero;
            var b = Current;
            if (b is { Finished: false, State: not null })
            {
                b.Finished = true;
                CutsceneManager.Abort(b.State);
            }
            StopLiveRoutine();
            liveWaiting = null;
            liveShowing = null;
            liveStyle = null;
            foreach (var style in styles)
            {
                if (!style) continue;
                var normal = style.TryCast<DialogueStyleNormal>();
                if (normal != null) normal.HideAll();
                else style.Hide(DialogueHideOptions.HideAll);
            }
            styles.Clear();
        }

        /// <summary>After a failure, the battle goes on: the ready prompt comes back, a break's song goes on, and the end isn't held.</summary>
        internal void LetGo()
        {
            phase = Phase.Released;
            ReleasePrompt();
            Resume();
        }

        /// <summary>The conductor and the mod's song, stopped for the lines after a loss, are let go.</summary>
        internal void ReleaseSong()
        {
            if (heldMusic)
            {
                heldMusic = false;
                CustomMusic.Hold(false);
            }
            if (pausedConductor)
            {
                pausedConductor = false;
                if (conductor) conductor.Paused = false;
            }
        }

        private static string Lines(int count) => count == 1 ? "1 line" : $"{count} lines";
    }

    // Problems are logged once a session each.
    private static readonly HashSet<string> Noted = new();

    private static void NoteOnce(string text)
    {
        if (Noted.Add(text)) ModLog.Info($"Battle dialogue: {text}.");
    }

    /// <summary>
    /// A data cutscene of Dialogue actions. It is always repeatable: when a cutscene that isn't ends,
    /// the game marks its caller as a seen scene in the loaded save.
    /// </summary>
    private static Cutscene NewBlock(IEnumerable<SceneAction> actions)
    {
        var builder = new Cutscene.Builder();
        foreach (var action in actions) builder.AddAction(new ICutsceneAction(new CutsceneAction(action).Pointer));
        var scene = builder.Build();
        scene._repeatable = true;
        return scene;
    }
}
