using HarmonyLib;
using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// Changes how loud the game's own miss sounds are. The game plays them from one place,
/// AudioController.PlayMissSound. At any volume other than 100% the mod plays the same sound
/// from its own source instead, with that source turned up or down. The on/off switch is the
/// game's own "Note Miss Sounds" setting.
/// </summary>
internal static class MissSound
{
    // SFX_Global_Combat_NoteMiss in SFX_Global_Combat.bnk, for the preview when the game's
    // own event can't be read.
    private const uint MissEvent = 2297350582u;
    private const string MissBank = "SFX_Global_Combat";
    private const float PreviewDelay = 0.2f;
    private static readonly ModSoundSource Source = new("NocturneFlatScroll.MissSound");
    private static float previewAt = -1f;
    private static int lastFrame = -1;
    private static bool lastCritical;
    private static bool triedBank;
    private static bool reportedError;

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(AudioController), "PlayMissSound")
                ?? throw new MissingMethodException(typeof(AudioController).FullName, "PlayMissSound"),
            prefix: new HarmonyMethod(typeof(MissSound), nameof(PlayMissSoundPrefix)));
    }

    /// <summary>Whether the game's miss sounds are on. This is the game's own setting.</summary>
    internal static bool Enabled => NocturneSettings.PlayMissedSoundEffects;

    internal static void SetEnabled(bool value)
    {
        NocturneSettings.PlayMissedSoundEffects = value;
        PlayerPrefs.Save();
        ModLog.Info("Miss sound: " + (value ? "On" : "Off"));
    }

    // The game names the parameter "isCritcal"; __0 refers to it by position.
    private static bool PlayMissSoundPrefix(bool __0)
    {
        try
        {
            var volume = SettingsState.MissSoundVolume;
            // At 100%, or with the sounds off, the game plays (or skips) its own sound.
            if (volume.Value == 100 || !NocturneSettings.PlayMissedSoundEffects) return true;
            var controller = AudioController.Instance;
            if (controller == null) return true;
            var soundEvent = __0 ? controller.CriticalSoundEvent : controller.MissSoundEvent;
            if (soundEvent == null) return true;
            // A failed hold posts the miss twice in a row; the second post would only restart it.
            int frame = Time.frameCount;
            if (frame == lastFrame && __0 == lastCritical) return false;
            if (!AkSoundEngine.IsInitialized()) return true;
            if (Source.Post(soundEvent.Id, volume.Factor) == 0) return true;
            lastFrame = frame;
            lastCritical = __0;
            return false;
        }
        catch (Exception ex)
        {
            ReportOnce(ex);
            return true;
        }
    }

    /// <summary>Plays one miss shortly at the current volume, so a change in the menu can be heard.</summary>
    internal static void Preview() => previewAt = Time.unscaledTime + PreviewDelay;

    internal static void CancelPreview() => previewAt = -1f;

    /// <summary>Called every frame; uses unscaled time so it also works in the pause menu.</summary>
    internal static void Update()
    {
        if (previewAt < 0f || Time.unscaledTime < previewAt) return;
        previewAt = -1f;
        try
        {
            // Like the hit sound, a volume change is previewed even while the switch is off.
            if (!AkSoundEngine.IsInitialized()) return;
            var controller = AudioController.Instance;
            var soundEvent = controller != null ? controller.MissSoundEvent : null;
            uint id = soundEvent != null ? soundEvent.Id : MissEvent;
            float volume = SettingsState.MissSoundVolume.Factor;
            if (Source.Post(id, volume) != 0 || triedBank) return;
            // Fights load this bank; load it once in case a menu is open without it.
            triedBank = true;
            AkBankManager.LoadBank(MissBank, false, false);
            Source.Post(id, volume);
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    private static void ReportOnce(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        ModLog.Error("Miss sound failed: " + ex);
    }
}
