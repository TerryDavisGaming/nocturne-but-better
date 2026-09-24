using UnityEngine;
using UnityEngine.UI;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

// The editor's screen, laid out like osu!'s editor: a top bar with tabs and file buttons, a
// toolbox on the left, the playfield in the middle, a panel on the right for the current tab,
// and the timeline along the bottom. Everything is anchored to the screen's edges, so it fits
// any window shape; buttons are clickable and show their keys.
internal static partial class ChartEditor
{
    private enum Tab { Compose, Timing, Events, Setup, Keys }

    private const float TopH = 64f, BottomH = 92f, LeftW = 300f, RightW = 430f;
    private const float NoteHeight = 26f;
    private static readonly Color Background = Hex(0x110F18);
    private static readonly Color PanelColor = Hex(0x1C1827);
    private static readonly Color ButtonColor = Hex(0x2A2538);
    private static readonly Color ButtonHover = Hex(0x3A3350);
    private static readonly Color Accent = Hex(0xF27333);
    private static readonly Color TextColor = Hex(0xEAE6F5);
    private static readonly Color DimText = Hex(0x9D92B4);
    private static readonly Color LaneColor = Hex(0x1A1724);
    private static readonly Color LaneEdge = Hex(0x3A3450);

    private static Tab tab = Tab.Compose;
    private static RectTransform? listPanel, editPanel, fieldArea, field, keysPanel, leftPanel, rightPanel, timeline;
    private static RectTransform? statusBg;
    private static TMP_Text? listTitle, listHint, statusText, titleText, timeText, infoText, tabText, keysHint;
    private static Image? timelineFill, timelinePlayhead, judgeLine, selectionBox;
    private static readonly List<Image> notePool = new();
    private static readonly List<Image> linePool = new();
    private static readonly List<Image> wavePool = new();
    private static readonly List<Image> markerPool = new();
    private static readonly List<TMP_Text> labelPool = new();
    private static readonly List<UiButton> uiButtons = new();
    private static readonly List<UiButton> listRows = new();
    private static readonly List<UiButton> eventRows = new();

    private static Color Hex(int rgb, float a = 1f) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);

    /// <summary>A clickable box with a label. Its text, highlight and visibility are worked out each frame.</summary>
    private sealed class UiButton
    {
        internal RectTransform Rect = null!;
        internal Image? Bg;
        internal TMP_Text Label = null!;
        internal Action? OnClick;
        internal Func<string>? Text;
        internal Func<bool>? Active;
        internal Func<bool>? Visible;
    }

    // ---- building --------------------------------------------------------------------------

    private static void BuildCanvas()
    {
        uiButtons.Clear();
        listRows.Clear();
        eventRows.Clear();
        markerPool.Clear();
        root = new GameObject("NocturneButBetter Chart Editor");
        Object.DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        // Expand keeps at least 1920x1080 units on screen at any aspect, so nothing is pushed off it.
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        canvasRect = root.GetComponent<RectTransform>();
        Stretch(MakeImage("Background", canvasRect!, Background).rectTransform, 0, 0, 0, 0);

        BuildLists();
        BuildEditor();
        editPanel!.gameObject.SetActive(false);
    }

    private const int ListRowsVisible = 16;

    private static void BuildLists()
    {
        listPanel = MakeRect("Lists", canvasRect!);
        Stretch(listPanel, 0, 0, 0, 0);
        var box = MakeImage("Box", listPanel, PanelColor).rectTransform;
        box.sizeDelta = new Vector2(1200, 900);
        listTitle = MakeText("Title", box, 40, TextAlignmentOptions.Center);
        Place(listTitle.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -30), new Vector2(1140, 60));
        listHint = MakeText("Hint", box, 20, TextAlignmentOptions.Center);
        listHint.color = DimText;
        Place(listHint.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -95), new Vector2(1140, 40));
        for (int i = 0; i < ListRowsVisible; i++)
        {
            int row = i;
            var b = MakeButton(box, "", () => ClickListRow(row));
            Place(b.Rect, new Vector2(0.5f, 1), new Vector2(0, -150 - i * 45), new Vector2(1100, 40));
            b.Label.alignment = TextAlignmentOptions.Left;
            b.Label.margin = new Vector4(18, 0, 18, 0);
            listRows.Add(b);
        }
    }

    private static void BuildEditor()
    {
        editPanel = MakeRect("Edit", canvasRect!);
        Stretch(editPanel, 0, 0, 0, 0);

        // The playfield sits between the panels and bars; the Keys tab uses the same space.
        fieldArea = MakeRect("FieldArea", editPanel);
        Stretch(fieldArea, LeftW, BottomH, RightW, TopH);
        field = MakeRect("Field", fieldArea);
        field.anchorMin = new Vector2(0.5f, 0);
        field.anchorMax = new Vector2(0.5f, 1);
        keysPanel = MakeRect("Keys", editPanel);
        Stretch(keysPanel, LeftW, BottomH, RightW, TopH);
        BuildKeysPanel();

        // Top bar: tabs, the chart's name, and the file buttons.
        var top = MakeImage("TopBar", editPanel, PanelColor).rectTransform;
        top.anchorMin = new Vector2(0, 1);
        top.anchorMax = new Vector2(1, 1);
        top.pivot = new Vector2(0.5f, 1);
        top.sizeDelta = new Vector2(0, TopH);
        top.anchoredPosition = Vector2.zero;
        var tabs = new[] { (Tab.Compose, "Compose"), (Tab.Timing, "Timing"), (Tab.Events, "Events"), (Tab.Setup, "Setup"), (Tab.Keys, "Keys") };
        for (int i = 0; i < tabs.Length; i++)
        {
            var (t, name) = tabs[i];
            var b = MakeButton(top, name, () => SetTab(t));
            b.Active = () => tab == t;
            Place(b.Rect, new Vector2(0, 0.5f), new Vector2(16 + i * 146, 0), new Vector2(138, 44), new Vector2(0, 0.5f));
        }
        titleText = MakeText("Title", top, 20, TextAlignmentOptions.Center);
        titleText.enableWordWrapping = false;
        titleText.overflowMode = TextOverflowModes.Ellipsis;
        titleText.rectTransform.anchorMin = new Vector2(0, 0);
        titleText.rectTransform.anchorMax = new Vector2(1, 1);
        titleText.rectTransform.offsetMin = new Vector2(16 + 5 * 146 + 10, 0);
        titleText.rectTransform.offsetMax = new Vector2(-(16 + 110 + 12 + 170 + 12 + 150 + 16), 0);
        var exit = MakeButton(top, "Exit", RequestClose);
        Place(exit.Rect, new Vector2(1, 0.5f), new Vector2(-16, 0), new Vector2(110, 44), new Vector2(1, 0.5f));
        var export = MakeButton(top, "Export", StartExportPack);
        export.Text = () => $"Export <size=65%><color=#9D92B4>{ShortKey(EditorAction.Export)}</color></size>";
        Place(export.Rect, new Vector2(1, 0.5f), new Vector2(-138, 0), new Vector2(170, 44), new Vector2(1, 0.5f));
        var save = MakeButton(top, "Save", () => Save());
        save.Text = () => $"Save <size=65%><color=#9D92B4>{ShortKey(EditorAction.Save)}</color></size>";
        save.Active = () => dirty;
        Place(save.Rect, new Vector2(1, 0.5f), new Vector2(-320, 0), new Vector2(150, 44), new Vector2(1, 0.5f));

        BuildLeftPanel();
        BuildRightPanel();
        BuildBottomBar();

        // Messages sit on a dark backing, sized to the text, so they stay readable over the notes.
        statusBg = MakeImage("StatusBg", editPanel, new Color(Background.r, Background.g, Background.b, 0.92f)).rectTransform;
        statusBg.anchorMin = statusBg.anchorMax = Vector2.zero;
        statusBg.pivot = new Vector2(0.5f, 0);
        statusText = MakeText("Status", statusBg, 22, TextAlignmentOptions.Center);
        statusText.color = Accent;
        Stretch(statusText.rectTransform, 18, 7, 18, 7);
    }

    private static void BuildLeftPanel()
    {
        leftPanel = MakeImage("Toolbox", editPanel!, PanelColor).rectTransform;
        leftPanel.anchorMin = new Vector2(0, 0);
        leftPanel.anchorMax = new Vector2(0, 1);
        leftPanel.pivot = new Vector2(0, 0.5f);
        leftPanel.offsetMin = new Vector2(0, BottomH);
        leftPanel.offsetMax = new Vector2(LeftW, -TopH);
        float y = -14;
        Header(leftPanel, "Tools", ref y);
        var tools = new[] { (Tool.Select, "Select", EditorAction.ToolSelect), (Tool.Note, "Note", EditorAction.ToolNote), (Tool.Hold, "Hold", EditorAction.ToolHold), (Tool.Mine, "Mine", EditorAction.ToolMine) };
        foreach (var (t, name, action) in tools)
        {
            var b = MakeButton(leftPanel, name, () => SetTool(t));
            b.Text = () => $"{name}  <size=65%><color=#9D92B4>{ShortKey(action)}</color></size>";
            b.Active = () => tool == t;
            b.Label.alignment = TextAlignmentOptions.Left;
            b.Label.margin = new Vector4(16, 0, 8, 0);
            PlaceTop(b.Rect, 16, y, 268, 40);
            y -= 45;
        }
        y -= 6;
        Header(leftPanel, "Beat snap", ref y);
        Stepper(leftPanel, ref y, () => $"1/{Snaps[snapIndex]}", () => snapIndex = Math.Max(0, snapIndex - 1), () => snapIndex = Math.Min(Snaps.Length - 1, snapIndex + 1));
        Header(leftPanel, "Zoom", ref y);
        Stepper(leftPanel, ref y, () => $"{pixelsPerSecond / 7f:0}%", () => Zoom(1 / 1.2f), () => Zoom(1.2f));
        Header(leftPanel, "Playback speed", ref y);
        for (int i = 0; i < Speeds.Length; i++)
        {
            int index = i;
            var b = MakeButton(leftPanel, $"{Speeds[i] * 100:0}%", () => SetSpeed(index));
            b.Active = () => speedIndex == index;
            b.Label.fontSize = 17;
            PlaceTop(b.Rect, 16 + i * 68, y, 64, 36);
        }
        y -= 48;
        Header(leftPanel, "Sound", ref y);
        Toggle(leftPanel, ref y, () => $"Note ticks: {(ticksOn ? "on" : "off")}", () => ticksOn, ToggleTicks);
        Toggle(leftPanel, ref y, () => $"Metronome: {(metronomeOn ? "on" : "off")}", () => metronomeOn, ToggleMetronome);
        Toggle(leftPanel, ref y, () => $"Waveform: {(waveformOn ? "on" : "off")}", () => waveformOn, () => waveformOn = !waveformOn);
        Header(leftPanel, "Music volume", ref y);
        Stepper(leftPanel, ref y, () => $"{musicVolume * 100:0}%", () => SetMusicVolume(musicVolume - 0.1f), () => SetMusicVolume(musicVolume + 0.1f));
        Header(leftPanel, "Tick volume", ref y);
        Stepper(leftPanel, ref y, () => $"{tickVolume * 100:0}%", () => SetTickVolume(tickVolume - 0.1f), () => SetTickVolume(tickVolume + 0.1f));
    }

    private const int EventRowsVisible = 7;

    private static void BuildRightPanel()
    {
        rightPanel = MakeImage("Panel", editPanel!, PanelColor).rectTransform;
        rightPanel.anchorMin = new Vector2(1, 0);
        rightPanel.anchorMax = new Vector2(1, 1);
        rightPanel.pivot = new Vector2(1, 0.5f);
        rightPanel.offsetMin = new Vector2(-RightW, BottomH);
        rightPanel.offsetMax = new Vector2(0, -TopH);
        float width = RightW - 32;

        infoText = MakeText("Info", rightPanel, 19, TextAlignmentOptions.TopLeft);
        PlaceTop(infoText.rectTransform, 16, -14, width, 250);
        tabText = MakeText("TabText", rightPanel, 17, TextAlignmentOptions.TopLeft);
        tabText.color = DimText;
        tabText.rectTransform.anchorMin = new Vector2(0, 0);
        tabText.rectTransform.anchorMax = new Vector2(1, 0);
        tabText.rectTransform.pivot = new Vector2(0, 0);
        tabText.rectTransform.offsetMin = new Vector2(16, 12);
        tabText.rectTransform.offsetMax = new Vector2(-16, 250);
        tabText.overflowMode = TextOverflowModes.Truncate;

        // Compose: what to do with the selection.
        var compose = new (string Label, EditorAction Action, Action Do)[]
        {
            ("Copy", EditorAction.Copy, Copy), ("Cut", EditorAction.Cut, () => { Copy(); DeleteSelection(); }),
            ("Paste", EditorAction.Paste, Paste), ("Delete", EditorAction.Delete, DeleteSelection),
            ("Mirror", EditorAction.Mirror, Mirror), ("Reverse", EditorAction.Reverse, Reverse),
            ("Snap to grid", EditorAction.Resnap, Resnap), ("Select all", EditorAction.SelectAll, SelectAll),
            ("Undo", EditorAction.Undo, Undo), ("Redo", EditorAction.Redo, Redo),
        };
        for (int i = 0; i < compose.Length; i++)
        {
            var (label, action, doIt) = compose[i];
            var b = MakeButton(rightPanel, label, doIt);
            b.Text = () => $"{label} <size=62%><color=#9D92B4>{ShortKey(action)}</color></size>";
            b.Visible = () => tab == Tab.Compose;
            b.Label.fontSize = 18;
            PlaceTop(b.Rect, 16 + (i % 2) * (width / 2 + 4), -270 - (i / 2) * 46, width / 2 - 4, 40);
        }

        // Timing: bookmarks and scroll speed sections.
        var timing = new (string Label, Action Do)[]
        {
            ("Bookmark here", ToggleBookmark), ("Clear bookmarks", ClearBookmarks),
            ("Prev bookmark", () => JumpBookmark(-1)), ("Next bookmark", () => JumpBookmark(1)),
            ("Scroll speed here", () => StartTyping(TextField.ScrollSpeed)), ("Remove speed here", RemoveScrollHere),
        };
        for (int i = 0; i < timing.Length; i++)
        {
            var (label, doIt) = timing[i];
            var b = MakeButton(rightPanel, label, doIt);
            b.Visible = () => tab == Tab.Timing;
            b.Label.fontSize = 17;
            PlaceTop(b.Rect, 16 + (i % 2) * (width / 2 + 4), -270 - (i / 2) * 46, width / 2 - 4, 40);
        }

        // Events: the chart's events, and what to do with the picked one.
        for (int i = 0; i < EventRowsVisible; i++)
        {
            int row = i;
            var b = MakeButton(rightPanel, "", () => ClickEventRow(row));
            b.Visible = () => tab == Tab.Events && EventRowShown(row);
            b.Label.alignment = TextAlignmentOptions.Left;
            b.Label.margin = new Vector4(10, 0, 6, 0);
            b.Label.fontSize = 15;
            PlaceTop(b.Rect, 16, -262 - i * 32, width, 29);
            eventRows.Add(b);
        }
        var eventButtons = new (string Label, Action Do)[]
        {
            ("Add here", AddEventHere), ("Edit text", () => StartTyping(TextField.EventMods)),
            ("Move here", MovePickedEventHere), ("Copy here", DuplicatePickedEvent),
            ("Earlier", () => NudgePickedEvent(-1)), ("Later", () => NudgePickedEvent(1)),
            ("Shorter", () => ChangePickedEventLength(-0.25)), ("Longer", () => ChangePickedEventLength(0.25)),
            ("Delete", DeletePickedEvent), ("Song's events", RestoreSongEvents),
        };
        for (int i = 0; i < eventButtons.Length; i++)
        {
            var (label, doIt) = eventButtons[i];
            var b = MakeButton(rightPanel, label, doIt);
            b.Visible = () => tab == Tab.Events;
            b.Label.fontSize = 16;
            PlaceTop(b.Rect, 16 + (i % 2) * (width / 2 + 4), -262 - EventRowsVisible * 32 - 8 - (i / 2) * 40, width / 2 - 4, 35);
        }

        // Setup: the chart's name and author, and the file actions.
        var name = MakeButton(rightPanel, "", () => StartTyping(TextField.Title));
        name.Text = () => $"Name: {Escape(title)}{(typing == TextField.Title ? "_" : "")}";
        name.Visible = () => tab == Tab.Setup;
        name.Active = () => typing == TextField.Title;
        name.Label.alignment = TextAlignmentOptions.Left;
        name.Label.margin = new Vector4(12, 0, 8, 0);
        PlaceTop(name.Rect, 16, -270, width, 42);
        var who = MakeButton(rightPanel, "", () => StartTyping(TextField.Author));
        who.Text = () => $"Author: {Escape(author.Length > 0 ? author : "(click to set)")}{(typing == TextField.Author ? "_" : "")}";
        who.Visible = () => tab == Tab.Setup;
        who.Active = () => typing == TextField.Author;
        who.Label.alignment = TextAlignmentOptions.Left;
        who.Label.margin = new Vector4(12, 0, 8, 0);
        PlaceTop(who.Rect, 16, -318, width, 42);
        var files = new (string Label, Action Do)[] { ("Save", () => Save()), ("Export pack", StartExportPack), ("Open folder", () => CustomChartOptions.OpenChartFolder()) };
        for (int i = 0; i < files.Length; i++)
        {
            var (label, doIt) = files[i];
            var b = MakeButton(rightPanel, label, doIt);
            b.Visible = () => tab == Tab.Setup;
            PlaceTop(b.Rect, 16 + (i % 2) * (width / 2 + 4), -378 - (i / 2) * 46, width / 2 - 4, 40);
        }

        // Keys: back to the defaults.
        var reset = MakeButton(rightPanel, "Reset all keys", () => { ResetBindings(); Say("Keys are back to the defaults", 3f); });
        reset.Visible = () => tab == Tab.Keys;
        PlaceTop(reset.Rect, 16, -270, width, 42);
    }

    private static void BuildBottomBar()
    {
        var bar = MakeImage("BottomBar", editPanel!, PanelColor).rectTransform;
        bar.anchorMin = new Vector2(0, 0);
        bar.anchorMax = new Vector2(1, 0);
        bar.pivot = new Vector2(0.5f, 0);
        bar.sizeDelta = new Vector2(0, BottomH);
        bar.anchoredPosition = Vector2.zero;
        var play = MakeButton(bar, "Play", TogglePlay);
        play.Text = () => (IsPlaying ? "Pause" : "Play") + $" <size=65%><color=#9D92B4>{ShortKey(EditorAction.PlayPause)}</color></size>";
        play.Active = () => IsPlaying;
        Place(play.Rect, new Vector2(0, 0.5f), new Vector2(16, 0), new Vector2(170, 52), new Vector2(0, 0.5f));
        timeText = MakeText("Time", bar, 22, TextAlignmentOptions.Left);
        timeText.enableWordWrapping = false;
        Place(timeText.rectTransform, new Vector2(0, 0.5f), new Vector2(202, 0), new Vector2(320, 52), new Vector2(0, 0.5f));
        timeline = MakeImage("Timeline", bar, ButtonColor).rectTransform;
        timeline.anchorMin = new Vector2(0, 0.5f);
        timeline.anchorMax = new Vector2(1, 0.5f);
        timeline.offsetMin = new Vector2(530, -10);
        timeline.offsetMax = new Vector2(-24, 10);
        timelineFill = MakeImage("Played", timeline, Hex(0x5A4F7A));
        timelineFill.rectTransform.anchorMin = new Vector2(0, 0);
        timelineFill.rectTransform.anchorMax = new Vector2(0, 1);
        timelineFill.rectTransform.pivot = new Vector2(0, 0.5f);
        timelinePlayhead = MakeImage("Playhead", timeline, Accent);
        timelinePlayhead.rectTransform.anchorMin = timelinePlayhead.rectTransform.anchorMax = new Vector2(0, 0.5f);
        timelinePlayhead.rectTransform.sizeDelta = new Vector2(4, 44);
    }

    private static void BuildKeysPanel()
    {
        Stretch(MakeImage("KeysBg", keysPanel!, Background).rectTransform, 0, 0, 0, 0);
        keysHint = MakeText("KeysHint", keysPanel!, 19, TextAlignmentOptions.Center);
        keysHint.color = DimText;
        keysHint.text = "Click an action, then press its new key (with Ctrl, Shift or Alt if you like). Esc keeps the old key.";
        keysHint.rectTransform.anchorMin = new Vector2(0, 1);
        keysHint.rectTransform.anchorMax = new Vector2(1, 1);
        keysHint.rectTransform.pivot = new Vector2(0.5f, 1);
        keysHint.rectTransform.offsetMin = new Vector2(20, -60);
        keysHint.rectTransform.offsetMax = new Vector2(-20, -12);
        int perColumn = (DefaultBindings.Length + 1) / 2;
        for (int i = 0; i < DefaultBindings.Length; i++)
        {
            var action = DefaultBindings[i].Action;
            var b = MakeButton(keysPanel!, "", () => { rebinding = action; Say($"Press the new key for \"{LabelOf(action)}\"", 30f); });
            b.Text = () => $"{LabelOf(action)}<pos=58%><color=#F2B02E>{(rebinding == action ? "press a key..." : KeysFor(action))}</color>";
            b.Active = () => rebinding == action;
            b.Label.alignment = TextAlignmentOptions.Left;
            b.Label.margin = new Vector4(10, 0, 6, 0);
            b.Label.fontSize = 15;
            int col = i / perColumn, row = i % perColumn;
            b.Rect.anchorMin = new Vector2(col == 0 ? 0.01f : 0.505f, 1);
            b.Rect.anchorMax = new Vector2(col == 0 ? 0.495f : 0.99f, 1);
            b.Rect.pivot = new Vector2(0, 1);
            b.Rect.offsetMin = new Vector2(0, -70 - row * 34 - 30);
            b.Rect.offsetMax = new Vector2(0, -70 - row * 34);
        }
    }

    // ---- small builders ---------------------------------------------------------------------

    private static void Header(RectTransform parent, string text, ref float y)
    {
        var t = MakeText(text, parent, 15, TextAlignmentOptions.Left);
        t.text = text.ToUpperInvariant();
        t.color = DimText;
        PlaceTop(t.rectTransform, 18, y, 260, 20);
        y -= 25;
    }

    private static void Stepper(RectTransform parent, ref float y, Func<string> value, Action less, Action more)
    {
        var minus = MakeButton(parent, "-", less);
        PlaceTop(minus.Rect, 16, y, 52, 36);
        var label = MakeText("Value", parent, 20, TextAlignmentOptions.Center);
        PlaceTop(label.rectTransform, 72, y, 156, 36);
        uiButtons.Add(new UiButton { Rect = label.rectTransform, Label = label, Text = value });
        var plus = MakeButton(parent, "+", more);
        PlaceTop(plus.Rect, 232, y, 52, 36);
        y -= 46;
    }

    private static void Toggle(RectTransform parent, ref float y, Func<string> text, Func<bool> on, Action flip)
    {
        var b = MakeButton(parent, "", flip);
        b.Text = text;
        b.Active = on;
        b.Label.fontSize = 18;
        PlaceTop(b.Rect, 16, y, 268, 36);
        y -= 42;
    }

    private static UiButton MakeButton(Transform parent, string text, Action? onClick)
    {
        var bg = MakeImage("Button", parent, ButtonColor);
        var label = MakeText("Label", bg.rectTransform, 20, TextAlignmentOptions.Center);
        label.text = text;
        Stretch(label.rectTransform, 0, 0, 0, 0);
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;
        var b = new UiButton { Rect = bg.rectTransform, Bg = bg, Label = label, OnClick = onClick };
        uiButtons.Add(b);
        return b;
    }

    private static RectTransform MakeRect(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        return rect;
    }

    private static Image MakeImage(string name, Transform parent, Color color)
    {
        var rect = MakeRect(name, parent);
        var image = rect.gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static TMP_Text MakeText(string name, Transform parent, float size, TextAlignmentOptions align)
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

    private static void Stretch(RectTransform rect, float left, float bottom, float right, float top)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
    }

    private static void Place(RectTransform rect, Vector2 anchor, Vector2 pos, Vector2 size, Vector2? pivot = null)
    {
        rect.anchorMin = rect.anchorMax = anchor;
        rect.pivot = pivot ?? new Vector2(0.5f, anchor.y);
        rect.anchoredPosition = pos;
        rect.sizeDelta = size;
    }

    private static void PlaceTop(RectTransform rect, float x, float y, float w, float h) =>
        Place(rect, new Vector2(0, 1), new Vector2(x, y), new Vector2(w, h), new Vector2(0, 1));

    private static string Escape(string text) => "<noparse>" + text.Replace("</noparse>", "") + "</noparse>";

    private static TMP_Text? fontSource;

    private static TMP_Text? GameFont()
    {
        if (fontSource) return fontSource;
        foreach (var menu in Resources.FindObjectsOfTypeAll<MainMenu>())
            if (menu && menu.optionsButton) { fontSource = menu.optionsButton.GetComponentInChildren<TMP_Text>(true); if (fontSource) return fontSource; }
        foreach (var text in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
            if (text && text.font) return fontSource = text;
        return null;
    }

    // ---- per-frame UI ------------------------------------------------------------------------

    private static void ShowScreen(Screen next)
    {
        screen = next;
        listPanel!.gameObject.SetActive(next != Screen.Edit);
        editPanel!.gameObject.SetActive(next == Screen.Edit);
    }

    private static void SetTab(Tab next)
    {
        tab = next;
        rebinding = null;
        typing = TextField.None;
        keysPanel!.gameObject.SetActive(tab == Tab.Keys);
        fieldArea!.gameObject.SetActive(tab != Tab.Keys);
    }

    /// <summary>Updates every button's text, colours and visibility, and runs a click. True when a button took it.</summary>
    private static bool UpdateButtons(InputMouse? mouse)
    {
        Vector2 pos = mouse != null ? mouse.position.ReadValue() : new Vector2(-1, -1);
        bool click = mouse != null && mouse.leftButton.wasPressedThisFrame;
        bool taken = false;
        foreach (var b in uiButtons)
        {
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
            var textColor = active ? Hex(0x16131F) : TextColor;
            if (b.Label.color != textColor) b.Label.color = textColor;
            if (click && over && !taken && b.OnClick != null)
            {
                taken = true;
                b.OnClick();
            }
        }
        return taken;
    }

    private static bool OverUi(Vector2 screenPos) =>
        (leftPanel && RectTransformUtility.RectangleContainsScreenPoint(leftPanel, screenPos, null))
        || (rightPanel && RectTransformUtility.RectangleContainsScreenPoint(rightPanel, screenPos, null));

    private static List<string> listItems = new();
    private static int listFirst;

    private static void DrawList(string heading, string hint, List<string> items, int index)
    {
        listTitle!.text = heading;
        listHint!.text = hint;
        listItems = items;
        listFirst = Math.Clamp(index - ListRowsVisible / 2, 0, Math.Max(0, items.Count - ListRowsVisible));
        for (int i = 0; i < listRows.Count; i++)
        {
            int item = listFirst + i;
            var row = listRows[i];
            row.Visible = () => item < listItems.Count;
            if (item >= items.Count) continue;
            row.Label.text = Escape(items[item]);
            bool picked = item == index;
            row.Active = () => picked;
        }
        UpdateButtons(InputMouse.current);
    }

    // ---- the playfield -----------------------------------------------------------------------

    private static float FieldHeight => field ? field!.rect.height : 900f;
    private static float JudgeY => -FieldHeight / 2f + FieldHeight * 0.16f;
    private static float LaneWidth => chart!.Lanes >= 5 ? 92f : 104f;
    private static float FieldLeft => -LaneWidth * chart!.Lanes / 2f;
    private static float LaneX(int lane) => FieldLeft + (lane + 0.5f) * LaneWidth;
    private static float TimeToY(double seconds, double now) => JudgeY + (float)((ScrollBeat(seconds) - ScrollBeat(now)) * pixelsPerSecond * 60.0 / chart!.BpmAt(0));
    private static double YToTime(float y, double now) => ScrollBeatToSeconds(ScrollBeat(now) + (y - JudgeY) / (pixelsPerSecond * 60.0 / chart!.BpmAt(0)));

    // The rect is wider than the lanes: event labels on the left, the waveform on the right. Lane
    // positions are measured from the lanes' centre, which sits this far left of the rect's centre.
    private const float FieldLeftRoom = 240f, FieldRightRoom = 150f;
    private static float FieldCentre => (FieldRightRoom - FieldLeftRoom) / 2f;

    private static void BuildField()
    {
        foreach (var image in field!.GetComponentsInChildren<Image>(true)) Object.Destroy(image.gameObject);
        foreach (var text in field.GetComponentsInChildren<TMP_Text>(true)) Object.Destroy(text.gameObject);
        notePool.Clear();
        linePool.Clear();
        wavePool.Clear();
        labelPool.Clear();
        float width = LaneWidth * chart!.Lanes;
        field.offsetMin = new Vector2(-width / 2 - FieldLeftRoom, 0);
        field.offsetMax = new Vector2(width / 2 + FieldRightRoom, 0);
        for (int lane = 0; lane < chart.Lanes; lane++) StretchColumn(MakeImage("Lane" + lane, field, LaneColor), LaneX(lane), LaneWidth - 4);
        StretchColumn(MakeImage("EdgeL", field, LaneEdge), FieldLeft - 2, 3);
        StretchColumn(MakeImage("EdgeR", field, LaneEdge), -FieldLeft + 2, 3);
        judgeLine = MakeImage("JudgeLine", field, Accent);
        selectionBox = MakeImage("SelectionBox", field, new Color(1f, 1f, 1f, 0.12f));
        selectionBox.gameObject.SetActive(false);
    }

    private static void StretchColumn(Image image, float x, float width)
    {
        image.rectTransform.anchorMin = new Vector2(0.5f, 0);
        image.rectTransform.anchorMax = new Vector2(0.5f, 1);
        image.rectTransform.sizeDelta = new Vector2(width, 0);
        image.rectTransform.anchoredPosition = new Vector2(x - FieldCentre, 0);
    }

    private static Image Pooled(List<Image> pool, int index, Transform parent, string name)
    {
        while (pool.Count <= index) pool.Add(MakeImage(name, parent, Color.white));
        var image = pool[index];
        if (!image.gameObject.activeSelf) image.gameObject.SetActive(true);
        return image;
    }

    private static void HideFrom(List<Image> pool, int used)
    {
        for (int i = used; i < pool.Count; i++)
            if (pool[i].gameObject.activeSelf) pool[i].gameObject.SetActive(false);
    }

    private static void PlaceField(Image image, float x, float y, float w, float h, Color color)
    {
        var rect = image.rectTransform;
        rect.anchoredPosition = new Vector2(x - FieldCentre, y);
        rect.sizeDelta = new Vector2(w, h);
        image.color = color;
    }

    private static Color SnapColor(int row)
    {
        if (row % EditorChart.RowsPerMeasure == 0) return new Color(1f, 1f, 1f, 0.85f);
        if (row % EditorChart.RowsPerBeat == 0) return new Color(1f, 1f, 1f, 0.45f);
        int within = row % EditorChart.RowsPerBeat;
        if (within % 24 == 0) return new Color(0.9f, 0.25f, 0.25f, 0.5f);   // 1/2
        if (within % 16 == 0) return new Color(0.7f, 0.35f, 0.9f, 0.5f);   // 1/3
        if (within % 12 == 0) return new Color(0.3f, 0.5f, 1f, 0.5f);      // 1/4
        if (within % 8 == 0) return new Color(0.85f, 0.45f, 0.8f, 0.4f);   // 1/6
        if (within % 6 == 0) return new Color(0.95f, 0.85f, 0.3f, 0.4f);   // 1/8
        return new Color(0.6f, 0.6f, 0.6f, 0.3f);
    }

    private static Color NoteColor(EditorChart.Note note) => note.Type switch
    {
        '1' => new Color(0.35f, 0.65f, 1f, 1f),
        '2' => new Color(0.6f, 0.4f, 0.95f, 1f),
        '4' => new Color(0.4f, 0.85f, 0.55f, 1f),
        'M' => new Color(0.84f, 0.28f, 0.2f, 1f),
        _ => new Color(0.95f, 0.8f, 0.3f, 1f),
    };

    private static void DrawField(double now)
    {
        float halfH = FieldHeight / 2f;
        double bottom = YToTime(-halfH - NoteHeight, now), top = YToTime(halfH + NoteHeight, now);
        float width = -FieldLeft * 2;
        int lines = 0;

        int step = SnapRows;
        int firstRow = Math.Max(0, (int)Math.Floor(chart!.SecondsToRow(bottom) / step) * step);
        int lastRow = (int)Math.Ceiling(chart.SecondsToRow(top));
        for (int row = firstRow; row <= lastRow && lines < 600; row += step)
        {
            float y = TimeToY(chart.RowToSeconds(row), now);
            bool measure = row % EditorChart.RowsPerMeasure == 0;
            PlaceField(Pooled(linePool, lines++, field!, "Line"), 0, y, width, measure ? 3 : 2, SnapColor(row));
        }
        HideFrom(linePool, lines);

        int images = 0;
        int firstNote = FirstNoteAtOrAfter(Math.Max(0, (int)chart.SecondsToRow(bottom) - EditorChart.RowsPerMeasure * 64));
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = firstNote; i < chart.Notes.Count; i++)
            {
                var note = chart.Notes[i];
                double start = chart.RowToSeconds(note.Row);
                if (start > top) break;
                double end = note.IsLong ? chart.RowToSeconds(note.EndRow) : start;
                if (end < bottom) continue;
                bool selected = selection.Contains(NoteKey(note));
                var color = NoteColor(note);
                float x = LaneX(note.Lane), w = LaneWidth - 14;
                if (pass == 0)
                {
                    if (!note.IsLong) continue;
                    float y0 = TimeToY(start, now), y1 = TimeToY(end, now);
                    PlaceField(Pooled(notePool, images++, field!, "Note"), x, (y0 + y1) / 2f, w * 0.6f, Math.Max(2f, y1 - y0), new Color(color.r, color.g, color.b, 0.45f));
                    PlaceField(Pooled(notePool, images++, field!, "Note"), x, y1, w * 0.8f, 8f, color);
                    continue;
                }
                float y = TimeToY(start, now);
                if (selected) PlaceField(Pooled(notePool, images++, field!, "Note"), x, y, w + 10, NoteHeight + 10, Color.white);
                PlaceField(Pooled(notePool, images++, field!, "Note"), x, y, note.Type == 'M' ? w * 0.6f : w, note.Type == 'M' ? NoteHeight * 0.8f : NoteHeight, color);
            }
        }
        if (drag == DragKind.Hold)
        {
            float y0 = TimeToY(chart.RowToSeconds(dragStartRow), now), y1 = TimeToY(chart.RowToSeconds(Math.Max(dragStartRow, hoverRow)), now);
            PlaceField(Pooled(notePool, images++, field!, "Note"), LaneX(dragLane), (y0 + y1) / 2f, (LaneWidth - 14) * 0.6f, Math.Max(2f, y1 - y0), new Color(0.6f, 0.4f, 0.95f, 0.35f));
        }
        if (hoverLane >= 0 && tool != Tool.Select && drag == DragKind.None)
            PlaceField(Pooled(notePool, images++, field!, "Note"), LaneX(hoverLane), TimeToY(chart.RowToSeconds(hoverRow), now), LaneWidth - 14, NoteHeight, new Color(1f, 1f, 1f, 0.18f));

        // Bookmarks, scroll speed changes and events as flags left of the lanes.
        int labels = 0;
        foreach (double b in bookmarks)
            if (b >= bottom && b <= top) PlaceField(Pooled(notePool, images++, field!, "Note"), FieldLeft - 22, TimeToY(b, now), 34, 4, Hex(0x5AA0FF));
        foreach (var (beat, ratio) in scrolls)
        {
            double t = chart.BeatToSeconds(beat);
            if (t < bottom || t > top) continue;
            float y = TimeToY(t, now);
            PlaceField(Pooled(notePool, images++, field!, "Note"), 0, y, width, 2, Hex(0x4FD1A5, 0.9f));
            Label(labels++, $"x{ratio:0.##}", y, Hex(0x4FD1A5));
        }
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Time + e.Length < bottom || e.Time > top) continue;
            float y0 = TimeToY(e.Time, now), y1 = TimeToY(e.Time + e.Length, now);
            bool picked = tab == Tab.Events && i == eventIndex;
            PlaceField(Pooled(notePool, images++, field!, "Note"), FieldLeft - 12, (y0 + y1) / 2f, 6, Math.Max(4f, y1 - y0), picked ? Accent : Hex(0xF2B02E, 0.6f));
            if (y0 > -halfH && y0 < halfH) Label(labels++, e.Verb, y0, picked ? Accent : Hex(0xF2B02E));
        }
        for (int i = labels; i < labelPool.Count; i++) if (labelPool[i].gameObject.activeSelf) labelPool[i].gameObject.SetActive(false);
        HideFrom(notePool, images);

        PlaceField(judgeLine!, 0, JudgeY, width + 30, 4, Accent);
        judgeLine!.transform.SetAsLastSibling();
        selectionBox!.transform.SetAsLastSibling();
        if (drag == DragKind.Box)
        {
            selectionBox.gameObject.SetActive(true);
            float y0 = TimeToY(boxStartTime, now), y1 = mouseY;
            PlaceField(selectionBox, (boxStartX + mouseX) / 2f, (y0 + y1) / 2f, Math.Abs(mouseX - boxStartX), Math.Abs(y1 - y0), new Color(1f, 1f, 1f, 0.12f));
        }
        else if (selectionBox.gameObject.activeSelf) selectionBox.gameObject.SetActive(false);

        DrawWaveform(now);
        DrawTimeline(now);
    }

    private static void Label(int index, string text, float y, Color color)
    {
        if (index >= 24) return;
        while (labelPool.Count <= index)
        {
            var t = MakeText("FieldLabel", field!, 15, TextAlignmentOptions.Right);
            t.rectTransform.anchorMin = t.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            t.rectTransform.pivot = new Vector2(1, 0.5f);
            t.rectTransform.sizeDelta = new Vector2(FieldLeftRoom - 30, 24);
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Ellipsis;
            labelPool.Add(t);
        }
        var label = labelPool[index];
        if (!label.gameObject.activeSelf) label.gameObject.SetActive(true);
        label.text = Escape(text);
        label.color = color;
        label.rectTransform.anchoredPosition = new Vector2(FieldLeft - 24 - FieldCentre, y);
    }

    /// <summary>The music's loudness beside the lanes, like the waveform in osu!'s editor.</summary>
    private static void DrawWaveform(double now)
    {
        int used = 0;
        if (peaks != null && peakRate > 0 && waveformOn)
        {
            float x = -FieldLeft + 14;
            const float stepPx = 4f;
            float halfH = FieldHeight / 2f;
            for (float y = -halfH; y < halfH; y += stepPx)
            {
                int index = (int)((YToTime(y, now) - audioOrigin) * peakRate);
                if (index < 0 || index >= peaks.Length) continue;
                float v = peaks[index];
                if (v < 0.02f) continue;
                PlaceField(Pooled(wavePool, used++, field!, "Wave"), x + v * (FieldRightRoom - 24) / 2f, y, v * (FieldRightRoom - 24), stepPx - 1, Hex(0x8C7FB8, 0.55f));
            }
        }
        HideFrom(wavePool, used);
    }

    private static void DrawTimeline(double now)
    {
        double length = SongLength - Start;
        float width = timeline!.rect.width;
        float fraction = length > 0 ? (float)Math.Clamp((now - Start) / length, 0, 1) : 0;
        timelineFill!.rectTransform.sizeDelta = new Vector2(width * fraction, 0);
        timelinePlayhead!.rectTransform.anchoredPosition = new Vector2(width * fraction, 0);
        int used = 0;
        float X(double t) => (float)((t - Start) / Math.Max(0.001, length)) * width;
        foreach (double b in bookmarks) Marker(used++, X(b), 18, Hex(0x5AA0FF));
        foreach (var (beat, _) in scrolls) Marker(used++, X(chart!.BeatToSeconds(beat)), 14, Hex(0x4FD1A5));
        foreach (var e in events) Marker(used++, X(e.Time), 10, Hex(0xF2B02E, 0.8f));
        for (int i = used; i < markerPool.Count; i++) if (markerPool[i].gameObject.activeSelf) markerPool[i].gameObject.SetActive(false);
        timelinePlayhead.transform.SetAsLastSibling();

        void Marker(int index, float x, float h, Color color)
        {
            while (markerPool.Count <= index)
            {
                var m = MakeImage("Marker", timeline, color);
                m.rectTransform.anchorMin = m.rectTransform.anchorMax = new Vector2(0, 0.5f);
                markerPool.Add(m);
            }
            var marker = markerPool[index];
            if (!marker.gameObject.activeSelf) marker.gameObject.SetActive(true);
            marker.color = color;
            marker.rectTransform.anchoredPosition = new Vector2(x, 0);
            marker.rectTransform.sizeDelta = new Vector2(3, h);
        }
    }
}
