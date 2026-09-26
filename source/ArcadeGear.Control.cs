using UnityEngine;
using UnityEngine.UI;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>
/// The Gear control at the right of the main-menu arcade's title bar, like "Gear: arcade G". It's
/// the mod's own box and text inside the arcade's header, so it fades with the arcade and anything
/// the game draws over the arcade covers it. It isn't a game button, so the arcade's keyboard and
/// pad navigation never land on it; the mod hit-tests the mouse itself. A click on it, G, or View
/// on a pad opens the page, but only while the arcade takes input by the game's own flags: its
/// button group is active and its panel is the focused one.
/// </summary>
internal static partial class ArcadeGear
{
    private const string ControlName = "NbbArcadeGear";
    private const string ArcadeTypeName = "ArcadeMenuV2";
    // From the title bar's right edge as far as the score column's, and as tall as the difficulty tabs.
    private const float ControlRight = 50f, ControlHeight = 25f, ControlPadding = 12f;
    // The click or key that closed the page doesn't open it again.
    private const float ReopenDelay = 0.3f;
    private static readonly Color HoverColor = new(1f, 1f, 1f, 0.12f);

    private static ArcadeMenuV2? menu;
    private static float nextMenuSearch;
    private static RectTransform? control;
    private static Image? controlBox;
    private static TMP_Text? controlText;
    private static string shownText = "";
    private static float closedAt = -100f;
    private static bool wasRunning, noHeader;
    // The arcade's selected card this frame and the last: a click on the control lands on no game
    // button, and the game clears the selection for that before the page opens.
    private static GameObject? selectedNow, selectedLastFrame;

    // The main-menu arcade's panel: exactly ArcadeMenuV2 (the debug song testing panel is one too), in a loaded scene.
    private static ArcadeMenuV2? FindMenu()
    {
        foreach (var candidate in Resources.FindObjectsOfTypeAll<ArcadeMenuV2>())
            if (candidate && candidate.gameObject.scene.IsValid() && candidate.GetIl2CppType().Name == ArcadeTypeName) return candidate;
        return null;
    }

    private static void UpdateControl()
    {
        if (!session || !ArcadeSession.Active || !BattleGear.Installed)
        {
            HideControl();
            return;
        }
        bool running = ArcadeUtility.IsRunning;
        // A battle may have used up a consumable.
        if (!running && wasRunning) Refresh();
        wasRunning = running;
        if (menu == null || !menu)
        {
            menu = null;
            if (Time.unscaledTime < nextMenuSearch) return;
            nextMenuSearch = Time.unscaledTime + 1f;
            menu = FindMenu();
            if (menu == null) return;
        }
        bool active = menu.Active;
        if ((control == null || !control) && !noHeader)
        {
            // Tried once: without the control, G and View still open the page.
            try { Build(menu); }
            catch (Exception ex)
            {
                noHeader = true;
                Report("building the Gear control", ex);
            }
        }
        bool shown = !running && active && control != null && control && control.parent && control.parent.gameObject.activeInHierarchy;
        if (control != null && control && control.gameObject.activeSelf != shown) control.gameObject.SetActive(shown);
        var mouse = InputMouse.current;
        bool over = false;
        if (shown)
        {
            ShowText(UsingPad());
            over = Cursor.visible && mouse != null && Over(control!, mouse.position.ReadValue());
            var color = over && !PageOpen ? HoverColor : Color.clear;
            if (controlBox != null && controlBox && controlBox.color != color) controlBox.color = color;
        }
        if (!running && active && !PageOpen)
        {
            selectedLastFrame = selectedNow;
            selectedNow = Selected();
        }
        bool ready = !running && active && menu.IsFocused && !PageOpen && !EditorOverlay.IsOpen && !TestPlay.Active &&
                     Time.unscaledTime >= closedAt + ReopenDelay;
        if (!ready) return;
        var keyboard = InputKeyboard.current;
        if ((over && mouse!.leftButton.wasPressedThisFrame) || (keyboard != null && keyboard[Key.G].wasPressedThisFrame) ||
            PadInput.Pressed(PadButton.Select))
            OpenPage();
    }

    // Built once and kept: the arcade's panel lives as long as the game.
    private static void Build(ArcadeMenuV2 arcade)
    {
        var header = arcade.transform.Find("ArcadeAndScoreContents/Header");
        if (header == null || !header)
        {
            noHeader = true;
            Note("Arcade gear: the arcade's title bar isn't laid out as expected, so the Gear control can't show; G (or View on a pad) still opens the page.");
            return;
        }
        // The chapter buttons' font, size and colour, else the title's.
        var prefab = arcade.categoryButtonPrefab;
        var source = prefab != null && prefab ? prefab.GetComponentInChildren<TMP_Text>(true) : null;
        if (source == null || !source) source = header.GetComponentInChildren<TMP_Text>(true);
        var old = header.Find(ControlName);
        if (old != null && old) Object.Destroy(old.gameObject);

        var go = new GameObject(ControlName);
        go.layer = header.gameObject.layer;
        go.transform.SetParent(header, false);
        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(1f, 0.5f);
        rect.pivot = new Vector2(1f, 0.5f);
        rect.anchoredPosition = new Vector2(-ControlRight, 0f);
        rect.sizeDelta = new Vector2(100f, ControlHeight);
        var box = go.AddComponent<Image>();
        box.color = Color.clear;
        // The game's pointer events stop here; nothing of the arcade sits under it anyway.
        box.raycastTarget = true;
        var textRect = EditorUi.MakeRect("Label", rect);
        textRect.gameObject.layer = go.layer;
        EditorUi.Stretch(textRect, 0, 0, 0, 0);
        // Fresh components rather than a copy of the game's, so no localization writes over them.
        var text = textRect.gameObject.AddComponent<TextMeshProUGUI>();
        if (source != null && source)
        {
            text.font = source.font;
            text.fontSharedMaterial = source.fontSharedMaterial;
            text.fontSize = source.fontSize > 0f ? source.fontSize : 10f;
            text.color = source.color;
        }
        text.enableAutoSizing = false;
        text.alignment = TextAlignmentOptions.Center;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        text.richText = true;
        go.SetActive(false);
        control = rect;
        controlBox = box;
        controlText = text;
        shownText = "";
    }

    private static void HideControl()
    {
        if (control != null && control && control.gameObject.activeSelf) control.gameObject.SetActive(false);
    }

    // The label and, small and dim, the key that opens the page. Measured again only when it changes.
    private static void ShowText(bool pad)
    {
        if (controlText == null || !controlText) return;
        string text = ArcadeGearRules.ControlText(Label) + $" <size=65%><color=#9D92B4>{(pad ? "View" : "G")}</color></size>";
        if (text == shownText) return;
        shownText = text;
        controlText.text = text;
        float width = controlText.GetPreferredValues(text, 2000f, ControlHeight).x;
        control!.sizeDelta = new Vector2(width + ControlPadding, ControlHeight);
    }

    // The arcade's canvas draws through a camera, so its hit test needs that camera.
    private static bool Over(RectTransform rect, Vector2 point) =>
        RectTransformUtility.RectangleContainsScreenPoint(rect, point, CameraOf(rect));

    private static Camera? CameraOf(RectTransform rect)
    {
        var canvas = rect.GetComponentInParent<Canvas>();
        var top = canvas != null && canvas ? canvas.rootCanvas : null;
        return top != null && top && top.renderMode != RenderMode.ScreenSpaceOverlay ? top.worldCamera : null;
    }

    private static GameObject? Selected()
    {
        var events = UnityEngine.EventSystems.EventSystem.current;
        var selected = events != null && events ? events.currentSelectedGameObject : null;
        return selected != null && selected ? selected : null;
    }

    private static bool UsingPad()
    {
        try { return NocturneInput.IsUsingGamepad; }
        catch { return false; }
    }

    // ---- for the QA drivers: reads only --------------------------------------------------------------

    /// <summary>Whether the Gear control shows now.</summary>
    internal static bool QaControlShown => control != null && control && control.gameObject.activeInHierarchy;

    /// <summary>The Gear control's rectangle on the screen (pixels from the bottom left), or null when it isn't showing.</summary>
    internal static Rect? QaControlScreenRect
    {
        get
        {
            if (!QaControlShown) return null;
            var rect = control!.rect;
            var camera = CameraOf(control);
            var low = RectTransformUtility.WorldToScreenPoint(camera, control.TransformPoint(new Vector3(rect.xMin, rect.yMin, 0f)));
            var high = RectTransformUtility.WorldToScreenPoint(camera, control.TransformPoint(new Vector3(rect.xMax, rect.yMax, 0f)));
            return Rect.MinMaxRect(low.x, low.y, high.x, high.y);
        }
    }

    /// <summary>The arcade's own input flags: its button group is active, its panel focused.</summary>
    internal static bool QaMenuActive => menu != null && menu && menu.Active;

    internal static bool QaMenuFocused => menu != null && menu && menu.IsFocused;

    /// <summary>Whether the arcade's difficulty tabs read their keys (the page turns them off while it's open).</summary>
    internal static bool QaTabBarEnabled
    {
        get
        {
            var bar = menu != null && menu ? menu.difficultyTabBar : null;
            return bar != null && bar && bar.enabled;
        }
    }

    /// <summary>The arcade's difficulty tab, or -1 without the arcade.</summary>
    internal static int QaSelectedDifficulty => menu != null && menu ? menu._selectedDifficulty : -1;
}
