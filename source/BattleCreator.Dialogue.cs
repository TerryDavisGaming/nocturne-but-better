using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityEngine;
using static NocturneFlatScroll.EditorInput;
using static NocturneFlatScroll.EditorUi;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;

namespace NocturneFlatScroll;

// The Dialogue page: the battle's lines before the fight, during the song, after a win and after
// a loss, and the battle's own speakers. Column 1 has the sections, the shown section's lines (in
// time order during the song) and what's wrong with them; column 2 the preview
// (BattleCreator.DialoguePreview.cs) and the chosen line or speaker. The lines are edited in the
// draft's JSON (BattleDraft), which keeps every change for Undo, and read back through the
// battle's own loader (DialogueReader), so the page shows the problems the arcade would.
internal static partial class BattleCreator
{
    // The page's tabs: the four sections, in DialogueSection's order, then the speakers.
    private enum DialogueTab { Before, During, AfterWin, AfterLoss, Speakers }

    // Who says a line: one of the battle's own speakers, the Narrator, or a game character.
    private enum Who { Custom, Narrator, Game }

    private const int LineRowsVisible = 9, FaceRowsVisible = 3;
    private const float LineRowH = 40, LineRowStep = 44, FaceRowH = 34, FaceRowStep = 38;
    private static readonly char[] BoxRefused = { '<', '>', '{', '}' };
    private const string BoxRefusedText = "The game's text box can't show < > { or }.";

    private static DialogueTab dialogueTab = DialogueTab.Before;
    // The chosen line, by its place in its section as written (-1: none), and the list's first row.
    private static int chosenLine = -1, lineFirst;
    // The rows the lists last scrolled to, so they follow the chosen line and face when those change.
    private static int followedRow = -1, followedFace = -1;
    // The chosen speaker (its key), its chosen expression (-1: the default face) and the first one shown.
    private static string? chosenSpeaker;
    private static int chosenFace = -1, faceFirst;
    // Who the last line went to, for new lines.
    private static string lastSpeaker = DialogueReader.Narrator;
    private static RectTransform? lineListArea, faceListArea;

    // The draft's dialogue as the battle's loader reads it, read again whenever the draft changes,
    // and the lines it keeps in each section (by their place as written).
    private static BattleDialogueData? dialogueRead;
    private static int dialogueReadAt = -1;
    private static BattleDraft? dialogueReadFor;
    private static readonly Dictionary<DialogueSection, HashSet<int>> linesKept = new();
    // The chart as saved: its tempo turns beats into seconds, and the loader checks lines against its notes.
    private static ChartText dialogueChart = new();
    private static ChartText.NoteBlock?[] dialogueSlots = new ChartText.NoteBlock?[BattleChartFile.SlotCount];
    private static EditorChart dialogueTiming = new(4);
    // The list's rows: lines by their place as written, or the speakers' keys.
    private static readonly List<int> lineRows = new();
    private static readonly List<string> speakerRows = new();

    // ---- building -------------------------------------------------------------------------------

    private static void BuildDialoguePage()
    {
        const Page p = Page.Dialogue;
        Func<bool> onLines = () => draft != null && dialogueTab != DialogueTab.Speakers;
        Func<bool> onSpeakers = () => draft != null && dialogueTab == DialogueTab.Speakers;
        Func<bool> lineChosen = () => onLines() && HasChosenLine;
        Func<bool> speakerChosen = () => onSpeakers() && HasChosenSpeaker;

        // Column 1: the sections, what they are, the list and its buttons, and the problems.
        float y = 0;
        AddHeader(p, 0, ref y, Col1W, "Dialogue");
        float w5 = (Col1W - 4 * 8) / 5f;
        for (int i = 0; i < 5; i++)
        {
            var tab = (DialogueTab)i;
            var b = AddButton(p, i * (w5 + 8), y, w5, RowH, "", () => SetDialogueTab(tab));
            b.Text = () => TabText(tab);
            b.Active = () => dialogueTab == tab;
            b.Label.fontSize = 18;
        }
        y -= RowStep;
        AddText(p, 0, ref y, Col1W, 46, DialogueHint, 16);
        lineListArea = MakeRect("Lines", pagePanels[p]);
        PlaceTop(lineListArea, 0, y, Col1W, LineRowsVisible * LineRowStep);
        for (int r = 0; r < LineRowsVisible; r++)
        {
            int row = r;
            var b = AddButton(p, 0, y - r * LineRowStep, Col1W, LineRowH, "", () => ChooseRow(row), () => draft != null && lineFirst + row < RowCount);
            b.Text = () => RowText(row);
            b.Active = () => RowChosen(row);
            b.Label.alignment = TextAlignmentOptions.Left;
            b.Label.margin = new Vector4(14, 0, 10, 0);
            b.Label.fontSize = 17;
        }
        float emptyY = y;
        var empty = AddText(p, 0, ref emptyY, Col1W, 60, () => onLines() ? "No lines yet. Click Add line." : "No speakers of your own yet. Game characters don't need adding: pick them for a line.",
            18, () => draft != null && RowCount == 0);
        empty.color = TextColor;
        y -= LineRowsVisible * LineRowStep;

        float w7 = (Col1W - 6 * 8) / 7f;
        float Slot(int i) => i * (w7 + 8);
        AddButton(p, Slot(0), y, w7, RowH, "Add line", AddDialogueLine, onLines);
        AddButton(p, Slot(1), y, w7, RowH, "Reply", ReplyToLine, lineChosen);
        AddButton(p, Slot(2), y, w7, RowH, "Copy", CopyDialogueLine, lineChosen);
        // 18, so "Move down" fits in a seventh of the column.
        var up = AddButton(p, Slot(3), y, w7, RowH, "", () => MoveDialogueLine(-1), lineChosen);
        up.Text = () => dialogueTab == DialogueTab.During ? "Earlier" : "Move up";
        up.Label.fontSize = 18;
        var down = AddButton(p, Slot(4), y, w7, RowH, "", () => MoveDialogueLine(1), lineChosen);
        down.Text = () => dialogueTab == DialogueTab.During ? "Later" : "Move down";
        down.Label.fontSize = 18;
        // Also for an item that isn't a line (written by hand), which can only be deleted.
        AddButton(p, Slot(5), y, w7, RowH, "Delete", DeleteDialogueLine, () => onLines() && ChosenInList);
        AddButton(p, Slot(0), y, 2 * w7 + 8, RowH, "New speaker...", () => NewSpeaker(forLine: false), onSpeakers);
        AddButton(p, Slot(2), y, 2 * w7 + 8, RowH, "Delete speaker", DeleteSpeaker, speakerChosen);
        AddButton(p, Slot(6), y, w7, RowH, "Undo", UndoDialogueChange, () => draft?.CanUndoDialogue == true);
        y -= RowStep;
        // Room for two lines, in case the keys don't fit on one.
        AddText(p, 0, ref y, Col1W, 38, () => onLines()
            ? "Insert: add   Ctrl+R: reply   Ctrl+D: copy   Delete: delete   Ctrl+Up/Down: move   Ctrl+Z / Ctrl+Y: undo, redo   Space: play"
            : "Ctrl+Z / Ctrl+Y: undo, redo", 15);
        var problems = AddText(p, 0, ref y, Col1W, 884 + y - 8, DialogueProblemsText, 16);
        problems.color = Hex(0xF2B02E);

        // Column 2: the preview, then the chosen line or speaker.
        float y2 = 0;
        AddHeader(p, Col2, ref y2, Col2W, "Preview");
        BuildDialoguePreview(p, y2);
        y2 -= PreviewH + 8;
        // Widths for the labels: the menu font is monospaced, 13 units a letter at 20, 11 at 17.
        var play = AddButton(p, Col2, y2, 80, RowH, "", ToggleDialoguePlay, onLines);
        play.Text = () => dialoguePlay != null ? "Stop" : "Play";
        play.Active = () => dialoguePlay != null;
        var closeUp = AddButton(p, Col2 + 88, y2, 118, RowH, "Close up", () => dialogueCloseUp = !dialogueCloseUp, onLines);
        closeUp.Active = () => dialogueCloseUp;
        var speakerCloseUp = AddButton(p, Col2, y2, 130, RowH, "Close up", () => dialogueCloseUp = !dialogueCloseUp, onSpeakers);
        speakerCloseUp.Active = () => dialogueCloseUp;
        var test = AddButton(p, Col2 + 214, y2, 190, RowH, "Test in a battle", () => TestDialogue(fromLine: false), () => onLines() && AnythingCharted());
        test.Label.fontSize = 17;
        var testLine = AddButton(p, Col2 + 412, y2, Col2W - 412, RowH, "Test from this line", () => TestDialogue(fromLine: true),
            () => lineChosen() && dialogueTab == DialogueTab.During && AnythingCharted() && ChosenTime() != null);
        testLine.Label.fontSize = 17;
        y2 -= RowStep;
        BuildLineEditor(p, y2, lineChosen);
        BuildSpeakerEditor(p, y2, speakerChosen);
    }

    // The chosen line's rows, under the preview.
    private static void BuildLineEditor(Page p, float y, Func<bool> lineChosen)
    {
        const float half = Col2W / 2 - 6;
        Func<bool> notNarrator = () => lineChosen() && ChosenWho != Who.Narrator;
        Func<bool> during = () => lineChosen() && dialogueTab == DialogueTab.During;
        LiveHeader(p, Col2, y, Col2W, ChosenLineTitle, lineChosen);
        y -= 28;
        var who = AddChoice(p, Col2, ref y, Col2W, "Speaker", SpeakerValue, ChooseLineSpeaker, lineChosen);
        // Right after New speaker..., this row takes the new speaker's name.
        who.Text = () => typing == SpeakerNameField ? FieldText(SpeakerNameField, who) : $"<color=#9D92B4>Speaker</color><pos=32%>{SpeakerValue()}";
        who.Active = () => typing == SpeakerNameField;
        float row = y;
        AddButton(p, Col2, row, 48, RowH, "<", () => StepLineFace(-1), notNarrator);
        var face = AddButton(p, Col2 + 52, row, half - 104, RowH, "", ChooseLineFace, notNarrator);
        face.Text = LineFaceText;
        face.Label.fontSize = 18;
        AddButton(p, Col2 + half - 48, row, 48, RowH, ">", () => StepLineFace(1), notNarrator);
        var side = AddButton(p, Col2 + half + 12, row, half, RowH, "", ToggleLineSide, notNarrator);
        side.Text = LineSideText;
        side.Label.fontSize = 19;
        y -= RowStep;
        AddField(p, Col2, ref y, Col2W, LineNameField, notNarrator);
        AddField(p, Col2, ref y, Col2W, LineTextField, lineChosen, 96);

        // During the song: when, and whether the song stops for it.
        float at = y;
        var when = AddField(p, Col2, ref at, half, WhenField, during);
        when.Text = () => typing == WhenField ? FieldText(WhenField, when) : $"<color=#9D92B4>When</color><pos=32%>{Escape(WhenText())}";
        var stops = AddButton(p, Col2 + half + 12, y, half, RowH, "", ToggleStops, during);
        stops.Text = () => $"Stops the song: {(ChosenStops() ? "on" : "off")}";
        stops.Active = ChosenStops;
        stops.Label.fontSize = 19;
        // A live line: how long the box shows.
        float live = at;
        var shows = AddField(p, Col2, ref live, Col2W, ShowsForField, () => during() && !ChosenStops());
        shows.Text = () => typing == ShowsForField ? FieldText(ShowsForField, shows)
            : LineNumber("duration") is double d ? $"<color=#9D92B4>Shows for</color><pos=32%>{Num(d)} s" : FieldText(ShowsForField, shows);
        // A line that waits (before, after, or stopping the song): for a key, or goes on by itself.
        BuildGoesOn(p, y, () => lineChosen() && dialogueTab != DialogueTab.During);
        BuildGoesOn(p, at, () => during() && ChosenStops());

        float noteY = y - RowStep;
        AddText(p, Col2, ref noteY, Col2W, 36, LineNote, 16, () => lineChosen() && dialogueTab != DialogueTab.During);
        noteY = at - RowStep;
        AddText(p, Col2, ref noteY, Col2W, 36, LineNote, 16, during);
    }

    private static void BuildGoesOn(Page p, float y, Func<bool> shown)
    {
        const float half = Col2W / 2 - 6;
        var toggle = AddButton(p, Col2, y, half, RowH, "", ToggleGoesOn, shown);
        toggle.Text = () => LineNumber("duration") != null ? "Goes on by itself: on" : "Goes on by itself: off";
        toggle.Active = () => LineNumber("duration") != null;
        toggle.Label.fontSize = 19;
        float at = y;
        var after = AddField(p, Col2 + half + 12, ref at, half, AfterField, () => shown() && LineNumber("duration") != null);
        after.Text = () => typing == AfterField ? FieldText(AfterField, after) : $"<color=#9D92B4>After</color><pos=32%>{Num(LineNumber("duration") ?? 0)} s";
        // While it's off, what it does instead.
        var waits = MakeText("Waits", pagePanels[p], 17, TextAlignmentOptions.Left);
        waits.color = DimText;
        PlaceTop(waits.rectTransform, Col2 + half + 20, y, half - 8, RowH);
        Ui.AddLiveText(waits, () => "Waits for a key (like the game)").Visible = () => shown() && LineNumber("duration") == null;
    }

    // The chosen speaker's rows, under the preview.
    private static void BuildSpeakerEditor(Page p, float y, Func<bool> speakerChosen)
    {
        const float half = Col2W / 2 - 6;
        LiveHeader(p, Col2, y, Col2W, () => chosenSpeaker == null ? "" : SpeakerName(chosenSpeaker), speakerChosen);
        y -= 28;
        AddField(p, Col2, ref y, Col2W, SpeakerNameField, speakerChosen);
        AddButton(p, Col2, y, 200, RowH, "Picture...", ChooseSpeakerPicture, speakerChosen);
        var picture = MakeText("Picture", pagePanels[p], 17, TextAlignmentOptions.Left);
        picture.color = DimText;
        PlaceTop(picture.rectTransform, Col2 + 214, y, Col2W - 214, RowH);
        Ui.AddLiveText(picture, SpeakerPictureText).Visible = speakerChosen;
        y -= RowStep;

        // Its other faces: a small list, and what to do with the chosen one.
        faceListArea = MakeRect("Faces", pagePanels[p]);
        PlaceTop(faceListArea, Col2, y, Col2W, FaceRowsVisible * FaceRowStep);
        for (int r = 0; r < FaceRowsVisible; r++)
        {
            int row = r;
            var b = AddButton(p, Col2, y - r * FaceRowStep, Col2W, FaceRowH, "", () => ChooseFaceRow(row), () => speakerChosen() && faceFirst + row < ChosenFaces().Count);
            b.Text = () => FaceRowText(row);
            b.Active = () => faceFirst + row == chosenFace;
            b.Label.alignment = TextAlignmentOptions.Left;
            b.Label.margin = new Vector4(14, 0, 10, 0);
            b.Label.fontSize = 17;
        }
        float emptyY = y;
        AddText(p, Col2, ref emptyY, Col2W, 50, () => "Only the one picture. Add expression... adds more faces, like angry or hurt.", 17,
            () => speakerChosen() && ChosenFaces().Count == 0);
        y -= FaceRowsVisible * FaceRowStep;
        // Widths for the labels (the menu font is monospaced): "Add expression..." needs 188 at 17.
        Func<bool> faceChosen = () => speakerChosen() && chosenFace >= 0 && chosenFace < ChosenFaces().Count;
        var add = AddButton(p, Col2, y, 200, RowH, "Add expression...", AddSpeakerFace, speakerChosen);
        add.Label.fontSize = 17;
        AddButton(p, Col2 + 208, y, 124, RowH, "Rename", () => StartTyping(FaceNameField), faceChosen);
        AddButton(p, Col2 + 340, y, 160, RowH, "Picture...", ChooseFacePicture, faceChosen);
        AddButton(p, Col2 + 508, y, Col2W - 508, RowH, "Remove", RemoveSpeakerFace, faceChosen);
        y -= RowStep;

        var side = AddButton(p, Col2, y, half, RowH, "", ToggleSpeakerSide, speakerChosen);
        side.Text = () => chosenSpeaker != null && SpeakerSide(chosenSpeaker) == DialogueSide.Left ? "Side: left" : "Side: right";
        side.Label.fontSize = 19;
        var mirror = AddButton(p, Col2 + half + 12, y, half, RowH, "", ToggleSpeakerMirror, speakerChosen);
        mirror.Text = () => $"Mirror: {(chosenSpeaker != null && draft?.SpeakerFlag(chosenSpeaker, "flip") == true ? "on" : "off")}";
        mirror.Active = () => chosenSpeaker != null && draft?.SpeakerFlag(chosenSpeaker, "flip") == true;
        mirror.Label.fontSize = 19;
        y -= RowStep;
        float nudge = y;
        PreviewStepper(p, Col2, ref nudge, half, NudgeXField, () => $"Nudge sideways {Num(SpeakerNudge().X)}", () => StepNudge(-1, 0), () => StepNudge(1, 0), speakerChosen);
        PreviewStepper(p, Col2 + half + 12, ref y, half, NudgeYField, () => $"Nudge up/down {Num(SpeakerNudge().Y)}", () => StepNudge(0, -1), () => StepNudge(0, 1), speakerChosen);
        AddText(p, Col2, ref y, Col2W, 34, SpeakerNote, 15, speakerChosen);
    }

    // A small caps heading whose text is worked out each frame.
    private static void LiveHeader(Page p, float x, float y, float w, Func<string> text, Func<bool> shown)
    {
        var t = MakeText("Heading", pagePanels[p], 15, TextAlignmentOptions.Left);
        t.color = DimText;
        PlaceTop(t.rectTransform, x + 2, y, w, 20);
        Ui.AddLiveText(t, () => Escape(text().ToUpperInvariant())).Visible = shown;
    }

    // ---- reading the draft ----------------------------------------------------------------------

    /// <summary>Reads the draft's dialogue again when it changed since the last look (or when <paramref name="force"/>).</summary>
    private static void RefreshDialogue(bool force = false)
    {
        if (draft == null)
        {
            dialogueRead = null;
            dialogueReadFor = null;
            return;
        }
        if (!force && dialogueReadFor == draft && dialogueReadAt == draft.Changes) return;
        dialogueReadFor = draft;
        dialogueReadAt = draft.Changes;
        ownChecksKey = "";
        try
        {
            JsonElement element = default;
            if (draft.DialogueJson() is { } json)
            {
                using var doc = JsonDocument.Parse(json);
                element = doc.RootElement.Clone();
            }
            // Nothing is stamped here: the page only reads.
            dialogueRead = DialogueReader.Read(element, PackageFiles.Folder(draft.Folder), dialogueChart, dialogueSlots, new List<string>(), _ => { });
        }
        catch (Exception ex)
        {
            // The page reads what the arcade will; whatever goes wrong here, the page stays open.
            if (ex is not (JsonException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException))
                ModLog.Error("Battle creator: reading the dialogue for its page failed: " + ex);
            dialogueRead = new BattleDialogueData();
            dialogueRead.Problems.Add(new DialogueProblem { Text = ex.Message, Plain = $"the dialogue couldn't be read ({ex.Message})" });
        }
        linesKept.Clear();
        foreach (DialogueSection section in Enum.GetValues(typeof(DialogueSection)))
            linesKept[section] = new HashSet<int>(dialogueRead.Lines(section).Select(l => l.Index));
        RefreshRows();
    }

    /// <summary>Reads the saved chart again, for the times of beats and the checks against its notes.</summary>
    private static void RefreshDialogueChart()
    {
        dialogueChart = new ChartText();
        try
        {
            string? name = draft == null ? null : PackageFiles.SafeName(draft.ChartPath);
            var files = draft == null ? null : PackageFiles.Folder(draft.Folder);
            if (name != null && files!.Exists(name)) dialogueChart = ChartText.Parse(files.ReadAllText(name, BattlePackage.MaxChartBytes));
            dialogueSlots = dialogueChart.SongSlots(draft?.Lanes ?? 4, new List<string>());
        }
        catch (Exception ex) when (BattleDraft.IsFileProblem(ex))
        {
            dialogueChart = new ChartText();
            dialogueSlots = new ChartText.NoteBlock?[BattleChartFile.SlotCount];
        }
        dialogueTiming = new EditorChart(4);
        dialogueTiming.ReadTiming(dialogueChart);
        dialogueReadAt = -1;
    }

    private static DialogueSection ShownSection => (DialogueSection)(int)dialogueTab;

    // The rows of the list: during the song by time (lines without one last), else as written.
    private static void RefreshRows()
    {
        lineRows.Clear();
        speakerRows.Clear();
        if (draft == null) return;
        if (dialogueTab == DialogueTab.Speakers)
        {
            speakerRows.AddRange(draft.SpeakerKeys());
            if (chosenSpeaker == null || !speakerRows.Contains(chosenSpeaker, StringComparer.OrdinalIgnoreCase))
                chosenSpeaker = speakerRows.FirstOrDefault();
            if (chosenFace >= ChosenFaces().Count) chosenFace = ChosenFaces().Count - 1;
            return;
        }
        var section = ShownSection;
        var rows = Enumerable.Range(0, draft.LineCount(section));
        if (section == DialogueSection.During) rows = rows.OrderBy(i => LineTime(i) ?? double.MaxValue);
        lineRows.AddRange(rows);
        if (chosenLine >= lineRows.Count) chosenLine = lineRows.Count - 1;
        if (chosenLine < 0 && lineRows.Count > 0) chosenLine = lineRows[0];
    }

    private static int RowCount => dialogueTab == DialogueTab.Speakers ? speakerRows.Count : lineRows.Count;

    private static void SetDialogueTab(DialogueTab tab)
    {
        if (!FinishTyping()) return;
        StopDialoguePlay();
        if (tab == dialogueTab) return;
        dialogueTab = tab;
        chosenLine = -1;
        lineFirst = 0;
        followedRow = -1;
        RefreshDialogue();
        RefreshRows();
    }

    // ---- the frame ------------------------------------------------------------------------------

    /// <summary>Every frame on the Dialogue page: the draft read again if it changed, the lists, and the preview.</summary>
    private static void UpdateDialoguePage(InputMouse? clicks)
    {
        RefreshDialogue();
        ScrollDialogueLists();
        UpdateDialoguePreview(clicks);
    }

    // The list follows the chosen line when it changes, and the mouse wheel over a list scrolls it.
    private static void ScrollDialogueLists()
    {
        int row = dialogueTab == DialogueTab.Speakers
            ? speakerRows.FindIndex(k => k.Equals(chosenSpeaker, StringComparison.OrdinalIgnoreCase))
            : lineRows.IndexOf(chosenLine);
        var mouse = InputMouse.current;
        float wheel = mouse != null ? mouse.scroll.ReadValue().y : 0;
        Vector2 at = mouse != null ? mouse.position.ReadValue() : new Vector2(-1, -1);
        bool overLines = wheel != 0 && lineListArea && RectTransformUtility.RectangleContainsScreenPoint(lineListArea, at, null);
        lineFirst = ListScroll.First(lineFirst, Math.Max(0, row), RowCount, LineRowsVisible, row != followedRow, overLines ? wheel : 0);
        followedRow = row;
        bool overFaces = wheel != 0 && faceListArea && RectTransformUtility.RectangleContainsScreenPoint(faceListArea, at, null);
        int faces = dialogueTab == DialogueTab.Speakers ? ChosenFaces().Count : 0;
        faceFirst = ListScroll.First(faceFirst, Math.Max(0, chosenFace), faces, FaceRowsVisible, chosenFace != followedFace, overFaces ? wheel : 0);
        followedFace = chosenFace;
    }

    /// <summary>The page's keys, when nothing is being typed; true when one was used.</summary>
    private static bool HandleDialogueKeys(InputKeyboard k)
    {
        if (page != Page.Dialogue || draft == null) return false;
        bool ctrl = Ctrl(k);
        bool enter = Pressed(k, Key.Enter) || Pressed(k, Key.NumpadEnter);
        if (Pressed(k, Key.Space)) ToggleDialoguePlay();
        else if (ctrl && Pressed(k, Key.Z)) UndoDialogueChange();
        else if (ctrl && Pressed(k, Key.Y)) RedoDialogueChange();
        else if (dialogueTab == DialogueTab.Speakers) return false;
        else if (Pressed(k, Key.Insert) || (ctrl && enter)) AddDialogueLine();
        else if (ctrl && Pressed(k, Key.R)) ReplyToLine();
        else if (ctrl && Pressed(k, Key.D)) CopyDialogueLine();
        else if (Pressed(k, Key.Delete)) DeleteDialogueLine();
        else if (ctrl && Pressed(k, Key.UpArrow)) MoveDialogueLine(-1);
        else if (ctrl && Pressed(k, Key.DownArrow)) MoveDialogueLine(1);
        else return false;
        return true;
    }

    /// <summary>Whether the dialogue can be changed; says why not when its file can't be read.</summary>
    private static bool DialogueEditable()
    {
        if (draft == null) return false;
        if (draft.DialogueLocked == null) return true;
        Say(draft.DialogueLocked, 8f);
        return false;
    }

    // ---- column 1: the sections and the list -----------------------------------------------------

    private static string TabText(DialogueTab tab)
    {
        if (draft == null) return "";
        return tab switch
        {
            DialogueTab.Before => $"Before ({draft.LineCount(DialogueSection.Before)})",
            DialogueTab.During => $"During ({draft.LineCount(DialogueSection.During)})",
            DialogueTab.AfterWin => $"After a win ({draft.LineCount(DialogueSection.AfterWin)})",
            DialogueTab.AfterLoss => $"After a loss ({draft.LineCount(DialogueSection.AfterLoss)})",
            _ => $"Speakers ({draft.SpeakerKeys().Count})",
        };
    }

    private static string DialogueHint()
    {
        if (draft == null) return "";
        string hint = dialogueTab switch
        {
            DialogueTab.Before => "Shown before the song starts, one line at a time. The player presses a key for the next line.",
            DialogueTab.During => "Shown while the song plays, at their time. The box comes and goes by itself and the notes keep coming. " +
                                  "A line can stop the song instead, like a boss's talk between songs.",
            DialogueTab.AfterWin => "Shown after the last note when the player wins, before the results.",
            DialogueTab.AfterLoss => "Shown when the player is beaten, before the battle ends. A test can't be lost, so use Play to see them.",
            _ => "Your own speakers: a name and a picture. Game characters don't need adding here.",
        };
        if (dialogueTab == DialogueTab.During && !dialogueSlots.Any(s => s != null)) hint += " Nothing is charted yet, so there's no song for them to play over.";
        return Escape(hint);
    }

    private static string RowText(int row)
    {
        int index = lineFirst + row;
        if (draft == null || index >= RowCount) return "";
        if (dialogueTab == DialogueTab.Speakers)
        {
            string key = speakerRows[index];
            int faces = draft.SpeakerExpressions(key).Count, lines = draft.LinesOf(key);
            string usable = dialogueRead?.FindSpeaker(key) == null ? "   <color=#F2B02E>(can't be used: see below)</color>" : "";
            return $"{Escape(SpeakerName(key))}   <color=#9D92B4>{faces} expression{(faces == 1 ? "" : "s")}   used by {lines} line{(lines == 1 ? "" : "s")}</color>{usable}";
        }
        var section = ShownSection;
        int i = lineRows[index];
        string start = section == DialogueSection.During
            ? (LineTime(i) is double t ? DialogueReader.Clock(t) : "no time")
            : (index + 1).ToString(CultureInfo.InvariantCulture);
        if (!draft.IsLine(section, i)) return $"<color=#9D92B4>{start}</color>  <color=#F2B02E>(not a line: battle.json has something else here)</color>";
        string speaker = draft.LineText(section, i, "speaker") ?? "";
        string? face = draft.LineText(section, i, "expression");
        string text = DialogueReader.CleanText(draft.LineText(section, i, "text"), out _);
        string who = Escape(SpeakerLabel(speaker)) + (face?.Trim().Length > 0 ? $" ({Escape(face.Trim())})" : "");
        string said = text.Length > 0 ? Escape(text) : "<color=#9D92B4>(no text yet)</color>";
        string stops = section == DialogueSection.During && draft.LineFlag(section, i, "pause") ? "  <color=#9D92B4>(stops the song)</color>" : "";
        string left = linesKept.TryGetValue(section, out var kept) && !kept.Contains(i) ? "  <color=#F2B02E>(left out)</color>" : "";
        return $"<color=#9D92B4>{start}</color>  {who}: {said}{stops}{left}";
    }

    private static bool RowChosen(int row)
    {
        int index = lineFirst + row;
        if (index >= RowCount) return false;
        return dialogueTab == DialogueTab.Speakers ? speakerRows[index].Equals(chosenSpeaker, StringComparison.OrdinalIgnoreCase) : lineRows[index] == chosenLine;
    }

    private static void ChooseRow(int row)
    {
        int index = lineFirst + row;
        if (!FinishTyping() || index >= RowCount) return;
        StopDialoguePlay();
        if (dialogueTab == DialogueTab.Speakers)
        {
            chosenSpeaker = speakerRows[index];
            chosenFace = -1;
            faceFirst = 0;
            return;
        }
        chosenLine = lineRows[index];
        if (LineValue("speaker") is { Length: > 0 } speaker) lastSpeaker = speaker;
    }

    // The problems with the shown section (or, on the Speakers tab, with the speakers), in the
    // page's words: the loader's, then the page's own (characters and faces the game doesn't
    // have, text too long for the box). Problems with the dialogue as a whole show on every tab.
    private static string DialogueProblemsText()
    {
        if (draft == null) return "";
        if (draft.DialogueLocked != null) return Escape(draft.DialogueLocked);
        if (dialogueRead == null) return "";
        bool speakers = dialogueTab == DialogueTab.Speakers;
        var lines = dialogueRead.Problems
            .Where(p => speakers ? p.Section == null : p.Section == ShownSection || (p.Section == null && p.Speaker == null))
            .Select(p => p.Plain).ToList();
        if (!speakers) lines.AddRange(OwnChecks());
        if (lines.Count == 0) return "";
        const int max = 10;
        var shown = lines.Take(max).Select(l => "- " + Escape(Sentence(l))).ToList();
        if (lines.Count > max) shown.Add($"(and {lines.Count - max} more)");
        return string.Join("\n", shown);
    }

    // A problem as a sentence: a capital first letter and a full stop.
    private static string Sentence(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return text;
        text = char.ToUpperInvariant(text[0]) + text.Substring(1);
        return text.EndsWith(".") ? text : text + ".";
    }

    // ---- column 1: the buttons -------------------------------------------------------------------

    private static void AddDialogueLine()
    {
        if (draft == null || dialogueTab == DialogueTab.Speakers || !FinishTyping() || !DialogueEditable()) return;
        StopDialoguePlay();
        var section = ShownSection;
        if (draft.LineCount(section) >= DialogueReader.MaxLines(section))
        {
            Say($"The lines {DialogueReader.SectionName(section)} are full: the battle plays at most {DialogueReader.MaxLines(section)}.", 5f);
            return;
        }
        // After the chosen line, with the same speaker (and side, when the line has its own).
        bool has = HasChosenLine;
        string speaker = (has ? LineValue("speaker") : null) ?? lastSpeaker;
        var values = new List<(string, JsonNode?)> { ("speaker", JsonValue.Create(speaker)) };
        if (has && LineValue("side") is { } side) values.Add(("side", JsonValue.Create(side)));
        values.Add(("text", JsonValue.Create("")));
        if (section == DialogueSection.During) values.AddRange(NextWhen());
        int at = section == DialogueSection.During || !has ? draft.LineCount(section) : chosenLine + 1;
        chosenLine = draft.AddLine(section, at, values.ToArray());
        lastSpeaker = speaker;
        StartTyping(LineTextField);
    }

    private static void CopyDialogueLine()
    {
        if (draft == null || !HasChosenLine || !FinishTyping() || !DialogueEditable()) return;
        StopDialoguePlay();
        var section = ShownSection;
        if (draft.LineCount(section) >= DialogueReader.MaxLines(section))
        {
            Say($"The lines {DialogueReader.SectionName(section)} are full: the battle plays at most {DialogueReader.MaxLines(section)}.", 5f);
            return;
        }
        bool during = section == DialogueSection.During;
        chosenLine = draft.CopyLine(section, chosenLine, during ? draft.LineCount(section) : chosenLine + 1, during ? NextWhen() : Array.Empty<(string, JsonNode?)>());
        Say(during ? $"Copied, one beat later ({WhenText()})." : "Copied.", 3f);
    }

    // When a new line during the song goes: a beat after the chosen line (written the way the
    // chosen line's is: a beat, or a time), or at 0:00 when there's none.
    private static (string, JsonNode?)[] NextWhen()
    {
        if (HasChosenLine && LineNumber("beat") is double beat)
            return new (string, JsonNode?)[] { ("beat", BattleDraft.ArtNumberNode(Math.Round(beat + 1, 3))), ("time", null) };
        if (HasChosenLine && LineNumber("time") is double time)
            return new (string, JsonNode?)[] { ("time", BattleDraft.ArtNumberNode(Math.Round(dialogueTiming.BeatToSeconds(dialogueTiming.SecondsToBeat(time) + 1), 3))), ("beat", null) };
        return new (string, JsonNode?)[] { ("time", BattleDraft.ArtNumberNode(0)), ("beat", null) };
    }

    /// <summary>
    /// Reply: a new line right after the chosen one, said by the other side of the talk. Anyone
    /// but the player is answered by the player, and the player by whoever spoke before her.
    /// </summary>
    private static void ReplyToLine()
    {
        if (draft == null || dialogueTab == DialogueTab.Speakers || !FinishTyping() || !DialogueEditable()) return;
        if (!HasChosenLine)
        {
            Say("Choose the line to reply to first.", 3f);
            return;
        }
        StopDialoguePlay();
        var section = ShownSection;
        if (draft.LineCount(section) >= DialogueReader.MaxLines(section))
        {
            Say($"The lines {DialogueReader.SectionName(section)} are full: the battle plays at most {DialogueReader.MaxLines(section)}.", 5f);
            return;
        }
        string speaker = ReplySpeaker();
        var values = new List<(string, JsonNode?)> { ("speaker", JsonValue.Create(speaker)), ("text", JsonValue.Create("")) };
        if (section == DialogueSection.During) values.AddRange(ReplyWhen());
        chosenLine = draft.AddLine(section, chosenLine + 1, values.ToArray());
        lastSpeaker = speaker;
        StartTyping(LineTextField);
    }

    // Who answers the chosen line: the player, unless she said it. Then the last one before her
    // who isn't the player or the Narrator (in this section, else anywhere in the battle's lines),
    // else the battle's first speaker of its own, else the Narrator. A speaker of the battle's own
    // with her id (written by hand) takes her place, since that key wins over the game's Karma.
    private static string ReplySpeaker()
    {
        string player = draft!.SpeakerKeys().FirstOrDefault(DialogueReader.IsPlayer) ?? PlayerId;
        if (!DialogueReader.IsPlayer(ChosenSpeakerId)) return player;
        var section = ShownSection;
        bool Other(string? id) => (id ?? "").Trim() is { Length: > 0 } s && !DialogueReader.IsPlayer(s) && WhoIs(s) != Who.Narrator;
        for (int r = lineRows.IndexOf(chosenLine) - 1; r >= 0; r--)
            if (draft.LineText(section, lineRows[r], "speaker") is { } id && Other(id)) return id.Trim();
        foreach (DialogueSection s in Enum.GetValues(typeof(DialogueSection)))
            for (int i = 0; i < draft.LineCount(s); i++)
                if (draft.LineText(s, i, "speaker") is { } id && Other(id)) return id.Trim();
        return draft.SpeakerKeys().FirstOrDefault(k => !DialogueReader.IsPlayer(k)) ?? DialogueReader.Narrator;
    }

    // When a reply during the song goes. To a line that stops the song: in the same stop, right
    // after it. To a live line: on the first beat after that line has gone, so it isn't cut short.
    private static (string, JsonNode?)[] ReplyWhen()
    {
        if (ChosenTime() is not double start) return NextWhen();
        // The chosen line's own beat or time as written, not rounded: only lines at the very same time are one stop.
        if (ChosenStops())
            return LineNumber("beat") != null
                ? new (string, JsonNode?)[] { ("beat", draft!.LineValueCopy(ShownSection, chosenLine, "beat")), ("time", null), ("pause", JsonValue.Create(true)) }
                : new (string, JsonNode?)[] { ("time", draft!.LineValueCopy(ShownSection, chosenLine, "time")), ("beat", null), ("pause", JsonValue.Create(true)) };
        double shows = LineNumber("duration") is double d ? Math.Clamp(d, DialogueReader.MinDuration, DialogueReader.MaxDuration)
            : DialogueReader.LiveSeconds(DialogueReader.CleanText(LineValue("text"), out _));
        double beat = Math.Ceiling(Math.Round(dialogueTiming.SecondsToBeat(start + shows), 3));
        if (LineNumber("beat") != null) return new (string, JsonNode?)[] { ("beat", BattleDraft.ArtNumberNode(beat)), ("time", null) };
        return new (string, JsonNode?)[] { ("time", BattleDraft.ArtNumberNode(Math.Round(dialogueTiming.BeatToSeconds(beat), 3))), ("beat", null) };
    }

    /// <summary>Move up or down in the list; during the song, a beat earlier or later.</summary>
    private static void MoveDialogueLine(int by)
    {
        if (draft == null || !HasChosenLine || !FinishTyping() || !DialogueEditable()) return;
        StopDialoguePlay();
        var section = ShownSection;
        if (section != DialogueSection.During)
        {
            int to = chosenLine + by;
            if (to < 0 || to >= draft.LineCount(section)) return;
            chosenLine = draft.MoveLine(section, chosenLine, to);
            return;
        }
        if (LineNumber("beat") is double beat)
            draft.SetLine(section, chosenLine, ("beat", BattleDraft.ArtNumberNode(Math.Max(0, Math.Round(beat + by, 3)))), ("time", null));
        else if (LineNumber("time") is double time)
            draft.SetLine(section, chosenLine, ("time", BattleDraft.ArtNumberNode(Math.Max(0, Math.Round(dialogueTiming.BeatToSeconds(dialogueTiming.SecondsToBeat(time) + by), 3)))));
        else
        {
            Say("This line has no time yet: type one in When.", 3f);
            return;
        }
        Say($"Now at {WhenText()}.", 2f);
    }

    private static void DeleteDialogueLine()
    {
        if (draft == null || !ChosenInList || !FinishTyping() || !DialogueEditable()) return;
        StopDialoguePlay();
        var section = ShownSection;
        string name = section == DialogueSection.During && ChosenTime() is double t ? $"the line at {DialogueReader.Clock(t)}" : $"line {chosenLine + 1}";
        // The next row in the list is chosen after it.
        int row = lineRows.IndexOf(chosenLine);
        int removed = chosenLine;
        draft.RemoveLine(section, removed);
        RefreshDialogue();
        chosenLine = lineRows.Count == 0 ? -1 : lineRows[Math.Clamp(row, 0, lineRows.Count - 1)];
        Say($"Deleted {name}. Undo brings it back.", 4f);
    }

    private static void UndoDialogueChange()
    {
        if (draft == null || !FinishTyping() || !DialogueEditable()) return;
        StopDialoguePlay();
        Say(draft.UndoDialogue() ? "Undone." : "Nothing to undo.", 2f);
    }

    private static void RedoDialogueChange()
    {
        if (draft == null || !FinishTyping() || !DialogueEditable()) return;
        StopDialoguePlay();
        Say(draft.RedoDialogue() ? "Redone." : "Nothing to redo.", 2f);
    }

    // ---- the chosen line -------------------------------------------------------------------------

    private static bool ChosenInList => draft != null && dialogueTab != DialogueTab.Speakers && chosenLine >= 0 && chosenLine < draft.LineCount(ShownSection);
    private static bool HasChosenLine => ChosenInList && draft!.IsLine(ShownSection, chosenLine);

    private static string? LineValue(string key) => HasChosenLine ? draft!.LineText(ShownSection, chosenLine, key) : null;
    private static double? LineNumber(string key) => HasChosenLine ? draft!.LineNumber(ShownSection, chosenLine, key) : null;

    private static void SetChosen(params (string Key, JsonNode? Value)[] values)
    {
        if (!HasChosenLine || !DialogueEditable()) return;
        draft!.SetLine(ShownSection, chosenLine, values);
    }

    private static string ChosenSpeakerId => (LineValue("speaker") ?? "").Trim() is { Length: > 0 } s ? s : DialogueReader.Narrator;
    private static Who ChosenWho => WhoIs(ChosenSpeakerId);
    private static bool ChosenStops() => HasChosenLine && dialogueTab == DialogueTab.During && draft!.LineFlag(ShownSection, chosenLine, "pause");

    /// <summary>Who a line's speaker is: one of the battle's own, the Narrator (also for none), or a game character.</summary>
    private static Who WhoIs(string? speaker)
    {
        string s = (speaker ?? "").Trim();
        if (draft != null && draft.SpeakerKeys().Any(k => k.Equals(s, StringComparison.OrdinalIgnoreCase))) return Who.Custom;
        return s.Length == 0 || s.Equals(DialogueReader.Narrator, StringComparison.OrdinalIgnoreCase) ? Who.Narrator : Who.Game;
    }

    /// <summary>A line's time in seconds on the song's clock (a beat's worked out with the chart's tempo); null without one.</summary>
    private static double? LineTime(int index)
    {
        if (draft == null) return null;
        if (draft.LineNumber(DialogueSection.During, index, "beat") is double beat) return dialogueTiming.BeatToSeconds(beat);
        return draft.LineNumber(DialogueSection.During, index, "time");
    }

    private static double? ChosenTime() => HasChosenLine && dialogueTab == DialogueTab.During ? LineTime(chosenLine) : null;

    private static string ChosenLineTitle()
    {
        if (!HasChosenLine) return "";
        return DialogueReader.LineName(ShownSection, chosenLine, ChosenTime());
    }

    // "Karma (the player)", "Kimothy", "The Warden (yours)", "Narrator (no picture)".
    private static string SpeakerValue()
    {
        string id = ChosenSpeakerId;
        return ChosenWho switch
        {
            Who.Custom => $"{Escape(SpeakerName(id))} <color=#9D92B4>(yours)</color>",
            Who.Narrator => "Narrator <color=#9D92B4>(no picture)</color>",
            _ when IsPlayer(id) => $"{Escape(GameName(id))} <color=#9D92B4>(the player)</color>",
            _ => Escape(GameName(id)) + (GameCharacters() != null && FindGame(id) == null ? " <color=#F2B02E>(the game has no such character: the Narrator says it)</color>" : ""),
        };
    }

    /// <summary>Whether a line's speaker is the player's character: the game's Karma (not a speaker of the battle's own with that key).</summary>
    private static bool IsPlayer(string? speaker) => DialogueReader.IsPlayer(speaker) && WhoIs(speaker) == Who.Game;

    /// <summary>The player's character's id as the game spells it; known before the game's characters load.</summary>
    private static string PlayerId => FindGame(DialogueReader.Player)?.Id ?? DialogueReader.Player;

    /// <summary>
    /// How the list and the rows name a speaker: its name, "Narrator", or a game character's name.
    /// The player's character is just "Karma" here, as the box's name tag says; the speaker
    /// picker and the chosen line's Speaker row say "Karma (the player)".
    /// </summary>
    private static string SpeakerLabel(string? speaker) => WhoIs(speaker) switch
    {
        Who.Custom => SpeakerName(speaker!.Trim()),
        Who.Narrator => (speaker ?? "").Trim().Length == 0 ? "(no speaker)" : "Narrator",
        _ => GameName(speaker!.Trim()),
    };

    /// <summary>One of the battle's own speakers' names (its key when it has none).</summary>
    private static string SpeakerName(string key)
    {
        string name = DialogueReader.CleanText(draft?.SpeakerText(key, "name"), out _);
        return name.Length > 0 ? name : key;
    }

    private static DialogueSide? SideOf(string? side) => (side ?? "").Trim().ToLowerInvariant() switch
    {
        "left" => DialogueSide.Left,
        "right" => DialogueSide.Right,
        _ => null,
    };

    /// <summary>Where a speaker usually stands: a speaker of the battle's own on its side, Karma on the left, other game characters on the right.</summary>
    private static DialogueSide UsualSide(string speaker) => WhoIs(speaker) switch
    {
        Who.Custom => SpeakerSide(speaker),
        Who.Game => DialogueReader.GameSide(speaker),
        _ => DialogueSide.Right,
    };

    private static DialogueSide SpeakerSide(string key) => SideOf(draft?.SpeakerText(key, "side")) ?? DialogueSide.Right;

    private static DialogueSide ChosenSide => SideOf(LineValue("side")) ?? UsualSide(ChosenSpeakerId);

    private static string LineSideText()
    {
        var side = ChosenSide;
        return $"Side: {(side == DialogueSide.Left ? "left" : "right")}{(side == UsualSide(ChosenSpeakerId) ? " (usual)" : "")}";
    }

    // The other side; the speaker's usual side is left to the speaker (the line's own goes).
    private static void ToggleLineSide()
    {
        if (!HasChosenLine || !FinishTyping()) return;
        var next = ChosenSide == DialogueSide.Left ? DialogueSide.Right : DialogueSide.Left;
        SetChosen(("side", next == UsualSide(ChosenSpeakerId) ? null : JsonValue.Create(next == DialogueSide.Left ? "left" : "right")));
    }

    // ---- the chosen line's face ------------------------------------------------------------------

    /// <summary>
    /// A speaker's faces by name, in order, with the default one's place: the battle's own
    /// speaker's pictures ("" is its main picture), or a game character's Emotions. Null while
    /// they can't be known yet, with why.
    /// </summary>
    private static List<string>? FaceNames(string speaker, out int usual, out string why)
    {
        usual = 0;
        why = "";
        switch (WhoIs(speaker))
        {
            case Who.Custom:
                var names = new List<string> { "" };
                names.AddRange(draft!.SpeakerExpressions(speaker.Trim()).Select(f => f.Name));
                return names;
            case Who.Narrator:
                why = "no picture";
                return null;
        }
        if (GameCharacters() == null)
        {
            why = "the game's characters aren't loaded yet";
            return null;
        }
        if (FindGame(speaker) is not { } character)
        {
            why = "no such character";
            return null;
        }
        var faces = FacesOf(character.Id);
        if (faces.Names == null)
        {
            why = faces.Problem ?? "Loading...";
            return null;
        }
        usual = Math.Max(0, faces.Names.FindIndex(n => n.Equals("Neutral1", StringComparison.OrdinalIgnoreCase)));
        if (faces.Names.Count == 0) why = "no faces";
        return faces.Names.Count == 0 ? null : faces.Names;
    }

    // Where the chosen line's face is among its speaker's: the default one when it names none, -1 when it names one they don't have.
    private static int ChosenFaceIndex(List<string> names, int usual)
    {
        string? face = LineValue("expression")?.Trim();
        if (string.IsNullOrEmpty(face)) return usual;
        return names.FindIndex(n => n.Equals(face, StringComparison.OrdinalIgnoreCase));
    }

    private static string FaceLabel(string name) => name.Length == 0 ? "main picture" : name;

    // "Determined1 (12 of 74)".
    private static string LineFaceText()
    {
        var names = FaceNames(ChosenSpeakerId, out int usual, out string why);
        if (names == null) return $"<color=#9D92B4>{Escape(why)}</color>";
        int at = ChosenFaceIndex(names, usual);
        if (at < 0) return $"<color=#F2B02E>{Escape(LineValue("expression")!.Trim())}</color> <color=#9D92B4>(not one of theirs)</color>";
        return $"{Escape(FaceLabel(names[at]))} <color=#9D92B4>({at + 1} of {names.Count})</color>";
    }

    private static void StepLineFace(int by)
    {
        if (!HasChosenLine || !FinishTyping()) return;
        var names = FaceNames(ChosenSpeakerId, out int usual, out string why);
        if (names == null) { Say($"{SpeakerLabel(ChosenSpeakerId)}: {why}.", 3f); return; }
        int at = ChosenFaceIndex(names, usual);
        if (at < 0) at = usual;
        SetLineFace(names, usual, ((at + by) % names.Count + names.Count) % names.Count);
    }

    // The default face is written as no expression, so the line follows the speaker's.
    private static void SetLineFace(List<string> names, int usual, int at) =>
        SetChosen(("expression", at == usual || names[at].Length == 0 ? null : JsonValue.Create(names[at])));

    /// <summary>Clicking the face: every face the speaker has, with the highlighted one beside the list.</summary>
    private static void ChooseLineFace()
    {
        if (!HasChosenLine || !FinishTyping() || !DialogueEditable()) return;
        string speaker = ChosenSpeakerId;
        var names = FaceNames(speaker, out int usual, out string why);
        if (names == null) { Say($"{SpeakerLabel(speaker)}: {why}.", 3f); return; }
        var section = ShownSection;
        int line = chosenLine;
        var files = WhoIs(speaker) == Who.Custom ? FaceFiles(speaker.Trim()) : null;
        ShowPicker(new Picker
        {
            Heading = $"{SpeakerLabel(speaker)}: which face?",
            Rows = names.Select((n, i) => FaceLabel(n) + (i == usual ? "  (the usual one)" : "") + (files != null ? $"   {files[i]}" : "")).ToList(),
            Index = Math.Max(0, ChosenFaceIndex(names, usual)),
            Jump = true,
            Hint = _ => "Click a face, or Up/Down and Enter.  Esc goes back.",
            Face = i => FaceSprite(speaker, i >= 0 && i < names.Count ? names[i] : null),
            FaceNote = i => FaceNote(speaker),
            Choose = i =>
            {
                if (draft == null || !draft.IsLine(section, line)) return;
                chosenLine = line;
                SetLineFace(names, usual, i);
                BackFromPicker();
            },
            Back = BackFromPicker,
        });
    }

    // A speaker of the battle's own: its files, the main picture first (as FaceNames lists them).
    private static List<string> FaceFiles(string key)
    {
        var files = new List<string> { draft!.SpeakerText(key, "portrait") ?? "(no picture)" };
        files.AddRange(draft.SpeakerExpressions(key).Select(f => f.File));
        return files;
    }

    // ---- the chosen line's speaker ---------------------------------------------------------------

    private sealed class SpeakerChoice
    {
        internal string Label = "";
        internal string Id = "";
        internal bool New, Player;
    }

    /// <summary>
    /// The speaker picker's rows: New speaker..., the player's character ("Karma (the player)",
    /// there even before the game's characters load), the battle's own speakers, the other game
    /// characters its lines already use, the Narrator, then every other game character with
    /// faces, A to Z. Without <paramref name="all"/> (the chart editor's short list), all but
    /// New speaker... and the A to Z.
    /// </summary>
    private static List<SpeakerChoice> SpeakerChoices(bool all = true)
    {
        var list = new List<SpeakerChoice>();
        if (all) list.Add(new SpeakerChoice { Label = "New speaker...", New = true });
        // Not when a speaker of the battle's own has her id (written by hand): that key wins.
        if (WhoIs(DialogueReader.Player) == Who.Game)
            list.Add(new SpeakerChoice { Label = $"{GameName(PlayerId)} (the player)", Id = PlayerId, Player = true });
        foreach (var key in draft!.SpeakerKeys()) list.Add(new SpeakerChoice { Label = $"{SpeakerName(key)} (yours)", Id = key });
        var used = new List<string>();
        foreach (DialogueSection section in Enum.GetValues(typeof(DialogueSection)))
            for (int i = 0; i < draft.LineCount(section); i++)
                if (draft.LineText(section, i, "speaker")?.Trim() is { Length: > 0 } id && WhoIs(id) == Who.Game && !DialogueReader.IsPlayer(id)
                    && !used.Contains(FindGame(id)?.Id ?? id, StringComparer.OrdinalIgnoreCase))
                    used.Add(FindGame(id)?.Id ?? id);
        foreach (var id in used) list.Add(new SpeakerChoice { Label = GameLabel(id), Id = id });
        list.Add(new SpeakerChoice { Label = "Narrator (no picture)", Id = DialogueReader.Narrator });
        if (all && GameCharacters() is { } everyone)
            foreach (var c in everyone.Where(c => c.Portrait && !DialogueReader.IsPlayer(c.Id))
                         .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal))
                list.Add(new SpeakerChoice { Label = GameLabel(c.Id), Id = c.Id });
        return list;
    }

    // A game character's name, with its id when that's different ("Yako (Fox)").
    private static string GameLabel(string id)
    {
        string name = GameName(id);
        return name.Equals(id, StringComparison.OrdinalIgnoreCase) ? name : $"{name} ({id})";
    }

    private static void ChooseLineSpeaker()
    {
        if (!HasChosenLine || !FinishTyping() || !DialogueEditable()) return;
        var section = ShownSection;
        int line = chosenLine;
        var choices = SpeakerChoices();
        string current = FindGame(ChosenSpeakerId)?.Id ?? ChosenSpeakerId;
        int index = choices.FindIndex(c => !c.New && c.Id.Equals(current, StringComparison.OrdinalIgnoreCase));
        bool loaded = GameCharacters() != null;
        ShowPicker(new Picker
        {
            Heading = "Who says this line?",
            Rows = choices.Select(c => c.Label).ToList(),
            Index = Math.Max(0, index),
            Jump = true,
            Hint = i => SpeakerChoiceHint(i >= 0 && i < choices.Count ? choices[i] : null) +
                        (loaded || (i >= 0 && i < choices.Count && choices[i].Player) ? "" : " The game's characters aren't loaded yet. Try again in a moment.") +
                        "  Typing jumps to a name.  Esc goes back.",
            Face = i => i >= 0 && i < choices.Count && !choices[i].New ? FaceSprite(choices[i].Id, null) : null,
            FaceNote = i => i >= 0 && i < choices.Count && !choices[i].New ? FaceNote(choices[i].Id) : "",
            Choose = i =>
            {
                if (draft == null || !draft.IsLine(section, line)) return;
                chosenLine = line;
                var choice = choices[i];
                if (choice.New)
                {
                    NewSpeaker(forLine: true);
                    return;
                }
                // Faces belong to their speaker, so the line starts on the new one's usual face.
                SetChosen(("speaker", JsonValue.Create(choice.Id)), ("expression", null));
                lastSpeaker = choice.Id;
                BackFromPicker();
            },
            Back = BackFromPicker,
        });
    }

    private static string SpeakerChoiceHint(SpeakerChoice? c)
    {
        if (c == null) return "";
        if (c.New) return "Pick a picture for a speaker of your own, then type its name.";
        if (c.Player) return "The player's character, with the game's faces. She stands on the left.";
        return WhoIs(c.Id) switch
        {
            Who.Custom => "Your own speaker. Its pictures, side and name are on the Speakers tab.",
            Who.Narrator => "The narration box: no name and no picture.",
            _ => $"The game's own character ({c.Id}), with its faces.",
        };
    }

    // ---- the chosen line's fields ----------------------------------------------------------------

    private static readonly TextField LineNameField = new()
    {
        Label = "Name shown",
        Max = DialogueReader.MaxName,
        Get = () => LineValue("name") ?? "",
        Set = text => SetChosen(("name", CleanName(text) is { Length: > 0 } name ? JsonValue.Create(name) : null)),
        Empty = () => $"({SpeakerLabel(ChosenSpeakerId)}'s own name)",
        Refused = BoxRefused,
        RefusedText = BoxRefusedText,
        Hint = "Type the name this line shows, then Enter. Empty shows the speaker's own. Esc cancels.",
    };

    private static readonly TextField LineTextField = new()
    {
        Label = "Text",
        Max = DialogueReader.MaxText,
        Wrap = true,
        Get = () => LineValue("text") ?? "",
        Set = text => SetChosen(("text", JsonValue.Create(DialogueReader.CleanText(text, out _)))),
        Empty = () => "(no text yet: the line is left out until it has some)",
        Refused = BoxRefused,
        RefusedText = BoxRefusedText,
        Measure = text => TooLongForBox(text) ? "too long for the box" : "",
        Hint = "Type the line, then Enter. Esc cancels.",
    };

    private static readonly TextField WhenField = new()
    {
        Label = "When",
        Max = 20,
        Get = () => LineNumber("beat") is double beat ? "beat " + Num(beat) : LineNumber("time") is double time ? Num(time) : "",
        Set = SetWhen,
        Empty = () => "(no time)",
        Hint = "Type a time like 1:02.5 or 62.5, or a beat like beat 96, then Enter. Esc cancels.",
    };

    private static readonly TextField ShowsForField = new()
    {
        Label = "Shows for",
        Max = 8,
        Get = () => LineNumber("duration") is double d ? Num(d) : "",
        Set = text => SetChosen(("duration", BattleDraft.ArtNumberNode(ParseNumber(text, DialogueReader.MinDuration, DialogueReader.MaxDuration, "How long it shows") is double s ? Math.Round(s, 2) : null))),
        Empty = () => $"(about {Num(Math.Round(DialogueReader.LiveSeconds(DialogueReader.CleanText(LineValue("text"), out _)), 1))} s, from its length)",
        Hint = "Type how many seconds the box shows, 1 to 15, then Enter. Empty works it out from the text's length. Esc cancels.",
    };

    private static readonly TextField AfterField = new()
    {
        Label = "After",
        Max = 8,
        Get = () => LineNumber("duration") is double d ? Num(d) : "",
        Set = text => SetChosen(("duration", BattleDraft.ArtNumberNode(ParseNumber(text, DialogueReader.MinDuration, DialogueReader.MaxDuration, "The time") is double s ? Math.Round(s, 2) : null))),
        Empty = () => "(waits for a key)",
        Hint = "Type after how many seconds the line goes on by itself, 1 to 15, then Enter. A key can't skip it. Empty waits for a key. Esc cancels.",
    };

    // Seconds the last "Goes on by itself" had, for the next one turned on.
    private static double lastGoesOn = 3;

    private static void ToggleGoesOn()
    {
        if (!HasChosenLine || !FinishTyping()) return;
        if (LineNumber("duration") is double d)
        {
            lastGoesOn = d;
            SetChosen(("duration", null));
            Say("It waits for a key, like the game's own lines.", 3f);
            return;
        }
        SetChosen(("duration", BattleDraft.ArtNumberNode(lastGoesOn)));
        Say($"It goes on by itself after {Num(lastGoesOn)} s. A key can't skip it.", 4f);
    }

    private static void ToggleStops()
    {
        if (!HasChosenLine || !FinishTyping()) return;
        bool now = !ChosenStops();
        SetChosen(("pause", now ? JsonValue.Create(true) : null));
        Say(now ? "Stops the song: on. The song waits here for a key, like a boss's talk between songs." : "Stops the song: off. The box comes and goes while the notes keep coming.", 5f);
    }

    // "1:02.5", "62.5" or "beat 96". A time or a beat is written as typed; the other goes.
    private static void SetWhen(string text)
    {
        string t = text.Trim().ToLowerInvariant();
        if (t.Length == 0) throw new InvalidDataException("A line during the song needs a time, like 1:02.5 or 62.5, or a beat, like beat 96.");
        if (t.StartsWith("b"))
        {
            double beat = ParseNumber(t.TrimStart('b', 'e', 'a', 't', ' '), 0, 100000, "The beat") ?? throw new InvalidDataException("Type the beat's number, like beat 96.");
            SetChosen(("beat", BattleDraft.ArtNumberNode(Math.Round(beat, 3))), ("time", null));
            return;
        }
        double seconds;
        int colon = t.IndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(t.Substring(0, colon).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) || minutes < 0 || minutes > 60)
                throw new InvalidDataException("That isn't a time. Type one like 1:02.5 (a minute and 2.5 seconds).");
            seconds = minutes * 60 + (ParseNumber(t.Substring(colon + 1), 0, 59.999, "The seconds after the colon") ?? 0);
        }
        else seconds = ParseNumber(t, 0, 3600, "The time") ?? 0;
        SetChosen(("time", BattleDraft.ArtNumberNode(Math.Round(seconds, 3))), ("beat", null));
    }

    // "0:42.50 (beat 85.0)".
    private static string WhenText()
    {
        if (ChosenTime() is not double time) return "(no time: the line is left out)";
        double beat = LineNumber("beat") ?? dialogueTiming.SecondsToBeat(time);
        return $"{DialogueReader.Clock(time)} (beat {beat.ToString("0.0", CultureInfo.InvariantCulture)})";
    }

    // What the box can show of a name: one line without < > { }, at most 24 letters.
    private static string CleanName(string text)
    {
        string name = DialogueReader.CleanText(text, out _);
        return name.Length > DialogueReader.MaxName ? name.Substring(0, DialogueReader.MaxName).TrimEnd() : name;
    }

    // Under the chosen line: how a picture of the battle's own speaker shows.
    private static string LineNote()
    {
        if (!HasChosenLine || ChosenWho != Who.Custom) return "";
        return Escape(ShownSizeText(ChosenSpeakerId.Trim()));
    }

    // ---- the Speakers tab ------------------------------------------------------------------------

    private static bool HasChosenSpeaker => draft != null && chosenSpeaker != null && draft.SpeakerKeys().Contains(chosenSpeaker, StringComparer.OrdinalIgnoreCase);

    private static List<(string Name, string File)> ChosenFaces() =>
        draft != null && chosenSpeaker != null ? draft.SpeakerExpressions(chosenSpeaker) : new List<(string, string)>();

    private static string FaceRowText(int row)
    {
        var faces = ChosenFaces();
        int index = faceFirst + row;
        if (index >= faces.Count) return "";
        if (typing == FaceNameField && index == chosenFace) return $"Name: {Escape(typed)}_";
        var (name, file) = faces[index];
        string usable = chosenSpeaker != null && dialogueRead?.FindSpeaker(chosenSpeaker)?.Expression(name) == null ? "   <color=#F2B02E>(can't be used: see below)</color>" : "";
        return $"{Escape(name)}   <color=#9D92B4>{Escape(file)}</color>{usable}";
    }

    private static void ChooseFaceRow(int row)
    {
        if (!FinishTyping()) return;
        int index = faceFirst + row;
        if (index < ChosenFaces().Count) chosenFace = index;
    }

    private static string SpeakerPictureText()
    {
        if (draft == null || chosenSpeaker == null) return "";
        string file = draft.SpeakerText(chosenSpeaker, "portrait") ?? "";
        if (file.Trim().Length == 0) return "<color=#F2B02E>no picture yet</color>";
        var face = dialogueRead?.FindSpeaker(chosenSpeaker)?.Portrait;
        return Escape(face != null ? $"{file}   {face.Width} x {face.Height}" : file);
    }

    // Under the speaker: how its picture shows, and what to keep in mind drawing one.
    private static string SpeakerNote()
    {
        if (chosenSpeaker == null) return "";
        return Escape(ShownSizeText(chosenSpeaker) + " The bottom of the picture goes off the screen like the game's portraits do. Keep the face in the top half.");
    }

    /// <summary>"Shown at 150 x 190 game pixels (scaled down from 600 x 760).", for a speaker of the battle's own.</summary>
    private static string ShownSizeText(string key)
    {
        var face = dialogueRead?.FindSpeaker(key)?.Portrait;
        if (face == null) return "";
        var (w, h, k, _) = DialogueReader.ShownSize(face.Width, face.Height);
        string shown = $"Shown at {Num(Math.Round(w))} x {Num(Math.Round(h))} game pixels";
        if (k < 1) return $"{shown} (scaled down from {face.Width} x {face.Height}).";
        if (k > 1) return $"{shown} ({Num(k)}x from {face.Width} x {face.Height}, pixels kept sharp).";
        return $"{shown}, pixel for pixel.";
    }

    private static readonly TextField SpeakerNameField = new()
    {
        Label = "Name",
        Max = DialogueReader.MaxName,
        Get = () => chosenSpeaker == null ? "" : draft?.SpeakerText(chosenSpeaker, "name") ?? "",
        Set = text =>
        {
            string name = CleanName(text);
            if (name.Length == 0) throw new InvalidDataException("A speaker needs a name: it's shown in the box's name tag.");
            if (HasChosenSpeaker && DialogueEditable()) draft!.SetSpeaker(chosenSpeaker!, ("name", JsonValue.Create(name)));
        },
        Refused = BoxRefused,
        RefusedText = BoxRefusedText,
        Hint = "Type the name the box shows, then Enter. Esc cancels.",
    };

    private static readonly TextField FaceNameField = new()
    {
        Label = "Name",
        Max = DialogueReader.MaxName,
        Get = () => chosenFace >= 0 && chosenFace < ChosenFaces().Count ? ChosenFaces()[chosenFace].Name : "",
        Set = text =>
        {
            var faces = ChosenFaces();
            if (draft == null || chosenSpeaker == null || chosenFace < 0 || chosenFace >= faces.Count || !DialogueEditable()) return;
            string name = CleanName(text);
            string old = faces[chosenFace].Name;
            if (name.Length == 0) throw new InvalidDataException("An expression needs a name, like angry. Lines name it to show it.");
            if (faces.Where((_, i) => i != chosenFace).Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"{SpeakerName(chosenSpeaker)} already has an expression called {name}.");
            if (name != old) draft.RenameSpeakerExpression(chosenSpeaker, old, name);
        },
        Refused = BoxRefused,
        RefusedText = BoxRefusedText,
        Hint = "Type the expression's name, like angry, then Enter. Its lines keep it. Esc cancels.",
    };

    private static void ToggleSpeakerSide()
    {
        if (!HasChosenSpeaker || !FinishTyping() || !DialogueEditable()) return;
        draft!.SetSpeaker(chosenSpeaker!, ("side", JsonValue.Create(SpeakerSide(chosenSpeaker!) == DialogueSide.Left ? "right" : "left")));
    }

    private static void ToggleSpeakerMirror()
    {
        if (!HasChosenSpeaker || !FinishTyping() || !DialogueEditable()) return;
        bool now = !draft!.SpeakerFlag(chosenSpeaker!, "flip");
        draft.SetSpeaker(chosenSpeaker!, ("flip", now ? JsonValue.Create(true) : null));
        if (now) Say("Mirrored. The game already turns pictures on the right to face the middle; this is for art drawn facing the other way.", 5f);
    }

    private static (double X, double Y) SpeakerNudge() => chosenSpeaker == null ? (0, 0) : draft?.SpeakerPair(chosenSpeaker, "offset") ?? (0, 0);

    private static void StepNudge(int x, int y)
    {
        var (nx, ny) = SpeakerNudge();
        SetNudge(nx + x, ny + y);
    }

    private static void SetNudge(double x, double y)
    {
        if (!HasChosenSpeaker || !DialogueEditable()) return;
        x = Math.Clamp(Math.Round(x), -DialogueReader.MaxOffset, DialogueReader.MaxOffset);
        y = Math.Clamp(Math.Round(y), -DialogueReader.MaxOffset, DialogueReader.MaxOffset);
        draft!.SetSpeaker(chosenSpeaker!, ("offset", x == 0 && y == 0 ? null : BattleDraft.ArtPairNode((x, y))));
    }

    private static TextField NudgeField(string label, Func<string> get, Action<double> set, string hint)
    {
        var field = new TextField { Label = label, Max = 8, Get = get, Hint = hint };
        field.Set = text => set(ParseNumber(text, -DialogueReader.MaxOffset, DialogueReader.MaxOffset, label) ?? 0);
        return field;
    }

    private static readonly TextField NudgeXField = NudgeField("Nudge sideways", () => Num(SpeakerNudge().X), v => SetNudge(v, SpeakerNudge().Y),
        "Type how many game pixels to move the picture toward its side's edge of the screen (toward the middle is negative), then Enter. Esc cancels.");
    private static readonly TextField NudgeYField = NudgeField("Nudge up/down", () => Num(SpeakerNudge().Y), v => SetNudge(SpeakerNudge().X, v),
        "Type how many game pixels to move the picture up (down is negative), then Enter. Esc cancels.");

    // ---- speakers' pictures --------------------------------------------------------------------

    private sealed class AddedPicture
    {
        internal string? Problem;
        internal string Path = "";
        internal bool Copied;
    }

    // On a worker: a picked picture is checked from its first bytes as the loader checks it (PNG
    // or JPEG, at most 4 MB and 2048 px a side), then copied into the battle's portraits folder.
    private static AddedPicture AddPortraitFile(string folder, string source)
    {
        var file = new FileInfo(source);
        if (!file.Exists) return new AddedPicture { Problem = $"{file.Name} is missing." };
        if (file.Length > DialogueReader.MaxPortraitBytes)
            return new AddedPicture { Problem = $"{file.Name} is {(file.Length / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture)} MB; speaker pictures can be at most {DialogueReader.MaxPortraitBytes / (1024 * 1024)} MB." };
        byte[] head;
        using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            head = ReadHead(stream, (int)Math.Min(DialogueReader.HeadBytes, file.Length));
        if (MediaSniff.PictureProblem(head, DialogueReader.MaxPortraitSide, "speaker pictures") is { } why)
            return new AddedPicture { Problem = $"{file.Name} {why}. Speaker pictures are PNG or JPEG files." };
        var (path, copied) = BattleFiles.AddFile(folder, source, "portraits", DialogueReader.MaxPortraitBytes);
        return new AddedPicture { Path = path, Copied = copied };
    }

    /// <summary>The Windows picker for a speaker's picture, then the checks and the copy; <paramref name="done"/> gets its path inside the battle.</summary>
    private static void PickPortrait(string title, Action<BattleDraft, string> done)
    {
        if (draft == null || !FinishTyping() || !DialogueEditable()) return;
        var d = draft;
        Run(FileDialogs.Open(FileDialogs.Purpose.Portraits, title), "Choose a picture in the window that opened...", path =>
        {
            if (path == null || draft != d) return;
            string folder = d.Folder;
            Run(Task.Run(() => AddPortraitFile(folder, path)), "Copying the picture...", added =>
            {
                if (draft != d) return;
                if (added.Problem != null)
                {
                    ModLog.Info($"Battle creator: {path} wasn't used as a speaker's picture: {added.Problem}");
                    Say(added.Problem, 8f);
                    return;
                }
                if (!DialogueEditable()) return;
                if (added.Copied) touched.Add(added.Path);
                ModLog.Info($"Battle creator: speaker picture {added.Path} for {d.Folder} (from {path}).");
                done(d, added.Path);
            });
        });
    }

    // "warden_angry.png" gives "Warden angry".
    private static string NameFromFile(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Replace('-', ' ').Trim();
        name = CleanName(string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        return name.Length == 0 ? "Speaker" : char.ToUpperInvariant(name[0]) + name.Substring(1);
    }

    /// <summary>
    /// New speaker...: its picture from the Windows picker, then its name (suggested from the
    /// file's). From a line's speaker picker, the line goes to the new speaker.
    /// </summary>
    private static void NewSpeaker(bool forLine)
    {
        if (draft == null) return;
        if (draft.SpeakerKeys().Count >= DialogueReader.MaxSpeakers)
        {
            Say($"A battle can have at most {DialogueReader.MaxSpeakers} speakers of its own.", 5f);
            if (forLine) BackFromPicker();
            return;
        }
        var section = ShownSection;
        int line = chosenLine;
        bool onLine = forLine && HasChosenLine;
        if (screen == Screen.Pick) BackFromPicker();
        PickPortrait("Choose the speaker's picture", (d, file) =>
        {
            string key = d.AddSpeaker(NameFromFile(file), file, GameCharacters()?.Select(c => c.Id));
            chosenSpeaker = key;
            chosenFace = -1;
            if (onLine && d.IsLine(section, line))
            {
                d.SetLine(section, line, ("speaker", JsonValue.Create(key)), ("expression", null));
                lastSpeaker = key;
            }
            StartTyping(SpeakerNameField);
            Say("New speaker. Type its name, then Enter. Save to keep it.", 3600f);
        });
    }

    private static void ChooseSpeakerPicture()
    {
        string? key = chosenSpeaker;
        if (key == null) return;
        PickPortrait($"Choose {SpeakerName(key)}'s picture", (d, file) =>
        {
            if (d.SpeakerText(key, "portrait") is { } old && !old.Equals(file, StringComparison.OrdinalIgnoreCase)) touched.Add(old);
            d.SetSpeaker(key, ("portrait", JsonValue.Create(file)));
            Say("Picture set. Save to keep it.", 4f);
        });
    }

    private static void AddSpeakerFace()
    {
        string? key = chosenSpeaker;
        if (key == null || draft == null) return;
        if (draft.SpeakerExpressions(key).Count >= DialogueReader.MaxExpressions)
        {
            Say($"A speaker can have at most {DialogueReader.MaxExpressions} expressions.", 4f);
            return;
        }
        PickPortrait($"Choose a picture for one of {SpeakerName(key)}'s expressions", (d, file) =>
        {
            // The file's name is the suggestion, made unique.
            var faces = d.SpeakerExpressions(key);
            string stem = CleanName(Path.GetFileNameWithoutExtension(file));
            if (stem.Length == 0) stem = "face";
            string name = stem;
            for (int n = 2; faces.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++) name = $"{stem} {n}";
            d.SetSpeakerExpression(key, name, file);
            chosenFace = d.SpeakerExpressions(key).FindIndex(f => f.Name == name);
            StartTyping(FaceNameField);
            Say("Expression added. Type its name (lines use it to show this face), then Enter. Save to keep it.", 3600f);
        });
    }

    private static void ChooseFacePicture()
    {
        string? key = chosenSpeaker;
        var faces = ChosenFaces();
        if (key == null || chosenFace < 0 || chosenFace >= faces.Count) return;
        var (name, oldFile) = faces[chosenFace];
        PickPortrait($"Choose {SpeakerName(key)}'s picture for {name}", (d, file) =>
        {
            if (!oldFile.Equals(file, StringComparison.OrdinalIgnoreCase)) touched.Add(oldFile);
            d.SetSpeakerExpression(key, name, file);
            Say("Picture set. Save to keep it.", 4f);
        });
    }

    private static void RemoveSpeakerFace()
    {
        var faces = ChosenFaces();
        if (draft == null || chosenSpeaker == null || chosenFace < 0 || chosenFace >= faces.Count || !FinishTyping() || !DialogueEditable()) return;
        var (name, file) = faces[chosenFace];
        touched.Add(file);
        draft.RemoveSpeakerExpression(chosenSpeaker, name);
        chosenFace = Math.Min(chosenFace, ChosenFaces().Count - 1);
        Say($"Removed {name}. Its lines show the main picture. Undo brings it back.", 4f);
    }

    private static void DeleteSpeaker()
    {
        if (draft == null || !HasChosenSpeaker || !FinishTyping() || !DialogueEditable()) return;
        var d = draft;
        string key = chosenSpeaker!, name = SpeakerName(key);
        int lines = d.LinesOf(key);
        if (lines == 0)
        {
            RemoveSpeaker(d, key, false);
            return;
        }
        ShowPicker(new Picker
        {
            Heading = $"{lines} line{(lines == 1 ? "" : "s")} use{(lines == 1 ? "s" : "")} {name}",
            Rows = { "Delete those lines too", "Give them to the Narrator", "Cancel" },
            Index = 2,
            Hint = i => i switch
            {
                0 => $"Deletes {name} and every line {name} says. Undo brings them back.  Esc goes back.",
                1 => $"Deletes {name}; the Narrator says those lines instead. Undo brings it back.  Esc goes back.",
                _ => $"Keeps {name}.  Esc goes back.",
            },
            Choose = i =>
            {
                if (i < 2 && draft == d) RemoveSpeaker(d, key, withLines: i == 0);
                BackFromPicker();
            },
            Back = BackFromPicker,
        });
    }

    private static void RemoveSpeaker(BattleDraft d, string key, bool withLines)
    {
        string name = SpeakerName(key);
        int lines = d.LinesOf(key);
        // Its pictures go to the Recycle Bin after a save, when nothing else uses them.
        if (d.SpeakerText(key, "portrait") is { } portrait) touched.Add(portrait);
        touched.AddRange(d.SpeakerExpressions(key).Select(f => f.File));
        d.RemoveSpeaker(key, withLines);
        RefreshDialogue();
        chosenSpeaker = speakerRows.FirstOrDefault();
        chosenFace = -1;
        Say(lines == 0 ? $"Deleted {name}. Undo brings it back."
            : withLines ? $"Deleted {name} and {lines} line{(lines == 1 ? "" : "s")}. Undo brings them back."
            : $"Deleted {name}; the Narrator says its {lines} line{(lines == 1 ? "" : "s")}. Undo brings it back.", 5f);
    }

    // ---- the chart editor ------------------------------------------------------------------------

    /// <summary>
    /// The chart editor's hold on the draft's lines during the song (see DialogueLink): it shows
    /// them, puts its changes in the draft, and its Save is this Save. Null when the dialogue
    /// can't be changed (the editor then shows the saved lines to look at).
    /// </summary>
    private static DialogueLink? DialogueLinkFor(BattleDraft d)
    {
        if (d.DialogueLocked != null) return null;
        return new DialogueLink
        {
            Get = () => draft == d ? d.DuringCues() : new List<DialogueCue>(),
            Set = cues =>
            {
                if (draft == d && d.DialogueLocked == null) d.SetDuringCues(cues);
            },
            Save = () => draft != d ? "the battle isn't open in the Battle creator any more" : Save() ? null : saveProblem ?? "the Battle creator couldn't save just now",
            NameOf = id => draft == d ? SpeakerLabel(id) : id,
            Speakers = () => draft == d ? SpeakerChoices(all: false).Select(c => (c.Id, c.Label)).ToList() : new List<(string, string)>(),
            ShowInCreator = index =>
            {
                if (draft != d) return;
                // The line shows on the Dialogue page once the creator is back.
                StopDialoguePlay();
                dialogueTab = DialogueTab.During;
                chosenLine = index;
                followedRow = -1;
                RefreshRows();
                SetPage(Page.Dialogue);
            },
        };
    }

    // ---- tests -----------------------------------------------------------------------------------

    private static bool AnythingCharted() => charts != null && charts.Notes.Any(n => n > 0);

    /// <summary>
    /// Test in a battle (from the start, with the lines before the fight) or Test from this line
    /// (two bars before it): the chart editor opens on the first charted difficulty, plays the
    /// test with the draft as it is, and closes itself when it ends.
    /// </summary>
    private static void TestDialogue(bool fromLine)
    {
        if (draft == null || !FinishTyping()) return;
        StopDialoguePlay();
        double at = 0;
        if (fromLine)
        {
            if (ChosenTime() is not double time) return;
            at = time;
        }
        EditCharts(-1, at);
    }
}
