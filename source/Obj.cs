using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace NocturnePlus;

/// <summary>
/// Unity's liveness and identity checks (if (obj), !obj, == and != on a UnityEngine.Object) without a
/// call into the game, while Optimizing. In this build Unity's own operators only test the object's
/// m_CachedPtr field, the pointer to its engine object that Unity clears when the object is destroyed
/// (op_Implicit and CompareBaseObjects read [obj+0x10]). Reading that field gives the same answer to
/// the bit, destroyed objects included. Normal, or a field that can't be found where Unity reads it,
/// uses Unity's own operators.
/// </summary>
internal static class Obj
{
    // Where Unity's operators read m_CachedPtr in this build.
    private const int UnityReadsAt = 0x10;
    // 0 until looked up, then the offset, or -1 when Unity's operators are used.
    private static int offset;

    /// <summary>Unity's if (o): o isn't null and hasn't been destroyed.</summary>
    internal static bool Alive([NotNullWhen(true)] UnityEngine.Object? o)
    {
        if (UMath.Shadow)
        {
            bool unity = Unity(o);
            if (Offset() > 0) UMath.Check("Obj.Alive", Exact(o), unity);
        }
        if (!Performance.Optimizing || Offset() < 0) return Unity(o) && o is not null;
        return Exact(o);
    }

    /// <summary>Unity's a == b: both null or destroyed, or the same object.</summary>
    internal static bool Same(UnityEngine.Object? a, UnityEngine.Object? b)
    {
        if (UMath.Shadow)
        {
            bool unity = a == b;
            if (Offset() > 0) UMath.Check("Obj.Same", ExactSame(a, b), unity);
        }
        if (!Performance.Optimizing || Offset() < 0) return a == b;
        return ExactSame(a, b);
    }

    /// <summary>Unity's o == null: o is null or has been destroyed.</summary>
    internal static bool IsNull([NotNullWhen(false)] UnityEngine.Object? o) => Same(o, null) || o is null;

    private static bool Unity(UnityEngine.Object? o) => o!;

    private static bool Exact([NotNullWhen(true)] UnityEngine.Object? o) =>
        o is not null && Offset() > 0 && Marshal.ReadIntPtr(o.Pointer, offset) != IntPtr.Zero;

    // CompareBaseObjects: both null are equal; two objects are equal when they're the same object;
    // one null equals the other only when that one has been destroyed.
    private static bool ExactSame(UnityEngine.Object? a, UnityEngine.Object? b)
    {
        if (a is null) return b is null || !Exact(b);
        if (b is null) return !Exact(a);
        return a.Pointer == b.Pointer;
    }

    private static int Offset()
    {
        if (offset != 0) return offset;
        offset = -1;
        try
        {
            IntPtr field = IL2CPP.GetIl2CppField(Il2CppClassPointerStore<UnityEngine.Object>.NativeClassPtr, "m_CachedPtr");
            // The interop's own m_CachedPtr accessor reads through this field.
            if (field == IntPtr.Zero)
                field = typeof(UnityEngine.Object).GetField("NativeFieldInfoPtr_m_CachedPtr", BindingFlags.NonPublic | BindingFlags.Static)
                    ?.GetValue(null) is IntPtr found ? found : IntPtr.Zero;
            if (field == IntPtr.Zero)
            {
                ModLog.Info("Exact object checks are off: UnityEngine.Object.m_CachedPtr wasn't found, so Unity's own checks are used.");
                return offset;
            }
            int at = (int)IL2CPP.il2cpp_field_get_offset(field);
            if (at != UnityReadsAt)
            {
                ModLog.Info($"Exact object checks are off: m_CachedPtr is at 0x{at:X}, not 0x{UnityReadsAt:X} where Unity's checks read it, so Unity's own checks are used.");
                return offset;
            }
            offset = at;
            ModLog.Info($"Exact object checks: m_CachedPtr is at 0x{at:X}, so Optimized reads it without calling into the game.");
        }
        catch (Exception ex)
        {
            ModLog.Error($"Exact object checks are off, so Unity's own checks are used: {ex}");
        }
        return offset;
    }
}
