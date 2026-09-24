using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// The custom chart rows in Options > Gameplay: import a chart file, pick a song and the custom
/// difficulty it plays, export charts into one pack, open the charts folder, and write the game's
/// own charts there as starting points.
/// </summary>
internal static class CustomChartOptions
{
    private const float StatusSeconds = 5f;
    private static int songIndex;
    private static bool exportAll;
    private static bool writeGameCharts;
    private static string? status;
    private static Row statusRow;
    private static float statusUntil;
    private static Task<string?>? dialog;
    private static bool dialogIsExport;
    private static Action<string>? onChosen;

    private enum Row { Import, Export, Folder }

    internal static readonly OptionsMenuIntegration.RowSpec[] Rows =
    {
        new("StateToggle_FlatChartEditor",
            "Chart editor",
            "Make or edit a custom difficulty for any song, with the song's music, like osu!mania's editor.",
            new[] { "Open..." },
            () => 0,
            (direction, click) => { if (click && ActionAllowed("StateToggle_FlatChartEditor")) ChartEditor.Open(CurrentSong); }),
        new("StateToggle_FlatChartImport",
            "Import custom chart",
            "Adds a custom difficulty from a .nbbchart pack or an .sm file. Opens a file picker.",
            () => new[] { Status(Row.Import) ?? (dialog != null && !dialogIsExport ? "Choosing..." : "Choose file...") },
            () => 0,
            (direction, click) => { if (click && ActionAllowed("StateToggle_FlatChartImport")) StartImport(); }),
        new("StateToggle_FlatChartSong",
            "Custom chart song",
            "The song to pick a custom difficulty for. Only songs with custom charts are listed.",
            () => SongList().Length > 0 ? SongList() : new[] { "No charts yet" },
            () => Math.Min(songIndex, Math.Max(0, SongList().Length - 1)),
            (direction, click) => songIndex = Cycle(songIndex, direction, SongList().Length)),
        new("StateToggle_FlatChartPick",
            "Custom difficulty",
            "Plays this chart instead of the game's own for the song above, whatever difficulty is set. Off plays the game's chart.",
            () => ChartList(),
            SelectedIndex,
            ChangeChart),
        new("StateToggle_FlatChartExport",
            "Export custom charts",
            "Saves custom difficulties into one .nbbchart file to share. Left and right choose which; click to save.",
            () => Status(Row.Export) is { } shown ? new[] { shown }
                : dialog != null && dialogIsExport ? new[] { "Saving..." }
                : new[] { "Save this song...", "Save all songs..." },
            () => Status(Row.Export) != null || (dialog != null && dialogIsExport) ? 0 : exportAll ? 1 : 0,
            (direction, click) =>
            {
                if (!click) exportAll = !exportAll;
                else if (ActionAllowed("StateToggle_FlatChartExport")) StartExport();
            }),
        new("StateToggle_FlatChartFolder",
            "Custom chart folder",
            "Opens the folder that holds your custom charts in Windows Explorer. Write game charts saves the game's own charts into it, to start new ones from.",
            () => Status(Row.Folder) is { } shown ? new[] { shown } : new[] { "Open", "Write game charts" },
            () => Status(Row.Folder) != null ? 0 : writeGameCharts ? 1 : 0,
            (direction, click) =>
            {
                if (!click) writeGameCharts = !writeGameCharts;
                else if (!ActionAllowed("StateToggle_FlatChartFolder")) return;
                else if (writeGameCharts) WriteGameCharts();
                else OpenFolder();
            }),
    };

    // The rows that open a window (Explorer, a file picker, the editor) only do so for a real
    // press: a mouse click has to be on the row itself, nothing fires just after the game gets its
    // focus back from that window, and the same action can't fire twice within a second.
    private const float ActionCooldown = 1f, RefocusGrace = 0.75f;
    private static float lastActionAt = -10f, refocusGuardUntil, lastFrameAt;
    private static bool wasFocused = true;

    private static bool ActionAllowed(string rowName)
    {
        float now = Time.unscaledTime;
        if (!Application.isFocused || now < refocusGuardUntil || now - lastActionAt < ActionCooldown) return false;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse != null && (mouse.leftButton.isPressed || mouse.leftButton.wasPressedThisFrame || mouse.leftButton.wasReleasedThisFrame)
            && !OptionsMenuIntegration.PointerOverRow(rowName, mouse.position.ReadValue()))
            return false;
        lastActionAt = now;
        return true;
    }

    private static void TrackFocus()
    {
        float now = Time.unscaledTime;
        bool focused = Application.isFocused;
        // Coming back to the game, or a long gap between frames (the game paused while in the
        // background), starts the grace period.
        if ((focused && !wasFocused) || now - lastFrameAt > 0.5f) refocusGuardUntil = now + RefocusGrace;
        wasFocused = focused;
        lastFrameAt = now;
    }

    private static string? Status(Row row) => status != null && statusRow == row ? status : null;

    private static string[] SongList() => CustomCharts.Songs.ToArray();

    internal static string? CurrentSong
    {
        get
        {
            var songs = SongList();
            return songs.Length == 0 ? null : songs[Math.Min(songIndex, songs.Length - 1)];
        }
    }

    private static string[] ChartList()
    {
        var song = CurrentSong;
        if (song == null) return new[] { "Off" };
        return new[] { "Off" }.Concat(CustomCharts.ForSong(song).Select(c => c.DisplayName)).ToArray();
    }

    private static int SelectedIndex()
    {
        var song = CurrentSong;
        if (song == null) return 0;
        var chosen = CustomCharts.Selected(song);
        if (chosen == null) return 0;
        var list = CustomCharts.ForSong(song).ToList();
        return list.IndexOf(chosen) + 1;
    }

    private static void ChangeChart(int direction, bool click)
    {
        var song = CurrentSong;
        if (song == null) return;
        var list = CustomCharts.ForSong(song).ToList();
        int next = Cycle(SelectedIndex(), direction, list.Count + 1);
        CustomCharts.Select(song, next == 0 ? null : list[next - 1]);
    }

    private static int Cycle(int value, int direction, int count) =>
        count <= 0 ? 0 : ((value + direction) % count + count) % count;

    private static void StartImport()
    {
        if (dialog != null) return;
        dialog = FileDialogs.Open("Import custom chart", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads",
            "Nocturne But Better charts (*.nbbchart, *.sm)", "*.nbbchart;*.sm;*.zip", "All files", "*.*");
        onChosen = path =>
        {
            int added = CustomCharts.Import(path);
            // Show the song that was just imported in the rows.
            var songs = SongList();
            var imported = CustomCharts.All.LastOrDefault(c => c.SourceFile.EndsWith(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
            if (imported != null) songIndex = Math.Max(0, Array.FindIndex(songs, s => s.Equals(imported.Song, StringComparison.OrdinalIgnoreCase)));
            Show(added == 0 ? "Already imported" : added == 1 ? "Imported 1 chart" : $"Imported {added} charts", Row.Import);
        };
        dialogIsExport = false;
        OptionsMenuIntegration.RefreshAll();
    }

    private static void StartExport()
    {
        if (dialog != null) return;
        var song = CurrentSong;
        var selection = exportAll || song == null ? CustomCharts.All.ToList() : CustomCharts.ForSong(song).ToList();
        if (selection.Count == 0) { Show("Nothing to export", Row.Export); return; }
        string name = exportAll || song == null ? "Custom charts" : song;
        dialog = FileDialogs.Save("Export custom charts", Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            name + CustomCharts.PackExtension, CustomCharts.PackExtension, "Nocturne But Better chart pack (*.nbbchart)", "*.nbbchart");
        onChosen = path =>
        {
            CustomCharts.Export(path, selection, name);
            Show($"Saved {selection.Count} to {Path.GetFileName(path)}", Row.Export);
        };
        dialogIsExport = true;
        OptionsMenuIntegration.RefreshAll();
    }

    internal static void OpenChartFolder() => OpenFolder();

    private static void OpenFolder(string? folder = null)
    {
        try
        {
            string path = Path.GetFullPath(folder ?? CustomCharts.Folder).Replace('/', '\\');
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ModLog.Error("Opening the custom chart folder failed: " + ex); Show("Couldn't open the folder", Row.Folder); }
    }

    private static void WriteGameCharts()
    {
        try
        {
            int written = CustomCharts.WriteGameCharts();
            Show($"Wrote {written} charts", Row.Folder);
            OpenFolder(CustomCharts.GameChartsFolder);
        }
        catch (Exception ex)
        {
            ModLog.Error("Writing the game's charts failed: " + ex);
            Show("Failed: " + ex.Message, Row.Folder);
        }
    }

    private static void Show(string message, Row row)
    {
        status = message;
        statusRow = row;
        statusUntil = Time.unscaledTime + StatusSeconds;
        OptionsMenuIntegration.RefreshAll();
    }

    internal static void MenuShownPrefix()
    {
        try { CustomCharts.Reload(); }
        catch (Exception ex) { ModLog.Error("Rescanning custom charts failed: " + ex); }
    }

    /// <summary>Called every frame: finishes a closed file dialog and clears old messages.</summary>
    internal static void Update()
    {
        TrackFocus();
        CustomChartsMenu.Update();
        if (dialog != null && dialog.IsCompleted)
        {
            var finished = dialog;
            var action = onChosen;
            var row = dialogIsExport ? Row.Export : Row.Import;
            dialog = null;
            onChosen = null;
            try
            {
                string? path = finished.Result;
                if (path == null) OptionsMenuIntegration.RefreshAll();
                else action?.Invoke(path);
            }
            catch (Exception ex)
            {
                var reason = ex is AggregateException agg && agg.InnerException != null ? agg.InnerException : ex;
                ModLog.Error("Custom chart file failed: " + reason);
                Show("Failed: " + reason.Message, row);
            }
        }
        if (status != null && Time.unscaledTime >= statusUntil)
        {
            status = null;
            OptionsMenuIntegration.RefreshAll();
        }
    }
}
