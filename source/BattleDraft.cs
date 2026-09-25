using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NocturneFlatScroll;

/// <summary>
/// A custom battle's battle.json as the battle creator edits it. The file is kept as a JSON tree,
/// so fields the creator doesn't know (from a newer version, or added by hand) are saved again as
/// they were; the properties read and write that tree. An enemy or dialogue kept in its own file
/// (like "enemy/enemy.json") is edited and saved there. Saving writes a temporary file and then
/// puts it in place, so a failed save never leaves half a file.
/// This file has no Unity or game dependencies.
/// </summary>
internal sealed class BattleDraft
{
    /// <summary>The stat keys an enemy can set, as EnemyStats names them.</summary>
    internal const string Hp = "hp", Damage = "damage", PassiveEnergyCharge = "passiveEnergyCharge",
        EnergyChargeOnMiss = "energyChargeOnMiss", AttackWindupTime = "attackWindupTime";

    // Read the way BattlePackage reads: any letter case, comments and trailing commas allowed.
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Written indented, with letters like é or あ kept as they are rather than escaped.</summary>
    internal static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly JsonObject root;
    private JsonObject enemy;
    // Where the enemy is saved when battle.json names a file for it; null when it's inline.
    private readonly string? enemyFile;
    private bool enemyAttached, enemyChanged;
    // The dialogue works like the enemy: inline, or in the file battle.json names.
    private JsonObject dialogue;
    private readonly string? dialogueFile;
    private bool dialogueAttached, dialogueChanged;
    private GearDefinition gear;
    private GearDefinition? lastSetGear;
    // The set health upgrades while the player's own are picked, so picking "set" again brings them back.
    private int? lastExtraHealth;
    private LevelDefinition level;
    // The set level while the player's own is picked, so picking "set" again brings it back.
    private int? lastSetLevel;
    // The battle's own info boxes while they are switched off, so switching them on again brings them back.
    private JsonArray? lastInfo;
    private string audioAtLoad;
    private int changes, savedChanges;

    /// <summary>The battle's folder.</summary>
    internal string Folder { get; }

    /// <summary>Things found while loading that the creator should show, like an enemy file that couldn't be read.</summary>
    internal List<string> Problems { get; } = new();

    /// <summary>
    /// Why the enemy can't be changed, or null when it can: its own file is there but can't be
    /// read, and saving over it would lose what it holds.
    /// </summary>
    internal string? EnemyLocked { get; }

    /// <summary>
    /// Why the dialogue can't be changed, or null when it can: its own file is there but can't be
    /// read, and saving over it would lose what it holds.
    /// </summary>
    internal string? DialogueLocked { get; }

    /// <summary>Whether anything changed since the last load or save.</summary>
    internal bool Dirty => changes != savedChanges;

    private BattleDraft(string folder, JsonObject root, JsonObject enemy, string? enemyFile, bool enemyAttached, string? enemyLocked,
        JsonObject dialogue, string? dialogueFile, bool dialogueAttached, string? dialogueLocked)
    {
        Folder = folder;
        this.root = root;
        this.enemy = enemy;
        this.enemyFile = enemyFile;
        this.enemyAttached = enemyAttached;
        EnemyLocked = enemyLocked;
        this.dialogue = dialogue;
        this.dialogueFile = dialogueFile;
        this.dialogueAttached = dialogueAttached;
        DialogueLocked = dialogueLocked;
        gear = ReadGear(root["gear"], Problems);
        // The loader's own reader, so the creator shows a level (and its problems) exactly as the arcade will use it.
        level = LevelDefinition.Read(JsonSerializer.Deserialize<JsonElement>(root["level"]?.ToJsonString() ?? "null"), Problems);
        audioAtLoad = Audio;
    }

    private string ManifestPath => Path.Combine(Folder, BattlePackage.ManifestName);

    // ---- loading and saving ---------------------------------------------------------------------

    /// <summary>Reads a battle folder's battle.json (and its enemy and dialogue files, when it names them).</summary>
    internal static BattleDraft Load(string folder)
    {
        string path = Path.Combine(folder, BattlePackage.ManifestName);
        var root = ParseObject(ReadText(path, BattlePackage.MaxJsonBytes), BattlePackage.ManifestName);
        CheckManifest(root);
        var problems = new List<string>();
        var (enemy, enemyFile, attached, locked) = ReadPart(folder, root, "enemy", "an enemy change", null, problems);
        var (dialogue, dialogueFile, dialogueAttached, dialogueLocked) = ReadPart(folder, root, "dialogue", "a dialogue change",
            "\"dialogue\" is neither an object nor a file name, so the battle plays without it; a dialogue change here replaces it", problems);
        var draft = new BattleDraft(folder, root, enemy, enemyFile, attached, locked, dialogue, dialogueFile, dialogueAttached, dialogueLocked);
        draft.Problems.InsertRange(0, problems);
        return draft;
    }

    /// <summary>
    /// A part of the battle that battle.json keeps inline or in a file of its own ("enemy",
    /// "dialogue"): the object, the file (null when inline), whether it's in place in battle.json
    /// (a part the battle doesn't have yet is added when it's first changed), and why it can't be
    /// changed when its file can't be read. <paramref name="change"/> names a change to it ("an
    /// enemy change"), and <paramref name="odd"/> is the problem to note when it's neither an
    /// object nor a file name (null notes nothing).
    /// </summary>
    private static (JsonObject Part, string? File, bool Attached, string? Locked) ReadPart(string folder, JsonObject root, string key, string change, string? odd,
        List<string> problems)
    {
        switch (root[key])
        {
            case JsonObject inline:
                return (inline, null, true, null);
            case JsonValue value when value.TryGetValue(out string? name):
                string? file = PackageFiles.SafeName(name ?? "");
                if (file == null)
                {
                    // Saving puts it into battle.json instead.
                    problems.Add($"\"{key}\" names a file outside the battle; the {key} is saved in battle.json");
                    return (new JsonObject(NodeOptions), null, false, null);
                }
                try { return (ParseObject(ReadText(Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar)), BattlePackage.MaxJsonBytes), file), file, true, null); }
                catch (FileNotFoundException)
                {
                    problems.Add($"{file} is missing; saving {change} writes a new one");
                    return (new JsonObject(NodeOptions), file, true, null);
                }
                catch (Exception ex) when (IsFileProblem(ex))
                {
                    // The file is there: it is never saved over, so nothing in it is lost.
                    string locked = $"{file} can't be read ({ex.Message}). Fix it by hand, then open the battle again. Until then the {key} can't be changed here.";
                    problems.Add(locked);
                    return (new JsonObject(NodeOptions), file, true, locked);
                }
            default:
                // None yet, or something that is neither: made when the creator first changes it.
                if (root[key] != null && odd != null) problems.Add(odd);
                return (new JsonObject(NodeOptions), null, false, null);
        }
    }

    /// <summary>A new battle's manifest, not saved yet: a new id, the placeholder Mantis without a name of its own, the player's own gear.</summary>
    internal static BattleDraft Create(string folder, string title, int lanes, string audio, string author)
    {
        if (lanes != 4 && lanes != 5) throw new ArgumentOutOfRangeException(nameof(lanes), "a battle has 4 or 5 lanes");
        var root = new JsonObject(NodeOptions)
        {
            ["format"] = BattlePackage.FormatVersion,
            ["kind"] = "battle",
            ["id"] = Guid.NewGuid().ToString("D"),
            ["title"] = CleanLine(title),
            ["artist"] = "",
            ["author"] = CleanLine(author),
            ["lore"] = "",
            ["audio"] = audio,
            ["previewStart"] = 0,
            ["lanes"] = lanes,
            ["chart"] = BattlePackage.DefaultChart,
            ["enemy"] = new JsonObject(NodeOptions)
            {
                ["mode"] = "placeholder",
                ["placeholder"] = EnemyPlaceholders.Default
            }
        };
        var draft = new BattleDraft(folder, root, (JsonObject)root["enemy"]!, null, true, null, new JsonObject(NodeOptions), null, false, null);
        draft.changes++;
        return draft;
    }

    /// <summary>
    /// Writes battle.json (and the enemy's and the dialogue's own files when they have one and it
    /// changed). When the audio changed, the chart's #MUSIC is changed to match, so the .sm names
    /// the same song.
    /// </summary>
    internal void Save()
    {
        PruneInfoBoxes();
        DropConsumableCount();
        PruneDialogue();
        Directory.CreateDirectory(Folder);
        if (enemyFile != null && enemyChanged && EnemyLocked == null)
            WriteAtomic(Path.Combine(Folder, enemyFile.Replace('/', Path.DirectorySeparatorChar)), enemy.ToJsonString(WriteOptions) + "\n");
        if (dialogueFile != null && dialogueChanged && DialogueLocked == null)
            WriteAtomic(Path.Combine(Folder, dialogueFile.Replace('/', Path.DirectorySeparatorChar)), dialogue.ToJsonString(WriteOptions) + "\n");
        WriteAtomic(ManifestPath, root.ToJsonString(WriteOptions) + "\n");
        string audio = Audio;
        if (!audio.Equals(audioAtLoad, StringComparison.Ordinal))
        {
            UpdateChartMusic(audio);
            ChartChanged();
        }
        audioAtLoad = audio;
        enemyChanged = false;
        dialogueChanged = false;
        savedChanges = changes;
    }

    /// <summary>battle.json as it would be saved, for checks.</summary>
    internal string ManifestJson() => root.ToJsonString(WriteOptions);

    private void UpdateChartMusic(string audio)
    {
        // Only a chart file of its own is written: never battle.json, the song, the card or the enemy's file.
        if (ChartFileProblem() != null) return;
        string? chart = PackageFiles.SafeName(ChartPath);
        if (chart == null) return;
        string path = Path.Combine(Folder, chart.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > BattlePackage.MaxChartBytes) return;
        string text = ReadText(path, BattlePackage.MaxChartBytes);
        string changed = SetMusicTag(text, audio);
        if (changed != text) WriteAtomic(path, changed);
    }

    private static readonly Regex MusicTag = new(@"^([ \t]*#MUSIC[ \t]*:)[^;\r\n]*;", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// The .sm text with #MUSIC set to <paramref name="music"/>. Only that tag changes; the rest of
    /// the file (comments, spacing, other tags) stays exactly as it was. A file without #MUSIC gets
    /// one at the top.
    /// </summary>
    internal static string SetMusicTag(string text, string music)
    {
        string value = CleanTagValue(music);
        var match = MusicTag.Match(text);
        if (match.Success) return text.Substring(0, match.Index) + match.Groups[1].Value + value + ";" + text.Substring(match.Index + match.Length);
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        return "#MUSIC:" + value + ";" + newline + text;
    }

    /// <summary>A value that can sit in an .sm tag: ':' and ';' end a value and '//' starts a comment.</summary>
    internal static string CleanTagValue(string text)
    {
        var clean = CleanLine(text).Replace(":", " ").Replace(";", " ");
        while (clean.Contains("//")) clean = clean.Replace("//", "/");
        return clean.Trim().TrimStart('#').Trim();
    }

    /// <summary>Gives the battle a new id, for a copy that would otherwise share its scores with the original.</summary>
    internal void NewId() => SetString(root, "id", Guid.NewGuid().ToString("D"));

    /// <summary>An id the way the loader compares ids: a GUID in its plain form ("{...}" and capitals don't count).</summary>
    internal static string NormalizeId(string? id)
    {
        string text = (id ?? "").Trim();
        return Guid.TryParse(text, out var guid) ? guid.ToString("D") : text;
    }

    // ---- the battle ---------------------------------------------------------------------------------

    internal string Id => GetString(root, "id");
    internal int Format => (int)(GetNumber(root, "format") ?? 0);
    internal string Kind => GetString(root, "kind");

    internal string Title { get => GetString(root, "title"); set => SetString(root, "title", CleanLine(value)); }
    internal string Artist { get => GetString(root, "artist"); set => SetString(root, "artist", CleanLine(value)); }
    internal string Author { get => GetString(root, "author"); set => SetString(root, "author", CleanLine(value)); }

    /// <summary>The text in the arcade's box on the right when the battle is selected; several lines are fine.</summary>
    internal string Lore
    {
        get => GetString(root, "lore");
        set => SetString(root, "lore", CleanText(value));
    }

    /// <summary>The card image, as a path inside the battle, or null for none.</summary>
    internal string? Card
    {
        get
        {
            string card = GetString(root, "card").Trim();
            return card.Length == 0 ? null : card;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) Remove(root, "card");
            else SetString(root, "card", value!.Trim());
        }
    }

    /// <summary>The song file battle.json names, as a path inside the battle; empty when it names none.</summary>
    internal string Audio { get => GetString(root, "audio").Trim(); set => SetString(root, "audio", value.Trim()); }

    /// <summary>
    /// The song file the battle plays: battle.json's "audio", or the chart's #MUSIC when battle.json
    /// has no "audio" (as the loader reads it). Setting <see cref="Audio"/> writes "audio".
    /// </summary>
    internal string EffectiveAudio => AudioFromChart ? ChartMusic() : Audio;

    /// <summary>Whether the song comes from the chart's #MUSIC, because battle.json has no "audio".</summary>
    internal bool AudioFromChart => root["audio"] is not JsonValue;

    private string? chartMusic;

    // Read once; ChartChanged reads it again.
    private string ChartMusic()
    {
        if (chartMusic != null) return chartMusic;
        chartMusic = "";
        try
        {
            string? chart = PackageFiles.SafeName(ChartPath);
            if (chart != null)
                chartMusic = (ChartText.Parse(ReadText(Path.Combine(Folder, chart.Replace('/', Path.DirectorySeparatorChar)), BattlePackage.MaxChartBytes)).GetTag("MUSIC") ?? "").Trim();
        }
        catch (Exception ex) when (IsFileProblem(ex)) { }
        return chartMusic;
    }

    /// <summary>The chart file changed (the chart editor saved it): what is read from it is read again.</summary>
    internal void ChartChanged()
    {
        chartMusic = null;
        inferredLanes = null;
    }

    /// <summary>Where the arcade's preview of the song starts, in seconds.</summary>
    internal double PreviewStart
    {
        get => Math.Max(0, GetNumber(root, "previewStart") ?? 0);
        set => SetNumber(root, "previewStart", Math.Max(0, Math.Round(value, 3)));
    }

    /// <summary>The chart file, as a path inside the battle.</summary>
    internal string ChartPath
    {
        get
        {
            string chart = GetString(root, "chart").Trim();
            return chart.Length == 0 ? BattlePackage.DefaultChart : chart;
        }
    }

    /// <summary>
    /// Why the Charts page can't edit the chart file battle.json names, or null when it can (see
    /// BattleChartTarget.ChartFileProblem): an .sm file of its own, not the song, card, enemy file
    /// or one of the enemy's art files (art is known by its bytes, so any name can be one).
    /// </summary>
    internal string? ChartFileProblem() => BattleChartTarget.ChartFileProblem(Folder, ChartPath,
        new (string What, string? Name)[] { ("song", EffectiveAudio), ("card image", Card), ("enemy file", enemyFile), ("dialogue", TextOnly(root, "dialogue")) }
            .Concat(EnemyArtReader.Names.Select(anim => ($"enemy's {anim} art", ArtFile(anim)))).ToArray());

    private int? inferredLanes;

    /// <summary>
    /// 4 or 5, fixed when the battle is made. Read from the chart when battle.json doesn't say (as
    /// the loader does); that is read once, until <see cref="ChartChanged"/>.
    /// </summary>
    internal int Lanes
    {
        get
        {
            int? lanes = (int?)GetNumber(root, "lanes");
            if (lanes is 4 or 5) return lanes.Value;
            return inferredLanes ??= InferLanes();
        }
    }

    private int InferLanes()
    {
        try
        {
            string? chart = PackageFiles.SafeName(ChartPath);
            if (chart == null) return 4;
            var parsed = ChartText.Parse(ReadText(Path.Combine(Folder, chart.Replace('/', Path.DirectorySeparatorChar)), BattlePackage.MaxChartBytes));
            foreach (var block in parsed.Blocks)
                if (ChartText.HasNotes(block)) return block.Lanes;
        }
        catch (Exception ex) when (IsFileProblem(ex)) { }
        return 4;
    }

    // ---- the enemy ----------------------------------------------------------------------------------

    /// <summary>
    /// The enemy's name. The battle shows it only as the title of the battle's own info boxes that
    /// have no title of their own; nothing else in the game shows an enemy's name.
    /// </summary>
    internal string EnemyName
    {
        get => GetString(enemy, "name");
        set
        {
            string name = CleanLine(value);
            if (name == EnemyName) return;
            CheckEnemy();
            if (name.Length == 0) EnemyRemove("name");
            else EnemySet("name", name);
        }
    }

    internal string EnemyMode => GetString(enemy, "mode").Trim();

    /// <summary>
    /// The game enemy the battle's enemy is copied from (an EnemyData asset name); empty means
    /// Mantis. A custom-art enemy fights like it and keeps its custom art.
    /// </summary>
    internal string Placeholder
    {
        get => GetString(enemy, "placeholder").Trim();
        set
        {
            if (Placeholder == value && EnemyMode.Length > 0) return;
            CheckEnemy();
            if (EnemyMode.Length == 0) EnemySet("mode", "placeholder");
            EnemySet("placeholder", value.Trim());
        }
    }

    /// <summary>Whether the enemy may be one of the bosses built around scripted fights.</summary>
    internal bool Advanced
    {
        get => enemy["advanced"] is JsonValue v && v.TryGetValue(out bool on) && on;
        set
        {
            if (value == Advanced) return;
            CheckEnemy();
            if (value) EnemySet("advanced", true);
            else EnemyRemove("advanced");
        }
    }

    /// <summary>A stat the battle sets, or null when the placeholder's own is used.</summary>
    internal double? Stat(string key) => enemy["stats"] is JsonObject stats ? GetNumber(stats, key) : null;

    internal void SetStat(string key, double? value)
    {
        if (Stat(key) == value) return;
        CheckEnemy();
        var stats = enemy["stats"] as JsonObject;
        if (value == null)
        {
            if (stats == null) return;
            Remove(stats, key);
            if (stats.Count == 0) enemy.Remove("stats");
        }
        else
        {
            if (stats == null) enemy["stats"] = stats = new JsonObject(NodeOptions);
            SetNumber(stats, key, value.Value);
        }
        EnemyTouched();
    }

    /// <summary>Whether the battle has its own info boxes (true) or shows the placeholder's (false).</summary>
    internal bool OwnInfo => enemy["info"] is JsonArray;

    /// <summary>
    /// Switches between the battle's own info boxes and the placeholder's. The battle's own boxes
    /// are kept while they are off, and come back when they're switched on again before the creator
    /// closes (saving while they are off leaves them out of the file).
    /// </summary>
    internal void SetOwnInfo(bool own)
    {
        if (own == OwnInfo) return;
        CheckEnemy();
        if (own)
        {
            enemy["info"] = lastInfo ?? new JsonArray();
            lastInfo = null;
        }
        else
        {
            lastInfo = enemy["info"] as JsonArray;
            enemy.Remove("info");
        }
        EnemyTouched();
    }

    /// <summary>Info box <paramref name="index"/> (0 to 2); empty texts when there is none.</summary>
    internal (string Title, string Description) InfoBox(int index)
    {
        if (enemy["info"] is not JsonArray boxes || index < 0 || index >= boxes.Count || boxes[index] is not JsonObject box) return ("", "");
        return (GetString(box, "title"), GetString(box, "description"));
    }

    /// <summary>Sets an info box's title or description (null leaves that part as it is). Turns on the battle's own info boxes.</summary>
    internal void SetInfoBox(int index, string? title, string? description)
    {
        if (index < 0 || index >= EnemyPlaceholders.MaxInfoBoxes) throw new ArgumentOutOfRangeException(nameof(index));
        if (!OwnInfo) SetOwnInfo(true);
        string? newTitle = title == null ? null : CleanLine(title);
        string? newDescription = description == null ? null : CleanText(description);
        var (oldTitle, oldDescription) = InfoBox(index);
        if ((newTitle == null || newTitle == oldTitle) && (newDescription == null || newDescription == oldDescription)) return;
        CheckEnemy();
        var boxes = (JsonArray)enemy["info"]!;
        while (boxes.Count <= index) boxes.Add(new JsonObject(NodeOptions));
        if (boxes[index] is not JsonObject box) boxes[index] = box = new JsonObject(NodeOptions);
        if (newTitle != null) box["title"] = newTitle;
        if (newDescription != null) box["description"] = newDescription;
        EnemyTouched();
    }

    /// <summary>The lines of a box's text the battle shows (the game's box stops there).</summary>
    internal const int InfoMaxLines = 3;

    /// <summary>
    /// About how many characters fit on one line of the battle's info box: its font has letters of
    /// one width, and 33 of them fill a line in the game (1.0.1, seen in battle screenshots).
    /// </summary>
    internal const int InfoLineChars = 33;

    /// <summary>
    /// About how many lines a box's text takes in the battle: each of its lines, wrapped at spaces
    /// the way the box wraps it (a word longer than a line is broken). 0 for no text.
    /// </summary>
    internal static int InfoTextLines(string? text)
    {
        string clean = CleanText(text);
        if (clean.Length == 0) return 0;
        int lines = 0;
        foreach (var line in clean.Split('\n'))
        {
            lines++;
            int used = 0;
            foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int length = word.Length;
                if (used > 0 && used + 1 + length <= InfoLineChars)
                {
                    used += 1 + length;
                    continue;
                }
                if (used > 0) lines++;
                // A word longer than a line runs onto the next ones.
                while (length > InfoLineChars)
                {
                    lines++;
                    length -= InfoLineChars;
                }
                used = length;
            }
        }
        return lines;
    }

    /// <summary>How many lines a box's text takes, for the typing hint: "about 2 of 3 lines", or "about 4 lines: the battle shows 3".</summary>
    internal static string InfoLinesText(string? text)
    {
        int lines = InfoTextLines(text);
        return lines > InfoMaxLines
            ? $"about {lines} lines: the battle shows {InfoMaxLines}"
            : $"about {lines} of {InfoMaxLines} lines";
    }

    /// <summary>
    /// The end of the creator's typing hint for a text with a limit: how much of it is used
    /// ("42 / 99", and "full" at the limit, where typing stops), then <paramref name="measure"/>
    /// when there is one (like <see cref="InfoLinesText"/>).
    /// </summary>
    internal static string TypingCount(int length, int max, string? measure)
    {
        string count = length >= max ? $"{max} / {max}, full" : $"{length} / {max}";
        return "   " + count + (string.IsNullOrEmpty(measure) ? "" : ", " + measure);
    }

    /// <summary>
    /// What the battle won't show of the battle's own info boxes, for the creator: a box with a
    /// title but no text (the game hides a box without text), and text longer than the box's lines.
    /// </summary>
    internal List<string> InfoBoxNotes()
    {
        var notes = new List<string>();
        if (!OwnInfo) return notes;
        for (int i = 0; i < EnemyPlaceholders.MaxInfoBoxes; i++)
        {
            var (title, description) = InfoBox(i);
            if (description.Trim().Length == 0)
            {
                if (title.Trim().Length > 0) notes.Add($"Box {i + 1} has a title but no text, so the battle doesn't show it.");
                continue;
            }
            int lines = InfoTextLines(description);
            if (lines > InfoMaxLines) notes.Add($"Box {i + 1}'s text takes about {lines} lines; the battle shows the first {InfoMaxLines}.");
        }
        return notes;
    }

    // Boxes with neither text never show in the battle (the game hides a box without text), so
    // they aren't saved. A box with only a title is kept, so what was typed isn't lost; the
    // creator points it out (InfoBoxNotes).
    private void PruneInfoBoxes()
    {
        if (enemy["info"] is not JsonArray boxes) return;
        bool pruned = false;
        for (int i = boxes.Count - 1; i >= 0; i--)
        {
            // Only boxes with nothing else in them: a key the creator doesn't know stays.
            if (boxes[i] is JsonObject box && GetString(box, "title").Trim().Length == 0 && GetString(box, "description").Trim().Length == 0
                && box.All(p => p.Key.Equals("title", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("description", StringComparison.OrdinalIgnoreCase)))
            {
                boxes.RemoveAt(i);
                pruned = true;
            }
        }
        if (pruned) enemyChanged = true;
    }

    // ---- the enemy's custom art ------------------------------------------------------------------

    /// <summary>
    /// Whether the enemy looks like its own art ("mode": "custom") or like the game enemy
    /// ("placeholder"). Its "art" is kept either way, so switching back brings it all back.
    /// </summary>
    internal bool CustomArt
    {
        get => EnemyMode.Equals("custom", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value == CustomArt && EnemyMode.Length > 0) return;
            CheckEnemy();
            EnemySet("mode", value ? "custom" : "placeholder");
        }
    }

    /// <summary>Changes counted since the draft was loaded (saving doesn't reset it), so a page can tell when to look again.</summary>
    internal int Changes => changes;

    // An animation as written: an object, or (by hand) just its file name.
    private JsonNode? ArtNode(string anim) => anim.Length == 0 ? enemy["art"] : (enemy["art"] as JsonObject)?[anim];

    /// <summary>Whether an animation ("idle", "attack", "hurt", "defeat") names a file.</summary>
    internal bool HasArt(string anim) => ArtFile(anim) != null;

    /// <summary>An animation's file, as a path inside the battle, or null.</summary>
    internal string? ArtFile(string anim)
    {
        string file = ArtNode(anim) switch
        {
            JsonObject obj => GetString(obj, "file"),
            JsonValue value when value.TryGetValue(out string? name) => name ?? "",
            _ => "",
        };
        return file.Trim().Length == 0 ? null : file.Trim();
    }

    /// <summary>A number in an animation (anim "" is the art itself), or null when it isn't set.</summary>
    internal double? ArtNumber(string anim, string key) => ArtNode(anim) is JsonObject obj ? GetNumber(obj, key) : null;

    /// <summary>A true or false in an animation (anim "" is the art itself), or null when it isn't set.</summary>
    internal bool? ArtBool(string anim, string key) =>
        ArtNode(anim) is JsonObject obj && obj[key] is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    /// <summary>A text in an animation (anim "" is the art itself), or null when it isn't set.</summary>
    internal string? ArtText(string anim, string key) =>
        ArtNode(anim) is JsonObject obj && obj[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>A pair of numbers like "offset": [x, y], or null when it isn't set (or isn't two numbers).</summary>
    internal (double X, double Y)? ArtPair(string anim, string key)
    {
        if (ArtNode(anim) is not JsonObject obj || obj[key] is not JsonArray pair || pair.Count != 2) return null;
        return Number(pair[0]) is double x && Number(pair[1]) is double y ? (x, y) : null;
    }

    /// <summary>Sets an animation to a new file and kind; its old settings go (the art's own size, offset and so on stay).</summary>
    internal void SetArtAnimation(string anim, string file, string kind)
    {
        CheckEnemy();
        var art = ArtObject();
        art[anim] = new JsonObject(NodeOptions) { ["file"] = file, ["kind"] = kind };
        EnemyTouched();
    }

    /// <summary>Sets or (with null) removes a value in an animation, or in the art itself when anim is "".</summary>
    internal void SetArtValue(string anim, string key, JsonNode? value)
    {
        var obj = anim.Length == 0 ? (value == null ? enemy["art"] as JsonObject : ArtObject()) : ArtAnimation(anim, value != null);
        if (obj == null) return;
        if (value == null)
        {
            if (!obj.ContainsKey(key)) return;
            CheckEnemy();
            Remove(obj, key);
            EnemyTouched();
            return;
        }
        if (obj[key]?.ToJsonString() == value.ToJsonString()) return;
        CheckEnemy();
        obj[key] = value;
        EnemyTouched();
    }

    /// <summary>A number for <see cref="SetArtValue"/>, whole numbers without ".0"; null removes the value.</summary>
    internal static JsonNode? ArtNumberNode(double? value) =>
        value is not double v ? null : v == Math.Floor(v) && Math.Abs(v) < 1e15 ? JsonValue.Create((long)v) : JsonValue.Create(Math.Round(v, 3));

    /// <summary>A pair like [x, y] for <see cref="SetArtValue"/>; null removes it.</summary>
    internal static JsonNode? ArtPairNode((double X, double Y)? pair) =>
        pair is not (double x, double y) ? null : new JsonArray(ArtNumberNode(x), ArtNumberNode(y));

    /// <summary>Removes an animation.</summary>
    internal void RemoveArt(string anim)
    {
        if (enemy["art"] is not JsonObject art || !art.ContainsKey(anim)) return;
        CheckEnemy();
        Remove(art, anim);
        EnemyTouched();
    }

    /// <summary>The art as it would be saved, for EnemyArtReader and the preview; null when there is none.</summary>
    internal string? ArtJson() => enemy["art"]?.ToJsonString();

    // The art object, made when the enemy has none (or "art" isn't an object).
    private JsonObject ArtObject()
    {
        if (enemy["art"] is JsonObject art) return art;
        CheckEnemy();
        enemy["art"] = art = new JsonObject(NodeOptions);
        EnemyTouched();
        return art;
    }

    // An animation's object; one written as just its file name becomes {"file": name} when it's changed.
    private JsonObject? ArtAnimation(string anim, bool make)
    {
        var node = ArtNode(anim);
        if (node is JsonObject obj) return obj;
        string? file = ArtFile(anim);
        if (!make || file == null) return null;
        CheckEnemy();
        var made = new JsonObject(NodeOptions) { ["file"] = file };
        ArtObject()[anim] = made;
        EnemyTouched();
        return made;
    }

    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        // As the loader reads it: numbers written as strings, but not "NaN" or "Infinity".
        if (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d)) return d;
        return null;
    }

    // Called before any change to the enemy.
    private void CheckEnemy()
    {
        if (EnemyLocked != null) throw new InvalidDataException(EnemyLocked);
    }

    private void EnemySet(string key, JsonNode? value)
    {
        enemy[key] = value;
        EnemyTouched();
    }

    private void EnemyRemove(string key)
    {
        if (!enemy.ContainsKey(key)) return;
        Remove(enemy, key);
        EnemyTouched();
    }

    private void EnemyTouched()
    {
        if (!enemyAttached)
        {
            // A battle without an enemy (or one whose file is out of reach) gets it in battle.json.
            if (enemyFile == null) root["enemy"] = enemy;
            enemyAttached = true;
        }
        enemyChanged = true;
        changes++;
    }

    // ---- gear -----------------------------------------------------------------------------------------

    /// <summary>Whether the battle sets the player's gear (true) or the player uses their own (false).</summary>
    internal bool SetGear => gear.IsSet;

    /// <summary>The gear as battle.json has it now, for reading; the methods below change it.</summary>
    internal GearDefinition Gear => gear;

    /// <summary>The item id in a slot of the set gear, or null when the slot is empty.</summary>
    internal string? GearItem(GearSlot slot) => gear.IsSet ? gear.ItemFor(slot) : null;

    /// <summary>Health upgrades for the battle, or null for the player's own.</summary>
    internal int? ExtraHealth => gear.IsSet ? gear.extraHealth : null;

    /// <summary>
    /// "Set gear for this battle" (true) or "Player's own gear" (false). Going back to the
    /// player's own writes {"mode": "player"} and nothing else; the set gear picked before comes
    /// back if it's turned on again before the creator closes.
    /// </summary>
    internal void SetGearMode(bool set)
    {
        if (set == gear.IsSet && root["gear"] != null) return;
        if (set) gear = lastSetGear ?? new GearDefinition { mode = "set" };
        else
        {
            if (gear.IsSet) lastSetGear = gear;
            gear = new GearDefinition { mode = "player" };
        }
        WriteGear();
    }

    internal void SetGearItem(GearSlot slot, string? itemId)
    {
        if (!gear.IsSet) SetGearMode(true);
        string? id = string.IsNullOrWhiteSpace(itemId) ? null : itemId!.Trim();
        if (gear.ItemFor(slot) == id) return;
        switch (slot)
        {
            case GearSlot.MainHand: gear.mainHand = id; break;
            case GearSlot.Body: gear.body = id; break;
            case GearSlot.Head: gear.head = id; break;
            case GearSlot.OffHand: gear.offHand = id; break;
            case GearSlot.Amulet: gear.amulet = id; break;
            // No count: the game allows one consumable use per battle, so a count does nothing (see DropConsumableCount).
            default: gear.consumable = id == null ? null : new GearConsumable { item = id }; break;
        }
        WriteGear();
    }

    /// <summary>
    /// Leaves out the set consumable's count, from an earlier creator or a battle.json written by
    /// hand: the game allows one consumable use per battle, so the count does nothing in play, and
    /// the creator doesn't show it. Saving the battle drops it, so it isn't left where no one can
    /// see or change it (and a count the loader would clamp stops being a problem).
    /// </summary>
    private void DropConsumableCount()
    {
        if (!gear.IsSet || gear.consumable?.count == null) return;
        gear.consumable.count = null;
        // Only the count goes; the rest of "gear" is saved as it was, keys the creator doesn't know too.
        if (root["gear"] is JsonObject saved && saved["consumable"] is JsonObject consumable && consumable.Remove("count")) changes++;
        else WriteGear();
    }

    internal void SetExtraHealth(int? count)
    {
        if (!gear.IsSet) SetGearMode(true);
        int? value = count is int n ? Math.Max(0, n) : null;
        if (gear.extraHealth == value) return;
        gear.extraHealth = value;
        WriteGear();
    }

    /// <summary>
    /// Health upgrades set for this battle (true) or the player's own (false). Setting them starts
    /// at the number set before (until the creator closes), else at 0.
    /// </summary>
    internal void SetExtraHealthMode(bool set)
    {
        if (set == (ExtraHealth != null)) return;
        if (set) SetExtraHealth(lastExtraHealth ?? 0);
        else
        {
            lastExtraHealth = ExtraHealth;
            SetExtraHealth(null);
        }
    }

    // The keys come from GearDefinition itself, so they're exactly what BattlePackage reads.
    private void WriteGear()
    {
        root["gear"] = gear.IsSet
            ? JsonSerializer.SerializeToNode(gear, WriteOptions)
            : new JsonObject(NodeOptions) { ["mode"] = "player" };
        changes++;
    }

    // ---- level -----------------------------------------------------------------------------------------

    /// <summary>Whether the battle sets the player's level (true) or the player plays at their own (false).</summary>
    internal bool SetLevel => level.IsSet;

    /// <summary>The level the battle sets (1 to 20); when it doesn't, the one it would set.</summary>
    internal int LevelValue => level.value is int n && level.IsSet
        ? Math.Clamp(n, LevelDefinition.MinLevel, LevelDefinition.MaxLevel)
        : lastSetLevel ?? LevelDefinition.MinLevel;

    /// <summary>
    /// "Set level for this battle" (true) or "Player's own level" (false). Setting it starts at the
    /// level picked before, else at <paramref name="suggested"/> (the player's own level, say).
    /// Going back to the player's own writes {"mode": "player"} and nothing else.
    /// </summary>
    internal void SetLevelMode(bool set, int suggested)
    {
        // Nothing changes, nothing is written; a level that couldn't be read is written over.
        if (set == level.IsSet && root["level"] != null && (set || level.IsPlayer)) return;
        if (set) level = new LevelDefinition { mode = "set", value = lastSetLevel ?? ClampLevel(suggested) };
        else
        {
            if (level.IsSet) lastSetLevel = LevelValue;
            level = new LevelDefinition { mode = "player" };
        }
        WriteLevel();
    }

    /// <summary>Sets the battle's level (kept between 1 and 20), and sets "set" mode if it's off.</summary>
    internal void SetLevelValue(int value)
    {
        value = ClampLevel(value);
        if (level.IsSet && level.value == value) return;
        level = new LevelDefinition { mode = "set", value = value };
        WriteLevel();
    }

    private static int ClampLevel(int value) => Math.Clamp(value, LevelDefinition.MinLevel, LevelDefinition.MaxLevel);

    private void WriteLevel()
    {
        root["level"] = level.IsSet
            ? new JsonObject(NodeOptions) { ["mode"] = "set", ["value"] = level.value!.Value }
            : new JsonObject(NodeOptions) { ["mode"] = "player" };
        changes++;
    }

    // ---- dialogue --------------------------------------------------------------------------------------
    //
    // The lines and speakers are edited on the JSON tree, so keys the creator doesn't know stay
    // where they are. Lines are found by their section and their place in it (as DialogueLine.Index
    // has it). Every change keeps the dialogue as it was for the Dialogue page's Undo.

    /// <summary>How many dialogue changes Undo can take back.</summary>
    internal const int DialogueUndoDepth = 50;

    // The dialogue's JSON before each change (newest last), and after each undone one.
    private readonly List<string> dialogueUndo = new(), dialogueRedo = new();

    /// <summary>The file the dialogue is kept in, as a path inside the battle; null when it's in battle.json.</summary>
    internal string? DialogueFile => dialogueFile;

    /// <summary>
    /// The dialogue as it would be saved (its JSON object), for a test play; null when there's
    /// nothing to give (the battle has none yet, or its file can't be read), and the saved
    /// battle.json's is used.
    /// </summary>
    internal string? DialogueJson() => DialogueLocked != null || (!dialogueAttached && dialogueFile == null) ? null : dialogue.ToJsonString();

    internal bool CanUndoDialogue => dialogueUndo.Count > 0 && DialogueLocked == null;
    internal bool CanRedoDialogue => dialogueRedo.Count > 0 && DialogueLocked == null;

    /// <summary>Takes back the last dialogue change; false when there is none.</summary>
    internal bool UndoDialogue() => StepDialogue(dialogueUndo, dialogueRedo);

    /// <summary>Makes the last undone dialogue change again; false when there is none.</summary>
    internal bool RedoDialogue() => StepDialogue(dialogueRedo, dialogueUndo);

    /// <summary>Every text in the dialogue's Undo and Redo steps: the pictures they can bring back, which stay out of the Recycle Bin while the battle is open.</summary>
    internal HashSet<string> DialogueUndoTexts()
    {
        var texts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in dialogueUndo.Concat(dialogueRedo)) texts.UnionWith(Texts(ParseAny(json)));
        return texts;
    }

    private bool StepDialogue(List<string> from, List<string> to)
    {
        if (from.Count == 0 || DialogueLocked != null) return false;
        to.Add(dialogue.ToJsonString());
        var node = ParseObject(from[^1], "the dialogue");
        from.RemoveAt(from.Count - 1);
        dialogue = node;
        if (dialogueFile == null) root["dialogue"] = node;
        dialogueAttached = true;
        dialogueChanged = true;
        changes++;
        return true;
    }

    // Every dialogue change goes through here: the dialogue as it was is kept for Undo, unless
    // nothing changed.
    private void EditDialogue(Action change)
    {
        if (DialogueLocked != null) throw new InvalidDataException(DialogueLocked);
        string before = dialogue.ToJsonString();
        change();
        if (dialogue.ToJsonString() == before) return;
        dialogueUndo.Add(before);
        if (dialogueUndo.Count > DialogueUndoDepth) dialogueUndo.RemoveAt(0);
        dialogueRedo.Clear();
        if (!dialogueAttached)
        {
            // A battle without dialogue (or one whose file is out of reach) gets it in battle.json.
            if (dialogueFile == null) root["dialogue"] = dialogue;
            dialogueAttached = true;
        }
        dialogueChanged = true;
        changes++;
    }

    // Sections and speakers left empty aren't saved, and a dialogue in battle.json left with
    // nothing in it goes. Only after a change: a file as it was read is saved as it was.
    private void PruneDialogue()
    {
        if (!dialogueChanged || DialogueLocked != null) return;
        if (dialogue["speakers"] is JsonObject { Count: 0 }) RemoveKey(dialogue, "speakers");
        foreach (var section in Sections)
            if (dialogue[DialogueReader.Key(section)] is JsonArray { Count: 0 }) RemoveKey(dialogue, DialogueReader.Key(section));
        if (dialogueFile == null && dialogueAttached && dialogue.Count == 0)
        {
            RemoveKey(root, "dialogue");
            dialogueAttached = false;
        }
    }

    private static readonly DialogueSection[] Sections = { DialogueSection.Before, DialogueSection.During, DialogueSection.AfterWin, DialogueSection.AfterLoss };

    // ---- lines

    private JsonArray? Section(DialogueSection section) => dialogue[DialogueReader.Key(section)] as JsonArray;

    // The section's list, made when it has none (or has something else, which the loader leaves out).
    private JsonArray SectionToEdit(DialogueSection section)
    {
        if (Section(section) is { } list) return list;
        list = new JsonArray();
        Put(dialogue, DialogueReader.Key(section), list);
        return list;
    }

    private JsonObject? LineAt(DialogueSection section, int index) =>
        Section(section) is { } list && index >= 0 && index < list.Count ? list[index] as JsonObject : null;

    /// <summary>How many lines a section has, as written (lines the loader leaves out count too).</summary>
    internal int LineCount(DialogueSection section) => Section(section)?.Count ?? 0;

    /// <summary>Whether a section's item is a line that can be edited (an object).</summary>
    internal bool IsLine(DialogueSection section, int index) => LineAt(section, index) != null;

    /// <summary>A line's text value (like "speaker" or "text"), or null when it has none.</summary>
    internal string? LineText(DialogueSection section, int index, string key) => LineAt(section, index) is { } line ? TextOnly(line, key) : null;

    /// <summary>A line's number (like "time" or "duration"), or null when it has none.</summary>
    internal double? LineNumber(DialogueSection section, int index, string key) => LineAt(section, index) is { } line ? GetNumber(line, key) : null;

    /// <summary>Whether a line has a key set to true (like "pause").</summary>
    internal bool LineFlag(DialogueSection section, int index, string key) => LineAt(section, index) is { } line && Flag(line, key);

    /// <summary>Sets a line's values (null removes one), as one change.</summary>
    internal void SetLine(DialogueSection section, int index, params (string Key, JsonNode? Value)[] values) => EditDialogue(() =>
    {
        var line = LineAt(section, index) ?? throw new ArgumentOutOfRangeException(nameof(index));
        foreach (var (key, value) in values) Put(line, key, value);
    });

    /// <summary>Adds a line with these values at <paramref name="at"/> in its section; returns where it went.</summary>
    internal int AddLine(DialogueSection section, int at, params (string Key, JsonNode? Value)[] values)
    {
        int index = 0;
        EditDialogue(() =>
        {
            var list = SectionToEdit(section);
            var line = new JsonObject(NodeOptions);
            foreach (var (key, value) in values) Put(line, key, value);
            index = Math.Clamp(at, 0, list.Count);
            list.Insert(index, line);
        });
        return index;
    }

    /// <summary>A copy of a line at <paramref name="at"/>, with these values changed; returns where it went.</summary>
    internal int CopyLine(DialogueSection section, int index, int at, params (string Key, JsonNode? Value)[] values)
    {
        EditDialogue(() =>
        {
            var line = LineAt(section, index) ?? throw new ArgumentOutOfRangeException(nameof(index));
            var copy = ParseObject(line.ToJsonString(), "the line");
            foreach (var (key, value) in values) Put(copy, key, value);
            var list = SectionToEdit(section);
            at = Math.Clamp(at, 0, list.Count);
            list.Insert(at, copy);
        });
        return at;
    }

    /// <summary>Moves a line to another place in its section; returns where it went.</summary>
    internal int MoveLine(DialogueSection section, int index, int to)
    {
        EditDialogue(() =>
        {
            var list = Section(section) ?? throw new ArgumentOutOfRangeException(nameof(index));
            var line = list[index];
            list.RemoveAt(index);
            to = Math.Clamp(to, 0, list.Count);
            list.Insert(to, line);
        });
        return to;
    }

    internal void RemoveLine(DialogueSection section, int index) => EditDialogue(() =>
    {
        var list = Section(section) ?? throw new ArgumentOutOfRangeException(nameof(index));
        list.RemoveAt(index);
    });

    // ---- speakers

    private JsonObject? SpeakersNode => dialogue["speakers"] as JsonObject;

    private JsonObject? SpeakerAt(string key) => SpeakersNode is { } all && ActualKey(all, key) is { } actual ? all[actual] as JsonObject : null;

    /// <summary>The battle's own speakers' keys that can be edited (those written as objects), in the order written.</summary>
    internal List<string> SpeakerKeys() => SpeakersNode?.Where(p => p.Value is JsonObject).Select(p => p.Key).ToList() ?? new List<string>();

    /// <summary>A speaker's text value (like "name" or "portrait"), or null when it has none.</summary>
    internal string? SpeakerText(string key, string value) => SpeakerAt(key) is { } speaker ? TextOnly(speaker, value) : null;

    /// <summary>Whether a speaker has a key set to true (like "flip").</summary>
    internal bool SpeakerFlag(string key, string value) => SpeakerAt(key) is { } speaker && Flag(speaker, value);

    /// <summary>A speaker's pair of numbers (like "offset": [x, y]), or null when it isn't two numbers.</summary>
    internal (double X, double Y)? SpeakerPair(string key, string value)
    {
        if (SpeakerAt(key)?[value] is not JsonArray pair || pair.Count != 2) return null;
        return Number(pair[0]) is double x && Number(pair[1]) is double y ? (x, y) : null;
    }

    /// <summary>Sets a speaker's values (null removes one), as one change.</summary>
    internal void SetSpeaker(string key, params (string Key, JsonNode? Value)[] values) => EditDialogue(() =>
    {
        var speaker = SpeakerAt(key) ?? throw new ArgumentException($"there is no speaker \"{key}\"", nameof(key));
        foreach (var (name, value) in values) Put(speaker, name, value);
    });

    /// <summary>
    /// A new speaker with a name and a picture; returns its key, made from the name (see
    /// <see cref="SpeakerKeyFor"/>). The key is never a speaker a line already names, nor one of
    /// <paramref name="gameIds"/>: a speaker's key wins over a game character spelled the same,
    /// so it would take over that character's lines.
    /// </summary>
    internal string AddSpeaker(string name, string portrait, IEnumerable<string>? gameIds = null)
    {
        var said = Sections.SelectMany(s => Section(s) ?? new JsonArray()).OfType<JsonObject>().Select(line => TextOnly(line, "speaker")?.Trim() ?? "");
        var taken = (SpeakersNode?.Select(p => p.Key) ?? Enumerable.Empty<string>()).Concat(said).Concat(gameIds ?? Enumerable.Empty<string>());
        string key = SpeakerKeyFor(name, taken);
        EditDialogue(() =>
        {
            if (SpeakersNode is not { } all) Put(dialogue, "speakers", all = new JsonObject(NodeOptions));
            all[key] = new JsonObject(NodeOptions) { ["name"] = name, ["portrait"] = portrait };
        });
        return key;
    }

    /// <summary>
    /// A speaker key made from a name: letters and digits in lower case, with "-" between words
    /// ("Mantis Queen" gives "mantis-queen"), and "-2", "-3"... when it's taken. Never "narrator",
    /// nor "karma": the player's character is always the game's, even before the game's characters load.
    /// </summary>
    internal static string SpeakerKeyFor(string name, IEnumerable<string> taken)
    {
        var sb = new StringBuilder();
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        string stem = sb.ToString().Trim('-');
        if (stem.Length > DialogueReader.MaxKey - 3) stem = stem.Substring(0, DialogueReader.MaxKey - 3).Trim('-');
        if (stem.Length == 0) stem = "speaker";
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase) { DialogueReader.Narrator, DialogueReader.Player };
        string key = stem;
        for (int n = 2; used.Contains(key); n++) key = $"{stem}-{n}";
        return key;
    }

    /// <summary>A speaker's other faces, as name and file, in the order written (only those naming a file).</summary>
    internal List<(string Name, string File)> SpeakerExpressions(string key)
    {
        var list = new List<(string, string)>();
        if (SpeakerAt(key)?["expressions"] is not JsonObject faces) return list;
        foreach (var p in faces)
            if (p.Value is JsonValue v && v.TryGetValue(out string? file) && file != null) list.Add((p.Key, file));
        return list;
    }

    /// <summary>Adds a face to a speaker, or gives the one with that name (in any letter case) a new file.</summary>
    internal void SetSpeakerExpression(string key, string name, string file) => EditDialogue(() =>
    {
        var speaker = SpeakerAt(key) ?? throw new ArgumentException($"there is no speaker \"{key}\"", nameof(key));
        if (speaker["expressions"] is not JsonObject faces) Put(speaker, "expressions", faces = new JsonObject(NodeOptions));
        Put(faces, name, file);
    });

    /// <summary>Renames one of a speaker's faces, in its place; its lines keep it.</summary>
    internal void RenameSpeakerExpression(string key, string name, string newName) => EditDialogue(() =>
    {
        if (SpeakerAt(key)?["expressions"] is not JsonObject faces || ActualKey(faces, name) is not { } actual) return;
        // Rebuilt in the same order, with the new name in the old one's place.
        var pairs = faces.Select(p => (p.Key, Json: p.Value?.ToJsonString())).ToList();
        faces.Clear();
        foreach (var (k, json) in pairs)
            faces[k == actual ? newName : k] = json == null ? null : JsonNode.Parse(json, NodeOptions);
        ForLinesOf(key, (_, line) =>
        {
            if (string.Equals(TextOnly(line, "expression")?.Trim(), actual, StringComparison.OrdinalIgnoreCase)) Put(line, "expression", newName);
        });
    });

    /// <summary>Removes one of a speaker's faces; its lines show the default face instead.</summary>
    internal void RemoveSpeakerExpression(string key, string name) => EditDialogue(() =>
    {
        if (SpeakerAt(key)?["expressions"] is not JsonObject faces || ActualKey(faces, name) is not { } actual) return;
        faces.Remove(actual);
        if (faces.Count == 0) RemoveKey(SpeakerAt(key)!, "expressions");
        ForLinesOf(key, (_, line) =>
        {
            if (string.Equals(TextOnly(line, "expression")?.Trim(), actual, StringComparison.OrdinalIgnoreCase)) RemoveKey(line, "expression");
        });
    });

    /// <summary>How many lines a speaker says, in every section.</summary>
    internal int LinesOf(string key)
    {
        int count = 0;
        ForLinesOf(key, (_, _) => count++);
        return count;
    }

    /// <summary>
    /// Removes one of the battle's own speakers. Its lines go too (<paramref name="withLines"/>),
    /// or the Narrator says them.
    /// </summary>
    internal void RemoveSpeaker(string key, bool withLines) => EditDialogue(() =>
    {
        if (SpeakersNode is not { } all || ActualKey(all, key) is not { } actual) return;
        foreach (var section in Sections)
        {
            if (Section(section) is not { } list) continue;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is not JsonObject line || !string.Equals(TextOnly(line, "speaker")?.Trim(), actual, StringComparison.OrdinalIgnoreCase)) continue;
                if (withLines) list.RemoveAt(i);
                else
                {
                    Put(line, "speaker", DialogueReader.Narrator);
                    RemoveKey(line, "expression");
                }
            }
        }
        all.Remove(actual);
    });

    // Runs over every line a speaker says, in every section.
    private void ForLinesOf(string key, Action<DialogueSection, JsonObject> each)
    {
        foreach (var section in Sections)
            foreach (var item in Section(section) ?? new JsonArray())
                if (item is JsonObject line && string.Equals(TextOnly(line, "speaker")?.Trim(), key.Trim(), StringComparison.OrdinalIgnoreCase)) each(section, line);
    }

    // ---- the chart editor's view of the lines during the song

    /// <summary>
    /// The lines during the song, in the order written, for the chart editor (items that aren't
    /// lines are left out). Each knows its place in the list, as the Dialogue page finds lines.
    /// </summary>
    internal List<DialogueCue> DuringCues()
    {
        var cues = new List<DialogueCue>();
        var list = Section(DialogueSection.During) ?? new JsonArray();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is not JsonObject line) continue;
            var cue = CueOf(line);
            cue.Place = i;
            cues.Add(cue);
        }
        return cues;
    }

    /// <summary>
    /// Puts the chart editor's lines during the song in place of these, in its order, as one
    /// change. What each line had that the chart editor doesn't change is kept as it was.
    /// </summary>
    internal void SetDuringCues(IEnumerable<DialogueCue> cues) => EditDialogue(() =>
    {
        // Items that aren't lines (the loader leaves them out) stay, after the lines.
        var odd = (Section(DialogueSection.During) ?? new JsonArray()).Where(n => n is not JsonObject).Select(n => n?.ToJsonString()).ToList();
        var list = new JsonArray();
        foreach (var cue in cues) list.Add(LineOf(cue));
        foreach (var json in odd) list.Add(json == null ? null : JsonNode.Parse(json, NodeOptions));
        if (list.Count == 0) RemoveKey(dialogue, DialogueReader.Key(DialogueSection.During));
        else Put(dialogue, DialogueReader.Key(DialogueSection.During), list);
    });

    private static DialogueCue CueOf(JsonObject line) => new()
    {
        Json = line.ToJsonString(),
        Speaker = TextOnly(line, "speaker") ?? "",
        Expression = TextOnly(line, "expression"),
        Name = TextOnly(line, "name"),
        Text = TextOnly(line, "text") ?? "",
        Time = GetNumber(line, "time"),
        Beat = GetNumber(line, "beat"),
        Duration = GetNumber(line, "duration"),
        Pause = Flag(line, "pause"),
    };

    // A cue as a line again: only what differs from the line it came from is written.
    private static JsonObject LineOf(DialogueCue cue)
    {
        var line = ParseAny(cue.Json) as JsonObject ?? new JsonObject(NodeOptions);
        var was = CueOf(line);
        if (was.Speaker != cue.Speaker) Put(line, "speaker", cue.Speaker);
        if (was.Expression != cue.Expression) Put(line, "expression", cue.Expression);
        if (was.Text != cue.Text) Put(line, "text", cue.Text);
        if (was.Time != cue.Time) Put(line, "time", ArtNumberNode(cue.Time));
        if (was.Beat != cue.Beat) Put(line, "beat", ArtNumberNode(cue.Beat));
        if (was.Duration != cue.Duration) Put(line, "duration", ArtNumberNode(cue.Duration));
        if (was.Pause != cue.Pause) Put(line, "pause", cue.Pause ? JsonValue.Create(true) : null);
        return line;
    }

    // ---- small JSON pieces for the dialogue

    private static bool Flag(JsonObject node, string key) => node[key] is JsonValue v && v.TryGetValue(out bool on) && on;

    // A key as the object has it, in any letter case; null when it has none.
    private static string? ActualKey(JsonObject node, string key) =>
        node.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.Ordinal))
        ?? node.Select(p => p.Key).FirstOrDefault(k => k.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase));

    // Sets a value where the key is (in whatever letter case it's written), or adds it; null removes it.
    private static void Put(JsonObject node, string key, JsonNode? value)
    {
        string? actual = ActualKey(node, key);
        if (value == null)
        {
            if (actual != null) node.Remove(actual);
            return;
        }
        node[actual ?? key] = value;
    }

    private static void Put(JsonObject node, string key, string? value) => Put(node, key, value == null ? null : JsonValue.Create(value));

    private static void RemoveKey(JsonObject node, string key)
    {
        if (ActualKey(node, key) is { } actual) node.Remove(actual);
    }

    private static GearDefinition ReadGear(JsonNode? node, List<string> problems)
    {
        if (node == null) return new GearDefinition();
        try
        {
            if (node is not JsonObject) throw new InvalidDataException("\"gear\" must be an object");
            return node.Deserialize<GearDefinition>(BattlePackage.JsonOptions) ?? new GearDefinition();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
        {
            problems.Add($"the gear couldn't be read ({ex.Message}); it shows as the player's own");
            return new GearDefinition();
        }
    }

    // ---- JSON helpers -----------------------------------------------------------------------------

    private static void CheckManifest(JsonObject root)
    {
        int format = (int)(GetNumber(root, "format") ?? 0);
        if (format > BattlePackage.FormatVersion) throw new InvalidDataException($"made for a newer version (format {format})");
        if (format != BattlePackage.FormatVersion) throw new InvalidDataException($"{BattlePackage.ManifestName} needs \"format\": {BattlePackage.FormatVersion}");
        if (!"battle".Equals(GetString(root, "kind").Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{BattlePackage.ManifestName} needs \"kind\": \"battle\"");
        if (!Guid.TryParse(GetString(root, "id").Trim(), out _))
            throw new InvalidDataException($"{BattlePackage.ManifestName} needs an \"id\" that is a GUID");
    }

    /// <summary>Parses battle.json text and checks it's a battle (format, kind and id), for imports. The id comes back in its plain form.</summary>
    internal static (string Id, string Title) Check(string json)
    {
        var root = ParseObject(json, BattlePackage.ManifestName);
        CheckManifest(root);
        return (NormalizeId(GetString(root, "id")), CleanLine(GetString(root, "title")));
    }

    /// <summary>
    /// Every text in battle.json and in the enemy's and the dialogue's own files, and the chart's
    /// #MUSIC: anything that may name one of the battle's files. Null when the enemy's or the
    /// dialogue's file can't be read, since what it names isn't known then.
    /// </summary>
    internal List<string>? NamedFiles()
    {
        if (EnemyLocked != null || DialogueLocked != null) return null;
        var names = Texts(root);
        if (enemyFile != null) names.AddRange(Texts(enemy));
        if (dialogueFile != null) names.AddRange(Texts(dialogue));
        string music = ChartMusic();
        if (music.Length > 0) names.Add(music);
        return names;
    }

    /// <summary>Every text value in a JSON tree, however deep.</summary>
    internal static List<string> Texts(JsonNode? node)
    {
        var texts = new List<string>();
        AddTexts(node, texts);
        return texts;
    }

    private static void AddTexts(JsonNode? node, List<string> texts)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj) AddTexts(pair.Value, texts);
                break;
            case JsonArray array:
                foreach (var item in array) AddTexts(item, texts);
                break;
            case JsonValue value when value.TryGetValue(out string? text) && text != null:
                texts.Add(text);
                break;
        }
    }

    /// <summary>Parses a JSON file's text the way battle.json is read (comments and trailing commas allowed).</summary>
    internal static JsonNode? ParseAny(string text)
    {
        var node = JsonNode.Parse(text, NodeOptions, DocumentOptions);
        Settle(node);
        return node;
    }

    // A JSON tree reads each object's keys only when it is first used, and a key given twice
    // (in two letter cases) throws then. Reading them all at once finds that while parsing,
    // not later in the middle of an edit or a save.
    private static void Settle(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj) Settle(pair.Value);
                break;
            case JsonArray array:
                foreach (var item in array) Settle(item);
                break;
        }
    }

    private static JsonObject ParseObject(string text, string name)
    {
        try
        {
            var node = JsonNode.Parse(text, NodeOptions, DocumentOptions) as JsonObject
                ?? throw new InvalidDataException($"{name} isn't a JSON object");
            Settle(node);
            return node;
        }
        catch (ArgumentException ex)
        {
            // Two keys that differ only in letter case (the exception names the second one).
            string key = string.IsNullOrEmpty(ex.ParamName) ? "a key" : $"\"{ex.ParamName}\"";
            throw new InvalidDataException($"{name} has {key} twice (letter case doesn't make two keys different)");
        }
    }

    /// <summary>
    /// Whether an exception is trouble with one battle's files: one that can't be read or isn't
    /// valid, or a name in battle.json that can't be a path (a control character in it makes .NET
    /// throw ArgumentException). Such a battle shows the problem, and the others still list.
    /// </summary>
    internal static bool IsFileProblem(Exception ex) =>
        ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    internal static string ReadText(string path, long maxBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"{Path.GetFileName(path)} is missing", path);
        if (info.Length > maxBytes) throw new InvalidDataException($"{Path.GetFileName(path)} is too big");
        return new UTF8Encoding(false).GetString(File.ReadAllBytes(path)).TrimStart((char)0xFEFF);
    }

    /// <summary>Writes a temporary file next to <paramref name="path"/>, then puts it in place.</summary>
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

    private static string GetString(JsonObject node, string key)
    {
        var value = node[key];
        if (value is JsonValue v)
        {
            if (v.TryGetValue(out string? s)) return s ?? "";
            return v.ToJsonString();
        }
        return "";
    }

    // A value only when it's a text, like "dialogue", which may also be an object.
    private static string? TextOnly(JsonObject node, string key) =>
        node[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static double? GetNumber(JsonObject node, string key)
    {
        if (node[key] is not JsonValue v) return null;
        // A value read from the file answers as any number type; one set here only as its own type.
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out float f)) return f;
        if (v.TryGetValue(out decimal m)) return (double)m;
        // The loader also takes numbers written as strings, but not "NaN" or "Infinity".
        if (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d)) return d;
        return null;
    }

    private void SetString(JsonObject node, string key, string value)
    {
        if (node[key] is JsonValue v && v.TryGetValue(out string? old) && old == value) return;
        node[key] = value;
        changes++;
    }

    private void SetNumber(JsonObject node, string key, double value)
    {
        if (GetNumber(node, key) == value) return;
        // Whole numbers are written without ".0".
        node[key] = value == Math.Floor(value) && Math.Abs(value) < 1e15 ? JsonValue.Create((long)value) : JsonValue.Create(value);
        changes++;
    }

    private void Remove(JsonObject node, string key)
    {
        // The node may have the key in another letter case; the case-insensitive lookup finds it.
        string? actual = node.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (actual == null) return;
        node.Remove(actual);
        changes++;
    }

    /// <summary>One line of text: line breaks become spaces.</summary>
    internal static string CleanLine(string? text) => (text ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>Text that may have several lines, each line break written as "\n".</summary>
    internal static string CleanText(string? text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
}
