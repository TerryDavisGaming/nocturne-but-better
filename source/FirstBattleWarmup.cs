using System.Collections.Concurrent;
using System.Diagnostics;
using Il2CppInterop.Runtime;
using UnityEngine.Events;
using Time = UnityEngine.Time;

namespace NocturnePlus;

/// <summary>
/// Optimized and Potato: the note skin's and the timing bar's artwork is made before the first
/// battle, so that battle's first notes don't wait for it. A background thread draws the masks
/// (SkinArt's plain math, nothing of Unity's or the game's); the main thread then makes one sprite
/// a frame, on the title, in the arcade menu or in the story, through the same accessors a battle
/// uses, so every note gets the same pixels and the same sprites it would have had. Never during a
/// battle, a scene change, the startup intro or a test play. Normal never starts it.
/// </summary>
internal static class FirstBattleWarmup
{
    // QA builds only: each sprite made the quick way is also made Normal's way and compared (SkinSprites).
    internal static readonly bool QaCheck = QaBuild.Env("NFS_QA_WARMUP_CHECK") == "1";
    // QA builds only: a line when a plan starts.
    private static readonly bool QaLog = QaBuild.On;
    private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

    /// <summary>A mask drawn by the worker, as RGBA bytes.</summary>
    internal sealed class Raster
    {
        internal readonly int Width, Height;
        internal readonly byte[] Rgba;

        internal Raster(int width, int height, byte[] rgba)
        {
            Width = width;
            Height = height;
            Rgba = rgba;
        }
    }

    private sealed class Job
    {
        internal readonly NoteSkin Skin;
        internal readonly bool Bar;
        // For the worker: each mask with the keys it serves (the receptor face is the note body).
        internal readonly List<(string[] Keys, Func<SkinArt.Mask> Draw)> Masks = new();
        // For the main thread: the sprites still to make, in the order the worker draws them.
        internal readonly List<SkinSprites.Warmable> Pending = new();
        internal readonly ConcurrentDictionary<string, Raster> Ready = new();
        internal volatile bool Cancelled, WorkerDone;
        // Written by the worker before it sets WorkerDone; read on the main thread only after it.
        internal Exception? Error;
        internal int MasksDrawn;
        internal double WorkerMs;
        // Main thread only.
        internal bool Reported;
        internal int Made;
        internal double SlowestMs;

        internal Job(NoteSkin skin, bool bar)
        {
            Skin = skin;
            Bar = bar;
        }

        /// <summary>One mask for the given sprites, leaving out any made already.</summary>
        internal void Add(params SkinSprites.Warmable[] sprites)
        {
            var keys = new List<string>();
            Func<SkinArt.Mask>? draw = null;
            foreach (var sprite in sprites)
            {
                if (sprite.Made()) continue;
                keys.Add(sprite.Key);
                draw ??= sprite.Draw;
                Pending.Add(sprite);
            }
            if (draw != null) Masks.Add((keys.ToArray(), draw));
        }
    }

    private static Job? current;
    // The setup (skin and timing bar) last planned for; null when none is.
    private static (NoteSkin Skin, bool Bar)? planned;
    // The once-a-second checks passed, the startup intro's included.
    private static bool open;
    private static bool failed;

    /// <summary>Something is planned, so Performance keeps calling Slow (to stop it) outside Optimized too.</summary>
    internal static bool Busy => planned != null;

    internal static double Ms(long start) => (Stopwatch.GetTimestamp() - start) * TickMs;

    /// <summary>
    /// Once a second (Performance.Slow): plans the sprites for the current skin and timing bar, and
    /// starts the worker, when that setup changed and the game is somewhere the warm-up may run.
    /// </summary>
    internal static void Slow()
    {
        if (failed || !Performance.Optimizing || Performance.Skip("prewarm"))
        {
            Stop();
            return;
        }
        open = false;
        var setup = (SettingsState.NoteSkin, SettingsState.TimingBar);
        bool replan = planned != setup;
        // Done for this setup.
        if (!replan && current == null) return;
        ListenForBattles();
        if (!Allowed()) return;
        // The intro check looks through the scene's objects, so it's only made while there's work.
        if (TestPlay.IntroShowing()) return;
        if (replan) Plan(setup);
        open = current != null;
    }

    /// <summary>Stops the warm-up and forgets its plan; whatever it hasn't made is made when first needed, as in Normal.</summary>
    internal static void Stop()
    {
        Cancel();
        planned = null;
        open = false;
    }

    // Outside battles, scene changes and test plays: the title and the arcade menu, or the story.
    // A battle's fade to black runs while the game state is still the menu's or the story's, so a
    // battle that is about to start (seen through the game's own event) and a curtain that is down
    // or moving count as outside too.
    private static bool Allowed()
    {
        var state = GameManager.GameState;
        if (battleStartingAt >= 0f && (state == GameStates.Combat || Time.unscaledTime - battleStartingAt > BattleStartSeconds))
            battleStartingAt = -1f;
        return (state == GameStates.MainMenu || state == GameStates.RPG) && battleStartingAt < 0f
            && !Performance.SceneChangeUnderWay && !Performance.CurtainBusy && !TestPlay.Active;
    }

    // Long enough for any battle's fade to black; a start that never became a battle stops holding it back then.
    private const float BattleStartSeconds = 10f;
    private static float battleStartingAt = -1f;
    private static SceneTransitionController? listeningOn;
    private static UnityAction<CombatOptions>? willEnterCombat;

    // The game's own "a battle is about to start" event (a listener, not a patch), raised before its fade to black.
    private static void ListenForBattles()
    {
        var stc = Performance.Controller;
        if (stc == null || (listeningOn != null && listeningOn && listeningOn.Pointer == stc.Pointer)) return;
        willEnterCombat ??= DelegateSupport.ConvertDelegate<UnityAction<CombatOptions>>((Action<CombatOptions>)(_ => battleStartingAt = Time.unscaledTime))
            ?? throw new InvalidOperationException("couldn't make the battle listener");
        stc.OnWillEnterCombat.AddListener(willEnterCombat);
        listeningOn = stc;
    }

    private static void Cancel()
    {
        if (current != null) current.Cancelled = true;
        current = null;
    }

    private static void Plan((NoteSkin Skin, bool Bar) setup)
    {
        Cancel();
        planned = setup;
        var job = new Job(setup.Skin, setup.Bar);
        if (setup.Skin != NoteSkin.Default)
        {
            // Only the skin's own shape. Diamonds (the middle lane of five-lane arrow charts) are
            // still made when first needed.
            var shape = setup.Skin == NoteSkin.Circle ? SkinArt.Shape.Circle : SkinArt.Shape.Arrow;
            job.Add(SkinSprites.ForWarmup(SkinSprites.Part.NoteBody, shape), SkinSprites.ForWarmup(SkinSprites.Part.ReceptorFace, shape));
            job.Add(SkinSprites.ForWarmup(SkinSprites.Part.NoteGlyph, shape));
            job.Add(SkinSprites.ForWarmup(SkinSprites.Part.NoteAccent, shape));
            job.Add(SkinSprites.ForWarmup(SkinSprites.Part.ReceptorBorder, shape));
            job.Add(SkinSprites.ForWarmup(SkinSprites.Part.ReceptorGlyph, shape));
            job.Add(SkinSprites.ForWarmup(SkinSprites.Part.ReceptorGlow, shape));
        }
        if (setup.Bar)
            foreach (var sprite in TimingBar.WarmSprites()) job.Add(sprite);
        if (job.Pending.Count == 0) return;
        if (QaLog)
            ModLog.Info($"First battle warm-up (qa): {job.Masks.Count} masks for {job.Pending.Count} sprites ({setup.Skin} skin, timing bar {(setup.Bar ? "on" : "off")}).");
        current = job;
        var thread = new Thread(() => Work(job))
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Nocturne+ first battle warm-up"
        };
        thread.Start();
    }

    // The worker: SkinArt's math and nothing else. No Unity or game calls and no logging off the
    // main thread; an error is kept for the main thread to report.
    private static void Work(Job job)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            foreach (var (keys, draw) in job.Masks)
            {
                if (job.Cancelled) break;
                var mask = draw();
                var raster = new Raster(mask.Width, mask.Height, mask.ToRgba32());
                foreach (var key in keys) job.Ready[key] = raster;
                job.MasksDrawn++;
            }
        }
        catch (Exception ex) { job.Error = ex; }
        finally
        {
            job.WorkerMs = Ms(start);
            job.WorkerDone = true;
        }
    }

    /// <summary>The worker's image for a sprite's key, taken so it's used once; false when there is none yet.</summary>
    internal static bool TryTake(string key, out Raster? raster)
    {
        raster = null;
        var job = current;
        return job != null && job.Ready.TryRemove(key, out raster);
    }

    /// <summary>
    /// Every LateUpdate, after the note skins: makes at most one sprite whose mask is ready, while
    /// the game is somewhere the warm-up may run.
    /// </summary>
    internal static void Tick()
    {
        var job = current;
        if (job == null || !open || !Performance.Optimizing) return;
        try
        {
            if (!Allowed()) return;
            Step(job);
        }
        catch (Exception ex)
        {
            failed = true;
            Stop();
            ModLog.Error("First battle warm-up failed, so it's off for this session: " + ex);
        }
    }

    private static void Step(Job job)
    {
        if (job.WorkerDone && !job.Reported)
        {
            job.Reported = true;
            if (job.Error != null)
                ModLog.Error($"First battle warm-up: drawing stopped after {job.MasksDrawn} of {job.Masks.Count} masks, so the rest are made when first needed: {job.Error}");
            else
                ModLog.Info($"First battle warm-up: {job.MasksDrawn} masks in {job.WorkerMs:0.0} ms on the worker ({job.Skin} skin, timing bar {(job.Bar ? "on" : "off")}).");
        }
        for (int i = 0; i < job.Pending.Count; i++)
        {
            var sprite = job.Pending[i];
            if (!job.Ready.ContainsKey(sprite.Key))
            {
                // Not drawn yet; or, once the worker is done, taken by a battle already or never drawn.
                if (job.WorkerDone) job.Pending.RemoveAt(i--);
                continue;
            }
            job.Pending.RemoveAt(i--);
            if (sprite.Made())
            {
                // Made without the warm-up in the meantime: its drawing isn't needed.
                job.Ready.TryRemove(sprite.Key, out _);
                continue;
            }
            long start = Stopwatch.GetTimestamp();
            sprite.Make();
            double ms = Ms(start);
            // The accessor took the image; this only matters if it didn't.
            job.Ready.TryRemove(sprite.Key, out _);
            job.Made++;
            job.SlowestMs = Math.Max(job.SlowestMs, ms);
            // One a frame.
            break;
        }
        if (!job.WorkerDone || job.Pending.Count > 0) return;
        ModLog.Info($"First battle warm-up: {job.Made} sprites finished, slowest {job.SlowestMs:0.0} ms.");
        current = null;
    }
}
