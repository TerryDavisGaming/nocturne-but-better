using System.Globalization;
using System.Text;
using UnityEngine;
using static NocturneFlatScroll.EditorUi;
using InputMouse = UnityEngine.InputSystem.Mouse;

namespace NocturneFlatScroll;

// A custom battle's lines during the song (battle.json's dialogue), which belong to the song's
// timeline: a violet lane beside the event flags shows each line from its start to its end (a
// line that stops the song as a line across the lanes), the timeline bar marks them, and the
// Events tab's Dialogue view adds, moves and times them. The lines stay in the battle creator's
// draft (BattleChartTarget.Dialogue), which the editor updates after each change, so tests play
// them at once; Save saves the creator's battle.json too. Expressions, pictures and the preview
// are on the creator's Dialogue page.
internal static partial class ChartEditor
{
    private static readonly Color DialogueColor = Hex(0xC77DFF);
    // Dialogue labels end this far left of the lanes (events' end at 24), and sit this far below
    // an event label at the same time.
    private const float DialogueLabelRight = 30, DialogueLabelDrop = 14;
    private const float BoxLengthStep = 0.5f;
    private static readonly char[] BoxRefused = { '<', '>', '{', '}' };

    private static DialogueLink? dialogueLink;
    // The lines during the song, in time order (lines without a time last), and the picked one.
    private static List<DialogueCue> cues = new();
    private static int cueIndex = -1;
    // Whether the lines changed since the last save, so Save saves the creator's battle.json too.
    private static bool cuesChanged;
    // The Events tab shows the dialogue lines instead of the events.
    private static bool dialogueView;
    // While Speaker... is picking: who can say the line, and the first shown in the list.
    private static List<(string Id, string Label)>? speakerChoices;
    private static int speakerFirst;
    private static string lastCueSpeaker = DialogueReader.Narrator;

    private static DialogueCue? PickedCue => cueIndex >= 0 && cueIndex < cues.Count ? cues[cueIndex] : null;

    // ---- loading --------------------------------------------------------------------------------

    /// <summary>The battle's lines for a new editing session: the creator's, else battle.json's to look at.</summary>
    private static void LoadDialogue(BattleChartTarget target)
    {
        dialogueLink = target.Dialogue ?? ReadOnlyDialogue(target.Folder);
        try { cues = dialogueLink.Get(); }
        catch (Exception ex)
        {
            ModLog.Error("Chart editor: the battle's dialogue couldn't be read: " + ex.Message);
            cues = new List<DialogueCue>();
        }
        SortCues();
        cueIndex = cues.Count > 0 ? 0 : -1;
        cuesChanged = false;
        dialogueView = false;
        speakerChoices = null;
    }

    // A battle opened from its folder (not from the creator): its saved lines, to look at only.
    private static DialogueLink ReadOnlyDialogue(string folder)
    {
        try
        {
            var draft = BattleDraft.Load(folder);
            return new DialogueLink
            {
                Get = draft.DuringCues,
                NameOf = id => draft.SpeakerKeys().Any(k => k.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))
                    ? draft.SpeakerText(id.Trim(), "name") ?? id : TidyId(id),
            };
        }
        catch (Exception ex)
        {
            // The chart opens all the same, without lines.
            ModLog.Info("Chart editor: the battle's dialogue wasn't read (" + ex.Message + ").");
            return new DialogueLink();
        }
    }

    // "NPC_Abbot" shows as "Abbot", as the game does without a name of its own.
    private static string TidyId(string id)
    {
        id = id.Trim();
        if (id.Length == 0) return DialogueReader.Narrator;
        return (id.StartsWith("NPC_", StringComparison.Ordinal) ? id.Substring(4) : id).Replace('_', ' ');
    }

    private static void ClearDialogue()
    {
        dialogueLink = null;
        cues = new List<DialogueCue>();
        cueIndex = -1;
        cuesChanged = false;
        dialogueView = false;
        speakerChoices = null;
    }

    // ---- times ----------------------------------------------------------------------------------

    /// <summary>When a line starts, in seconds on the chart's clock (a beat moves with the tempo); null without a time.</summary>
    private static double? CueTime(DialogueCue cue) => cue.Beat is double beat ? chart!.BeatToSeconds(beat) : cue.Time;

    /// <summary>How long a live line shows: its own time, else worked out from its text as the battle does.</summary>
    private static double CueSeconds(DialogueCue cue) =>
        cue.Duration is double d ? Math.Clamp(d, DialogueReader.MinDuration, DialogueReader.MaxDuration) : DialogueReader.LiveSeconds(DialogueReader.CleanText(cue.Text, out _));

    // In time order, lines at the same time as they were; the picked line stays picked.
    private static void SortCues()
    {
        var picked = PickedCue;
        cues = cues.OrderBy(c => chart != null ? CueTime(c) ?? double.MaxValue : 0).ToList();
        if (picked != null) cueIndex = cues.IndexOf(picked);
    }

    private static string CueLabel(DialogueCue cue)
    {
        string who = (cue.Name ?? "").Trim().Length > 0 ? cue.Name!.Trim() : NameOf(cue.Speaker);
        string text = DialogueReader.CleanText(cue.Text, out _);
        return $"{who}: {(text.Length > 0 ? text : "(no text yet)")}";
    }

    private static string NameOf(string speaker)
    {
        try { return dialogueLink?.NameOf(speaker) ?? speaker; }
        catch (Exception) { return speaker; }
    }

    // ---- drawing ----------------------------------------------------------------------------------

    /// <summary>The lines on the playfield: a violet box from each line's start to its end, and a line across the lanes where the song stops.</summary>
    private static void DrawDialogueLane(double now, double bottom, double top, float width, ref int images, ref int labels)
    {
        float halfH = FieldHeight / 2f;
        double lastStop = double.NaN;
        for (int i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            if (CueTime(cue) is not double start) continue;
            bool picked = tab == Tab.Events && dialogueView && i == cueIndex;
            if (cue.Pause)
            {
                // Lines that stop the song at the same time are one stop.
                if (start < bottom || start > top || start == lastStop) continue;
                lastStop = start;
                float y = TimeToY(start, now);
                PlaceField(Pooled(notePool, images++, field!, "Note"), 0, y, width, 2, picked ? Accent : new Color(DialogueColor.r, DialogueColor.g, DialogueColor.b, 0.9f));
                if (y > -halfH && y < halfH) Label(labels++, "talk: song stops", y - DialogueLabelDrop, picked ? Accent : DialogueColor, DialogueLabelRight);
                continue;
            }
            double end = start + CueSeconds(cue);
            if (end < bottom || start > top) continue;
            float y0 = TimeToY(start, now), y1 = TimeToY(end, now);
            PlaceField(Pooled(notePool, images++, field!, "Note"), FieldLeft - 21, (y0 + y1) / 2f, 8, Math.Max(4f, y1 - y0),
                picked ? new Color(Accent.r, Accent.g, Accent.b, 0.6f) : new Color(DialogueColor.r, DialogueColor.g, DialogueColor.b, 0.35f));
            if (y0 > -halfH && y0 < halfH) Label(labels++, CueLabel(cue), y0 - DialogueLabelDrop, picked ? Accent : DialogueColor, DialogueLabelRight);
        }
    }

    // ---- the Events tab's Dialogue view ------------------------------------------------------------

    private static void BuildDialogueView(float width)
    {
        // Events or Dialogue, above the list.
        var views = new (string Label, bool Dialogue)[] { ("Events", false), ("Dialogue", true) };
        for (int i = 0; i < views.Length; i++)
        {
            var (label, dialogue) = views[i];
            var b = Ui.MakeButton(rightPanel!, label, () => SetEventsView(dialogue));
            b.Active = () => dialogueView == dialogue;
            b.Visible = () => tab == Tab.Events;
            b.Label.fontSize = 17;
            PlaceTop(b.Rect, 16 + i * (width / 2 + 4), -218, width / 2 - 4, 36);
        }
        var buttons = new (string Label, Action Do)[]
        {
            ("Add line here", AddCueHere), ("Edit text", EditCueText),
            ("Speaker...", PickCueSpeaker), ("Move here", MoveCueHere),
            ("Earlier", () => NudgeCue(-1)), ("Later", () => NudgeCue(1)),
            ("Shorter", () => ChangeCueLength(-BoxLengthStep)), ("Longer", () => ChangeCueLength(BoxLengthStep)),
            ("Stops song", ToggleCueStops), ("Copy here", CopyCueHere),
            ("Delete", DeleteCue), ("More in the creator", ShowCueInCreator),
        };
        for (int i = 0; i < buttons.Length; i++)
        {
            var (label, doIt) = buttons[i];
            var b = Ui.MakeButton(rightPanel!, label, doIt);
            b.Visible = () => tab == Tab.Events && dialogueView && speakerChoices == null;
            b.Label.fontSize = 16;
            if (label == "Stops song") b.Active = () => PickedCue?.Pause == true;
            PlaceTop(b.Rect, 16 + (i % 2) * (width / 2 + 4), -262 - EventRowsVisible * 32 - 8 - (i / 2) * 40, width / 2 - 4, 35);
        }
    }

    private static void SetEventsView(bool dialogue)
    {
        if (typing != TextField.None) return;
        dialogueView = dialogue;
        speakerChoices = null;
    }

    private static bool DialogueRowShown(int row) =>
        speakerChoices != null ? speakerFirst + row < speakerChoices.Count : eventFirst + row < cues.Count;

    private static void UpdateDialogueRows()
    {
        if (speakerChoices != null)
        {
            // The mouse wheel over the panel scrolls the speakers.
            var mouse = InputMouse.current;
            float wheel = mouse != null ? mouse.scroll.ReadValue().y : 0;
            if (wheel != 0 && rightPanel && RectTransformUtility.RectangleContainsScreenPoint(rightPanel, mouse!.position.ReadValue(), null))
                speakerFirst += wheel > 0 ? -1 : 1;
            speakerFirst = Math.Clamp(speakerFirst, 0, Math.Max(0, speakerChoices.Count - EventRowsVisible));
            string current = PickedCue?.Speaker.Trim() ?? "";
            for (int i = 0; i < eventRows.Count; i++)
            {
                int index = speakerFirst + i;
                if (index >= speakerChoices.Count) continue;
                eventRows[i].Label.text = Escape(speakerChoices[index].Label);
                bool same = speakerChoices[index].Id.Equals(current, StringComparison.OrdinalIgnoreCase);
                eventRows[i].Active = () => same;
            }
            return;
        }
        eventFirst = Math.Clamp(cueIndex - EventRowsVisible / 2, 0, Math.Max(0, cues.Count - EventRowsVisible));
        for (int i = 0; i < eventRows.Count; i++)
        {
            int index = eventFirst + i;
            if (index >= cues.Count) continue;
            var cue = cues[index];
            string when = CueTime(cue) is double t ? FormatTime(t) : "no time";
            eventRows[i].Label.text = $"{when}  {Escape(CueLabel(cue))}{(cue.Pause ? "  <color=#9D92B4>(stops the song)</color>" : "")}";
            bool picked = index == cueIndex;
            eventRows[i].Active = () => picked;
        }
    }

    private static void ClickDialogueRow(int row)
    {
        if (typing != TextField.None) { Say("Press Enter to finish typing first (Esc cancels it).", 3f); return; }
        if (speakerChoices is { } choices)
        {
            int index = speakerFirst + row;
            if (index < choices.Count) SetCueSpeaker(choices[index].Id);
            return;
        }
        if (cues.Count == 0) return;
        cueIndex = Math.Clamp(eventFirst + row, 0, cues.Count - 1);
        if (CueTime(cues[cueIndex]) is double t) SeekTo(t);
    }

    private static string DialogueEventsText()
    {
        if (speakerChoices != null)
            return "Who says this line? Click a speaker. Esc goes back.\n\n" +
                   "Here are the battle's own speakers and the game characters its lines already use. " +
                   "Anyone else, and the lines' faces, are on the Battle creator's Dialogue page.";
        var sb = new StringBuilder();
        var cue = PickedCue;
        if (cue == null) sb.Append("No lines during the song yet. \"Add line here\" makes one at the play position.\n");
        else
        {
            string when = CueTime(cue) is double t ? FormatTime(t) : "no time";
            string beat = cue.Beat is double b ? $" (beat {b.ToString("0.##", CultureInfo.InvariantCulture)})" : "";
            string length = cue.Pause
                ? cue.Duration is double d ? $"stops the song, goes on after {d:0.#} s" : "stops the song until a key"
                : $"shows for {(cue.Duration != null ? "" : "about ")}{CueSeconds(cue):0.#} s";
            sb.Append($"<color=#EAE6F5>At {when}{beat}, {length}</color>\n");
            sb.Append(Escape(CueLabel(cue))).Append('\n');
        }
        sb.Append("\nDialogue lines are shared by every difficulty. Expressions, pictures and the preview are on the Battle creator's Dialogue page.");
        if (dialogueLink?.ReadOnly != false) sb.Append(" <color=#F2B02E>These are battle.json's lines, to look at: open the chart from the Battle creator to change them.</color>");
        return sb.ToString();
    }

    // ---- changes ----------------------------------------------------------------------------------

    /// <summary>Whether the lines can change here; says why not when they can't.</summary>
    private static bool CuesEditable()
    {
        // Typed text goes to the picked line once Enter takes it, so the lines wait until then.
        if (typing != TextField.None) { Say("Press Enter to finish typing first (Esc cancels it).", 3f); return false; }
        if (dialogueLink is { ReadOnly: false }) return true;
        Say("These lines are only to look at here: open the chart from the Battle creator to change them.", 5f);
        return false;
    }

    private static bool CueEditable()
    {
        if (!CuesEditable()) return false;
        if (PickedCue != null) return true;
        Say("Pick a line first (or add one).", 2f);
        return false;
    }

    // After every change: the list in time order, and the creator's draft told, so a test plays it.
    private static void CuesEdited()
    {
        SortCues();
        cuesChanged = true;
        dirty = true;
        SendCues();
    }

    private static void SendCues()
    {
        try
        {
            if (dialogueLink?.Set is not { } set) return;
            set(cues.Select(c => c.Copy()).ToList());
            // The draft has them in this order now, which More in the creator goes by.
            for (int i = 0; i < cues.Count; i++) cues[i].Place = i;
        }
        catch (Exception ex)
        {
            ModLog.Error("Chart editor: the battle creator didn't take the dialogue lines: " + ex);
            Say("The dialogue lines couldn't go to the Battle creator: " + ex.Message, 6f);
        }
    }

    // The play position on the snap, as a beat (lines placed here move with the notes).
    private static double SnappedBeat() => SnapRow(chart!.SecondsToRow(Now)) / (double)EditorChart.RowsPerBeat;

    private static void AddCueHere()
    {
        if (!CuesEditable()) return;
        if (cues.Count >= DialogueReader.MaxLines(DialogueSection.During))
        {
            Say($"The battle plays at most {DialogueReader.MaxLines(DialogueSection.During)} lines during the song.", 4f);
            return;
        }
        PushUndo();
        string speaker = PickedCue?.Speaker is { Length: > 0 } picked ? picked : lastCueSpeaker;
        var added = new DialogueCue { Speaker = speaker, Beat = SnappedBeat() };
        cues.Add(added);
        cueIndex = cues.Count - 1;
        CuesEdited();
        StartTyping(TextField.DialogueText);
    }

    private static void EditCueText()
    {
        if (CueEditable()) StartTyping(TextField.DialogueText);
    }

    private static void MoveCueHere()
    {
        if (!CueEditable()) return;
        PushUndo();
        var cue = PickedCue!;
        cue.Beat = SnappedBeat();
        cue.Time = null;
        CuesEdited();
    }

    // One snap earlier or later; a line written at a time goes onto the beats.
    private static void NudgeCue(int snaps)
    {
        if (!CueEditable()) return;
        var cue = PickedCue!;
        if (CueTime(cue) is not double time) { Say("This line has no time yet: Move here gives it the play position.", 3f); return; }
        PushUndo();
        double row = chart!.SecondsToRow(time) + snaps * SnapRows;
        cue.Beat = Math.Max(0, SnapRow(row)) / (double)EditorChart.RowsPerBeat;
        cue.Time = null;
        CuesEdited();
    }

    // Half a second shorter or longer; the first press makes the worked-out length a set one.
    private static void ChangeCueLength(double seconds)
    {
        if (!CueEditable()) return;
        PushUndo();
        var cue = PickedCue!;
        cue.Duration = Math.Clamp(Math.Round((cue.Duration ?? CueSeconds(cue)) + seconds, 2), DialogueReader.MinDuration, DialogueReader.MaxDuration);
        CuesEdited();
        Say(cue.Pause ? $"It goes on by itself after {cue.Duration:0.#} s." : $"It shows for {cue.Duration:0.#} s.", 2f);
    }

    private static void ToggleCueStops()
    {
        if (!CueEditable()) return;
        PushUndo();
        var cue = PickedCue!;
        cue.Pause = !cue.Pause;
        CuesEdited();
        Say(cue.Pause ? "Stops the song: the song waits here for a key, like a boss's talk between songs." : "The box comes and goes while the notes keep coming.", 4f);
    }

    private static void CopyCueHere()
    {
        if (!CueEditable()) return;
        if (cues.Count >= DialogueReader.MaxLines(DialogueSection.During))
        {
            Say($"The battle plays at most {DialogueReader.MaxLines(DialogueSection.During)} lines during the song.", 4f);
            return;
        }
        PushUndo();
        var copy = PickedCue!.Copy();
        copy.Beat = SnappedBeat();
        copy.Time = null;
        cues.Add(copy);
        cueIndex = cues.Count - 1;
        CuesEdited();
    }

    private static void DeleteCue()
    {
        if (!CueEditable()) return;
        PushUndo();
        cues.RemoveAt(cueIndex);
        cueIndex = Math.Min(cueIndex, cues.Count - 1);
        CuesEdited();
        Say($"Deleted the line. {ShortKey(EditorAction.Undo)} brings it back.", 3f);
    }

    private static void PickCueSpeaker()
    {
        if (!CueEditable()) return;
        List<(string Id, string Label)> choices;
        try { choices = dialogueLink!.Speakers(); }
        catch (Exception ex)
        {
            ModLog.Error("Chart editor: the battle's speakers couldn't be listed: " + ex.Message);
            choices = new List<(string, string)>();
        }
        if (!choices.Any(c => c.Id.Equals(DialogueReader.Narrator, StringComparison.OrdinalIgnoreCase)))
            choices.Add((DialogueReader.Narrator, "Narrator (no picture)"));
        speakerChoices = choices;
        string current = PickedCue!.Speaker.Trim();
        speakerFirst = Math.Max(0, choices.FindIndex(c => c.Id.Equals(current, StringComparison.OrdinalIgnoreCase)) - EventRowsVisible / 2);
    }

    private static void SetCueSpeaker(string id)
    {
        speakerChoices = null;
        var cue = PickedCue;
        if (cue == null || !CuesEditable() || cue.Speaker == id) return;
        PushUndo();
        cue.Speaker = id;
        // Faces belong to their speaker: the line starts on the new one's usual face.
        cue.Expression = null;
        lastCueSpeaker = id;
        CuesEdited();
    }

    /// <summary>Closes the editor (asking about unsaved changes first) and opens the picked line on the creator's Dialogue page.</summary>
    private static void ShowCueInCreator()
    {
        if (!CuesEditable() || dialogueLink?.ShowInCreator == null) return;
        // By its place in the draft: the editor's list is in time order, the draft's as written.
        try { dialogueLink.ShowInCreator(PickedCue?.Place ?? -1); }
        catch (Exception ex) { ModLog.Error("Chart editor: the battle creator couldn't show the line: " + ex); }
        RequestClose();
    }

    // The typed text of the picked line, as the box can show it.
    private static void SetCueText(string text)
    {
        var cue = PickedCue;
        string clean = DialogueReader.CleanText(text, out _);
        if (clean.Length > DialogueReader.MaxText) clean = clean.Substring(0, DialogueReader.MaxText).TrimEnd();
        if (cue == null || clean == cue.Text) return;
        PushUndo();
        cue.Text = clean;
        lastCueSpeaker = cue.Speaker;
        CuesEdited();
    }

    // ---- undo and save ----------------------------------------------------------------------------

    private static List<DialogueCue>? CaptureCues() => battle != null ? cues.Select(c => c.Copy()).ToList() : null;

    private static void RestoreCues(List<DialogueCue>? saved)
    {
        if (saved == null || battle == null) return;
        var picked = PickedCue;
        // Undoing a change to the notes brings back the same lines: nothing to tell the creator.
        bool same = saved.Count == cues.Count && saved.Zip(cues).All(p => SameCue(p.First, p.Second));
        // The same lines keep their places in the draft, which may have changed since.
        if (same) for (int i = 0; i < saved.Count; i++) saved[i].Place = cues[i].Place;
        cues = saved;
        cueIndex = picked == null ? -1 : Math.Min(cueIndex, cues.Count - 1);
        speakerChoices = null;
        if (same || dialogueLink is not { ReadOnly: false }) return;
        cuesChanged = true;
        SendCues();
    }

    private static bool SameCue(DialogueCue a, DialogueCue b) =>
        a.Json == b.Json && a.Speaker == b.Speaker && a.Expression == b.Expression && a.Text == b.Text
        && a.Time == b.Time && a.Beat == b.Beat && a.Duration == b.Duration && a.Pause == b.Pause;

    /// <summary>After the chart is saved: the battle creator saves battle.json too when the lines changed. What to add to the status line.</summary>
    private static string SaveDialogue()
    {
        if (!cuesChanged || dialogueLink?.Save == null) return "";
        bool saved;
        try { saved = dialogueLink.Save(); }
        catch (Exception ex)
        {
            ModLog.Error("Chart editor: the battle creator's save failed: " + ex);
            saved = false;
        }
        if (!saved) return " battle.json with the dialogue lines wasn't saved: the Battle creator says why when it's back.";
        cuesChanged = false;
        ModLog.Info("Chart editor: the battle creator saved battle.json with the dialogue lines.");
        return " battle.json is saved too, with the dialogue lines (and the Battle creator's other unsaved changes).";
    }
}
