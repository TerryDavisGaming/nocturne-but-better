namespace NocturneFlatScroll;

/// <summary>
/// Frame pixels for custom enemy art: packing an animation's frames into one atlas (the way the
/// game's own enemy sheets are one texture of equal cells), shrinking by whole numbers, the
/// see-through colour, and measuring where a frame's visible pixels are. Frames are RGBA with
/// the top row first; the atlas is written bottom row first, which is what Unity's
/// Texture2D.LoadRawTextureData expects, and its rects count from the bottom-left like Unity's.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class FrameAtlas
{
    /// <summary>A clear pixel around every cell, so smooth filtering never picks up a neighbour.</summary>
    internal const int Gutter = 1;
    /// <summary>Pixels with less alpha than this count as see-through when measuring.</summary>
    internal const int VisibleAlpha = 16;

    internal sealed class Layout
    {
        internal int SrcW, SrcH, Scale, FrameW, FrameH, Cols, Rows, AtlasW, AtlasH, Count;
        internal long Bytes => (long)AtlasW * AtlasH * 4;
    }

    /// <summary>
    /// The smallest whole-number shrink from <paramref name="minScale"/> up at which all the
    /// frames fit one atlas of at most maxSide on a side and maxBytes of RGBA, with the column
    /// count that keeps it closest to square. Null when even maxScale doesn't fit.
    /// </summary>
    internal static Layout? Plan(int w, int h, int count, int maxSide, long maxBytes, int minScale = 1, int maxScale = 16)
    {
        if (w < 1 || h < 1 || count < 1) return null;
        for (int s = Math.Max(1, minScale); s <= maxScale; s++)
        {
            int fw = (w + s - 1) / s, fh = (h + s - 1) / s;
            Layout? best = null;
            for (int cols = 1; cols <= count; cols++)
            {
                int rows = (count + cols - 1) / cols;
                long aw = (long)cols * (fw + Gutter) + Gutter, ah = (long)rows * (fh + Gutter) + Gutter;
                if (aw > maxSide) break;
                if (ah > maxSide || aw * ah * 4 > maxBytes) continue;
                if (best == null || Math.Max(aw, ah) < Math.Max(best.AtlasW, best.AtlasH) ||
                    (Math.Max(aw, ah) == Math.Max(best.AtlasW, best.AtlasH) && aw * ah < (long)best.AtlasW * best.AtlasH))
                    best = new Layout { SrcW = w, SrcH = h, Scale = s, FrameW = fw, FrameH = fh, Cols = cols, Rows = rows, AtlasW = (int)aw, AtlasH = (int)ah, Count = count };
            }
            if (best != null) return best;
        }
        return null;
    }

    /// <summary>Cell i's rect in Unity's texture space (origin bottom-left).</summary>
    internal static (int X, int Y, int W, int H) Cell(Layout l, int i)
    {
        int c = i % l.Cols, r = i / l.Cols;
        int left = Gutter + c * (l.FrameW + Gutter);
        int top = Gutter + r * (l.FrameH + Gutter);
        return (left, l.AtlasH - top - l.FrameH, l.FrameW, l.FrameH);
    }

    /// <summary>
    /// Copies a frame (SrcW x SrcH, top row first) into cell i of a bottom-up atlas, box-filtering
    /// with alpha weighting when shrunk, so edges don't pick up dark fringes.
    /// </summary>
    internal static void Put(byte[] atlas, Layout l, int i, byte[] frame)
    {
        var cell = Cell(l, i);
        int s = l.Scale;
        for (int y = 0; y < l.FrameH; y++)
        {
            int dstRow = cell.Y + (l.FrameH - 1 - y);        // frame row y (from the top) lands this far up
            if (s == 1)
            {
                Buffer.BlockCopy(frame, y * l.SrcW * 4, atlas, (dstRow * l.AtlasW + cell.X) * 4, l.SrcW * 4);
                continue;
            }
            for (int x = 0; x < l.FrameW; x++)
            {
                int o = (dstRow * l.AtlasW + cell.X + x) * 4;
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                int y1 = Math.Min(l.SrcH, y * s + s), x1 = Math.Min(l.SrcW, x * s + s);
                for (int yy = y * s; yy < y1; yy++)
                    for (int xx = x * s; xx < x1; xx++)
                    {
                        int q = (yy * l.SrcW + xx) * 4;
                        int al = frame[q + 3];
                        r += frame[q] * al; g += frame[q + 1] * al; b += frame[q + 2] * al; a += al; n++;
                    }
                if (a > 0) { atlas[o] = (byte)(r / a); atlas[o + 1] = (byte)(g / a); atlas[o + 2] = (byte)(b / a); }
                else { atlas[o] = 0; atlas[o + 1] = 0; atlas[o + 2] = 0; }
                atlas[o + 3] = (byte)(n > 0 ? a / n : 0);
            }
        }
    }

    /// <summary>Copies a w x h rect at (x, y) of a picture (top row first) into a frame buffer.</summary>
    internal static void CopyRect(byte[] picture, int pictureW, int x, int y, int w, int h, byte[] frame)
    {
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(picture, ((y + row) * pictureW + x) * 4, frame, row * w * 4, w * 4);
    }

    /// <summary>
    /// Makes a colour see-through: pixels within <paramref name="range"/> (0..1) of it vanish,
    /// with a soft edge just past the range. The distance is in YCbCr, with brightness counting
    /// half for coloured keys, so a green screen's shading still goes. Edge pixels of a green or
    /// blue key lose that colour's spill.
    /// </summary>
    internal static void Key(byte[] frame, int pixels, int rgb, double range)
    {
        var key = new KeyColour(rgb);
        double lo = Math.Max(0, range), soft = lo * 0.5 + 0.02;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            if (frame[o + 3] == 0) continue;
            double d = key.Distance(frame[o], frame[o + 1], frame[o + 2]);
            if (d >= lo + soft) continue;
            double keep = d <= lo ? 0 : (d - lo) / soft;
            frame[o + 3] = (byte)Math.Round(frame[o + 3] * keep);
            if (keep > 0 && key.Green) frame[o + 1] = Math.Min(frame[o + 1], Math.Max(frame[o], frame[o + 2]));
            if (keep > 0 && key.Blue) frame[o + 2] = Math.Min(frame[o + 2], Math.Max(frame[o], frame[o + 1]));
        }
    }

    /// <summary>How far a colour is from a see-through colour, 0..about 1, measured as Key measures it.</summary>
    internal static double Distance(int keyRgb, int r, int g, int b) => new KeyColour(keyRgb).Distance(r, g, b);

    // A see-through colour in YCbCr, and what its edge pixels lose.
    private readonly struct KeyColour
    {
        private readonly double y, cb, cr, wy;
        internal readonly bool Green, Blue;

        internal KeyColour(int rgb)
        {
            int kr = (rgb >> 16) & 0xFF, kg = (rgb >> 8) & 0xFF, kb = rgb & 0xFF;
            Ycc(kr, kg, kb, out y, out cb, out cr);
            bool grey = Math.Abs(cb - 128) + Math.Abs(cr - 128) < 24;
            wy = grey ? 1.0 : 0.5;
            Green = !grey && kg > kr && kg > kb;
            Blue = !grey && kb > kr && kb > kg;
        }

        internal double Distance(int r, int g, int b)
        {
            Ycc(r, g, b, out double py, out double pcb, out double pcr);
            double dy = (py - y) * wy, dcb = pcb - cb, dcr = pcr - cr;
            return Math.Sqrt(dy * dy + dcb * dcb + dcr * dcr) / 255.0;
        }
    }

    private static void Ycc(double r, double g, double b, out double y, out double cb, out double cr)
    {
        y = 0.299 * r + 0.587 * g + 0.114 * b;
        cb = 128 - 0.168736 * r - 0.331264 * g + 0.5 * b;
        cr = 128 + 0.5 * r - 0.418688 * g - 0.081312 * b;
    }

    /// <summary>The box of pixels with alpha of at least VisibleAlpha (right and bottom exclusive); false when there are none.</summary>
    internal static bool Visible(byte[] frame, int w, int h, out int left, out int top, out int right, out int bottom)
    {
        left = w; top = h; right = 0; bottom = 0;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                if (frame[row + x * 4 + 3] < VisibleAlpha) continue;
                if (x < left) left = x;
                if (x >= right) right = x + 1;
                if (y < top) top = y;
                bottom = y + 1;
            }
        }
        return right > left && bottom > top;
    }

    /// <summary>Whether every pixel is fully opaque (a JPEG, or a picture with a background).</summary>
    internal static bool Opaque(byte[] frame, int pixels)
    {
        for (int i = 0; i < pixels; i++)
            if (frame[i * 4 + 3] != 255) return false;
        return true;
    }
}
