using UnityEngine;
using UnityEngine.UI;
using static NocturneFlatScroll.EditorUi;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

// The editor's screen, laid out like osu!'s editor: a top bar with tabs and file buttons, a
// toolbox on the left, the playfield in the middle, a panel on the right for the current tab,
// and the timeline along the bottom. Everything is anchored to the screen's edges, so it fits
// any window shape; buttons are clickable and show their keys. The widgets, palette, list screens
// and status line come from EditorUi.
internal static partial class ChartEditor
{
    private enum Tab { Compose, Timing, Events, Setup, Keys }

    private const float TopH = 64f, BottomH = 92f, LeftW = 300f, RightW = 430f;
    private const float NoteHeight = 26f;
    private static readonly Color LaneColor = Hex(0x1A1724);
    private static readonly Color LaneEdge = Hex(0x3A3450);

    private static Tab tab = Tab.Compose;
    private static RectTransform? editPanel, fieldArea, field, keysPanel, leftPanel, rightPanel, timeline;
    private static TMP_Text? titleText, timeText, infoText, tabText, keysHint;
    private static Image? timelineFill, timelinePlayhead, judgeLine, selectionBox;
    private static readonly List<Image> notePool = new();
    private static readonly List<Image> linePool = new();
    private static readonly List<Image> wavePool = new();
    private static readonly List<Image> markerPool = new();
    private static readonly List<TMP_Text> labelPool = new();
    private static readonly List<UiButton> eventRows = new();

    // ---- building --------------------------------------------------------------------------

    private static void BuildCanvas()
    {
        eventRows.Clear();
        markerPool.Clear();
        ClearBattleWidgets();
        ui = new EditorUi("NocturneButBetter Chart Editor");
        Ui.BuildList();
        BuildEditor();
        editPanel!.gameObject.SetActive(false);
    }

    private static void BuildEditor()
    {
        editPanel = MakeRect("Edit", Ui.CanvasRect);
        Stretch(editPanel, 0, 0, 0, 0);

        // The playfield sits between the panels and bars; the Keys tab uses the same space. A
        // battle's difficulty tabs take a strip above the playfield.
        fieldArea = MakeRect("FieldArea", editPanel);
        Stretch(fieldArea, LeftW, BottomH, RightW, TopH + (battle != null ? DifficultyH : 0));
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
            var b = Ui.MakeButton(top, name, () => SetTab(t));
            b.Active = () => tab == t;
            Place(b.Rect, new Vector2(0, 0.5f), new Vector2(16 + i * 146, 0), new Vector2(138, 44), new Vector2(0, 0.5f));
        }
        titleText = MakeText("Title", top, 20, TextAlignmentOptions.Center);
        titleText.enableWordWrapping = false;
        titleText.overflowMode = TextOverflowModes.Ellipsis;
        titleText.rectTransform.anchorMin = new Vector2(0, 0);
        titleText.rectTransform.anchorMax = new Vector2(1, 1);
        titleText.rectTransform.offsetMin = new Vector2(16 + 5 * 146 + 10, 0);
        // A battle has no Export button: the battle creator exports the whole battle.
        titleText.rectTransform.offsetMax = new Vector2(-(16 + 110 + 12 + (battle != null ? 0 : 170 + 12) + 150 + 12 + 130 + 16), 0);
        var exit = Ui.MakeButton(top, "Exit", RequestClose);
        Place(exit.Rect, new Vector2(1, 0.5f), new Vector2(-16, 0), new Vector2(110, 44), new Vector2(1, 0.5f));
        if (battle == null)
        {
            var export = Ui.MakeButton(top, "Export", StartExportPack);
            export.Text = () => $"Export <size=65%><color=#9D92B4>{ShortKey(EditorAction.Export)}</color></size>";
            Place(export.Rect, new Vector2(1, 0.5f), new Vector2(-138, 0), new Vector2(170, 44), new Vector2(1, 0.5f));
        }
        var save = Ui.MakeButton(top, "Save", () => Save());
        save.Text = () => $"Save <size=65%><color=#9D92B4>{ShortKey(EditorAction.Save)}</color></size>";
        save.Active = () => dirty;
        Place(save.Rect, new Vector2(1, 0.5f), new Vector2(battle != null ? -138 : -320, 0), new Vector2(150, 44), new Vector2(1, 0.5f));
        // Plays the chart in a battle from the play position; with Shift, from the start.
        var test = Ui.MakeButton(top, "Test", () => StartTest(fromStart: InputKeyboard.current is { } k && EditorInput.Shift(k)));
        test.Text = () => $"Test <size=65%><color=#9D92B4>{ShortKey(EditorAction.TestHere)}</color></size>";
        test.Active = () => TestPlay.Active;
        Place(test.Rect, new Vector2(1, 0.5f), new Vector2(battle != null ? -300 : -482, 0), new Vector2(130, 44), new Vector2(1, 0.5f));

        BuildLeftPanel();
        BuildRightPanel();
        BuildBottomBar();
        if (battle != null) BuildBattleWidgets();

        // Messages go last, so they draw over the notes.
        Ui.BuildStatus(editPanel);
    }

    private static void BuildLeftPanel()
    {
        leftPanel = MakeImage("Toolbox", editPanel!, PanelColor).rectTransform;
        leftPanel.anchorMin = new Vector2(0, 0);
        leftPanel.anchorMax = new Vector2(0, 1);
        leftPanel.pivot = new Vector2(0, 0.5f);
        leftPanel.offsetMin = new Vector2(0, BottomH);
        leftPanel.offsetMax = new Vector2(LeftW, -TopH);
        Ui.AddSolidPanel(leftPanel);
        float y = -14;
        Ui.Header(leftPanel, "Tools", ref y);
        var tools = new[] { (Tool.Select, "Select", EditorAction.ToolSelect), (Tool.Note, "Note", EditorAction.ToolNote), (Tool.Hold, "Hold", EditorAction.ToolHold), (Tool.Mine, "Mine", EditorAction.ToolMine) };
        foreach (var (t, name, action) in tools)
        {
            var b = Ui.MakeButton(leftPanel, name, () => SetTool(t));
            b.Text = () => $"{name}  <size=65%><color=#9D92B4>{ShortKey(action)}</color></size>";
            b.Active = () => tool == t;
            b.Label.alignment = TextAlignmentOptions.Left;
            b.Label.margin = new Vector4(16, 0, 8, 0);
            PlaceTop(b.Rect, 16, y, 268, 40);
            y -= 45;
        }
        y -= 6;
        Ui.Header(leftPanel, "Beat snap", ref y);
        Ui.Stepper(leftPanel, ref y, () => $"1/{Snaps[snapIndex]}", () => snapIndex = Math.Max(0, snapIndex - 1), () => snapIndex = Math.Min(Snaps.Length - 1, snapIndex + 1));
        Ui.Header(leftPanel, "Zoom", ref y);
        Ui.Stepper(leftPanel, ref y, () => $"{pixelsPerSecond / 7f:0}%", () => Zoom(1 / 1.2f), () => Zoom(1.2f));
        Ui.Header(leftPanel, "Playback speed", ref y);
        for (int i = 0; i < Speeds.Length; i++)
        {
            int index = i;
            var b = Ui.MakeButton(leftPanel, $"{Speeds[i] * 100:0}%", () => SetSpeed(index));
            b.Active = () => speedIndex == index;
            b.Label.fontSize = 17;
            PlaceTop(b.Rect, 16 + i * 68, y, 64, 36);
        }
        y -= 48;
        Ui.Header(leftPanel, "Sound", ref y);
        Ui.Toggle(leftPanel, ref y, () => $"Note ticks: {(ticksOn ? "on" : "off")}", () => ticksOn, ToggleTicks);
        Ui.Toggle(leftPanel, ref y, () => $"Metronome: {(metronomeOn ? "on" : "off")}", () => metronomeOn, ToggleMetronome);
        Ui.Toggle(leftPanel, ref y, () => $"Waveform: {(waveformOn ? "on" : "off")}", () => waveformOn, () => waveformOn = !waveformOn);
        Ui.Header(leftPanel, "Music volume", ref y);
        Ui.Stepper(leftPanel, ref y, () => $"{musicVolume * 100:0}%", () => SetMusicVolume(musicVolume - 0.1f), () => SetMusicVolume(musicVolume + 0.1f));
        Ui.Header(leftPanel, "Tick volume", ref y);
        Ui.Stepper(leftPanel, ref y, () => $"{tickVolume * 100:0}%", () => SetTickVolume(tickVolume - 0.1f), () => SetTickVolume(tickVolume + 0.1f));
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
        Ui.AddSolidPanel(rightPanel);
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
            var b = Ui.MakeButton(rightPanel, label, doIt);
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
            var b = Ui.MakeButton(rightPanel, label, doIt);
            b.Visible = () => tab == Tab.Timing;
            b.Label.fontSize = 17;
            PlaceTop(b.Rect, 16 + (i % 2) * (width / 2 + 4), -270 - (i / 2) * 46, width / 2 - 4, 40);
        }
        if (battle != null) BuildBattleTiming(width, -270 - (timing.Length / 2) * 46);

        // Events: the chart's events, and what to do with the picked one.
        for (int i = 0; i < EventRowsVisible; i++)
        {
            int row = i;
            var b = Ui.MakeButton(rightPanel, "", () => { if (dialogueView) ClickDialogueRow(row); else ClickEventRow(row); });
            b.Visible = () => tab == Tab.Events && (dialogueView ? DialogueRowShown(row) : EventRowShown(row));
            b.Label.alignment = TextAlignmentOptions.Left;
            b.Label.margin = new Vector4(10, 0, 6, 0);
            b.Label.fontSize = 15;
            PlaceTop(b.Rect, 16, -262 - i * 32, width, 29);
            eventRows.Add(b);
        }
        var eventButtons = new List<(string Label, Action Do)>
        {
            ("Add here", AddEventHere), ("Edit text", () => StartTyping(TextField.EventMods)),
            ("Move here", MovePickedEventHere), ("Copy here", DuplicatePickedEvent),
            ("Earlier", () => NudgePickedEvent(-1)), ("Later", () => NudgePickedEvent(1)),
            ("Shorter", () => ChangePickedEventLength(-0.25)), ("Longer", () => ChangePickedEventLength(0.25)),
            ("Delete", DeletePickedEvent), ("Song's events", RestoreSongEvents),
        };
        // A battle has no song events to go back to; it gets player attacks instead.
        if (battle != null)
        {
            eventButtons[^1] = ("Delete all", ClearEvents);
            eventButtons.Add(("Player attack", AddPlayerAttack));
        }
        for (int i = 0; i < eventButtons.Count; i++)
        {
            var (label, doIt) = eventButtons[i];
            var b = Ui.MakeButton(rightPanel, label, doIt);
            b.Visible = () => tab == Tab.Events && !dialogueView;
            b.Label.fontSize = 16;
            PlaceTop(b.Rect, 16 + (i % 2) * (width / 2 + 4), -262 - EventRowsVisible * 32 - 8 - (i / 2) * 40, width / 2 - 4, 35);
        }
        // A battle's dialogue lines, in the same place.
        if (battle != null) BuildDialogueView(width);

        // Setup: the chart's name and author, and the file actions. A battle's are its difficulties'.
        if (battle != null) BuildBattleSetup(width);
        else BuildSetup(width);

        // Keys: back to the defaults.
        var reset = Ui.MakeButton(rightPanel, "Reset all keys", () => { ResetBindings(); Say("Keys are back to the defaults", 3f); });
        reset.Visible = () => tab == Tab.Keys;
        PlaceTop(reset.Rect, 16, -270, width, 42);
    }

    private static void BuildSetup(float width)
    {
        var name = Ui.MakeButton(rightPanel!, "", () => StartTyping(TextField.Title));
        name.Text = () => $"Name: {Escape(title)}{(typing == TextField.Title ? "_" : "")}";
        name.Visible = () => tab == Tab.Setup;
        name.Active = () => typing == TextField.Title;
        name.Label.alignment = TextAlignmentOptions.Left;
        name.Label.margin = new Vector4(12, 0, 8, 0);
        PlaceTop(name.Rect, 16, -270, width, 42);
        var who = Ui.MakeButton(rightPanel!, "", () => StartTyping(TextField.Author));
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
            var b = Ui.MakeButton(rightPanel!, label, doIt);
            b.Visible = () => tab == Tab.Setup;
            PlaceTop(b.Rect, 16 + (i % 2) * (width / 2 + 4), -378 - (i / 2) * 46, width / 2 - 4, 40);
        }
    }

    private static void BuildBottomBar()
    {
        var bar = MakeImage("BottomBar", editPanel!, PanelColor).rectTransform;
        bar.anchorMin = new Vector2(0, 0);
        bar.anchorMax = new Vector2(1, 0);
        bar.pivot = new Vector2(0.5f, 0);
        bar.sizeDelta = new Vector2(0, BottomH);
        bar.anchoredPosition = Vector2.zero;
        var play = Ui.MakeButton(bar, "Play", TogglePlay);
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
        // 25 rows a column (50 actions) end at 916 of the panel's 924 units; more need a smaller pitch.
        int perColumn = (DefaultBindings.Length + 1) / 2;
        for (int i = 0; i < DefaultBindings.Length; i++)
        {
            var action = DefaultBindings[i].Action;
            var b = Ui.MakeButton(keysPanel!, "", () => { keyMap.Rebinding = action; Say($"Press the new key for \"{LabelOf(action)}\"", 30f); });
            b.Text = () => $"{LabelOf(action)}<pos=58%><color=#F2B02E>{(keyMap.Rebinding == action ? "press a key..." : KeysFor(action))}</color>";
            b.Active = () => keyMap.Rebinding == action;
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

    // ---- per-frame UI ------------------------------------------------------------------------

    private static void ShowScreen(Screen next)
    {
        screen = next;
        Ui.ListPanel!.gameObject.SetActive(next != Screen.Edit);
        editPanel!.gameObject.SetActive(next == Screen.Edit);
    }

    private static void SetTab(Tab next)
    {
        tab = next;
        keyMap.Rebinding = null;
        typing = TextField.None;
        EditorInput.EndText();
        speakerChoices = null;
        keysPanel!.gameObject.SetActive(tab == Tab.Keys);
        fieldArea!.gameObject.SetActive(tab != Tab.Keys);
        if (difficultyBar) difficultyBar!.gameObject.SetActive(tab != Tab.Keys);
        // The offset loop belongs to the Timing tab.
        if (tab != Tab.Timing) looping = false;
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
        for (int lane = 0; lane < chart.Lanes; lane++) StretchColumn(MakeImage("Lane" + lane, field, lane == AttackLane ? AttackLaneColor : LaneColor), LaneX(lane), LaneWidth - 4);
        StretchColumn(MakeImage("EdgeL", field, LaneEdge), FieldLeft - 2, 3);
        StretchColumn(MakeImage("EdgeR", field, LaneEdge), -FieldLeft + 2, 3);
        BuildAttackLabel();
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
        if (hoverLane >= 0 && tool != Tool.Select && drag == DragKind.None && (battle == null || tabCharted))
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
        if (battle != null) DrawDialogueLane(now, bottom, top, width, ref images, ref labels);
        for (int i = labels; i < labelPool.Count; i++) if (labelPool[i].gameObject.activeSelf) labelPool[i].gameObject.SetActive(false);
        HideFrom(notePool, images);

        PlaceField(judgeLine!, 0, JudgeY, width + 30, 4, Accent);
        judgeLine!.transform.SetAsLastSibling();
        PlaceAttackLabel();
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

    /// <param name="right">How far left of the lanes the label ends (dialogue lines' end further left than events').</param>
    private static void Label(int index, string text, float y, Color color, float right = 24)
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
        label.rectTransform.anchoredPosition = new Vector2(FieldLeft - right - FieldCentre, y);
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
        // A battle's dialogue lines; the ones that stop the song full height.
        foreach (var cue in cues)
            if (CueTime(cue) is double t) Marker(used++, X(t), cue.Pause ? 20 : 14, DialogueColor);
        for (int i = used; i < markerPool.Count; i++) if (markerPool[i].gameObject.activeSelf) markerPool[i].gameObject.SetActive(false);
        timelinePlayhead.transform.SetAsLastSibling();

        void Marker(int index, float x, float h, Color color)
        {
            while (markerPool.Count <= index)
            {
                var m = MakeImage("Marker", timeline!, color);
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
