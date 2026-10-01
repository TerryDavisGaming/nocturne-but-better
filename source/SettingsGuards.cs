using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using EventSystem = UnityEngine.EventSystems.EventSystem;
using Gamepad = UnityEngine.InputSystem.Gamepad;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;

namespace NocturnePlus;

/// <summary>
/// Keeps the player's volume settings safe from two game bugs in Options > Latency, and logs every
/// change the game's own Window Mode row and volume sliders make, with what made it, so a setting
/// that seems to change by itself can be traced in the log.
/// <list type="bullet">
/// <item>The latency test's video step (VideoCalibrationMenu) puts master volume back, when it's
/// destroyed while showing (the game closing then), to a value it only notes when it mutes the
/// sound. It never mutes (nothing calls its Mute), so that value is always 0 and master volume
/// was saved at 0%.</item>
/// <item>The latency test's song (CalibrationMenuV2) saves Overworld SFX at 0 when it starts and
/// only puts it back when the song ends the usual way. Leaving the page any other way left it at 0.</item>
/// </list>
/// Each hook is on a method that returns nothing and takes no float or struct, so both loaders can
/// patch it. The guards and the log don't depend on the Performance setting.
/// </summary>
internal static class SettingsGuards
{
    internal static void Install(HarmonyLib.Harmony harmony)
    {
        // Each hook goes in on its own: one the game no longer has doesn't stop the others.
        Patch(harmony, typeof(VideoCalibrationMenu), "OnDestroy", Type.EmptyTypes, prefix: nameof(VideoOnDestroyPrefix));
        Patch(harmony, typeof(CalibrationMenuV2), "Deactivate", new[] { typeof(bool), typeof(bool) }, postfix: nameof(CalibrationLeftPostfix));
        Patch(harmony, typeof(CalibrationMenuV2), "OnDisable", Type.EmptyTypes, postfix: nameof(CalibrationLeftPostfix));
        Patch(harmony, typeof(NocturneSettings), "ToggleScreenMode", Type.EmptyTypes, nameof(ScreenModePrefix), nameof(ScreenModePostfix));
        foreach (var volume in Volumes)
            Patch(harmony, typeof(AudioOptionsMenu), volume.Method, new[] { typeof(int) }, nameof(VolumePrefix), nameof(VolumePostfix));
    }

    private static void Patch(HarmonyLib.Harmony harmony, Type type, string method, Type[] parameters, string? prefix = null, string? postfix = null)
    {
        try
        {
            var target = AccessTools.DeclaredMethod(type, method, parameters) ?? throw new MissingMethodException(type.FullName, method);
            harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(SettingsGuards), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(SettingsGuards), postfix));
        }
        catch (Exception ex) { ModLog.Error($"Settings guards: {type.Name}.{method} could not be patched, so it runs as the game has it: {ex}"); }
    }

    private static readonly HashSet<string> reported = new();

    // One line per kind of error, so a hook that keeps failing doesn't fill the log.
    private static void ReportOnce(string what, Exception ex)
    {
        if (reported.Add(what)) ModLog.Error($"Settings guards: {what} failed: {ex}");
    }

    private static string Percent(float volume) => Math.Round(volume * 100f).ToString(CultureInfo.InvariantCulture) + "%";

    // ---- the latency test's video step ----------------------------------------------------------

    // Skips the game's OnDestroy while the volume it would put back is the 0 it never noted. The
    // method does nothing else (it only writes master volume, and only while the step is showing).
    // Should Mute ever run, the noted volume isn't 0 and the game's own OnDestroy puts it back.
    private static bool VideoOnDestroyPrefix(VideoCalibrationMenu __instance)
    {
        try
        {
            // "is null" and not Unity's check: the object is being destroyed, and that check may already say it's gone.
            if (__instance is null || __instance.savedVolume != 0f) return true;
            if (__instance.Active)
                ModLog.Info($"Settings guard: the latency test's video step closed while showing; master volume stays at {Percent(NocturneSettings.MasterVolume)} (the game would have saved 0%).");
            return false;
        }
        catch (Exception ex)
        {
            ReportOnce("checking the latency test's video step", ex);
            return true;
        }
    }

    // ---- the latency test's song ----------------------------------------------------------------

    // The page left or turned off with Overworld SFX still silenced for its song: the game's own
    // RestoreOverworldSfx puts back the volume it noted and clears the flag. After the song ends
    // the usual way the flag is already clear, so this does nothing.
    private static void CalibrationLeftPostfix(CalibrationMenuV2 __instance, MethodBase __originalMethod)
    {
        try
        {
            if (__instance is null || !__instance.sfxSilenced) return;
            __instance.RestoreOverworldSfx();
            // The game saves its settings when Options closes, but this can also be the game closing.
            PlayerPrefs.Save();
            ModLog.Info($"Settings guard: the latency test's song stopped without ending ({__originalMethod?.Name}); Overworld SFX is back to {Percent(NocturneSettings.OverworldSfxVolume)}.");
        }
        catch (Exception ex) { ReportOnce("putting back Overworld SFX after the latency test", ex); }
    }

    // ---- the log of setting changes --------------------------------------------------------------

    private sealed class VolumeHandler
    {
        internal readonly string Method, Name;
        internal readonly Func<float> Read;

        internal VolumeHandler(string method, string name, Func<float> read)
        {
            Method = method;
            Name = name;
            Read = read;
        }
    }

    // The Audio page's slider handlers, each with the setting it changes.
    private static readonly VolumeHandler[] Volumes =
    {
        new("ChangeMasterVolume", "Master volume", () => NocturneSettings.MasterVolume),
        new("ChangeMusicVolume", "Music volume", () => NocturneSettings.MusicVolume),
        new("ChangeOverworldMusicVolume", "Overworld music volume", () => NocturneSettings.OverworldMusicVolume),
        new("ChangeCombatMusicVolume", "Combat music volume", () => NocturneSettings.CombatMusicVolume),
        new("ChangeChaseMusicVolume", "Chase music volume", () => NocturneSettings.ChaseMusicVolume),
        new("ChangeSfxVolume", "Sound effects volume", () => NocturneSettings.SfxVolume),
        new("ChangeOverworldSfxVolume", "Overworld SFX volume", () => NocturneSettings.OverworldSfxVolume),
        new("ChangeCombatSfxVolume", "Combat SFX volume", () => NocturneSettings.CombatSfxVolume),
        new("ChangeUISfxVolume", "UI SFX volume", () => NocturneSettings.UISfxVolume),
    };

    // The value before the handler running now (they don't nest, and all run on the main thread).
    private static VolumeHandler? volumeBefore;
    private static float volumeWas;
    private static bool screenNoted;
    private static NocturneScreenMode screenWas;

    private static VolumeHandler? HandlerFor(MethodBase? method)
    {
        string name = method?.Name ?? "";
        foreach (var volume in Volumes)
            if (volume.Method == name) return volume;
        return null;
    }

    private static void VolumePrefix(MethodBase __originalMethod)
    {
        volumeBefore = null;
        try
        {
            var volume = HandlerFor(__originalMethod);
            if (volume == null) return;
            volumeWas = volume.Read();
            volumeBefore = volume;
        }
        catch (Exception ex) { ReportOnce("noting a volume before it changed", ex); }
    }

    private static void VolumePostfix(MethodBase __originalMethod)
    {
        var volume = volumeBefore;
        volumeBefore = null;
        try
        {
            if (volume == null || volume != HandlerFor(__originalMethod)) return;
            float now = volume.Read();
            // The page setting its sliders to the saved values changes nothing.
            if (now != volumeWas) Changed(volume.Name, Percent(volumeWas), Percent(now));
        }
        catch (Exception ex) { ReportOnce("logging a volume change", ex); }
    }

    private static void ScreenModePrefix()
    {
        screenNoted = false;
        try
        {
            screenWas = NocturneSettings.ScreenMode;
            screenNoted = true;
        }
        catch (Exception ex) { ReportOnce("noting the window mode before it changed", ex); }
    }

    private static void ScreenModePostfix()
    {
        if (!screenNoted) return;
        screenNoted = false;
        try
        {
            var now = NocturneSettings.ScreenMode;
            if (now != screenWas) Changed("Window mode", screenWas.ToString(), now.ToString());
        }
        catch (Exception ex) { ReportOnce("logging a window mode change", ex); }
    }

    // A slider dragged or held changes its setting at every step: the first change of a run is
    // logged with what made it, and the value the run ends on once the setting has been still for BurstGap.
    private const float BurstGap = 1f;

    private sealed class Burst
    {
        internal string Setting = "", From = "", Last = "";
        internal int Changes;
        internal float LastAt;
    }

    private static readonly List<Burst> bursts = new();

    private static void Changed(string setting, string from, string to)
    {
        float now = Time.unscaledTime;
        for (int i = 0; i < bursts.Count; i++)
        {
            var burst = bursts[i];
            if (burst.Setting != setting) continue;
            if (now - burst.LastAt <= BurstGap)
            {
                burst.Last = to;
                burst.Changes++;
                burst.LastAt = now;
                return;
            }
            Finish(burst);
            bursts.RemoveAt(i);
            break;
        }
        ModLog.Info($"Settings probe: {setting} {from} -> {to}; input: {InputNow()}; selected: {Selected()}; {ModScreens()}.");
        bursts.Add(new Burst { Setting = setting, From = from, Last = to, Changes = 1, LastAt = now });
    }

    private static void Finish(Burst burst)
    {
        if (burst.Changes > 1) ModLog.Info($"Settings probe: {burst.Setting} ended at {burst.Last} ({burst.Changes} changes from {burst.From}).");
    }

    /// <summary>Called every frame: logs where a run of changes ended once it has stopped.</summary>
    internal static void Update()
    {
        if (bursts.Count == 0) return;
        try
        {
            float now = Time.unscaledTime;
            for (int i = bursts.Count - 1; i >= 0; i--)
            {
                if (now - bursts[i].LastAt <= BurstGap) continue;
                var burst = bursts[i];
                bursts.RemoveAt(i);
                Finish(burst);
            }
        }
        catch (Exception ex)
        {
            bursts.Clear();
            ReportOnce("logging where a setting change ended", ex);
        }
    }

    // What the player was pressing when the setting changed.
    private static string InputNow()
    {
        var said = new List<string>();
        var mouse = InputMouse.current;
        if (mouse != null)
        {
            var left = mouse.leftButton;
            string? button = left.wasPressedThisFrame ? "pressed this frame" : left.wasReleasedThisFrame ? "released this frame" : left.isPressed ? "held" : null;
            if (button != null)
            {
                var at = mouse.position.ReadValue();
                said.Add($"mouse button {button} at {Math.Round(at.x)},{Math.Round(at.y)}");
            }
        }
        var keyboard = InputKeyboard.current;
        if (keyboard != null && keyboard.anyKey.isPressed) said.Add("keyboard");
        var pad = Gamepad.current;
        if (pad != null && PadHeld(pad)) said.Add("pad");
        return said.Count > 0 ? string.Join(" and ", said) : "none seen";
    }

    private static bool PadHeld(Gamepad pad) =>
        pad.buttonSouth.isPressed || pad.buttonEast.isPressed || pad.buttonWest.isPressed || pad.buttonNorth.isPressed ||
        pad.dpad.left.isPressed || pad.dpad.right.isPressed || pad.dpad.up.isPressed || pad.dpad.down.isPressed ||
        pad.leftStick.left.isPressed || pad.leftStick.right.isPressed || pad.leftStick.up.isPressed || pad.leftStick.down.isPressed ||
        pad.leftShoulder.isPressed || pad.rightShoulder.isPressed || pad.startButton.isPressed;

    // The game object the menus have selected, with its parent to tell rows apart.
    private static string Selected()
    {
        var events = EventSystem.current;
        var selected = events != null && events ? events.currentSelectedGameObject : null;
        if (selected == null || !selected) return "nothing";
        var parent = selected.transform.parent;
        return parent != null && parent ? $"{selected.name} (in {parent.name})" : selected.name;
    }

    // Which of the mod's own screens were open over the game's menus.
    private static string ModScreens()
    {
        if (!EditorOverlay.IsOpen) return "no mod screen open";
        var open = new List<string>();
        if (ChartEditor.IsOpen) open.Add("chart editor");
        if (BattleCreator.IsOpen) open.Add("battle creator");
        if (HubPage.IsOpen) open.Add("hub");
        if (ArcadeGear.PageOpen) open.Add("arcade gear");
        if (CustomNoteColors.PageOpen) open.Add("note colors");
        if (QuickSaveLoad.Rebinding) open.Add("quick save key");
        string which = open.Count > 0 ? string.Join(", ", open) : "a mod screen";
        return EditorOverlay.Suspended ? $"open: {which} (lock lifted for a test play)" : $"open: {which}";
    }
}
