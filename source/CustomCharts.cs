using System.IO.Compression;
using System.Text;
using System.Text.Json;
using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// Player-made difficulties for the game's own songs. They live in one folder as loose .sm files
/// or as .nbbchart packs (a zip with a manifest and one or more .sm files), and each #NOTES
/// block in them is one custom difficulty. The player picks at most one per song.
/// </summary>
internal static class CustomCharts
{
    internal const string PackExtension = ".nbbchart";
    internal const int FormatVersion = 1;
    private const string SelectionKeyPrefix = "NocturneFlatScroll.CustomChart.";
    private const string SelectionKeySuffix = ".v1";
    // Bigger chart files are refused rather than read into memory.
    private const long MaxChartBytes = 8 * 1024 * 1024;

    private static List<CustomChart>? charts;
    private static Dictionary<string, SongData>? songs;
    private static readonly Dictionary<string, int> LaneCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One playable custom difficulty.</summary>
    internal sealed class CustomChart
    {
        internal string Key = "";
        internal string Song = "";
        internal int Melody = 1;
        internal bool KeepSongEvents = true;
        internal string Title = "";
        internal string Author = "";
        internal int Lanes;
        internal int BlockIndex;
        internal string SourceFile = "";
        internal string? PackEntry;
        internal ChartText Chart = new();

        internal string DisplayName => string.IsNullOrWhiteSpace(Author) ? Title : $"{Title} by {Author}";
    }

    internal sealed class Manifest
    {
        public int format { get; set; } = FormatVersion;
        public string title { get; set; } = "";
        public string author { get; set; } = "";
        public List<ManifestChart> charts { get; set; } = new();
    }

    internal sealed class ManifestChart
    {
        // "game" songs are named by the game's own song name, like "Firefly - 1". Later
        // versions can add "file" songs that bring their own audio.
        public string source { get; set; } = "game";
        public string song { get; set; } = "";
        public int melody { get; set; } = 1;
        // "song" keeps the song's own enemy events; "chart" uses the chart's #ATTACKS.
        public string events { get; set; } = "song";
        public string file { get; set; } = "";
    }

    internal static string Folder
    {
        get
        {
            string path = Path.Combine(Application.persistentDataPath, "NocturneButBetter", "CustomCharts");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>Where "Write game charts" puts the game's own charts. Scans skip it.</summary>
    internal static string GameChartsFolder => Path.Combine(Folder, GameChartsFolderName);

    private const string GameChartsFolderName = "_game charts";

    internal static IReadOnlyList<CustomChart> All => charts ??= Scan();

    internal static void Reload() => charts = null;

    internal static IEnumerable<string> Songs => All.Select(c => c.Song).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

    internal static IEnumerable<CustomChart> ForSong(string song) =>
        All.Where(c => c.Song.Equals(song, StringComparison.OrdinalIgnoreCase));

    /// <summary>The custom difficulty the player chose for a song, if any.</summary>
    internal static CustomChart? Selected(string song)
    {
        string key = PlayerPrefs.GetString(SelectionKeyPrefix + song + SelectionKeySuffix, "");
        if (key.Length == 0) return null;
        return ForSong(song).FirstOrDefault(c => c.Key == key);
    }

    internal static void Select(string song, CustomChart? chart)
    {
        string prefKey = SelectionKeyPrefix + song + SelectionKeySuffix;
        if (chart == null) PlayerPrefs.DeleteKey(prefKey);
        else PlayerPrefs.SetString(prefKey, chart.Key);
        PlayerPrefs.Save();
        ModLog.Info($"Custom chart for {song}: {(chart == null ? "Off" : chart.DisplayName)}");
    }

    /// <summary>The game's song by its name, like "Firefly - 1", or null.</summary>
    internal static SongData? FindSong(string name)
    {
        if (songs != null && songs.TryGetValue(name, out var known) && known) return known;
        // Rebuilt on a miss, since songs load and unload as the game goes.
        songs = new Dictionary<string, SongData>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in Resources.FindObjectsOfTypeAll<SongData>())
            if (song && !songs.ContainsKey(song.name)) songs[song.name] = song;
        return songs.TryGetValue(name, out var found) && found ? found : null;
    }

    /// <summary>How many lanes the song's own chart uses (4, or 5 for a few songs).</summary>
    internal static int LanesOf(SongData song)
    {
        if (LaneCounts.TryGetValue(song.name, out int lanes)) return lanes;
        lanes = 0;
        var maps = song.beatmaps;
        if (maps != null && maps.Length > 0 && maps[0])
        {
            var own = ChartText.Parse(maps[0].text);
            if (own.Blocks.Count > 0) lanes = own.Blocks[0].Lanes;
        }
        LaneCounts[song.name] = lanes;
        return lanes;
    }

    /// <summary>
    /// Whether the song's score is shared with other songs, as in fights split into parts. A
    /// custom chart there would mix its score with the game's, so those songs don't take one yet.
    /// </summary>
    internal static bool SharesScore(SongData song)
    {
        string key = song.HighScoreKey ?? "";
        if (!song.overrideHighScoreKey || key.Length == 0) return false;
        foreach (var other in Resources.FindObjectsOfTypeAll<SongData>())
            if (other && other.name != song.name && other.HighScoreKey == key) return true;
        return false;
    }

    /// <summary>Throws with a readable reason when a chart can't play on its song.</summary>
    private static void Check(IEnumerable<CustomChart> found)
    {
        foreach (var chart in found)
        {
            var song = FindSong(chart.Song) ?? throw new InvalidDataException($"there is no song named \"{chart.Song}\"");
            if (SharesScore(song)) throw new InvalidDataException($"{song.name} is part of a longer fight, which custom charts don't support yet");
            chart.Chart.Validate(chart.BlockIndex);
            int lanes = LanesOf(song);
            if (lanes > 0 && chart.Lanes != lanes)
                throw new InvalidDataException($"\"{chart.Title}\" has {chart.Lanes} lanes but {chart.Song} has {lanes}");
        }
    }

    private static List<CustomChart> Scan()
    {
        var found = new List<CustomChart>();
        string root = Folder;
        IEnumerable<string> files;
        try { files = Directory.GetFiles(root, "*", SearchOption.AllDirectories); }
        catch (Exception ex)
        {
            // An unreadable subfolder must not take the options rows down with it.
            ModLog.Error("Listing the custom chart folder failed: " + ex.Message);
            files = Directory.GetFiles(root);
        }
        foreach (var file in files)
        {
            if (Path.GetRelativePath(root, file).StartsWith(GameChartsFolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (file.EndsWith(".sm", StringComparison.OrdinalIgnoreCase)) AddLoose(found, root, file);
                else if (file.EndsWith(PackExtension, StringComparison.OrdinalIgnoreCase)) AddPack(found, root, file);
            }
            catch (Exception ex) { ModLog.Error($"Skipping custom chart {file}: {ex.Message}"); }
        }
        ModLog.Info($"Custom charts: {found.Count} difficulties for {found.Select(c => c.Song).Distinct().Count()} songs.");
        return found;
    }

    /// <summary>
    /// A loose .sm names its song with #NBBSONG, or by sitting in a folder named after the song.
    /// #NBBMELODY (1 to 3) and #NBBEVENTS (song or chart) are optional.
    /// </summary>
    private static void AddLoose(List<CustomChart> found, string root, string file)
    {
        if (new FileInfo(file).Length > MaxChartBytes) throw new InvalidDataException("the file is too big to be a chart");
        var chart = ChartText.Parse(File.ReadAllText(file));
        string song = chart.GetTag("NBBSONG")?.Trim() ?? "";
        if (song.Length == 0)
        {
            string? folder = Path.GetFileName(Path.GetDirectoryName(file));
            if (folder != null && FindSong(folder) != null) song = folder;
        }
        if (song.Length == 0) throw new InvalidDataException("no song: add #NBBSONG:<song name>; or put it in a folder named after the song");
        int melody = int.TryParse(chart.GetTag("NBBMELODY"), out int m) ? m : 1;
        bool keepEvents = !"chart".Equals(chart.GetTag("NBBEVENTS")?.Trim(), StringComparison.OrdinalIgnoreCase);
        string relative = Path.GetRelativePath(root, file);
        AddBlocks(found, chart, "file:" + relative, song, melody, keepEvents, file, null, "");
    }

    private static void AddPack(List<CustomChart> found, string root, string file)
    {
        using var zip = ZipFile.OpenRead(file);
        var manifest = ReadManifest(zip);
        string relative = Path.GetRelativePath(root, file);
        foreach (var entry in manifest.charts ?? new List<ManifestChart>())
        {
            if (entry == null || !"game".Equals(entry.source, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(entry.song) || string.IsNullOrWhiteSpace(entry.file))
                throw new InvalidDataException("a chart in manifest.json has no song or file");
            var zipEntry = zip.GetEntry(entry.file) ?? throw new InvalidDataException("missing " + entry.file);
            if (zipEntry.Length > MaxChartBytes) throw new InvalidDataException(entry.file + " is too big to be a chart");
            using var reader = new StreamReader(zipEntry.Open(), Encoding.UTF8);
            var chart = ChartText.Parse(reader.ReadToEnd());
            AddBlocks(found, chart, "pack:" + relative + "/" + entry.file, entry.song, entry.melody,
                !"chart".Equals(entry.events, StringComparison.OrdinalIgnoreCase), file, entry.file, manifest.author ?? "");
        }
    }

    private static Manifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("no manifest.json");
        if (entry.Length > MaxChartBytes) throw new InvalidDataException("manifest.json is too big");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var manifest = JsonSerializer.Deserialize<Manifest>(reader.ReadToEnd()) ?? throw new InvalidDataException("empty manifest");
        if (manifest.format > FormatVersion) throw new InvalidDataException($"made for a newer version (format {manifest.format})");
        return manifest;
    }

    private static void AddBlocks(List<CustomChart> found, ChartText chart, string keyBase, string song, int melody,
                                  bool keepEvents, string sourceFile, string? packEntry, string packAuthor)
    {
        string author = chart.GetTag("CREDIT")?.Trim() ?? "";
        if (author.Length == 0) author = packAuthor;
        // The game's own spelling, so the selection saved for a song matches it in battle.
        song = song.Trim();
        song = FindSong(song)?.name ?? song;
        for (int i = 0; i < chart.Blocks.Count; i++)
        {
            var block = chart.Blocks[i];
            found.Add(new CustomChart
            {
                Key = keyBase + "#" + i,
                Song = song,
                Melody = Math.Clamp(melody, 1, 3),
                KeepSongEvents = keepEvents,
                Title = block.DisplayName,
                Author = author,
                Lanes = block.Lanes,
                BlockIndex = i,
                SourceFile = sourceFile,
                PackEntry = packEntry,
                Chart = chart
            });
        }
    }

    /// <summary>Copies a chosen .nbbchart or .sm into the folder after checking it loads.</summary>
    /// <returns>How many difficulties were added; 0 when all of them were already there.</returns>
    internal static int Import(string path)
    {
        string name = Path.GetFileName(path);
        string target;
        var probe = new List<CustomChart>();
        if (path.EndsWith(PackExtension, StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            AddPack(probe, Path.GetDirectoryName(path)!, path);
            target = Path.Combine(Folder, Path.ChangeExtension(name, PackExtension));
        }
        else if (path.EndsWith(".sm", StringComparison.OrdinalIgnoreCase))
        {
            AddLoose(probe, Folder, path);
            if (probe.Count == 0) throw new InvalidDataException("the file has no difficulties in it");
            target = Path.Combine(Folder, Sanitize(probe[0].Song), name);
        }
        else throw new InvalidDataException("choose a " + PackExtension + " or .sm file");
        if (probe.Count == 0) throw new InvalidDataException("the file has no difficulties in it");
        Check(probe);
        // Importing the same charts twice (or a pack of your own exports) would only list them twice.
        var have = new HashSet<string>(All.Select(Fingerprint));
        if (probe.All(c => have.Contains(Fingerprint(c))))
        {
            ModLog.Info($"Everything in {name} is already imported.");
            return 0;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        target = FreeName(target);
        File.Copy(path, target);
        Reload();
        ModLog.Info($"Imported {probe.Count} custom difficulties from {name}.");
        return probe.Count;
    }

    /// <summary>
    /// Saves every loaded song's own .sm, tagged with its song and melody, as a starting point for
    /// new charts: copy one out of the folder, change the notes, and it keeps the song's timing.
    /// </summary>
    internal static int WriteGameCharts()
    {
        string dir = GameChartsFolder;
        Directory.CreateDirectory(dir);
        var bad = Path.GetInvalidFileNameChars();
        int written = 0;
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in Resources.FindObjectsOfTypeAll<SongData>())
        {
            if (!song || !done.Add(song.name)) continue;
            var maps = song.beatmaps;
            if (maps == null) continue;
            string safe = new string(song.name.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray());
            for (int i = 0; i < maps.Length; i++)
            {
                var map = maps[i];
                if (!map || string.IsNullOrWhiteSpace(map.text)) continue;
                string name = maps.Length > 1 ? $"{safe} - melody {i + 1}.sm" : safe + ".sm";
                string text = map.text.TrimStart((char)0xFEFF);
                File.WriteAllText(Path.Combine(dir, name), $"#NBBSONG:{song.name};\n#NBBMELODY:{i + 1};\n" + text);
                written++;
            }
        }
        ModLog.Info($"Wrote {written} game charts for {done.Count} songs to {dir}.");
        return written;
    }

    private static string Fingerprint(CustomChart c) =>
        string.Join("|", c.Song.ToLowerInvariant(), c.Melody, c.KeepSongEvents, c.Title, c.Author,
            new string(c.Chart.Blocks[c.BlockIndex].Notes.Where(ch => !char.IsWhiteSpace(ch)).ToArray()));

    internal static string FreeName(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Writes every given difficulty into one pack, one .sm per song and melody.</summary>
    internal static void Export(string path, IEnumerable<CustomChart> selection, string title)
    {
        // Charts from different files keep their own header (timing, credit, events).
        var groups = selection.GroupBy(c => (c.Song, c.Melody, c.KeepSongEvents, c.Chart)).ToList();
        if (groups.Count == 0) throw new InvalidDataException("there are no custom difficulties to export");
        var manifest = new Manifest { title = title, author = selection.Select(c => c.Author).FirstOrDefault(a => a.Length > 0) ?? "" };
        // Written to a temporary file first, which a failed export doesn't leave behind.
        string temp = path + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        try
        {
            WritePack(temp, groups, manifest);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        ModLog.Info($"Exported {selection.Count()} custom difficulties to {path}.");
    }

    private static void WritePack(string path, List<IGrouping<(string Song, int Melody, bool KeepSongEvents, ChartText Chart), CustomChart>> groups, Manifest manifest)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        int n = 0;
        foreach (var group in groups)
        {
            // One file per source chart, song and melody, holding each chosen block once.
            var chart = new ChartText();
            chart.Tags.AddRange(group.Key.Chart.Tags.Where(t => !t.Key.StartsWith("NBB", StringComparison.OrdinalIgnoreCase)));
            foreach (var c in group) chart.Blocks.Add(c.Chart.Blocks[c.BlockIndex]);
            string file = $"charts/{Sanitize(group.Key.Song)} M{group.Key.Melody}{(n++ > 0 ? " " + n : "")}.sm";
            var entry = zip.CreateEntry(file);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(chart.Write());
            manifest.charts.Add(new ManifestChart
            {
                song = group.Key.Song, melody = group.Key.Melody,
                events = group.Key.KeepSongEvents ? "song" : "chart", file = file
            });
        }
        var manifestEntry = zip.CreateEntry("manifest.json");
        using var mw = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false));
        mw.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0) name = "chart";
        // CON, NUL, COM1 and the like are devices on Windows, even with an extension.
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            name = "_" + name;
        return name;
    }
}
