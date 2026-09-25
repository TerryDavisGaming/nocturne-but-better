using System.Globalization;
using System.Text;

namespace NocturneFlatScroll;

// The osu!mania import's summary (beta): what the battle creator shows for a .osz it has read and
// the player's choices, as plain rows with their hints, the lists behind them (slot, lanes, speed
// changes, details), and the log lines. The creator escapes all of it, so a beatmap's names show
// as they are (control characters out, long ones cut). This file has no Unity or game
// dependencies, so the offline checks read the same text the player does.

/// <summary>What a row of the import's summary is, and so what choosing it does.</summary>
internal enum OszRow { Make, About, Lanes, Slot, Excluded, Unusable, OtherLanes, Speed, Attacks, Tempo, HeadsUp, Song, Details, Cancel }

/// <summary>A row of the import's summary.</summary>
internal sealed class OszSummaryRow
{
    internal OszRow Kind;
    /// <summary>The difficulty a Slot, Excluded, Unusable or OtherLanes row is about.</summary>
    internal OszDifficulty? Difficulty;
    internal string Text = "", Hint = "";
    /// <summary>The row only tells (its hint says it all): choosing it does nothing.</summary>
    internal bool Info;
}

/// <summary>A set of speed changes some of a lane group's difficulties share.</summary>
internal sealed class OszSpeedSet
{
    /// <summary>The difficulty the battle takes it from: the one in the battle with the most notes, else the one with the most.</summary>
    internal OszDifficulty From = null!;
    /// <summary>Hardest first.</summary>
    internal readonly List<OszDifficulty> Members = new();
    internal OszSpeeds Speeds = null!;
}

internal static class OszSummary
{
    internal const string Beta = "Beta, not recommended";
    internal const string ListRow = "New battle from an osu!mania beatmap (.osz)...";
    internal const string ListHint = "Beta, not recommended. Makes a battle from an osu!mania .osz: its song, 4K or 5K charts, tempo and speed changes. All of it stays editable.";
    internal const string DialogTitle = "New battle from an osu!mania beatmap (beta, not recommended)";
    internal const string Heading = "osu!mania import  (Beta, not recommended)";
    internal const string MakeText = "Make the battle";
    internal const string SlotHint = "Each slot holds one chart. Choosing a slot another difficulty has swaps the two.  Esc goes back.";
    internal const string LeaveOut = "Leave it out";
    internal const string LanesHeading = "How many lanes?";
    internal const string LanesHint = "A battle has 4 or 5 lanes. The difficulties with the other key count are left out; import the .osz again for those.  Esc goes back.";
    internal const string SpeedHeading = "Speed changes for the whole battle:";
    internal const string SpeedHint = "A battle has one set of speed changes for all its difficulties. Pick whose to use.  Esc goes back.";
    internal const string NoSpeedChanges = "No speed changes";
    internal const string DetailsHeading = "Changed or left out";
    internal const string Made = "Made from the osu!mania beatmap (beta). Test play each chart before you share it; everything can be edited in the chart editor.";
    /// <summary>A first note sooner than this into the song gets a heads-up row: a battle starts with the song.</summary>
    internal const double HeadsUpSeconds = OszConvert.OpeningSeconds;
    // A beatmap's names in a row are cut shorter than in a hint, so the rest of the row still shows.
    private const int RowName = 40, HintName = 60;
    /// <summary>The hint line holds about three lines of text.</summary>
    internal const int MaxHint = 300;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // ---- the summary -----------------------------------------------------------------------------

    /// <summary>The summary's rows, top to bottom: Make first, Cancel last.</summary>
    internal static List<OszSummaryRow> Rows(OszPlan plan, OszChoices choices)
    {
        var rows = new List<OszSummaryRow> { MakeRow(plan, choices), AboutRow(plan), LanesRow(plan, choices) };
        var group = plan.Group(choices.Lanes);
        if (group != null)
        {
            for (int s = 0; s < choices.Slots.Length; s++)
                if (choices.Slots[s] is { } d && group.Charts.TryGetValue(d, out var chart)) rows.Add(SlotRow(s, d, chart, group, choices));
            bool full = choices.Slots.All(d => d != null);
            foreach (var d in group.Usable)
                if (choices.SlotOf(d) < 0) rows.Add(ExcludedRow(d, group.Charts[d], group, full, choices));
        }
        foreach (var d in plan.Difficulties)
            if (d.Unusable != null) rows.Add(UnusableRow(d));
        var other = plan.Group(choices.Lanes == 5 ? 4 : 5);
        if (other != null)
            foreach (var d in other.Usable) rows.Add(OtherLanesRow(d, other.Lanes, choices.Lanes));
        if (group != null)
        {
            rows.Add(SpeedRow(plan, group, choices));
            if (choices.Lanes == 5) rows.Add(AttacksRow(group, choices));
            rows.Add(TempoRow(group, choices));
            if (FirstNoteSeconds(group, choices) is double first && first < HeadsUpSeconds) rows.Add(HeadsUpRow(first, group, choices));
        }
        if (plan.Song != null) rows.Add(SongRow(plan, plan.Song));
        int details = OszConvert.Details(plan, choices).Count;
        if (details > 0)
            rows.Add(new OszSummaryRow
            {
                Kind = OszRow.Details,
                Text = $"Changed or left out: {OszConvert.Count(details, "thing", "things")} (choose to see them)",
                Hint = "What the import changed to fit a battle, and what osu! has that a battle doesn't use.",
            });
        rows.Add(new OszSummaryRow { Kind = OszRow.Cancel, Text = "Cancel", Hint = "Goes back without making anything." });
        return rows;
    }

    /// <summary>Why the battle can't be made with these choices (a sentence), or null.</summary>
    internal static string? MakeProblem(OszPlan plan, OszChoices choices)
    {
        var group = plan.Group(choices.Lanes);
        if (group == null || plan.Song == null || !choices.Slots.Any(d => d != null && group.Charts.ContainsKey(d)))
            return "Give at least one difficulty a slot first.";
        if (!choices.Slots.Any(d => d != null && group.Charts.TryGetValue(d, out var chart) && OszConvert.KeptCount(chart, choices) > 0))
            return $"All the notes of the charts with a slot are in the first {Seconds(HeadsUpSeconds)} s. Keep those notes first.";
        long bytes = ChartBytes(plan, choices);
        if (bytes > BattlePackage.MaxChartBytes)
            return $"The charts come to {Mb(bytes)} MB, and a battle holds at most {BattlePackage.MaxChartBytes / (1024 * 1024)} MB. Leave a difficulty out first.";
        return null;
    }

    /// <summary>
    /// About how big the battle's chart would be: the notes exactly (measured when the .osz was
    /// read), the header closely. Making it checks the real size again.
    /// </summary>
    internal static long ChartBytes(OszPlan plan, OszChoices choices)
    {
        var group = plan.Group(choices.Lanes);
        if (group == null) return 0;
        long bytes = 1024 + Utf8(plan.Title) + Utf8(plan.Artist) + Utf8(plan.Mapper) + Utf8(group.Timing.BpmsTag)
                     + Utf8(ChosenSpeeds(group, choices).Tag) + Utf8(string.Join(",", group.Bookmarks.Select(b => b.ToString("0.###", Invariant))));
        foreach (var d in choices.Slots)
            if (d != null && group.Charts.TryGetValue(d, out var chart)) bytes += chart.TextBytes + 200 + Utf8(d.Name);
        if (choices.Lanes == 5 && choices.PlayerAttacks && IncludedRows(group, choices) is var (first, last) && last >= 0)
            bytes += 48L * OszConvert.PlayerAttackRows(first, last).Count;
        return bytes;
    }

    private static OszSummaryRow MakeRow(OszPlan plan, OszChoices choices) => new()
    {
        Kind = OszRow.Make,
        Text = MakeText,
        Hint = MakeProblem(plan, choices)
               ?? "Beta, not recommended: osu!mania charts may not play well as battles. Makes a new battle with this song, card and these charts, then opens it.",
    };

    private static OszSummaryRow AboutRow(OszPlan plan)
    {
        string text = Q(plan.Title.Trim().Length > 0 ? plan.Title : "(no title)", HintName);
        if (plan.Artist.Trim().Length > 0) text += " by " + Show(plan.Artist, RowName);
        if (plan.Mapper.Trim().Length > 0) text += ", charted by " + Show(plan.Mapper, RowName);
        const string hint = "From the beatmap. Change the title, artist and charter on the battle's Info page afterwards.";
        return new OszSummaryRow { Kind = OszRow.About, Text = text, Hint = hint, Info = true };
    }

    private static OszSummaryRow LanesRow(OszPlan plan, OszChoices choices)
    {
        int lanes = choices.Lanes;
        bool both = plan.Four != null && plan.Five != null;
        return new OszSummaryRow
        {
            Kind = OszRow.Lanes,
            Text = "Lanes: " + LaneText(lanes),
            Hint = both
                ? "A battle has 4 or 5 lanes, set when it's made. Choose to switch; the difficulties with the other key count are left out."
                : $"Its usable difficulties are all {lanes}K, so the battle has {lanes} lanes.",
            Info = !both,
        };
    }

    private static OszSummaryRow SlotRow(int slot, OszDifficulty d, OszChart chart, OszLaneGroup group, OszChoices choices)
    {
        string text = $"{ChartText.GameDifficultyLabels[slot]} <- {Q(d.Name)} ({group.Lanes}K): {Notes(chart, choices)}";
        if (chart.Moved > 0) text += $"; {OszConvert.N(chart.Moved)} moved to the beat grid (up to {OszConvert.Ms(chart.WorstMs)} ms)";
        return new OszSummaryRow { Kind = OszRow.Slot, Difficulty = d, Text = text, Hint = DifficultyHint(d, chart, choices) + " Choose to change its slot." };
    }

    private static OszSummaryRow ExcludedRow(OszDifficulty d, OszChart chart, OszLaneGroup group, bool slotsFull, OszChoices choices) => new()
    {
        Kind = OszRow.Excluded,
        Difficulty = d,
        Text = $"Left out <- {Q(d.Name)} ({group.Lanes}K): {Notes(chart, choices)}",
        Hint = DifficultyHint(d, chart, choices) + (slotsFull ? " Only six difficulty slots: choose it to swap it in." : " It isn't in the battle: choose it to give it a slot."),
    };

    private static OszSummaryRow UnusableRow(OszDifficulty d) => new()
    {
        Kind = OszRow.Unusable,
        Difficulty = d,
        Text = $"Left out <- {Q(d.Name)}: {d.Unusable}",
        Hint = $"{Quote(d.Name)} can't be in a battle: {d.Unusable}.",
        Info = true,
    };

    private static OszSummaryRow OtherLanesRow(OszDifficulty d, int keys, int lanes) => new()
    {
        Kind = OszRow.OtherLanes,
        Difficulty = d,
        Text = $"Left out <- {Q(d.Name)}: {keys}K; this battle has {lanes} lanes",
        Hint = $"Switch Lanes to {keys} to use the {keys}K difficulties.",
        Info = true,
    };

    private static OszSummaryRow SpeedRow(OszPlan plan, OszLaneGroup group, OszChoices choices)
    {
        var sets = SpeedSets(group, choices);
        var from = choices.SpeedsFrom;
        bool on = from != null && group.HasSpeedChanges(from);
        // The ones that keep stretched sections looking even aren't osu!'s, so they're counted apart.
        int evening = ChosenSpeeds(group, choices).Evening;
        string text = evening > 0 ? $"Speed changes: none from osu! ({Evening(evening)})" : "Speed changes: none";
        if (on)
        {
            var (count, range) = Own(group.Speeds[from!]);
            text = $"Speed changes: {OszConvert.N(count)} ({range}), from {Q(from!.Name)}{(choices.SlotOf(from) < 0 ? " (left out)" : "")}, for every difficulty"
                   + (evening > 0 ? $"; {OszConvert.N(evening)} more {(evening == 1 ? "keeps a stretched beat" : "keep stretched beats")} even" : "");
        }
        string hint;
        if (sets.Count == 0)
            hint = (plan.Group(group.Lanes == 5 ? 4 : 5) != null ? $"Its {group.Lanes}K difficulties have no speed changes." : "The beatmap has no speed changes.")
                   + (evening > 0 ? " Each stretched beat (see Tempo) has one of its own that keeps it looking even." : "");
        else if (!on) hint = "The battle has no speed changes. Choose to use a difficulty's.";
        else if (sets.Count > 1) hint = "A battle has one set of speed changes for all its difficulties. Choose whose to use, or none.";
        else
        {
            var members = sets[0].Members;
            bool everyOne = choices.Slots.All(d => d == null || !group.Charts.ContainsKey(d) || members.Contains(d));
            hint = everyOne
                ? "All its difficulties have these speed changes. Edit them later on the chart editor's Timing page. Choose to leave them out."
                : $"Only {Names(members, 3, RowName)} {(members.Count == 1 ? "has" : "have")} speed changes; the battle uses them for every difficulty. Choose to leave them out.";
        }
        return new OszSummaryRow { Kind = OszRow.Speed, Text = text, Hint = hint, Info = sets.Count == 0 };
    }

    private static OszSummaryRow AttacksRow(OszLaneGroup group, OszChoices choices)
    {
        var (first, last) = IncludedRows(group, choices);
        int count = last >= 0 ? OszConvert.PlayerAttackRows(first, last).Count : 0;
        return new OszSummaryRow
        {
            Kind = OszRow.Attacks,
            Text = choices.PlayerAttacks ? $"Player attacks: one every 8 bars ({OszConvert.N(count)} in all)" : "Player attacks: none",
            Hint = choices.PlayerAttacks
                ? "In 5 lanes, player attacks are the only way to hurt the enemy, and osu! has none. These go every 8 bars; move or delete them in the chart editor (Events)."
                : "Without player attacks the enemy can't be hurt in 5 lanes. Add some in the chart editor (Events) before playing.",
        };
    }

    private static OszSummaryRow TempoRow(OszLaneGroup group, OszChoices choices)
    {
        var timing = group.Timing;
        var tempos = Tempos(timing);
        int changes = 0;
        for (int i = 1; i < tempos.Count; i++)
            if (Bpm(tempos[i]) != Bpm(tempos[i - 1])) changes++;
        string from = Q(group.TimingFrom.Name);
        string text = changes == 0
            ? $"Tempo: {Bpm(tempos[0])} BPM, from {from}"
            : $"Tempo: {Bpm(tempos.Min())} to {Bpm(tempos.Max())} BPM ({OszConvert.Count(changes, "change", "changes")}), from {from}";
        var hint = new StringBuilder($"Every chart uses the tempo of {Q(group.TimingFrom.Name)}, the one with the most notes. {BeatZero(timing.Chart.Offset)}");
        if (choices.SlotOf(group.TimingFrom) < 0) hint.Append(" It isn't in the battle, but still times every chart.");
        if (timing.Fillers > 0)
            hint.Append(timing.Fillers == 1
                ? " 1 beat is stretched to reach a tempo line that wasn't on a beat."
                : $" {OszConvert.N(timing.Fillers)} beats are stretched to reach tempo lines that weren't on a beat.");
        if (timing.Effects > 0)
            hint.Append(timing.Effects == 1 ? " 1 osu! tempo effect is now a speed change." : $" {OszConvert.N(timing.Effects)} osu! tempo effects are now speed changes.");
        if (timing.Stretched > 0)
        {
            const string evenOut = " Each stretched beat has a speed change that keeps it looking even; deleting it makes that beat look faster or slower.";
            const string shortEvenOut = " Each stretched beat has a speed change that keeps it looking even.";
            if (hint.Length + evenOut.Length <= MaxHint) hint.Append(evenOut);
            else if (hint.Length + shortEvenOut.Length <= MaxHint) hint.Append(shortEvenOut);
        }
        const string fineTune = " Fine-tune it on the chart editor's Timing page.";
        if (hint.Length + fineTune.Length <= MaxHint) hint.Append(fineTune);
        return new OszSummaryRow { Kind = OszRow.Tempo, Text = text, Hint = hint.ToString(), Info = true };
    }

    private static OszSummaryRow HeadsUpRow(double first, OszLaneGroup group, OszChoices choices)
    {
        int opening = 0;
        foreach (var d in choices.Slots)
            if (d != null && group.Charts.TryGetValue(d, out var chart)) opening += chart.Opening;
        string notes = OszConvert.Count(opening, "note", "notes"), within = $"in the first {Seconds(HeadsUpSeconds)} s";
        if (choices.LeaveOutOpening)
            return new OszSummaryRow
            {
                Kind = OszRow.HeadsUp,
                Text = $"Heads-up: {notes} {within} {(opening == 1 ? "is" : "are")} left out",
                Hint = $"A battle starts with the song, so notes {within} come right away. The battle leaves out {notes}. Choose to keep them, as osu! has them.",
            };
        return new OszSummaryRow
        {
            Kind = OszRow.HeadsUp,
            Text = $"Heads-up: the first note is {Math.Max(0, first).ToString("0.00", Invariant)} s into the song",
            Hint = $"osu! waits a moment before the song; a battle starts with the song, so the first notes come right away. Choose to leave out the {notes} {within}, or edit them in the chart editor later.",
        };
    }

    private static OszSummaryRow SongRow(OszPlan plan, OszSong song)
    {
        string text = $"Song: {Show(Leaf(song.FileName), 50)} ({OszConvert.Clock(song.Seconds)})   Card: {(plan.Card != null ? Show(Leaf(plan.Card.FileName), 50) : "none")}";
        var hint = new StringBuilder("The song and the background image are copied into the battle.");
        if (plan.Card == null) hint.Append(' ').Append(plan.CardProblem ?? "The beatmap has no background image, so the battle has no card.").Append(" You can pick one on the Info page.");
        if (Math.Abs(song.Shift) >= 0.00005)
        {
            string ms = OszConvert.Ms(Math.Abs(song.Shift) * 1000);
            hint.Append(song.Shift > 0
                ? $" osu! starts this MP3 {ms} ms later than the game does, so every note is {ms} ms earlier to stay on the music."
                : $" osu! starts this MP3 {ms} ms earlier than the game does, so every note is {ms} ms later to stay on the music.");
        }
        return new OszSummaryRow { Kind = OszRow.Song, Text = text, Hint = hint.ToString(), Info = true };
    }

    // ---- the lists behind the rows ---------------------------------------------------------------

    internal static string SlotHeading(OszDifficulty d) => $"{Q(d.Name)} plays as:";

    /// <summary>The six slots (each naming the difficulty in it now), then "Leave it out".</summary>
    internal static List<string> SlotRows(OszChoices choices)
    {
        var rows = new List<string>();
        for (int s = 0; s < choices.Slots.Length; s++)
            rows.Add(ChartText.GameDifficultyLabels[s] + (choices.Slots[s] is { } taken ? $"  (now {Q(taken.Name)})" : ""));
        rows.Add(LeaveOut);
        return rows;
    }

    internal static List<string> LaneRows(OszPlan plan) => new()
    {
        $"4 lanes (D F J K): {OszConvert.Count(plan.Four?.Usable.Count ?? 0, "difficulty", "difficulties")}",
        $"5 lanes (D F Space J K; the middle lane attacks): {OszConvert.Count(plan.Five?.Usable.Count ?? 0, "difficulty", "difficulties")}",
    };

    /// <summary>One row per set of speed changes the lane group's difficulties have, hardest first, then "No speed changes".</summary>
    internal static List<(string Text, OszDifficulty? From)> SpeedChoices(OszPlan plan, OszChoices choices)
    {
        var list = new List<(string Text, OszDifficulty? From)>();
        if (plan.Group(choices.Lanes) is { } group)
            foreach (var set in SpeedSets(group, choices))
            {
                bool inBattle = set.Members.Any(m => choices.SlotOf(m) >= 0);
                var (count, range) = Own(set.Speeds);
                list.Add(($"{Names(set.Members, 3, RowName)}: {OszConvert.Count(count, "change", "changes")}, {range}{(inBattle ? "" : " (not in the battle)")}", set.From));
            }
        int evening = plan.Group(choices.Lanes)?.BaseScrolls.Evening ?? 0;
        list.Add((evening > 0 ? $"{NoSpeedChanges} from osu! ({Evening(evening)})" : NoSpeedChanges, null));
        return list;
    }

    /// <summary>The row of <paramref name="list"/> (from <see cref="SpeedChoices"/>) the battle has now.</summary>
    internal static int SpeedIndex(OszPlan plan, OszChoices choices, List<(string Text, OszDifficulty? From)> list)
    {
        var group = plan.Group(choices.Lanes);
        var from = choices.SpeedsFrom;
        if (group == null || from == null || !group.HasSpeedChanges(from)) return list.Count - 1;
        string tag = group.Speeds[from].Tag;
        int i = list.FindIndex(c => c.From != null && group.Speeds.TryGetValue(c.From, out var s) && s.Tag == tag);
        return i >= 0 ? i : list.Count - 1;
    }

    /// <summary>The distinct sets of speed changes among a lane group's difficulties, hardest first.</summary>
    internal static List<OszSpeedSet> SpeedSets(OszLaneGroup group, OszChoices choices)
    {
        var sets = new List<OszSpeedSet>();
        for (int i = group.Usable.Count - 1; i >= 0; i--)
        {
            var d = group.Usable[i];
            if (!group.HasSpeedChanges(d)) continue;
            var speeds = group.Speeds[d];
            var set = sets.Find(s => s.Speeds.Tag == speeds.Tag);
            if (set == null) sets.Add(set = new OszSpeedSet { Speeds = speeds });
            set.Members.Add(d);
        }
        foreach (var set in sets) set.From = set.Members.FirstOrDefault(m => choices.SlotOf(m) >= 0) ?? set.Members[0];
        return sets;
    }

    // ---- the log -------------------------------------------------------------------------------------
    // Each line goes after "Battle creator: ", like the creator's own.

    internal static string ReadingLine(string path) => $"reading the osu!mania beatmap {path} (beta import).";

    internal static string RefusedLine(string path, OszPlan plan) =>
        $"can't import {path}: {plan.Error}" + (plan.Detail != null ? $" ({BattleDraft.CleanLine(plan.Detail)})" : "");

    internal static string CancelledLine(string path) => $"the osu!mania import of {path} was cancelled.";

    /// <param name="summary">What <see cref="BattleFiles.CreateFromOsz"/> says it made.</param>
    internal static string CreatedLine(string folder, int lanes, string path, string summary) =>
        $"created {folder} ({lanes} lanes) from the osu!mania beatmap {path} (beta): {summary}.";

    /// <summary>What reading the .osz found, for the log (the creator puts "Battle creator: " before each).</summary>
    internal static List<string> LogLines(OszPlan plan)
    {
        var lines = new List<string>();
        string who = (plan.Artist.Trim().Length > 0 ? " - " + Show(plan.Artist, 100) : "") + (plan.Mapper.Trim().Length > 0 ? " by " + Show(plan.Mapper, 100) : "");
        var song = plan.Song;
        string songText = song == null ? "no song"
            : $"song {Show(Leaf(song.FileName), 60)} ({song.Seconds.ToString("0.00", Invariant)} s, shift {(song.Shift * 1000).ToString("0.0", Invariant)} ms{(song.Guessed ? ", found as the only song file" : "")})";
        lines.Add($"osu!mania beatmap {Q(plan.Title, 100)}{who}: {OszConvert.Count(plan.Difficulties.Count, "difficulty", "difficulties")}, " +
                  $"{plan.Four?.Usable.Count ?? 0} usable in 4 lanes, {plan.Five?.Usable.Count ?? 0} in 5 lanes; {songText}; read in {plan.ReadMs} ms.");
        foreach (var group in new[] { plan.Four, plan.Five })
        {
            if (group == null) continue;
            var defaults = OszChoices.Default(plan, group.Lanes);
            for (int i = group.Usable.Count - 1; i >= 0; i--)
            {
                var d = group.Usable[i];
                var c = group.Charts[d];
                int slot = defaults.SlotOf(d);
                int speeds = group.HasSpeedChanges(d) ? Own(group.Speeds[d]).Count : 0;
                lines.Add($"osu! difficulty {Q(d.Name, HintName)}: {group.Lanes}K, {OszConvert.N(c.Notes.Count)} notes ({OszConvert.N(c.Holds)} holds), " +
                          $"{OszConvert.N(c.Moved)} moved over 2 ms (worst {OszConvert.Ms(c.WorstMs)} ms), {OszConvert.Count(speeds, "speed change", "speed changes")}; " +
                          (slot >= 0 ? $"slot {ChartText.GameDifficultyLabels[slot]}." : "left out (only six slots)."));
            }
            var t = group.Timing;
            lines.Add($"osu! timing ({group.Lanes} lanes) from {Q(group.TimingFrom.Name, HintName)}: #OFFSET {t.OffsetTag}, #BPMS {Show(t.BpmsTag, 200)}; " +
                      $"{t.Fillers} filler beats, {t.RowSnaps} on the nearest 1/48 beat, {t.Effects} tempo effects, {t.Merged} merged, {t.Restated} restated.");
        }
        foreach (var d in plan.Difficulties)
            if (d.Unusable != null) lines.Add($"osu! difficulty {Q(d.Name, HintName)}: left out ({d.Unusable}).");
        if (plan.Details.Count > 0)
            lines.Add("osu! details: " + string.Join(" ", plan.Details.Take(40)) + (plan.Details.Count > 40 ? $" (and {plan.Details.Count - 40} more)" : ""));
        return lines;
    }

    // ---- small parts -------------------------------------------------------------------------------

    internal static string LaneText(int lanes) => lanes == 5 ? "5 (D F Space J K; the middle lane attacks)" : "4 (D F J K)";

    /// <summary>Whose speed changes the battle gets: the chosen difficulty's, else the filler beats' alone.</summary>
    internal static OszSpeeds ChosenSpeeds(OszLaneGroup group, OszChoices choices) =>
        choices.SpeedsFrom != null && group.Speeds.TryGetValue(choices.SpeedsFrom, out var chosen) ? chosen : group.BaseScrolls;

    /// <summary>The first and last row of the notes the battle gets; last is -1 when there are none.</summary>
    internal static (int First, int Last) IncludedRows(OszLaneGroup group, OszChoices choices)
    {
        int first = int.MaxValue, last = -1;
        foreach (var d in choices.Slots)
        {
            if (d == null || !group.Charts.TryGetValue(d, out var chart)) continue;
            var kept = OszConvert.Kept(chart, choices);
            if (kept.Count == 0) continue;
            first = Math.Min(first, kept[0].Row);
            last = Math.Max(last, kept[^1].Row);
        }
        return (first, last);
    }

    /// <summary>How far into the song the first note of the charts with a slot is (left out or not), or null when they have none.</summary>
    internal static double? FirstNoteSeconds(OszLaneGroup group, OszChoices choices)
    {
        int first = int.MaxValue;
        foreach (var d in choices.Slots)
            if (d != null && group.Charts.TryGetValue(d, out var chart) && chart.Notes.Count > 0) first = Math.Min(first, chart.FirstRow);
        return first == int.MaxValue ? null : group.Timing.Clock.RowToSeconds(first);
    }

    // The tempos osu!'s red lines set from beat 0 on, in order (fillers aren't the music's).
    private static List<double> Tempos(OszTiming timing)
    {
        var list = new List<double>();
        var sections = timing.Sections;
        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i].Filler || (i + 1 < sections.Count && sections[i + 1].Beat <= 0)) continue;
            list.Add(sections[i].Bpm);
        }
        if (list.Count == 0) list.Add(timing.MinBpm);
        return list;
    }

    private static string Bpm(double bpm) => Math.Round(bpm, 2).ToString("0.##", Invariant);

    private static string BeatZero(double offset)
    {
        if (Math.Abs(offset) < 0.0005) return "Beat 0 is at the start of the song.";
        return offset > 0
            ? $"Beat 0 is {offset.ToString("0.###", Invariant)} s before the song starts."
            : $"Beat 0 is {(-offset).ToString("0.###", Invariant)} s into the song.";
    }

    // "145 notes, 9 holds": the ones the battle gets.
    private static string Notes(OszChart chart, OszChoices choices)
    {
        int holds = Holds(chart, choices);
        return OszConvert.Count(OszConvert.KeptCount(chart, choices), "note", "notes") + (holds > 0 ? ", " + OszConvert.Count(holds, "hold", "holds") : "");
    }

    private static int Holds(OszChart chart, OszChoices choices) => chart.Holds - (choices.LeaveOutOpening ? chart.OpeningHolds : 0);

    private static string DifficultyHint(OszDifficulty d, OszChart chart, OszChoices choices)
    {
        string creator = d.File != null ? BattleDraft.CleanLine(d.File.Creator) : "";
        int holds = Holds(chart, choices);
        var hint = new StringBuilder($"{Q(d.Name)}{(creator.Length > 0 ? " by " + Show(creator, 30) : "")}: {OszConvert.Count(OszConvert.KeptCount(chart, choices), "note", "notes")}");
        hint.Append(holds > 0 ? $" ({OszConvert.Count(holds, "hold", "holds")})." : ".");
        if (chart.Moved > 0) hint.Append($" {OszConvert.Count(chart.Moved, "note", "notes")} moved to the beat grid, by at most {OszConvert.Ms(chart.WorstMs)} ms.");
        if (chart.NotOnGrid) hint.Append(" Most aren't on its beat grid: its tempo may be a placeholder.");
        return hint.ToString();
    }

    // osu!'s own speed changes in a set, and their range ("x0.75 to x1.5").
    private static (int Count, string Range) Own(OszSpeeds speeds)
    {
        string min = "x" + speeds.OwnMin.ToString("0.###", Invariant), max = "x" + speeds.OwnMax.ToString("0.###", Invariant);
        return (speeds.Own.Count, min == max ? min : $"{min} to {max}");
    }

    // "2 keep stretched beats even"
    private static string Evening(int count) =>
        count == 1 ? "1 keeps a stretched beat even" : $"{OszConvert.N(count)} keep stretched beats even";

    // "1.5"
    private static string Seconds(double seconds) => seconds.ToString("0.#", Invariant);

    // "Hard", "Normal" and 2 more
    private static string Names(List<OszDifficulty> names, int most, int max)
    {
        var shown = names.Take(most).Select(d => Q(d.Name, max)).ToList();
        if (names.Count > most) shown.Add($"{names.Count - most} more");
        return shown.Count <= 1 ? string.Concat(shown) : string.Join(", ", shown.Take(shown.Count - 1)) + " and " + shown[^1];
    }

    private static string Mb(long bytes) => (Math.Ceiling(bytes / (1024.0 * 1024.0) * 10) / 10).ToString("0.0", Invariant);

    private static long Utf8(string text) => Encoding.UTF8.GetByteCount(text);

    private static string Leaf(string path) => path.Substring(path.LastIndexOf('/') + 1);

    // A beatmap's text: control characters out, cut with "..." when long.
    private static string Show(string text, int max) => OszImport.Shorten(BattleDraft.CleanLine(text), max);

    private static string Q(string name, int max = RowName) => "\"" + Show(name, max) + "\"";

    private static string Quote(string name) => Q(name, HintName);
}
