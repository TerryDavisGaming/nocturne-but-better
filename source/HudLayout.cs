using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NocturnePlus
{
    /// <summary>
    /// Flattens the original meters beside the player and enemy ends of the track.
    ///
    /// While Optimizing, the small struct math (UMath) and the Unity object checks (Obj) are done in
    /// the mod's own code with the same results to the bit, and the meter clipping works out a mask's
    /// bounds only when a shown graphic needs them. Normal makes the same Unity calls as before.
    /// </summary>
    internal static class HudLayout
    {
        private const float ReferenceHeight = 1080f;
        private const float FieldHalfWidth = 490f;
        private const float NativeBarLength = 1024f;
        private const float PlayerMeterY = -150f;
        private const float EnemyMeterY = 150f;
        private const float MeterHeight = 600f;
        // A native bar is 94 units wide, and its icon (the heart, or the energy bar's
        // stagger icon) reaches 62 units above its top.
        private const float NativeBarWidth = 94f;
        private const float NativeIconHeight = 62f;
        // Moved enemy meters shrink rather than reach lower than the player's full-size ones.
        private const float EnemyMeterFloor = PlayerMeterY - 0.5f * MeterHeight;
        private const float BuffScale = 32f / 15f;
        // Sixteen statuses, 17 units apart in their layout group.
        private const int StatusCount = 16;
        private const float NativeStatusStep = 17f;
        private const float StatusFloor = -0.5f * ReferenceHeight + 20f;
        private const float ArmorScale = 0.18f;
        // The armor badge sits beside the top of the enemy's health bar, with the
        // enemy's statuses below it. Its art is 300 units square.
        private const float ArmorY = 465f;
        private const float EnemyBuffsBelowArmor = 60f;
        private const float ArmorHalfSize = 150f * ArmorScale + 6f;
        private const float InfoPanelGap = 12f;
        private const float InfoPanelHysteresis = 20f;

        private static readonly Dictionary<int, LayoutState> States = new Dictionary<int, LayoutState>();
        // Counts the meter clipping passes, so a mask's bounds say which pass they're for.
        private static int clipPass;

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
            internal bool ArmorBelowPanel;
            internal bool MetersBelowPanel;
            internal GameObject? ArmorObject;
            internal TMP_Text? ArmorText;
            internal EnemyCombatBars? EnemyBars;
            internal float ArmorShownAt = -1f;
            internal bool ArmorChecked;
            internal bool ArmorReported;
            internal IntPtr ArmorBattle;
            // Fetched once: each fetch through the interop makes a new wrapper.
            internal CombatEnemyInfoPanel? InfoPanel;
            internal GameObject? InfoPanelObject;
            internal readonly List<(RectTransform Rect, GameObject Box)> InfoEntries =
                new List<(RectTransform Rect, GameObject Box)>();
            internal Canvas? InfoCanvas;
            internal Camera? InfoCamera;
            internal bool ShowInfo;
            internal float NextInfoCheck;
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
                if (Obj.Alive(Rect))
                {
                    Pivot = Rect.pivot;
                    AnchoredPosition = Rect.anchoredPosition3D;
                }
                RecordWritten();
            }

            internal void CaptureAnimation()
            {
                if (!Obj.Alive(Target)) return;
                Position = ChangedAxes(Position, Target.localPosition, LastPosition);
                if (Obj.Alive(Rect))
                    AnchoredPosition = ChangedAxes(AnchoredPosition, Rect.anchoredPosition3D,
                                                   LastAnchoredPosition);
            }

            internal void RecordWritten()
            {
                if (!Obj.Alive(Target)) return;
                LastPosition = Target.localPosition;
                if (Obj.Alive(Rect)) LastAnchoredPosition = Rect.anchoredPosition3D;
            }

            internal void Restore()
            {
                if (!Obj.Alive(Target)) return;
                if (Obj.Alive(Rect)) Rect.pivot = Pivot;
                Target.localPosition = Position;
                Target.localRotation = Rotation;
                Target.localScale = Scale;
                if (Obj.Alive(Rect)) Rect.anchoredPosition3D = AnchoredPosition;
            }
        }

        private sealed class MaskClip
        {
            internal RectMask2D Mask;
            internal RectTransform Rect;
            internal Rect Bounds;
            internal bool Valid;
            // The clipping pass whose Bounds and Valid these are (Optimizing works them out on first use).
            internal int Pass;
            // QA builds: the old full pass's answer, to compare with.
            internal Rect QaBounds;
            internal bool QaValid, QaDead;
            internal int QaPass;
        }

        private sealed class GraphicClip
        {
            internal MaskableGraphic Graphic;
            internal MaskClip[] Masks;
        }

        /// <summary>Call after the game's Animator update, while this view is active.</summary>
        public static void Apply(CombatNoteFieldView view, Camera camera, ScrollMode mode)
        {
            UMath.Follow(Performance.Optimizing);
            UMath.QaReport();
            if (mode == ScrollMode.Default)
            {
                foreach (LayoutState cached in States.Values) Restore(cached);
                return;
            }
            if (!Obj.Alive(view) || !Obj.Alive(camera))
                return;

            int id = view.GetInstanceID();
            LayoutState state;
            if (!States.TryGetValue(id, out state) || !Obj.Alive(state.View))
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
            if (UMath.Height(pixels) <= 0f || UMath.Width(pixels) <= 0f)
                return;

            float halfWidth = 0.5f * ReferenceHeight * UMath.Width(pixels) / UMath.Height(pixels);
            // Follow the visible lanes rather than the outer screen edges, so
            // four- and five-lane charts keep their meters close to the action.
            float fieldEdge = VisibleFieldHalfWidth(state, camera, halfWidth);
            float sideSpace = UMath.Max(120f, halfWidth - fieldEdge);
            float fit = UMath.Min(1f, sideSpace / 275f);
            float barScale = MeterHeight / NativeBarLength * fit;
            float energyX = fieldEdge + 60f * fit;
            // Match the authored 120-unit spacing between the two meter roots.
            // Their decorative corruption effects overlap in the native HUD too.
            float healthX = energyX + 120f * barScale;
            float statusX = healthX + 105f * fit;
            float depth = UMath.Max(camera.nearClipPlane + 5f, 100f);
            depth = UMath.Min(depth, camera.farClipPlane * 0.5f);
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
                    if (Obj.Alive(target)) state.Originals.Add(new NativeTransform(target));
                state.PlayerBuffs.pivot = UMath.V2(0.5f, 1f);
                state.EnemyBuffs.pivot = UMath.V2(0.5f, 1f);
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

            // The game's enemy info panel (the "Armor" box and the others in the upper
            // right corner) draws over this HUD. When it covers the top of the enemy's
            // meters (wide lanes, large notes, or a squarer window), they move down to
            // just below its lowest box, and shrink if they would reach lower than the
            // player's meters.
            Rect panel;
            bool hasPanel = EnemyInfoBounds(state, halfWidth, out panel);
            float meterHalf = 0.5f * NativeBarWidth * barScale;
            float meterTop = EnemyMeterY + (0.5f * NativeBarLength + NativeIconHeight) * barScale;
            float meterBottom = EnemyMeterY - 0.5f * NativeBarLength * barScale;
            // Once below, the meters and the badge need a clear margin to move back up,
            // so the lanes' hit shake can't make them jump at the panel's edge.
            float margin = state.MetersBelowPanel ? InfoPanelHysteresis : 0f;
            state.MetersBelowPanel = hasPanel &&
                healthX + meterHalf + margin > UMath.XMin(panel) && energyX - meterHalf - margin < UMath.XMax(panel) &&
                meterTop + margin > UMath.YMin(panel) && meterBottom - margin < UMath.YMax(panel);
            float enemyScale = barScale;
            float enemyMeterY = EnemyMeterY;
            if (state.MetersBelowPanel)
            {
                float top = UMath.Min(meterTop, UMath.YMin(panel) - InfoPanelGap);
                enemyScale = UMath.Clamp((top - EnemyMeterFloor) / (NativeBarLength + NativeIconHeight),
                                         0.5f * barScale, barScale);
                enemyMeterY = top - (0.5f * NativeBarLength + NativeIconHeight) * enemyScale;
            }
            float enemyHealthX = energyX + 120f * enemyScale;
            float enemyStatusX = enemyHealthX + 105f * fit;
            Place(state.EnemyHealth, camera, enemyHealthX, enemyMeterY, depth, halfWidth,
                  facing, units * enemyScale);
            Place(state.EnemyEnergy, camera, energyX, enemyMeterY, depth, halfWidth,
                  facing, units * enemyScale);

            // When the panel covers the armor badge's spot, the badge and the enemy's
            // statuses start just below its lowest box. Moved meters always take them
            // along, so the badge stays beside the top of the health bar.
            float armorY = ArmorY;
            float armorHalf = ArmorHalfSize * fit;
            float reach = armorHalf + (state.ArmorBelowPanel ? InfoPanelHysteresis : 0f);
            state.ArmorBelowPanel = state.MetersBelowPanel || (hasPanel &&
                enemyStatusX + reach > UMath.XMin(panel) && enemyStatusX - reach < UMath.XMax(panel) &&
                armorY + armorHalf > UMath.YMin(panel) && armorY - armorHalf < UMath.YMax(panel));
            if (state.ArmorBelowPanel)
                armorY = UMath.Min(ArmorY, UMath.YMin(panel) - InfoPanelGap - armorHalf);

            // Keep the original vertical layout groups and all counters. All sixteen
            // statuses fit beside the meters; when a tall panel pushes the enemy's
            // column down, its statuses shrink so all sixteen still fit on screen.
            float statusScale = BuffScale * fit;
            float enemyBuffsY = armorY - EnemyBuffsBelowArmor;
            float enemyStatusScale = UMath.Clamp((enemyBuffsY - StatusFloor) / (StatusCount * NativeStatusStep),
                                                 0.5f * statusScale, statusScale);
            Place(state.PlayerBuffs, camera, -statusX, 165f, depth, halfWidth,
                  facing, units * statusScale);
            Place(state.EnemyBuffs, camera, enemyStatusX, enemyBuffsY, depth, halfWidth,
                  facing, units * enemyStatusScale);

            if (Obj.Alive(state.Armor))
            {
                // Amour_None animates the root scale to zero. Preserve that
                // visibility and all armor hit/break animation child transforms.
                Vector3 animated = state.Armor.localScale;
                if (!state.ArmorScaleWritten || UMath.NotEqual(animated, state.LastArmorScale))
                    state.ArmorAnimationScale = animated;
                state.Armor.SetPositionAndRotation(
                    Position(camera, enemyStatusX, armorY, depth, halfWidth), facing);
                Vector3 size = LocalScale(state.Armor, units * ArmorScale * fit);
                state.LastArmorScale = UMath.Scale(size, state.ArmorAnimationScale);
                state.Armor.localScale = state.LastArmorScale;
                state.ArmorScaleWritten = true;
                ReportArmor(state, enemyStatusX, armorY);
            }

            ApplyMeterClipping(state);
            foreach (NativeTransform original in state.Originals) original.RecordWritten();
        }

        /// <summary>
        /// Call every frame while a view is hidden, as it is between battles. The field's Animator
        /// takes the current values as its defaults when the view shows again, and states such as
        /// the five-lane ones write those defaults to the status columns every frame, so a 2D layout
        /// left in place would stay after a switch to Default. The next 2D frame captures the
        /// game's values again.
        /// </summary>
        public static void Hidden(CombatNoteFieldView view)
        {
            UMath.Follow(Performance.Optimizing);
            if (!Obj.Alive(view))
                return;
            if (!States.TryGetValue(view.GetInstanceID(), out LayoutState? state) || state == null)
                return;
            Restore(state);
            // Each battle reports its badge, even when its enemy's model has the last one's address,
            // and a second after the badge shows in that battle, even if it never hides first.
            state.ArmorReported = false;
            state.ArmorShownAt = -1f;
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

            Transform enemyBars = canvas.Find("CombatEnemyBars");
            state.EnemyBars = enemyBars ? enemyBars.GetComponent<EnemyCombatBars>() : null;
            state.ArmorObject = state.Armor ? state.Armor.gameObject : null;
            Transform? armorAmount = state.Armor ? state.Armor.Find("AmourAmount") : null;
            state.ArmorText = armorAmount != null && armorAmount ? armorAmount.GetComponent<TMP_Text>() : null;
            // The info panel is on the battle's CombatUI canvas, a sibling of this view.
            Transform battle = state.View.transform.parent ? state.View.transform.parent : state.View.transform;
            CombatEnemyInfoPanel infoPanel = battle.GetComponentInChildren<CombatEnemyInfoPanel>(true);
            if (infoPanel)
            {
                state.InfoPanel = infoPanel;
                state.InfoPanelObject = infoPanel.gameObject;
                foreach (CombatEnemyInfoEntryView entry in infoPanel.GetComponentsInChildren<CombatEnemyInfoEntryView>(true))
                {
                    RectTransform entryRect = entry.GetComponent<RectTransform>();
                    if (entryRect) state.InfoEntries.Add((entryRect, entry.gameObject));
                }
            }

            // Native icons and vertical labels retain their exact transforms.
            state.Ready = true;
            return true;
        }

        private static float VisibleFieldHalfWidth(LayoutState state, Camera camera, float halfWidth)
        {
            float edge = 0f;
            // Larger 2D notes and receptors take more room beside the outer lanes.
            float laneHalf = 17.5f * UMath.Max(1f, SettingsState.NoteSize.Factor);
            int count = Obj.Alive(state.NoteField) ? state.NoteField.ActiveColumnCount : state.Columns.Count;
            if (count <= 0) count = state.Columns.Count;
            for (int i = 0; i < UMath.Min(count, state.Columns.Count); i++)
            {
                Transform column = state.Columns[i];
                if (!Obj.Alive(column) || !column.gameObject.activeInHierarchy ||
                    UMath.Abs(column.lossyScale.x) < 0.001f) continue;
                // The authored receptor is about 35 units wide. Exclude the
                // unused fifth column even if its GameObject remains active.
                Vector3 left = camera.WorldToViewportPoint(column.TransformPoint(UMath.V3(-laneHalf, 0f, 0f)));
                Vector3 right = camera.WorldToViewportPoint(column.TransformPoint(UMath.V3(laneHalf, 0f, 0f)));
                if (left.z <= 0f || right.z <= 0f) continue;
                edge = UMath.Max(edge, UMath.Max(UMath.Abs(left.x - 0.5f),
                                                 UMath.Abs(right.x - 0.5f)) * 2f * halfWidth);
            }
            return edge > 0f ? edge + 12f : FieldHalfWidth;
        }

        /// <summary>The enemy info panel's boxes, in the same 1080-high units as the meters.</summary>
        private static bool EnemyInfoBounds(LayoutState state, float halfWidth, out Rect bounds)
        {
            bounds = new Rect();
            CombatEnemyInfoPanel? panel = state.InfoPanel;
            if (Obj.IsNull(panel) || !Obj.Alive(panel))
                return false;
            // The setting is a PlayerPrefs read, and the canvas and its camera stay put,
            // so they are checked once a second, like the meters' clipping.
            if (Time.unscaledTime >= state.NextInfoCheck)
            {
                state.NextInfoCheck = Time.unscaledTime + 1f;
                state.ShowInfo = NocturneSettings.ShowEnemyInfo;
                Canvas? canvas = state.InfoCanvas;
                if (Obj.IsNull(canvas) || !Obj.Alive(canvas))
                {
                    Canvas? parent = panel.GetComponentInParent<Canvas>();
                    canvas = !Obj.IsNull(parent) && Obj.Alive(parent) ? parent.rootCanvas : null;
                    state.InfoCanvas = canvas;
                }
                state.InfoCamera = !Obj.IsNull(canvas) && Obj.Alive(canvas) && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                    ? canvas.worldCamera : null;
            }
            // The game shows the panel when this enemy has boxes and its show enemy info
            // setting is on. Follow that, not the panel's fade: it also fades out for
            // cutscenes, and the badge shouldn't jump up and back down around them.
            GameObject? panelObject = state.InfoPanelObject;
            if (!state.ShowInfo || !panel.hasContent || Obj.IsNull(panelObject) || !panelObject.activeInHierarchy)
                return false;
            Camera? uiCamera = state.InfoCamera;
            bool hasCamera = !Obj.IsNull(uiCamera) && Obj.Alive(uiCamera);

            bool found = false;
            foreach ((RectTransform entry, GameObject box) in state.InfoEntries)
            {
                // The game turns off the boxes this enemy has nothing for.
                if (!Obj.Alive(entry) || !box.activeInHierarchy) continue;
                Rect rect = entry.rect;
                if (UMath.Width(rect) <= 0f || UMath.Height(rect) <= 0f) continue;
                for (int corner = 0; corner < 4; corner++)
                {
                    Vector3 world = entry.TransformPoint(UMath.V3(corner < 2 ? UMath.XMin(rect) : UMath.XMax(rect),
                                                                  corner % 2 == 0 ? UMath.YMin(rect) : UMath.YMax(rect), 0f));
                    // Both cameras fill the screen, so their viewport coordinates agree.
                    Vector3 viewport = hasCamera
                        ? uiCamera!.WorldToViewportPoint(world)
                        : UMath.V3(world.x / Screen.width, world.y / Screen.height, 0f);
                    Vector2 point = UMath.V2((viewport.x - 0.5f) * 2f * halfWidth,
                                             (viewport.y - 0.5f) * ReferenceHeight);
                    if (!found)
                    {
                        bounds = UMath.PointRect(point);
                        found = true;
                    }
                    else
                    {
                        bounds = UMath.MinMax(UMath.Min(UMath.XMin(bounds), point.x), UMath.Min(UMath.YMin(bounds), point.y),
                                              UMath.Max(UMath.XMax(bounds), point.x), UMath.Max(UMath.YMax(bounds), point.y));
                    }
                }
            }
            return found;
        }

        private static void ReportArmor(LayoutState state, float x, float y)
        {
            // Once a battle, a second after the badge first shows (Amour_None scales it
            // to zero), when the info panel has its boxes and the spot has settled.
            GameObject? armor = state.ArmorObject;
            if (state.ArmorAnimationScale.x <= 0.001f || Obj.IsNull(armor) || !armor.activeInHierarchy)
            {
                state.ArmorShownAt = -1f;
                return;
            }
            if (state.ArmorShownAt < 0f)
            {
                state.ArmorShownAt = Time.unscaledTime;
                state.ArmorChecked = false;
            }
            if (state.ArmorChecked || Time.unscaledTime - state.ArmorShownAt < 1f)
                return;
            state.ArmorChecked = true;
            EnemyCombatBars? bars = state.EnemyBars;
            CombatCharacterModel? enemy = !Obj.IsNull(bars) && Obj.Alive(bars) ? bars.characterModel : null;
            IntPtr battle = enemy != null ? enemy.Pointer : IntPtr.Zero;
            if (state.ArmorReported && battle == state.ArmorBattle)
                return;
            state.ArmorReported = true;
            state.ArmorBattle = battle;
            TMP_Text? text = state.ArmorText;
            string amount = !Obj.IsNull(text) && Obj.Alive(text) ? text.text : "?";
            ModLog.Info($"Armor badge ({amount}) placed beside the 2D enemy meters at x {x:0}, y {y:0} of a 1080-high view" +
                        (state.MetersBelowPanel ? ", below the enemy info panel with the meters." :
                         state.ArmorBelowPanel ? ", below the enemy info panel." : "."));
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
            Vector3 nativePosition = Obj.Alive(parent)
                ? parent.TransformPoint(state.NativeCanvasPosition)
                : state.NativeCanvasPosition;
            Quaternion nativeRotation = Obj.Alive(parent)
                ? parent.rotation * state.NativeCanvasRotation
                : state.NativeCanvasRotation;
            Vector3 nativeVinesPosition = UMath.Add(nativePosition, nativeRotation *
                UMath.Scale(canvas.lossyScale, state.NativeVinesPosition));

            canvas.SetPositionAndRotation(Position(camera, 0f, 0f, depth, halfWidth), facing);
            state.LastCanvasPosition = canvas.localPosition;
            state.CanvasPositionWritten = true;

            if (Obj.Alive(state.Vines))
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
            if (!state.Modified || !Obj.Alive(state.View) || !Obj.Alive(state.Canvas)) return;
            state.NativeCanvasPosition = ChangedAxes(state.NativeCanvasPosition,
                state.Canvas.localPosition, state.LastCanvasPosition);
            foreach (NativeTransform original in state.Originals) original.CaptureAnimation();
            if (Obj.Alive(state.Armor) && UMath.NotEqual(state.Armor.localScale, state.LastArmorScale))
                state.ArmorAnimationScale = state.Armor.localScale;

            state.Canvas.localPosition = state.NativeCanvasPosition;
            state.Canvas.localRotation = state.NativeCanvasRotation;
            if (Obj.Alive(state.Vines))
            {
                state.Vines.localPosition = state.NativeVinesPosition;
                state.Vines.localRotation = state.NativeVinesRotation;
                state.Vines.localScale = state.NativeVinesScale;
            }
            foreach (NativeTransform original in state.Originals) original.Restore();
            if (Obj.Alive(state.Armor)) state.Armor.localScale = state.ArmorAnimationScale;

            foreach (MaskClip clip in state.Masks)
                if (Obj.Alive(clip.Mask)) clip.Mask.enabled = true;
            foreach (GraphicClip clip in state.Graphics)
            {
                if (!Obj.Alive(clip.Graphic)) continue;
                clip.Graphic.SetClipRect(new Rect(), false);
                clip.Graphic.Cull(new Rect(-1000000f, -1000000f, 2000000f, 2000000f), true);
                clip.Graphic.RecalculateClipping();
            }
            foreach (MaskClip clip in state.Masks)
                if (Obj.Alive(clip.Mask) && clip.Mask.isActiveAndEnabled) clip.Mask.PerformClipping();

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

            // Optimizing works out a mask's bounds the first time a shown graphic reads them in this
            // pass, rather than every mask's up front. Only the graphics' clip rectangles and cull
            // states change in between, so the numbers are the same. It doesn't recheck the mask's
            // RectTransform, which can't be destroyed while the mask lives, and reads the padding
            // field that the padding getter only copies.
            bool lazy = Performance.Optimizing;
            int pass = ++clipPass;
            for (int i = 0; i < state.Masks.Count; i++)
            {
                MaskClip mask = state.Masks[i];
                mask.Pass = pass;
                if (!Obj.Alive(mask.Mask) || !lazy && !Obj.Alive(mask.Rect))
                {
                    mask.Valid = false;
                    continue;
                }
                if (mask.Mask.enabled)
                    mask.Mask.enabled = false;
                if (lazy)
                    mask.Pass = 0;
                else
                    MaskBounds(state, mask, false, out mask.Valid, out mask.Bounds);
            }
            if (lazy && UMath.Shadow) QaFullPass(state, pass);

            for (int i = 0; i < state.Graphics.Count; i++)
            {
                GraphicClip target = state.Graphics[i];
                MaskableGraphic graphic = target.Graphic;
                if (!Obj.Alive(graphic) || !graphic.isActiveAndEnabled)
                    continue;
                MaskClip first = Ready(state, target.Masks[0], pass);
                Rect bounds = first.Bounds;
                bool valid = first.Valid;
                for (int j = 1; j < target.Masks.Length; j++)
                {
                    MaskClip next = Ready(state, target.Masks[j], pass);
                    valid &= next.Valid;
                    bounds = UMath.MinMax(UMath.Max(UMath.XMin(bounds), UMath.XMin(next.Bounds)),
                                          UMath.Max(UMath.YMin(bounds), UMath.YMin(next.Bounds)),
                                          UMath.Min(UMath.XMax(bounds), UMath.XMax(next.Bounds)),
                                          UMath.Min(UMath.YMax(bounds), UMath.YMax(next.Bounds)));
                }
                valid &= UMath.Width(bounds) > 0f && UMath.Height(bounds) > 0f;
                graphic.SetClipRect(bounds, valid);
                // Let Unity change the cull state and notify dirty graphics, so
                // an empty meter rebuilds correctly when it begins filling.
                graphic.Cull(bounds, valid);
            }
        }

        /// <summary>A mask's bounds for this pass, worked out now if they haven't been yet (Optimizing).</summary>
        private static MaskClip Ready(LayoutState state, MaskClip mask, int pass)
        {
            if (mask.Pass != pass)
            {
                mask.Pass = pass;
                MaskBounds(state, mask, true, out mask.Valid, out mask.Bounds);
            }
            if (mask.QaPass == pass)
            {
                mask.QaPass = 0;
                bool same = mask.Valid == mask.QaValid && (mask.QaDead || UMath.SameRect(mask.Bounds, mask.QaBounds));
                UMath.Compare("meter clipping bounds", same, same ? null :
                    $"on first use valid {mask.Valid}, bounds {UMath.Show(mask.Bounds)}; " +
                    $"full pass valid {mask.QaValid}, bounds {UMath.Show(mask.QaBounds)}");
            }
            return mask;
        }

        /// <summary>
        /// The mask's padded rectangle in the canvas plane. The meters' fill components write the
        /// padding, and a padding that meets itself leaves nothing shown (valid false).
        /// </summary>
        private static void MaskBounds(LayoutState state, MaskClip mask, bool paddingField,
                                       out bool valid, out Rect bounds)
        {
            Rect rect = mask.Rect.rect;
            Vector4 padding = paddingField ? mask.Mask.m_Padding : mask.Mask.padding;
            float left = UMath.XMin(rect) + padding.x;
            float bottom = UMath.YMin(rect) + padding.y;
            float right = UMath.XMax(rect) - padding.z;
            float top = UMath.YMax(rect) - padding.w;
            valid = right > left && top > bottom;
            if (!valid)
            {
                bounds = new Rect();
                return;
            }

            Transform canvas = state.Canvas;
            Vector3 a = canvas.InverseTransformPoint(mask.Rect.TransformPoint(UMath.V3(left, bottom, 0f)));
            Vector3 b = canvas.InverseTransformPoint(mask.Rect.TransformPoint(UMath.V3(left, top, 0f)));
            Vector3 c = canvas.InverseTransformPoint(mask.Rect.TransformPoint(UMath.V3(right, top, 0f)));
            Vector3 d = canvas.InverseTransformPoint(mask.Rect.TransformPoint(UMath.V3(right, bottom, 0f)));
            bounds = UMath.MinMax(
                UMath.Min(UMath.Min(a.x, b.x), UMath.Min(c.x, d.x)),
                UMath.Min(UMath.Min(a.y, b.y), UMath.Min(c.y, d.y)),
                UMath.Max(UMath.Max(a.x, b.x), UMath.Max(c.x, d.x)),
                UMath.Max(UMath.Max(a.y, b.y), UMath.Max(c.y, d.y)));
        }

        /// <summary>QA builds: the old pass over every mask, before any graphic's clipping, to compare with.</summary>
        private static void QaFullPass(LayoutState state, int pass)
        {
            foreach (MaskClip mask in state.Masks)
            {
                mask.QaPass = pass;
                mask.QaDead = !Obj.Alive(mask.Mask) || !Obj.Alive(mask.Rect);
                if (mask.QaDead)
                    mask.QaValid = false;
                else
                    MaskBounds(state, mask, false, out mask.QaValid, out mask.QaBounds);
            }
        }

        private static void RefreshClipping(LayoutState state)
        {
            state.Masks.Clear();
            state.Graphics.Clear();
            // Optimizing finds a graphic's masks by their transforms' addresses instead of asking the
            // game for instance ids: within this one pass each transform is one object at one address.
            bool byAddress = Performance.Optimizing;
            var byTransform = new Dictionary<long, MaskClip>();
            Dictionary<int, MaskClip>? qaById = byAddress && UMath.Shadow ? new Dictionary<int, MaskClip>() : null;
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
                    byTransform[Key(clip.Rect, byAddress)] = clip;
                    if (qaById != null) qaById[clip.Rect.GetInstanceID()] = clip;
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
                    while (Obj.Alive(current) && !Obj.Same(current, state.Canvas))
                    {
                        MaskClip mask;
                        bool found = false;
                        if (byTransform.TryGetValue(Key(current, byAddress), out mask))
                        {
                            found = true;
                            ancestors.Add(mask);
                        }
                        if (qaById != null)
                        {
                            bool same = qaById.TryGetValue(current.GetInstanceID(), out MaskClip? old) == found &&
                                        (!found || ReferenceEquals(old, mask));
                            UMath.Compare("meter clipping masks", same, same ? null : $"a transform found by address {found}, by id {old != null}");
                        }
                        current = current.parent;
                    }
                    if (ancestors.Count != 0)
                        state.Graphics.Add(new GraphicClip { Graphic = graphic, Masks = ancestors.ToArray() });
                }
            }
            // A periodic refresh also catches scrolling-text copies created
            // when a previously inactive status message first becomes visible.
        }

        private static long Key(Transform transform, bool byAddress) =>
            byAddress ? transform.Pointer.ToInt64() : transform.GetInstanceID();

        private static Vector3 Position(Camera camera, float x, float y, float depth, float halfWidth)
        {
            return camera.ViewportToWorldPoint(UMath.V3(0.5f + x / (2f * halfWidth),
                                                        0.5f + y / ReferenceHeight, depth));
        }

        private static void Place(Transform target, Camera camera, float x, float y,
                                  float depth, float halfWidth, Quaternion rotation, float scale)
        {
            if (!Obj.Alive(target))
                return;
            target.SetPositionAndRotation(Position(camera, x, y, depth, halfWidth), rotation);
            target.localScale = LocalScale(target, scale);
        }

        private static Vector3 LocalScale(Transform target, float worldScale)
        {
            Transform parent = target.parent;
            if (!Obj.Alive(parent))
                return UMath.OneTimes(worldScale);
            Vector3 inherited = parent.lossyScale;
            return UMath.V3(worldScale / SafeScale(inherited.x),
                            worldScale / SafeScale(inherited.y),
                            worldScale / SafeScale(inherited.z));
        }

        private static float SafeScale(float value)
        {
            return UMath.Abs(value) < 0.000001f ? 0.000001f : value;
        }
    }
}
