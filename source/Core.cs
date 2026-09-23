using UnityEngine;

namespace NocturneFlatScroll;

internal static class ModInfo
{
    public const string Id = "local.nocturne.flat-scroll";
    public const string Name = "Nocturne Flat Scroll";
    public const string Version = "2.3.0";
}

/// <summary>Routes messages to whichever loader started the mod.</summary>
internal static class ModLog
{
    private static Action<string>? _info;
    private static Action<string>? _error;

    public static void Initialize(Action<string> info, Action<string> error)
    {
        _info = info;
        _error = error;
    }

    public static void Info(string message) => _info?.Invoke(message);
    public static void Error(string message) => _error?.Invoke(message);
}

public enum ScrollMode
{
    Default = 0,
    Downscroll2D = 1,
    Upscroll2D = 2
}

public enum NoteSkin
{
    Default = 0,
    Circle = 1,
    Arrow = 2
}

public static class SettingsState
{
    private const string Key = "NocturneFlatScroll.Mode.v3";
    private const string PreviousKey = "NocturneFlatScroll.Direction.v2";
    private const string ReceptorKey = "NocturneFlatScroll.ReceptorHeight.v1";
    private const string SkinKey = "NocturneFlatScroll.NoteSkin.v1";
    private const string TimingBarKey = "NocturneFlatScroll.TimingBar.v1";
    public const int MinReceptorHeight = -10;
    public const int MaxReceptorHeight = 30;
    private static ScrollMode? _mode;
    private static int? _receptorHeight;
    private static NoteSkin? _noteSkin;
    private static bool? _timingBar;
    public static ScrollMode Mode => _mode ??= LoadMode();

    /// <summary>How notes and receptors are drawn; Default keeps the game's own bars.</summary>
    public static NoteSkin NoteSkin => _noteSkin ??= LoadNoteSkin();

    /// <summary>Whether the early/late timing bar is shown during combat.</summary>
    public static bool TimingBar => _timingBar ??= PlayerPrefs.GetInt(TimingBarKey, 0) == 1;

    private static NoteSkin LoadNoteSkin()
    {
        int saved = PlayerPrefs.GetInt(SkinKey, (int)NoteSkin.Default);
        return saved >= (int)NoteSkin.Default && saved <= (int)NoteSkin.Arrow ? (NoteSkin)saved : NoteSkin.Default;
    }

    public static void SetNoteSkin(NoteSkin value)
    {
        if (value < NoteSkin.Default || value > NoteSkin.Arrow)
            throw new ArgumentOutOfRangeException(nameof(value));
        _noteSkin = value;
        PlayerPrefs.SetInt(SkinKey, (int)value);
        PlayerPrefs.Save();
        ModLog.Info("Note skin: " + value);
    }

    public static void SetTimingBar(bool value)
    {
        _timingBar = value;
        PlayerPrefs.SetInt(TimingBarKey, value ? 1 : 0);
        PlayerPrefs.Save();
        ModLog.Info("Timing bar: " + (value ? "On" : "Off"));
    }

    /// <summary>
    /// Percent of screen height that the 2D receptors move in from their screen edge:
    /// up in downscroll and down in upscroll. Zero keeps the original position.
    /// </summary>
    public static int ReceptorHeight => _receptorHeight ??= LoadReceptorHeight();

    private static ScrollMode LoadMode()
    {
        if (PlayerPrefs.HasKey(Key))
        {
            int saved = PlayerPrefs.GetInt(Key, (int)ScrollMode.Default);
            return saved >= (int)ScrollMode.Default && saved <= (int)ScrollMode.Upscroll2D
                ? (ScrollMode)saved : ScrollMode.Default;
        }
        if (!PlayerPrefs.HasKey(PreviousKey)) return ScrollMode.Default;

        // Preserve an existing two-state selection when upgrading the plugin.
        var migrated = PlayerPrefs.GetInt(PreviousKey, 1) != 0
            ? ScrollMode.Upscroll2D : ScrollMode.Downscroll2D;
        PlayerPrefs.SetInt(Key, (int)migrated);
        PlayerPrefs.Save();
        return migrated;
    }

    private static int LoadReceptorHeight()
    {
        int saved = PlayerPrefs.GetInt(ReceptorKey, 0);
        return saved >= MinReceptorHeight && saved <= MaxReceptorHeight ? saved : 0;
    }

    public static void SetMode(ScrollMode value)
    {
        if (value < ScrollMode.Default || value > ScrollMode.Upscroll2D)
            throw new ArgumentOutOfRangeException(nameof(value));
        _mode = value;
        PlayerPrefs.SetInt(Key, (int)value);
        PlayerPrefs.Save();
        ModLog.Info("Note scrolling: " + (value switch
        {
            ScrollMode.Downscroll2D => "2D Downscroll",
            ScrollMode.Upscroll2D => "2D Upscroll",
            _ => "Default"
        }));
    }

    public static void SetReceptorHeight(int value)
    {
        if (value < MinReceptorHeight || value > MaxReceptorHeight)
            throw new ArgumentOutOfRangeException(nameof(value));
        _receptorHeight = value;
        PlayerPrefs.SetInt(ReceptorKey, value);
        PlayerPrefs.Save();
        ModLog.Info("Receptor height: " + FormatReceptorHeight(value));
    }

    public static string FormatReceptorHeight(int value) => value > 0 ? $"+{value}%" : $"{value}%";
}

/// <summary>Installs every feature's patches; a failing feature does not stop the others.</summary>
internal static class ModSetup
{
    internal static void Patch(HarmonyLib.Harmony harmony)
    {
        Run("Options menu rows", () => OptionsMenuIntegration.Install(harmony));
        Run("Note skins", () => NoteSkins.Install(harmony));
        Run("Timing bar", () => TimingBar.Install(harmony));
        Run("Title text", () => TitleBranding.InstallTitle(harmony));
        Run("Intro text", () => TitleBranding.InstallIntro(harmony));
    }

    /// <summary>Covers menus that already existed before the patches were installed.</summary>
    internal static void AttachToExisting()
    {
        Run("Options menu rows", OptionsMenuIntegration.AttachToExistingMenus);
        Run("Title text", TitleBranding.AttachToExisting);
    }

    private static void Run(string feature, Action action)
    {
        try { action(); }
        catch (Exception ex) { ModLog.Error($"{feature} could not be installed: {ex}"); }
    }
}

/// <summary>Per-frame layout work shared by the BepInEx and MelonLoader entry points.</summary>
internal static class LayoutDriver
{
    private static float _nextDiscovery;
    private static readonly List<CombatNoteFieldView> Views = new();
    private static readonly Dictionary<int, Camera> Cameras = new();
    private static readonly Dictionary<int, NoteFieldBehaviour> NoteFields = new();
    private static readonly Dictionary<int, Transform> FieldTransforms = new();
    private static readonly List<(Transform Field, int Columns)> SkinFields = new();
    private static bool _reportedError;

    public static void Update()
    {
        if (Time.unscaledTime < _nextDiscovery) return;
        _nextDiscovery = Time.unscaledTime + 1f;
        try
        {
            Views.Clear();
            foreach (var view in Resources.FindObjectsOfTypeAll<CombatNoteFieldView>())
            {
                if (!view || view.gameObject.scene.handle == 0) continue;
                Views.Add(view);
                var id = view.GetInstanceID();
                // Looked up here, once a second, rather than by path every frame.
                if (!FieldTransforms.TryGetValue(id, out var cachedField) || !cachedField)
                {
                    var found = view.transform.Find("FieldPivot/Field");
                    if (found) FieldTransforms[id] = found;
                }
                if (!Cameras.TryGetValue(id, out var camera) || !camera)
                {
                    // CombatCamera_02 is a Cinemachine virtual camera, not a renderer.
                    // The actual camera lives in the persistent CameraManager hierarchy.
                    camera = null;
                    foreach (var candidate in Resources.FindObjectsOfTypeAll<Camera>())
                    {
                        if (!candidate || candidate.gameObject.scene.handle == 0 ||
                            candidate.name != "Camera (Combat)" || candidate.orthographic ||
                            (candidate.cullingMask & (1 << 15)) == 0) continue;
                        camera = candidate;
                        if (candidate.isActiveAndEnabled) break;
                    }
                    if (camera)
                    {
                        Cameras[id] = camera;
                        ModLog.Info("Combat layout camera: " + camera.name);
                    }
                }
            }
            MenuFieldLayout.Discover();
        }
        catch (Exception ex) { Report(ex); }
    }

    public static void LateUpdate()
    {
        try
        {
            var mode = SettingsState.Mode;
            MenuFieldLayout.Apply(mode);
            SkinFields.Clear();
            foreach (var view in Views)
            {
                if (!view) continue;
                int id = view.GetInstanceID();
                Cameras.TryGetValue(id, out var camera);
                FieldTransforms.TryGetValue(id, out var field);
                if (mode != ScrollMode.Default && !view.gameObject.activeInHierarchy)
                {
                    UpdateTimingBar(view, field, camera);
                    continue;
                }
                // Restore captured native transforms even while a previously modified view is hidden.
                FieldLayout.Apply(view, mode);
                if (camera) HudLayout.Apply(view, camera, mode);
                UpdateTimingBar(view, field, camera);
                if (field) SkinFields.Add((field!, ColumnCount(view)));
            }
            MenuFieldLayout.AddSkinFields(SkinFields);
        }
        catch (Exception ex) { Report(ex); }

        // The optional features fail on their own, so an error in one of them never stops
        // the layout above or the other feature.
        try { NoteSkins.LateUpdate(SkinFields); }
        catch (Exception ex) { ReportOnce(ref _reportedSkinError, "Note skins failed: ", ex); }
    }

    private static void UpdateTimingBar(CombatNoteFieldView view, Transform? field, Camera? camera)
    {
        if (!field) return;
        try { TimingBar.LateUpdate(view, field!, camera); }
        catch (Exception ex) { ReportOnce(ref _reportedBarError, "Timing bar failed: ", ex); }
    }

    private static bool _reportedSkinError, _reportedBarError;

    private static void ReportOnce(ref bool reported, string prefix, Exception ex)
    {
        if (reported) return;
        reported = true;
        ModLog.Error(prefix + ex);
    }

    private static int ColumnCount(CombatNoteFieldView view)
    {
        int id = view.GetInstanceID();
        if (!NoteFields.TryGetValue(id, out var noteField) || !noteField)
        {
            noteField = view.GetComponentInChildren<NoteFieldBehaviour>(true);
            if (!noteField) return 4;
            NoteFields[id] = noteField;
        }
        int count = noteField.ActiveColumnCount;
        return count > 0 ? count : 4;
    }

    private static void Report(Exception ex) => ReportOnce(ref _reportedError, "", ex);
}
