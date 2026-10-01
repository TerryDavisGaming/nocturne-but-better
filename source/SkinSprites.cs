using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace NocturnePlus;

/// <summary>Turns SkinArt masks into Unity sprites once and keeps them for the whole session.</summary>
internal static class SkinSprites
{
    internal enum Part { NoteBody, NoteGlyph, NoteAccent, ReceptorFace, ReceptorBorder, ReceptorGlyph, ReceptorGlow }

    // Indexed by part and shape, so the per-frame lookups build no strings or delegates.
    private static readonly Sprite?[,] ShapeSprites = new Sprite?[7, 3];
    private static readonly Dictionary<string, Sprite> Cache = new();

    // The timing bar's cache keys and masks, shared by its sprites below and by the first-battle
    // warm-up (FirstBattleWarmup), so the two can't drift apart.
    private const string TickKey = "bar.tick", MarkerKey = "bar.marker", RabbitKey = "bar.rabbit", TurtleKey = "bar.turtle";
    private static readonly Func<SkinArt.Mask> TickArt = () => SkinArt.RoundedBar(16, 64);
    private static readonly Func<SkinArt.Mask> PillArt = () => SkinArt.RoundedBar(64, 32);
    private static readonly Func<SkinArt.Mask> MarkerArt = SkinArt.Marker;
    private static readonly Func<SkinArt.Mask> RabbitArt = SkinArt.Rabbit;
    private static readonly Func<SkinArt.Mask> TurtleArt = SkinArt.Turtle;

    public static Sprite NoteBody(SkinArt.Shape shape) => ForShape(Part.NoteBody, shape);
    public static Sprite NoteGlyph(SkinArt.Shape shape) => ForShape(Part.NoteGlyph, shape);
    public static Sprite NoteAccent(SkinArt.Shape shape) => ForShape(Part.NoteAccent, shape);
    public static Sprite ReceptorFace(SkinArt.Shape shape) => ForShape(Part.ReceptorFace, shape);
    public static Sprite ReceptorBorder(SkinArt.Shape shape) => ForShape(Part.ReceptorBorder, shape);
    public static Sprite ReceptorGlyph(SkinArt.Shape shape) => ForShape(Part.ReceptorGlyph, shape);
    public static Sprite ReceptorGlow(SkinArt.Shape shape) => ForShape(Part.ReceptorGlow, shape);
    // The game's own bar note and mine, for the note color preview.
    public static Sprite BarBody => Get("note.bar.body", SkinArt.BarBody);
    public static Sprite BarGlyph => Get("note.bar.glyph", SkinArt.BarGlyph);
    public static Sprite BarAccent => Get("note.bar.accent", SkinArt.BarAccent);
    public static Sprite MineBody => Get("note.mine.body", SkinArt.MineBody);
    public static Sprite MineMarks => Get("note.mine.marks", SkinArt.MineMarks);
    public static Sprite MineLight => Get("note.mine.light", SkinArt.MineLight);
    public static Sprite Tick => Get(TickKey, TickArt);
    public static Sprite Marker => Get(MarkerKey, MarkerArt);
    public static Sprite Rabbit => Get(RabbitKey, RabbitArt);
    public static Sprite Turtle => Get(TurtleKey, TurtleArt);

    /// <summary>A pill for sliced drawing: its round ends stay half as wide as it is tall.</summary>
    public static Sprite Pill(float height) =>
        Get(PillKey(height), PillArt, 32f / height, new Vector4(16f, 0f, 16f, 0f));

    private static string PillKey(float height) =>
        "bar.pill." + height.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string ShapeKey(Part part, SkinArt.Shape shape) => shape + "." + part;

    private static SkinArt.Mask Draw(Part part, SkinArt.Shape shape) => part switch
    {
        Part.NoteBody => SkinArt.NoteBody(shape),
        Part.NoteGlyph => SkinArt.NoteGlyph(shape),
        Part.NoteAccent => SkinArt.NoteAccent(shape),
        Part.ReceptorFace => SkinArt.ReceptorFace(shape),
        Part.ReceptorBorder => SkinArt.ReceptorBorder(shape),
        Part.ReceptorGlyph => SkinArt.ReceptorGlyph(shape),
        _ => SkinArt.ReceptorGlow(shape)
    };

    // ---- For the first-battle warm-up (FirstBattleWarmup) ----

    /// <summary>
    /// A sprite the warm-up can draw ahead: the key it shares with the cache, its mask (plain math, safe
    /// on another thread), the public accessor that makes the sprite, and whether that is done already.
    /// </summary>
    internal readonly record struct Warmable(string Key, Func<SkinArt.Mask> Draw, Func<Sprite> Make, Func<bool> Made);

    internal static Warmable ForWarmup(Part part, SkinArt.Shape shape) =>
        new(ShapeKey(part, shape), () => Draw(part, shape), () => ForShape(part, shape),
            () => Alive(ShapeSprites[(int)part, (int)shape]));

    internal static Warmable PillForWarmup(float height)
    {
        string key = PillKey(height);
        return new(key, PillArt, () => Pill(height), () => IsCached(key));
    }

    internal static Warmable TickForWarmup => new(TickKey, TickArt, () => Tick, () => IsCached(TickKey));
    internal static Warmable MarkerForWarmup => new(MarkerKey, MarkerArt, () => Marker, () => IsCached(MarkerKey));
    internal static Warmable RabbitForWarmup => new(RabbitKey, RabbitArt, () => Rabbit, () => IsCached(RabbitKey));
    internal static Warmable TurtleForWarmup => new(TurtleKey, TurtleArt, () => Turtle, () => IsCached(TurtleKey));

    private static bool IsCached(string key) => Cache.TryGetValue(key, out var cached) && cached;

    private static bool Alive(Sprite? sprite) => sprite is not null && sprite;

    // ---- Making the sprites ----

    // Optimized and Potato copy the pixels in one go (CreateOptimized). QA can turn that off
    // (NFS_QA_PERF_SKIP=rgba) to measure it apart from the warm-up.
    private static bool QuickPixels => Performance.Optimizing && !Performance.Skip("rgba");

    private static Sprite ForShape(Part part, SkinArt.Shape shape)
    {
        var sprite = ShapeSprites[(int)part, (int)shape];
        if (sprite) return sprite!;
        string key = ShapeKey(part, shape);
        if (QuickPixels) sprite = CreateOptimized(part, shape, key);
        else
        {
            var mask = Draw(part, shape);
            sprite = Create(key, mask.Width, mask.Height, mask, null, 0f, Vector4.zero);
        }
        ShapeSprites[(int)part, (int)shape] = sprite;
        return sprite;
    }

    // Its own method: a lambda in ForShape would allocate on every lookup, found or not.
    private static Sprite CreateOptimized(Part part, SkinArt.Shape shape, string key) =>
        CreateOptimized(key, () => Draw(part, shape), 0f, Vector4.zero);

    private static Sprite Get(string key, Func<SkinArt.Mask> draw) => Get(key, draw, 0f, Vector4.zero);

    private static Sprite Get(string key, Func<SkinArt.Mask> draw, float pixelsPerUnit, Vector4 border)
    {
        if (Cache.TryGetValue(key, out var cached) && cached) return cached;
        Sprite sprite;
        if (QuickPixels) sprite = CreateOptimized(key, draw, pixelsPerUnit, border);
        else
        {
            var mask = draw();
            sprite = Create(key, mask.Width, mask.Height, mask, null, pixelsPerUnit, border);
        }
        Cache[key] = sprite;
        return sprite;
    }

    /// <summary>
    /// Optimized and Potato: the warm-up's ready image when it has one, or the mask drawn now. Either
    /// way the pixels are copied from the mask's bytes in one go; Normal's ToColor32 makes one engine
    /// call for each pixel. The bytes are the same.
    /// </summary>
    private static Sprite CreateOptimized(string key, Func<SkinArt.Mask> draw, float pixelsPerUnit, Vector4 border)
    {
        long start = FirstBattleWarmup.QaCheck ? Stopwatch.GetTimestamp() : 0L;
        bool warmed = FirstBattleWarmup.TryTake(key, out var ready);
        int width, height;
        byte[] rgba;
        if (warmed)
        {
            width = ready!.Width;
            height = ready.Height;
            rgba = ready.Rgba;
        }
        else
        {
            var mask = draw();
            if (FirstBattleWarmup.QaCheck) start = Stopwatch.GetTimestamp();
            width = mask.Width;
            height = mask.Height;
            rgba = mask.ToRgba32();
        }
        var pixels = FromRgba(rgba);
        if (FirstBattleWarmup.QaCheck) QaCompare(key, warmed, draw, pixels, FirstBattleWarmup.Ms(start));
        return Create(key, width, height, null, pixels, pixelsPerUnit, border);
    }

    /// <summary>
    /// A mask's RGBA bytes (SkinArt.Mask.ToRgba32) as Unity pixels. Color32 is four bytes, r, g, b
    /// and a, in both loaders' interop types, so this is one copy.
    /// </summary>
    private static Color32[] FromRgba(byte[] rgba) => MemoryMarshal.Cast<byte, Color32>(rgba).ToArray();

    // QA builds only (NFS_QA_WARMUP_CHECK=1): the same pixels made Normal's way, compared byte for
    // byte, with the time each way took (the quick way's includes ToRgba32 when drawn here).
    private static void QaCompare(string key, bool warmed, Func<SkinArt.Mask> draw, Color32[] pixels, double quickMs)
    {
        var mask = draw();
        long start = Stopwatch.GetTimestamp();
        var normal = mask.ToColor32();
        double normalMs = FirstBattleWarmup.Ms(start);
        var a = MemoryMarshal.AsBytes(pixels.AsSpan());
        var b = MemoryMarshal.AsBytes(normal.AsSpan());
        int same = 0, total = Math.Max(a.Length, b.Length);
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] == b[i]) same++;
        ModLog.Info($"SkinSprites (qa): {key} {mask.Width}x{mask.Height} ({(warmed ? "from the warm-up" : "drawn now")}): " +
            $"bytes identical {same}/{total}; ToColor32 {normalMs:0.00} ms, quick way {quickMs:0.00} ms.");
    }

    /// <summary>
    /// Makes the texture and sprite from <paramref name="pixels"/>, or, when there are none, from
    /// <paramref name="mask"/> as Normal always has.
    /// </summary>
    private static Sprite Create(string key, int width, int height, SkinArt.Mask? mask, Color32[]? pixels,
        float pixelsPerUnit, Vector4 border)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, true)
        {
            name = "NocturneFlatScroll." + key,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 2,
            // Scene changes must not unload artwork that pooled notes keep referencing.
            hideFlags = HideFlags.HideAndDontSave
        };
        // Upload the full-size image and let Unity build the smaller mip levels, which keep
        // shapes clean when they are drawn far smaller than the texture.
        texture.SetPixels32(pixels ?? mask!.ToColor32());
        texture.Apply(true, true);
        // By default one sprite unit spans the texture: renderers scale it to world size.
        if (pixelsPerUnit <= 0f) pixelsPerUnit = Math.Max(width, height);
        var sprite = Sprite.Create(texture, new Rect(0f, 0f, width, height),
            new Vector2(0.5f, 0.5f), pixelsPerUnit, 0u, SpriteMeshType.FullRect, border);
        sprite.name = texture.name;
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }
}
