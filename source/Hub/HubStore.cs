using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NocturneFlatScroll;

/// <summary>
/// Where the hub's files are (DESIGN-HUB 3.3), under the mod's own folder
/// (...\LocalLow\PracyStudios\Nocturne\NocturneButBetter), captured on the main thread before any
/// worker starts. Downloads land in CustomBattles\Downloaded and CustomCharts\Downloaded; the hub's
/// own files (key, index, thumbnails, the work folder) are in Hub\, which no scan reads.
/// This file has no Unity or game dependencies.
/// </summary>
internal sealed class HubPaths
{
    private readonly string? identity;

    /// <param name="root">The NocturneButBetter folder.</param>
    /// <param name="identityFile">Another identity.json (a QA build's NFS_QA_HUB_IDENTITY, to act as a second player).</param>
    internal HubPaths(string root, string? identityFile = null)
    {
        Root = Path.GetFullPath(root).TrimEnd('\\', '/');
        identity = string.IsNullOrWhiteSpace(identityFile) ? null : Path.GetFullPath(identityFile!);
    }

    internal string Root { get; }
    internal string HubFolder => Path.Combine(Root, "Hub");
    internal string Identity => identity ?? Path.Combine(HubFolder, "identity.json");
    internal string InstalledFile => Path.Combine(HubFolder, "installed.json");
    internal string UploadsFile => Path.Combine(HubFolder, "uploads.json");
    internal string SettingsFile => Path.Combine(HubFolder, "settings.json");
    internal string Thumbs => Path.Combine(HubFolder, "thumbs");
    /// <summary>Downloads in progress and upload builds; outside the battle and chart folders, on the same drive.</summary>
    internal string Work => Path.Combine(HubFolder, "work");
    internal string Battles => Path.Combine(Root, "CustomBattles");
    internal string Charts => Path.Combine(Root, "CustomCharts");
    internal string BattlesDownloaded => Path.Combine(Battles, "Downloaded");
    internal string ChartsDownloaded => Path.Combine(Charts, "Downloaded");

    internal string DownloadedFor(string kind) => kind == "battle" ? BattlesDownloaded : ChartsDownloaded;

    internal static string ExtensionFor(string kind) => kind == "battle" ? BattlePackage.Extension : ".nbbchart";

    /// <summary>A path the index keeps (relative to the mod's folder, with /) as a full path; null when it would lead outside it.</summary>
    internal string? Full(string relative)
    {
        try
        {
            string full = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
            return BattleFiles.IsInside(full, Root) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    internal string Relative(string full) => Path.GetRelativePath(Root, full).Replace('\\', '/');

    internal string Thumb(string packageId) =>
        Path.Combine(Thumbs, (HubText.IsPackageId(packageId) ? packageId : throw new ArgumentException("not a package id", nameof(packageId))) + ".jpg");
}

/// <summary>An entry the hub installed on this PC (installed.json).</summary>
internal sealed class HubInstalledItem
{
    public string Package { get; set; } = "";
    /// <summary>The installed version; 0 when it isn't known (the index was rebuilt and the hub hasn't said yet).</summary>
    public int Version { get; set; }
    public string Kind { get; set; } = "";
    /// <summary>The file, relative to the mod's folder.</summary>
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public long Size { get; set; }
    public HubContents? Contents { get; set; }
    public string? BattleId { get; set; }
    public string Title { get; set; } = "";
    public int Lanes { get; set; }
    public List<string>? Songs { get; set; }
    public HubUploaderRef? Uploader { get; set; }
    public long InstalledAt { get; set; }
}

/// <summary>A file a local upload was built from, with the size and time it had then.</summary>
internal sealed class HubSourceFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public long Time { get; set; }
}

/// <summary>One of this PC's uploads (uploads.json): where it came from, for "changed since your last upload".</summary>
internal sealed class HubUploadRecord
{
    public string Package { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? BattleId { get; set; }
    public string Title { get; set; } = "";
    /// <summary>The battle's folder, relative to the mod's folder ("" for a difficulty pack).</summary>
    public string Source { get; set; } = "";
    public List<HubSourceFile> SourceFiles { get; set; } = new();
    public string SourceFingerprint { get; set; } = "";
    public int LastVersion { get; set; }
    public long UploadedAt { get; set; }
}

internal sealed class HubSettings
{
    public int Format { get; set; } = 1;
    /// <summary>The one-time notice (DESIGN-HUB 5.2) was shown.</summary>
    public bool NoticeSeen { get; set; }
    public string LastSort { get; set; } = "new";
    public string LastKind { get; set; } = "all";
    public int LastLanes { get; set; }
}

internal sealed class HubIndexFile<T>
{
    public int Format { get; set; } = 1;
    public List<T> Items { get; set; } = new();
}

/// <summary>What the hub says about an installed entry, after a lookup.</summary>
internal enum HubInstalledStatus { Unknown, UpToDate, UpdateAvailable, Removed, Deleted, UnderReview, NotOnHub }

/// <summary>A Browse row's state tag (DESIGN-HUB 1.4). InUse is worked out by the page (it asks the game's chart picks).</summary>
internal enum HubRowState { None, Downloading, Installed, InUse, Update, Yours, YouHaveIt, NeedsNewerMod, Failed }

/// <summary>
/// The hub's local state (DESIGN-HUB 3.3, 1.5): what it installed (installed.json), what this PC
/// uploaded (uploads.json), the page's settings (settings.json) and the saved thumbnails. The files
/// are written atomically and read defensively: a damaged one is renamed *.broken-&lt;date&gt;.json,
/// and the installed list is rebuilt from the two Downloaded folders. Reads and writes are locked,
/// so the page can read the lists while a worker changes them. File work runs on worker threads.
/// This file has no Unity or game dependencies.
/// </summary>
internal sealed class HubStore
{
    private static readonly JsonSerializerOptions WriteOptions = new(HubJson.Options) { WriteIndented = true };
    // "Title [k7q2m9x4t1].nbbbattle": the id in the file name, as the hub installs it.
    private static readonly Regex IdInName = new(@"\[([0-9a-hjkmnp-tv-z]{10})\]\.(nbbbattle|nbbchart)$", RegexOptions.CultureInvariant);

    private readonly object gate = new();
    private readonly List<HubInstalledItem> installed = new();
    private readonly List<HubUploadRecord> uploads = new();
    private readonly Dictionary<string, HubLookupItem> lookup = new(StringComparer.Ordinal);

    private HubStore(HubPaths paths) => Paths = paths;

    internal HubPaths Paths { get; }
    internal HubSettings Settings { get; private set; } = new();
    /// <summary>What went wrong reading the files ("installed.json was damaged ..."), for the log and the status line.</summary>
    internal List<string> Notes { get; } = new();

    /// <summary>Reads the hub's files (a worker's job: it may hash files to rebuild the list).</summary>
    internal static HubStore Open(HubPaths paths)
    {
        var store = new HubStore(paths);
        Directory.CreateDirectory(paths.HubFolder);
        var list = store.ReadIndex<HubInstalledItem>(paths.InstalledFile, out bool broken);
        if (broken) list = store.Rebuild();
        store.installed.AddRange(list.Where(Valid));
        store.uploads.AddRange(store.ReadIndex<HubUploadRecord>(paths.UploadsFile, out _).Where(u => u != null && HubText.IsPackageId(u.Package)));
        store.Settings = store.ReadSettings();
        if (broken) store.SaveInstalled();
        return store;
    }

    private static bool Valid(HubInstalledItem? item) =>
        item != null && HubText.IsPackageId(item.Package) && item.Kind is "battle" or "charts" && item.Path.Length > 0 && item.Version >= 0;

    private List<T> ReadIndex<T>(string file, out bool broken)
    {
        broken = false;
        if (!File.Exists(file)) return new List<T>();
        try
        {
            var read = JsonSerializer.Deserialize<HubIndexFile<T>>(BattleDraft.ReadText(file, 16 * 1024 * 1024), HubJson.Options);
            if (read == null || read.Format != 1 || read.Items == null) throw new InvalidDataException("not an index file this mod reads");
            return read.Items;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or NotSupportedException)
        {
            broken = true;
            string kept = SetAside(file);
            Notes.Add($"{Path.GetFileName(file)} was damaged ({ex.Message}); it was kept as {Path.GetFileName(kept)}");
            return new List<T>();
        }
    }

    private HubSettings ReadSettings()
    {
        if (!File.Exists(Paths.SettingsFile)) return new HubSettings();
        try
        {
            var read = JsonSerializer.Deserialize<HubSettings>(BattleDraft.ReadText(Paths.SettingsFile, 64 * 1024), HubJson.Options);
            if (read != null && read.Format == 1) return read;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { }
        SetAside(Paths.SettingsFile);
        return new HubSettings();
    }

    // "installed.json" to "installed.broken-20260926-101500.json".
    private static string SetAside(string file)
    {
        string dir = Path.GetDirectoryName(file)!;
        string kept = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(file)}.broken-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        for (int i = 2; File.Exists(kept); i++)
            kept = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(file)}.broken-{DateTime.UtcNow:yyyyMMdd-HHmmss} ({i}).json");
        try { File.Move(file, kept); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return kept;
    }

    /// <summary>
    /// The installed list made again from the two Downloaded folders: each file's id comes from its
    /// name, its version stays unknown until a lookup finds its SHA-256 (<see cref="ApplyLookup"/>).
    /// </summary>
    private List<HubInstalledItem> Rebuild()
    {
        var items = new List<HubInstalledItem>();
        foreach (var (kind, folder) in new[] { ("battle", Paths.BattlesDownloaded), ("charts", Paths.ChartsDownloaded) })
            foreach (var file in Files(folder))
                if (ItemFromFile(kind, file) is { } item) items.Add(item);
        Notes.Add($"the installed list was made again from the Downloaded folders ({items.Count} found)");
        return items;
    }

    private static IEnumerable<string> Files(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetFiles(folder) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private HubInstalledItem? ItemFromFile(string kind, string file)
    {
        var m = IdInName.Match(Path.GetFileName(file));
        if (!m.Success || (m.Groups[2].Value == "nbbbattle") != (kind == "battle")) return null;
        try
        {
            var info = new FileInfo(file);
            string name = Path.GetFileNameWithoutExtension(file);
            return new HubInstalledItem
            {
                Package = m.Groups[1].Value, Version = 0, Kind = kind, Path = Paths.Relative(file), Sha256 = Sha256Of(file), Size = info.Length,
                Title = name.Substring(0, Math.Max(0, name.LastIndexOf(" [", StringComparison.Ordinal))).Trim(),
                InstalledAt = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string Sha256Of(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    // ---- saving ---------------------------------------------------------------------------------

    private void SaveInstalled()
    {
        HubIndexFile<HubInstalledItem> file;
        lock (gate) file = new HubIndexFile<HubInstalledItem> { Items = installed.ToList() };
        BattleDraft.WriteAtomic(Paths.InstalledFile, JsonSerializer.Serialize(file, WriteOptions));
    }

    private void SaveUploads()
    {
        HubIndexFile<HubUploadRecord> file;
        lock (gate) file = new HubIndexFile<HubUploadRecord> { Items = uploads.ToList() };
        BattleDraft.WriteAtomic(Paths.UploadsFile, JsonSerializer.Serialize(file, WriteOptions));
    }

    internal void SaveSettings() => BattleDraft.WriteAtomic(Paths.SettingsFile, JsonSerializer.Serialize(Settings, WriteOptions));

    // ---- installed ------------------------------------------------------------------------------

    internal List<HubInstalledItem> Installed
    {
        get { lock (gate) return installed.ToList(); }
    }

    internal HubInstalledItem? InstalledFor(string package)
    {
        lock (gate) return installed.FirstOrDefault(i => i.Package == package);
    }

    /// <summary>Adds or replaces an entry's item and saves the list.</summary>
    internal void Record(HubInstalledItem item)
    {
        lock (gate)
        {
            installed.RemoveAll(i => i.Package == item.Package);
            installed.Add(item);
        }
        SaveInstalled();
    }

    /// <summary>Drops an entry's item and its saved thumbnail, and saves the list.</summary>
    internal void Forget(string package)
    {
        lock (gate) installed.RemoveAll(i => i.Package == package);
        SaveInstalled();
        try { File.Delete(Paths.Thumb(package)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    /// <summary>
    /// When the page opens (DESIGN-HUB 1.5): drops items whose file is gone (deleted in Explorer, or
    /// unpacked in the battle creator), adds hub files the list lost, and empties the work folder of
    /// anything over an hour old. Returns the leftover ".old" copies of updates to be recycled.
    /// </summary>
    internal List<string> Reconcile()
    {
        bool changed = false;
        lock (gate)
        {
            int before = installed.Count;
            installed.RemoveAll(i => Paths.Full(i.Path) is not { } full || !File.Exists(full));
            changed = installed.Count != before;
        }
        var leftovers = new List<string>();
        foreach (var (kind, folder) in new[] { ("battle", Paths.BattlesDownloaded), ("charts", Paths.ChartsDownloaded) })
            foreach (var file in Files(folder))
            {
                if (file.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                {
                    leftovers.Add(file);
                    continue;
                }
                string relative = Paths.Relative(file);
                bool known;
                lock (gate) known = installed.Any(i => i.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
                if (known) continue;
                var item = ItemFromFile(kind, file);
                if (item == null) continue;
                lock (gate)
                {
                    if (installed.Any(i => i.Package == item.Package)) continue;
                    installed.Add(item);
                }
                changed = true;
            }
        if (changed) SaveInstalled();
        CleanWork(TimeSpan.FromHours(1));
        return leftovers;
    }

    /// <summary>Removes files and folders in the work folder older than <paramref name="age"/> (and only there).</summary>
    internal void CleanWork(TimeSpan age)
    {
        if (!Directory.Exists(Paths.Work)) return;
        var cutoff = DateTime.UtcNow - age;
        foreach (var entry in new DirectoryInfo(Paths.Work).EnumerateFileSystemInfos())
        {
            try
            {
                if (entry.LastWriteTimeUtc > cutoff || !BattleFiles.IsInside(entry.FullName, Paths.Work)) continue;
                if (entry is DirectoryInfo dir) dir.Delete(true);
                else entry.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---- what the hub says about them -------------------------------------------------------------

    /// <summary>
    /// Takes a lookup's answer. An item whose version wasn't known gets the hub's version when its
    /// SHA-256 matches the hub's current file (else it stays unknown, shown as "update available").
    /// </summary>
    internal void ApplyLookup(IEnumerable<HubLookupItem> items)
    {
        bool changed = false;
        lock (gate)
        {
            foreach (var item in items) lookup[item.Id] = item;
            foreach (var i in installed)
                if (i.Version == 0 && lookup.TryGetValue(i.Package, out var hub) && hub.Status == "live" && hub.Sha256 == i.Sha256)
                {
                    i.Version = hub.Version;
                    changed = true;
                }
        }
        if (changed) SaveInstalled();
    }

    internal HubLookupItem? LookupFor(string package)
    {
        lock (gate) return lookup.TryGetValue(package, out var item) ? item : null;
    }

    internal HubInstalledStatus StatusOf(HubInstalledItem item)
    {
        var hub = LookupFor(item.Package);
        if (hub == null) return HubInstalledStatus.Unknown;
        return hub.Status switch
        {
            "live" => item.Version == 0 || hub.Version > item.Version ? HubInstalledStatus.UpdateAvailable : HubInstalledStatus.UpToDate,
            "removed" => HubInstalledStatus.Removed,
            "deleted" => HubInstalledStatus.Deleted,
            "unavailable" => HubInstalledStatus.UnderReview,
            _ => HubInstalledStatus.NotOnHub,
        };
    }

    /// <summary>The Installed tab's line about an item, in plain words.</summary>
    internal string StatusWords(HubInstalledItem item)
    {
        var hub = LookupFor(item.Package);
        return StatusOf(item) switch
        {
            HubInstalledStatus.UpToDate => "Up to date",
            HubInstalledStatus.UpdateAvailable => item.Version == 0 ? $"Unknown version, update available (v{hub!.Version})" : $"Update available (v{item.Version} to v{hub!.Version})",
            HubInstalledStatus.Removed => "Removed from the hub (" + HubText.ReasonWords(hub!.Reason) + "). Your copy stays.",
            HubInstalledStatus.Deleted => "Deleted from the hub by its uploader. Your copy stays.",
            HubInstalledStatus.UnderReview => "Under review on the hub. Your copy stays.",
            HubInstalledStatus.NotOnHub => "Not on the hub. Your copy stays.",
            _ => item.Version > 0 ? $"v{item.Version}" : "Unknown version",
        };
    }

    /// <summary>How many installed entries have an update ("Installed 12 (3 updates)").</summary>
    internal int UpdateCount => Installed.Count(i => StatusOf(i) == HubInstalledStatus.UpdateAvailable);

    /// <summary>
    /// A Browse row's state. <paramref name="localBattleIds"/> are the battle ids on this PC that the
    /// hub didn't install (the player's own folders, copies dropped in by hand).
    /// </summary>
    internal HubRowState StateOf(HubCard card, ISet<string> localBattleIds, string? myUploaderId, string? downloading, ISet<string> failed)
    {
        if (card.Id == downloading) return HubRowState.Downloading;
        if (myUploaderId != null && card.Uploader.Id == myUploaderId) return HubRowState.Yours;
        var item = InstalledFor(card.Id);
        if (item != null)
        {
            if (item.Version == 0 || card.Version > item.Version) return HubConfig.Playable(card) ? HubRowState.Update : HubRowState.NeedsNewerMod;
            return HubRowState.Installed;
        }
        if (card.IsBattle && card.BattleId != null && localBattleIds.Contains(card.BattleId)) return HubRowState.YouHaveIt;
        if (!HubConfig.Playable(card)) return HubRowState.NeedsNewerMod;
        if (failed.Contains(card.Id)) return HubRowState.Failed;
        return HubRowState.None;
    }

    /// <summary>
    /// The battle ids on this PC that the hub didn't install, from the battle list (a worker's
    /// BattleFiles.List of CustomBattles).
    /// </summary>
    internal HashSet<string> LocalBattleIds(IEnumerable<BattleFiles.BattleEntry> battles)
    {
        var ours = new HashSet<string>(Installed.Select(i => Paths.Full(i.Path)).Where(p => p != null)!, StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in battles)
            if (b.Id.Length > 0 && !ours.Contains(Path.GetFullPath(b.Path))) ids.Add(b.Id.ToLowerInvariant());
        return ids;
    }

    // ---- this PC's uploads ----------------------------------------------------------------------

    internal List<HubUploadRecord> Uploads
    {
        get { lock (gate) return uploads.ToList(); }
    }

    internal HubUploadRecord? UploadFor(string package)
    {
        lock (gate) return uploads.FirstOrDefault(u => u.Package == package);
    }

    /// <summary>This PC's upload of a battle (by its battle id), for "You uploaded this (v2); this makes v3".</summary>
    internal HubUploadRecord? UploadOfBattle(string battleId)
    {
        lock (gate) return uploads.FirstOrDefault(u => u.BattleId != null && u.BattleId.Equals(battleId, StringComparison.OrdinalIgnoreCase));
    }

    internal void RecordUpload(HubUploadRecord record)
    {
        lock (gate)
        {
            uploads.RemoveAll(u => u.Package == record.Package);
            uploads.Add(record);
        }
        SaveUploads();
    }

    internal void ForgetUpload(string package)
    {
        lock (gate) uploads.RemoveAll(u => u.Package == package);
        SaveUploads();
    }

    /// <summary>
    /// Whether the files an upload was built from changed since (a size or time differs, or one is
    /// gone): "Changed since your last upload". Only looks at the files' sizes and times.
    /// </summary>
    internal bool SourceChanged(HubUploadRecord record)
    {
        if (record.SourceFiles.Count == 0) return false;
        foreach (var f in record.SourceFiles)
        {
            if (Paths.Full(f.Path) is not { } full) return true;
            var info = new FileInfo(full);
            if (!info.Exists || info.Length != f.Size || info.LastWriteTimeUtc.Ticks != f.Time) return true;
        }
        return false;
    }
}
