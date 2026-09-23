using HarmonyLib;
using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// Plays a short tick when the player hits a note. Unity's own audio is switched off in this
/// game, so the tick is one of the game's Wwise sounds: the 33 ms menu "select" click, from
/// a sound bank the game keeps loaded. It goes through the game's SFX and UI SFX volumes.
/// </summary>
internal static class HitSound
{
    // SFX_Global_Menu_Select in SFX_Global_Menu.bnk.
    private const uint ClickEvent = 1218145993u;
    private const string ClickBank = "SFX_Global_Menu";

    // The menu plays this same click when a value changes, so the preview waits until the
    // menu's click is over; holding left or right previews only the final value.
    private const float PreviewDelay = 0.2f;
    private static readonly ModSoundSource Source = new("NocturneFlatScroll.HitSound");
    private static float previewAt = -1f;
    private static int lastFrame = -1;
    private static bool triedBank;
    private static bool failed;
    private static bool reportedError;

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(CombatManagerV3), "OnNoteJudged")
                ?? throw new MissingMethodException(typeof(CombatManagerV3).FullName, "OnNoteJudged"),
            postfix: new HarmonyMethod(typeof(HitSound), nameof(OnNoteJudgedPostfix)));
    }

    /// <summary>Ticks for each tap and hold start the player hits, once per frame for chords.</summary>
    private static void OnNoteJudgedPostfix(CombatManagerV3 __instance, TapNote note, CombatNoteData data)
    {
        try
        {
            if (!SettingsState.HitSound || __instance == null || data == null) return;
            // End-of-fight auto judging, replays and auto-played lanes are not the player's hits.
            if (__instance.combatResultDecided) return;
            var player = __instance.combatPlayerState;
            if (player != null && player.ScriptedInput) return;
            if (data.ResultSource != TapResultSource.Tap) return;
            // "Bad" and "miss" presses count as misses, which have the game's own miss sound.
            if (data.TapResult != TapResult.Hit) return;
            // Releasing a hold is judged too, but only the press ticks.
            if (note.type == TapNoteType.HoldHead && data.HoldResult != HoldResult.Invalid) return;
            int frame = Time.frameCount;
            if (frame == lastFrame) return;
            lastFrame = frame;
            Play(SettingsState.HitSoundVolume.Factor);
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    /// <summary>Plays one tick at the current volume shortly, so a change in the menu can be heard.</summary>
    internal static void Preview() => previewAt = Time.unscaledTime + PreviewDelay;

    internal static void CancelPreview() => previewAt = -1f;

    /// <summary>Called every frame; uses unscaled time so it also works in the pause menu.</summary>
    internal static void Update()
    {
        if (previewAt < 0f || Time.unscaledTime < previewAt) return;
        previewAt = -1f;
        try { Play(SettingsState.HitSoundVolume.Factor); }
        catch (Exception ex) { ReportOnce(ex); }
    }

    private static void Play(float volume)
    {
        if (failed || volume <= 0f || !AkSoundEngine.IsInitialized()) return;
        if (Source.Post(ClickEvent, volume) != 0) return;
        if (!triedBank)
        {
            // The game loads this bank at start-up; load it again in case something unloaded it.
            triedBank = true;
            AkBankManager.LoadBank(ClickBank, false, false);
            if (Source.Post(ClickEvent, volume) != 0) return;
        }
        failed = true;
        ModLog.Error("Hit sound: the game's click sound could not be played, so the hit sound is off until the game restarts.");
    }

    private static void ReportOnce(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        ModLog.Error("Hit sound failed: " + ex);
    }
}

/// <summary>
/// A sound source owned by the mod. Its volume applies between this source and each of the
/// game's listeners, so the game's music and other sounds are untouched.
/// </summary>
internal sealed class ModSoundSource
{
    // Wwise caps a source's output volume at 16 (+24 dB).
    private const float MaxVolume = 16f;
    private readonly string _name;
    private GameObject? _object;

    internal ModSoundSource(string name) => _name = name;

    /// <returns>The playing id, or 0 when Wwise could not post the event.</returns>
    internal uint Post(uint eventId, float volume)
    {
        if (!_object)
        {
            // Wwise registers an active object as a sound source the first time it plays on it.
            _object = new GameObject(_name);
            UnityEngine.Object.DontDestroyOnLoad(_object);
        }
        var listeners = AkAudioListener.DefaultListeners.ListenerList;
        for (int i = 0; i < listeners.Count; i++)
        {
            var listener = listeners[i];
            if (listener) AkSoundEngine.SetGameObjectOutputBusVolume(_object, listener.gameObject, Mathf.Min(volume, MaxVolume));
        }
        return AkSoundEngine.PostEvent(eventId, _object);
    }
}
