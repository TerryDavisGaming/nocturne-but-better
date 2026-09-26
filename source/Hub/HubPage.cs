using System.Globalization;
using UnityEngine;
using static NocturneFlatScroll.EditorInput;
using static NocturneFlatScroll.EditorPageKit;
using static NocturneFlatScroll.EditorUi;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;

namespace NocturneFlatScroll;

/// <summary>
/// Get Custom Battles: the online hub's page (DESIGN-HUB 1). One full-screen EditorUi over the
/// title screen with four tabs: Browse (the hub's entries, like osu!'s beatmap listing: search,
/// filters, rows with thumbnails, a detail panel, download), Installed (what the hub installed on
/// this PC: updates, Use it now, delete), My uploads (this PC's uploads, new versions, delete from
/// the hub, the hub key's backup, restore and replacement) and Upload (a battle or a pack of custom
/// difficulties, checked and built here, sent after the rules are confirmed). It opens from the
/// title's Get Custom Battles button (CustomChartsMenu) or the battle creator's Upload button, never
/// during a battle. The hub is contacted only while the page is open, and never when the Online hub
/// option is off. Everything from the network is shown with rich text off. Work runs on worker
/// threads through the page's own job list (<see cref="HubJobs"/>), so the page keeps drawing and
/// taking keys while a list loads or a download runs; the game's own steps run here, on the main
/// thread. It works with the mouse, the keyboard and a pad (<see cref="PadInput"/>).
/// </summary>
internal static partial class HubPage
{
    private enum Tab { Browse, Installed, Mine, Upload }

    private enum Screen { Main, Pick, Form }

    /// <summary>Where the keyboard and the pad are on the main screen: the list, the bar above it, or the panel's buttons.</summary>
    private enum Zone { List, Bar, Panel }

    /// <summary>How the hub answers: the page's files are read first, then /v1/info.</summary>
    private enum Link { Opening, Connecting, Online, Down, TooOld }

    // Names the page to EditorOverlay, which keeps the menus locked while it is open.
    private static readonly object OverlayOwner = "hub";
    // The chart editor's remembered name for charts, the display name's default at the first upload.
    private const string AuthorPref = "NocturneFlatScroll.EditorAuthor.v1";

    private static EditorUi? ui;
    private static EditorUi Ui => ui ?? throw new InvalidOperationException("the hub page isn't open");
    private static EditorPageKit? kit;
    private static EditorPageKit Kit => kit ?? throw new InvalidOperationException("the hub page isn't open");
    private static Screen screen;
    private static Tab tab;
    private static Zone zone;
    private static int panelFocus, barFocus;

    private static readonly HubJobs jobs = new();
    private static readonly HubThumbs thumbs = new();
    private static CancellationTokenSource? pageCts;
    private static CancellationToken Ct => pageCts?.Token ?? CancellationToken.None;
    private static HubPaths? paths;
    private static HubStore? store;
    private static HubApi? api;
    private static HubInfo? info;
    private static Link link;
    private static string linkProblem = "";
    private static HubIdentity? identity;
    private static string? identityProblem;
    private static IHubRecycler? recycler;
    private static string defaultName = "";
    // The battle creator waits for the page to close; a battle it sent to upload once the hub answers.
    private static Action? whenClosed;
    private static string? pendingUpload;
    private static float nextGuardCheck;

    /// <summary>Whether the page is open.</summary>
    internal static bool IsOpen => ui != null && ui.IsAlive;

    /// <summary>Whether the page reads the pad now (PadInput is read only then).</summary>
    internal static bool WantsPad => IsOpen;

    // ---- opening and closing ------------------------------------------------------------------

    /// <summary>
    /// Whether the page can open now, and if not, why: the hub is on, the title screen is up (not
    /// the story, a battle, the arcade or the startup intro), and no other mod page is open (the
    /// battle creator may hand over to it).
    /// </summary>
    internal static bool CanOpen(out string why, bool fromCreator = false)
    {
        why = "";
        if (IsOpen)
        {
            why = "The hub is already open.";
            return false;
        }
        if (!HubGame.Enabled)
        {
            why = SettingsState.OnlineHub ? "This build of the mod has no hub." : "The online hub is off (Options > Custom Charts > Online hub).";
            return false;
        }
        if (ChartEditor.IsOpen || TestPlay.Active || (!fromCreator && (BattleCreator.IsOpen || EditorOverlay.IsOpen)))
        {
            why = "Close the other page first.";
            return false;
        }
        if (!OnTitle())
        {
            why = fromCreator
                ? "Uploading works from the title screen. Open the battle creator from Options there."
                : "Get Custom Battles works from the title screen.";
            return false;
        }
        bool intro = false;
        try { intro = TestPlay.IntroShowing(); }
        catch (Exception ex) { ReportOnce("checking for the title's startup intro", ex); }
        if (intro)
        {
            why = "The title screen is still starting. Try again in a moment.";
            return false;
        }
        return true;
    }

    // The title screen, as the test play checks it: not the story, a battle or the arcade.
    private static bool OnTitle()
    {
        try { return GameManager.GameState == GameStates.MainMenu && !ArcadeUtility.IsRunning && !ArcadeSession.Active; }
        catch (Exception ex)
        {
            ReportOnce("checking for the title screen", ex);
            return false;
        }
    }

    /// <summary>The title's Get Custom Battles button.</summary>
    internal static void OpenFromTitle()
    {
        if (!CanOpen(out string why))
        {
            ModLog.Info("Hub: not opened: " + why);
            return;
        }
        Open(null, null);
    }

    /// <summary>
    /// The battle creator's Upload button: the page opens on its Upload tab with that battle, and
    /// <paramref name="closed"/> runs when it closes (the creator shows again).
    /// </summary>
    internal static void OpenForUpload(string battleFolder, Action closed)
    {
        if (!CanOpen(out string why, fromCreator: true))
        {
            ModLog.Info("Hub: not opened: " + why);
            return;
        }
        Open(battleFolder, closed);
    }

    private static void Open(string? uploadFolder, Action? closed)
    {
        try
        {
            var address = HubGame.Address(out string? problem);
            if (address == null)
            {
                ModLog.Error("Hub: can't open: " + problem);
                return;
            }
            ResetState();
            ui = new EditorUi("NocturneButBetter Hub", blockGameClicks: true);
            kit = new EditorPageKit(ui, "Hub");
            pageCts = new CancellationTokenSource();
            paths = HubGame.Paths();
            api = new HubApi(address, ModInfo.Version);
            recycler = HubGame.Recycler();
            defaultName = PlayerPrefs.GetString(AuthorPref, "");
            whenClosed = closed;
            pendingUpload = uploadFolder;
            tab = uploadFolder != null ? Tab.Upload : Tab.Browse;
            BuildPage();
            EditorOverlay.Enter(OverlayOwner);
            Kit.IgnoreKeysNow();
            ShowScreen(Screen.Main);
            StartOpening();
            ModLog.Info("Hub: opened" + (uploadFolder != null ? " from the battle creator to upload " + Path.GetFileName(uploadFolder.TrimEnd('\\', '/')) : "") + ".");
        }
        catch (Exception ex)
        {
            ModLog.Error("Hub: opening the page failed: " + ex);
            Close("it failed to open");
        }
    }

    // Every way out comes here: the keys and buttons, the checks each frame, and errors.
    private static void Close(string reason)
    {
        if (ui == null) return;
        try { pageCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        // Work still going sees the page's cancel and cleans up after itself (a download removes
        // its file, an upload is stopped on the hub). The hub's client goes once that's done.
        var running = jobs.Running;
        jobs.Clear();
        var oldApi = api;
        var oldPaths = paths;
        var oldBuild = built;
        var verified = download?.Verified;
        Task.WhenAll(running).ContinueWith(_ =>
        {
            if (verified != null) HubInstall.Discard(verified);
            if (oldBuild != null && oldPaths != null) HubUploadBuild.Discard(oldBuild, oldPaths);
            oldApi?.Dispose();
        }, TaskScheduler.Default);
        try { kit?.EndTyping(); }
        catch (Exception ex) { ReportOnce("ending the typing", ex); }
        thumbs.Clear();
        ClearFaces();
        ui.Destroy();
        ui = null;
        kit = null;
        api = null;
        // Gives the cursor back; the menus come back once the key that closed the page is let go.
        EditorOverlay.Leave(OverlayOwner);
        var closed = whenClosed;
        ResetState();
        ModLog.Info($"Hub: closed ({reason}).");
        try { closed?.Invoke(); }
        catch (Exception ex) { ModLog.Error("Hub: going back to the battle creator failed: " + ex); }
    }

    private static void ResetState()
    {
        whenClosed = null;
        pendingUpload = null;
        store = null;
        info = null;
        identity = null;
        identityProblem = null;
        link = Link.Opening;
        linkProblem = "";
        zone = Zone.List;
        panelFocus = barFocus = 0;
        working = null;
        workCts = null;
        panelLayout = "";
        forms.Clear();
        panelButtons.Clear();
        barControls.Clear();
        panelActions.Clear();
        ResetBrowse();
        ResetInstalled();
        ResetMine();
        ResetUpload();
    }

    // ---- the page's files, then the hub -------------------------------------------------------

    private sealed class Opened
    {
        internal HubStore Store = null!;
        internal HubIdentity? Identity;
        internal string? IdentityProblem;
        internal HashSet<string> LocalIds = new();
    }

    // The page's own files are read first (a worker: the list may be rebuilt by hashing files).
    private static void StartOpening()
    {
        link = Link.Opening;
        var p = paths!;
        var r = recycler!;
        jobs.Run(Task.Run(() => OpenLocal(p, r)), opened =>
        {
            store = opened.Store;
            identity = opened.Identity;
            identityProblem = opened.IdentityProblem;
            localBattleIds = opened.LocalIds;
            foreach (var note in opened.Store.Notes) ModLog.Info("Hub: " + note + ".");
            if (opened.Store.Notes.Count > 0) Say("The list of what the hub installed was damaged, so it was made again from the Downloaded folders.", 8f);
            if (identityProblem != null) ModLog.Error("Hub: the hub key can't be read: " + identityProblem);
            ReadSettings();
            RefreshInstalled();
            if (!opened.Store.Settings.NoticeSeen) ShowForm(FormKind.Notice);
            else Connect();
        }, ex =>
        {
            ModLog.Error("Hub: reading its files failed: " + ex);
            link = Link.Down;
            linkProblem = "The hub's files on this PC can't be read: " + Unwrap(ex).Message;
        });
    }

    private static Opened OpenLocal(HubPaths p, IHubRecycler r)
    {
        var opened = new Opened { Store = HubStore.Open(p) };
        HubInstall.CleanLeftovers(opened.Store.Reconcile(), opened.Store, r);
        try { opened.Identity = HubIdentity.Load(p.Identity); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { opened.IdentityProblem = ex.Message; }
        opened.LocalIds = opened.Store.LocalBattleIds(BattleFiles.List(p.Battles));
        return opened;
    }

    /// <summary>Asks the hub what it is (/v1/info), then loads what the tabs show.</summary>
    private static void Connect()
    {
        link = Link.Connecting;
        linkProblem = "";
        var a = api!;
        var p = paths!;
        var ct = Ct;
        jobs.Run(Task.Run(async () =>
        {
            var hub = await a.InfoAsync(ct).ConfigureAwait(false);
            // A key change that stopped before its answer was saved is sorted out first.
            HubIdentity? recovered = null;
            try { recovered = await HubIdentity.RecoverAsync(a, p.Identity, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HubException or InvalidDataException or IOException) { }
            return (hub, recovered);
        }, ct), result =>
        {
            info = result.hub;
            if (result.recovered != null && result.recovered.Key != identity?.Key)
            {
                identity = result.recovered;
                identityProblem = null;
                ModLog.Info("Hub: a key change that was cut short was finished.");
            }
            link = info.TooOld(ModInfo.Version) ? Link.TooOld : Link.Online;
            linkProblem = link == Link.TooOld ? HubErrorsText.TooOld : "";
            ModLog.Info($"Hub: connected ({(link == Link.TooOld ? "this mod is too old for it" : "online")}; uploads {(info.UploadsOpen ? "open" : "closed")}).");
            if (info.Message.Length > 0) Say(info.Message, 12f);
            if (!info.Counts && sort == HubSort.Popular) sort = HubSort.Default;
            LookupInstalled();
            if (link == Link.Online) RestartBrowse();
            LoadMine();
            if (pendingUpload != null)
            {
                string folder = pendingUpload;
                pendingUpload = null;
                StartBattleUpload(folder);
            }
        }, LinkFailed);
    }

    private static void LinkFailed(Exception ex)
    {
        link = Link.Down;
        linkProblem = Words(ex);
        ModLog.Info("Hub: the hub can't be used now: " + LogWords(ex));
    }

    /// <summary>
    /// F5 (and Try again): the page's own files when they couldn't be read; the Downloaded folders
    /// (on Installed and Browse, with or without the hub); then the hub, and the tab's list.
    /// </summary>
    private static void Refresh()
    {
        if (store == null)
        {
            if (link == Link.Down) StartOpening();
            return;
        }
        if (link is Link.Opening or Link.Connecting) return;
        // Files deleted in Explorer drop out, and the battles on this PC are read again (YOU HAVE IT).
        if (tab == Tab.Installed) RescanInstalled();
        else if (tab == Tab.Browse) RescanLocal();
        if (link is Link.Down)
        {
            Connect();
            return;
        }
        switch (tab)
        {
            case Tab.Browse:
                if (link != Link.Online) break;
                // A later page that failed is asked for again (the rows so far stay); otherwise the list starts over.
                if (browse != null && browse.Started && browseProblem != null) RetryBrowsePage();
                else RestartBrowse(liveSearch);
                break;
            case Tab.Mine:
                LoadMine(force: true);
                break;
        }
    }

    // ---- per frame ------------------------------------------------------------------------------

    /// <summary>Called every frame by LayoutDriver, after the other pages.</summary>
    internal static void Update()
    {
        if (ui == null) return;
        EditorOverlay.Update();
        try
        {
            if (!ui.IsAlive)
            {
                ModLog.Error("Hub: its screen was destroyed from outside; closing it.");
                Close("its screen was destroyed");
                return;
            }
            if (Time.unscaledTime >= nextGuardCheck)
            {
                nextGuardCheck = Time.unscaledTime + 0.5f;
                // The page belongs to the title screen: it goes if a battle or the story starts under it.
                if (!OnTitle() || TestPlay.Active)
                {
                    Close("the title screen went away");
                    return;
                }
            }
            jobs.Poll(() => IsOpen);
            if (!IsOpen) return;
            var keyboard = InputKeyboard.current;
            if (keyboard == null) return;
            var mouse = InputMouse.current;
            switch (screen)
            {
                case Screen.Main: UpdateMain(keyboard, mouse); break;
                case Screen.Pick: UpdatePick(keyboard); break;
                case Screen.Form: UpdateForm(keyboard, mouse); break;
            }
            if (!IsOpen) return;
            thumbs.Update();
            ReleaseUnwantedFaces();
        }
        catch (Exception ex)
        {
            ModLog.Error("Hub: failed, so the page closes: " + ex);
            if (ui != null) Close("it failed");
        }
    }

    private static void ShowScreen(Screen next)
    {
        screen = next;
        Kit.DelayClicks();
        Ui.ListPanel!.gameObject.SetActive(next == Screen.Pick);
        mainPanel!.gameObject.SetActive(next == Screen.Main);
        formsPanel!.gameObject.SetActive(next == Screen.Form);
        bottomBar!.gameObject.SetActive(next != Screen.Pick);
        topBar!.gameObject.SetActive(next != Screen.Pick);
        Kit.HidePickerFace();
        if (next != Screen.Form) foreach (var f in forms.Values) f.Panel.gameObject.SetActive(false);
        // The list screens show messages in their hint; the status line (drawn over everything) waits.
        if (next == Screen.Pick) Ui.DrawStatus("", 0, 0, 0);
    }

    /// <summary>Back to the tabs.</summary>
    private static void BackToMain()
    {
        Kit.Picking = null;
        ShowScreen(Screen.Main);
    }

    private static void Say(string text, float seconds = 6f) => ui?.Say(text, seconds);

    private static bool PadNames => PadInput.InUse;

    // ---- pickers (the kit's list screens) ---------------------------------------------------------

    private static void ShowPicker(Picker next)
    {
        Kit.ShowPicker(next);
        ShowScreen(Screen.Pick);
    }

    private static void UpdatePick(InputKeyboard keyboard)
    {
        var p = Kit.Picking;
        if (Blocked)
        {
            // Work started from a picker: the list stays, and keys wait.
            if (p != null) Ui.DrawList(p.Plain ? p.Heading : Escape(p.Heading), p.Plain ? working! : Escape(working!), p.Rows, p.Index, plain: p.Plain);
            if (Kit.Live && (Pressed(keyboard, Key.Escape) || PadInput.Pressed(PadButton.East))) StopWork();
            return;
        }
        switch (Kit.UpdatePicker(keyboard))
        {
            case PickerStep.None:
                BackToMain();
                return;
            case PickerStep.Chosen:
                // A choice that doesn't go anywhere else goes back.
                if (IsOpen && screen == Screen.Pick && Kit.Picking == null) BackToMain();
                return;
        }
    }

    // The list screens' button for the mouse: Back. Esc and the pad's B do the same.
    private static void BuildListBack()
    {
        var b = Ui.MakeButton(Ui.ListPanel!, "", () =>
        {
            if (!Kit.Live || !Kit.ClicksLive || Blocked) return;
            if (Kit.Picking is { } p)
            {
                Kit.Picking = null;
                p.Back();
                if (IsOpen && screen == Screen.Pick && Kit.Picking == null) BackToMain();
            }
        });
        b.Text = () => $"Back <size=65%><color=#9D92B4>{(PadNames ? "B" : "Esc")}</color></size>";
        Place(b.Rect, new Vector2(1, 0), new Vector2(-16, 20), new Vector2(170, 52), new Vector2(1, 0));
    }

    // ---- work the page waits for (a modal step: building, sending, deleting) ------------------------

    private static string? working;
    private static CancellationTokenSource? workCts;

    /// <summary>Whether the page waits for work: keys and clicks wait, and Esc stops it when it can be stopped.</summary>
    private static bool Blocked => working != null;

    /// <summary>
    /// Runs <paramref name="task"/> while the status line says <paramref name="what"/>, then
    /// <paramref name="done"/> on the main thread; a failure is said in plain words (or goes to
    /// <paramref name="failed"/>). With <paramref name="cancel"/>, Esc stops it.
    /// </summary>
    private static void Work<T>(string what, Task<T> task, Action<T> done, Action<Exception>? failed = null, CancellationTokenSource? cancel = null)
    {
        working = what;
        workCts = cancel;
        jobs.Run(task, result =>
        {
            working = null;
            workCts = null;
            done(result);
        }, ex =>
        {
            working = null;
            workCts = null;
            if (!IsOpen) return;
            LogFailure(what, ex);
            if (failed != null) failed(ex);
            else Say(Words(ex), 9f);
        });
    }

    private static void StopWork()
    {
        if (workCts == null)
        {
            Say(working + " It can't be stopped now.", 3f);
            return;
        }
        try { workCts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    // ---- words ------------------------------------------------------------------------------------

    /// <summary>What went wrong, in the words the player sees.</summary>
    private static string Words(Exception ex)
    {
        ex = Unwrap(ex);
        return ex switch
        {
            HubException hub => hub.Message,
            OperationCanceledException => "Stopped.",
            InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException => "That didn't work: " + ex.Message,
            _ => "Something went wrong (see the log).",
        };
    }

    // For the log: never a key, a header or a body; the hub's code and status, or the error. A
    // connection that failed also names its first cause (a certificate, a proxy, a name that
    // doesn't resolve, a refused connection), whose words hold only the host, the port and the reason.
    private static string LogWords(Exception ex)
    {
        ex = Unwrap(ex);
        if (ex is HubException hub)
            return $"{hub.Code}{(hub.Status > 0 ? $" ({hub.Status})" : "")}" + (hub.Problems.Count > 0 ? ": " + string.Join("; ", hub.Problems) : "")
                + (HubErrors.Cause(hub) is { } cause ? $" ({cause})" : "");
        return ex is OperationCanceledException ? "stopped" : ex.GetType().Name + ": " + ex.Message;
    }

    private static void LogFailure(string what, Exception ex)
    {
        var reason = Unwrap(ex);
        bool expected = reason is HubException or OperationCanceledException or InvalidDataException or IOException or UnauthorizedAccessException;
        if (expected) ModLog.Info($"Hub: {what.TrimEnd('.')} didn't work: {LogWords(reason)}");
        else ModLog.Error($"Hub: {what.TrimEnd('.')} failed: {reason}");
    }

    private static readonly HashSet<string> Reported = new();

    private static void ReportOnce(string what, Exception ex)
    {
        if (Reported.Add(what)) ModLog.Error($"Hub: {what} failed: {ex}");
    }

    internal static string Size(long bytes) =>
        bytes < 1024 * 1024
            ? Math.Max(1, (long)Math.Ceiling(bytes / 1024.0)).ToString(CultureInfo.InvariantCulture) + " KB"
            : (bytes / (1024.0 * 1024.0)).ToString(bytes < 10L * 1024 * 1024 ? "0.#" : "0", CultureInfo.InvariantCulture) + " MB";

    internal static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    internal static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    internal static string Date(long unixSeconds) =>
        unixSeconds <= 0 ? "" : DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(unixSeconds, 0, 253402300799)).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 360000));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    // ---- for the QA drivers: reads only ------------------------------------------------------------

    /// <summary>The screen and tab, like "main browse" or "pick", or "closed".</summary>
    internal static string QaScreen => !IsOpen ? "closed" : screen == Screen.Main ? "main " + tab.ToString().ToLowerInvariant()
        : screen == Screen.Form ? "form " + formKind.ToString().ToLowerInvariant() : "pick";

    /// <summary>How the hub answers now: opening, connecting, online, down or tooold.</summary>
    internal static string QaLink => link.ToString().ToLowerInvariant();

    /// <summary>The tab's rows as they're listed: title | tag.</summary>
    internal static List<string> QaRows
    {
        get
        {
            var rows = new List<string>();
            if (!IsOpen) return rows;
            for (int i = 0; i < ListCount(); i++)
            {
                var row = RowAt(i);
                rows.Add(row.Title + " | " + BattleNotice.StripTags(row.Tag));
            }
            return rows;
        }
    }
}

/// <summary>Words the page uses in more than one place.</summary>
internal static class HubErrorsText
{
    internal const string TooOld = "This version of the mod is too old for the hub. Update the mod.";
}

/// <summary>
/// The hub page's work on worker threads: each task's result is handed to the page on the main
/// thread (<see cref="Poll"/>, every frame), the same way the page kit's job runner does, but many
/// at a time, so a list can load while a download runs, and none of them holds the page's keys.
/// A failure goes to its own handler with the reason unwrapped.
/// </summary>
internal sealed class HubJobs
{
    private readonly List<(Task Task, Action<Task> Finish)> jobs = new();

    internal void Run<T>(Task<T> task, Action<T> done, Action<Exception> failed) =>
        jobs.Add((task, finished =>
        {
            T result;
            try { result = ((Task<T>)finished).Result; }
            catch (Exception ex)
            {
                failed(EditorPageKit.Unwrap(ex));
                return;
            }
            try { done(result); }
            catch (Exception ex) when (ex is HubException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
                                        or OperationCanceledException or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
            {
                failed(ex);
            }
        }));

    /// <summary>The tasks still going (the page waits for them before letting go of the hub's client).</summary>
    internal Task[] Running => jobs.Where(j => !j.Task.IsCompleted).Select(j => j.Task).ToArray();

    /// <summary>Runs what comes after each finished task; stops when <paramref name="open"/> says the page closed.</summary>
    internal void Poll(Func<bool> open)
    {
        for (int i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            if (!job.Task.IsCompleted) continue;
            jobs.RemoveAt(i);
            i--;
            job.Finish(job.Task);
            if (!open()) return;
        }
    }

    /// <summary>Drops what was waiting (the page closed): the tasks still finish, their results are ignored.</summary>
    internal void Clear() => jobs.Clear();
}
