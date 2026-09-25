using System.Globalization;
using System.Text;

namespace NocturneFlatScroll;

// The Timing tab: bookmarks, the metronome, and scroll speed changes. A scroll speed change makes
// the notes scroll faster or slower from a beat on without moving them in time (like osu!'s
// slider velocity or StepMania's #SCROLLS); the mod applies them in battle (ScrollSpeeds).
internal static partial class ChartEditor
{
    private static readonly List<double> bookmarks = new();          // seconds
    private static List<(double Beat, double Ratio)> scrolls = new();  // sorted by beat
    private static bool metronomeOn;
    private static bool waveformOn = true;
    private static float musicVolume = 1f, tickVolume = 0.45f;

    private static void LoadTiming(ChartText source)
    {
        bookmarks.Clear();
        foreach (var part in (source.GetTag("NBBBOOKMARKS") ?? "").Split(','))
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double t)) bookmarks.Add(t);
        bookmarks.Sort();
        scrolls = ScrollSpeeds.Parse(source.GetTag("SCROLLS"));
    }

    // ---- scroll speed: the "displayed beat" the notes are drawn at -------------------------

    /// <summary>The beat the playfield is drawn at for a chart time: the beat, stretched by the scroll speeds.</summary>
    private static double ScrollBeat(double seconds) => ScrollSpeeds.Displayed(scrolls, chart!.SecondsToBeat(seconds));

    private static double ScrollBeatToSeconds(double displayed) => chart!.BeatToSeconds(ScrollSpeeds.Undisplayed(scrolls, displayed));

    private static void SetScrollHere(double ratio)
    {
        PushUndo();
        double beat = SnapRow(chart!.SecondsToRow(Now)) / (double)EditorChart.RowsPerBeat;
        // Within a 1/96 beat counts as the same place, as saved beats are rounded.
        scrolls.RemoveAll(s => Math.Abs(s.Beat - beat) < 1.0 / 96);
        scrolls.Add((beat, Math.Clamp(ratio, 0.05, 20)));
        scrolls.Sort((a, b) => a.Beat.CompareTo(b.Beat));
        dirty = true;
        Say($"Notes scroll at x{ratio:0.##} from beat {beat:0.##}", 3f);
    }

    private static void RemoveScrollHere()
    {
        double beat = chart!.SecondsToBeat(Now);
        // The change in effect here (the last one at or before now).
        int index = scrolls.FindLastIndex(s => s.Beat <= beat + 1e-6);
        if (index < 0) { Say("No scroll speed change before this point", 2f); return; }
        PushUndo();
        Say($"Removed the x{scrolls[index].Ratio:0.##} change at beat {scrolls[index].Beat:0.##}", 3f);
        scrolls.RemoveAt(index);
        dirty = true;
    }

    private static string ScrollsTag() => ScrollSpeeds.Write(scrolls);

    // ---- bookmarks ---------------------------------------------------------------------------

    private static void ToggleBookmark()
    {
        double t = chart!.RowToSeconds(SnapRow(chart.SecondsToRow(Now)));
        int near = bookmarks.FindIndex(b => Math.Abs(b - t) < 0.01);
        if (near >= 0) { bookmarks.RemoveAt(near); Say("Bookmark removed", 1.5f); }
        else { bookmarks.Add(t); bookmarks.Sort(); Say("Bookmark added", 1.5f); }
        dirty = true;
    }

    private static void ClearBookmarks()
    {
        if (bookmarks.Count == 0) return;
        bookmarks.Clear();
        dirty = true;
        Say("Bookmarks cleared", 1.5f);
    }

    private static void JumpBookmark(int direction)
    {
        double now = Now;
        double? target = direction > 0
            ? bookmarks.Where(b => b > now + 0.01).Select(b => (double?)b).FirstOrDefault()
            : bookmarks.Where(b => b < now - 0.01).Select(b => (double?)b).LastOrDefault();
        if (target == null) { Say(direction > 0 ? "No bookmark after this" : "No bookmark before this", 1.5f); return; }
        SeekTo(target.Value);
    }

    // ---- sound ------------------------------------------------------------------------------

    private static void ToggleTicks()
    {
        ticksOn = !ticksOn;
        ticksDirty = true;
    }

    private static void ToggleMetronome()
    {
        metronomeOn = !metronomeOn;
        ticksDirty = true;
    }

    private static void SetMusicVolume(float value)
    {
        musicVolume = (float)Math.Round(Math.Clamp(value, 0f, 1f), 2);
        audio?.SetMusicVolume(musicVolume);
    }

    private static void SetTickVolume(float value)
    {
        tickVolume = (float)Math.Round(Math.Clamp(value, 0f, 1f), 2);
        ticksDirty = true;
    }

    private static void Zoom(float factor) => pixelsPerSecond = Math.Clamp(pixelsPerSecond * factor, 150f, 3000f);

    // ---- the Timing tab's text --------------------------------------------------------------

    private static string TimingText()
    {
        var sb = new StringBuilder();
        sb.Append("<color=#EAE6F5>Tempo</color> (from the song)\n");
        foreach (var (beat, bpm) in chart!.Bpms.Take(6))
            sb.Append($"  beat {beat:0.##}: {bpm:0.##} BPM\n");
        if (chart.Bpms.Count > 6) sb.Append($"  ...and {chart.Bpms.Count - 6} more\n");
        sb.Append("\n<color=#EAE6F5>Scroll speed</color> (notes keep their timing)\n");
        if (scrolls.Count == 0) sb.Append("  none: normal speed throughout\n");
        foreach (var (beat, ratio) in scrolls.Take(6)) sb.Append($"  beat {beat:0.##}: x{ratio:0.##}\n");
        if (scrolls.Count > 6) sb.Append($"  ...and {scrolls.Count - 6} more\n");
        sb.Append($"\n<color=#EAE6F5>Bookmarks</color>: {bookmarks.Count}");
        return sb.ToString();
    }
}
