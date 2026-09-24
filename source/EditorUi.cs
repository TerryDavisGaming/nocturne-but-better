using UnityEngine;
using UnityEngine.UI;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>
/// The editor screens' shared look and widgets, first made for the chart editor: a full-screen
/// canvas drawn over the game, the palette, buttons whose text, highlight and visibility are
/// worked out each frame, list screens, and a status line. Each screen builds its own instance
/// and destroys it when it closes. Layout helpers that need no state are static.
/// </summary>
internal sealed class EditorUi
{
    // ---- the palette --------------------------------------------------------------------------

    internal static readonly Color Background = Hex(0x110F18);
    internal static readonly Color PanelColor = Hex(0x1C1827);
    internal static readonly Color ButtonColor = Hex(0x2A2538);
    internal static readonly Color ButtonHover = Hex(0x3A3350);
    internal static readonly Color Accent = Hex(0xF27333);
    internal static readonly Color TextColor = Hex(0xEAE6F5);
    internal static readonly Color DimText = Hex(0x9D92B4);
    /// <summary>A highlighted button's label, dark on the accent colour.</summary>
    internal static readonly Color ActiveText = Hex(0x16131F);

    internal static Color Hex(int rgb, float a = 1f) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);

    /// <summary>A clickable box with a label. Its text, highlight and visibility are worked out each frame.</summary>
    internal sealed class UiButton
    {
        internal RectTransform Rect = null!;
        internal Image? Bg;
        internal TMP_Text Label = null!;
        internal Action? OnClick;
        internal Func<string>? Text;
        internal Func<bool>? Active;
        internal Func<bool>? Visible;
    }

    private readonly GameObject root;
    private readonly List<UiButton> buttons = new();
    private readonly List<RectTransform> solidPanels = new();
    private bool destroyed;

    /// <summary>The canvas, 1920x1080 units or more at any window shape.</summary>
    internal RectTransform CanvasRect { get; }

    /// <summary>False once <see cref="Destroy"/> ran (or Unity destroyed the canvas).</summary>
    internal bool IsAlive => !destroyed && root;

    /// <summary>Builds the canvas: drawn over everything, with the dark background filling it.</summary>
    internal EditorUi(string name, int sortingOrder = 32000)
    {
        root = new GameObject(name);
        Object.DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        // Expand keeps at least 1920x1080 units on screen at any aspect, so nothing is pushed off it.
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        CanvasRect = root.GetComponent<RectTransform>();
        Stretch(MakeImage("Background", CanvasRect, Background).rectTransform, 0, 0, 0, 0);
    }

    /// <summary>Removes the canvas and everything on it.</summary>
    internal void Destroy()
    {
        destroyed = true;
        if (root) Object.Destroy(root);
    }

    /// <summary>Shows or hides the whole screen, for handing over to another screen and back.</summary>
    internal void SetVisible(bool visible)
    {
        if (root && root.activeSelf != visible) root.SetActive(visible);
    }

    // ---- small builders -----------------------------------------------------------------------

    internal UiButton MakeButton(Transform parent, string text, Action? onClick)
    {
        var bg = MakeImage("Button", parent, ButtonColor);
        var label = MakeText("Label", bg.rectTransform, 20, TextAlignmentOptions.Center);
        label.text = text;
        Stretch(label.rectTransform, 0, 0, 0, 0);
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;
        var b = new UiButton { Rect = bg.rectTransform, Bg = bg, Label = label, OnClick = onClick };
        buttons.Add(b);
        return b;
    }

    /// <summary>A text whose content is worked out each frame, like a button without a box.</summary>
    internal UiButton AddLiveText(TMP_Text text, Func<string> value)
    {
        var b = new UiButton { Rect = text.rectTransform, Label = text, Text = value };
        buttons.Add(b);
        return b;
    }

    /// <summary>A small caps heading in a panel's column; moves <paramref name="y"/> down past it.</summary>
    internal void Header(RectTransform parent, string text, ref float y, float x = 18, float width = 260)
    {
        var t = MakeText(text, parent, 15, TextAlignmentOptions.Left);
        t.text = text.ToUpperInvariant();
        t.color = DimText;
        PlaceTop(t.rectTransform, x, y, width, 20);
        y -= 25;
    }

    /// <summary>"-" and "+" buttons around a value.</summary>
    internal void Stepper(RectTransform parent, ref float y, Func<string> value, Action less, Action more, float x = 16, float width = 268)
    {
        var minus = MakeButton(parent, "-", less);
        PlaceTop(minus.Rect, x, y, 52, 36);
        var label = MakeText("Value", parent, 20, TextAlignmentOptions.Center);
        PlaceTop(label.rectTransform, x + 56, y, width - 112, 36);
        AddLiveText(label, value);
        var plus = MakeButton(parent, "+", more);
        PlaceTop(plus.Rect, x + width - 52, y, 52, 36);
        y -= 46;
    }

    /// <summary>A button that shows a setting and flips it; highlighted while it's on.</summary>
    internal UiButton Toggle(RectTransform parent, ref float y, Func<string> text, Func<bool> on, Action flip, float x = 16, float width = 268)
    {
        var b = MakeButton(parent, "", flip);
        b.Text = text;
        b.Active = on;
        b.Label.fontSize = 18;
        PlaceTop(b.Rect, x, y, width, 36);
        y -= 42;
        return b;
    }

    internal static RectTransform MakeRect(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        return rect;
    }

    internal static Image MakeImage(string name, Transform parent, Color color)
    {
        var rect = MakeRect(name, parent);
        var image = rect.gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    internal static TMP_Text MakeText(string name, Transform parent, float size, TextAlignmentOptions align)
    {
        var rect = MakeRect(name, parent);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        var font = GameFont();
        if (font)
        {
            text.font = font!.font;
            text.fontSharedMaterial = font.fontSharedMaterial;
        }
        text.fontSize = size;
        text.alignment = align;
        text.color = TextColor;
        text.raycastTarget = false;
        text.richText = true;
        return text;
    }

    /// <summary>Fills the parent, less the given margins.</summary>
    internal static void Stretch(RectTransform rect, float left, float bottom, float right, float top)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
    }

    internal static void Place(RectTransform rect, Vector2 anchor, Vector2 pos, Vector2 size, Vector2? pivot = null)
    {
        rect.anchorMin = rect.anchorMax = anchor;
        rect.pivot = pivot ?? new Vector2(0.5f, anchor.y);
        rect.anchoredPosition = pos;
        rect.sizeDelta = size;
    }

    /// <summary>Places by the top-left corner, measured from the parent's top-left (y goes down, so it's negative).</summary>
    internal static void PlaceTop(RectTransform rect, float x, float y, float w, float h) =>
        Place(rect, new Vector2(0, 1), new Vector2(x, y), new Vector2(w, h), new Vector2(0, 1));

    /// <summary>Shows text as it is, without TextMeshPro reading tags in it.</summary>
    internal static string Escape(string text) => "<noparse>" + text.Replace("</noparse>", "") + "</noparse>";

    private static TMP_Text? fontSource;

    /// <summary>The font of the game's own menus, found once.</summary>
    private static TMP_Text? GameFont()
    {
        if (fontSource) return fontSource;
        foreach (var menu in Resources.FindObjectsOfTypeAll<MainMenu>())
            if (menu && menu.optionsButton) { fontSource = menu.optionsButton.GetComponentInChildren<TMP_Text>(true); if (fontSource) return fontSource; }
        foreach (var text in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
            if (text && text.font) return fontSource = text;
        return null;
    }

    // ---- per-frame ----------------------------------------------------------------------------

    /// <summary>
    /// Updates every button's text, colours and visibility, and runs a click. True when a button
    /// took it. A click that closes the screen ends the update there.
    /// </summary>
    internal bool UpdateButtons(InputMouse? mouse)
    {
        Vector2 pos = mouse != null ? mouse.position.ReadValue() : new Vector2(-1, -1);
        bool click = mouse != null && mouse.leftButton.wasPressedThisFrame;
        bool taken = false;
        // By index: a click may add buttons (they join in this same pass).
        for (int i = 0; i < buttons.Count; i++)
        {
            var b = buttons[i];
            if (!b.Rect) continue;
            bool shown = b.Visible?.Invoke() ?? true;
            if (b.Rect.gameObject.activeSelf != shown) b.Rect.gameObject.SetActive(shown);
            if (!shown || !b.Rect.gameObject.activeInHierarchy) continue;
            if (b.Text != null)
            {
                var t = b.Text();
                if (b.Label.text != t) b.Label.text = t;
            }
            if (b.Bg == null) continue;
            bool over = RectTransformUtility.RectangleContainsScreenPoint(b.Rect, pos, null);
            bool active = b.Active?.Invoke() ?? false;
            var color = active ? Accent : over ? ButtonHover : ButtonColor;
            if (b.Bg.color != color) b.Bg.color = color;
            var textColor = active ? ActiveText : TextColor;
            if (b.Label.color != textColor) b.Label.color = textColor;
            if (click && over && !taken && b.OnClick != null)
            {
                taken = true;
                b.OnClick();
                if (destroyed) return true;
            }
        }
        return taken;
    }

    /// <summary>Marks a panel as solid: the mouse over it is on the UI, not on what's behind.</summary>
    internal void AddSolidPanel(RectTransform panel) => solidPanels.Add(panel);

    /// <summary>Whether the point is over one of the solid panels that is showing.</summary>
    internal bool OverUi(Vector2 screenPos)
    {
        foreach (var panel in solidPanels)
            if (panel && panel.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(panel, screenPos, null)) return true;
        return false;
    }

    // ---- list screens: a title, a hint and rows to pick from ----------------------------------

    internal const int ListRowsVisible = 16;

    private readonly List<UiButton> listRows = new();
    private List<string> listItems = new();
    private int listFirst, listIndex, listClick = -1;
    private string listHeading = "";
    private TMP_Text? listTitle, listHint;

    /// <summary>The full-screen panel holding the list (made by <see cref="BuildList"/>).</summary>
    internal RectTransform? ListPanel { get; private set; }

    internal void BuildList()
    {
        ListPanel = MakeRect("Lists", CanvasRect);
        Stretch(ListPanel, 0, 0, 0, 0);
        var listBox = MakeImage("Box", ListPanel, PanelColor).rectTransform;
        listBox.sizeDelta = new Vector2(1200, 900);
        listTitle = MakeText("Title", listBox, 40, TextAlignmentOptions.Center);
        Place(listTitle.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -30), new Vector2(1140, 60));
        listHint = MakeText("Hint", listBox, 20, TextAlignmentOptions.Center);
        listHint.color = DimText;
        Place(listHint.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -95), new Vector2(1140, 40));
        for (int i = 0; i < ListRowsVisible; i++)
        {
            int row = i;
            var b = MakeButton(listBox, "", () => listClick = listFirst + row);
            b.Visible = () => listFirst + row < listItems.Count;
            b.Active = () => listFirst + row == listIndex;
            Place(b.Rect, new Vector2(0.5f, 1), new Vector2(0, -150 - i * 45), new Vector2(1100, 40));
            b.Label.alignment = TextAlignmentOptions.Left;
            b.Label.margin = new Vector4(18, 0, 18, 0);
            listRows.Add(b);
        }
    }

    /// <summary>
    /// Whether a row was picked: clicked on the last frame (then <paramref name="index"/> becomes
    /// that row) or Enter pressed on the current one.
    /// </summary>
    internal bool Chosen(InputKeyboard keyboard, int count, ref int index)
    {
        if (listClick >= 0 && listClick < count)
        {
            index = listClick;
            listClick = -1;
            return true;
        }
        listClick = -1;
        return count > 0 && (EditorInput.Pressed(keyboard, Key.Enter) || EditorInput.Pressed(keyboard, Key.NumpadEnter));
    }

    /// <summary>
    /// Shows the list with <paramref name="index"/> picked and in view, and handles the mouse: a
    /// click picks a row (see <see cref="Chosen"/>), the wheel scrolls the rows.
    /// </summary>
    internal void DrawList(string heading, string hint, List<string> items, int index)
    {
        listTitle!.text = heading;
        listHint!.text = hint;
        var mouse = InputMouse.current;
        float wheel = mouse != null ? mouse.scroll.ReadValue().y : 0;
        bool follow = index != listIndex || items.Count != listItems.Count || heading != listHeading;
        listFirst = ListScroll.First(listFirst, index, items.Count, ListRowsVisible, follow, wheel);
        listItems = items;
        listIndex = index;
        listHeading = heading;
        for (int i = 0; i < listRows.Count; i++)
        {
            int item = listFirst + i;
            if (item < items.Count) listRows[i].Label.text = Escape(items[item]);
        }
        UpdateButtons(mouse);
    }

    // ---- the status line: messages, and what's being typed ------------------------------------

    private RectTransform? statusBg;
    private TMP_Text? statusText;
    private string message = "";
    private float messageUntil;

    /// <summary>The last message, shown or not.</summary>
    internal string Message => message;

    /// <summary>Whether the last message is still showing.</summary>
    internal bool MessageShowing => Time.unscaledTime < messageUntil;

    internal void Say(string text, float seconds = 4f)
    {
        message = text;
        messageUntil = Time.unscaledTime + seconds;
    }

    /// <summary>Builds the status line in <paramref name="parent"/>; build it last so it draws on top.</summary>
    internal void BuildStatus(RectTransform parent)
    {
        // Messages sit on a dark backing, sized to the text, so they stay readable over what's behind.
        statusBg = MakeImage("StatusBg", parent, new Color(Background.r, Background.g, Background.b, 0.92f)).rectTransform;
        statusBg.anchorMin = statusBg.anchorMax = Vector2.zero;
        statusBg.pivot = new Vector2(0.5f, 0);
        statusText = MakeText("Status", statusBg, 22, TextAlignmentOptions.Center);
        statusText.color = Accent;
        Stretch(statusText.rectTransform, 18, 7, 18, 7);
    }

    /// <summary>
    /// Shows <paramref name="status"/> (rich text; empty hides the line), centred in the space
    /// between the side panels and just above the bottom bar.
    /// </summary>
    internal void DrawStatus(string status, float left, float right, float bottom)
    {
        statusText!.text = status;
        statusBg!.gameObject.SetActive(status.Length > 0);
        if (status.Length == 0) return;
        float areaWidth = CanvasRect.rect.width - left - right;
        var size = statusText.GetPreferredValues(status, areaWidth - 76, 0);
        statusBg.sizeDelta = new Vector2(Math.Min(size.x, areaWidth - 76) + 36, size.y + 14);
        statusBg.anchoredPosition = new Vector2(left + areaWidth / 2, bottom + 8);
    }
}

/// <summary>Which row of a long list sits at the top of the view.</summary>
internal static class ListScroll
{
    /// <summary>
    /// The new top row. When <paramref name="follow"/> (the picked row or the list changed) the
    /// picked row is brought back to the middle of the view; otherwise the mouse wheel moves the
    /// view three rows a notch. Without the wheel this is always the picked row, centred.
    /// </summary>
    internal static int First(int first, int index, int count, int visible, bool follow, float wheel)
    {
        int maxFirst = Math.Max(0, count - visible);
        if (follow) return Math.Clamp(index - visible / 2, 0, maxFirst);
        if (wheel != 0) return Math.Clamp(first + (wheel > 0 ? -3 : 3), 0, maxFirst);
        return Math.Clamp(first, 0, maxFirst);
    }
}
