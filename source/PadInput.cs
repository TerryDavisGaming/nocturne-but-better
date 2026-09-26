using UnityEngine;
using Gamepad = UnityEngine.InputSystem.Gamepad;

namespace NocturneFlatScroll;

/// <summary>The pad buttons the mod's pages read.</summary>
internal enum PadButton
{
    /// <summary>A on an Xbox pad (cross on a PlayStation one): choose.</summary>
    South,
    /// <summary>B (circle): back.</summary>
    East,
    /// <summary>View (select, share): opens the arcade gear page.</summary>
    Select
}

/// <summary>
/// A game pad for the mod's own pages, read once a frame from the Input System's current pad
/// (Gamepad.current): button presses, and up or down from the d-pad or the left stick with key
/// repeat. With no pad, nothing is ever pressed. If the pad can't be read, pad input is off for
/// the session and the pages keep working with the keyboard and mouse.
/// </summary>
internal static class PadInput
{
    // The left stick counts past half-way; holding a direction repeats after a pause, then steadily.
    private const float StickPress = 0.5f, FirstRepeat = 0.35f, Repeat = 0.1f;

    private static bool off;
    private static bool south, east, select;
    private static int move, held;
    private static float nextRepeat;
    // QA builds only: presses handed in by a QA driver, taken on the next update.
    private static PadButton? injected;

    /// <summary>Whether a pad button went down this frame.</summary>
    internal static bool Pressed(PadButton button) => button switch
    {
        PadButton.South => south,
        PadButton.East => east,
        _ => select
    };

    /// <summary>Up (-1) or down (+1) this frame, from the d-pad or the left stick, with repeat; 0 for none.</summary>
    internal static int Move() => move;

    /// <summary>Called once a frame, before the pages read it.</summary>
    internal static void Update()
    {
        south = east = select = false;
        move = 0;
        if (injected is PadButton qa)
        {
            injected = null;
            Set(qa);
        }
        if (off) return;
        try
        {
            var pad = Gamepad.current;
            if (pad == null)
            {
                held = 0;
                return;
            }
            south |= pad.buttonSouth.wasPressedThisFrame;
            east |= pad.buttonEast.wasPressedThisFrame;
            select |= pad.selectButton.wasPressedThisFrame;
            float stick = pad.leftStick.ReadValue().y;
            int direction = pad.dpad.up.isPressed || stick > StickPress ? -1 : pad.dpad.down.isPressed || stick < -StickPress ? 1 : 0;
            float now = Time.unscaledTime;
            if (direction == 0) held = 0;
            else if (direction != held)
            {
                held = direction;
                move = direction;
                nextRepeat = now + FirstRepeat;
            }
            else if (now >= nextRepeat)
            {
                move = direction;
                nextRepeat = now + Repeat;
            }
        }
        catch (Exception ex)
        {
            off = true;
            ModLog.Error("Pad input: the pad couldn't be read, so the mod's pages use the keyboard and mouse only this session: " + ex.Message);
        }
    }

    private static void Set(PadButton button)
    {
        switch (button)
        {
            case PadButton.South: south = true; break;
            case PadButton.East: east = true; break;
            default: select = true; break;
        }
    }

    /// <summary>QA builds only: presses a pad button for the next frame, for a QA driver without a real pad. Does nothing in a release build.</summary>
    internal static void QaInject(PadButton button) => injected = QaBuild.On ? button : null;
}
