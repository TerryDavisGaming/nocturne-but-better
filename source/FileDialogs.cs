using System.Runtime.InteropServices;

namespace NocturneFlatScroll;

/// <summary>
/// The standard Windows open and save dialogs. Each runs on its own thread (the dialogs need a
/// single-threaded apartment), so the game keeps drawing while one is open; the caller polls
/// the returned task.
/// </summary>
internal static class FileDialogs
{
    private const int MaxPath = 1024;
    private const int OfnOverwritePrompt = 0x2, OfnNoChangeDir = 0x8, OfnPathMustExist = 0x800,
                      OfnFileMustExist = 0x1000, OfnExplorer = 0x80000;

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

    /// <param name="filter">Pairs of "label", "pattern;pattern".</param>
    internal static Task<string?> Open(string title, string initialDir, params string[] filter) =>
        Run(false, title, initialDir, "", "", filter);

    internal static Task<string?> Save(string title, string initialDir, string fileName, string extension, params string[] filter) =>
        Run(true, title, initialDir, fileName, extension, filter);

    private static Task<string?> Run(bool save, string title, string initialDir, string fileName, string extension, string[] filter)
    {
        // The game's window, found on the main thread, owns the dialog so it opens in front.
        IntPtr owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        var result = new TaskCompletionSource<string?>();
        var thread = new Thread(() =>
        {
            IntPtr buffer = Marshal.AllocHGlobal(MaxPath * 2);
            try
            {
                var initial = new char[MaxPath];
                fileName.CopyTo(0, initial, 0, Math.Min(fileName.Length, MaxPath - 1));
                Marshal.Copy(initial, 0, buffer, MaxPath);
                var ofn = new OpenFileName
                {
                    lStructSize = Marshal.SizeOf<OpenFileName>(),
                    hwndOwner = owner,
                    lpstrFilter = string.Join("\0", filter) + "\0\0",
                    nFilterIndex = 1,
                    lpstrFile = buffer,
                    nMaxFile = MaxPath,
                    lpstrInitialDir = initialDir,
                    lpstrTitle = title,
                    lpstrDefExt = extension.TrimStart('.'),
                    Flags = OfnExplorer | OfnNoChangeDir | OfnPathMustExist |
                            (save ? OfnOverwritePrompt : OfnFileMustExist)
                };
                bool ok = save ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
                // Cancel returns false with no error code; anything else is a real failure.
                uint error = ok ? 0 : CommDlgExtendedError();
                if (error != 0) result.SetException(new InvalidOperationException($"the file dialog failed (error 0x{error:X})"));
                else result.SetResult(ok ? Marshal.PtrToStringUni(buffer) : null);
            }
            catch (Exception ex) { result.SetException(ex); }
            finally { Marshal.FreeHGlobal(buffer); }
        }) { IsBackground = true, Name = "NocturneButBetter file dialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }
}
