using System.Text;

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
            int end = ValueEnd(s, colon + 1);
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

    /// <summary>
    /// Where a tag's value that starts at <paramref name="from"/> ends. Like StepMania, a value ends
    /// at ';' or at the line break before a line that starts another tag ('#' after spaces or
    /// tabs), so one missing ';' doesn't swallow the next tag. It only looks as far as that end, so
    /// reading a file takes one pass however it's laid out.
    /// </summary>
    private static int ValueEnd(string s, int from)
    {
        for (int k = from; k < s.Length; k++)
        {
            char c = s[k];
            if (c == ';') return k;
            if (c != '\n') continue;
            int j = k + 1;
            while (j < s.Length && (s[j] == ' ' || s[j] == '\t')) j++;
            if (j < s.Length && s[j] == '#') return k;
        }
        return s.Length;
    }

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

    // ---- custom battles ---------------------------------------------------------------------------

    /// <summary>The game's difficulty slot (0 Beginner to 5 Zen) a StepMania difficulty name plays in, or -1.</summary>
    internal static int SlotOf(string difficulty) =>
        Array.FindIndex(GameDifficultyNames, n => n.Equals(difficulty.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a block has at least one note. The editor saves untouched difficulties as all zeros.</summary>
    internal static bool HasNotes(NoteBlock block)
    {
        foreach (char c in block.Notes)
            if (c != '0' && c != ',' && !char.IsWhiteSpace(c)) return true;
        return false;
    }

    /// <summary>The last row with a note on the game's grid (192 rows a measure), or -1 when there is none.</summary>
    internal static int LastNoteRow(NoteBlock block)
    {
        const int rowsPerMeasure = 192;
        int last = -1, measure = 0;
        foreach (var chunk in block.Notes.Split(','))
        {
            var rows = new List<string>();
            foreach (var raw in chunk.Split('\n'))
            {
                string row = raw.Trim();
                if (row.Length > 0) rows.Add(row);
            }
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Any(c => c != '0')) last = measure * rowsPerMeasure + (int)Math.Round(i * (double)rowsPerMeasure / rows.Count);
            measure++;
        }
        return last;
    }

    /// <summary>
    /// A custom battle's blocks by the game's six difficulty slots, from each block's difficulty
    /// name in the game's own order (Edit plays as Beginner, Beginner as Novice, and so on up to
    /// Challenge as Zen). Blocks without notes are skipped, so an editor's empty difficulties don't
    /// count. A block that can't be played, has the wrong lane count or repeats a slot is left
    /// out, with the reason added to <paramref name="problems"/>.
    /// </summary>
    internal NoteBlock?[] SongSlots(int lanes, List<string> problems)
    {
        var slots = new NoteBlock?[GameDifficultyNames.Length];
        for (int i = 0; i < Blocks.Count; i++)
        {
            var block = Blocks[i];
            if (!HasNotes(block)) continue;
            int slot = SlotOf(block.Difficulty);
            if (slot < 0)
            {
                problems.Add($"\"{block.DisplayName}\" has the difficulty \"{block.Difficulty}\", which isn't one of {string.Join(", ", GameDifficultyNames)}");
                continue;
            }
            if (block.Lanes != lanes)
            {
                problems.Add($"\"{block.DisplayName}\" is {block.StepsType}, but the song has {lanes} lanes ({(lanes == 5 ? "pump-single" : "dance-single")})");
                continue;
            }
            if (slots[slot] != null)
            {
                problems.Add($"\"{block.DisplayName}\" is a second {block.Difficulty.Trim()} chart; the first one plays");
                continue;
            }
            try { Validate(i); }
            catch (InvalidDataException ex)
            {
                problems.Add(ex.Message);
                continue;
            }
            slots[slot] = block;
        }
        return slots;
    }

    /// <summary>The chart that plays in a slot: the slot's own, else the nearest one, the easier one when two are as near.</summary>
    internal static NoteBlock? NearestSlot(NoteBlock?[] slots, int slot)
    {
        for (int d = 0; d < slots.Length; d++)
        {
            if (slot - d >= 0 && slots[slot - d] != null) return slots[slot - d];
            if (slot + d < slots.Length && slots[slot + d] != null) return slots[slot + d];
        }
        return null;
    }

    /// <summary>
    /// The chart the game plays for a custom battle: this chart's header and timing with exactly
    /// six blocks, one per difficulty slot in the game's order. A slot without a chart of its own
    /// plays the nearest one, so every difficulty tab works; other blocks in the file are left out.
    /// </summary>
    internal string BuildPlayableSong(NoteBlock?[] slots)
    {
        if (slots.Length != GameDifficultyNames.Length) throw new ArgumentException("a song has six difficulty slots", nameof(slots));
        var playable = new ChartText();
        playable.Tags.AddRange(Tags);
        for (int s = 0; s < slots.Length; s++)
        {
            var block = NearestSlot(slots, s) ?? throw new InvalidDataException("the song has no playable difficulty");
            playable.Blocks.Add(block.CloneAs(GameDifficultyNames[s]));
        }
        return playable.Write();
    }
}
