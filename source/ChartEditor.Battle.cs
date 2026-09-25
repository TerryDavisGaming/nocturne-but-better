using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;
using static NocturneFlatScroll.EditorInput;
using static NocturneFlatScroll.EditorUi;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;

namespace NocturneFlatScroll;

// Custom battles: the editor opened on a battle's own chart (one .sm with every difficulty) and
// its own song file. The six difficulty tabs each edit one note block; the timing, events and
// scroll speeds in the file's header are shared by all of them. #OFFSET works as in the game
// (beat 0 at song time -#OFFSET), and the Timing tab can set it along with the tempo. Saving
// writes the whole file; exporting is the battle creator's job.
internal static partial class ChartEditor
{
    private static BattleChartTarget? battle;
    private static int slot;                                         // the difficulty tab shown
    private static readonly List<EditorChart.Note>?[] tabNotes = new List<EditorChart.Note>?[BattleChartFile.SlotCount];
    private static bool tabCharted;                                  // the shown tab has a chart (maybe still empty)
    private static int[] claimed = new int[BattleChartFile.SlotCount];
    private static int leftOutBlocks;
    private static bool timingEdited;
    private static bool closePrompt;
    private static readonly TapTempo taps = new();
    private static bool tapsOnSongClock;
    private static bool looping;
    private static double loopBeat;

    // Battles whose editor has closed, and the frame it closed on; their Closed runs at the start
    // of a later frame's update.
    private static readonly List<BattleChartTarget> closedBattles = new();
    private static int closedFrame;

    // A test asked for on opening (see OpenBattle), and whether the editor closes when it ends.
    private static double? openTest;
    private static bool closeAfterTest;

    /// <summary>
    /// Opens the editor on a custom battle's chart, at a difficulty slot (0 Beginner to 5 Zen) or,
    /// with -1, the first one that has notes. <see cref="BattleChartTarget.Closed"/> runs once
    /// after the editor has closed, however it closes (also when it fails to open), on the frame
    /// after, so the key or click that closed the editor can't reach the screen that shows again.
    /// With <paramref name="test"/> (the battle creator's Test buttons), a test starts as soon as
    /// the music has loaded, from that many seconds in (0: from the start), and the editor closes
    /// itself when the test ends; a test that can't start leaves the editor open, saying why.
    /// </summary>
    internal static void OpenBattle(BattleChartTarget target, int slot = -1, double? test = null)
    {
        if (IsOpen)
        {
            ModLog.Error($"The chart editor is already open, so the battle {target.Title} can't open in it.");
            QueueClosed(target);
            return;
        }
        battle = target;
        try
        {
            if (target.Lanes != 4 && target.Lanes != 5) throw new InvalidDataException($"a battle has 4 or 5 lanes, not {target.Lanes}");
            BuildCanvas();
            EditorOverlay.Enter(OverlayOwner);
            StartBattle(target, slot);
            openTest = test;
            closeAfterTest = test != null;
            ModLog.Info($"Chart editor opened for the custom battle {target.Title} ({target.ChartFullPath}), {target.Lanes} lanes{(test != null ? ", to test it" : "")}.");
        }
        catch (Exception ex)
        {
            ModLog.Error("Opening the chart editor for a custom battle failed: " + ex);
            Close();
        }
    }

    private static void QueueClosed(BattleChartTarget target)
    {
        closedBattles.Add(target);
        closedFrame = Time.frameCount;
    }

    /// <summary>Runs the Closed of the battles whose editor closed on an earlier frame, each once.</summary>
    private static void RunClosedBattles()
    {
        if (closedBattles.Count == 0 || Time.frameCount <= closedFrame) return;
        var due = closedBattles.ToArray();
        closedBattles.Clear();
        foreach (var target in due)
        {
            if (target.Closed == null) continue;
            try { target.Closed(); }
            catch (Exception ex) { ModLog.Error("After the chart editor closed, the battle screen failed: " + ex); }
        }
    }

    private static void StartBattle(BattleChartTarget target, int requested)
    {
        int lanes = target.Lanes;
        string path = target.ChartFullPath;
        ChartText source;
        if (File.Exists(path)) source = ChartText.Parse(PackageFiles.Folder(target.Folder).ReadAllText(target.ChartPath, BattlePackage.MaxChartBytes));
        else
        {
            // A battle whose chart isn't written yet: saving makes it.
            source = new ChartText();
            source.Tags.Add(new("TITLE", CleanText(target.Title)));
            source.Tags.Add(new("MUSIC", target.AudioPath));
            source.Tags.Add(new("OFFSET", "0"));
            source.Tags.Add(new("BPMS", "0=120"));
        }
        header = source;
        editing = null;
        chart = new EditorChart(lanes);
        // Unlike a game song's, a battle's #OFFSET counts: the game puts beat 0 at song time -#OFFSET.
        chart.ReadTiming(source);
        claimed = BattleChartFile.ClaimBlocks(source, lanes);
        for (int s = 0; s < BattleChartFile.SlotCount; s++)
        {
            tabNotes[s] = null;
            if (claimed[s] < 0) continue;
            var read = new EditorChart(lanes);
            read.ReadNotes(source.Blocks[claimed[s]].Notes);
            if (read.Notes.Count > 0) tabNotes[s] = read.Notes;
        }
        slot = requested >= 0 && requested < BattleChartFile.SlotCount ? requested : Math.Max(0, Array.FindIndex(tabNotes, n => n != null));
        tabCharted = tabNotes[slot] != null;
        chart.Notes = tabNotes[slot] ?? new List<EditorChart.Note>();
        leftOutBlocks = BattleChartFile.LeftOut(source, lanes, claimed).Count;
        if (leftOutBlocks > 0) ModLog.Info($"Chart editor: {path} has {leftOutBlocks} extra difficulty blocks that never play; saving leaves them out.");

        LoadBattleEvents(source);
        LoadTiming(source);
        LoadDialogue(target);
        title = target.Title;
        author = "";
        dirty = false;
        confirmLeave = false;
        closePrompt = false;
        timingEdited = false;
        looping = false;
        taps.Clear();
        undo.Clear();
        redo.Clear();
        selection.Clear();
        drag = DragKind.None;
        typing = TextField.None;
        keyMap.Rebinding = null;
        manualPlaying = false;
        var all = tabNotes.Where(n => n != null).SelectMany(n => n!).ToList();
        manualTime = chart.Notes.Count > 0 ? Math.Max(0, chart.RowToSeconds(chart.Notes[0].Row) - 1) : 0;
        manualLength = Math.Max(120, all.Count > 0 ? chart.RowToSeconds(all.Max(n => n.LastRow)) + 10 : 0);
        ticksDirty = true;
        peaks = null;

        audio?.Dispose();
        audio = null;
        music = null;
        audioOrigin = 0;
        musicState = "Loading music...";
        string folder = target.Folder, name = target.AudioPath;
        loading = Task.Run(() => SongAudio.LoadFile(folder, name));

        ShowScreen(Screen.Edit);
        SetTab(Tab.Compose);
        BuildField();
        Say(tabCharted
            ? $"Space plays{TestKeyHint()}. Click in a lane to place a note. {ShortKey(EditorAction.PrevDifficulty)} / {ShortKey(EditorAction.NextDifficulty)} switch difficulty."
            : $"{SlotName(slot)} isn't charted yet: click Start empty, or copy another difficulty.", 6f);
    }

    // ---- the difficulty tabs ----------------------------------------------------------------

    private static string SlotName(int s) => ChartText.GameDifficultyLabels[s];

    private static List<EditorChart.Note>? NotesOf(int s) => s == slot ? (tabCharted ? chart!.Notes : null) : tabNotes[s];

    private static int NoteCount(int s) => NotesOf(s)?.Count ?? 0;

    // The tabs the arcade would play as they are now: those with notes.
    private static bool[] ChartedTabs() => Enumerable.Range(0, BattleChartFile.SlotCount).Select(s => NoteCount(s) > 0).ToArray();

    /// <summary>Keeps the shown tab's notes with its tab, before another tab shows or the file is saved.</summary>
    private static void StoreTab() => tabNotes[slot] = tabCharted ? chart!.Notes : null;

    private static void ShowDifficulty(int next, bool quiet = false)
    {
        next = ((next % BattleChartFile.SlotCount) + BattleChartFile.SlotCount) % BattleChartFile.SlotCount;
        if (next == slot) return;
        StoreTab();
        slot = next;
        tabCharted = tabNotes[next] != null;
        chart!.Notes = tabNotes[next] ?? new List<EditorChart.Note>();
        selection.Clear();
        drag = DragKind.None;
        ticksDirty = true;
        if (!quiet) Say(tabCharted ? $"{SlotName(slot)}: {chart.Notes.Count} notes" : $"{SlotName(slot)} isn't charted yet", 2f);
    }

    /// <summary>Whether notes can go on the shown tab; says how to start it when they can't.</summary>
    private static bool CanPlaceNotes()
    {
        if (battle == null || tabCharted) return true;
        Say($"{SlotName(slot)} isn't charted yet: click Start empty, or copy another difficulty.", 3f);
        return false;
    }

    private static void StartEmpty()
    {
        if (tabCharted) return;
        PushUndo();
        tabCharted = true;
        chart!.Notes = new List<EditorChart.Note>();
        Say($"{SlotName(slot)} is started. Click in a lane to place notes.", 3f);
    }

    /// <summary>Takes the shown tab's notes away, so the difficulty isn't charted (undo brings them back).</summary>
    private static void UnchartTab()
    {
        if (!tabCharted) return;
        PushUndo();
        tabCharted = false;
        chart!.Notes = new List<EditorChart.Note>();
        selection.Clear();
        Say($"{SlotName(slot)} isn't charted any more. {ShortKey(EditorAction.Undo)} brings its notes back.", 4f);
    }

    /// <summary>Replaces the shown tab's notes with a copy of another tab's whole chart.</summary>
    private static void CopyFromTab(int from)
    {
        var notes = NotesOf(from);
        if (from == slot || notes == null || notes.Count == 0) return;
        PushUndo();
        tabCharted = true;
        chart!.Notes = new List<EditorChart.Note>(notes);
        chart.Sort();
        selection.Clear();
        Say($"Copied {notes.Count} notes from {SlotName(from)} into {SlotName(slot)}.", 3f);
    }

    // ---- events -----------------------------------------------------------------------------

    private static void LoadBattleEvents(ChartText source)
    {
        // A battle has no game song to fall back on: its events are always its own.
        songEvents = new List<ChartEvent>();
        events = ParseEvents(source.GetTag("ATTACKS"));
        eventsChanged = true;
        eventIndex = events.Count > 0 ? 0 : -1;
    }

    private static void ClearEvents()
    {
        if (events.Count == 0) return;
        PushUndo();
        events = new List<ChartEvent>();
        eventIndex = -1;
        Say($"Deleted every event. {ShortKey(EditorAction.Undo)} brings them back.", 3f);
    }

    /// <summary>
    /// The player attacks the enemy here: "PlayerAttack charges [damage]" in the game's charts. In
    /// 5 lanes the player's own attacks are off, so these are the only way to hurt the enemy.
    /// </summary>
    private static void AddPlayerAttack()
    {
        PushUndo();
        var added = new ChartEvent { Time = chart!.RowToSeconds(SnapRow(chart.SecondsToRow(Now))), Length = 0.5, Mods = "PlayerAttack 1" };
        events.Add(added);
        EventsEdited();
        eventIndex = events.IndexOf(added);
        Say("Added a player attack. Edit text to change it: PlayerAttack, the charges it uses, and the damage if you like.", 5f);
    }

    // ---- timing -----------------------------------------------------------------------------

    private static void TimingEdited()
    {
        timingEdited = true;
        ticksDirty = true;
        dirty = true;
    }

    /// <summary>Sets the tempo of the section the play position is in (the whole song when it has one tempo).</summary>
    private static void SetSectionBpm(double bpm)
    {
        PushUndo();
        double beat = chart!.SecondsToBeat(Now);
        BattleTiming.SetSectionBpm(chart.Bpms, beat, bpm);
        TimingEdited();
        var section = chart.Bpms[BattleTiming.SectionAt(chart.Bpms, beat)];
        SayTiming(chart.Bpms.Count == 1 ? $"The song's tempo is {bpm:0.##} BPM" : $"From beat {section.Beat:0.##} the tempo is {bpm:0.##} BPM");
    }

    private static void AddTempoChange(double bpm)
    {
        PushUndo();
        double beat = SnapRow(chart!.SecondsToRow(Now)) / (double)EditorChart.RowsPerBeat;
        BattleTiming.AddChange(chart.Bpms, beat, bpm);
        TimingEdited();
        SayTiming($"The tempo changes to {bpm:0.##} BPM at beat {beat:0.##}");
    }

    private static void RemoveTempoChange()
    {
        double beat = chart!.SecondsToBeat(Now);
        if (BattleTiming.SectionAt(chart.Bpms, beat) <= 0) { Say("No tempo change before this point (the song's first tempo stays)", 2.5f); return; }
        PushUndo();
        var removed = BattleTiming.RemoveChange(chart.Bpms, beat);
        TimingEdited();
        SayTiming($"Removed the tempo change at beat {removed:0.##}");
    }

    /// <summary>Sets #OFFSET so beat 0 is at the play position.</summary>
    private static void FirstBeatHere()
    {
        PushUndo();
        chart!.Offset = BattleTiming.OffsetForFirstBeat(Now);
        TimingEdited();
        SayTiming($"Beat 0 is now at {FormatTime(-chart.Offset)}");
    }

    /// <summary>
    /// Says what a tempo or beat 0 change did. Notes sit on beats, so on every difficulty they
    /// move with them, and so do dialogue lines placed on beats; events are at times in seconds,
    /// so they stay where they were, and so do dialogue lines placed at times.
    /// </summary>
    private static void SayTiming(string what)
    {
        bool notes = ChartedTabs().Any(c => c), lines = cues.Any(c => c.Beat != null);
        if (!notes && !lines) { Say(what, 3f); return; }
        string moved = notes && lines ? "The notes on every difficulty and the dialogue lines on beats" : notes ? "The notes on every difficulty" : "The dialogue lines on beats";
        Say($"{what}. {moved} moved with the beats{(events.Count > 0 ? "; events keep their times." : ".")}", 6f);
    }

    /// <summary>Moves every beat (and the notes on them) later against the music; earlier when negative.</summary>
    private static void NudgeOffset(double ms)
    {
        PushUndo();
        chart!.Offset = BattleTiming.Nudge(chart.Offset, ms);
        TimingEdited();
        Say($"Beats {Math.Abs(ms):0} ms {(ms > 0 ? "later" : "earlier")}: beat 0 at {FormatTime(-chart.Offset)}", 2f);
    }

    private static void Tap()
    {
        // Taps along the playing music count in song time, so they give the song's tempo at any
        // playback speed; without music they count in real time.
        bool songClock = IsPlaying;
        if (songClock != tapsOnSongClock) taps.Clear();
        tapsOnSongClock = songClock;
        taps.Tap(songClock ? Now : Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        var bpm = taps.Bpm;
        Say(bpm == null ? "Keep tapping on the beats..." : $"{bpm:0.00} BPM from {taps.Count} taps. Enter sets {Math.Round(bpm.Value):0}, Shift+Enter {bpm:0.00}.", 3f);
    }

    private static void ApplyTaps(bool exact)
    {
        if (taps.Bpm is not double bpm) return;
        double value = exact ? Math.Round(bpm, 2) : Math.Round(bpm);
        taps.Clear();
        SetSectionBpm(Math.Clamp(value, BattleTiming.MinBpm, BattleTiming.MaxBpm));
    }

    /// <summary>Plays two measures over and over with the metronome, to set the offset by ear.</summary>
    private static void ToggleLoop()
    {
        looping = !looping;
        if (!looping) { Say("Loop off", 1.5f); return; }
        loopBeat = Math.Max(0, Math.Floor(chart!.SecondsToBeat(Now) / 4) * 4);
        if (!metronomeOn) ToggleMetronome();
        SeekTo(chart.BeatToSeconds(loopBeat));
        if (!IsPlaying) TogglePlay();
        Say("Looping two measures with the metronome. Move the beats until the clicks sit on the music.", 5f);
    }

    private static void UpdateLoop()
    {
        if (!looping || !IsPlaying || chart == null) return;
        double now = Now, start = chart.BeatToSeconds(loopBeat), end = chart.BeatToSeconds(loopBeat + 8);
        // Moved away from the loop (a click on the timeline, say): loop where the music is now.
        if (now < start - 0.5 || now > end + 0.5)
        {
            loopBeat = Math.Max(0, Math.Floor(chart.SecondsToBeat(now) / 4) * 4);
            return;
        }
        if (now >= end) SeekTo(start);
    }

    // ---- saving -----------------------------------------------------------------------------

    /// <summary>Writes every difficulty into the battle's chart file, after checking it as the battle loader will.</summary>
    private static bool SaveBattle()
    {
        var target = battle!;
        try
        {
            StoreTab();
            int lanes = target.Lanes;
            var notes = new string?[BattleChartFile.SlotCount];
            var counts = new int[BattleChartFile.SlotCount];
            var meters = new int[BattleChartFile.SlotCount];
            for (int s = 0; s < BattleChartFile.SlotCount; s++)
            {
                var list = tabNotes[s];
                if (list == null || list.Count == 0) continue;
                var tab = new EditorChart(lanes) { Notes = new List<EditorChart.Note>(list) };
                tab.Sort();
                notes[s] = tab.WriteNotes();
                counts[s] = list.Count;
                meters[s] = EstimateMeter(tab.Notes);
            }
            if (counts.All(c => c == 0)) { Say("Place some notes in at least one difficulty before saving.", 4f); return false; }

            var output = BattleChartFile.Compose(header!, BattleTags(), lanes, claimed, notes, meters);
            string text = output.Write();
            var notices = new List<string>();
            string? problem = BattleChartFile.Check(text, lanes, counts, notices) ?? GameReaderProblem(text, lanes);
            if (problem != null)
            {
                ModLog.Error($"The battle chart {target.ChartFullPath} wasn't saved: {problem}");
                Say("Not saved: " + problem, 8f);
                return false;
            }
            string path = target.ChartFullPath;
            BattleChartFile.WriteAtomic(path, text);
            header = ChartText.Parse(text);
            claimed = BattleChartFile.ClaimBlocks(header, lanes);
            leftOutBlocks = 0;
            timingEdited = false;
            dirty = false;
            confirmLeave = false;
            string charted = string.Join(", ", Enumerable.Range(0, BattleChartFile.SlotCount).Where(s => counts[s] > 0).Select(SlotName));
            foreach (var notice in notices) ModLog.Info($"Battle chart {target.Title}: {notice}.");
            ModLog.Info($"Chart editor saved the battle chart {path}: {charted}.");
            string lines = SaveDialogue();
            Say((notices.Count > 0 ? $"Saved {charted}. Note: {notices[0]}." : $"Saved {charted} to {target.ChartPath}.") + lines, notices.Count > 0 || lines.Length > 0 ? 7f : 4f);
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Error("Saving the battle chart failed: " + ex);
            Say("Saving failed: " + ex.Message, 6f);
            return false;
        }
    }

    /// <summary>
    /// The arcade only lists a battle whose playable chart the game's own reader takes
    /// (CustomBattles.CheckChart), so the same reader checks it here. If the reader itself fails,
    /// the save goes ahead and the arcade reports it. The battle creator asks it about a battle made
    /// from an osu!mania beatmap only when its own check (BattleCreator.GameCheck) didn't reach it.
    /// </summary>
    internal static string? GameReaderProblem(string text, int lanes)
    {
        var parsed = ChartText.Parse(text);
        string playable = parsed.BuildPlayableSong(parsed.SongSlots(lanes, new List<string>()));
        try
        {
            var built = NotesLoaderSM.Instance.LoadFromText(playable);
            if (built == null || built.steps == null || built.steps.Count == 0 || built.timingData == null)
                return "the game's chart reader found nothing playable in it";
        }
        catch (Exception ex) { ModLog.Error("The game's chart reader couldn't check the battle chart: " + ex.Message); }
        return null;
    }

    /// <summary>The file's header tags with the editor's timing, events, scroll speeds and bookmarks.</summary>
    private static List<KeyValuePair<string, string>> BattleTags()
    {
        var tags = new ChartText();
        tags.Tags.AddRange(header!.Tags);
        if (timingEdited || BattleChartFile.TimingNeedsWriting(header))
        {
            tags.SetTag("OFFSET", BattleTiming.Number(chart!.Offset));
            tags.SetTag("BPMS", BattleTiming.WriteBpms(chart.Bpms));
        }
        SetOrRemove(tags, "SCROLLS", scrolls.Count > 0 ? ScrollsTag() : null);
        tags.SetTag("ATTACKS", WriteEvents(events));
        SetOrRemove(tags, "NBBBOOKMARKS", bookmarks.Count > 0 ? string.Join(",", bookmarks.Select(b => b.ToString("0.###", CultureInfo.InvariantCulture))) : null);
        return tags.Tags;
    }

    private static void SetOrRemove(ChartText file, string tag, string? value)
    {
        if (value != null) file.SetTag(tag, value);
        else file.Tags.RemoveAll(t => t.Key.Equals(tag, StringComparison.OrdinalIgnoreCase));
    }

    private static void OpenBattleFolder()
    {
        try
        {
            string path = Path.GetFullPath(battle!.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ModLog.Error("Opening the battle folder failed: " + ex);
            Say("Couldn't open the folder", 3f);
        }
    }

    // ---- closing ----------------------------------------------------------------------------

    private static void PromptSave()
    {
        closePrompt = false;
        if (SaveBattle()) Close();
    }

    private static void UpdateClosePrompt(InputKeyboard k)
    {
        if (Pressed(k, Key.Escape)) closePrompt = false;
        else if (Pressed(k, Key.Enter) || Pressed(k, Key.NumpadEnter)) PromptSave();
        else if (Pressed(k, Key.D)) Close();
    }

    /// <summary>
    /// While the unsaved-changes prompt shows, only its own buttons take the mouse: the rest of the
    /// screen updates its text but ignores clicks.
    /// </summary>
    private static bool UpdatePromptButtons(InputMouse? mouse)
    {
        Ui.UpdateButtons(null);
        if (mouse == null) return false;
        var pos = mouse.position.ReadValue();
        foreach (var b in promptButtons)
        {
            if (!RectTransformUtility.RectangleContainsScreenPoint(b.Rect, pos, null)) continue;
            if (b.Bg != null) b.Bg.color = ButtonHover;
            if (!mouse.leftButton.wasPressedThisFrame) continue;
            b.OnClick?.Invoke();
            return true;
        }
        return false;
    }

    // ---- the panels' text -------------------------------------------------------------------

    private static string BattleTitle() =>
        $"{Escape(battle!.Title)}  <color=#9D92B4>{battle.Lanes} lanes</color>  {SlotName(slot)}{(tabCharted ? "" : " <color=#9D92B4>(not charted)</color>")}{(dirty ? " <color=#F2B02E>*</color>" : "")}";

    private static string BattleSetupText()
    {
        var target = battle!;
        var sb = new StringBuilder();
        sb.Append($"{Escape(target.Title)}: {target.Lanes} lanes{(target.Lanes == 5 ? $"; the middle lane is the attack key ({Escape(AttackKeyName())})" : "")}.\n");
        sb.Append($"Chart {Escape(target.ChartPath)}, song {Escape(target.AudioPath)}.\n\n");
        var tabs = ChartedTabs();
        var charted = Enumerable.Range(0, BattleChartFile.SlotCount).Where(s => tabs[s]).Select(s => $"{SlotName(s)} ({NoteCount(s)})").ToList();
        sb.Append(charted.Count > 0 ? $"Charted: {string.Join(", ", charted)}.\n" : "Nothing is charted yet.\n");
        // The arcade still offers every difficulty: one without notes plays the nearest charted one.
        var standIns = Enumerable.Range(0, BattleChartFile.SlotCount).Where(s => !tabs[s])
            .GroupBy(s => BattleChartFile.PlaysInstead(tabs, s)).Where(g => g.Key >= 0)
            .Select(g => $"{SlotName(g.Key)}'s notes on {JoinAnd(g.Select(SlotName))}").ToList();
        if (standIns.Count > 0) sb.Append($"Until the rest are charted, the arcade plays {string.Join("; ", standIns)}.\n");
        if (leftOutBlocks > 0) sb.Append($"{leftOutBlocks} extra copies of a difficulty in the file never play; saving leaves them out.\n");
        sb.Append($"\n{ShortKey(EditorAction.Save)} saves every difficulty into the chart file. The battle creator exports the battle.\n");
        sb.Append(TestText());
        return sb.ToString();
    }

    private static string BattleTimingText()
    {
        var sb = new StringBuilder();
        var bpms = chart!.Bpms;
        sb.Append("<color=#EAE6F5>Tempo</color> ");
        sb.Append(bpms.Count == 1 ? $"{bpms[0].Bpm:0.##} BPM" : string.Join(", ", bpms.Take(3).Select(b => $"beat {b.Beat:0.##}: {b.Bpm:0.##}")) + (bpms.Count > 3 ? $", and {bpms.Count - 3} more" : ""));
        sb.Append($"\n<color=#EAE6F5>Beat 0</color> at {FormatTime(-chart.Offset)} in the song (#OFFSET {chart.Offset.ToString("0.####", CultureInfo.InvariantCulture)})\n");
        if (chart.Offset > 0.001) sb.Append("<color=#F2B02E>Beat 0 is before the song starts, so notes before 0:00 can't be played.</color>\n");
        sb.Append("<color=#EAE6F5>Tap tempo</color> ");
        string tapKey = ShortKey(EditorAction.NoteTicks);
        sb.Append(taps.Bpm is double tapped
            ? $"{tapped:0.00} BPM from {taps.Count} taps. Enter sets {Math.Round(tapped):0}, Shift+Enter {tapped:0.00}.\n"
            : $"{(tapKey.Length > 0 ? "press " + tapKey : "click Tap tempo")} on the beats while the music plays.\n");
        sb.Append($"The ms buttons move every beat against the music. Keys: {KeysFor(EditorAction.BeatsEarlier)} / {KeysFor(EditorAction.BeatsLater)} 1 ms, " +
            $"{KeysFor(EditorAction.BeatsEarlier10)} / {KeysFor(EditorAction.BeatsLater10)} 10 ms.\n");
        // Notes sit on beats; events sit at times in seconds.
        sb.Append("Tempo and beat changes move the notes on every difficulty, and dialogue lines on beats move with them; events keep their times.\n");
        sb.Append($"<color=#EAE6F5>Scroll speed</color> {(scrolls.Count == 0 ? "normal throughout" : $"{scrolls.Count} changes")}   <color=#EAE6F5>Bookmarks</color> {bookmarks.Count}");
        return sb.ToString();
    }

    // "A", "A and B", "A, B and C".
    private static string JoinAnd(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }

    private static string BattleEventsText()
    {
        var e = PickedEvent;
        var sb = new StringBuilder();
        if (e == null) sb.Append("No events. \"Add here\" or \"Player attack\" makes one at the current time.\n");
        else
        {
            sb.Append($"<color=#EAE6F5>At {FormatTime(e.Time)}, for {e.Length:0.##}s</color>\n");
            sb.Append(Escape(e.Mods)).Append('\n');
        }
        sb.Append("\nThe battle's events, shared by every difficulty. PlayerAttack hits the enemy");
        sb.Append(battle!.Lanes == 5 ? " (the only way to hurt it in 5 lanes)." : ".");
        // A custom battle spawns no song props and has no text lines of its own, so SpawnPrefab and
        // ShowText show nothing; these two work with the placeholder enemy.
        sb.Append(" EnemyAnimation and TriggerCombatEffect use the enemy's own animations and effects.");
        return sb.ToString();
    }
}
