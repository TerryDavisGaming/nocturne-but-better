using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// Note fields the mod currently lays out flat. Note size applies only to these, so the
/// 3D track and fields the mod leaves alone keep their original sizes.
/// </summary>
internal static class FlatFields
{
    private static readonly HashSet<int> Ids = new();

    /// <summary>Changes whenever a field is added or removed, so notes can be resized.</summary>
    internal static int Version { get; private set; }

    internal static void Set(int fieldId, bool flat)
    {
        if (flat ? Ids.Add(fieldId) : Ids.Remove(fieldId)) Version++;
    }

    /// <summary>The note size factor for a field: the setting when it is flat, otherwise 1.</summary>
    internal static float SizeFor(int fieldId) => Ids.Contains(fieldId) ? SettingsState.NoteSize.Factor : 1f;

    /// <summary>
    /// Half the height of a receptor in field units, for placing things beside it: the
    /// original bar is 11 tall, the circle and arrow skins about 23 across.
    /// </summary>
    internal static float ReceptorHalfHeight(NoteSkin skin, float size) =>
        (skin == NoteSkin.Default ? 5.5f : 11.7f) * size;
}

/// <summary>
/// One lane ("CombatColumn") of a flat note field. Spreads the lane by the spacing setting
/// and sizes its receptor and lane strip by the note size, always from the game's own
/// values, so turning the settings back (or leaving 2D) restores the lane exactly.
/// </summary>
internal sealed class LaneColumn
{
    private readonly Transform _column;
    // Receptor parts scale evenly around the lane's origin. "Center Dot" inside the decal is
    // left alone: the game tweens its scale on every key press, and it follows its parent.
    private readonly List<(Transform Part, Vector3 Native)> _receptor = new();
    // The lane strip and its dots keep their length and only change width.
    private readonly List<(Transform Part, Vector3 Native)> _strip = new();
    // The press beams start at the receptor's edge. Their height is tweened by the game
    // (HitGlowScale), so only their width and starting point are ever written.
    private readonly List<(Transform Part, Vector3 NativePosition, float NativeWidth)> _beams = new();

    private float _nativeX, _writtenX;
    private bool _spaced;
    private float _size = 1f, _width = 1f;
    private bool _sized;

    private LaneColumn(Transform column) => _column = column;

    internal bool IsAlive => _column;

    /// <summary>The lane's own x position, as the game last set it.</summary>
    internal float NativeX => _spaced ? _nativeX : _column.localPosition.x;

    internal static LaneColumn Create(Transform column)
    {
        var lane = new LaneColumn(column);
        foreach (var name in new[] { "Hitbox BG Border", "Hitbox Decal Default", "Hitbox Decal Disable", "HitGlow" })
        {
            var part = column.Find(name);
            if (part) lane._receptor.Add((part, part.localScale));
        }
        foreach (var name in new[] { "Background Top", "Background Bot", "DotsSortGroup" })
        {
            var part = column.Find(name);
            if (part) lane._strip.Add((part, part.localScale));
        }
        foreach (var name in new[] { "HitGlowColumnTop", "HitGlowColumnBot" })
        {
            var part = column.Find(name);
            if (part) lane._beams.Add((part, part.localPosition, part.localScale.x));
        }
        return lane;
    }

    /// <summary>
    /// Moves the lane to pivot + (x - pivot) * spacing. The game animates lane positions
    /// (battle intros, four-to-five lane changes, chart events), so this runs every frame
    /// and treats any x it did not write itself as the game's new position.
    /// </summary>
    internal void Space(float pivot, float spacing)
    {
        var position = _column.localPosition;
        if (!_spaced || position.x != _writtenX) _nativeX = position.x;
        float x = pivot + (_nativeX - pivot) * spacing;
        if (position.x != x)
        {
            position.x = x;
            _column.localPosition = position;
        }
        _writtenX = x;
        _spaced = true;
    }

    /// <summary>Puts the game's lane position back, unless the game has moved the lane since.</summary>
    internal void Unspace()
    {
        if (!_spaced) return;
        _spaced = false;
        if (!_column) return;
        var position = _column.localPosition;
        if (position.x != _writtenX || position.x == _nativeX) return;
        position.x = _nativeX;
        _column.localPosition = position;
    }

    /// <summary>Sizes the receptor by <paramref name="size"/> and the lane strip's width by <paramref name="width"/>.</summary>
    internal void Size(float size, float width)
    {
        if (_sized && size == _size && width == _width) return;
        _size = size;
        _width = width;
        _sized = true;
        foreach (var (part, native) in _receptor)
            if (part) part.localScale = native * size;
        foreach (var (part, native) in _strip)
            if (part) part.localScale = new Vector3(native.x * width, native.y, native.z);
        foreach (var (part, nativePosition, nativeWidth) in _beams)
        {
            if (!part) continue;
            part.localPosition = new Vector3(nativePosition.x, nativePosition.y * size, nativePosition.z);
            var scale = part.localScale;
            part.localScale = new Vector3(nativeWidth * width, scale.y, scale.z);
        }
    }

    internal void Unsize()
    {
        if (!_sized) return;
        _sized = false;
        foreach (var (part, native) in _receptor)
            if (part) part.localScale = native;
        foreach (var (part, native) in _strip)
            if (part) part.localScale = native;
        foreach (var (part, nativePosition, nativeWidth) in _beams)
        {
            if (!part) continue;
            part.localPosition = nativePosition;
            var scale = part.localScale;
            part.localScale = new Vector3(nativeWidth, scale.y, scale.z);
        }
    }

    /// <summary>The lanes of a note field, in child order.</summary>
    internal static List<LaneColumn> Collect(Transform field)
    {
        var lanes = new List<LaneColumn>();
        for (int i = 0; i < field.childCount; i++)
        {
            var child = field.GetChild(i);
            if (child.name.StartsWith("CombatColumn", StringComparison.Ordinal)) lanes.Add(Create(child));
        }
        return lanes;
    }

    /// <summary>
    /// How far the key and AUTO labels move away from the receptor so a bigger receptor, or a
    /// skinned one, does not cover them. Zero for the original bar at its original size.
    /// </summary>
    internal static float LabelDrop(float size) =>
        FlatFields.ReceptorHalfHeight(SettingsState.NoteSkin, size) - FlatFields.ReceptorHalfHeight(NoteSkin.Default, 1f);
}
