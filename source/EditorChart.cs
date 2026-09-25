using System.Globalization;
using System.Text;

namespace NocturneFlatScroll;

/// <summary>
/// The chart being edited: notes on a StepMania row grid (48 rows a beat, 192 a measure) and the
/// song's timing (#OFFSET, #BPMS, #STOPS) to turn rows into seconds and back. It reads and writes
/// the #NOTES text of one difficulty. No Unity or game dependencies.
/// </summary>
internal sealed class EditorChart
{
    internal const int RowsPerBeat = 48;
    internal const int RowsPerMeasure = RowsPerBeat * 4;

    internal struct Note
    {
        internal int Row;
        internal int Lane;
        // StepMania note characters: '1' tap, '2' hold, '4' roll, 'M' mine, and the game's own
        // 'L', 'F' and 'K', kept as they are. Holds and rolls end at EndRow.
        internal char Type;
        internal int EndRow;

        internal bool IsLong => Type is '2' or '4';
        internal int LastRow => IsLong ? EndRow : Row;
    }

    internal readonly int Lanes;
    internal List<Note> Notes = new();
    internal double Offset;
    internal readonly List<(double Beat, double Bpm)> Bpms = new();
    internal readonly List<(double Beat, double Seconds)> Stops = new();

    internal EditorChart(int lanes) => Lanes = lanes;

    // ---- timing -----------------------------------------------------------------------------

    internal void ReadTiming(ChartText chart)
    {
        Offset = ParseDouble(chart.GetTag("OFFSET") ?? "0");
        Bpms.Clear();
        foreach (var (beat, value) in ParsePairs(chart.GetTag("BPMS")))
            if (value > 0) Bpms.Add((beat, value));
        if (Bpms.Count == 0) Bpms.Add((0, 120));
        Bpms.Sort((a, b) => a.Beat.CompareTo(b.Beat));
        if (Bpms[0].Beat > 0) Bpms.Insert(0, (0, Bpms[0].Bpm));
        Stops.Clear();
        foreach (var (beat, value) in ParsePairs(chart.GetTag("STOPS")))
            if (value != 0) Stops.Add((beat, value));
        Stops.Sort((a, b) => a.Beat.CompareTo(b.Beat));
    }

    /// <summary>Seconds on the chart's clock (the song's audio time) for a beat, like StepMania: beat 0 is at -#OFFSET.</summary>
    internal double BeatToSeconds(double beat)
    {
        double t = -Offset;
        double lastBeat = 0, bpm = Bpms[0].Bpm;
        for (int i = 1; i < Bpms.Count && Bpms[i].Beat < beat; i++)
        {
            t += (Bpms[i].Beat - lastBeat) * 60.0 / bpm;
            lastBeat = Bpms[i].Beat;
            bpm = Bpms[i].Bpm;
        }
        t += (beat - lastBeat) * 60.0 / bpm;
        foreach (var stop in Stops)
            if (stop.Beat < beat) t += stop.Seconds;
        return t;
    }

    internal double SecondsToBeat(double seconds)
    {
        // Walk the tempo changes and stops in order until the time is used up.
        double t = -Offset, beat = 0, bpm = Bpms[0].Bpm;
        int nextBpm = 1, nextStop = 0;
        while (true)
        {
            double nextBpmBeat = nextBpm < Bpms.Count ? Bpms[nextBpm].Beat : double.MaxValue;
            double nextStopBeat = nextStop < Stops.Count ? Stops[nextStop].Beat : double.MaxValue;
            double nextBeat = Math.Min(nextBpmBeat, nextStopBeat);
            double segmentEnd = nextBeat == double.MaxValue ? double.MaxValue : t + (nextBeat - beat) * 60.0 / bpm;
            if (seconds <= segmentEnd) return beat + (seconds - t) * bpm / 60.0;
            t = segmentEnd;
            beat = nextBeat;
            if (nextStopBeat <= nextBpmBeat)
            {
                double stopEnd = t + Stops[nextStop].Seconds;
                if (seconds <= stopEnd) return beat;
                t = stopEnd;
                nextStop++;
            }
            else
            {
                bpm = Bpms[nextBpm].Bpm;
                nextBpm++;
            }
        }
    }

    internal double RowToSeconds(int row) => BeatToSeconds(row / (double)RowsPerBeat);
    internal double SecondsToRow(double seconds) => SecondsToBeat(seconds) * RowsPerBeat;
    internal double BpmAt(double beat)
    {
        double bpm = Bpms[0].Bpm;
        foreach (var b in Bpms) if (b.Beat <= beat) bpm = b.Bpm;
        return bpm;
    }

    // ---- notes ------------------------------------------------------------------------------

    internal void ReadNotes(string notes)
    {
        Notes.Clear();
        var open = new int[Lanes];
        var openType = new char[Lanes];
        for (int i = 0; i < Lanes; i++) open[i] = -1;
        var measures = notes.Split(',');
        for (int m = 0; m < measures.Length; m++)
        {
            var lines = measures[m].Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l != ";").ToList();
            if (lines.Count == 0) continue;
            for (int i = 0; i < lines.Count; i++)
            {
                int row = m * RowsPerMeasure + (int)Math.Round(i * (double)RowsPerMeasure / lines.Count);
                string line = lines[i];
                for (int lane = 0; lane < Lanes && lane < line.Length; lane++)
                {
                    char c = line[lane];
                    switch (c)
                    {
                        case '0': break;
                        case '2':
                        case '4':
                            open[lane] = Notes.Count;
                            openType[lane] = c;
                            Notes.Add(new Note { Row = row, Lane = lane, Type = c, EndRow = row });
                            break;
                        case '3':
                            if (open[lane] >= 0)
                            {
                                var n = Notes[open[lane]];
                                n.EndRow = Math.Max(row, n.Row + 1);
                                Notes[open[lane]] = n;
                                open[lane] = -1;
                            }
                            break;
                        case '1':
                        case 'M':
                        case 'L':
                        case 'F':
                        case 'K':
                            Notes.Add(new Note { Row = row, Lane = lane, Type = c, EndRow = row });
                            break;
                        default:
                            // Characters the game doesn't use (keysound brackets and so on) are dropped.
                            break;
                    }
                }
            }
        }
        // A hold without an end becomes a tap.
        for (int i = 0; i < Notes.Count; i++)
        {
            var n = Notes[i];
            if (n.IsLong && n.EndRow <= n.Row) { n.Type = '1'; Notes[i] = n; }
        }
        Sort();
    }

    internal void Sort() => Notes.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Lane.CompareTo(b.Lane));

    /// <summary>The #NOTES body: each measure at the coarsest row count that keeps every note on its row.</summary>
    internal string WriteNotes()
    {
        int lastRow = Notes.Count == 0 ? 0 : Notes.Max(n => n.LastRow);
        int measureCount = lastRow / RowsPerMeasure + 1;
        var grid = new Dictionary<int, char[]>();
        char[] Line(int row)
        {
            if (!grid.TryGetValue(row, out var line)) grid[row] = line = Enumerable.Repeat('0', Lanes).ToArray();
            return line;
        }
        foreach (var n in Notes)
        {
            var line = Line(n.Row);
            if (line[n.Lane] != '0') continue; // two notes in one cell: the first stays
            line[n.Lane] = n.Type;
            if (n.IsLong)
            {
                var end = Line(n.EndRow);
                if (end[n.Lane] == '0') end[n.Lane] = '3';
                else line[n.Lane] = '1'; // no room for the end: keep it as a tap
            }
        }
        var sb = new StringBuilder();
        int[] divisions = { 4, 8, 12, 16, 24, 32, 48, 64, 96, 192 };
        // One pass over the rows in order: each measure looks only at its own rows.
        var rows = grid.Keys.ToList();
        rows.Sort();
        int next = 0;
        while (next < rows.Count && rows[next] < 0) next++;
        for (int m = 0; m < measureCount; m++)
        {
            int start = m * RowsPerMeasure;
            int first = next;
            while (next < rows.Count && rows[next] < start + RowsPerMeasure) next++;
            int lines = divisions.First(d => OnGrid(rows, first, next, start, RowsPerMeasure / d));
            int step = RowsPerMeasure / lines;
            for (int i = 0; i < lines; i++)
            {
                int row = start + i * step;
                sb.Append(grid.TryGetValue(row, out var line) ? new string(line) : new string('0', Lanes)).Append('\n');
            }
            if (m < measureCount - 1) sb.Append(",\n");
        }
        return sb.ToString();
    }

    // Whether rows[from..to) all sit on every step-th row of the measure starting at start.
    private static bool OnGrid(List<int> rows, int from, int to, int start, int step)
    {
        for (int i = from; i < to; i++)
            if ((rows[i] - start) % step != 0) return false;
        return true;
    }

    internal EditorChart Clone()
    {
        var copy = new EditorChart(Lanes) { Offset = Offset, Notes = new List<Note>(Notes) };
        copy.Bpms.AddRange(Bpms);
        copy.Stops.AddRange(Stops);
        return copy;
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static double ParseDouble(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    private static IEnumerable<(double, double)> ParsePairs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (var part in text!.Split(','))
        {
            var kv = part.Split('=');
            if (kv.Length != 2) continue;
            yield return (ParseDouble(kv[0]), ParseDouble(kv[1]));
        }
    }
}
