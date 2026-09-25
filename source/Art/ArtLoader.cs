using System.Security.Cryptography;
using System.Text;

namespace NocturneFlatScroll;

/// <summary>What loading an enemy's art makes, for the Unity side (or the offline check).</summary>
internal interface IArtLoadHost
{
    /// <summary>Decodes a PNG or JPEG (its size already checked) into RGBA, top row first.</summary>
    Task<Picture> DecodePicture(ArtAnimationSpec a, byte[] file, CancellationToken cancel);
    /// <summary>Why a video of this container can't play on this PC, or null.</summary>
    string? VideoUnsupported(VideoFacts facts);
    /// <summary>An animation's frames are packed.</summary>
    Task Frames(PackedAnimation packed, bool smooth, CancellationToken cancel);
    /// <summary>A video animation is checked and placed.</summary>
    Task Video(VideoPlan plan, bool smooth, CancellationToken cancel);
}

/// <summary>How loading went, animation by animation.</summary>
internal sealed class ArtLoadResult
{
    internal readonly Dictionary<string, MeasuredAnimation> Measured = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, PackedAnimation> Packed = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, VideoPlan> Videos = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Animations that couldn't be loaded, and why (written to follow "can't be used (...)").</summary>
    internal readonly Dictionary<string, string> Failed = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Things the player should know (unusual files, cut attacks...).</summary>
    internal readonly List<string> Notes = new();
    /// <summary>Unexpected errors, in full, for the log.</summary>
    internal readonly List<string> Errors = new();
    internal double Size, K;
    internal bool Smooth;

    internal bool IdleUsable => Packed.ContainsKey("idle") || Videos.ContainsKey("idle");
    internal long TextureBytes => Packed.Values.Sum(p => p.Bytes) + Videos.Values.Sum(v => (long)v.TextureW * v.TextureH * 8);

    /// <summary>"idle GIF 8 frames 0.96 s; attack GIF 12 frames 1.04 s, hit 0.48 s, parry 0.23 s; textures 0.6 MB", for the log.</summary>
    internal string Describe()
    {
        var parts = new List<string>();
        foreach (var name in EnemyArtReader.Names)
        {
            if (!Measured.TryGetValue(name, out var m) || Failed.ContainsKey(name)) continue;
            var t = m.Timeline;
            string what = m.Video != null
                ? $"{name} {m.Spec.KindName} {m.FrameW} x {m.FrameH} {Sec(t.Length)} s"
                : $"{name} {m.Spec.KindName} {t.Keep.Length} frame{(t.Keep.Length == 1 ? "" : "s")} {Sec(t.Length)} s";
            if (t.Hit >= 0) what += $", hit {Sec(t.Hit)} s, parry {Sec(t.Parry)} s";
            parts.Add(what);
        }
        parts.Add($"textures {TextureBytes / (1024.0 * 1024.0):0.0} MB");
        return string.Join("; ", parts);
    }

    internal static string Sec(double seconds) => seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Loads a custom-art enemy's animations in order: every animation's file is read and its first
/// frame looked at (pictures shared between animations are decoded once); then the size, the
/// shared feet and the filtering are settled from the idle; then each animation is packed and
/// handed to the host. A failed animation is recorded and the others go on; a failed idle stops
/// the rest. Runs on worker threads: only the host's DecodePicture, Frames and Video touch Unity
/// (the host moves those to the main thread). This file has no Unity or game dependencies.
/// </summary>
internal sealed class ArtLoader
{
    private readonly EnemyArtSpec spec;
    private readonly PackageFiles files;
    private readonly IArtLoadHost host;
    private readonly string? cacheFolder;
    private readonly Dictionary<string, Task<Picture>> pictures = new(StringComparer.OrdinalIgnoreCase);
    // Two animations can use the same video file; its cache copy is made once.
    private static readonly object CacheLock = new();
    internal readonly ArtLoadResult Result = new();

    /// <param name="cacheFolder">Where videos that can't play in place are copied; null plays them in place.</param>
    internal ArtLoader(EnemyArtSpec spec, PackageFiles files, IArtLoadHost host, string? cacheFolder)
    {
        this.spec = spec;
        this.files = files;
        this.host = host;
        this.cacheFolder = cacheFolder;
    }

    internal async Task Run(CancellationToken cancel)
    {
        var names = EnemyArtReader.Names.Where(spec.Animations.ContainsKey).ToList();
        var measured = await Task.WhenAll(names.Select(n => Guard(n, () => Measure(spec.Animations[n], cancel)))).ConfigureAwait(false);
        for (int i = 0; i < names.Count; i++)
            if (measured[i] != null) Result.Measured[names[i]] = measured[i]!;
        lock (pictures) pictures.Clear();
        if (!Result.Measured.TryGetValue("idle", out var idle)) return;
        cancel.ThrowIfCancellationRequested();

        // One sheet has one pivot, as in the game: an animation from the same file and grid as an
        // earlier one stands where that one does, unless it says otherwise.
        foreach (var name in names)
        {
            if (!Result.Measured.TryGetValue(name, out var m) || m.Spec.Feet != null) continue;
            foreach (var earlier in names.TakeWhile(n => n != name))
                if (Result.Measured.TryGetValue(earlier, out var e) && m.Spec.SameSheet(e.Spec))
                {
                    (m.FeetX, m.FeetY) = (e.FeetX, e.FeetY);
                    break;
                }
        }

        Result.Size = spec.Size ?? ArtDecode.DefaultSize(idle);
        Result.K = Result.Size / idle.FrameH;
        // Crisp pixels unless the art says, or some frames don't land on whole game pixels.
        var layouts = new Dictionary<string, FrameAtlas.Layout?>(StringComparer.OrdinalIgnoreCase);
        bool whole = true;
        foreach (var (name, m) in Result.Measured)
        {
            if (m.Source == null) continue;
            var layout = ArtDecode.Layout(m, K(m));
            layouts[name] = layout;
            if (layout != null) whole &= ArtDecode.WholePixels(layout, K(m));
        }
        Result.Smooth = spec.Smooth ?? !whole;

        var packs = Result.Measured.Values.OrderBy(m => m.Spec.Name == "idle" ? 0 : 1).Select(m => Guard(m.Spec.Name, async () =>
        {
            if (m.Source == null)
            {
                var plan = ArtDecode.PlanVideo(m, K(m), m.Spec.OffsetX, m.Spec.OffsetY);
                await host.Video(plan, Result.Smooth, cancel).ConfigureAwait(false);
                lock (Result) Result.Videos[m.Spec.Name] = plan;
                return (object)plan;
            }
            var layout = layouts[m.Spec.Name] ?? throw new InvalidDataException(
                $"{m.Spec.File}'s {m.Timeline.Keep.Length} frames of {m.FrameW} x {m.FrameH} don't fit in one {ArtDecode.MaxAtlasSide} x {ArtDecode.MaxAtlasSide} texture even at 1/16 size");
            var packed = await Task.Run(() => ArtDecode.Pack(m, layout, K(m), m.Spec.OffsetX, m.Spec.OffsetY, cancel), cancel).ConfigureAwait(false);
            await host.Frames(packed, Result.Smooth, cancel).ConfigureAwait(false);
            lock (Result) Result.Packed[m.Spec.Name] = packed;
            // The CPU copy isn't needed once the host has it.
            packed.Atlas = Array.Empty<byte>();
            return (object)packed;
        })).ToList();
        await Task.WhenAll(packs).ConfigureAwait(false);
        foreach (var m in Result.Measured.Values) m.Source = null;
    }

    /// <summary>Game pixels per source pixel for an animation: the idle's, times its own scale.</summary>
    private double K(MeasuredAnimation m) => Result.K * (m.Spec.Name == "idle" ? 1 : m.Spec.Scale);

    // A failed animation is recorded, never thrown: the others still load.
    private async Task<T?> Guard<T>(string name, Func<Task<T>> work) where T : class
    {
        try { return await work().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            lock (Result)
            {
                if (ex is InvalidDataException or IOException or UnauthorizedAccessException) Result.Failed[name] = ex.Message;
                else
                {
                    Result.Failed[name] = $"{spec.Animations[name].File} couldn't be read ({ex.GetType().Name})";
                    Result.Errors.Add($"{name}: {ex}");
                }
            }
            return null;
        }
    }

    private async Task<MeasuredAnimation> Measure(ArtAnimationSpec a, CancellationToken cancel)
    {
        var notes = new List<string>();
        MeasuredAnimation m;
        switch (a.Kind)
        {
            case ArtKind.Video:
                m = await Task.Run(() => MeasureVideo(a, notes), cancel).ConfigureAwait(false);
                break;
            case ArtKind.Gif:
                m = await Task.Run(() =>
                {
                    var bytes = files.ReadAllBytes(a.File, EnemyArtReader.MaxPictureBytes);
                    GifFrames source;
                    try { source = new GifFrames(bytes, MediaSniff.GifLimits); }
                    catch (InvalidDataException ex) { throw new InvalidDataException($"{a.File} {ex.Message}"); }
                    string? note = MediaSniff.GifNote(source.Info);
                    if (note != null) notes.Add($"{a.File} {note}");
                    var timeline = ArtTimeline.Plan(a, source.Count, source.OwnMs, 0, notes);
                    return ArtDecode.Measure(a, source, timeline);
                }, cancel).ConfigureAwait(false);
                break;
            default:
                var picture = await PictureFor(a, cancel).ConfigureAwait(false);
                m = await Task.Run(() =>
                {
                    var source = new PictureFrames(picture, a);
                    if (a.Kind == ArtKind.Sheet && a.Frames == null) source.TrimEmptyTail(a);
                    var timeline = ArtTimeline.Plan(a, source.Count, null, 0, notes);
                    return ArtDecode.Measure(a, source, timeline);
                }, cancel).ConfigureAwait(false);
                break;
        }
        lock (Result) foreach (var note in notes) Result.Notes.Add($"the enemy art's {a.Name}: {note}");
        return m;
    }

    // Each picture file is decoded once, however many animations use it.
    private Task<Picture> PictureFor(ArtAnimationSpec a, CancellationToken cancel)
    {
        lock (pictures)
        {
            if (!pictures.TryGetValue(a.File, out var task))
            {
                task = DecodePicture(a, cancel);
                pictures[a.File] = task;
            }
            return task;
        }
    }

    private async Task<Picture> DecodePicture(ArtAnimationSpec a, CancellationToken cancel)
    {
        var bytes = await Task.Run(() => files.ReadAllBytes(a.File, EnemyArtReader.MaxPictureBytes), cancel).ConfigureAwait(false);
        // Checked again: the file may have changed since the battle was listed, and the decoder
        // would make whatever size the header claims.
        MediaInfo info;
        try { info = MediaSniff.Probe(bytes, true); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{a.File} {ex.Message}"); }
        if (info.Type is not (MediaType.Png or MediaType.Jpeg)) throw new InvalidDataException($"{a.File} isn't a PNG or JPEG any more");
        if (info.Width > EnemyArtReader.MaxPictureSide || info.Height > EnemyArtReader.MaxPictureSide)
            throw new InvalidDataException($"{a.File} says it is {info.Width} x {info.Height}; pictures can be at most {EnemyArtReader.MaxPictureSide} on a side");
        var picture = await host.DecodePicture(a, bytes, cancel).ConfigureAwait(false);
        if (picture.Width != info.Width || picture.Height != info.Height || picture.Rgba.Length != picture.Width * picture.Height * 4)
            throw new InvalidDataException($"{a.File} decoded as {picture.Width} x {picture.Height}, not the {info.Width} x {info.Height} it says");
        return picture;
    }

    private MeasuredAnimation MeasureVideo(ArtAnimationSpec a, List<string> notes)
    {
        string path = PlayablePath(files, a, cacheFolder, notes);
        VideoFacts facts;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            facts = VideoProbe.Read(stream);
        EnemyArtReader.CheckVideoFacts(a, facts);
        string? unsupported = host.VideoUnsupported(facts);
        if (unsupported != null) throw new InvalidDataException($"{a.File} {unsupported}");
        bool useFeet = true;
        if (a.Feet is (double x, double y) && (x < -facts.Width || x > 2 * facts.Width || y < -facts.Height || y > 2 * facts.Height))
        {
            notes.Add($"\"feet\" is far outside the {facts.Width} x {facts.Height} video, so it stands on the video's bottom edge");
            useFeet = false;
        }
        var timeline = ArtTimeline.Plan(a, 1, null, facts.Seconds, notes);
        return ArtDecode.MeasureVideo(a, facts, path, timeline, useFeet);
    }

    /// <summary>
    /// A path Unity's video player can open. It needs a real file whose extension matches what
    /// the file is (it picks its decoder by extension), and it trips over some characters, so a
    /// video inside a zip, or one with another extension or an awkward path, is copied once to
    /// the cache (also the battle creator's, when it turns a video into frames).
    /// </summary>
    internal static string PlayablePath(PackageFiles files, ArtAnimationSpec a, string? cacheFolder, List<string> notes)
    {
        string wanted = a.Media.Type == MediaType.WebM ? ".webm" : ".mp4";
        string? loose = files.LoosePathOf(a.File);
        if (loose != null && IsPlainPath(loose) && ExtensionFits(loose, a.Media.Type)) return loose;
        if (cacheFolder == null) return loose ?? throw new InvalidDataException($"{a.File} is inside a zip, which needs the video cache");
        string key;
        using (var sha = SHA1.Create())
            key = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes($"{files.Describe(a.File)}|{files.Stamp(a.File)}")))[..20].ToLowerInvariant();
        Directory.CreateDirectory(cacheFolder);
        string cached = Path.Combine(cacheFolder, key + wanted);
        var info = new FileInfo(cached);
        if (info.Exists && info.Length == a.Bytes)
        {
            File.SetLastWriteTimeUtc(cached, DateTime.UtcNow);
            return cached;
        }
        lock (CacheLock)
        {
            info.Refresh();
            if (info.Exists && info.Length == a.Bytes) return cached;
            string temp = cached + ".part";
            files.CopyTo(a.File, temp, EnemyArtReader.MaxVideoBytes);
            File.Move(temp, cached, true);
        }
        notes.Add($"{a.File} was copied to the video cache ({(files.IsZip ? "it's in a zip" : "its path or extension doesn't suit the video player")})");
        return cached;
    }

    private static bool IsPlainPath(string path)
    {
        foreach (char c in path)
            if (c > 126 || c < 32 || c == '#' || c == '%' || c == '?') return false;
        return true;
    }

    private static bool ExtensionFits(string path, MediaType type)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return type == MediaType.WebM ? ext == ".webm" : ext is ".mp4" or ".m4v" or ".mov";
    }

    /// <summary>Deletes the oldest cached videos until the cache is at most <paramref name="maxBytes"/>.</summary>
    internal static int TrimCache(string folder, long maxBytes)
    {
        if (!Directory.Exists(folder)) return 0;
        var cached = new DirectoryInfo(folder).GetFiles()
            .Where(f => f.Extension is ".mp4" or ".webm" or ".part")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        long total = 0;
        int removed = 0;
        foreach (var f in cached)
        {
            // A copy that was cut off long ago; a recent one may still be being written.
            if (f.Extension == ".part" && f.LastWriteTimeUtc > DateTime.UtcNow.AddHours(-1)) continue;
            total += f.Length;
            if (total <= maxBytes && f.Extension != ".part") continue;
            try { f.Delete(); removed++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }
}
