using System.Globalization;
using System.Text;

namespace NocturneFlatScroll;

// The chart's enemy events: the song's #ATTACKS (lane layouts, the enemy's props and animations,
// helper attacks, combat effects like vines, camera moves, text). A chart starts with the song's
// own events, so it plays exactly like the song; the Events tab changes them, and only then does
// the chart save its own list (with #NBBEVENTS:chart).
internal static partial class ChartEditor
{
    private sealed class ChartEvent
    {
        internal double Time;       // seconds on the chart's clock
        internal double Length;     // seconds
        internal string Mods = "";  // "Verb arg arg", several joined with ", "

        internal ChartEvent Copy() => new() { Time = Time, Length = Length, Mods = Mods };
        internal string Verb => Mods.Split(' ', ',')[0];
    }

    private static List<ChartEvent> events = new();
    private static List<ChartEvent> songEvents = new();   // the song's own, for "Song's events"
    private static bool eventsChanged;
    private static int eventIndex = -1;                   // the event picked in the Events tab

    /// <summary>Reads #ATTACKS: TIME=s:LEN=s:MODS=text, repeated.</summary>
    private static List<ChartEvent> ParseEvents(string? attacks)
    {
        var list = new List<ChartEvent>();
        if (string.IsNullOrWhiteSpace(attacks)) return list;
        ChartEvent? current = null;
        foreach (var raw in attacks!.Split(':'))
        {
            var part = raw.Trim();
            int eq = part.IndexOf('=');
            if (eq < 0) { if (current != null) current.Mods += ":" + part; continue; }
            string key = part.Substring(0, eq).Trim().ToUpperInvariant(), value = part.Substring(eq + 1).Trim();
            switch (key)
            {
                case "TIME":
                    current = new ChartEvent { Time = Number(value) };
                    list.Add(current);
                    break;
                case "LEN":
                    if (current != null) current.Length = Number(value);
                    break;
                case "END":
                    if (current != null) current.Length = Number(value) - current.Time;
                    break;
                case "MODS":
                    if (current != null) current.Mods = value;
                    break;
            }
        }
        // A stable sort, so events at the same time keep the song's order.
        return list.OrderBy(e => e.Time).ToList();
    }

    private static string WriteEvents(List<ChartEvent> list)
    {
        var sb = new StringBuilder();
        foreach (var e in list.OrderBy(e => e.Time))
        {
            if (sb.Length > 0) sb.Append(':');
            sb.Append("TIME=").Append(e.Time.ToString("0.000", CultureInfo.InvariantCulture))
              .Append(":LEN=").Append(e.Length.ToString("0.000", CultureInfo.InvariantCulture))
              .Append(":MODS=").Append(e.Mods.Replace(":", " ").Replace(";", " "));
        }
        return sb.ToString();
    }

    private static double Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    /// <summary>Loads the events for a new editing session.</summary>
    private static void LoadEvents(ChartText source, CustomCharts.CustomChart? custom)
    {
        songEvents = ParseEvents(GameChart().GetTag("ATTACKS"));
        if (custom != null && !custom.KeepSongEvents)
        {
            events = ParseEvents(source.GetTag("ATTACKS"));
            eventsChanged = true;
        }
        else
        {
            events = songEvents.Select(e => e.Copy()).ToList();
            eventsChanged = false;
        }
        eventIndex = events.Count > 0 ? 0 : -1;
    }

    private static void EventsEdited()
    {
        eventsChanged = true;
        dirty = true;
        confirmLeave = false;
        // Stable, and the picked event stays picked wherever it moved to.
        var picked = PickedEvent;
        events = events.OrderBy(e => e.Time).ToList();
        if (picked != null) eventIndex = events.IndexOf(picked);
    }

    // ---- actions for the Events tab ---------------------------------------------------------

    private static ChartEvent? PickedEvent => eventIndex >= 0 && eventIndex < events.Count ? events[eventIndex] : null;

    private static void PickEvent(int index, bool jump)
    {
        if (events.Count == 0) { eventIndex = -1; return; }
        eventIndex = Math.Clamp(index, 0, events.Count - 1);
        if (jump) SeekTo(events[eventIndex].Time);
    }

    private static void AddEventHere()
    {
        PushUndo();
        // A copy of the picked event (or the song's first) at the current time, on the snap.
        var template = PickedEvent ?? songEvents.FirstOrDefault();
        double time = chart!.RowToSeconds(SnapRow(chart.SecondsToRow(Now)));
        var added = new ChartEvent { Time = time, Length = template?.Length ?? 1, Mods = template?.Mods ?? "TriggerCombatEffect 0" };
        events.Add(added);
        EventsEdited();
        eventIndex = events.IndexOf(added);
        Say("Added an event here. Edit its text to change what it does.", 4f);
    }

    private static void MovePickedEventHere()
    {
        var e = PickedEvent;
        if (e == null) return;
        PushUndo();
        e.Time = chart!.RowToSeconds(SnapRow(chart.SecondsToRow(Now)));
        EventsEdited();
        eventIndex = events.IndexOf(e);
    }

    private static void NudgePickedEvent(int snaps)
    {
        var e = PickedEvent;
        if (e == null) return;
        PushUndo();
        double row = chart!.SecondsToRow(e.Time) + snaps * SnapRows;
        e.Time = chart.RowToSeconds(Math.Max(0, SnapRow(row)));
        EventsEdited();
        eventIndex = events.IndexOf(e);
    }

    private static void ChangePickedEventLength(double seconds)
    {
        var e = PickedEvent;
        if (e == null) return;
        PushUndo();
        e.Length = Math.Max(0, Math.Round(e.Length + seconds, 3));
        EventsEdited();
    }

    private static void DuplicatePickedEvent()
    {
        var e = PickedEvent;
        if (e == null) return;
        PushUndo();
        var copy = e.Copy();
        copy.Time = chart!.RowToSeconds(SnapRow(chart.SecondsToRow(Now)));
        events.Add(copy);
        EventsEdited();
        eventIndex = events.IndexOf(copy);
    }

    private static void DeletePickedEvent()
    {
        if (PickedEvent == null) return;
        PushUndo();
        events.RemoveAt(eventIndex);
        EventsEdited();
        eventIndex = Math.Min(eventIndex, events.Count - 1);
    }

    private static void RestoreSongEvents()
    {
        PushUndo();
        events = songEvents.Select(e => e.Copy()).ToList();
        eventsChanged = false;
        dirty = true;
        eventIndex = events.Count > 0 ? 0 : -1;
        Say("Back to the song's own events", 3f);
    }
}
