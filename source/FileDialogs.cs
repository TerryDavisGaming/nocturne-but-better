using System.Runtime.InteropServices;
using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// The standard Windows open and save dialogs, a multi-file open, and a folder picker. Each runs
/// on its own thread (the dialogs need a single-threaded apartment), so the game keeps drawing
/// while one is open; the caller polls the returned task. Dialogs opened for a
/// <see cref="Purpose"/> start in the folder last used for that purpose.
/// </summary>
internal static class FileDialogs
{
    private const int MaxPath = 1024;
    // Room for a long multi-selection: "folder\0name\0name\0...\0\0".
    private const int MaxMultiPath = 65536;
    private const int OfnOverwritePrompt = 0x2, OfnNoChangeDir = 0x8, OfnAllowMultiSelect = 0x200, OfnPathMustExist = 0x800,
                      OfnFileMustExist = 0x1000, OfnExplorer = 0x80000;
    private const uint FnErrBufferTooSmall = 0x3003;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetSaveFileNameW(ref OpenFileName ofn);

    [DllImport("comdlg32.dll")]
    private static extern uint CommDlgExtendedError();

    // ---- filter presets -----------------------------------------------------------------------

    /// <summary>Filter presets for the dialogs' file type list: pairs of "label", "pattern;pattern".</summary>
    internal static class Filters
    {
        internal static string[] Images => new[] { "Images (*.png, *.jpg, *.gif, *.webp, *.bmp)", "*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp" };
        internal static string[] Videos => new[] { "Videos (*.mp4, *.webm, *.m4v, *.mov)", "*.mp4;*.webm;*.m4v;*.mov" };
        internal static string[] Songs => new[] { "Music (*.ogg, *.mp3, *.wav, *.flac, *.m4a, *.wma)", "*.ogg;*.mp3;*.wav;*.flac;*.m4a;*.wma" };
        internal static string[] StepMania => new[] { "StepMania charts (*.sm, *.ssc)", "*.sm;*.ssc" };
        internal static string[] ChartPacks => new[] { "Nocturne But Better chart packs (*.nbbchart)", "*.nbbchart" };
        internal static string[] BattlePacks => new[] { "Nocturne But Better battles (*.nbbbattle)", "*.nbbbattle" };
        internal static string[] AllFiles => new[] { "All files", "*.*" };

        /// <summary>Several presets in one list, in order; the first is selected when the dialog opens.</summary>
        internal static string[] Join(params string[][] filters) => filters.SelectMany(f => f).ToArray();
    }

    // ---- purposes: what a dialog is for, and the folder it starts in ---------------------------

    /// <summary>
    /// What a dialog is for: its file types, and the folder it starts in. The folder last chosen
    /// for a purpose is remembered (in the player prefs, per PC); until then it starts in
    /// <see cref="DefaultFolder"/>.
    /// </summary>
    internal sealed class Purpose
    {
        internal static readonly Purpose Images = new("images", PicturesFolder, Filters.Join(Filters.Images, Filters.AllFiles));
        internal static readonly Purpose Videos = new("videos", VideosFolder, Filters.Join(Filters.Videos, Filters.AllFiles));
        internal static readonly Purpose Songs = new("songs", MusicFolder, Filters.Join(Filters.Songs, Filters.AllFiles));
        internal static readonly Purpose StepMania = new("stepmania", DownloadsFolder, Filters.Join(Filters.StepMania, Filters.AllFiles));
        internal static readonly Purpose ChartPacks = new("chartpacks", DownloadsFolder, Filters.Join(Filters.ChartPacks, Filters.AllFiles));
        internal static readonly Purpose BattlePacks = new("battlepacks", DownloadsFolder, Filters.Join(Filters.BattlePacks, Filters.AllFiles));
        /// <summary>For picking a whole song folder, like a StepMania song.</summary>
        internal static readonly Purpose SongFolders = new("songfolders", DownloadsFolder, Array.Empty<string>());

        internal Purpose(string key, Func<string> defaultFolder, string[] filter)
        {
            Key = key;
            DefaultFolder = defaultFolder;
            Filter = filter;
        }

        /// <summary>Names the remembered folder; letters, digits and dots.</summary>
        internal string Key { get; }
        internal Func<string> DefaultFolder { get; }
        internal string[] Filter { get; }

        private static string PicturesFolder() => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        private static string VideosFolder() => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        private static string MusicFolder() => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        private static string DownloadsFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    private const string FolderPrefPrefix = "NocturneFlatScroll.DialogFolder.";
    private const string FolderPrefSuffix = ".v1";
    private static readonly object folderLock = new();
    private static readonly Dictionary<string, string> folders = new();
    private static readonly HashSet<string> unsavedFolders = new();

    /// <summary>The remembered folder for a purpose, or "" (main thread: reads the player prefs once).</summary>
    private static string RememberedFolder(Purpose purpose)
    {
        lock (folderLock)
            if (folders.TryGetValue(purpose.Key, out var known)) return known;
        string saved = "";
        try { saved = PlayerPrefs.GetString(FolderPrefPrefix + purpose.Key + FolderPrefSuffix, ""); }
        catch (Exception ex) { ModLog.Error("Reading the last folder for a file dialog failed: " + ex.Message); }
        lock (folderLock)
        {
            // A dialog that finished meanwhile wins.
            if (!folders.TryGetValue(purpose.Key, out var known)) folders[purpose.Key] = known = saved;
            return known;
        }
    }

    /// <summary>Remembers the folder a dialog ended in (any thread); saved by <see cref="Update"/>.</summary>
    private static void Remember(Purpose purpose, string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        lock (folderLock)
        {
            folders[purpose.Key] = folder!;
            unsavedFolders.Add(purpose.Key);
        }
    }

    /// <summary>Called every frame on the main thread: saves newly remembered folders.</summary>
    internal static void Update()
    {
        List<(string Key, string Folder)>? save = null;
        lock (folderLock)
        {
            if (unsavedFolders.Count == 0) return;
            save = unsavedFolders.Select(k => (k, folders[k])).ToList();
            unsavedFolders.Clear();
        }
        try
        {
            foreach (var (key, folder) in save) PlayerPrefs.SetString(FolderPrefPrefix + key + FolderPrefSuffix, folder);
            PlayerPrefs.Save();
        }
        catch (Exception ex) { ModLog.Error("Saving the last folder for a file dialog failed: " + ex.Message); }
    }

    /// <summary>The first of the folders that exists, checked on the dialog's thread (a network folder can be slow).</summary>
    private static string? FirstExisting(params string?[] candidates)
    {
        foreach (var folder in candidates)
        {
            try { if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) return folder; }
            catch { }
        }
        return null;
    }

    // ---- the dialogs ------------------------------------------------------------------------

    /// <param name="filter">Pairs of "label", "pattern;pattern".</param>
    internal static Task<string?> Open(string title, string initialDir, params string[] filter) =>
        Run(false, title, initialDir, "", "", filter);

    internal static Task<string?> Save(string title, string initialDir, string fileName, string extension, params string[] filter) =>
        Run(true, title, initialDir, fileName, extension, filter);

    /// <summary>Picks one file for <paramref name="purpose"/>, starting in its last folder.</summary>
    internal static Task<string?> Open(Purpose purpose, string title)
    {
        string remembered = RememberedFolder(purpose), fallback = SafeDefault(purpose);
        return OnDialogThread(() =>
        {
            string? path = Show(false, false, title, FirstExisting(remembered, fallback), "", "", purpose.Filter).FirstOrDefault();
            if (path != null) Remember(purpose, Path.GetDirectoryName(path));
            return path;
        });
    }

    /// <summary>Asks where to save a file for <paramref name="purpose"/>, starting in its last folder.</summary>
    internal static Task<string?> Save(Purpose purpose, string title, string fileName, string extension)
    {
        string remembered = RememberedFolder(purpose), fallback = SafeDefault(purpose);
        return OnDialogThread(() =>
        {
            string? path = Show(true, false, title, FirstExisting(remembered, fallback), fileName, extension, purpose.Filter).FirstOrDefault();
            if (path != null) Remember(purpose, Path.GetDirectoryName(path));
            return path;
        });
    }

    /// <summary>Picks one or more files; null when cancelled.</summary>
    /// <param name="filter">Pairs of "label", "pattern;pattern".</param>
    internal static Task<string[]?> OpenMany(string title, string initialDir, params string[] filter) =>
        OnDialogThread(() =>
        {
            var paths = Show(false, true, title, initialDir, "", "", filter);
            return paths.Length > 0 ? paths : null;
        });

    /// <summary>Picks one or more files for <paramref name="purpose"/>, starting in its last folder; null when cancelled.</summary>
    internal static Task<string[]?> OpenMany(Purpose purpose, string title)
    {
        string remembered = RememberedFolder(purpose), fallback = SafeDefault(purpose);
        return OnDialogThread(() =>
        {
            var paths = Show(false, true, title, FirstExisting(remembered, fallback), "", "", purpose.Filter);
            if (paths.Length == 0) return null;
            Remember(purpose, Path.GetDirectoryName(paths[0]));
            return paths;
        });
    }

    /// <summary>Picks a folder; null when cancelled.</summary>
    internal static Task<string?> PickFolder(string title, string initialDir) =>
        OnDialogThread(() => ShowFolderPicker(title, initialDir, null));

    /// <summary>
    /// Picks a folder for <paramref name="purpose"/>. It starts in the folder that held the last
    /// one picked, so picking several songs from one place is quick. Null when cancelled.
    /// </summary>
    internal static Task<string?> PickFolder(Purpose purpose, string title)
    {
        string remembered = RememberedFolder(purpose), fallback = SafeDefault(purpose);
        return OnDialogThread(() =>
        {
            string? folder = ShowFolderPicker(title, FirstExisting(remembered, fallback), ClientGuid(purpose));
            if (folder != null) Remember(purpose, Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? folder);
            return folder;
        });
    }

    private static string SafeDefault(Purpose purpose)
    {
        try { return purpose.DefaultFolder() ?? ""; }
        catch { return ""; }
    }

    private static Task<string?> Run(bool save, string title, string initialDir, string fileName, string extension, string[] filter) =>
        OnDialogThread(() => Show(save, false, title, initialDir, fileName, extension, filter).FirstOrDefault());

    /// <summary>
    /// Runs <paramref name="dialog"/> on a new single-threaded apartment thread. The game's window,
    /// found here on the main thread, owns the dialog so it opens in front.
    /// </summary>
    private static Task<T> OnDialogThread<T>(Func<T> dialog)
    {
        IntPtr owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        var result = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            ownerWindow = owner;
            try { result.SetResult(dialog()); }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true, Name = "NocturneButBetter file dialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    // The window that owns the dialog on this dialog thread.
    [ThreadStatic] private static IntPtr ownerWindow;

    /// <summary>
    /// Shows the open or save dialog on this thread. Returns the chosen paths: none when
    /// cancelled, one for a single pick, and every picked file for a multi-selection.
    /// </summary>
    private static string[] Show(bool save, bool many, string title, string? initialDir, string fileName, string extension, string[] filter)
    {
        int size = many ? MaxMultiPath : MaxPath;
        IntPtr buffer = Marshal.AllocHGlobal(size * 2);
        try
        {
            var initial = new char[size];
            fileName.CopyTo(0, initial, 0, Math.Min(fileName.Length, MaxPath - 1));
            Marshal.Copy(initial, 0, buffer, size);
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = ownerWindow,
                lpstrFilter = string.Join("\0", filter) + "\0\0",
                nFilterIndex = 1,
                lpstrFile = buffer,
                nMaxFile = size,
                lpstrInitialDir = initialDir,
                lpstrTitle = title,
                lpstrDefExt = extension.TrimStart('.'),
                Flags = OfnExplorer | OfnNoChangeDir | OfnPathMustExist |
                        (save ? OfnOverwritePrompt : OfnFileMustExist) |
                        (many ? OfnAllowMultiSelect : 0)
            };
            bool ok = save ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
            // Cancel returns false with no error code; anything else is a real failure.
            uint error = ok ? 0 : CommDlgExtendedError();
            if (error == FnErrBufferTooSmall) throw new InvalidOperationException("too many files were picked at once");
            if (error != 0) throw new InvalidOperationException($"the file dialog failed (error 0x{error:X})");
            if (!ok) return Array.Empty<string>();
            if (!many) return new[] { Marshal.PtrToStringUni(buffer) ?? "" };
            var chars = new char[size];
            Marshal.Copy(buffer, chars, 0, size);
            return ParseMultiSelection(chars);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// Reads a multi-selection buffer: "folder\0name\0name\0\0" for several files, or just
    /// "path\0\0" when one file was picked.
    /// </summary>
    internal static string[] ParseMultiSelection(char[] buffer)
    {
        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0') continue;
            if (i == start) break;   // the empty string that ends the list
            parts.Add(new string(buffer, start, i - start));
            start = i + 1;
        }
        if (parts.Count <= 1) return parts.ToArray();
        string folder = parts[0];
        return parts.Skip(1).Select(name => Path.Combine(folder, name)).ToArray();
    }

    // ---- the folder picker: the Common Item Dialog (IFileOpenDialog) --------------------------
    //
    // Called through its vtable, like the Media Foundation code: no COM interface declarations.
    // Slots: IUnknown 0-2, IModalWindow.Show 3, IFileDialog 4-26 (SetOptions 9, GetOptions 10,
    // SetFolder 12, SetTitle 17, GetResult 20, SetClientGuid 24), IShellItem.GetDisplayName 5.

    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
    private static readonly Guid IidFileOpenDialog = new("d57c7288-d4ad-4768-be02-9d969532d960");
    private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private const uint FosNoChangeDir = 0x8, FosPickFolders = 0x20, FosForceFileSystem = 0x40, FosPathMustExist = 0x800;
    private const uint SigdnFileSysPath = 0x80058000;
    private const int ErrorCancelled = unchecked((int)0x800704C7);
    private const uint ClsctxInprocServer = 1, CoinitApartmentThreaded = 2, CoinitDisableOle1Dde = 4;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IntPtr item);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ShowFn(IntPtr self, IntPtr owner);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetOptionsFn(IntPtr self, uint options);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOptionsFn(IntPtr self, out uint options);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetItemFn(IntPtr self, IntPtr item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate int SetTextFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetItemFn(IntPtr self, out IntPtr item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGuidFn(IntPtr self, ref Guid guid);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDisplayNameFn(IntPtr self, uint sigdn, out IntPtr name);

    private static T Slot<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    private static void Release(IntPtr instance)
    {
        if (instance != IntPtr.Zero) Slot<ReleaseFn>(instance, 2)(instance);
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"the folder picker failed {what} (0x{hr:X8})");
    }

    /// <summary>A stable id per purpose, so Windows keeps each purpose's dialog settings apart.</summary>
    internal static Guid ClientGuid(Purpose purpose)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return new Guid(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("NocturneButBetter.FileDialog." + purpose.Key)));
    }

    /// <summary>Shows the folder picker on this (STA) thread; null when cancelled.</summary>
    private static string? ShowFolderPicker(string title, string? initialDir, Guid? clientGuid)
    {
        // The thread is already a single-threaded apartment; this just balances the count.
        int init = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded | CoinitDisableOle1Dde);
        IntPtr dialog = IntPtr.Zero, item = IntPtr.Zero;
        try
        {
            dialog = CreateFolderPicker(title, initialDir, clientGuid);
            int shown = Slot<ShowFn>(dialog, 3)(dialog, ownerWindow);
            if (shown == ErrorCancelled) return null;
            Check(shown, "while open");
            Check(Slot<GetItemFn>(dialog, 20)(dialog, out item), "reading the choice");
            Check(Slot<GetDisplayNameFn>(item, 5)(item, SigdnFileSysPath, out IntPtr name), "reading the folder's path");
            try { return Marshal.PtrToStringUni(name); }
            finally { Marshal.FreeCoTaskMem(name); }
        }
        finally
        {
            Release(item);
            Release(dialog);
            if (init >= 0) CoUninitialize();
        }
    }

    /// <summary>
    /// Makes the folder picker and sets it up, without showing it; the caller releases it. Needs a
    /// COM apartment on this thread.
    /// </summary>
    internal static IntPtr CreateFolderPicker(string title, string? initialDir, Guid? clientGuid)
    {
        IntPtr dialog = IntPtr.Zero, folder = IntPtr.Zero;
        try
        {
            var clsid = ClsidFileOpenDialog;
            var iid = IidFileOpenDialog;
            Check(CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out dialog), "to open");
            Check(Slot<GetOptionsFn>(dialog, 10)(dialog, out uint options), "reading its options");
            Check(Slot<SetOptionsFn>(dialog, 9)(dialog, options | FosPickFolders | FosForceFileSystem | FosNoChangeDir | FosPathMustExist), "setting its options");
            Check(Slot<SetTextFn>(dialog, 17)(dialog, title), "setting its title");
            if (clientGuid is { } guid)
            {
                var id = guid;
                Check(Slot<SetGuidFn>(dialog, 24)(dialog, ref id), "setting its id");
            }
            if (!string.IsNullOrEmpty(initialDir))
            {
                // A folder that can't be opened just leaves the dialog where Windows puts it. The
                // dialog keeps its own reference to the folder.
                var shellItem = IidShellItem;
                if (SHCreateItemFromParsingName(initialDir!, IntPtr.Zero, ref shellItem, out folder) >= 0)
                    Slot<SetItemFn>(dialog, 12)(dialog, folder);
            }
            var made = dialog;
            dialog = IntPtr.Zero;
            return made;
        }
        finally
        {
            Release(folder);
            Release(dialog);
        }
    }
}
