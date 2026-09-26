using System.Globalization;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace NocturneFlatScroll;

/// <summary>
/// The game's own checks and changes the hub needs, which run on the main thread (they touch the
/// game): the game's chart reader for a battle, the custom-chart check for a pack, reloading the
/// custom charts, and the upload thumbnail. The page calls them between its worker steps; the
/// offline checks use a stand-in.
/// </summary>
internal interface IHubGame
{
    /// <summary>Why the game's chart reader wouldn't take the battle, or null.</summary>
    string? CheckBattle(BattlePackage package);

    /// <summary>Why the custom-chart check refuses the pack (a song that isn't the game's, lanes that don't match), or null.</summary>
    string? CheckPack(string path);

    /// <summary>After a pack was installed, updated or deleted: the custom charts are read again.</summary>
    void ChartsChanged();
}

/// <summary>Moves a file to the Recycle Bin (never deleting it for good), refusing anything outside a folder.</summary>
internal interface IHubRecycler
{
    /// <summary>Throws <see cref="BattleFiles.NotRecyclableException"/> when Windows has no Recycle Bin for it (the file stays).</summary>
    void Recycle(string path, string mustBeInside);
}

/// <summary>The Windows Recycle Bin, through the battle creator's shell move, on a thread of its own (the shell asks for one).</summary>
internal sealed class HubRecycleBin : IHubRecycler
{
    private readonly IntPtr owner;

    /// <param name="owner">The game window (captured on the main thread), which owns any shell prompt.</param>
    internal HubRecycleBin(IntPtr owner) => this.owner = owner;

    public void Recycle(string path, string mustBeInside) => OnShellThread(() => BattleFiles.Recycle(path, mustBeInside, owner));

    /// <summary>Runs <paramref name="work"/> on a single-threaded-apartment thread and waits for it (from a worker).</summary>
    internal static void OnShellThread(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true, Name = "Hub Recycle Bin" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}

/// <summary>A difficulty as the hub lists it: from the chart, the same way on upload and after download.</summary>
internal static class HubDifficulties
{
    /// <summary>A block's listing: its display name (or its slot's name when it has none), its level from #METER and its notes.</summary>
    internal static HubDifficulty Of(ChartText.NoteBlock block, string fallbackName)
    {
        string name = HubText.CleanLine(block.DisplayName, HubText.Difficulty) ?? "";
        if (name.Length == 0) name = HubText.CleanLine(fallbackName, HubText.Difficulty) ?? "Chart";
        int level = int.TryParse(block.Meter.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int meter) ? Math.Clamp(meter, 0, 99) : 0;
        return new HubDifficulty { Name = name, Level = level, Notes = Math.Min(BattleFiles.NoteCount(block), 1_000_000) };
    }

    /// <summary>A battle's difficulties in the game's slot order (the ones it plays).</summary>
    internal static List<HubDifficulty> Of(BattlePackage package)
    {
        var list = new List<HubDifficulty>();
        for (int s = 0; s < package.Slots.Length; s++)
            if (package.Slots[s] is { } block) list.Add(Of(block, ChartText.GameDifficultyLabels[s]));
        return list;
    }

    /// <summary>Whether two listings name the same difficulties with the same levels, in the same order.</summary>
    internal static bool Same(IReadOnlyList<HubDifficulty> a, IReadOnlyList<HubDifficulty> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Name == p.Second.Name && p.First.Level == p.Second.Level);

    /// <summary>The lowest and highest BPM in a chart's #BPMS, or null.</summary>
    internal static double[]? BpmRange(ChartText chart)
    {
        var bpms = new List<double>();
        foreach (var pair in (chart.GetTag("BPMS") ?? "").Split(','))
        {
            var parts = pair.Split('=');
            if (parts.Length == 2 && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm) && bpm > 0 && bpm <= 2000)
                bpms.Add(bpm);
        }
        return bpms.Count == 0 ? null : new[] { Math.Round(bpms.Min(), 2), Math.Round(bpms.Max(), 2) };
    }
}

/// <summary>A difficulty pack's songs and difficulties, read from its (checked) file the way the hub lists them.</summary>
internal sealed class HubPackInfo
{
    internal string Title = "";
    internal int Lanes;
    internal List<HubSongDifficulties> Songs = new();
    internal List<string> Problems = new();

    /// <summary>
    /// Reads a pack that passed <see cref="HubZipCheck.Check"/>: manifest.json's charts, each chart
    /// file's blocks as difficulties per song (in the manifest's order). A chart whose lanes aren't
    /// the pack's, or that names a song file of its own with #MUSIC, is a problem.
    /// </summary>
    internal static HubPackInfo Read(string path, HubPackageFacts facts)
    {
        var info = new HubPackInfo { Title = facts.Title, Lanes = facts.Lanes };
        using var zip = ZipFile.OpenRead(path);
        byte[] manifestBytes = ReadEntry(zip, "manifest.json", HubZipCheck.MaxRootJson);
        using var doc = JsonDocument.Parse(manifestBytes.AsMemory(manifestBytes.Length >= 3 && manifestBytes[0] == 0xEF ? 3 : 0));
        foreach (var c in HubZipCheck.Field(doc.RootElement, "charts").EnumerateArray())
        {
            string song = HubText.CleanLine(HubZipCheck.Field(c, "song").GetString(), HubText.Song) ?? "";
            string file = (HubZipCheck.Field(c, "file").GetString() ?? "").Replace('\\', '/').Trim();
            var entry = facts.Files.First(e => e.Relative.Equals(file, StringComparison.OrdinalIgnoreCase));
            var chart = ChartText.Parse(new UTF8Encoding(false).GetString(ReadEntry(zip, entry.Relative, BattlePackage.MaxChartBytes)).TrimStart((char)0xFEFF));
            var list = info.Songs.FirstOrDefault(s => s.Song == song);
            if (list == null) info.Songs.Add(list = new HubSongDifficulties { Song = song });
            foreach (var block in chart.Blocks)
            {
                if (block.Lanes != facts.Lanes) info.Problems.Add($"a chart for {song} has {block.Lanes} lanes, but the pack has {facts.Lanes}");
                list.Difficulties.Add(HubDifficulties.Of(block, "Chart"));
            }
            // A chart that names a song file of its own would play it; a pack carries none.
            if (chart.GetTag("MUSIC")?.Trim() is { Length: > 0 } music && PackageFiles.SafeName(music) is { } name)
            {
                string folder = entry.Relative.Contains('/') ? entry.Relative.Substring(0, entry.Relative.LastIndexOf('/') + 1) : "";
                var named = facts.Files.FirstOrDefault(e => e.Relative.Equals(folder + name, StringComparison.OrdinalIgnoreCase))
                    ?? facts.Files.FirstOrDefault(e => e.Relative.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (named != null) info.Problems.Add($"the chart for {song} plays a file in the pack ({named.Relative}) as its song, which a pack on the hub can't carry");
            }
        }
        return info;
    }

    private static byte[] ReadEntry(ZipArchive zip, string name, long max)
    {
        var entry = PackageFiles.Find(zip, name) ?? throw new InvalidDataException($"the pack has no {name}");
        if (entry.Length > max) throw new InvalidDataException($"{name} is too big");
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        BattleFiles.CopyAtMost(stream, copy, entry.Length, name);
        return copy.ToArray();
    }
}

/// <summary>A download that passed every check except the game's own (which the main thread does next).</summary>
internal sealed class HubVerified
{
    internal HubDetail Detail = null!;
    /// <summary>The checked file in the work folder ("dl-&lt;guid&gt;.nbbbattle" or ".nbbchart").</summary>
    internal string TempPath = "";
    internal HubPackageFacts Facts = null!;
    /// <summary>The battle as the loader reads it (battles), for the game's chart check.</summary>
    internal BattlePackage? Battle;
    internal HubPackInfo? Pack;
    internal string Kind => Detail.Kind;
}

/// <summary>What deleting an installed entry did.</summary>
internal enum HubDeleteOutcome
{
    /// <summary>It went to the Recycle Bin.</summary>
    Recycled,
    /// <summary>There's no Recycle Bin for it; it's an untouched hub download, so it may be deleted for good after a second confirm.</summary>
    AskForGood,
    /// <summary>There's no Recycle Bin for it, and the file changed since the download: nothing was deleted (delete it in Explorer).</summary>
    Changed,
    /// <summary>The file was already gone; the list forgets it.</summary>
    Gone,
}

/// <summary>
/// Installing from the hub (DESIGN-HUB 3.4, 3.5, 1.4, 1.5): one path for battles and packs. The
/// download is checked in full in the work folder (size, SHA-256, the zip rules, every entry, media
/// by decoder, the mod's own loader, the listing), then the game's own check runs on the main
/// thread, then the file is moved into CustomBattles\Downloaded or CustomCharts\Downloaded as
/// "&lt;title&gt; [&lt;id&gt;].nbbbattle" (or .nbbchart). An update keeps the file name and swaps the
/// file in one step (File.Replace), so a pack's pick and a battle's scores stay. Nothing is
/// unpacked, opened or run. Deleting sends the file to the Recycle Bin; where there is none, an
/// untouched download may be deleted for good after a second confirm. The worker steps run on
/// worker threads; the page runs <see cref="IHubGame"/>'s checks on the main thread between them.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class HubInstall
{
    private static string MB(long bytes) => Math.Ceiling(bytes / (1024.0 * 1024.0)).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Downloads an entry into the work folder and checks it (worker). The file waits there for the
    /// game's check (<see cref="GameCheck"/>, main thread) and <see cref="Place"/>; <see cref="Discard"/>
    /// removes it when the player stops.
    /// </summary>
    internal static async Task<HubVerified> DownloadAsync(HubApi api, HubStore store, HubDetail detail, HubZipCheck.Limits limits, HubTransfer transfer,
        CancellationToken ct)
    {
        var paths = store.Paths;
        if (!HubText.IsPackageId(detail.Id) || detail.Version < 1) throw HubDownload.Damaged("its listing isn't well formed");
        if (!HubConfig.Playable(detail)) throw new HubException("needs_newer_mod", "This entry needs a newer version of the mod.");
        if (detail.File.Size > limits.MaxPackage) throw HubDownload.Damaged($"it's bigger ({MB(detail.File.Size)} MB) than the hub takes");
        Directory.CreateDirectory(paths.Work);
        Directory.CreateDirectory(paths.DownloadedFor(detail.Kind));
        long free = HubDownload.FreeSpace(paths.Work);
        if (free >= 0 && free < 2 * detail.File.Size)
            throw new HubException("disk_full", $"There isn't enough free space on the drive. The download needs {MB(2 * detail.File.Size)} MB free.");
        string part = Path.Combine(paths.Work, $"dl-{Guid.NewGuid():N}.part");
        string ready = Path.ChangeExtension(part, HubPaths.ExtensionFor(detail.Kind));
        try
        {
            await HubDownload.FetchAsync(api.Http, api.FileUrl(detail.Id, detail.Version), detail.File.Size, detail.File.Sha256, part, transfer, ct).ConfigureAwait(false);
            File.Move(part, ready);
            transfer.Stage = "Checking";
            ct.ThrowIfCancellationRequested();
            return Verify(ready, detail, limits, ct);
        }
        catch
        {
            HubDownload.TryDelete(part);
            HubDownload.TryDelete(ready);
            throw;
        }
    }

    /// <summary>
    /// Checks a downloaded package against the hub's rules and its listing (worker): the zip check,
    /// the directory fingerprint, and for a battle the mod's loader (battle id, lanes, difficulties,
    /// title), for a pack its songs, lanes and difficulties. Throws "damaged" with the reason.
    /// </summary>
    internal static HubVerified Verify(string path, HubDetail detail, HubZipCheck.Limits limits, CancellationToken ct)
    {
        HubPackageFacts facts;
        try { facts = HubZipCheck.Check(path, detail.Kind, limits, ct); }
        catch (HubZipProblem ex) { throw new HubException("damaged", "That download was damaged or doesn't match its listing.") { Problems = ex.Problems }; }
        var why = new List<string>();
        if (facts.Fingerprint != detail.File.Fingerprint) why.Add("its files aren't the ones in its listing (fingerprint)");
        if (facts.Lanes != detail.Lanes) why.Add($"it has {facts.Lanes} lanes, but its listing says {detail.Lanes}");
        if (!HubText.SameTitle(facts.Title, detail.Title)) why.Add("its title isn't the one in its listing");
        if (facts.Format > (detail.IsBattle ? HubConfig.BattleFormat : HubConfig.PackFormat)) why.Add("it needs a newer version of the mod");
        var verified = new HubVerified { Detail = detail, TempPath = path, Facts = facts };
        if (detail.IsBattle)
        {
            if (facts.BattleId != detail.BattleId) why.Add("its battle id isn't the one in its listing");
            try
            {
                var package = BattlePackage.Load(path);
                verified.Battle = package;
                if (package.Id != facts.BattleId) why.Add("the loader reads another battle id");
                if (package.Lanes != detail.Lanes) why.Add("the loader reads other lanes");
                // The song and the card the game shows must be the files the check judged as a song and a picture.
                if (!string.Equals(package.AudioPath, facts.Audio, StringComparison.OrdinalIgnoreCase)) why.Add("the loader plays another file as the song than the one checked");
                if (!string.Equals(package.CardPath, facts.Card, StringComparison.OrdinalIgnoreCase)) why.Add("the loader shows another file as the card than the one checked");
                if (!HubDifficulties.Same(HubDifficulties.Of(package), detail.Difficulties)) why.Add("its difficulties aren't the ones in its listing");
            }
            catch (Exception ex) when (BattleDraft.IsFileProblem(ex)) { why.Add("the battle doesn't load: " + ex.Message); }
        }
        else
        {
            try
            {
                var pack = HubPackInfo.Read(path, facts);
                verified.Pack = pack;
                why.AddRange(pack.Problems);
                var listed = detail.Songs ?? new List<HubSongDifficulties>();
                bool same = listed.Count == pack.Songs.Count && listed.Zip(pack.Songs).All(p =>
                    p.First.Song == p.Second.Song && HubDifficulties.Same(p.First.Difficulties, p.Second.Difficulties));
                if (!same) why.Add("its songs and difficulties aren't the ones in its listing");
            }
            catch (Exception ex) when (BattleDraft.IsFileProblem(ex) || ex is InvalidOperationException or KeyNotFoundException)
            {
                why.Add("the pack can't be read: " + ex.Message);
            }
        }
        if (why.Count > 0) throw new HubException("damaged", "That download was damaged or doesn't match its listing.") { Problems = why };
        return verified;
    }

    /// <summary>The game's own check of a verified download (main thread). Null when it passes.</summary>
    internal static string? GameCheck(HubVerified verified, IHubGame game)
    {
        try
        {
            return verified.Battle != null ? game.CheckBattle(verified.Battle) : game.CheckPack(verified.TempPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    /// <summary>Removes a download that won't be installed.</summary>
    internal static void Discard(HubVerified verified) => HubDownload.TryDelete(verified.TempPath);

    /// <summary>
    /// Moves a checked download into place and records it (worker). A new entry becomes
    /// "Downloaded\&lt;title&gt; [&lt;id&gt;]"; an update replaces the installed file under its own name,
    /// and the old one goes to the Recycle Bin (or is deleted where there's none, but only while its
    /// SHA-256 is still the hub's copy that was installed). An installed file that changed since
    /// (or one the hub didn't install) goes to the Recycle Bin whole before the new one takes its
    /// name, and where there's none the update isn't installed: it's never deleted for good. A
    /// battle whose id another battle on this PC already has isn't installed.
    /// </summary>
    internal static HubInstalledItem Place(HubVerified verified, HubStore store, IHubRecycler recycler, Func<List<BattleFiles.BattleEntry>>? listBattles = null)
    {
        var paths = store.Paths;
        var detail = verified.Detail;
        string folder = paths.DownloadedFor(detail.Kind);
        Directory.CreateDirectory(folder);
        var old = store.InstalledFor(detail.Id);
        string? final = old != null && paths.Full(old.Path) is { } full && File.Exists(full) && BattleFiles.IsInside(full, folder) ? full : null;
        final ??= Path.Combine(folder, $"{BattleFiles.SafeFolderName(verified.Facts.Title)} [{detail.Id}]{HubPaths.ExtensionFor(detail.Kind)}");
        if (!BattleFiles.IsInside(final, folder)) throw new InvalidOperationException("the file would land outside the Downloaded folder");

        if (detail.IsBattle)
        {
            // Another battle with this id (the player's own folder, a copy dropped in) wins in the arcade: the hub never adds a second one.
            var battles = listBattles?.Invoke() ?? BattleFiles.List(paths.Battles);
            var other = battles.FirstOrDefault(b => b.Id.Equals(detail.BattleId, StringComparison.OrdinalIgnoreCase)
                && !Path.GetFullPath(b.Path).Equals(Path.GetFullPath(final), StringComparison.OrdinalIgnoreCase));
            if (other != null)
                throw new HubException("you_have_it", $"This battle is already on your PC ({Path.GetFileName(other.Path.TrimEnd('\\', '/'))}), so it isn't installed a second time.");
        }

        string? replaced = null;
        if (File.Exists(final))
        {
            // The hub's own copy, untouched: a known version whose file still has the SHA-256 it was installed with.
            bool untouched = old is { Version: > 0 } && HubStore.Sha256Of(final) == old.Sha256;
            if (!untouched)
            {
                // Changed since the download, or not the hub's copy: it goes to the Recycle Bin whole, never deleted for good.
                try { recycler.Recycle(final, folder); }
                catch (BattleFiles.NotRecyclableException)
                {
                    throw new HubException("changed", $"{Path.GetFileName(final)} isn't the copy the hub installed (it changed since, or the hub can't tell), " +
                        "and Windows has no Recycle Bin for it, so the update wasn't installed. Move it out of the Downloaded folder, then update again.");
                }
                File.Move(verified.TempPath, final);
            }
            else
            {
                string backup = final + ".old";
                HubDownload.TryDelete(backup);
                File.Replace(verified.TempPath, final, backup, ignoreMetadataErrors: true);
                replaced = old!.Sha256;
                try
                {
                    recycler.Recycle(backup, folder);
                    replaced = null;
                }
                catch (BattleFiles.NotRecyclableException)
                {
                    // The hub's own older copy (checked above), and the player asked for the update.
                    HubDownload.TryDelete(backup);
                    if (!File.Exists(backup)) replaced = null;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    // Left for the next time the page opens (Reconcile finds it, and OldSha256 says it's the hub's copy).
                    ModLog.Info("Hub: the old copy of an update stays for now: " + ex.Message);
                }
            }
        }
        else File.Move(verified.TempPath, final);

        var facts = verified.Facts;
        var item = new HubInstalledItem
        {
            Package = detail.Id, Version = detail.Version, Kind = detail.Kind, Path = paths.Relative(final), Sha256 = detail.File.Sha256,
            Fingerprint = facts.Fingerprint, Size = detail.File.Size, Contents = facts.Contents, BattleId = facts.BattleId, Title = facts.Title,
            Lanes = facts.Lanes, Songs = detail.IsBattle ? null : facts.Songs, Uploader = detail.Uploader,
            InstalledAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), OldSha256 = replaced,
        };
        store.Record(item);
        SaveThumb(store, detail);
        return item;
    }

    private static void SaveThumb(HubStore store, HubCard card)
    {
        string file = store.Paths.Thumb(card.Id);
        try
        {
            if (HubZipCheck.ThumbProblem(card.Thumb, out var bytes) == null)
            {
                Directory.CreateDirectory(store.Paths.Thumbs);
                File.WriteAllBytes(file, bytes!);
            }
            else if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Sends an installed entry's file to the Recycle Bin and forgets it (worker). Where Windows has
    /// no Recycle Bin for it, nothing is deleted: an untouched download (its SHA-256 still the one
    /// installed) can then be deleted for good with <see cref="DeleteForGood"/> after a second confirm.
    /// </summary>
    internal static HubDeleteOutcome Delete(HubInstalledItem item, HubStore store, IHubRecycler recycler)
    {
        var paths = store.Paths;
        string folder = paths.DownloadedFor(item.Kind);
        string? full = paths.Full(item.Path);
        if (full == null || !BattleFiles.IsInside(full, folder)) throw new InvalidOperationException("the hub only deletes inside its Downloaded folders");
        if (!File.Exists(full))
        {
            store.Forget(item.Package);
            return HubDeleteOutcome.Gone;
        }
        try { recycler.Recycle(full, folder); }
        catch (BattleFiles.NotRecyclableException)
        {
            return HubStore.Sha256Of(full) == item.Sha256 ? HubDeleteOutcome.AskForGood : HubDeleteOutcome.Changed;
        }
        store.Forget(item.Package);
        return HubDeleteOutcome.Recycled;
    }

    /// <summary>
    /// Deletes an untouched hub download for good (worker), after the player confirmed it twice
    /// where there's no Recycle Bin. Only inside the Downloaded folder, and only while the file is
    /// still exactly what the hub installed.
    /// </summary>
    internal static void DeleteForGood(HubInstalledItem item, HubStore store)
    {
        var paths = store.Paths;
        string folder = paths.DownloadedFor(item.Kind);
        string? full = paths.Full(item.Path);
        if (full == null || !BattleFiles.IsInside(full, folder)) throw new InvalidOperationException("the hub only deletes inside its Downloaded folders");
        if (File.Exists(full))
        {
            if (HubStore.Sha256Of(full) != item.Sha256) throw new InvalidOperationException("the file changed since it was downloaded, so it isn't deleted; delete it in Explorer");
            File.Delete(full);
        }
        store.Forget(item.Package);
    }

    /// <summary>
    /// The ".old" copies an update left behind (from <see cref="HubStore.Reconcile"/>) to the Recycle
    /// Bin. Where there's none, only one that is still the hub's copy an update replaced (next to its
    /// entry's file, with the SHA-256 in <see cref="HubInstalledItem.OldSha256"/>) is deleted for
    /// good; any other ".old" file is left alone.
    /// </summary>
    internal static void CleanLeftovers(IEnumerable<string> leftovers, HubStore store, IHubRecycler recycler)
    {
        foreach (var file in leftovers)
        {
            string? folder = new[] { store.Paths.BattlesDownloaded, store.Paths.ChartsDownloaded }.FirstOrDefault(f => BattleFiles.IsInside(file, f));
            if (folder == null || !file.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) continue;
            try { recycler.Recycle(file, folder); }
            catch (BattleFiles.NotRecyclableException)
            {
                var item = store.Installed.FirstOrDefault(i => i.OldSha256 != null && store.Paths.Full(i.Path) is { } full
                    && string.Equals(full + ".old", Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase));
                try
                {
                    if (item != null && HubStore.Sha256Of(file) == item.OldSha256) HubDownload.TryDelete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { }
        }
    }
}
