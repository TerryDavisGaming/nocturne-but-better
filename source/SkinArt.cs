namespace NocturneFlatScroll;

/// <summary>
/// Draws the mod's artwork as anti-aliased signed-distance shapes, so it stays crisp at any
/// resolution and matches the game's clean vector look. Every layer is a white alpha mask
/// that a renderer tints, which lets one texture follow any note color style.
/// This file has no Unity dependencies; the same code renders previews outside the game.
/// </summary>
internal static class SkinArt
{
    internal sealed class Mask
    {
        public readonly int Width;
        public readonly int Height;
        public readonly float[] Alpha;

        public Mask(int width, int height)
        {
            Width = width;
            Height = height;
            Alpha = new float[width * height];
        }

        /// <summary>Adds a shape given by its signed distance in pixels (negative inside).</summary>
        public void Add(Func<float, float, float> distance)
        {
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                // Sample at the pixel center, with y growing upward like Unity textures.
                float coverage = Coverage(distance(x + 0.5f - Width * 0.5f, y + 0.5f - Height * 0.5f));
                int i = y * Width + x;
                Alpha[i] = Alpha[i] + coverage - Alpha[i] * coverage;
            }
        }

        /// <summary>Removes a shape, leaving transparent cut-outs.</summary>
        public void Cut(Func<float, float, float> distance)
        {
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                float coverage = Coverage(distance(x + 0.5f - Width * 0.5f, y + 0.5f - Height * 0.5f));
                Alpha[y * Width + x] *= 1f - coverage;
            }
        }

        public byte[] ToRgba32()
        {
            var bytes = new byte[Width * Height * 4];
            for (int i = 0; i < Alpha.Length; i++)
            {
                bytes[i * 4] = 255;
                bytes[i * 4 + 1] = 255;
                bytes[i * 4 + 2] = 255;
                bytes[i * 4 + 3] = (byte)MathF.Round(Math.Clamp(Alpha[i], 0f, 1f) * 255f);
            }
            return bytes;
        }

#if !ART_PREVIEW
        public UnityEngine.Color32[] ToColor32()
        {
            var pixels = new UnityEngine.Color32[Width * Height];
            for (int i = 0; i < Alpha.Length; i++)
                pixels[i] = new UnityEngine.Color32(255, 255, 255, (byte)MathF.Round(Math.Clamp(Alpha[i], 0f, 1f) * 255f));
            return pixels;
        }
#endif

        private static float Coverage(float distance) => Math.Clamp(0.5f - distance, 0f, 1f);
    }

    // ---- Note and receptor artwork. Arrows are drawn pointing up; renderers rotate them. ----

    internal const int NoteSize = 256;

    // The fat arrow's outline, shrunk by ArrowRadius so the offset shape gets round corners.
    private const float ArrowRadius = 16f;
    private static readonly float[] ArrowCore =
    {
        0f, 95.4f, 79.4f, 16f, 34f, 16f, 34f, -102f, -34f, -102f, -34f, 16f, -79.4f, 16f
    };

    internal static float ArrowShape(float x, float y) => Polygon(x, y, ArrowCore) - ArrowRadius;

    internal static float CircleShape(float x, float y) => Circle(x, y, 120f);

    /// <summary>A rounded diamond for the middle lane of five-lane arrow charts.</summary>
    internal static float DiamondShape(float x, float y) => (MathF.Abs(x) + MathF.Abs(y) - 104f) * 0.7071f - 10f;

    internal enum Shape { Circle, Arrow, Diamond }

    private static Func<float, float, float> Outline(Shape shape) => shape switch
    {
        Shape.Arrow => ArrowShape,
        Shape.Diamond => DiamondShape,
        _ => CircleShape
    };

    /// <summary>The solid note body, tinted with the note's main color.</summary>
    internal static Mask NoteBody(Shape shape)
    {
        var mask = new Mask(NoteSize, NoteSize);
        mask.Add(Outline(shape));
        return mask;
    }

    /// <summary>
    /// The note's light line work (the game's third note color), echoing the bars'
    /// side marks and center ring.
    /// </summary>
    internal static Mask NoteGlyph(Shape shape)
    {
        var mask = new Mask(NoteSize, NoteSize);
        if (shape == Shape.Circle)
        {
            mask.Add((x, y) => Arc(x, y, 92f, 7f, 140f, 220f));
            mask.Add((x, y) => Arc(x, y, 92f, 7f, -40f, 40f));
            mask.Add((x, y) => Ring(x, y, 26f, 6.5f));
        }
        else
        {
            var outline = Outline(shape);
            mask.Add((x, y) => MathF.Abs(outline(x, y) + 22f) - 2.75f);
            mask.Add((x, y) => Ring(x, y, 24f, 6.5f));
        }
        return mask;
    }

    /// <summary>The note's accent details (the game's second note color): inner ring and ticks.</summary>
    internal static Mask NoteAccent(Shape shape)
    {
        var mask = new Mask(NoteSize, NoteSize);
        mask.Add((x, y) => Ring(x, y, 10.5f, 4f));
        if (shape == Shape.Circle)
        {
            mask.Add((x, y) => Segment(x, y, -64f, 0f, -34f, 0f, 3.5f));
            mask.Add((x, y) => Segment(x, y, 34f, 0f, 64f, 0f, 3.5f));
            mask.Add((x, y) => Circle(x + 70f, y, 5.5f));
            mask.Add((x, y) => Circle(x - 70f, y, 5.5f));
        }
        return mask;
    }

    /// <summary>The receptor's dark face.</summary>
    internal static Mask ReceptorFace(Shape shape) => NoteBody(shape);

    /// <summary>The receptor's light border.</summary>
    internal static Mask ReceptorBorder(Shape shape)
    {
        var mask = new Mask(NoteSize, NoteSize);
        if (shape == Shape.Circle) mask.Add((x, y) => Ring(x, y, 115f, 10f));
        else
        {
            var outline = Outline(shape);
            mask.Add((x, y) => MathF.Abs(outline(x, y) + 5f) - 5f);
        }
        return mask;
    }

    /// <summary>
    /// The receptor's accent glyph, like the native decal. The center stays open so the
    /// game's own dot can light it up while the key is held.
    /// </summary>
    internal static Mask ReceptorGlyph(Shape shape)
    {
        var mask = new Mask(NoteSize, NoteSize);
        mask.Add((x, y) => Ring(x, y, 22f, 6f));
        if (shape == Shape.Circle)
        {
            mask.Add((x, y) => Arc(x, y, 90f, 7f, 140f, 220f));
            mask.Add((x, y) => Arc(x, y, 90f, 7f, -40f, 40f));
            mask.Add((x, y) => Segment(x, y, -59f, 0f, -31f, 0f, 3.5f));
            mask.Add((x, y) => Segment(x, y, 31f, 0f, 59f, 0f, 3.5f));
            mask.Add((x, y) => Ring(x + 68f, y, 8f, 3.5f));
            mask.Add((x, y) => Ring(x - 68f, y, 8f, 3.5f));
        }
        else
        {
            var outline = Outline(shape);
            mask.Add((x, y) => MathF.Abs(outline(x, y) + 24f) - 2.75f);
        }
        return mask;
    }

    internal const int GlowSize = 320;

    /// <summary>
    /// A soft glow in the receptor's shape, replacing the game's rectangular key-press glow.
    /// Drawn on a larger canvas at the same scale so the halo has room around the shape.
    /// </summary>
    internal static Mask ReceptorGlow(Shape shape)
    {
        var mask = new Mask(GlowSize, GlowSize);
        var outline = Outline(shape);
        for (int y = 0; y < GlowSize; y++)
        for (int x = 0; x < GlowSize; x++)
        {
            float d = outline(x + 0.5f - GlowSize * 0.5f, y + 0.5f - GlowSize * 0.5f);
            float a = d > 0f ? 0.9f * MathF.Exp(-d / 14f) : 0.3f + 0.6f * MathF.Exp(d / 10f);
            // Fade out before the texture's edge, or a faint square shows around the glow.
            float edge = MathF.Min(MathF.Min(x + 0.5f, GlowSize - x - 0.5f), MathF.Min(y + 0.5f, GlowSize - y - 0.5f));
            float fade = Math.Clamp(edge / 24f, 0f, 1f);
            mask.Alpha[y * GlowSize + x] = Math.Clamp(a * fade * fade * (3f - 2f * fade), 0f, 1f);
        }
        return mask;
    }

    // ---- Timing bar artwork ----

    /// <summary>A rounded bar used for the track, ticks, and center line (scaled per use).</summary>
    internal static Mask RoundedBar(int width, int height)
    {
        var mask = new Mask(width, height);
        float radius = MathF.Min(width, height) * 0.5f;
        mask.Add((x, y) => RoundBox(x, y, width * 0.5f - 1f, height * 0.5f - 1f, radius - 1f));
        return mask;
    }

    /// <summary>The average marker: a small rounded triangle pointing down at the track.</summary>
    internal static Mask Marker()
    {
        var mask = new Mask(64, 64);
        var tri = new[] { 0f, -18f, 20f, 14f, -20f, 14f };
        mask.Add((x, y) => Polygon(x, y, tri) - 6f);
        return mask;
    }

    /// <summary>A hare facing left, toward the early side of the bar.</summary>
    internal static Mask Rabbit()
    {
        var mask = new Mask(128, 128);
        mask.Add((x, y) => Ellipse(x - 8f, y + 14f, 38f, 26f));                // body
        mask.Add((x, y) => Circle(x - 30f, y + 20f, 20f));                     // haunch
        mask.Add((x, y) => Circle(x - 48f, y + 8f, 9f));                       // tail
        mask.Add((x, y) => Ellipse(x + 30f, y - 6f, 20f, 17f));                // head
        mask.Add((x, y) => Segment(x, y, -26f, 18f, -16f, 58f, 11f));          // back ear
        mask.Add((x, y) => Segment(x, y, -34f, 18f, -38f, 60f, 11f));          // front ear
        mask.Add((x, y) => Segment(x, y, -30f, -38f, -14f, -38f, 10f));        // front paw
        mask.Add((x, y) => Segment(x, y, 14f, -38f, 44f, -38f, 12f));          // hind foot
        mask.Cut((x, y) => Circle(x + 38f, y - 10f, 3.5f));                    // eye
        return mask;
    }

    /// <summary>A tortoise facing right, toward the late side of the bar.</summary>
    internal static Mask Turtle()
    {
        var mask = new Mask(128, 128);
        // Shell: the top half of an ellipse, with plate seams cut out.
        mask.Add((x, y) => MathF.Max(Ellipse(x + 4f, y + 10f, 44f, 38f), -(y + 10f)));
        mask.Add((x, y) => RoundBox(x + 4f, y + 12f, 48f, 5f, 4f));            // shell rim
        mask.Add((x, y) => Ellipse(x - 52f, y + 4f, 13f, 11f));                // head
        mask.Add((x, y) => Segment(x, y, 38f, -10f, 44f, -8f, 12f));           // neck
        mask.Add((x, y) => Segment(x, y, 26f, -18f, 28f, -34f, 13f));          // front leg
        mask.Add((x, y) => Segment(x, y, -30f, -18f, -32f, -34f, 13f));        // back leg
        mask.Add((x, y) => Segment(x, y, -50f, -14f, -60f, -20f, 7f));         // tail
        mask.Cut((x, y) => Segment(x, y, -20f, -6f, -10f, 26f, 3.5f));         // plate seams
        mask.Cut((x, y) => Segment(x, y, 16f, -6f, 8f, 26f, 3.5f));
        mask.Cut((x, y) => Segment(x, y, -38f, 8f, 30f, 8f, 3.5f));
        mask.Cut((x, y) => Circle(x - 56f, y + 1f, 3f));                       // eye
        return mask;
    }

    // ---- Signed distance primitives (Inigo Quilez's formulas) ----

    internal static float Circle(float x, float y, float r) => MathF.Sqrt(x * x + y * y) - r;

    internal static float Ring(float x, float y, float r, float width) => MathF.Abs(Circle(x, y, r)) - width * 0.5f;

    internal static float RoundBox(float x, float y, float halfW, float halfH, float radius)
    {
        float qx = MathF.Abs(x) - halfW + radius;
        float qy = MathF.Abs(y) - halfH + radius;
        float outside = MathF.Sqrt(MathF.Max(qx, 0f) * MathF.Max(qx, 0f) + MathF.Max(qy, 0f) * MathF.Max(qy, 0f));
        return outside + MathF.Min(MathF.Max(qx, qy), 0f) - radius;
    }

    internal static float Segment(float x, float y, float ax, float ay, float bx, float by, float width)
    {
        float px = x - ax, py = y - ay, dx = bx - ax, dy = by - ay;
        float h = Math.Clamp((px * dx + py * dy) / (dx * dx + dy * dy), 0f, 1f);
        float ex = px - dx * h, ey = py - dy * h;
        return MathF.Sqrt(ex * ex + ey * ey) - width * 0.5f;
    }

    internal static float Ellipse(float x, float y, float rx, float ry)
    {
        // A scaled circle is a close enough distance estimate for soft edges on small icons.
        float k = MathF.Min(rx, ry);
        return (MathF.Sqrt(x * x / (rx * rx) + y * y / (ry * ry)) - 1f) * k;
    }

    /// <summary>Exact distance to a simple polygon (convex or concave).</summary>
    internal static float Polygon(float x, float y, float[] v)
    {
        int n = v.Length / 2;
        float d = (x - v[0]) * (x - v[0]) + (y - v[1]) * (y - v[1]);
        float s = 1f;
        for (int i = 0, j = n - 1; i < n; j = i, i++)
        {
            float ex = v[j * 2] - v[i * 2], ey = v[j * 2 + 1] - v[i * 2 + 1];
            float wx = x - v[i * 2], wy = y - v[i * 2 + 1];
            float h = Math.Clamp((wx * ex + wy * ey) / (ex * ex + ey * ey), 0f, 1f);
            float bx = wx - ex * h, by = wy - ey * h;
            d = MathF.Min(d, bx * bx + by * by);
            bool c1 = y >= v[i * 2 + 1], c2 = y < v[j * 2 + 1], c3 = ex * wy > ey * wx;
            if ((c1 && c2 && c3) || (!c1 && !c2 && !c3)) s = -s;
        }
        return s * MathF.Sqrt(d);
    }

    /// <summary>Part of a ring between two angles in degrees (0 = +x, counter-clockwise).</summary>
    internal static float Arc(float x, float y, float r, float width, float fromDeg, float toDeg)
    {
        float angle = MathF.Atan2(y, x) * 180f / MathF.PI;
        float mid = (fromDeg + toDeg) * 0.5f, half = (toDeg - fromDeg) * 0.5f;
        float delta = MathF.Abs(((angle - mid) % 360f + 540f) % 360f - 180f);
        if (delta <= half) return Ring(x, y, r, width);
        // Outside the span, measure to the nearer rounded end cap.
        float a1 = fromDeg * MathF.PI / 180f, a2 = toDeg * MathF.PI / 180f;
        float d1 = MathF.Sqrt((x - r * MathF.Cos(a1)) * (x - r * MathF.Cos(a1)) + (y - r * MathF.Sin(a1)) * (y - r * MathF.Sin(a1)));
        float d2 = MathF.Sqrt((x - r * MathF.Cos(a2)) * (x - r * MathF.Cos(a2)) + (y - r * MathF.Sin(a2)) * (y - r * MathF.Sin(a2)));
        return MathF.Min(d1, d2) - width * 0.5f;
    }
}
