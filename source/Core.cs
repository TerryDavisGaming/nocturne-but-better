using UnityEngine;

namespace NocturneFlatScroll;

internal static class ModInfo
{
    public const string Id = "local.nocturne.flat-scroll";
    public const string Name = "Nocturne But Better";
    public const string Version = "2.4.1";
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
    private const string HitSoundKey = "NocturneFlatScroll.HitSound.v1";
    private const string TimingBarTopKey = "NocturneFlatScroll.TimingBarPosition.v1";
    private const string NoteFlaresKey = "NocturneFlatScroll.NoteFlares.v1";
    public const int MinReceptorHeight = -10;
    public const int MaxReceptorHeight = 30;
    private static ScrollMode? _mode;
    private static int? _receptorHeight;
    private static NoteSkin? _noteSkin;
    private static bool? _timingBar;
    private static bool? _hitSound;
    private static bool? _timingBarTop;
    private static bool? _noteFlares;
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
    /// Where the timing bar goes in 2D upscroll: false puts it just below the receptors
    /// (below the enemy), true at the top of the screen above the enemy.
    /// </summary>
    public static bool TimingBarTop => _timingBarTop ??= PlayerPrefs.GetInt(TimingBarTopKey, 0) == 1;

    public static void SetTimingBarTop(bool value)
    {
        _timingBarTop = value;
        PlayerPrefs.SetInt(TimingBarTopKey, value ? 1 : 0);
        PlayerPrefs.Save();
        ModLog.Info("Timing bar position: " + (value ? "Above enemy" : "Below enemy"));
    }

    /// <summary>Whether the bursts on the receptors show when notes are hit. On unless turned off.</summary>
    public static bool NoteFlares => _noteFlares ??= PlayerPrefs.GetInt(NoteFlaresKey, 1) == 1;

    public static void SetNoteFlares(bool value)
    {
        _noteFlares = value;
        PlayerPrefs.SetInt(NoteFlaresKey, value ? 1 : 0);
        PlayerPrefs.Save();
        ModLog.Info("Note flares: " + (value ? "On" : "Off"));
    }

    /// <summary>Whether a tick plays when the player hits a note. Off unless turned on.</summary>
    public static bool HitSound => _hitSound ??= PlayerPrefs.GetInt(HitSoundKey, 0) == 1;

    public static void SetHitSound(bool value)
    {
        _hitSound = value;
        PlayerPrefs.SetInt(HitSoundKey, value ? 1 : 0);
        PlayerPrefs.Save();
        ModLog.Info("Hit sound: " + (value ? "On" : "Off"));
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

    /// <summary>Size of 2D notes and receptors, in percent of the original.</summary>
    internal static readonly PercentSetting NoteSize =
        new("NocturneFlatScroll.NoteSize.v1", "Note size", 50, 150, 5, 100);

    /// <summary>Distance between 2D lanes, in percent of the original.</summary>
    internal static readonly PercentSetting LaneSpacing =
        new("NocturneFlatScroll.LaneSpacing.v1", "Lane spacing", 60, 150, 5, 100);

    /// <summary>How loud the hit sound is, on top of the game's own sound volumes.</summary>
    internal static readonly PercentSetting HitSoundVolume =
        new("NocturneFlatScroll.HitSoundVolume.v1", "Hit sound volume", 5, 100, 5, 80);

    /// <summary>How loud the game's miss sounds are, in percent of their normal level.</summary>
    internal static readonly PercentSetting MissSoundVolume =
        new("NocturneFlatScroll.MissSoundVolume.v1", "Miss sound volume", 10, 300, 10, 100);

    /// <summary>How visible the enemy is while it attacks, in percent. 100 leaves it untouched.</summary>
    internal static readonly PercentSetting EnemyAttackOpacity =
        new("NocturneFlatScroll.EnemyAttackOpacity.v1", "Enemy attack opacity", 0, 100, 10, 100);
}

/// <summary>A saved percentage that moves in fixed steps between a minimum and a maximum.</summary>
internal sealed class PercentSetting
{
    private readonly string _key;
    private readonly string _name;
    private int? _value;
    public readonly int Min, Max, Step, Default;

    public PercentSetting(string key, string name, int min, int max, int step, int defaultValue)
    {
        _key = key;
        _name = name;
        Min = min;
        Max = max;
        Step = step;
        Default = defaultValue;
    }

    public int Value => _value ??= Load();
    public float Factor => Value / 100f;
    public int Count => (Max - Min) / Step + 1;
    public int Index => (Value - Min) / Step;

    public static string Format(int value) => value + "%";

    public string[] Labels()
    {
        var labels = new string[Count];
        for (int i = 0; i < labels.Length; i++) labels[i] = Format(Min + i * Step);
        return labels;
    }

    private int Load()
    {
        int saved = PlayerPrefs.GetInt(_key, Default);
        return saved >= Min && saved <= Max && (saved - Min) % Step == 0 ? saved : Default;
    }

    public void Set(int value)
    {
        if (value < Min || value > Max || (value - Min) % Step != 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        _value = value;
        PlayerPrefs.SetInt(_key, value);
        PlayerPrefs.Save();
        ModLog.Info(_name + ": " + Format(value));
    }

    /// <param name="wrap">Clicking cycles through every value; left and right stop at the ends.</param>
    public void Change(int direction, bool wrap)
    {
        int value = Value + direction * Step;
        if (value > Max) value = wrap ? Min : Max;
        else if (value < Min) value = wrap ? Max : Min;
        if (value != Value) Set(value);
    }
}

/// <summary>Installs every feature's patches; a failing feature does not stop the others.</summary>
internal static class ModSetup
{
    internal static void Patch(HarmonyLib.Harmony harmony)
    {
        Run("Options menu rows", () => OptionsMenuIntegration.Install(harmony));
        Run("Audio options rows", () => AudioOptionsIntegration.Install(harmony));
        Run("Note skins", () => NoteSkins.Install(harmony));
        Run("Timing bar", () => TimingBar.Install(harmony));
        Run("Hit sound", () => HitSound.Install(harmony));
        Run("Miss sound", () => MissSound.Install(harmony));
        Run("Ready key filter", () => ReadyKeyFilter.Install(harmony));
        Run("Note color preview", () => NoteColorPreview.Install(harmony));
        Run("Title text", () => TitleBranding.InstallTitle(harmony));
        Run("Intro text", () => TitleBranding.InstallIntro(harmony));
    }

    /// <summary>Covers menus that already existed before the patches were installed.</summary>
    internal static void AttachToExisting()
    {
        Run("Options menu rows", OptionsMenuIntegration.AttachToExistingMenus);
        Run("Audio options rows", AudioOptionsIntegration.AttachToExisting);
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
                if (!view.gameObject.activeInHierarchy) FadeAttacks(view, false);
                if (mode != ScrollMode.Default && !view.gameObject.activeInHierarchy)
                {
                    FieldLayout.Hidden(view);
                    UpdateTimingBar(view, field, camera);
                    continue;
                }
                // Restore captured native transforms even while a previously modified view is hidden.
                FieldLayout.Apply(view, mode);
                if (camera) HudLayout.Apply(view, camera, mode);
                UpdateTimingBar(view, field, camera);
                if (view.gameObject.activeInHierarchy) FadeAttacks(view, true);
                if (field) SkinFields.Add((field!, ColumnCount(view)));
            }
            MenuFieldLayout.AddSkinFields(SkinFields);
        }
        catch (Exception ex) { Report(ex); }

        // The optional features fail on their own, so an error in one of them never stops
        // the layout above or the other feature.
        try { NoteSkins.LateUpdate(SkinFields); }
        catch (Exception ex) { ReportOnce(ref _reportedSkinError, "Note skins failed: ", ex); }
        HitSound.Update();
        MissSound.Update();
    }

    private static void FadeAttacks(CombatNoteFieldView view, bool active)
    {
        try { EnemyAttackFade.LateUpdate(view, active); }
        catch (Exception ex) { ReportOnce(ref _reportedFadeError, "Enemy attack opacity failed: ", ex); }
    }

    private static void UpdateTimingBar(CombatNoteFieldView view, Transform? field, Camera? camera)
    {
        if (!field) return;
        try { TimingBar.LateUpdate(view, field!, camera); }
        catch (Exception ex) { ReportOnce(ref _reportedBarError, "Timing bar failed: ", ex); }
    }

    private static bool _reportedSkinError, _reportedBarError, _reportedFadeError;

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
