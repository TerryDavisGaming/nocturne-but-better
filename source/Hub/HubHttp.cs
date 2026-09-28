using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NocturnePlus;

/// <summary>What a hub call was for, so its errors are worded for it.</summary>
internal enum HubContext { General, Info, Browse, Detail, Download, Upload, Report, Key, Mine }

/// <summary>
/// A hub call that didn't work. <see cref="Exception.Message"/> is what the player sees, in plain
/// words; <see cref="Code"/> is the API's error code, or one of the mod's own: "network" (the hub
/// can't be reached), "timeout", "stalled" (a transfer stopped moving), "cloudflare" (one of
/// Cloudflare's own error pages), "bad_answer" and "too_big_answer" (an answer the mod won't read),
/// and "damaged" (a download that doesn't match its listing).
/// </summary>
internal sealed class HubException : Exception
{
    internal HubException(string code, string message, int status = 0, Exception? inner = null) : base(message, inner)
    {
        Code = code;
        Status = status;
    }

    internal string Code { get; }
    /// <summary>The HTTP status, or 0 when there was no answer.</summary>
    internal int Status { get; }
    internal TimeSpan? RetryAfter { get; init; }
    internal IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    internal string? PackageId { get; init; }
    internal IReadOnlyList<int> Missing { get; init; } = Array.Empty<int>();
    /// <summary>A removal's reason (410 removed).</summary>
    internal string? Reason { get; init; }

    /// <summary>No answer came (worth trying again).</summary>
    internal bool Network => Code is "network" or "timeout" or "stalled";

    /// <summary>The entry isn't on the hub (any more).</summary>
    internal bool Gone => Code is "removed" or "deleted" or "unavailable" or "not_found" or "no_package";

    /// <summary>The whole hub is out of reach for now (Browse says so with Retry; Installed and Delete still work).</summary>
    internal bool HubDown => Network || Code is "cloudflare" or "busy_today" or "not_set_up" or "server_error" or "db_unavailable" or "bad_answer";
}

/// <summary>
/// The hub's HTTP clients (DESIGN-HUB 3.2): one for the API (JSON, gzip allowed, capped reads) and
/// one for files (no decompression, so the bytes counted are the bytes listed). No redirects are
/// followed and no cookies kept; every request says who it is and carries X-NBB-Client, and the
/// hub key goes only on the request that needs it. Every call has its own time limit, and
/// transfers a watchdog that stops them when no byte moves for a while. Runs on worker threads.
/// This file has no Unity or game dependencies.
/// </summary>
internal sealed class HubHttp : IDisposable
{
    /// <summary>A small call's limit, a download's and an upload part's stall limits (the checks shorten them).</summary>
    internal static TimeSpan CallTimeout = TimeSpan.FromSeconds(20);
    internal static TimeSpan DownloadStall = TimeSpan.FromSeconds(30);
    internal static TimeSpan UploadStall = TimeSpan.FromSeconds(60);

    /// <summary>How much of an answer is read: lists, details, everything else.</summary>
    internal const int ListCap = 2 * 1024 * 1024, DetailCap = 256 * 1024, SmallCap = 64 * 1024;

    private readonly HttpClient api;
    private readonly HttpClient files;

    internal Uri Base { get; }
    internal string UserAgent { get; }

    internal HubHttp(Uri baseUri, string userAgent)
    {
        Base = baseUri;
        UserAgent = userAgent;
        api = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false,
            UseCookies = false,
        })
        { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = ListCap };
        files = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            UseCookies = false,
        })
        { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var client in new[] { api, files })
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-NBB-Client", "1");
        }
        files.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");
    }

    public void Dispose()
    {
        api.Dispose();
        files.Dispose();
    }

    /// <summary>The address of a path on the hub ("/v1/..."), always built by the mod.</summary>
    internal Uri Url(string path) => new(Base, path);

    /// <summary>
    /// One JSON call. A GET that gets no answer is tried once more; nothing else is repeated here.
    /// <paramref name="body"/> goes as application/json; null sends none (or "{}" for a POST).
    /// </summary>
    internal async Task<T> CallAsync<T>(HttpMethod method, string path, object? body, string? key, int cap, HubContext context, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var bytes = await SendAsync(method, path, body, key, cap, context, ct).ConfigureAwait(false);
                return Parse<T>(bytes, context);
            }
            catch (HubException ex) when (ex.Network && method == HttpMethod.Get && attempt == 0 && !ct.IsCancellationRequested) { }
        }
    }

    /// <summary>A call whose answer has no body the mod reads (204).</summary>
    internal async Task CallAsync(HttpMethod method, string path, object? body, string? key, HubContext context, CancellationToken ct) =>
        await SendAsync(method, path, body, key, SmallCap, context, ct).ConfigureAwait(false);

    internal static T Parse<T>(byte[] bytes, HubContext context)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, HubJson.Options) ?? throw new JsonException("empty");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            throw HubErrors.BadAnswer(ex);
        }
    }

    private async Task<byte[]> SendAsync(HttpMethod method, string path, object? body, string? key, int cap, HubContext context, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(CallTimeout);
        using var request = new HttpRequestMessage(method, Url(path));
        if (key != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body != null || method == HttpMethod.Post) request.Content = JsonContent(body);
        try
        {
            using var response = await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            bool ok = response.IsSuccessStatusCode;
            // An error's body is only looked at, so a long one (a Cloudflare page) is cut rather than refused.
            var bytes = await ReadCapped(response.Content, ok ? cap : SmallCap, limit.Token, cut: !ok).ConfigureAwait(false);
            if (!ok) throw HubErrors.From(response, bytes, context);
            return bytes;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw HubErrors.Timeout(context); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { throw HubErrors.Network(ex, context); }
    }

    internal static ByteArrayContent JsonContent(object? body)
    {
        var bytes = body == null ? Encoding.UTF8.GetBytes("{}") : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), HubJson.Options);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>
    /// Reads an answer's body, refusing it past <paramref name="cap"/> bytes while reading (after
    /// decompression), so a small gzip body that unpacks to far more is stopped early.
    /// </summary>
    internal static async Task<byte[]> ReadCapped(HttpContent content, int cap, CancellationToken ct, bool cut = false)
    {
        if (!cut && content.Headers.ContentLength is long declared && declared > cap) throw HubErrors.TooBigAnswer();
        using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var copy = new MemoryStream();
        var buffer = new byte[16384];
        int n;
        while ((n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            if (copy.Length + n > cap)
            {
                if (!cut) throw HubErrors.TooBigAnswer();
                copy.Write(buffer, 0, (int)(cap - copy.Length));
                break;
            }
            copy.Write(buffer, 0, n);
        }
        return copy.ToArray();
    }

    /// <summary>A GET on the files client, headers only (the caller streams the body).</summary>
    internal async Task<HttpResponseMessage> GetFileAsync(Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        try
        {
            return await files.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { throw HubErrors.Network(ex, HubContext.Download); }
    }

    /// <summary>A request on the API client, headers only, for upload parts (the caller reads the answer).</summary>
    internal async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { throw HubErrors.Network(ex, HubContext.Upload); }
    }
}

/// <summary>
/// Stops a transfer that doesn't move: its token cancels when <see cref="Kick"/> hasn't been
/// called for the stall time. <see cref="Stalled"/> tells a stall from the caller's own cancel.
/// </summary>
internal sealed class HubStallWatch : IDisposable
{
    private readonly CancellationTokenSource source;
    private readonly CancellationToken outer;
    private readonly TimeSpan stall;

    internal HubStallWatch(TimeSpan stall, CancellationToken outer)
    {
        this.stall = stall;
        this.outer = outer;
        source = CancellationTokenSource.CreateLinkedTokenSource(outer);
        source.CancelAfter(stall);
    }

    internal CancellationToken Token => source.Token;

    internal void Kick()
    {
        try { source.CancelAfter(stall); }
        catch (ObjectDisposedException) { }
    }

    internal bool Stalled => source.IsCancellationRequested && !outer.IsCancellationRequested;

    public void Dispose() => source.Dispose();
}

/// <summary>A transfer's progress, updated by the worker and read by the page each frame.</summary>
internal sealed class HubTransfer
{
    private long done, total;
    private volatile string stage = "";

    internal long Done => Interlocked.Read(ref done);
    internal long Total => Interlocked.Read(ref total);
    internal string Stage { get => stage; set => stage = value; }

    internal void Start(long bytes, string what)
    {
        Interlocked.Exchange(ref done, 0);
        Interlocked.Exchange(ref total, bytes);
        stage = what;
    }

    internal void Add(long bytes) => Interlocked.Add(ref done, bytes);

    internal void Set(long bytes) => Interlocked.Exchange(ref done, bytes);

    /// <summary>0 to 1; 0 while the size isn't known.</summary>
    internal double Fraction
    {
        get
        {
            long t = Total;
            return t <= 0 ? 0 : Math.Clamp(Done / (double)t, 0, 1);
        }
    }
}

/// <summary>
/// The hub's error codes (server/API.md), Cloudflare's own error pages and connection trouble, in
/// the words the player sees. The server's own message shows only for codes the mod doesn't know.
/// </summary>
internal static class HubErrors
{
    internal const string CantReach = "Can't reach the hub. Check your internet connection.";
    internal const string NotRunning = "The hub isn't running right now.";

    internal static HubException Network(Exception ex, HubContext context) => new("network", CantReach, 0, ex);

    /// <summary>
    /// For the log: the reason under a failed connection ("AuthenticationException: The remote
    /// certificate is invalid ...", "SocketException: No such host is known. (host:443)"), so a
    /// certificate, a proxy, a name that doesn't resolve and a refused connection can be told apart.
    /// Those words hold the host, the port and the reason, never a header, a body or the key. Null
    /// for anything else.
    /// </summary>
    internal static string? Cause(HubException ex)
    {
        if (!ex.Network || ex.InnerException == null) return null;
        var cause = ex.InnerException;
        while (cause.InnerException != null) cause = cause.InnerException;
        return HubText.CleanLine($"{cause.GetType().Name}: {cause.Message}", 300);
    }

    internal static HubException Timeout(HubContext context) =>
        new("timeout", "The hub didn't answer in time. Check your internet connection and try again.");

    internal static HubException BadAnswer(Exception? ex = null) =>
        new("bad_answer", "The hub sent an answer the mod can't read. Try again in a while.", 0, ex);

    internal static HubException TooBigAnswer() =>
        new("too_big_answer", "The hub sent a bigger answer than the mod reads. Try again in a while.");

    internal static HubException Stalled(bool upload) =>
        new("stalled", upload
            ? $"The upload stopped moving (nothing was sent for {Span(HubHttp.UploadStall)}). Try again."
            : $"The download stopped moving (nothing arrived for {Span(HubHttp.DownloadStall)}). Try again.");

    private static string Span(TimeSpan span) => span.TotalSeconds >= 60 && span.TotalSeconds % 60 == 0
        ? (span.TotalSeconds == 60 ? "a minute" : $"{(int)span.TotalMinutes} minutes")
        : $"{(int)Math.Ceiling(span.TotalSeconds)} seconds";

    /// <summary>"in 5 h" or "in 40 min" until 00:00 UTC, when the free plan's day starts again.</summary>
    internal static string UntilMidnightUtc(DateTime nowUtc)
    {
        var left = nowUtc.Date.AddDays(1) - nowUtc;
        if (left.TotalMinutes < 60) return $"in {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min";
        return $"in {(int)Math.Ceiling(left.TotalHours)} h";
    }

    internal static string TooBusyToday(DateTime nowUtc) =>
        $"The hub is too busy today. It resets at 00:00 UTC ({UntilMidnightUtc(nowUtc)}).";

    internal static string Seconds(TimeSpan? wait) =>
        wait is TimeSpan w ? Math.Max(1, (int)Math.Ceiling(w.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " s" : "a moment";

    /// <summary>The error for a non-2xx answer, from its status, headers and (capped) body.</summary>
    internal static HubException From(HttpResponseMessage response, byte[] body, HubContext context)
    {
        TimeSpan? retry = null;
        var header = response.Headers.RetryAfter;
        if (header?.Delta is TimeSpan delta) retry = delta;
        else if (header?.Date is DateTimeOffset date) retry = date - DateTimeOffset.UtcNow;
        return From((int)response.StatusCode, body, retry, context, DateTime.UtcNow);
    }

    internal static HubException From(int status, byte[] body, TimeSpan? retryHeader, HubContext context, DateTime nowUtc)
    {
        HubErrorBody? error = null;
        try
        {
            if (body.Length > 0 && (body[0] == (byte)'{' || (body.Length > 3 && body[0] == 0xEF && body[3] == (byte)'{')))
                error = JsonSerializer.Deserialize<HubErrorBody>(body, HubJson.Options);
        }
        catch (JsonException) { }
        if (error?.Error == null)
        {
            // One of Cloudflare's own pages: only 1027 means the day's allowance is spent.
            string text = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 65536));
            if (text.Contains("1027")) return new HubException("busy_today", TooBusyToday(nowUtc), status);
            if (context == HubContext.Info && status == 404) return new HubException("not_set_up", NotRunning, status);
            return new HubException("cloudflare", $"The hub had a problem (error {status}). Try again in a while.", status);
        }
        string code = error.Error;
        TimeSpan? retry = retryHeader;
        if (retry == null && error.RetryAfter is double seconds && double.IsFinite(seconds)) retry = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (retry is TimeSpan r && (r < TimeSpan.Zero || r > TimeSpan.FromDays(2))) retry = null;
        var problems = (error.Problems ?? new List<string>())
            .Select(p => HubText.CleanLine(p, 300) ?? "").Where(p => p.Length > 0).Take(20).ToList();
        string? packageId = HubText.IsPackageId(error.PackageId) ? error.PackageId : null;
        string message = Words(code, status, context, retry, problems, error, nowUtc);
        return new HubException(code, message, status)
        {
            RetryAfter = retry,
            Problems = problems,
            PackageId = packageId,
            Missing = (error.Missing ?? new List<int>()).Take(100).ToList(),
            Reason = HubText.CleanLine(error.Reason, 32),
        };
    }

    private static string Words(string code, int status, HubContext context, TimeSpan? retry, List<string> problems, HubErrorBody error, DateTime nowUtc)
    {
        string first = problems.Count > 0 ? problems[0] : "";
        switch (code)
        {
            case "not_set_up": return NotRunning;
            case "busy_today": return TooBusyToday(nowUtc);
            case "server_error":
            case "db_unavailable":
                return $"The hub had a problem (error {status}). Try again in a while.";
            case "slow_down": return $"Slow down a little. Try again in {Seconds(retry)}.";
            case "daily_limit":
                return context switch
                {
                    HubContext.Upload => "You've uploaded as much as the hub allows today (new keys: 2 a day for the first 2 days). Try again tomorrow.",
                    HubContext.Report => "You've sent as many reports as the hub takes in a day. Try again tomorrow.",
                    HubContext.Key => "The hub isn't taking more new uploaders from this network today. Try again tomorrow.",
                    _ => "The hub's limit for today is used up. Try again tomorrow.",
                };
            case "not_found":
                return context switch
                {
                    HubContext.Info => NotRunning,
                    HubContext.Detail or HubContext.Download or HubContext.Report => "This entry isn't on the hub any more.",
                    HubContext.Upload => "That upload isn't on the hub any more. Start it again.",
                    _ => "The hub doesn't have that.",
                };
            case "removed":
                return "The hub's owner removed this entry" + (error.Reason != null ? " (" + HubText.ReasonWords(HubText.CleanLine(error.Reason, 32)) + ")" : "") + ".";
            case "deleted": return "Its uploader deleted this entry from the hub.";
            case "unavailable": return "This entry is under review by the hub's owner.";
            case "no_package": return "That entry isn't on the hub any more.";
            case "bad_client":
            case "method_not_allowed":
                return "The hub refused the mod's request. Update the mod.";
            case "bad_query": return "The hub didn't take that list or search. Try another search.";
            case "bad_request":
                return first.Length > 0 ? "The hub refused it: " + first : "The hub refused the mod's request. Update the mod.";
            case "no_key":
            case "bad_key":
                return "The mod's hub key isn't usable. Use a saved key or make a new one in My uploads.";
            case "unknown_key":
                return context == HubContext.Key
                    ? "The hub doesn't know this key. Only keys that have uploaded something are known."
                    : "This hub key isn't registered yet. It's registered at your first upload.";
            case "banned": return "This hub key can't upload any more (the hub's owner banned it, or it had 3 uploads removed for copyright).";
            case "revoked": return "The hub's owner turned this hub key off.";
            case "not_yours": return "That entry was uploaded with another hub key.";
            case "rename_limit": return "You can change your name on the hub once a day.";
            case "bad_name": return HubErrorsWords.BadName;
            case "bad_reason": return "Pick a reason for the report.";
            case "too_big":
                return error.MaxPackageBytes is long max && max > 0
                    ? $"The package is too big for the hub (it takes up to {max / (1024 * 1024)} MB)."
                    : "The package is too big for the hub.";
            case "length_required":
            case "bad_part":
                return "A part of the upload didn't arrive whole. Try the upload again.";
            case "upload_in_progress": return "Another upload with this hub key is still running. Wait a few minutes, then try again.";
            case "upload_closed": return "That upload was stopped, or waited too long. Start it again.";
            case "busy": return "The hub is still finishing that upload. Try again in a minute.";
            case "missing_parts": return "Some parts of the upload didn't arrive. Try the upload again.";
            case "package_refused": return first.Length > 0 ? "The hub can't take this package: " + first : "The hub can't take this package.";
            case "battle_taken":
                return "Someone else's entry on the hub already has this battle. If it's yours, report that entry with \"This is mine\" as the note. " +
                    "If you made your own version, use \"Make it a separate battle\" in the battle creator first.";
            case "battle_yours": return "One of your own entries already has this battle. Upload it as a new version of that entry instead (My uploads).";
            case "battle_changed":
                return "A new version must be the same battle (the same battle id), and this one isn't. Upload it as a new entry instead.";
            case "removed_before": return "The hub's owner removed this battle for copyright, so it can't be uploaded again with this key.";
            case "duplicate": return "The same files are already on the hub.";
            case "not_live": return "Only an entry that's on the hub can get a new version.";
            case "too_many_live": return "A hub key can have 50 entries on the hub. Delete one from the hub first (My uploads).";
            case "key_too_new": return "Uploads from new hub keys are paused right now. Try again in a few days.";
            case "key_in_use": return "That key is already in use. Try making a new key again.";
            case "client_too_old": return "This version of the mod is too old for the hub. Update the mod.";
            case "uploads_closed": return "Uploads are closed right now. Downloads still work.";
            case "closed": return "The hub isn't taking new uploaders right now. Downloads still work.";
            case "read_only": return "The hub is read-only right now. Downloads still work.";
            case "storage_full": return "Uploads are closed right now: the hub is full. Downloads still work.";
            default:
                // A code this version doesn't know: the hub's own words (cleaned), or the status.
                string? own = HubText.CleanLine(error.Message, 300);
                return string.IsNullOrEmpty(own) ? $"The hub said no (error {status})." : own!;
        }
    }
}
