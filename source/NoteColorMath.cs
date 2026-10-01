using System.Globalization;
using UnityEngine;

namespace NocturnePlus;

/// <summary>
/// Color maths for the note color palettes (CustomNoteColors): hex, HSV for the page's sliders, the
/// accents and line work worked out from a note's body color, how far apart two colors look
/// (CIE delta E 2000), and contrast. Colors are sRGB, as the game's palettes are.
/// </summary>
internal static class NoteColorMath
{
    /// <summary>"#RRGGBB", "RRGGBB" or the short "#RGB".</summary>
    internal static bool TryParseHex(string text, out Color color)
    {
        color = default;
        string s = (text ?? "").Trim();
        if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
        if (s.Length == 3) s = string.Concat(s.Select(c => new string(c, 2)));
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return false;
        color = EditorUi.Hex(rgb);
        return true;
    }

    // ---- HSV: hue 0 to 360, saturation and value 0 to 1 ----------------------------------------------

    internal static (float H, float S, float V) ToHsv(Color c)
    {
        float max = Math.Max(c.r, Math.Max(c.g, c.b)), min = Math.Min(c.r, Math.Min(c.g, c.b)), d = max - min;
        float h = 0f;
        if (d > 0f)
        {
            if (max == c.r) h = 60f * ((c.g - c.b) / d % 6f);
            else if (max == c.g) h = 60f * ((c.b - c.r) / d + 2f);
            else h = 60f * ((c.r - c.g) / d + 4f);
        }
        if (h < 0f) h += 360f;
        return (h, max > 0f ? d / max : 0f, max);
    }

    internal static Color FromHsv(float h, float s, float v)
    {
        h = ((h % 360f) + 360f) % 360f;
        s = Math.Clamp(s, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);
        float c = v * s, x = c * (1f - Math.Abs(h / 60f % 2f - 1f)), m = v - c;
        (float r, float g, float b) = h < 60 ? (c, x, 0f) : h < 120 ? (x, c, 0f) : h < 180 ? (0f, c, x) : h < 240 ? (0f, x, c) : h < 300 ? (x, 0f, c) : (c, 0f, x);
        return new Color(r + m, g + m, b + m, 1f);
    }

    // ---- the shades worked out from a body color, in OKLab ----------------------------------------------
    //
    // Fitted to the game's own palettes and Akuma: the accents (and the hold) are about 0.13 lighter
    // than the body with the same hue and chroma, and the line work is a pale tint of the hue (L 0.91),
    // except on a pale body, where it's a dark shade instead (as on Forest's pale middle note and
    // Akuma's yellow), so it still shows.

    internal static Color Accents(Color body)
    {
        var (l, c, h) = ToOklch(body);
        // A near-white body has no room to go lighter: its accents go darker instead, as on Forest's pale note.
        return l > 0.89f ? FromOklch(l - 0.18f, c * 0.9f, h) : FromOklch(Math.Min(0.97f, l + 0.13f), c, h);
    }

    internal static Color Lines(Color body)
    {
        var (l, c, h) = ToOklch(body);
        return l < 0.8f
            ? FromOklch(Math.Max(0.91f, Math.Min(0.97f, l + 0.3f)), Math.Min(c * 0.45f, 0.07f), h)
            : FromOklch(l >= 0.94f ? 0.22f : 0.42f, Math.Min(c * 0.8f, 0.1f), h);
    }

    private static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float ToSrgb(float c) => c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;

    internal static (float L, float A, float B) ToOklab(Color color)
    {
        float r = ToLinear(color.r), g = ToLinear(color.g), b = ToLinear(color.b);
        float l = MathF.Cbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b);
        float m = MathF.Cbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b);
        float s = MathF.Cbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b);
        return (0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
                1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
                0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    // Linear sRGB for an OKLab color; outside 0..1 when the color is outside sRGB.
    private static (float R, float G, float B) FromOklabLinear(float lightness, float a, float b)
    {
        float l = lightness + 0.3963377774f * a + 0.2158037573f * b;
        float m = lightness - 0.1055613458f * a - 0.0638541728f * b;
        float s = lightness - 0.0894841775f * a - 1.2914855480f * b;
        l = l * l * l;
        m = m * m * m;
        s = s * s * s;
        return (4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
                -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
                -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s);
    }

    internal static (float L, float C, float H) ToOklch(Color color)
    {
        var (l, a, b) = ToOklab(color);
        return (l, MathF.Sqrt(a * a + b * b), MathF.Atan2(b, a));
    }

    /// <summary>An OKLCH color (hue in radians), with its chroma lowered until it fits in sRGB.</summary>
    internal static Color FromOklch(float lightness, float chroma, float hue)
    {
        lightness = Math.Clamp(lightness, 0f, 1f);
        float low = 0f, high = Math.Max(0f, chroma);
        if (!Fits(lightness, high, hue))
        {
            for (int i = 0; i < 20; i++)
            {
                float mid = (low + high) / 2f;
                if (Fits(lightness, mid, hue)) low = mid; else high = mid;
            }
            chroma = low;
        }
        var (r, g, b) = FromOklabLinear(lightness, chroma * MathF.Cos(hue), chroma * MathF.Sin(hue));
        return new Color(ToSrgb(Math.Clamp(r, 0f, 1f)), ToSrgb(Math.Clamp(g, 0f, 1f)), ToSrgb(Math.Clamp(b, 0f, 1f)), 1f);
    }

    private static bool Fits(float lightness, float chroma, float hue)
    {
        var (r, g, b) = FromOklabLinear(lightness, chroma * MathF.Cos(hue), chroma * MathF.Sin(hue));
        const float e = 0.0001f;
        return r >= -e && r <= 1 + e && g >= -e && g <= 1 + e && b >= -e && b <= 1 + e;
    }

    // ---- how different two colors look -------------------------------------------------------------

    /// <summary>
    /// CIE delta E 2000 between two colors: about 1 is the smallest difference an eye sees side by side,
    /// and under about 10 two small shapes are easily taken for each other.
    /// </summary>
    internal static float DeltaE2000(Color x, Color y)
    {
        var (l1, a1, b1) = ToLab(x);
        var (l2, a2, b2) = ToLab(y);
        double c1 = Math.Sqrt(a1 * a1 + b1 * b1), c2 = Math.Sqrt(a2 * a2 + b2 * b2), cBar = (c1 + c2) / 2;
        double g = 0.5 * (1 - Math.Sqrt(Math.Pow(cBar, 7) / (Math.Pow(cBar, 7) + Math.Pow(25, 7))));
        double a1p = (1 + g) * a1, a2p = (1 + g) * a2;
        double c1p = Math.Sqrt(a1p * a1p + b1 * b1), c2p = Math.Sqrt(a2p * a2p + b2 * b2);
        double h1p = Hue(b1, a1p), h2p = Hue(b2, a2p);
        double dL = l2 - l1, dC = c2p - c1p;
        double dh = c1p * c2p == 0 ? 0 : Math.Abs(h2p - h1p) <= 180 ? h2p - h1p : h2p - h1p > 180 ? h2p - h1p - 360 : h2p - h1p + 360;
        double dH = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(Rad(dh / 2));
        double lBar = (l1 + l2) / 2, cBarP = (c1p + c2p) / 2;
        double hBar = c1p * c2p == 0 ? h1p + h2p : Math.Abs(h1p - h2p) <= 180 ? (h1p + h2p) / 2 : h1p + h2p < 360 ? (h1p + h2p + 360) / 2 : (h1p + h2p - 360) / 2;
        double t = 1 - 0.17 * Math.Cos(Rad(hBar - 30)) + 0.24 * Math.Cos(Rad(2 * hBar)) + 0.32 * Math.Cos(Rad(3 * hBar + 6)) - 0.20 * Math.Cos(Rad(4 * hBar - 63));
        double dTheta = 30 * Math.Exp(-Math.Pow((hBar - 275) / 25, 2));
        double rC = 2 * Math.Sqrt(Math.Pow(cBarP, 7) / (Math.Pow(cBarP, 7) + Math.Pow(25, 7)));
        double sL = 1 + 0.015 * Math.Pow(lBar - 50, 2) / Math.Sqrt(20 + Math.Pow(lBar - 50, 2));
        double sC = 1 + 0.045 * cBarP, sH = 1 + 0.015 * cBarP * t;
        double rT = -Math.Sin(Rad(2 * dTheta)) * rC;
        return (float)Math.Sqrt(Math.Pow(dL / sL, 2) + Math.Pow(dC / sC, 2) + Math.Pow(dH / sH, 2) + rT * (dC / sC) * (dH / sH));
    }

    private static double Rad(double degrees) => degrees * Math.PI / 180;

    private static double Hue(double b, double a)
    {
        if (a == 0 && b == 0) return 0;
        double h = Math.Atan2(b, a) * 180 / Math.PI;
        return h < 0 ? h + 360 : h;
    }

    // CIELAB under D65.
    private static (double L, double A, double B) ToLab(Color color)
    {
        double r = ToLinear(color.r), g = ToLinear(color.g), b = ToLinear(color.b);
        double x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047;
        double y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
        double z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / 1.08883;
        static double F(double v) => v > 216.0 / 24389 ? Math.Cbrt(v) : (24389.0 / 27 * v + 16) / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>The contrast between two colors, 1 (none) to 21 (black on white), as the web's accessibility rules measure it.</summary>
    internal static float Contrast(Color x, Color y)
    {
        float lx = Luminance(x), ly = Luminance(y);
        return (Math.Max(lx, ly) + 0.05f) / (Math.Min(lx, ly) + 0.05f);
    }

    private static float Luminance(Color c) => 0.2126f * ToLinear(c.r) + 0.7152f * ToLinear(c.g) + 0.0722f * ToLinear(c.b);
}
