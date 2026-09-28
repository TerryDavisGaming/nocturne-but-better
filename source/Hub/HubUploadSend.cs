namespace NocturnePlus;

/// <summary>What the player chose for an upload, besides the package.</summary>
internal sealed class HubUploadDetails
{
    internal string Description { get; set; } = "";
    /// <summary>A new version of this entry (one of the player's own), or null for a new entry.</summary>
    internal string? PackageId { get; set; }
    /// <summary>The listing thumbnail (a baseline JPEG in base64, made on the main thread), or null for none.</summary>
    internal string? Thumb { get; set; }
    /// <summary>The name to register the hub key with at its first upload.</summary>
    internal string DisplayName { get; set; } = "";
    /// <summary>The player confirmed the rules (DESIGN-HUB 5.1). Nothing is sent without it.</summary>
    internal bool RightsConfirmed { get; set; }
}

internal sealed class HubUploadResult
{
    internal HubCompleted Completed = new();
    /// <summary>This was the key's first upload (the page then says to back the key up).</summary>
    internal bool FirstUpload;
}

/// <summary>
/// Sends a built package (DESIGN-HUB 2.6, 3.2, 3.6 steps 6 to 8): registers the hub key at its first
/// upload, starts the upload (safe to repeat), sends the parts one after another with their exact
/// lengths (each tried up to 3 times: after 1 s, then 5 s), completes it (a repeat returns the
/// same answer; a busy hub is asked again), and records it in uploads.json. An upload that stops
/// part way, or that the player stops, is stopped on the hub too. Runs on a worker thread.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class HubUploadSend
{
    /// <summary>How long a failed part waits before it's sent again (the checks shorten them).</summary>
    internal static TimeSpan[] PartRetryWaits = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5) };
    /// <summary>The longest wait for a hub that says it's busy or asks to slow down, before giving up.</summary>
    internal static TimeSpan MaxWait = TimeSpan.FromSeconds(90);

    internal static async Task<HubUploadResult> SendAsync(HubApi api, HubStore store, HubIdentity identity, HubBuild build, HubUploadDetails details,
        HubInfo info, HubTransfer transfer, CancellationToken ct)
    {
        if (!build.Ok) throw new InvalidOperationException("the package isn't ready: " + string.Join("; ", build.Problems));
        if (!details.RightsConfirmed) throw new InvalidOperationException("the rules weren't confirmed");
        if (details.PackageId != null && !HubText.IsPackageId(details.PackageId)) throw new ArgumentException("not a package id");
        if (!info.UploadsOpen) throw new HubException("uploads_closed", "Uploads are closed right now. Downloads still work.");
        if (info.TooOld(api.ModVersion)) throw new HubException("client_too_old", "This version of the mod is too old for the hub. Update the mod.");
        if (build.Size > info.MaxPackageBytes)
            throw new HubException("too_big", $"The package is too big for the hub (it takes up to {info.MaxPackageBytes / (1024 * 1024)} MB).");
        string? thumb = details.Thumb != null && HubZipCheck.ThumbProblem(details.Thumb, out _) == null ? details.Thumb : null;
        string key = identity.Key;

        // The key is registered at its first upload (a restored key may be registered already).
        bool first = false;
        if (identity.UploaderId == null)
        {
            HubUploader uploader;
            try { uploader = (await api.MeAsync(key, ct).ConfigureAwait(false)).Uploader; }
            catch (HubException ex) when (ex.Code == "unknown_key")
            {
                uploader = (await api.RegisterAsync(details.DisplayName, key, ct).ConfigureAwait(false)).Uploader;
                first = true;
            }
            identity.UploaderId = uploader.Id.Length > 0 ? uploader.Id : null;
            identity.Name = uploader.Name;
            identity.Save(store.Paths.Identity);
        }

        var start = new HubUploadStart
        {
            ClientUploadId = Guid.NewGuid().ToString("N"),
            Kind = build.Kind,
            PackageId = details.PackageId,
            File = new HubUploadFile { Size = build.Size, Sha256 = build.Sha256, EntriesSha256 = build.Fingerprint },
            Meta = HubUploadBuild.Meta(build, details.Description),
            Thumb = thumb,
            RightsConfirmed = true,
            Client = api.ModVersion,
        };
        transfer.Start(build.Size, "Starting the upload");
        HubUploadStarted started;
        try { started = await api.StartUploadAsync(start, key, ct).ConfigureAwait(false); }
        catch (HubException ex) when (ex.Network && !ct.IsCancellationRequested)
        {
            // The same clientUploadId again gives the same upload back.
            started = await api.StartUploadAsync(start, key, ct).ConfigureAwait(false);
        }
        long partSize = started.PartSize;
        if (started.Parts != (int)((build.Size + partSize - 1) / partSize)) throw HubErrors.BadAnswer();

        HubCompleted completed;
        try
        {
            transfer.Start(build.Size, "Uploading");
            for (int n = 1; n <= started.Parts; n++) await SendPart(api, started, n, build, transfer, key, ct).ConfigureAwait(false);
            transfer.Stage = "Finishing";
            completed = await Complete(api, started, build, transfer, key, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Stopped part way (or refused): the hub's side is stopped too, so the key can upload again at once.
            if (!(ex is HubException refused && refused.Code == "package_refused"))
            {
                try
                {
                    using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await api.AbortUploadAsync(started.UploadId, key, quick.Token).ConfigureAwait(false);
                }
                catch (Exception abort) when (abort is HubException or OperationCanceledException) { }
            }
            throw;
        }

        store.RecordUpload(new HubUploadRecord
        {
            Package = completed.PackageId, Kind = build.Kind, BattleId = build.BattleId, Title = build.Title, Source = build.Kind == "battle" ? build.Source : "",
            SourceFiles = build.SourceFiles, SourceFingerprint = build.SourceFingerprint, LastVersion = completed.Version,
            UploadedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        HubUploadBuild.Discard(build, store.Paths);
        return new HubUploadResult { Completed = completed, FirstUpload = first };
    }

    private static async Task SendPart(HubApi api, HubUploadStarted started, int n, HubBuild build, HubTransfer transfer, string key, CancellationToken ct)
    {
        long offset = (n - 1) * started.PartSize;
        long length = Math.Min(started.PartSize, build.Size - offset);
        for (int attempt = 0; ; attempt++)
        {
            transfer.Set(offset);
            try
            {
                await api.PutPartAsync(started.UploadId, n, build.PackagePath, offset, length, transfer, key, ct).ConfigureAwait(false);
                transfer.Set(offset + length);
                return;
            }
            catch (HubException ex) when (attempt < PartRetryWaits.Length && Retryable(ex) && !ct.IsCancellationRequested)
            {
                var wait = PartRetryWaits[attempt];
                if (ex.RetryAfter is TimeSpan asked && asked > wait) wait = asked < MaxWait ? asked : MaxWait;
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }
    }

    // A part is sent again after no answer, a stall, a server error or "slow down"; not after a refusal.
    private static bool Retryable(HubException ex) =>
        ex.Network || ex.Code is "slow_down" or "server_error" or "db_unavailable" or "cloudflare" || (ex.Status >= 500 && ex.Code != "storage_full" && ex.Code != "uploads_closed" && ex.Code != "read_only");

    private static async Task<HubCompleted> Complete(HubApi api, HubUploadStarted started, HubBuild build, HubTransfer transfer, string key, CancellationToken ct)
    {
        bool resent = false;
        var waited = TimeSpan.Zero;
        for (int attempt = 0; ; attempt++)
        {
            try { return await api.CompleteAsync(started.UploadId, key, ct).ConfigureAwait(false); }
            catch (HubException ex) when (ex.Code == "missing_parts" && !resent && ex.Missing.Count > 0)
            {
                // A part the hub didn't record: sent once more, then finished again.
                resent = true;
                foreach (int n in ex.Missing.Where(n => n >= 1 && n <= started.Parts)) await SendPart(api, started, n, build, transfer, key, ct).ConfigureAwait(false);
            }
            catch (HubException ex) when ((ex.Network || ex.Code is "busy" or "slow_down" or "server_error" or "db_unavailable" or "cloudflare") && attempt < 8 && waited < MaxWait)
            {
                // A repeated complete returns the stored answer, so asking again is safe.
                var wait = ex.RetryAfter ?? TimeSpan.FromSeconds(ex.Network ? 2 : 5);
                if (wait > MaxWait - waited) wait = MaxWait - waited;
                waited += wait;
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }
    }
}
