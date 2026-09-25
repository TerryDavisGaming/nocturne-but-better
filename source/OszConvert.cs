using System.Globalization;

namespace NocturneFlatScroll;

// The osu!mania import's conversion (beta): osu!'s timing, notes and speed changes as a battle
// chart (.sm) the battle creator and the chart editor edit like any other.
//
// Timing: osu!'s red lines become #BPMS. Each red line starts on a whole beat: when it isn't on
//   one, (part of) the beat before it is stretched to reach it (a "filler" beat, shown at the speed
//   around it), so both grids stay exact. Beat 0 goes on a beat of the first red line and becomes
//   #OFFSET.
//   Every note keeps its osu! time: its row is the nearest 1/48 beat to that time, worked out with
//   the chart editor's own timing read back from the tags as written.
// Speed: osu!mania scrolls at speed x (tempo / main tempo) and the game at ratio x tempo, so a
//   green line's speed is the #SCROLLS ratio from its beat. Red lines with a tempo no song has
//   (osu!'s tempo speed effects) keep the tempo around them and become a ratio instead.
// This file has no Unity or game dependencies.

/// <summary>A tempo section of the converted chart: an osu! red line, or a filler beat before one.</summary>
internal sealed class OszSection
{
    internal double Time;             // osu! ms where it starts
    internal double OsuBpm;           // the red line's tempo as osu! plays it
    internal double Bpm;              // the tempo the chart uses: the red line's, unless it's an osu! speed effect
    internal double Speed = 1;        // the scroll speed it stands for (OsuBpm / Bpm, or a filler's evening-out)
    internal double FillerRatio = 1;  // the evening-out alone: a filler's, or a stretched red line's; else 1
    internal bool Filler;
    internal int Meter = 4;
    internal double Beat;             // the chart's beat where it starts
    internal double ChartBpm;         // the tempo written, once the next section's beat is known
}

/// <summary>A lane group's timing: #OFFSET and #BPMS, and the clock every note is placed with.</summary>
internal sealed class OszTiming
{
    internal string OffsetTag = "0", BpmsTag = "";
    internal readonly List<OszSection> Sections = new();
    internal int Fillers, RowSnaps, Restated, Effects, Merged;
    /// <summary>Sections played slower than in osu! (fillers, and a red line's section when the next starts on the nearest 1/48 beat), each evened out by a speed change.</summary>
    internal int Stretched;
    /// <summary>Where beat 0 is, in osu!'s ms.</summary>
    internal double BeatZeroMs;
    /// <summary>The chart editor's timing, read from the tags as written.</summary>
    internal EditorChart Chart = new(4);
    internal OszClock Clock = null!;
    internal double MinBpm, MaxBpm;
    internal int Changes;
}

/// <summary>
/// The chart editor's own timing (<see cref="EditorChart"/> without #STOPS) with its sums kept, so
/// a lookup is a binary search rather than a walk over every tempo change. The sums are made in
/// the same order as the editor's, so the results are the editor's to the last bit.
/// </summary>
internal sealed class OszClock
{
    private readonly double[] beats, bpms, starts;

    internal OszClock(EditorChart chart)
    {
        int n = chart.Bpms.Count;
        beats = new double[n];
        bpms = new double[n];
        starts = new double[n];
        double t = -chart.Offset, lastBeat = 0, bpm = chart.Bpms[0].Bpm;
        beats[0] = 0;
        bpms[0] = bpm;
        starts[0] = t;
        for (int i = 1; i < n; i++)
        {
            t += (chart.Bpms[i].Beat - lastBeat) * 60.0 / bpm;
            lastBeat = chart.Bpms[i].Beat;
            bpm = chart.Bpms[i].Bpm;
            beats[i] = lastBeat;
            bpms[i] = bpm;
            starts[i] = t;
        }
    }

    /// <summary>EditorChart.BeatToSeconds: the section is the last change before the beat.</summary>
    internal double BeatToSeconds(double beat)
    {
        // The first change at or after the beat, from 1 on.
        int lo = 1, hi = beats.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (beats[mid] < beat) lo = mid + 1;
            else hi = mid;
        }
        int k = lo - 1;
        return starts[k] + (beat - beats[k]) * 60.0 / bpms[k];
    }

    /// <summary>EditorChart.SecondsToBeat: a time on a section's end belongs to the section before.</summary>
    internal double SecondsToBeat(double seconds)
    {
        // The first section end (the next section's start) at or after the time.
        int lo = 1, hi = starts.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (seconds <= starts[mid]) hi = mid;
            else lo = mid + 1;
        }
        int k = lo - 1;
        return beats[k] + (seconds - starts[k]) * bpms[k] / 60.0;
    }

    internal double RowToSeconds(int row) => BeatToSeconds(row / (double)EditorChart.RowsPerBeat);
}

/// <summary>One difficulty's notes on a lane group's timing, and what placing them changed.</summary>
internal sealed class OszChart
{
    internal readonly List<EditorChart.Note> Notes = new();
    internal int Taps, Holds;
    /// <summary>Notes more than 2 ms from their osu! time, and the farthest.</summary>
    internal int Moved;
    internal double WorstMs;
    /// <summary>Most of its notes aren't on its own beat grid (its tempo may be a placeholder).</summary>
    internal bool NotOnGrid;
    internal int Early, AfterEnd, Duplicates, Shortened, HoldsToTaps, Skipped, BadLines, PastLimit;
    /// <summary>Notes a second over the lane group's span (so harder charts have more).</summary>
    internal double Density;
    internal int Meter = 1;
    /// <summary>The #NOTES text's size.</summary>
    internal long TextBytes;
    internal int FirstRow = -1, LastRow = -1;
    /// <summary>
    /// Notes in the song's first <see cref="OszConvert.OpeningSeconds"/> (the first ones, as the
    /// notes are in order), and the holds among them: a battle starts with the song, so the
    /// player can leave them out.
    /// </summary>
    internal int Opening, OpeningHolds;
}

/// <summary>A difficulty's speed changes as #SCROLLS.</summary>
internal sealed class OszSpeeds
{
    internal readonly List<(double Beat, double Ratio)> Scrolls = new();
    internal string Tag = "";
    /// <summary>Ratios outside the game's x0.05 to x20, kept at those limits.</summary>
    internal int Clamped;
    /// <summary>Green lines outside osu!'s own 0.01 to 10, which osu! keeps at its limits.</summary>
    internal int OutsideOsu;
    /// <summary>When there were more than <see cref="OszConvert.MaxScrolls"/>: the time (seconds) from which the rest were dropped.</summary>
    internal double? CappedAtSeconds;
    internal int Count => Scrolls.Count;
    internal double Min => Scrolls.Count == 0 ? 1 : Scrolls.Min(s => s.Ratio);
    internal double Max => Scrolls.Count == 0 ? 1 : Scrolls.Max(s => s.Ratio);
    /// <summary>
    /// osu!'s own speed changes: the same without the ones that keep stretched sections looking
    /// even. The summary counts these; the battle gets <see cref="Scrolls"/>.
    /// </summary>
    internal readonly List<(double Beat, double Ratio)> Own = new();
    internal double OwnMin => Own.Count == 0 ? 1 : Own.Min(s => s.Ratio);
    internal double OwnMax => Own.Count == 0 ? 1 : Own.Max(s => s.Ratio);
    /// <summary>How many more the battle gets to keep the stretched sections looking even.</summary>
    internal int Evening => Math.Max(0, Scrolls.Count - Own.Count);
}

internal static class OszConvert
{
    internal const double MinBpm = 40, MaxBpm = 1000;
    internal const double MinRatio = 0.05, MaxRatio = 20;   // ScrollSpeeds.Parse
    internal const double WholeBeatSnapMs = 2;
    internal const double MovedMs = 2;
    /// <summary>osu! keeps note times in whole ms, so a note this close to its row is on it.</summary>
    internal const double FillerNoteMs = 1;
    internal const int MaxScrolls = 5000;
    internal const int RowsPerBeat = EditorChart.RowsPerBeat;
    /// <summary>
    /// osu! waits about 2 s before the song; a battle starts with it, so notes this soon come
    /// right away. The summary warns about them, and the player can leave them out.
    /// </summary>
    internal const double OpeningSeconds = 1.5;

    // Notes a second (first to last note) halfway between the medians of the game's own charts for
    // each slot (research/osz/gamestats.py over its 237 charts).
    private static readonly double[] Thresholds4 = { 1.3, 2.1, 3.5, 5.2, 6.8 };
    private static readonly double[] Thresholds5 = { 1.6, 2.7, 4.4, 6.5, 8.4 };

    internal static string ModeName(long mode) => mode switch
    {
        0 => "osu!standard", 1 => "osu!taiko", 2 => "osu!catch", 3 => "osu!mania",
        _ => "mode " + mode.ToString(CultureInfo.InvariantCulture)
    };

    private static bool Musical(double bpm) => bpm >= MinBpm && bpm <= MaxBpm;

    // ---- timing ----------------------------------------------------------------------------------

    /// <param name="firstNoteMs">The lane group's first note that can be played, in osu! ms.</param>
    /// <param name="lastNoteMs">The group's last note (or hold end) that can be played.</param>
    /// <param name="shift">Seconds to take off osu!'s times (<see cref="OsuAudio.Shift"/>).</param>
    /// <param name="noteMs">Every note start and hold end of the group, in osu! ms, sorted: a filler goes where it moves them least.</param>
    internal static OszTiming Timing(OsuFile from, double firstNoteMs, double lastNoteMs, double shift, double[] noteMs, OszGuard guard)
    {
        var timing = new OszTiming();
        var reds = from.Timing.Where(t => t.IsTempo).ToList();
        reds.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Order.CompareTo(b.Order));
        var segs = new List<OszSection>();
        foreach (var r in reds)
        {
            guard.Step();
            if (segs.Count > 0 && segs[^1].Time == r.Time) continue;   // the same time: the first counts (as in osu!lazer)
            if (segs.Count > 0 && r.Time > lastNoteMs) break;          // after the last note: no use
            segs.Add(new OszSection { Time = r.Time, OsuBpm = r.Bpm, Meter = r.Meter });
        }
        if (segs.Count == 0) throw new InvalidDataException("no tempo line");

        // Tempos no song has are osu! speed effects: keep the tempo around them, and the speed as a ratio.
        var nextMusical = new int[segs.Count];
        for (int i = segs.Count - 1, next = -1; i >= 0; i--)
        {
            nextMusical[i] = next;
            if (Musical(segs[i].OsuBpm)) next = i;
        }
        int lastMusical = -1;
        for (int i = 0; i < segs.Count; i++)
        {
            var seg = segs[i];
            if (Musical(seg.OsuBpm))
            {
                seg.Bpm = seg.OsuBpm;
                lastMusical = i;
                continue;
            }
            timing.Effects++;
            double bpm = lastMusical >= 0 ? segs[lastMusical].OsuBpm : nextMusical[i] >= 0 ? segs[nextMusical[i]].OsuBpm : seg.OsuBpm;
            for (int step = 0; step < 64 && bpm > MaxBpm; step++) bpm /= 2;
            for (int step = 0; step < 64 && bpm < MinBpm; step++) bpm *= 2;
            seg.Bpm = bpm;
        }
        foreach (var seg in segs) seg.Speed = seg.OsuBpm / seg.Bpm;
        // osu!mania scrolls relative to the map's main tempo and a battle relative to its chart's.
        // They only differ when the main tempo is itself a speed effect: then every speed is taken
        // relative to it, so the main part plays at x1, as it does in osu!.
        double main = MainSpeed(segs, lastNoteMs);
        if (main != 1) foreach (var seg in segs) seg.Speed /= main;

        // A section shorter than a 1/48 beat can't hold a note or be seen: it joins the one before.
        var kept = new List<OszSection>(segs.Count);
        for (int i = 0; i < segs.Count; i++)
        {
            if (i + 1 < segs.Count && segs[i + 1].Time - segs[i].Time < 60000 / segs[i].Bpm / RowsPerBeat)
            {
                timing.Merged++;
                continue;
            }
            kept.Add(segs[i]);
        }
        segs = kept;

        // Beat 0: a beat of the first tempo at or before the first note, and not before the song
        // when that can be; a bar line when one fits. Otherwise before the song (a positive #OFFSET).
        var s0 = segs[0];
        double len0 = 60000 / s0.Bpm;
        double shiftMs = shift * 1000;
        long kMax = (long)Math.Floor((firstNoteMs - s0.Time) / len0 + 1.0 / (2 * RowsPerBeat));
        if (segs.Count > 1) kMax = Math.Min(kMax, (long)Math.Ceiling((segs[1].Time - s0.Time) / len0) - 1);
        long kMin = (long)Math.Ceiling((shiftMs - s0.Time) / len0 - 1e-9);
        long k;
        if (kMin <= 0 && kMax >= 0) k = 0;
        else if (kMin <= kMax)
        {
            long bar = FloorTo(kMax, s0.Meter);
            k = bar >= kMin ? bar : kMax;
        }
        else k = kMax;
        // The loader takes an #OFFSET of at most 600 s: when the first tempo line comes later than
        // that, beat 0 goes earlier on its grid (a bar line), before the tempo line.
        double latestMs = shiftMs + (LeadIn.MaxOffset - 1) * 1000;
        if (s0.Time + k * len0 > latestMs) k = Math.Min(k, FloorTo((long)Math.Floor((latestMs - s0.Time) / len0), s0.Meter));
        timing.BeatZeroMs = s0.Time + k * len0;
        s0.Beat = -k;

        // Each later red line on a whole beat. Within 2 ms of one: that beat (the tempo before it
        // changes a hair). Otherwise the part of the beat before it is stretched to reach it (a
        // filler, at most half as fast, so it never raises the top tempo), so the notes on both
        // sides stay on grids.
        var chart = new List<OszSection> { s0 };
        for (int i = 1; i < segs.Count; i++)
        {
            guard.Step();
            var prev = chart[^1];
            var seg = segs[i];
            double exact = prev.Beat + (seg.Time - prev.Time) * prev.Bpm / 60000;
            double whole = Math.Round(exact);
            bool nearWhole = Math.Abs(exact - whole) * 60000 / prev.Bpm <= WholeBeatSnapMs && whole >= prev.Beat + 1;
            // The same tempo again on the same grid (mappers add these for bar lines and hit sounds): nothing changes.
            if (nearWhole && seg.Bpm == prev.Bpm && seg.Speed == prev.Speed)
            {
                timing.Restated++;
                continue;
            }
            if (nearWhole) seg.Beat = whole;
            else if (FillerStart(prev, seg, exact, noteMs, guard) is double start)
            {
                var filler = new OszSection { Filler = true, Beat = start, OsuBpm = prev.OsuBpm, Meter = prev.Meter };
                filler.Time = prev.Time + (filler.Beat - prev.Beat) * 60000 / prev.Bpm;
                filler.Bpm = 60000 * (Math.Floor(exact) - start) / (seg.Time - filler.Time);
                filler.FillerRatio = prev.Bpm / filler.Bpm;
                filler.Speed = prev.Speed * filler.FillerRatio;
                chart.Add(filler);
                seg.Beat = Math.Floor(exact);
                timing.Fillers++;
            }
            else
            {
                // No room for a filler: the 1/48 beat at or before it, so the section before only
                // slows (a later row would speed it up and could raise the chart's top tempo, which
                // slows the whole battle under MMod), and a speed change keeps it looking the same.
                seg.Beat = Math.Max(Math.Floor(exact * RowsPerBeat + 1e-6) / RowsPerBeat, prev.Beat + 1.0 / RowsPerBeat);
                double stretch = (exact - prev.Beat) / (seg.Beat - prev.Beat);
                if (stretch > 1)
                {
                    prev.FillerRatio *= stretch;
                    prev.Speed *= stretch;
                }
                timing.RowSnaps++;
            }
            chart.Add(seg);
        }
        for (int i = 0; i < chart.Count; i++)
            chart[i].ChartBpm = i + 1 < chart.Count ? 60000 * (chart[i + 1].Beat - chart[i].Beat) / (chart[i + 1].Time - chart[i].Time) : chart[i].Bpm;
        timing.Sections.AddRange(chart);
        timing.Stretched = chart.Count(c => Math.Round(c.FillerRatio, 3) != 1);
        // A red line within 2 ms of beat 0 starts on it: beat 0 is then exactly that red line.
        var zero = chart.LastOrDefault(c => c.Beat <= 0);
        if (zero != null && zero != s0) timing.BeatZeroMs = zero.Time - zero.Beat * 60000 / zero.ChartBpm;

        // #BPMS: the tempo at beat 0 first, then each change after it, skipping repeats.
        var bpms = new List<(string Beat, string Bpm)>();
        for (int i = 0; i < chart.Count; i++)
        {
            if (i + 1 < chart.Count && chart[i + 1].Beat <= 0) continue;   // ends at or before beat 0
            string text = BattleTiming.Number(chart[i].ChartBpm);
            if (bpms.Count > 0 && bpms[^1].Bpm == text) continue;
            bpms.Add((bpms.Count == 0 ? "0" : BattleTiming.Number(chart[i].Beat), text));
        }
        timing.BpmsTag = string.Join(",", bpms.Select(b => b.Beat + "=" + b.Bpm));
        timing.OffsetTag = BattleTiming.Number(shift - timing.BeatZeroMs / 1000);

        // From here on the editor's own timing, read from the tags as written, places everything.
        var header = new ChartText();
        header.SetTag("OFFSET", timing.OffsetTag);
        header.SetTag("BPMS", timing.BpmsTag);
        timing.Chart.ReadTiming(header);
        timing.Clock = new OszClock(timing.Chart);
        timing.MinBpm = timing.Chart.Bpms.Min(b => b.Bpm);
        timing.MaxBpm = timing.Chart.Bpms.Max(b => b.Bpm);
        timing.Changes = timing.Chart.Bpms.Count - 1;
        return timing;
    }

    private static long FloorTo(long value, int step) => step <= 1 ? value : (long)Math.Floor(value / (double)step) * step;

    /// <summary>
    /// Where a filler before a red line off the grid starts: on a row of the grid before it, at
    /// most a beat before the whole beat the red line goes on and late enough that the filler is
    /// at most half as fast; after the section before starts, and not before beat 0 (so beat 0
    /// stays on the first tempo's grid). Notes before the filler stay exactly on their grid and
    /// the ones inside it land on its longer rows, so of those starts it takes the first whose
    /// notes stay within <see cref="FillerNoteMs"/>, else the one that moves them least. Null when
    /// none fits.
    /// </summary>
    /// <param name="noteMs">Every note start and hold end of the lane group, in osu! ms, sorted.</param>
    private static double? FillerStart(OszSection prev, OszSection seg, double exact, double[] noteMs, OszGuard guard)
    {
        double end = Math.Floor(exact), frac = exact - end;
        double earliest = Math.Max(Math.Max(end - 1, prev.Beat + 1.0 / RowsPerBeat), 0);
        long first = (long)Math.Ceiling(earliest * RowsPerBeat - 1e-6);
        long last = Math.Min((long)Math.Floor((end - frac) * RowsPerBeat + 1e-6), (long)Math.Round(end * RowsPerBeat) - 1);
        if (first > last) return null;
        double? best = null;
        double bestError = double.PositiveInfinity;
        for (long row = first; row <= last; row++)
        {
            double start = row / (double)RowsPerBeat;
            double startMs = prev.Time + (start - prev.Beat) * 60000 / prev.Bpm;
            double span = seg.Time - startMs, beats = end - start;
            // The notes inside: each on the filler's nearest row, and how far that is from it.
            double worst = 0;
            int at = Array.BinarySearch(noteMs, startMs);
            if (at < 0) at = ~at;
            for (int n = 0; at < noteMs.Length && noteMs[at] < seg.Time; at++, n++)
            {
                guard.Step();
                double place = Math.Round((noteMs[at] - startMs) / span * beats * RowsPerBeat) / RowsPerBeat;
                worst = Math.Max(worst, Math.Abs(startMs + place / beats * span - noteMs[at]));
                // A beat with this many notes in it moves some whichever row it starts on.
                if (n >= 1000) break;
            }
            if (worst <= FillerNoteMs) return start;
            if (worst < bestError)
            {
                bestError = worst;
                best = start;
            }
        }
        return best;
    }

    // osu!'s main tempo (lazer's "most common beat length"): each red line's time up to the last
    // note, the first counted from 0 as osu!stable did, summed by beat length. Returns the speed of
    // the longest section with that tempo: 1 unless it's a speed effect.
    private static double MainSpeed(List<OszSection> segs, double lastNoteMs)
    {
        var byTempo = new Dictionary<double, double>();
        var keys = new double[segs.Count];
        var lengths = new double[segs.Count];
        for (int i = 0; i < segs.Count; i++)
        {
            double start = i == 0 ? 0 : segs[i].Time;
            double end = i + 1 < segs.Count ? segs[i + 1].Time : lastNoteMs;
            keys[i] = Math.Round(60000 / segs[i].OsuBpm, 3);
            lengths[i] = Math.Max(0, end - start);
            byTempo[keys[i]] = byTempo.GetValueOrDefault(keys[i]) + lengths[i];
        }
        double mainKey = keys[0], most = -1;
        foreach (var (key, length) in byTempo)
            if (length > most) { most = length; mainKey = key; }
        double speed = 1, longest = -1;
        for (int i = 0; i < segs.Count; i++)
            if (keys[i] == mainKey && lengths[i] > longest) { longest = lengths[i]; speed = segs[i].Speed; }
        return speed;
    }

    // ---- notes -----------------------------------------------------------------------------------

    private struct Placed
    {
        internal int Row, End, Lane;
        internal bool Hold;
        internal double ErrorMs;
    }

    /// <summary>A difficulty's notes and holds on the group's timing.</summary>
    /// <param name="songSeconds">The song's length: notes from there on can't be played.</param>
    internal static OszChart Notes(OsuFile d, int lanes, OszTiming timing, double shift, double songSeconds, OszGuard guard)
    {
        var chart = new OszChart
        {
            Skipped = d.OtherObjects,
            BadLines = d.BadLines,
            PastLimit = d.TimingPastLimit + d.ObjectsPastLimit,
            HoldsToTaps = d.EmptyHolds,
        };
        var clock = timing.Clock;
        int lastSongRow = (int)Math.Floor(clock.SecondsToBeat(songSeconds) * RowsPerBeat);
        if (clock.RowToSeconds(lastSongRow) >= songSeconds) lastSongRow--;
        var placed = new List<Placed>(d.Objects.Count);
        foreach (var o in d.Objects)
        {
            guard.Step();
            double t = o.Time / 1000 - shift;
            if (t < -0.0005) { chart.Early++; continue; }
            if (t >= songSeconds) { chart.AfterEnd++; continue; }
            int row = (int)Math.Round(clock.SecondsToBeat(t) * RowsPerBeat);
            // On the grid, a note right at the song's start or end can land just outside it.
            double at = clock.RowToSeconds(row);
            if (row < 0 || at < -0.0005) { chart.Early++; continue; }
            if (at >= songSeconds) { chart.AfterEnd++; continue; }
            var p = new Placed { Row = row, End = row, Lane = o.Column(lanes), ErrorMs = Math.Abs(at - t) * 1000 };
            if (o.IsHold)
            {
                double end = o.EndTime / 1000 - shift;
                int endRow = end >= songSeconds ? lastSongRow : Math.Min(lastSongRow, (int)Math.Round(clock.SecondsToBeat(end) * RowsPerBeat));
                if (endRow > row) { p.Hold = true; p.End = endRow; }
                else chart.HoldsToTaps++;
            }
            placed.Add(p);
        }

        // One note a cell; a hold ends at least a row before the next note in its lane.
        placed.Sort((a, b) => a.Lane != b.Lane ? a.Lane.CompareTo(b.Lane) : a.Row != b.Row ? a.Row.CompareTo(b.Row) : b.Hold.CompareTo(a.Hold));
        var kept = new List<Placed>(placed.Count);
        foreach (var n in placed)
        {
            guard.Step();
            if (kept.Count > 0 && kept[^1].Lane == n.Lane)
            {
                var last = kept[^1];
                if (last.Row == n.Row) { chart.Duplicates++; continue; }
                if (last.Hold && last.End >= n.Row)
                {
                    last.End = n.Row - 1;
                    if (last.End <= last.Row) { last.Hold = false; last.End = last.Row; chart.HoldsToTaps++; }
                    else chart.Shortened++;
                    kept[^1] = last;
                }
            }
            kept.Add(n);
        }
        foreach (var n in kept)
        {
            chart.Notes.Add(new EditorChart.Note { Row = n.Row, Lane = n.Lane, Type = n.Hold ? '2' : '1', EndRow = n.End });
            if (n.Hold) chart.Holds++;
            else chart.Taps++;
            if (n.ErrorMs > MovedMs) chart.Moved++;
            chart.WorstMs = Math.Max(chart.WorstMs, n.ErrorMs);
        }
        chart.Notes.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Lane.CompareTo(b.Lane));
        chart.NotOnGrid = chart.Notes.Count > 0 && chart.Moved > chart.Notes.Count / 4.0;
        if (chart.Notes.Count > 0)
        {
            chart.FirstRow = chart.Notes[0].Row;
            chart.LastRow = chart.Notes[^1].Row;
        }
        // The chart editor's own estimate (EstimateMeter).
        if (chart.Notes.Count >= 2)
        {
            double span = clock.RowToSeconds(chart.LastRow) - clock.RowToSeconds(chart.FirstRow);
            double nps = span > 0 ? chart.Notes.Count / span : 0;
            chart.Meter = Math.Clamp((int)Math.Round(nps * 1.6), 1, 20);
        }
        chart.TextBytes = WriteNotes(chart.Notes, lanes).Length;
        // The notes the player can leave out because they come before a battle has shown them.
        while (chart.Opening < chart.Notes.Count && clock.RowToSeconds(chart.Notes[chart.Opening].Row) < OpeningSeconds)
        {
            if (chart.Notes[chart.Opening].Type == '2') chart.OpeningHolds++;
            chart.Opening++;
        }
        return chart;
    }

    /// <summary>A chart's notes in the battle: all of them, or those after the opening seconds when the player leaves those out.</summary>
    internal static List<EditorChart.Note> Kept(OszChart chart, OszChoices choices) =>
        choices.LeaveOutOpening && chart.Opening > 0 ? chart.Notes.GetRange(chart.Opening, chart.Notes.Count - chart.Opening) : chart.Notes;

    internal static int KeptCount(OszChart chart, OszChoices choices) => chart.Notes.Count - (choices.LeaveOutOpening ? chart.Opening : 0);

    internal static string WriteNotes(List<EditorChart.Note> notes, int lanes)
    {
        var writer = new EditorChart(lanes) { Notes = notes };
        return writer.WriteNotes();
    }

    // ---- speed changes ---------------------------------------------------------------------------

    /// <summary>
    /// #SCROLLS for a difficulty's green lines (osu!'s slider velocity) on the group's timing, with
    /// the tempo sections' own ratios (speed effects, fillers). With <paramref name="from"/> null,
    /// only the fillers' ratios, which keep them looking even ("no speed changes").
    /// </summary>
    internal static OszSpeeds Speeds(OsuFile? from, OszTiming timing, double shift, double lastNoteMs, OszGuard guard)
    {
        var result = new OszSpeeds();
        var speeds = new List<(double Time, double Speed)>();
        if (from != null)
        {
            var points = from.Timing.ToList();
            points.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Order.CompareTo(b.Order));
            // osu!'s speed from each time with timing points on: the last green line there, else 1 (a red line resets it).
            for (int i = 0; i < points.Count;)
            {
                guard.Step();
                double t = points[i].Time;
                double? green = null;
                for (; i < points.Count && points[i].Time == t; i++)
                {
                    if (points[i].Uninherited) continue;
                    green = points[i].Speed;
                    if (points[i].SpeedOutsideOsu) result.OutsideOsu++;
                }
                speeds.Add((t, green ?? 1.0));
            }
        }
        Place(speeds, timing, shift, lastNoteMs, from == null, true, result.Scrolls, result, guard);
        // What the summary counts: osu!'s own changes, without the ones that even out stretched sections.
        if (from != null) Place(speeds, timing, shift, lastNoteMs, false, false, result.Own, null, guard);
        result.Tag = ScrollSpeeds.Write(result.Scrolls);
        return result;
    }

    // The speed changes on the chart's beats, into list. fillersOnly: only the stretched sections'
    // evening-out; evenOut false: without it. Clamps and the cap are counted in counts when given.
    private static void Place(List<(double Time, double Speed)> speeds, OszTiming timing, double shift, double lastNoteMs,
        bool fillersOnly, bool evenOut, List<(double Beat, double Ratio)> list, OszSpeeds? counts, OszGuard guard)
    {
        var sections = timing.Sections;
        var clock = timing.Clock;
        double Ratio(double sv, OszSection section, bool count)
        {
            double ratio = fillersOnly ? section.FillerRatio : evenOut ? sv * section.Speed : sv * section.Speed / section.FillerRatio;
            if (count && counts != null && (ratio < MinRatio || ratio > MaxRatio)) counts.Clamped++;
            return Math.Round(Math.Clamp(ratio, MinRatio, MaxRatio), 3);
        }

        double atZero = Ratio(1, sections[0], false);   // before the first section, its speed applies (as in osu!)
        double sv = 1;
        var section = sections[0];
        int si = 0, ki = 0;
        while (true)
        {
            guard.Step();
            double t = double.PositiveInfinity;
            if (ki < speeds.Count) t = speeds[ki].Time;
            if (si < sections.Count) t = Math.Min(t, sections[si].Time);
            if (double.IsPositiveInfinity(t) || t > lastNoteMs) break;
            while (ki < speeds.Count && speeds[ki].Time <= t) sv = speeds[ki++].Speed;
            while (si < sections.Count && sections[si].Time <= t) section = sections[si++];
            double r = Ratio(sv, section, true);
            double seconds = t / 1000 - shift;
            double beat = clock.SecondsToBeat(seconds);
            double rowBeat = Math.Round(beat * RowsPerBeat) / RowsPerBeat;
            beat = Math.Abs(clock.BeatToSeconds(rowBeat) - seconds) <= 0.001 ? rowBeat : Math.Round(beat, 6);
            // The change in effect at beat 0 counts from beat 0 (and before it, as in battle).
            if (beat <= 0)
            {
                atZero = r;
                continue;
            }
            if (list.Count == 0 && atZero != 1) list.Add((0, atZero));
            if (list.Count > 0 && list[^1].Beat >= beat)
            {
                // Two at one beat: the later wins, and it goes if it changes nothing.
                list[^1] = (list[^1].Beat, r);
                if (r == (list.Count > 1 ? list[^2].Ratio : 1)) list.RemoveAt(list.Count - 1);
                continue;
            }
            if (r == (list.Count > 0 ? list[^1].Ratio : 1)) continue;
            if (list.Count >= MaxScrolls)
            {
                if (counts != null) counts.CappedAtSeconds = seconds;
                break;
            }
            list.Add((beat, r));
        }
        if (list.Count == 0 && atZero != 1) list.Add((0, atZero));
    }

    // ---- slots -------------------------------------------------------------------------------------

    /// <summary>The slot (0 Beginner to 5 Zen) a chart this dense would be, by the game's own charts.</summary>
    internal static int WantedSlot(double density, int lanes)
    {
        int slot = 0;
        foreach (double t in lanes == 5 ? Thresholds5 : Thresholds4)
            if (density >= t) slot++;
        return slot;
    }

    /// <summary>Strictly increasing slots nearest to what each (sorted, easiest first) difficulty wants; -1 = left out (only past six).</summary>
    internal static int[] AssignSlots(int[] wanted)
    {
        int n = wanted.Length;
        const int skip = 100;
        var cost = new int[n + 1, 7];
        var take = new int[n + 1, 7];
        for (int i = n - 1; i >= 0; i--)
            for (int j = 6; j >= 0; j--)
            {
                int best = cost[i + 1, j] + skip, pick = -1;
                for (int s = j; s < 6; s++)
                {
                    int c = cost[i + 1, s + 1] + Math.Abs(s - wanted[i]);
                    if (c < best) { best = c; pick = s; }
                }
                cost[i, j] = best;
                take[i, j] = pick;
            }
        var result = new int[n];
        for (int i = 0, j = 0; i < n; i++)
        {
            result[i] = take[i, j];
            if (result[i] >= 0) j = result[i] + 1;
        }
        return result;
    }

    // ---- player attacks (5 lanes) ----------------------------------------------------------------

    /// <summary>
    /// #ATTACKS with a player attack every 8 bars (beats 32, 64, ...) from 16 beats after the first
    /// note to the last one, or one at the last note when none fits: the chart editor's own format.
    /// </summary>
    internal static string PlayerAttacks(OszTiming timing, int firstRow, int lastRow) =>
        string.Join(":", PlayerAttackRows(firstRow, lastRow).Select(r =>
            "TIME=" + timing.Clock.RowToSeconds(r).ToString("0.000", CultureInfo.InvariantCulture) + ":LEN=0.500:MODS=PlayerAttack 1"));

    /// <summary>The rows <see cref="PlayerAttacks"/> puts a player attack on.</summary>
    internal static List<int> PlayerAttackRows(int firstRow, int lastRow)
    {
        double first = firstRow / (double)RowsPerBeat, last = lastRow / (double)RowsPerBeat;
        var rows = new List<int>();
        for (int beat = 32; beat <= last; beat += 32)
            if (beat >= first + 16) rows.Add(beat * RowsPerBeat);
        if (rows.Count == 0) rows.Add(lastRow);
        return rows;
    }

    // ---- the chart -------------------------------------------------------------------------------

    /// <summary>
    /// The battle's chart for a plan and the player's choices: the header (title, song, timing,
    /// speed changes, player attacks, bookmarks) and one block per chosen slot. Also each slot's
    /// note count (-1 for an empty slot).
    /// </summary>
    internal static (string Text, int[] NoteCounts) Build(OszPlan plan, OszChoices choices)
    {
        var group = plan.Group(choices.Lanes) ?? throw new InvalidDataException($"the beatmap has no {choices.Lanes}K difficulty");
        var song = plan.Song ?? throw new InvalidDataException("the beatmap has no song");
        var chart = new ChartText();
        chart.SetTag("TITLE", BattleDraft.CleanTagValue(plan.Title));
        if (BattleDraft.CleanTagValue(plan.Artist).Length > 0) chart.SetTag("ARTIST", BattleDraft.CleanTagValue(plan.Artist));
        if (BattleDraft.CleanTagValue(plan.Mapper).Length > 0) chart.SetTag("CREDIT", BattleDraft.CleanTagValue(plan.Mapper));
        chart.SetTag("MUSIC", BattleDraft.CleanTagValue(song.FileName));
        chart.SetTag("OFFSET", group.Timing.OffsetTag);
        chart.SetTag("BPMS", group.Timing.BpmsTag);
        var speeds = choices.SpeedsFrom != null && group.Speeds.TryGetValue(choices.SpeedsFrom, out var chosen) ? chosen : group.BaseScrolls;
        if (speeds.Tag.Length > 0) chart.SetTag("SCROLLS", speeds.Tag);

        var counts = new int[6];
        int firstRow = int.MaxValue, lastRow = -1;
        for (int s = 0; s < 6; s++)
        {
            var d = choices.Slots[s];
            var kept = d != null && group.Charts.TryGetValue(d, out var converted) ? Kept(converted, choices) : null;
            if (kept == null || kept.Count == 0)
            {
                counts[s] = -1;
                continue;
            }
            counts[s] = kept.Count;
            firstRow = Math.Min(firstRow, kept[0].Row);
            lastRow = Math.Max(lastRow, kept[^1].Row);
            chart.Blocks.Add(new ChartText.NoteBlock
            {
                StepsType = choices.Lanes == 5 ? "pump-single" : "dance-single",
                Description = BattleDraft.CleanTagValue(d!.Name),
                Difficulty = ChartText.GameDifficultyNames[s],
                Meter = group.Charts[d].Meter.ToString(CultureInfo.InvariantCulture),
                Radar = "0,0,0,0,0",
                Notes = WriteNotes(kept, choices.Lanes),
            });
        }
        if (choices.Lanes == 5 && choices.PlayerAttacks && lastRow >= 0)
            chart.SetTag("ATTACKS", PlayerAttacks(group.Timing, firstRow, lastRow));
        if (group.Bookmarks.Count > 0)
            chart.SetTag("NBBBOOKMARKS", string.Join(",", group.Bookmarks.Select(b => b.ToString("0.###", CultureInfo.InvariantCulture))));
        return (chart.Write(), counts);
    }

    // ---- what the import changed or left out -----------------------------------------------------

    /// <summary>
    /// Everything the import changed to fit a battle and what osu! has that a battle doesn't use,
    /// for these choices, one plain sentence each.
    /// </summary>
    internal static List<string> Details(OszPlan plan, OszChoices choices)
    {
        var items = new List<string>(plan.Details);
        var group = plan.Group(choices.Lanes);
        if (group == null) return items;
        var included = choices.Slots.Where(d => d != null).Select(d => d!).ToList();
        foreach (var d in included)
        {
            if (!group.Charts.TryGetValue(d, out var c)) continue;
            string name = Quote(d.Name);
            if (c.NotOnGrid || c.WorstMs > 5)
                items.Add($"{name}: {Count(c.Moved, "note", "notes")} moved to the beat grid, by at most {Ms(c.WorstMs)} ms.");
            if (c.Shortened > 0)
                items.Add($"{name}: {Count(c.Shortened, "hold ends", "holds end")} a row early so the next note in {(c.Shortened == 1 ? "its" : "their")} lane fits.");
            if (c.HoldsToTaps > 0)
                items.Add($"{name}: {Count(c.HoldsToTaps, "hold", "holds")} too short for the 1/48 grid became {(c.HoldsToTaps == 1 ? "a tap" : "taps")}.");
            if (c.Duplicates > 0)
                items.Add(c.Duplicates == 1
                    ? $"{name}: 1 note landed on another note in the same lane and was dropped."
                    : $"{name}: {N(c.Duplicates)} notes landed on other notes in the same lane and were dropped.");
            if (c.Early > 0) items.Add($"{name}: {Count(c.Early, "note", "notes")} before the song starts {(c.Early == 1 ? "was" : "were")} left out.");
            if (choices.LeaveOutOpening && c.Opening > 0)
                items.Add($"{name}: {Count(c.Opening, "note", "notes")} in the first {OpeningSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s {(c.Opening == 1 ? "was" : "were")} left out, as you chose.");
            if (c.AfterEnd > 0) items.Add($"{name}: {Count(c.AfterEnd, "note", "notes")} after the song ends {(c.AfterEnd == 1 ? "was" : "were")} left out.");
            if (c.Skipped > 0) items.Add($"{name}: {Count(c.Skipped, "slider or spinner was", "sliders and spinners were")} skipped (osu!mania has none of its own).");
            if (c.BadLines > 0) items.Add($"{name}: {Count(c.BadLines, "line", "lines")} couldn't be read and {(c.BadLines == 1 ? "was" : "were")} skipped.");
            if (c.PastLimit > 0) items.Add($"{name}: {Count(c.PastLimit, "line", "lines")} past the limit {(c.PastLimit == 1 ? "was" : "were")} left out.");
        }
        var timing = group.Timing;
        if (timing.Fillers > 0)
            items.Add(timing.Fillers == 1
                ? "1 tempo line wasn't on a beat, so up to a beat before it is stretched to reach it."
                : $"{N(timing.Fillers)} tempo lines weren't on a beat, so up to a beat before each is stretched to reach it.");
        if (timing.RowSnaps > 0)
            items.Add(timing.RowSnaps == 1
                ? "1 tempo line starts on the nearest 1/48 beat instead of a whole beat."
                : $"{N(timing.RowSnaps)} tempo lines start on the nearest 1/48 beat instead of a whole beat.");
        if (timing.Effects > 0)
            items.Add(timing.Effects == 1
                ? "1 osu! tempo effect (under 40 or over 1000 BPM) became a speed change."
                : $"{N(timing.Effects)} osu! tempo effects (under 40 or over 1000 BPM) became speed changes.");
        if (timing.Merged > 0)
            items.Add(timing.Merged == 1
                ? "1 tempo section shorter than a 1/48 beat was merged into the one before."
                : $"{N(timing.Merged)} tempo sections shorter than a 1/48 beat were merged into the ones before.");
        var speeds = choices.SpeedsFrom != null && group.Speeds.TryGetValue(choices.SpeedsFrom, out var chosen) ? chosen : group.BaseScrolls;
        if (speeds.OutsideOsu > 0)
            items.Add($"{Count(speeds.OutsideOsu, "green line is", "green lines are")} outside osu!'s own x0.01 to x10 and {(speeds.OutsideOsu == 1 ? "counts" : "count")} as those limits, as in osu!.");
        if (speeds.Clamped > 0)
            items.Add($"{Count(speeds.Clamped, "speed change is", "speed changes are")} outside the game's x0.05 to x20 and {(speeds.Clamped == 1 ? "was" : "were")} kept at those limits.");
        if (speeds.CappedAtSeconds is double capped)
            items.Add($"Only the first {N(MaxScrolls)} speed changes (up to {Clock(capped)}) are kept.");
        // Other difficulties' speed changes, which the battle can't have as well.
        var shown = new HashSet<string>();
        if (choices.SpeedsFrom != null) shown.Add(speeds.Tag);
        foreach (var d in included)
        {
            if (!group.HasSpeedChanges(d) || !group.Speeds.TryGetValue(d, out var own) || !shown.Add(own.Tag)) continue;
            items.Add(choices.SpeedsFrom == null
                ? $"The speed changes of {Quote(d.Name)} ({N(own.Own.Count)}): a battle has one set for every difficulty, and this one uses none."
                : $"The speed changes of {Quote(d.Name)} ({N(own.Own.Count)}): a battle has one set for every difficulty, and it uses {Quote(choices.SpeedsFrom.Name)}'s.");
        }
        if (plan.Song != null && Math.Abs(plan.Song.Shift) >= 0.00005)
        {
            string ms = Ms(Math.Abs(plan.Song.Shift) * 1000);
            items.Add(plan.Song.Shift > 0
                ? $"osu! starts this MP3 {ms} ms later than the game does, so every note was moved {ms} ms earlier."
                : $"osu! starts this MP3 {ms} ms earlier than the game does, so every note was moved {ms} ms later.");
        }
        return items;
    }

    internal static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    internal static string Count(long n, string one, string many) => n == 1 ? "1 " + one : N(n) + " " + many;

    internal static string Ms(double ms) => ms.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>m:ss.</summary>
    internal static string Clock(double seconds)
    {
        int whole = (int)Math.Max(0, Math.Floor(seconds));
        return $"{whole / 60}:{(whole % 60).ToString("00", CultureInfo.InvariantCulture)}";
    }

    /// <summary>A name from the beatmap in quotes, cut short when it's long.</summary>
    internal static string Quote(string name) => "\"" + OszImport.Shorten(name, 60) + "\"";
}
