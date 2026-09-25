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
    private string audioAtLoad;
    private int changes, savedChanges;

    /// <summary>The battle's folder.</summary>
    internal string Folder { get; }

    /// <summary>Things found while loading that the creator should show, like an enemy file that couldn't be read.</summary>
    internal List<string> Problems { get; } = new();

    /// <summary>Whether anything changed since the last load or save.</summary>
    internal bool Dirty => changes != savedChanges;

    private BattleDraft(string folder, JsonObject root, JsonObject enemy, string? enemyFile, bool enemyAttached)
    {
        Folder = folder;
        this.root = root;
        this.enemy = enemy;
        this.enemyFile = enemyFile;
        this.enemyAttached = enemyAttached;
        gear = ReadGear(root["gear"], Problems);
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
        string? enemyFile = null;
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
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
                {
                    problems.Add($"{enemyFile} couldn't be read ({ex.Message}); saving an enemy change writes a new one");
                    enemy = new JsonObject(NodeOptions);
                }
                break;
            default:
                // No enemy yet: made when the creator first changes it.
                enemy = new JsonObject(NodeOptions);
                attached = false;
                break;
        }
        var draft = new BattleDraft(folder, root, enemy, enemyFile, attached);
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
        if (enemyFile != null && enemyChanged)
            WriteAtomic(Path.Combine(Folder, enemyFile.Replace('/', Path.DirectorySeparatorChar)), enemy.ToJsonString(WriteOptions) + "\n");
        WriteAtomic(ManifestPath, root.ToJsonString(WriteOptions) + "\n");
        string audio = Audio;
        if (!audio.Equals(audioAtLoad, StringComparison.Ordinal)) UpdateChartMusic(audio);
        audioAtLoad = audio;
        enemyChanged = false;
        savedChanges = changes;
    }

    /// <summary>battle.json as it would be saved, for checks.</summary>
    internal string ManifestJson() => root.ToJsonString(WriteOptions);

    private void UpdateChartMusic(string audio)
    {
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

    // ---- the battle ---------------------------------------------------------------------------------

    internal string Id => GetString(root, "id");
    internal int Format => (int)(GetNumber(root, "format") ?? 0);
    internal string Kind => GetString(root, "kind");

    internal string Title { get => GetString(root, "title"); set => SetString(root, "title", CleanLine(value)); }
    internal string Artist { get => GetString(root, "artist"); set => SetString(root, "artist", CleanLine(value)); }
    internal string Author { get => GetString(root, "author"); set => SetString(root, "author", CleanLine(value)); }

    /// <summary>The text on the battle's arcade card; several lines are fine.</summary>
    internal string Lore
    {
        get => GetString(root, "lore");
        set => SetString(root, "lore", value.Replace("\r\n", "\n").Replace('\r', '\n').Trim());
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

    /// <summary>The song file, as a path inside the battle.</summary>
    internal string Audio { get => GetString(root, "audio").Trim(); set => SetString(root, "audio", value.Trim()); }

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

    private int? inferredLanes;

    /// <summary>
    /// 4 or 5, fixed when the battle is made. Read from the chart when battle.json doesn't say (as
    /// the loader does); that is read once, since nothing here changes the lanes.
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
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        return 4;
    }

    // ---- the enemy ----------------------------------------------------------------------------------

    /// <summary>The enemy's own name; empty shows the placeholder's.</summary>
    internal string EnemyName
    {
        get => GetString(enemy, "name");
        set
        {
            string name = CleanLine(value);
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
            if (value) EnemySet("advanced", true);
            else EnemyRemove("advanced");
        }
    }

    /// <summary>A stat the battle sets, or null when the placeholder's own is used.</summary>
    internal double? Stat(string key) => enemy["stats"] is JsonObject stats ? GetNumber(stats, key) : null;

    internal void SetStat(string key, double? value)
    {
        if (Stat(key) == value) return;
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

    internal void SetOwnInfo(bool own)
    {
        if (own == OwnInfo) return;
        if (own) enemy["info"] = new JsonArray();
        else enemy.Remove("info");
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
        var (oldTitle, oldDescription) = InfoBox(index);
        if (OwnInfo && (title == null || CleanLine(title) == oldTitle) && (description == null || description.Trim() == oldDescription)) return;
        if (enemy["info"] is not JsonArray boxes) enemy["info"] = boxes = new JsonArray();
        while (boxes.Count <= index) boxes.Add(new JsonObject(NodeOptions));
        if (boxes[index] is not JsonObject box) boxes[index] = box = new JsonObject(NodeOptions);
        if (title != null) box["title"] = CleanLine(title);
        if (description != null) box["description"] = description.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        EnemyTouched();
    }

    // Boxes with neither text would show as empty boxes in the battle, so they aren't saved.
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

    // The keys come from GearDefinition itself, so they're exactly what BattlePackage reads.
    private void WriteGear()
    {
        root["gear"] = gear.IsSet
            ? JsonSerializer.SerializeToNode(gear, WriteOptions)
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

    /// <summary>Parses battle.json text and checks it's a battle (format, kind and id), for imports.</summary>
    internal static (string Id, string Title) Check(string json)
    {
        var root = ParseObject(json, BattlePackage.ManifestName);
        CheckManifest(root);
        return (GetString(root, "id").Trim(), CleanLine(GetString(root, "title")));
    }

    private static JsonObject ParseObject(string text, string name)
    {
        try
        {
            return JsonNode.Parse(text, NodeOptions, DocumentOptions) as JsonObject
                ?? throw new InvalidDataException($"{name} isn't a JSON object");
        }
        catch (ArgumentException ex)
        {
            // Two keys that differ only in letter case.
            throw new InvalidDataException($"{name} has a key twice ({ex.Message})");
        }
    }

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
}
