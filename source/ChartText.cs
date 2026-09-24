using System.Text;
using System.Text.RegularExpressions;

namespace NocturneFlatScroll;

/// <summary>
/// Reads and writes StepMania .sm text, the format the game's own charts use. A chart file has
/// header tags (#TITLE, #BPMS, #ATTACKS and so on) and one #NOTES block per difficulty.
/// This file has no Unity or game dependencies.
/// </summary>
internal sealed class ChartText
{
    // The game's six difficulties (Beginner, Novice, Adept, Expert, Elite, Zen) read these
    // StepMania names, in this order.
    internal static readonly string[] GameDifficultyNames = { "Edit", "Beginner", "Easy", "Medium", "Hard", "Challenge" };
    internal static readonly string[] GameDifficultyLabels = { "Beginner", "Novice", "Adept", "Expert", "Elite", "Zen" };

    /// <summary>Header tags in file order, with their raw values (without the trailing ';').</summary>
    internal readonly List<KeyValuePair<string, string>> Tags = new();
    internal readonly List<NoteBlock> Blocks = new();

    internal sealed class NoteBlock
    {
        internal string StepsType = "dance-single";
        internal string Description = "";
        internal string Difficulty = "Challenge";
        internal string Meter = "1";
        internal string Radar = "0,0,0,0,0";
        internal string Notes = "";

        internal int Lanes => StepsType.Trim().ToLowerInvariant() switch
        {
            "dance-single" => 4,
            "pump-single" => 5,
            "dance-solo" => 6,
            "dance-double" => 8,
            _ => 0
        };

        /// <summary>A readable name: the block's description, or its difficulty and level.</summary>
        internal string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Description)) return Description.Trim();
                int index = Array.FindIndex(GameDifficultyNames, n => n.Equals(Difficulty.Trim(), StringComparison.OrdinalIgnoreCase));
                string name = index >= 0 ? GameDifficultyLabels[index] : Difficulty.Trim();
                return $"{name} {Meter.Trim()}";
            }
        }

        internal NoteBlock CloneAs(string difficulty) => new()
        {
            StepsType = StepsType, Description = Description, Difficulty = difficulty,
            Meter = Meter, Radar = Radar, Notes = Notes
        };
    }

    internal string? GetTag(string name)
    {
        foreach (var tag in Tags)
            if (tag.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) return tag.Value;
        return null;
    }

    internal void SetTag(string name, string value)
    {
        for (int i = 0; i < Tags.Count; i++)
        {
            if (!Tags[i].Key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            Tags[i] = new KeyValuePair<string, string>(Tags[i].Key, value);
            return;
        }
        Tags.Add(new KeyValuePair<string, string>(name, value));
    }

    /// <summary>Parses .sm text. Comments (//) are dropped; unknown tags are kept as they are.</summary>
    internal static ChartText Parse(string text)
    {
        var chart = new ChartText();
        var clean = new StringBuilder(text.Length);
        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            int comment = rawLine.IndexOf("//", StringComparison.Ordinal);
            clean.Append(comment >= 0 ? rawLine.Substring(0, comment) : rawLine).Append('\n');
        }
        string s = clean.ToString();
        int i = 0;
        while (true)
        {
            int start = s.IndexOf('#', i);
            if (start < 0) break;
            int colon = s.IndexOf(':', start);
            if (colon < 0) break;
            // Like StepMania, a value ends at ';' or where a new line starts another tag, so one
            // missing ';' doesn't swallow the next tag.
            int end = s.IndexOf(';', colon);
            if (end < 0) end = s.Length;
            var next = NextTag.Match(s, colon);
            if (next.Success && next.Index < end) end = next.Index;
            string key = s.Substring(start + 1, colon - start - 1).Trim();
            string value = s.Substring(colon + 1, end - colon - 1);
            i = end < s.Length && s[end] == ';' ? end + 1 : end;
            if (key.Equals("NOTES", StringComparison.OrdinalIgnoreCase))
            {
                var parts = value.Split(':');
                if (parts.Length < 6) continue;
                chart.Blocks.Add(new NoteBlock
                {
                    StepsType = parts[0].Trim(),
                    Description = parts[1].Trim(),
                    Difficulty = parts[2].Trim(),
                    Meter = parts[3].Trim(),
                    Radar = parts[4].Trim(),
                    Notes = string.Join(":", parts, 5, parts.Length - 5).Trim()
                });
            }
            else chart.Tags.Add(new KeyValuePair<string, string>(key, value.Trim()));
        }
        return chart;
    }

    private static readonly Regex NextTag = new(@"\n[ \t]*#", RegexOptions.Compiled);

    /// <summary>Throws with a readable reason when a block can't be played.</summary>
    internal void Validate(int blockIndex)
    {
        var block = Blocks[blockIndex];
        int lanes = block.Lanes;
        if (lanes == 0) throw new InvalidDataException($"\"{block.DisplayName}\" has an unknown chart type ({block.StepsType})");
        string? bpms = GetTag("BPMS");
        var first = bpms?.Split(',')[0].Split('=');
        if (first == null || first.Length != 2 || !double.TryParse(first[1].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double bpm) || !(bpm > 0))
            throw new InvalidDataException("the chart has no usable #BPMS");
        int rows = 0;
        foreach (var raw in block.Notes.Split('\n'))
        {
            string row = raw.Trim();
            if (row.Length == 0 || row == ",") continue;
            if (row.EndsWith(",")) row = row.Substring(0, row.Length - 1).Trim();
            if (row.Length == 0) continue;
            if (row.Length != lanes) throw new InvalidDataException($"\"{block.DisplayName}\" has a note row \"{row}\" that isn't {lanes} lanes wide");
            rows++;
        }
        if (rows == 0) throw new InvalidDataException($"\"{block.DisplayName}\" has no notes");
    }

    internal string Write()
    {
        var sb = new StringBuilder();
        foreach (var tag in Tags) sb.Append('#').Append(tag.Key).Append(':').Append(tag.Value).Append(";\n");
        foreach (var block in Blocks)
        {
            sb.Append("\n//--------------- ").Append(block.StepsType).Append(" - ").Append(block.Description).Append(" ----------------\n");
            sb.Append("#NOTES:\n");
            sb.Append("     ").Append(block.StepsType).Append(":\n");
            sb.Append("     ").Append(block.Description).Append(":\n");
            sb.Append("     ").Append(block.Difficulty).Append(":\n");
            sb.Append("     ").Append(block.Meter).Append(":\n");
            sb.Append("     ").Append(block.Radar).Append(":\n");
            sb.Append(block.Notes.Trim('\n')).Append("\n;\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// The chart the game plays for a custom difficulty: this chart's header and timing with the
    /// chosen block in every difficulty slot, so it plays whatever difficulty the player has set.
    /// When <paramref name="songEvents"/> is given, its #ATTACKS replace this chart's, which keeps
    /// the song's own enemy events.
    /// </summary>
    internal string BuildPlayable(int blockIndex, ChartText? songEvents)
    {
        var playable = new ChartText();
        playable.Tags.AddRange(Tags);
        if (songEvents != null) playable.SetTag("ATTACKS", songEvents.GetTag("ATTACKS") ?? "");
        var block = Blocks[blockIndex];
        foreach (var name in GameDifficultyNames) playable.Blocks.Add(block.CloneAs(name));
        return playable.Write();
    }
}
