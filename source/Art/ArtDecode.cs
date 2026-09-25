namespace NocturneFlatScroll;

/// <summary>A decoded still picture: RGBA, top row first.</summary>
internal sealed class Picture
{
    internal byte[] Rgba = Array.Empty<byte>();
    internal int Width, Height;
}

/// <summary>An animation's frames in the file's pixels, handed out one at a time (RGBA, top row first).</summary>
internal abstract class FrameSource
{
    /// <summary>One frame's size, and how many frames the file has for this animation.</summary>
    internal int Width, Height, Count;
    /// <summary>A GIF's own delays in ms.</summary>
    internal int[]? OwnMs;
    /// <summary>
    /// The top-left pixel of the whole picture, for a "corner" see-through colour (null when that
    /// pixel is see-through already). GIFs use each frame's own corner.
    /// </summary>
    internal int? Corner;

    /// <summary>
    /// Calls onFrame(index, frame) for each frame that <paramref name="want"/> picks, in order. The
    /// buffer is reused and may be changed by the callback. Returning false stops.
    /// </summary>
    internal abstract void Read(Func<int, bool> want, Func<int, byte[], bool> onFrame);
}

/// <summary>A still picture, or a sprite sheet's cells read left to right, then down (the game's order).</summary>
internal sealed class PictureFrames : FrameSource
{
    private readonly Picture picture;
    private readonly int columns, first;

    internal PictureFrames(Picture picture, ArtAnimationSpec a)
    {
        this.picture = picture;
        bool sheet = a.Kind == ArtKind.Sheet;
        columns = sheet ? a.Columns : 1;
        Width = sheet ? picture.Width / a.Columns : picture.Width;
        Height = sheet ? picture.Height / a.Rows : picture.Height;
        first = sheet ? Math.Min(a.First, a.Cells - 1) : 0;
        Count = sheet ? Math.Min(a.Frames ?? a.Cells - first, a.Cells - first) : 1;
        var p = picture.Rgba;
        Corner = p.Length >= 4 && p[3] >= FrameAtlas.VisibleAlpha ? p[0] << 16 | p[1] << 8 | p[2] : null;
    }

    internal override void Read(Func<int, bool> want, Func<int, byte[], bool> onFrame)
    {
        var frame = new byte[Width * Height * 4];
        for (int i = 0; i < Count; i++)
        {
            if (!want(i)) continue;
            int cell = first + i;
            FrameAtlas.CopyRect(picture.Rgba, picture.Width, cell % columns * Width, cell / columns * Height, Width, Height, frame);
            if (!onFrame(i, frame)) return;
        }
    }

    /// <summary>For a sheet without "frames": leaves out the empty cells at its end (after the see-through colour).</summary>
    internal void TrimEmptyTail(ArtAnimationSpec a)
    {
        var frame = new byte[Width * Height * 4];
        while (Count > 1)
        {
            int cell = first + Count - 1;
            FrameAtlas.CopyRect(picture.Rgba, picture.Width, cell % columns * Width, cell / columns * Height, Width, Height, frame);
            ArtDecode.ApplyKey(a, this, frame);
            if (FrameAtlas.Visible(frame, Width, Height, out _, out _, out _, out _)) break;
            Count--;
        }
    }
}

/// <summary>A GIF's frames, composited as browsers show them.</summary>
internal sealed class GifFrames : FrameSource
{
    private readonly byte[] file;
    private readonly GifDecoder.Limits limits;
    internal readonly GifDecoder.Info Info;

    internal GifFrames(byte[] file, GifDecoder.Limits limits)
    {
        this.file = file;
        this.limits = limits;
        Info = GifDecoder.Scan(file, limits);
        Width = Info.Width;
        Height = Info.Height;
        Count = Info.Frames;
        OwnMs = Info.DelaysMs;
    }

    internal override void Read(Func<int, bool> want, Func<int, byte[], bool> onFrame) =>
        GifDecoder.Decode(file, limits, (i, canvas) => !want(i) || onFrame(i, canvas));
}

/// <summary>One animation after its first frame was looked at: where it stands and how big it looks.</summary>
internal sealed class MeasuredAnimation
{
    internal ArtAnimationSpec Spec = null!;
    internal FrameSource? Source;          // null for a video
    internal VideoFacts? Video;
    /// <summary>A path the video player can open.</summary>
    internal string? VideoPath;
    internal ArtTimeline Timeline = new();
    /// <summary>One frame's size in the file's pixels.</summary>
    internal int FrameW, FrameH;
    /// <summary>Where it stands, in the frame's pixels from its top-left (y is the row under the feet).</summary>
    internal double FeetX, FeetY;
    /// <summary>The first frame's visible pixels (the whole frame when it has no see-through pixels).</summary>
    internal int VisibleLeft, VisibleTop, VisibleW, VisibleH;
    internal bool FeetFound;
}

/// <summary>An animation's frames packed into one atlas, ready for Unity.</summary>
internal sealed class PackedAnimation
{
    internal MeasuredAnimation From = null!;
    internal byte[] Atlas = Array.Empty<byte>();
    internal int AtlasW, AtlasH;
    internal (int X, int Y, int W, int H)[] Rects = Array.Empty<(int, int, int, int)>();
    /// <summary>Source pixels per stored pixel (a whole-number shrink).</summary>
    internal int Shrink = 1;
    /// <summary>Stored pixels per game pixel, for Sprite.Create.</summary>
    internal float Ppu;
    /// <summary>The sprite's pivot as a share of the stored frame, from its bottom-left.</summary>
    internal float PivotX, PivotY;
    /// <summary>Drawn at a whole number of game pixels per stored pixel.</summary>
    internal bool WholePixels;
    /// <summary>The atlas's size in memory (kept after the CPU copy is let go).</summary>
    internal long Bytes;
}

/// <summary>How a video animation is placed; its player is made on the Unity side.</summary>
internal sealed class VideoPlan
{
    internal MeasuredAnimation From = null!;
    /// <summary>The render texture's size: at most 720 tall and 8 of its pixels per game pixel.</summary>
    internal int TextureW, TextureH;
    internal float Ppu, PivotX, PivotY;
}

/// <summary>
/// The CPU half of loading custom enemy art: frames are cut from a sheet or decoded from a GIF,
/// thinned, keyed, measured and packed into one atlas per animation. No Unity calls, so it runs
/// on a worker thread; see ArtLoader for the order. This file has no Unity or game dependencies.
/// </summary>
internal static class ArtDecode
{
    internal const int MaxAtlasSide = 4096;
    internal const long MaxAtlasBytes = 48L * 1024 * 1024;
    /// <summary>Nothing is kept above this many source pixels per game pixel (8 screen pixels per game pixel is 4K).</summary>
    internal const double MaxDensity = 8;
    internal const int MaxVideoTexture = 720;
    /// <summary>Stock enemies look 26 to 87 game pixels tall; a default size keeps custom art near them.</summary>
    internal const double PixelArtMin = 24, PixelArtMax = 96, DefaultLooks = 64, SmallLooks = 32;

    /// <summary>
    /// The see-through colour, if the animation has one. "corner" takes the picture's top-left
    /// pixel (each frame's, for a GIF); when that pixel is already see-through there's nothing to do.
    /// </summary>
    internal static void ApplyKey(ArtAnimationSpec a, FrameSource source, byte[] frame)
    {
        if (!a.HasKey) return;
        int rgb;
        if (a.KeyRgb is int fixedColour) rgb = fixedColour;
        else if (source is PictureFrames)
        {
            if (source.Corner is not int corner) return;
            rgb = corner;
        }
        else
        {
            if (frame.Length < 4 || frame[3] < FrameAtlas.VisibleAlpha) return;
            rgb = frame[0] << 16 | frame[1] << 8 | frame[2];
        }
        FrameAtlas.Key(frame, source.Width * source.Height, rgb, a.KeyRange);
    }

    /// <summary>Looks at the first frame: its visible pixels and where the character stands.</summary>
    internal static MeasuredAnimation Measure(ArtAnimationSpec a, FrameSource source, ArtTimeline timeline)
    {
        var m = new MeasuredAnimation { Spec = a, Source = source, Timeline = timeline, FrameW = source.Width, FrameH = source.Height };
        int first = timeline.Keep.Length > 0 ? timeline.Keep[0] : 0;
        source.Read(i => i == first, (i, frame) =>
        {
            ApplyKey(a, source, frame);
            bool opaque = FrameAtlas.Opaque(frame, source.Width * source.Height);
            if (!opaque && FrameAtlas.Visible(frame, source.Width, source.Height, out int left, out int top, out int right, out int bottom))
            {
                m.VisibleLeft = left;
                m.VisibleTop = top;
                m.VisibleW = right - left;
                m.VisibleH = bottom - top;
                m.FeetX = (left + right) / 2.0;
                m.FeetY = bottom;
                m.FeetFound = true;
            }
            return false;
        });
        if (!m.FeetFound) WholeFrame(m);
        if (a.Feet is (double x, double y)) (m.FeetX, m.FeetY) = (x, y);
        return m;
    }

    /// <summary>A video, or a frame with no see-through pixels: the whole frame counts, standing on its bottom edge.</summary>
    internal static void WholeFrame(MeasuredAnimation m)
    {
        m.VisibleLeft = 0;
        m.VisibleTop = 0;
        m.VisibleW = m.FrameW;
        m.VisibleH = m.FrameH;
        m.FeetX = m.FrameW / 2.0;
        m.FeetY = m.FrameH;
    }

    /// <param name="useFeet">The animation's "feet" fits the video (checked once its size is known).</param>
    internal static MeasuredAnimation MeasureVideo(ArtAnimationSpec a, VideoFacts facts, string path, ArtTimeline timeline, bool useFeet)
    {
        var m = new MeasuredAnimation { Spec = a, Video = facts, VideoPath = path, Timeline = timeline, FrameW = facts.Width, FrameH = facts.Height };
        WholeFrame(m);
        if (useFeet && a.Feet is (double x, double y)) (m.FeetX, m.FeetY) = (x, y);
        return m;
    }

    /// <summary>
    /// The idle frame's height in game pixels when the art doesn't say: pixel art whose visible
    /// part is 24 to 96 pixels tall shows 1:1 (crisp, like the game's own art), smaller art is
    /// scaled up by a whole number, and bigger art shows 64 game pixels tall.
    /// </summary>
    internal static double DefaultSize(MeasuredAnimation idle)
    {
        double v = Math.Max(1, idle.VisibleH);
        double k = v < PixelArtMin ? Math.Min(8, Math.Ceiling(SmallLooks / v)) : v <= PixelArtMax ? 1 : DefaultLooks / v;
        return Math.Clamp(k * idle.FrameH, EnemyArtReader.MinSize, EnemyArtReader.MaxSize);
    }

    /// <summary>
    /// The atlas layout for an animation drawn at <paramref name="k"/> game pixels per source
    /// pixel: shrunk by a whole number when it's denser than MaxDensity or too big for one atlas.
    /// Null when even 1/16 doesn't fit.
    /// </summary>
    internal static FrameAtlas.Layout? Layout(MeasuredAnimation m, double k)
    {
        double density = 1 / k;
        int minShrink = density > MaxDensity ? (int)Math.Ceiling(density / MaxDensity - 1e-9) : 1;
        return FrameAtlas.Plan(m.FrameW, m.FrameH, Math.Max(1, m.Timeline.Keep.Length), MaxAtlasSide, MaxAtlasBytes, minShrink, 16);
    }

    /// <summary>Whether frames stored at this layout show at a whole number of game pixels per pixel.</summary>
    internal static bool WholePixels(FrameAtlas.Layout layout, double k)
    {
        double g = k * layout.Scale;
        return g >= 0.999 && Math.Abs(g - Math.Round(g)) < 0.001;
    }

    /// <summary>Cuts, keys and packs the kept frames into one atlas.</summary>
    internal static PackedAnimation Pack(MeasuredAnimation m, FrameAtlas.Layout layout, double k, double offsetX, double offsetY, CancellationToken cancel)
    {
        var source = m.Source ?? throw new InvalidOperationException("a video has no frames to pack");
        var keep = m.Timeline.Keep;
        var slots = new Dictionary<int, int>(keep.Length);
        for (int j = 0; j < keep.Length; j++) slots[keep[j]] = j;
        var atlas = new byte[layout.Bytes];
        int packed = 0;
        source.Read(i => slots.ContainsKey(i), (i, frame) =>
        {
            cancel.ThrowIfCancellationRequested();
            ApplyKey(m.Spec, source, frame);
            FrameAtlas.Put(atlas, layout, slots[i], frame);
            return ++packed < keep.Length;
        });
        if (packed == 0) throw new InvalidDataException($"{m.Spec.File} has no frames left to show");
        var rects = new (int, int, int, int)[keep.Length];
        for (int j = 0; j < keep.Length; j++) rects[j] = FrameAtlas.Cell(layout, Math.Min(j, packed - 1));
        // The stored frame covers FrameW x FrameH stored pixels of Shrink source pixels each.
        var (px, py) = Pivot(m, layout.FrameW * layout.Scale, layout.FrameH * layout.Scale, k, offsetX, offsetY);
        return new PackedAnimation
        {
            From = m,
            Atlas = atlas,
            AtlasW = layout.AtlasW,
            AtlasH = layout.AtlasH,
            Rects = rects,
            Shrink = layout.Scale,
            Ppu = (float)(1 / (k * layout.Scale)),
            PivotX = px,
            PivotY = py,
            WholePixels = WholePixels(layout, k),
            Bytes = atlas.LongLength,
        };
    }

    /// <summary>The render texture and sprite placement for a video drawn at <paramref name="k"/> game pixels per source pixel.</summary>
    internal static VideoPlan PlanVideo(MeasuredAnimation m, double k, double offsetX, double offsetY)
    {
        double shown = m.FrameH * k;                     // game pixels tall
        int h = (int)Math.Max(1, Math.Min(m.FrameH, Math.Min(MaxVideoTexture, Math.Ceiling(MaxDensity * shown))));
        int w = (int)Math.Max(1, Math.Round(m.FrameW * (double)h / m.FrameH));
        var (px, py) = Pivot(m, m.FrameW, m.FrameH, k, offsetX, offsetY);
        return new VideoPlan { From = m, TextureW = w, TextureH = h, Ppu = (float)(h / shown), PivotX = px, PivotY = py };
    }

    /// <summary>
    /// The feet as a share of the stored frame from its bottom-left, moved by the animation's own
    /// offset (game pixels, + is right and up): moving the art right moves the pivot left. The
    /// stored frame covers coverW x coverH source pixels from the frame's top-left (a shrunk
    /// frame's last row and column stand for what's left of the source).
    /// </summary>
    private static (float X, float Y) Pivot(MeasuredAnimation m, double coverW, double coverH, double k, double offsetX, double offsetY)
    {
        double x = (m.FeetX - offsetX / k) / coverW;
        double y = (coverH - m.FeetY - offsetY / k) / coverH;
        return ((float)x, (float)y);
    }
}
