using System.Globalization;

namespace NocturneFlatScroll;

/// <summary>
/// When an animation's frames show, and for the attack when its hit lands and the parry window
/// opens. Worked out from the file's own frames and the animation's settings, so the battle and
/// the checks at package load agree. This file has no Unity or game dependencies.
/// </summary>
internal sealed class ArtTimeline
{
    /// <summary>Frames kept per animation; more are thinned evenly and the animation keeps its length.</summary>
    internal const int MaxFrames = 240;
    internal const double DefaultSheetFps = 10;
    internal const double StillSeconds = 1.0;
    internal const double MinAttack = 0.2, MaxAttack = 6;
    /// <summary>The game stops waiting for an attack's hit 6 s after it starts (CombatEnemyView.PlayAttackRoutine).</summary>
    internal const double MaxHit = 5.5;
    internal const double MinHit = 0.05;
    /// <summary>Nearly every stock attack opens the parry window 0.25 s before its hit.</summary>
    internal const double ParryLead = 0.25;
    /// <summary>The stock attacks' median hit, as a share of their length.</summary>
    internal const double HitShare = 0.57;
    /// <summary>An attack without art: the Mantis rig's own Empty_Attack (its hit at 0.3 s).</summary>
    internal const double NoArtLength = 0.5, NoArtHit = 0.3;

    /// <summary>The file's own frames (1 for a still picture or a video).</summary>
    internal int SourceFrames = 1;
    /// <summary>The file's frames that are kept, in order.</summary>
    internal int[] Keep = Array.Empty<int>();
    /// <summary>How long each kept frame shows, in seconds (after speed).</summary>
    internal float[] Holds = Array.Empty<float>();
    /// <summary>Seconds; the attack's is its whole length in the battle.</summary>
    internal double Length;
    internal bool Loop;
    /// <summary>Attack only: seconds after the attack starts; -1 otherwise.</summary>
    internal double Hit = -1, Parry = -1;

    internal static ArtTimeline NoAttackArt() => new()
    {
        Length = NoArtLength, Hit = NoArtHit, Parry = Math.Max(0, NoArtHit - ParryLead), Keep = new[] { 0 }, Holds = new[] { (float)NoArtLength },
    };

    /// <summary>
    /// The timeline for an animation with <paramref name="sourceFrames"/> frames in its file.
    /// <paramref name="ownMs"/>: a GIF's own delays. <paramref name="videoSeconds"/>: a video's
    /// length. Anything the player should know goes to <paramref name="notes"/>.
    /// </summary>
    internal static ArtTimeline Plan(ArtAnimationSpec a, int sourceFrames, int[]? ownMs, double videoSeconds, List<string> notes)
    {
        var t = new ArtTimeline { Loop = a.Name == "idle" || (a.Name == "defeat" && a.Loop) };
        bool attack = a.Name == "attack";
        double speed = a.Kind is ArtKind.Gif or ArtKind.Video ? a.Speed : 1;
        double[] seconds;
        if (a.Kind == ArtKind.Video)
        {
            seconds = new[] { videoSeconds / speed };
        }
        else
        {
            int n = a.Kind == ArtKind.Image ? 1 : Math.Max(1, sourceFrames);
            t.SourceFrames = n;
            seconds = new double[n];
            if (a.Kind == ArtKind.Image) seconds[0] = a.Seconds ?? StillSeconds;
            else
            {
                int[]? ms = null;
                if (a.Times != null)
                {
                    if (a.Times.Length == n) ms = a.Times;
                    else notes.Add($"the {a.Name} has {n} frame{(n == 1 ? "" : "s")} but {a.Times.Length} \"times\", so they aren't used");
                }
                if (ms == null && a.Fps == null && a.Kind == ArtKind.Gif && ownMs != null && ownMs.Length == n) ms = ownMs;
                double each = 1.0 / (a.Fps ?? DefaultSheetFps);
                for (int i = 0; i < n; i++) seconds[i] = (ms != null ? ms[i] / 1000.0 : each) / speed;
            }
        }
        double total = seconds.Sum();
        t.Length = total;

        if (attack)
        {
            double length = total;
            if (length > MaxAttack)
            {
                notes.Add($"the attack is {Num(length)} s long, so only its first {Num(MaxAttack)} s play");
                length = MaxAttack;
            }
            length = Math.Max(MinAttack, length);
            t.Length = length;
            t.Hit = HitTime(a, seconds, speed, length, notes);
            t.Parry = Math.Max(0, t.Hit - ParryLead);
        }

        Thin(a, t, seconds, notes);
        return t;
    }

    private static double HitTime(ArtAnimationSpec a, double[] seconds, double speed, double length, List<string> notes)
    {
        double hit;
        bool byFrame = a.Kind is ArtKind.Sheet or ArtKind.Gif && seconds.Length > 1;
        if (a.HitFrame is int frame && byFrame)
        {
            if (frame > seconds.Length - 1)
            {
                notes.Add($"\"hitFrame\" is {frame} but the attack's frames are 0 to {seconds.Length - 1}, so the hit lands on the last one");
                frame = seconds.Length - 1;
            }
            hit = seconds.Take(frame).Sum();
        }
        else if (a.HitTime is double time) hit = time / speed;
        else
        {
            // Like the game's own attacks: a little past halfway, on a frame's start, with room
            // for the whole parry window before it when the attack is long enough.
            double target = HitShare * length, limit = Math.Min(length, MaxHit);
            double floor = length >= 0.5 ? ParryLead : 0;
            hit = Math.Max(floor, target);
            if (byFrame)
            {
                double at = 0, best = double.MaxValue;
                foreach (double hold in seconds)
                {
                    if (at > limit) break;
                    if (at >= floor && Math.Abs(at - target) < Math.Abs(best - target)) best = at;
                    at += hold;
                }
                if (best != double.MaxValue) hit = best;
            }
        }
        if (hit > length)
        {
            notes.Add($"the hit ({Num(hit)} s) comes after the attack ends ({Num(length)} s), so it lands at the end");
            hit = length;
        }
        if (hit > MaxHit)
        {
            notes.Add($"the hit comes {Num(hit)} s into the attack; the game waits at most 6 s, so it lands at {Num(MaxHit)} s");
            hit = MaxHit;
        }
        hit = Math.Max(MinHit, hit);
        if (hit < ParryLead) notes.Add($"the hit comes {Num(hit)} s into the attack, so the player has less time to parry than the game's usual {Num(ParryLead)} s");
        return hit;
    }

    // Over MaxFrames, every so many frames are kept and the dropped ones' time goes to the kept frame before them.
    private static void Thin(ArtAnimationSpec a, ArtTimeline t, double[] seconds, List<string> notes)
    {
        int n = seconds.Length;
        int kept = Math.Min(n, MaxFrames);
        t.Keep = new int[kept];
        t.Holds = new float[kept];
        int j = -1;
        for (int i = 0; i < n; i++)
        {
            int slot = (int)((long)i * kept / n);
            if (slot != j)
            {
                j = slot;
                t.Keep[j] = i;
            }
            t.Holds[j] += (float)seconds[i];
        }
        if (kept < n)
            notes.Add(n % kept == 0
                ? $"{a.File} has {n} frames, so every {Ordinal(n / kept)} frame is used"
                : $"{a.File} has {n} frames, so {kept} of them are used");
    }

    private static string Ordinal(int n) => n switch { 2 => "2nd", 3 => "3rd", _ => n + "th" };

    internal static string Num(double value) => value.ToString(Math.Abs(value) < 1e6 ? "0.##" : "0.##E+0", CultureInfo.InvariantCulture);

    /// <summary>Which kept frame shows <paramref name="time"/> seconds in (looping, or holding the last).</summary>
    internal static int FrameAt(float[] holds, float length, bool loop, float time)
    {
        if (holds.Length <= 1) return 0;
        if (loop && length > 0) time %= length;
        float at = 0;
        for (int i = 0; i < holds.Length; i++)
        {
            at += holds[i];
            if (time < at) return i;
        }
        return holds.Length - 1;
    }
}
