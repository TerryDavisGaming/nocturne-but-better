using System.Globalization;
using System.Text;

namespace NocturneFlatScroll;

/// <summary>
/// Puts a chart's beat 0 where StepMania puts it (song time -#OFFSET) in the chart text the game
/// plays. The game ignores #OFFSET in battles: WwiseConductor.InitializeSong zeroes the offset its
/// chart reader parsed, so beat 0 is at clock 0. For a battle whose music is a song file (a custom
/// battle, or a custom chart with #MUSIC) the clock is the file's own time, so a chart made the
/// StepMania way (the chart editor, the battle creator's "first beat here", the osu!mania import)
/// would play #OFFSET seconds off. Baking moves every note and timing change so that beat 0 of
/// the new chart is at the file's 0:00, and writes #OFFSET:0. The clock stays the file's time, so
/// #ATTACKS times, dialogue times and a test's clock start stay as they are.
/// A negative #OFFSET (beat 0 inside the song, the usual case) gets a lead-in section before the
/// chart's first tempo, no faster than it, so Speed Mod's top tempo never rises. A positive one
/// (beat 0 before the song starts) drops the notes before 0:00, which can't be played.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class ChartOffset
{
    internal sealed class Result
    {
        /// <summary>The chart the game plays: the chart itself (the same object) when #OFFSET is 0.</summary>
        internal ChartText Chart = null!;
        /// <summary>The #OFFSET that was baked in, in seconds.</summary>
        internal double Offset;
        /// <summary>How many rows later every note is in <see cref="Chart"/> (earlier when negative).</summary>
        internal int Shift;
        /// <summary>Rows at the start that play at <see cref="LeadBpm"/> before the chart's own tempo; 0 for none.</summary>
        internal int LeadRows;
        internal double LeadBpm;
        /// <summary>What's left after 0:00 of a stop the song starts in, now at row 0; 0 for none.</summary>
        internal double StartStop;
        /// <summary>Notes left out because they come before the song starts (the most any one difficulty lost).</summary>
        internal int Dropped;

        internal bool Changed => Offset != 0;

        /// <summary>For the log.</summary>
        internal string Describe()
        {
            if (!Changed) return "#OFFSET 0";
            var sb = new StringBuilder($"#OFFSET {Offset.ToString("0.####", CultureInfo.InvariantCulture)}: notes {Math.Abs(Shift)} rows {(Shift >= 0 ? "later" : "earlier")}");
            if (LeadRows > 0) sb.Append($", {LeadRows} rows at {LeadBpm.ToString("0.###", CultureInfo.InvariantCulture)} BPM first");
            if (StartStop > 0) sb.Append($", starting {StartStop.ToString("0.###", CultureInfo.InvariantCulture)} s before the end of a stop");
            if (Dropped > 0) sb.Append($", {Dropped} notes before 0:00 left out");
            return sb.ToString();
        }
    }

    // Tags keyed by beat ("beat=..."): they move with the notes. #BPMS, #STOPS and #SCROLLS are
    // written on their own; the game's .sm reader ignores the rest, but they move all the same.
    private static readonly string[] BeatTags = { "FREEZES", "DELAYS", "WARPS", "SPEEDS", "TIMESIGNATURES", "TICKCOUNTS", "COMBOS", "FAKES" };

    private const int RowsPerBeat = EditorChart.RowsPerBeat;
    private const int RowsPerMeasure = EditorChart.RowsPerMeasure;
    private const double Tiny = 1e-6;

    /// <summary>The chart's #OFFSET, as the battle loader reads it (0 when it isn't a number).</summary>
    internal static double OffsetOf(ChartText chart) =>
        double.TryParse(chart.GetTag("OFFSET"), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : 0;

    /// <summary>
    /// The chart the game plays for a song file whose first sample is at clock 0: with #OFFSET
    /// baked in, or the chart itself when #OFFSET is 0 (or out of range, over
    /// <see cref="LeadIn.MaxOffset"/> either way, which the battle loader refuses). Note times,
    /// with beat 0 at clock 0, are the times StepMania gives the chart, to within half a row in
    /// the few cases a lead-in section can't be fitted (a note or a stop less than a row after the
    /// song's start, with nothing between); exactly otherwise.
    /// </summary>
    internal static Result Bake(ChartText chart)
    {
        double offset = OffsetOf(chart);
        if (offset == 0 || Math.Abs(offset) > LeadIn.MaxOffset) return new Result { Chart = chart };

        var timing = new EditorChart(4);
        timing.ReadTiming(chart);
        var result = new Result { Offset = offset };

        // The first row at or after the song's start (in a stop there: the rows after it), and how
        // far after the start it is.
        double atStart = timing.SecondsToRow(0);
        int c = (int)Math.Ceiling(atStart - Tiny);
        while (timing.RowToSeconds(c) < -Tiny) c++;
        double late = Math.Max(0, timing.RowToSeconds(c));

        int zero = c;      // the authored row that becomes row 0
        int keepFrom = 0;  // rows before this one (in the new chart) are before the song
        double before = timing.RowToSeconds(c - 1);
        double stopBefore = timing.Stops.Where(s => Math.Abs(s.Beat * RowsPerBeat - (c - 1)) < Tiny).Sum(s => s.Seconds);
        if (late <= Tiny) { }
        else if (stopBefore > 0 && before < -Tiny && before + stopBefore > Tiny)
        {
            // The song starts inside a stop on the row before: the chart starts on that row, in what's
            // left of the stop. The row's own notes come before the stop, so before the song.
            zero = c - 1;
            keepFrom = 1;
            result.StartStop = before + stopBefore;
        }
        else
        {
            // Row c is `late` seconds after the start. A lead-in section from row 0 to the next
            // note, tempo change or stop (whichever is first) takes up that time, at a tempo just
            // below the one there. With nothing in between, row 0 is c or the row before it,
            // whichever is nearer in time.
            double end = double.MaxValue;
            foreach (var block in chart.Blocks)
                foreach (int row in NoteRows(block, heads: false))
                    if (row >= c) { end = Math.Min(end, row); break; }
            foreach (var (beat, _) in timing.Bpms)
                if (beat * RowsPerBeat > c + Tiny) { end = Math.Min(end, beat * RowsPerBeat); break; }
            foreach (var (beat, _) in timing.Stops)
                if (beat * RowsPerBeat >= c - Tiny) end = Math.Min(end, beat * RowsPerBeat);
            if (end == double.MaxValue) end = c + RowsPerBeat;
            int lead = (int)Math.Floor(end + Tiny) - c;
            if (lead >= 1)
            {
                result.LeadRows = lead;
                result.LeadBpm = 60.0 * lead / RowsPerBeat / timing.RowToSeconds(c + lead);
            }
            else if (-before < late) zero = c - 1;
        }
        result.Shift = -zero;

        var baked = new ChartText();
        foreach (var tag in chart.Tags)
        {
            string key = tag.Key.ToUpperInvariant();
            string value = key switch
            {
                "OFFSET" => "0",
                "BPMS" => Bpms(timing, zero, result),
                "STOPS" => Stops(timing, zero, result.StartStop),
                "SCROLLS" => Scrolls(tag.Value, zero),
                _ => Array.IndexOf(BeatTags, key) >= 0 ? MovePairs(tag.Value, zero) : tag.Value
            };
            baked.Tags.Add(new KeyValuePair<string, string>(tag.Key, value));
        }
        // A chart without the tag had its tempo from the editor's default; the game needs one.
        if (chart.GetTag("BPMS") == null) baked.Tags.Add(new KeyValuePair<string, string>("BPMS", Bpms(timing, zero, result)));

        // The six difficulty slots often share a block: each note text is moved once.
        var moved = new Dictionary<string, (string Notes, int Dropped)>();
        foreach (var block in chart.Blocks)
        {
            if (!moved.TryGetValue(block.Notes, out var notes))
            {
                int dropped = 0;
                notes = (MoveNotes(block, result.Shift, keepFrom, ref dropped), dropped);
                moved[block.Notes] = notes;
                result.Dropped = Math.Max(result.Dropped, dropped);
            }
            baked.Blocks.Add(new ChartText.NoteBlock
            {
                StepsType = block.StepsType, Description = block.Description, Difficulty = block.Difficulty,
                Meter = block.Meter, Radar = block.Radar, Notes = notes.Notes
            });
        }
        result.Chart = baked;
        return result;
    }

    // ---- timing tags ------------------------------------------------------------------------------

    // The tempo from row 0: the lead-in section, then the authored tempo from `zero` on.
    private static string Bpms(EditorChart timing, int zero, Result result)
    {
        var list = new List<(double Beat, double Bpm)>();
        int from = zero + result.LeadRows;   // the authored row where the authored tempo takes over
        if (result.LeadRows > 0) list.Add((0, result.LeadBpm));
        if (!timing.Bpms.Any(b => Math.Abs(b.Beat * RowsPerBeat - from) < Tiny))
            list.Add((result.LeadRows / (double)RowsPerBeat, timing.BpmAt(from / (double)RowsPerBeat)));
        foreach (var (beat, bpm) in timing.Bpms)
            if (beat * RowsPerBeat >= from - Tiny) list.Add((Moved(beat, zero), bpm));
        return string.Join(",", list.Select(b => $"{Number(b.Beat)}={Number(b.Bpm)}"));
    }

    // Stops before row 0 are over before the song starts; their time is already in the shift. The
    // song can start inside the one on row 0: what's left of it stays.
    private static string Stops(EditorChart timing, int zero, double startStop)
    {
        var list = new List<string>();
        if (startStop > 0) list.Add("0=" + Number(startStop));
        foreach (var (beat, seconds) in timing.Stops)
        {
            double row = beat * RowsPerBeat;
            if (row < zero - Tiny || (startStop > 0 && Math.Abs(row - zero) < Tiny)) continue;
            list.Add($"{Number(Moved(beat, zero))}={Number(seconds)}");
        }
        return string.Join(",", list);
    }

    /// <summary>
    /// A beat moved so that authored row <paramref name="zero"/> is beat 0. A beat on the row grid
    /// stays exactly on it (the same number as row / 48), so a stop or tempo change on a note's row
    /// is still on that row when the mod's own timing reads the chart back.
    /// </summary>
    private static double Moved(double beat, int zero)
    {
        double row = beat * RowsPerBeat, whole = Math.Round(row);
        return Math.Abs(row - whole) < Tiny ? (whole - zero) / RowsPerBeat : beat - zero / (double)RowsPerBeat;
    }

    /// <summary>
    /// #SCROLLS read the way the scroll speed hooks read it (a beat before 0 counts as 0; before the
    /// first change the ratio is 1, or the one a change at beat 0 sets), moved so every row keeps its
    /// ratio: row 0 gets the ratio its authored row had.
    /// </summary>
    private static string Scrolls(string value, int zero)
    {
        var pairs = Pairs(value)
            .Where(p => double.TryParse(p.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(p => (Beat: Math.Max(0, p.Beat), p.Value)).OrderBy(p => p.Beat).ToList();
        if (pairs.Count == 0) return value;
        string atZero = "1";
        foreach (var (beat, ratio) in pairs)
            if (beat * RowsPerBeat <= Math.Max(zero, 0) + Tiny) atZero = ratio;
        var list = new List<string> { "0=" + atZero };
        foreach (var (beat, ratio) in pairs)
            if (beat * RowsPerBeat > zero + Tiny) list.Add($"{Number(Moved(beat, zero))}={ratio}");
        return string.Join(",", list);
    }

    // Other beat=value tags: moved, and those before row 0 left out.
    private static string MovePairs(string value, int zero) =>
        string.Join(",", Pairs(value)
            .Where(p => p.Beat * RowsPerBeat >= zero - Tiny)
            .Select(p => $"{Number(Moved(p.Beat, zero))}={p.Value}"));

    private static List<(double Beat, string Value)> Pairs(string? text)
    {
        var list = new List<(double, string)>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var part in text!.Split(','))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) continue;
            if (!double.TryParse(part.Substring(0, eq).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double beat) || !double.IsFinite(beat)) continue;
            list.Add((beat, part.Substring(eq + 1).Trim()));
        }
        return list;
    }

    // Beats, tempos and seconds for the game's reader: invariant, the shortest text that reads back
    // as the same number, never with an exponent or as "-0".
    private static string Number(double value)
    {
        if (Math.Abs(value) < 5e-13) return "0";
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('E') ? value.ToString("0.###############", CultureInfo.InvariantCulture) : text;
    }

    // ---- notes ------------------------------------------------------------------------------------

    /// <summary>
    /// Rows with a note (any character but '0') in a block, in order, on the game's grid. With
    /// <paramref name="heads"/>, a row with only the ends of holds doesn't count.
    /// </summary>
    private static IEnumerable<int> NoteRows(ChartText.NoteBlock block, bool heads)
    {
        int measure = 0;
        foreach (var chunk in block.Notes.Split(','))
        {
            var lines = Lines(chunk);
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Any(ch => ch != '0' && (!heads || ch != '3'))) yield return RowOf(measure, i, lines.Count);
            measure++;
        }
    }

    private static List<string> Lines(string measure)
    {
        var lines = new List<string>();
        foreach (var raw in measure.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    // As ChartText.LastNoteRow and the game's reader place a line.
    private static int RowOf(int measure, int line, int lines) =>
        measure * RowsPerMeasure + (int)Math.Round(line * (double)RowsPerMeasure / lines);

    /// <summary>
    /// A block's #NOTES text with every row moved by <paramref name="shift"/>, each line kept as it
    /// is, each measure written at the fewest lines that keep its rows. Rows before
    /// <paramref name="keepFrom"/> are left out, with the ends of holds that started there.
    /// </summary>
    private static string MoveNotes(ChartText.NoteBlock block, int shift, int keepFrom, ref int dropped)
    {
        var rows = new SortedDictionary<int, char[]>();
        int width = block.Lanes;
        var cut = new HashSet<int>();   // lanes whose hold started before row 0
        int measure = 0;
        foreach (var chunk in block.Notes.Split(','))
        {
            var lines = Lines(chunk);
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (width <= 0) width = line.Length;
                if (line.All(ch => ch == '0')) continue;
                int row = RowOf(measure, i, lines.Count) + shift;
                if (row < keepFrom)
                {
                    for (int lane = 0; lane < line.Length; lane++)
                    {
                        char ch = line[lane];
                        if (ch is '2' or '4') cut.Add(lane);
                        else if (ch == '3') cut.Remove(lane);
                        if (ch != '0' && ch != '3') dropped++;
                    }
                    continue;
                }
                var chars = line.ToCharArray();
                for (int lane = 0; lane < chars.Length; lane++)
                {
                    if (chars[lane] == '0' || !cut.Remove(lane)) continue;
                    if (chars[lane] == '3') chars[lane] = '0';
                }
                if (chars.All(ch => ch == '0')) continue;
                if (rows.TryGetValue(row, out var have))
                {
                    // Two lines on one row (a measure finer than the grid): each lane keeps its first note.
                    for (int lane = 0; lane < Math.Min(have.Length, chars.Length); lane++)
                        if (have[lane] == '0') have[lane] = chars[lane];
                }
                else rows[row] = chars;
            }
            measure++;
        }

        if (width <= 0) width = 4;
        string empty = new string('0', width);
        var keys = rows.Keys.ToList();
        int measures = keys.Count == 0 ? 1 : keys[^1] / RowsPerMeasure + 1;
        int[] divisions = { 4, 8, 12, 16, 24, 32, 48, 64, 96, 192 };
        var sb = new StringBuilder();
        int next = 0;
        for (int m = 0; m < measures; m++)
        {
            int start = m * RowsPerMeasure, first = next;
            while (next < keys.Count && keys[next] < start + RowsPerMeasure) next++;
            int count = divisions.First(d => Enumerable.Range(first, next - first).All(k => (keys[k] - start) % (RowsPerMeasure / d) == 0));
            int step = RowsPerMeasure / count;
            for (int i = 0; i < count; i++)
                sb.Append(rows.TryGetValue(start + i * step, out var line) ? new string(line) : empty).Append('\n');
            if (m < measures - 1) sb.Append(",\n");
        }
        return sb.ToString();
    }

    // ---- the battle's start -----------------------------------------------------------------------

    /// <summary>How far the notes scroll in before the first one when it comes too soon, at most.</summary>
    internal const double MaxLeadIn = 3;
    /// <summary>A moment more than the notes need, so the first one appears at the far end of the lane.</summary>
    internal const double LeadInMargin = 0.25;
    /// <summary>When the note speed can't be read: about when the game's own charts start.</summary>
    internal const double DefaultApproach = 2;

    /// <summary>The first note that plays, where it is and how fast notes move there.</summary>
    internal readonly record struct FirstNote(int Row, double Seconds, double Bpm, double Scroll);

    /// <summary>
    /// The first note (not the end of a hold) of the blocks at or after the song's start, in
    /// seconds on the song file's clock (StepMania's rule, beat 0 at -#OFFSET, which is the game's
    /// for a baked chart), with the tempo and scroll ratio there. Null when there's none.
    /// </summary>
    internal static FirstNote? FirstNoteOf(ChartText chart, IEnumerable<ChartText.NoteBlock?> blocks)
    {
        var timing = new EditorChart(4);
        timing.ReadTiming(chart);
        int? first = null;
        foreach (var block in blocks.Distinct())
        {
            if (block == null) continue;
            foreach (int row in NoteRows(block, heads: true))
            {
                if (first != null && row >= first) break;
                if (timing.RowToSeconds(row) < -0.0005) continue;
                first = row;
                break;
            }
        }
        if (first is not int r) return null;
        double beat = r / (double)RowsPerBeat, scroll = 1;
        foreach (var (b, ratio) in ScrollSpeeds.Parse(chart.GetTag("SCROLLS")))
            if (b <= beat + 1e-9) scroll = ratio;
        return new FirstNote(r, Math.Max(0, timing.RowToSeconds(r)), timing.BpmAt(beat + 1e-9), scroll);
    }

    /// <summary>The chart's top tempo, which Speed Mod divides by.</summary>
    internal static double TopBpm(ChartText chart)
    {
        var timing = new EditorChart(4);
        timing.ReadTiming(chart);
        return timing.Bpms.Max(b => b.Bpm);
    }

    /// <summary>
    /// How long a note takes to cross the note field's spawn window (<paramref name="window"/>
    /// units, the game's Size / 2 + Offset) at <paramref name="unitsPerBeat"/>, the tempo and the
    /// scroll ratio: when it appears at the far end, how long before it's hit.
    /// </summary>
    internal static double Approach(double window, double unitsPerBeat, double bpm, double scroll)
    {
        double seconds = window / (unitsPerBeat * bpm / 60 * scroll);
        return double.IsFinite(seconds) && seconds > 0 ? seconds : DefaultApproach;
    }

    /// <summary>
    /// How long before the song the battle's clock starts (the game's startDelay), so that the first
    /// note scrolls in from the far end of the lane: none when it comes late enough.
    /// </summary>
    internal static double StartDelay(double approach, double firstNoteSeconds) =>
        Math.Clamp(approach + LeadInMargin - firstNoteSeconds, 0, MaxLeadIn);
}
