using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NocturneFlatScroll;

/// <summary>A package built for an upload, with what the summary shows and what the upload sends.</summary>
internal sealed class HubBuild
{
    internal string Kind = "";
    /// <summary>The package file (work\up-&lt;guid&gt;\package.nbbbattle or .nbbchart) and its folder.</summary>
    internal string PackagePath = "", WorkFolder = "";
    internal long Size;
    internal string Sha256 = "", Fingerprint = "";
    internal int Entries;
    internal HubContents Contents = new();
    internal string Title = "", Artist = "", Author = "";
    internal string? BattleId;
    internal int Lanes;
    /// <summary>A battle's difficulties, or a pack's songs with theirs.</summary>
    internal List<HubDifficulty> Difficulties = new();
    internal List<HubSongDifficulties> Songs = new();
    internal double? LengthSeconds;
    internal double[]? Bpm;
    internal List<string> Requires = new();
    /// <summary>Files left out, and why ("art/old.png: not used by the battle").</summary>
    internal List<string> LeftOut = new();
    /// <summary>What was cleaned ("images/card.jpg: picture metadata removed").</summary>
    internal List<string> Cleaned = new();
    /// <summary>What stays that the player might want to know ("audio/song.ogg keeps its tags: TITLE, ARTIST").</summary>
    internal List<string> Kept = new();
    /// <summary>What stops the upload, in plain words. Nothing is sent while there's one.</summary>
    internal List<string> Problems = new();
    /// <summary>Where it was built from (for "changed since your last upload").</summary>
    internal string Source = "";
    internal List<HubSourceFile> SourceFiles = new();
    internal string SourceFingerprint = "";
    /// <summary>The card picture as it went into the package (PNG or JPEG only), for the thumbnail the main thread makes.</summary>
    internal byte[]? CardBytes;
    internal string? CardName;
    internal CardLayout.Look CardLook = CardLayout.Look.Default;

    internal bool Ok => Problems.Count == 0 && PackagePath.Length > 0;
}

/// <summary>A battle in the Upload tab's picker, and why it isn't offered when it isn't.</summary>
internal sealed class HubBattleChoice
{
    internal BattleFiles.BattleEntry Entry = null!;
    internal bool Offered;
    internal string Why = "";
    /// <summary>This PC's earlier upload of the same battle ("You uploaded this (v2); this makes v3").</summary>
    internal HubUploadRecord? Previous;
}

/// <summary>One custom difficulty picked for a pack (made on the main thread from the custom charts).</summary>
internal sealed class HubPackChoice
{
    internal string Song = "";
    internal int Melody = 1;
    internal bool KeepSongEvents = true;
    /// <summary>The chart file it comes from (every difficulty of one file shares it).</summary>
    internal ChartText Chart = new();
    internal int BlockIndex;
    internal int Lanes;
    /// <summary>The file it's in (a loose .sm or a pack), for the source fingerprint and the Downloaded check.</summary>
    internal string SourceFile = "";
    /// <summary>It plays a song file of its own (#MUSIC next to it or in its pack); a pack on the hub can't carry one.</summary>
    internal bool PlaysOwnSong;
    internal string Author = "";
}

/// <summary>
/// Builds the package an upload sends (DESIGN-HUB 3.6), in a work folder of its own, from a copy:
/// the player's files are only read. A battle takes battle.json (a strict copy with its lanes and
/// song always written), its chart, and the files it uses; unused files and files the hub doesn't
/// take are left out and listed. Pictures lose their metadata, and the text files and song tags are
/// searched for the Windows user's paths and name. A pack takes the picked difficulties, one .sm per
/// song and melody. The result is checked with the same check a download gets, which also gives
/// its fingerprint and contents. Runs on a worker thread (the thumbnail comes later, on the main
/// thread, from <see cref="HubBuild.CardBytes"/>).
/// This file has no Unity or game dependencies.
/// </summary>
internal static class HubUploadBuild
{
    private static readonly string[] LayoutFolders = { "audio/", "images/", "art/", "portraits/" };

    // battle.json's documented keys, written in their documented case (the reader ignores case).
    private static readonly string[] ManifestKeys =
    {
        "format", "kind", "id", "title", "artist", "author", "lore", "card", "cardFit", "cardFocus", "cardSmooth", "audio", "previewStart",
        "lanes", "chart", "enemy", "dialogue", "gear", "level", "source",
    };

    private static string MB(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture);

    // ---- what can be uploaded ----------------------------------------------------------------------

    /// <summary>
    /// The player's battles for the Upload tab (from BattleFiles.List on a worker): battle folders that
    /// load, have a charted difficulty and aren't a copy of another. Zips, hub downloads and broken
    /// battles are listed with why they aren't offered.
    /// </summary>
    internal static List<HubBattleChoice> BattleChoices(IEnumerable<BattleFiles.BattleEntry> battles, HubStore store)
    {
        var list = new List<HubBattleChoice>();
        foreach (var b in battles)
        {
            var choice = new HubBattleChoice { Entry = b, Previous = b.Id.Length > 0 ? store.UploadOfBattle(b.Id) : null };
            if (BattleFiles.IsInside(b.Path, store.Paths.BattlesDownloaded)) choice.Why = "it came from the hub";
            else if (b.IsZip) choice.Why = "it's a zip: unpack it in the battle creator first";
            else if (b.Broken) choice.Why = "it can't be read" + (b.Problems.Count > 0 ? ": " + b.Problems[0] : "");
            else if (b.Charted.Count == 0) choice.Why = "nothing in it is charted yet";
            else if (!b.Loads) choice.Why = "it doesn't load" + (b.Problems.Count > 0 ? ": " + b.Problems[0] : "");
            else if (b.CopyOf != null) choice.Why = "it's a copy of another battle (\"Make it a separate battle\" in the creator gives it its own id)";
            else choice.Offered = true;
            list.Add(choice);
        }
        return list;
    }

    // ---- a battle ---------------------------------------------------------------------------------

    private sealed class Planned
    {
        internal string Relative = "";
        internal string Full = "";
        /// <summary>The bytes that go in (a cleaned picture, battle.json); null to copy the file as it is (songs, videos, charts).</summary>
        internal byte[]? Data;
    }

    /// <summary>Builds a battle folder's package for an upload. Look at <see cref="HubBuild.Problems"/> before sending it.</summary>
    internal static HubBuild BuildBattle(string folder, HubPaths paths, HubZipCheck.Limits limits, string userName, CancellationToken ct = default)
    {
        folder = Path.GetFullPath(folder).TrimEnd('\\', '/');
        var build = new HubBuild { Kind = "battle", Source = paths.Relative(folder) };
        if (BattleFiles.IsInside(folder, paths.BattlesDownloaded))
        {
            build.Problems.Add("This battle came from the hub. Upload battles you made.");
            return build;
        }
        BattlePackage package;
        BattleDraft draft;
        JsonObject manifest;
        try
        {
            package = BattlePackage.Load(folder);
            draft = BattleDraft.Load(folder);
            manifest = BattleDraft.ParseAny(BattleDraft.ReadText(Path.Combine(folder, BattlePackage.ManifestName), BattlePackage.MaxJsonBytes)) as JsonObject
                ?? throw new InvalidDataException("battle.json isn't a JSON object");
        }
        catch (Exception ex) when (BattleDraft.IsFileProblem(ex))
        {
            build.Problems.Add("The battle doesn't load: " + ex.Message);
            return build;
        }
        build.Title = HubText.CleanLine(package.Title, HubText.Title) ?? "";
        build.Artist = HubText.CleanLine(package.Artist, HubText.Artist) ?? "";
        build.Author = HubText.CleanLine(package.Author, HubText.Author) ?? "";
        build.BattleId = package.Id;
        build.Lanes = package.Lanes;
        if ((HubText.CleanLine(draft.Title, HubText.Title) ?? "").Length == 0) build.Problems.Add("The battle has no title. Give it one in the battle creator first.");

        // What the battle uses: every file battle.json, its enemy and dialogue files and the chart's
        // #MUSIC name, and files named inside JSON files it names. When one of them can't be read,
        // what it names isn't known, and every file in the battle's folders goes.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = draft.NamedFiles();
        bool unknown = names == null;
        // battle.json's own names count even when a file it names can't be read.
        foreach (var name in (names ?? new List<string>()).Concat(BattleDraft.Texts(manifest)).ToList())
        {
            if (FileIn(folder, name) is not { } full || !File.Exists(full) || !used.Add(full)) continue;
            if (!full.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                foreach (var inner in BattleDraft.Texts(BattleDraft.ParseAny(BattleDraft.ReadText(full, BattlePackage.MaxJsonBytes))))
                    if (FileIn(folder, inner) is { } innerFull && File.Exists(innerFull)) used.Add(innerFull);
            }
            catch (Exception ex) when (BattleDraft.IsFileProblem(ex)) { unknown = true; }
        }
        foreach (var name in new[] { package.ChartPath, package.AudioPath, package.CardPath })
            if (name != null && FileIn(folder, name) is { } full && File.Exists(full)) used.Add(full);

        string top = TopFolder(build.Title);
        var planned = new List<Planned>();
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        long total = 0;
        int heavy = 0;
        foreach (var full in files)
        {
            ct.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(folder, full).Replace('\\', '/');
            if (relative.Equals(BattlePackage.ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
            if (BattleFiles.Skipped(full)) continue;
            bool isUsed = used.Contains(full);
            bool inLayout = LayoutFolders.Any(f => relative.StartsWith(f, StringComparison.OrdinalIgnoreCase));
            if (!isUsed && !(inLayout && unknown))
            {
                build.LeftOut.Add($"{relative}: {(inLayout ? "not used by the battle" : "not one of the battle's files")}");
                continue;
            }
            string? why = FileProblem(relative, top, full);
            byte[]? data = null;
            if (why == null)
            {
                data = File.ReadAllBytes(full);
                why = MediaProblem(relative, data);
            }
            if (why != null)
            {
                if (isUsed) build.Problems.Add($"{relative} {why}.");
                else build.LeftOut.Add($"{relative}: {why}");
                continue;
            }
            string ext = HubZipCheck.ExtensionOf(relative);
            if (HubZipCheck.PictureExtensions.Contains(ext))
            {
                var clean = HubScrub.Picture(data!, out bool changed);
                if (clean == null)
                {
                    if (isUsed) build.Problems.Add($"{relative} couldn't be read to take its metadata out. Save it again in a picture editor.");
                    else build.LeftOut.Add($"{relative}: its metadata couldn't be taken out");
                    continue;
                }
                if (changed) build.Cleaned.Add($"{relative}: picture metadata removed");
                data = clean;
            }
            else if (ext is "json" or "sm")
            {
                if (HubScrub.TextProblem(Utf8(data!), userName) is { } line)
                {
                    build.Problems.Add($"{relative} has a path with your Windows user folder in it ({line}). Take it out in the battle creator or a text editor.");
                    continue;
                }
            }
            else if (HubZipCheck.AudioExtensions.Contains(ext)) AudioTags(relative, data!, userName, build);
            bool heavyFile = HubZipCheck.AudioExtensions.Contains(ext) || HubZipCheck.VideoExtensions.Contains(ext);
            if (heavyFile) heavy++;
            total += data!.Length;
            // Songs and videos are copied from the file when the package is written (the package is checked again after).
            planned.Add(new Planned { Relative = relative, Full = full, Data = heavyFile ? null : data });
        }

        // battle.json: a strict copy the hub can read, with its documented keys, lanes and song always written.
        string canonical = CanonicalManifest(manifest, package);
        if (HubScrub.TextProblem(canonical, userName) is { } manifestLine)
            build.Problems.Add($"battle.json has a path with your Windows user folder in it ({manifestLine}). Take it out in the battle creator.");
        var manifestBytes = new UTF8Encoding(false).GetBytes(canonical);
        if (manifestBytes.Length > HubZipCheck.MaxRootJson) build.Problems.Add("battle.json is over 256 KB.");
        planned.Add(new Planned { Relative = BattlePackage.ManifestName, Full = Path.Combine(folder, BattlePackage.ManifestName), Data = manifestBytes });
        total += manifestBytes.Length;

        if (heavy > HubZipCheck.MaxHeavyMedia) build.Problems.Add($"The battle has {heavy} song and video files; the hub takes up to {HubZipCheck.MaxHeavyMedia}.");
        if (planned.Count > limits.MaxEntries) build.Problems.Add($"The battle has {planned.Count} files; the hub takes up to {limits.MaxEntries}.");
        if (total > limits.MaxUnpacked) build.Problems.Add($"The battle is {MB(total)} MB unpacked; the hub takes up to {limits.MaxUnpacked / (1024 * 1024)} MB.");
        if (build.Problems.Count > 0) return build;

        build.SourceFiles = planned.Select(p => SourceFile(paths, p.Full)).Where(f => f != null).Select(f => f!).ToList();
        build.SourceFingerprint = SourceFingerprint(build.SourceFiles);
        if (package.CardPath != null)
        {
            var card = planned.FirstOrDefault(p => p.Relative.Equals(package.CardPath, StringComparison.OrdinalIgnoreCase));
            if (card?.Data != null && MediaSniff.TypeOf(card.Data) is MediaType.Png or MediaType.Jpeg)
            {
                build.CardBytes = card.Data;
                build.CardName = card.Relative;
                build.CardLook = package.CardLook;
            }
        }

        // Stopped or failed from here on, the package's work folder goes with it (the page never gets the build).
        try
        {
            Write(build, paths, BattlePackage.Extension, planned.Select(p => (top + "/" + p.Relative, p.Data, p.Full)), limits, ct);
            if (!build.Ok) return build;
            try
            {
                var built = BattlePackage.Load(build.PackagePath);
                build.Difficulties = HubDifficulties.Of(built);
                build.Bpm = HubDifficulties.BpmRange(built.Chart);
                var song = planned.First(p => p.Relative.Equals(built.AudioPath, StringComparison.OrdinalIgnoreCase));
                build.LengthSeconds = SongSeconds(File.ReadAllBytes(song.Full), song.Relative);
                if (built.Id != build.BattleId || built.Lanes != build.Lanes) build.Problems.Add("The package doesn't load the same as the battle (this is a bug in the mod; see the log).");
            }
            catch (Exception ex) when (BattleDraft.IsFileProblem(ex) || ex is InvalidOperationException)
            {
                build.Problems.Add("The package doesn't load (this is a bug in the mod; see the log): " + ex.Message);
            }
            ct.ThrowIfCancellationRequested();
            return build;
        }
        catch
        {
            Discard(build, paths);
            throw;
        }
    }

    /// <summary>The one folder a battle's files go in, inside the package: its title, made safe, or "battle".</summary>
    internal static string TopFolder(string title)
    {
        string name = BattleFiles.SafeFolderName(title);
        return HubZipCheck.NameProblem(name + "/" + BattlePackage.ManifestName) == null && Encoding.UTF8.GetByteCount(name) <= 100 ? name : "battle";
    }

    // Why a file can't go into the package by its name, type or size (before it's read), or null.
    private static string? FileProblem(string relative, string top, string full)
    {
        if (HubZipCheck.NameProblem(top + "/" + relative) is { } name) return name;
        if (BattleFiles.SafeEntryName(relative) != relative) return "has a name a battle can't have";
        string ext = HubZipCheck.ExtensionOf(relative);
        if (!HubZipCheck.BattleExtensions.Contains(ext))
            return ext is "mp4" or "m4v" or "mov" ? "is an MP4 video; the hub takes WebM (VP8) videos or frames. Turn it into frames in the battle creator, or convert it to WebM (VP8)"
                : ext is "flac" or "m4a" or "aac" or "wma" or "opus" ? "is a song type the hub doesn't take; convert the song to .ogg or .mp3"
                : ext.Length == 0 ? "has no file type"
                : $"is a .{ext} file, which the hub doesn't take";
        long size = new FileInfo(full).Length;
        if (size > BattleFiles.LimitFor(relative)) return $"is too big ({MB(size)} MB)";
        return null;
    }

    // Why a file's bytes would reach a decoder the hub doesn't take (the download check's own rules), or null.
    private static string? MediaProblem(string relative, byte[] data)
    {
        string ext = HubZipCheck.ExtensionOf(relative);
        if (ext is "json" or "sm") return HubZipCheck.TextProblem(data.AsSpan(0, Math.Min(64, data.Length)));
        if (HubZipCheck.AudioExtensions.Contains(ext)) return HubZipCheck.AudioProblem(data);
        if (HubZipCheck.PictureExtensions.Contains(ext)) return HubZipCheck.PictureProblem(data);
        if (HubZipCheck.VideoExtensions.Contains(ext))
            return HubZipCheck.VideoProblem(data) is { } video ? video + ". Turn it into frames in the battle creator, or convert it to WebM (VP8)" : null;
        return null;
    }

    private static void AudioTags(string relative, byte[] data, string userName, HubBuild build)
    {
        var tags = HubScrub.AudioTags(data).Where(t => t.Text.Trim().Length > 0).ToList();
        // The tags, then the rest of the file outside its sound (XMP and other chunks and frames keep editors' paths).
        if (tags.Any(t => HubScrub.Names(t.Text, userName)) || HubScrub.SongHasUserPath(data, userName))
        {
            build.Problems.Add($"{relative} has your Windows user name or a path to your user folder in its tags. Remove the tags in your audio editor, then upload again.");
            return;
        }
        if (tags.Count > 0)
            build.Kept.Add($"{relative} keeps its tags: {string.Join(", ", tags.Select(t => t.Name).Distinct().Take(8))}");
    }

    private static string Utf8(byte[] data) => new UTF8Encoding(false).GetString(data).TrimStart((char)0xFEFF);

    // The full path of a name inside the battle, or null when it isn't a path to something inside it.
    private static string? FileIn(string folder, string name)
    {
        string? safe = PackageFiles.SafeName(name);
        if (safe == null) return null;
        try
        {
            string full = Path.GetFullPath(Path.Combine(folder, safe.Replace('/', Path.DirectorySeparatorChar))).TrimEnd('\\', '/');
            return BattleFiles.IsInside(full, folder) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>
    /// battle.json as the hub reads it: strict JSON with the documented key names, the battle's id in
    /// its plain lower-case form, and its lanes and song file always written (the player's own file
    /// is left as it is).
    /// </summary>
    internal static string CanonicalManifest(JsonObject manifest, BattlePackage package)
    {
        var pairs = manifest.ToList();
        manifest.Clear();
        var root = new JsonObject();
        foreach (var (key, value) in pairs)
        {
            string name = ManifestKeys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
            root[name] = value;
        }
        root["format"] = BattlePackage.FormatVersion;
        root["kind"] = "battle";
        root["id"] = package.Id;
        root["lanes"] = package.Lanes;
        root["audio"] = package.AudioPath;
        // A card the loader couldn't find isn't named (the hub refuses a card that isn't in the package).
        if (package.CardPath == null && root["card"] != null) root.Remove("card");
        return root.ToJsonString(BattleDraft.WriteOptions);
    }

    /// <summary>A song's length from its header, else by decoding it (the mod's own readers for WAV and Ogg, Windows for MP3).</summary>
    private static double? SongSeconds(byte[] data, string name)
    {
        double? seconds = OszImport.SongSecondsFromHeader(data);
        if (seconds == null)
        {
            try
            {
                var (stereo, rate) = AudioFile.Decode(data, name);
                seconds = rate > 0 ? stereo.Length / 2.0 / rate : null;
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or System.Runtime.InteropServices.COMException) { }
        }
        return seconds is double s && s > 0 && s <= 7200 ? Math.Round(s, 1) : null;
    }

    // ---- a difficulty pack ----------------------------------------------------------------------------

    /// <summary>
    /// Builds a difficulty pack for an upload from the picked difficulties (made on the main thread
    /// from the custom charts), one .sm per song, melody and source file, as the chart export
    /// writes them, plus manifest.json with its lanes.
    /// </summary>
    internal static HubBuild BuildPack(IReadOnlyList<HubPackChoice> choices, string title, string author, HubPaths paths, HubZipCheck.Limits limits,
        string userName, CancellationToken ct = default)
    {
        var build = new HubBuild
        {
            Kind = "charts",
            Title = HubText.CleanLine(title, HubText.PackTitle) ?? "",
            Author = HubText.CleanLine(author, HubText.Author) ?? "",
        };
        if (choices.Count == 0) build.Problems.Add("Pick at least one difficulty.");
        if (build.Title.Length == 0) build.Problems.Add("Give the pack a title.");
        var lanes = choices.Select(c => c.Lanes).Distinct().ToList();
        if (lanes.Count > 1) build.Problems.Add("All the difficulties in a pack must have the same lanes. Upload the 5-lane ones as a separate pack.");
        if (lanes.Count == 1 && lanes[0] is not (4 or 5)) build.Problems.Add("Only 4- and 5-lane difficulties can go in a pack.");
        foreach (var c in choices)
        {
            string name = $"{c.Song}: {c.Chart.Blocks.ElementAtOrDefault(c.BlockIndex)?.DisplayName}";
            if (c.PlaysOwnSong) build.Problems.Add($"{name} plays its own song file. Make it a custom battle to share it.");
            if (c.SourceFile.Length > 0 && BattleFiles.IsInside(c.SourceFile, paths.ChartsDownloaded)) build.Problems.Add($"{name} came from the hub. Upload difficulties you made.");
            if (c.BlockIndex < 0 || c.BlockIndex >= c.Chart.Blocks.Count) build.Problems.Add($"{c.Song}: a difficulty can't be found any more. Pick it again.");
        }
        var songs = choices.Select(c => c.Song).Distinct(StringComparer.Ordinal).ToList();
        if (songs.Count > limits.MaxSongs) build.Problems.Add($"The pack covers {songs.Count} songs; the hub takes up to {limits.MaxSongs}.");
        if (songs.Any(s => (HubText.CleanLine(s, HubText.Song) ?? "") != s)) build.Problems.Add("A song's name can't be written in a pack.");
        if (build.Problems.Count > 0) return build;
        build.Lanes = lanes[0];

        // As CustomCharts' export: one file per source chart, song, melody and events, holding each picked block once.
        var entries = new List<(string Name, byte[]? Data, string File)>();
        var manifestCharts = new JsonArray();
        int n = 0;
        foreach (var group in choices.GroupBy(c => (c.Song, c.Melody, c.KeepSongEvents, c.Chart)))
        {
            var chart = new ChartText();
            chart.Tags.AddRange(group.Key.Chart.Tags.Where(t => !t.Key.StartsWith("NBB", StringComparison.OrdinalIgnoreCase)));
            foreach (int block in group.Select(c => c.BlockIndex).Distinct()) chart.Blocks.Add(group.Key.Chart.Blocks[block]);
            string file = $"charts/{SafeFileStem(group.Key.Song)} M{group.Key.Melody}{(n++ > 0 ? " " + n : "")}.sm";
            string text = chart.Write();
            if (HubScrub.TextProblem(text, userName) is { } line)
            {
                build.Problems.Add($"The chart for {group.Key.Song} has a path with your Windows user folder in it ({line}). Take it out in the chart editor.");
                continue;
            }
            entries.Add((file, new UTF8Encoding(false).GetBytes(text), ""));
            manifestCharts.Add(new JsonObject
            {
                ["source"] = "game", ["song"] = group.Key.Song, ["melody"] = group.Key.Melody,
                ["events"] = group.Key.KeepSongEvents ? "song" : "chart", ["file"] = file,
            });
        }
        if (build.Problems.Count > 0) return build;
        var manifest = new JsonObject
        {
            ["format"] = HubConfig.PackFormat, ["title"] = build.Title, ["author"] = build.Author, ["lanes"] = build.Lanes, ["charts"] = manifestCharts,
        };
        entries.Add(("manifest.json", new UTF8Encoding(false).GetBytes(manifest.ToJsonString(BattleDraft.WriteOptions)), ""));
        build.SourceFiles = choices.Select(c => c.SourceFile).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(f => SourceFile(paths, f)).Where(f => f != null).Select(f => f!).ToList();
        build.SourceFingerprint = SourceFingerprint(build.SourceFiles);

        // Stopped or failed from here on, the package's work folder goes with it (the page never gets the build).
        try
        {
            Write(build, paths, ".nbbchart", entries, limits, ct);
            if (!build.Ok) return build;
            try
            {
                var facts = HubZipCheck.Check(build.PackagePath, "charts", limits, ct);
                var pack = HubPackInfo.Read(build.PackagePath, facts);
                if (pack.Problems.Count > 0) build.Problems.AddRange(pack.Problems);
                foreach (var song in pack.Songs.Where(s => s.Difficulties.Count > 20))
                    build.Problems.Add($"The pack has {song.Difficulties.Count} difficulties for {song.Song}; the hub takes up to 20 a song.");
                build.Songs = pack.Songs;
                build.Difficulties = pack.Songs.SelectMany(s => s.Difficulties).ToList();
                var bpms = choices.Select(c => HubDifficulties.BpmRange(c.Chart)).Where(b => b != null).ToList();
                if (bpms.Count > 0) build.Bpm = new[] { bpms.Min(b => b![0]), bpms.Max(b => b![1]) };
            }
            catch (Exception ex) when (ex is HubZipProblem || BattleDraft.IsFileProblem(ex))
            {
                build.Problems.Add("The pack doesn't read back (this is a bug in the mod; see the log): " + ex.Message);
            }
            ct.ThrowIfCancellationRequested();
            return build;
        }
        catch
        {
            Discard(build, paths);
            throw;
        }
    }

    /// <summary>A song name as a file name inside a pack (as the chart export makes it).</summary>
    internal static string SafeFileStem(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0) name = "chart";
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            name = "_" + name;
        return name;
    }

    // ---- writing and checking the package ------------------------------------------------------------

    /// <summary>
    /// Writes the package (text files first, each group sorted by name, so the hub reads its .sm and
    /// .json files in a few windows), then checks it with the download check itself, which gives its
    /// fingerprint and contents, and hashes it.
    /// </summary>
    private static void Write(HubBuild build, HubPaths paths, string extension, IEnumerable<(string Name, byte[]? Data, string File)> entries, HubZipCheck.Limits limits,
        CancellationToken ct)
    {
        build.WorkFolder = Path.Combine(paths.Work, "up-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(build.WorkFolder);
        build.PackagePath = Path.Combine(build.WorkFolder, "package" + extension);
        var ordered = entries
            .OrderBy(e => HubZipCheck.ExtensionOf(e.Name) is "json" or "sm" ? 0 : 1)
            .ThenBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();
        using (var zip = new HubZipWriter(build.PackagePath))
        {
            foreach (var (name, data, source) in ordered)
            {
                ct.ThrowIfCancellationRequested();
                bool deflate = !BattleFiles.Packed.Contains(Path.GetExtension(name));
                if (data != null) zip.Add(name, data, deflate);
                else
                {
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                    zip.Add(name, input, deflate);
                }
            }
            zip.Finish();
        }
        build.Size = new FileInfo(build.PackagePath).Length;
        if (build.Size > limits.MaxPackage)
        {
            build.Problems.Add(build.Kind == "battle"
                ? $"The battle is {MB(build.Size)} MB and the hub takes up to {limits.MaxPackage / (1024 * 1024)} MB. Turn the enemy video into frames in the creator, or use a shorter video or song."
                : $"The pack is {MB(build.Size)} MB and the hub takes up to {limits.MaxPackage / (1024 * 1024)} MB.");
            return;
        }
        try
        {
            var facts = HubZipCheck.Check(build.PackagePath, build.Kind, limits, ct);
            build.Fingerprint = facts.Fingerprint;
            build.Contents = facts.Contents;
            build.Entries = facts.Entries;
        }
        catch (HubZipProblem ex)
        {
            build.Problems.AddRange(ex.Problems.Select(p => "The package didn't pass the hub's check (this is a bug in the mod; see the log): " + p));
            return;
        }
        build.Sha256 = HubStore.Sha256Of(build.PackagePath);
    }

    /// <summary>Removes an upload's work folder (after it was sent, or when the player stops).</summary>
    internal static void Discard(HubBuild build, HubPaths paths)
    {
        try
        {
            if (build.WorkFolder.Length > 0 && BattleFiles.IsInside(build.WorkFolder, paths.Work) && Directory.Exists(build.WorkFolder))
                Directory.Delete(build.WorkFolder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- where it came from -------------------------------------------------------------------------

    private static HubSourceFile? SourceFile(HubPaths paths, string full)
    {
        var info = new FileInfo(full);
        if (!info.Exists || !BattleFiles.IsInside(full, paths.Root)) return null;
        return new HubSourceFile { Path = paths.Relative(full), Size = info.Length, Time = info.LastWriteTimeUtc.Ticks };
    }

    /// <summary>The files an upload was built from, with their sizes and times, as one SHA-256.</summary>
    internal static string SourceFingerprint(IEnumerable<HubSourceFile> files)
    {
        var text = string.Concat(files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(f => $"{f.Path}\0{f.Size.ToString(CultureInfo.InvariantCulture)}\0{f.Time.ToString(CultureInfo.InvariantCulture)}\n"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>The listing's facts for the start of an upload (what the hub checks against the file itself).</summary>
    internal static HubUploadMeta Meta(HubBuild build, string? description) => new()
    {
        Description = HubText.CleanText(description, HubText.Description) ?? "",
        Difficulties = build.Kind == "battle" ? build.Difficulties : null,
        Songs = build.Kind == "charts" ? build.Songs : null,
        LengthSeconds = build.LengthSeconds,
        Bpm = build.Bpm,
        Requires = build.Requires,
    };
}
