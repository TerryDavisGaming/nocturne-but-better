using HarmonyLib;
using UnityEngine;
// The game has its own Nocturne.Keyboard (an on-screen keyboard), so name the input one.
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using Key = UnityEngine.InputSystem.Key;
using KeyControl = UnityEngine.InputSystem.Controls.KeyControl;

namespace NocturneFlatScroll;

/// <summary>
/// Keeps Alt, Tab and the Windows key from starting a battle at "press any key". That prompt
/// reads the game's Global/AnyKey input action, which fires for any keyboard key, so alt-tabbing
/// away or opening the Start menu started the fight. Controller buttons still count, and no
/// other "press any key" prompt uses this action.
/// </summary>
internal static class ReadyKeyFilter
{
    private static bool reportedError;

    internal static void Install(HarmonyLib.Harmony harmony) =>
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(NocturneInput.Global), "AnyKeyDown")
                ?? throw new MissingMethodException(typeof(NocturneInput.Global).FullName, "AnyKeyDown"),
            postfix: new HarmonyMethod(typeof(ReadyKeyFilter), nameof(AnyKeyDownPostfix)));

    private static void AnyKeyDownPostfix(ref bool __result)
    {
        try
        {
            // The prompt asks every frame while it waits. Reading the ignored keys each time,
            // even on frames with no press, makes the input system track their presses, so one
            // that goes down and back up inside a single update (a hotkey tool or macro sending a
            // whole shortcut at once) still counts as pressed on that frame.
            var keyboard = InputKeyboard.current;
            bool altOrWindows = keyboard != null &&
                (Down(keyboard.leftAltKey) | Down(keyboard.rightAltKey) |
                 Down(keyboard.leftMetaKey) | Down(keyboard.rightMetaKey));
            bool tab = keyboard != null && Down(keyboard.tabKey);
            if (!__result) return;
            // When the press lands in the same update the window loses focus in (a quick
            // alt-tab), the game has already let go of every key, so there is nothing to check.
            // Alt and Windows block with any other key: those are Windows shortcuts, and AltGr
            // arrives as Alt plus Ctrl. Tab blocks only on its own. With no key down at all, a
            // controller button fired the action.
            if (!Application.isFocused || altOrWindows || (tab && !OtherKeyHeld(keyboard!))) __result = false;
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    // Not short-circuited, so the press tracking is always read.
    private static bool Down(KeyControl key) => key.wasPressedThisFrame | key.isPressed;

    private static bool OtherKeyHeld(InputKeyboard keyboard)
    {
        var keys = keyboard.m_Keys;
        if (keys == null) return true;
        for (int i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            if (key != null && key.keyCode != Key.Tab && key.isPressed) return true;
        }
        return false;
    }

    private static void ReportOnce(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        ModLog.Error("Ready key filter failed: " + ex);
    }
}
