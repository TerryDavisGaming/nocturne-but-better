using UnityEngine;
using UnityEngine.UI;
using static NocturneFlatScroll.EditorInput;
using static NocturneFlatScroll.EditorPageKit;
using static NocturneFlatScroll.EditorUi;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;

namespace NocturneFlatScroll;

// The page's frame: the tabs along the top, the list on the left with the bar above it (Browse's
// search and filters), the detail panel on the right with the entry's buttons, and the bottom bar
// with the progress of a download or upload, the keys, and Close. The forms (the notice, a report,
// the upload's steps) use the same top and bottom bars. The keyboard and the pad move through the
// list, the bar above it and the panel's buttons (Right and Left go between them); every button is
// clickable.
internal static partial class HubPage
{
    private const float TopH = 64f, SubTop = 76f, SubH = 48f, ListTop = 136f, BottomH = 104f, PanelW = 640f, Edge = 24f;
    private const float PictureSide = 176f, ButtonRows = 3;

    private static RectTransform? mainPanel, formsPanel, bottomBar, topBar, filterBar, detailPanel;
    private static ThumbList? list;
    private static TMP_Text? listMessage, listFoot, subLine, hintsText;
    private static ProgressBar? bottomProgress;
    private static RawImage? panelPicture;
    private static Image? panelTile;
    private static TMP_Text? panelTileText, panelTitle, panelLine1, panelLine2, panelLine3, panelBadge, panelFacts, panelTable, panelBody, panelNote;
    private static readonly List<Control> panelButtons = new();
    private static readonly List<Control> barControls = new();

    /// <summary>A button in the detail panel: what it says, its key, and what it does.</summary>
    private sealed class PanelAction
    {
        internal string Text = "";
        internal string Key = "";
        internal Action Do = () => { };
    }

    private static List<PanelAction> panelActions = new();

    // ---- building --------------------------------------------------------------------------------

    private static void BuildPage()
    {
        // The pickers' list screen, first, so the page's own panels draw over it.
        Ui.BuildList();
        BuildListBack();
        Kit.BuildPickerFace();
        mainPanel = MakeRect("Main", Ui.CanvasRect);
        Stretch(mainPanel, 0, 0, 0, 0);
        BuildFilterBar();
        BuildListArea();
        BuildDetailPanel();
        formsPanel = MakeRect("Forms", Ui.CanvasRect);
        Stretch(formsPanel, 0, 0, 0, 0);
        BuildForms();
        BuildTopBar();
        BuildBottomBar();
        Kit.BuildFocusMarker(Ui.CanvasRect);
        // Messages go last, so they draw over the rest; they can hold text from the hub, so they're plain.
        Ui.BuildStatus(Ui.CanvasRect, plain: true);
    }

    private static void BuildTopBar()
    {
        topBar = AddBar(Ui.CanvasRect, "TopBar", true, TopH);
        var title = MakeText("Title", topBar, 22, TextAlignmentOptions.Left);
        title.text = "GET CUSTOM BATTLES";
        title.enableWordWrapping = false;
        PlaceTop(title.rectTransform, Edge, 0, 320, TopH);
        float x = 360;
        foreach (var (t, w) in new[] { (Tab.Browse, 150f), (Tab.Installed, 300f), (Tab.Mine, 230f), (Tab.Upload, 150f) })
        {
            var which = t;
            var b = Ui.MakeButton(topBar, "", () => { if (!Blocked) SetTab(which); });
            b.Text = () => TabText(which);
            b.Active = () => tab == which;
            b.Visible = () => screen == Screen.Main;
            PlaceTop(b.Rect, x, -10, w, 44);
            x += w + 10;
        }
        var linkText = MakeText("Link", topBar, 18, TextAlignmentOptions.Right);
        linkText.enableWordWrapping = false;
        linkText.rectTransform.anchorMin = linkText.rectTransform.anchorMax = new Vector2(1, 0.5f);
        linkText.rectTransform.pivot = new Vector2(1, 0.5f);
        linkText.rectTransform.sizeDelta = new Vector2(420, TopH);
        linkText.rectTransform.anchoredPosition = new Vector2(-Edge, 0);
        Ui.AddLiveText(linkText, LinkText);
    }

    private static string TabText(Tab t)
    {
        switch (t)
        {
            case Tab.Installed:
                int count = installedRows.Count, updates = store?.UpdateCount ?? 0;
                return "Installed" + (store == null ? "" : $" {count}") + (updates > 0 ? $" <size=80%><color={(tab == t ? "#16131F" : "#F2B02E")}>({Plural(updates, "update", "updates")})</color></size>" : "");
            case Tab.Mine:
                return "My uploads" + (mine != null && mine.Count > 0 ? $" {mine.Count}" : "");
            case Tab.Upload:
                return "Upload";
            default:
                return "Browse";
        }
    }

    private static string LinkText() => link switch
    {
        Link.Opening => "<color=#9D92B4>hub: opening...</color>",
        Link.Connecting => "<color=#9D92B4>hub: connecting...</color>",
        Link.Online => "<color=#4FD1A5>hub: online</color>" + (info != null && !info.UploadsOpen ? " <color=#9D92B4>(uploads closed)</color>" : ""),
        Link.TooOld => "<color=#F2B02E>hub: update the mod</color>",
        _ => "<color=#F2B02E>hub: can't be used now</color>",
    };

    private static void BuildFilterBar()
    {
        filterBar = MakeRect("Filters", mainPanel!);
        filterBar.anchorMin = new Vector2(0, 1);
        filterBar.anchorMax = new Vector2(1, 1);
        filterBar.pivot = new Vector2(0, 1);
        filterBar.offsetMin = new Vector2(Edge, -SubTop - SubH);
        filterBar.offsetMax = new Vector2(-Edge, -SubTop);
        float y = -2, x = 0;
        Kit.AddField(filterBar, barControls, x, ref y, 620, SearchField);
        x += 632;
        y = -2;
        Kit.AddChoice(filterBar, barControls, x, ref y, 270, "Type", KindWords, () => CycleKind(1));
        x += 282;
        y = -2;
        Kit.AddChoice(filterBar, barControls, x, ref y, 220, "Lanes", LanesWords, () => CycleLanes(1));
        x += 232;
        y = -2;
        Kit.AddChoice(filterBar, barControls, x, ref y, 340, "Sort", SortWords, () => CycleSort(1));
        x += 352;
        // "More by this uploader": the uploader's name is the hub's text, so its label is plain.
        var chip = Kit.AddButton(filterBar, barControls, x, -2, 360, RowH, "", ClearUploader, () => byUploader != null);
        chip.Label.richText = false;
        chip.Label.parseCtrlCharacters = false;
        chip.Label.fontSize = 19;
        chip.Text = () => byUploader == null ? "" : $"By {UploaderName(byUploader)}   (x clears)";

        subLine = MakePlainText("SubLine", mainPanel!, 19, TextAlignmentOptions.Left);
        subLine.color = DimText;
        subLine.enableWordWrapping = false;
        subLine.overflowMode = TextOverflowModes.Ellipsis;
        subLine.rectTransform.anchorMin = new Vector2(0, 1);
        subLine.rectTransform.anchorMax = new Vector2(1, 1);
        subLine.rectTransform.pivot = new Vector2(0, 1);
        subLine.rectTransform.offsetMin = new Vector2(Edge + 4, -SubTop - SubH);
        subLine.rectTransform.offsetMax = new Vector2(-Edge, -SubTop);
    }

    private static void BuildListArea()
    {
        list = new ThumbList("List", mainPanel!);
        var rect = list.Rect;
        rect.anchorMin = new Vector2(0, 1);
        rect.anchorMax = new Vector2(1, 1);
        rect.pivot = new Vector2(0, 1);
        rect.offsetMin = new Vector2(Edge, -ListTop - ThumbList.Height);
        rect.offsetMax = new Vector2(-(PanelW + 2 * Edge), -ListTop);
        listMessage = MakePlainText("Message", mainPanel!, 24, TextAlignmentOptions.Top);
        listMessage.color = DimText;
        listMessage.enableWordWrapping = true;
        var m = listMessage.rectTransform;
        m.anchorMin = new Vector2(0, 1);
        m.anchorMax = new Vector2(1, 1);
        m.pivot = new Vector2(0, 1);
        m.offsetMin = new Vector2(Edge + 60, -ListTop - 360);
        m.offsetMax = new Vector2(-(PanelW + 2 * Edge) - 60, -ListTop - 160);
        // One line under the rows (between them and the bottom bar): a later page that failed, or the hub out of reach.
        listFoot = MakePlainText("Foot", mainPanel!, 18, TextAlignmentOptions.Left);
        listFoot.color = Hex(0xF2B02E);
        listFoot.enableWordWrapping = false;
        listFoot.overflowMode = TextOverflowModes.Ellipsis;
        var f = listFoot.rectTransform;
        f.anchorMin = new Vector2(0, 1);
        f.anchorMax = new Vector2(1, 1);
        f.pivot = new Vector2(0, 1);
        f.offsetMin = new Vector2(Edge + 8, -ListTop - ThumbList.Height - 28);
        f.offsetMax = new Vector2(-(PanelW + 2 * Edge), -ListTop - ThumbList.Height - 2);
        var retry = Ui.MakeButton(mainPanel!, "", () => { if (!Blocked) Refresh(); });
        retry.Text = () => $"Try again <size=65%><color=#9D92B4>{(PadNames ? "A" : "F5")}</color></size>";
        retry.Visible = () => screen == Screen.Main && ListCount() == 0 && RetryOffered();
        var r = retry.Rect;
        r.anchorMin = r.anchorMax = new Vector2(0, 1);
        r.pivot = new Vector2(0, 1);
        r.sizeDelta = new Vector2(240, 52);
        r.anchoredPosition = new Vector2(Edge + 60, -ListTop - 380);
    }

    private static void BuildDetailPanel()
    {
        detailPanel = MakeImage("Detail", mainPanel!, PanelColor).rectTransform;
        detailPanel.anchorMin = new Vector2(1, 0);
        detailPanel.anchorMax = new Vector2(1, 1);
        detailPanel.pivot = new Vector2(1, 0.5f);
        detailPanel.offsetMin = new Vector2(-(PanelW + Edge), BottomH + 12);
        detailPanel.offsetMax = new Vector2(-Edge, -ListTop);
        panelTile = MakeImage("Tile", detailPanel, ButtonHover);
        PlaceTop(panelTile.rectTransform, 20, -20, PictureSide, PictureSide);
        panelTileText = MakePlainText("Letters", panelTile.rectTransform, 60, TextAlignmentOptions.Center);
        panelTileText.enableWordWrapping = false;
        Stretch(panelTileText.rectTransform, 6, 6, 6, 6);
        panelPicture = MakeRawImage("Picture", detailPanel);
        PlaceTop(panelPicture.rectTransform, 20, -20, PictureSide, PictureSide);
        float x = 20 + PictureSide + 16, w = PanelW - x - 20;
        panelTitle = PanelText("Title", true, 26, TextColor, x, -16, w, 66, wrap: true);
        panelLine1 = PanelText("Line1", true, 20, TextColor, x, -84, w, 28);
        panelLine2 = PanelText("Line2", true, 17, DimText, x, -114, w, 26);
        panelLine3 = PanelText("Line3", true, 17, DimText, x, -142, w, 26);
        panelBadge = PanelText("Badge", false, 17, Hex(0xB9A6FF), x, -170, w, 26);
        panelFacts = PanelText("Facts", false, 18, DimText, 20, -210, PanelW - 40, 98, wrap: true);
        panelTable = PanelText("Table", true, 17, TextColor, 20, -312, PanelW - 40, 126, wrap: true);
        float buttonsTop = 16 + ButtonRows * 52;
        panelBody = MakePlainText("Body", detailPanel, 17, TextAlignmentOptions.TopLeft);
        panelBody.enableWordWrapping = true;
        panelBody.overflowMode = TextOverflowModes.Ellipsis;
        panelBody.rectTransform.anchorMin = Vector2.zero;
        panelBody.rectTransform.anchorMax = Vector2.one;
        panelBody.rectTransform.offsetMin = new Vector2(20, buttonsTop + 76);
        panelBody.rectTransform.offsetMax = new Vector2(-20, -446);
        // The notes can hold the hub's words (why a download failed), so they're plain too.
        panelNote = MakePlainText("Note", detailPanel, 17, TextAlignmentOptions.BottomLeft);
        panelNote.color = Hex(0xF2B02E);
        panelNote.enableWordWrapping = true;
        panelNote.overflowMode = TextOverflowModes.Ellipsis;
        panelNote.rectTransform.anchorMin = new Vector2(0, 0);
        panelNote.rectTransform.anchorMax = new Vector2(1, 0);
        panelNote.rectTransform.offsetMin = new Vector2(20, buttonsTop + 6);
        panelNote.rectTransform.offsetMax = new Vector2(-20, buttonsTop + 70);
        for (int i = 0; i < ButtonRows * 2; i++)
        {
            int slot = i;
            Action act = () => { if (!Blocked) RunAction(slot); };
            var b = Ui.MakeButton(detailPanel, "", act);
            b.Text = () => slot < panelActions.Count ? ActionText(panelActions[slot]) : "";
            b.Visible = () => screen == Screen.Main && slot < panelActions.Count;
            b.Label.fontSize = 18;
            int row = slot / 2, col = slot % 2;
            float bw = (PanelW - 40 - 12) / 2;
            Place(b.Rect, Vector2.zero, new Vector2(20 + col * (bw + 12), 16 + (ButtonRows - 1 - row) * 52), new Vector2(bw, 46), Vector2.zero);
            panelButtons.Add(new Control { Button = b, Activate = act, Shown = () => slot < panelActions.Count });
        }
    }

    private static TMP_Text PanelText(string name, bool plain, float size, Color color, float x, float y, float w, float h, bool wrap = false)
    {
        var t = plain ? MakePlainText(name, detailPanel!, size, TextAlignmentOptions.TopLeft) : MakeText(name, detailPanel!, size, TextAlignmentOptions.TopLeft);
        t.color = color;
        t.enableWordWrapping = wrap;
        t.overflowMode = TextOverflowModes.Ellipsis;
        PlaceTop(t.rectTransform, x, y, w, h);
        return t;
    }

    private static string ActionText(PanelAction a) =>
        a.Key.Length == 0 ? a.Text : $"{a.Text} <size=65%><color=#9D92B4>{a.Key}</color></size>";

    private static void RunAction(int slot)
    {
        if (slot < 0 || slot >= panelActions.Count) return;
        zone = Zone.Panel;
        panelFocus = slot;
        panelActions[slot].Do();
    }

    private static void BuildBottomBar()
    {
        bottomBar = AddBar(Ui.CanvasRect, "BottomBar", false, BottomH);
        bottomProgress = new ProgressBar("Progress", bottomBar);
        var p = bottomProgress.Rect;
        p.anchorMin = new Vector2(0, 1);
        p.anchorMax = new Vector2(1, 1);
        p.pivot = new Vector2(0, 1);
        p.offsetMin = new Vector2(Edge, -14 - 32);
        p.offsetMax = new Vector2(-(Edge + 200), -14);
        hintsText = MakeText("Keys", bottomBar, 16, TextAlignmentOptions.BottomLeft);
        hintsText.color = DimText;
        hintsText.enableWordWrapping = false;
        hintsText.overflowMode = TextOverflowModes.Ellipsis;
        hintsText.rectTransform.anchorMin = new Vector2(0, 0);
        hintsText.rectTransform.anchorMax = new Vector2(1, 0);
        hintsText.rectTransform.offsetMin = new Vector2(Edge, 12);
        hintsText.rectTransform.offsetMax = new Vector2(-(Edge + 200), 44);
        var close = Ui.MakeButton(bottomBar, "", () => { if (!Blocked) CloseClicked(); });
        close.Text = () => $"Close <size=65%><color=#9D92B4>{(PadNames ? "B" : "Esc")}</color></size>";
        close.Visible = () => screen == Screen.Main;
        Place(close.Rect, new Vector2(1, 0.5f), new Vector2(-Edge, 0), new Vector2(170, 52), new Vector2(1, 0.5f));
    }

    private static void CloseClicked()
    {
        if (download != null) AskStopDownload(leaving: true);
        else Close("Close");
    }

    // ---- tabs ------------------------------------------------------------------------------------

    private static void SetTab(Tab next)
    {
        if (!Kit.FinishTyping()) return;
        if (tab == next) return;
        tab = next;
        zone = Zone.List;
        panelFocus = 0;
        // The tabs share the list's rows: each starts at the top, then shows its picked row.
        list?.ResetScroll();
        switch (tab)
        {
            case Tab.Installed:
                RefreshInUse();
                break;
            case Tab.Mine:
                LoadMine();
                break;
        }
    }

    private static Tab NextTab(int step)
    {
        int count = Enum.GetValues(typeof(Tab)).Length;
        return (Tab)((((int)tab + step) % count + count) % count);
    }

    // ---- the list, per tab ---------------------------------------------------------------------------

    private static int ListCount() => tab switch
    {
        Tab.Browse => browse?.Items.Count ?? 0,
        Tab.Installed => installedRows.Count,
        Tab.Mine => mine?.Count ?? 0,
        _ => UploadRows.Length,
    };

    private static int ListIndex() => tab switch
    {
        Tab.Browse => browseIndex,
        Tab.Installed => installedIndex,
        Tab.Mine => mineIndex,
        _ => uploadIndex,
    };

    private static void SetListIndex(int index)
    {
        int count = ListCount();
        index = count == 0 ? 0 : Math.Clamp(index, 0, count - 1);
        switch (tab)
        {
            case Tab.Browse: browseIndex = index; break;
            case Tab.Installed: installedIndex = index; break;
            case Tab.Mine: mineIndex = index; break;
            default: uploadIndex = index; break;
        }
    }

    private static ThumbRow RowAt(int i) => tab switch
    {
        Tab.Browse => BrowseRow(i),
        Tab.Installed => InstalledRow(i),
        Tab.Mine => MineRow(i),
        _ => UploadRow(i),
    };

    /// <summary>Enter, A or a double click on the list: the row's main action (an empty list that failed: Try again).</summary>
    private static void Primary()
    {
        int count = store == null ? 0 : ListCount();
        if (count == 0 ? RetryOffered() : tab == Tab.Browse && link == Link.Down)
        {
            Refresh();
            return;
        }
        if (count == 0) return;
        switch (tab)
        {
            case Tab.Browse: BrowsePrimary(); break;
            case Tab.Installed: InstalledPrimary(); break;
            case Tab.Mine: MinePrimary(); break;
            default: UploadPrimary(); break;
        }
    }

    /// <summary>Whether an empty list offers Try again: the page's files or the hub couldn't be read, or the tab's list failed.</summary>
    private static bool RetryOffered() => store == null
        ? link == Link.Down
        : tab switch
        {
            Tab.Browse => link == Link.Down || (browseProblem != null && browseRetryAt == 0),
            Tab.Mine => link == Link.Down || mineProblem != null,
            _ => false,
        };

    private static List<PanelAction> Actions() => tab switch
    {
        Tab.Browse => BrowseActions(),
        Tab.Installed => InstalledActions(),
        Tab.Mine => MineActions(),
        _ => UploadActions(),
    };

    // ---- the main screen's frame ----------------------------------------------------------------------

    private static void UpdateMain(InputKeyboard keyboard, InputMouse? mouse)
    {
        // Clicks wait a moment after the screen changes, and while the page waits for work.
        var clicks = Kit.ClicksLive && !Blocked ? mouse : null;
        // A click anywhere finishes the search being typed (clicking it again starts it again).
        if (Kit.Typing != null && clicks != null && clicks.leftButton.wasPressedThisFrame && !Kit.CommitTyping()) clicks = null;
        panelActions = store == null ? new List<PanelAction>() : Actions();
        if (panelFocus >= panelActions.Count) panelFocus = Math.Max(0, panelActions.Count - 1);
        if (zone == Zone.Panel && panelActions.Count == 0) zone = Zone.List;
        if (zone == Zone.Bar && tab != Tab.Browse) zone = Zone.List;
        Ui.UpdateButtons(clicks);
        if (!IsOpen || screen != Screen.Main) return;
        if (Kit.Live)
        {
            if (Blocked)
            {
                if (Pressed(keyboard, Key.Escape) || PadInput.Pressed(PadButton.East)) StopWork();
            }
            else if (Kit.Typing != null) Kit.UpdateTyping(keyboard);
            else HandleMainKeys(keyboard);
        }
        if (!IsOpen || screen != Screen.Main) return;
        // The list follows the search as it's typed, and goes back to the kept one on Esc.
        AfterSearchTyping();
        DrawMain(clicks, mouse);
    }

    private static void HandleMainKeys(InputKeyboard keyboard)
    {
        if (Pressed(keyboard, Key.Escape) || PadInput.Pressed(PadButton.East))
        {
            if (zone != Zone.List) zone = Zone.List;
            else if (download != null) AskStopDownload(leaving: false);
            else Close("Esc");
            return;
        }
        if (Pressed(keyboard, Key.Tab))
        {
            SetTab(NextTab(Shift(keyboard) ? -1 : 1));
            return;
        }
        if (PadInput.Pressed(PadButton.LeftShoulder) || PadInput.Pressed(PadButton.RightShoulder))
        {
            SetTab(NextTab(PadInput.Pressed(PadButton.LeftShoulder) ? -1 : 1));
            return;
        }
        if (Pressed(keyboard, Key.F5))
        {
            Refresh();
            return;
        }
        if (store == null)
        {
            // The page's own files couldn't be read: Enter or A tries again, like the button.
            if (Pressed(keyboard, Key.Enter) || Pressed(keyboard, Key.NumpadEnter) || PadInput.Pressed(PadButton.South)) Primary();
            return;
        }
        if (TabKeys(keyboard)) return;
        switch (zone)
        {
            case Zone.Bar: BarKeys(keyboard); break;
            case Zone.Panel: PanelKeys(keyboard); break;
            default: ListKeys(keyboard); break;
        }
    }

    // The letter keys of each tab.
    private static bool TabKeys(InputKeyboard keyboard)
    {
        switch (tab)
        {
            case Tab.Browse: return BrowseKeys(keyboard);
            case Tab.Installed: return InstalledKeys(keyboard);
            case Tab.Mine: return MineKeys(keyboard);
            default: return false;
        }
    }

    private static void ListKeys(InputKeyboard keyboard)
    {
        int count = ListCount(), before = ListIndex(), index = before;
        index = MoveInList(keyboard, index, count);
        if (Pressed(keyboard, Key.Home)) index = 0;
        if (Pressed(keyboard, Key.End) && count > 0) index = count - 1;
        int step = PadInput.Move();
        bool up = Pressed(keyboard, Key.UpArrow) || step < 0;
        if (step != 0 && count > 0) index = Math.Clamp(index + step, 0, count - 1);
        // Up from the top row goes to the search and filters above the list.
        if (up && before == 0 && tab == Tab.Browse)
        {
            zone = Zone.Bar;
            return;
        }
        SetListIndex(index);
        if ((Pressed(keyboard, Key.RightArrow) || PadInput.Side() > 0) && panelActions.Count > 0)
        {
            zone = Zone.Panel;
            panelFocus = 0;
            return;
        }
        if (Pressed(keyboard, Key.Enter) || Pressed(keyboard, Key.NumpadEnter) || PadInput.Pressed(PadButton.South)) Primary();
    }

    private static void BarKeys(InputKeyboard keyboard)
    {
        var shown = barControls.Where(c => c.Visible).ToList();
        if (shown.Count == 0)
        {
            zone = Zone.List;
            return;
        }
        if (Pressed(keyboard, Key.LeftArrow) || PadInput.Side() < 0) barFocus--;
        if (Pressed(keyboard, Key.RightArrow) || PadInput.Side() > 0) barFocus++;
        barFocus = Math.Clamp(barFocus, 0, shown.Count - 1);
        if (Pressed(keyboard, Key.DownArrow) || PadInput.Move() > 0)
        {
            zone = Zone.List;
            return;
        }
        if (Pressed(keyboard, Key.Enter) || Pressed(keyboard, Key.NumpadEnter) || PadInput.Pressed(PadButton.South)) shown[barFocus].Activate();
    }

    private static void PanelKeys(InputKeyboard keyboard)
    {
        int count = panelActions.Count;
        if (count == 0) zone = Zone.List;
        if (Pressed(keyboard, Key.LeftArrow) || PadInput.Side() < 0)
        {
            // From the right column to the left one, and from the left one back to the list.
            if (panelFocus % 2 == 1) panelFocus--;
            else zone = Zone.List;
        }
        if (zone != Zone.Panel) return;
        // The buttons are in two columns: Up and Down move a row, Right the column.
        if (Pressed(keyboard, Key.UpArrow)) panelFocus -= 2;
        if (Pressed(keyboard, Key.DownArrow)) panelFocus += 2;
        if (Pressed(keyboard, Key.RightArrow) || PadInput.Side() > 0) panelFocus++;
        panelFocus += PadInput.Move();
        panelFocus = Math.Clamp(panelFocus, 0, count - 1);
        if (Pressed(keyboard, Key.Enter) || Pressed(keyboard, Key.NumpadEnter) || PadInput.Pressed(PadButton.South)) RunAction(panelFocus);
    }

    private static void DrawMain(InputMouse? clicks, InputMouse? mouse)
    {
        bool browsing = tab == Tab.Browse;
        if (filterBar!.gameObject.activeSelf != browsing) filterBar.gameObject.SetActive(browsing);
        if (subLine!.gameObject.activeSelf == browsing) subLine.gameObject.SetActive(!browsing);
        if (!browsing)
        {
            string line = SubLineText();
            if (subLine.text != line) subLine.text = line;
        }
        int count = store == null ? 0 : ListCount();
        SetListIndex(ListIndex());
        var (clicked, twice) = list!.Draw(count, ListIndex(), RowAt, clicks, mouse);
        if (clicked >= 0)
        {
            zone = Zone.List;
            SetListIndex(clicked);
            if (twice) Primary();
            if (!IsOpen || screen != Screen.Main) return;
        }
        if (browsing) AfterBrowseDrawn();
        else if (tab == Tab.Installed) AfterInstalledDrawn();
        string message = count > 0 ? "" : ListMessage();
        if (listMessage!.text != message) listMessage.text = message;
        string foot = browsing && count > 0 ? BrowseFoot() : "";
        if (listFoot!.text != foot)
        {
            listFoot.text = foot;
            listFoot.color = browseLoading && browseProblem == null && link != Link.Down ? DimText : Hex(0xF2B02E);
        }
        DrawPanel();
        Control? focused = null;
        if (Kit.Typing == null)
        {
            if (zone == Zone.Panel && panelFocus < panelButtons.Count) focused = panelButtons[panelFocus];
            else if (zone == Zone.Bar)
            {
                var shown = barControls.Where(c => c.Visible).ToList();
                if (shown.Count > 0) focused = shown[Math.Clamp(barFocus, 0, shown.Count - 1)];
            }
        }
        Kit.DrawFocus(focused);
        DrawBottom();
        Ui.DrawStatus(StatusText(), 0, PanelW + Edge, BottomH);
    }

    private static string StatusText()
    {
        if (Ui.MessageShowing) return Ui.Message;
        if (working != null) return working + (workCts != null ? $"  {(PadNames ? "B" : "Esc")} stops." : "");
        return "";
    }

    private static string SubLineText() => tab switch
    {
        Tab.Installed => "What the hub installed on this PC. Battles are in the arcade's custom battles; difficulties are in Options > Custom Charts.",
        Tab.Mine => identity?.UploaderId != null
            ? $"Your uploads, with this PC's hub key ({(identity.Name.Length > 0 ? identity.Name : "no name yet")}). Only this key can change or delete them."
            : "Your uploads are tied to this PC's hub key, which is made at your first upload.",
        Tab.Upload => "Share what you made. Uploads go live at once; the hub's owner can remove anything, and players can report it.",
        _ => "",
    };

    private static string ListMessage()
    {
        if (store == null) return link == Link.Down ? linkProblem : "Opening...";
        switch (tab)
        {
            case Tab.Browse:
                if (link == Link.Opening || link == Link.Connecting) return "Connecting to the hub...";
                if (link == Link.Down) return linkProblem + "\n\nInstalled and deleting still work.";
                if (link == Link.TooOld) return HubErrorsText.TooOld + "\n\nInstalled and deleting still work.";
                if (browseProblem != null) return BrowseProblemText(empty: true);
                if (browse == null || browseLoading || !browse.Started) return "Loading...";
                return browse.Query.Searching || byUploader != null || kindFilter != null || lanesFilter != 0
                    ? "Nothing on the hub matches. Try another search or filter."
                    : "Nothing is on the hub yet. Be the first: the Upload tab shares a battle you made.";
            case Tab.Installed:
                return "Nothing from the hub is installed yet. Download something on the Browse tab.";
            case Tab.Mine:
                return MineMessage();
            default:
                return "";
        }
    }

    // ---- the detail panel --------------------------------------------------------------------------

    /// <summary>What the panel shows, filled in by the tab each frame.</summary>
    private sealed class PanelView
    {
        internal Texture? Picture;
        internal bool NoPicture;
        internal string Tile = "";
        internal Color TileColor = ButtonHover;
        // Title, the Line fields, Table, Body and Note are shown as they are (rich text off); Badge
        // and Facts are the mod's own words.
        internal string Title = "", Line1 = "", Line2 = "", Line3 = "", Badge = "", Facts = "", Table = "", Body = "", Note = "";
    }

    private static void DrawPanel()
    {
        var v = new PanelView();
        if (store != null)
        {
            switch (tab)
            {
                case Tab.Browse: BrowsePanel(v); break;
                case Tab.Installed: InstalledPanel(v); break;
                case Tab.Mine: MinePanel(v); break;
                default: UploadPanel(v); break;
            }
        }
        bool picture = !v.NoPicture && v.Picture != null, tile = !v.NoPicture && v.Picture == null;
        if (panelPicture!.gameObject.activeSelf != picture) panelPicture.gameObject.SetActive(picture);
        if (picture && panelPicture.texture != v.Picture)
        {
            panelPicture.texture = v.Picture;
            FitPicture(panelPicture, v.Picture!, 20, -20, PictureSide);
        }
        if (!picture && panelPicture.texture != null) panelPicture.texture = null;
        if (panelTile!.gameObject.activeSelf != tile) panelTile.gameObject.SetActive(tile);
        if (tile)
        {
            if (panelTile.color != v.TileColor) panelTile.color = v.TileColor;
            SetText(panelTileText!, v.Tile);
        }
        PanelLayout(v.NoPicture, v.Facts.Length > 0, v.Table.Length > 0);
        SetText(panelTitle!, v.Title);
        SetText(panelLine1!, v.Line1);
        SetText(panelLine2!, v.Line2);
        SetText(panelLine3!, v.Line3);
        SetText(panelBadge!, v.Badge);
        SetText(panelFacts!, v.Facts);
        SetText(panelTable!, v.Table);
        SetText(panelBody!, v.Body);
        SetText(panelNote!, v.Note);
    }

    private static void SetText(TMP_Text t, string text)
    {
        if (t.text != text) t.text = text;
    }

    private static string panelLayout = "";

    // Where the panel's texts go: beside the picture, or (without one) from the top, with the
    // body moving up past the parts that are empty.
    private static void PanelLayout(bool noPicture, bool facts, bool table)
    {
        string key = $"{noPicture}{facts}{table}";
        if (key == panelLayout) return;
        panelLayout = key;
        float x = noPicture ? 20 : 20 + PictureSide + 16;
        foreach (var t in new[] { panelTitle!, panelLine1!, panelLine2!, panelLine3!, panelBadge! })
            PlaceTop(t.rectTransform, x, t.rectTransform.anchoredPosition.y, PanelW - x - 20, t.rectTransform.sizeDelta.y);
        float y = noPicture ? -92 : -210;
        PlaceTop(panelFacts!.rectTransform, 20, y, PanelW - 40, 98);
        if (facts || !noPicture) y -= 102;
        PlaceTop(panelTable!.rectTransform, 20, y, PanelW - 40, 126);
        if (table || !noPicture) y -= 134;
        panelBody!.rectTransform.offsetMax = new Vector2(-20, y);
    }

    // ---- the bottom bar -------------------------------------------------------------------------------

    private static void DrawBottom()
    {
        var transfer = TransferLine(out string line);
        bool shown = transfer != null;
        if (bottomProgress!.Rect.gameObject.activeSelf != shown) bottomProgress.Rect.gameObject.SetActive(shown);
        if (shown) bottomProgress.Set(transfer!.Fraction, line);
        if (sending != null) sendBar?.Set(sending.Transfer.Fraction, line);
        string hints = Hints();
        if (hintsText!.text != hints) hintsText.text = hints;
    }

    // The download's or upload's progress, as the bar says it.
    private static HubTransfer? TransferLine(out string line)
    {
        line = "";
        if (sending != null)
        {
            var t = sending.Transfer;
            line = t.Stage switch
            {
                "Uploading" => $"Uploading {sending.Title}... {(int)(t.Fraction * 100)}% ({Size(t.Done)} of {Size(t.Total)})",
                "Finishing" => $"Finishing the upload of {sending.Title}...",
                _ => $"Starting the upload of {sending.Title}...",
            };
            return t;
        }
        if (download != null)
        {
            var t = download.Transfer;
            string stop = PadNames ? "B stops" : "Esc stops";
            line = t.Stage switch
            {
                "Downloading" => $"Downloading {download.Title}... {(int)(t.Fraction * 100)}% ({Size(t.Done)} of {Size(t.Total)})   {stop}",
                "Checking" => $"Checking {download.Title}...   {stop}",
                "Installing" => $"Installing {download.Title}...",
                _ => $"Getting {download.Title} ready...   {stop}",
            };
            return t;
        }
        return null;
    }

    private static string Hints()
    {
        string K(string key, string what) => $"<color=#EAE6F5>{key}</color> {what}";
        var keys = new List<string>();
        bool pad = PadNames;
        if (screen == Screen.Form)
        {
            keys.Add(pad ? K("A", "choose") : K("Up/Down, Enter", "choose"));
            keys.Add(pad ? K("B", "back") : K("Esc", "back"));
            return string.Join("    ", keys);
        }
        if (Kit.Typing != null) return pad ? K("A", "keeps it") + "    " + K("B", "goes back") : K("Enter", "keeps it") + "    " + K("Esc", "goes back");
        if (pad)
        {
            keys.Add(K("A", "choose"));
            keys.Add(K("Right", "buttons"));
            keys.Add(K("LB/RB", "tabs"));
            keys.Add(K("B", zone == Zone.List ? "close" : "back"));
            return string.Join("    ", keys);
        }
        switch (tab)
        {
            case Tab.Browse:
                keys.Add(K("Enter", "download"));
                keys.Add(K("R", "report"));
                keys.Add(K("Del", "delete"));
                keys.Add(K("Ctrl+F", "search"));
                keys.Add(K("T L S", "type, lanes, sort"));
                keys.Add(K("M", "more by the uploader"));
                break;
            case Tab.Installed:
                keys.Add(K("Enter", "update or use it now"));
                keys.Add(K("Del", "delete"));
                keys.Add(K("R", "report"));
                break;
            case Tab.Mine:
                keys.Add(K("Enter", "new version"));
                keys.Add(K("Del", "delete from the hub"));
                break;
            default:
                keys.Add(K("Enter", "choose"));
                break;
        }
        keys.Add(K("Right", "buttons"));
        keys.Add(K("Tab", "next tab"));
        keys.Add(K("F5", "refresh"));
        keys.Add(K("Esc", zone == Zone.List ? "close" : "back"));
        return string.Join("    ", keys);
    }

    // ---- forms: the notice, a report, the upload's steps ------------------------------------------------

    private enum FormKind { Notice, Report, Details, Summary, Rules, Sending, Description }

    private sealed class Form
    {
        internal RectTransform Panel = null!;
        internal readonly List<Control> Controls = new();
        internal Action Back = () => { };
        internal int Focus;
        /// <summary>Which of the buttons the keyboard starts on (the Rules start on Back).</summary>
        internal Func<int>? StartFocus;
    }

    private static readonly Dictionary<FormKind, Form> forms = new();
    private static FormKind formKind;
    private const float FormW = 1240f;

    private static Form NewForm(FormKind kind, Func<string> header)
    {
        var panel = MakeImage(kind.ToString(), formsPanel!, PanelColor).rectTransform;
        panel.anchorMin = new Vector2(0.5f, 0);
        panel.anchorMax = new Vector2(0.5f, 1);
        panel.pivot = new Vector2(0.5f, 0.5f);
        panel.offsetMin = new Vector2(-FormW / 2, BottomH + 16);
        panel.offsetMax = new Vector2(FormW / 2, -(TopH + 16));
        var title = MakeText("Header", panel, 32, TextAlignmentOptions.TopLeft);
        title.enableWordWrapping = false;
        title.overflowMode = TextOverflowModes.Ellipsis;
        PlaceTop(title.rectTransform, 40, -28, FormW - 80, 44);
        Ui.AddLiveText(title, header);
        panel.gameObject.SetActive(false);
        var form = new Form { Panel = panel };
        forms[kind] = form;
        return form;
    }

    /// <summary>Text on a form shown as it is (it can hold text from the hub or the player's files).</summary>
    private static TMP_Text FormPlain(Form form, float x, ref float y, float w, float h, Func<string> text, float size = 20, Func<bool>? shown = null)
    {
        var t = MakePlainText("Text", form.Panel, size, TextAlignmentOptions.TopLeft);
        t.enableWordWrapping = true;
        t.overflowMode = TextOverflowModes.Ellipsis;
        PlaceTop(t.rectTransform, x, y, w, h);
        var live = Ui.AddLiveText(t, text);
        if (shown != null) live.Visible = shown;
        y -= h + 8;
        return t;
    }

    private static void ShowForm(FormKind kind)
    {
        if (!forms.TryGetValue(kind, out var form)) return;
        formKind = kind;
        ShowScreen(Screen.Form);
        foreach (var (k, f) in forms) f.Panel.gameObject.SetActive(k == kind);
        form.Focus = form.StartFocus?.Invoke() ?? 0;
    }

    private static void UpdateForm(InputKeyboard keyboard, InputMouse? mouse)
    {
        var kind = formKind;
        var form = forms[kind];
        var clicks = Kit.ClicksLive && !Blocked ? mouse : null;
        if (Kit.Typing != null && clicks != null && clicks.leftButton.wasPressedThisFrame && !Kit.CommitTyping()) clicks = null;
        Ui.UpdateButtons(clicks);
        if (!IsOpen || screen != Screen.Form || formKind != kind) return;
        if (Kit.Live)
        {
            bool back = Pressed(keyboard, Key.Escape) || PadInput.Pressed(PadButton.East);
            if (Blocked)
            {
                if (back) StopWork();
            }
            else if (Kit.Typing != null) Kit.UpdateTyping(keyboard);
            else if (back)
            {
                form.Back();
                return;
            }
            else
            {
                var shown = form.Controls.Where(c => c.Visible).ToList();
                int step = PadInput.Move();
                if (step != 0 && shown.Count > 0) form.Focus = Math.Clamp(form.Focus + step, 0, shown.Count - 1);
                WalkControls(keyboard, shown, ref form.Focus);
                if (!IsOpen || screen != Screen.Form || formKind != kind) return;
                if (PadInput.Pressed(PadButton.South) && form.Focus >= 0 && form.Focus < shown.Count) shown[form.Focus].Activate();
                if (!IsOpen || screen != Screen.Form || formKind != kind) return;
            }
        }
        var visible = form.Controls.Where(c => c.Visible).ToList();
        form.Focus = Math.Clamp(form.Focus, visible.Count == 0 ? -1 : 0, visible.Count - 1);
        Kit.DrawFocus(form.Focus >= 0 && Kit.Typing == null && !Blocked ? visible[form.Focus] : null);
        DrawBottom();
        Ui.DrawStatus(StatusText(), 0, 0, BottomH);
    }

    private static void BuildForms()
    {
        BuildNoticeForm();
        BuildReportForm();
        BuildUploadForms();
        BuildDescriptionForm();
    }

    // ---- a whole description (the panel shows its first lines) ---------------------------------------

    private static string readTitle = "", readText = "";

    private static void BuildDescriptionForm()
    {
        var form = NewForm(FormKind.Description, () => "Description");
        float y = -96;
        // The entry's title and description are the hub's text: shown plain.
        FormPlain(form, 40, ref y, FormW - 80, 40, () => readTitle, 26);
        FormPlain(form, 40, ref y, FormW - 80, 560, () => readText, 20);
        Kit.AddButton(form.Panel, form.Controls, 40, y, 200, 52, "Back", BackToMain);
        form.Back = BackToMain;
    }

    /// <summary>Whether a description may be longer than the panel shows (it fits about 6 lines of 60 to 70 letters), so it gets a button.</summary>
    private static bool LongDescription(string text) =>
        text.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / 60.0))) > 5;

    private static PanelAction ReadDescription(string title, string text) => new()
    {
        Text = "Read the description",
        Do = () =>
        {
            readTitle = title;
            readText = text;
            ShowForm(FormKind.Description);
        },
    };

    // ---- the one-time notice (DESIGN-HUB 5.2) -----------------------------------------------------------

    private const string NoticeText =
        "The hub is a small server run for this mod. Browsing and downloading send requests to it; nothing about you is stored.\n\n" +
        "Downloads are checked before they're installed, go into the game's own folders, and are never run as programs.\n\n" +
        "Content is made by players. Press R on anything that breaks the rules.\n\n" +
        "On busy days the hub can be unavailable until midnight UTC.\n\n" +
        "The Online hub row in Options > Custom Charts turns the hub off, and then the mod never contacts it.";

    private static void BuildNoticeForm()
    {
        var form = NewForm(FormKind.Notice, () => "Before you start");
        float y = -100;
        FormPlain(form, 40, ref y, FormW - 80, 420, () => NoticeText, 23);
        Kit.AddButton(form.Panel, form.Controls, 40, y - 10, 300, 52, "OK, go to the hub", NoticeSeen);
        Kit.AddButton(form.Panel, form.Controls, 352, y - 10, 200, 52, "Not now", () => Close("the notice was closed"));
        // Esc leaves without contacting the hub; the notice shows again next time.
        form.Back = () => Close("the notice was closed");
    }

    private static void NoticeSeen()
    {
        if (store == null) return;
        store.Settings.NoticeSeen = true;
        SaveSettings();
        ShowScreen(Screen.Main);
        Connect();
    }
}
