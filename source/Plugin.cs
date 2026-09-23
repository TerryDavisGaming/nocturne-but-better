using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Nocturne;
using UnityEngine;

namespace NocturneFlatScroll;

[BepInPlugin("local.nocturne.flat-scroll", "Nocturne Flat Scroll", "2.1.2")]
public sealed class Plugin : BasePlugin
{
    public new static ManualLogSource Log = null!;
    public override void Load()
    {
        Log = base.Log;
        OptionsMenuIntegration.Install(new Harmony("local.nocturne.flat-scroll"));
        AddComponent<LayoutController>();
        Log.LogInfo("Flat scrolling loaded; select a layout in Options > Gameplay > Note scrolling.");
    }
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
    private static ScrollMode? _mode;
    public static ScrollMode Mode => _mode ??= LoadMode();

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

    public static void SetMode(ScrollMode value)
    {
        if (value < ScrollMode.Default || value > ScrollMode.Upscroll2D)
            throw new ArgumentOutOfRangeException(nameof(value));
        _mode = value;
        PlayerPrefs.SetInt(Key, (int)value);
        PlayerPrefs.Save();
        Plugin.Log.LogInfo("Note scrolling: " + (value switch
        {
            ScrollMode.Downscroll2D => "2D Downscroll",
            ScrollMode.Upscroll2D => "2D Upscroll",
            _ => "Default"
        }));
    }
}

public sealed class LayoutController : MonoBehaviour
{
    public LayoutController(IntPtr pointer) : base(pointer) { }
    private float _nextDiscovery;
    private readonly List<CombatNoteFieldView> _views = new();
    private readonly Dictionary<int, Camera> _cameras = new();
    private bool _reportedError;

    public void Update()
    {
        if (Time.unscaledTime < _nextDiscovery) return;
        _nextDiscovery = Time.unscaledTime + 1f;
        try
        {
            _views.Clear();
            foreach (var view in Resources.FindObjectsOfTypeAll<CombatNoteFieldView>())
            {
                if (!view || view.gameObject.scene.handle == 0) continue;
                _views.Add(view);
                var id = view.GetInstanceID();
                if (!_cameras.TryGetValue(id, out var camera) || !camera)
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
                        _cameras[id] = camera;
                        Plugin.Log.LogInfo("Combat layout camera: " + camera.name);
                    }
                }
            }
            MenuFieldLayout.Discover();
        }
        catch (Exception ex) { Report(ex); }
    }

    public void LateUpdate()
    {
        try
        {
            var mode = SettingsState.Mode;
            MenuFieldLayout.Apply(mode);
            foreach (var view in _views)
            {
                if (!view || (mode != ScrollMode.Default && !view.gameObject.activeInHierarchy)) continue;
                // Restore captured native transforms even while a previously modified view is hidden.
                FieldLayout.Apply(view, mode);
                if (_cameras.TryGetValue(view.GetInstanceID(), out var camera) && camera)
                    HudLayout.Apply(view, camera, mode);
            }
        }
        catch (Exception ex) { Report(ex); }
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void Report(Exception ex)
    {
        if (_reportedError) return;
        _reportedError = true;
        Plugin.Log.LogError(ex);
    }
}
