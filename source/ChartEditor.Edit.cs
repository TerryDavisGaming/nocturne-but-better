using System.Globalization;
using System.Text;
using UnityEngine;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;

namespace NocturneFlatScroll;

// Editing: the tools, the clock and music, selection, clipboard, undo, saving and exporting.
internal static partial class ChartEditor
{
    private enum DragKind { None, Hold, Box, Move, Scrub }
    private enum TextField { None, Title, Author, EventMods, ScrollSpeed }

    private const string AuthorPref = "NocturneFlatScroll.EditorAuthor.v1";

    private static EditorChart? chart;
    private static ChartText? header;              // timing, events and other tags the chart keeps
    private static CustomCharts.CustomChart? editing;
    private static string title = "", author = "";
    private static bool dirty, confirmLeave;
    private static readonly HashSet<long> selection = new();
    private static List<EditorChart.Note> clipboard = new();
    private static int clipboardLanes;
    private static Tool tool = Tool.Note;
    private static int snapIndex = 3;              // 1/4
    private static int speedIndex = Speeds.Length - 1;
    private static float pixelsPerSecond = 700f;
    private static bool ticksOn = true;
    private static bool ticksDirty = true;
    private static TextField typing;
    private static string typed = "";
    private static string message = "";
    private static float messageUntil;
    private static Task<string?>? exportDialog;

    // The clock: the music's when it's loaded, otherwise a plain timer.
    private static EditorAudio? audio;
    private static Task<SongAudio.Result>? loading;
    private static string musicState = "";
    private static double manualTime, manualLength = 120;
    private static bool manualPlaying;
    private static float[]? peaks;
    private static float peakRate;
    // Chart time of the music's first sample; negative when the music starts before beat 0.
    private static double audioOrigin;

    // Mouse state for the current frame.
    private static DragKind drag;
    private static int dragStartRow, dragLane, dragStartLane;
    private static double boxStartTime;
    private static float boxStartX, mouseX, mouseY;
    private static int hoverLane = -1, hoverRow;

    private static int SnapRows => EditorChart.RowsPerBeat / Snaps[snapIndex];
    private static double SongLength => audio != null ? audio.Length + audioOrigin : manualLength;
    private static double Now => audio != null ? audio.Time + audioOrigin : manualTime;
    private static double Start => audio != null ? Math.Min(0, audioOrigin) : 0;
    private static bool IsPlaying => audio?.Playing ?? manualPlaying;

    private static long NoteKey(EditorChart.Note n) => ((long)n.Row << 8) | (uint)n.Lane;

    private static void StartEditing(ChartText source, int block, CustomCharts.CustomChart? custom)
    {
        int lanes = CustomCharts.LanesOf(song!);
        if (lanes <= 0) lanes = source.Blocks.Count > 0 ? source.Blocks[0].Lanes : 4;
        header = source;
        editing = custom;
        chart = new EditorChart(lanes);
        chart.ReadTiming(source);
        // The game ignores #OFFSET in battles (its clock starts at the music's entry cue), so the
        // editor does too, or notes would sit where the game won't play them.
        chart.Offset = 0;
        if (block >= 0 && block < source.Blocks.Count) chart.ReadNotes(source.Blocks[block].Notes);
        LoadEvents(source, custom);
        LoadTiming(source);
        title = custom?.Title ?? (block >= 0 ? source.Blocks[block].DisplayName + " edit" : "New chart");
        author = custom?.Author ?? PlayerPrefs.GetString(AuthorPref, "");
        dirty = false;
        confirmLeave = false;
        undo.Clear();
        redo.Clear();
        selection.Clear();
        drag = DragKind.None;
        typing = TextField.None;
        rebinding = null;
        manualPlaying = false;
        manualTime = chart.Notes.Count > 0 ? Math.Max(0, chart.RowToSeconds(chart.Notes[0].Row) - 1) : 0;
        manualLength = Math.Max(120, chart.Notes.Count > 0 ? chart.RowToSeconds(chart.Notes.Max(n => n.LastRow)) + 10 : 0);
        ticksDirty = true;
        peaks = null;

        audio?.Dispose();
        audio = null;
        musicState = "Loading music...";
        string songName = song!.name;
        int m = melody;
        string root = SongAudio.MusicRoot;
        loading = Task.Run(() => SongAudio.Load(songName, m, root));

        ShowScreen(Screen.Edit);
        SetTab(Tab.Compose);
        BuildField();
        Say("Space plays. Click in a lane to place a note; right click deletes.", 5f);
    }

    private static void Say(string text, float seconds = 4f)
    {
        message = text;
        messageUntil = Time.unscaledTime + seconds;
    }

    // ---- the frame --------------------------------------------------------------------------

    private static void UpdateEdit(InputKeyboard keyboard, InputMouse? mouse)
    {
        FinishLoading();
        FinishExport();
        bool clicked = UpdateButtons(mouse);
        if (!IsOpen) return;
        if (exportDialog != null) { }
        else if (rebinding != null) UpdateRebinding(keyboard);
        else if (typing != TextField.None) UpdateTyping(keyboard);
        else if (!HandleKeys(keyboard)) return; // closed
        if (mouse != null && tab != Tab.Keys && exportDialog == null) HandleMouse(mouse, clicked);
        if (manualPlaying)
        {
            manualTime += Time.unscaledDeltaTime * Speeds[speedIndex];
            if (manualTime >= manualLength) { manualTime = manualLength; manualPlaying = false; }
        }
        if (ticksDirty) UpdateTicks();
        if (selection.Count > 0) selection.IntersectWith(chart!.Notes.Select(NoteKey));
        double now = Now;
        if (tab != Tab.Keys) DrawField(now);
        DrawPanels(now);
    }

    private static void FinishLoading()
    {
        if (loading == null || !loading.IsCompleted) return;
        var task = loading;
        loading = null;
        if (task.IsFaulted || task.Result == null)
        {
            var reason = task.Exception?.InnerException?.Message ?? "no music found";
            musicState = "No music: " + reason;
            ModLog.Error($"Chart editor music for {song?.name} melody {melody} couldn't load: {task.Exception?.InnerException ?? task.Exception}");
            return;
        }
        try
        {
            var result = task.Result;
            bool wasPlaying = manualPlaying;
            manualPlaying = false;
            audioOrigin = result.Origin;
            audio = new EditorAudio(result.Stereo, result.SampleRate);
            audio.SetSpeed(Speeds[speedIndex]);
            audio.SetMusicVolume(musicVolume);
            audio.Seek(manualTime - audioOrigin);
            peaks = result.Peaks;
            peakRate = result.PeakRate;
            musicState = $"Music {FormatTime(audio.Length)}";
            ticksDirty = true;
            if (wasPlaying) audio.Play();
        }
        catch (Exception ex)
        {
            musicState = "No music: " + ex.Message;
            ModLog.Error("Chart editor audio failed: " + ex);
        }
    }

    /// <summary>Hands the note ticks and the metronome's beats to the player.</summary>
    private static void UpdateTicks()
    {
        ticksDirty = false;
        if (audio == null) return;
        var notes = ticksOn
            ? chart!.Notes.Where(n => n.Type != 'M').Select(n => n.Row).Distinct().Select(r => chart.RowToSeconds(r) - audioOrigin).ToArray()
            : Array.Empty<double>();
        Array.Sort(notes);
        var beats = new List<double>();
        if (metronomeOn)
            for (int b = 0; ; b++)
            {
                double t = chart!.BeatToSeconds(b);
                if (t > SongLength) break;
                if (t >= Start) beats.Add(t - audioOrigin);
            }
        audio.SetTicks(notes, beats.ToArray(), tickVolume);
    }

    // ---- keyboard ---------------------------------------------------------------------------

    /// <returns>False when the editor closed.</returns>
    private static bool HandleKeys(InputKeyboard k)
    {
        if (Pressed(k, Key.Escape))
        {
            if (drag != DragKind.None) { drag = DragKind.None; return true; }
            if (selection.Count > 0) { selection.Clear(); return true; }
            return RequestCloseKeepOpen();
        }
        if (k.anyKey.wasPressedThisFrame) confirmLeave = false;

        if (Triggered(k, EditorAction.PlayPause)) TogglePlay();
        if (Triggered(k, EditorAction.SnapForward)) Step(SnapRows);
        if (Triggered(k, EditorAction.SnapBack)) Step(-SnapRows);
        if (Triggered(k, EditorAction.BeatForward)) Step(EditorChart.RowsPerBeat);
        if (Triggered(k, EditorAction.BeatBack)) Step(-EditorChart.RowsPerBeat);
        if (Triggered(k, EditorAction.MeasureForward)) Step(EditorChart.RowsPerMeasure);
        if (Triggered(k, EditorAction.MeasureBack)) Step(-EditorChart.RowsPerMeasure);
        if (Triggered(k, EditorAction.GoStart)) SeekTo(Start);
        if (Triggered(k, EditorAction.GoEnd)) SeekTo(chart!.Notes.Count > 0 ? chart.RowToSeconds(chart.Notes.Max(n => n.LastRow)) : SongLength);
        if (Triggered(k, EditorAction.SpeedDown)) SetSpeed(speedIndex - 1);
        if (Triggered(k, EditorAction.SpeedUp)) SetSpeed(speedIndex + 1);
        if (Triggered(k, EditorAction.SnapFiner)) snapIndex = (snapIndex + 1) % Snaps.Length;
        if (Triggered(k, EditorAction.SnapCoarser)) snapIndex = (snapIndex + Snaps.Length - 1) % Snaps.Length;
        if (Triggered(k, EditorAction.ZoomIn)) Zoom(1.2f);
        if (Triggered(k, EditorAction.ZoomOut)) Zoom(1 / 1.2f);
        if (Triggered(k, EditorAction.ToolSelect)) SetTool(Tool.Select);
        if (Triggered(k, EditorAction.ToolNote)) SetTool(Tool.Note);
        if (Triggered(k, EditorAction.ToolHold)) SetTool(Tool.Hold);
        if (Triggered(k, EditorAction.ToolMine)) SetTool(Tool.Mine);
        if (Triggered(k, EditorAction.NoteTicks)) { ToggleTicks(); Say(ticksOn ? "Note ticks on" : "Note ticks off", 1.5f); }
        if (Triggered(k, EditorAction.Metronome)) { ToggleMetronome(); Say(metronomeOn ? "Metronome on" : "Metronome off", 1.5f); }
        if (Triggered(k, EditorAction.AddBookmark)) ToggleBookmark();
        if (Triggered(k, EditorAction.NextBookmark)) JumpBookmark(1);
        if (Triggered(k, EditorAction.PrevBookmark)) JumpBookmark(-1);
        if (Triggered(k, EditorAction.Undo)) Undo();
        if (Triggered(k, EditorAction.Redo)) Redo();
        if (Triggered(k, EditorAction.SelectAll)) SelectAll();
        if (Triggered(k, EditorAction.Copy)) Copy();
        if (Triggered(k, EditorAction.Cut)) { Copy(); DeleteSelection(); }
        if (Triggered(k, EditorAction.Paste)) Paste();
        if (Triggered(k, EditorAction.Delete)) DeleteSelection();
        if (Triggered(k, EditorAction.Mirror)) Mirror();
        if (Triggered(k, EditorAction.Reverse)) Reverse();
        if (Triggered(k, EditorAction.NudgeLater)) MoveSelection(SnapRows, 0);
        if (Triggered(k, EditorAction.NudgeEarlier)) MoveSelection(-SnapRows, 0);
        if (Triggered(k, EditorAction.NudgeLeft)) MoveSelection(0, -1);
        if (Triggered(k, EditorAction.NudgeRight)) MoveSelection(0, 1);
        if (Triggered(k, EditorAction.Resnap)) Resnap();
        if (Triggered(k, EditorAction.Save)) Save();
        if (Triggered(k, EditorAction.Export)) StartExportPack();
        if (Triggered(k, EditorAction.Rename)) StartTyping(TextField.Title);
        if (Triggered(k, EditorAction.SetAuthor)) StartTyping(TextField.Author);
        return true;
    }

    /// <summary>The Exit button: asks first when something isn't saved.</summary>
    private static void RequestClose() => RequestCloseKeepOpen();

    private static bool RequestCloseKeepOpen()
    {
        if (dirty && !confirmLeave)
        {
            confirmLeave = true;
            Say("Unsaved changes. Save first, or press Esc (or Exit) again to leave without saving.", 6f);
            return true;
        }
        Close();
        return false;
    }

    private static void StartTyping(TextField fieldToType)
    {
        if (fieldToType == TextField.EventMods && PickedEvent == null) { Say("Pick an event first (or add one)", 2f); return; }
        typing = fieldToType;
        typed = fieldToType switch
        {
            TextField.Title => title,
            TextField.Author => author,
            TextField.EventMods => PickedEvent!.Mods,
            TextField.ScrollSpeed => "",
            _ => "",
        };
        Say(fieldToType switch
        {
            TextField.EventMods => "Type the event (a verb and its values), Enter to finish, Esc to cancel.",
            TextField.ScrollSpeed => "Type how fast the notes scroll from here, like 0.5 or 2, then Enter.",
            _ => "Type, then Enter to finish (Esc cancels).",
        }, 60f);
    }

    /// <summary>
    /// Typed text goes into the .sm file, where ':' and ';' end a value, '//' starts a comment and
    /// a '#' at the start of a line starts a tag, so those are left out.
    /// </summary>
    private static string CleanText(string text)
    {
        var clean = text.Replace(":", " ").Replace(";", " ");
        while (clean.Contains("//")) clean = clean.Replace("//", "/");
        return clean.Trim().TrimStart('#').Trim();
    }

    private static void UpdateTyping(InputKeyboard k)
    {
        int max = typing == TextField.EventMods ? 160 : typing == TextField.ScrollSpeed ? 6 : 40;
        TypeInto(k, ref typed, max);
        if (Pressed(k, Key.Escape)) { typing = TextField.None; Say("", 0f); return; }
        if (!Pressed(k, Key.Enter) && !Pressed(k, Key.NumpadEnter)) return;
        switch (typing)
        {
            case TextField.Title:
                title = CleanText(typed).Length > 0 ? CleanText(typed) : "New chart";
                dirty = true;
                break;
            case TextField.Author:
                author = CleanText(typed);
                PlayerPrefs.SetString(AuthorPref, author);
                PlayerPrefs.Save();
                dirty = true;
                break;
            case TextField.EventMods:
                if (PickedEvent != null && CleanText(typed) != PickedEvent.Mods)
                {
                    PushUndo();
                    PickedEvent.Mods = CleanText(typed);
                    EventsEdited();
                }
                break;
            case TextField.ScrollSpeed:
                if (double.TryParse(typed.Trim().TrimStart('x', 'X'), NumberStyles.Float, CultureInfo.InvariantCulture, out double ratio) && ratio > 0) SetScrollHere(ratio);
                else Say("That isn't a speed; use a number like 0.5, 1 or 2", 3f);
                break;
        }
        typing = TextField.None;
        if (message.StartsWith("Type")) Say("", 0f);
    }

    private static void SetTool(Tool next)
    {
        tool = next;
        drag = DragKind.None;
    }

    private static void SetSpeed(int index)
    {
        speedIndex = Math.Clamp(index, 0, Speeds.Length - 1);
        audio?.SetSpeed(Speeds[speedIndex]);
        Say($"Playback speed {Speeds[speedIndex] * 100:0}%", 1.5f);
    }

    private static void TogglePlay()
    {
        if (audio != null) { if (audio.Playing) audio.Pause(); else audio.Play(); }
        else manualPlaying = !manualPlaying;
    }

    /// <summary>Moves by rows, landing on the snap grid.</summary>
    private static void Step(int rows)
    {
        double row = chart!.SecondsToRow(Now);
        int size = Math.Abs(rows);
        int snapped = rows > 0
            ? (int)(Math.Floor(row / size + 1e-6) + 1) * size
            : (int)(Math.Ceiling(row / size - 1e-6) - 1) * size;
        SeekTo(chart.RowToSeconds(Math.Max(0, snapped)));
    }

    private static void SeekTo(double seconds)
    {
        seconds = Math.Clamp(seconds, Start, SongLength);
        if (audio != null) audio.Seek(seconds - audioOrigin);
        else manualTime = seconds;
    }

    // ---- mouse ------------------------------------------------------------------------------

    private static void HandleMouse(InputMouse mouse, bool buttonClicked)
    {
        var screenPos = mouse.position.ReadValue();
        RectTransformUtility.ScreenPointToLocalPointInRectangle(field, screenPos, null, out var local);
        // Field coordinates are measured from the lanes' centre.
        mouseX = local.x + FieldCentre;
        mouseY = local.y;
        double now = Now;
        int lane = (int)Math.Floor((mouseX - FieldLeft) / LaneWidth);
        bool inField = RectTransformUtility.RectangleContainsScreenPoint(fieldArea, screenPos, null) && !OverUi(screenPos);
        hoverLane = inField && lane >= 0 && lane < chart!.Lanes ? lane : -1;
        hoverRow = SnapRow(chart!.SecondsToRow(YToTime(mouseY, now)));

        float wheel = mouse.scroll.ReadValue().y;
        if (wheel != 0 && !OverUi(screenPos))
        {
            int notches = wheel > 0 ? 1 : -1;
            var k = InputKeyboard.current;
            if (k != null && Ctrl(k)) snapIndex = Math.Clamp(snapIndex + notches, 0, Snaps.Length - 1);
            else if (k != null && Shift(k)) Zoom(notches > 0 ? 1.15f : 1 / 1.15f);
            else Step(notches * SnapRows);
        }

        bool onTimeline = timeline && RectTransformUtility.RectangleContainsScreenPoint(timeline, screenPos, null);
        if (mouse.leftButton.wasPressedThisFrame && !buttonClicked)
        {
            if (onTimeline) drag = DragKind.Scrub;
            else if (hoverLane >= 0) LeftPress(now);
        }
        if (drag == DragKind.Scrub && mouse.leftButton.isPressed)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(timeline, screenPos, null, out var t);
            float fraction = Mathf.Clamp01((t.x - timeline!.rect.xMin) / Math.Max(1f, timeline.rect.width));
            SeekTo(Start + fraction * (SongLength - Start));
        }
        if (mouse.leftButton.wasReleasedThisFrame) LeftRelease(now);
        if (mouse.rightButton.wasPressedThisFrame && hoverLane >= 0) RightPress(now);
    }

    private static int SnapRow(double row) => Math.Max(0, (int)Math.Round(row / SnapRows) * SnapRows);

    private static void LeftPress(double now)
    {
        int hit = NoteUnderMouse(now);
        switch (tool)
        {
            case Tool.Note:
            case Tool.Mine:
                if (hit >= 0 || FindNote(hoverRow, hoverLane) >= 0) return;
                PushUndo();
                chart!.Notes.Add(new EditorChart.Note { Row = hoverRow, Lane = hoverLane, Type = tool == Tool.Mine ? 'M' : '1', EndRow = hoverRow });
                chart.Sort();
                break;
            case Tool.Hold:
                if (hit >= 0) return;
                drag = DragKind.Hold;
                dragStartRow = hoverRow;
                dragLane = hoverLane;
                break;
            case Tool.Select:
                var k = InputKeyboard.current;
                bool add = k != null && (Ctrl(k) || Shift(k));
                if (hit >= 0)
                {
                    long key = NoteKey(chart!.Notes[hit]);
                    if (add) { if (!selection.Remove(key)) selection.Add(key); }
                    else if (!selection.Contains(key)) { selection.Clear(); selection.Add(key); }
                    drag = DragKind.Move;
                    dragStartRow = hoverRow;
                    dragStartLane = hoverLane;
                }
                else
                {
                    if (!add) selection.Clear();
                    drag = DragKind.Box;
                    boxStartTime = YToTime(mouseY, now);
                    boxStartX = mouseX;
                }
                break;
        }
    }

    private static void LeftRelease(double now)
    {
        switch (drag)
        {
            case DragKind.Hold:
                int end = Math.Max(dragStartRow, hoverRow);
                if (FindNote(dragStartRow, dragLane) < 0)
                {
                    PushUndo();
                    chart!.Notes.Add(end > dragStartRow
                        ? new EditorChart.Note { Row = dragStartRow, Lane = dragLane, Type = '2', EndRow = end }
                        : new EditorChart.Note { Row = dragStartRow, Lane = dragLane, Type = '1', EndRow = dragStartRow });
                    RemoveOverlaps(chart.Notes.Count - 1);
                    chart.Sort();
                }
                break;
            case DragKind.Box:
                double t0 = Math.Min(boxStartTime, YToTime(mouseY, now)), t1 = Math.Max(boxStartTime, YToTime(mouseY, now));
                float x0 = Math.Min(boxStartX, mouseX), x1 = Math.Max(boxStartX, mouseX);
                foreach (var n in chart!.Notes)
                {
                    float x = LaneX(n.Lane);
                    double t = chart.RowToSeconds(n.Row);
                    if (x >= x0 && x <= x1 && t >= t0 && t <= t1) selection.Add(NoteKey(n));
                }
                break;
            case DragKind.Move:
                MoveSelection(hoverRow - dragStartRow, (hoverLane >= 0 ? hoverLane : dragStartLane) - dragStartLane);
                break;
        }
        drag = DragKind.None;
    }

    private static void RightPress(double now)
    {
        int hit = NoteUnderMouse(now);
        if (hit < 0) return;
        PushUndo();
        if (selection.Contains(NoteKey(chart!.Notes[hit])) && selection.Count > 1) DeleteSelectedNotes();
        else
        {
            selection.Remove(NoteKey(chart.Notes[hit]));
            chart.Notes.RemoveAt(hit);
        }
    }

    /// <summary>The note (or hold) under the mouse, found by its drawn position.</summary>
    private static int NoteUnderMouse(double now)
    {
        if (hoverLane < 0) return -1;
        for (int i = 0; i < chart!.Notes.Count; i++)
        {
            var n = chart.Notes[i];
            if (n.Lane != hoverLane) continue;
            float y0 = TimeToY(chart.RowToSeconds(n.Row), now);
            float y1 = n.IsLong ? TimeToY(chart.RowToSeconds(n.EndRow), now) : y0;
            if (mouseY >= y0 - NoteHeight / 2 && mouseY <= y1 + NoteHeight / 2) return i;
        }
        // Otherwise the note on the snap under the mouse, which is where a click would place one.
        return FindNote(hoverRow, hoverLane);
    }

    private static int FindNote(int row, int lane)
    {
        for (int i = 0; i < chart!.Notes.Count; i++)
        {
            var n = chart.Notes[i];
            if (n.Lane == lane && row >= n.Row && row <= n.LastRow) return i;
        }
        return -1;
    }

    private static int FirstNoteAtOrAfter(int row)
    {
        int lo = 0, hi = chart!.Notes.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (chart.Notes[mid].Row < row) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Removes notes that the note at <paramref name="index"/> now covers in its lane.</summary>
    private static void RemoveOverlaps(int index)
    {
        var keep = chart!.Notes[index];
        chart.Notes.RemoveAll(n => n.Lane == keep.Lane && !(n.Row == keep.Row && n.Type == keep.Type && n.EndRow == keep.EndRow)
                                   && n.Row <= keep.LastRow && n.LastRow >= keep.Row);
    }

    // ---- undo: notes, events and scroll speeds together -------------------------------------

    private sealed class Snapshot
    {
        internal List<EditorChart.Note> Notes = null!;
        internal List<ChartEvent> Events = null!;
        internal bool EventsChanged;
        internal List<(double Beat, double Ratio)> Scrolls = null!;
    }

    private static readonly List<Snapshot> undo = new(), redo = new();

    private static Snapshot Capture() => new()
    {
        Notes = new List<EditorChart.Note>(chart!.Notes),
        Events = events.Select(e => e.Copy()).ToList(),
        EventsChanged = eventsChanged,
        Scrolls = new List<(double, double)>(scrolls),
    };

    private static void Restore(Snapshot s)
    {
        chart!.Notes = s.Notes;
        events = s.Events;
        eventsChanged = s.EventsChanged;
        scrolls = s.Scrolls;
        eventIndex = Math.Min(eventIndex, events.Count - 1);
        selection.Clear();
        dirty = ticksDirty = true;
    }

    private static void PushUndo()
    {
        undo.Add(Capture());
        if (undo.Count > 200) undo.RemoveAt(0);
        redo.Clear();
        dirty = true;
        ticksDirty = true;
        confirmLeave = false;
    }

    private static void Undo()
    {
        if (undo.Count == 0) { Say("Nothing to undo", 1.5f); return; }
        redo.Add(Capture());
        Restore(undo[^1]);
        undo.RemoveAt(undo.Count - 1);
    }

    private static void Redo()
    {
        if (redo.Count == 0) { Say("Nothing to redo", 1.5f); return; }
        undo.Add(Capture());
        Restore(redo[^1]);
        redo.RemoveAt(redo.Count - 1);
    }

    // ---- note edits -------------------------------------------------------------------------

    private static List<EditorChart.Note> SelectedNotes() => chart!.Notes.Where(n => selection.Contains(NoteKey(n))).ToList();

    private static void SelectAll()
    {
        selection.Clear();
        foreach (var n in chart!.Notes) selection.Add(NoteKey(n));
    }

    private static void DeleteSelection()
    {
        if (selection.Count == 0) return;
        PushUndo();
        DeleteSelectedNotes();
    }

    private static void DeleteSelectedNotes()
    {
        chart!.Notes.RemoveAll(n => selection.Contains(NoteKey(n)));
        selection.Clear();
    }

    private static void Copy()
    {
        var notes = SelectedNotes();
        if (notes.Count == 0) { Say("Select notes to copy first", 1.5f); return; }
        int first = notes.Min(n => n.Row);
        clipboard = notes.Select(n => new EditorChart.Note { Row = n.Row - first, Lane = n.Lane, Type = n.Type, EndRow = n.EndRow - first }).ToList();
        clipboardLanes = chart!.Lanes;
        Say($"Copied {clipboard.Count} notes", 1.5f);
    }

    /// <summary>Pastes the clipboard starting at the current time, on the snap grid.</summary>
    private static void Paste()
    {
        if (clipboard.Count == 0) { Say("Nothing copied yet", 1.5f); return; }
        if (clipboardLanes != chart!.Lanes) { Say($"Those notes were copied from a {clipboardLanes}-lane chart", 3f); return; }
        PushUndo();
        int at = SnapRow(chart.SecondsToRow(Now));
        PlaceNotes(clipboard.Select(c => new EditorChart.Note { Row = c.Row + at, Lane = c.Lane, Type = c.Type, EndRow = c.EndRow + at }).ToList());
        Say($"Pasted {clipboard.Count} notes", 1.5f);
    }

    private static void MoveSelection(int rows, int lanes)
    {
        if ((rows == 0 && lanes == 0) || selection.Count == 0) return;
        var moving = SelectedNotes();
        if (moving.Any(n => n.Row + rows < 0 || n.Lane + lanes < 0 || n.Lane + lanes >= chart!.Lanes)) return;
        PushUndo();
        chart!.Notes.RemoveAll(n => selection.Contains(NoteKey(n)));
        PlaceNotes(moving.Select(n => new EditorChart.Note { Row = n.Row + rows, Lane = n.Lane + lanes, Type = n.Type, EndRow = n.EndRow + rows }).ToList());
    }

    /// <summary>Puts the selected notes (starts and ends) on the current snap grid.</summary>
    private static void Resnap()
    {
        var notes = SelectedNotes();
        if (notes.Count == 0) { Say("Select notes to snap first", 1.5f); return; }
        PushUndo();
        chart!.Notes.RemoveAll(n => selection.Contains(NoteKey(n)));
        PlaceNotes(notes.Select(n =>
        {
            var r = n;
            r.Row = SnapRow(n.Row);
            r.EndRow = r.IsLong ? Math.Max(r.Row + SnapRows, SnapRow(n.EndRow)) : r.Row;
            return r;
        }).ToList());
        Say($"Snapped {notes.Count} notes to 1/{Snaps[snapIndex]}", 2f);
    }

    /// <summary>Mirrors the selection (or the whole chart) left to right.</summary>
    private static void Mirror()
    {
        bool all = selection.Count == 0;
        PushUndo();
        if (all)
        {
            chart!.Notes = chart.Notes.Select(n => { n.Lane = chart.Lanes - 1 - n.Lane; return n; }).ToList();
            chart.Sort();
            Say("Mirrored the whole chart", 1.5f);
            return;
        }
        var mirrored = SelectedNotes().Select(n => { n.Lane = chart!.Lanes - 1 - n.Lane; return n; }).ToList();
        chart!.Notes.RemoveAll(n => selection.Contains(NoteKey(n)));
        PlaceNotes(mirrored);
        Say("Mirrored the selection", 1.5f);
    }

    /// <summary>
    /// Adds notes, removing whatever they land on in their lane, and makes them the selection.
    /// Later notes in the list win over earlier ones.
    /// </summary>
    private static void PlaceNotes(List<EditorChart.Note> notes)
    {
        selection.Clear();
        foreach (var n in notes)
        {
            chart!.Notes.RemoveAll(o => o.Lane == n.Lane && o.Row <= n.LastRow && o.LastRow >= n.Row);
            chart.Notes.Add(n);
        }
        chart!.Sort();
        foreach (var n in chart.Notes) if (notes.Any(p => p.Row == n.Row && p.Lane == n.Lane)) selection.Add(NoteKey(n));
    }

    /// <summary>Reverses the selection in time, like osu!'s vertical flip.</summary>
    private static void Reverse()
    {
        var notes = SelectedNotes();
        if (notes.Count < 2) { Say("Select notes to reverse first", 1.5f); return; }
        PushUndo();
        int first = notes.Min(n => n.Row), last = notes.Max(n => n.LastRow);
        chart!.Notes.RemoveAll(n => selection.Contains(NoteKey(n)));
        PlaceNotes(notes.Select(n =>
        {
            var r = n;
            r.Row = first + last - n.LastRow;
            r.EndRow = r.IsLong ? first + last - n.Row : r.Row;
            return r;
        }).ToList());
    }

    // ---- saving and exporting ---------------------------------------------------------------

    /// <returns>Whether the chart is saved and loaded as a custom chart.</returns>
    private static bool Save()
    {
        try
        {
            if (chart!.Notes.Count == 0) { Say("Place some notes before saving", 3f); return false; }
            var output = new ChartText();
            output.Tags.Add(new("NBBSONG", song!.name));
            output.Tags.Add(new("NBBEDITOR", "1"));
            output.Tags.Add(new("NBBMELODY", melody.ToString()));
            // The song's own events unless they were changed here; then the chart carries its own.
            if (eventsChanged) output.Tags.Add(new("NBBEVENTS", "chart"));
            if (bookmarks.Count > 0) output.Tags.Add(new("NBBBOOKMARKS", string.Join(",", bookmarks.Select(b => b.ToString("0.###", CultureInfo.InvariantCulture)))));
            foreach (var tag in header!.Tags)
            {
                if (tag.Key.StartsWith("NBB", StringComparison.OrdinalIgnoreCase)) continue;
                if (tag.Key.ToUpperInvariant() is "TITLE" or "CREDIT" or "SCROLLS") continue;
                output.Tags.Add(tag);
            }
            title = CleanText(title).Length > 0 ? CleanText(title) : "New chart";
            author = CleanText(author);
            output.SetTag("TITLE", title);
            output.SetTag("CREDIT", author);
            output.SetTag("ATTACKS", eventsChanged ? WriteEvents(events) : GameChart().GetTag("ATTACKS") ?? "");
            if (scrolls.Count > 0) output.SetTag("SCROLLS", ScrollsTag());
            output.Blocks.Add(new ChartText.NoteBlock
            {
                StepsType = header.Blocks.FirstOrDefault(b => b.Lanes == chart.Lanes)?.StepsType ?? (chart.Lanes == 5 ? "pump-single" : "dance-single"),
                Description = title,
                Difficulty = "Challenge",
                Meter = EstimateMeter().ToString(),
                Radar = "0,0,0,0,0",
                Notes = chart.WriteNotes(),
            });
            string path = SavePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, output.Write(), new UTF8Encoding(false));
            CustomCharts.Reload();
            var saved = CustomCharts.ForSong(song.name).FirstOrDefault(c => c.SourceFile.Equals(path, StringComparison.OrdinalIgnoreCase) && c.PackEntry == null);
            confirmLeave = false;
            if (saved == null)
            {
                // Still counts as unsaved, so leaving asks first.
                Say($"Saved to {Path.GetFileName(path)}, but it didn't load as a custom chart; see the log.", 6f);
                return false;
            }
            dirty = false;
            editing = saved;
            header = saved.Chart;
            CustomCharts.Select(song.name, saved);
            Say($"Saved \"{title}\". It's now the custom difficulty for {song.name}.", 5f);
            ModLog.Info($"Chart editor saved {path}.");
            OptionsMenuIntegration.RefreshAll();
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Error("Saving the chart failed: " + ex);
            Say("Saving failed: " + ex.Message, 6f);
            return false;
        }
    }

    /// <summary>
    /// A chart the editor saved before (a loose .sm with its #NBBEDITOR tag and one difficulty) is
    /// saved over; anything else, like an imported or hand-made file, is kept and a new file made.
    /// </summary>
    private static string SavePath()
    {
        if (editing != null && editing.PackEntry == null && File.Exists(editing.SourceFile))
        {
            var existing = ChartText.Parse(File.ReadAllText(editing.SourceFile));
            if (existing.Blocks.Count == 1 && existing.GetTag("NBBEDITOR") != null) return editing.SourceFile;
        }
        string dir = Path.Combine(CustomCharts.Folder, CustomCharts.Sanitize(song!.name));
        return CustomCharts.FreeName(Path.Combine(dir, CustomCharts.Sanitize(title) + ".sm"));
    }

    /// <summary>Saves, then asks where to put a one-file pack with the chart and all its events.</summary>
    private static void StartExportPack()
    {
        if (exportDialog != null) return;
        if ((dirty || editing == null || editing.PackEntry != null) && !Save()) return;
        if (editing == null) return;
        string name = $"{song!.name} - {title}";
        exportDialog = FileDialogs.Save("Export custom chart", Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            CustomCharts.Sanitize(name) + CustomCharts.PackExtension, CustomCharts.PackExtension, "Nocturne But Better chart pack (*.nbbchart)", "*.nbbchart");
        Say("Choose where to save the pack...", 60f);
    }

    private static void FinishExport()
    {
        if (exportDialog == null || !exportDialog.IsCompleted) return;
        var task = exportDialog;
        exportDialog = null;
        try
        {
            string? path = task.Result;
            if (path == null) { Say("Export cancelled", 2f); return; }
            CustomCharts.Export(path, new[] { editing! }, title);
            Say($"Exported to {Path.GetFileName(path)}: the notes{(eventsChanged ? ", its own events" : "")}{(scrolls.Count > 0 ? ", scroll speeds" : "")} in one file", 6f);
        }
        catch (Exception ex)
        {
            var reason = ex is AggregateException agg && agg.InnerException != null ? agg.InnerException : ex;
            ModLog.Error("Exporting the chart failed: " + reason);
            Say("Export failed: " + reason.Message, 6f);
        }
    }

    /// <summary>A rough level from how dense the chart is, for the .sm meter field.</summary>
    private static int EstimateMeter()
    {
        var notes = chart!.Notes.Where(n => n.Type != 'M').ToList();
        if (notes.Count < 2) return 1;
        double span = chart.RowToSeconds(notes[^1].Row) - chart.RowToSeconds(notes[0].Row);
        double nps = span > 0 ? notes.Count / span : 0;
        return Math.Clamp((int)Math.Round(nps * 1.6), 1, 20);
    }

    // ---- the panels' text -------------------------------------------------------------------

    private static string FormatTime(double seconds)
    {
        if (seconds < 0) return "-" + FormatTime(-seconds);
        int m = (int)(seconds / 60);
        return $"{m}:{seconds - m * 60:00.000}";
    }

    private static void DrawPanels(double now)
    {
        double beat = chart!.SecondsToBeat(now);
        titleText!.text = $"{Escape(song!.name)}  <color=#9D92B4>melody {melody}</color>  {Escape(title)}{(dirty ? " <color=#F2B02E>*</color>" : "")}";
        timeText!.text = $"{FormatTime(now)} <size=70%><color=#9D92B4>/ {FormatTime(SongLength)}</color></size>";

        int holds = chart.Notes.Count(n => n.IsLong), mines = chart.Notes.Count(n => n.Type == 'M');
        var sb = new StringBuilder();
        sb.Append($"<color=#9D92B4>Beat</color> {beat:0.00}   <color=#9D92B4>BPM</color> {chart.BpmAt(beat):0.##}\n");
        sb.Append($"<color=#9D92B4>Scroll</color> x{ScrollAt(beat):0.##}   <color=#9D92B4>Snap</color> 1/{Snaps[snapIndex]}\n");
        sb.Append($"<color=#9D92B4>Notes</color> {chart.Notes.Count}  ({holds} holds, {mines} mines)\n");
        sb.Append($"<color=#9D92B4>Selected</color> {selection.Count}\n");
        sb.Append($"<color=#9D92B4>Events</color> {events.Count} {(eventsChanged ? "(edited)" : "(the song's)")}\n");
        sb.Append($"<color=#9D92B4>{Escape(musicState)}</color>\n");
        infoText!.text = sb.ToString();

        tabText!.text = tab switch
        {
            Tab.Compose => $"Click a lane to place with the tool. Hold tool: drag up. Select tool: click or drag a box, then drag to move. Right click deletes.\n\n{ShortKey(EditorAction.NudgeLater)} / {ShortKey(EditorAction.NudgeEarlier)} move the selection by a snap; {ShortKey(EditorAction.NudgeLeft)} / {ShortKey(EditorAction.NudgeRight)} change its lanes.",
            Tab.Timing => TimingText(),
            Tab.Events => EventsText(),
            Tab.Setup => SetupText(),
            _ => "Keys are saved on this PC and used every time you open the editor.",
        };

        // The Events tab's list and buttons reach further down, so its help text gets less room.
        tabText.rectTransform.offsetMax = new Vector2(-16, tab == Tab.Events ? 200 : 250);
        if (tab == Tab.Events) UpdateEventRows();
        string status = Time.unscaledTime < messageUntil ? Escape(message) : "";
        if (typing != TextField.None) status = $"{Escape(message)}\n<color=#EAE6F5>{Escape(typed)}_</color>";
        statusText!.text = status;
        statusBg!.gameObject.SetActive(status.Length > 0);
        if (status.Length > 0)
        {
            float areaWidth = canvasRect!.rect.width - LeftW - RightW;
            var size = statusText.GetPreferredValues(status, areaWidth - 76, 0);
            statusBg.sizeDelta = new Vector2(Math.Min(size.x, areaWidth - 76) + 36, size.y + 14);
            statusBg.anchoredPosition = new Vector2(LeftW + areaWidth / 2, BottomH + 8);
        }
    }

    private static double ScrollAt(double beat)
    {
        double ratio = 1;
        foreach (var (b, r) in scrolls) if (b <= beat) ratio = r;
        return ratio;
    }

    private static string SetupText() =>
        $"{Escape(song!.name)}, melody {melody}, {chart!.Lanes} lanes.\n\n" +
        "Saving makes this the song's custom difficulty. Export writes one .nbbchart file with the notes, " +
        "the events and the scroll speeds, ready to share.\n\n" +
        $"Events: {(eventsChanged ? "this chart's own" : "the song's own (the enemy plays as usual)")}";

    private static string EventsText()
    {
        var e = PickedEvent;
        var sb = new StringBuilder();
        if (e == null) sb.Append("No events. \"Add here\" makes one at the current time.\n");
        else
        {
            sb.Append($"<color=#EAE6F5>At {FormatTime(e.Time)}, for {e.Length:0.##}s</color>\n");
            sb.Append(Escape(e.Mods)).Append('\n');
        }
        sb.Append($"\n{(eventsChanged ? "Edited: this chart plays its own events." : "These are the song's own events, so the enemy plays as usual.")}\n");
        sb.Append("Verbs include ColumnLayout, SpawnPrefab, EnemyAnimation and ShowText.");
        return sb.ToString();
    }

    private static int eventFirst;

    private static bool EventRowShown(int row) => eventFirst + row < events.Count;

    private static void UpdateEventRows()
    {
        eventFirst = Math.Clamp(eventIndex - EventRowsVisible / 2, 0, Math.Max(0, events.Count - EventRowsVisible));
        for (int i = 0; i < eventRows.Count; i++)
        {
            int index = eventFirst + i;
            if (index >= events.Count) continue;
            var e = events[index];
            eventRows[i].Label.text = $"{FormatTime(e.Time)}  {Escape(e.Mods)}";
            bool picked = index == eventIndex;
            eventRows[i].Active = () => picked;
        }
    }

    private static void ClickEventRow(int row) => PickEvent(eventFirst + row, jump: true);
}
