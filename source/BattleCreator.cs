using System.Diagnostics;
using UnityEngine;
using static NocturneFlatScroll.EditorInput;
using static NocturneFlatScroll.EditorUi;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;

namespace NocturneFlatScroll;

/// <summary>
/// The battle creator: makes and edits the custom battles in the CustomBattles folder. It opens
/// on a list of the battles (plus New, Import and Open folder); picking one opens its pages: info,
/// song, charts, enemy, gear and level, and dialogue. It looks and works like the chart editor (an
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

    // Keys pressed on the frame the creator opens or comes back from the chart editor belong to
    // what opened it (the Enter that picked the menu row, the Esc that closed the chart editor).
    private static int ignoreKeysFrame = -1;
    private static bool Live => Time.frameCount > ignoreKeysFrame;

    // Clicks wait a moment after the screen changes, so the second click of a double-click (or a
    // click that lands where the button that opened the screen was) doesn't choose on the new one.
    // Two prompts in a row count as a change, so a double-click can't answer both.
    private const float ClickDelay = 0.5f;
    private static float clicksFrom;
    private static bool ClicksLive => Time.unscaledTime >= clicksFrom;

    private static string Root => CustomBattles.Folder;

    // ---- opening and closing ----------------------------------------------------------------

    /// <summary>Opens the creator on its list of battles.</summary>
    internal static void Open()
    {
        if (IsOpen || ChartEditor.IsOpen) return;
        try
        {
            ui = new EditorUi("NocturneButBetter Battle Creator");
            Ui.BuildList();
            BuildListBack();
            BuildEdit();
            EditorOverlay.Enter(OverlayOwner);
            ignoreKeysFrame = Time.frameCount;
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
        ClearCardPreview();
        EndTyping();
        ui?.Destroy();
        ui = null;
        // Gives the cursor back; the menus come back once the key that closed the creator is let go.
        EditorOverlay.Leave(OverlayOwner);
        draft = null;
        song = null;
        audioLoad = null;
        picker = null;
        pending = null;
        pendingDone = null;
        handedOver = false;
        touched.Clear();
        pagePanels.Clear();
        controls.Clear();
        barControls.Clear();
        markerOn = null;
        fittedField = null;
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
            // On every screen, so the preview stops on time behind a prompt too.
            UpdatePreview();
            var keyboard = InputKeyboard.current;
            if (keyboard == null) return;
            FinishPending();
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
        clicksFrom = Time.unscaledTime + ClickDelay;
        Ui.ListPanel!.gameObject.SetActive(next != Screen.Edit);
        editPanel!.gameObject.SetActive(next == Screen.Edit);
    }

    /// <summary>
    /// Whether a list row was picked, like <see cref="EditorUi.Chosen"/>, except that a click in
    /// the first moment after the screen changed doesn't count (Enter always does).
    /// </summary>
    private static bool Chosen(InputKeyboard keyboard, int count, ref int index)
    {
        int before = index;
        bool chosen = Ui.Chosen(keyboard, count, ref index);
        if (!chosen || ClicksLive || Pressed(keyboard, Key.Enter) || Pressed(keyboard, Key.NumpadEnter)) return chosen;
        index = before;
        return false;
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

    private static Task? pending;
    private static Action<Task>? pendingDone;
    private static string pendingWhat = "";
    private static IntPtr gameWindow;

    /// <summary>
    /// Waits for <paramref name="task"/> without stopping the game, then runs <paramref name="done"/>
    /// with its result. Keys and clicks wait meanwhile; <paramref name="what"/> shows until then.
    /// </summary>
    private static void Run<T>(Task<T> task, string what, Action<T> done)
    {
        pending = task;
        pendingWhat = what;
        pendingDone = finished => done(((Task<T>)finished).Result);
        Say(what, 3600f);
    }

    private static void FinishPending()
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
            ModLog.Error($"Battle creator: {pendingWhat.TrimEnd('.')} failed: {(expected ? reason.Message : reason.ToString())}");
            Say("That didn't work: " + reason.Message, 7f);
        }
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is AggregateException agg && agg.InnerException != null) ex = agg.InnerException;
        return ex;
    }

    private static bool Busy => pending != null;

    /// <summary>Runs <paramref name="work"/> on a thread of its own in a single-threaded apartment, as the Windows shell asks.</summary>
    private static Task<T> OnShellThread<T>(Func<T> work)
    {
        var result = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { result.SetResult(work()); }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true, Name = "NocturneButBetter battle creator" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

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

    private const int ActionRows = 3;
    private static List<BattleFiles.BattleEntry> entries = new();
    private static int listIndex;

    private static void Rescan()
    {
        try
        {
            // Folders first, by title; zips (which are imported to be edited) after them.
            entries = BattleFiles.List(Root)
                .OrderBy(e => e.IsZip)
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
        bool chosen = Chosen(keyboard, count, ref listIndex);
        if (live && chosen)
        {
            ChooseListRow(listIndex);
            if (!IsOpen || screen != Screen.List) return;
        }
        listIndex = Math.Clamp(listIndex, 0, Math.Max(0, ActionRows + entries.Count - 1));
        Ui.DrawList("Battle creator", Escape(ListHint(listIndex)), ListLines(), listIndex);
    }

    private static List<string> ListLines()
    {
        var lines = new List<string> { "New battle...", "Import a .nbbbattle file...", "Open the battles folder" };
        foreach (var e in entries)
        {
            string name = e.Artist.Length > 0 ? $"{e.Title} - {e.Artist}" : e.Title;
            // What the battle sets for the player, like "set gear, level 12".
            string overrides = e.Overrides.Length > 0 ? "   " + e.Overrides : "";
            if (e.IsZip) lines.Add($"zip: {name}  (choose it to import and edit){overrides}");
            else if (e.Broken) lines.Add($"{name}  (battle.json can't be read)");
            else
            {
                string charted = e.Charted.Count > 0 ? string.Join(", ", e.Charted) : "not charted yet";
                string problems = e.Problems.Count == 0 ? "" : e.Problems.Count == 1 ? "   1 problem" : $"   {e.Problems.Count} problems";
                lines.Add($"{name}  ({e.Lanes} lanes; {charted}){overrides}{problems}");
            }
        }
        return lines;
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
            case 1: return "Unpacks a .nbbbattle file into a new battle folder you can edit.";
            case 2: return "Opens the CustomBattles folder in Windows Explorer.";
        }
        int i = index - ActionRows;
        if (i < 0 || i >= entries.Count) return "";
        var e = entries[i];
        if (e.IsZip) return "A zip can't be edited as it is. Choose it to unpack it into a battle folder you can edit.";
        if (e.Problems.Count > 0)
            return (e.Broken ? "Can't open it: " : "Problem: ") + e.Problems[0] + (e.Problems.Count > 1 ? $" (and {e.Problems.Count - 1} more)" : "");
        return "Click a battle to edit it, or Up/Down and Enter.  F5 reads the folder again.";
    }

    private static void ChooseListRow(int index)
    {
        switch (index)
        {
            case 0: StartNewBattle(); return;
            case 1: StartImport(); return;
            case 2: OpenInExplorer(Root); return;
        }
        int i = index - ActionRows;
        if (i < 0 || i >= entries.Count) return;
        var e = entries[i];
        if (e.IsZip) AskImportZip(e.Path, e.Title);
        else if (e.Broken) Say("battle.json can't be read: " + (e.Problems.FirstOrDefault() ?? "unknown problem"), 6f);
        else OpenBattle(e.Path);
    }

    /// <summary>Puts the list's cursor on a battle folder.</summary>
    private static void SelectInList(string folder)
    {
        int i = entries.FindIndex(e => string.Equals(Path.GetFullPath(e.Path), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase));
        listIndex = i >= 0 ? ActionRows + i : 0;
    }

    // ---- small list screens: pick one of a few rows ------------------------------------------------

    private sealed class Picker
    {
        internal string Heading = "";
        internal List<string> Rows = new();
        internal Func<int, string> Hint = _ => "";
        internal Action<int> Choose = _ => { };
        internal Action Back = () => { };
        internal int Index;
    }

    private static Picker? picker;

    private static void ShowPicker(Picker next)
    {
        // A prompt or a list interrupts the song's preview.
        StopPreview();
        picker = next;
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
        if (p == null) { BackFromPicker(); return; }
        bool live = Live && !Busy;
        if (live && Pressed(keyboard, Key.Escape)) { picker = null; p.Back(); return; }
        if (live) p.Index = MoveInList(keyboard, p.Index, p.Rows.Count);
        bool chosen = Chosen(keyboard, p.Rows.Count, ref p.Index);
        if (live && chosen)
        {
            picker = null;
            p.Choose(p.Index);
            // A choice that doesn't go anywhere else goes back.
            if (IsOpen && screen == Screen.Pick && picker == null) BackFromPicker();
            return;
        }
        string hint = Ui.MessageShowing ? Ui.Message : p.Hint(p.Index);
        Ui.DrawList(Escape(p.Heading), Escape(hint), p.Rows, p.Index);
    }

    /// <summary>A yes or no prompt, with the cursor on no.</summary>
    /// <param name="yesFirst">
    /// Yes on the first row instead of the second: a second prompt right after a first one
    /// then has its No where the first one had its Yes.
    /// </param>
    private static Picker Confirm(string heading, string no, string yes, string hint, Action onYes, Action onNo, bool yesFirst = false) => new()
    {
        Heading = heading,
        Rows = yesFirst ? new List<string> { yes, no } : new List<string> { no, yes },
        Index = yesFirst ? 1 : 0,
        Hint = _ => hint,
        Choose = i => { if (i == (yesFirst ? 0 : 1)) onYes(); else onNo(); },
        Back = onNo,
    };

    // ---- the chart editor ---------------------------------------------------------------------------

    // While the chart editor has the screen, the creator waits hidden (and keeps the menus locked).
    private static bool handedOver;
    private static int handOverFrame;

    /// <summary>Opens the battle's chart in the chart editor, on a difficulty slot (0 to 5) or -1 for its choice.</summary>
    private static void EditCharts(int slot)
    {
        if (draft == null || !FinishTyping()) return;
        StopPreview();
        string? chart = PackageFiles.SafeName(draft.ChartPath);
        string? audio = PackageFiles.SafeName(draft.EffectiveAudio);
        if (chart == null || audio == null) { Say("The battle needs a chart file and a song first.", 4f); return; }
        handedOver = true;
        handOverFrame = Time.frameCount;
        bool opened;
        try { opened = OpenCharts(draft.Folder, chart, audio, draft.Lanes, draft.Title, slot, ChartsClosed); }
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
        ModLog.Info($"Battle creator: opened the chart of {draft.Folder} in the chart editor ({SlotName(slot)}).");
    }

    /// <summary>
    /// Hands the battle's chart to the chart editor; true when it opened. The editor calls
    /// <paramref name="onClosed"/> when it closes, and the creator shows again.
    /// </summary>
    private static bool OpenCharts(string folder, string chartPath, string audioPath, int lanes, string title, int slot, Action onClosed)
    {
        // A failed open still calls onClosed (a frame later); ChartsClosed ignores it then. A test
        // play from the editor uses the battle as it is here, saved or not.
        ChartEditor.OpenBattle(new BattleChartTarget(folder, chartPath, audioPath, lanes, title, onClosed) { Manifest = () => draft?.ManifestJson() }, slot);
        return ChartEditor.IsOpen;
    }

    private static string SlotName(int slot) => slot >= 0 && slot < ChartText.GameDifficultyLabels.Length ? ChartText.GameDifficultyLabels[slot] : "any difficulty";

    // The chart editor closed: the creator shows again with the chart as it is now. Not over a
    // test play's battle, though: if the editor went during one, the creator waits for its end.
    private static void ChartsClosed()
    {
        if (!IsOpen || !handedOver || TestPlay.Active) return;
        handedOver = false;
        ignoreKeysFrame = Time.frameCount;
        clicksFrom = Time.unscaledTime + ClickDelay;
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

    // ---- QA ------------------------------------------------------------------------------------------

    // QA ONLY: with NFS_QA_CREATOR=1 in the game's environment, the creator opens by itself once
    // the title screen's menu is showing (once a session), so the lead can test it without
    // clicking through the menus. Players never set it; without it this does nothing.
    private static readonly bool QaOpen = Environment.GetEnvironmentVariable("NFS_QA_CREATOR") == "1";
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
