using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>Turns SkinArt masks into Unity sprites once and keeps them for the whole session.</summary>
internal static class SkinSprites
{
    private enum Part { NoteBody, NoteGlyph, NoteAccent, ReceptorFace, ReceptorBorder, ReceptorGlyph, ReceptorGlow }

    // Indexed by part and shape, so the per-frame lookups build no strings or delegates.
    private static readonly Sprite?[,] ShapeSprites = new Sprite?[7, 3];
    private static readonly Dictionary<string, Sprite> Cache = new();

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
    public static Sprite Tick => Get("bar.tick", () => SkinArt.RoundedBar(16, 64));
    public static Sprite Marker => Get("bar.marker", SkinArt.Marker);
    public static Sprite Rabbit => Get("bar.rabbit", SkinArt.Rabbit);
    public static Sprite Turtle => Get("bar.turtle", SkinArt.Turtle);

    /// <summary>A pill for sliced drawing: its round ends stay half as wide as it is tall.</summary>
    public static Sprite Pill(float height) =>
        Get("bar.pill." + height.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            () => SkinArt.RoundedBar(64, 32), 32f / height, new Vector4(16f, 0f, 16f, 0f));

    private static Sprite ForShape(Part part, SkinArt.Shape shape)
    {
        var sprite = ShapeSprites[(int)part, (int)shape];
        if (sprite) return sprite!;
        var mask = part switch
        {
            Part.NoteBody => SkinArt.NoteBody(shape),
            Part.NoteGlyph => SkinArt.NoteGlyph(shape),
            Part.NoteAccent => SkinArt.NoteAccent(shape),
            Part.ReceptorFace => SkinArt.ReceptorFace(shape),
            Part.ReceptorBorder => SkinArt.ReceptorBorder(shape),
            Part.ReceptorGlyph => SkinArt.ReceptorGlyph(shape),
            _ => SkinArt.ReceptorGlow(shape)
        };
        sprite = Create(shape + "." + part, mask, 0f, Vector4.zero);
        ShapeSprites[(int)part, (int)shape] = sprite;
        return sprite;
    }

    private static Sprite Get(string key, Func<SkinArt.Mask> draw) => Get(key, draw, 0f, Vector4.zero);

    private static Sprite Get(string key, Func<SkinArt.Mask> draw, float pixelsPerUnit, Vector4 border)
    {
        if (Cache.TryGetValue(key, out var cached) && cached) return cached;
        var sprite = Create(key, draw(), pixelsPerUnit, border);
        Cache[key] = sprite;
        return sprite;
    }

    private static Sprite Create(string key, SkinArt.Mask mask, float pixelsPerUnit, Vector4 border)
    {
        var texture = new Texture2D(mask.Width, mask.Height, TextureFormat.RGBA32, true)
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
        texture.SetPixels32(mask.ToColor32());
        texture.Apply(true, true);
        // By default one sprite unit spans the texture: renderers scale it to world size.
        if (pixelsPerUnit <= 0f) pixelsPerUnit = Math.Max(mask.Width, mask.Height);
        var sprite = Sprite.Create(texture, new Rect(0f, 0f, mask.Width, mask.Height),
            new Vector2(0.5f, 0.5f), pixelsPerUnit, 0u, SpriteMeshType.FullRect, border);
        sprite.name = texture.name;
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }
}
