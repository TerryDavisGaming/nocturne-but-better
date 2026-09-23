using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NocturneFlatScroll
{
    /// <summary>Flattens the original meters beside the player and enemy ends of the track.</summary>
    internal static class HudLayout
    {
        private const float ReferenceHeight = 1080f;
        private const float FieldHalfWidth = 490f;
        private const float NativeBarLength = 1024f;
        private const float PlayerMeterY = -150f;
        private const float EnemyMeterY = 150f;
        private const float MeterHeight = 600f;
        private const float BuffScale = 32f / 15f;
        private const float ArmorScale = 0.18f;

        private static readonly Dictionary<int, LayoutState> States = new Dictionary<int, LayoutState>();

        private sealed class LayoutState
        {
            internal CombatNoteFieldView View;
            internal Transform Canvas;
            internal Transform Vines;
            internal Vector3 NativeCanvasPosition;
            internal Quaternion NativeCanvasRotation;
            internal Vector3 LastCanvasPosition;
            internal bool CanvasPositionWritten;
            internal Vector3 NativeVinesPosition;
            internal Quaternion NativeVinesRotation;
            internal Vector3 NativeVinesScale;
            internal Transform PlayerHealth;
            internal Transform PlayerEnergy;
            internal Transform EnemyHealth;
            internal Transform EnemyEnergy;
            internal NoteFieldBehaviour NoteField;
            internal readonly List<Transform> Columns = new List<Transform>();
            internal Transform Armor;
            internal RectTransform PlayerBuffs;
            internal RectTransform EnemyBuffs;
            internal Vector3 ArmorAnimationScale = Vector3.one;
            internal Vector3 LastArmorScale;
            internal bool ArmorScaleWritten;
            internal bool Ready;
            internal bool Modified;
            internal readonly List<NativeTransform> Originals = new List<NativeTransform>();
            internal float RetryAt;
            internal float NextClipRefresh;
            internal readonly List<MaskClip> Masks = new List<MaskClip>();
            internal readonly List<GraphicClip> Graphics = new List<GraphicClip>();
            internal readonly HashSet<int> OwnedMasks = new HashSet<int>();
        }

        private sealed class NativeTransform
        {
            private readonly Transform Target;
            private Vector3 Position;
            private readonly Quaternion Rotation;
            private readonly Vector3 Scale;
            private readonly RectTransform Rect;
            private readonly Vector2 Pivot;
            private Vector3 AnchoredPosition;
            private Vector3 LastPosition;
            private Vector3 LastAnchoredPosition;

            internal NativeTransform(Transform target)
            {
                Target = target;
                Position = target.localPosition;
                Rotation = target.localRotation;
                Scale = target.localScale;
                Rect = target.GetComponent<RectTransform>();
                if (Rect)
                {
                    Pivot = Rect.pivot;
                    AnchoredPosition = Rect.anchoredPosition3D;
                }
                RecordWritten();
            }

            internal void CaptureAnimation()
            {
                if (!Target) return;
                Position = ChangedAxes(Position, Target.localPosition, LastPosition);
                if (Rect)
                    AnchoredPosition = ChangedAxes(AnchoredPosition, Rect.anchoredPosition3D,
                                                   LastAnchoredPosition);
            }

            internal void RecordWritten()
            {
                if (!Target) return;
                LastPosition = Target.localPosition;
                if (Rect) LastAnchoredPosition = Rect.anchoredPosition3D;
            }

            internal void Restore()
            {
                if (!Target) return;
                if (Rect) Rect.pivot = Pivot;
                Target.localPosition = Position;
                Target.localRotation = Rotation;
                Target.localScale = Scale;
                if (Rect) Rect.anchoredPosition3D = AnchoredPosition;
            }
        }

        private sealed class MaskClip
        {
            internal RectMask2D Mask;
            internal RectTransform Rect;
            internal Rect Bounds;
            internal bool Valid;
        }

        private sealed class GraphicClip
        {
            internal MaskableGraphic Graphic;
            internal MaskClip[] Masks;
        }

        /// <summary>Call after the game's Animator update, while this view is active.</summary>
        public static void Apply(CombatNoteFieldView view, Camera camera, ScrollMode mode)
        {
            if (mode == ScrollMode.Default)
            {
                foreach (LayoutState cached in States.Values) Restore(cached);
                return;
            }
            if (!view || !camera)
                return;

            int id = view.GetInstanceID();
            LayoutState state;
            if (!States.TryGetValue(id, out state) || !state.View)
            {
                state = new LayoutState { View = view };
                States[id] = state;
            }

            if (!state.Ready)
            {
                if (Time.unscaledTime < state.RetryAt)
                    return;
                state.RetryAt = Time.unscaledTime + 1f;
                if (!Initialize(state))
                    return;
            }

            Rect pixels = camera.pixelRect;
            if (pixels.height <= 0f || pixels.width <= 0f)
                return;

            float halfWidth = 0.5f * ReferenceHeight * pixels.width / pixels.height;
            // Follow the visible lanes rather than the outer screen edges, so
            // four- and five-lane charts keep their meters close to the action.
            float fieldEdge = VisibleFieldHalfWidth(state, camera, halfWidth);
            float sideSpace = Mathf.Max(120f, halfWidth - fieldEdge);
            float fit = Mathf.Min(1f, sideSpace / 275f);
            float barScale = MeterHeight / NativeBarLength * fit;
            float energyX = fieldEdge + 60f * fit;
            // Match the authored 120-unit spacing between the two meter roots.
            // Their decorative corruption effects overlap in the native HUD too.
            float healthX = energyX + 120f * barScale;
            float statusX = healthX + 105f * fit;
            float depth = Mathf.Max(camera.nearClipPlane + 5f, 100f);
            depth = Mathf.Min(depth, camera.farClipPlane * 0.5f);
            float units = camera.orthographic
                ? camera.orthographicSize * 2f / ReferenceHeight
                : 2f * depth * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f) / ReferenceHeight;
            Quaternion facing = camera.transform.rotation;

            if (!state.Modified)
            {
                state.Originals.Clear();
                foreach (Transform target in new[] { state.PlayerHealth, state.PlayerEnergy,
                    state.EnemyHealth, state.EnemyEnergy, state.Armor,
                    state.PlayerBuffs, state.EnemyBuffs })
                    if (target) state.Originals.Add(new NativeTransform(target));
                state.PlayerBuffs.pivot = new Vector2(0.5f, 1f);
                state.EnemyBuffs.pivot = new Vector2(0.5f, 1f);
                state.Modified = true;
            }
            foreach (NativeTransform original in state.Originals) original.CaptureAnimation();

            // RectMask2D's clip rectangles use the root Canvas plane. Keep the
            // meter graphics and that plane together, not merely parallel child
            // quads floating in the original tilted Canvas coordinate system.
            AlignCanvas(state, camera, depth, halfWidth, facing);

            Place(state.PlayerHealth, camera, -healthX, PlayerMeterY, depth, halfWidth,
                  facing, units * barScale);
            Place(state.PlayerEnergy, camera, -energyX, PlayerMeterY, depth, halfWidth,
                  facing, units * barScale);
            Place(state.EnemyHealth, camera, healthX, EnemyMeterY, depth, halfWidth,
                  facing, units * barScale);
            Place(state.EnemyEnergy, camera, energyX, EnemyMeterY, depth, halfWidth,
                  facing, units * barScale);

            // Keep the original vertical layout groups and all counters. Sixteen
            // active statuses fit beside the meters at the reference resolution.
            float statusScale = BuffScale * fit;
            Place(state.PlayerBuffs, camera, -statusX, 165f, depth, halfWidth,
                  facing, units * statusScale);
            Place(state.EnemyBuffs, camera, statusX, 405f, depth, halfWidth,
                  facing, units * statusScale);

            if (state.Armor)
            {
                // Amour_None animates the root scale to zero. Preserve that
                // visibility and all armor hit/break animation child transforms.
                Vector3 animated = state.Armor.localScale;
                if (!state.ArmorScaleWritten || animated != state.LastArmorScale)
                    state.ArmorAnimationScale = animated;
                state.Armor.SetPositionAndRotation(
                    Position(camera, statusX, 465f, depth, halfWidth), facing);
                Vector3 size = LocalScale(state.Armor, units * ArmorScale * fit);
                state.LastArmorScale = Vector3.Scale(size, state.ArmorAnimationScale);
                state.Armor.localScale = state.LastArmorScale;
                state.ArmorScaleWritten = true;
            }

            ApplyMeterClipping(state);
            foreach (NativeTransform original in state.Originals) original.RecordWritten();
        }

        private static bool Initialize(LayoutState state)
        {
            Transform canvas = state.View.transform.Find("FieldPivot/Canvas");
            if (!canvas)
                return false;

            state.PlayerHealth = canvas.Find("CombatPlayerBars/HealthBar");
            state.PlayerEnergy = canvas.Find("CombatPlayerBars/EnergyBar");
            state.EnemyHealth = canvas.Find("CombatEnemyBars/HealthBar");
            state.EnemyEnergy = canvas.Find("CombatEnemyBars/EnergyBar");
            state.Armor = canvas.Find("CombatEnemyBars/Amour");
            Transform playerBuffs = canvas.Find("PlayerBuff");
            Transform enemyBuffs = canvas.Find("EnemyBuff");
            if (!state.PlayerHealth || !state.PlayerEnergy || !state.EnemyHealth ||
                !state.EnemyEnergy || !playerBuffs || !enemyBuffs)
                return false;

            state.PlayerBuffs = playerBuffs.GetComponent<RectTransform>();
            state.EnemyBuffs = enemyBuffs.GetComponent<RectTransform>();
            if (!state.PlayerBuffs || !state.EnemyBuffs)
                return false;

            Transform field = state.View.transform.Find("FieldPivot/Field");
            state.NoteField = state.View.GetComponentInChildren<NoteFieldBehaviour>(true);
            if (field)
                for (int i = 0; i < field.childCount; i++)
                {
                    Transform column = field.GetChild(i);
                    if (column.name.StartsWith("CombatColumn", System.StringComparison.Ordinal))
                        state.Columns.Add(column);
                }

            state.Canvas = canvas;
            state.NativeCanvasPosition = canvas.localPosition;
            state.NativeCanvasRotation = canvas.localRotation;
            state.Vines = canvas.Find("Vines");
            if (state.Vines)
            {
                // The Vines root is not animated; its children are. Preserve
                // their original frame and existing Animator binding paths.
                state.NativeVinesPosition = state.Vines.localPosition;
                state.NativeVinesRotation = state.Vines.localRotation;
                state.NativeVinesScale = state.Vines.localScale;
            }

            // Native icons and vertical labels retain their exact transforms.
            state.Ready = true;
            return true;
        }

        private static float VisibleFieldHalfWidth(LayoutState state, Camera camera, float halfWidth)
        {
            float edge = 0f;
            int count = state.NoteField ? state.NoteField.ActiveColumnCount : state.Columns.Count;
            if (count <= 0) count = state.Columns.Count;
            for (int i = 0; i < Mathf.Min(count, state.Columns.Count); i++)
            {
                Transform column = state.Columns[i];
                if (!column || !column.gameObject.activeInHierarchy ||
                    Mathf.Abs(column.lossyScale.x) < 0.001f) continue;
                // The authored receptor is about 35 units wide. Exclude the
                // unused fifth column even if its GameObject remains active.
                Vector3 left = camera.WorldToViewportPoint(column.TransformPoint(new Vector3(-17.5f, 0f, 0f)));
                Vector3 right = camera.WorldToViewportPoint(column.TransformPoint(new Vector3(17.5f, 0f, 0f)));
                if (left.z <= 0f || right.z <= 0f) continue;
                edge = Mathf.Max(edge, Mathf.Max(Mathf.Abs(left.x - 0.5f),
                                                Mathf.Abs(right.x - 0.5f)) * 2f * halfWidth);
            }
            return edge > 0f ? edge + 12f : FieldHalfWidth;
        }

        private static void AlignCanvas(LayoutState state, Camera camera, float depth,
                                        float halfWidth, Quaternion facing)
        {
            Transform canvas = state.Canvas;
            Vector3 animated = canvas.localPosition;
            state.NativeCanvasPosition = state.CanvasPositionWritten
                ? ChangedAxes(state.NativeCanvasPosition, animated, state.LastCanvasPosition)
                : animated;

            Transform parent = canvas.parent;
            Vector3 nativePosition = parent
                ? parent.TransformPoint(state.NativeCanvasPosition)
                : state.NativeCanvasPosition;
            Quaternion nativeRotation = parent
                ? parent.rotation * state.NativeCanvasRotation
                : state.NativeCanvasRotation;
            Vector3 nativeVinesPosition = nativePosition + nativeRotation *
                Vector3.Scale(canvas.lossyScale, state.NativeVinesPosition);

            canvas.SetPositionAndRotation(Position(camera, 0f, 0f, depth, halfWidth), facing);
            state.LastCanvasPosition = canvas.localPosition;
            state.CanvasPositionWritten = true;

            if (state.Vines)
            {
                state.Vines.SetPositionAndRotation(nativeVinesPosition,
                    nativeRotation * state.NativeVinesRotation);
                state.Vines.localScale = state.NativeVinesScale;
            }
        }

        private static Vector3 ChangedAxes(Vector3 native, Vector3 current, Vector3 written)
        {
            // Animators can write just one axis. Do not capture the other axes
            // of our projected position as native animation coordinates.
            if (current.x != written.x) native.x = current.x;
            if (current.y != written.y) native.y = current.y;
            if (current.z != written.z) native.z = current.z;
            return native;
        }

        private static void Restore(LayoutState state)
        {
            if (!state.Modified || !state.View || !state.Canvas) return;
            state.NativeCanvasPosition = ChangedAxes(state.NativeCanvasPosition,
                state.Canvas.localPosition, state.LastCanvasPosition);
            foreach (NativeTransform original in state.Originals) original.CaptureAnimation();
            if (state.Armor && state.Armor.localScale != state.LastArmorScale)
                state.ArmorAnimationScale = state.Armor.localScale;

            state.Canvas.localPosition = state.NativeCanvasPosition;
            state.Canvas.localRotation = state.NativeCanvasRotation;
            if (state.Vines)
            {
                state.Vines.localPosition = state.NativeVinesPosition;
                state.Vines.localRotation = state.NativeVinesRotation;
                state.Vines.localScale = state.NativeVinesScale;
            }
            foreach (NativeTransform original in state.Originals) original.Restore();
            if (state.Armor) state.Armor.localScale = state.ArmorAnimationScale;

            foreach (MaskClip clip in state.Masks)
                if (clip.Mask) clip.Mask.enabled = true;
            foreach (GraphicClip clip in state.Graphics)
            {
                if (!clip.Graphic) continue;
                clip.Graphic.SetClipRect(new Rect(), false);
                clip.Graphic.Cull(new Rect(-1000000f, -1000000f, 2000000f, 2000000f), true);
                clip.Graphic.RecalculateClipping();
            }
            foreach (MaskClip clip in state.Masks)
                if (clip.Mask && clip.Mask.isActiveAndEnabled) clip.Mask.PerformClipping();

            state.Masks.Clear();
            state.Graphics.Clear();
            state.OwnedMasks.Clear();
            state.NextClipRefresh = 0f;
            state.CanvasPositionWritten = false;
            state.ArmorScaleWritten = false;
            state.Modified = false;
        }

        private static void ApplyMeterClipping(LayoutState state)
        {
            // Native fill components continue writing RectMask2D.padding. The
            // built-in clipper assumes unrotated rectangles, so only these HUD
            // masks are disabled and their padding is transformed correctly.
            if (Time.unscaledTime >= state.NextClipRefresh)
            {
                RefreshClipping(state);
                state.NextClipRefresh = Time.unscaledTime + 1f;
            }

            for (int i = 0; i < state.Masks.Count; i++)
            {
                MaskClip mask = state.Masks[i];
                if (!mask.Mask || !mask.Rect)
                {
                    mask.Valid = false;
                    continue;
                }
                if (mask.Mask.enabled)
                    mask.Mask.enabled = false;
                Rect rect = mask.Rect.rect;
                Vector4 padding = mask.Mask.padding;
                float left = rect.xMin + padding.x;
                float bottom = rect.yMin + padding.y;
                float right = rect.xMax - padding.z;
                float top = rect.yMax - padding.w;
                mask.Valid = right > left && top > bottom;
                if (!mask.Valid)
                {
                    mask.Bounds = new Rect();
                    continue;
                }

                Transform canvas = state.Canvas;
                Vector3 a = canvas.InverseTransformPoint(mask.Rect.TransformPoint(new Vector3(left, bottom, 0f)));
                Vector3 b = canvas.InverseTransformPoint(mask.Rect.TransformPoint(new Vector3(left, top, 0f)));
                Vector3 c = canvas.InverseTransformPoint(mask.Rect.TransformPoint(new Vector3(right, top, 0f)));
                Vector3 d = canvas.InverseTransformPoint(mask.Rect.TransformPoint(new Vector3(right, bottom, 0f)));
                mask.Bounds = Rect.MinMaxRect(
                    Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x)),
                    Mathf.Min(Mathf.Min(a.y, b.y), Mathf.Min(c.y, d.y)),
                    Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x)),
                    Mathf.Max(Mathf.Max(a.y, b.y), Mathf.Max(c.y, d.y)));
            }

            for (int i = 0; i < state.Graphics.Count; i++)
            {
                GraphicClip target = state.Graphics[i];
                MaskableGraphic graphic = target.Graphic;
                if (!graphic || !graphic.isActiveAndEnabled)
                    continue;
                Rect bounds = target.Masks[0].Bounds;
                bool valid = target.Masks[0].Valid;
                for (int j = 1; j < target.Masks.Length; j++)
                {
                    MaskClip next = target.Masks[j];
                    valid &= next.Valid;
                    bounds = Rect.MinMaxRect(Mathf.Max(bounds.xMin, next.Bounds.xMin),
                                              Mathf.Max(bounds.yMin, next.Bounds.yMin),
                                              Mathf.Min(bounds.xMax, next.Bounds.xMax),
                                              Mathf.Min(bounds.yMax, next.Bounds.yMax));
                }
                valid &= bounds.width > 0f && bounds.height > 0f;
                graphic.SetClipRect(bounds, valid);
                // Let Unity change the cull state and notify dirty graphics, so
                // an empty meter rebuilds correctly when it begins filling.
                graphic.Cull(bounds, valid);
            }
        }

        private static void RefreshClipping(LayoutState state)
        {
            state.Masks.Clear();
            state.Graphics.Clear();
            var byTransform = new Dictionary<int, MaskClip>();
            Transform[] roots = { state.PlayerHealth, state.PlayerEnergy,
                                  state.EnemyHealth, state.EnemyEnergy };
            for (int i = 0; i < roots.Length; i++)
            {
                foreach (RectMask2D mask in roots[i].GetComponentsInChildren<RectMask2D>(true))
                {
                    int id = mask.GetInstanceID();
                    if (!mask.enabled && !state.OwnedMasks.Contains(id))
                        continue;
                    state.OwnedMasks.Add(id);
                    var clip = new MaskClip { Mask = mask, Rect = mask.GetComponent<RectTransform>() };
                    byTransform[clip.Rect.GetInstanceID()] = clip;
                    state.Masks.Add(clip);
                    mask.enabled = false;
                }
            }
            for (int i = 0; i < roots.Length; i++)
            {
                foreach (MaskableGraphic graphic in roots[i].GetComponentsInChildren<MaskableGraphic>(true))
                {
                    var ancestors = new List<MaskClip>();
                    Transform current = graphic.transform;
                    while (current && current != state.Canvas)
                    {
                        MaskClip mask;
                        if (byTransform.TryGetValue(current.GetInstanceID(), out mask))
                            ancestors.Add(mask);
                        current = current.parent;
                    }
                    if (ancestors.Count != 0)
                        state.Graphics.Add(new GraphicClip { Graphic = graphic, Masks = ancestors.ToArray() });
                }
            }
            // A periodic refresh also catches scrolling-text copies created
            // when a previously inactive status message first becomes visible.
        }

        private static Vector3 Position(Camera camera, float x, float y, float depth, float halfWidth)
        {
            return camera.ViewportToWorldPoint(new Vector3(0.5f + x / (2f * halfWidth),
                                                           0.5f + y / ReferenceHeight, depth));
        }

        private static void Place(Transform target, Camera camera, float x, float y,
                                  float depth, float halfWidth, Quaternion rotation, float scale)
        {
            if (!target)
                return;
            target.SetPositionAndRotation(Position(camera, x, y, depth, halfWidth), rotation);
            target.localScale = LocalScale(target, scale);
        }

        private static Vector3 LocalScale(Transform target, float worldScale)
        {
            Transform parent = target.parent;
            if (!parent)
                return Vector3.one * worldScale;
            Vector3 inherited = parent.lossyScale;
            return new Vector3(worldScale / SafeScale(inherited.x),
                               worldScale / SafeScale(inherited.y),
                               worldScale / SafeScale(inherited.z));
        }

        private static float SafeScale(float value)
        {
            return Mathf.Abs(value) < 0.000001f ? 0.000001f : value;
        }
    }
}
