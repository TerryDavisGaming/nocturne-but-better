using System.Text.Json;

namespace NocturneFlatScroll;

/// <summary>
/// A custom battle's chart for the chart editor (<see cref="ChartEditor.OpenBattle"/>): the
/// battle's folder, its chart and song files as names inside that folder (with '/'), its lane
/// count (4 or 5) and title, and what to run once the editor has closed (the battle creator
/// shows itself again). This file has no Unity or game dependencies.
/// </summary>
internal sealed record BattleChartTarget(string Folder, string ChartPath, string AudioPath, int Lanes, string Title, Action? Closed)
{
    /// <summary>The chart file on disk. Throws when its name isn't one the editor may write (see <see cref="ChartNameProblem"/>).</summary>
    internal string ChartFullPath => ChartNameProblem(ChartPath) is { } bad ? throw new InvalidDataException(bad) : FullPath(ChartPath);

    /// <summary>
    /// battle.json as the battle creator would save it now, so a test play uses its unsaved
    /// changes (title, enemy, gear); null, or no function, reads the file.
    /// </summary>
    internal Func<string?>? Manifest { get; init; }

    /// <summary>
    /// Reads what the editor needs from a battle folder's battle.json: the chart it names, the
    /// song (battle.json's "audio", else the chart's #MUSIC), the lanes (else those of the
    /// chart's first difficulty with notes, else 4) and the title. Nothing else is checked, so a
    /// battle with nothing charted yet opens too.
    /// </summary>
    internal static BattleChartTarget FromFolder(string folder, Action? closed = null)
    {
        var files = PackageFiles.Folder(folder);
        var manifest = JsonSerializer.Deserialize<BattleManifest>(files.ReadAllText(BattlePackage.ManifestName, BattlePackage.MaxJsonBytes), BattlePackage.JsonOptions)
            ?? throw new InvalidDataException(BattlePackage.ManifestName + " is empty");
        string chart = PackageFiles.SafeName(manifest.chart ?? BattlePackage.DefaultChart)
            ?? throw new InvalidDataException("\"chart\" must be a file inside the battle folder");
        ChartText? sm = files.Exists(chart) ? ChartText.Parse(files.ReadAllText(chart, BattlePackage.MaxChartBytes)) : null;
        string audio = PackageFiles.SafeName(manifest.audio ?? sm?.GetTag("MUSIC") ?? "")
            ?? throw new InvalidDataException("the battle names no song file (\"audio\" in " + BattlePackage.ManifestName + ")");
        string? enemyFile = manifest.enemy.ValueKind == JsonValueKind.String ? manifest.enemy.GetString() : null;
        if (ChartFileProblem(folder, chart, ("song", audio), ("card image", manifest.card), ("enemy file", enemyFile), ("dialogue", manifest.dialogue)) is { } bad)
            throw new InvalidDataException(bad);
        int lanes = manifest.lanes ?? sm?.Blocks.FirstOrDefault(ChartText.HasNotes)?.Lanes ?? 4;
        if (lanes != 4 && lanes != 5) throw new InvalidDataException($"\"lanes\" must be 4 or 5, not {lanes}");
        string title = (manifest.title ?? "").Trim();
        if (title.Length == 0) title = Path.GetFileName(folder.TrimEnd('\\', '/'));
        return new BattleChartTarget(folder, chart, audio, lanes, title, closed);
    }

    /// <summary>
    /// Why the chart editor and the battle creator can't write the chart file battle.json's "chart"
    /// names, or null when they can: it must be an .sm file inside the battle whose name has no
    /// control characters (so never battle.json, a song or an image), and not a file the battle
    /// uses for something else (<paramref name="others"/>: what it is, and its name in
    /// battle.json), like an enemy file named .sm. Chart text written over any of them would
    /// break the battle.
    /// </summary>
    internal static string? ChartFileProblem(string folder, string chart, params (string What, string? Name)[] others)
    {
        if (ChartNameProblem(chart) is { } bad) return bad;
        string? full = FullIn(folder, chart);
        if (full == null) return "the chart file must be inside the battle's folder";
        foreach (var (what, name) in others)
            if (!string.IsNullOrWhiteSpace(name) && string.Equals(FullIn(folder, name!), full, StringComparison.OrdinalIgnoreCase))
                return $"the chart file {chart.Trim()} is also the battle's {what}";
        return null;
    }

    /// <summary>The part of <see cref="ChartFileProblem"/> that needs only the chart's name.</summary>
    internal static string? ChartNameProblem(string chart)
    {
        string? name = PackageFiles.SafeName(chart);
        if (name == null) return "the chart file must be inside the battle's folder";
        if (name.Any(char.IsControl)) return "the chart's file name has characters a file name can't have";
        if (!name.EndsWith(".sm", StringComparison.OrdinalIgnoreCase)) return $"the chart file {name} isn't an .sm file";
        return null;
    }

    // The full path of a name inside the battle, or null when it isn't one.
    private static string? FullIn(string folder, string name)
    {
        string? safe = PackageFiles.SafeName(name);
        if (safe == null || safe.Any(char.IsControl)) return null;
        try { return Path.GetFullPath(Path.Combine(folder, safe.Replace('/', Path.DirectorySeparatorChar))).TrimEnd('\\', '/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private string FullPath(string name) =>
        Path.Combine(Folder, (PackageFiles.SafeName(name) ?? throw new InvalidDataException($"\"{name}\" must be a file inside the battle folder"))
            .Replace('/', Path.DirectorySeparatorChar));
}
