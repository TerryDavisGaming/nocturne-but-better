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
/// they were; the properties read and write that tree. An enemy kept in its own file (like
/// "enemy/enemy.json") is edited and saved there. Saving writes a temporary file and then puts it
/// in place, so a failed save never leaves half a file.
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

    /// <summary>Whether anything changed since the last load or save.</summary>
    internal bool Dirty => changes != savedChanges;

    private BattleDraft(string folder, JsonObject root, JsonObject enemy, string? enemyFile, bool enemyAttached, string? enemyLocked = null)
    {
        Folder = folder;
        this.root = root;
        this.enemy = enemy;
        this.enemyFile = enemyFile;
        this.enemyAttached = enemyAttached;
        EnemyLocked = enemyLocked;
        gear = ReadGear(root["gear"], Problems);
        // The loader's own reader, so the creator shows a level (and its problems) exactly as the arcade will use it.
        level = LevelDefinition.Read(JsonSerializer.Deserialize<JsonElement>(root["level"]?.ToJsonString() ?? "null"), Problems);
        audioAtLoad = Audio;
    }

    private string ManifestPath => Path.Combine(Folder, BattlePackage.ManifestName);

    // ---- loading and saving ---------------------------------------------------------------------

    /// <summary>Reads a battle folder's battle.json (and its enemy file, when it names one).</summary>
    internal static BattleDraft Load(string folder)
    {
        string path = Path.Combine(folder, BattlePackage.ManifestName);
        var root = ParseObject(ReadText(path, BattlePackage.MaxJsonBytes), BattlePackage.ManifestName);
        CheckManifest(root);
        var problems = new List<string>();
        JsonObject enemy;
        string? enemyFile = null, locked = null;
        bool attached = true;
        switch (root["enemy"])
        {
            case JsonObject inline:
                enemy = inline;
                break;
            case JsonValue value when value.TryGetValue(out string? name):
                enemyFile = PackageFiles.SafeName(name ?? "");
                if (enemyFile == null)
                {
                    // Saving puts the enemy into battle.json instead.
                    problems.Add("\"enemy\" names a file outside the battle; the enemy is saved in battle.json");
                    enemy = new JsonObject(NodeOptions);
                    attached = false;
                    break;
                }
                try { enemy = ParseObject(ReadText(Path.Combine(folder, enemyFile.Replace('/', Path.DirectorySeparatorChar)), BattlePackage.MaxJsonBytes), enemyFile); }
                catch (FileNotFoundException)
                {
                    problems.Add($"{enemyFile} is missing; saving an enemy change writes a new one");
                    enemy = new JsonObject(NodeOptions);
                }
                catch (Exception ex) when (IsFileProblem(ex))
                {
                    // The file is there: it is never saved over, so nothing in it is lost.
                    locked = $"{enemyFile} can't be read ({ex.Message}). Fix it by hand, then open the battle again. Until then the enemy can't be changed here.";
                    problems.Add(locked);
                    enemy = new JsonObject(NodeOptions);
                }
                break;
            default:
                // No enemy yet: made when the creator first changes it.
                enemy = new JsonObject(NodeOptions);
                attached = false;
                break;
        }
        var draft = new BattleDraft(folder, root, enemy, enemyFile, attached, locked);
        draft.Problems.InsertRange(0, problems);
        return draft;
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
        var draft = new BattleDraft(folder, root, (JsonObject)root["enemy"]!, null, true);
        draft.changes++;
        return draft;
    }

    /// <summary>
    /// Writes battle.json (and the enemy's own file when it has one and changed). When the audio
    /// changed, the chart's #MUSIC is changed to match, so the .sm names the same song.
    /// </summary>
    internal void Save()
    {
        PruneInfoBoxes();
        Directory.CreateDirectory(Folder);
        if (enemyFile != null && enemyChanged && EnemyLocked == null)
            WriteAtomic(Path.Combine(Folder, enemyFile.Replace('/', Path.DirectorySeparatorChar)), enemy.ToJsonString(WriteOptions) + "\n");
        WriteAtomic(ManifestPath, root.ToJsonString(WriteOptions) + "\n");
        string audio = Audio;
        if (!audio.Equals(audioAtLoad, StringComparison.Ordinal))
        {
            UpdateChartMusic(audio);
            ChartChanged();
        }
        audioAtLoad = audio;
        enemyChanged = false;
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
    /// BattleChartTarget.ChartFileProblem): an .sm file of its own, not the song, card or enemy file.
    /// </summary>
    internal string? ChartFileProblem() => BattleChartTarget.ChartFileProblem(Folder, ChartPath,
        ("song", EffectiveAudio), ("card image", Card), ("enemy file", enemyFile), ("dialogue", GetString(root, "dialogue")));

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

    /// <summary>The game enemy the battle's enemy is copied from (an EnemyData asset name); empty means Mantis.</summary>
    internal string Placeholder
    {
        get => GetString(enemy, "placeholder").Trim();
        set
        {
            if (Placeholder == value && EnemyMode.Equals("placeholder", StringComparison.OrdinalIgnoreCase)) return;
            CheckEnemy();
            EnemySet("mode", "placeholder");
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

    /// <summary>How many of the consumable the battle gives (1 to 99), when it gives one.</summary>
    internal int ConsumableCount => gear.consumable?.count is int count ? Math.Clamp(count, 1, GearDefinition.MaxCount) : 1;

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
            default: gear.consumable = id == null ? null : new GearConsumable { item = id, count = ConsumableCount }; break;
        }
        WriteGear();
    }

    internal void SetConsumableCount(int count)
    {
        count = Math.Clamp(count, 1, GearDefinition.MaxCount);
        if (gear.consumable == null || gear.consumable.count == count) return;
        gear.consumable.count = count;
        WriteGear();
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
    /// Every text in battle.json and in the enemy's own file, and the chart's #MUSIC: anything
    /// that may name one of the battle's files. Null when the enemy's file can't be read, since
    /// what it names isn't known then.
    /// </summary>
    internal List<string>? NamedFiles()
    {
        if (EnemyLocked != null) return null;
        var names = Texts(root);
        if (enemyFile != null) names.AddRange(Texts(enemy));
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

    private static double? GetNumber(JsonObject node, string key)
    {
        if (node[key] is not JsonValue v) return null;
        // A value read from the file answers as any number type; one set here only as its own type.
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out float f)) return f;
        if (v.TryGetValue(out decimal m)) return (double)m;
        // The loader also takes numbers written as strings.
        if (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
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
