using UnityEngine;
using UnityEngine.UI;
using static NocturnePlus.EditorInput;
using static NocturnePlus.EditorPageKit;
using static NocturnePlus.EditorUi;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;

namespace NocturnePlus;

/// <summary>
/// The Custom note colors page, opened from its row under Options > Gameplay > Note Colors: a
/// list of the player's palettes (and New palette, which starts from a copy of any palette), a
/// palette's page (its name, its five lanes, use it, copy, delete) and a lane's page (its color as
/// hex, hue, saturation and brightness, presets, its accents and line work, a copy of another
/// lane's). A big preview draws the notes in the current skin next to a mine, and says when a lane
/// could be taken for a mine or is hard to see on the lanes. It works with the mouse, the keyboard
/// (Up/Down and Enter, Left/Right on a slider, Esc back) and a pad; the menus underneath are locked
/// while it's open (EditorOverlay). Changes are saved a second after they're made, and on the way out.
/// </summary>
internal static partial class CustomNoteColors
{
    private enum Screen { List, Palette, Lane }

    // Names the page to EditorOverlay, which keeps the menus locked while it is open.
    private static readonly object OverlayOwner = "custom note colors";
    private const float TopH = 64f, RowsX = 60f, RowsW = 760f, PreviewX = 900f, PreviewW = 960f, PreviewH = 470f;

    private static EditorUi? ui;
    private static EditorPageKit? kit;
    private static EditorUi Ui => ui ?? throw new InvalidOperationException("the custom note colors page isn't open");
    private static EditorPageKit Kit => kit ?? throw new InvalidOperationException("the custom note colors page isn't open");
    private static Screen screen;
    private static Palette? editing;
    private static int editingLane, focus = -1, listIndex;
    // The lane page's hue, saturation and brightness: kept apart from the color, so hue still moves on a gray.
    private static float hue, saturation, brightness;
    private static RectTransform? editPanel, paletteRows, laneRows;
    private static readonly List<Control> paletteControls = new(), laneControls = new();
    private static readonly Dictionary<Control, Action<int>> sliders = new();
    private static readonly List<(Image Image, Func<Color?> Color)> swatches = new();
    private static PalettePreview? pagePreview, listPreview;
    private static TMP_Text? titleText, warningText;
    // What the list screen's preview shows now: a palette's lanes and one to mark (-1: none), or nothing.
    private static Func<(CombatNoteColorSet[]? Sets, int Lane)>? listShows;
    private static bool dirty;
    private static float saveAt;
    private static int sideHeld;
    private static float sideRepeatAt;

    /// <summary>Whether the page is open.</summary>
    internal static bool PageOpen => ui != null && ui.IsAlive;

    /// <summary>The page reads the pad while it's open.</summary>
    internal static bool WantsPad => PageOpen;

    // The row under the game's Note Colors row that opens the page.
    internal static readonly OptionsMenuIntegration.RowSpec[] Rows =
    {
        new("StateToggle_PlusCustomNoteColors",
            "Custom note colors",
            "Make your own palettes for Note Colors, with any color on each lane. They're listed there after the game's own.",
            () => new string[] { RowValue() },
            () => 0,
            (direction, click) => { if (click && CustomChartOptions.ActionAllowed("StateToggle_PlusCustomNoteColors")) Open(); }),
    };

    // Why the row's click didn't open the page, shown as its value for a moment.
    private static string rowNote = "";
    private static float rowNoteUntil;

    private static string RowValue() =>
        Time.unscaledTime < rowNoteUntil ? rowNote : palettes.Count == 0 ? "Open..." : $"Open... ({palettes.Count})";

    // ---- opening and closing ----------------------------------------------------------------------

    internal static void Open()
    {
        if (PageOpen) return;
        if (EditorOverlay.IsOpen)
        {
            // Another of the mod's pages has the screen, or a chart editor test is being played.
            rowNote = TestPlay.Active ? "Not during a test" : "Close the other page first";
            rowNoteUntil = Time.unscaledTime + 3f;
            OptionsMenuIntegration.RefreshAll();
            ModLog.Info("Custom note colors: not opened, another of the mod's pages is open.");
            return;
        }
        try
        {
            Load();
            EnsureRegistered();
            ui = new EditorUi("NocturnePlus Custom Note Colors", blockGameClicks: true);
            kit = new EditorPageKit(ui, "Custom note colors") { CanType = () => editing != null && !ReadOnly };
            ui.BuildList();
            BuildListSide();
            BuildEdit();
            EditorOverlay.Enter(OverlayOwner);
            kit.IgnoreKeysNow();
            ShowList(0);
            string? message = ReadProblem != null
                ? $"{FileName} couldn't be read (see the log). Making a palette starts a new file; the old one is kept as {FileName}.bad."
                : ReadOnly ? $"{FileName} was made by a newer version of the mod: its palettes can be used, but not changed here."
                : SkippedOnLoad > 0 ? $"{SkippedOnLoad} palettes in {FileName} couldn't be used (see the log). The file is kept as it was, as a .bak copy, when you next change a palette."
                : null;
            if (message != null) ui.Say(message, 12f);
            ModLog.Info($"Custom note colors: opened, {palettes.Count} palettes.");
        }
        catch (Exception ex)
        {
            ModLog.Error("Custom note colors: opening the page failed: " + ex);
            Close("it failed to open");
        }
    }

    private static void Close(string reason)
    {
        if (ui == null) return;
        try { kit?.EndTyping(); } catch { }
        SaveNow();
        ui.Destroy();
        ui = null;
        kit = null;
        editing = null;
        pagePreview = listPreview = null;
        titleText = warningText = null;
        editPanel = paletteRows = laneRows = null;
        paletteControls.Clear();
        laneControls.Clear();
        sliders.Clear();
        swatches.Clear();
        listShows = null;
        // Gives the cursor back; the menus come back once the key that closed the page is let go.
        EditorOverlay.Leave(OverlayOwner);
        EnsureRegistered();
        OptionsMenuIntegration.RefreshAll();
        OptionsMenuIntegration.RefreshPreviews();
        ModLog.Info($"Custom note colors: closed ({reason}).");
    }

    /// <summary>Called every frame, with the mod's other pages.</summary>
    internal static void PageUpdate()
    {
        EditorOverlay.Update();
        try
        {
            if (dirty && Time.unscaledTime >= saveAt) SaveNow();
            if (ui == null) return;
            // Only something else destroying the canvas gets here: close properly, or the menus stay locked.
            if (!ui.IsAlive)
            {
                ModLog.Error("Custom note colors: its screen was destroyed from outside; closing it.");
                Close("its screen was destroyed");
                return;
            }
            var keyboard = InputKeyboard.current;
            if (keyboard == null) return;
            var mouse = InputMouse.current;
            if (screen == Screen.List) UpdateList(keyboard);
            else UpdateRows(keyboard, mouse);
        }
        catch (Exception ex)
        {
            ModLog.Error("Custom note colors: the page failed, so it closes: " + ex);
            try { if (ui != null) Close("it failed"); }
            catch (Exception again) { ModLog.Error("Custom note colors: closing it failed too: " + again.Message); }
        }
    }

    private static void Touch()
    {
        dirty = true;
        saveAt = Time.unscaledTime + 1f;
    }

    private static void SaveNow()
    {
        if (!dirty) return;
        if (Save(out var problem))
        {
            dirty = false;
            return;
        }
        // Tried again in a few seconds (a newer version's file is never written, so that stops here).
        if (ReadOnly) dirty = false;
        else saveAt = Time.unscaledTime + 5f;
        if (problem != null) ui?.Say(problem, 8f);
    }

    // ---- the list screens: the palettes, and the pickers ---------------------------------------------

    // The list screen's button for the mouse (Close on the palettes, Back on a picker), and the preview beside the list.
    private static void BuildListSide()
    {
        var b = Ui.MakeButton(Ui.ListPanel!, "", () =>
        {
            if (!Kit.Live || !Kit.ClicksLive || Kit.Picking is not { } p) return;
            Kit.Picking = null;
            p.Back();
        });
        b.Text = () => (Kit.Picking?.Heading == ListHeading ? "Close" : "Back") + " <size=65%><color=#9D92B4>Esc</color></size>";
        Place(b.Rect, new Vector2(1, 0), new Vector2(-16, 20), new Vector2(170, 52), new Vector2(1, 0));
        var box = MakeImage("PreviewBox", Ui.ListPanel!, PanelColor).rectTransform;
        Place(box, new Vector2(0.5f, 0.5f), new Vector2(785, 60), new Vector2(330, 440));
        listPreview = new PalettePreview(box, 1.45f, 300f);
    }

    private const string ListHeading = "Custom note colors";

    private static void ShowPickerScreen(Picker picker, Func<(CombatNoteColorSet[]? Sets, int Lane)> shows)
    {
        screen = Screen.List;
        Kit.EndTyping();
        Kit.DelayClicks();
        Ui.ListPanel!.gameObject.SetActive(true);
        editPanel!.gameObject.SetActive(false);
        listShows = shows;
        Kit.ShowPicker(picker);
    }

    private static void ShowList(int index)
    {
        editing = null;
        var rows = new List<string> { "+ New palette" };
        rows.AddRange(palettes.Select(p => IsPicked(p) ? p.Name + "   (in use)" : p.Name));
        listIndex = Math.Clamp(index, 0, rows.Count - 1);
        ShowPickerScreen(new Picker
        {
            Heading = ListHeading,
            Rows = rows,
            Index = listIndex,
            Hint = i => i == 0
                ? palettes.Count >= MaxPalettes ? $"You have {MaxPalettes} palettes, the most there can be. Delete one to make another." : "Make a new palette, starting as a copy of any palette."
                : ReadOnly ? $"Enter uses {palettes[i - 1].Name} for your notes." : $"Enter changes {palettes[i - 1].Name}. Your palettes are listed in Options > Gameplay > Note Colors.",
            Choose = i =>
            {
                if (i == 0) NewPalette();
                else if (ReadOnly) { Pick(palettes[i - 1]); Say($"Your notes use {palettes[i - 1].Name} now."); ShowList(i); }
                else OpenPalette(palettes[i - 1]);
            },
            Back = () => Close("closed"),
        }, () =>
        {
            int i = Kit.Picking?.Index ?? 0;
            return i >= 1 && i <= palettes.Count ? (palettes[i - 1].Sets, -1) : (null, -1);
        });
    }

    private static void UpdateList(InputKeyboard keyboard)
    {
        var step = Kit.UpdatePicker(keyboard);
        if (!PageOpen || screen != Screen.List) return;
        // A choice that opened nothing else leaves no picker: back to the palettes.
        if (step != PickerStep.Shown && Kit.Picking == null) { ShowList(listIndex); return; }
        if (listShows != null)
        {
            var (sets, lane) = listShows();
            listPreview?.Draw(sets, lane);
        }
    }

    private static void NewPalette()
    {
        if (ReadOnly) { Say($"{FileName} was made by a newer version of the mod, so palettes can't be made here."); ShowList(0); return; }
        if (palettes.Count >= MaxPalettes) { Say($"You have {MaxPalettes} palettes, the most there can be."); ShowList(0); return; }
        string current = CurrentId();
        var sources = new List<(string Label, string Id)> { ($"The note colors in use now ({current})", current) };
        foreach (var (id, _) in GameStyles()) sources.Add((id, id));
        ShowPickerScreen(new Picker
        {
            Heading = "Start from which colors?",
            Rows = sources.Select(s => s.Label).ToList(),
            Hint = _ => "The new palette starts as a copy of these. Change any lane after.",
            Choose = i =>
            {
                var palette = new Palette { Name = FreeName("My colors") };
                var lanes = LanesShown(sources[i].Id) ?? DefaultLanes();
                for (int l = 0; l < LaneCount; l++) palette.Lanes[l] = lanes[l];
                Add(palette);
                Touch();
                SaveNow();
                ModLog.Info($"Custom note colors: made {palette.Name} from {sources[i].Id}.");
                OpenPalette(palette);
                Say($"Made {palette.Name}. Pick a lane to change its color.");
            },
            Back = () => ShowList(0),
        }, () =>
        {
            int i = Kit.Picking?.Index ?? 0;
            var lanes = i >= 0 && i < sources.Count ? LanesShown(sources[i].Id) : null;
            return (lanes?.Select(l => l.Set).ToArray(), -1);
        });
    }

    private static string CurrentId()
    {
        try { return NoteStyleManager.CurrentStyleId ?? "Karma"; }
        catch { return "Karma"; }
    }

    // Karma's colors, for when no palette can be read.
    private static Lane[] DefaultLanes()
    {
        int[] bodies = { 0x3478DD, 0x7B57C0, 0x7B57C0, 0x3478DD, 0xD86830 };
        return bodies.Select(b => new Lane { Body = Hex(b) }).ToArray();
    }

    // ---- the palette and lane pages -----------------------------------------------------------------

    private static void OpenPalette(Palette palette, int focusAt = 1)
    {
        editing = palette;
        screen = Screen.Palette;
        Kit.Picking = null;
        Kit.EndTyping();
        Kit.DelayClicks();
        focus = focusAt;
        Ui.ListPanel!.gameObject.SetActive(false);
        editPanel!.gameObject.SetActive(true);
        paletteRows!.gameObject.SetActive(true);
        laneRows!.gameObject.SetActive(false);
    }

    private static void OpenLane(int lane)
    {
        if (editing == null) return;
        editingLane = lane;
        ReadHsv();
        screen = Screen.Lane;
        Kit.Picking = null;
        Kit.EndTyping();
        Kit.DelayClicks();
        focus = 0;
        Ui.ListPanel!.gameObject.SetActive(false);
        editPanel!.gameObject.SetActive(true);
        paletteRows!.gameObject.SetActive(false);
        laneRows!.gameObject.SetActive(true);
    }

    private static Lane EditLane => editing!.Lanes[editingLane];

    private static void ReadHsv() => (hue, saturation, brightness) = NoteColorMath.ToHsv(EditLane.Body);

    private static void Back()
    {
        if (!Kit.FinishTyping()) return;
        // Back on the lane's row (the Name row is first).
        if (screen == Screen.Lane && editing != null) { OpenPalette(editing, 1 + editingLane); return; }
        SaveNow();
        int index = editing != null ? palettes.IndexOf(editing) + 1 : 0;
        ShowList(index);
    }

    private static List<Control> Shown() => (screen == Screen.Lane ? laneControls : paletteControls).Where(c => c.Visible).ToList();

    private static void UpdateRows(InputKeyboard k, InputMouse? mouse)
    {
        if (editing == null) { ShowList(0); return; }
        // Clicks wait a moment after the screen changed; a click anywhere finishes the field being typed in.
        var clicks = Kit.ClicksLive ? mouse : null;
        if (Kit.Typing != null && clicks != null && clicks.leftButton.wasPressedThisFrame && !Kit.CommitTyping()) clicks = null;
        Ui.UpdateButtons(clicks);
        if (!PageOpen || screen == Screen.List || editing == null) return;
        if (Kit.Live)
        {
            if (Kit.Typing != null) Kit.UpdateTyping(k);
            else if (Kit.BackPressed(k, mouse)) { Back(); return; }
            else
            {
                var shown = Shown();
                var on = screen;
                WalkControls(k, shown, ref focus);
                // A row that opened another screen (or a picker) takes nothing more from this frame.
                if (!PageOpen || screen != on || editing == null) return;
                int step = PadInput.Move();
                if (step != 0 && shown.Count > 0) focus = Math.Clamp(focus + step, 0, shown.Count - 1);
                if (PadInput.Pressed(PadButton.South) && focus >= 0 && focus < shown.Count) shown[focus].Activate();
                if (!PageOpen || screen != on || editing == null) return;
                int side = Side(k);
                if (side != 0 && focus >= 0 && focus < shown.Count && sliders.TryGetValue(shown[focus], out var slide)) slide(side);
            }
        }
        if (!PageOpen || screen == Screen.List || editing == null) return;
        Draw();
    }

    // Left or right, from the arrows (held: repeats) or the pad.
    private static int Side(InputKeyboard k)
    {
        int held = k[Key.LeftArrow].isPressed ? -1 : k[Key.RightArrow].isPressed ? 1 : 0;
        if (held == 0)
        {
            sideHeld = 0;
            return PadInput.SideRepeat();
        }
        float now = Time.unscaledTime;
        if (held != sideHeld)
        {
            sideHeld = held;
            sideRepeatAt = now + 0.35f;
            return held;
        }
        if (now < sideRepeatAt) return 0;
        sideRepeatAt = now + 0.05f;
        return held;
    }

    private static void Draw()
    {
        var palette = editing!;
        titleText!.text = $"<color=#9D92B4>Custom note colors</color>   {Escape(palette.Name)}" +
                          (IsPicked(palette) ? "   <size=75%><color=#9D92B4>in use</color></size>" : "") +
                          (screen == Screen.Lane ? $"   <color=#9D92B4>/</color>   {LaneNames[editingLane]}" : "");
        var shown = Shown();
        if (focus >= shown.Count) focus = shown.Count - 1;
        Kit.DrawFocus(focus >= 0 && Kit.Typing == null ? shown[focus] : null);
        foreach (var (image, color) in swatches)
            if (image && color() is { } c) image.color = c;
        pagePreview?.Draw(palette.Sets, screen == Screen.Lane ? editingLane : -1);
        warningText!.text = Warnings(palette);
        Ui.DrawStatus(Ui.MessageShowing ? Escape(Ui.Message) : "", 0, 0, 64);
    }

    private static void Say(string text, float seconds = 5f) => ui?.Say(text, seconds);

    // ---- building the pages ----------------------------------------------------------------------------

    private static void BuildEdit()
    {
        paletteControls.Clear();
        laneControls.Clear();
        sliders.Clear();
        swatches.Clear();
        editPanel = MakeRect("Edit", Ui.CanvasRect);
        Stretch(editPanel, 0, 0, 0, 0);
        var top = AddBar(editPanel, "TopBar", true, TopH);
        titleText = MakeText("Title", top, 22, TextAlignmentOptions.Left);
        titleText.enableWordWrapping = false;
        titleText.overflowMode = TextOverflowModes.Ellipsis;
        Stretch(titleText.rectTransform, 24, 0, 24, 0);

        paletteRows = MakeRect("PaletteRows", editPanel);
        Stretch(paletteRows, 0, 0, 0, 0);
        laneRows = MakeRect("LaneRows", editPanel);
        Stretch(laneRows, 0, 0, 0, 0);
        BuildPaletteRows();
        BuildLaneRows();
        // The keyboard's marker: a bar left of the row it's on.
        Kit.BuildFocusMarker(editPanel);

        var box = MakeImage("PreviewBox", editPanel, PanelColor).rectTransform;
        PlaceTop(box, PreviewX, -(TopH + 36), PreviewW, PreviewH);
        pagePreview = new PalettePreview(box, 3f, 380f);
        warningText = MakeText("Warnings", editPanel, 19, TextAlignmentOptions.TopLeft);
        warningText.color = DimText;
        warningText.enableWordWrapping = true;
        PlaceTop(warningText.rectTransform, PreviewX + 4, -(TopH + 36 + PreviewH + 16), PreviewW - 8, 220);
        var keys = MakeText("Keys", editPanel, 16, TextAlignmentOptions.BottomLeft);
        keys.color = DimText;
        keys.text = "Up/Down: pick a row. Enter: change it. Left/Right: move a slider (Shift: finer). Esc: back.";
        Place(keys.rectTransform, new Vector2(0, 0), new Vector2(RowsX, 24), new Vector2(1400, 30), new Vector2(0, 0));
        // Messages go last, so they draw over the page.
        Ui.BuildStatus(editPanel);
        editPanel.gameObject.SetActive(false);
    }

    private static void BuildPaletteRows()
    {
        var panel = paletteRows!;
        var keys = paletteControls;
        float y = -(TopH + 36);
        Kit.AddHeader(panel, RowsX, ref y, RowsW, "Palette");
        Kit.AddField(panel, keys, RowsX, ref y, RowsW, new TextField
        {
            Label = "Name",
            Get = () => editing?.Name ?? "",
            Set = SetName,
            Max = MaxName,
            Refused = NameRefused,
            RefusedText = "A name can't have < or >.",
            Hint = "Type the palette's name, then Enter. Esc cancels.",
        });
        Kit.AddHeader(panel, RowsX, ref y, RowsW, "Lanes (Enter changes one)");
        for (int i = 0; i < LaneCount; i++)
        {
            int lane = i;
            // Only from here does the lane page start over (a picker's Back, which also returns to it, keeps its state).
            var b = Kit.AddChoice(panel, keys, RowsX, ref y, RowsW, LaneNames[i], () => LaneValue(lane), () => { shadesSetHere = false; OpenLane(lane); });
            AddSwatch(b, () => editing?.Lanes[lane].Body);
        }
        y -= 12;
        Kit.AddToggle(panel, keys, RowsX, ref y, RowsW,
            () => editing != null && IsPicked(editing) ? "In use for your notes" : "Use these colors for your notes",
            () => editing != null && IsPicked(editing), UseEditing);
        Kit.AddButton(panel, keys, RowsX, y, RowsW, RowH, "Make a copy", Duplicate);
        y -= RowStep;
        Kit.AddButton(panel, keys, RowsX, y, RowsW, RowH, "Delete this palette", AskDelete);
        y -= RowStep;
        Kit.AddButton(panel, keys, RowsX, y, RowsW, RowH, "Done", Back);
    }

    private static void BuildLaneRows()
    {
        var panel = laneRows!;
        var keys = laneControls;
        float y = -(TopH + 36);
        Kit.AddHeader(panel, RowsX, ref y, RowsW, "The note's color");
        var body = Kit.AddField(panel, keys, RowsX, ref y, RowsW, new TextField
        {
            Label = "Color",
            Get = () => editing != null ? "#" + HexOf(EditLane.Body) : "",
            Set = SetBody,
            Max = 7,
            Hint = "Type a color as #RRGGBB (like #3478DD), then Enter. Esc cancels.",
        });
        AddSwatch(body, () => editing != null ? EditLane.Body : null);
        AddSlider(panel, keys, ref y, "Hue", () => $"{hue:0}", d => SetHsv(hue + d * Step(5f), saturation, brightness));
        AddSlider(panel, keys, ref y, "Saturation", () => $"{saturation * 100f:0}%", d => SetHsv(hue, saturation + d * Step(5f) / 100f, brightness));
        AddSlider(panel, keys, ref y, "Brightness", () => $"{brightness * 100f:0}%", d => SetHsv(hue, saturation, brightness + d * Step(5f) / 100f));
        Kit.AddChoice(panel, keys, RowsX, ref y, RowsW, "Pick a color", () => "Presets...", PickPreset);
        Kit.AddChoice(panel, keys, RowsX, ref y, RowsW, "Copy", () => "Another lane's colors...", CopyLane);
        Kit.AddHeader(panel, RowsX, ref y, RowsW, "Shades (empty: worked out from the color, and a new color resets them)");
        var accents = Kit.AddField(panel, keys, RowsX, ref y, RowsW, new TextField
        {
            Label = "Accents and hold",
            Get = () => editing != null && EditLane.Accents is { } a ? "#" + HexOf(a) : "",
            Empty = () => editing != null ? $"auto (#{HexOf(EditLane.AccentsShown)})" : "",
            Set = text => SetShade(text, lines: false),
            Max = 7,
            Hint = "Type a color as #RRGGBB, or leave it empty to have it worked out, then Enter. Esc cancels.",
        });
        AddSwatch(accents, () => editing != null ? EditLane.AccentsShown : null);
        var lines = Kit.AddField(panel, keys, RowsX, ref y, RowsW, new TextField
        {
            Label = "Line work",
            Get = () => editing != null && EditLane.Lines is { } l ? "#" + HexOf(l) : "",
            Empty = () => editing != null ? $"auto (#{HexOf(EditLane.LinesShown)})" : "",
            Set = text => SetShade(text, lines: true),
            Max = 7,
            Hint = "Type a color as #RRGGBB, or leave it empty to have it worked out, then Enter. Esc cancels.",
        });
        AddSwatch(lines, () => editing != null ? EditLane.LinesShown : null);
        y -= 12;
        Kit.AddButton(panel, keys, RowsX, y, RowsW, RowH, "Done", Back);
    }

    // A row whose value Left and Right move (Shift: finer), with "-" and "+" for the mouse at its end.
    private static void AddSlider(RectTransform panel, List<Control> keys, ref float y, string label, Func<string> value, Action<int> move)
    {
        float top = y;
        Kit.AddChoice(panel, keys, RowsX, ref y, RowsW - 124, label, () => $"<  {value()}  >", () => move(1));
        sliders[keys[keys.Count - 1]] = move;
        var less = Ui.MakeButton(panel, "-", () => { if (Kit.ClicksLive) move(-1); });
        PlaceTop(less.Rect, RowsX + RowsW - 116, top, 56, RowH);
        var more = Ui.MakeButton(panel, "+", () => { if (Kit.ClicksLive) move(1); });
        PlaceTop(more.Rect, RowsX + RowsW - 56, top, 56, RowH);
    }

    private static float Step(float coarse)
    {
        var k = InputKeyboard.current;
        return k != null && Shift(k) ? 1f : coarse;
    }

    // A small patch of color at the right end of a row.
    private static void AddSwatch(UiButton b, Func<Color?> color)
    {
        var image = MakeImage("Swatch", b.Rect, Color.white);
        image.raycastTarget = false;
        Place(image.rectTransform, new Vector2(1, 0.5f), new Vector2(-14, 0), new Vector2(72, 26), new Vector2(1, 0.5f));
        swatches.Add((image, color));
    }

    private static string LaneValue(int lane)
    {
        if (editing == null) return "";
        var l = editing.Lanes[lane];
        string own = l.Accents != null || l.Lines != null ? "   <color=#9D92B4>own shades</color>" : "";
        return "#" + HexOf(l.Body) + own;
    }

    // ---- changes ----------------------------------------------------------------------------------

    private static void SetName(string text)
    {
        var palette = editing!;
        string name = text.Trim();
        if (name == palette.Name) return;
        if (NameProblem(name, palette) is { } problem) throw new InvalidDataException(problem);
        string old = palette.Name;
        Rename(palette, name);
        Touch();
        ModLog.Info($"Custom note colors: renamed {old} to {name}.");
    }

    // Whether the player set a shade on this lane's page since it opened. Until they do, a new color
    // takes its shades with it (worked out from it): the ones a palette started with belong to its old color.
    private static bool shadesSetHere;

    private static void SetBody(string text)
    {
        if (!NoteColorMath.TryParseHex(text, out var color)) throw new InvalidDataException("That isn't a color: type six hex digits, like #3478DD.");
        SetBodyColor(color);
        ReadHsv();
    }

    private static void SetHsv(float h, float s, float v)
    {
        hue = ((h % 360f) + 360f) % 360f;
        saturation = Math.Clamp(s, 0f, 1f);
        brightness = Math.Clamp(v, 0f, 1f);
        SetBodyColor(NoteColorMath.FromHsv(hue, saturation, brightness));
    }

    private static void SetBodyColor(Color color)
    {
        var lane = EditLane;
        // The same color again (Enter on the field, a slider at its end) changes nothing, shades included.
        if (HexOf(color) == HexOf(lane.Body)) return;
        lane.Body = color;
        if (!shadesSetHere) lane.Accents = lane.Lines = null;
        LaneChanged();
    }

    private static void SetShade(string text, bool lines)
    {
        string t = text.Trim();
        Color? value = null;
        if (t.Length > 0 && !t.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (!NoteColorMath.TryParseHex(t, out var color)) throw new InvalidDataException("That isn't a color: type six hex digits like #3478DD, or leave it empty.");
            value = color;
        }
        if (lines) EditLane.Lines = value;
        else EditLane.Accents = value;
        shadesSetHere = true;
        LaneChanged();
    }

    private static void LaneChanged()
    {
        Changed(editing!);
        Touch();
    }

    private static void UseEditing()
    {
        var palette = editing!;
        if (IsPicked(palette)) { Say("It's in use. Pick another palette in Note Colors to stop using it."); return; }
        Pick(palette);
        ModLog.Info($"Custom note colors: {palette.Name} is in use.");
        Say($"Your notes use {palette.Name} now.");
    }

    private static void Duplicate()
    {
        var palette = editing!;
        if (palettes.Count >= MaxPalettes) { Say($"You have {MaxPalettes} palettes, the most there can be."); return; }
        var copy = Add(palette.Copy(FreeName(palette.Name + " copy")));
        Touch();
        SaveNow();
        OpenPalette(copy);
        Say($"Made {copy.Name}, a copy of {palette.Name}.");
    }

    private static void AskDelete()
    {
        var palette = editing!;
        bool picked = IsPicked(palette);
        ShowPickerScreen(Confirm($"Delete {palette.Name}?", "Keep it", "Delete it",
            picked ? "It's in use: your notes go back to Karma." : "It's gone for good.",
            () =>
            {
                bool wasPicked = IsPicked(palette);
                Remove(palette);
                if (wasPicked) PickDefault();
                Touch();
                SaveNow();
                ModLog.Info($"Custom note colors: deleted {palette.Name}.");
                ShowList(0);
                Say(wasPicked ? $"Deleted {palette.Name}. Your notes use Karma again." : $"Deleted {palette.Name}.");
            },
            () => OpenPalette(palette, paletteControls.Count - 2)),
            () => (palette.Sets, -1));
    }

    // The presets: well apart from each other, and from the mines' red (crimson, not red).
    private static readonly (string Name, int Rgb)[] Presets =
    {
        ("Crimson", 0xCC1650), ("Rose", 0xE0457B), ("Pink", 0xF06292), ("Magenta", 0xD63AAF), ("Purple", 0x7B57C0),
        ("Indigo", 0x5B4BD6), ("Blue", 0x2C6FE6), ("Sky", 0x4FADF3), ("Cyan", 0x22C3E6), ("Teal", 0x14A89A),
        ("Green", 0x2BB14A), ("Lime", 0x9CCC24), ("Yellow", 0xF0C814), ("Amber", 0xFFB300), ("Orange", 0xF7931E),
        ("Brown", 0x8D5A3B), ("White", 0xF2F2F2), ("Silver", 0xA8A8B8), ("Gray", 0x6E6E80), ("Charcoal", 0x34323E),
    };

    private static void PickPreset()
    {
        var palette = editing!;
        int lane = editingLane;
        ShowPickerScreen(new Picker
        {
            Heading = $"A color for {LaneNames[lane]}",
            Rows = Presets.Select(p => p.Name).ToList(),
            Hint = i => $"#{Presets[i].Rgb:X6}. The shades are worked out from it.",
            Jump = true,
            Choose = i =>
            {
                var l = palette.Lanes[lane];
                l.Body = Hex(Presets[i].Rgb);
                l.Accents = l.Lines = null;
                Changed(palette);
                Touch();
                OpenLane(lane);
            },
            Back = () => OpenLane(lane),
        }, () =>
        {
            int i = Kit.Picking?.Index ?? 0;
            var sets = (CombatNoteColorSet[])palette.Sets.Clone();
            if (i >= 0 && i < Presets.Length) sets[lane] = new Lane { Body = Hex(Presets[i].Rgb) }.Set;
            return (sets, lane);
        });
    }

    private static void CopyLane()
    {
        var palette = editing!;
        int lane = editingLane;
        var others = Enumerable.Range(0, LaneCount).Where(i => i != lane).ToList();
        ShowPickerScreen(new Picker
        {
            Heading = $"Copy which lane into {LaneNames[lane]}?",
            Rows = others.Select(i => $"{LaneNames[i]}   #{HexOf(palette.Lanes[i].Body)}").ToList(),
            Hint = _ => "Its color and both shades.",
            Choose = i =>
            {
                palette.Lanes[lane] = palette.Lanes[others[i]].Copy();
                Changed(palette);
                Touch();
                OpenLane(lane);
            },
            Back = () => OpenLane(lane),
        }, () =>
        {
            int i = Kit.Picking?.Index ?? 0;
            var sets = (CombatNoteColorSet[])palette.Sets.Clone();
            if (i >= 0 && i < others.Count) sets[lane] = palette.Sets[others[i]];
            return (sets, lane);
        });
    }

    // ---- what the preview warns about ----------------------------------------------------------------

    // The mine's fixed colors from the game's note prefab (no palette changes them).
    private static readonly Color MineBody = Hex(0xD64734), MineMarks = Hex(0xFF8681), MineLight = Hex(0xFFD4CC);
    // Under these delta E 2000s, a note is easily taken for the mine next to it: its body to the mine's body, or
    // its accents to the mine's marks. Kimothy's edge lanes (6.3) are under; Karma's middle (11.3) and the presets aren't.
    private const float MineDistance = 10f, MarksDistance = 7f;
    // Under this contrast, a note's body hardly shows on the lanes.
    private const float LaneContrast = 1.6f;

    private static Color LaneBackground()
    {
        try { return ColumnStyleManager.CurrentColumnColors.defaultColors.color2; }
        catch { return Hex(0x11101B); }
    }

    private static string Warnings(Palette palette)
    {
        var lines = new List<string>();
        var lanes = LaneBackground();
        for (int i = 0; i < LaneCount; i++)
        {
            var set = palette.Sets[i];
            bool mine = NoteColorMath.DeltaE2000(set.color1, MineBody) < MineDistance || NoteColorMath.DeltaE2000(set.color2, MineMarks) < MarksDistance;
            if (mine) lines.Add($"<color=#F2B02E>{LaneNames[i]} looks close to the mines' red</color> (mines keep their red whatever the palette), so its notes could be taken for mines.");
            float contrast = NoteColorMath.Contrast(set.color1, lanes);
            if (contrast < LaneContrast) lines.Add($"<color=#F2B02E>{LaneNames[i]} is hard to see on the lanes</color> (contrast {contrast:0.0}).");
        }
        if (lines.Count == 0) lines.Add("The mine on the right keeps its red whatever the palette; no lane looks like it.");
        return string.Join("\n", lines);
    }

    // ---- the preview ----------------------------------------------------------------------------------

    /// <summary>
    /// Four lanes with a hold, the five-lane middle and a mine, drawn with the game's note shapes in
    /// the note skin in use, in a palette's colors. One lane can be marked (the lane being changed).
    /// </summary>
    private sealed class PalettePreview
    {
        private readonly RectTransform root;
        private readonly float scale, laneHeight;
        private readonly float[] xs;
        private readonly Image[] fills = new Image[6];
        private readonly Image[] edges = new Image[12];
        private readonly NoteLayers[] notes = new NoteLayers[5];
        private readonly Image holdBody, holdPattern, marker;
        private readonly float noteY;
        private NoteSkin? shapedFor;

        internal PalettePreview(RectTransform parent, float scale, float laneHeight)
        {
            this.scale = scale;
            this.laneHeight = laneHeight;
            root = MakeRect("Preview", parent);
            Stretch(root, 0, 0, 0, 0);
            float step = 30f * scale;
            // Lanes 1 to 4, then the middle and the mine, far enough apart for their captions.
            float first = -3.2f * step;
            xs = new[] { first, first + step, first + 2 * step, first + 3 * step, first + 4.6f * step, first + 6.4f * step };
            float laneWidth = 22f * scale;
            noteY = -laneHeight * 0.22f;
            for (int i = 0; i < 6; i++)
            {
                fills[i] = Box("Lane" + i, xs[i], 20f, laneWidth, laneHeight);
                edges[i * 2] = Box("Lane" + i + "Left", xs[i] - laneWidth / 2f, 20f, Math.Max(1f, scale * 0.5f), laneHeight);
                edges[i * 2 + 1] = Box("Lane" + i + "Right", xs[i] + laneWidth / 2f, 20f, Math.Max(1f, scale * 0.5f), laneHeight);
            }
            // A hold in the first lane, reaching up from its head to the top of the lane.
            float holdTop = 20f + laneHeight / 2f - 8f, holdLength = holdTop - noteY;
            holdBody = Box("HoldBody", xs[0], noteY + holdLength / 2f, 21.5f * scale, holdLength);
            holdPattern = Box("HoldPattern", xs[0], noteY + holdLength / 2f, 19f * scale, holdLength);
            for (int i = 0; i < 5; i++) notes[i] = new NoteLayers(root, "Note" + i, new Vector2(xs[i], noteY));
            var mine = new NoteLayers(root, "Mine", new Vector2(xs[5], noteY));
            mine.ShowMine(scale);
            marker = Box("Marker", 0f, 20f - laneHeight / 2f - 10f, laneWidth + 8f, 6f);
            marker.color = Accent;
            Caption("5 lanes", xs[4], DimText);
            Caption("mine", xs[5], MineBody);
            for (int i = 0; i < 4; i++) Caption((i + 1).ToString(), xs[i], DimText);
        }

        private Image Box(string name, float x, float y, float w, float h)
        {
            var image = MakeImage(name, root, Color.white);
            image.raycastTarget = false;
            Place(image.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(x, y), new Vector2(w, h));
            return image;
        }

        private void Caption(string text, float x, Color color)
        {
            var t = MakeText("Caption", root, scale < 2f ? 13f : 12f + 3f * scale, TextAlignmentOptions.Center);
            t.text = text;
            t.color = color;
            t.enableWordWrapping = false;
            Place(t.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(x, 20f - laneHeight / 2f - 22f - 4f * scale), new Vector2(40f * scale + 40f, 30f));
        }

        internal void Draw(CombatNoteColorSet[]? sets, int highlight)
        {
            bool show = sets != null && sets.Length == LaneCount;
            if (root.gameObject.activeSelf != show) root.gameObject.SetActive(show);
            if (!show) return;
            var skin = SettingsState.NoteSkin;
            if (shapedFor != skin)
            {
                for (int i = 0; i < 5; i++) notes[i].Shape(skin, i < 4 ? i : 2, i < 4 ? 4 : 5, scale);
                float width = skin == NoteSkin.Default ? 1f : NoteSkins.HoldWidthFactor;
                Resize(holdBody, 21.5f * scale * width);
                Resize(holdPattern, 19f * scale * width);
                shapedFor = skin;
            }
            // The lanes as the game's column style draws them, as in the options preview.
            Color fill = Hex(0x11101B), edge = Hex(0x2A2538);
            try
            {
                var columns = ColumnStyleManager.CurrentColumnColors.defaultColors;
                fill = columns.color2;
                edge = columns.color1;
            }
            catch { }
            foreach (var f in fills) f.color = fill;
            foreach (var e in edges) e.color = new Color(edge.r, edge.g, edge.b, 0.5f);
            for (int i = 0; i < 5; i++) notes[i].Tint(sets![i]);
            // The hold body takes the second color and its pattern the first, as in battle.
            holdBody.color = sets![0].color2;
            holdPattern.color = new Color(sets[0].color1.r, sets[0].color1.g, sets[0].color1.b, 0.6f);
            bool mark = highlight >= 0 && highlight < LaneCount;
            if (marker.gameObject.activeSelf != mark) marker.gameObject.SetActive(mark);
            if (mark)
            {
                var rect = marker.rectTransform;
                rect.anchoredPosition = new Vector2(xs[highlight], rect.anchoredPosition.y);
            }
        }

        private static void Resize(Image image, float width)
        {
            var rect = image.rectTransform;
            rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);
        }
    }

    /// <summary>One note from three tinted layers, like the game's notes and the mod's skins.</summary>
    private sealed class NoteLayers
    {
        private readonly RectTransform rect;
        private readonly Image body, glyph, accent;

        internal NoteLayers(RectTransform parent, string name, Vector2 position)
        {
            rect = MakeRect(name, parent);
            Place(rect, new Vector2(0.5f, 0.5f), position, new Vector2(10f, 10f));
            body = Layer("Body");
            glyph = Layer("Glyph");
            accent = Layer("Accent");
        }

        private Image Layer(string name)
        {
            var image = MakeImage(name, rect, Color.white);
            image.raycastTarget = false;
            Stretch(image.rectTransform, 0, 0, 0, 0);
            return image;
        }

        internal void Shape(NoteSkin skin, int column, int count, float scale)
        {
            if (skin == NoteSkin.Default)
            {
                body.sprite = SkinSprites.BarBody;
                glyph.sprite = SkinSprites.BarGlyph;
                accent.sprite = SkinSprites.BarAccent;
                rect.sizeDelta = new Vector2(SkinArt.BarMaskWidth / 8f * scale, SkinArt.BarMaskHeight / 8f * scale);
                rect.localRotation = Quaternion.identity;
                return;
            }
            var shape = NoteSkins.ShapeFor(skin, column, count);
            body.sprite = SkinSprites.NoteBody(shape);
            glyph.sprite = SkinSprites.NoteGlyph(shape);
            accent.sprite = SkinSprites.NoteAccent(shape);
            float size = NoteSkins.NoteScale * scale;
            rect.sizeDelta = new Vector2(size, size);
            // On screen the arrows read left, down, up, right in both scroll directions.
            rect.localRotation = Quaternion.Euler(0f, 0f, NoteSkins.AngleFor(skin, column, count, false));
        }

        internal void Tint(CombatNoteColorSet colors)
        {
            body.color = colors.color1;
            glyph.color = colors.color3;
            accent.color = colors.color2;
        }

        internal void ShowMine(float scale)
        {
            body.sprite = SkinSprites.MineBody;
            glyph.sprite = SkinSprites.MineLight;
            accent.sprite = SkinSprites.MineMarks;
            rect.sizeDelta = new Vector2(SkinArt.BarMaskWidth / 8f * scale, SkinArt.BarMaskHeight / 8f * scale);
            body.color = MineBody;
            glyph.color = MineLight;
            accent.color = MineMarks;
        }
    }

    // ---- QA builds read the page through these ----------------------------------------------------------

    internal static string QaScreen => PageOpen ? screen.ToString() : "Closed";
    internal static int QaFocus => focus;
    internal static string QaHeading => kit?.Picking?.Heading ?? "";
    internal static int QaPickerIndex => kit?.Picking?.Index ?? -1;
    internal static string QaTyping => kit?.Typing != null ? kit.Typed : "";
    internal static string? QaEditing => editing?.Name;
    internal static int QaLane => editingLane;
    internal static string QaWarnings => warningText != null ? warningText.text : "";
}
