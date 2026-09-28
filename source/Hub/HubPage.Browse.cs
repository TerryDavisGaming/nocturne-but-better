using UnityEngine;
using static NocturnePlus.EditorInput;
using static NocturnePlus.EditorPageKit;
using static NocturnePlus.EditorUi;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using Key = UnityEngine.InputSystem.Key;

namespace NocturnePlus;

// Browse (DESIGN-HUB 1.3, 1.4): the hub's entries, 24 a page as the list scrolls, with a search
// (the list follows it a second after the last key once the last word has 3 letters, or on Enter:
// the hub takes 10 searches a minute from an address, and each page of one counts), type, lanes
// and sort, and "more by this uploader". Each row has its thumbnail or a title tile, the title and
// artist, who charted and uploaded it, its difficulties as coloured chips, what it brings, its size
// and downloads, and its state (INSTALLED, UPDATE, 45% ...). The panel adds the description, the
// difficulty table and what's inside. One download at a time; it's checked in full before anything
// is installed. Report sends a reason and a note.
internal static partial class HubPage
{
    private static HubBrowseList? browse;
    private static int browseGen, browseIndex;
    private static bool browseLoading, liveSearching;
    private static string? browseProblem;
    // When the page that failed is asked for again by itself (after "slow down", or a later page), or 0.
    private static float browseRetryAt;
    // A filter changed while a search shows: the list follows a moment later, so cycling one spends one search.
    private static float pendingRestartAt;
    private const float LiveSearchDelay = 1f, FilterSearchDelay = 0.6f, LaterPageRetry = 15f;
    // The search kept (Enter), and the one the list shows (while typing, what's typed so far).
    private static string search = "", liveSearch = "";
    private static string? kindFilter;
    private static int lanesFilter;
    private static HubSort sort;
    private static HubUploaderRef? byUploader;
    private static HashSet<string> localBattleIds = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HubDetail> details = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> detailProblems = new(StringComparer.Ordinal);
    private static readonly HashSet<string> detailLoading = new(StringComparer.Ordinal);
    private static string detailWanted = "";
    private static float detailWantedAt;
    private static readonly HashSet<string> failedIds = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Failure> failures = new(StringComparer.Ordinal);

    /// <summary>Why an entry's last download failed, for its FAILED tag and "Press R to report it".</summary>
    private sealed class Failure
    {
        internal string Words = "";
        internal IReadOnlyList<string> Problems = Array.Empty<string>();
        internal bool Damaged;
    }

    private static readonly Color Green = Hex(0x4FD1A5), Amber = Hex(0xF2B02E), Red = Hex(0xE0525A), Lilac = Hex(0xB9A6FF);

    private static void ResetBrowse()
    {
        browse = null;
        browseGen++;
        browseIndex = 0;
        browseLoading = liveSearching = false;
        browseProblem = null;
        browseRetryAt = pendingRestartAt = 0;
        search = liveSearch = "";
        kindFilter = null;
        lanesFilter = 0;
        sort = HubSort.Default;
        byUploader = null;
        localBattleIds = new HashSet<string>(StringComparer.Ordinal);
        details.Clear();
        detailProblems.Clear();
        detailLoading.Clear();
        detailWanted = "";
        failedIds.Clear();
        failures.Clear();
        download = null;
        typingSearch = false;
        reportId = reportTitle = reportReason = reportNote = "";
    }

    // ---- the filters, remembered in Hub\settings.json -------------------------------------------------

    private static void ReadSettings()
    {
        var s = store!.Settings;
        kindFilter = s.LastKind is "battle" or "charts" ? s.LastKind : null;
        lanesFilter = s.LastLanes is 4 or 5 ? s.LastLanes : 0;
        sort = s.LastSort switch { "popular" => HubSort.Popular, "title" => HubSort.Title, _ => HubSort.Default };
    }

    private static void SaveSettings()
    {
        if (store == null) return;
        var s = store.Settings;
        s.LastKind = kindFilter ?? "all";
        s.LastLanes = lanesFilter;
        s.LastSort = sort switch { HubSort.Popular => "popular", HubSort.Title => "title", _ => "new" };
        try { store.SaveSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ReportOnce("saving the hub's settings", ex); }
    }

    private static readonly TextField SearchField = new()
    {
        Label = "Search",
        Get = () => search,
        Set = SetSearch,
        Max = HubText.SearchMaxRaw,
        Empty = () => "title, artist, charter or song",
        Hint = "Type to search; the list follows once the last word has 3 letters. Enter searches now and keeps it, Esc goes back.",
    };

    private static void SetSearch(string text)
    {
        search = text.Trim();
        if (Normal(search) != Normal(liveSearch)) RestartBrowse(search);
        else liveSearch = search;
    }

    private static string Normal(string text) => new HubListQuery { Search = text }.Normalized;

    private static void StartSearchTyping()
    {
        zone = Zone.Bar;
        barFocus = 0;
        Kit.StartTyping(SearchField);
    }

    private static bool typingSearch;
    private static string typedSeen = "";
    private static float typedAt;
    private static bool typedChecked;

    // While the search is typed, the list follows a second after the last key, once the last word has
    // 3 letters (a shorter last word only matches whole words, and every search counts against the
    // hub's limit); Enter searches at once, and Esc goes back to the kept search.
    private static void AfterSearchTyping()
    {
        if (Kit.Typing == SearchField)
        {
            if (!typingSearch || Kit.Typed != typedSeen)
            {
                typingSearch = true;
                typedSeen = Kit.Typed;
                typedAt = Time.unscaledTime;
                typedChecked = false;
            }
            else if (!typedChecked && Time.unscaledTime - typedAt >= LiveSearchDelay)
            {
                typedChecked = true;
                if (link == Link.Online && Normal(typedSeen) != Normal(liveSearch) && FollowsTyping(typedSeen)) RestartBrowse(typedSeen);
            }
            return;
        }
        if (!typingSearch) return;
        typingSearch = false;
        if (link == Link.Online && Normal(liveSearch) != Normal(search)) RestartBrowse(search);
    }

    // Whether the list follows what's typed so far: a cleared search (the plain list isn't a search),
    // or a last word of 3 or more letters (the hub matches it as the start of a word then).
    private static bool FollowsTyping(string typed)
    {
        string normal = Normal(typed);
        if (normal.Length == 0) return true;
        string last = normal.Substring(normal.LastIndexOf(' ') + 1);
        // Counted in code points, as the hub counts them.
        return last.Count(c => !char.IsLowSurrogate(c)) >= 3;
    }

    private static string KindWords() => kindFilter switch { "battle" => "Battles", "charts" => "Difficulties", _ => "All" };

    private static string LanesWords() => lanesFilter == 0 ? "Any" : $"{lanesFilter} lanes";

    private static string SortWords()
    {
        if (byUploader != null && !liveSearching) return "Newest";
        return sort switch
        {
            HubSort.New => "Newest",
            HubSort.Popular => "Most downloaded",
            HubSort.Title => "Title (A-Z)",
            _ => liveSearching ? "Best match" : "Newest",
        };
    }

    private static void CycleKind(int step)
    {
        if (!Kit.FinishTyping()) return;
        var kinds = new string?[] { null, "battle", "charts" };
        int i = Array.IndexOf(kinds, kindFilter);
        kindFilter = kinds[((i + step) % kinds.Length + kinds.Length) % kinds.Length];
        FiltersChanged();
    }

    private static void CycleLanes(int step)
    {
        if (!Kit.FinishTyping()) return;
        var lanes = new[] { 0, 4, 5 };
        int i = Array.IndexOf(lanes, lanesFilter);
        lanesFilter = lanes[((i + step) % lanes.Length + lanes.Length) % lanes.Length];
        FiltersChanged();
    }

    private static void CycleSort(int step)
    {
        if (!Kit.FinishTyping()) return;
        if (byUploader != null && !liveSearching)
        {
            Say("More by this uploader is always newest first (a search can sort it).", 4f);
            return;
        }
        var sorts = new List<HubSort> { HubSort.Default };
        if (liveSearching) sorts.Add(HubSort.New);
        // Most downloaded shows only while the hub counts downloads.
        if (info?.Counts ?? false) sorts.Add(HubSort.Popular);
        sorts.Add(HubSort.Title);
        int i = Math.Max(0, sorts.IndexOf(sort));
        sort = sorts[((i + step) % sorts.Count + sorts.Count) % sorts.Count];
        FiltersChanged();
    }

    private static void FiltersChanged()
    {
        SaveSettings();
        if (link != Link.Online) return;
        // While a search shows, the list follows a moment after the last change: each search counts against the hub's limit.
        if (liveSearching) pendingRestartAt = Time.unscaledTime + FilterSearchDelay;
        else RestartBrowse(liveSearch);
    }

    private static void MoreBy(HubCard card)
    {
        if (!HubText.IsUploaderId(card.Uploader.Id)) return;
        byUploader = new HubUploaderRef { Id = card.Uploader.Id, Name = card.Uploader.Name, Tag = card.Uploader.Tag };
        if (link == Link.Online) RestartBrowse(liveSearch);
        zone = Zone.List;
    }

    private static void ClearUploader()
    {
        if (byUploader == null) return;
        byUploader = null;
        if (link == Link.Online) RestartBrowse(liveSearch);
    }

    internal static string UploaderName(HubUploaderRef? u)
    {
        if (u == null) return "";
        string tag = u.Tag.Length > 0 ? " #" + u.Tag.ToUpperInvariant() : "";
        return (u.Name.Length > 0 ? u.Name : "someone") + tag;
    }

    // ---- loading the list -------------------------------------------------------------------------

    /// <summary>A new list for the filters and <paramref name="text"/> (the kept search when null), from its first page.</summary>
    private static void RestartBrowse(string? text = null)
    {
        liveSearch = text ?? search;
        var query = new HubListQuery { Kind = kindFilter, Lanes = lanesFilter, Uploader = byUploader?.Id, Search = liveSearch };
        liveSearching = query.Searching;
        // "Newest" is the default without a search; with one it's asked for by name.
        query.Sort = sort == HubSort.New && !liveSearching ? HubSort.Default : sort;
        browse = new HubBrowseList(query);
        browseGen++;
        browseIndex = 0;
        browseLoading = false;
        browseProblem = null;
        browseRetryAt = 0;
        pendingRestartAt = 0;
        if (tab == Tab.Browse) list?.ResetScroll();
        LoadMoreBrowse();
    }

    private static void LoadMoreBrowse()
    {
        var list = browse;
        // A page that failed is asked for again only by F5 (or by itself after a wait, see browseRetryAt).
        if (list == null || browseLoading || browseProblem != null || !list.HasMore || link != Link.Online || api == null) return;
        browseLoading = true;
        int gen = browseGen;
        var a = api;
        jobs.Run(Task.Run(() => list.FetchNextAsync(a, Ct)), page =>
        {
            if (gen != browseGen) return;
            browseLoading = false;
            list.Add(page);
        }, ex =>
        {
            if (gen != browseGen) return;
            browseLoading = false;
            LogFailure("loading the list", ex);
            if (Unwrap(ex) is HubException hub && hub.HubDown)
            {
                LinkFailed(hub);
                return;
            }
            // "Slow down" is asked again once the hub's wait is over; a later page after a while (the rows so far stay).
            if (Unwrap(ex) is HubException { Code: "slow_down" } slow)
            {
                browseProblem = "Slow down a little.";
                double wait = (slow.RetryAfter ?? TimeSpan.FromSeconds(30)).TotalSeconds;
                browseRetryAt = Time.unscaledTime + (float)Math.Clamp(wait, 1, 120);
            }
            else
            {
                browseProblem = Words(ex);
                browseRetryAt = list.Started ? Time.unscaledTime + LaterPageRetry : 0;
            }
        });
    }

    /// <summary>The page that failed, asked for again (the rows so far stay).</summary>
    private static void RetryBrowsePage()
    {
        browseProblem = null;
        browseRetryAt = 0;
        LoadMoreBrowse();
    }

    // After the rows are drawn: a filter change that waited, a failed page's retry, the next page once
    // the rows in view reach near the end, and the picked entry's details.
    private static void AfterBrowseDrawn()
    {
        float now = Time.unscaledTime;
        if (pendingRestartAt > 0 && now >= pendingRestartAt && link == Link.Online)
        {
            RestartBrowse(liveSearch);
            return;
        }
        if (browseRetryAt > 0 && now >= browseRetryAt && link == Link.Online) RetryBrowsePage();
        var items = browse?.Items;
        if (items == null) return;
        if (items.Count == 0 || browseIndex >= items.Count - 3 || list!.NearEnd(items.Count, 3)) LoadMoreBrowse();
        if (items.Count > 0) WantDetail(items[Math.Clamp(browseIndex, 0, items.Count - 1)].Id);
    }

    // Seconds until a failed page is asked for again.
    private static int RetrySeconds() => Math.Max(1, (int)Math.Ceiling(browseRetryAt - Time.unscaledTime));

    /// <summary>What went wrong with the list, and what happens next (the message over an empty list, or the line under the rows).</summary>
    private static string BrowseProblemText(bool empty)
    {
        if (browseProblem == null) return "";
        if (browseRetryAt > 0) return $"{browseProblem} The list tries again in {RetrySeconds()} s.";
        string retry = PadNames ? (empty ? "A tries again." : "") : "F5 tries again.";
        return empty ? $"{browseProblem}\n\n{retry}" : $"{browseProblem} {retry}".TrimEnd();
    }

    /// <summary>The line under Browse's rows: a later page that failed or is loading, or the hub being out of reach.</summary>
    private static string BrowseFoot()
    {
        if (browse == null || browse.Items.Count == 0) return "";
        if (link == Link.Down) return $"{linkProblem} {(PadNames ? "A" : "F5")} tries again.";
        if (browseProblem != null) return "Couldn't load more: " + BrowseProblemText(empty: false);
        if (browseLoading) return "Loading more...";
        return "";
    }

    /// <summary>
    /// Asks for an entry's details (the description, what's inside, the file's facts) once the
    /// selection has stayed on it for a moment; kept for the page's session.
    /// </summary>
    private static void WantDetail(string id)
    {
        if (link is not (Link.Online or Link.TooOld) || api == null) return;
        if (details.ContainsKey(id) || detailLoading.Contains(id) || detailProblems.ContainsKey(id)) return;
        if (detailWanted != id)
        {
            detailWanted = id;
            detailWantedAt = Time.unscaledTime;
            return;
        }
        if (Time.unscaledTime - detailWantedAt < 0.25f) return;
        detailLoading.Add(id);
        var a = api;
        jobs.Run(Task.Run(() => a.DetailAsync(id, Ct)), detail =>
        {
            detailLoading.Remove(id);
            details[id] = detail;
        }, ex =>
        {
            detailLoading.Remove(id);
            detailProblems[id] = Words(ex);
            LogFailure("loading an entry's details", ex);
        });
    }

    private static HubCard? SelectedCard => browse != null && browse.Items.Count > 0 ? browse.Items[Math.Clamp(browseIndex, 0, browse.Items.Count - 1)] : null;

    // ---- the rows ----------------------------------------------------------------------------------

    private static HubRowState StateOf(HubCard card)
    {
        var state = store!.StateOf(card, localBattleIds, identity?.UploaderId, download?.Id, failedIds);
        return state == HubRowState.Installed && !card.IsBattle && inUse.Contains(card.Id) ? HubRowState.InUse : state;
    }

    private static (string Tag, Color Color) TagOf(HubRowState state) => state switch
    {
        HubRowState.Downloading => (DownloadTag(), Accent),
        HubRowState.Installed => ("INSTALLED", Green),
        HubRowState.InUse => ("IN USE", Green),
        HubRowState.Update => ("UPDATE", Amber),
        HubRowState.Yours => ("YOURS", Lilac),
        HubRowState.YouHaveIt => ("YOU HAVE IT", DimText),
        HubRowState.NeedsNewerMod => ("NEEDS A NEWER MOD", Amber),
        HubRowState.Failed => ("FAILED", Red),
        _ => ("", TextColor),
    };

    private static string DownloadTag()
    {
        var d = download;
        if (d == null) return "";
        return d.Transfer.Stage switch
        {
            "Downloading" => $"{(int)(d.Transfer.Fraction * 100)}%",
            "Installing" => "INSTALLING",
            "Checking" => "CHECKING",
            _ => "STARTING",
        };
    }

    internal static Color LevelColor(int level) => level <= 0 ? DimText : level <= 3 ? Green : level <= 6 ? Hex(0x5AA9E6) : level <= 9 ? Hex(0xF2A33A) : Red;

    private static string ChipText(HubDifficulty d) => d.Level > 0 ? $"{d.Name} {d.Level}" : d.Name;

    private static void AddChips(ThumbRow row, HubCard card)
    {
        IEnumerable<HubDifficulty> all = card.IsBattle ? card.Difficulties : (card.Songs ?? new List<HubSongDifficulties>()).SelectMany(s => s.Difficulties);
        foreach (var d in all.Take(64)) row.Chips.Add((ChipText(d), LevelColor(d.Level)));
    }

    private static string KindBadge(string kind, int lanes) => (kind == "battle" ? "BATTLE" : "DIFFICULTIES") + $" {lanes}K";

    private static string FlagWords(HubFlags flags)
    {
        var words = new List<string>();
        if (flags.Gear) words.Add("sets your gear");
        if (flags.Level) words.Add("sets your level");
        if (flags.Dialogue) words.Add("dialogue");
        if (flags.Video) words.Add("video");
        return string.Join("   ", words.Select(w => "(" + w + ")"));
    }

    private static string Songs(IEnumerable<string> songs, int shown = 3)
    {
        var list = songs.ToList();
        return list.Count <= shown ? string.Join(", ", list) : string.Join(", ", list.Take(shown)) + $" and {list.Count - shown} more";
    }

    // Who charted it and who uploaded it; a pack names its songs instead. All the hub's text: shown plain.
    private static string CardSub(HubCard card)
    {
        var parts = new List<string>();
        if (card.IsBattle)
        {
            if (card.Author.Length > 0) parts.Add("charted by " + card.Author);
            if (card.Source != null && card.Source.Kind == "osu!mania")
                parts.Add(card.Source.Mapper.Length > 0 ? "from osu!mania, mapped by " + card.Source.Mapper : "from osu!mania");
            parts.Add("uploaded by " + UploaderName(card.Uploader));
        }
        else
        {
            var songs = (card.Songs ?? new List<HubSongDifficulties>()).Select(s => s.Song);
            parts.Add("for " + Songs(songs));
            parts.Add("by " + UploaderName(card.Uploader));
        }
        return string.Join(" - ", parts);
    }

    private static string TitleLine(string title, string artist) => artist.Length > 0 ? $"{title} - {artist}" : title;

    private static string SizeAndCount(long size, long? downloads) =>
        Size(size) + (downloads is long n ? $"   {Count(n)} {(n == 1 ? "download" : "downloads")}" : "");

    private static Texture? CardPicture(HubCard card) =>
        thumbs.Want("c:" + card.Id + ":" + card.Version, () => HubThumbs.Checked(card.Thumb));

    private static ThumbRow BrowseRow(int i)
    {
        var card = browse!.Items[i];
        var row = new ThumbRow { Picture = CardPicture(card) };
        (row.Tile, row.TileColor) = HubThumbs.Tile(card.Id, card.Title);
        row.Title = TitleLine(card.Title, card.Artist);
        row.Sub = CardSub(card);
        AddChips(row, card);
        row.Note = FlagWords(card.Flags);
        (row.Tag, row.TagColor) = TagOf(StateOf(card));
        row.Meta = KindBadge(card.Kind, card.Lanes) + "\n" + SizeAndCount(card.Size, card.Downloads);
        return row;
    }

    // ---- the panel -------------------------------------------------------------------------------------

    private static void BrowsePanel(PanelView v)
    {
        var card = SelectedCard;
        if (card == null)
        {
            v.NoPicture = true;
            v.Title = "Get Custom Battles";
            v.Body = "Find custom battles and custom difficulties that other players made, and share yours.\n\n" +
                     "Pick an entry on the left to see what's in it. Downloads are checked in full before they're installed, and they're never run as programs.";
            return;
        }
        details.TryGetValue(card.Id, out var detail);
        if (detail != null && detail.Version < card.Version) detail = null;
        CardPanel(v, card, detail, StateOf(card));
    }

    /// <summary>An entry in the panel, from its row and, once they're loaded, its details.</summary>
    private static void CardPanel(PanelView v, HubCard card, HubDetail? detail, HubRowState state)
    {
        v.Picture = CardPicture(card);
        (v.Tile, v.TileColor) = HubThumbs.Tile(card.Id, card.Title);
        v.Title = card.Title;
        v.Line1 = card.Artist;
        if (card.IsBattle)
        {
            v.Line2 = card.Author.Length > 0 ? "charted by " + card.Author : "";
            if (card.Source != null && card.Source.Kind == "osu!mania")
                v.Line2 += (v.Line2.Length > 0 ? " - " : "") + (card.Source.Mapper.Length > 0 ? "from osu!mania, mapped by " + card.Source.Mapper : "from osu!mania");
        }
        else v.Line2 = "for " + Songs((card.Songs ?? new List<HubSongDifficulties>()).Select(s => s.Song), 2);
        v.Line3 = "uploaded by " + UploaderName(card.Uploader);
        var (tag, color) = TagOf(state);
        v.Badge = KindBadge(card.Kind, card.Lanes) + (tag.Length > 0 ? $"   <color=#{HexOf(color)}>{tag}</color>" : "");

        var facts = new List<string>();
        if (card.LengthSeconds is double length) facts.Add(Clock(length));
        if (card.Bpm is { } bpm) facts.Add(Math.Abs(bpm[0] - bpm[1]) < 0.01 ? $"{Num(bpm[0])} BPM" : $"{Num(bpm[0])}-{Num(bpm[1])} BPM");
        facts.Add($"v{card.Version}");
        facts.Add(SizeAndCount(detail?.File.Size ?? card.Size, card.Downloads));
        if (card.UpdatedAt > 0) facts.Add("updated " + Date(card.UpdatedAt));
        var lines = new List<string> { string.Join("  -  ", facts) };
        if (detail != null) lines.Add("Inside: " + Inside(detail.Contents));
        string flags = FlagWords(card.Flags);
        if (flags.Length > 0) lines.Add(flags);
        v.Facts = string.Join("\n", lines);
        v.Table = DifficultyTable(card);
        if (detail != null) v.Body = detail.Description.Length > 0 ? detail.Description : "(no description)";
        else if (detailProblems.TryGetValue(card.Id, out var problem)) v.Body = problem;
        else v.Body = link is Link.Online or Link.TooOld ? "Loading the details..." : "";
        v.Note = StateNote(card, state, detail);
    }

    private static string Num(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"1 song, 14 pictures, 1 video, 2 charts" from what's inside a package.</summary>
    internal static string Inside(HubContents c)
    {
        var parts = new List<string>();
        if (c.Songs > 0) parts.Add(Plural(c.Songs, "song", "songs"));
        if (c.Pictures > 0) parts.Add(Plural(c.Pictures, "picture", "pictures"));
        if (c.Videos > 0) parts.Add(Plural(c.Videos, "video", "videos"));
        if (c.Charts > 0) parts.Add(Plural(c.Charts, "chart", "charts"));
        int other = c.Json + c.Other;
        if (other > 0) parts.Add(Plural(other, "other file", "other files"));
        return parts.Count == 0 ? $"{Plural(c.Files, "file", "files")}" : string.Join(", ", parts);
    }

    // The difficulties as a table (the hub's text, shown plain): a battle's, or each song's in a pack.
    private static string DifficultyTable(HubCard card)
    {
        string Line(HubDifficulty d) => $"{d.Name}   level {d.Level}   {Plural(d.Notes, "note", "notes")}";
        if (card.IsBattle) return string.Join("\n", card.Difficulties.Take(8).Select(Line));
        var songs = card.Songs ?? new List<HubSongDifficulties>();
        return string.Join("\n", songs.Take(8).Select(s => $"{s.Song}: {string.Join(", ", s.Difficulties.Select(ChipText))} ({s.Difficulties.Count})"))
               + (songs.Count > 8 ? $"\n... and {songs.Count - 8} more songs" : "");
    }

    // What the state means for this entry, in the mod's own words.
    private static string StateNote(HubCard card, HubRowState state, HubDetail? detail)
    {
        switch (state)
        {
            case HubRowState.YouHaveIt:
                return "This battle is already on your PC (your own folder, or a copy you added), so the hub doesn't install a second one.";
            case HubRowState.NeedsNewerMod:
                return "It needs a newer version of the mod. Update the mod to play it.";
            case HubRowState.Yours:
                return "You uploaded this. My uploads has its new version and delete.";
            case HubRowState.Failed:
                return failures.TryGetValue(card.Id, out var f) ? f.Words + (f.Damaged && !PadNames ? " Press R to report it as broken." : "") : "";
            case HubRowState.Update:
                var item = store!.InstalledFor(card.Id);
                return item != null ? ChangeSummary(item, card.Version, detail?.File.Size ?? card.Size, detail?.Contents) : "";
            case HubRowState.Installed:
                return card.IsBattle ? "Installed. It's in the arcade's custom battles." : "Installed. Use it now picks its difficulties for their songs.";
            case HubRowState.InUse:
                return "Installed, and one of its difficulties plays for its song now.";
            default:
                return "";
        }
    }

    private static List<PanelAction> BrowseActions()
    {
        var actions = new List<PanelAction>();
        var card = SelectedCard;
        if (card == null) return actions;
        string enter = PadNames ? "A" : "Enter";
        switch (StateOf(card))
        {
            case HubRowState.None:
                actions.Add(new PanelAction { Text = "Download", Key = enter, Do = () => StartDownload(card.Id, card.Title, card.Size) });
                break;
            case HubRowState.Failed:
                actions.Add(new PanelAction { Text = "Try again", Key = enter, Do = () => StartDownload(card.Id, card.Title, card.Size) });
                break;
            case HubRowState.Update:
                actions.Add(new PanelAction { Text = $"Update to v{card.Version}", Key = enter, Do = () => StartDownload(card.Id, card.Title, card.Size) });
                break;
            case HubRowState.Downloading:
                actions.Add(new PanelAction { Text = "Stop the download", Key = PadNames ? "B" : "Esc", Do = AskStopDownload });
                break;
            case HubRowState.Installed when !card.IsBattle:
            case HubRowState.InUse:
                actions.Add(new PanelAction { Text = "Use it now", Key = enter, Do = () => UseInstalled(card.Id) });
                break;
            case HubRowState.Yours:
                actions.Add(new PanelAction { Text = "Show in My uploads", Key = enter, Do = () => ShowInMine(card.Id) });
                break;
        }
        if (HubText.IsUploaderId(card.Uploader.Id))
        {
            if (byUploader?.Id == card.Uploader.Id) actions.Add(new PanelAction { Text = "All uploaders", Key = PadNames ? "" : "M", Do = ClearUploader });
            else actions.Add(new PanelAction { Text = "More by this uploader", Key = PadNames ? "" : "M", Do = () => MoreBy(card) });
        }
        actions.Add(new PanelAction { Text = "Report...", Key = PadNames ? "" : "R", Do = () => ReportCard(card) });
        if (store!.InstalledFor(card.Id) is { } item && download?.Id != card.Id)
            actions.Add(new PanelAction { Text = "Delete", Key = PadNames ? "" : "Del", Do = () => AskDelete(item) });
        if (details.TryGetValue(card.Id, out var detail) && detail.Version >= card.Version && LongDescription(detail.Description))
            actions.Add(ReadDescription(card.Title, detail.Description));
        return actions;
    }

    private static void BrowsePrimary()
    {
        var card = SelectedCard;
        if (card == null) return;
        switch (StateOf(card))
        {
            case HubRowState.None:
            case HubRowState.Failed:
            case HubRowState.Update:
                StartDownload(card.Id, card.Title, card.Size);
                break;
            case HubRowState.Downloading:
                AskStopDownload();
                break;
            case HubRowState.InUse:
                UseInstalled(card.Id);
                break;
            case HubRowState.Installed:
                if (card.IsBattle) Say("It's installed: it's in the arcade's custom battles.", 4f);
                else UseInstalled(card.Id);
                break;
            case HubRowState.Yours:
                ShowInMine(card.Id);
                break;
            case HubRowState.YouHaveIt:
                Say("This battle is already on your PC, so the hub doesn't install a second one.", 5f);
                break;
            case HubRowState.NeedsNewerMod:
                Say("It needs a newer version of the mod.", 4f);
                break;
        }
    }

    private static bool BrowseKeys(InputKeyboard k)
    {
        if ((Ctrl(k) && Pressed(k, Key.F)) || Pressed(k, Key.Slash))
        {
            StartSearchTyping();
            return true;
        }
        if (Ctrl(k) || Alt(k)) return false;
        int step = Shift(k) ? -1 : 1;
        if (Pressed(k, Key.T)) { CycleKind(step); return true; }
        if (Pressed(k, Key.L)) { CycleLanes(step); return true; }
        if (Pressed(k, Key.S)) { CycleSort(step); return true; }
        var card = SelectedCard;
        if (card == null) return false;
        if (Pressed(k, Key.M))
        {
            if (byUploader?.Id == card.Uploader.Id) ClearUploader();
            else MoreBy(card);
            return true;
        }
        if (Pressed(k, Key.R))
        {
            ReportCard(card);
            return true;
        }
        if (Pressed(k, Key.Delete) && store!.InstalledFor(card.Id) is { } item)
        {
            if (download?.Id == card.Id) Say("It's downloading; stop the download first.", 3f);
            else AskDelete(item);
            return true;
        }
        return false;
    }

    private static void ShowInMine(string id)
    {
        SetTab(Tab.Mine);
        pendingMineSelect = id;
        SelectMine();
    }

    // ---- downloading (DESIGN-HUB 3.4) --------------------------------------------------------------

    private sealed class Download
    {
        internal string Id = "", Title = "";
        internal bool Update;
        internal readonly HubTransfer Transfer = new();
        internal CancellationTokenSource Cts = null!;
        internal HubVerified? Verified;
        internal bool Stopped;
    }

    private static Download? download;

    /// <summary>
    /// Downloads an entry and installs it: the listing's details first (the file's SHA-256 and
    /// fingerprint), the file into Hub\work with every check (a worker), the game's own check here,
    /// then the move into Downloaded (a worker), and for a pack, Use it now.
    /// </summary>
    private static void StartDownload(string id, string title, long size)
    {
        if (download != null)
        {
            Say(download.Id == id ? $"It's downloading. {(PadNames ? "B" : "Esc")} stops it."
                : $"One download at a time. Wait for {download.Title}, or press {(PadNames ? "B" : "Esc")} to stop it.", 5f);
            return;
        }
        if (link != Link.Online || api == null || store == null)
        {
            Say(link == Link.TooOld ? HubErrorsText.TooOld : linkProblem.Length > 0 ? linkProblem : "The hub isn't connected yet.", 6f);
            return;
        }
        var d = new Download { Id = id, Title = title, Update = store.InstalledFor(id) != null, Cts = CancellationTokenSource.CreateLinkedTokenSource(Ct) };
        download = d;
        failedIds.Remove(id);
        failures.Remove(id);
        d.Transfer.Start(size, "Starting");
        var a = api;
        var s = store;
        var r = recycler!;
        var limits = HubZipCheck.Limits.From(info);
        var ct = d.Cts.Token;
        ModLog.Info($"Hub: downloading {id}.");
        jobs.Run(Task.Run(async () =>
        {
            var detail = await a.DetailAsync(id, ct).ConfigureAwait(false);
            return await HubInstall.DownloadAsync(a, s, detail, limits, d.Transfer, ct).ConfigureAwait(false);
        }, ct), verified =>
        {
            d.Verified = verified;
            details[id] = verified.Detail;
            if (d.Stopped)
            {
                HubInstall.Discard(verified);
                FinishDownload(d);
                return;
            }
            // The game's own check, here on the main thread: its chart reader for a battle, the custom-chart check for a pack.
            string? why = HubInstall.GameCheck(verified, HubGame.Instance);
            if (why != null)
            {
                HubInstall.Discard(verified);
                DownloadFailed(d, new HubException("damaged", "That download doesn't play in the game.") { Problems = new[] { why } });
                return;
            }
            d.Transfer.Stage = "Installing";
            jobs.Run(Task.Run(() => HubInstall.Place(verified, s, r)), item => Installed(d, item), ex =>
            {
                HubInstall.Discard(verified);
                DownloadFailed(d, ex);
            });
        }, ex => DownloadFailed(d, ex));
    }

    private static void FinishDownload(Download d)
    {
        if (download == d) download = null;
    }

    private static void Installed(Download d, HubInstalledItem item)
    {
        FinishDownload(d);
        ModLog.Info($"Hub: installed {item.Package} v{item.Version} as {item.Path}.");
        api?.CountInstall(item.Package, item.Version);
        RefreshInstalled();
        if (item.Kind == "charts")
        {
            HubGame.Instance.ChartsChanged();
            UseAfterInstall(item, d.Update);
        }
        else Say(d.Update ? $"{d.Title} is updated to v{item.Version}. Its scores stay." : $"Installed. {d.Title} is in the arcade's custom battles.", 8f);
    }

    private static void DownloadFailed(Download d, Exception ex)
    {
        FinishDownload(d);
        ex = Unwrap(ex);
        if (d.Stopped || ex is OperationCanceledException)
        {
            if (IsOpen) Say($"The download of {d.Title} stopped. Nothing was installed.", 5f);
            return;
        }
        var hub = ex as HubException;
        bool damaged = hub != null && hub.Code == "damaged";
        var failure = new Failure { Words = Words(ex), Problems = hub?.Problems ?? Array.Empty<string>(), Damaged = damaged };
        failedIds.Add(d.Id);
        failures[d.Id] = failure;
        LogFailure("downloading " + d.Id, ex);
        if (hub != null && hub.Code == "you_have_it") RescanLocalBattles();
        Say(failure.Words + (damaged && !PadNames ? " Press R to report it as broken." : ""), 10f);
    }

    private static void AskStopDownload()
    {
        var d = download;
        if (d == null) return;
        if (d.Transfer.Stage == "Installing")
        {
            Say("It's being installed now; that can't be stopped.", 3f);
            return;
        }
        ShowPicker(new Picker
        {
            Plain = true,
            Heading = $"Stop downloading {d.Title}?",
            Rows = { "Keep downloading", "Stop the download", "Stop it and close the hub" },
            Hint = i => i switch
            {
                0 => "The download goes on.",
                1 => "Nothing is installed, and what was downloaded so far is removed.",
                _ => "Stops the download, then closes the hub.",
            },
            Choose = i =>
            {
                if (i > 0 && download == d)
                {
                    d.Stopped = true;
                    try { d.Cts.Cancel(); }
                    catch (ObjectDisposedException) { }
                    download = null;
                    ModLog.Info($"Hub: the download of {d.Id} was stopped.");
                }
                if (i == 2) Close("Close");
                else BackToMain();
            },
            Back = BackToMain,
        });
    }

    private static void AskStopDownload(bool leaving) => AskStopDownload();

    // After an install or delete: the battle ids on this PC that the hub didn't install (YOU HAVE IT).
    private static void RescanLocalBattles()
    {
        var s = store;
        if (s == null) return;
        jobs.Run(Task.Run(() => s.LocalBattleIds(BattleFiles.List(s.Paths.Battles))), ids => localBattleIds = ids, ex => LogFailure("reading the battles", ex));
    }

    // ---- reports (DESIGN-HUB 1.8) ------------------------------------------------------------------

    private static string reportId = "", reportTitle = "", reportReason = "", reportNote = "";

    private static void ReportCard(HubCard card)
    {
        // A download that failed its checks is reported as broken, with what was found.
        if (failures.TryGetValue(card.Id, out var f) && f.Damaged)
        {
            string note = HubText.CleanText("The download didn't pass the mod's checks: " + string.Join("; ", f.Problems), HubText.Note) ?? "";
            StartReport(card.Id, card.Title, "broken", note);
        }
        else StartReport(card.Id, card.Title);
    }

    private static void StartReport(string id, string title, string? reason = null, string note = "")
    {
        if (link is not (Link.Online or Link.TooOld))
        {
            Say("Reports go to the hub, which can't be used now. " + linkProblem, 6f);
            return;
        }
        if (!HubText.IsPackageId(id)) return;
        reportId = id;
        reportTitle = title;
        reportNote = note;
        if (reason != null)
        {
            reportReason = reason;
            ShowForm(FormKind.Report);
            return;
        }
        reportReason = "";
        PickReportReason(backToForm: false);
    }

    private static void PickReportReason() => PickReportReason(backToForm: true);

    private static void PickReportReason(bool backToForm)
    {
        int current = Math.Max(0, Array.FindIndex(HubText.ReportReasons, r => r.Code == reportReason));
        ShowPicker(new Picker
        {
            Plain = true,
            Heading = "Report " + reportTitle,
            Rows = HubText.ReportReasons.Select(r => r.Words).ToList(),
            Index = current,
            Hint = _ => "Why are you reporting it? The hub's owner reads every report.",
            Choose = i =>
            {
                reportReason = HubText.ReportReasons[i].Code;
                ShowForm(FormKind.Report);
            },
            Back = () =>
            {
                if (backToForm) ShowForm(FormKind.Report);
                else BackToMain();
            },
        });
    }

    private static string ReasonWords(string code) => HubText.ReportReasons.FirstOrDefault(r => r.Code == code).Words ?? "(pick one)";

    private static void BuildReportForm()
    {
        var form = NewForm(FormKind.Report, () => "Report");
        float y = -96;
        FormPlain(form, 40, ref y, FormW - 80, 34, () => reportTitle, 24);
        var field = new TextField
        {
            Label = "Note (optional)",
            Get = () => reportNote,
            Set = text => reportNote = HubText.CleanText(text, HubText.Note) ?? "",
            Max = HubText.Note,
            MultiLine = true,
            Empty = () => "(click to add a note for the hub's owner)",
        };
        Kit.AddChoice(form.Panel, form.Controls, 40, ref y, 1000, "Reason", () => Escape(ReasonWords(reportReason)), PickReportReason);
        Kit.AddField(form.Panel, form.Controls, 40, ref y, 1000, field, h: 180);
        Kit.AddText(form.Panel, 40, ref y, 1000, 84, () =>
            "The hub's owner reads every report; nothing is hidden by reports alone. Your hub key goes with the report " +
            "(the hub keeps only a scrambled form of it), so the hub can tell a second report of the same entry from yours.", 18);
        Kit.AddButton(form.Panel, form.Controls, 40, y - 8, 280, 52, "Send the report", SendReport);
        Kit.AddButton(form.Panel, form.Controls, 332, y - 8, 200, 52, "Back", BackToMain);
        form.Back = BackToMain;
    }

    private static void SendReport()
    {
        if (!Kit.FinishTyping()) return;
        if (reportReason.Length == 0)
        {
            PickReportReason();
            return;
        }
        if (api == null || paths == null) return;
        string id = reportId, reason = reportReason, note = reportNote, name = defaultName;
        var a = api;
        var p = paths;
        var ct = Ct;
        // The key is made now if there isn't one yet: a report needs no registration.
        Work("Sending the report...", Task.Run(async () =>
        {
            var who = HubIdentity.LoadOrCreate(p.Identity, name);
            string status = await a.ReportAsync(id, reason, note, who.Key, ct).ConfigureAwait(false);
            return (who, status);
        }, ct), result =>
        {
            identity ??= result.who;
            identityProblem = null;
            BackToMain();
            ModLog.Info($"Hub: reported {id} ({reason}): {result.status}.");
            Say(result.status == "already" ? "You already reported this." : "Thanks, the hub's owner will look at it.", 7f);
        });
    }
}
