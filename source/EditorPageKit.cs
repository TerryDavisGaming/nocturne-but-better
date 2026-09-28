using UnityEngine;
using UnityEngine.UI;
using static NocturnePlus.EditorInput;
using static NocturnePlus.EditorUi;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;

namespace NocturnePlus;

/// <summary>
/// What the mod's full-screen pages share on top of <see cref="EditorUi"/>, first made for the
/// battle creator: when keys and clicks count after the screen changes, lists that also take the
/// pad, pickers (a list to pick a row from, with a picture beside it), text fields and their
/// typing (<see cref="TextField"/>), rows of buttons that the keyboard walks, a top bar, a bottom
/// bar and a side column, and work that finishes later while a line says what's going on. A page
/// makes one with its EditorUi when it opens and drops it when it closes. Which screen shows, and
/// what a choice does, stay the page's own.
/// </summary>
internal sealed class EditorPageKit
{
    // The page's name at the start of its log lines, like "Battle creator".
    private readonly string name;

    internal EditorPageKit(EditorUi ui, string name)
    {
        Ui = ui;
        this.name = name;
    }

    /// <summary>The page's screen.</summary>
    internal EditorUi Ui { get; }

    internal void Say(string text, float seconds = 4f) => Ui.Say(text, seconds);

    // ---- keys and clicks after the screen changes ------------------------------------------------

    /// <summary>
    /// How long clicks wait after the screen changes, so the second click of a double-click (or a
    /// click that lands where the button that opened the screen was) doesn't choose on the new one.
    /// Two prompts in a row count as a change, so a double-click can't answer both.
    /// </summary>
    internal const float ClickDelay = 0.5f;

    private int ignoreKeysFrame = -1;
    private float clicksFrom;

    /// <summary>False on the frame of <see cref="IgnoreKeysNow"/>.</summary>
    internal bool Live => Time.frameCount > ignoreKeysFrame;

    /// <summary>False for <see cref="ClickDelay"/> after <see cref="DelayClicks"/>.</summary>
    internal bool ClicksLive => Time.unscaledTime >= clicksFrom;

    /// <summary>
    /// Keys pressed on this frame belong to what opened the page or brought it back (the Enter that
    /// picked the menu row, the Esc that closed the chart editor).
    /// </summary>
    internal void IgnoreKeysNow() => ignoreKeysFrame = Time.frameCount;

    /// <summary>Clicks wait a moment from now; called whenever the screen changes.</summary>
    internal void DelayClicks() => clicksFrom = Time.unscaledTime + ClickDelay;

    /// <summary>
    /// Whether a list row was picked, like <see cref="EditorUi.Chosen"/>, except that a click in
    /// the first moment after the screen changed doesn't count (Enter always does).
    /// </summary>
    internal bool Chosen(InputKeyboard keyboard, int count, ref int index)
    {
        int before = index;
        bool chosen = Ui.Chosen(keyboard, count, ref index);
        if (!chosen || ClicksLive || Pressed(keyboard, Key.Enter) || Pressed(keyboard, Key.NumpadEnter)) return chosen;
        index = before;
        return false;
    }

    // ---- lists on a page that takes the pad (PadInput) ---------------------------------------------

    /// <summary>
    /// Moves the highlight (Up/Down, PgUp/PgDn, the pad's d-pad or stick; the wheel scrolls in
    /// <see cref="EditorUi.DrawList"/>) and says whether the highlighted row was chosen: a click
    /// (not in the first moment after the screen changed), Enter, Z or the pad's A.
    /// </summary>
    internal bool MoveAndChoose(InputKeyboard keyboard, int count, ref int index)
    {
        if (Live)
        {
            index = MoveInList(keyboard, index, count);
            int step = PadInput.Move();
            if (step != 0 && count > 0) index = Math.Clamp(index + step, 0, count - 1);
        }
        bool chosen = Chosen(keyboard, count, ref index);
        if (!chosen && count > 0 && (Pressed(keyboard, Key.Z) || PadInput.Pressed(PadButton.South))) chosen = true;
        return Live && chosen;
    }

    /// <summary>Esc, X, a right click or the pad's B.</summary>
    internal bool BackPressed(InputKeyboard keyboard, InputMouse? mouse) =>
        Live && (Pressed(keyboard, Key.Escape) || Pressed(keyboard, Key.X) || PadInput.Pressed(PadButton.East) ||
                 (ClicksLive && mouse != null && mouse.rightButton.wasPressedThisFrame));

    // ---- work that finishes later: file pickers, copying, zipping ---------------------------------

    private Task? pending;
    private Action<Task>? pendingDone;
    private string pendingWhat = "";

    /// <summary>
    /// Waits for <paramref name="task"/> without stopping the game, then runs <paramref name="done"/>
    /// with its result (in <see cref="FinishPending"/>). Keys and clicks wait meanwhile
    /// (<see cref="Busy"/>); <paramref name="what"/> shows until then.
    /// </summary>
    internal void Run<T>(Task<T> task, string what, Action<T> done)
    {
        pending = task;
        pendingWhat = what;
        pendingDone = finished => done(((Task<T>)finished).Result);
        Say(what, 3600f);
    }

    /// <summary>Whether work from <see cref="Run"/> is still going.</summary>
    internal bool Busy => pending != null;

    /// <summary>The work from <see cref="Run"/> still going, or null.</summary>
    internal Task? Pending => pending;

    /// <summary>Called every frame by the page: once the work is done, runs what comes after it, or says why it failed.</summary>
    internal void FinishPending()
    {
        if (pending == null || !pending.IsCompleted) return;
        var task = pending;
        var done = pendingDone;
        pending = null;
        pendingDone = null;
        Say("", 0f);
        try { done?.Invoke(task); }
        catch (Exception ex)
        {
            var reason = Unwrap(ex);
            bool expected = reason is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
                or NotSupportedException or System.Text.Json.JsonException;
            ModLog.Error($"{name}: {pendingWhat.TrimEnd('.')} failed: {(expected ? reason.Message : reason.ToString())}");
            Say("That didn't work: " + reason.Message, 7f);
        }
    }

    internal static Exception Unwrap(Exception ex)
    {
        while (ex is AggregateException agg && agg.InnerException != null) ex = agg.InnerException;
        return ex;
    }

    /// <summary>Runs <paramref name="work"/> on a thread of its own in a single-threaded apartment, as the Windows shell asks.</summary>
    internal static Task<T> OnShellThread<T>(Func<T> work, string threadName)
    {
        var result = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { result.SetResult(work()); }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true, Name = threadName };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    // ---- pickers: small list screens to pick one of a few rows -------------------------------------

    internal sealed class Picker
    {
        internal string Heading = "";
        internal List<string> Rows = new();
        internal Func<int, string> Hint = _ => "";
        internal Action<int> Choose = _ => { };
        internal Action Back = () => { };
        internal int Index;
        /// <summary>Typing letters jumps to the first row that starts with them (for long lists).</summary>
        internal bool Jump;
        /// <summary>A picture beside the list for the highlighted row (null shows none), and a line under it.</summary>
        internal Func<int, Sprite?>? Face;
        internal Func<int, string>? FaceNote;
        /// <summary>
        /// The heading, hint, rows and note are shown exactly as they are, with rich text off: for
        /// text from the network (the hub), which is never escaped into the mod's own rich text.
        /// </summary>
        internal bool Plain;
    }

    /// <summary>What <see cref="UpdatePicker"/> did this frame.</summary>
    internal enum PickerStep
    {
        /// <summary>No picker is showing.</summary>
        None,
        /// <summary>Esc went back (the picker's Back ran).</summary>
        Back,
        /// <summary>A row was chosen (the picker's Choose ran).</summary>
        Chosen,
        /// <summary>The picker is showing.</summary>
        Shown
    }

    /// <summary>The picker showing, or null. It's cleared before its Choose or Back runs, so they can show another.</summary>
    internal Picker? Picking { get; set; }

    // What has been typed to jump in the list, and when last (a pause starts it over).
    private string jumpTyped = "";
    private float jumpAt;
    private RectTransform? faceBox;
    private Image? face;
    private TMP_Text? faceNote;
    // Said once a session in the log.
    private static bool reportedFace;

    /// <summary>What has been typed to jump in the list.</summary>
    internal string JumpTyped => jumpTyped;

    /// <summary>Shows a picker; the page shows the list screen that <see cref="UpdatePicker"/> draws on.</summary>
    internal void ShowPicker(Picker next)
    {
        Picking = next;
        jumpTyped = "";
    }

    /// <summary>
    /// The picture beside a picker's list, right of its box (the list's box is 1200 wide, so the
    /// canvas has room at any window shape), for pickers with a <see cref="Picker.Face"/>. Needs
    /// <see cref="EditorUi.BuildList"/> first.
    /// </summary>
    internal void BuildPickerFace()
    {
        faceBox = MakeImage("FaceBox", Ui.ListPanel!, PanelColor).rectTransform;
        Place(faceBox, new Vector2(0.5f, 0.5f), new Vector2(780, 60), new Vector2(320, 440));
        face = MakeImage("Face", faceBox, Color.white);
        face.preserveAspect = true;
        PlaceTop(face.rectTransform, 10, -10, 300, 300);
        faceNote = MakeText("Note", faceBox, 18, TextAlignmentOptions.Top);
        faceNote.color = DimText;
        PlaceTop(faceNote.rectTransform, 12, -318, 296, 112);
        faceBox.gameObject.SetActive(false);
    }

    /// <summary>Hides the picture beside the list; a picker with a picture shows it again on its first update.</summary>
    internal void HidePickerFace()
    {
        if (faceBox) faceBox!.gameObject.SetActive(false);
    }

    /// <summary>Lets go of the picture beside the list, before its sprite is destroyed.</summary>
    internal void ClearPickerFace()
    {
        if (face) face!.sprite = null;
    }

    private void DrawPickerFace(Picker p)
    {
        // A page whose pickers have no pictures needn't build the box.
        if (faceBox == null) return;
        bool show = p.Face != null;
        if (faceBox.gameObject.activeSelf != show) faceBox.gameObject.SetActive(show);
        if (!show) return;
        Sprite? sprite = null;
        string note;
        // The picture is only a help: if it fails, the list still works.
        try
        {
            sprite = p.Face!(p.Index);
            note = p.FaceNote?.Invoke(p.Index) ?? "";
        }
        catch (Exception ex)
        {
            if (!reportedFace) ModLog.Error($"{name}: the picture beside the list failed: " + ex);
            reportedFace = true;
            note = "";
        }
        if (face!.sprite != sprite) face.sprite = sprite;
        if (face.enabled != (sprite != null)) face.enabled = sprite != null;
        if (faceNote!.richText == p.Plain)
        {
            faceNote.richText = !p.Plain;
            faceNote.parseCtrlCharacters = !p.Plain;
        }
        faceNote.text = p.Plain ? note : Escape(note);
    }

    // Typing letters jumps to the first row that starts with them; a pause of a second starts over.
    private void TypeJump(InputKeyboard keyboard, Picker p)
    {
        if (Time.unscaledTime > jumpAt + 1f) jumpTyped = "";
        string before = jumpTyped;
        if (!TypeInto(keyboard, ref jumpTyped, 24) || jumpTyped == before) return;
        jumpAt = Time.unscaledTime;
        if (jumpTyped.Trim().Length == 0) return;
        int row = p.Rows.FindIndex(r => r.StartsWith(jumpTyped, StringComparison.OrdinalIgnoreCase));
        if (row >= 0) p.Index = row;
    }

    /// <summary>
    /// The showing picker's frame: Esc goes back, Up/Down and typing move, a click or Enter chooses,
    /// else it's drawn on the list screen. Nothing of the page is touched after a Back or a Choose.
    /// </summary>
    internal PickerStep UpdatePicker(InputKeyboard keyboard)
    {
        var p = Picking;
        if (p == null) return PickerStep.None;
        bool live = Live && !Busy;
        // The pad's B goes back and A chooses, on a page that reads the pad (PadInput presses nothing otherwise).
        if (live && (Pressed(keyboard, Key.Escape) || PadInput.Pressed(PadButton.East)))
        {
            Picking = null;
            p.Back();
            return PickerStep.Back;
        }
        int before = p.Index;
        if (live) p.Index = MoveInList(keyboard, p.Index, p.Rows.Count);
        if (live && PadInput.Move() is int step && step != 0 && p.Rows.Count > 0) p.Index = Math.Clamp(p.Index + step, 0, p.Rows.Count - 1);
        if (live && p.Jump) TypeJump(keyboard, p);
        // A message shows where the hint goes; moving to another row brings back that row's hint.
        if (p.Index != before && Ui.MessageShowing) Ui.ClearMessage();
        bool chosen = Chosen(keyboard, p.Rows.Count, ref p.Index) || (p.Rows.Count > 0 && PadInput.Pressed(PadButton.South));
        if (live && chosen)
        {
            Picking = null;
            p.Choose(p.Index);
            return PickerStep.Chosen;
        }
        string hint = Ui.MessageShowing ? Ui.Message : p.Hint(p.Index);
        if (p.Jump && jumpTyped.Trim().Length > 0 && Time.unscaledTime <= jumpAt + 1f) hint = $"Jump: {jumpTyped}   " + hint;
        if (p.Plain) Ui.DrawList(p.Heading, hint, p.Rows, p.Index, plain: true);
        else Ui.DrawList(Escape(p.Heading), Escape(hint), p.Rows, p.Index);
        DrawPickerFace(p);
        return PickerStep.Shown;
    }

    /// <summary>A yes or no prompt, with the cursor on no.</summary>
    /// <param name="yesFirst">
    /// Yes on the first row instead of the second: a second prompt right after a first one
    /// then has its No where the first one had its Yes.
    /// </param>
    internal static Picker Confirm(string heading, string no, string yes, string hint, Action onYes, Action onNo, bool yesFirst = false) => new()
    {
        Heading = heading,
        Rows = yesFirst ? new List<string> { yes, no } : new List<string> { no, yes },
        Index = yesFirst ? 1 : 0,
        Hint = _ => hint,
        Choose = i => { if (i == (yesFirst ? 0 : 1)) onYes(); else onNo(); },
        Back = onNo,
    };

    // ---- text fields (TextField, below the kit) and their typing -------------------------------------

    /// <summary>Whether a field can start typing at all now; a page with nothing to type into says no.</summary>
    internal Func<bool>? CanType;

    /// <summary>Keys the page takes while a field is open, after Esc (like Ctrl+S to save); true when one was used.</summary>
    internal Func<InputKeyboard, bool>? KeysWhileTyping;

    /// <summary>The field being typed in, or null.</summary>
    internal TextField? Typing { get; private set; }

    /// <summary>What has been typed in it so far.</summary>
    internal string Typed { get; set; } = "";

    // Until when the typing hint says a letter was left out.
    private float refusedUntil;

    internal void StartTyping(TextField field)
    {
        if (CanType != null && !CanType()) return;
        if (!FinishTyping()) return;
        if (field.MayEdit != null && !field.MayEdit()) return;
        Typing = field;
        Typed = field.Get();
        refusedUntil = 0;
        BeginText();
        SayTypingHint();
    }

    // The hint while typing. The long texts, and those with a measure (like the info boxes' lines),
    // also show how much of them is used, so it's clear why typing stops at the limit.
    private void SayTypingHint()
    {
        var field = Typing;
        if (field == null) return;
        string hint = field.MultiLine ? "Type, then Enter. Shift+Enter starts a new line. Esc cancels." : field.Hint;
        if (Time.unscaledTime < refusedUntil) hint = field.RefusedText + "  " + hint;
        if (field.Max >= 100 || field.Measure != null) hint += BattleDraft.TypingCount(Typed.Length, field.Max, field.Measure?.Invoke(Typed));
        Say(hint, 3600f);
    }

    /// <summary>
    /// Called every frame by the page while a field is open (and keys are live). On a page that
    /// reads the pad, B leaves the field like Esc and A keeps it like Enter, so a pad is never stuck
    /// in one (PadInput reports nothing on the other pages).
    /// </summary>
    internal void UpdateTyping(InputKeyboard k)
    {
        var field = Typing!;
        if (Pressed(k, Key.Escape) || PadInput.Pressed(PadButton.East)) { EndTyping(); return; }
        if (KeysWhileTyping != null && KeysWhileTyping(k)) return;
        bool enter = Pressed(k, Key.Enter) || Pressed(k, Key.NumpadEnter);
        if (enter && field.MultiLine && Shift(k))
        {
            if (Typed.Length < field.Max)
            {
                Typed += "\n";
                SayTypingHint();
            }
            return;
        }
        string typed = Typed;
        if (TypeText(k, ref typed, field.Max))
        {
            if (field.Refused is { } refused && typed.IndexOfAny(refused) >= 0)
            {
                typed = new string(typed.Where(c => Array.IndexOf(refused, c) < 0).ToArray());
                refusedUntil = Time.unscaledTime + 4f;
            }
            Typed = typed;
            SayTypingHint();
        }
        if (enter || PadInput.Pressed(PadButton.South)) CommitTyping();
    }

    /// <summary>
    /// Keeps what was typed. False when the field refuses it (like "abc" for HP): the field stays
    /// open with the reason showing, and nothing is saved until it's fixed or Esc cancels it.
    /// </summary>
    internal bool CommitTyping()
    {
        var field = Typing;
        if (field == null) return true;
        try { field.Set(Typed); }
        catch (InvalidDataException ex)
        {
            Say(ex.Message + "  Fix it, or Esc keeps the old value.", 3600f);
            return false;
        }
        EndTyping();
        return true;
    }

    /// <summary>Keeps what is being typed, if anything; false when the field refused it (see <see cref="CommitTyping"/>).</summary>
    internal bool FinishTyping() => Typing == null || CommitTyping();

    /// <summary>Stops typing and drops what was typed.</summary>
    internal void EndTyping()
    {
        if (Typing == null) return;
        Typing = null;
        EndText();
        Say("", 0f);
    }

    // What is being typed, as last fitted to its row: it is measured again only when it changes.
    private TextField? fittedField;
    private string fittedText = "", fittedShown = "";

    /// <summary>A field's row: its label, then its value (or, while it's typed, what's typed and a "_" cursor).</summary>
    internal string FieldText(TextField field, UiButton button)
    {
        // While typing the row is lit in the accent colour, where the dim label wouldn't read.
        string label = Typing == field ? field.Label : $"<color=#9D92B4>{field.Label}</color>";
        string Row(string shown) => field.Tall ? $"{label}\n{shown}" : $"{label}<pos=32%>{shown}";
        if (Typing == field)
        {
            // The end of the text, where the "_" cursor is, always shows: what doesn't fit is cut from the start.
            if (fittedField != field || fittedText != Typed)
            {
                fittedField = field;
                fittedText = Typed;
                fittedShown = TextTail.Fit(Typed, shown => Fits(button, field.Tall, Row(Escape(shown) + "_"), Escape(shown) + "_"));
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

    // ---- rows of buttons that the keyboard walks ---------------------------------------------------

    /// <summary>A row's height, and the step from one row to the next.</summary>
    internal const float RowH = 44f, RowStep = 52f;

    /// <summary>A button that the keyboard can reach.</summary>
    internal sealed class Control
    {
        internal UiButton Button = null!;
        internal Func<bool>? Shown;
        internal Action Activate = null!;
        internal bool Visible => Shown?.Invoke() ?? true;
    }

    /// <summary>
    /// A button on <paramref name="panel"/>, placed by its top-left corner, that the keyboard
    /// reaches through <paramref name="keys"/>. A click does nothing while work is going (<see cref="Busy"/>).
    /// </summary>
    internal UiButton AddButton(RectTransform panel, List<Control> keys, float x, float y, float w, float h, string text, Action click, Func<bool>? shown = null)
    {
        var b = Ui.MakeButton(panel, text, () => { if (!Busy) click(); });
        PlaceTop(b.Rect, x, y, w, h);
        if (shown != null) b.Visible = shown;
        keys.Add(new Control { Button = b, Shown = shown, Activate = click });
        return b;
    }

    /// <summary>A row showing a label and a value; clicking it types a new value.</summary>
    internal UiButton AddField(RectTransform panel, List<Control> keys, float x, ref float y, float w, TextField field, Func<bool>? shown = null, float h = RowH)
    {
        var b = AddButton(panel, keys, x, y, w, h, "", () => StartTyping(field), shown);
        b.Text = () => FieldText(field, b);
        b.Active = () => Typing == field;
        b.Label.alignment = field.Tall ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.Left;
        b.Label.margin = new Vector4(16, field.Tall ? 10 : 0, 12, field.Tall ? 8 : 0);
        b.Label.fontSize = 19;
        if (field.Tall)
        {
            // A long text ends in "..." when it doesn't fit; while it's typed, its end shows instead (FieldText).
            b.Label.enableWordWrapping = true;
            b.Label.overflowMode = TextOverflowModes.Ellipsis;
        }
        y -= h + (RowStep - RowH);
        return b;
    }

    /// <summary>A row with a label and a value that picks from a list when clicked.</summary>
    internal UiButton AddChoice(RectTransform panel, List<Control> keys, float x, ref float y, float w, string label, Func<string> value, Action click, Func<bool>? shown = null)
    {
        var b = AddButton(panel, keys, x, y, w, RowH, "", click, shown);
        b.Text = () => $"<color=#9D92B4>{label}</color><pos=32%>{value()}";
        b.Label.alignment = TextAlignmentOptions.Left;
        b.Label.margin = new Vector4(16, 0, 12, 0);
        b.Label.fontSize = 19;
        y -= RowStep;
        return b;
    }

    /// <summary>A button that shows a setting and flips it; lit while it's on.</summary>
    internal UiButton AddToggle(RectTransform panel, List<Control> keys, float x, ref float y, float w, Func<string> text, Func<bool> on, Action flip, Func<bool>? shown = null)
    {
        var b = AddButton(panel, keys, x, y, w, RowH, "", flip, shown);
        b.Text = text;
        b.Active = on;
        b.Label.fontSize = 19;
        y -= RowStep;
        return b;
    }

    /// <summary>"-" and "+" around a value.</summary>
    internal void AddStepper(RectTransform panel, List<Control> keys, float x, ref float y, float w, Func<string> value, Action less, Action more, Func<bool>? shown = null)
    {
        AddButton(panel, keys, x, y, 56, RowH, "-", less, shown);
        var label = MakeText("Value", panel, 19, TextAlignmentOptions.Center);
        PlaceTop(label.rectTransform, x + 60, y, w - 120, RowH);
        var live = Ui.AddLiveText(label, value);
        if (shown != null) live.Visible = shown;
        AddButton(panel, keys, x + w - 56, y, 56, RowH, "+", more, shown);
        y -= RowStep;
    }

    internal void AddHeader(RectTransform panel, float x, ref float y, float w, string text, Func<bool>? shown = null)
    {
        var t = MakeText(text, panel, 15, TextAlignmentOptions.Left);
        t.text = text.ToUpperInvariant();
        t.color = DimText;
        PlaceTop(t.rectTransform, x + 2, y, w, 20);
        if (shown != null) Ui.AddLiveText(t, () => text.ToUpperInvariant()).Visible = shown;
        y -= 28;
    }

    /// <summary>Text worked out each frame (rich text allowed; escape anything from files).</summary>
    internal TMP_Text AddText(RectTransform panel, float x, ref float y, float w, float h, Func<string> text, float size = 18, Func<bool>? shown = null)
    {
        var t = MakeText("Text", panel, size, TextAlignmentOptions.TopLeft);
        t.color = DimText;
        PlaceTop(t.rectTransform, x + 2, y, w, h);
        var live = Ui.AddLiveText(t, text);
        if (shown != null) live.Visible = shown;
        y -= h + 8;
        return t;
    }

    private Image? focusMarker;
    // The button the marker sits on (it is moved into that button, just left of it).
    private UiButton? markerOn;

    /// <summary>The keyboard's marker: a bar left of the button it's on (see <see cref="DrawFocus"/>).</summary>
    internal void BuildFocusMarker(RectTransform parent)
    {
        focusMarker = MakeImage("Focus", parent, Accent);
        focusMarker.gameObject.SetActive(false);
    }

    /// <summary>Shows the keyboard's marker left of <paramref name="on"/>'s button, or hides it for null.</summary>
    internal void DrawFocus(Control? on)
    {
        if (on != null)
        {
            if (markerOn != on.Button)
            {
                // Inside the button, just left of it, so it goes wherever the button is (a page or the bottom bar).
                var rect = focusMarker!.rectTransform;
                rect.SetParent(on.Button.Rect, false);
                rect.anchorMin = new Vector2(0, 0);
                rect.anchorMax = new Vector2(0, 1);
                rect.pivot = new Vector2(1, 0.5f);
                rect.sizeDelta = new Vector2(6, 0);
                rect.anchoredPosition = new Vector2(-8, 0);
                markerOn = on.Button;
            }
            if (!focusMarker!.gameObject.activeSelf) focusMarker.gameObject.SetActive(true);
        }
        else if (focusMarker!.gameObject.activeSelf) focusMarker.gameObject.SetActive(false);
    }

    /// <summary>
    /// Up/Down move the keyboard's marker through <paramref name="shown"/> (the buttons it can
    /// reach now, in order), and Enter presses the one it's on.
    /// </summary>
    internal static void WalkControls(InputKeyboard k, List<Control> shown, ref int focus)
    {
        if (Pressed(k, Key.DownArrow)) focus = shown.Count == 0 ? -1 : Math.Min(shown.Count - 1, focus + 1);
        if (Pressed(k, Key.UpArrow)) focus = shown.Count == 0 ? -1 : Math.Max(0, focus - 1);
        if ((Pressed(k, Key.Enter) || Pressed(k, Key.NumpadEnter)) && focus >= 0 && focus < shown.Count) shown[focus].Activate();
    }

    // ---- the page's frame ------------------------------------------------------------------------

    /// <summary>A bar the width of <paramref name="parent"/> along its top or bottom edge, in the panel colour.</summary>
    internal static RectTransform AddBar(RectTransform parent, string name, bool top, float height)
    {
        var bar = MakeImage(name, parent, PanelColor).rectTransform;
        bar.anchorMin = new Vector2(0, top ? 1 : 0);
        bar.anchorMax = new Vector2(1, top ? 1 : 0);
        bar.pivot = new Vector2(0.5f, top ? 1 : 0);
        bar.sizeDelta = new Vector2(0, height);
        bar.anchoredPosition = Vector2.zero;
        return bar;
    }

    /// <summary>A column down the left of <paramref name="parent"/>, between a top bar and a bottom bar, in the panel colour.</summary>
    internal static RectTransform AddSideColumn(RectTransform parent, string name, float width, float top, float bottom)
    {
        var column = MakeImage(name, parent, PanelColor).rectTransform;
        column.anchorMin = new Vector2(0, 0);
        column.anchorMax = new Vector2(0, 1);
        column.pivot = new Vector2(0, 0.5f);
        column.offsetMin = new Vector2(0, bottom);
        column.offsetMax = new Vector2(width, -top);
        return column;
    }
}

/// <summary>A value typed on a page: how to show it, and how to keep what was typed (Set throws InvalidDataException to refuse it).</summary>
/// <remarks>
/// It sits beside <see cref="EditorPageKit"/> rather than inside it: the game has a TextField of
/// its own in the global namespace (under BepInEx), which a page's "using static" of the kit
/// wouldn't win over; in this namespace, this one does.
/// </remarks>
internal sealed class TextField
{
    internal string Label = "";
    internal Func<string> Get = () => "";
    internal Action<string> Set = _ => { };
    /// <summary>What an empty value shows, like the enemy's own stat.</summary>
    internal Func<string>? Empty;
    internal int Max = 80;
    internal bool MultiLine;
    /// <summary>Tall and wrapped like a multi-line text, but one line: Enter ends it (a line of dialogue).</summary>
    internal bool Wrap;
    internal bool Tall => MultiLine || Wrap;
    internal string Hint = "Type, then Enter. Esc cancels.";
    /// <summary>
    /// Whether it can be typed in now, asked when typing would start; false keeps it closed, and
    /// says why itself (like a part of the enemy while the enemy's file can't be read).
    /// </summary>
    internal Func<bool>? MayEdit;
    /// <summary>More for the typing hint, worked out from what is typed (like how many lines it takes).</summary>
    internal Func<string, string>? Measure;
    /// <summary>Letters that typing leaves out, and what it says when one is typed.</summary>
    internal char[]? Refused;
    internal string RefusedText = "";
}
