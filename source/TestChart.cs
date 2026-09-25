namespace NocturneFlatScroll;

/// <summary>
/// The chart a test play runs, worked out from the chart editor's play position: where the
/// battle's clock starts, which notes are left, and which events still happen. A test "from
/// here" starts the clock two bars before the play position with the notes before it left out;
/// events before the clock start are dropped, except those that set up how the battle looks
/// (lane layouts, the camera, props), which are moved to the clock start so they're in place from
/// the first frame. This file has no Unity or game dependencies.
/// </summary>
internal static class TestChart
{
    /// <summary>How far before the play position the battle starts: two bars, kept to 1.5 to 4 s.</summary>
    internal const double LeadBeats = 8, MinLead = 1.5, MaxLead = 4.0;
    /// <summary>A start this close to the song's start is the usual start instead.</summary>
    internal const double SnapToStart = 1.0;
    /// <summary>A note this much before the play position still counts, so one on the judge line is tested.</summary>
    internal const double NoteSlack = 0.010;

    /// <summary>
    /// Where a test starts: the battle's clock start <c>T0</c> in seconds (0 is the usual start)
    /// and the first row whose notes play. <paramref name="at"/> is the play position.
    /// </summary>
    internal static (double T0, int FirstRow) StartPoint(EditorChart chart, double at, bool fromStart)
    {
        if (fromStart) return (0, 0);
        int firstRow = Math.Max(0, (int)Math.Ceiling(chart.SecondsToRow(at - NoteSlack) - 1e-6));
        double t0 = chart.BeatToSeconds(chart.SecondsToBeat(at) - LeadBeats);
        t0 = Math.Clamp(t0, at - MaxLead, at - MinLead);
        if (t0 < SnapToStart) return (0, firstRow);
        // Whole milliseconds, so the events moved to the start are written at exactly the start.
        return (Math.Floor(t0 * 1000) / 1000, firstRow);
    }

    /// <summary>The notes from a row on; a hold counts by its head.</summary>
    internal static List<EditorChart.Note> NotesFrom(IEnumerable<EditorChart.Note> notes, int firstRow) =>
        notes.Where(n => n.Row >= firstRow).ToList();

    /// <summary>Whether a player has something to hit: a note that isn't a mine.</summary>
    internal static bool HasPlayableNote(IEnumerable<EditorChart.Note> notes) => notes.Any(n => n.Type != 'M');

    /// <summary>
    /// The events a test starting at <paramref name="t0"/> plays. Events from <paramref name="t0"/>
    /// on stay as they are. From earlier ones, the mods that set state (see <see cref="StateKey"/>)
    /// are moved to <paramref name="t0"/>, the last one of each kind in the order they last came,
    /// each as an event of its own lasting what was left of the original; the rest (attacks,
    /// text, one-off animations) are dropped, since firing them all at the start would land at
    /// once. The moved events come first. <paramref name="carried"/> counts them.
    /// </summary>
    internal static List<(double Time, double Length, string Mods)> EventsFrom(IEnumerable<(double Time, double Length, string Mods)> events,
                                                                                 double t0, out int carried)
    {
        var sorted = events.OrderBy(e => e.Time).ToList();
        carried = 0;
        if (t0 <= 0) return sorted;
        // Key -> the mod and when its event ended, in the order each key last came.
        var state = new List<(string Key, string Mod, double End)>();
        foreach (var e in sorted.Where(e => e.Time < t0))
            foreach (var raw in e.Mods.Split(','))
            {
                string mod = raw.Trim();
                string? key = StateKey(mod);
                if (key == null) continue;
                state.RemoveAll(s => s.Key == key);
                state.Add((key, mod, e.Time + e.Length));
            }
        List<(double Time, double Length, string Mods)> result = state.Select(s => (t0, Math.Max(0, s.End - t0), s.Mod)).ToList();
        carried = result.Count;
        result.AddRange(sorted.Where(e => e.Time >= t0));
        return result;
    }

    /// <summary>
    /// What a mod sets, when it sets something that lasts: two mods with the same key set the same
    /// thing, so only the later one counts. Null for a mod that only does something once.
    /// </summary>
    internal static string? StateKey(string mod)
    {
        var words = mod.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;
        string verb = words[0];
        string Arg(int i) => i < words.Length ? words[i].ToLowerInvariant() : "";
        switch (verb.ToLowerInvariant())
        {
            case "columnlayout": return "ColumnLayout";
            case "columnanimationint": return "ColumnAnimationInt " + Arg(1);
            case "prefabanimationint": return "PrefabAnimationInt " + Arg(1) + " " + Arg(2);
            case "enemyanimatorint": return "EnemyAnimatorInt " + Arg(1);
            case "enemyanimatorbool": return "EnemyAnimatorBool " + Arg(1);
            // In and out of one profile set the same thing; two profiles can be on at once.
            case "postprocessin":
            case "postprocessout": return "PostProcess " + Arg(1);
            case "camposition": return "CamPosition";
            case "camrotate": return "CamRotate";
            case "hardcutoverworld":
            case "hardcutcombat": return "HardCut";
            // Every prop once.
            case "spawnprefab": return "SpawnPrefab " + Arg(1);
            default: return null;
        }
    }
}
