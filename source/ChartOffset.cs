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
/// The chart's first row at or after 0:00 becomes row 0. When 0:00 falls between two rows, row 0
/// waits (a stop) for the time from 0:00 to that row, and the tempo stays the chart's own, so the
/// notes scroll in at the chart's speed and Speed Mod's top tempo never rises. Notes before 0:00
/// (a positive #OFFSET puts beat 0 before the song starts) can't be played and are left out.
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
        /// <summary>
        /// The stop at row 0 when the bake makes one: the time from 0:00 to the chart's first row
        /// (with any stop that row has), or what's left after 0:00 of a stop the song starts in. 0 for none.
        /// </summary>
        internal double StartStop;
        /// <summary>
        /// The most any note that plays is off the time StepMania gives it, in seconds: 0 but for a
        /// note less than a row after 0:00 (it plays at 0:00, or every note plays as much later as
        /// the row before is from 0:00, whichever is less) or less than <see cref="EarlyTolerance"/> before it.
        /// </summary>
        internal double Error;
        /// <summary>Notes left out because they come before the song starts (the most any one difficulty lost).</summary>
        internal int Dropped;

        internal bool Changed => Offset != 0;

        /// <summary>For the log.</summary>
        internal string Describe()
        {
            if (!Changed) return "#OFFSET 0";
            var sb = new StringBuilder($"#OFFSET {Offset.ToString("0.####", CultureInfo.InvariantCulture)}: notes {Math.Abs(Shift)} rows {(Shift >= 0 ? "later" : "earlier")}");
            if (StartStop > 0) sb.Append($", a {(StartStop * 1000).ToString("0.###", CultureInfo.InvariantCulture)} ms stop at the start");
            if (Error >= 5e-7) sb.Append($", within {(Error * 1000).ToString("0.###", CultureInfo.InvariantCulture)} ms");
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

    /// <summary>
    /// A note this little before the song's start still plays, at 0:00, and nothing calls it early;
    /// one further before it is left out.
    /// </summary>
    internal const double EarlyTolerance = 0.0005;

    /// <summary>The chart's #OFFSET, as the battle loader reads it (0 when it isn't a number).</summary>
    internal static double OffsetOf(ChartText chart) =>
        double.TryParse(chart.GetTag("OFFSET"), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : 0;

    /// <summary>What the chart editor, the battle creator and the arcade say about <see cref="Result.Dropped"/>.</summary>
    internal static string DroppedText(int dropped) => dropped == 1
        ? "a note before the song starts (0:00) can't be played, so it's left out"
        : $"{dropped} notes before the song starts (0:00) can't be played, so they're left out";

    /// <summary>
    /// The chart the game plays for a song file whose first sample is at clock 0: with #OFFSET
    /// baked in, or the chart itself when #OFFSET is 0 (or out of range, over
    /// <see cref="LeadIn.MaxOffset"/> either way, which the battle loader refuses). Note times,
    /// with beat 0 at clock 0, are the times StepMania gives the chart, exactly but for
    /// <see cref="Result.Error"/>.
    /// </summary>
    internal static Result Bake(ChartText chart)
    {
        double offset = OffsetOf(chart);
        if (offset == 0 || Math.Abs(offset) > LeadIn.MaxOffset) return new Result { Chart = chart };

        var timing = new EditorChart(4);
        timing.ReadTiming(chart);
        var result = new Result { Offset = offset };

        // c: the first row at the song's start or after it (or a hair before it), and how far after
        // the start it is. In a stop there: the rows after the stop.
        int c = (int)Math.Ceiling(timing.SecondsToRow(-EarlyTolerance) - Tiny);
        while (timing.RowToSeconds(c) < -EarlyTolerance) c++;
        while (timing.RowToSeconds(c - 1) >= -EarlyTolerance) c--;
        double atC = timing.RowToSeconds(c), late = Math.Max(0, atC);
        double before = timing.RowToSeconds(c - 1), stopBefore = StopOn(timing, c - 1);

        int zero = c;      // the authored row that becomes row 0
        int keepFrom = 0;  // rows before this one (in the new chart) are before the song
        if (late <= Tiny) result.Error = Math.Max(0, -atC);
        else if (stopBefore > 0 && before + stopBefore > Tiny)
        {
            // The song starts inside a stop on the row before: the chart starts on that row, in what's
            // left of the stop. The row's own notes come before the stop, so before the song.
            zero = c - 1;
            keepFrom = 1;
            result.StartStop = before + stopBefore;
        }
        else if (!HasNoteOn(chart, c) || late <= -before)
        {
            // Row c is `late` seconds after the start (less than a row): row 0 waits that long, as a
            // stop, then the chart goes on at its own tempo. Exact, but for notes on row c itself:
            // they play at 0:00, `late` early (the row before would put every note later than that).
            result.StartStop = late + StopOn(timing, c);
            if (HasNoteOn(chart, c)) result.Error = late;
        }
        else
        {
            // Row c has notes, and the row before is nearer to 0:00: that one is row 0, and every
            // note plays that little late.
            zero = c - 1;
            result.Error = -before;
        }
        result.Shift = -zero;

        var baked = new ChartText();
        foreach (var tag in chart.Tags)
        {
            string key = tag.Key.ToUpperInvariant();
            string value = key switch
            {
                "OFFSET" => "0",
                "BPMS" => Bpms(timing, zero),
                "STOPS" => Stops(timing, zero, result.StartStop),
                "SCROLLS" => Scrolls(tag.Value, zero),
                _ => Array.IndexOf(BeatTags, key) >= 0 ? MovePairs(tag.Value, zero) : tag.Value
            };
            baked.Tags.Add(new KeyValuePair<string, string>(tag.Key, value));
        }
        // A chart without the tag had its tempo from the editor's default; the game needs one.
        if (chart.GetTag("BPMS") == null) baked.Tags.Add(new KeyValuePair<string, string>("BPMS", Bpms(timing, zero)));
        if (result.StartStop > 0 && chart.GetTag("STOPS") == null)
            baked.Tags.Add(new KeyValuePair<string, string>("STOPS", Stops(timing, zero, result.StartStop)));

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

    // The seconds of the stops on a row (0 for none).
    private static double StopOn(EditorChart timing, int row) =>
        timing.Stops.Where(s => Math.Abs(s.Beat * RowsPerBeat - row) < Tiny).Sum(s => s.Seconds);

    // Whether any block has a note to hit on a row: not a mine, and not the end of a hold (a hold
    // that ends on the song's first row started before the song, so it is left out).
    private static bool HasNoteOn(ChartText chart, int row)
    {
        if (row < 0) return false;
        foreach (var block in chart.Blocks)
        {
            var chunks = block.Notes.Split(',');
            int measure = row / RowsPerMeasure;
            if (measure >= chunks.Length) continue;
            var lines = Lines(chunks[measure]);
            for (int i = 0; i < lines.Count; i++)
                if (RowOf(measure, i, lines.Count) == row && lines[i].Any(ch => ch != '0' && ch != '3' && ch != 'M')) return true;
        }
        return false;
    }

    // ---- timing tags ------------------------------------------------------------------------------

    // The tempo from row 0: the authored tempo at `zero`, then the changes after it.
    private static string Bpms(EditorChart timing, int zero)
    {
        var list = new List<(double Beat, double Bpm)>();
        if (!timing.Bpms.Any(b => Math.Abs(b.Beat * RowsPerBeat - zero) < Tiny))
            list.Add((0, timing.BpmAt(zero / (double)RowsPerBeat)));
        foreach (var (beat, bpm) in timing.Bpms)
            if (beat * RowsPerBeat >= zero - Tiny && (list.Count == 0 || list[^1].Bpm != bpm)) list.Add((Moved(beat, zero), bpm));
        return string.Join(",", list.Select(b => $"{Number(b.Beat)}={Number(b.Bpm)}"));
    }

    // Stops before row 0 are over before the song starts; their time is already in the shift. Row 0
    // may wait (startStop): that replaces a stop the chart has there.
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
    /// <summary>
    /// A moment more than the notes need, so the first one appears at the far end of the lane. It's
    /// long enough for a long frame as the notes start (the battle's first frames can be slow, and
    /// until the song starts the clock moves by the whole frame): up to this much, the first note
    /// still comes in from the far end.
    /// </summary>
    internal const double LeadInMargin = 0.75;
    /// <summary>When the note speed can't be read: about when the game's own charts start.</summary>
    internal const double DefaultApproach = 2;

    /// <summary>The first note that plays: its row and its time on the battle's clock.</summary>
    internal readonly record struct FirstNote(int Row, double Seconds);

    /// <summary>
    /// The timing of a chart as the game plays it in a battle: beat 0 at clock 0, whatever #OFFSET
    /// says (a baked chart's is 0), and before beat 0 the clock runs at the first tempo.
    /// </summary>
    private static EditorChart GameTiming(ChartText chart)
    {
        var timing = new EditorChart(4);
        timing.ReadTiming(chart);
        timing.Offset = 0;
        return timing;
    }

    /// <summary>
    /// The first note (not the end of a hold) of the blocks, at or after the song's start, in
    /// seconds on the battle's clock (the song file's time) for <paramref name="chart"/>, the chart
    /// the game plays. Null when there's none.
    /// </summary>
    internal static FirstNote? FirstNoteOf(ChartText chart, IEnumerable<ChartText.NoteBlock?> blocks)
    {
        var timing = GameTiming(chart);
        int? first = null;
        foreach (var block in blocks.Distinct())
        {
            if (block == null) continue;
            foreach (int row in NoteRows(block, heads: true))
            {
                if (first != null && row >= first) break;
                if (timing.RowToSeconds(row) < -EarlyTolerance) continue;
                first = row;
                break;
            }
        }
        if (first is not int r) return null;
        return new FirstNote(r, Math.Max(0, timing.RowToSeconds(r)));
    }

    /// <summary>The chart's top tempo, which Speed Mod divides by.</summary>
    internal static double TopBpm(ChartText chart)
    {
        var timing = new EditorChart(4);
        timing.ReadTiming(chart);
        return timing.Bpms.Max(b => b.Bpm);
    }

    /// <summary>
    /// When the first note comes into view at the far end of the lane, on the battle's clock for
    /// <paramref name="chart"/>, the chart the game plays (negative before the song): when the
    /// notes have <paramref name="windowBeats"/> beats of scrolling left to it, with #SCROLLS
    /// stretching the beats as the scroll speed hooks do, and the tempo and stops of the chart
    /// between (before beat 0 the clock runs at the first tempo, as the game's does). A window that
    /// isn't known (NaN) takes <see cref="DefaultApproach"/>.
    /// </summary>
    internal static double EntersView(ChartText chart, FirstNote first, double windowBeats)
    {
        if (!(windowBeats > 0) || double.IsInfinity(windowBeats)) return first.Seconds - DefaultApproach;
        var scrolls = ScrollSpeeds.Parse(chart.GetTag("SCROLLS"));
        double beat = first.Row / (double)RowsPerBeat;
        double enters = ScrollSpeeds.Undisplayed(scrolls, ScrollSpeeds.Displayed(scrolls, beat) - windowBeats);
        return GameTiming(chart).BeatToSeconds(enters);
    }

    /// <summary>
    /// How long before the song the battle's clock starts (the game's startDelay), so that the first
    /// note comes into view a moment after the notes start: none when it comes into view late enough.
    /// </summary>
    internal static double StartDelay(double entersView) => Math.Clamp(LeadInMargin - entersView, 0, MaxLeadIn);

    // ---- events -----------------------------------------------------------------------------------

    /// <summary>
    /// #ATTACKS for a battle whose clock starts <paramref name="leadIn"/> s before the song. The
    /// game fires an event once the clock reaches its time, so events at 0:00 used to fire on the
    /// battle's first frame; with a lead-in they would fire as the song starts, with the notes
    /// already on their way. So the mods at 0:00 or before it that set how the battle looks (lane
    /// layouts, the camera, props: <see cref="TestChart.StateKey"/>) move to the clock start, each as
    /// an event of its own that ends when its event did. Other mods (attacks, text) keep their
    /// times. The game's reader takes a negative TIME (float.TryParse with NumberStyles.Any). Null
    /// when nothing moves; <paramref name="moved"/> counts the mods moved.
    /// </summary>
    internal static string? EventsBeforeSong(string? attacks, double leadIn, out int moved)
    {
        moved = 0;
        if (!(leadIn > 0) || string.IsNullOrWhiteSpace(attacks)) return null;
        // The clock start, in whole milliseconds and never after it.
        double start = -Math.Ceiling(leadIn * 1000 - Tiny) / 1000;

        // The events as the game reads them: ':' between values, each event from its TIME on.
        var head = new List<string>();
        var events = new List<List<string>>();
        foreach (var part in attacks!.Split(':'))
        {
            if (Key(part) == "TIME") events.Add(new List<string> { part });
            else if (events.Count > 0) events[^1].Add(part);
            else head.Add(part);
        }

        var early = new List<string>();
        var rest = new List<string>();
        foreach (var e in events)
        {
            double time = Value(e[0]) ?? double.NaN;
            int mods = e.FindIndex(p => Key(p) == "MODS");
            if (!(time <= 0) || mods < 0 || e.Count(p => Key(p) == "MODS") > 1)
            {
                rest.AddRange(e);
                continue;
            }
            double length = e.Where(p => Key(p) == "LEN").Select(Value).LastOrDefault() ?? 0;
            string text = e[mods].Substring(e[mods].IndexOf('=') + 1);
            var all = text.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
            var state = all.Where(m => TestChart.StateKey(m) != null).ToList();
            if (state.Count == 0)
            {
                rest.AddRange(e);
                continue;
            }
            double end = Math.Max(0, Math.Round(time + Math.Max(0, length) - start, 6));
            foreach (var mod in state)
                early.Add($"TIME={Number(start)}:LEN={Number(end)}:MODS={mod}");
            moved += state.Count;
            var others = all.Where(m => TestChart.StateKey(m) == null).ToList();
            if (others.Count == 0) continue;
            e[mods] = e[mods].Substring(0, e[mods].IndexOf('=') + 1) + string.Join(",", others);
            rest.AddRange(e);
        }
        if (moved == 0) return null;
        return string.Join(":", head.Concat(early).Concat(rest));

        static string Key(string part)
        {
            int eq = part.IndexOf('=');
            return eq < 0 ? "" : part.Substring(0, eq).Trim().ToUpperInvariant();
        }

        static double? Value(string part)
        {
            int eq = part.IndexOf('=');
            return eq >= 0 && double.TryParse(part.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
        }
    }
}
