using System.Globalization;

namespace NocturneFlatScroll;

/// <summary>
/// Scroll speed changes (#SCROLLS: beat=ratio,...). A ratio multiplies how fast the notes move
/// from that beat until the next change; timing and judgement stay the same. The "displayed beat"
/// is the integral of the ratio over beats, as in StepMania.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class ScrollSpeeds
{
    internal static List<(double Beat, double Ratio)> Parse(string? tag)
    {
        var list = new List<(double, double)>();
        if (string.IsNullOrWhiteSpace(tag)) return list;
        foreach (var part in tag!.Split(','))
        {
            var kv = part.Split('=');
            if (kv.Length != 2) continue;
            if (!double.TryParse(kv[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double beat)) continue;
            if (!double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ratio)) continue;
            // Zero or negative speeds would stop or reverse the notes; keep them moving forward.
            list.Add((Math.Max(0, beat), Math.Clamp(ratio, 0.05, 20)));
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    /// <summary>The #SCROLLS text for a list: beats with up to six decimals, ratios with up to three.</summary>
    internal static string Write(List<(double Beat, double Ratio)> list) =>
        string.Join(",", list.Select(s => $"{s.Beat.ToString("0.######", CultureInfo.InvariantCulture)}={s.Ratio.ToString("0.###", CultureInfo.InvariantCulture)}"));

    internal static double Displayed(List<(double Beat, double Ratio)> scrolls, double beat)
    {
        if (scrolls.Count == 0) return beat;
        // Before beat 0 the notes move at the speed a change at beat 0 sets, as in battle.
        double displayed = 0, last = 0, ratio = scrolls[0].Beat <= 0 ? scrolls[0].Ratio : 1;
        foreach (var (b, r) in scrolls)
        {
            if (b >= beat) break;
            displayed += (b - last) * ratio;
            last = b;
            ratio = r;
        }
        return displayed + (beat - last) * ratio;
    }

    internal static double Undisplayed(List<(double Beat, double Ratio)> scrolls, double displayed)
    {
        if (scrolls.Count == 0) return displayed;
        double d = 0, last = 0, ratio = scrolls[0].Beat <= 0 ? scrolls[0].Ratio : 1;
        foreach (var (b, r) in scrolls)
        {
            double next = d + (b - last) * ratio;
            if (next >= displayed) break;
            d = next;
            last = b;
            ratio = r;
        }
        return last + (displayed - d) / ratio;
    }
}
