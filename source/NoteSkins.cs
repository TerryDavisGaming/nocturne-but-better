using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>
/// Draws notes and receptors as circles or arrows. The game's own bar shapes stay in place
/// and are only disabled, so switching back to Default restores them exactly.
/// </summary>
internal static class NoteSkins
{
    // World units covered by one note texture: circles come out 19 units across, arrows
    // just under that, so they sit inside the 22-unit lane like the 28-unit bars overhang it.
    private const float NoteScale = 20.3f;
    private const float ReceptorScale = 24f;
    // Hold bodies are narrowed to suit the smaller heads.
    private const float HoldWidthFactor = 0.7f;
    private const string NoteRootName = "FlatScrollSkin";
    private const string ReceptorRootName = "FlatScrollReceptor";

    private static readonly Dictionary<int, SkinnedNote> Notes = new();
    private static readonly Dictionary<int, SkinnedField> Fields = new();
    private static readonly List<int> Dead = new();
    private static int generation;
    private static int appliedGeneration;
    private static NoteSkin lastSkin;
    private static ScrollMode lastMode;
    private static int lastSize = -1;
    private static int lastFlatVersion = -1;
    private static float nextPrune;
    private static Material? spriteMaterial;
    private static float nextMaterialSearch;

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(NocturneCombatNoteView), "SetNote")
                ?? throw new MissingMethodException(typeof(NocturneCombatNoteView).FullName, "SetNote"),
            postfix: new HarmonyMethod(typeof(NoteSkins), nameof(SetNotePostfix)));
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(NocturneCombatNoteView), "SetColors", new[] { typeof(CombatNoteColorSet) })
                ?? throw new MissingMethodException(typeof(NocturneCombatNoteView).FullName, "SetColors"),
            postfix: new HarmonyMethod(typeof(NoteSkins), nameof(SetColorsPostfix)));
        // Hit flares are sized with the notes, or hidden when note flares are off. The game
        // resets a flare's scale when it plays a tap flare, but not a hold flare.
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(NocturneCombatNoteColumnBehaviour), "NoteFlareSingle")
                ?? throw new MissingMethodException(typeof(NocturneCombatNoteColumnBehaviour).FullName, "NoteFlareSingle"),
            postfix: new HarmonyMethod(typeof(NoteSkins), nameof(FlareSinglePostfix)));
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(NocturneCombatNoteColumnBehaviour), "NoteFlareHold")
                ?? throw new MissingMethodException(typeof(NocturneCombatNoteColumnBehaviour).FullName, "NoteFlareHold"),
            postfix: new HarmonyMethod(typeof(NoteSkins), nameof(FlareHoldPostfix)));
    }

    // Mine explosions stay: they warn of a mine hit, and their animation plays the mine sound.
    // The calibration screen keeps its flares too.
    private static void FlareSinglePostfix(NocturneCombatNoteColumnBehaviour __instance, bool isCalibration, bool isMine)
    {
        try
        {
            SizeFlare(__instance, isMine ? __instance.mineFlarePool : __instance.noteFlarePool,
                hide: !isMine && !isCalibration && !SettingsState.NoteFlares);
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    private static void FlareHoldPostfix(NocturneCombatNoteColumnBehaviour __instance)
    {
        try { SizeFlare(__instance, __instance.noteFlarePool, hide: !SettingsState.NoteFlares); }
        catch (Exception ex) { ReportOnce(ex); }
    }

    /// <summary>
    /// Shows or hides the hold flares already on screen, so switching note flares in the
    /// pause menu applies to a hold that is still held.
    /// </summary>
    internal static void RefreshFlares()
    {
        try
        {
            foreach (var column in Resources.FindObjectsOfTypeAll<NocturneCombatNoteColumnBehaviour>())
            {
                if (column && column.gameObject.scene.handle != 0)
                    SizeFlare(column, column.noteFlarePool, hide: !SettingsState.NoteFlares);
            }
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    // Scaling to zero hides a flare without changing the game's state: its animation still
    // runs and returns it to the pool as usual.
    private static void SizeFlare(NocturneCombatNoteColumnBehaviour column, NoteFlarePool? pool, bool hide)
    {
        if (!column || pool == null) return;
        var flare = pool.LastNoteFlare;
        if (!flare) return;
        if (hide)
        {
            flare.transform.localScale = Vector3.zero;
            return;
        }
        var field = column.transform.parent;
        float size = field ? FlatFields.SizeFor(field.GetInstanceID()) : 1f;
        flare.transform.localScale = new Vector3(size, size, 1f);
    }

    private static void SetNotePostfix(NocturneCombatNoteView __instance, int noteColumn, int columnCount)
    {
        try
        {
            if (!__instance) return;
            var note = Track(__instance);
            note.Column = noteColumn;
            note.ColumnCount = columnCount;
            note.Apply();
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    private static void SetColorsPostfix(NocturneCombatNoteView __instance, CombatNoteColorSet colors)
    {
        try
        {
            if (!__instance) return;
            Track(__instance).SetColors(colors.color1, colors.color2, colors.color3);
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    /// <summary>
    /// Reapplies skins after a setting change and keeps receptor colors in step. A field's
    /// lane count of zero means "count the active lanes".
    /// </summary>
    internal static void LateUpdate(List<(Transform Field, int Columns)> fields)
    {
        var skin = SettingsState.NoteSkin;
        var mode = SettingsState.Mode;
        int size = SettingsState.NoteSize.Value;
        if (skin != lastSkin || mode != lastMode || size != lastSize || FlatFields.Version != lastFlatVersion)
        {
            // A scroll mode change can mirror the field, which swaps up and down arrows, and
            // decides which fields use the note size.
            lastSkin = skin;
            lastMode = mode;
            lastSize = size;
            lastFlatVersion = FlatFields.Version;
            generation++;
        }

        foreach (var (field, columns) in fields)
        {
            if (!field) continue;
            int id = field.GetInstanceID();
            if (!Fields.TryGetValue(id, out var state) || !state.IsAlive)
            {
                if (skin == NoteSkin.Default) continue;
                state = new SkinnedField(field);
                Fields[id] = state;
            }
            // A field that turns mirrored after its notes were set up swaps their arrows too.
            if (state.Update(skin, columns)) generation++;
        }

        if (appliedGeneration != generation)
        {
            appliedGeneration = generation;
            foreach (var note in Notes.Values) note.Apply();
        }

        float now = Time.unscaledTime;
        if (now < nextPrune) return;
        nextPrune = now + 1f;
        // Pooled notes and fields from finished battles and closed menus.
        Dead.Clear();
        foreach (var pair in Notes) if (!pair.Value.IsAlive) Dead.Add(pair.Key);
        foreach (var id in Dead) Notes.Remove(id);
        Dead.Clear();
        foreach (var pair in Fields) if (!pair.Value.IsAlive) Dead.Add(pair.Key);
        foreach (var id in Dead) Fields.Remove(id);
    }

    private static SkinnedNote Track(NocturneCombatNoteView view)
    {
        int id = view.GetInstanceID();
        if (!Notes.TryGetValue(id, out var note) || !note.IsAlive)
        {
            note = new SkinnedNote(view);
            Notes[id] = note;
        }
        return note;
    }

    private static int ColumnIndex(string name, int fallback)
    {
        int open = name.IndexOf('('), close = name.IndexOf(')');
        return open >= 0 && close > open && int.TryParse(name.Substring(open + 1, close - open - 1), out int index)
            ? index : fallback;
    }

    /// <summary>Picks each lane's shape: four lanes are left, down, up, right; five add a middle diamond.</summary>
    internal static SkinArt.Shape ShapeFor(NoteSkin skin, int column, int columnCount)
    {
        if (skin == NoteSkin.Circle) return SkinArt.Shape.Circle;
        return columnCount % 2 == 1 && column == columnCount / 2 ? SkinArt.Shape.Diamond : SkinArt.Shape.Arrow;
    }

    /// <summary>Rotation of an up-pointing arrow for a lane; mirrored fields swap up and down.</summary>
    internal static float AngleFor(NoteSkin skin, int column, int columnCount, bool mirrored)
    {
        if (skin != NoteSkin.Arrow) return 0f;
        if (column <= 0) return 90f;
        if (column >= columnCount - 1) return -90f;
        int inner = columnCount % 2 == 1 && column > columnCount / 2 ? column - 1 : column;
        bool down = inner % 2 == 1;
        return down != mirrored ? 180f : 0f;
    }

    /// <summary>True when the transform's space is mirrored, as the flipped upscroll field is.</summary>
    private static bool IsMirrored(Transform transform)
    {
        var right = transform.TransformVector(Vector3.right);
        var up = transform.TransformVector(Vector3.up);
        var forward = transform.TransformVector(Vector3.forward);
        // Unity is left-handed: right x up points forward unless the space is mirrored.
        return Vector3.Dot(Vector3.Cross(right, up), forward) < 0f;
    }

    /// <summary>The game's unlit combat sprite material, so new sprites render like its own.</summary>
    internal static Material? SharedSpriteMaterial()
    {
        if (spriteMaterial) return spriteMaterial;
        // Scanning every material is slow, so after a miss look again at most once a second.
        if (Time.unscaledTime < nextMaterialSearch) return null;
        nextMaterialSearch = Time.unscaledTime + 1f;
        foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
        {
            if (material && material.name == "Combat-Sprite-Unlit")
            {
                spriteMaterial = material;
                break;
            }
        }
        return spriteMaterial;
    }

    private static SpriteRenderer AddLayer(Transform parent, string name, int sortingLayer, int order)
    {
        var go = new GameObject(name);
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        var renderer = go.AddComponent<SpriteRenderer>();
        var material = SharedSpriteMaterial();
        if (material) renderer.sharedMaterial = material;
        renderer.sortingLayerID = sortingLayer;
        renderer.sortingOrder = order;
        return renderer;
    }

    private static bool reportedError;

    private static void ReportOnce(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        ModLog.Error("Note skin failed: " + ex);
    }

    private sealed class SkinnedNote
    {
        private readonly NocturneCombatNoteView _view;
        private readonly Transform? _tap;
        private readonly List<ShapeRenderer> _native = new();
        private GameObject? _root;
        private SpriteRenderer? _body, _glyph, _accent;
        private Color _c1 = Color.white, _c2 = Color.white, _c3 = Color.white;
        private bool _hidingNative;
        // The note's parts with the game's own sizes, taken before the mod changes anything.
        // Every resize writes absolute values from these, so Default restores them exactly.
        private readonly Transform? _mine, _fake, _end, _lineStart, _mask;
        private readonly LineRenderer? _background, _pattern;
        private readonly Vector3 _tapScale, _mineScale, _fakeScale, _endScale, _lineStartScale;
        private readonly Vector3 _maskPosition, _maskScale, _patternOffset, _lineStartOffset;
        private readonly float _backgroundWidth, _patternWidth;
        private float _size = 1f, _narrow = 1f;
        internal int Column = -1;
        internal int ColumnCount = 4;

        internal SkinnedNote(NocturneCombatNoteView view)
        {
            _view = view;
            var tap = view.tapNoteView;
            _tap = tap ? tap.transform : null;
            if (_tap)
            {
                _tapScale = _tap!.localScale;
                foreach (var shape in _tap.GetComponentsInChildren<ShapeRenderer>(true))
                    _native.Add(shape);
            }
            _mine = TransformOf(view.mineNoteView);
            _fake = TransformOf(view.fakeNoteView);
            _end = TransformOf(view.endNoteView);
            _mask = TransformOf(view.noteMask);
            var lineStart = view.lineStartSpriteRenderer;
            _lineStart = lineStart ? lineStart.transform : null;
            if (_mine) _mineScale = _mine!.localScale;
            if (_fake) _fakeScale = _fake!.localScale;
            if (_end) _endScale = _end!.localScale;
            if (_lineStart) _lineStartScale = _lineStart!.localScale;
            if (_mask)
            {
                _maskPosition = _mask!.localPosition;
                _maskScale = _mask.localScale;
            }
            _background = view.backgroundLineRenderer;
            _pattern = view.patternLineRenderer;
            if (_background) _backgroundWidth = _background!.widthMultiplier;
            if (_pattern) _patternWidth = _pattern!.widthMultiplier;
            _patternOffset = view.patternLineOffset;
            _lineStartOffset = view.lineStartSpriteOffset;
        }

        private static Transform? TransformOf(GameObject? part) => part ? part!.transform : null;

        internal bool IsAlive => _view && _tap;

        internal void SetColors(Color c1, Color c2, Color c3)
        {
            _c1 = c1;
            _c2 = c2;
            _c3 = c3;
            if (!_body) return;
            _body!.color = c1;
            _glyph!.color = c3;
            _accent!.color = c2;
        }

        internal void Apply()
        {
            if (!IsAlive || Column < 0) return;
            var skin = SettingsState.NoteSkin;
            Resize(FlatFields.SizeFor(FieldId()), skin == NoteSkin.Default ? 1f : HoldWidthFactor);
            if (skin == NoteSkin.Default)
            {
                if (_root) _root!.SetActive(false);
                SetNativeVisible(true);
                return;
            }

            if (!_root) Build();
            var shape = ShapeFor(skin, Column, ColumnCount);
            _body!.sprite = SkinSprites.NoteBody(shape);
            _glyph!.sprite = SkinSprites.NoteGlyph(shape);
            _accent!.sprite = SkinSprites.NoteAccent(shape);
            var root = _root!.transform;
            root.localRotation = Quaternion.Euler(0f, 0f, AngleFor(skin, Column, ColumnCount, IsMirrored(_tap!)));
            _root.SetActive(true);
            SetNativeVisible(false);
        }

        /// <summary>The note field this note is in: note, NoteParent, lane, field.</summary>
        private int FieldId()
        {
            var parent = _view.transform.parent;
            var lane = parent ? parent!.parent : null;
            var field = lane ? lane!.parent : null;
            return field ? field!.GetInstanceID() : 0;
        }

        /// <summary>
        /// Scales the note by <paramref name="size"/> and its hold body by size times
        /// <paramref name="narrow"/>. Never touches the note's own transform or where the
        /// game places it, so timing and scroll distance stay the same.
        /// </summary>
        private void Resize(float size, float narrow)
        {
            if (size == _size && narrow == _narrow) return;
            _size = size;
            _narrow = narrow;
            _tap!.localScale = _tapScale * size;
            if (_mine) _mine!.localScale = _mineScale * size;
            if (_fake) _fake!.localScale = _fakeScale * size;
            float width = size * narrow;
            if (_end) _end!.localScale = new Vector3(_endScale.x * width, _endScale.y * size, _endScale.z);
            if (_lineStart) _lineStart!.localScale = new Vector3(_lineStartScale.x * width, _lineStartScale.y * size, _lineStartScale.z);
            if (_background) _background!.widthMultiplier = _backgroundWidth * width;
            if (_pattern) _pattern!.widthMultiplier = _patternWidth * width;
            // The hold's pattern and start cap sit a fixed distance from the tail; keep them
            // against the resized end cap.
            _view.patternLineOffset = _patternOffset * size;
            _view.lineStartSpriteOffset = _lineStartOffset * size;
            if (_mask)
            {
                // The mask's local Z points along the lane (it is turned 90 degrees).
                _mask!.localPosition = new Vector3(_maskPosition.x * size, _maskPosition.y * size, _maskPosition.z);
                _mask.localScale = new Vector3(_maskScale.x * size, _maskScale.y, _maskScale.z * size);
            }
        }

        private void Build()
        {
            _root = new GameObject(NoteRootName);
            _root.layer = _tap!.gameObject.layer;
            var root = _root.transform;
            root.SetParent(_tap, false);
            root.localScale = Vector3.one * NoteScale;
            // Keep the game's sorting: the note body is order 6, its markings order 7.
            int layer = _native.Count > 0 && _native[0] ? _native[0].SortingLayerID : 0;
            _body = AddLayer(root, "Body", layer, 6);
            _glyph = AddLayer(root, "Glyph", layer, 7);
            _accent = AddLayer(root, "Accent", layer, 8);
            SetColors(_c1, _c2, _c3);
        }

        private void SetNativeVisible(bool visible)
        {
            if (_hidingNative == !visible) return;
            _hidingNative = !visible;
            // Disabling the component (not just its MeshRenderer) survives the game
            // toggling the note's GameObjects, because ShapeRenderer re-enables in OnEnable.
            foreach (var shape in _native)
                if (shape) shape.enabled = visible;
        }
    }

    /// <summary>One note field's receptors, found by a scan that runs about once a second.</summary>
    private sealed class SkinnedField
    {
        private readonly Transform _field;
        private readonly int _fieldId;
        private readonly List<(int ColumnId, SkinnedReceptor Receptor, int Index)> _receptors = new();
        private int _activeColumns;
        private float _nextScan;
        private NoteSkin _scannedSkin;
        private bool? _mirrored;

        internal SkinnedField(Transform field)
        {
            _field = field;
            _fieldId = field.GetInstanceID();
        }

        internal bool IsAlive => _field;

        /// <summary>Updates the receptors; returns true when the field's mirroring changed.</summary>
        internal bool Update(NoteSkin skin, int columns)
        {
            float now = Time.unscaledTime;
            if (now >= _nextScan || skin != _scannedSkin)
            {
                _nextScan = now + 1f;
                _scannedSkin = skin;
                Scan(skin);
            }
            if (columns <= 0) columns = _activeColumns > 0 ? _activeColumns : 4;
            bool mirrored = IsMirrored(_field);
            bool flipped = _mirrored.HasValue && _mirrored.Value != mirrored;
            _mirrored = mirrored;
            float size = FlatFields.SizeFor(_fieldId);
            foreach (var (_, receptor, index) in _receptors) receptor.Update(skin, index, columns, mirrored, size);
            return flipped;
        }

        private void Scan(NoteSkin skin)
        {
            for (int i = _receptors.Count - 1; i >= 0; i--)
                if (!_receptors[i].Receptor.IsAlive) _receptors.RemoveAt(i);
            _activeColumns = 0;
            for (int i = 0; i < _field.childCount; i++)
            {
                var column = _field.GetChild(i);
                var name = column.name;
                if (!name.StartsWith("CombatColumn", StringComparison.Ordinal)) continue;
                if (column.gameObject.activeSelf) _activeColumns++;
                int id = column.GetInstanceID();
                if (skin == NoteSkin.Default || Tracks(id)) continue;
                var receptor = SkinnedReceptor.Create(column);
                if (receptor != null) _receptors.Add((id, receptor, ColumnIndex(name, i)));
            }
        }

        private bool Tracks(int columnId)
        {
            foreach (var entry in _receptors)
                if (entry.ColumnId == columnId) return true;
            return false;
        }
    }

    private sealed class SkinnedReceptor
    {
        private readonly Transform _column;
        private readonly ShapeRenderer? _fill, _border, _decal;
        private readonly SpriteRenderer? _hitGlow;
        private readonly List<ShapeRenderer> _native = new();
        private GameObject? _root, _glowRoot;
        private SpriteRenderer? _face, _edge, _glyph, _glow;
        private bool _hidingNative;
        // What the skinned parts show now, so sprites and rotation change only when needed.
        private bool _shown;
        private SkinArt.Shape _shape;
        private float _angle;
        private float _size = -1f;

        private SkinnedReceptor(Transform column, ShapeRenderer? fill, ShapeRenderer? border, ShapeRenderer? decal)
        {
            _column = column;
            _fill = fill;
            _border = border;
            _decal = decal;
            var glow = column.Find("HitGlow");
            _hitGlow = glow ? glow.GetComponent<SpriteRenderer>() : null;
        }

        internal static SkinnedReceptor? Create(Transform column)
        {
            var borderT = column.Find("Hitbox BG Border");
            var decalT = column.Find("Hitbox Decal Default");
            if (!borderT || !decalT) return null;
            var fillT = borderT.Find("Hitbox BG Fill");
            var receptor = new SkinnedReceptor(column,
                fillT ? fillT.GetComponent<ShapeRenderer>() : null,
                borderT.GetComponent<ShapeRenderer>(),
                decalT.Find("Center") is { } center && center ? center.GetComponent<ShapeRenderer>() : null);
            if (receptor._fill) receptor._native.Add(receptor._fill!);
            if (receptor._border) receptor._native.Add(receptor._border!);
            // The held-key dot stays: it lights the open center of the skinned receptor.
            foreach (var shape in decalT.GetComponentsInChildren<ShapeRenderer>(true))
                if (shape && shape.gameObject.name != "Center Dot") receptor._native.Add(shape);
            return receptor;
        }

        internal bool IsAlive => _column;

        internal void Update(NoteSkin skin, int column, int columnCount, bool mirrored, float size)
        {
            if (skin == NoteSkin.Default)
            {
                if (_shown)
                {
                    if (_root) _root!.SetActive(false);
                    if (_glowRoot) _glowRoot!.SetActive(false);
                    _shown = false;
                }
                SetNativeVisible(true);
                return;
            }
            if (!_root || !_glowRoot)
            {
                Build();
                _shown = false;
                _size = -1f;
            }
            if (size != _size)
            {
                _size = size;
                _root!.transform.localScale = Vector3.one * (ReceptorScale * size);
                _glowRoot!.transform.localScale = Vector3.one * (ReceptorScale * size * SkinArt.GlowSize / SkinArt.NoteSize);
            }
            var shape = ShapeFor(skin, column, columnCount);
            float angle = AngleFor(skin, column, columnCount, mirrored);
            if (!_shown || shape != _shape || angle != _angle)
            {
                _face!.sprite = SkinSprites.ReceptorFace(shape);
                _edge!.sprite = SkinSprites.ReceptorBorder(shape);
                _glyph!.sprite = SkinSprites.ReceptorGlyph(shape);
                _glow!.sprite = SkinSprites.ReceptorGlow(shape);
                var rotation = Quaternion.Euler(0f, 0f, angle);
                _root!.transform.localRotation = rotation;
                _glowRoot!.transform.localRotation = rotation;
                _root.SetActive(true);
                _glowRoot.SetActive(true);
                SetNativeVisible(false);
                _shown = true;
                _shape = shape;
                _angle = angle;
            }
            // The game flashes its glow by animating the color; mirror that on the shaped glow.
            if (_hitGlow) _glow!.color = _hitGlow!.color;
            // The game recolors receptors for column states; follow its hidden shapes.
            if (_fill) _face!.color = _fill!.Color;
            if (_border) _edge!.color = _border!.Color;
            if (_decal) _glyph!.color = _decal!.Color;
        }

        private void Build()
        {
            _root = new GameObject(ReceptorRootName);
            _root.layer = _column.gameObject.layer;
            var root = _root.transform;
            root.SetParent(_column, false);
            root.localScale = Vector3.one * ReceptorScale;
            int layer = _border ? _border!.SortingLayerID : 0;
            _face = AddLayer(root, "Face", layer, -1);
            _glyph = AddLayer(root, "Glyph", layer, 1);
            _edge = AddLayer(root, "Border", layer, 2);
            // The glow texture has extra room around the shape, so it is scaled up to match.
            _glowRoot = new GameObject(ReceptorRootName + "Glow");
            _glowRoot.layer = _column.gameObject.layer;
            var glowRoot = _glowRoot.transform;
            glowRoot.SetParent(_column, false);
            glowRoot.localScale = Vector3.one * (ReceptorScale * SkinArt.GlowSize / SkinArt.NoteSize);
            int glowLayer = _hitGlow ? _hitGlow!.sortingLayerID : layer;
            int glowOrder = _hitGlow ? _hitGlow!.sortingOrder : 5;
            _glow = AddLayer(glowRoot, "Glow", glowLayer, glowOrder);
            if (_hitGlow && _hitGlow!.sharedMaterial) _glow.sharedMaterial = _hitGlow.sharedMaterial;
        }

        private void SetNativeVisible(bool visible)
        {
            if (_hidingNative == !visible) return;
            _hidingNative = !visible;
            foreach (var shape in _native)
                if (shape) shape.enabled = visible;
            if (_hitGlow) _hitGlow!.enabled = visible;
        }
    }
}
