using HarmonyLib;

namespace NocturneFlatScroll;

/// <summary>
/// Plays a custom chart's scroll speed changes (#SCROLLS: beat=ratio) in battle. The game's note
/// movers place each note by how many rows it is from the current visual row, times a constant,
/// and never read StepMania's scroll segments. So the mod rescales their result with the chart's
/// "displayed row" (the scroll ratio integrated over rows), which changes how fast notes move
/// without moving them in time; judgement is time based and unaffected. The spawn window gets the
/// same mapping so notes still appear at the top of the lane in slow sections. Everything is off
/// unless the battle's custom chart has scroll changes, and only touches that battle's notes.
/// </summary>
internal static class ScrollSpeedHooks
{
    private sealed class Map
    {
        internal double[] Rows = Array.Empty<double>();    // where each change starts
        internal double[] Ratios = Array.Empty<double>();
        internal double[] Displayed = Array.Empty<double>(); // displayed row at each start

        internal static Map? From(List<(double Beat, double Ratio)> scrolls)
        {
            if (scrolls.Count == 0 || scrolls.All(s => Math.Abs(s.Ratio - 1) < 1e-9)) return null;
            var rows = new List<double> { 0 };
            var ratios = new List<double> { 1 };
            foreach (var (beat, ratio) in scrolls)
            {
                double row = beat * EditorChart.RowsPerBeat;
                if (row <= rows[^1]) { rows[^1] = Math.Max(0, row); ratios[^1] = ratio; continue; }
                rows.Add(row);
                ratios.Add(ratio);
            }
            var map = new Map { Rows = rows.ToArray(), Ratios = ratios.ToArray(), Displayed = new double[rows.Count] };
            for (int i = 1; i < rows.Count; i++)
                map.Displayed[i] = map.Displayed[i - 1] + (rows[i] - rows[i - 1]) * ratios[i - 1];
            return map;
        }

        private int Segment(double row)
        {
            int lo = 0, hi = Rows.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Rows[mid] <= row) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        internal double Disp(double row)
        {
            if (row <= 0) return row * Ratios[0];
            int i = Segment(row);
            return Displayed[i] + (row - Rows[i]) * Ratios[i];
        }

        internal double Inverse(double displayed)
        {
            if (displayed <= 0) return displayed / Ratios[0];
            int lo = 0, hi = Displayed.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Displayed[mid] <= displayed) lo = mid; else hi = mid - 1;
            }
            return Rows[lo] + (displayed - Displayed[lo]) / Ratios[lo];
        }

        internal double RatioAt(double row) => Ratios[Segment(Math.Max(0, row))];
    }

    private static Map? pending, active;
    private static WwiseConductor? conductor;
    private static IntPtr battlePosition;
    private static bool reportedError;

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        Patch(harmony, typeof(WwiseConductor), "InitializeSong", postfix: nameof(InitializeSongPostfix));
        foreach (var mover in new[] { typeof(NocturneXModMover), typeof(NocturneMModMover) })
        {
            Patch(harmony, mover, "GetNotePositionX", postfix: nameof(NotePostfix));
            Patch(harmony, mover, "GetHoldPositionX", postfix: nameof(HoldPostfix));
        }
        Patch(harmony, typeof(BaseNoteMover), "GetTopRows", postfix: nameof(TopRowsPostfix));
        Patch(harmony, typeof(BaseNoteMover), "GetBottomRows", postfix: nameof(BottomRowsPostfix));
    }

    private static void Patch(HarmonyLib.Harmony harmony, Type type, string method, string postfix)
    {
        var target = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.FullName, method);
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(ScrollSpeedHooks), postfix));
    }

    /// <summary>Called by ChartSwap for each battle: the custom chart's scroll changes, or none.</summary>
    internal static void Prepare(ChartText? chart)
    {
        pending = chart == null ? null : Map.From(ScrollSpeeds.Parse(chart.GetTag("SCROLLS")));
        active = null;
        conductor = null;
        battlePosition = IntPtr.Zero;
        lastRow = 0;
    }

    private static void InitializeSongPostfix(WwiseConductor __instance)
    {
        // The battle's conductor takes the prepared changes. If that same conductor starts its song
        // again without a new chart being built, it keeps them; any other conductor gets none.
        bool restart = pending == null && active != null && conductor && conductor!.Pointer == __instance.Pointer;
        if (!restart) active = pending;
        pending = null;
        conductor = active != null ? __instance : null;
        try { battlePosition = active != null && __instance.songPosition != null ? __instance.songPosition.Pointer : IntPtr.Zero; }
        catch { battlePosition = IntPtr.Zero; }
        if (active == null) battlePosition = IntPtr.Zero;
        else ModLog.Info($"Custom chart scroll speed changes: {active.Rows.Length - 1}.");
    }

    private static bool Live => active != null && conductor && conductor!.initializedSong;

    // Bound by position: the movers name their first parameter differently ("col" / "column").
    private static void NotePostfix(int __1, ISongPosition __4, ref double __result)
    {
        if (active == null || __4 == null || __4.Pointer != battlePosition) return;
        Rescale(ref __result, __1, __4);
    }

    private static void HoldPostfix(int __1, TapNote __2, ISongPosition __4, ref double __result)
    {
        if (active == null || __4 == null || __4.Pointer != battlePosition) return;
        Rescale(ref __result, __1 + __2.duration, __4);
    }

    /// <summary>The game's position is k * (target - now); this makes it k * (Disp(target) - Disp(now)).</summary>
    private static void Rescale(ref double result, double target, ISongPosition position)
    {
        try
        {
            double now = position.VisualTime.Row;
            double span = target - now;
            double factor = Math.Abs(span) < 1e-6 ? active!.RatioAt(now) : (active!.Disp(target) - active.Disp(now)) / span;
            result *= factor;
        }
        catch (Exception ex) { Report(ex); }
    }

    private static double lastRow;

    /// <summary>The battle's current visual row, or the last one seen if it can't be read.</summary>
    private static double NowRow()
    {
        try
        {
            var position = conductor!.songPosition;
            if (position != null) lastRow = position.VisualTime.Row;
        }
        catch { }
        return lastRow;
    }

    // How far ahead (top) and behind (bottom) notes are spawned, in rows: the same screen distance
    // covers more or fewer rows when the notes scroll slower or faster.
    private static void TopRowsPostfix(ref double __result)
    {
        if (!Live) return;
        double v = NowRow();
        __result = Math.Max(__result, active!.Inverse(active.Disp(v) + __result) - v);
    }

    private static void BottomRowsPostfix(ref double __result)
    {
        if (!Live) return;
        double v = NowRow();
        __result = Math.Max(__result, v - active!.Inverse(active.Disp(v) - __result));
    }

    private static void Report(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        ModLog.Error("Scroll speed changes failed: " + ex);
    }
}
