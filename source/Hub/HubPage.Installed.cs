using UnityEngine;
using static NocturnePlus.EditorInput;
using static NocturnePlus.EditorPageKit;
using static NocturnePlus.EditorUi;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using Key = UnityEngine.InputSystem.Key;

namespace NocturnePlus;

// Installed (DESIGN-HUB 1.5): everything the hub installed, from Hub\installed.json with the saved
// thumbnails, so it works without the hub. When the hub answers, one lookup marks each item up to
// date, updatable (the tab counts them), removed or deleted; removed items stay installed. Enter
// updates (the file keeps its name, so a pack's pick and a battle's scores stay) or, for a pack,
// opens Use it now. Del sends the file to the Recycle Bin (or, where there's none, deletes an
// untouched download for good after a second confirm).
internal static partial class HubPage
{
    private static List<HubInstalledItem> installedRows = new();
    private static int installedIndex;
    private static HashSet<string> inUse = new(StringComparer.Ordinal);
    private static bool lookupLoading;

    private static void ResetInstalled()
    {
        installedRows = new List<HubInstalledItem>();
        installedIndex = 0;
        inUse = new HashSet<string>(StringComparer.Ordinal);
        lookupLoading = false;
    }

    /// <summary>The list again from the store, and which packs are in use.</summary>
    private static void RefreshInstalled()
    {
        if (store == null) return;
        string? was = installedRows.Count > 0 ? installedRows[Math.Clamp(installedIndex, 0, installedRows.Count - 1)].Package : null;
        installedRows = store.Installed.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Package, StringComparer.Ordinal).ToList();
        int at = was == null ? -1 : installedRows.FindIndex(i => i.Package == was);
        installedIndex = at >= 0 ? at : Math.Clamp(installedIndex, 0, Math.Max(0, installedRows.Count - 1));
        RefreshInUse();
    }

    // Which installed packs have a difficulty picked for its song (the IN USE tag). The game's
    // custom charts are read on the main thread.
    private static void RefreshInUse()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (store != null)
        {
            try
            {
                foreach (var item in store.Installed.Where(i => i.Kind == "charts"))
                    if (store.Paths.Full(item.Path) is { } full && File.Exists(full) && HubGame.InUse(full)) used.Add(item.Package);
            }
            catch (Exception ex) { ReportOnce("checking which custom difficulties are picked", ex); }
        }
        inUse = used;
    }

    /// <summary>Asks the hub about every installed entry (50 a call): up to date, updatable, removed.</summary>
    private static void LookupInstalled()
    {
        if (store == null || api == null || lookupLoading || link is not (Link.Online or Link.TooOld)) return;
        var ids = store.Installed.Select(i => i.Package).ToList();
        if (ids.Count == 0) return;
        lookupLoading = true;
        var a = api;
        var s = store;
        var ct = Ct;
        jobs.Run(Task.Run(async () =>
        {
            var items = await a.LookupAsync(ids, ct).ConfigureAwait(false);
            s.ApplyLookup(items);
            return items.Count;
        }, ct), _ =>
        {
            lookupLoading = false;
            RefreshInstalled();
        }, ex =>
        {
            lookupLoading = false;
            LogFailure("checking for updates", ex);
        });
    }

    /// <summary>F5 on Installed: the Downloaded folders read again (files deleted in Explorer drop out), then the hub asked again.</summary>
    private static void RescanInstalled()
    {
        var s = store;
        var r = recycler;
        if (s == null || r == null) return;
        Work("Reading the Downloaded folders...", Task.Run(() => Rescan(s, r)), ids =>
        {
            localBattleIds = ids;
            RefreshInstalled();
            LookupInstalled();
        });
    }

    /// <summary>F5 on Browse: the same, while the list keeps working (INSTALLED and YOU HAVE IT follow).</summary>
    private static void RescanLocal()
    {
        var s = store;
        var r = recycler;
        if (s == null || r == null) return;
        jobs.Run(Task.Run(() => Rescan(s, r)), ids =>
        {
            localBattleIds = ids;
            RefreshInstalled();
        }, ex => LogFailure("reading the Downloaded folders", ex));
    }

    // A worker's job: the list made to match the Downloaded folders, and the battle ids on this PC the hub didn't install.
    private static HashSet<string> Rescan(HubStore s, IHubRecycler r)
    {
        HubInstall.CleanLeftovers(s.Reconcile(), s, r);
        return s.LocalBattleIds(BattleFiles.List(s.Paths.Battles));
    }

    private static HubInstalledItem? SelectedInstalled => installedRows.Count > 0 ? installedRows[Math.Clamp(installedIndex, 0, installedRows.Count - 1)] : null;

    private static Texture? InstalledPicture(HubInstalledItem item)
    {
        var p = store!.Paths;
        return thumbs.Want("i:" + item.Package, () => HubThumbs.CheckedFile(p.Thumb(item.Package)));
    }

    private static (string Tag, Color Color) InstalledTag(HubInstalledItem item)
    {
        if (download?.Id == item.Package) return (DownloadTag(), Accent);
        return store!.StatusOf(item) switch
        {
            HubInstalledStatus.UpdateAvailable => ("UPDATE", Amber),
            HubInstalledStatus.Removed => ("REMOVED", Red),
            HubInstalledStatus.Deleted => ("DELETED", DimText),
            HubInstalledStatus.UnderReview => ("UNDER REVIEW", DimText),
            _ => inUse.Contains(item.Package) ? ("IN USE", Green) : ("INSTALLED", Green),
        };
    }

    private static string KindBadge(HubInstalledItem item) =>
        (item.Kind == "battle" ? "BATTLE" : "DIFFICULTIES") + (item.Lanes > 0 ? $" {item.Lanes}K" : "");

    private static string InstalledSub(HubInstalledItem item)
    {
        string by = item.Uploader != null ? "uploaded by " + UploaderName(item.Uploader) : "";
        if (item.Kind == "battle") return by;
        string songs = item.Songs != null && item.Songs.Count > 0 ? "for " + Songs(item.Songs) : "custom difficulties";
        return by.Length > 0 ? songs + " - " + by : songs;
    }

    private static ThumbRow InstalledRow(int i)
    {
        var item = installedRows[i];
        var row = new ThumbRow { Picture = InstalledPicture(item) };
        (row.Tile, row.TileColor) = HubThumbs.Tile(item.Package, item.Title);
        row.Title = item.Title;
        row.Sub = InstalledSub(item);
        row.Note = store!.StatusWords(item);
        (row.Tag, row.TagColor) = InstalledTag(item);
        row.Meta = KindBadge(item) + "\n" + Size(item.Size) + (item.Version > 0 ? $"   v{item.Version}" : "");
        return row;
    }

    // After the rows are drawn: the details of an update, for the panel's "v3 to v4" line.
    private static void AfterInstalledDrawn()
    {
        var item = SelectedInstalled;
        if (item != null && store!.StatusOf(item) == HubInstalledStatus.UpdateAvailable) WantDetail(item.Package);
    }

    private static void InstalledPanel(PanelView v)
    {
        var item = SelectedInstalled;
        if (item == null)
        {
            v.NoPicture = true;
            v.Title = "Installed";
            v.Body = "What you download from the hub shows here, with its updates. This tab works without the internet, too.";
            return;
        }
        v.Picture = InstalledPicture(item);
        (v.Tile, v.TileColor) = HubThumbs.Tile(item.Package, item.Title);
        v.Title = item.Title;
        v.Line1 = item.Kind == "battle" ? "a custom battle" : item.Songs != null && item.Songs.Count > 0 ? "for " + Songs(item.Songs, 2) : "custom difficulties";
        v.Line2 = item.Uploader != null ? "uploaded by " + UploaderName(item.Uploader) : "";
        v.Line3 = item.InstalledAt > 0 ? "installed " + Date(item.InstalledAt) : "";
        var (tag, color) = InstalledTag(item);
        v.Badge = KindBadge(item) + $"   <color=#{HexOf(color)}>{tag}</color>";
        var facts = new List<string> { store!.StatusWords(item), (item.Version > 0 ? $"v{item.Version}  -  " : "") + Size(item.Size) };
        if (item.Contents != null) facts.Add("Inside: " + Inside(item.Contents));
        v.Facts = string.Join("\n", facts);
        v.Table = "File: " + item.Path.Replace('/', '\\');
        v.Body = item.Kind == "battle"
            ? "It's in the arcade's custom battles, with your scores kept by its battle id. An update keeps them."
            : "Its difficulties are in Options > Custom Charts. Use it now picks them for their songs; an update keeps the picks.";
        var hub = store.LookupFor(item.Package);
        if (store.StatusOf(item) == HubInstalledStatus.UpdateAvailable && hub != null)
        {
            details.TryGetValue(item.Package, out var detail);
            v.Note = detail != null && detail.Version >= hub.Version
                ? ChangeSummary(item, detail.Version, detail.File.Size, detail.Contents)
                : ChangeSummary(item, hub.Version, 0, null);
        }
        else if (store.StatusOf(item) is HubInstalledStatus.Removed or HubInstalledStatus.Deleted)
            v.Note = "It's gone from the hub, but your copy stays and keeps working.";
    }

    private static List<PanelAction> InstalledActions()
    {
        var actions = new List<PanelAction>();
        var item = SelectedInstalled;
        if (item == null) return actions;
        string enter = PadNames ? "A" : "Enter";
        bool update = store!.StatusOf(item) == HubInstalledStatus.UpdateAvailable && link == Link.Online;
        if (download?.Id == item.Package)
            actions.Add(new PanelAction { Text = "Stop the download", Key = PadNames ? "B" : "Esc", Do = AskStopDownload });
        else if (update)
            actions.Add(new PanelAction { Text = $"Update to v{store.LookupFor(item.Package)!.Version}", Key = enter, Do = () => UpdateInstalled(item) });
        if (item.Kind == "charts")
            actions.Add(new PanelAction { Text = "Use it now", Key = update ? "" : enter, Do = () => UseInstalled(item.Package) });
        if (download?.Id != item.Package)
            actions.Add(new PanelAction { Text = "Delete", Key = PadNames ? "" : "Del", Do = () => AskDelete(item) });
        if (link is Link.Online or Link.TooOld)
            actions.Add(new PanelAction { Text = "Report...", Key = PadNames ? "" : "R", Do = () => StartReport(item.Package, item.Title) });
        return actions;
    }

    private static void UpdateInstalled(HubInstalledItem item)
    {
        var hub = store!.LookupFor(item.Package);
        details.TryGetValue(item.Package, out var detail);
        StartDownload(item.Package, item.Title, detail?.File.Size ?? item.Size);
        if (hub != null) ModLog.Info($"Hub: updating {item.Package} from v{item.Version} to v{hub.Version}.");
    }

    private static void InstalledPrimary()
    {
        var item = SelectedInstalled;
        if (item == null) return;
        if (download?.Id == item.Package)
        {
            AskStopDownload();
            return;
        }
        if (store!.StatusOf(item) == HubInstalledStatus.UpdateAvailable)
        {
            if (link == Link.Online) UpdateInstalled(item);
            else Say("Updating needs the hub, which can't be used now. " + linkProblem, 6f);
            return;
        }
        if (item.Kind == "charts") UseInstalled(item.Package);
        else Say("It's in the arcade's custom battles.", 3f);
    }

    private static bool InstalledKeys(InputKeyboard k)
    {
        var item = SelectedInstalled;
        if (item == null || Ctrl(k) || Alt(k)) return false;
        if (Pressed(k, Key.Delete))
        {
            if (download?.Id == item.Package) Say("It's downloading; stop the download first.", 3f);
            else AskDelete(item);
            return true;
        }
        if (Pressed(k, Key.R))
        {
            StartReport(item.Package, item.Title);
            return true;
        }
        return false;
    }

    /// <summary>"v3 to v4: 31 MB (was 24 MB), adds a video, 2 more pictures."</summary>
    private static string ChangeSummary(HubInstalledItem item, int version, long size, HubContents? contents)
    {
        string from = item.Version > 0 ? $"v{item.Version}" : "The version you have";
        var parts = new List<string> { $"{from} to v{version}" + (size > 0 ? $": {Size(size)} (was {Size(item.Size)})" : "") };
        if (contents != null && item.Contents != null)
        {
            void Diff(int now, int was, string one, string many)
            {
                int d = now - was;
                if (d == 0) return;
                if (was == 0 && d == 1) parts.Add($"adds a {one}");
                else if (d > 0) parts.Add($"{d} more {(d == 1 ? one : many)}");
                else parts.Add($"{-d} {(d == -1 ? one : many)} fewer");
            }
            Diff(contents.Songs, item.Contents.Songs, "song", "songs");
            Diff(contents.Pictures, item.Contents.Pictures, "picture", "pictures");
            Diff(contents.Videos, item.Contents.Videos, "video", "videos");
            Diff(contents.Charts, item.Contents.Charts, "chart", "charts");
        }
        return string.Join(", ", parts) + ".";
    }

    // ---- delete (DESIGN-HUB 3.5) ---------------------------------------------------------------------

    private static void AskDelete(HubInstalledItem item)
    {
        var confirm = Confirm($"Delete {item.Title}?", "No, keep it", "Yes, delete it",
            "It goes to the Recycle Bin. You can download it again from the hub.", () => Delete(item), BackToMain);
        confirm.Plain = true;
        ShowPicker(confirm);
    }

    private static void Delete(HubInstalledItem item)
    {
        var s = store!;
        var r = recycler!;
        Work($"Deleting {item.Title}...", Task.Run(() => HubInstall.Delete(item, s, r)), outcome =>
        {
            switch (outcome)
            {
                case HubDeleteOutcome.AskForGood:
                    // The second prompt has its Yes on the other row, so one spot clicked twice can't answer both.
                    var confirm = Confirm($"{item.Title} can't go to the Recycle Bin. Delete it for good?", "No, keep it", "Yes, delete it for good",
                        "Windows has no Recycle Bin for that drive. You can download it again from the hub.", () => DeleteForGood(item), BackToMain, yesFirst: true);
                    confirm.Plain = true;
                    ShowPicker(confirm);
                    return;
                case HubDeleteOutcome.Changed:
                    BackToMain();
                    Say($"{item.Title} can't go to the Recycle Bin, and it changed since it was downloaded, so it wasn't deleted. Delete it in Explorer if you want it gone.", 10f);
                    return;
                default:
                    Deleted(item, outcome == HubDeleteOutcome.Gone ? $"{item.Title} was already gone." : $"{item.Title} went to the Recycle Bin.");
                    return;
            }
        }, ex =>
        {
            BackToMain();
            Say(Words(ex), 8f);
        });
    }

    private static void DeleteForGood(HubInstalledItem item)
    {
        var s = store!;
        Work($"Deleting {item.Title}...", Task.Run(() =>
        {
            HubInstall.DeleteForGood(item, s);
            return true;
        }), _ => Deleted(item, $"{item.Title} is deleted."), ex =>
        {
            BackToMain();
            Say(Words(ex), 8f);
        });
    }

    private static void Deleted(HubInstalledItem item, string message)
    {
        ModLog.Info($"Hub: deleted {item.Package} ({item.Path}).");
        if (item.Kind == "charts") HubGame.Instance.ChartsChanged();
        RefreshInstalled();
        RescanLocalBattles();
        BackToMain();
        Say(message, 6f);
    }

    // ---- Use it now (DESIGN-HUB 1.4) ----------------------------------------------------------------

    /// <summary>A pack that's installed: its difficulties to pick for their songs.</summary>
    private static void UseInstalled(string package)
    {
        var item = store?.InstalledFor(package);
        if (item == null || item.Kind != "charts") return;
        if (store!.Paths.Full(item.Path) is not { } full || !File.Exists(full))
        {
            Say("Its file is gone. F5 reads the Downloaded folders again.", 5f);
            return;
        }
        HubGame.UseResult result;
        try { result = HubGame.UseItNow(full, pick: false); }
        catch (Exception ex)
        {
            ReportOnce("reading a pack's difficulties", ex);
            Say("Its difficulties can't be read now (see the log).", 5f);
            return;
        }
        ShowUsePicker(item, result, 0);
    }

    /// <summary>Right after a pack is installed: its difficulties are picked for songs without a pick, and the picker says so.</summary>
    private static void UseAfterInstall(HubInstalledItem item, bool update)
    {
        string done = update ? $"{item.Title} is updated to v{item.Version}." : $"Installed {item.Title}.";
        if (store!.Paths.Full(item.Path) is not { } full)
        {
            Say(done, 6f);
            return;
        }
        HubGame.UseResult result;
        try { result = HubGame.UseItNow(full, pick: true); }
        catch (Exception ex)
        {
            ReportOnce("picking a pack's difficulties", ex);
            Say(done + " Its difficulties are in Options > Custom Charts.", 8f);
            return;
        }
        RefreshInUse();
        var words = new List<string> { done };
        var picked = result.Rows.Where(r => r.Picked).ToList();
        if (picked.Count == 1) words.Add($"{picked[0].Song} now plays {picked[0].Charts[0].DisplayName}. Options > Custom Charts turns it off.");
        else if (picked.Count > 1) words.Add($"{picked.Count} songs now play its difficulties. Options > Custom Charts turns them off.");
        var kept = result.Rows.Where(r => r.Kept != null && !r.Charts.Any(c => c.Key == r.Kept.Key)).ToList();
        if (kept.Count > 0) words.Add($"{Songs(kept.Select(r => r.Song), 2)} {(kept.Count == 1 ? "keeps its" : "keep their")} pick; choose one here to use this pack's instead.");
        if (result.Duplicates.Count > 0) words.Add(result.Duplicates[0] + (result.Duplicates.Count > 1 ? $" (and {result.Duplicates.Count - 1} more)." : "."));
        string message = string.Join(" ", words);
        ModLog.Info("Hub: " + message);
        if (screen == Screen.Main && !Blocked && result.Rows.Count > 0) ShowUsePicker(item, result, 0, message);
        else Say(message, 12f);
    }

    private static void ShowUsePicker(HubInstalledItem item, HubGame.UseResult result, int index, string? message = null)
    {
        var targets = new List<(string Song, CustomCharts.CustomChart? Chart)>();
        var rows = new List<string>();
        foreach (var r in result.Rows)
            foreach (var chart in r.Charts)
            {
                bool now = CustomCharts.Selected(r.Song)?.Key == chart.Key;
                rows.Add($"{r.Song}: {chart.DisplayName}{(now ? "   (plays now)" : "")}");
                targets.Add((r.Song, chart));
            }
        rows.Add("Done");
        targets.Add(("", null));
        string enter = PadNames ? "A" : "Enter";
        ShowPicker(new Picker
        {
            Plain = true,
            Heading = $"{item.Title}: use it now",
            Rows = rows,
            Index = Math.Clamp(index, 0, rows.Count - 1),
            Hint = i =>
            {
                var (song, chart) = targets[i];
                if (chart == null) return "Back to the hub.";
                var current = CustomCharts.Selected(song);
                if (current?.Key == chart.Key) return $"{song} plays this now. Options > Custom Charts turns it off.";
                return current != null ? $"{song} plays {current.DisplayName} now. {enter} uses this one instead." : $"{enter} makes {song} play this.";
            },
            Choose = i =>
            {
                var (song, chart) = targets[i];
                if (chart == null)
                {
                    BackToMain();
                    return;
                }
                string words;
                if (CustomCharts.Selected(song)?.Key == chart.Key) words = $"{song} already plays this. Options > Custom Charts turns it off.";
                else
                {
                    CustomCharts.Select(song, chart);
                    OptionsMenuIntegration.RefreshAll();
                    RefreshInUse();
                    words = $"{song} now plays {chart.DisplayName}. Options > Custom Charts turns it off.";
                }
                ShowUsePicker(item, result, i, words);
            },
            Back = BackToMain,
        });
        if (message != null) Say(message, 10f);
    }
}
