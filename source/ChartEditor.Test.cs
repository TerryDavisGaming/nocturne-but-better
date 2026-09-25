using System.Globalization;
using UnityEngine;
using static NocturneFlatScroll.EditorInput;

namespace NocturneFlatScroll;

// Test play, like osu!'s editor test: Test (F5) plays the chart being edited in a real battle,
// from two bars before the play position or (Shift) from the start, and when the battle is left
// the editor is back as it was: the same tab, play position, undo and unsaved changes. The test's
// chart is built from what's in memory, the way saving would write it; nothing is saved. TestPlay
// runs the battle; this side builds the test, and hides and shows the screen around it.
internal static partial class ChartEditor
{
    // The editor's decoded song. A test plays it too (the same buffer, not a copy): a game song
    // can only start partway on it, since the game's own music can't seek.
    private static SongAudio.Result? music;
    private static double testFrom;       // the play position when Test was pressed
    private static bool testFromStart;    // whether that test was from the start (Shift)
    private static float inputFrom;       // keys and clicks count again from this time
    private static bool testHidden;
    private static bool testSkipReady;    // QA only: no Ready prompt
    private static string? testNotice;    // said instead of "Starting the test..."

    // A battle's tests play its dialogue unless the Setup tab turns it off (to test notes without
    // the talk); kept on this PC like the editor's keys.
    private const string TestDialoguePref = "NocturneFlatScroll.EditorTestDialogue";
    private static bool? testDialogue;

    private static bool TestDialogue => testDialogue ??= PlayerPrefs.GetInt(TestDialoguePref, 1) != 0;

    private static void ToggleTestDialogue()
    {
        bool on = !TestDialogue;
        testDialogue = on;
        PlayerPrefs.SetInt(TestDialoguePref, on ? 1 : 0);
        PlayerPrefs.Save();
        Say(on ? "Tests play the battle's dialogue." : "Tests play without the battle's dialogue.", 3f);
    }

    // ---- starting ----------------------------------------------------------------------------

    private static void StartTest(bool fromStart)
    {
        try
        {
            if (screen != Screen.Edit || chart == null || closePrompt || exportDialog != null) return;
            // Typed text only counts once Enter takes it. F5 can't get here while typing; a click on
            // Test can, and would leave the text behind without a word.
            if (typing != TextField.None)
            {
                Say("Press Enter to finish typing first (Esc cancels it).", 4f);
                return;
            }
            if (!TestPlay.CanStart(out string why, battle: battle != null))
            {
                Say(why, 6f);
                return;
            }
            if (loading != null)
            {
                Say("The music is still loading. Test again in a moment.", 3f);
                return;
            }
            testNotice = null;
            double at = Now;
            var run = battle != null ? BattleTestRun(at, fromStart) : SongTestRun(at, fromStart);
            if (run == null) return;
            run.SkipReady = testSkipReady;
            if (!TestPlay.Start(run, out why))
            {
                Say(why, 8f);
                return;
            }
            // Only once the test is on: the editor's music waits where it is (the battle plays its
            // own), and a loop, a drag or a key being set stops. A refused test leaves them be.
            audio?.Pause();
            manualPlaying = false;
            looping = false;
            drag = DragKind.None;
            keyMap.Rebinding = null;
            testFrom = at;
            testFromStart = fromStart;
            testHidden = false;
            Say(testNotice ?? (run.T0 > 0 || run.FirstRow > 0 ? $"Starting the test from {ShortTime(at)}..." : "Starting the test from the start..."), 30f);
        }
        catch (Exception ex)
        {
            ModLog.Error("Test play: building the test failed: " + ex);
            Say("The test couldn't start: " + ex.Message, 8f);
        }
    }

    /// <summary>A game song's test: the chart as saving would write it, trimmed to start at the play position.</summary>
    private static TestPlay.Run? SongTestRun(double at, bool fromStart)
    {
        var ch = chart!;
        string label = $"{song!.name} melody {melody}";
        if (!fromStart && music == null)
        {
            fromStart = true;
            testNotice = "There's no editor music for this song, so the test plays from the start with the game's music.";
        }
        var (t0, firstRow) = TestChart.StartPoint(ch, at, fromStart);
        var kept = TestChart.NotesFrom(ch.Notes, firstRow);
        if (!TestChart.HasPlayableNote(kept))
        {
            Say(fromStart ? "Place some notes before testing." : NoNotesHere(), 4f);
            return null;
        }
        if (PastTheMusic(t0)) return null;

        int carried = 0;
        var tested = t0 > 0 ? TestEvents(t0, out carried) : events;
        // From the start with the song's own events, their text goes as it is, as saving does.
        var text = GameSongHeader(t0 > 0 ? WriteEvents(tested) : SongAttacks());
        text.Blocks.Add(new ChartText.NoteBlock
        {
            StepsType = GameSongStepsType(),
            Description = CleanText(title),
            Difficulty = "Challenge",
            Meter = EstimateMeter(kept).ToString(CultureInfo.InvariantCulture),
            Radar = "0,0,0,0,0",
            Notes = new EditorChart(ch.Lanes) { Notes = kept }.WriteNotes(),
        });
        string? problem = ReaderProblem(text, out string playable);
        if (problem != null)
        {
            ModLog.Error($"Test play: the game can't read the test chart for {label}: {problem}");
            Say("The game can't read this chart: " + problem, 8f);
            return null;
        }

        // Partway in, only the editor's song can follow; from the start, the game's own music
        // plays (or the chart's #MUSIC file), exactly as in the arcade.
        CustomMusic.Source? source;
        bool fallsBack = false;
        string musicName;
        if (t0 > 0)
        {
            source = EditorMusicSource(label);
            musicName = "the editor's music";
        }
        else
        {
            source = editing != null ? CustomMusic.SourceFor(editing) : null;
            fallsBack = source != null;
            musicName = source != null ? "the chart's #MUSIC file" : "the game's (Wwise)";
        }
        return new TestPlay.Run
        {
            Kind = TestPlay.Kind.GameSong,
            Song = song,
            Melody = melody - 1,
            Difficulty = TestPlay.PlayerDifficulty(),
            T0 = t0,
            PlayableText = playable,
            Chart = text,
            Music = source,
            MusicFallsBackToWwise = fallsBack,
            MusicName = musicName,
            Label = label,
            FirstRow = firstRow,
            Notes = kept.Count,
            Events = tested.Count,
            Carried = carried,
        };
    }

    /// <summary>
    /// A custom battle's test: the shown difficulty, trimmed to start at the play position, in a
    /// throwaway battle built from the battle's files with this chart (the chart file isn't read).
    /// </summary>
    private static TestPlay.Run? BattleTestRun(double at, bool fromStart)
    {
        var target = battle!;
        var ch = chart!;
        string tabName = SlotName(slot);
        if (!tabCharted || !TestChart.HasPlayableNote(ch.Notes))
        {
            Say($"{tabName} has no notes to test.", 4f);
            return null;
        }
        var (t0, firstRow) = TestChart.StartPoint(ch, at, fromStart);
        var kept = TestChart.NotesFrom(ch.Notes, firstRow);
        if (!TestChart.HasPlayableNote(kept))
        {
            Say(NoNotesHere(), 4f);
            return null;
        }
        if (PastTheMusic(t0)) return null;

        int carried = 0;
        var tested = t0 > 0 ? TestEvents(t0, out carried) : events;
        var text = new ChartText();
        text.Tags.AddRange(BattleTags());
        text.SetTag("ATTACKS", WriteEvents(tested));
        var notes = new EditorChart(target.Lanes) { Notes = new List<EditorChart.Note>(kept) };
        notes.Sort();
        // In the tab's own slot; the battle plays it on every difficulty.
        text.Blocks.Add(new ChartText.NoteBlock
        {
            StepsType = target.Lanes == 5 ? "pump-single" : "dance-single",
            Difficulty = ChartText.GameDifficultyNames[slot],
            Meter = EstimateMeter(kept).ToString(CultureInfo.InvariantCulture),
            Radar = "0,0,0,0,0",
            Notes = notes.WriteNotes(),
        });
        string label = $"{target.Title} {tabName}";
        var package = TestPackage(target, text.Write());
        if (package == null) return null;
        var source = EditorMusicSource(label);
        CustomBattles.Battle built;
        try { built = CustomBattles.BuildTest(package, source); }
        catch (Exception ex)
        {
            ModLog.Error($"Test play: the test battle for {label} couldn't be built: {ex}");
            // Building checks the chart with the game's own reader; that's the chart's problem.
            Say((ex is InvalidDataException ? "The game can't read this chart: " : "The test couldn't start: ") + ex.Message, 8f);
            return null;
        }
        return new TestPlay.Run
        {
            Kind = TestPlay.Kind.Battle,
            Song = built.Data,
            Battle = built,
            Melody = 0,
            Difficulty = slot,
            T0 = t0,
            DialogueOn = TestDialogue,
            MusicName = source != null ? "the editor's music" : "the battle's file",
            Label = label,
            FirstRow = firstRow,
            Notes = kept.Count,
            Events = tested.Count,
            Carried = carried,
        };
    }

    /// <summary>
    /// The battle's package with the test chart: its battle.json and dialogue as the battle
    /// creator has them now (saved or not), else the ones on disk. Says why and returns null when
    /// neither loads.
    /// </summary>
    private static BattlePackage? TestPackage(BattleChartTarget target, string chartText)
    {
        string? manifest = null, dialogue = null;
        try { manifest = target.Manifest?.Invoke(); }
        catch (Exception ex) { ModLog.Error("Test play: the battle creator's battle.json couldn't be made, so the saved one is used: " + ex.Message); }
        try { dialogue = target.DialogueJson?.Invoke(); }
        catch (Exception ex) { ModLog.Error("Test play: the battle creator's dialogue couldn't be made, so the saved one is used: " + ex.Message); }
        if (manifest != null)
        {
            try { return BattlePackage.Load(target.Folder, chartText, manifest, target.Lanes, target.AudioPath, dialogue); }
            catch (Exception ex) { ModLog.Info($"Test play: the battle creator's unsaved battle.json didn't load ({ex.Message}); the saved one is used."); }
        }
        try { return BattlePackage.Load(target.Folder, chartText, null, target.Lanes, target.AudioPath, dialogue); }
        catch (Exception ex)
        {
            ModLog.Error($"Test play: the battle {target.Title} couldn't be loaded for a test: {ex}");
            Say("The test couldn't start: " + ex.Message, 8f);
            return null;
        }
    }

    /// <summary>The events a test from <paramref name="t0"/> plays (see <see cref="TestChart.EventsFrom"/>).</summary>
    private static List<ChartEvent> TestEvents(double t0, out int carried) =>
        TestChart.EventsFrom(events.Select(e => (e.Time, e.Length, e.Mods)), t0, out carried)
            .Select(e => new ChartEvent { Time = e.Time, Length = e.Length, Mods = e.Mods })
            .ToList();

    /// <summary>Checks a test chart with the game's own reader; the reason it can't be played, or null.</summary>
    private static string? ReaderProblem(ChartText text, out string playable)
    {
        playable = "";
        try { text.Validate(0); }
        catch (InvalidDataException ex) { return ex.Message; }
        playable = text.BuildPlayable(0, null);
        try
        {
            var built = NotesLoaderSM.Instance.LoadFromText(playable);
            if (built == null || built.steps == null || built.steps.Count == 0 || built.timingData == null)
                return "the game's chart reader found nothing playable in it";
        }
        catch (Exception ex) { return ex.Message; }
        return null;
    }

    // The editor's song for a test, when it loaded; the battle's file (or the game's music) otherwise.
    private static CustomMusic.Source? EditorMusicSource(string label)
    {
        var result = music;
        if (result == null) return null;
        return new CustomMusic.Source
        {
            Name = $"the editor's music for {label} (test)",
            Key = "",
            Decoded = () => (result.Stereo, result.SampleRate, result.Origin),
        };
    }

    // A start past the song's end would end the battle at once.
    private static bool PastTheMusic(double t0)
    {
        var result = music;
        if (result == null || result.SampleRate <= 0) return false;
        double end = result.Stereo.Length / 2.0 / result.SampleRate + result.Origin;
        if (t0 <= end - 1) return false;
        Say("The music ends before this point.", 4f);
        return true;
    }

    private static string NoNotesHere()
    {
        string key = ShortKey(EditorAction.TestFromStart);
        return "No notes from here on." + (key.Length > 0 ? $" {key} tests from the start." : "");
    }

    // ---- while it runs, and back ---------------------------------------------------------------

    /// <summary>Called every frame while the editor is open. True while a test has the screen.</summary>
    private static bool UpdateTest()
    {
        var outcome = TestPlay.TakeOutcome();
        if (outcome != null) Returned(outcome);
        switch (TestPlay.State)
        {
            case TestPlay.Phase.Starting:
                // The editor stays up while the game fades to black behind it; it only draws.
                DrawOnly();
                return true;
            case TestPlay.Phase.Running:
                if (!testHidden)
                {
                    Ui.SetVisible(false);
                    testHidden = true;
                }
                return true;
            default:
                return false;
        }
    }

    // The screen without reading keys or clicks.
    private static void DrawOnly()
    {
        if (screen != Screen.Edit || chart == null) return;
        Ui.UpdateButtons(null);
        double now = Now;
        if (tab != Tab.Keys) DrawField(now);
        DrawPanels(now);
    }

    // The battle was left: the screen shows again, on the same frame the game turns black.
    private static void Returned(TestPlay.Outcome outcome)
    {
        Ui.SetVisible(true);
        testHidden = false;
        inputFrom = Time.unscaledTime + 0.4f;
        if (chart != null) SeekTo(testFrom);
        // The key that runs the same test again: from the start after a Shift test.
        string key = ShortKey(testFromStart ? EditorAction.TestFromStart : EditorAction.TestHere);
        string where = testFromStart ? "from the start" : "from here";
        if (!outcome.Started)
            Say("The test didn't start. If the game behind stays dark, restart it.", 10f);
        else if (outcome.Finished)
        {
            string percent = (outcome.Percent ?? 0f).ToString("0.0", CultureInfo.InvariantCulture);
            string misses = outcome.FullCombo ? "full combo" : outcome.Misses == 0 ? "no misses" : outcome.Misses == 1 ? "1 miss" : $"{outcome.Misses} misses";
            Say($"Test finished: {percent}%, {misses}.{(key.Length > 0 ? $" {key} tests again {where}." : "")}", 10f);
        }
        else Say($"Back from the test.{(key.Length > 0 ? $" {key} tests again {where}." : "")}", 6f);
    }

    // ---- text ----------------------------------------------------------------------------------

    // ", F5 tests" for the status line on opening; nothing when Test has no key.
    private static string TestKeyHint()
    {
        string key = ShortKey(EditorAction.TestHere);
        return key.Length > 0 ? $", {key} tests" : "";
    }

    private static string TestText()
    {
        string here = ShortKey(EditorAction.TestHere), start = ShortKey(EditorAction.TestFromStart);
        return $"Test{(here.Length > 0 ? $" ({here})" : "")} plays this chart in the game from here" +
               $"{(start.Length > 0 ? $"; {start} from the start" : "")}. Nothing is saved.";
    }

    // 1:23.4
    private static string ShortTime(double seconds)
    {
        if (seconds < 0) return "-" + ShortTime(-seconds);
        int m = (int)(seconds / 60);
        return $"{m}:{(seconds - m * 60).ToString("00.0", CultureInfo.InvariantCulture)}";
    }

    // ---- QA ------------------------------------------------------------------------------------

    // QA ONLY: with NFS_QA_TESTPLAY in the game's environment, the editor opens by itself once the
    // title screen's menu shows and its startup intro is over (once a session), seeks, and presses
    // Test with no Ready prompt, so a whole test runs and comes back without input. The value is
    // battle:<folder>[#<slot 0-5>][@<seconds>|@start] or song:<SongData name>[#<melody>][@<seconds>|@start];
    // without @ it starts 20 s before the last note. Players never set it; without it this does nothing.
    private static readonly string QaSpec = (Environment.GetEnvironmentVariable("NFS_QA_TESTPLAY") ?? "").Trim();
    private enum QaStep { Waiting, Opened, Pressed, Done }
    private static QaStep qaStep;
    private static float qaNextCheck, qaOpenedAt;
    private static bool qaLoggedIntro;

    private static void QaTestPlay()
    {
        if (QaSpec.Length == 0 || qaStep == QaStep.Done) return;
        try
        {
            switch (qaStep)
            {
                case QaStep.Waiting:
                    QaOpen();
                    break;
                case QaStep.Opened:
                    QaPress();
                    break;
                case QaStep.Pressed:
                    if (TestPlay.Active) return;
                    qaStep = QaStep.Done;
                    ModLog.Info("Test play QA: done; the editor is yours.");
                    break;
            }
        }
        catch (Exception ex)
        {
            qaStep = QaStep.Done;
            testSkipReady = false;
            ModLog.Error("Test play QA failed: " + ex);
        }
    }

    private static (string Kind, string Name, int? Number, string? At) QaParse()
    {
        int colon = QaSpec.IndexOf(':');
        if (colon < 0) throw new FormatException($"NFS_QA_TESTPLAY=\"{QaSpec}\" isn't battle:... or song:...");
        string kind = QaSpec.Substring(0, colon).Trim().ToLowerInvariant();
        string rest = QaSpec.Substring(colon + 1);
        string? at = null;
        int atSign = rest.LastIndexOf('@');
        if (atSign >= 0)
        {
            at = rest.Substring(atSign + 1).Trim();
            rest = rest.Substring(0, atSign);
        }
        int? number = null;
        int hash = rest.LastIndexOf('#');
        if (hash >= 0 && int.TryParse(rest.Substring(hash + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
        {
            number = n;
            rest = rest.Substring(0, hash);
        }
        return (kind, rest.Trim(), number, at);
    }

    private static void QaOpen()
    {
        if (IsOpen || EditorOverlay.IsOpen || Time.unscaledTime < qaNextCheck) return;
        qaNextCheck = Time.unscaledTime + 1f;
        bool titleShowing = false;
        foreach (var menu in Resources.FindObjectsOfTypeAll<MainMenu>())
            if (menu && menu.optionsButton && menu.optionsButton.gameObject.activeInHierarchy) titleShowing = true;
        if (!titleShowing) return;
        // The game shows its main menu under its startup intro (the title background's load, then
        // about 20 s with no input); a player can't reach Options before the intro ends, and Test
        // refuses until then, so the QA waits too.
        if (TestPlay.IntroShowing())
        {
            if (!qaLoggedIntro) ModLog.Info("Test play QA: waiting for the title intro to end.");
            qaLoggedIntro = true;
            return;
        }
        var (kind, name, number, _) = QaParse();
        qaStep = QaStep.Opened;
        qaOpenedAt = Time.unscaledTime;
        ModLog.Info($"Test play QA: opening {QaSpec}.");
        if (kind == "battle")
        {
            OpenBattle(BattleChartTarget.FromFolder(Path.Combine(CustomBattles.Folder, name)), number ?? -1);
            return;
        }
        if (kind != "song") throw new FormatException($"\"{kind}\" isn't battle or song");
        var found = CustomCharts.FindSong(name) ?? throw new InvalidDataException($"there is no song named \"{name}\"");
        int melodies = found.beatmaps != null ? found.beatmaps.Length : 1;
        song = found;
        melody = Math.Clamp(number ?? 1, 1, Math.Max(1, melodies));
        BuildCanvas();
        EditorOverlay.Enter(OverlayOwner);
        // A copy of the game's hardest chart for the melody.
        var own = GameChart();
        StartEditing(own, own.Blocks.Count - 1, null);
        ModLog.Info($"Chart editor opened on {found.name} melody {melody} (QA).");
    }

    private static void QaPress()
    {
        if (!IsOpen)
        {
            qaStep = QaStep.Done;
            ModLog.Error("Test play QA: the chart editor didn't open.");
            return;
        }
        if (screen != Screen.Edit || loading != null || Time.unscaledTime < qaOpenedAt + 1f || TestPlay.Active) return;
        var (_, _, _, at) = QaParse();
        bool fromStart = string.Equals(at, "start", StringComparison.OrdinalIgnoreCase);
        if (!fromStart)
        {
            double seconds = at != null && double.TryParse(at, NumberStyles.Float, CultureInfo.InvariantCulture, out double given)
                ? given
                : chart!.Notes.Count > 0 ? chart.RowToSeconds(chart.Notes.Max(n => n.LastRow)) - 20 : 0;
            SeekTo(seconds);
        }
        qaStep = QaStep.Pressed;
        ModLog.Info(fromStart ? "Test play QA: pressed Test (from the start)." : $"Test play QA: pressed Test (from {Now.ToString("0.000", CultureInfo.InvariantCulture)} s).");
        testSkipReady = true;
        try { StartTest(fromStart); }
        finally { testSkipReady = false; }
        if (!TestPlay.Active) ModLog.Error($"Test play QA: Test didn't start: {Ui.Message}");
    }
}
