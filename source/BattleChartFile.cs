using System.Globalization;
using System.Text;

namespace NocturneFlatScroll;

/// <summary>
/// A custom battle's chart as the chart editor edits it: one .sm whose header (timing, events,
/// scroll speeds) every difficulty shares, and a note block for each of the game's six difficulty
/// slots, which are the editor's difficulty tabs. Saving writes the tabs back into the same file
/// and checks it the way the battle loader (<see cref="BattlePackage"/>) will read it.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class BattleChartFile
{
    internal const int SlotCount = 6;

    /// <summary>
    /// The block each difficulty tab edits, by slot (-1 for none): the block the game plays in the
    /// slot (see <see cref="ChartText.SongSlots"/>); else the slot's first block with the battle's
    /// lane count, one with notes before an empty one, so its notes, name and meter are kept.
    /// </summary>
    internal static int[] ClaimBlocks(ChartText chart, int lanes)
    {
        var claimed = Enumerable.Repeat(-1, SlotCount).ToArray();
        var playing = chart.SongSlots(lanes, new List<string>());
        for (int s = 0; s < SlotCount; s++)
            if (playing[s] != null) claimed[s] = chart.Blocks.IndexOf(playing[s]!);
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < chart.Blocks.Count; i++)
            {
                var block = chart.Blocks[i];
                int s = ChartText.SlotOf(block.Difficulty);
                if (s < 0 || block.Lanes != lanes || claimed[s] >= 0 || claimed.Contains(i)) continue;
                if (pass == 0 && !ChartText.HasNotes(block)) continue;
                claimed[s] = i;
            }
        return claimed;
    }

    /// <summary>
    /// Blocks for the battle's lanes and a difficulty slot that no tab edits: second copies of a
    /// difficulty, which never play. Saving leaves them out.
    /// </summary>
    internal static List<int> LeftOut(ChartText chart, int lanes, int[] claimed) =>
        Enumerable.Range(0, chart.Blocks.Count)
            .Where(i => !claimed.Contains(i) && Slotted(chart.Blocks[i], lanes))
            .ToList();

    private static bool Slotted(ChartText.NoteBlock block, int lanes) => ChartText.SlotOf(block.Difficulty) >= 0 && block.Lanes == lanes;

    /// <summary>
    /// The slot whose notes the arcade plays in <paramref name="slot"/>: the slot itself when it has
    /// notes, else the nearest one that has, the easier when two are as near (the game's playable
    /// chart is filled that way, see <see cref="ChartText.BuildPlayableSong"/>); -1 when none has.
    /// </summary>
    internal static int PlaysInstead(bool[] charted, int slot)
    {
        var slots = new ChartText.NoteBlock?[SlotCount];
        for (int s = 0; s < SlotCount; s++)
            if (charted[s]) slots[s] = new ChartText.NoteBlock();
        var block = ChartText.NearestSlot(slots, slot);
        return block == null ? -1 : Array.IndexOf(slots, block);
    }

    /// <summary>
    /// The chart to save: <paramref name="tags"/> as its header, a block for each tab with notes
    /// in slot order (the tab's own block keeps its name, meter and radar values; a new one gets
    /// <paramref name="meters"/>), then the blocks the battle never plays (other lane counts or
    /// difficulty names) as they were. A tab without notes gets no block, so it plays nothing.
    /// </summary>
    /// <param name="tabNotes">Each slot's #NOTES text, or null when the tab has no notes.</param>
    internal static ChartText Compose(ChartText original, IEnumerable<KeyValuePair<string, string>> tags, int lanes, int[] claimed, string?[] tabNotes, int[] meters)
    {
        var output = new ChartText();
        output.Tags.AddRange(tags);
        for (int s = 0; s < SlotCount; s++)
        {
            if (tabNotes[s] == null) continue;
            var own = claimed[s] >= 0 ? original.Blocks[claimed[s]] : null;
            output.Blocks.Add(new ChartText.NoteBlock
            {
                StepsType = lanes == 5 ? "pump-single" : "dance-single",
                Description = own?.Description ?? "",
                Difficulty = ChartText.GameDifficultyNames[s],
                Meter = own != null && own.Meter.Trim().Length > 0 ? own.Meter : meters[s].ToString(CultureInfo.InvariantCulture),
                Radar = own != null && own.Radar.Trim().Length > 0 ? own.Radar : "0,0,0,0,0",
                Notes = tabNotes[s]!,
            });
        }
        for (int i = 0; i < original.Blocks.Count; i++)
            if (!claimed.Contains(i) && !Slotted(original.Blocks[i], lanes)) output.Blocks.Add(original.Blocks[i]);
        return output;
    }

    /// <summary>
    /// Checks chart text the way the battle loader reads it: parses it again, finds each
    /// difficulty as the game will, and builds the chart the game plays. Returns why it can't be
    /// saved, or null. <paramref name="notices"/> gets what doesn't stop it (the loader logs those).
    /// </summary>
    /// <param name="noteCounts">How many notes each tab has; a tab with none must not play.</param>
    internal static string? Check(string text, int lanes, int[] noteCounts, List<string> notices)
    {
        if (noteCounts.All(n => n == 0)) return "no difficulty has notes yet";
        var parsed = ChartText.Parse(text);
        var problems = new List<string>();
        var slots = parsed.SongSlots(lanes, problems);
        for (int s = 0; s < SlotCount; s++)
        {
            string name = ChartText.GameDifficultyLabels[s];
            if (noteCounts[s] > 0 && slots[s] == null)
                return $"{name} can't be played ({(problems.Count > 0 ? string.Join("; ", problems) : "the game won't read it")})";
            if (noteCounts[s] == 0 && slots[s] != null) return $"{name} would play notes the editor doesn't show";
            if (slots[s] == null) continue;
            var read = new EditorChart(lanes);
            read.ReadNotes(slots[s]!.Notes);
            if (read.Notes.Count != noteCounts[s])
                notices.Add($"{name}: {noteCounts[s] - read.Notes.Count} notes that sat on another note in the same lane were left out");
        }
        notices.AddRange(problems);
        string? offsetTag = parsed.GetTag("OFFSET");
        double offset = 0;
        if (offsetTag != null && !double.TryParse(offsetTag, NumberStyles.Float, CultureInfo.InvariantCulture, out offset))
            return $"#OFFSET \"{offsetTag}\" isn't a number";
        if (Math.Abs(offset) > LeadIn.MaxOffset) return $"#OFFSET is {offset.ToString("0.###", CultureInfo.InvariantCulture)} s; it can be at most {LeadIn.MaxOffset:0} s either way";
        if (offset > 0.001) notices.Add("beat 0 comes before the song starts, so notes before 0:00 can't be played");
        try { parsed.BuildPlayableSong(slots); }
        catch (InvalidDataException ex) { return ex.Message; }
        return null;
    }

    /// <summary>
    /// Whether the chart's own #BPMS and #OFFSET must be written from the editor's timing: the
    /// loader needs a first tempo that is a positive number, and an #OFFSET that is a number.
    /// </summary>
    internal static bool TimingNeedsWriting(ChartText chart)
    {
        var first = (chart.GetTag("BPMS") ?? "").Split(',')[0].Split('=');
        bool bpmOk = first.Length == 2 && double.TryParse(first[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm) && bpm > 0;
        bool offsetOk = double.TryParse(chart.GetTag("OFFSET") ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        return !bpmOk || !offsetOk;
    }

    /// <summary>Writes the file whole or not at all: to a temporary file first, then over the old one.</summary>
    internal static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

/// <summary>The Timing tab's arithmetic for battles: tempo sections, #OFFSET, and number formats.</summary>
internal static class BattleTiming
{
    internal const double MinBpm = 1, MaxBpm = 1000;

    /// <summary>The tempo section a beat is in: the last change at or before it.</summary>
    internal static int SectionAt(List<(double Beat, double Bpm)> bpms, double beat)
    {
        int index = 0;
        for (int i = 0; i < bpms.Count; i++)
            if (bpms[i].Beat <= beat + 1e-6) index = i;
        return index;
    }

    /// <summary>Sets the tempo of the section <paramref name="beat"/> is in (the whole song when it has one tempo).</summary>
    internal static void SetSectionBpm(List<(double Beat, double Bpm)> bpms, double beat, double bpm)
    {
        if (bpms.Count == 0) { bpms.Add((0, bpm)); return; }
        int i = SectionAt(bpms, beat);
        bpms[i] = (bpms[i].Beat, bpm);
    }

    /// <summary>Starts a new tempo at a beat (on the 1/48 beat grid), or changes the one that starts there.</summary>
    internal static void AddChange(List<(double Beat, double Bpm)> bpms, double beat, double bpm)
    {
        beat = Math.Max(0, Math.Round(beat * EditorChart.RowsPerBeat) / EditorChart.RowsPerBeat);
        int same = bpms.FindIndex(b => Math.Abs(b.Beat - beat) < 1.0 / 96);
        if (same >= 0) bpms[same] = (bpms[same].Beat, bpm);
        else
        {
            bpms.Add((beat, bpm));
            bpms.Sort((a, b) => a.Beat.CompareTo(b.Beat));
        }
    }

    /// <summary>Removes the tempo change in effect at a beat. The song's first tempo stays; then this returns null.</summary>
    internal static double? RemoveChange(List<(double Beat, double Bpm)> bpms, double beat)
    {
        int i = SectionAt(bpms, beat);
        if (i <= 0) return null;
        double removed = bpms[i].Beat;
        bpms.RemoveAt(i);
        return removed;
    }

    /// <summary>#OFFSET that puts beat 0 at <paramref name="seconds"/> into the song (StepMania: beat 0 is at -#OFFSET).</summary>
    internal static double OffsetForFirstBeat(double seconds) => Clean(-seconds);

    /// <summary>#OFFSET after moving every beat <paramref name="ms"/> milliseconds later (earlier when negative).</summary>
    internal static double Nudge(double offset, double ms) => Clean(offset - ms / 1000.0);

    // To a tenth of a millisecond, and never "-0".
    private static double Clean(double seconds)
    {
        seconds = Math.Round(seconds, 4);
        return seconds == 0 ? 0 : seconds;
    }

    internal static string WriteBpms(IEnumerable<(double Beat, double Bpm)> bpms) =>
        string.Join(",", bpms.Select(b => $"{Number(b.Beat)}={Number(b.Bpm)}"));

    /// <summary>A number for the .sm file: invariant, up to six decimals, never "-0".</summary>
    internal static string Number(double value) =>
        (Math.Abs(value) < 5e-7 ? 0.0 : value).ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>
/// Tap tempo: the times of the taps and the tempo they make. A pause of more than
/// <see cref="MaxGap"/> seconds, or a tap before the last one (the music was moved back), starts
/// over. The tempo is a least-squares fit over the last <see cref="MaxTaps"/> taps, which evens
/// out taps that each land a frame early or late.
/// </summary>
internal sealed class TapTempo
{
    internal const int MaxTaps = 16;
    internal const double MaxGap = 2.0;

    private readonly List<double> taps = new();

    internal int Count => taps.Count;

    internal void Clear() => taps.Clear();

    internal void Tap(double seconds)
    {
        if (taps.Count > 0 && (seconds <= taps[^1] || seconds - taps[^1] > MaxGap)) taps.Clear();
        taps.Add(seconds);
        if (taps.Count > MaxTaps) taps.RemoveAt(0);
    }

    /// <summary>The taps' tempo in beats a minute, or null before the second tap.</summary>
    internal double? Bpm
    {
        get
        {
            int n = taps.Count;
            if (n < 2) return null;
            double meanIndex = (n - 1) / 2.0, meanTime = taps.Average(), num = 0, den = 0;
            for (int i = 0; i < n; i++)
            {
                num += (i - meanIndex) * (taps[i] - meanTime);
                den += (i - meanIndex) * (i - meanIndex);
            }
            double secondsPerBeat = num / den;
            return secondsPerBeat > 0 ? 60.0 / secondsPerBeat : null;
        }
    }
}
