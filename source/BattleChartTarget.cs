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
    /// <summary>The chart file on disk.</summary>
    internal string ChartFullPath => FullPath(ChartPath);

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
        int lanes = manifest.lanes ?? sm?.Blocks.FirstOrDefault(ChartText.HasNotes)?.Lanes ?? 4;
        if (lanes != 4 && lanes != 5) throw new InvalidDataException($"\"lanes\" must be 4 or 5, not {lanes}");
        string title = (manifest.title ?? "").Trim();
        if (title.Length == 0) title = Path.GetFileName(folder.TrimEnd('\\', '/'));
        return new BattleChartTarget(folder, chart, audio, lanes, title, closed);
    }

    private string FullPath(string name) =>
        Path.Combine(Folder, (PackageFiles.SafeName(name) ?? throw new InvalidDataException($"\"{name}\" must be a file inside the battle folder"))
            .Replace('/', Path.DirectorySeparatorChar));
}
