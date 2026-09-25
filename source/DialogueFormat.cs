using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NocturneFlatScroll;

// ---- battle.json's "dialogue", as written ----------------------------------------------------------

/// <summary>
/// battle.json's "dialogue" (or the JSON file it names): lines in four sections, and the battle's
/// own speakers by key. These are the keys as written; <see cref="DialogueReader"/> reads them one
/// line and one speaker at a time, so a mistake in one costs only that one.
/// </summary>
internal sealed class DialogueDefinition
{
    public Dictionary<string, DialogueSpeakerDefinition>? speakers { get; set; }
    public List<DialogueLineDefinition>? before { get; set; }
    public List<DialogueLineDefinition>? during { get; set; }
    public List<DialogueLineDefinition>? afterWin { get; set; }
    public List<DialogueLineDefinition>? afterLoss { get; set; }
}

/// <summary>One of the battle's own speakers: a name, a picture, and more pictures for its expressions.</summary>
internal sealed class DialogueSpeakerDefinition
{
    public string? name { get; set; }
    // The default face: a PNG or JPEG inside the battle, normally in portraits/.
    public string? portrait { get; set; }
    // More faces, by a name the battle's lines use.
    public Dictionary<string, string>? expressions { get; set; }
    // "left" or "right"; right when missing.
    public string? side { get; set; }
    // Mirrors the picture (the game already mirrors every right-side portrait).
    public bool flip { get; set; }
    // [x, y] in game pixels.
    public double[]? offset { get; set; }
}

/// <summary>One line of dialogue as written.</summary>
internal sealed class DialogueLineDefinition
{
    // A key of "speakers", "Narrator", or a game character's id in any letter case.
    public string? speaker { get; set; }
    public string? expression { get; set; }
    public string? text { get; set; }
    // The name tag for this line only.
    public string? name { get; set; }
    public string? side { get; set; }
    // Seconds: a blocking line goes on by itself after this long; a live line shows this long.
    public double? duration { get; set; }
    // "during" only: seconds on the song's clock, or a beat instead.
    public double? time { get; set; }
    public double? beat { get; set; }
    // "during" only: the song stops for this line.
    public bool pause { get; set; }
}

// ---- the dialogue, checked ------------------------------------------------------------------------

internal enum DialogueSection { Before, During, AfterWin, AfterLoss }

internal enum DialogueSpeakerKind { Custom, Narrator, Game }

/// <summary>Where a speaker stands in the box; the same numbers as the game's speaker positions (Left 0, Right 1).</summary>
internal enum DialogueSide { Left, Right }

/// <summary>One picture of a custom speaker, checked: its name ("" for the default face), its file inside the battle, and its size in pixels.</summary>
internal sealed class DialogueFace
{
    internal string Name = "";
    internal string File = "";
    internal int Width, Height;
    internal long Bytes;
}

/// <summary>One of the battle's own speakers, checked.</summary>
internal sealed class DialogueSpeaker
{
    /// <summary>Its key in "speakers", as written first; lines find it in any letter case.</summary>
    internal string Key = "";
    /// <summary>The name tag (the key when it has no name).</summary>
    internal string Name = "";
    internal DialogueSide Side = DialogueSide.Right;
    internal bool Flip;
    /// <summary>A nudge of the picture, in game pixels.</summary>
    internal double OffsetX, OffsetY;
    /// <summary>The default face; null when it has no picture that can be shown (it then shows without one).</summary>
    internal DialogueFace? Portrait;
    /// <summary>The other faces, in the order written. Only a speaker with a default face has any.</summary>
    internal readonly List<DialogueFace> Expressions = new();

    /// <summary>The default face first, then the others.</summary>
    internal IEnumerable<DialogueFace> Faces => Portrait == null ? Expressions : new[] { Portrait }.Concat(Expressions);

    /// <summary>An expression by its name in any letter case; null for an unknown one.</summary>
    internal DialogueFace? Expression(string? name) =>
        name == null ? null : Expressions.FirstOrDefault(f => f.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>One line, checked and ready to play.</summary>
internal sealed class DialogueLine
{
    internal DialogueSection Section;
    /// <summary>Where the line is in its section in battle.json, from 0. Lines that are left out keep their places, so this finds the line to edit.</summary>
    internal int Index;
    internal DialogueSpeakerKind SpeakerKind;
    /// <summary>A custom speaker's key (as in <see cref="BattleDialogueData.Speakers"/>), "Narrator", or a game character's id as written (the runtime finds it in any letter case).</summary>
    internal string Speaker = "";
    /// <summary>
    /// The face: a custom speaker's expression name as its speaker has it, or a game character's
    /// Emotions name as written (the runtime checks it). Null for the default face.
    /// </summary>
    internal string? Face;
    /// <summary>The line's own side; null for the speaker's (<see cref="BattleDialogueData.SideOf"/>).</summary>
    internal DialogueSide? Side;
    /// <summary>The line's own name tag; null for the speaker's (<see cref="BattleDialogueData.NameOf"/>).</summary>
    internal string? Name;
    /// <summary>At most 150 letters, one line, without &lt; &gt; { or }.</summary>
    internal string Text = "";
    /// <summary>
    /// Before, after and stopping lines: they go on by themselves after this many seconds (a key
    /// can't skip them); null waits for a key. Live lines: how long they show; null works it out
    /// from the text (<see cref="ShowSeconds"/>).
    /// </summary>
    internal double? Duration;
    /// <summary>During only: seconds on the song's clock (the chart editor's clock; beat 0 is at -#OFFSET).</summary>
    internal double Time;
    /// <summary>During only: the beat, when the line was placed on one (it moves with the notes when the timing changes).</summary>
    internal double? Beat;
    /// <summary>During only: the song stops for this line, like a boss's talk between its songs.</summary>
    internal bool Pause;

    /// <summary>A line during the song that doesn't stop it: the box comes and goes by itself.</summary>
    internal bool IsLive => Section == DialogueSection.During && !Pause;

    /// <summary>How long a live line shows: its duration, else worked out from its length.</summary>
    internal double ShowSeconds => Duration ?? DialogueReader.LiveSeconds(Text);
}

/// <summary>One problem with the dialogue, where it is, in the loader's words and in the battle creator's.</summary>
internal sealed class DialogueProblem
{
    /// <summary>The section it's about; null for the dialogue as a whole or its speakers.</summary>
    internal DialogueSection? Section;
    /// <summary>The line it's about (its place in the section, from 0), if one.</summary>
    internal int? Index;
    /// <summary>The key of the speaker it's about, if one.</summary>
    internal string? Speaker;
    /// <summary>As BattlePackage.Problems has it: battle.json's keys by name, for the log.</summary>
    internal string Text = "";
    /// <summary>In the battle creator's words (its page and buttons, no JSON keys).</summary>
    internal string Plain = "";
}

/// <summary>
/// A custom battle's dialogue, checked (see <see cref="DialogueReader"/>). Never null on a
/// package: a battle without dialogue has empty sections.
/// </summary>
internal sealed class BattleDialogueData
{
    private readonly List<DialogueSpeaker> speakers = new();
    private readonly Dictionary<string, DialogueSpeaker> byKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The battle's own speakers that can be used, in the order written.</summary>
    internal IReadOnlyList<DialogueSpeaker> Speakers => speakers;
    internal readonly List<DialogueLine> Before = new();
    /// <summary>In time order; lines at the same time keep the order written.</summary>
    internal readonly List<DialogueLine> During = new();
    internal readonly List<DialogueLine> AfterWin = new();
    internal readonly List<DialogueLine> AfterLoss = new();
    /// <summary>Also in the package's Problems (as <see cref="DialogueProblem.Text"/>).</summary>
    internal readonly List<DialogueProblem> Problems = new();

    internal bool HasAny => Before.Count + During.Count + AfterWin.Count + AfterLoss.Count > 0;

    internal List<DialogueLine> Lines(DialogueSection section) => section switch
    {
        DialogueSection.Before => Before,
        DialogueSection.During => During,
        DialogueSection.AfterWin => AfterWin,
        _ => AfterLoss,
    };

    /// <summary>A custom speaker by its key in any letter case.</summary>
    internal DialogueSpeaker? FindSpeaker(string? key) =>
        key != null && byKey.TryGetValue(key.Trim(), out var speaker) ? speaker : null;

    internal void AddSpeaker(DialogueSpeaker speaker)
    {
        speakers.Add(speaker);
        byKey[speaker.Key] = speaker;
    }

    /// <summary>A line's custom speaker; null for the Narrator and game characters.</summary>
    internal DialogueSpeaker? SpeakerOf(DialogueLine line) => line.SpeakerKind == DialogueSpeakerKind.Custom ? FindSpeaker(line.Speaker) : null;

    /// <summary>Where a line's speaker stands: the line's side, else the custom speaker's, else the game character's usual one.</summary>
    internal DialogueSide SideOf(DialogueLine line) => line.Side ?? line.SpeakerKind switch
    {
        DialogueSpeakerKind.Custom => SpeakerOf(line)?.Side ?? DialogueSide.Right,
        DialogueSpeakerKind.Game => DialogueReader.GameSide(line.Speaker),
        _ => DialogueSide.Right,
    };

    /// <summary>
    /// A line's name tag when the battle sets it: the line's own, else the custom speaker's. Null
    /// for game characters and the Narrator, whose names come from the game.
    /// </summary>
    internal string? NameOf(DialogueLine line) => line.Name ?? SpeakerOf(line)?.Name;

    /// <summary>The game characters the lines use, each once (in any letter case), in the order they first speak.</summary>
    internal List<string> GameSpeakers()
    {
        var ids = new List<string>();
        foreach (var line in Before.Concat(During).Concat(AfterWin).Concat(AfterLoss))
            if (line.SpeakerKind == DialogueSpeakerKind.Game && !ids.Contains(line.Speaker, StringComparer.OrdinalIgnoreCase)) ids.Add(line.Speaker);
        return ids;
    }

    /// <summary>"3 before, 7 during (2 stop the song), 2 after a win, 2 after a loss", for the log.</summary>
    internal string Counts()
    {
        int stops = During.Count(l => l.Pause);
        return $"{Before.Count} before, {During.Count} during{(stops > 0 ? $" ({stops} stop the song)" : "")}, {AfterWin.Count} after a win, {AfterLoss.Count} after a loss";
    }
}

/// <summary>
/// Reads and checks battle.json's "dialogue": an object, or the name of a JSON file inside the
/// battle holding one. It never refuses the battle: everything wrong is noted in the package's
/// problems (in the loader's words, and in the battle creator's in <see cref="BattleDialogueData.Problems"/>),
/// and the line or speaker is fixed up or left out. Beats become seconds with the chart's own
/// timing. Unknown keys are ignored (the battle creator keeps them). Only the start of each
/// picture is read here; the runtime decodes them. This file has no Unity or game dependencies.
/// </summary>
internal static class DialogueReader
{
    internal const string Narrator = "Narrator";
    internal const int MaxText = 150, MaxName = 24, MaxKey = 24;
    internal const int MaxSpeakers = 12, MaxExpressions = 24;
    internal const double MinDuration = 1, MaxDuration = 15;
    /// <summary>How far a speaker's "offset" may nudge its picture, in game pixels.</summary>
    internal const double MaxOffset = 120;
    internal const long MaxPortraitBytes = 4L * 1024 * 1024;
    internal const long MaxPortraitsBytes = 64L * 1024 * 1024;
    internal const int MaxPortraitSide = 2048;
    /// <summary>How much of each picture is read to check it.</summary>
    internal const int HeadBytes = 256 * 1024;
    /// <summary>An expression whose shape is further than this from the default face's shows stretched.</summary>
    internal const double ShapeTolerance = 0.02;
    /// <summary>A line that stops the song with a note this soon after it is noted.</summary>
    internal const double BreakNoteBeats = 2;

    internal static int MaxLines(DialogueSection section) => section switch
    {
        DialogueSection.Before => 60,
        DialogueSection.During => 200,
        _ => 30,
    };

    /// <summary>The section's key in battle.json.</summary>
    internal static string Key(DialogueSection section) => section switch
    {
        DialogueSection.Before => "before",
        DialogueSection.During => "during",
        DialogueSection.AfterWin => "afterWin",
        _ => "afterLoss",
    };

    /// <summary>"before the fight", "during the song", "after a win", "after a loss".</summary>
    internal static string SectionName(DialogueSection section) => section switch
    {
        DialogueSection.Before => "before the fight",
        DialogueSection.During => "during the song",
        DialogueSection.AfterWin => "after a win",
        _ => "after a loss",
    };

    /// <summary>
    /// A line as the battle creator names it: "line 3 before the fight" (index from 0), or for a
    /// line during the song with a time, "the line at 0:42.50".
    /// </summary>
    internal static string LineName(DialogueSection section, int index, double? time = null) =>
        section == DialogueSection.During && time is double t ? $"the line at {Clock(t)}" : $"line {index + 1} {SectionName(section)}";

    /// <summary>Seconds as the dialogue shows them: "0:42.50".</summary>
    internal static string Clock(double seconds)
    {
        long hundredths = (long)Math.Round(Math.Max(0, seconds) * 100);
        return $"{hundredths / 6000}:{hundredths % 6000 / 100:00}.{hundredths % 100:00}";
    }

    /// <summary>How long a live line without a duration shows: 2.5 s for 20 letters, 4.5 s for 60, at most 8 s.</summary>
    internal static double LiveSeconds(string text) => Math.Clamp(1.5 + 0.05 * text.Length, 2.5, 8);

    /// <summary>A game character's usual side: Karma and Young Karma stand left, everyone else right.</summary>
    internal static DialogueSide GameSide(string id) =>
        id.Trim().Equals("Karma", StringComparison.OrdinalIgnoreCase) || id.Trim().Equals("Young Karma", StringComparison.OrdinalIgnoreCase)
            ? DialogueSide.Left : DialogueSide.Right;

    /// <summary>
    /// How a custom speaker's default face shows in the game's box: its size in game pixels, game
    /// pixels per picture pixel, and whether it is drawn sharp (a whole number of them). A picture
    /// up to 256 x 240 shows pixel for pixel, and a tiny one (under 96 tall) at a whole number up
    /// to 4x; a bigger one is scaled down to fit 256 x 240. Every other face shows at this size.
    /// </summary>
    internal static (double Width, double Height, double Scale, bool Sharp) ShownSize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        double k = 1;
        if (width > 256 || height > 240) k = Math.Min(256.0 / width, 240.0 / height);
        else if (height < 96) k = Math.Max(1, Math.Min(4, Math.Floor(160.0 / height)));
        return (width * k, height * k, k, k == Math.Floor(k));
    }

    /// <summary>
    /// Text the game's box can show on one line: line breaks become spaces, and &lt; &gt; { } are
    /// taken out (the box reads &lt;...&gt; as tags and {x} as a value to fill in).
    /// <paramref name="removed"/> says whether any were.
    /// </summary>
    internal static string CleanText(string? text, out bool removed)
    {
        var sb = new StringBuilder();
        removed = false;
        foreach (char c in (text ?? "").Replace("\r\n", " "))
        {
            if (c is '<' or '>' or '{' or '}') removed = true;
            else sb.Append(c is '\r' or '\n' or '\t' ? ' ' : c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Whether a speaker key can be used: 1 to 24 letters, digits, spaces, '-' and '_'.</summary>
    internal static bool IsSpeakerKey(string key) =>
        key.Trim().Length is > 0 and <= MaxKey && key.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_');

    /// <summary>
    /// Reads "dialogue" (an object, or a JSON file inside the battle). <paramref name="slots"/> are
    /// the chart's playable difficulties, for the checks against its notes. Each file is passed to
    /// <paramref name="stamp"/> before it's read (a missing one too), so the package's fingerprint
    /// covers them.
    /// </summary>
    internal static BattleDialogueData Read(JsonElement dialogue, PackageFiles files, ChartText chart, IReadOnlyList<ChartText.NoteBlock?> slots,
        List<string> problems, Action<string> stamp)
    {
        var reading = new Reading(files, chart, slots, problems, stamp);
        JsonElement root;
        try { root = Open(dialogue, files, stamp); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            reading.Note(null, null, null, $"the dialogue couldn't be read ({ex.Message}); the battle plays without it");
            return reading.Data;
        }
        if (root.ValueKind == JsonValueKind.Undefined) return reading.Data;
        reading.ReadSpeakers(Prop(root, "speakers"));
        foreach (var section in new[] { DialogueSection.Before, DialogueSection.During, DialogueSection.AfterWin, DialogueSection.AfterLoss })
            reading.ReadSection(section, Prop(root, Key(section)));
        reading.CheckDuring();
        return reading.Data;
    }

    // The dialogue object: inline, or from its file. Undefined when there is none.
    private static JsonElement Open(JsonElement dialogue, PackageFiles files, Action<string> stamp)
    {
        switch (dialogue.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return default;
            case JsonValueKind.Object:
                return dialogue;
            case JsonValueKind.String:
                string path = PackageFiles.SafeName(dialogue.GetString() ?? "") ?? throw new InvalidDataException("\"dialogue\" must be a file inside the battle");
                stamp(path);
                if (!files.Exists(path)) throw new InvalidDataException($"{path} is missing");
                using (var doc = JsonDocument.Parse(files.ReadAllText(path, BattlePackage.MaxJsonBytes),
                           new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{path} doesn't hold a JSON object");
                    return doc.RootElement.Clone();
                }
            default:
                throw new InvalidDataException("\"dialogue\" must be an object or the name of a JSON file");
        }
    }

    // One reading's state: what's read so far, and what the checks need.
    private sealed class Reading
    {
        internal readonly BattleDialogueData Data = new();
        private readonly PackageFiles files;
        private readonly ChartText chart;
        private readonly IReadOnlyList<ChartText.NoteBlock?> slots;
        private readonly List<string> problems;
        private readonly Action<string> stamp;
        // Speakers that were written but left out: their lines show as the Narrator.
        private readonly HashSet<string> dropped = new(StringComparer.OrdinalIgnoreCase);
        // Expressions that were written but left out, by speaker: noted once, not again for each line.
        private readonly HashSet<string> droppedFaces = new(StringComparer.OrdinalIgnoreCase);
        // Pictures counted towards the limit for all of them, and whether it was reached.
        private readonly HashSet<string> counted = new(StringComparer.OrdinalIgnoreCase);
        private long pictureBytes;
        private bool picturesFull;
        private EditorChart? timing;
        private List<int>? noteRows;

        internal Reading(PackageFiles files, ChartText chart, IReadOnlyList<ChartText.NoteBlock?> slots, List<string> problems, Action<string> stamp)
        {
            this.files = files;
            this.chart = chart;
            this.slots = slots;
            this.problems = problems;
            this.stamp = stamp;
        }

        internal void Note(DialogueSection? section, int? index, string? speaker, string text, string? plain = null)
        {
            problems.Add(text);
            Data.Problems.Add(new DialogueProblem { Section = section, Index = index, Speaker = speaker, Text = text, Plain = plain ?? text });
        }

        // The chart's timing, read once: beats to seconds, and where the battle ends.
        private EditorChart Timing
        {
            get
            {
                if (timing != null) return timing;
                timing = new EditorChart(4);
                timing.ReadTiming(chart);
                return timing;
            }
        }

        // ---- speakers --------------------------------------------------------------------------

        internal void ReadSpeakers(JsonElement node)
        {
            if (node.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;
            if (node.ValueKind != JsonValueKind.Object)
            {
                Note(null, null, null, "the dialogue's \"speakers\" must be an object, so the battle's own speakers are left out and their lines show as the Narrator",
                    "the battle's own speakers couldn't be read, so their lines show as the Narrator");
                return;
            }
            var extra = new List<string>();
            foreach (var p in node.EnumerateObject())
            {
                string key = p.Name.Trim();
                if (!IsSpeakerKey(key))
                {
                    dropped.Add(key);
                    Note(null, null, key, $"the speaker key \"{key}\" must be 1 to {MaxKey} letters, digits, spaces, - or _, so it's left out and its lines show as the Narrator",
                        $"the speaker \"{key}\" can't be used (its key must be 1 to {MaxKey} letters, digits, spaces, - or _), so its lines show as the Narrator");
                    continue;
                }
                if (key.Equals(Narrator, StringComparison.OrdinalIgnoreCase))
                {
                    Note(null, null, key, $"a speaker can't have the key \"{key}\" (that is the Narrator), so it's left out",
                        $"a speaker of the battle's own can't be called \"{key}\" (that is the Narrator), so it's left out");
                    continue;
                }
                if (Data.FindSpeaker(key) is { } first)
                {
                    Note(null, null, key, first.Key == key
                        ? $"the speaker \"{key}\" is written twice, so the second is left out"
                        : $"the speakers \"{first.Key}\" and \"{key}\" differ only in letter case, so \"{key}\" is left out");
                    continue;
                }
                if (Data.Speakers.Count >= MaxSpeakers)
                {
                    dropped.Add(key);
                    extra.Add(key);
                    continue;
                }
                if (ReadSpeaker(key, p.Value) is { } speaker) Data.AddSpeaker(speaker);
                else dropped.Add(key);
            }
            if (extra.Count > 0)
                Note(null, null, null, $"the dialogue has {MaxSpeakers + extra.Count} speakers; only the first {MaxSpeakers} are used, so the lines of {string.Join(", ", extra.Select(k => $"\"{k}\""))} show as the Narrator");
        }

        private DialogueSpeaker? ReadSpeaker(string key, JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                Note(null, null, key, $"the speaker \"{key}\" must be an object with a \"name\" and a \"portrait\", so its lines show as the Narrator",
                    $"the speaker \"{key}\" couldn't be read, so its lines show as the Narrator");
                return null;
            }
            string where = $"the speaker \"{key}\"";
            var def = new DialogueSpeakerDefinition
            {
                name = Text(node, "name", where, "name", null, key),
                portrait = Text(node, "portrait", where, "picture", null, key),
                side = Text(node, "side", where, "side", null, key),
                flip = Bool(node, "flip", where, "mirror setting", null, key),
            };
            var speaker = new DialogueSpeaker { Key = key, Flip = def.flip };

            // The name tag.
            string name = CleanText(def.name, out bool removed);
            if (removed) Note(null, null, key, $"{where}: its \"name\" had < > {{ or }}, which the game's box can't show; they're taken out",
                $"the speaker {key}'s name had < > {{ or }}, which the game's box can't show; they're taken out");
            if (name.Length > MaxName)
            {
                Note(null, null, key, $"{where}: its \"name\" is longer than {MaxName} letters, so it's cut", $"the speaker {key}'s name is longer than {MaxName} letters, so it's cut");
                name = name.Substring(0, MaxName).TrimEnd();
            }
            if (name.Length == 0)
            {
                Note(null, null, key, $"{where} has no \"name\", so \"{key}\" shows", $"the speaker {key} has no name, so \"{key}\" shows");
                name = key;
            }
            speaker.Name = name;
            string who = name;

            // Where it stands, and a nudge.
            if (def.side != null)
            {
                if (Side(def.side) is DialogueSide side) speaker.Side = side;
                else Note(null, null, key, $"{where}: \"side\" must be \"left\" or \"right\", so it stands on the right", $"{who}'s side must be left or right, so {who} stands on the right");
            }
            var offset = Prop(node, "offset");
            if (offset.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                if (offset.ValueKind != JsonValueKind.Array || offset.GetArrayLength() != 2 || !TryNumber(offset[0], out double x) || !TryNumber(offset[1], out double y))
                    Note(null, null, key, $"{where}: \"offset\" must be two numbers, like [0, 10], so it isn't used", $"{who}'s nudge couldn't be read, so it isn't used");
                else
                {
                    if (Math.Abs(x) > MaxOffset || Math.Abs(y) > MaxOffset)
                        Note(null, null, key, $"{where}: \"offset\" is kept within {MaxOffset:0} game pixels", $"{who}'s nudge is kept within {MaxOffset:0} game pixels");
                    speaker.OffsetX = Math.Clamp(x, -MaxOffset, MaxOffset);
                    speaker.OffsetY = Math.Clamp(y, -MaxOffset, MaxOffset);
                }
            }

            // The default face, then the others (only with a default face to size them by).
            if (def.portrait == null || def.portrait.Trim().Length == 0)
            {
                Note(null, null, key, $"{where} has no \"portrait\", so it shows without a picture", $"{who} has no picture, so {who} shows without one");
                return speaker;
            }
            speaker.Portrait = Picture(def.portrait, "", key, where, who);
            var expressions = Prop(node, "expressions");
            if (expressions.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return speaker;
            if (expressions.ValueKind != JsonValueKind.Object)
            {
                Note(null, null, key, $"{where}: \"expressions\" must be an object of names and files, so they're left out", $"{who}'s expressions couldn't be read, so they're left out");
                return speaker;
            }
            if (speaker.Portrait == null)
            {
                Note(null, null, key, $"{where}: its expressions aren't used either, since its \"portrait\" can't be", $"{who}'s expressions aren't used either, since its picture can't be");
                return speaker;
            }
            int written = 0;
            foreach (var e in expressions.EnumerateObject())
            {
                string face = e.Name.Trim();
                // Taken back out below when it's used.
                droppedFaces.Add(key + "/" + face);
                if (++written > MaxExpressions)
                {
                    Note(null, null, key, $"{where} has more than {MaxExpressions} expressions; only the first {MaxExpressions} are used", $"{who} has more than {MaxExpressions} expressions; only the first {MaxExpressions} are used");
                    break;
                }
                if (face.Length == 0 || face.Length > MaxName)
                {
                    Note(null, null, key, $"{where}: the expression name \"{face}\" must be 1 to {MaxName} letters, so it's left out", $"{who}'s expression \"{face}\" needs a name of 1 to {MaxName} letters, so it's left out");
                    continue;
                }
                if (speaker.Expression(face) is { } same)
                {
                    Note(null, null, key, $"{where}: the expressions \"{same.Name}\" and \"{face}\" differ only in letter case, so \"{face}\" is left out",
                        $"{who}'s expressions \"{same.Name}\" and \"{face}\" differ only in letter case, so \"{face}\" is left out");
                    continue;
                }
                if (e.Value.ValueKind != JsonValueKind.String)
                {
                    Note(null, null, key, $"{where}: the expression \"{face}\" must name a picture file, so it's left out", $"{who}'s expression \"{face}\" has no picture, so it's left out");
                    continue;
                }
                if (Picture(e.Value.GetString() ?? "", face, key, where, who) is not { } picture) continue;
                speaker.Expressions.Add(picture);
                droppedFaces.Remove(key + "/" + face);
                // Every face shows at the default face's size, so one of another shape is stretched.
                double wanted = speaker.Portrait.Width / (double)speaker.Portrait.Height, shape = picture.Width / (double)picture.Height;
                if (Math.Abs(shape / wanted - 1) > ShapeTolerance)
                    Note(null, null, key, $"{where}: the expression \"{face}\" ({picture.Width} x {picture.Height}) isn't the shape of its \"portrait\" ({speaker.Portrait.Width} x {speaker.Portrait.Height}), so it shows stretched to that shape",
                        $"{who}'s expression \"{face}\" ({picture.Width} x {picture.Height}) isn't the shape of the default picture ({speaker.Portrait.Width} x {speaker.Portrait.Height}), so it shows stretched");
            }
            return speaker;
        }

        // A face's file, checked: inside the battle, a PNG or JPEG of at most 4 MB and 2048 px a
        // side, within the limit for all pictures. Null (noted) when it can't be used.
        private DialogueFace? Picture(string written, string face, string key, string where, string who)
        {
            string what = face.Length == 0 ? "\"portrait\"" : $"expression \"{face}\"";
            string plainWhat = face.Length == 0 ? $"{who}'s picture" : $"{who}'s expression \"{face}\"";
            string outcome = face.Length == 0 ? "it shows without a picture" : "it's left out and its lines show the default picture";
            string plainOutcome = face.Length == 0 ? $"{who} shows without a picture" : "it's left out and its lines show the default picture";
            string? file = PackageFiles.SafeName(written);
            if (file == null)
            {
                Note(null, null, key, $"{where}: the {what} names \"{written.Trim()}\", which isn't a file inside the battle, so {outcome}",
                    $"{plainWhat} isn't a file inside the battle, so {plainOutcome}");
                return null;
            }
            // A missing picture is stamped too, so the battle is loaded again when it turns up.
            stamp(file);
            string? why = null;
            var picture = new DialogueFace { Name = face, File = file };
            try
            {
                if (!files.Exists(file)) why = "is missing";
                else
                {
                    picture.Bytes = files.Length(file);
                    if (picture.Bytes > MaxPortraitBytes)
                        why = $"is {(picture.Bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture)} MB; speaker pictures can be at most {MaxPortraitBytes / (1024 * 1024)} MB";
                    else
                    {
                        byte[] head = files.ReadHead(file, HeadBytes);
                        why = MediaSniff.PictureProblem(head, MaxPortraitSide, "speaker pictures");
                        if (why == null)
                        {
                            var info = MediaSniff.Probe(head, picture.Bytes <= head.Length);
                            picture.Width = info.Width;
                            picture.Height = info.Height;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                why = $"couldn't be read ({ex.Message})";
            }
            if (why != null)
            {
                Note(null, null, key, $"{where}: the {what} {file} {why}, so {outcome}", $"{plainWhat} {file} {why}, so {plainOutcome}");
                return null;
            }
            // All the battle's pictures together have a limit; each file counts once.
            if (counted.Contains(file)) return picture;
            if (picturesFull || pictureBytes + picture.Bytes > MaxPortraitsBytes)
            {
                if (!picturesFull)
                    Note(null, null, null, $"the speakers' pictures come to more than {MaxPortraitsBytes / (1024 * 1024)} MB, so {file} and the pictures after it are left out");
                picturesFull = true;
                return null;
            }
            counted.Add(file);
            pictureBytes += picture.Bytes;
            return picture;
        }

        // ---- lines -----------------------------------------------------------------------------

        internal void ReadSection(DialogueSection section, JsonElement node)
        {
            if (node.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;
            string name = SectionName(section);
            if (node.ValueKind != JsonValueKind.Array)
            {
                Note(section, null, null, $"the dialogue's \"{Key(section)}\" must be a list of lines, so the lines {name} are left out",
                    $"the lines {name} couldn't be read, so they're left out");
                return;
            }
            int count = node.GetArrayLength(), max = MaxLines(section);
            if (count > max) Note(section, null, null, $"there are {count} lines {name}; only the first {max} are used");
            var lines = Data.Lines(section);
            int index = 0;
            foreach (var item in node.EnumerateArray())
            {
                if (index >= max) break;
                if (ReadLine(section, index, item) is { } line) lines.Add(line);
                index++;
            }
            // By time; OrderBy keeps lines at the same time in the order written.
            if (section == DialogueSection.During)
            {
                var sorted = lines.OrderBy(l => l.Time).ToList();
                lines.Clear();
                lines.AddRange(sorted);
            }
        }

        private DialogueLine? ReadLine(DialogueSection section, int index, JsonElement node)
        {
            string where = LineName(section, index);
            if (node.ValueKind != JsonValueKind.Object)
            {
                Note(section, index, null, $"{where} must be an object with a \"speaker\" and a \"text\", so it's left out", $"{where} couldn't be read, so it's left out");
                return null;
            }
            var line = new DialogueLine { Section = section, Index = index };
            bool during = section == DialogueSection.During;
            double? time = Number(node, "time", section, index, where, "time");
            double? beat = Number(node, "beat", section, index, where, "beat");
            bool pause = Bool(node, "pause", where, "\"stops the song\" setting", section, null, index);

            // When it plays: first, so the notes below can name the line by its time.
            if (during)
            {
                if (time == null && beat == null)
                {
                    Note(section, index, null, $"{where} has no \"time\" or \"beat\", so it's left out", $"{where} has no time, so it's left out");
                    return null;
                }
                if ((beat ?? time) < 0)
                {
                    string at = beat != null ? $"beat {Num(beat.Value)}" : $"{Num(time!.Value)} s";
                    Note(section, index, null, $"{where} is at {at}, before the song starts, so it's left out", $"{where} is at {at}, before the song starts, so it's left out");
                    return null;
                }
                line.Beat = beat;
                line.Time = beat is double b ? Timing.BeatToSeconds(b) : time!.Value;
                line.Pause = pause;
                where = LineName(section, index, line.Time);
                if (time != null && beat != null)
                    Note(section, index, null, $"{where} has both a \"time\" and a \"beat\"; the \"beat\" is used", $"{where} has both a time and a beat; the beat is used");
                // The battle ends a beat after its last note.
                double end = Timing.RowToSeconds(LastNoteRow() + EditorChart.RowsPerBeat);
                if (line.Time > end + 1e-6)
                {
                    Note(section, index, null, $"{where} comes after the last note, so it never shows; put it in \"afterWin\"",
                        $"{where} comes after the last note, so it never shows; put it in After a win");
                    return null;
                }
            }
            else
            {
                if (time != null || beat != null)
                    Note(section, index, null, $"{where}: \"time\" and \"beat\" are only for lines during the song, so they aren't used",
                        $"{where}: only lines during the song have a time, so its time isn't used");
                if (pause)
                    Note(section, index, null, $"{where}: \"pause\" is only for lines during the song, so it isn't used",
                        $"{where}: only lines during the song can stop the song, so it doesn't");
            }

            var def = new DialogueLineDefinition
            {
                speaker = Text(node, "speaker", where, "speaker", section, null, index),
                expression = Text(node, "expression", where, "expression", section, null, index),
                text = Text(node, "text", where, "text", section, null, index),
                name = Text(node, "name", where, "name shown", section, null, index),
                side = Text(node, "side", where, "side", section, null, index),
                duration = Number(node, "duration", section, index, where, "time on screen"),
                time = time,
                beat = beat,
                pause = pause,
            };

            // The text: one line the box can show, at most 150 letters. Without it there's no line.
            string text = CleanText(def.text, out bool removed);
            if (text.Length == 0)
            {
                Note(section, index, null, $"{where} has no \"text\", so it's left out", $"{where} has no text, so it's left out");
                return null;
            }
            if (removed) Note(section, index, null, $"{where}: its \"text\" had < > {{ or }}, which the game's text box can't show; they're taken out",
                $"{where}: the game's text box can't show < > {{ or }}, so they're taken out");
            if (text.Length > MaxText)
            {
                Note(section, index, null, $"{where}: its \"text\" is {text.Length} letters long; it's cut to {MaxText}", $"{where} is {text.Length} letters long; it's cut to {MaxText}");
                text = text.Substring(0, MaxText).TrimEnd();
            }
            line.Text = text;

            // Who says it: one of the battle's own speakers, the Narrator, or a game character.
            string who = (def.speaker ?? "").Trim();
            DialogueSpeaker? custom = null;
            if (who.Length == 0)
            {
                Note(section, index, null, $"{where} has no \"speaker\", so the Narrator says it", $"{where} has no speaker, so the Narrator says it");
                line.SpeakerKind = DialogueSpeakerKind.Narrator;
                line.Speaker = Narrator;
            }
            else if ((custom = Data.FindSpeaker(who)) != null)
            {
                line.SpeakerKind = DialogueSpeakerKind.Custom;
                line.Speaker = custom.Key;
            }
            else if (who.Equals(Narrator, StringComparison.OrdinalIgnoreCase) || dropped.Contains(who))
            {
                // A speaker that was left out was noted once, with the reason.
                line.SpeakerKind = DialogueSpeakerKind.Narrator;
                line.Speaker = Narrator;
            }
            else
            {
                // A game character; only the game knows its ids, so the runtime checks it.
                line.SpeakerKind = DialogueSpeakerKind.Game;
                line.Speaker = who;
            }

            // The face: a custom speaker's is checked here, a game character's by the runtime.
            string? face = def.expression?.Trim();
            if (face?.Length > 0)
            {
                if (line.SpeakerKind == DialogueSpeakerKind.Game) line.Face = face;
                else if (custom?.Portrait != null)
                {
                    if (custom.Expression(face) is { } found) line.Face = found.Name;
                    else if (!droppedFaces.Contains(custom.Key + "/" + face)) Note(section, index, null, $"{where}: {custom.Name} has no expression \"{face}\", so the default picture shows", $"{where}: {custom.Name} has no expression \"{face}\", so the default picture shows");
                }
            }

            if (def.side != null)
            {
                line.Side = Side(def.side);
                if (line.Side == null)
                    Note(section, index, null, $"{where}: \"side\" must be \"left\" or \"right\", so the speaker's own side is used", $"{where}: the side must be left or right, so the speaker's usual side is used");
            }

            string name = CleanText(def.name, out removed);
            if (removed) Note(section, index, null, $"{where}: its \"name\" had < > {{ or }}, which the game's box can't show; they're taken out",
                $"{where}: the name shown had < > {{ or }}, which the game's box can't show; they're taken out");
            if (name.Length > MaxName)
            {
                Note(section, index, null, $"{where}: its \"name\" is longer than {MaxName} letters, so it's cut", $"{where}: the name shown is longer than {MaxName} letters, so it's cut");
                name = name.Substring(0, MaxName).TrimEnd();
            }
            if (name.Length > 0) line.Name = name;

            if (def.duration is double seconds)
            {
                if (seconds < MinDuration || seconds > MaxDuration)
                {
                    Note(section, index, null, $"{where}: \"duration\" is {Num(seconds)} s; it's kept between {MinDuration:0} and {MaxDuration:0} s",
                        $"{where}: its time on screen is {Num(seconds)} s; it's kept between {MinDuration:0} and {MaxDuration:0} s");
                    seconds = Math.Clamp(seconds, MinDuration, MaxDuration);
                }
                line.Duration = seconds;
            }
            return line;
        }

        // The checks that need every line during the song: lines that overlap, and stops right before notes.
        internal void CheckDuring()
        {
            DialogueLine? shown = null;
            double lastStop = double.NaN;
            foreach (var line in Data.During)
            {
                if (line.Pause)
                {
                    // A stop hides the line that's showing. Stopping lines at the same time are one stop.
                    shown = null;
                    if (line.Time == lastStop) continue;
                    lastStop = line.Time;
                    if (NotesSoonAfter(line))
                        Note(DialogueSection.During, line.Index, null,
                            $"{LineName(DialogueSection.During, line.Index, line.Time)} stops the song, and notes right after this stop come at once when the song goes on");
                    continue;
                }
                if (shown != null && line.Time < shown.Time + shown.ShowSeconds - 1e-6)
                    Note(DialogueSection.During, line.Index, null,
                        $"{LineName(DialogueSection.During, line.Index, line.Time)} starts before {LineName(DialogueSection.During, shown.Index, shown.Time)} ends, so it cuts that one short");
                shown = line;
            }
        }

        // Whether any difficulty has a note (not a mine) within the next 2 beats of a stop.
        private bool NotesSoonAfter(DialogueLine line)
        {
            noteRows ??= NoteRows();
            double row = (line.Beat ?? Timing.SecondsToBeat(line.Time)) * EditorChart.RowsPerBeat;
            double until = row + BreakNoteBeats * EditorChart.RowsPerBeat;
            return noteRows.Any(r => r >= row - 1e-6 && r <= until + 1e-6);
        }

        private List<int> NoteRows()
        {
            var rows = new List<int>();
            foreach (var block in slots.Where(s => s != null).Distinct())
            {
                var notes = new EditorChart(block!.Lanes);
                notes.ReadNotes(block.Notes);
                rows.AddRange(notes.Notes.Where(n => n.Type != 'M').Select(n => n.Row));
            }
            return rows;
        }

        private int LastNoteRow() => slots.Where(s => s != null).Select(s => ChartText.LastNoteRow(s!)).DefaultIfEmpty(0).Max();

        // ---- values: keys in any letter case, numbers as numbers or text -------------------------

        // A text value, or null when it's missing; anything else is noted and left out.
        private string? Text(JsonElement obj, string key, string where, string plainKey, DialogueSection? section, string? speaker, int? index = null)
        {
            var v = Prop(obj, key);
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                Note(section, index, speaker, $"{where}: \"{key}\" must be text, so it isn't used", $"{where}: its {plainKey} couldn't be read, so it isn't used");
            return null;
        }

        private double? Number(JsonElement obj, string key, DialogueSection section, int index, string where, string plainKey)
        {
            var v = Prop(obj, key);
            if (v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
            if (TryNumber(v, out double value)) return value;
            Note(section, index, null, $"{where}: \"{key}\" must be a number, so it isn't used", $"{where}: its {plainKey} couldn't be read, so it isn't used");
            return null;
        }

        private bool Bool(JsonElement obj, string key, string where, string plainKey, DialogueSection? section, string? speaker, int? index = null)
        {
            var v = Prop(obj, key);
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind is not (JsonValueKind.False or JsonValueKind.Undefined or JsonValueKind.Null))
                Note(section, index, speaker, $"{where}: \"{key}\" must be true or false, so it isn't used", $"{where}: its {plainKey} couldn't be read, so it's off");
            return false;
        }
    }

    // "left" or "right" in any letter case; null for anything else.
    private static DialogueSide? Side(string side) => side.Trim().ToLowerInvariant() switch
    {
        "left" => DialogueSide.Left,
        "right" => DialogueSide.Right,
        _ => null,
    };

    private static JsonElement Prop(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return default;
        foreach (var p in obj.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return default;
    }

    // Numbers may be written as text, as the rest of battle.json; "NaN" and "Infinity" don't count.
    private static bool TryNumber(JsonElement v, out double value)
    {
        value = 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetDouble(out value) && double.IsFinite(value);
        if (v.ValueKind == JsonValueKind.String)
            return double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        return false;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
