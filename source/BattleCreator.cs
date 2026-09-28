using System.Diagnostics;
using UnityEngine;
using static NocturnePlus.EditorInput;
using static NocturnePlus.EditorPageKit;
using static NocturnePlus.EditorUi;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;

namespace NocturnePlus;

/// <summary>
/// The battle creator: makes and edits the custom battles in the CustomBattles folder. It opens
/// on a list of the battles (plus New, New from an osu!mania beatmap (beta), Import and Open
/// folder); picking one opens its pages: info, song, charts, enemy, art, gear and level, and
/// dialogue. It looks and works like the chart editor (an
/// <see cref="EditorUi"/> drawn over the game, Windows file pickers, mouse or keyboard), and the
/// game's menus underneath are locked while it is open (<see cref="EditorOverlay"/>). Charting
/// hands over to the chart editor and comes back when it closes.
/// </summary>
internal static partial class BattleCreator
{
    private enum Screen { List, Pick, Edit }

    internal static bool IsOpen => ui != null && ui.IsAlive;

    // The screen, built on open and destroyed on close.
    private static EditorUi? ui;
    private static EditorUi Ui => ui ?? throw new InvalidOperationException("the battle creator's screen isn't built");
    private static Screen screen;

    // Names the creator to EditorOverlay, which keeps the menus locked while any editor is open.
    private static readonly object OverlayOwner = "battle creator";

    // The page kit, built with the screen: pickers, typing, work that finishes later, and when
    // keys and clicks count (keys on the frame the creator opens or comes back from the chart
    // editor, and clicks just after the screen changes, belong to what came before).
    private static EditorPageKit? kit;
    private static EditorPageKit Kit => kit ?? throw new InvalidOperationException("the battle creator's screen isn't built");
    private static bool Live => kit?.Live ?? true;
    private static bool ClicksLive => kit?.ClicksLive ?? true;

    private static string Root => CustomBattles.Folder;

    // ---- opening and closing ----------------------------------------------------------------

    /// <summary>Opens the creator on its list of battles.</summary>
    internal static void Open()
    {
        if (IsOpen || ChartEditor.IsOpen) return;
        try
        {
            ui = new EditorUi("NocturneButBetter Battle Creator");
            kit = new EditorPageKit(Ui, "Battle creator") { CanType = () => draft != null, KeysWhileTyping = SaveKey };
            Ui.BuildList();
            BuildListBack();
            Kit.BuildPickerFace();
            BuildEdit();
            EditorOverlay.Enter(OverlayOwner);
            Kit.IgnoreKeysNow();
            gameWindow = Process.GetCurrentProcess().MainWindowHandle;
            try { BattleFiles.CleanWork(Root); }
            catch (Exception ex) { ModLog.Error("Battle creator: clearing old work folders failed: " + ex.Message); }
            Rescan();
            listIndex = 0;
            ShowScreen(Screen.List);
            ModLog.Info($"Battle creator: opened, {entries.Count(e => !e.IsZip)} battle folders and {entries.Count(e => e.IsZip)} zips in {Root}.");
        }
        catch (Exception ex)
        {
            ModLog.Error("Battle creator: opening failed: " + ex);
            Close();
        }
    }

    private static void Close()
    {
        StopPreview();
        StopArtPreview(true);
        StopDialoguePreview(true);
        ClearCardPreview();
        EndTyping();
        ui?.Destroy();
        ui = null;
        // The kit goes with the screen, and with it the picker showing and any work still going.
        // A read still running is dropped (it writes nothing); a battle being made still finishes,
        // and shows the next time the creator opens.
        kit = null;
        // Gives the cursor back; the menus come back once the key that closed the creator is let go.
        EditorOverlay.Leave(OverlayOwner);
        draft = null;
        song = null;
        audioLoad = null;
        ClearOsz();
        handedOver = false;
        hubHandedOver = false;
        touched.Clear();
        pagePanels.Clear();
        controls.Clear();
        barControls.Clear();
        ModLog.Info("Battle creator: closed.");
    }

    /// <summary>Called every frame, after the chart editor's update.</summary>
    internal static void Update()
    {
        EditorOverlay.Update();
        try
        {
            QaAutoOpen();
            if (ui == null) return;
            // Only something else destroying the canvas gets here with the screen still set. Close
            // properly, or EditorOverlay would keep the menus and their Back locked for good.
            if (!ui.IsAlive)
            {
                ModLog.Error("Battle creator: its screen was destroyed from outside; closing it.");
                Close();
                return;
            }
            FinishCleanup();
            if (handedOver) { UpdateHandedOver(); return; }
            if (hubHandedOver) { UpdateHubHandedOver(); return; }
            // On every screen, so the preview stops on time behind a prompt too.
            UpdatePreview();
            var keyboard = InputKeyboard.current;
            if (keyboard == null) return;
            Kit.FinishPending();
            if (!IsOpen) return;
            switch (screen)
            {
                case Screen.List: UpdateList(keyboard); break;
                case Screen.Pick: UpdatePicker(keyboard); break;
                case Screen.Edit: UpdateEdit(keyboard, InputMouse.current); break;
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Battle creator: failed, so it closes (unsaved changes are lost): " + ex);
            if (ui != null) Close();
        }
    }

    private static void ShowScreen(Screen next)
    {
        screen = next;
        Kit.DelayClicks();
        Ui.ListPanel!.gameObject.SetActive(next != Screen.Edit);
        editPanel!.gameObject.SetActive(next == Screen.Edit);
        // A picker with a picture shows it again on its first update.
        Kit.HidePickerFace();
    }

    // The list screens' button for the mouse: Close on the list of battles, Back on a prompt or a
    // picker. Esc does the same.
    private static void BuildListBack()
    {
        var b = Ui.MakeButton(Ui.ListPanel!, "", () =>
        {
            if (!Live || Busy || !ClicksLive) return;
            if (screen == Screen.List) Close();
            else if (picker is { } p)
            {
                picker = null;
                p.Back();
            }
        });
        b.Text = () => (screen == Screen.List ? "Close" : "Back") + " <size=65%><color=#9D92B4>Esc</color></size>";
        Place(b.Rect, new Vector2(1, 0), new Vector2(-16, 20), new Vector2(170, 52), new Vector2(1, 0));
    }

    private static void Say(string text, float seconds = 4f) => ui?.Say(text, seconds);

    // ---- work that finishes later: file pickers, copying, zipping ------------------------------

    private static IntPtr gameWindow;

    /// <summary>
    /// Waits for <paramref name="task"/> without stopping the game, then runs <paramref name="done"/>
    /// with its result (see <see cref="EditorPageKit.Run"/>). Keys and clicks wait meanwhile;
    /// <paramref name="what"/> shows until then. Dropped when the creator is closed.
    /// </summary>
    private static void Run<T>(Task<T> task, string what, Action<T> done) => kit?.Run(task, what, done);

    private static bool Busy => pending != null;

    // The work still going, or null (the QA drivers wait on it).
    private static Task? pending => kit?.Pending;

    private static Task<T> OnShellThread<T>(Func<T> work) => EditorPageKit.OnShellThread(work, "NocturneButBetter battle creator");

    private static void OpenInExplorer(string folder)
    {
        try
        {
            string path = Path.GetFullPath(folder).Replace('/', '\\');
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            ModLog.Info($"Battle creator: opened {path} in Explorer.");
        }
        catch (Exception ex)
        {
            ModLog.Error("Battle creator: opening the folder failed: " + ex);
            Say("Couldn't open the folder: " + ex.Message, 5f);
        }
    }

    // ---- the list of battles -------------------------------------------------------------------

    private const int ActionRows = 4;
    private static List<BattleFiles.BattleEntry> entries = new();
    private static int listIndex;
    // Where Get Custom Battles puts the battles it downloads (CustomBattles\Downloaded).
    private static string hubFolder = "";

    // A battle the hub downloaded: listed after the others, as "from the hub".
    private static bool FromHub(BattleFiles.BattleEntry e) => e.IsZip && hubFolder.Length > 0 && BattleFiles.IsInside(e.Path, hubFolder);

    private static void Rescan()
    {
        try
        {
            hubFolder = Path.Combine(Root, "Downloaded");
            // Folders first, by title; zips (which are imported to be edited) after them, and the hub's downloads last.
            entries = BattleFiles.List(Root, GameCheck)
                .OrderBy(e => e.IsZip)
                .ThenBy(FromHub)
                .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            entries = new List<BattleFiles.BattleEntry>();
            ModLog.Error("Battle creator: reading the battles folder failed: " + ex);
            Say("Couldn't read the battles folder: " + ex.Message, 6f);
        }
    }

    private static void UpdateList(InputKeyboard keyboard)
    {
        int count = ActionRows + entries.Count;
        bool live = Live && !Busy;
        if (live && Pressed(keyboard, Key.Escape)) { Close(); return; }
        if (live && Pressed(keyboard, Key.F5)) { Rescan(); Say("Read the battles folder again.", 2f); }
        if (live) listIndex = MoveInList(keyboard, listIndex, count);
        // A row clicked with the mouse is chosen on the next update.
        bool chosen = Kit.Chosen(keyboard, count, ref listIndex);
        if (live && chosen)
        {
            ChooseListRow(listIndex);
            if (!IsOpen || screen != Screen.List) return;
        }
        listIndex = Math.Clamp(listIndex, 0, Math.Max(0, ActionRows + entries.Count - 1));
        Ui.DrawList("Battle creator", Escape(ListHint(listIndex)), ListLines(), listIndex, ListTags());
    }

    private static List<string> ListLines()
    {
        var lines = new List<string> { "New battle...", OszSummary.ListRow, "Import a .nbbbattle file...", "Open the battles folder" };
        foreach (var e in entries)
        {
            string name = e.Artist.Length > 0 ? $"{e.Title} - {e.Artist}" : e.Title;
            // What the battle sets for the player, like "set gear, level 12".
            string overrides = e.Overrides.Length > 0 ? "   " + e.Overrides : "";
            string problems = e.Problems.Count == 0 ? "" : e.Problems.Count == 1 ? "   1 problem" : $"   {e.Problems.Count} problems";
            if (e.IsZip) lines.Add($"{(FromHub(e) ? "from the hub" : "zip")}: {name}  ({(e.Loads ? "choose it to import and edit" : "the arcade skips it")}){overrides}{problems}");
            else if (e.Broken) lines.Add($"{name}  (battle.json can't be read)");
            else
            {
                string charted = e.Charted.Count > 0 ? string.Join(", ", e.Charted) : "not charted yet";
                lines.Add($"{name}  ({e.Lanes} lanes; {charted}){overrides}{problems}");
            }
        }
        return lines;
    }

    /// <summary>What shows right after a row's text in amber: the osu!mania import's beta warning.</summary>
    private static List<string?> ListTags()
    {
        var tags = new List<string?>(new string?[ActionRows + entries.Count]);
        tags[1] = OszSummary.Beta;
        return tags;
    }

    private static string ListHint(int index)
    {
        if (Ui.MessageShowing) return Ui.Message;
        string hint = RowHint(index);
        return (hint.Length > 0 ? hint + "  " : "") + "Esc leaves.";
    }

    private static string RowHint(int index)
    {
        switch (index)
        {
            case 0: return "Pick a song file, and a battle is made around it. Then chart it and set up its enemy.";
            case 1: return OszSummary.ListHint;
            case 2: return "Unpacks a .nbbbattle file into a new battle folder you can edit.";
            case 3: return "Opens the CustomBattles folder in Windows Explorer.";
        }
        int i = index - ActionRows;
        if (i < 0 || i >= entries.Count) return "";
        var e = entries[i];
        string more = e.Problems.Count > 1 ? $" (and {e.Problems.Count - 1} more)" : "";
        if (e.IsZip)
        {
            const string unpack = "Choose it to unpack it into a battle folder you can edit.";
            if (e.Problems.Count == 0) return "A zip can't be edited as it is. " + unpack;
            // A zip the loader refuses is left out of the arcade, which only says why in the log.
            return (e.Loads ? "Problem: " : "The arcade skips it: ") + e.Problems[0] + more + ".  " + unpack;
        }
        if (e.Problems.Count > 0)
            return (e.Broken ? "Can't open it: " : "Problem: ") + e.Problems[0] + more + (e.CopyOf != null ? ".  Choose it to make it a separate battle." : "");
        return "Click a battle to edit it, or Up/Down and Enter.  F5 reads the folder again.";
    }

    private static void ChooseListRow(int index)
    {
        switch (index)
        {
            case 0: StartNewBattle(); return;
            case 1: StartOszImport(); return;
            case 2: StartImport(); return;
            case 3: OpenInExplorer(Root); return;
        }
        int i = index - ActionRows;
        if (i < 0 || i >= entries.Count) return;
        var e = entries[i];
        if (e.IsZip) AskImportZip(e.Path, e.Title);
        else if (e.Broken) Say("battle.json can't be read: " + (e.Problems.FirstOrDefault() ?? "unknown problem"), 6f);
        else if (e.CopyOf != null) AskSeparate(e.Path, e.Title, e.CopyOf);
        else OpenBattle(e.Path);
    }

    /// <summary>
    /// A battle folder copied in Explorer has the same battle id as the original, and the arcade
    /// shows only the one it finds first: this gives the other one an id of its own, or opens it
    /// as it is.
    /// </summary>
    private static void AskSeparate(string folder, string title, string original)
    {
        string other = Path.GetFileName(original);
        ShowPicker(new Picker
        {
            Heading = $"\"{title}\" has the same battle id as {other}",
            Rows = { "Make it a separate battle", "Edit it as it is" },
            Hint = i => i == 0
                ? $"Gives it a battle id of its own, so the arcade shows both. The scores so far stay with {other}.  Esc goes back."
                : $"Opens it as it is. The arcade keeps showing only {other}.  Esc goes back.",
            Choose = i =>
            {
                if (i == 1) { OpenBattle(folder); return; }
                try { BattleFiles.MakeSeparate(folder); }
                catch (Exception ex) when (BattleDraft.IsFileProblem(ex))
                {
                    ModLog.Error($"Battle creator: giving {folder} a battle id of its own failed: {ex.Message}");
                    Say("It couldn't be made a separate battle: " + ex.Message, 7f);
                    ShowScreen(Screen.List);
                    return;
                }
                ModLog.Info($"Battle creator: gave {folder} a battle id of its own (it had the id of {original}).");
                Rescan();
                SelectInList(folder);
                OpenBattle(folder);
                Say("It is a separate battle now: the arcade shows both.", 5f);
                RefreshArcade();
            },
            Back = () => ShowScreen(Screen.List),
        });
    }

    // The game's chart reader must take a battle's chart, or the arcade skips the battle
    // (CustomBattles.CheckChart). Answers are kept by the battle's files, so the list asks the
    // game once per version of a chart. A reader that fails gives no answer, not a problem.
    private static readonly Dictionary<string, string?> gameChecks = new();
    // The last answer for each battle folder or zip (by its full path), a failed reader's "no answer"
    // included, so the osz import reads what the list and the Charts page were told instead of asking again.
    private static readonly Dictionary<string, string?> lastGameChecks = new(StringComparer.OrdinalIgnoreCase);

    private static string? GameCheck(BattlePackage package)
    {
        string key = package.Location + "|" + package.Fingerprint;
        if (!gameChecks.TryGetValue(key, out var problem))
        {
            try
            {
                var built = NotesLoaderSM.Instance.LoadFromText(package.PlayableText);
                if (built == null || built.steps == null || built.steps.Count == 0 || built.timingData == null)
                    problem = "the game's chart reader finds nothing playable in its chart";
            }
            catch (Exception ex)
            {
                ModLog.Error($"Battle creator: the game's chart reader couldn't check {package.Location}: {ex.Message}");
                KeepGameCheck(package.Location, null);
                return null;
            }
            if (gameChecks.Count >= 500) gameChecks.Clear();
            gameChecks[key] = problem;
        }
        KeepGameCheck(package.Location, problem);
        return problem;
    }

    private static void KeepGameCheck(string path, string? problem)
    {
        if (lastGameChecks.Count >= 500) lastGameChecks.Clear();
        lastGameChecks[GameCheckPlace(path)] = problem;
    }

    /// <summary>
    /// What <see cref="GameCheck"/> last said about a battle folder or zip, without asking the game
    /// again; false when it hasn't been asked about it since <see cref="ForgetGameCheck"/>.
    /// </summary>
    private static bool LastGameCheck(string path, out string? problem) =>
        lastGameChecks.TryGetValue(GameCheckPlace(path), out problem);

    private static void ForgetGameCheck(string path) => lastGameChecks.Remove(GameCheckPlace(path));

    private static string GameCheckPlace(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return path; }
    }

    /// <summary>Puts the list's cursor on a battle folder.</summary>
    private static void SelectInList(string folder)
    {
        int i = entries.FindIndex(e => string.Equals(Path.GetFullPath(e.Path), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase));
        listIndex = i >= 0 ? ActionRows + i : 0;
    }

    // ---- small list screens: pick one of a few rows (the kit's pickers) -----------------------------

    // The picker showing, under its old name (the pages and the QA drivers set it).
    private static Picker? picker
    {
        get => kit?.Picking;
        set { if (kit != null) kit.Picking = value; }
    }

    // What has been typed to jump in the picker's list (the QA drivers read it).
    private static string jumpTyped => kit?.JumpTyped ?? "";

    private static void ShowPicker(Picker next)
    {
        // A prompt or a list interrupts the song's preview, and the dialogue's.
        StopPreview();
        StopDialoguePlay();
        Kit.ShowPicker(next);
        ShowScreen(Screen.Pick);
    }

    /// <summary>Back to the battle's pages when one is open, else to the list.</summary>
    private static void BackFromPicker()
    {
        picker = null;
        ShowScreen(draft != null ? Screen.Edit : Screen.List);
    }

    private static void UpdatePicker(InputKeyboard keyboard)
    {
        var p = picker;
        switch (Kit.UpdatePicker(keyboard))
        {
            case PickerStep.None:
                BackFromPicker();
                return;
            case PickerStep.Chosen:
                // A choice that doesn't go anywhere else goes back.
                if (IsOpen && screen == Screen.Pick && picker == null) BackFromPicker();
                return;
            case PickerStep.Shown:
                // Only the highlighted row's faces stay loaded (see FacesOf).
                if (p!.Face != null) ReleaseUnwantedFaces();
                return;
        }
    }

    // ---- the chart editor ---------------------------------------------------------------------------

    // While the chart editor has the screen, the creator waits hidden (and keeps the menus locked).
    private static bool handedOver;
    private static int handOverFrame;

    /// <summary>
    /// Opens the battle's chart in the chart editor, on a difficulty slot (0 to 5) or -1 for its
    /// choice. With <paramref name="test"/>, the editor starts a test at once (from that many
    /// seconds in, or from the start at 0) and closes itself when the test ends.
    /// </summary>
    private static void EditCharts(int slot, double? test = null)
    {
        if (draft == null || !FinishTyping()) return;
        StopPreview();
        StopArtPreview(false);
        StopDialoguePreview(false);
        string? chart = PackageFiles.SafeName(draft.ChartPath);
        string? audio = PackageFiles.SafeName(draft.EffectiveAudio);
        if (chart == null || audio == null) { Say("The battle needs a chart file and a song first.", 4f); return; }
        // Saving writes chart text to the file battle.json's "chart" names, so it must be an .sm file of its own.
        if (draft.ChartFileProblem() is { } bad)
        {
            Say($"The chart can't be edited here: {bad}. In battle.json, \"chart\" has to name an .sm file of its own.", 8f);
            return;
        }
        handedOver = true;
        handOverFrame = Time.frameCount;
        bool opened;
        try { opened = OpenCharts(draft.Folder, chart, audio, draft.Lanes, draft.Title, slot, ChartsClosed, test); }
        catch (Exception ex)
        {
            opened = false;
            ModLog.Error("Battle creator: opening the chart editor failed: " + ex);
            Say("The chart editor didn't open: " + ex.Message, 6f);
        }
        if (!opened)
        {
            handedOver = false;
            return;
        }
        Ui.SetVisible(false);
        ModLog.Info($"Battle creator: opened the chart of {draft.Folder} in the chart editor ({SlotName(slot)})" +
                    (test is double at ? $" to test it from {(at > 0 ? DialogueReader.Clock(at) : "the start")}." : "."));
    }

    /// <summary>
    /// Hands the battle's chart to the chart editor; true when it opened. The editor calls
    /// <paramref name="onClosed"/> when it closes, and the creator shows again.
    /// </summary>
    private static bool OpenCharts(string folder, string chartPath, string audioPath, int lanes, string title, int slot, Action onClosed, double? test)
    {
        // A failed open still calls onClosed (a frame later); ChartsClosed ignores it then. A test
        // play from the editor uses the battle as it is here, saved or not, dialogue too.
        ChartEditor.OpenBattle(new BattleChartTarget(folder, chartPath, audioPath, lanes, title, onClosed)
        {
            Manifest = () => draft?.ManifestJson(),
            DialogueJson = () => draft?.DialogueJson(),
            Dialogue = draft != null ? DialogueLinkFor(draft) : null,
        }, slot, test);
        return ChartEditor.IsOpen;
    }

    private static string SlotName(int slot) => slot >= 0 && slot < ChartText.GameDifficultyLabels.Length ? ChartText.GameDifficultyLabels[slot] : "any difficulty";

    // The chart editor closed: the creator shows again with the chart as it is now. Not over a
    // test play's battle, though: if the editor went during one, the creator waits for its end.
    private static void ChartsClosed()
    {
        if (!IsOpen || !handedOver || TestPlay.Active) return;
        handedOver = false;
        Kit.IgnoreKeysNow();
        Kit.DelayClicks();
        Ui.SetVisible(true);
        if (draft != null)
        {
            // The chart may have changed, and with it the #MUSIC a battle without "audio" plays.
            string audio = draft.EffectiveAudio;
            draft.ChartChanged();
            if (draft.EffectiveAudio != audio) StartAudioLoad();
        }
        RefreshBattleInfo();
        ModLog.Info("Battle creator: back from the chart editor.");
    }

    private static void UpdateHandedOver()
    {
        // The chart editor closed without saying so: come back anyway.
        if (!ChartEditor.IsOpen && Time.frameCount > handOverFrame + 1) ChartsClosed();
    }

    // ---- the hub (Get Custom Battles) -----------------------------------------------------------------

    // While the hub's page has the screen to upload the battle, the creator waits hidden (and keeps the menus locked).
    private static bool hubHandedOver, hubShown;

    /// <summary>
    /// "Upload to the hub...": saves the battle, then hands over to the hub's page on its Upload tab
    /// with this battle; the creator shows again when the page closes. Only from the title screen
    /// (the hub's own rule), never in the story.
    /// </summary>
    private static void StartHubUpload()
    {
        if (draft == null || !FinishTyping()) return;
        if (draft.Dirty && !Save()) return;
        if (!HubPage.CanOpen(out string why, fromCreator: true))
        {
            Say(why, 6f);
            return;
        }
        StopPreview();
        StopArtPreview(false);
        StopDialoguePreview(false);
        string folder = draft.Folder;
        hubHandedOver = true;
        Ui.SetVisible(false);
        HubPage.OpenForUpload(folder, HubClosed);
        if (!HubPage.IsOpen)
        {
            hubHandedOver = false;
            Ui.SetVisible(true);
            Say("The hub didn't open (see the log).", 5f);
            return;
        }
        ModLog.Info($"Battle creator: handed {folder} to the hub to upload.");
    }

    // The hub's page closed: the creator shows again, on the battle as it was.
    private static void HubClosed()
    {
        if (!IsOpen || !hubHandedOver) return;
        hubHandedOver = false;
        Kit.IgnoreKeysNow();
        Kit.DelayClicks();
        Ui.SetVisible(true);
        RefreshBattleInfo();
        ModLog.Info("Battle creator: back from the hub.");
    }

    private static void UpdateHubHandedOver()
    {
        // The hub's page went without saying so: come back anyway.
        if (!HubPage.IsOpen) HubClosed();
    }

    // ---- QA ------------------------------------------------------------------------------------------

    // QA ONLY: with NFS_QA_CREATOR=1 in the game's environment, the creator opens by itself once
    // the title screen's menu is showing (once a session), so the lead can test it without
    // clicking through the menus. Players never set it; without it this does nothing.
    private static readonly bool QaOpen = QaBuild.Env("NFS_QA_CREATOR") == "1";
    private static bool qaDone;
    private static float qaNextCheck;

    private static void QaAutoOpen()
    {
        if (!QaOpen || qaDone || IsOpen || Time.unscaledTime < qaNextCheck) return;
        qaNextCheck = Time.unscaledTime + 1f;
        if (EditorOverlay.IsOpen) return;
        foreach (var menu in Resources.FindObjectsOfTypeAll<MainMenu>())
        {
            if (!menu || !menu.optionsButton || !menu.optionsButton.gameObject.activeInHierarchy) continue;
            qaDone = true;
            ModLog.Info("Battle creator: opening by itself on the title screen (NFS_QA_CREATOR=1, QA only).");
            Open();
            return;
        }
    }
}
