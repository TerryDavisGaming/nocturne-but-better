using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using static NocturneFlatScroll.EditorUi;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

// The battle's pages, laid out like the chart editor: a top bar with the battle's name, the pages
// down the left, the page itself in the middle, and the file buttons along the bottom. Every
// button is clickable; the keyboard moves a marker through the page's buttons and then the
// bottom bar's (Up/Down, Enter).
internal static partial class BattleCreator
{
    private enum Page { Info, Song, Charts, Enemy, Art, Gear, Dialogue }

    private const float TopH = 64f, BottomH = 92f, LeftW = 300f;
    private const float RowH = 44f, RowStep = 52f;
    // Two columns on the pages: the first from 0, the second from Col2.
    private const float Col1W = 820f, Col2 = 880f, Col2W = 640f;

    /// <summary>A button that the keyboard can reach.</summary>
    private sealed class Control
    {
        internal UiButton Button = null!;
        internal Func<bool>? Shown;
        internal Action Activate = null!;
        internal bool Visible => Shown?.Invoke() ?? true;
    }

    private static Page page = Page.Info;
    private static int focus = -1;
    private static RectTransform? editPanel, pagesArea;
    private static TMP_Text? titleText;
    private static Image? focusMarker, cardImage;
    // The button the marker sits on (it is moved into that button, just left of it).
    private static UiButton? markerOn;
    private static readonly Dictionary<Page, RectTransform> pagePanels = new();
    private static readonly Dictionary<Page, List<Control>> controls = new();
    // The bottom bar's buttons, after each page's own in the keyboard's order.
    private static readonly List<Control> barControls = new();

    private static readonly (Page Page, string Name)[] Pages =
    {
        (Page.Info, "Info"), (Page.Song, "Song"), (Page.Charts, "Charts"), (Page.Enemy, "Enemy"), (Page.Art, "Art"), (Page.Gear, "Gear & level"), (Page.Dialogue, "Dialogue"),
    };

    // ---- building --------------------------------------------------------------------------------

    private static void BuildEdit()
    {
        pagePanels.Clear();
        controls.Clear();
        barControls.Clear();
        markerOn = null;
        editPanel = MakeRect("Edit", Ui.CanvasRect);
        Stretch(editPanel, 0, 0, 0, 0);

        pagesArea = MakeRect("Pages", editPanel);
        Stretch(pagesArea, LeftW + 40, BottomH + 16, 40, TopH + 24);
        foreach (var (p, name) in Pages)
        {
            var panel = MakeRect(name, pagesArea);
            Stretch(panel, 0, 0, 0, 0);
            pagePanels[p] = panel;
            controls[p] = new List<Control>();
        }
        BuildInfoPage();
        BuildSongPage();
        BuildChartsPage();
        BuildEnemyPage();
        BuildArtPage();
        BuildGearPage();
        BuildDialoguePage();
        // The keyboard's marker: a bar left of the button it's on (see DrawEdit).
        focusMarker = MakeImage("Focus", pagesArea, Accent);
        focusMarker.gameObject.SetActive(false);

        BuildTopBar();
        BuildLeftPanel();
        BuildBottomBar();
        // Messages go last, so they draw over the page.
        Ui.BuildStatus(editPanel);
        editPanel.gameObject.SetActive(false);
    }

    private static void BuildTopBar()
    {
        var top = MakeImage("TopBar", editPanel!, PanelColor).rectTransform;
        top.anchorMin = new Vector2(0, 1);
        top.anchorMax = new Vector2(1, 1);
        top.pivot = new Vector2(0.5f, 1);
        top.sizeDelta = new Vector2(0, TopH);
        top.anchoredPosition = Vector2.zero;
        titleText = MakeText("Title", top, 22, TextAlignmentOptions.Left);
        titleText.enableWordWrapping = false;
        titleText.overflowMode = TextOverflowModes.Ellipsis;
        Stretch(titleText.rectTransform, 24, 0, 24, 0);
    }

    private static void BuildLeftPanel()
    {
        var left = MakeImage("PagesList", editPanel!, PanelColor).rectTransform;
        left.anchorMin = new Vector2(0, 0);
        left.anchorMax = new Vector2(0, 1);
        left.pivot = new Vector2(0, 0.5f);
        left.offsetMin = new Vector2(0, BottomH);
        left.offsetMax = new Vector2(LeftW, -TopH);
        float y = -14;
        Ui.Header(left, "Pages", ref y);
        foreach (var (p, name) in Pages)
        {
            var b = Ui.MakeButton(left, name, () => SetPage(p));
            b.Active = () => page == p;
            b.Label.alignment = TextAlignmentOptions.Left;
            b.Label.margin = new Vector4(16, 0, 8, 0);
            PlaceTop(b.Rect, 16, y, 268, 44);
            y -= 50;
        }
        y -= 10;
        Ui.Header(left, "Battle", ref y);
        var about = MakeText("About", left, 16, TextAlignmentOptions.TopLeft);
        about.color = DimText;
        PlaceTop(about.rectTransform, 18, y, 264, 300);
        Ui.AddLiveText(about, AboutText);
        var keys = MakeText("Keys", left, 15, TextAlignmentOptions.BottomLeft);
        keys.color = DimText;
        keys.text = "Tab: next page\nUp/Down, Enter: any button\nCtrl+S: save\nEsc: back";
        keys.rectTransform.anchorMin = new Vector2(0, 0);
        keys.rectTransform.anchorMax = new Vector2(1, 0);
        keys.rectTransform.pivot = new Vector2(0, 0);
        keys.rectTransform.offsetMin = new Vector2(18, 14);
        keys.rectTransform.offsetMax = new Vector2(-18, 110);
    }

    private static void BuildBottomBar()
    {
        var bar = MakeImage("BottomBar", editPanel!, PanelColor).rectTransform;
        bar.anchorMin = new Vector2(0, 0);
        bar.anchorMax = new Vector2(1, 0);
        bar.pivot = new Vector2(0.5f, 0);
        bar.sizeDelta = new Vector2(0, BottomH);
        bar.anchoredPosition = Vector2.zero;
        var buttons = new (string Label, Action Do, float Width)[]
        {
            ("Save", () => Save(), 170), ("Export .nbbbattle...", StartExport, 260), ("Open folder", () => { if (draft != null) OpenInExplorer(draft.Folder); }, 190),
            ("Delete battle", AskDelete, 200),
        };
        float x = 16;
        foreach (var (label, doIt, width) in buttons)
        {
            Action act = () => { if (!Busy) doIt(); };
            var b = Ui.MakeButton(bar, label, act);
            if (label == "Save")
            {
                b.Text = () => "Save <size=65%><color=#9D92B4>Ctrl+S</color></size>";
                b.Active = () => draft?.Dirty ?? false;
            }
            Place(b.Rect, new Vector2(0, 0.5f), new Vector2(x, 0), new Vector2(width, 52), new Vector2(0, 0.5f));
            barControls.Add(new Control { Button = b, Activate = act });
            x += width + 12;
        }
        Action goBack = () => { if (!Busy) RequestBack(); };
        var back = Ui.MakeButton(bar, "Back", goBack);
        back.Text = () => "Back <size=65%><color=#9D92B4>Esc</color></size>";
        Place(back.Rect, new Vector2(1, 0.5f), new Vector2(-16, 0), new Vector2(170, 52), new Vector2(1, 0.5f));
        barControls.Add(new Control { Button = back, Activate = goBack });
    }

    // ---- page building blocks ----------------------------------------------------------------------

    private static UiButton AddButton(Page p, float x, float y, float w, float h, string text, Action click, Func<bool>? shown = null)
    {
        var b = Ui.MakeButton(pagePanels[p], text, () => { if (!Busy) click(); });
        PlaceTop(b.Rect, x, y, w, h);
        if (shown != null) b.Visible = shown;
        controls[p].Add(new Control { Button = b, Shown = shown, Activate = click });
        return b;
    }

    /// <summary>A row showing a label and a value; clicking it types a new value.</summary>
    private static UiButton AddField(Page p, float x, ref float y, float w, TextField field, Func<bool>? shown = null, float h = RowH)
    {
        var b = AddButton(p, x, y, w, h, "", () => StartTyping(field), shown);
        b.Text = () => FieldText(field, b);
        b.Active = () => typing == field;
        b.Label.alignment = field.MultiLine ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.Left;
        b.Label.margin = new Vector4(16, field.MultiLine ? 10 : 0, 12, field.MultiLine ? 8 : 0);
        b.Label.fontSize = 19;
        if (field.MultiLine)
        {
            // A long text ends in "..." when it doesn't fit; while it's typed, its end shows instead (FieldText).
            b.Label.enableWordWrapping = true;
            b.Label.overflowMode = TextOverflowModes.Ellipsis;
        }
        y -= h + (RowStep - RowH);
        return b;
    }

    /// <summary>A row with a label and a value that picks from a list when clicked.</summary>
    private static UiButton AddChoice(Page p, float x, ref float y, float w, string label, Func<string> value, Action click, Func<bool>? shown = null)
    {
        var b = AddButton(p, x, y, w, RowH, "", click, shown);
        b.Text = () => $"<color=#9D92B4>{label}</color><pos=32%>{value()}";
        b.Label.alignment = TextAlignmentOptions.Left;
        b.Label.margin = new Vector4(16, 0, 12, 0);
        b.Label.fontSize = 19;
        y -= RowStep;
        return b;
    }

    /// <summary>A button that shows a setting and flips it; lit while it's on.</summary>
    private static UiButton AddToggle(Page p, float x, ref float y, float w, Func<string> text, Func<bool> on, Action flip, Func<bool>? shown = null)
    {
        var b = AddButton(p, x, y, w, RowH, "", flip, shown);
        b.Text = text;
        b.Active = on;
        b.Label.fontSize = 19;
        y -= RowStep;
        return b;
    }

    /// <summary>"-" and "+" around a value.</summary>
    private static void AddStepper(Page p, float x, ref float y, float w, Func<string> value, Action less, Action more, Func<bool>? shown = null)
    {
        AddButton(p, x, y, 56, RowH, "-", less, shown);
        var label = MakeText("Value", pagePanels[p], 19, TextAlignmentOptions.Center);
        PlaceTop(label.rectTransform, x + 60, y, w - 120, RowH);
        var live = Ui.AddLiveText(label, value);
        if (shown != null) live.Visible = shown;
        AddButton(p, x + w - 56, y, 56, RowH, "+", more, shown);
        y -= RowStep;
    }

    private static void AddHeader(Page p, float x, ref float y, float w, string text, Func<bool>? shown = null)
    {
        var t = MakeText(text, pagePanels[p], 15, TextAlignmentOptions.Left);
        t.text = text.ToUpperInvariant();
        t.color = DimText;
        PlaceTop(t.rectTransform, x + 2, y, w, 20);
        if (shown != null) Ui.AddLiveText(t, () => text.ToUpperInvariant()).Visible = shown;
        y -= 28;
    }

    /// <summary>Text worked out each frame (rich text allowed; escape anything from files).</summary>
    private static TMP_Text AddText(Page p, float x, ref float y, float w, float h, Func<string> text, float size = 18, Func<bool>? shown = null)
    {
        var t = MakeText("Text", pagePanels[p], size, TextAlignmentOptions.TopLeft);
        t.color = DimText;
        PlaceTop(t.rectTransform, x + 2, y, w, h);
        var live = Ui.AddLiveText(t, text);
        if (shown != null) live.Visible = shown;
        y -= h + 8;
        return t;
    }

    // What is being typed, as last fitted to its row: it is measured again only when it changes.
    private static TextField? fittedField;
    private static string fittedText = "", fittedShown = "";

    private static string FieldText(TextField field, UiButton button)
    {
        // While typing the row is lit in the accent colour, where the dim label wouldn't read.
        string label = typing == field ? field.Label : $"<color=#9D92B4>{field.Label}</color>";
        string Row(string shown) => field.MultiLine ? $"{label}\n{shown}" : $"{label}<pos=32%>{shown}";
        if (typing == field)
        {
            // The end of the text, where the "_" cursor is, always shows: what doesn't fit is cut from the start.
            if (fittedField != field || fittedText != typed)
            {
                fittedField = field;
                fittedText = typed;
                fittedShown = TextTail.Fit(typed, shown => Fits(button, field.MultiLine, Row(Escape(shown) + "_"), Escape(shown) + "_"));
            }
            return Row(Escape(fittedShown) + "_");
        }
        string value = field.Get();
        return Row(value.Trim().Length == 0 ? $"<color=#9D92B4>{Escape(field.Empty?.Invoke() ?? "(click to set)")}</color>" : Escape(value));
    }

    /// <summary>Whether a field's row text fits in its button, measured the way TextMeshPro lays it out.</summary>
    private static bool Fits(UiButton button, bool multiLine, string row, string value)
    {
        var text = button.Label;
        var margin = text.margin;
        var size = button.Rect.rect.size;
        float width = size.x - margin.x - margin.z, height = size.y - margin.y - margin.w;
        if (width <= 0 || height <= 0) return true;
        if (multiLine) return text.GetPreferredValues(row, width, 0).y <= height;
        // One line: the value starts at 32% of the width (the <pos=32%> in the row).
        return text.GetPreferredValues(value).x <= width * 0.68f - 4;
    }

    // ---- the pages ------------------------------------------------------------------------------------

    private static void BuildInfoPage()
    {
        const Page p = Page.Info;
        float y = 0;
        AddHeader(p, 0, ref y, Col1W, "About the battle");
        AddField(p, 0, ref y, Col1W, TitleField);
        AddField(p, 0, ref y, Col1W, ArtistField);
        AddField(p, 0, ref y, Col1W, AuthorField);
        AddField(p, 0, ref y, Col1W, LoreField, h: 300);
        AddText(p, 0, ref y, Col1W, 66, LoreHint, 16);
        AddHeader(p, 0, ref y, Col1W, "Arcade preview");
        float row = y;
        AddField(p, 0, ref y, 560, PreviewField);
        var play = AddButton(p, 576, row, Col1W - 576, RowH, "", TogglePreview);
        play.Text = () => preview != null ? "Stop" : "Play 10 s";
        play.Active = () => preview != null;
        AddText(p, 0, ref y, Col1W, 26, () => "Where the arcade's preview of the song starts, in seconds.", 16);

        float y2 = 0;
        AddHeader(p, Col2, ref y2, Col2W, "Card image");
        var box = MakeImage("CardBox", pagePanels[p], PanelColor).rectTransform;
        PlaceTop(box, Col2, y2, 380, 380);
        cardImage = MakeImage("Card", box, Color.white);
        Stretch(cardImage.rectTransform, 10, 10, 10, 10);
        cardImage.preserveAspect = true;
        cardImage.gameObject.SetActive(false);
        y2 -= 392;
        float buttons = y2;
        AddButton(p, Col2, buttons, 250, RowH, "Choose image...", ChooseCard);
        AddButton(p, Col2 + 262, buttons, 118, RowH, "Remove", RemoveCard, () => draft?.Card != null);
        y2 -= RowStep;
        AddText(p, Col2, ref y2, Col2W, 90, () => Escape(cardState), 17);
    }

    private static void BuildSongPage()
    {
        const Page p = Page.Song;
        float y = 0;
        AddHeader(p, 0, ref y, Col1W, "Song");
        var info = AddText(p, 0, ref y, Col1W, 200, SongText, 20);
        info.color = TextColor;
        float row = y;
        AddButton(p, 0, row, 300, RowH, "Replace the song...", ReplaceSong);
        AddButton(p, 312, row, 240, RowH, "Edit charts", () => EditCharts(-1));
        y -= RowStep;
        AddText(p, 0, ref y, Col1W, 120, () =>
            "The BPM and the offset (where beat 0 is in the song) are set in the chart editor, on its Timing page.\n" +
            "Replacing the song keeps the charts as they are, so check their timing afterwards.", 17);
    }

    private static void BuildChartsPage()
    {
        const Page p = Page.Charts;
        float y = 0;
        AddHeader(p, 0, ref y, Col1W, "Difficulties");
        for (int s = 0; s < ChartText.GameDifficultyLabels.Length; s++)
        {
            int slot = s;
            AddChoice(p, 0, ref y, Col1W, ChartText.GameDifficultyLabels[slot], () => SlotText(slot), () => EditCharts(slot));
        }
        AddText(p, 0, ref y, Col1W, 136, () =>
        {
            int lanes = draft?.Lanes ?? 4;
            string keys = lanes == 5
                ? "5 lanes: the middle lane is played with the Attack key (Space by default). The player's own attacks are off in 5 lanes, " +
                  "so the enemy only takes damage from Player attack events (the chart editor's Events tab)."
                : "4 lanes, played with the lane keys (D F J K by default).";
            return $"{keys} Lanes are set when the battle is made; for {(lanes == 5 ? 4 : 5)} lanes, make a new battle.\n" +
                   "Click a difficulty to chart it. In the arcade, a difficulty without its own chart plays the nearest one.";
        }, 17);
        var problems = AddText(p, 0, ref y, Col1W, 200, ChartProblemsText, 17);
        problems.color = Hex(0xF2B02E);
    }

    private static void BuildEnemyPage()
    {
        const Page p = Page.Enemy;
        float y = 0;
        AddHeader(p, 0, ref y, Col1W, "Enemy");
        // How it looks: a game enemy's own art, or the battle's custom art (the Art page).
        float row = y;
        var gameArt = AddButton(p, 0, row, Col1W / 2 - 6, RowH, "Game enemy's art", () => SetArtMode(false));
        gameArt.Active = () => draft != null && !CustomArtEnemy();
        var customArt = AddButton(p, Col1W / 2 + 6, row, Col1W / 2 - 6, RowH, "Custom art", () => SetArtMode(true));
        customArt.Active = CustomArtEnemy;
        y -= RowStep;
        var looks = AddChoice(p, 0, ref y, Col1W, "Looks like", () => Escape(EnemyChoices.NameOf(draft?.Placeholder)), ChooseEnemy);
        // A custom-art enemy only fights like the game enemy.
        looks.Text = () => $"<color=#9D92B4>{(CustomArtEnemy() ? "Fights like" : "Looks like")}</color><pos=32%>{Escape(EnemyChoices.NameOf(draft?.Placeholder))}";
        // Scripted bosses can't take custom art, so custom mode shows its art instead of that toggle.
        float slot = y;
        AddToggle(p, 0, ref y, Col1W, () => $"Advanced bosses: {((draft?.Advanced ?? false) ? "on" : "off")}", () => draft?.Advanced ?? false, ToggleAdvanced,
            () => !CustomArtEnemy());
        AddChoice(p, 0, ref slot, Col1W, "Art", ArtRowSummary, () => SetPage(Page.Art), CustomArtEnemy);
        AddHeader(p, 0, ref y, Col1W, "Stats (blank keeps the enemy's own)");
        foreach (var field in StatFields) AddField(p, 0, ref y, Col1W, field);
        var warning = AddText(p, 0, ref y, Col1W, 60, EnemyWarning, 17);
        warning.color = Hex(0xF2B02E);
        AddText(p, 0, ref y, Col1W, 50, () => "Custom art fights with this enemy's attacks, sounds and stats; the Art page sets how it looks.", 16, CustomArtEnemy);

        float y2 = 0;
        AddHeader(p, Col2, ref y2, Col2W, "Info boxes (top right in the battle)");
        AddToggle(p, Col2, ref y2, Col2W, () => (draft?.OwnInfo ?? false) ? "Info boxes: this battle's own" : "Info boxes: the enemy's own",
            () => draft?.OwnInfo ?? false, ToggleOwnInfo);
        Func<bool> own = () => draft?.OwnInfo ?? false;
        Func<bool> theirs = () => !own();
        // In the room the battle's own boxes use below, while the enemy's own are shown.
        float y3 = y2;
        AddText(p, Col2, ref y3, Col2W, 90, () =>
            $"The battle shows the info boxes of the enemy it {(CustomArtEnemy() ? "fights" : "looks")} like ({Escape(EnemyChoices.NameOf(draft?.Placeholder))}). " +
            "Choose this battle's own to write up to 3 boxes, with an enemy name as their title.", 16, theirs);
        AddField(p, Col2, ref y2, Col2W, EnemyNameField, own);
        AddText(p, Col2, ref y2, Col2W, 66, () =>
            $"A box shows only when it has a text, and at most {BattleDraft.InfoMaxLines} lines of it. A box without a title shows the enemy name. " +
            "With no text in any box, the battle shows no info boxes.", 16, own);
        for (int i = 0; i < EnemyPlaceholders.MaxInfoBoxes; i++)
        {
            AddField(p, Col2, ref y2, Col2W, InfoTitleFields[i], own);
            AddField(p, Col2, ref y2, Col2W, InfoTextFields[i], own, 118);
        }
        var notes = AddText(p, Col2, ref y2, Col2W, 66, () => draft == null ? "" : Escape(string.Join("\n", draft.InfoBoxNotes())), 16, own);
        notes.color = Hex(0xF2B02E);
    }

    private static void BuildGearPage()
    {
        const Page p = Page.Gear;
        float y = 0;
        AddHeader(p, 0, ref y, Col1W, "The player's gear in this battle");
        float row = y;
        var own = AddButton(p, 0, row, Col1W / 2 - 6, RowH, "Player's own gear", () => SetGearMode(false));
        own.Active = () => !(draft?.SetGear ?? false);
        var set = AddButton(p, Col1W / 2 + 6, row, Col1W / 2 - 6, RowH, "Set gear for this battle", () => SetGearMode(true));
        set.Active = () => draft?.SetGear ?? false;
        y -= RowStep + 8;
        Func<bool> setMode = () => draft?.SetGear ?? false;
        AddHeader(p, 0, ref y, Col1W, "Set gear", setMode);
        foreach (var slot in GearSlots)
        {
            var s = slot;
            AddChoice(p, 0, ref y, Col1W, GearCatalog.LabelOf(s), () => GearText(s), () => ChooseGear(s), setMode);
        }
        AddToggle(p, 0, ref y, Col1W, () => $"Show test items: {(showTestItems ? "on" : "off")}", () => showTestItems, () => showTestItems = !showTestItems, setMode);
        // Health upgrades: the player's own, or a number set for this battle (only then the stepper shows).
        AddHeader(p, 0, ref y, Col1W, "Health upgrades", setMode);
        float healthRow = y;
        var ownHealth = AddButton(p, 0, healthRow, Col1W / 2 - 6, RowH, "Player's own", () => SetHealthMode(false), setMode);
        ownHealth.Active = () => draft?.ExtraHealth == null;
        var setHealth = AddButton(p, Col1W / 2 + 6, healthRow, Col1W / 2 - 6, RowH, "Set for this battle", () => SetHealthMode(true), setMode);
        setHealth.Active = () => draft?.ExtraHealth != null;
        y -= RowStep;
        AddStepper(p, 0, ref y, Col1W, () => $"Health upgrades: {draft?.ExtraHealth ?? 0}",
            () => StepExtraHealth(-1), () => StepExtraHealth(1), () => setMode() && draft?.ExtraHealth != null);

        // The level, then how both work and what players will see (worked out in RefreshGear).
        float y2 = 0;
        AddHeader(p, Col2, ref y2, Col2W, "The player's level in this battle");
        float levelRow = y2;
        var ownLevel = AddButton(p, Col2, levelRow, Col2W / 2 - 6, RowH, "Player's own level", () => SetLevelMode(false));
        ownLevel.Active = () => !(draft?.SetLevel ?? false);
        var setLevel = AddButton(p, Col2 + Col2W / 2 + 6, levelRow, Col2W / 2 - 6, RowH, "Set level for this battle", () => SetLevelMode(true));
        setLevel.Active = () => draft?.SetLevel ?? false;
        y2 -= RowStep + 8;
        Func<bool> levelMode = () => draft?.SetLevel ?? false;
        AddStepper(p, Col2, ref y2, Col2W, () => $"Level: {draft?.LevelValue ?? 1}", () => StepLevel(-1), () => StepLevel(1), levelMode);
        AddText(p, Col2, ref y2, Col2W, 26, () => levelGains, 16, levelMode);
        AddHeader(p, Col2, ref y2, Col2W, "How gear and level work");
        AddText(p, Col2, ref y2, Col2W, 300, GearHelpText, 16);
        AddHeader(p, Col2, ref y2, Col2W, "Players see in the arcade");
        AddText(p, Col2, ref y2, Col2W, 210, () => noticePreview, 16);
    }

    private static void BuildDialoguePage()
    {
        const Page p = Page.Dialogue;
        float y = 0;
        AddHeader(p, 0, ref y, Col1W, "Dialogue");
        var t = AddText(p, 0, ref y, Col1W, 80, () => "Boss-style dialogue: coming later.", 22);
        t.color = TextColor;
    }

    // ---- per-frame --------------------------------------------------------------------------------

    private static void SetPage(Page next)
    {
        if (!FinishTyping()) return;
        // The Art page's preview lets go of its textures and videos when the page closes.
        if (page == Page.Art && next != Page.Art) StopArtPreview(false);
        page = next;
        focus = -1;
        foreach (var (p, panel) in pagePanels) panel.gameObject.SetActive(p == page);
        if (page == Page.Charts) RefreshBattleInfo();
        // The Info page's lore hint depends on the gear and level too.
        if (page == Page.Gear || page == Page.Info) RefreshGear();
    }

    /// <summary>The buttons the keyboard can reach now, in order: the page's own that are showing, then the bottom bar's.</summary>
    private static List<Control> VisibleControls()
    {
        var shown = controls.TryGetValue(page, out var list) ? list.Where(c => c.Visible).ToList() : new List<Control>();
        shown.AddRange(barControls);
        return shown;
    }

    private static void DrawEdit()
    {
        if (draft == null) return;
        titleText!.text = $"<color=#9D92B4>Battle creator</color>   {Escape(draft.Title.Length > 0 ? draft.Title : "(no title)")}" +
                          $"{(draft.Dirty ? " <color=#F2B02E>*</color>" : "")}   <size=75%><color=#9D92B4>{Escape(Path.GetFileName(draft.Folder))}</color></size>";
        var shown = VisibleControls();
        if (focus >= shown.Count) focus = shown.Count - 1;
        if (focus >= 0 && typing == null)
        {
            var c = shown[focus];
            if (markerOn != c.Button)
            {
                // Inside the button, just left of it, so it goes wherever the button is (a page or the bottom bar).
                var rect = focusMarker!.rectTransform;
                rect.SetParent(c.Button.Rect, false);
                rect.anchorMin = new Vector2(0, 0);
                rect.anchorMax = new Vector2(0, 1);
                rect.pivot = new Vector2(1, 0.5f);
                rect.sizeDelta = new Vector2(6, 0);
                rect.anchoredPosition = new Vector2(-8, 0);
                markerOn = c.Button;
            }
            if (!focusMarker!.gameObject.activeSelf) focusMarker.gameObject.SetActive(true);
        }
        else if (focusMarker!.gameObject.activeSelf) focusMarker.gameObject.SetActive(false);
        string status = Ui.MessageShowing ? Escape(Ui.Message) : "";
        Ui.DrawStatus(status, LeftW, 0, BottomH);
    }

    private static string AboutText()
    {
        if (draft == null) return "";
        var lines = new List<string> { $"{draft.Lanes} lanes" };
        if (summary != null)
        {
            if (summary.Charted.Count == 0) lines.Add("Not charted yet");
            else lines.Add(string.Join(", ", summary.Charted));
            if (summary.Charted.Count > 0 && summary.Problems.Count == 0) lines.Add("<color=#4FD1A5>Ready for the arcade</color>");
            foreach (var problem in summary.Problems.Take(3)) lines.Add($"<color=#F2B02E>{Escape(problem)}</color>");
        }
        if (draft.Dirty) lines.Add("Unsaved changes");
        return string.Join("\n", lines);
    }

    private static string SongText()
    {
        if (draft == null) return "";
        string audio = draft.EffectiveAudio;
        var lines = new List<string>
        {
            $"<color=#9D92B4>File</color><pos=22%>{Escape(audio.Length > 0 ? audio : "(none)")}" +
            (draft.AudioFromChart && audio.Length > 0 ? "  <color=#9D92B4>(named by the chart's #MUSIC)</color>" : ""),
            $"<color=#9D92B4>Length</color><pos=22%>{Escape(audioState)}",
        };
        if (charts != null && charts.Found)
        {
            double offset = charts.Offset;
            string o = offset.ToString("0.###", CultureInfo.InvariantCulture);
            string where = Math.Abs(offset) < 0.0005 ? "beat 0 is at the start of the song"
                : offset > 0 ? $"beat 0 is {o} s before the song starts"
                : $"beat 0 is {(-offset).ToString("0.###", CultureInfo.InvariantCulture)} s into the song";
            lines.Add($"<color=#9D92B4>Offset</color><pos=22%>{o} s ({where})");
            lines.Add($"<color=#9D92B4>BPM</color><pos=22%>{charts.BpmText}");
        }
        else lines.Add("<color=#9D92B4>Chart</color><pos=22%>can't be read (see the Charts page)");
        lines.Add($"<color=#9D92B4>Lanes</color><pos=22%>{draft.Lanes}");
        return string.Join("\n", lines);
    }

    private static string SlotText(int slot)
    {
        int notes = charts?.Notes[slot] ?? -1;
        return notes < 0 ? "<color=#9D92B4>not charted</color>" : notes == 1 ? "1 note" : $"{notes} notes";
    }

    private static string ChartProblemsText()
    {
        if (charts == null || charts.Problems.Count == 0) return "";
        return string.Join("\n", charts.Problems.Take(6).Select(p => "- " + Escape(p)));
    }

    private static bool CustomArtEnemy() => draft?.CustomArt == true;

    private static string EnemyWarning()
    {
        if (draft == null) return "";
        if (draft.EnemyLocked != null) return Escape(draft.EnemyLocked);
        // Custom art never takes a scripted boss (its Advanced toggle is hidden), whatever that toggle says.
        if (CustomArtEnemy() && EnemyChoices.IsAdvanced(draft.Placeholder)) return Escape(ArtWarning() ?? "");
        string? problem = EnemyChoices.Problem(draft.Placeholder, draft.Advanced);
        if (problem != null) return Escape(problem);
        if (CustomArtEnemy()) return Escape(ArtWarning() ?? "");
        return EnemyChoices.IsAdvanced(draft.Placeholder) ? "Advanced bosses are built around scripted fights and may not play well here." : "";
    }

    // What the battle sets comes first in the arcade's box, so it leaves less room for the lore (loreRoom, from RefreshGear).
    private static string LoreHint() =>
        "The lore shows in the box on the right of the arcade when the battle is selected, under what the battle sets. " +
        (loreRoom >= BattleNotice.BoxLines ? "Keep it short: about 5 lines fit."
            : loreRoom > 0 ? $"What this battle sets takes part of the box, so about {loreRoom} line{(loreRoom == 1 ? "" : "s")} of lore fit (see the Gear & level page)."
            : "What this battle sets fills the box, so the lore doesn't show there (see the Gear & level page).") +
        " Shift+Enter starts a new line.";

    private static string GearHelpText()
    {
        var lines = new List<string>
        {
            "<color=#EAE6F5>Player's own gear:</color> the player fights with what they have on.",
            "<color=#EAE6F5>Set gear:</color> the player gets exactly these items for this battle. Their own gear comes back after it.",
            "<color=#EAE6F5>Player's own level:</color> the player fights at the level they have reached.",
            "<color=#EAE6F5>Set level:</color> the player is this level for this battle only. Their own level comes back after it; nothing is saved and no XP is earned.",
            "Level changes Strength, Regen and Critical. Gear that sets a stat outright (like the Pool Noodle) wins over the level.",
            "The game allows one consumable use per battle. Battles that set gear or level don't count towards achievements.",
        };
        bool gear = draft?.SetGear == true, level = draft?.SetLevel == true;
        if (gear)
        {
            if (!gearListed) lines.Add("<color=#F2B02E>The game's items aren't loaded yet, so they can't be listed now. Try again after loading a save.</color>");
            foreach (var unknown in gearUnknown) lines.Add($"<color=#F2B02E>{unknown}</color>");
        }
        if (level && !(BattleGear.Installed && BattleGear.LevelAvailable))
            lines.Add("<color=#F2B02E>Setting the level isn't available: a game hook couldn't be installed (see the log).</color>");
        if ((gear || level) && !(BattleNoticeArcade.BoxInstalled && BattleNoticeArcade.BadgeInstalled))
            lines.Add("<color=#F2B02E>The arcade can't show what a battle sets: a game hook couldn't be installed (see the log).</color>");
        return string.Join("\n", lines);
    }

    // ---- the card image preview -------------------------------------------------------------------------

    private static Texture2D? cardTexture;
    private static Sprite? cardSprite;
    private static string cardState = "";

    private static void LoadCardPreview()
    {
        ClearCardPreview();
        string? card = draft?.Card;
        if (draft == null || card == null) { cardState = "No card image: the arcade shows a plain card."; return; }
        string? name = PackageFiles.SafeName(card);
        string path = name == null ? "" : Path.Combine(draft.Folder, name.Replace('/', Path.DirectorySeparatorChar));
        if (name == null || !File.Exists(path)) { cardState = $"{card} is missing."; return; }
        try
        {
            var info = new FileInfo(path);
            if (info.Length > BattlePackage.MaxImageBytes) { cardState = $"{card} is too big (at most {BattlePackage.MaxImageBytes / (1024 * 1024)} MB)."; return; }
            // At the image's own size (the preview says it), and refused as the arcade refuses it.
            var texture = CustomBattles.CardImages.Decode(File.ReadAllBytes(path), "NocturneButBetter/creator/card", 0, out _, out string? why);
            if (texture == null) { cardState = $"{card}\nIt {why}, so the arcade shows a plain card."; return; }
            cardTexture = texture;
            cardSprite = CustomBattles.CardImages.ToSprite(texture);
            cardImage!.sprite = cardSprite;
            cardImage.gameObject.SetActive(true);
            cardState = $"{card}\n{texture.width} x {texture.height}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            cardState = $"{card} couldn't be read: {ex.Message}";
        }
    }

    private static void ClearCardPreview()
    {
        if (cardImage)
        {
            cardImage!.sprite = null;
            cardImage.gameObject.SetActive(false);
        }
        if (cardSprite != null && cardSprite) Object.Destroy(cardSprite);
        if (cardTexture != null && cardTexture) Object.Destroy(cardTexture);
        cardSprite = null;
        cardTexture = null;
    }
}
