using HarmonyLib;
using UnityEngine;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using Key = UnityEngine.InputSystem.Key;

namespace NocturneFlatScroll;

/// <summary>
/// What every editor screen needs from the game while it is open: the menus underneath locked
/// (no navigation, select or back), and the cursor free. Screens register with
/// <see cref="Enter"/> and <see cref="Leave"/>; the menus stay locked while any screen is
/// registered, so one screen can hand over to another (enter the next, then leave, or leave and
/// enter in the same frame) without the menus waking up in between. A test play's battle lifts
/// the lock while it runs (<see cref="Suspend"/> and <see cref="Resume"/>).
/// </summary>
internal static class EditorOverlay
{
    private static readonly List<object> screens = new();
    private static bool installed;

    // The Escape that closes the last screen is still down for a few frames; the menus mustn't see it.
    private static int blockBackUntilFrame = -1;
    private static bool pendingUnlock;
    private static int updatedFrame = -1;

    /// <summary>Whether any editor screen is open.</summary>
    internal static bool IsOpen => screens.Count > 0;

    // A test play's battle runs with the editors still open but hidden: the game's own menus (the
    // pause menu) must work then, so the lock is lifted until the battle is over.
    private static bool suspended;

    /// <summary>Whether the lock is lifted for a test play's battle.</summary>
    internal static bool Suspended => suspended;

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        if (installed) return;
        // While an editor is open, Back (Escape, the pad's B) must not close the menus underneath;
        // LockGameInput also turns off the open panels' own back setting. Every Back press goes
        // through NocturneGui.PopStack, which asks the top panel's TryBackAction and pops it. The
        // block sits there, not on MenuPanel.TryBackAction: that one is compiled to the same code
        // as a dozen unrelated bool getters (RVA 0x1A892A0), and a patch on it would run for all.
        var back = AccessTools.DeclaredMethod(typeof(NocturneGui), "PopStack")
            ?? throw new MissingMethodException(typeof(NocturneGui).FullName, "PopStack");
        harmony.Patch(back, prefix: new HarmonyMethod(typeof(EditorOverlay), nameof(PopStackPrefix)));
        installed = true;
    }

    private static bool BlockBack
    {
        get
        {
            if ((IsOpen && !suspended) || Time.frameCount <= blockBackUntilFrame) return true;
            // With no editor open or closing, nothing else is read: the game's Back runs as usual.
            if (!pendingUnlock) return false;
            var keyboard = InputKeyboard.current;
            return keyboard != null && keyboard[Key.Escape].isPressed;
        }
    }

    private static bool reportedBackError;

    // Skips the game's Back while it's blocked; otherwise the game handles it as usual.
    private static bool PopStackPrefix()
    {
        try { return !BlockBack; }
        catch (Exception ex)
        {
            if (!reportedBackError) ModLog.Error("Checking the editor's Back block failed: " + ex.Message);
            reportedBackError = true;
            return true;
        }
    }

    /// <summary>A screen opens: locks the menus and remembers the cursor, unless another screen already did.</summary>
    internal static void Enter(object screen)
    {
        bool first = screens.Count == 0;
        if (!screens.Contains(screen)) screens.Add(screen);
        pendingUnlock = false;
        LockGameInput(true);
        if (first && cursorWas == null) cursorWas = (Cursor.lockState, Cursor.visible);
    }

    /// <summary>
    /// A screen closes. When it was the last one, the cursor goes back to how the game had it and the
    /// menus come back once the key that closed the screen is let go.
    /// </summary>
    internal static void Leave(object screen)
    {
        screens.Remove(screen);
        if (screens.Count > 0) return;
        suspended = false;
        if (cursorWas is { } was)
        {
            Cursor.lockState = was.Lock;
            Cursor.visible = was.Visible;
            cursorWas = null;
        }
        blockBackUntilFrame = Time.frameCount + 3;
        pendingUnlock = true;
    }

    /// <summary>
    /// A test play's battle starts: the menus and the cursor go back to how the game had them,
    /// whichever screens are open, until <see cref="Resume"/>.
    /// </summary>
    internal static void Suspend()
    {
        suspended = true;
        LockGameInput(false);
        if (cursorWas is { } was)
        {
            Cursor.lockState = was.Lock;
            Cursor.visible = was.Visible;
        }
    }

    /// <summary>The test's battle is over: the menus are locked again while a screen is open.</summary>
    internal static void Resume()
    {
        suspended = false;
        if (IsOpen) Relock();
    }

    /// <summary>
    /// Locks the menus again, including panels that came up since they were locked. The game's own
    /// input lock may still hold navigation during a fade, so the setting noted when the first
    /// screen opened is kept rather than read again.
    /// </summary>
    internal static void Relock()
    {
        if (suspended) return;
        try
        {
            var events = UnityEngine.EventSystems.EventSystem.current;
            if (lockedEvents != null && lockedEvents && events && lockedEvents.Pointer != events.Pointer)
                lockedEvents.sendNavigationEvents = navigationWas;
            if (events)
            {
                events.sendNavigationEvents = false;
                lockedEvents = events;
            }
        }
        catch (Exception ex) { ModLog.Error("Locking the menus for the editor failed: " + ex.Message); }
        LockGameInput(true);
    }

    /// <summary>
    /// Called every frame, before the screens update. Safe to call more than once a frame (only
    /// the first call does anything), so every screen can call it.
    /// </summary>
    internal static void Update()
    {
        if (updatedFrame == Time.frameCount) return;
        updatedFrame = Time.frameCount;
        // Runs from LayoutDriver.LateUpdate with no guard of its own: an error here mustn't stop
        // the features updated after it (custom music).
        try
        {
            if (pendingUnlock && !BlockBack)
            {
                pendingUnlock = false;
                LockGameInput(false);
            }
            if (IsOpen && !suspended) FreeCursor();
            // Folders chosen in a file dialog are saved to the player prefs here, on the main thread.
            FileDialogs.Update();
        }
        catch (Exception ex)
        {
            if (!reportedUpdateError) ModLog.Error("The editor overlay failed: " + ex);
            reportedUpdateError = true;
        }
    }

    private static bool reportedUpdateError;

    // ---- the menus underneath -----------------------------------------------------------------

    private static UnityEngine.EventSystems.EventSystem? lockedEvents;
    private static readonly List<MenuPanel> noBackPanels = new();
    private static bool navigationWas = true;

    /// <summary>
    /// Stops the menu underneath from reacting to the editor's keys: its navigation, select and
    /// back events. (The game's own input lock also holds keyboard events back, which the editor
    /// needs.) Back is also blocked by <see cref="PopStackPrefix"/>. Locking twice does nothing more.
    /// </summary>
    private static void LockGameInput(bool locked)
    {
        try
        {
            var events = UnityEngine.EventSystems.EventSystem.current;
            if (locked)
            {
                if (events && lockedEvents == null)
                {
                    lockedEvents = events;
                    navigationWas = events.sendNavigationEvents;
                    events.sendNavigationEvents = false;
                }
                // The game only pops a panel on Back when the panel allows it.
                foreach (var panel in Resources.FindObjectsOfTypeAll<MenuPanel>())
                {
                    if (!panel || !panel.gameObject.activeInHierarchy || !panel.allowBacktrack) continue;
                    panel.allowBacktrack = false;
                    noBackPanels.Add(panel);
                }
            }
            else
            {
                if (lockedEvents != null)
                {
                    if (lockedEvents) lockedEvents.sendNavigationEvents = navigationWas;
                    lockedEvents = null;
                }
                foreach (var panel in noBackPanels)
                    if (panel) panel.allowBacktrack = true;
                noBackPanels.Clear();
            }
        }
        catch (Exception ex) { ModLog.Error("Locking the menus for the editor failed: " + ex.Message); }
    }

    // ---- the cursor ---------------------------------------------------------------------------

    // The game can lock and hide the cursor; the editors need it free while one is open.
    private static (CursorLockMode Lock, bool Visible)? cursorWas;

    private static void FreeCursor()
    {
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
        if (!Cursor.visible) Cursor.visible = true;
    }
}
