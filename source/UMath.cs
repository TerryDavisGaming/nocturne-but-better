using System.Globalization;
using UnityEngine;

namespace NocturnePlus;

/// <summary>
/// Unity's small struct math, done in the mod's own code while Optimizing. In this interop build every
/// Mathf, Rect and Vector member is a call into the game that boxes its result, while the game's own
/// versions are one to three float instructions each. These copy those instructions (checked in the
/// game's code), so every result is the same to the bit. With Exact off (Normal), each helper makes the
/// same Unity call as before.
///
/// A QA build with NFS_QA_EXACT=1 works out both and counts any difference (Obj's checks too).
/// </summary>
internal static class UMath
{
    /// <summary>Set from Performance.Optimizing by the code that uses these helpers, each time it's called.</summary>
    internal static bool Exact;

    // Vector3's == and != compare the squared distance with kEpsilon * kEpsilon (0x2EDBE6FE).
    private const float SqrEpsilon = 9.99999944E-11f;

    internal static void Follow(bool optimizing)
    {
        if (optimizing == Exact) return;
        Exact = optimizing;
        ModLog.Info(optimizing
            ? "HUD layout: exact math in the mod's own code and the lighter meter clipping are on."
            : "HUD layout: back to Unity's own math and the full meter clipping.");
    }

    internal static float Min(float a, float b)
    {
        float exact = a < b ? a : b;
        if (Shadow) return Pick("Mathf.Min", exact, Mathf.Min(a, b));
        return Exact ? exact : Mathf.Min(a, b);
    }

    internal static float Max(float a, float b)
    {
        float exact = a > b ? a : b;
        if (Shadow) return Pick("Mathf.Max", exact, Mathf.Max(a, b));
        return Exact ? exact : Mathf.Max(a, b);
    }

    internal static int Min(int a, int b)
    {
        int exact = a < b ? a : b;
        if (Shadow) return Pick("Mathf.Min(int)", exact, Mathf.Min(a, b));
        return Exact ? exact : Mathf.Min(a, b);
    }

    internal static float Abs(float value)
    {
        float exact = Math.Abs(value);
        if (Shadow) return Pick("Mathf.Abs", exact, Mathf.Abs(value));
        return Exact ? exact : Mathf.Abs(value);
    }

    internal static float Clamp(float value, float min, float max)
    {
        float exact = value < min ? min : (value > max ? max : value);
        if (Shadow) return Pick("Mathf.Clamp", exact, Mathf.Clamp(value, min, max));
        return Exact ? exact : Mathf.Clamp(value, min, max);
    }

    internal static float XMin(Rect r)
    {
        if (Shadow) return Pick("Rect.xMin", r.m_XMin, r.xMin);
        return Exact ? r.m_XMin : r.xMin;
    }

    internal static float YMin(Rect r)
    {
        if (Shadow) return Pick("Rect.yMin", r.m_YMin, r.yMin);
        return Exact ? r.m_YMin : r.yMin;
    }

    internal static float XMax(Rect r)
    {
        if (Shadow) return Pick("Rect.xMax", r.m_Width + r.m_XMin, r.xMax);
        return Exact ? r.m_Width + r.m_XMin : r.xMax;
    }

    internal static float YMax(Rect r)
    {
        if (Shadow) return Pick("Rect.yMax", r.m_Height + r.m_YMin, r.yMax);
        return Exact ? r.m_Height + r.m_YMin : r.yMax;
    }

    internal static float Width(Rect r)
    {
        if (Shadow) return Pick("Rect.width", r.m_Width, r.width);
        return Exact ? r.m_Width : r.width;
    }

    internal static float Height(Rect r)
    {
        if (Shadow) return Pick("Rect.height", r.m_Height, r.height);
        return Exact ? r.m_Height : r.height;
    }

    /// <summary>Rect.MinMaxRect.</summary>
    internal static Rect MinMax(float xmin, float ymin, float xmax, float ymax)
    {
        var exact = new Rect { m_XMin = xmin, m_YMin = ymin, m_Width = xmax - xmin, m_Height = ymax - ymin };
        if (Shadow) return Pick("Rect.MinMaxRect", exact, Rect.MinMaxRect(xmin, ymin, xmax, ymax));
        return Exact ? exact : Rect.MinMaxRect(xmin, ymin, xmax, ymax);
    }

    /// <summary>new Rect(point, Vector2.zero).</summary>
    internal static Rect PointRect(Vector2 point)
    {
        var exact = new Rect { m_XMin = point.x, m_YMin = point.y, m_Width = 0f, m_Height = 0f };
        if (Shadow) return Pick("new Rect(point, Vector2.zero)", exact, new Rect(point, Vector2.zero));
        return Exact ? exact : new Rect(point, Vector2.zero);
    }

    internal static Vector2 V2(float x, float y)
    {
        var exact = new Vector2 { x = x, y = y };
        if (Shadow) return Pick("new Vector2", exact, new Vector2(x, y));
        return Exact ? exact : new Vector2(x, y);
    }

    internal static Vector3 V3(float x, float y, float z)
    {
        var exact = new Vector3 { x = x, y = y, z = z };
        if (Shadow) return Pick("new Vector3", exact, new Vector3(x, y, z));
        return Exact ? exact : new Vector3(x, y, z);
    }

    /// <summary>a + b.</summary>
    internal static Vector3 Add(Vector3 a, Vector3 b)
    {
        var exact = new Vector3 { x = a.x + b.x, y = a.y + b.y, z = a.z + b.z };
        if (Shadow) return Pick("Vector3 +", exact, a + b);
        return Exact ? exact : a + b;
    }

    /// <summary>Vector3.one * d.</summary>
    internal static Vector3 OneTimes(float d)
    {
        var exact = new Vector3 { x = 1f * d, y = 1f * d, z = 1f * d };
        if (Shadow) return Pick("Vector3.one *", exact, Vector3.one * d);
        return Exact ? exact : Vector3.one * d;
    }

    /// <summary>Vector3.Scale(a, b).</summary>
    internal static Vector3 Scale(Vector3 a, Vector3 b)
    {
        var exact = new Vector3 { x = a.x * b.x, y = a.y * b.y, z = a.z * b.z };
        if (Shadow) return Pick("Vector3.Scale", exact, Vector3.Scale(a, b));
        return Exact ? exact : Vector3.Scale(a, b);
    }

    /// <summary>a != b.</summary>
    internal static bool NotEqual(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
        bool exact = !((dy * dy + dx * dx) + dz * dz < SqrEpsilon);
        if (Shadow) return Pick("Vector3 !=", exact, a != b);
        return Exact ? exact : a != b;
    }

    // ---- QA builds: both answers, compared ------------------------------------------------------------

    internal static readonly bool Shadow = QaBuild.Env("NFS_QA_EXACT") == "1";
    private const int ShownMismatches = 5;
    private static long checks, mismatches;
    private static float nextReport;

    /// <summary>QA builds with NFS_QA_EXACT=1: the running totals, at most every 10 s while a battle runs.</summary>
    internal static void QaReport()
    {
        if (!Shadow) return;
        float now = Time.unscaledTime;
        if (now < nextReport || GameManager.GameState != GameStates.Combat) return;
        nextReport = now + 10f;
        ModLog.Info($"Exact checks: {checks} checks, {mismatches} mismatches.");
    }

    private static bool Pick(string what, bool exact, bool unity)
    {
        Check(what, exact, unity);
        return Exact ? exact : unity;
    }

    internal static void Check(string what, bool exact, bool unity)
    {
        checks++;
        if (exact != unity) Mismatch(what, $"exact {exact}, Unity {unity}");
    }

    private static float Pick(string what, float exact, float unity)
    {
        checks++;
        if (!Same(exact, unity)) Mismatch(what, $"exact {Show(exact)}, Unity {Show(unity)}");
        return Exact ? exact : unity;
    }

    private static int Pick(string what, int exact, int unity)
    {
        checks++;
        if (exact != unity) Mismatch(what, $"exact {exact}, Unity {unity}");
        return Exact ? exact : unity;
    }

    private static Rect Pick(string what, Rect exact, Rect unity)
    {
        checks++;
        if (!SameRect(exact, unity)) Mismatch(what, $"exact {Show(exact)}, Unity {Show(unity)}");
        return Exact ? exact : unity;
    }

    private static Vector2 Pick(string what, Vector2 exact, Vector2 unity)
    {
        checks++;
        if (!Same(exact.x, unity.x) || !Same(exact.y, unity.y))
            Mismatch(what, $"exact ({Show(exact.x)}, {Show(exact.y)}), Unity ({Show(unity.x)}, {Show(unity.y)})");
        return Exact ? exact : unity;
    }

    private static Vector3 Pick(string what, Vector3 exact, Vector3 unity)
    {
        checks++;
        if (!Same(exact.x, unity.x) || !Same(exact.y, unity.y) || !Same(exact.z, unity.z))
            Mismatch(what, $"exact ({Show(exact.x)}, {Show(exact.y)}, {Show(exact.z)}), Unity ({Show(unity.x)}, {Show(unity.y)}, {Show(unity.z)})");
        return Exact ? exact : unity;
    }

    /// <summary>QA: a whole result the caller compared itself (the meter clipping's two passes).</summary>
    internal static void Compare(string what, bool same, string? detail)
    {
        checks++;
        if (!same) Mismatch(what, detail ?? "");
    }

    internal static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    internal static bool SameRect(Rect a, Rect b) =>
        Same(a.m_XMin, b.m_XMin) && Same(a.m_YMin, b.m_YMin) && Same(a.m_Width, b.m_Width) && Same(a.m_Height, b.m_Height);

    internal static string Show(float value) =>
        value.ToString("R", CultureInfo.InvariantCulture) + " (0x" + BitConverter.SingleToInt32Bits(value).ToString("X8") + ")";

    internal static string Show(Rect r) => $"({Show(r.m_XMin)}, {Show(r.m_YMin)}, {Show(r.m_Width)}, {Show(r.m_Height)})";

    private static void Mismatch(string what, string detail)
    {
        mismatches++;
        if (mismatches <= ShownMismatches)
            ModLog.Info($"Exact check mismatch {mismatches}: {what}: {detail}.");
    }
}
