using UnityEngine;

namespace NocturneFlatScroll;

internal static class FieldLayout
{
    // Apply to Field rather than FieldPivot: the shake manager owns the pivot's
    // cached position, and its Canvas sibling retains the game's animation paths.
    private static readonly Vector3 UpPosition = new(0f, 49.47874656f, 61.06627922f);
    private static readonly Vector3 DownPosition = new(0f, 4.26273263f, -1.16822486f);
    private static readonly Quaternion FlatRotation = Quaternion.Euler(54f, 0f, 0f);
    // The flat Field faces the combat camera, so its local Y axis is screen-up. Moving
    // along it changes only the receptors' height, never their depth or lane width.
    private static readonly Vector3 ScreenUp = FlatRotation * Vector3.up;
    // Field units per 1% of screen height at the combat camera's distance.
    internal const float UnitsPerPercent = 1.9527f;
    private static readonly Dictionary<int, FieldState> Fields = new();

    public static void Apply(CombatNoteFieldView view, ScrollMode mode)
    {
        if (mode == ScrollMode.Default)
        {
            // A mode change must also restore previously modified, inactive battles.
            // Subsequent Default frames perform no writes and leave native animation alone.
            foreach (var existing in Fields.Values) existing.Restore();
            return;
        }
        if (!view || !view.gameObject.activeInHierarchy) return;
        bool up = mode == ScrollMode.Upscroll2D;
        int id = view.GetInstanceID();
        if (!Fields.TryGetValue(id, out var state) || !state.Field)
        {
            var field = view.transform.Find("FieldPivot/Field");
            if (!field) return;
            state = new FieldState(field);
            Fields[id] = state;
            ModLog.Info("Registered combat field for flat scrolling.");
        }
        state.BeginModification();
        // A positive height moves the receptors in from their screen edge in both
        // directions: upward in downscroll and downward in upscroll.
        float shift = SettingsState.ReceptorHeight * UnitsPerPercent * (up ? -1f : 1f);
        state.Field.localPosition = (up ? UpPosition : DownPosition) + ScreenUp * shift;
        state.Field.localRotation = FlatRotation;
        state.Field.localScale = new Vector3(1f, up ? -1f : 1f, 1f);
        state.ApplyLanes();
        state.ApplyLabels(up);
    }

    /// <summary>
    /// Called while a 2D view is hidden between battles: gives the lanes back their animated
    /// positions, so the game's Animator never picks up a spread layout as its starting point.
    /// </summary>
    public static void Hidden(CombatNoteFieldView view)
    {
        if (Fields.TryGetValue(view.GetInstanceID(), out var state) && state.Field) state.ReleaseLanes();
    }

    private sealed class FieldState
    {
        public readonly Transform Field;
        private readonly int _fieldId;
        private readonly NativeTransformState _native;
        private readonly List<LabelState> _labels = new();
        private readonly List<LaneColumn> _lanes;
        private bool? _direction;
        private float _labelDrop;
        private bool _modified;
        public FieldState(Transform field)
        {
            Field = field;
            _fieldId = field.GetInstanceID();
            _native = new NativeTransformState(field);
            _lanes = LaneColumn.Collect(field);
            for (int i = 0; i < field.childCount; i++)
            {
                var labels = field.GetChild(i).Find("Labels");
                if (labels) _labels.Add(new LabelState(labels));
            }
        }

        /// <summary>Spreads the lanes around the field's centerline and sizes their receptors.</summary>
        public void ApplyLanes()
        {
            float size = SettingsState.NoteSize.Factor;
            float spacing = SettingsState.LaneSpacing.Factor;
            // The lane strips follow the notes but never grow past the spacing, so they
            // keep a gap between them.
            float width = Mathf.Min(size, spacing);
            foreach (var lane in _lanes)
            {
                if (!lane.IsAlive) continue;
                lane.Space(0f, spacing);
                lane.Size(size, width);
            }
            FlatFields.Set(_fieldId, true);
        }

        public void ReleaseLanes()
        {
            foreach (var lane in _lanes) lane.Unspace();
        }
        public void BeginModification()
        {
            if (_modified) return;
            // Native animations may have changed the layout since the last Default
            // period, so snapshot at entry to 2D rather than reusing an old baseline.
            _native.Capture();
            foreach (var label in _labels) label.Capture();
            _modified = true;
        }
        public void Restore()
        {
            if (!_modified) return;
            _native.Restore();
            foreach (var label in _labels) label.Restore();
            foreach (var lane in _lanes)
            {
                lane.Unspace();
                lane.Unsize();
            }
            FlatFields.Set(_fieldId, false);
            _direction = null;
            _modified = false;
        }
        public void ApplyLabels(bool up)
        {
            float drop = LaneColumn.LabelDrop(SettingsState.NoteSize.Factor);
            if (_direction == up && _labelDrop == drop) return;
            _direction = up;
            _labelDrop = drop;
            foreach (var label in _labels) label.Apply(up, drop);
        }
    }

    private sealed class LabelState
    {
        private readonly Transform _root;
        private readonly NativeTransformState _native;
        private readonly List<LabelChild> _children = new();
        public LabelState(Transform root)
        {
            _root = root;
            _native = new NativeTransformState(root);
            for (int i = 0; i < root.childCount; i++)
            {
                _children.Add(new LabelChild(root.GetChild(i)));
            }
        }
        public void Capture()
        {
            _native.Capture();
            foreach (var child in _children) child.Capture();
        }
        public void Restore()
        {
            _native.Restore();
            foreach (var child in _children) child.Restore();
        }
        public void Apply(bool up, float drop)
        {
            if (!_root) return;
            var scale = _native.Scale;
            _root.localScale = new Vector3(scale.x, up ? -scale.y : scale.y, scale.z);
            foreach (var child in _children) child.Apply(up, drop);
        }
    }

    private sealed class LabelChild
    {
        private readonly Transform _transform;
        private readonly RectTransform? _rect;
        private readonly NativeTransformState _native;
        // The sideways "disabled" label scrolls along the lane on its own and is not moved.
        private readonly bool _scrolls;

        public LabelChild(Transform transform)
        {
            _transform = transform;
            _rect = transform.TryCast<RectTransform>();
            _native = new NativeTransformState(transform);
            _scrolls = transform.name == "DisabledLabel";
        }

        public void Capture() => _native.Capture();
        public void Restore() => _native.Restore();

        /// <param name="drop">Extra distance from the receptor, for bigger or skinned receptors.</param>
        public void Apply(bool up, float drop)
        {
            if (!_transform) return;
            float direction = up ? -1f : 1f;
            if (_scrolls) drop = 0f;
            if (_rect)
            {
                // World-space key canvases and TMP labels use anchored coordinates. Their
                // serialized local Y can be zero until Unity has resolved the canvas layout.
                var anchoredPosition = _native.AnchoredPosition;
                _rect.anchoredPosition = new Vector2(anchoredPosition.x, (anchoredPosition.y - drop) * direction);
                var position = _rect.localPosition;
                position.z = _native.Position.z;
                _rect.localPosition = position;
            }
            else
            {
                var position = _native.Position;
                _transform.localPosition = new Vector3(position.x, (position.y - drop) * direction, position.z);
            }
        }
    }
}

/// <summary>A native baseline shared by combat and menu layouts; capturing never writes Unity state.</summary>
internal sealed class NativeTransformState
{
    private readonly Transform _transform;
    private readonly RectTransform? _rect;
    public Vector3 Position { get; private set; }
    public Quaternion Rotation { get; private set; }
    public Vector3 Scale { get; private set; }
    public Vector2 AnchoredPosition { get; private set; }
    private Vector3 _anchoredPosition3D;
    private Vector2 _anchorMin;
    private Vector2 _anchorMax;
    private Vector2 _pivot;
    private Vector2 _sizeDelta;

    public NativeTransformState(Transform transform)
    {
        _transform = transform;
        _rect = transform.TryCast<RectTransform>();
        Capture();
    }

    public void Capture()
    {
        if (!_transform) return;
        Position = _transform.localPosition;
        Rotation = _transform.localRotation;
        Scale = _transform.localScale;
        if (!_rect) return;
        AnchoredPosition = _rect.anchoredPosition;
        _anchoredPosition3D = _rect.anchoredPosition3D;
        _anchorMin = _rect.anchorMin;
        _anchorMax = _rect.anchorMax;
        _pivot = _rect.pivot;
        _sizeDelta = _rect.sizeDelta;
    }

    public void Restore()
    {
        if (!_transform) return;
        _transform.localPosition = Position;
        _transform.localRotation = Rotation;
        _transform.localScale = Scale;
        if (!_rect) return;
        _rect.anchorMin = _anchorMin;
        _rect.anchorMax = _anchorMax;
        _rect.pivot = _pivot;
        _rect.sizeDelta = _sizeDelta;
        // Restore anchors last so layout can resolve correctly after a window resize.
        _rect.anchoredPosition3D = _anchoredPosition3D;
    }
}
