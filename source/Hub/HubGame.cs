using System.Diagnostics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>
/// The hub's steps that touch the game, all on the main thread (DESIGN-HUB 3.2): where the mod's
/// folders are and whether the hub is on, the game's chart reader for a downloaded battle, the
/// custom-chart check and reload for a pack, "Use it now", the difficulties the player can put in
/// a pack, and the upload thumbnail made with the game's own JPEG encoder. Everything else in the
/// hub runs on workers and has no Unity dependencies.
/// </summary>
internal sealed class HubGame : IHubGame
{
    internal static readonly HubGame Instance = new();

    private static bool reportedThumbFormat;

    private HubGame() { }

    /// <summary>The hub's address for this build (a QA build may use NFS_QA_HUB_URL), or null with why.</summary>
    internal static Uri? Address(out string? problem) => HubConfig.Address(ModInfo.HubUrl, QaBuild.Env(HubConfig.QaUrlVariable), out problem);

    /// <summary>Whether the hub is on: the Options switch, and an address in this build. Off means no request is ever made.</summary>
    internal static bool Enabled => HubConfig.Enabled(SettingsState.OnlineHub, Address(out _));

    /// <summary>The hub's folders (captured here, on the main thread, before any worker starts).</summary>
    internal static HubPaths Paths() =>
        new(Path.GetDirectoryName(CustomBattles.Folder.TrimEnd('\\', '/'))!, QaBuild.Env(HubConfig.QaIdentityVariable));

    /// <summary>The Recycle Bin, owned by the game window.</summary>
    internal static IHubRecycler Recycler() => new HubRecycleBin(Process.GetCurrentProcess().MainWindowHandle);

    // ---- IHubGame -----------------------------------------------------------------------------------

    public string? CheckBattle(BattlePackage package)
    {
        try
        {
            CustomBattles.CheckChart(package);
            return null;
        }
        catch (Exception ex)
        {
            return ex is InvalidDataException ? ex.Message : "the game's chart reader failed on it (" + ex.GetType().Name + ")";
        }
    }

    public string? CheckPack(string path)
    {
        try
        {
            CustomCharts.Probe(path);
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or ArgumentException)
        {
            return ex.Message;
        }
    }

    public void ChartsChanged()
    {
        CustomCharts.Reload();
        OptionsMenuIntegration.RefreshAll();
    }

    // ---- Use it now (DESIGN-HUB 1.4) ----------------------------------------------------------------

    /// <summary>A song an installed pack covers, and what "Use it now" did or offers for it.</summary>
    internal sealed class UseRow
    {
        internal string Song = "";
        internal List<CustomCharts.CustomChart> Charts = new();
        /// <summary>The pick was made just now (the song had none).</summary>
        internal bool Picked;
        /// <summary>The song already had a pick, which stays ("Enter uses this one instead").</summary>
        internal CustomCharts.CustomChart? Kept;
    }

    /// <summary>
    /// After a pack is installed: its songs, and for each song without a custom pick yet, the pack's
    /// first difficulty for it picked at once. A song that has a pick keeps it.
    /// </summary>
    internal static List<UseRow> UseItNow(string packPath)
    {
        CustomCharts.Reload();
        string full = Path.GetFullPath(packPath);
        var rows = new List<UseRow>();
        foreach (var group in CustomCharts.All.Where(c => Path.GetFullPath(c.SourceFile).Equals(full, StringComparison.OrdinalIgnoreCase)).GroupBy(c => c.Song))
        {
            var row = new UseRow { Song = group.Key, Charts = group.ToList() };
            var current = CustomCharts.Selected(group.Key);
            if (current == null)
            {
                CustomCharts.Select(group.Key, row.Charts[0]);
                row.Picked = true;
            }
            else row.Kept = current;
            rows.Add(row);
        }
        OptionsMenuIntegration.RefreshAll();
        return rows;
    }

    // ---- the difficulties a pack can hold (DESIGN-HUB 1.6) --------------------------------------------

    /// <summary>
    /// The player's custom difficulties that can go in a pack: every one outside Downloaded\ (and
    /// outside the game's own charts), each marked when it plays a song file of its own.
    /// </summary>
    internal static List<HubPackChoice> PackChoices(HubPaths paths)
    {
        var list = new List<HubPackChoice>();
        foreach (var chart in CustomCharts.All)
        {
            if (BattleFiles.IsInside(chart.SourceFile, paths.ChartsDownloaded) || BattleFiles.IsInside(chart.SourceFile, CustomCharts.GameChartsFolder)) continue;
            list.Add(new HubPackChoice
            {
                Song = chart.Song, Melody = chart.Melody, KeepSongEvents = chart.KeepSongEvents, Chart = chart.Chart, BlockIndex = chart.BlockIndex,
                Lanes = chart.Lanes, SourceFile = chart.SourceFile, PlaysOwnSong = CustomMusic.SourceFor(chart) != null, Author = chart.Author,
            });
        }
        return list;
    }

    // ---- the upload thumbnail (DESIGN-HUB 3.6 step 5) -------------------------------------------------

    /// <summary>
    /// The listing thumbnail of a built battle: its card as the arcade shows it, at most 128 px a
    /// side, encoded with the game's own JPEG encoder (75, then 60, then 45 until it fits 16 KB of
    /// base64), and only when it comes out as a baseline JPEG the hub takes. Null otherwise (the
    /// listing then shows a title tile, as it does for packs and battles without a card).
    /// </summary>
    internal static string? Thumbnail(HubBuild build)
    {
        if (build.CardBytes == null) return null;
        var card = CustomBattles.CardImages.Make(build.CardBytes, "hub thumbnail", build.CardLook, keepPart: true, out _);
        if (card == null) return null;
        RenderTexture? previous = null, target = null;
        Texture2D? readable = null;
        try
        {
            var texture = card.Texture;
            var part = card.Sprite.rect;
            float factor = 128f / Math.Max(part.width, part.height);
            int width = Math.Clamp((int)Math.Round(part.width * factor), 1, 128), height = Math.Clamp((int)Math.Round(part.height * factor), 1, 128);
            previous = RenderTexture.active;
            target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var scale = new Vector2(part.width / texture.width, part.height / texture.height);
            var offset = new Vector2(part.x / texture.width, part.y / texture.height);
            Graphics.Blit(texture, target, scale, offset);
            RenderTexture.active = target;
            readable = new Texture2D(width, height, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
            readable.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
            readable.Apply(false, false);
            foreach (int quality in new[] { 75, 60, 45 })
            {
                var encoded = ImageConversion.EncodeToJPG(readable, quality);
                if (encoded == null) return null;
                var bytes = new byte[encoded.Length];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = encoded[i];
                string b64 = Convert.ToBase64String(bytes);
                if (b64.Length > HubZipCheck.MaxThumbB64) continue;
                string? why = HubZipCheck.ThumbProblem(b64, out _);
                if (why == null) return b64;
                if (!reportedThumbFormat)
                {
                    reportedThumbFormat = true;
                    ModLog.Error("Hub: the game's JPEG encoder made a thumbnail the hub won't take (" + why + "), so uploads go without one.");
                }
                return null;
            }
            return null;
        }
        catch (Exception ex)
        {
            ModLog.Error("Hub: making the thumbnail failed: " + ex.Message);
            return null;
        }
        finally
        {
            try
            {
                RenderTexture.active = previous;
                if (target != null) RenderTexture.ReleaseTemporary(target);
            }
            catch { }
            if (readable != null && readable) Object.Destroy(readable);
            if (card.Sprite) Object.Destroy(card.Sprite);
            if (card.Texture) Object.Destroy(card.Texture);
        }
    }
}

/// <summary>
/// The hub's multi-step jobs as the page runs them (DESIGN-HUB 3.2): each worker step through the
/// page kit's job runner (<see cref="EditorPageKit.Run"/>), and the game's own steps on the main
/// thread in between, where the runner hands the result back. The page's Update calls the kit's
/// FinishPending every frame, as the battle creator does. <paramref name="ct"/> is the page's
/// token: closing the page stops the worker, whose own cleanup removes its temporary files.
/// </summary>
internal static class HubFlow
{
    /// <summary>
    /// Download, check, the game's check, install (DESIGN-HUB 3.4). <paramref name="done"/> runs on
    /// the main thread with the installed item; a pack's custom charts are read again first.
    /// </summary>
    internal static void Install(EditorPageKit kit, HubApi api, HubStore store, HubDetail detail, HubZipCheck.Limits limits, HubTransfer transfer,
        IHubRecycler recycler, CancellationToken ct, Action<HubInstalledItem> done)
    {
        kit.Run(Task.Run(() => HubInstall.DownloadAsync(api, store, detail, limits, transfer, ct), ct), $"Downloading {detail.Title}...", verified =>
        {
            string? why = HubInstall.GameCheck(verified, HubGame.Instance);
            if (why != null)
            {
                HubInstall.Discard(verified);
                throw new HubException("damaged", "That download doesn't play in the game: " + why) { Problems = new[] { why } };
            }
            kit.Run(Task.Run(() => HubInstall.Place(verified, store, recycler), ct), $"Installing {detail.Title}...", item =>
            {
                if (item.Kind == "charts") HubGame.Instance.ChartsChanged();
                api.CountInstall(item.Package, item.Version);
                done(item);
            });
        });
    }

    /// <summary>
    /// Build, thumbnail (main thread, the game's JPEG encoder), send (DESIGN-HUB 3.6). The page
    /// shows the build's summary and the rules between <paramref name="built"/> and
    /// <see cref="Send"/>; the thumbnail is made here, when the build comes back.
    /// </summary>
    internal static void Build(EditorPageKit kit, Func<HubBuild> build, string what, Action<HubBuild, string?> built) =>
        kit.Run(Task.Run(build), what, result => built(result, result.Ok ? HubGame.Thumbnail(result) : null));

    internal static void Send(EditorPageKit kit, HubApi api, HubStore store, HubIdentity identity, HubBuild build, HubUploadDetails details, HubInfo info,
        HubTransfer transfer, CancellationToken ct, Action<HubUploadResult> done) =>
        kit.Run(Task.Run(() => HubUploadSend.SendAsync(api, store, identity, build, details, info, transfer, ct), ct), $"Uploading {build.Title}...", done);
}
