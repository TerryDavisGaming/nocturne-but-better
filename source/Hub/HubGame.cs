using System.Diagnostics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NocturnePlus;

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

    /// <summary>What "Use it now" found: the pack's songs, and its difficulties the player already has elsewhere.</summary>
    internal sealed class UseResult
    {
        internal List<UseRow> Rows = new();
        /// <summary>"Firefly - 1 Hard 9 is also in CustomCharts\my charts.nbbchart".</summary>
        internal List<string> Duplicates = new();
    }

    /// <summary>
    /// After a pack is installed: its songs, and for each song without a custom pick yet, the pack's
    /// first difficulty for it picked at once. A song that has a pick keeps it. <paramref name="pick"/>
    /// false only lists them (the Installed tab's "Use it now" on a pack installed before).
    /// </summary>
    internal static UseResult UseItNow(string packPath, bool pick = true)
    {
        CustomCharts.Reload();
        string full = Path.GetFullPath(packPath);
        var result = new UseResult();
        var mine = CustomCharts.All.Where(c => SameFile(c.SourceFile, full)).ToList();
        foreach (var group in mine.GroupBy(c => c.Song, StringComparer.OrdinalIgnoreCase))
        {
            var row = new UseRow { Song = group.Key, Charts = group.ToList() };
            var current = CustomCharts.Selected(group.Key);
            if (current == null && pick)
            {
                CustomCharts.Select(group.Key, row.Charts[0]);
                row.Picked = true;
            }
            else row.Kept = current;
            result.Rows.Add(row);
        }
        // The same difficulty elsewhere in CustomCharts (installed anyway; the note says where).
        string root = Path.GetDirectoryName(CustomCharts.Folder.TrimEnd('\\', '/'))!;
        var elsewhere = CustomCharts.All.Where(c => !SameFile(c.SourceFile, full)).GroupBy(CustomCharts.Fingerprint).ToDictionary(g => g.Key, g => g.First());
        foreach (var chart in mine)
            if (elsewhere.TryGetValue(CustomCharts.Fingerprint(chart), out var other))
                result.Duplicates.Add($"{chart.Song} {chart.Title} is also in {Path.GetRelativePath(root, other.SourceFile)}");
        if (pick) OptionsMenuIntegration.RefreshAll();
        return result;
    }

    /// <summary>Whether one of an installed pack's difficulties is the pick for its song (the IN USE tag).</summary>
    internal static bool InUse(string packPath)
    {
        string full = Path.GetFullPath(packPath);
        foreach (var song in CustomCharts.All.Where(c => SameFile(c.SourceFile, full)).Select(c => c.Song).Distinct(StringComparer.OrdinalIgnoreCase))
            if (CustomCharts.Selected(song) is { } picked && SameFile(picked.SourceFile, full)) return true;
        return false;
    }

    private static bool SameFile(string a, string full)
    {
        try { return Path.GetFullPath(a).Equals(full, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
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
