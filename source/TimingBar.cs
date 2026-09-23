using HarmonyLib;
using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// An early/late ("fast/slow") bar in the style of osu!'s hit error meter. It lives in the
/// note field just behind the receptors, so it follows each scroll layout, the receptor
/// height, and the default perspective track. Early hits are on the rabbit's side (left).
/// </summary>
internal static class TimingBar
{
    // The game's hit zones (GameConfig): early and late limits in seconds, and colors.
    private static readonly (double Early, double Late, Color Color)[] Zones =
    {
        (-0.300, 0.102, Hex(0xE54728)), // 0 miss
        (-0.230, 0.101, Hex(0xFFD869)), // 1 bad
        (-0.170, 0.100, Hex(0xFCFFF5)), // 2 okay
        (-0.110, 0.075, Hex(0x59CDEE)), // 3 good
        (-0.070, 0.050, Hex(0x49D7B4)), // 4 great
        (-0.030, 0.025, Hex(0x65E974)), // 5 perfect
    };
    private const double Range = 0.17;          // seconds shown at each end of the bar
    private const int MaxTicks = 32;
    private const float TickLife = 4f;           // seconds a tick stays visible
    private const float Gap = 27f;               // field units from the receptors to the bar
    private const float TrackHeight = 2.2f;
    private const float BandHeight = 1.4f;
    private const float IconSize = 8f;
    private static readonly Color TrackColor = new(0.067f, 0.063f, 0.106f, 0.85f);
    private static readonly Color IconColor = new(0.616f, 0.573f, 0.706f, 1f);

    private static readonly List<(double Offset, Color Color, float Time)> Hits = new();
    private static double average;
    private static bool hasAverage;
    private static readonly Dictionary<int, Bar> Bars = new();
    private static readonly HashSet<int> Failed = new();
    private static readonly List<int> Dead = new();
    private static float nextPrune;

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(CombatManagerV3), "OnNoteJudged")
                ?? throw new MissingMethodException(typeof(CombatManagerV3).FullName, "OnNoteJudged"),
            postfix: new HarmonyMethod(typeof(TimingBar), nameof(OnNoteJudgedPostfix)));
    }

    /// <summary>Records each tap and hold start that the player hit.</summary>
    private static void OnNoteJudgedPostfix(CombatManagerV3 __instance, TapNote note, CombatNoteData data)
    {
        try
        {
            if (!SettingsState.TimingBar || __instance == null || data == null) return;
            // End-of-fight auto judging, replays and auto-played columns are not the player's timing.
            if (__instance.combatResultDecided) return;
            var player = __instance.combatPlayerState;
            if (player != null && player.ScriptedInput) return;
            if (data.ResultSource != TapResultSource.Tap) return;
            // Hold releases stay off the bar. Holding past the end passes by itself, so a
            // release is never late, and letting go early reports the rest of the hold's
            // length: both would only drag the average early.
            if (note.type == TapNoteType.HoldHead && data.HoldResult != HoldResult.Invalid) return;
            // Like osu!, misses stay off the bar too.
            var zone = data.HitZone;
            if (zone == null || zone.index <= 0 || zone.isAuto) return;
            Record(data.HitOffset, zone.textColor);
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    private static void Record(double offset, Color color)
    {
        if (double.IsNaN(offset) || double.IsInfinity(offset)) return;
        Hits.Add((offset, color, Time.unscaledTime));
        if (Hits.Count > MaxTicks) Hits.RemoveAt(0);
        // The marker follows an exponential average, like osu!'s moving-average arrow.
        average = hasAverage ? average + (offset - average) * 0.15 : offset;
        hasAverage = true;
    }

    /// <summary>QA-only entry point for synthetic hits.</summary>
    internal static void RecordForQa(double offset)
    {
        var color = Zones[0].Color;
        for (int i = Zones.Length - 1; i >= 0; i--)
            if (offset >= Zones[i].Early && offset <= Zones[i].Late) { color = Zones[i].Color; break; }
        Record(offset, color);
    }

    /// <summary>Lays out the bar for one note field; call after the field's own layout.</summary>
    internal static void LateUpdate(CombatNoteFieldView view, Transform field, Camera? camera)
    {
        int id = view.GetInstanceID();
        bool show = SettingsState.TimingBar && view.gameObject.activeInHierarchy;
        if (!Bars.TryGetValue(id, out var bar) || !bar.IsAlive)
        {
            if (!show || Failed.Contains(id)) return;
            try { bar = new Bar(field); }
            catch
            {
                // Try again on the next battle's field, not on every frame of this one.
                Failed.Add(id);
                throw;
            }
            Bars[id] = bar;
        }
        if (show) bar.Update(camera);
        else bar.Hide();

        float now = Time.unscaledTime;
        if (now < nextPrune) return;
        nextPrune = now + 1f;
        // Bars of finished battles.
        Dead.Clear();
        foreach (var pair in Bars) if (!pair.Value.IsAlive) Dead.Add(pair.Key);
        foreach (var dead in Dead) Bars.Remove(dead);
    }

    private static Color Hex(int rgb) =>
        new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);

    private static bool reportedError;

    private static void ReportOnce(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        ModLog.Error("Timing bar failed: " + ex);
    }

    private sealed class Bar
    {
        private readonly Transform _field;
        private readonly GameObject _root;
        private readonly Transform _rootTransform;
        private readonly SpriteRenderer _track, _center, _marker, _rabbit, _turtle;
        private readonly SpriteRenderer[] _bands;
        private readonly SpriteRenderer[] _ticks = new SpriteRenderer[MaxTicks];
        private readonly List<SpriteRenderer> _renderers = new();
        // Transforms are fetched once: each fetch through the interop makes a new wrapper.
        private readonly Transform[] _bandTransforms = new Transform[4];
        private readonly Transform[] _tickTransforms = new Transform[MaxTicks];
        private readonly bool[] _tickShown = new bool[MaxTicks];
        private readonly Transform _markerTransform, _rabbitTransform, _turtleTransform;
        // Behind the receptors the bar draws over notes that slid past after a miss; in front
        // of them it draws under the notes that are still coming in.
        private readonly int _behindLayer, _frontLayer;
        private bool? _inFront;
        private bool _markerShown;
        private float _markerX;
        private bool _wasHidden = true;
        // The lanes in use, rescanned a few times a second.
        private float _nextScan;
        private bool _hasLanes;
        private float _minX, _maxX, _receptorY, _depth;
        // The last layout written, so unchanged frames skip the writes.
        private bool _placedOnce;
        private Vector3 _placed;
        private float _placedHalf;
        private bool _placedMirrored;

        internal Bar(Transform field)
        {
            _field = field;
            _root = new GameObject("FlatScrollTimingBar");
            _root.layer = field.gameObject.layer;
            try
            {
                _rootTransform = _root.transform;
                _rootTransform.SetParent(field, false);
                _frontLayer = ReceptorLayerOf(field);
                // The notes' layer puts misses that slide past under the bar. (The fighters
                // come from a second camera drawn over the whole field, so they still cover it.)
                int notes = SortingLayer.NameToID("CombatNotes");
                _behindLayer = notes != 0 ? notes : _frontLayer;
                _track = Layer("Track", 20, SkinSprites.Pill(TrackHeight), true);
                // Widest zone first so the narrower, stricter zones draw on top.
                _bands = new SpriteRenderer[4];
                for (int i = 0; i < 4; i++)
                {
                    _bands[i] = Layer("Zone" + (i + 2), 21 + i, SkinSprites.Pill(BandHeight), true);
                    _bandTransforms[i] = _bands[i].transform;
                    var c = Zones[i + 2].Color;
                    _bands[i].color = new Color(c.r, c.g, c.b, 0.85f);
                }
                _center = Layer("Center", 26, SkinSprites.Tick);
                Place(_center.transform, 0f, 0.45f, 4.6f);
                for (int i = 0; i < MaxTicks; i++)
                {
                    _ticks[i] = Layer("Tick", 27, SkinSprites.Tick);
                    _ticks[i].enabled = false;
                    _tickTransforms[i] = _ticks[i].transform;
                }
                _marker = Layer("Average", 28, SkinSprites.Marker);
                _marker.enabled = false;
                _markerTransform = _marker.transform;
                // It sits on the far side of the bar and points at it.
                _markerTransform.localRotation = Quaternion.Euler(0f, 0f, 180f);
                _markerTransform.localScale = Vector3.one * 3.2f;
                _rabbit = Layer("Early", 28, SkinSprites.Rabbit);
                _turtle = Layer("Late", 28, SkinSprites.Turtle);
                _rabbitTransform = _rabbit.transform;
                _turtleTransform = _turtle.transform;
                _track.color = TrackColor;
                _center.color = Color.white;
                _marker.color = Color.white;
                _rabbit.color = IconColor;
                _turtle.color = IconColor;
            }
            catch
            {
                // Leave nothing half built under the field.
                UnityEngine.Object.Destroy(_root);
                throw;
            }
        }

        internal bool IsAlive => _field && _root;

        internal void Hide()
        {
            if (_wasHidden) return;
            _root.SetActive(false);
            _wasHidden = true;
        }

        internal void Update(Camera? camera)
        {
            if (_wasHidden)
            {
                // A new battle (or turning the bar on) starts with an empty history.
                _wasHidden = false;
                Hits.Clear();
                hasAverage = false;
                _nextScan = 0f;
                _root.SetActive(true);
            }

            float now = Time.unscaledTime;
            if (now >= _nextScan)
            {
                _nextScan = now + 0.5f;
                ScanLanes();
            }
            if (!_hasLanes) return;

            // Span the lanes that are in use and sit just behind their receptors.
            float centerX = (_minX + _maxX) * 0.5f;
            float half = Mathf.Min((_maxX - _minX) * 0.42f, 46f);
            float y = _receptorY - Gap;
            float limit = ScreenEdgeY(camera, centerX, _receptorY);
            // Keep the icons on screen; with no room behind the receptors, go in front of them.
            if (y - IconSize * 0.6f < limit) y = limit + IconSize * 0.6f;
            bool inFront = y > _receptorY - 13f;
            if (inFront) y = _receptorY + 17f;
            if (_inFront != inFront)
            {
                _inFront = inFront;
                int layer = inFront ? _frontLayer : _behindLayer;
                foreach (var renderer in _renderers) renderer.sortingLayerID = layer;
            }
            bool mirrored = IsMirrored(_rootTransform);
            Layout(new Vector3(centerX, y, _depth), half, mirrored);

            float scale = (float)(half / Range);
            for (int i = 0; i < MaxTicks; i++)
            {
                int hit = Hits.Count - 1 - i;
                float age = hit >= 0 ? now - Hits[hit].Time : TickLife;
                if (age >= TickLife)
                {
                    if (_tickShown[i]) { _ticks[i].enabled = false; _tickShown[i] = false; }
                    continue;
                }
                var (offset, c, _) = Hits[hit];
                float x = Mathf.Clamp((float)(offset * scale), -half, half);
                Place(_tickTransforms[i], x, 0.55f, 3.8f);
                _ticks[i].color = new Color(c.r, c.g, c.b, 0.9f * (1f - age / TickLife));
                if (!_tickShown[i]) { _ticks[i].enabled = true; _tickShown[i] = true; }
            }

            if (_markerShown != hasAverage)
            {
                _markerShown = hasAverage;
                _marker.enabled = hasAverage;
            }
            if (hasAverage)
            {
                float target = Mathf.Clamp((float)(average * scale), -half, half);
                _markerX += (target - _markerX) * (1f - Mathf.Exp(-Time.unscaledDeltaTime / 0.12f));
                _markerTransform.localPosition = new Vector3(_markerX, -3.4f, 0f);
            }
            else _markerX = 0f;
        }

        private void ScanLanes()
        {
            _minX = float.MaxValue;
            _maxX = float.MinValue;
            for (int i = 0; i < _field.childCount; i++)
            {
                var column = _field.GetChild(i);
                if (!column.gameObject.activeSelf || !column.name.StartsWith("CombatColumn", StringComparison.Ordinal)) continue;
                var p = column.localPosition;
                _minX = Mathf.Min(_minX, p.x);
                _maxX = Mathf.Max(_maxX, p.x);
                _receptorY = p.y;
                _depth = p.z;
            }
            _hasLanes = _minX <= _maxX;
        }

        /// <summary>Moves and sizes the fixed parts, but only when the layout changed.</summary>
        private void Layout(Vector3 position, float half, bool mirrored)
        {
            bool first = !_placedOnce;
            _placedOnce = true;
            if (first || (position - _placed).sqrMagnitude > 1e-6f)
            {
                _placed = position;
                _rootTransform.localPosition = position;
            }
            if (!first && half == _placedHalf && mirrored == _placedMirrored) return;
            _placedHalf = half;
            _placedMirrored = mirrored;
            _track.size = new Vector2(half * 2f + TrackHeight, TrackHeight);
            float scale = (float)(half / Range);
            for (int i = 0; i < 4; i++)
            {
                var zone = Zones[i + 2];
                float left = Mathf.Max((float)(zone.Early * scale), -half);
                float right = Mathf.Min((float)(zone.Late * scale), half);
                _bandTransforms[i].localPosition = new Vector3((left + right) * 0.5f, 0f, 0f);
                _bands[i].size = new Vector2(Mathf.Max(right - left, BandHeight), BandHeight);
            }
            // The icons stay upright even on the mirrored upscroll field.
            float iconX = half + TrackHeight * 0.5f + IconSize * 0.75f;
            PlaceIcon(_rabbitTransform, -iconX, mirrored);
            PlaceIcon(_turtleTransform, iconX, mirrored);
        }

        private static void Place(Transform t, float x, float width, float height)
        {
            t.localPosition = new Vector3(x, 0f, 0f);
            // The tick sprite is a quarter as wide as it is tall (16 x 64 pixels, 1 unit tall).
            t.localScale = new Vector3(width * 4f, height, 1f);
        }

        private static void PlaceIcon(Transform t, float x, bool mirrored)
        {
            t.localPosition = new Vector3(x, 0f, 0f);
            t.localScale = new Vector3(IconSize, mirrored ? -IconSize : IconSize, 1f);
        }

        /// <summary>
        /// Field-local Y where the screen edge behind the receptors meets the field plane,
        /// or negative infinity when the camera cannot tell.
        /// </summary>
        private float ScreenEdgeY(Camera? camera, float x, float receptorY)
        {
            if (!camera) return float.NegativeInfinity;
            var receptor = camera!.WorldToViewportPoint(_field.TransformPoint(new Vector3(x, receptorY, 0f)));
            var behind = camera.WorldToViewportPoint(_field.TransformPoint(new Vector3(x, receptorY - 10f, 0f)));
            float edge = behind.y < receptor.y ? 0f : 1f;
            var ray = camera.ViewportPointToRay(new Vector3(receptor.x, edge, 0f));
            var plane = new Plane(_field.TransformDirection(Vector3.forward), _field.position);
            if (!plane.Raycast(ray, out float enter)) return float.NegativeInfinity;
            var local = _field.InverseTransformPoint(ray.GetPoint(enter));
            return local.y < receptorY ? local.y : float.NegativeInfinity;
        }

        private SpriteRenderer Layer(string name, int order, Sprite sprite, bool sliced = false)
        {
            var go = new GameObject(name);
            go.layer = _root.layer;
            go.transform.SetParent(_rootTransform, false);
            var renderer = go.AddComponent<SpriteRenderer>();
            var material = NoteSkins.SharedSpriteMaterial();
            if (material) renderer.sharedMaterial = material;
            renderer.sprite = sprite;
            renderer.sortingLayerID = _behindLayer;
            renderer.sortingOrder = order;
            // Sliced pills keep round ends at any length.
            if (sliced) renderer.drawMode = SpriteDrawMode.Sliced;
            _renderers.Add(renderer);
            return renderer;
        }

        private static int ReceptorLayerOf(Transform field)
        {
            // Draw with the receptors: above the lanes, below the notes.
            foreach (var shape in field.GetComponentsInChildren<ShapeRenderer>(true))
                if (shape && shape.gameObject.name == "Hitbox BG Border") return shape.SortingLayerID;
            return 0;
        }

        private static bool IsMirrored(Transform transform)
        {
            var right = transform.TransformVector(Vector3.right);
            var up = transform.TransformVector(Vector3.up);
            var forward = transform.TransformVector(Vector3.forward);
            return Vector3.Dot(Vector3.Cross(right, up), forward) < 0f;
        }
    }
}
