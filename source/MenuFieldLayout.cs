using System;
using System.Collections.Generic;
using UnityEngine;

namespace NocturneFlatScroll
{
    /// <summary>Flattens the menu previews independently of the combat camera.</summary>
    internal static class MenuFieldLayout
    {
        private enum FieldKind
        {
            Tilted,
            AutoVideo,
            CalibrationTest
        }

        private sealed class LabelChild
        {
            internal readonly Transform Transform;
            internal readonly RectTransform Rect;
            private readonly NativeTransformState Native;
            // The sideways "disabled" label scrolls along the lane on its own and is not moved.
            private readonly bool Scrolls;

            internal LabelChild(Transform transform)
            {
                Transform = transform;
                Rect = transform.TryCast<RectTransform>();
                Native = new NativeTransformState(transform);
                Scrolls = transform.name == "DisabledLabel";
            }

            internal void Capture() => Native.Capture();
            internal void Restore() => Native.Restore();

            /// <param name="drop">Extra distance from the receptor, for bigger or skinned receptors.</param>
            internal void Apply(bool upscroll, float drop)
            {
                if (Transform == null) return;
                float direction = upscroll ? -1f : 1f;
                if (Scrolls) drop = 0f;
                if (Rect != null)
                {
                    var anchoredPosition = Native.AnchoredPosition;
                    Rect.anchoredPosition = new Vector2(anchoredPosition.x, (anchoredPosition.y - drop) * direction);
                    var position = Rect.localPosition;
                    position.z = Native.Position.z;
                    Rect.localPosition = position;
                }
                else
                {
                    var position = Native.Position;
                    Transform.localPosition = new Vector3(position.x, (position.y - drop) * direction, position.z);
                }
            }
        }

        private sealed class LabelGroup
        {
            internal readonly Transform Transform;
            private readonly NativeTransformState Native;
            internal readonly List<LabelChild> Children = new List<LabelChild>();

            internal LabelGroup(Transform transform)
            {
                Transform = transform;
                Native = new NativeTransformState(transform);
                for (int i = 0; i < transform.childCount; i++)
                    Children.Add(new LabelChild(transform.GetChild(i)));
            }

            internal void Capture()
            {
                Native.Capture();
                foreach (var child in Children) child.Capture();
            }

            internal void Restore()
            {
                Native.Restore();
                foreach (var child in Children) child.Restore();
            }

            internal void Apply(bool upscroll, float drop)
            {
                if (Transform == null) return;
                var scale = Native.Scale;
                Transform.localScale = new Vector3(scale.x, scale.y * (upscroll ? -1f : 1f), scale.z);
                // Counter-reflect glyphs while retaining their reflected positions.
                foreach (var child in Children) child.Apply(upscroll, drop);
            }
        }

        private sealed class FieldState
        {
            internal readonly int Id;
            internal readonly RectTransform Field;
            internal readonly FieldKind Kind;
            private readonly NativeTransformState Native;
            private bool Modified;
            internal readonly List<LabelGroup> Labels = new List<LabelGroup>();
            private readonly List<LaneColumn> Lanes;
            // The middle of the preview's lanes, which spacing spreads around.
            private readonly float LaneCenter;

            internal FieldState(RectTransform field, FieldKind kind)
            {
                Id = field.GetInstanceID();
                Field = field;
                Kind = kind;
                Native = new NativeTransformState(field);
                Lanes = LaneColumn.Collect(field);
                if (Lanes.Count > 0)
                {
                    float min = float.MaxValue, max = float.MinValue;
                    foreach (var lane in Lanes)
                    {
                        min = Mathf.Min(min, lane.NativeX);
                        max = Mathf.Max(max, lane.NativeX);
                    }
                    LaneCenter = (min + max) * 0.5f;
                }
                for (int i = 0; i < field.childCount; i++)
                {
                    var column = field.GetChild(i);
                    if (!column.name.StartsWith("CombatColumn", StringComparison.Ordinal)) continue;
                    var labels = column.Find("Labels");
                    if (labels != null) Labels.Add(new LabelGroup(labels));
                }
            }

            /// <param name="shift">Canvas units that move the receptors toward the screen's middle.</param>
            internal void Apply(bool upscroll, float shift)
            {
                if (!Modified)
                {
                    Native.Capture();
                    foreach (var labels in Labels) labels.Capture();
                    // Mark before writing so Default can restore a partially applied field too.
                    Modified = true;
                }
                float targetY;
                float targetZ = Native.Position.z;
                if (Kind == FieldKind.Tilted)
                {
                    // Match the original asset patch's 640x360 menu projection.
                    // Preserve receptor depth, and therefore lane width, in both directions.
                    float theta = 30f * Mathf.Deg2Rad;
                    float originalConductorZ = -65f * Mathf.Sin(theta);
                    float canvasWorldScale = 2f * 100f * Mathf.Tan(theta) / 360f;
                    float originalDepth = 100f + originalConductorZ * 1.5f * canvasWorldScale;
                    float projectedY = (upscroll ? 108f - shift : -108f + shift) * originalDepth / 100f / 1.5f;
                    targetY = Mathf.Cos(theta) * projectedY + Mathf.Sin(theta) * originalConductorZ;
                    targetZ = -Mathf.Sin(theta) * projectedY + Mathf.Cos(theta) * originalConductorZ;
                    Field.localRotation = Quaternion.Euler(-30f, 0f, 0f);
                }
                else
                {
                    // Video calibration's top-anchored conductor is 20 units below center.
                    targetY = Kind == FieldKind.AutoVideo
                        ? (upscroll ? 128f - shift : -88f + shift)
                        : (upscroll ? 108f - shift : -108f + shift);
                    Field.localRotation = Native.Rotation;
                }

                var scale = Native.Scale;
                Field.localScale = new Vector3(scale.x, scale.y * (upscroll ? -1f : 1f), scale.z);
                Field.anchoredPosition = new Vector2(Native.AnchoredPosition.x, targetY);
                var position = Field.localPosition;
                position.z = targetZ;
                Field.localPosition = position;
                float size = SettingsState.NoteSize.Factor;
                // The previews have little room beside them, so they spread their lanes to
                // at most 110%.
                float spacing = Mathf.Min(SettingsState.LaneSpacing.Factor, MaxPreviewSpacing);
                float width = Mathf.Min(size, spacing);
                foreach (var lane in Lanes)
                {
                    if (!lane.IsAlive) continue;
                    lane.Space(LaneCenter, spacing);
                    lane.Size(size, width);
                }
                FlatFields.Set(Id, true);
                float drop = LaneColumn.LabelDrop(size);
                foreach (var labels in Labels) labels.Apply(upscroll, drop);
            }

            internal void Restore()
            {
                if (!Modified) return;
                Native.Restore();
                foreach (var labels in Labels) labels.Restore();
                foreach (var lane in Lanes)
                {
                    lane.Unspace();
                    lane.Unsize();
                }
                FlatFields.Set(Id, false);
                Modified = false;
            }
        }

        private const float MaxPreviewSpacing = 1.1f;
        private static readonly List<FieldState> Fields = new List<FieldState>();
        private static readonly HashSet<int> KnownIds = new HashSet<int>();
        // Previews that keep their layout but still get skinned receptors.
        private static readonly List<(int Id, Transform Field)> SkinOnlyFields = new List<(int Id, Transform Field)>();

        /// <summary>Called periodically so newly loaded menu objects are included.</summary>
        internal static void Discover()
        {
            for (int i = Fields.Count - 1; i >= 0; i--)
            {
                if (Fields[i].Field != null) continue;
                KnownIds.Remove(Fields[i].Id);
                FlatFields.Set(Fields[i].Id, false);
                Fields.RemoveAt(i);
            }
            for (int i = SkinOnlyFields.Count - 1; i >= 0; i--)
            {
                if (SkinOnlyFields[i].Field != null) continue;
                KnownIds.Remove(SkinOnlyFields[i].Id);
                SkinOnlyFields.RemoveAt(i);
            }

            // The named conductor objects use WwiseConductor/SongConductor components.
            // Query that small set rather than allocating every Transform in the game.
            foreach (var parent in ConductorTransforms())
            {
                if (parent == null || (parent.name != "CalibrationConductorV2" && parent.name != "DifficultyConductor"))
                    continue;
                var transform = parent.Find("Field");
                if (transform == null || KnownIds.Contains(transform.GetInstanceID())) continue;
                if (!HasAncestor(transform, "Menu Pages")) continue;
                if (HasAncestor(transform, "AudioCalibration"))
                {
                    // The audio calibration lanes stay as they are, but its notes are skinned.
                    SkinOnlyFields.Add((transform.GetInstanceID(), transform));
                    KnownIds.Add(transform.GetInstanceID());
                    continue;
                }
                var field = transform.TryCast<RectTransform>();
                if (field == null) continue;

                FieldKind kind;
                if (HasAncestor(transform, "VideoCalibration"))
                    kind = FieldKind.AutoVideo;
                else if (HasAncestor(transform, "CalibrationTest"))
                    kind = FieldKind.CalibrationTest;
                else if (HasAncestor(transform, "Panel_CalibrationOptionsV2") ||
                         HasAncestor(transform, "Panel_CalibrationOptions") ||
                         HasAncestor(transform, "Panel_DifficultyScreen"))
                    kind = FieldKind.Tilted;
                else
                    continue;

                var state = new FieldState(field, kind);
                Fields.Add(state);
                KnownIds.Add(state.Id);
                ModLog.Info("Flat scroll menu preview: " + field.parent.name + " (" + kind + ")");
            }
        }

        private static IEnumerable<Transform> ConductorTransforms()
        {
            foreach (var conductor in Resources.FindObjectsOfTypeAll<WwiseConductor>())
                if (conductor != null && conductor.gameObject.scene.IsValid()) yield return conductor.transform;
            foreach (var conductor in Resources.FindObjectsOfTypeAll<SongConductor>())
                if (conductor != null && conductor.gameObject.scene.IsValid()) yield return conductor.transform;
        }

        /// <summary>Reapplies absolute values after menu animations, without accumulating flips.</summary>
        internal static void Apply(ScrollMode mode)
        {
            // The previews use a 360-unit-high canvas, so 1% of screen height is 3.6 units.
            float shift = SettingsState.ReceptorHeight * 3.6f;
            foreach (var state in Fields)
            {
                if (state.Field == null) continue;
                // Include inactive panels, but write the native baseline only once.
                if (mode == ScrollMode.Default) state.Restore();
                else state.Apply(mode == ScrollMode.Upscroll2D, shift);
            }
        }

        /// <summary>Adds the visible preview fields for receptor skinning; NoteSkins counts their lanes.</summary>
        internal static void AddSkinFields(List<(Transform Field, int Columns)> fields)
        {
            foreach (var state in Fields)
                if (state.Field != null && state.Field.gameObject.activeInHierarchy) fields.Add((state.Field, 0));
            foreach (var (_, field) in SkinOnlyFields)
                if (field != null && field.gameObject.activeInHierarchy) fields.Add((field, 0));
        }

        private static bool HasAncestor(Transform transform, string name)
        {
            for (var current = transform.parent; current != null; current = current.parent)
                if (current.name == name) return true;
            return false;
        }
    }
}
