using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NocturneFlatScroll;

/// <summary>
/// The battle creator's file work: making a new battle folder, copying songs and images into it,
/// listing the battles, importing a .nbbbattle zip (safely: nothing lands outside the new folder,
/// with size limits like the loader's), exporting one, and moving things to the Recycle Bin.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class BattleFiles
{
    /// <summary>A zip with more files, or more bytes in all, isn't imported.</summary>
    internal const int MaxZipEntries = 4000;
    internal const long MaxZipBytes = 1024L * 1024 * 1024;
    internal const int MaxNameLength = 60;

    /// <summary>
    /// New and imported battles are put together here first and moved into place when complete.
    /// It sits one folder deeper than the battle scan looks, so a half-made battle is never loaded.
    /// </summary>
    internal const string WorkFolderName = ".creator-work";

    // ---- names ---------------------------------------------------------------------------------

    /// <summary>A folder name from a title: no characters Windows refuses, no device names, not too long.</summary>
    internal static string SafeFolderName(string? title)
    {
        string name = Clean(title, MaxNameLength).TrimStart('.').Trim();
        return name.Length == 0 ? "Battle" : name;
    }

    /// <summary>A file name that is safe on Windows and inside an .sm tag (no ';').</summary>
    internal static string SafeFileName(string? fileName)
    {
        string ext = Clean(Path.GetExtension(fileName ?? ""), 12).Replace(";", "_").Replace("#", "_");
        string stem = Clean(Path.GetFileNameWithoutExtension(fileName ?? ""), MaxNameLength).Replace(";", "_").TrimStart('#', '.').Trim();
        if (stem.Length == 0) stem = "file";
        return stem + ext;
    }

    private static string Clean(string? text, int max)
    {
        var sb = new StringBuilder();
        var bad = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        foreach (char c in (text ?? "").Trim())
            sb.Append(bad.Contains(c) || char.IsControl(c) ? '_' : c);
        string name = sb.ToString();
        if (name.Length > max) name = name.Substring(0, max);
        name = name.Trim().TrimEnd('.', ' ');
        // CON, NUL, COM1 and the like are devices on Windows, even with an extension.
        string stem = name.Split('.')[0].Trim().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            name = "_" + name;
        return name;
    }

    /// <summary>"name", or "name (2)", "name (3)"... whichever isn't taken in <paramref name="parent"/>.</summary>
    internal static string FreeFolder(string parent, string name)
    {
        string path = Path.Combine(parent, name);
        for (int i = 2; Directory.Exists(path) || File.Exists(path); i++) path = Path.Combine(parent, $"{name} ({i})");
        return path;
    }

    private static string FreeFile(string folder, string fileName)
    {
        string path = Path.Combine(folder, fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        for (int i = 2; File.Exists(path) || Directory.Exists(path); i++) path = Path.Combine(folder, $"{stem} ({i}){ext}");
        return path;
    }

    /// <summary>Whether <paramref name="path"/> is inside <paramref name="folder"/> (not the folder itself).</summary>
    internal static bool IsInside(string path, string folder)
    {
        string full = Path.GetFullPath(path).TrimEnd('\\', '/');
        string parent = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && full.Length > parent.Length;
    }

    // ---- a new battle ---------------------------------------------------------------------------

    /// <summary>A new battle's chart: its title, its song, beat 0 at the song's start, 120 BPM, and no difficulties yet.</summary>
    internal static string ChartSkeleton(string title, string music) =>
        $"#TITLE:{BattleDraft.CleanTagValue(title)};\n#MUSIC:{BattleDraft.CleanTagValue(music)};\n#OFFSET:0;\n#BPMS:0=120;\n";

    /// <summary>
    /// Makes a battle folder in <paramref name="root"/> for a song file: named after the song,
    /// with the song copied into audio/, battle.json and an empty chart. Returns the folder.
    /// </summary>
    internal static string CreateBattle(string root, string audioSource, int lanes, string author)
    {
        if (!File.Exists(audioSource)) throw new FileNotFoundException("the song file is missing", audioSource);
        long size = new FileInfo(audioSource).Length;
        if (size > BattlePackage.MaxAudioBytes) throw new InvalidDataException($"the song file is too big ({size / (1024 * 1024)} MB; at most {BattlePackage.MaxAudioBytes / (1024 * 1024)} MB)");
        string title = BattleDraft.CleanLine(Path.GetFileNameWithoutExtension(audioSource));
        if (title.Length == 0) title = "New battle";
        return Build(root, SafeFolderName(title), staging =>
        {
            string audio = "audio/" + SafeFileName(Path.GetFileName(audioSource));
            Directory.CreateDirectory(Path.Combine(staging, "audio"));
            File.Copy(audioSource, Path.Combine(staging, "audio", Path.GetFileName(audio)));
            BattleDraft.WriteAtomic(Path.Combine(staging, "charts", "song.sm"), ChartSkeleton(title, audio));
            BattleDraft.Create(staging, title, lanes, audio, author).Save();
        });
    }

    /// <summary>
    /// Fills a staging folder with <paramref name="fill"/>, then moves it to a free name in
    /// <paramref name="root"/>. A failure leaves nothing behind.
    /// </summary>
    private static string Build(string root, string name, Action<string> fill)
    {
        Directory.CreateDirectory(root);
        string work = Path.Combine(root, WorkFolderName, Guid.NewGuid().ToString("N"));
        string staging = Path.Combine(work, "battle");
        Directory.CreateDirectory(staging);
        try
        {
            fill(staging);
            string final = FreeFolder(root, name);
            Directory.Move(staging, final);
            return final;
        }
        finally
        {
            TryDeleteWork(root, work);
        }
    }

    // Only ever the creator's own staging folders, made moments ago.
    private static void TryDeleteWork(string root, string work)
    {
        try
        {
            if (!IsInside(work, Path.Combine(root, WorkFolderName))) return;
            if (Directory.Exists(work)) Directory.Delete(work, true);
            string parent = Path.Combine(root, WorkFolderName);
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes staging folders a crash may have left (they never hold a finished battle).</summary>
    internal static void CleanWork(string root)
    {
        string parent = Path.Combine(root, WorkFolderName);
        if (!Directory.Exists(parent)) return;
        foreach (var work in Directory.GetDirectories(parent)) TryDeleteWork(root, work);
    }

    /// <summary>
    /// Copies a file into the battle's <paramref name="subfolder"/> (like "audio" or "images") and
    /// returns its path inside the battle. A file already there under that name is kept, and the
    /// copy gets a free name, unless it is the same file.
    /// </summary>
    internal static (string Path, bool Copied) AddFile(string folder, string source, string subfolder, long maxBytes)
    {
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException("the file is missing", source);
        if (info.Length > maxBytes) throw new InvalidDataException($"{info.Name} is too big ({info.Length / (1024 * 1024)} MB; at most {maxBytes / (1024 * 1024)} MB)");
        string dir = Path.Combine(folder, subfolder);
        string target = Path.Combine(dir, SafeFileName(info.Name));
        if (File.Exists(target) && (SamePath(target, source) || SameBytes(target, source)))
            return (subfolder + "/" + Path.GetFileName(target), false);
        Directory.CreateDirectory(dir);
        target = FreeFile(dir, Path.GetFileName(target));
        File.Copy(source, target);
        return (subfolder + "/" + Path.GetFileName(target), true);
    }

    private static bool SamePath(string a, string b) =>
        Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool SameBytes(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        var ba = new byte[65536];
        var bb = new byte[65536];
        while (true)
        {
            int na = sa.Read(ba, 0, ba.Length);
            int nb = ReadFull(sb, bb, na);
            if (na != nb) return false;
            if (na == 0) return true;
            if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
        }
    }

    private static int ReadFull(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = stream.Read(buffer, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// Of <paramref name="candidates"/> (paths inside the battle), the song and image files that
    /// the saved battle.json no longer names. Only files in audio/ and images/ are ever listed.
    /// </summary>
    internal static List<string> Unreferenced(string folder, IEnumerable<string> candidates)
    {
        var draft = BattleDraft.Load(folder);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { draft.Audio.Replace('\\', '/') };
        if (draft.Card != null) used.Add(draft.Card.Replace('\\', '/'));
        var unused = new List<string>();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? name = PackageFiles.SafeName(candidate);
            if (name == null || used.Contains(name)) continue;
            if (!name.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("images/", StringComparison.OrdinalIgnoreCase)) continue;
            string path = Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) && IsInside(path, folder)) unused.Add(path);
        }
        return unused;
    }

    // ---- the list of battles ----------------------------------------------------------------------

    /// <summary>A battle in the CustomBattles folder, as the creator's list shows it.</summary>
    internal sealed class BattleEntry
    {
        internal string Path = "";
        internal bool IsZip;
        internal string Id = "";
        internal string Title = "";
        internal string Artist = "";
        internal int Lanes;
        /// <summary>The difficulties with a chart of their own, by the game's names.</summary>
        internal readonly List<string> Charted = new();
        internal readonly List<string> Problems = new();
        /// <summary>battle.json couldn't be read, so the battle can't be opened.</summary>
        internal bool Broken;
    }

    /// <summary>
    /// Every battle folder and .nbbbattle zip, found the way the arcade finds them (two folder
    /// levels, zips first). A battle that has no chart yet isn't a problem here, just "not charted".
    /// </summary>
    internal static List<BattleEntry> List(string root)
    {
        var entries = new List<BattleEntry>();
        if (!Directory.Exists(root)) return entries;
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Candidates(root, 0))
        {
            var entry = File.Exists(path) ? ReadZip(path) : ReadFolder(path);
            if (entry.Id.Length > 0)
            {
                // The arcade uses the first of two battles with one id.
                if (ids.TryGetValue(entry.Id, out var first)) entry.Problems.Add($"it has the same id as {System.IO.Path.GetFileName(first)}, so the arcade skips it");
                else ids[entry.Id] = path;
            }
            entries.Add(entry);
        }
        return entries;
    }

    // The same folders and files BattlePackage.Scan looks at, in the same order.
    private static IEnumerable<string> Candidates(string folder, int depth)
    {
        string[] files, folders;
        try
        {
            files = Directory.GetFiles(folder, "*" + BattlePackage.Extension);
            folders = Directory.GetDirectories(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) yield return file;
        foreach (var sub in folders)
        {
            if (File.Exists(Path.Combine(sub, BattlePackage.ManifestName))) yield return sub;
            else if (depth < 1)
                foreach (var nested in Candidates(sub, depth + 1)) yield return nested;
        }
    }

    /// <summary>One battle folder or zip, as the list shows it (without the check for a second battle with its id).</summary>
    internal static BattleEntry Read(string path) => File.Exists(path) ? ReadZip(path) : ReadFolder(path);

    private static BattleEntry ReadZip(string path)
    {
        var entry = new BattleEntry { Path = path, IsZip = true, Title = Path.GetFileNameWithoutExtension(path) };
        try
        {
            var package = BattlePackage.Load(path);
            entry.Id = package.Id;
            entry.Title = package.Title;
            entry.Artist = package.Artist;
            entry.Lanes = package.Lanes;
            for (int s = 0; s < package.Slots.Length; s++)
                if (package.Slots[s] != null) entry.Charted.Add(ChartText.GameDifficultyLabels[s]);
            entry.Problems.AddRange(package.Problems);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            entry.Problems.Add(ex.Message);
        }
        return entry;
    }

    private static BattleEntry ReadFolder(string path)
    {
        var entry = new BattleEntry { Path = path, Title = Path.GetFileName(path.TrimEnd('\\', '/')) };
        BattleDraft draft;
        try { draft = BattleDraft.Load(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            entry.Broken = true;
            entry.Problems.Add(ex.Message);
            return entry;
        }
        entry.Id = draft.Id;
        if (draft.Title.Trim().Length > 0) entry.Title = draft.Title.Trim();
        entry.Artist = draft.Artist.Trim();
        entry.Lanes = draft.Lanes;
        var summary = SummarizeChart(path, draft.ChartPath, entry.Lanes);
        for (int s = 0; s < summary.Notes.Length; s++)
            if (summary.Notes[s] >= 0) entry.Charted.Add(ChartText.GameDifficultyLabels[s]);
        if (entry.Charted.Count > 0)
        {
            // The loader's own checks, as the arcade will see the battle.
            try { entry.Problems.AddRange(BattlePackage.Load(path).Problems); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                entry.Problems.Add(ex.Message);
            }
        }
        else
        {
            // Not charted yet: the loader would only say that; the chart's own problems explain why.
            entry.Problems.AddRange(summary.Problems);
            string? audio = PackageFiles.SafeName(draft.Audio);
            if (audio == null) entry.Problems.Add("the battle names no audio file");
            else if (!File.Exists(Path.Combine(path, audio.Replace('/', Path.DirectorySeparatorChar)))) entry.Problems.Add($"the audio file {audio} is missing");
        }
        entry.Problems.InsertRange(0, draft.Problems);
        return entry;
    }

    // ---- the chart, for the creator's pages ----------------------------------------------------------

    /// <summary>What the creator shows about a battle's chart: notes per difficulty, timing, problems.</summary>
    internal sealed class ChartSummary
    {
        /// <summary>Notes in each of the game's six difficulty slots; -1 where it isn't charted.</summary>
        internal readonly int[] Notes = { -1, -1, -1, -1, -1, -1 };
        internal readonly List<string> Problems = new();
        internal bool Found;
        internal double Offset;
        internal readonly List<(double Beat, double Bpm)> Bpms = new();

        internal string BpmText
        {
            get
            {
                if (Bpms.Count == 0) return "not set";
                double low = Bpms.Min(b => b.Bpm), high = Bpms.Max(b => b.Bpm);
                string first = Bpms[0].Bpm.ToString("0.###", CultureInfo.InvariantCulture);
                if (Bpms.Count == 1) return first;
                return $"{low.ToString("0.###", CultureInfo.InvariantCulture)} to {high.ToString("0.###", CultureInfo.InvariantCulture)} ({Bpms.Count - 1} changes), starting at {first}";
            }
        }
    }

    internal static ChartSummary SummarizeChart(string folder, string chartPath, int lanes)
    {
        var summary = new ChartSummary();
        string? name = PackageFiles.SafeName(chartPath);
        if (name == null)
        {
            summary.Problems.Add("\"chart\" must be a file inside the battle");
            return summary;
        }
        ChartText chart;
        try { chart = ChartText.Parse(BattleDraft.ReadText(Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar)), BattlePackage.MaxChartBytes)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            summary.Problems.Add($"the chart {name} couldn't be read ({ex.Message})");
            return summary;
        }
        summary.Found = true;
        var slots = chart.SongSlots(lanes, summary.Problems);
        for (int s = 0; s < slots.Length; s++)
            if (slots[s] != null) summary.Notes[s] = NoteCount(slots[s]!);
        if (double.TryParse(chart.GetTag("OFFSET"), NumberStyles.Float, CultureInfo.InvariantCulture, out double offset)) summary.Offset = offset;
        foreach (var pair in (chart.GetTag("BPMS") ?? "").Split(','))
        {
            var parts = pair.Split('=');
            if (parts.Length == 2
                && double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double beat)
                && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm))
                summary.Bpms.Add((beat, bpm));
        }
        return summary;
    }

    /// <summary>Taps, holds and rolls in a block (mines don't count).</summary>
    internal static int NoteCount(ChartText.NoteBlock block)
    {
        int count = 0;
        foreach (char c in block.Notes)
            if (c is '1' or '2' or '4') count++;
        return count;
    }

    // ---- importing a .nbbbattle ------------------------------------------------------------------

    /// <summary>
    /// Unpacks a .nbbbattle zip into a new folder in <paramref name="root"/> and returns it. The
    /// zip holds battle.json at its root or inside its one top folder (as the loader reads it);
    /// only that battle's files are unpacked. Names that would reach outside the folder, files
    /// bigger than the loader takes, and zips that unpack to too much are refused before anything
    /// is written. When another battle in the folder has the same id (other than the zip itself,
    /// <paramref name="ignore"/>), the copy gets a new one so both show in the arcade.
    /// </summary>
    internal static (string Folder, string Title, bool NewId) Import(string zipPath, string root, string? ignore = null)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        string prefix = PackagePrefix(zip);
        var plan = new List<(ZipArchiveEntry Entry, string Name)>();
        long total = 0;
        ZipArchiveEntry? manifest = null;
        foreach (var entry in zip.Entries)
        {
            string full = entry.FullName.Replace('\\', '/');
            if (full.EndsWith("/") || full.Length == 0) continue;   // a folder entry
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (full.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)) continue;
            string name = SafeEntryName(full.Substring(prefix.Length))
                ?? throw new InvalidDataException($"\"{full}\" in the zip points outside the battle");
            long limit = LimitFor(name);
            if (entry.Length > limit) throw new InvalidDataException($"{name} is too big ({entry.Length / (1024 * 1024)} MB)");
            total += entry.Length;
            if (total > MaxZipBytes) throw new InvalidDataException($"the zip unpacks to more than {MaxZipBytes / (1024 * 1024)} MB");
            if (plan.Count >= MaxZipEntries) throw new InvalidDataException($"the zip has more than {MaxZipEntries} files");
            if (name.Equals(BattlePackage.ManifestName, StringComparison.OrdinalIgnoreCase)) manifest = entry;
            plan.Add((entry, name));
        }
        if (manifest == null) throw new InvalidDataException("no " + BattlePackage.ManifestName + " in it");
        var (id, title) = BattleDraft.Check(ReadEntryText(manifest, BattlePackage.MaxJsonBytes));

        var taken = new HashSet<string>(List(root).Where(e => ignore == null || !SamePath(e.Path, ignore)).Select(e => e.Id).Where(i => i.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        bool newId = taken.Contains(id);
        if (title.Length == 0) title = Path.GetFileNameWithoutExtension(zipPath);
        string folder = Build(root, SafeFolderName(title), staging =>
        {
            string stagingFull = Path.GetFullPath(staging);
            foreach (var (entry, name) in plan)
            {
                string target = Path.GetFullPath(Path.Combine(stagingFull, name.Replace('/', Path.DirectorySeparatorChar)));
                // The names were checked above; this is the last word on where a file lands.
                if (!IsInside(target, stagingFull)) throw new InvalidDataException($"\"{name}\" points outside the battle");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = File.Create(target);
                CopyAtMost(input, output, entry.Length, name);
            }
            if (newId)
            {
                var draft = BattleDraft.Load(staging);
                draft.NewId();
                draft.Save();
            }
        });
        return (folder, title, newId);
    }

    /// <summary>Where the battle is inside the zip: "" for its root, else "Top/". Like BattlePackage's reader.</summary>
    internal static string PackagePrefix(ZipArchive zip)
    {
        if (PackageFiles.Find(zip, BattlePackage.ManifestName) != null) return "";
        var folders = zip.Entries
            .Select(e => e.FullName.Replace('\\', '/'))
            .Where(n => n.EndsWith("/" + BattlePackage.ManifestName, StringComparison.OrdinalIgnoreCase) && n.Count(c => c == '/') == 1)
            .Select(n => n.Substring(0, n.IndexOf('/')))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folders.Count == 1) return folders[0] + "/";
        throw new InvalidDataException(folders.Count == 0 ? "no " + BattlePackage.ManifestName + " in it" : "more than one " + BattlePackage.ManifestName + " in it");
    }

    /// <summary>
    /// A name inside the zip as a path inside the battle, or null when it could land anywhere
    /// else: absolute paths, drive letters, "..", and characters Windows can't put in a name.
    /// </summary>
    internal static string? SafeEntryName(string name)
    {
        string? safe = PackageFiles.SafeName(name);
        if (safe == null) return null;
        var parts = safe.Split('/');
        var bad = Path.GetInvalidFileNameChars();
        foreach (var part in parts)
        {
            if (part.Length == 0 || part == "." || part.IndexOfAny(bad) >= 0) return null;
            // Windows drops trailing dots and spaces, so "a." and "a" would be the same file.
            if (part.EndsWith(".") || part.EndsWith(" ")) return null;
            // CON, NUL and the like are devices, not files.
            if (Clean(part, part.Length) != part) return null;
        }
        return string.Join("/", parts);
    }

    // The loader's limits for each kind of file.
    private static long LimitFor(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".json" => BattlePackage.MaxJsonBytes,
            ".sm" or ".ssc" => BattlePackage.MaxChartBytes,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" => BattlePackage.MaxImageBytes,
            _ => BattlePackage.MaxAudioBytes,
        };
    }

    // A zip entry can say one size and hold more; stop at what it says.
    private static void CopyAtMost(Stream input, Stream output, long max, string name)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += n;
            if (copied > max) throw new InvalidDataException($"{name} holds more than the zip says");
            output.Write(buffer, 0, n);
        }
    }

    private static string ReadEntryText(ZipArchiveEntry entry, long max)
    {
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        CopyAtMost(stream, copy, Math.Min(max, entry.Length), entry.FullName);
        return new UTF8Encoding(false).GetString(copy.ToArray()).TrimStart((char)0xFEFF);
    }

    // ---- exporting a .nbbbattle ------------------------------------------------------------------

    // Already compressed: zipping them again only takes time.
    private static readonly HashSet<string> Packed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ogg", ".mp3", ".m4a", ".aac", ".flac", ".wma", ".opus", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".mp4", ".webm", ".m4v", ".mov"
    };

    // Files Windows or the creator leave in folders, which don't belong in a shared battle.
    private static bool Skipped(string name)
    {
        string file = Path.GetFileName(name);
        return file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            || file.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes the battle folder into a .nbbbattle zip: every file inside one top folder named
    /// like the battle's folder, so battle.json is at "Top/battle.json", as the loader reads it.
    /// The zip is written to a temporary file first and can't be put inside the battle itself
    /// or in the battles folder (there the arcade would find the battle twice).
    /// </summary>
    internal static (int Files, long Bytes) Export(string folder, string zipPath, string battlesRoot)
    {
        if (IsInside(zipPath, folder)) throw new InvalidDataException("the export can't go inside the battle's own folder");
        if (IsInside(zipPath, battlesRoot)) throw new InvalidDataException("save it outside the battles folder (the arcade would load the battle twice)");
        if (!File.Exists(Path.Combine(folder, BattlePackage.ManifestName))) throw new InvalidDataException("the folder has no " + BattlePackage.ManifestName);
        string top = SafeFolderName(Path.GetFileName(Path.GetFullPath(folder).TrimEnd('\\', '/')));
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !Skipped(f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        string temp = zipPath + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        long bytes = 0;
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    string name = Path.GetRelativePath(folder, file).Replace('\\', '/');
                    var level = Packed.Contains(Path.GetExtension(file)) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                    zip.CreateEntryFromFile(file, top + "/" + name, level);
                    bytes += new FileInfo(file).Length;
                }
            }
            File.Move(temp, zipPath, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return (files.Count, bytes);
    }

    // ---- the Recycle Bin ------------------------------------------------------------------------

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400, FofWantNukeWarning = 0x4000;

    /// <summary>SHFILEOPSTRUCTW with the 64-bit layout (shellapi.h packs it tightly only for 32-bit Windows).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShFileOp
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOp op);

    /// <summary>
    /// Moves a file or folder to the Recycle Bin, never deleting it for good without asking:
    /// when Windows can't recycle it, Windows asks first. Refuses anything that isn't inside
    /// <paramref name="mustBeInside"/>.
    /// </summary>
    internal static void Recycle(string path, string mustBeInside, IntPtr owner)
    {
        if (!IsInside(path, mustBeInside)) throw new InvalidOperationException($"{path} isn't inside {mustBeInside}");
        string full = Path.GetFullPath(path).TrimEnd('\\', '/');
        if (!File.Exists(full) && !Directory.Exists(full)) return;
        int result = ShellDelete(full, owner, out bool aborted);
        if (result != 0) throw new IOException($"Windows couldn't move {Path.GetFileName(full)} to the Recycle Bin (error 0x{result:X})");
        if (aborted) throw new IOException($"moving {Path.GetFileName(full)} to the Recycle Bin was cancelled");
    }

    /// <summary>The shell's delete with undo (the Recycle Bin); the result is the shell's error code, 0 when it worked.</summary>
    internal static int ShellDelete(string fullPath, IntPtr owner, out bool aborted)
    {
        var op = new ShFileOp
        {
            hwnd = owner,
            wFunc = FoDelete,
            // A list of paths, each ending in a null, with one more null at the end.
            pFrom = fullPath + "\0\0",
            fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning)
        };
        int result = SHFileOperationW(ref op);
        aborted = op.fAnyOperationsAborted;
        return result;
    }
}
