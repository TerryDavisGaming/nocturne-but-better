using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace NocturneFlatScroll;

/// <summary>
/// Reads a package's files by their names inside it: from a folder, or from a zip (and a
/// folder inside the zip). Names use '/' and can't reach outside the package. Shared by custom
/// songs and by the song files that custom charts name with #MUSIC.
/// This file has no Unity or game dependencies.
/// </summary>
internal sealed class PackageFiles
{
    private readonly string source;        // the folder, or the zip file
    private readonly string? zipFolder;    // null for a folder; "" or "name/" inside the zip
    private readonly bool zipRootFallback; // also look at the zip's root, as chart packs do

    private PackageFiles(string source, string? zipFolder, bool zipRootFallback)
    {
        this.source = source;
        this.zipFolder = zipFolder;
        this.zipRootFallback = zipRootFallback;
    }

    internal static PackageFiles Folder(string folder) => new(folder, null, false);

    /// <param name="folderInZip">Where the package's files are inside the zip: "" for its root.</param>
    /// <param name="rootFallback">Look for a name at the zip's root when the folder doesn't have it.</param>
    internal static PackageFiles Zip(string zipFile, string folderInZip, bool rootFallback = false)
    {
        string folder = folderInZip.Replace('\\', '/').Trim('/');
        return new PackageFiles(zipFile, folder.Length == 0 ? "" : folder + "/", rootFallback);
    }

    internal bool IsZip => zipFolder != null;

    /// <summary>A name as a path inside the package, or null if it points elsewhere.</summary>
    internal static string? SafeName(string name)
    {
        name = name.Replace('\\', '/').Trim();
        if (name.Length == 0 || name.Contains("..") || name.StartsWith("/") || name.Contains(':')) return null;
        return name;
    }

    private static string Checked(string name) =>
        SafeName(name) ?? throw new InvalidDataException($"\"{name}\" must be a file inside the package");

    private string LoosePath(string name) => Path.Combine(source, name.Replace('/', Path.DirectorySeparatorChar));

    internal bool Exists(string name)
    {
        name = Checked(name);
        if (!IsZip) return File.Exists(LoosePath(name));
        using var zip = ZipFile.OpenRead(source);
        return Entry(zip, name) != null;
    }

    internal byte[] ReadAllBytes(string name, long maxBytes)
    {
        name = Checked(name);
        if (!IsZip)
        {
            string path = LoosePath(name);
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException($"{name} is missing", path);
            if (info.Length > maxBytes) throw new InvalidDataException($"{name} is too big ({info.Length / (1024 * 1024)} MB)");
            return File.ReadAllBytes(path);
        }
        using var zip = ZipFile.OpenRead(source);
        var entry = Entry(zip, name) ?? throw new FileNotFoundException($"the package has no {name}");
        if (entry.Length > maxBytes) throw new InvalidDataException($"{name} is too big ({entry.Length / (1024 * 1024)} MB)");
        using var stream = entry.Open();
        using var copy = new MemoryStream((int)Math.Max(0, entry.Length));
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    internal string ReadAllText(string name, long maxBytes)
    {
        var bytes = ReadAllBytes(name, maxBytes);
        // UTF-8, with or without a byte order mark.
        return new UTF8Encoding(false).GetString(bytes).TrimStart((char)0xFEFF);
    }

    /// <summary>The file's size and time, which change when it's replaced; for caches.</summary>
    internal string Stamp(string name)
    {
        name = Checked(name);
        var info = new FileInfo(IsZip ? source : LoosePath(name));
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
    }

    /// <summary>Where a file is, for messages.</summary>
    internal string Describe(string name) =>
        IsZip ? $"{Path.GetFileName(source)}/{zipFolder}{name}" : LoosePath(name);

    private ZipArchiveEntry? Entry(ZipArchive zip, string name)
    {
        var found = Find(zip, zipFolder + name);
        if (found == null && zipRootFallback && zipFolder!.Length > 0) found = Find(zip, name);
        return found;
    }

    internal static ZipArchiveEntry? Find(ZipArchive zip, string path)
    {
        var exact = zip.GetEntry(path);
        if (exact != null) return exact;
        // Some Windows tools write '\' separators or change the case of names.
        foreach (var entry in zip.Entries)
            if (entry.FullName.Replace('\\', '/').Equals(path, StringComparison.OrdinalIgnoreCase)) return entry;
        return null;
    }
}

/// <summary>battle.json, the manifest of a custom battle package.</summary>
internal sealed class BattleManifest
{
    public int format { get; set; }
    public string? kind { get; set; }
    public string? id { get; set; }
    public string? title { get; set; }
    public string? artist { get; set; }
    public string? author { get; set; }
    public string? lore { get; set; }
    public string? card { get; set; }
    public string? audio { get; set; }
    public double? previewStart { get; set; }
    public int? lanes { get; set; }
    public string? chart { get; set; }
    // An enemy object, or the path of a JSON file holding one.
    public JsonElement enemy { get; set; }
    // Reserved for boss-style dialogue.
    public string? dialogue { get; set; }
    // The player's gear in this battle; missing means the player's own.
    public JsonElement gear { get; set; }
}

/// <summary>
/// A custom battle's enemy. Mode "placeholder" is a game enemy (its art, sounds and abilities)
/// with the song's own stats and info boxes. Mode "custom" (the player's own images and videos)
/// comes later; until then such an enemy plays as its rig, or as the placeholder.
/// </summary>
internal sealed class EnemyDefinition
{
    public string? name { get; set; }
    public string? mode { get; set; }
    // A game EnemyData asset name, like "EnemyData_Mantis".
    public string? placeholder { get; set; }
    // Custom-art mode's rig enemy.
    public string? rig { get; set; }
    // Bosses built around timelines or scripted attacks need this to be used as placeholders.
    public bool advanced { get; set; }
    public EnemyStats? stats { get; set; }
    // Up to 3 boxes in the battle's top right; leaving it out keeps the placeholder's own.
    public List<EnemyInfoBox>? info { get; set; }
}

internal sealed class EnemyStats
{
    public double? hp { get; set; }
    public double? damage { get; set; }
    public double? passiveEnergyCharge { get; set; }
    public double? energyChargeOnMiss { get; set; }
    public double? attackWindupTime { get; set; }
}

internal sealed class EnemyInfoBox
{
    public string? title { get; set; }
    public string? description { get; set; }
}

/// <summary>The equipment slots a custom battle can set, named as battle.json names them.</summary>
internal enum GearSlot { MainHand, Body, Head, OffHand, Amulet, Consumable }

/// <summary>
/// battle.json's "gear". Mode "player" (the default) fights with the player's own gear. Mode "set"
/// gives the player exactly these items for this battle only; the player's own gear is back as it
/// was afterwards. Slots hold the game's item ids (like "DBA1"); a missing or null slot is empty.
/// The game allows one consumable use per battle.
/// </summary>
internal sealed class GearDefinition
{
    internal const int MaxCount = 99;

    public string? mode { get; set; }
    public string? mainHand { get; set; }
    public string? body { get; set; }
    public string? head { get; set; }
    public string? offHand { get; set; }
    public string? amulet { get; set; }
    public GearConsumable? consumable { get; set; }
    // Health upgrades (the game's Item_ExtraHealth count); null keeps the player's own.
    public int? extraHealth { get; set; }

    internal bool IsSet => "set".Equals(mode?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The item id in a slot, or null when the slot is empty.</summary>
    internal string? ItemFor(GearSlot slot)
    {
        string? id = slot switch
        {
            GearSlot.MainHand => mainHand,
            GearSlot.Body => body,
            GearSlot.Head => head,
            GearSlot.OffHand => offHand,
            GearSlot.Amulet => amulet,
            _ => consumable?.item
        };
        return string.IsNullOrWhiteSpace(id) ? null : id!.Trim();
    }

    /// <summary>How many of the consumable the battle starts with (0 when there is none).</summary>
    internal int ConsumableCount => ItemFor(GearSlot.Consumable) == null ? 0 : Math.Clamp(consumable?.count ?? 1, 1, MaxCount);
}

internal sealed class GearConsumable
{
    public string? item { get; set; }
    public int? count { get; set; }
}

/// <summary>Which game enemies can stand in for a custom battle's enemy.</summary>
internal static class EnemyPlaceholders
{
    internal const string Default = "EnemyData_Mantis";
    internal const int MaxInfoBoxes = 3;

    // These have no art to load; a battle with them breaks.
    private static readonly HashSet<string> Broken = new(StringComparer.OrdinalIgnoreCase)
    {
        "EnemyData_GenericEnemy", "EnemyData_Roach"
    };

    // Bosses built around timelines, extra parts or scripted attacks. They only play when the
    // enemy says "advanced": true.
    private static readonly HashSet<string> Advanced = new(StringComparer.OrdinalIgnoreCase)
    {
        "EnemyData_Yako", "EnemyData_Nocturne", "EnemyData_CagedWei", "EnemyData_Sue",
        "EnemyData_WingedWei", "EnemyData_Kitsune", "EnemyData_Ladybug"
    };

    /// <summary>The EnemyData asset name to clone: the requested one when it can be used, else Mantis.</summary>
    internal static string Resolve(string? requested, bool advanced, List<string> problems)
    {
        string name = (requested ?? "").Trim();
        if (name.Length == 0) return Default;
        if (!name.StartsWith("EnemyData_", StringComparison.OrdinalIgnoreCase)) name = "EnemyData_" + name;
        if (Broken.Contains(name))
        {
            problems.Add($"{name} can't be used as an enemy (it has no art); {Default} stands in");
            return Default;
        }
        if (Advanced.Contains(name) && !advanced)
        {
            problems.Add($"{name} needs \"advanced\": true in the enemy (its fights are scripted); {Default} stands in");
            return Default;
        }
        return name;
    }
}

/// <summary>
/// A custom battle: a folder with battle.json, or a .nbbbattle zip of the same files, in the
/// CustomBattles folder. battle.json names the chart (one .sm with every difficulty), the audio
/// file, the card image and the enemy. Loading checks everything the game needs and builds the
/// chart the game plays. This file has no Unity or game dependencies.
/// </summary>
internal sealed class BattlePackage
{
    internal const int FormatVersion = 2;
    internal const string Extension = ".nbbbattle";
    internal const string ManifestName = "battle.json";
    internal const string DefaultChart = "charts/song.sm";
    /// <summary>Custom battles' score keys (and their SongData names) start with this.</summary>
    internal const string ScoreKeyPrefix = "NocturneButBetter/battle/";

    internal const long MaxJsonBytes = 1024 * 1024;
    internal const long MaxChartBytes = 8 * 1024 * 1024;
    internal const long MaxAudioBytes = 512L * 1024 * 1024;
    internal const long MaxImageBytes = 16 * 1024 * 1024;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    internal string Id = "";
    internal string Title = "";
    internal string Artist = "";
    internal string Author = "";
    internal string Lore = "";
    internal string? CardPath;
    internal string AudioPath = "";
    internal double PreviewStart;
    internal int Lanes;
    internal string ChartPath = DefaultChart;
    internal string? DialoguePath;
    internal EnemyDefinition Enemy = new();
    /// <summary>The player's gear in this battle ("player" mode unless battle.json sets it).</summary>
    internal GearDefinition Gear = new();
    /// <summary>The EnemyData asset to clone, after the checks.</summary>
    internal string EnemyPlaceholder = EnemyPlaceholders.Default;

    internal PackageFiles Files = null!;
    /// <summary>The folder or .nbbbattle file.</summary>
    internal string Location = "";
    /// <summary>Changes when any file the song uses changes.</summary>
    internal string Fingerprint = "";

    internal ChartText Chart = new();
    /// <summary>The authored chart for each of the game's six difficulty slots, or null.</summary>
    internal ChartText.NoteBlock?[] Slots = new ChartText.NoteBlock?[6];
    /// <summary>The .sm the game plays: six blocks in slot order.</summary>
    internal string PlayableText = "";
    /// <summary>The last note row over every difficulty, so each one runs as long as the song.</summary>
    internal int LastNoteRow;
    /// <summary>
    /// #OFFSET, as in StepMania: beat 0 is at audio file time -OFFSET. The game's chart reader
    /// applies it; the audio itself always starts with the battle's clock.
    /// </summary>
    internal double Offset;
    /// <summary>Things that don't stop the song from playing, for the log.</summary>
    internal readonly List<string> Problems = new();

    internal string ScoreKey => ScoreKeyPrefix + Id;

    internal string DisplayName => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Title} ({Artist})";

    /// <summary>
    /// Loads a package from a folder with battle.json, or from a .nbbbattle file. A test play
    /// loads it with what the editors have in memory instead of some of its files:
    /// <paramref name="chartText"/> for the chart file, <paramref name="manifestJson"/> for
    /// battle.json (checked the same way), and <paramref name="lanes"/> and
    /// <paramref name="audio"/> over what battle.json and the chart say.
    /// </summary>
    internal static BattlePackage Load(string path, string? chartText = null, string? manifestJson = null, int? lanes = null, string? audio = null)
    {
        PackageFiles files;
        if (Directory.Exists(path)) files = PackageFiles.Folder(path);
        else if (File.Exists(path) && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) files = OpenZip(path);
        else throw new FileNotFoundException("not a battle folder or " + Extension + " file", path);

        var manifest = JsonSerializer.Deserialize<BattleManifest>(manifestJson ?? files.ReadAllText(ManifestName, MaxJsonBytes), JsonOptions)
            ?? throw new InvalidDataException(ManifestName + " is empty");
        if (manifest.format > FormatVersion) throw new InvalidDataException($"made for a newer version (format {manifest.format})");
        if (manifest.format != FormatVersion) throw new InvalidDataException($"{ManifestName} needs \"format\": {FormatVersion}");
        if (!"battle".Equals(manifest.kind?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{ManifestName} needs \"kind\": \"battle\"");
        if (!Guid.TryParse(manifest.id?.Trim(), out var guid))
            throw new InvalidDataException($"{ManifestName} needs an \"id\" that is a GUID, like \"{Guid.Empty}\"");

        var song = new BattlePackage
        {
            Id = guid.ToString("D"),
            Files = files,
            Location = path,
            Title = Clean(manifest.title),
            Artist = Clean(manifest.artist),
            Author = Clean(manifest.author),
            Lore = (manifest.lore ?? "").Trim(),
            PreviewStart = Math.Max(0, manifest.previewStart ?? 0),
            DialoguePath = manifest.dialogue == null ? null : PackageFiles.SafeName(manifest.dialogue)
        };
        if (song.Title.Length == 0) song.Title = Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/'));
        var stamps = new List<string> { files.Stamp(ManifestName) };

        // The chart holds every difficulty.
        song.ChartPath = PackageFiles.SafeName(manifest.chart ?? DefaultChart)
            ?? throw new InvalidDataException("\"chart\" must be a file inside the package");
        song.Chart = ChartText.Parse(chartText ?? files.ReadAllText(song.ChartPath, MaxChartBytes));
        if (chartText == null) stamps.Add(files.Stamp(song.ChartPath));
        song.Lanes = lanes ?? manifest.lanes ?? InferLanes(song.Chart);
        if (song.Lanes != 4 && song.Lanes != 5) throw new InvalidDataException($"\"lanes\" must be 4 or 5, not {song.Lanes}");
        song.Slots = song.Chart.SongSlots(song.Lanes, song.Problems);
        if (song.Slots.All(s => s == null))
            throw new InvalidDataException("the chart has no playable difficulty" + (song.Problems.Count > 0 ? ": " + string.Join("; ", song.Problems) : ""));
        song.PlayableText = song.Chart.BuildPlayableSong(song.Slots);
        song.LastNoteRow = song.Slots.Where(s => s != null).Max(s => ChartText.LastNoteRow(s!));
        song.Offset = double.TryParse(song.Chart.GetTag("OFFSET"), NumberStyles.Float, CultureInfo.InvariantCulture, out double offset) ? offset : 0;
        if (Math.Abs(song.Offset) > LeadIn.MaxOffset)
            throw new InvalidDataException($"#OFFSET is {song.Offset:0.###} s; it can be at most {LeadIn.MaxOffset:0} s either way");
        // The battle's clock starts with the audio, so a chart that starts before it loses its start.
        if (song.Offset > 0.001)
            song.Problems.Add($"#OFFSET is {song.Offset.ToString("0.###", CultureInfo.InvariantCulture)} s, so beat 0 comes before the audio starts; notes in the chart's first {song.Offset.ToString("0.###", CultureInfo.InvariantCulture)} s can't be played");

        // The audio: battle.json's, else the chart's #MUSIC.
        string audioName = audio ?? manifest.audio ?? song.Chart.GetTag("MUSIC") ?? "";
        song.AudioPath = PackageFiles.SafeName(audioName) ?? throw new InvalidDataException("the song names no audio file (\"audio\" in " + ManifestName + ")");
        if (!files.Exists(song.AudioPath)) throw new InvalidDataException($"the audio file {song.AudioPath} is missing");
        stamps.Add(files.Stamp(song.AudioPath));

        if (!string.IsNullOrWhiteSpace(manifest.card))
        {
            var card = PackageFiles.SafeName(manifest.card!);
            if (card == null) song.Problems.Add("\"card\" must be a file inside the package");
            else if (!files.Exists(card)) song.Problems.Add($"the card image {card} is missing");
            else
            {
                song.CardPath = card;
                stamps.Add(files.Stamp(card));
            }
        }

        song.Enemy = ReadEnemy(manifest.enemy, files, song.Problems, stamps);
        string requested = song.Enemy.placeholder ?? "";
        if ("custom".Equals(song.Enemy.mode?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            song.Problems.Add("custom enemy art comes in a later version; the enemy plays as its rig for now");
            requested = song.Enemy.rig ?? song.Enemy.placeholder ?? "";
        }
        song.EnemyPlaceholder = EnemyPlaceholders.Resolve(requested, song.Enemy.advanced, song.Problems);
        if (song.Enemy.info != null && song.Enemy.info.Count > EnemyPlaceholders.MaxInfoBoxes)
            song.Problems.Add($"the enemy has {song.Enemy.info.Count} info boxes; the first {EnemyPlaceholders.MaxInfoBoxes} show");

        song.Gear = ReadGear(manifest.gear, song.Problems);

        song.Fingerprint = string.Join("|", stamps);
        return song;
    }

    // The items themselves are checked against the game's database when the battle starts.
    private static GearDefinition ReadGear(JsonElement gear, List<string> problems)
    {
        try
        {
            if (gear.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new GearDefinition();
            if (gear.ValueKind != JsonValueKind.Object) throw new InvalidDataException("\"gear\" must be an object");
            var read = gear.Deserialize<GearDefinition>(JsonOptions) ?? new GearDefinition();
            string mode = read.mode?.Trim() ?? "";
            if (mode.Length > 0 && !mode.Equals("player", StringComparison.OrdinalIgnoreCase) && !read.IsSet)
                throw new InvalidDataException($"\"mode\" must be \"player\" or \"set\", not \"{mode}\"");
            // A number out of range is kept in range; the rest of the gear still counts.
            if (read.extraHealth is int health && (health < 0 || health > GearDefinition.MaxCount))
            {
                problems.Add($"\"extraHealth\" is {health}; it is kept between 0 and {GearDefinition.MaxCount}");
                read.extraHealth = Math.Clamp(health, 0, GearDefinition.MaxCount);
            }
            if (read.consumable?.count is int count && (count < 1 || count > GearDefinition.MaxCount))
                problems.Add($"the consumable's count is {count}; it is kept between 1 and {GearDefinition.MaxCount}");
            return read;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            problems.Add($"the gear couldn't be read ({ex.Message}); the player uses their own gear");
            return new GearDefinition();
        }
    }

    private static string Clean(string? text) => (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static int InferLanes(ChartText chart)
    {
        foreach (var block in chart.Blocks)
            if (ChartText.HasNotes(block)) return block.Lanes;
        return 4;
    }

    /// <summary>A .nbbbattle zip: battle.json at its root, or inside its one top folder.</summary>
    private static PackageFiles OpenZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (PackageFiles.Find(zip, ManifestName) != null) return PackageFiles.Zip(path, "");
        var folders = zip.Entries
            .Select(e => e.FullName.Replace('\\', '/'))
            .Where(n => n.EndsWith("/" + ManifestName, StringComparison.OrdinalIgnoreCase) && n.Count(c => c == '/') == 1)
            .Select(n => n.Substring(0, n.IndexOf('/')))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folders.Count == 1) return PackageFiles.Zip(path, folders[0]);
        throw new InvalidDataException(folders.Count == 0 ? "no " + ManifestName + " in it" : "more than one " + ManifestName + " in it");
    }

    private static EnemyDefinition ReadEnemy(JsonElement enemy, PackageFiles files, List<string> problems, List<string> stamps)
    {
        try
        {
            switch (enemy.ValueKind)
            {
                case JsonValueKind.Object:
                    return enemy.Deserialize<EnemyDefinition>(JsonOptions) ?? new EnemyDefinition();
                case JsonValueKind.String:
                    string path = PackageFiles.SafeName(enemy.GetString() ?? "") ?? throw new InvalidDataException("\"enemy\" must be a file inside the package");
                    stamps.Add(files.Stamp(path));
                    return JsonSerializer.Deserialize<EnemyDefinition>(files.ReadAllText(path, MaxJsonBytes), JsonOptions) ?? new EnemyDefinition();
                case JsonValueKind.Undefined:
                case JsonValueKind.Null:
                    return new EnemyDefinition();
                default:
                    throw new InvalidDataException("\"enemy\" must be an object or the name of a JSON file");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            problems.Add($"the enemy couldn't be read ({ex.Message}); {EnemyPlaceholders.Default} stands in with its own stats");
            return new EnemyDefinition();
        }
    }

    /// <summary>
    /// Every battle package in a folder: folders with battle.json (at most two levels down, so battles
    /// can be grouped) and .nbbbattle files. A package that doesn't load is reported and skipped;
    /// of two packages with the same id, the first by path is used.
    /// </summary>
    internal static List<BattlePackage> Scan(string root, Action<string> report)
    {
        var found = new List<BattlePackage>();
        var ids = new Dictionary<string, string>();
        foreach (var path in Candidates(root, 0, report))
        {
            BattlePackage song;
            try { song = Load(path); }
            catch (Exception ex)
            {
                // One broken package must not hide the others.
                bool expected = ex is InvalidDataException or IOException or JsonException or UnauthorizedAccessException;
                report($"Skipping custom battle {path}: {(expected ? ex.Message : ex.ToString())}");
                continue;
            }
            if (ids.TryGetValue(song.Id, out var first))
            {
                report($"Skipping custom battle {path}: it has the same id as {first}");
                continue;
            }
            ids[song.Id] = path;
            found.Add(song);
        }
        return found;
    }

    private static IEnumerable<string> Candidates(string folder, int depth, Action<string> report)
    {
        string[] files, folders;
        try
        {
            files = Directory.GetFiles(folder, "*" + Extension);
            folders = Directory.GetDirectories(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report($"Listing {folder} failed: {ex.Message}");
            yield break;
        }
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) yield return file;
        foreach (var sub in folders)
        {
            if (File.Exists(Path.Combine(sub, ManifestName))) yield return sub;
            else if (depth < 1)
                foreach (var nested in Candidates(sub, depth + 1, report)) yield return nested;
        }
    }
}

/// <summary>
/// Where a song file sits on the battle's clock. The battle starts the music at clock time -0.1 s
/// or a moment later, so the file must already be playing there: silence is added before it
/// when its first sample comes later. The clock is the song file's own time (the game applies
/// the chart's #OFFSET to the notes itself), so a song's first sample is at 0.
/// </summary>
internal static class LeadIn
{
    /// <summary>How much of the clock before 0 the file covers.</summary>
    internal const double StartMargin = 0.25;
    /// <summary>A bigger #OFFSET is a mistake rather than a lead-in.</summary>
    internal const double MaxOffset = 600;

    /// <summary>
    /// Interleaved stereo with enough silence in front that clock time -StartMargin is in the
    /// file, and the clock time of its new first sample. <paramref name="origin"/> is the clock
    /// time of the file's first sample.
    /// </summary>
    internal static (short[] Stereo, double Origin) Pad(short[] stereo, int rate, double origin)
    {
        if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
        double seconds = origin + StartMargin;
        if (seconds <= 0) return (stereo, origin);
        if (seconds > MaxOffset + StartMargin) throw new InvalidDataException($"#OFFSET {origin:0.###} s is too long a lead-in");
        int frames = (int)Math.Ceiling(seconds * rate);
        var padded = new short[stereo.Length + frames * 2];
        Array.Copy(stereo, 0, padded, frames * 2, stereo.Length);
        return (padded, origin - frames / (double)rate);
    }
}
