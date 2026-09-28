using System.Security.Cryptography;

namespace NocturnePlus;

/// <summary>
/// A package's download into the hub's work folder (DESIGN-HUB 3.4 steps 2 to 4): the file address
/// built by the mod, the bytes streamed straight to disk through SHA-256 and a byte counter, the
/// listed size never passed, and the size and SHA-256 compared with the listing at the end. The
/// files client asks for the bytes as they are, and a compressed answer is refused, so the bytes
/// counted are the bytes listed. A download that moves no byte for 30 s stops. Runs on a worker.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class HubDownload
{
    internal static HubException Damaged(string why) =>
        new("damaged", "That download was damaged or doesn't match its listing.") { Problems = new[] { why } };

    /// <summary>
    /// Downloads <paramref name="url"/> into <paramref name="path"/> (made new) and checks it is
    /// exactly <paramref name="size"/> bytes with SHA-256 <paramref name="sha256"/>. The file is
    /// deleted when anything fails, and when <paramref name="ct"/> stops it.
    /// </summary>
    internal static async Task FetchAsync(HubHttp http, Uri url, long size, string sha256, string path, HubTransfer transfer, CancellationToken ct)
    {
        transfer.Start(size, "Downloading");
        bool done = false;
        try
        {
            using var stall = new HubStallWatch(HubHttp.DownloadStall, ct);
            try
            {
                using var response = await http.GetFileAsync(url, stall.Token).ConfigureAwait(false);
                stall.Kick();
                if (!response.IsSuccessStatusCode)
                {
                    var body = await HubHttp.ReadCapped(response.Content, HubHttp.SmallCap, stall.Token, cut: true).ConfigureAwait(false);
                    throw HubErrors.From(response, body, HubContext.Download);
                }
                if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw Damaged("the hub sent it compressed, which the mod doesn't take");
                if (response.Content.Headers.ContentLength is long declared && declared != size)
                    throw Damaged("its size isn't the size in its listing");
                using var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long got = 0;
                while (true)
                {
                    int n;
                    try { n = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), stall.Token).ConfigureAwait(false); }
                    catch (IOException ex) { throw HubErrors.Network(ex, HubContext.Download); }
                    if (n <= 0) break;
                    got += n;
                    if (got > size) throw Damaged("it's bigger than its listing says");
                    hash.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), stall.Token).ConfigureAwait(false);
                    transfer.Set(got);
                    stall.Kick();
                }
                if (got != size) throw Damaged("it's smaller than its listing says (the download was cut off)");
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw Damaged("its SHA-256 isn't the one in its listing");
                done = true;
            }
            catch (OperationCanceledException) when (stall.Stalled) { throw HubErrors.Stalled(upload: false); }
            catch (HttpRequestException ex) { throw HubErrors.Network(ex, HubContext.Download); }
        }
        finally
        {
            if (!done) TryDelete(path);
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The free space on the drive a folder is on, or -1 when Windows won't say.</summary>
    internal static long FreeSpace(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return -1; }
    }
}
