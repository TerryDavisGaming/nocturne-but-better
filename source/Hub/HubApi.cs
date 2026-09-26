using System.Net;
using System.Net.Http.Headers;

namespace NocturneFlatScroll;

/// <summary>
/// Where the hub is and what this mod can play from it (DESIGN-HUB 3.8, 2.14). The address is built
/// in (<see cref="ModInfo.HubUrl"/>); only a QA build may use another one, from NFS_QA_HUB_URL, and
/// only there is plain http allowed, for this PC's own address. The hub is on when the player's
/// Options switch is on and the build has an address; otherwise the mod never contacts it.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class HubConfig
{
#if MELONLOADER
    internal const string Loader = "MelonLoader";
#else
    internal const string Loader = "BepInEx";
#endif
    internal const string QaUrlVariable = "NFS_QA_HUB_URL";
    internal const string QaIdentityVariable = "NFS_QA_HUB_IDENTITY";

    /// <summary>The newest package formats this mod reads: battle.json's and a pack's manifest.json's.</summary>
    internal const int BattleFormat = BattlePackage.FormatVersion, PackFormat = 1;

    /// <summary>
    /// The feature names (a package's "requires") this mod plays. Empty in the first hub version:
    /// everything it uploads plays on it. A later mod adds names here as packages start using them.
    /// </summary>
    internal static readonly HashSet<string> KnownFeatures = new(StringComparer.Ordinal);

    internal static string UserAgent(string modVersion) => $"NocturneButBetter/{modVersion} ({Loader})";

    /// <summary>
    /// The hub's address: the QA one when a QA build has it (<paramref name="qa"/>), else the built-in
    /// one. Null, with why, when there's none or it isn't allowed.
    /// </summary>
    internal static Uri? Address(string builtIn, string? qa, out string? problem)
    {
        if (!string.IsNullOrWhiteSpace(qa)) return Parse(qa!.Trim(), allowLoopbackHttp: true, out problem);
        if (string.IsNullOrWhiteSpace(builtIn))
        {
            problem = "this build has no hub address";
            return null;
        }
        return Parse(builtIn.Trim(), allowLoopbackHttp: false, out problem);
    }

    /// <summary>An https address with nothing after the host (a QA address may be http on this PC's own address).</summary>
    internal static Uri? Parse(string text, bool allowLoopbackHttp, out string? problem)
    {
        problem = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            problem = "the hub address isn't an address";
            return null;
        }
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || (uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/"))
        {
            problem = "the hub address must be only a host (no path, query or user)";
            return null;
        }
        bool https = uri.Scheme == Uri.UriSchemeHttps;
        bool loopbackHttp = uri.Scheme == Uri.UriSchemeHttp && allowLoopbackHttp && IsLoopback(uri.Host);
        if (!https && !loopbackHttp)
        {
            problem = allowLoopbackHttp ? "the hub address must be https (or http on 127.0.0.1, localhost or [::1])" : "the hub address must be https";
            return null;
        }
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static bool IsLoopback(string host) => host is "127.0.0.1" or "localhost" or "[::1]" or "::1";

    internal static bool Enabled(bool switchOn, Uri? address) => switchOn && address != null;

    /// <summary>Whether this mod can play an entry: its format isn't newer than ours and it needs no feature we don't know.</summary>
    internal static bool Playable(HubCard card) =>
        card.Format <= (card.IsBattle ? BattleFormat : PackFormat) && card.Requires.All(KnownFeatures.Contains);
}

/// <summary>How a list is sorted: Default is newest first without a search and best match with one.</summary>
internal enum HubSort { Default, New, Popular, Title }

/// <summary>The filters of a list or search (DESIGN-HUB 1.3), as the hub's strict query string has them.</summary>
internal sealed class HubListQuery
{
    /// <summary>"battle", "charts", or null for both.</summary>
    internal string? Kind { get; set; }
    /// <summary>4 or 5, or 0 for both.</summary>
    internal int Lanes { get; set; }
    /// <summary>More by this uploader (an uploader id), or null.</summary>
    internal string? Uploader { get; set; }
    /// <summary>The search as typed; the hub gets its normalized form.</summary>
    internal string Search { get; set; } = "";
    internal HubSort Sort { get; set; }

    /// <summary>The search as sent: normalized, and short enough for the hub (200 UTF-16 units).</summary>
    internal string Normalized
    {
        get
        {
            var words = HubText.NormalizeSearch(Search).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (words.Count > 0 && string.Join(" ", words).Length > HubText.SearchMaxRaw) words.RemoveAt(words.Count - 1);
            return string.Join(" ", words);
        }
    }

    internal bool Searching => Normalized.Length > 0;

    /// <summary>
    /// The list's address: parameters in the hub's order, each once, defaults left out, the search
    /// normalized and encoded like JavaScript's encodeURIComponent, and a page cursor exactly as the
    /// hub sent it. An uploader's list without a search is always newest first.
    /// </summary>
    internal string Path(string? cursor)
    {
        var parts = new List<string>();
        if (Kind is "battle" or "charts") parts.Add("kind=" + Kind);
        if (Lanes is 4 or 5) parts.Add("lanes=" + Lanes);
        bool uploader = HubText.IsUploaderId(Uploader);
        if (uploader) parts.Add("uploader=" + Uploader);
        string q = Normalized;
        if (q.Length > 0) parts.Add("q=" + HubText.EncodeUriComponent(q));
        string? sort = Sort switch
        {
            HubSort.Popular => "popular",
            HubSort.Title => "title",
            HubSort.New when q.Length > 0 => "new",
            _ => null,
        };
        if (uploader && q.Length == 0) sort = null;
        if (sort != null) parts.Add("sort=" + sort);
        if (!string.IsNullOrEmpty(cursor)) parts.Add("cursor=" + HubText.EncodeUriComponent(cursor!));
        return "/v1/packages" + (parts.Count > 0 ? "?" + string.Join("&", parts) : "");
    }
}

/// <summary>
/// A list being browsed: its pages so far, without repeats, and the cursor for the next one. The
/// page asks for the next page when the selection comes near the end (DESIGN-HUB 1.3).
/// </summary>
internal sealed class HubBrowseList
{
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);

    internal HubBrowseList(HubListQuery query) => Query = query;

    internal HubListQuery Query { get; }
    internal List<HubCard> Items { get; } = new();
    internal string? Next { get; private set; }
    internal bool Started { get; private set; }
    internal bool HasMore => !Started || Next != null;

    /// <summary>Loads the next page (the first when none was loaded yet) and adds its new entries.</summary>
    internal async Task<int> LoadMoreAsync(HubApi api, CancellationToken ct)
    {
        if (!HasMore) return 0;
        var page = await api.ListAsync(Query, Started ? Next : null, ct).ConfigureAwait(false);
        Started = true;
        Next = page.Next;
        int added = 0;
        foreach (var card in page.Items)
            if (seen.Add(card.Id))
            {
                Items.Add(card);
                added++;
            }
        return added;
    }
}

/// <summary>
/// The hub's API as typed calls (server/API.md), each on a worker thread; the page waits for them
/// with its job runner and carries on on the main thread. Keyed calls take the hub key and send it
/// in that request's Authorization header only. Everything that comes back is cleaned and checked
/// (<see cref="HubCard.Clean"/>) before the mod uses it.
/// This file has no Unity or game dependencies.
/// </summary>
internal sealed class HubApi : IDisposable
{
    internal HubApi(Uri baseUri, string modVersion)
    {
        ModVersion = modVersion;
        Http = new HubHttp(baseUri, HubConfig.UserAgent(modVersion));
    }

    internal HubHttp Http { get; }
    internal string ModVersion { get; }

    public void Dispose() => Http.Dispose();

    private static string Id(string id) =>
        HubText.IsPackageId(id) ? id : throw new ArgumentException("not a package id", nameof(id));

    // ---- public routes --------------------------------------------------------------------------

    internal async Task<HubInfo> InfoAsync(CancellationToken ct) =>
        (await Http.CallAsync<HubInfo>(HttpMethod.Get, "/v1/info", null, null, HubHttp.SmallCap, HubContext.Info, ct).ConfigureAwait(false)).Clean();

    internal async Task<HubListPage> ListAsync(HubListQuery query, string? cursor, CancellationToken ct)
    {
        var page = await Http.CallAsync<HubListPage>(HttpMethod.Get, query.Path(cursor), null, null, HubHttp.ListCap, HubContext.Browse, ct).ConfigureAwait(false);
        page.Items = (page.Items ?? new List<HubCard>()).Where(c => c != null && c.Clean()).ToList();
        if (page.Next != null && (page.Next.Length > 1024 || page.Next.Any(ch => ch <= ' ' || ch > '~'))) page.Next = null;
        return page;
    }

    /// <summary>The status of installed entries, 50 ids a call (sorted, no repeats, as the hub wants them).</summary>
    internal async Task<List<HubLookupItem>> LookupAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var sorted = ids.Where(HubText.IsPackageId).Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal).ToList();
        var found = new List<HubLookupItem>();
        for (int at = 0; at < sorted.Count; at += 50)
        {
            var chunk = sorted.Skip(at).Take(50);
            var answer = await Http.CallAsync<HubLookupAnswer>(HttpMethod.Get, "/v1/packages/lookup?ids=" + string.Join(",", chunk), null, null,
                HubHttp.ListCap, HubContext.Browse, ct).ConfigureAwait(false);
            foreach (var item in answer.Items ?? new List<HubLookupItem>())
            {
                if (item == null || !HubText.IsPackageId(item.Id) || !sorted.Contains(item.Id)) continue;
                item.Title = HubText.CleanLine(item.Title, HubText.Title) ?? "";
                item.Sha256 = item.Sha256?.ToLowerInvariant();
                if (item.Sha256 != null && !HubText.IsHex64(item.Sha256)) item.Sha256 = null;
                item.Reason = HubText.CleanLine(item.Reason, 32);
                found.Add(item);
            }
        }
        return found;
    }

    internal async Task<HubDetail> DetailAsync(string id, CancellationToken ct)
    {
        var detail = await Http.CallAsync<HubDetail>(HttpMethod.Get, "/v1/packages/" + Id(id), null, null, HubHttp.DetailCap, HubContext.Detail, ct).ConfigureAwait(false);
        if (!detail.Clean() || detail.Id != id) throw HubErrors.BadAnswer();
        return detail;
    }

    /// <summary>A package's file address, built by the mod from the id and version (never one the hub hands out).</summary>
    internal Uri FileUrl(string id, int version)
    {
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
        return Http.Url($"/v1/files/{Id(id)}/{version}/package");
    }

    /// <summary>Counts a verified install (sent once after it).</summary>
    internal Task InstalledAsync(string id, int version, CancellationToken ct) =>
        Http.CallAsync(HttpMethod.Post, $"/v1/packages/{Id(id)}/installed", new { version }, null, HubContext.General, ct);

    /// <summary><see cref="InstalledAsync"/> with nothing waiting for it: a failure only means one download isn't counted.</summary>
    internal void CountInstall(string id, int version) =>
        InstalledAsync(id, version, CancellationToken.None).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

    /// <summary>A report ("received", or "already" when this key reported the entry before).</summary>
    internal async Task<string> ReportAsync(string id, string reason, string? note, string key, CancellationToken ct)
    {
        if (!HubText.ReportReasons.Any(r => r.Code == reason)) throw new ArgumentException("not a report reason", nameof(reason));
        var answer = await Http.CallAsync<HubStatusAnswer>(HttpMethod.Post, $"/v1/packages/{Id(id)}/report",
            new { reason, note = HubText.CleanText(note, HubText.Note) ?? "" }, key, HubHttp.SmallCap, HubContext.Report, ct).ConfigureAwait(false);
        return answer.Status == "already" ? "already" : "received";
    }

    // ---- keyed routes ---------------------------------------------------------------------------

    /// <summary>Registers the key (at the first upload) or renames it.</summary>
    internal async Task<HubRegistered> RegisterAsync(string name, string key, CancellationToken ct)
    {
        string clean = HubText.CleanName(name) ?? throw new HubException("bad_name", HubErrorsWords.BadName);
        var answer = await Http.CallAsync<HubRegistered>(HttpMethod.Put, "/v1/me", new { name = clean }, key, HubHttp.SmallCap, HubContext.Key, ct).ConfigureAwait(false);
        answer.Uploader = (answer.Uploader ?? new HubUploader()).Clean();
        return answer;
    }

    internal async Task<HubMe> MeAsync(string key, CancellationToken ct)
    {
        var me = await Http.CallAsync<HubMe>(HttpMethod.Get, "/v1/me", null, key, HubHttp.SmallCap, HubContext.Key, ct).ConfigureAwait(false);
        me.Uploader = (me.Uploader ?? new HubUploader()).Clean();
        me.Limits ??= new HubLimits();
        return me;
    }

    internal Task RotateAsync(string newKeyHash, string key, CancellationToken ct) =>
        Http.CallAsync(HttpMethod.Post, "/v1/me/rotate", new { newKeyHash }, key, HubContext.Key, ct);

    internal async Task<List<HubMyCard>> MyPackagesAsync(string key, CancellationToken ct)
    {
        var mine = await Http.CallAsync<HubMyPackages>(HttpMethod.Get, "/v1/me/packages", null, key, HubHttp.ListCap, HubContext.Mine, ct).ConfigureAwait(false);
        return (mine.Items ?? new List<HubMyCard>()).Where(c => c != null && c.Clean()).ToList();
    }

    /// <summary>Removes one of the player's own entries from the hub for everyone.</summary>
    internal Task DeletePackageAsync(string id, string key, CancellationToken ct) =>
        Http.CallAsync(HttpMethod.Delete, "/v1/packages/" + Id(id), null, key, HubContext.Mine, ct);

    // ---- uploading ------------------------------------------------------------------------------

    internal async Task<HubUploadStarted> StartUploadAsync(HubUploadStart body, string key, CancellationToken ct)
    {
        var started = await Http.CallAsync<HubUploadStarted>(HttpMethod.Post, "/v1/uploads", body, key, HubHttp.SmallCap, HubContext.Upload, ct).ConfigureAwait(false);
        if (!HubText.IsUploadId(started.UploadId) || !HubText.IsPackageId(started.PackageId) || started.Parts < 1 || started.Parts > 10000
            || started.PartSize < 1 || started.Version < 1) throw HubErrors.BadAnswer();
        return started;
    }

    /// <summary>
    /// Sends part <paramref name="n"/> (bytes <paramref name="offset"/> to offset + length of the
    /// package file) with its exact Content-Length, counting the bytes in <paramref name="transfer"/>.
    /// A part that doesn't move for <see cref="HubHttp.UploadStall"/> is stopped.
    /// </summary>
    internal async Task<HubPartAnswer> PutPartAsync(string uploadId, int n, string file, long offset, long length, HubTransfer transfer,
        string key, CancellationToken ct)
    {
        if (!HubText.IsUploadId(uploadId)) throw new ArgumentException("not an upload id", nameof(uploadId));
        using var stall = new HubStallWatch(HubHttp.UploadStall, ct);
        using var request = new HttpRequestMessage(HttpMethod.Put, Http.Url($"/v1/uploads/{uploadId}/parts/{n}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new HubPartContent(file, offset, length, transfer, stall);
        try
        {
            using var response = await Http.SendRawAsync(request, stall.Token).ConfigureAwait(false);
            stall.Kick();
            bool ok = response.IsSuccessStatusCode;
            var bytes = await HubHttp.ReadCapped(response.Content, HubHttp.SmallCap, stall.Token, cut: !ok).ConfigureAwait(false);
            if (!ok) throw HubErrors.From(response, bytes, HubContext.Upload);
            return HubHttp.Parse<HubPartAnswer>(bytes, HubContext.Upload);
        }
        catch (OperationCanceledException) when (stall.Stalled) { throw HubErrors.Stalled(upload: true); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { throw HubErrors.Network(ex, HubContext.Upload); }
    }

    internal async Task<HubCompleted> CompleteAsync(string uploadId, string key, CancellationToken ct)
    {
        if (!HubText.IsUploadId(uploadId)) throw new ArgumentException("not an upload id", nameof(uploadId));
        var done = await Http.CallAsync<HubCompleted>(HttpMethod.Post, $"/v1/uploads/{uploadId}/complete", null, key, HubHttp.SmallCap, HubContext.Upload, ct).ConfigureAwait(false);
        if (!HubText.IsPackageId(done.PackageId) || done.Version < 1) throw HubErrors.BadAnswer();
        return done;
    }

    internal Task AbortUploadAsync(string uploadId, string key, CancellationToken ct) =>
        HubText.IsUploadId(uploadId) ? Http.CallAsync(HttpMethod.Delete, $"/v1/uploads/{uploadId}", null, key, HubContext.Upload, ct) : Task.CompletedTask;
}

/// <summary>Words used by more than one file.</summary>
internal static class HubErrorsWords
{
    internal const string BadName = "Pick a name of 1 to 32 characters with a letter or digit in it (not admin, owner, moderator or hub).";
}

/// <summary>
/// An upload part's body: a slice of the package file, sent as it's read with its exact length,
/// counting the bytes for the progress bar and the stall watchdog.
/// </summary>
internal sealed class HubPartContent : HttpContent
{
    private readonly string file;
    private readonly long offset, length;
    private readonly HubTransfer transfer;
    private readonly HubStallWatch stall;

    internal HubPartContent(string file, long offset, long length, HubTransfer transfer, HubStallWatch stall)
    {
        this.file = file;
        this.offset = offset;
        this.length = length;
        this.transfer = transfer;
        this.stall = stall;
        Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Send(stream, stall.Token);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) => Send(stream, ct);

    private async Task Send(Stream stream, CancellationToken ct)
    {
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
        input.Position = offset;
        var buffer = new byte[64 * 1024];
        long left = length;
        while (left > 0)
        {
            int n = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("the package file got shorter while it was sent");
            await stream.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            left -= n;
            transfer.Add(n);
            stall.Kick();
        }
    }

    protected override bool TryComputeLength(out long size)
    {
        size = length;
        return true;
    }
}
