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

    /// <summary>A picture drawn whole in a square (placed by its top-left corner), keeping its shape.</summary>
    internal static void FitPicture(RawImage image, Texture texture, float x, float y, float side)
    {
        float w = Math.Max(1, texture.width), h = Math.Max(1, texture.height), scale = side / Math.Max(w, h);
        w *= scale;
        h *= scale;
        PlaceTop(image.rectTransform, x + (side - w) / 2, y - (side - h) / 2, w, h);
    }

    /// <summary>A colour as rich text's RRGGBB.</summary>
    internal static string HexOf(Color c) =>
        ((int)Math.Round(Math.Clamp(c.r, 0f, 1f) * 255)).ToString("X2") + ((int)Math.Round(Math.Clamp(c.g, 0f, 1f) * 255)).ToString("X2") + ((int)Math.Round(Math.Clamp(c.b, 0f, 1f) * 255)).ToString("X2");

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
    /// <param name="blockGameClicks">
    /// The game's menus take mouse clicks and hovers through the EventSystem, which doesn't see this
    /// canvas. With this, the canvas gets a raycaster and its background takes the ray, so nothing
    /// the game draws underneath is hovered or clicked while the screen is up. The screen's own
    /// buttons don't need it: <see cref="UpdateButtons"/> hit-tests them itself.
    /// </param>
    internal EditorUi(string name, int sortingOrder = 32000, bool blockGameClicks = false)
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
        var background = MakeImage("Background", CanvasRect, Background);
        Stretch(background.rectTransform, 0, 0, 0, 0);
        if (!blockGameClicks) return;
        root.AddComponent<GraphicRaycaster>();
        background.raycastTarget = true;
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

    /// <summary>A picture drawn straight from a texture (a part of it, with uvRect), like the creator's art preview.</summary>
    internal static RawImage MakeRawImage(string name, Transform parent)
    {
        var rect = MakeRect(name, parent);
        var image = rect.gameObject.AddComponent<RawImage>();
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

    /// <summary>
    /// A text that shows exactly as it is, never read as TextMeshPro's rich text: rich text off, and
    /// "\n" written in it stays two characters. For anything from the network (the hub), which never
    /// shares a label with the mod's own tags.
    /// </summary>
    internal static TMP_Text MakePlainText(string name, Transform parent, float size, TextAlignmentOptions align)
    {
        var text = MakeText(name, parent, size, align);
        text.richText = false;
        text.parseCtrlCharacters = false;
        return text;
    }

    /// <summary>
    /// Shows text as it is, without TextMeshPro reading tags in it. Every way of closing the
    /// noparse early is taken out first: TextMeshPro reads tag names in any case (and a closing tag
    /// with spaces or anything else before its "&gt;"), and taking one out can join the letters around
    /// it into another, so they're taken out until none is left.
    /// </summary>
    internal static string Escape(string text)
    {
        string clean = text;
        while (true)
        {
            string next = NoparseEnd.Replace(clean, "");
            if (next.Length == clean.Length) break;
            clean = next;
        }
        return "<noparse>" + clean + "</noparse>";
    }

    private static readonly System.Text.RegularExpressions.Regex NoparseEnd =
        new(@"<\s*/\s*noparse[^>]*>?", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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
    /// <param name="tags">Words drawn right after a row's text, smaller and in amber (like "Beta, not recommended"); null for none.</param>
    /// <param name="plain">
    /// The heading, hint and rows are shown exactly as they are, with rich text off (text from the
    /// network, which isn't escaped then); tags aren't drawn.
    /// </param>
    internal void DrawList(string heading, string hint, List<string> items, int index, IReadOnlyList<string?>? tags = null, bool plain = false)
    {
        SetRich(listTitle!, !plain);
        SetRich(listHint!, !plain);
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
            if (item >= items.Count) continue;
            var label = listRows[i].Label;
            SetRich(label, !plain);
            if (plain)
            {
                label.text = items[item];
                continue;
            }
            string? tag = tags != null && item < tags.Count ? tags[item] : null;
            label.text = Escape(items[item]) + (tag == null ? "" : RowTag(tag, item == index));
        }
        UpdateButtons(mouse);
    }

    // A label shared by plain and rich lists: rich text on or off, and control characters read only with it.
    private static void SetRich(TMP_Text text, bool rich)
    {
        if (text.richText == rich) return;
        text.richText = rich;
        text.parseCtrlCharacters = rich;
    }

    // Amber doesn't read on the picked row's accent colour, so there the tag is dark like the row's text.
    private static string RowTag(string tag, bool picked) =>
        $"   <size=85%><color=#{(picked ? "16131F" : "F2B02E")}>{Escape(tag)}</color></size>";

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

    /// <summary>Stops showing the message (it stays in <see cref="Message"/>).</summary>
    internal void ClearMessage() => messageUntil = 0;

    /// <summary>Builds the status line in <paramref name="parent"/>; build it last so it draws on top.</summary>
    /// <param name="plain">The line is shown as it is, with rich text off (a page whose messages can hold text from the network).</param>
    internal void BuildStatus(RectTransform parent, bool plain = false)
    {
        // Messages sit on a dark backing, sized to the text, so they stay readable over what's behind.
        statusBg = MakeImage("StatusBg", parent, new Color(Background.r, Background.g, Background.b, 0.92f)).rectTransform;
        statusBg.anchorMin = statusBg.anchorMax = Vector2.zero;
        statusBg.pivot = new Vector2(0.5f, 0);
        statusText = plain ? MakePlainText("Status", statusBg, 22, TextAlignmentOptions.Center) : MakeText("Status", statusBg, 22, TextAlignmentOptions.Center);
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

/// <summary>
/// A bar that fills from the left as work goes on (a download, an upload), with a line on it. The
/// fill is a stretched Image whose right edge is the fraction done; the line is plain text (it can
/// hold a title from the network).
/// </summary>
internal sealed class ProgressBar
{
    private readonly RectTransform fill;
    private readonly TMP_Text label;
    private double shown = -1;

    internal ProgressBar(string name, Transform parent)
    {
        Rect = EditorUi.MakeImage(name, parent, EditorUi.ButtonColor).rectTransform;
        fill = EditorUi.MakeImage("Fill", Rect, EditorUi.Hex(0xF27333, 0.55f)).rectTransform;
        fill.anchorMin = Vector2.zero;
        fill.anchorMax = new Vector2(0, 1);
        fill.offsetMin = fill.offsetMax = Vector2.zero;
        label = EditorUi.MakePlainText("Text", Rect, 18, TextAlignmentOptions.Center);
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;
        EditorUi.Stretch(label.rectTransform, 12, 0, 12, 0);
    }

    internal RectTransform Rect { get; }

    /// <summary>How much is done (0 to 1), and the line on the bar.</summary>
    internal void Set(double fraction, string text)
    {
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        if (Math.Abs(fraction - shown) > 0.0005)
        {
            shown = fraction;
            fill.anchorMax = new Vector2((float)fraction, 1);
        }
        if (label.text != text) label.text = text;
    }
}

/// <summary>What a <see cref="ThumbList"/> row shows, filled in by the page for the rows in view.</summary>
internal sealed class ThumbRow
{
    /// <summary>The row's picture, or null for a tile with <see cref="Tile"/>'s letters on it.</summary>
    internal Texture? Picture;
    /// <summary>Neither a picture nor a tile: the text starts at the row's left (rows that aren't entries).</summary>
    internal bool NoPicture;
    internal string Tile = "";
    internal Color TileColor = EditorUi.ButtonHover;
    /// <summary>The first line and the one under it: shown as they are (rich text off).</summary>
    internal string Title = "", Sub = "";
    /// <summary>Small coloured chips on the third line (difficulties): their text is shown as it is.</summary>
    internal readonly List<(string Text, Color Color)> Chips = new();
    /// <summary>The mod's own words after the chips (rich text allowed; nothing from the network).</summary>
    internal string Note = "";
    /// <summary>The state at the top right (the mod's own words), and the lines under it (rich text allowed).</summary>
    internal string Tag = "", Meta = "";
    internal Color TagColor = EditorUi.Accent;
}

/// <summary>
/// A list of rows with a picture and a few lines each, like osu!'s beatmap listing: a fixed number
/// of reused rows shows the part of the list in view (the virtual list of
/// <see cref="EditorUi.BuildList"/>), the mouse wheel scrolls it three rows a notch, a click picks a
/// row and a second click on the same row soon after is a double click. Text from the network goes
/// only into labels with rich text off; the mod's own words go into their own labels. The rows
/// aren't buttons of the EditorUi: <see cref="Draw"/> hit-tests them and says what was clicked.
/// </summary>
internal sealed class ThumbList
{
    internal const int Rows = 8;
    internal const float RowHeight = 98, Gap = 4, Picture = 90;
    private const float DoubleClick = 0.4f, RightW = 270;
    private const int MaxChips = 8;

    private sealed class RowView
    {
        internal RectTransform Rect = null!;
        internal Image Bg = null!, Marker = null!, Tile = null!;
        internal RawImage Picture = null!;
        internal TMP_Text TileText = null!, Title = null!, Sub = null!, Note = null!, Tag = null!, Meta = null!;
        internal readonly List<(Image Bg, TMP_Text Text)> Chips = new();
        internal string ChipsShown = "\u0001";
        internal bool NoPicture;
    }

    private readonly RowView[] rows = new RowView[Rows];
    private int first;
    private int lastIndex = -1, lastCount = -1;
    private int clickItem = -1;
    private float clickAt = -10f;

    internal ThumbList(string name, RectTransform parent)
    {
        Rect = EditorUi.MakeRect(name, parent);
        for (int i = 0; i < Rows; i++) rows[i] = BuildRow(i);
    }

    /// <summary>The list's box; the page places it (the rows are its width).</summary>
    internal RectTransform Rect { get; }

    /// <summary>The first row in view.</summary>
    internal int First => first;

    /// <summary>The height the rows take.</summary>
    internal static float Height => Rows * RowHeight + (Rows - 1) * Gap;

    private RowView BuildRow(int i)
    {
        var v = new RowView();
        v.Bg = EditorUi.MakeImage("Row", Rect, EditorUi.ButtonColor);
        v.Rect = v.Bg.rectTransform;
        v.Rect.anchorMin = new Vector2(0, 1);
        v.Rect.anchorMax = new Vector2(1, 1);
        v.Rect.pivot = new Vector2(0.5f, 1);
        v.Rect.sizeDelta = new Vector2(0, RowHeight);
        v.Rect.anchoredPosition = new Vector2(0, -i * (RowHeight + Gap));
        v.Marker = EditorUi.MakeImage("Marker", v.Rect, EditorUi.Accent);
        v.Marker.rectTransform.anchorMin = new Vector2(0, 0);
        v.Marker.rectTransform.anchorMax = new Vector2(0, 1);
        v.Marker.rectTransform.pivot = new Vector2(0, 0.5f);
        v.Marker.rectTransform.sizeDelta = new Vector2(6, 0);
        v.Marker.rectTransform.anchoredPosition = Vector2.zero;
        float top = -(RowHeight - Picture) / 2;
        v.Tile = EditorUi.MakeImage("Tile", v.Rect, EditorUi.ButtonHover);
        EditorUi.PlaceTop(v.Tile.rectTransform, 10, top, Picture, Picture);
        v.TileText = EditorUi.MakePlainText("Letters", v.Tile.rectTransform, 34, TextAlignmentOptions.Center);
        v.TileText.enableWordWrapping = false;
        v.TileText.overflowMode = TextOverflowModes.Truncate;
        EditorUi.Stretch(v.TileText.rectTransform, 4, 4, 4, 4);
        v.Picture = EditorUi.MakeRawImage("Picture", v.Rect);
        EditorUi.PlaceTop(v.Picture.rectTransform, 10, top, Picture, Picture);
        v.Title = Line(v.Rect, "Title", 23, EditorUi.TextColor, plain: true);
        v.Sub = Line(v.Rect, "Sub", 17, EditorUi.DimText, plain: true);
        v.Note = Line(v.Rect, "Note", 15, EditorUi.DimText, plain: false);
        v.Tag = EditorUi.MakeText("Tag", v.Rect, 18, TextAlignmentOptions.TopRight);
        v.Tag.enableWordWrapping = false;
        v.Tag.overflowMode = TextOverflowModes.Ellipsis;
        v.Meta = EditorUi.MakeText("Meta", v.Rect, 15, TextAlignmentOptions.TopRight);
        v.Meta.color = EditorUi.DimText;
        v.Meta.enableWordWrapping = false;
        v.Meta.overflowMode = TextOverflowModes.Ellipsis;
        foreach (var right in new[] { v.Tag.rectTransform, v.Meta.rectTransform })
        {
            right.anchorMin = right.anchorMax = new Vector2(1, 1);
            right.pivot = new Vector2(1, 1);
        }
        v.Tag.rectTransform.sizeDelta = new Vector2(RightW - 16, 26);
        v.Tag.rectTransform.anchoredPosition = new Vector2(-14, -8);
        v.Meta.rectTransform.sizeDelta = new Vector2(RightW - 16, 52);
        v.Meta.rectTransform.anchoredPosition = new Vector2(-14, -38);
        Layout(v, noPicture: false);
        v.Rect.gameObject.SetActive(false);
        return v;
    }

    private static TMP_Text Line(RectTransform row, string name, float size, Color color, bool plain)
    {
        var t = plain ? EditorUi.MakePlainText(name, row, size, TextAlignmentOptions.TopLeft) : EditorUi.MakeText(name, row, size, TextAlignmentOptions.TopLeft);
        t.color = color;
        t.enableWordWrapping = false;
        t.overflowMode = TextOverflowModes.Ellipsis;
        t.rectTransform.anchorMin = new Vector2(0, 1);
        t.rectTransform.anchorMax = new Vector2(1, 1);
        t.rectTransform.pivot = new Vector2(0, 1);
        return t;
    }

    private static float TextLeft(bool noPicture) => noPicture ? 22 : 10 + Picture + 16;

    // Where the lines start: after the picture, or at the row's left when it has none.
    private static void Layout(RowView v, bool noPicture)
    {
        v.NoPicture = noPicture;
        float left = TextLeft(noPicture);
        SetLine(v.Title, left, -8, 30);
        SetLine(v.Sub, left, -40, 24);
        SetLine(v.Note, left, -68, 24);
        v.ChipsShown = "\u0001";
    }

    private static void SetLine(TMP_Text t, float left, float y, float h)
    {
        t.rectTransform.offsetMin = new Vector2(left, y - h);
        t.rectTransform.offsetMax = new Vector2(-RightW, y);
    }

    /// <summary>
    /// Shows <paramref name="count"/> items with <paramref name="index"/> picked and in view, each
    /// row filled by <paramref name="row"/> (asked only for the rows in view). Returns the item
    /// clicked on this frame (or -1), and whether that was the second click of a double click.
    /// <paramref name="mouse"/> is null while clicks don't count; <paramref name="wheel"/> is the
    /// mouse for the wheel (it scrolls while the pointer is over the list).
    /// </summary>
    internal (int Clicked, bool Double) Draw(int count, int index, Func<int, ThumbRow> row, InputMouse? mouse, InputMouse? wheel)
    {
        int maxFirst = Math.Max(0, count - Rows);
        // Rows added at the end (the next page) leave the view where the wheel put it.
        if (index != lastIndex || count < lastCount)
        {
            // Keep the picked row in view, moving as little as possible.
            if (index >= 0 && index < first) first = index;
            else if (index >= first + Rows) first = index - Rows + 1;
            lastIndex = index;
        }
        lastCount = count;
        var pointer = wheel ?? mouse;
        Vector2 pos = pointer != null ? pointer.position.ReadValue() : new Vector2(-1, -1);
        if (wheel != null && RectTransformUtility.RectangleContainsScreenPoint(Rect, pos, null))
        {
            float notch = wheel.scroll.ReadValue().y;
            if (notch != 0) first += notch > 0 ? -3 : 3;
        }
        first = Math.Clamp(first, 0, maxFirst);

        int clicked = -1;
        bool click = mouse != null && mouse.leftButton.wasPressedThisFrame;
        for (int i = 0; i < Rows; i++)
        {
            var v = rows[i];
            int item = first + i;
            bool shown = item < count;
            if (v.Rect.gameObject.activeSelf != shown) v.Rect.gameObject.SetActive(shown);
            if (!shown) continue;
            Fill(v, row(item));
            bool hover = pointer != null && RectTransformUtility.RectangleContainsScreenPoint(v.Rect, pos, null);
            bool picked = item == index;
            var bg = picked ? EditorUi.Hex(0x453C60) : hover ? EditorUi.ButtonHover : EditorUi.ButtonColor;
            if (v.Bg.color != bg) v.Bg.color = bg;
            if (v.Marker.gameObject.activeSelf != picked) v.Marker.gameObject.SetActive(picked);
            if (click && hover) clicked = item;
        }
        bool twice = false;
        if (clicked >= 0)
        {
            twice = clicked == clickItem && Time.unscaledTime - clickAt <= DoubleClick;
            // A third click starts over rather than counting as another double click.
            clickItem = twice ? -1 : clicked;
            clickAt = Time.unscaledTime;
        }
        return (clicked, twice);
    }

    /// <summary>Whether the rows in view reach within <paramref name="margin"/> rows of the list's end (the page loads more then).</summary>
    internal bool NearEnd(int count, int margin) => first + Rows + margin >= count;

    /// <summary>For another list in the same rows (a tab, a new search): the view starts at the top, then shows the picked row.</summary>
    internal void ResetScroll()
    {
        first = 0;
        lastIndex = lastCount = -1;
    }

    private void Fill(RowView v, ThumbRow r)
    {
        if (r.NoPicture != v.NoPicture) Layout(v, r.NoPicture);
        bool picture = !r.NoPicture && r.Picture != null;
        bool tile = !r.NoPicture && r.Picture == null;
        if (v.Picture.gameObject.activeSelf != picture) v.Picture.gameObject.SetActive(picture);
        if (picture && v.Picture.texture != r.Picture)
        {
            v.Picture.texture = r.Picture;
            EditorUi.FitPicture(v.Picture, r.Picture!, 10, -(RowHeight - Picture) / 2, Picture);
        }
        if (!picture && v.Picture.texture != null) v.Picture.texture = null;
        if (v.Tile.gameObject.activeSelf != tile) v.Tile.gameObject.SetActive(tile);
        if (tile)
        {
            if (v.Tile.color != r.TileColor) v.Tile.color = r.TileColor;
            Set(v.TileText, r.Tile);
        }
        Set(v.Title, r.Title);
        Set(v.Sub, r.Sub);
        Set(v.Tag, r.Tag);
        if (v.Tag.color != r.TagColor) v.Tag.color = r.TagColor;
        Set(v.Meta, r.Meta);
        FillChips(v, r);
    }

    private static void Set(TMP_Text t, string text)
    {
        if (t.text != text) t.text = text;
    }

    // The chips are laid out again only when they change: each as wide as its text, with a "+3"
    // chip for the ones that don't fit; the note goes after them.
    private void FillChips(RowView v, ThumbRow r)
    {
        var key = new System.Text.StringBuilder();
        foreach (var (text, color) in r.Chips) key.Append(text).Append('\u0003').Append(EditorUi.HexOf(color)).Append(color.a).Append('\u0002');
        key.Append('\u0004').Append(r.Note).Append('\u0004').Append(v.Rect.rect.width);
        string shown = key.ToString();
        if (shown == v.ChipsShown) return;
        v.ChipsShown = shown;
        float left = TextLeft(v.NoPicture);
        float limit = v.Rect.rect.width - RightW - 8;
        if (limit <= left + 100) limit = left + 600;
        float x = left, y = -66;
        int used = 0;
        for (int c = 0; c < r.Chips.Count; c++)
        {
            string text = r.Chips[c].Text;
            var color = r.Chips[c].Color;
            var chip = Chip(v, used);
            float w = ChipWidth(chip.Text, text);
            // A chip leaves room for the "+n" one after it, unless it's the last.
            int rest = r.Chips.Count - c - 1;
            float room = rest > 0 ? ChipWidth(chip.Text, "+" + rest) + 6 : 0;
            if (used == MaxChips - 1 && rest > 0 || x + w + room > limit)
            {
                text = "+" + (r.Chips.Count - c);
                color = EditorUi.ButtonHover;
                w = ChipWidth(chip.Text, text);
                c = r.Chips.Count;
            }
            chip.Bg.color = color;
            chip.Text.color = color == EditorUi.ButtonHover ? EditorUi.TextColor : EditorUi.ActiveText;
            chip.Text.text = text;
            EditorUi.PlaceTop(chip.Bg.rectTransform, x, y, w, 24);
            if (!chip.Bg.gameObject.activeSelf) chip.Bg.gameObject.SetActive(true);
            x += w + 6;
            used++;
        }
        for (int c = used; c < v.Chips.Count; c++)
            if (v.Chips[c].Bg.gameObject.activeSelf) v.Chips[c].Bg.gameObject.SetActive(false);
        v.Note.rectTransform.offsetMin = new Vector2(x + (used > 0 ? 6 : 0), -68 - 24);
        v.Note.text = r.Note;
    }

    private static float ChipWidth(TMP_Text text, string value) => Math.Max(30, text.GetPreferredValues(value).x + 16);

    private (Image Bg, TMP_Text Text) Chip(RowView v, int i)
    {
        while (v.Chips.Count <= i)
        {
            var bg = EditorUi.MakeImage("Chip", v.Rect, EditorUi.ButtonHover);
            var text = EditorUi.MakePlainText("Text", bg.rectTransform, 15, TextAlignmentOptions.Center);
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
            EditorUi.Stretch(text.rectTransform, 0, 0, 0, 0);
            bg.gameObject.SetActive(false);
            v.Chips.Add((bg, text));
        }
        return v.Chips[i];
    }
}
