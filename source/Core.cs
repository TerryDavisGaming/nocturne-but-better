using UnityEngine;

namespace NocturneFlatScroll;

internal static class ModInfo
{
    public const string Id = "local.nocturne.flat-scroll";
    public const string Name = "Nocturne Flat Scroll";
    public const string Version = "2.2.0";
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

public static class SettingsState
{
    private const string Key = "NocturneFlatScroll.Mode.v3";
    private const string PreviousKey = "NocturneFlatScroll.Direction.v2";
    private const string ReceptorKey = "NocturneFlatScroll.ReceptorHeight.v1";
    public const int MinReceptorHeight = -10;
    public const int MaxReceptorHeight = 30;
    private static ScrollMode? _mode;
    private static int? _receptorHeight;
    public static ScrollMode Mode => _mode ??= LoadMode();

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

/// <summary>Per-frame layout work shared by the BepInEx and MelonLoader entry points.</summary>
internal static class LayoutDriver
{
    private static float _nextDiscovery;
    private static readonly List<CombatNoteFieldView> Views = new();
    private static readonly Dictionary<int, Camera> Cameras = new();
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
            foreach (var view in Views)
            {
                if (!view || (mode != ScrollMode.Default && !view.gameObject.activeInHierarchy)) continue;
                // Restore captured native transforms even while a previously modified view is hidden.
                FieldLayout.Apply(view, mode);
                if (Cameras.TryGetValue(view.GetInstanceID(), out var camera) && camera)
                    HudLayout.Apply(view, camera, mode);
            }
        }
        catch (Exception ex) { Report(ex); }
    }

    private static void Report(Exception ex)
    {
        if (_reportedError) return;
        _reportedError = true;
        ModLog.Error(ex.ToString());
    }
}
