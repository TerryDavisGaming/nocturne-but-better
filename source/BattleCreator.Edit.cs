using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;
using static NocturneFlatScroll.EditorInput;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;

namespace NocturneFlatScroll;

// Editing a battle: its text fields, the song, card, enemy, gear and level pickers, and the file
// actions (new, import, save, export, delete). Changes live in the BattleDraft until Save.
internal static partial class BattleCreator
{
    // The chart editor's remembered name for charts, used as a new battle's charter.
    private const string AuthorPref = "NocturneFlatScroll.EditorAuthor.v1";

    private static BattleDraft? draft;
    private static BattleFiles.ChartSummary? charts;
    private static BattleFiles.BattleEntry? summary;
    // Files copied in or replaced while editing (paths inside the battle). After a save or when
    // leaving, the ones the saved battle.json doesn't use go to the Recycle Bin.
    private static readonly List<string> touched = new();
    private static Task<(List<string> Moved, List<string> Kept)>? cleanup;

    // ---- opening and leaving a battle -------------------------------------------------------------

    private static void OpenBattle(string folder)
    {
        BattleDraft loaded;
        try { loaded = BattleDraft.Load(folder); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Say("It can't be opened: " + ex.Message, 6f);
            return;
        }
        draft = loaded;
        touched.Clear();
        RefreshBattleInfo();
        LoadCardPreview();
        StartAudioLoad();
        SetPage(Page.Info);
        ShowScreen(Screen.Edit);
        if (loaded.Problems.Count > 0) Say(loaded.Problems[0], 6f);
        ModLog.Info($"Battle creator: editing {folder}.");
    }

    /// <summary>Back to the list: asks first when something isn't saved.</summary>
    private static void RequestBack()
    {
        if (!FinishTyping()) return;
        if (draft == null) { ShowScreen(Screen.List); return; }
        if (!draft.Dirty) { CloseBattle(); return; }
        var d = draft;
        ShowPicker(new Picker
        {
            Heading = $"\"{d.Title}\" has unsaved changes",
            Rows = { "Save", "Don't save", "Cancel" },
            Hint = i => i switch
            {
                0 => "Saves the changes, then goes back to the list.",
                1 => "Goes back to the list without the changes. Songs and images added since the last save go to the Recycle Bin.",
                _ => "Keeps editing.",
            },
            Choose = i =>
            {
                if (i == 0) { if (Save()) CloseBattle(); else BackFromPicker(); }
                else if (i == 1)
                {
                    ModLog.Info($"Battle creator: left {d.Folder} without saving.");
                    CloseBattle();
                }
                else BackFromPicker();
            },
            Back = BackFromPicker,
        });
    }

    private static void CloseBattle()
    {
        string? folder = draft?.Folder;
        StopPreview();
        ClearCardPreview();
        EndTyping();
        CleanUnused();
        draft = null;
        song = null;
        audioLoad = null;
        charts = null;
        summary = null;
        Rescan();
        if (folder != null) SelectInList(folder);
        ShowScreen(Screen.List);
    }

    /// <summary>Reads the chart and the loader's view of the battle again, for the pages.</summary>
    private static void RefreshBattleInfo()
    {
        if (draft == null) return;
        try
        {
            charts = BattleFiles.SummarizeChart(draft.Folder, draft.ChartPath, draft.Lanes);
            summary = BattleFiles.Read(draft.Folder);
        }
        catch (Exception ex)
        {
            ModLog.Error("Battle creator: reading the battle's chart failed: " + ex);
        }
    }

    // ---- the frame --------------------------------------------------------------------------------

    private static void UpdateEdit(InputKeyboard keyboard, InputMouse? mouse)
    {
        if (draft == null) { ShowScreen(Screen.List); return; }
        FinishAudioLoad();
        // Clicks wait a moment after the screen changed (see ClickDelay).
        var clicks = ClicksLive ? mouse : null;
        // A click anywhere finishes the field being typed in (clicking it again starts it again).
        // A value the field refuses stays open with the reason showing, and the click does nothing else.
        if (typing != null && clicks != null && clicks.leftButton.wasPressedThisFrame && !Busy && !CommitTyping()) clicks = null;
        Ui.UpdateButtons(clicks);
        if (!IsOpen || screen != Screen.Edit || draft == null) return;
        if (Live && !Busy)
        {
            if (typing != null) UpdateTyping(keyboard);
            else if (!HandleEditKeys(keyboard)) return;
        }
        DrawEdit();
    }

    /// <returns>False when the edit screen closed.</returns>
    private static bool HandleEditKeys(InputKeyboard k)
    {
        if (Pressed(k, Key.Escape))
        {
            RequestBack();
            return IsOpen && screen == Screen.Edit;
        }
        if (Ctrl(k) && Pressed(k, Key.S)) { Save(); return true; }
        if (Pressed(k, Key.Tab))
        {
            int i = Array.FindIndex(Pages, x => x.Page == page);
            SetPage(Pages[(i + (Shift(k) ? Pages.Length - 1 : 1)) % Pages.Length].Page);
            return true;
        }
        var shown = VisibleControls();
        if (Pressed(k, Key.DownArrow)) focus = shown.Count == 0 ? -1 : Math.Min(shown.Count - 1, focus + 1);
        if (Pressed(k, Key.UpArrow)) focus = shown.Count == 0 ? -1 : Math.Max(0, focus - 1);
        if ((Pressed(k, Key.Enter) || Pressed(k, Key.NumpadEnter)) && focus >= 0 && focus < shown.Count) shown[focus].Activate();
        return IsOpen && screen == Screen.Edit;
    }

    // ---- text fields ------------------------------------------------------------------------------

    /// <summary>A value typed on a page: how to show it, and how to keep what was typed (Set throws InvalidDataException to refuse it).</summary>
    private sealed class TextField
    {
        internal string Label = "";
        internal Func<string> Get = () => "";
        internal Action<string> Set = _ => { };
        /// <summary>What an empty value shows, like the enemy's own stat.</summary>
        internal Func<string>? Empty;
        internal int Max = 80;
        internal bool MultiLine;
        internal string Hint = "Type, then Enter. Esc cancels.";
        /// <summary>A part of the enemy, which can't change while the enemy's file can't be read.</summary>
        internal bool Enemy;
        /// <summary>More for the typing hint, worked out from what is typed (like how many lines it takes).</summary>
        internal Func<string, string>? Measure;
    }

    private static TextField? typing;
    private static string typed = "";

    private static void StartTyping(TextField field)
    {
        if (draft == null) return;
        if (!FinishTyping()) return;
        if (field.Enemy && !EnemyEditable()) return;
        typing = field;
        typed = field.Get();
        BeginText();
        SayTypingHint();
    }

    // The hint while typing; the long texts also show how much of them is used.
    private static void SayTypingHint()
    {
        var field = typing;
        if (field == null) return;
        string hint = field.MultiLine ? "Type, then Enter. Shift+Enter starts a new line. Esc cancels." : field.Hint;
        if (field.Max >= 100) hint += $"   {typed.Length} / {field.Max}";
        if (field.Measure != null) hint += field.Measure(typed);
        Say(hint, 3600f);
    }

    private static void UpdateTyping(InputKeyboard k)
    {
        var field = typing!;
        if (Pressed(k, Key.Escape)) { EndTyping(); return; }
        if (Ctrl(k) && Pressed(k, Key.S)) { Save(); return; }
        bool enter = Pressed(k, Key.Enter) || Pressed(k, Key.NumpadEnter);
        if (enter && field.MultiLine && Shift(k))
        {
            if (typed.Length < field.Max)
            {
                typed += "\n";
                SayTypingHint();
            }
            return;
        }
        if (TypeText(k, ref typed, field.Max)) SayTypingHint();
        if (enter) CommitTyping();
    }

    /// <summary>
    /// Keeps what was typed. False when the field refuses it (like "abc" for HP): the field stays
    /// open with the reason showing, and nothing is saved until it's fixed or Esc cancels it.
    /// </summary>
    private static bool CommitTyping()
    {
        var field = typing;
        if (field == null) return true;
        try { field.Set(typed); }
        catch (InvalidDataException ex)
        {
            Say(ex.Message + "  Fix it, or Esc keeps the old value.", 3600f);
            return false;
        }
        EndTyping();
        return true;
    }

    /// <summary>Keeps what is being typed, if anything; false when the field refused it (see <see cref="CommitTyping"/>).</summary>
    private static bool FinishTyping() => typing == null || CommitTyping();

    /// <summary>Stops typing and drops what was typed.</summary>
    private static void EndTyping()
    {
        if (typing == null) return;
        typing = null;
        EndText();
        Say("", 0f);
    }

    // The enemy can't change while its own file can't be read (see BattleDraft.EnemyLocked).
    private static bool EnemyEditable()
    {
        if (draft == null) return false;
        if (draft.EnemyLocked == null) return true;
        Say(draft.EnemyLocked, 8f);
        return false;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A typed number, or null when nothing was typed. A comma works as the decimal point too.</summary>
    private static double? ParseNumber(string text, double min, double max, string what)
    {
        text = text.Trim().Replace(',', '.');
        if (text.Length == 0) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidDataException($"{what} has to be a number, like {Num(Math.Max(min, 1))}.");
        if (value < min || value > max) throw new InvalidDataException($"{what} has to be between {Num(min)} and {Num(max)}.");
        return value;
    }

    private static readonly TextField TitleField = new()
    {
        Label = "Title",
        Get = () => draft?.Title ?? "",
        Set = text =>
        {
            if (BattleDraft.CleanLine(text).Length == 0) throw new InvalidDataException("A battle needs a title.");
            draft!.Title = text;
        },
    };

    private static readonly TextField ArtistField = new()
    {
        Label = "Artist",
        Get = () => draft?.Artist ?? "",
        Set = text => draft!.Artist = text,
        Empty = () => "(who made the song)",
    };

    private static readonly TextField AuthorField = new()
    {
        Label = "Charter",
        Get = () => draft?.Author ?? "",
        Set = text =>
        {
            draft!.Author = text;
            // Remembered like the chart editor's author, for the next new battle.
            if (draft.Author.Length > 0)
            {
                PlayerPrefs.SetString(AuthorPref, draft.Author);
                PlayerPrefs.Save();
            }
        },
        Empty = () => "(who made the charts)",
    };

    private static readonly TextField LoreField = new()
    {
        Label = "Lore",
        Max = 600,
        MultiLine = true,
        Get = () => draft?.Lore ?? "",
        Set = text => draft!.Lore = text,
        Empty = () => "(none: the arcade says who made the song and the charts)",
    };

    private static readonly TextField PreviewField = new()
    {
        Label = "Preview start",
        Max = 10,
        Get = () => draft == null ? "" : Num(draft.PreviewStart),
        Set = text => draft!.PreviewStart = ParseNumber(text, 0, 3600, "The preview start") ?? 0,
        Empty = () => "0",
        Hint = "Type the time in seconds, like 42.5, then Enter. Esc cancels.",
    };

    // The battle shows the name only as the title of its own info boxes that have none, so the
    // field sits with them on the Enemy page and shows only while the battle has its own boxes.
    private static readonly TextField EnemyNameField = new()
    {
        Label = "Enemy name",
        Max = 60,
        Enemy = true,
        Get = () => draft?.EnemyName ?? "",
        Set = text => draft!.EnemyName = text,
        Empty = () => "(none)",
        Hint = "Type, then Enter. It is the title of a box that has no title of its own. Esc cancels.",
    };

    // The loader's limits for each stat.
    private static readonly (string Key, string Label, double Min, double Max)[] Stats =
    {
        (BattleDraft.Hp, "HP", 1, 100000),
        (BattleDraft.Damage, "Damage", 0, 1000),
        (BattleDraft.AttackWindupTime, "Attack windup (s)", 0.05, 60),
        (BattleDraft.EnergyChargeOnMiss, "Energy per miss", 0, 1000),
        (BattleDraft.PassiveEnergyCharge, "Passive energy", 0, 10000),
    };

    private static readonly TextField[] StatFields = Stats.Select(s => new TextField
    {
        Label = s.Label,
        Max = 12,
        Enemy = true,
        Get = () => draft?.Stat(s.Key) is double v ? Num(v) : "",
        Set = text => draft!.SetStat(s.Key, ParseNumber(text, s.Min, s.Max, s.Label)),
        Empty = () => EnemyChoices.Find(draft?.Placeholder) is { } c ? $"{c.Name}'s own: {Num(c.OwnStat(s.Key))}" : "the enemy's own",
        Hint = "Type a number, then Enter. Leave it empty for the enemy's own. Esc cancels.",
    }).ToArray();

    // In the battle, a box without a title shows the Enemy name (not the placeholder's), or no
    // title when there is none. The game only shows a box that has a text, and at most 3 lines of
    // it (BattleDraft.InfoBoxNotes says when a box won't show as typed).
    private static readonly TextField[] InfoTitleFields = Enumerable.Range(0, EnemyPlaceholders.MaxInfoBoxes).Select(i => new TextField
    {
        Label = $"Box {i + 1} title",
        Max = 60,
        Enemy = true,
        Get = () => draft?.InfoBox(i).Title ?? "",
        Set = text => draft!.SetInfoBox(i, text, null),
        Empty = () => draft?.EnemyName is { Length: > 0 } name ? $"(empty: shows the enemy name, {name})" : "(empty: no title)",
    }).ToArray();

    // About three lines of the box's width; the typing hint says how many lines it takes.
    private static readonly TextField[] InfoTextFields = Enumerable.Range(0, EnemyPlaceholders.MaxInfoBoxes).Select(i => new TextField
    {
        Label = $"Box {i + 1} text",
        Max = BattleDraft.InfoMaxLines * BattleDraft.InfoLineChars,
        MultiLine = true,
        Enemy = true,
        Get = () => draft?.InfoBox(i).Description ?? "",
        Set = text => draft!.SetInfoBox(i, null, text),
        Empty = () => "(empty: the battle doesn't show this box)",
        Measure = InfoLinesHint,
    }).ToArray();

    private static string InfoLinesHint(string text)
    {
        int lines = BattleDraft.InfoTextLines(text);
        return lines > BattleDraft.InfoMaxLines
            ? $", about {lines} lines: the battle shows {BattleDraft.InfoMaxLines}"
            : $", about {lines} of {BattleDraft.InfoMaxLines} lines";
    }

    // ---- saving ----------------------------------------------------------------------------------

    private static bool Save()
    {
        if (draft == null || !FinishTyping()) return false;
        try
        {
            draft.Save();
            ModLog.Info($"Battle creator: saved {Path.Combine(draft.Folder, BattlePackage.ManifestName)}.");
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the creator stays open with the changes still in it.
            ModLog.Error("Battle creator: saving failed: " + ex);
            Say("Saving failed: " + ex.Message, 7f);
            return false;
        }
        CleanUnused();
        RefreshBattleInfo();
        RefreshArcade();
        Say("Saved.", 2.5f);
        return true;
    }

    // The arcade reads the folder again each time it opens; this also updates the battles it
    // already built this session (it rebuilds the changed ones).
    private static void RefreshArcade()
    {
        if (CustomBattles.Version == 0) return;
        try { CustomBattles.Refresh(); }
        catch (Exception ex) { ModLog.Error("Battle creator: updating the arcade's battles failed: " + ex.Message); }
    }

    /// <summary>Sends the songs and images that the saved battle no longer uses to the Recycle Bin, in the background.</summary>
    private static void CleanUnused()
    {
        // One at a time: whatever is left waits for the next save or for leaving the battle.
        if (draft == null || touched.Count == 0 || cleanup != null) return;
        var candidates = touched.ToList();
        touched.Clear();
        string folder = draft.Folder;
        IntPtr owner = gameWindow;
        cleanup = OnShellThread(() =>
        {
            var moved = new List<string>();
            var kept = new List<string>();
            foreach (var path in BattleFiles.Unreferenced(folder, candidates))
            {
                // A file Windows can't put in the Recycle Bin stays where it is (it is never deleted for good).
                try
                {
                    BattleFiles.Recycle(path, folder, owner);
                    moved.Add(path);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    kept.Add($"{path} ({ex.Message})");
                }
            }
            return (moved, kept);
        });
    }

    private static void FinishCleanup()
    {
        if (cleanup == null || !cleanup.IsCompleted) return;
        var task = cleanup;
        cleanup = null;
        if (task.IsFaulted)
        {
            ModLog.Error("Battle creator: moving unused files to the Recycle Bin failed: " + Unwrap(task.Exception!).Message);
            return;
        }
        var (moved, kept) = task.Result;
        foreach (var path in moved) ModLog.Info($"Battle creator: moved {path} to the Recycle Bin (the battle doesn't use it any more).");
        foreach (var path in kept) ModLog.Info($"Battle creator: left {path} in the battle's folder; the battle doesn't use it any more.");
        if (kept.Count > 0) Say("Songs or images the battle doesn't use any more stay in its folder: Windows can't put them in the Recycle Bin.", 6f);
    }

    // ---- new battles, imports and exports ------------------------------------------------------------

    private static void StartNewBattle()
    {
        Run(FileDialogs.Open(FileDialogs.Purpose.Songs, "Choose the song for the new battle"), "Choose a song in the window that opened...", path =>
        {
            if (path == null) return;
            ShowPicker(new Picker
            {
                Heading = $"{Path.GetFileNameWithoutExtension(path)}: how many lanes?",
                Rows = { "4 lanes", "5 lanes: the middle lane is played with the Attack key" },
                Hint = i => (i == 0
                    ? "Played with the lane keys (D F J K by default)."
                    : "In 5 lanes the player's own attacks are off: the enemy only takes damage from Player attack events, added on the chart editor's Events tab.") +
                    " This can't change later; for the other number of lanes, make another battle.  Esc goes back.",
                Choose = i => CreateBattle(path, i == 0 ? 4 : 5),
                Back = () => ShowScreen(Screen.List),
            });
        });
    }

    private static void CreateBattle(string songFile, int lanes)
    {
        string root = Root;
        string author = PlayerPrefs.GetString(AuthorPref, "");
        Run(Task.Run(() => BattleFiles.CreateBattle(root, songFile, lanes, author)), "Making the battle...", folder =>
        {
            ModLog.Info($"Battle creator: created {folder} ({lanes} lanes) from {songFile}.");
            Rescan();
            SelectInList(folder);
            OpenBattle(folder);
            Say("The battle is made. Next: chart it on the Charts page, and pick its enemy.", 7f);
        });
    }

    private static void StartImport()
    {
        Run(FileDialogs.Open(FileDialogs.Purpose.BattlePacks, "Import a battle"), "Choose a .nbbbattle file in the window that opened...", path =>
        {
            if (path == null) return;
            // A zip the arcade reads from the battles folder is unpacked in its place, like
            // choosing its row, and asks first the same way. Any other zip stays where it is.
            if (BattleFiles.IsScanned(Root, path))
            {
                var listed = entries.FirstOrDefault(e => e.IsZip && string.Equals(Path.GetFullPath(e.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                AskImportZip(path, listed?.Title ?? Path.GetFileNameWithoutExtension(path));
            }
            else ImportZip(path, fromRoot: false);
        });
    }

    private static void AskImportZip(string zip, string title)
    {
        ShowPicker(Confirm($"Unpack \"{title}\" to edit it?", "Cancel", "Unpack it",
            "It becomes a battle folder you can edit. The zip goes to the Recycle Bin, so the arcade doesn't list the battle twice.",
            () => ImportZip(zip, fromRoot: true), () => ShowScreen(Screen.List)));
    }

    /// <param name="fromRoot">The zip is one of the battles in the battles folder: the new folder replaces it.</param>
    private static void ImportZip(string zip, bool fromRoot)
    {
        string root = Root;
        IntPtr owner = gameWindow;
        Run(OnShellThread(() =>
        {
            var result = BattleFiles.Import(zip, root, fromRoot ? zip : null);
            string zipNote = "";
            bool zipStays = false;
            if (fromRoot)
            {
                // With both the zip and the folder (one id), the arcade would load the zip and skip the folder.
                try
                {
                    BattleFiles.Recycle(zip, root, owner);
                    zipNote = "; the zip is in the Recycle Bin";
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    string aside = zip + ".imported";
                    if (File.Exists(aside)) aside = zip + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".imported";
                    try
                    {
                        File.Move(zip, aside);
                        zipNote = $"; the zip couldn't go to the Recycle Bin ({ex.Message}), so it was renamed to {Path.GetFileName(aside)}";
                    }
                    catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
                    {
                        // The folder is made all the same; the zip, still there, hides it in the arcade.
                        zipStays = true;
                        zipNote = $"; the zip couldn't go to the Recycle Bin ({ex.Message}) or be renamed ({moveEx.Message}), so it is still there and the arcade plays it instead of the folder";
                    }
                }
            }
            return (result.Folder, result.NewId, zipNote, zipStays);
        }), "Importing...", imported =>
        {
            ModLog.Info($"Battle creator: imported {zip} to {imported.Folder}" +
                        (imported.NewId ? " with a new id (a battle with its id is already here)" : "") + imported.zipNote + ".");
            Rescan();
            SelectInList(imported.Folder);
            OpenBattle(imported.Folder);
            if (imported.zipStays)
                Say($"Imported, but {Path.GetFileName(zip)} couldn't be moved, so the arcade plays the zip instead of this folder. Move the zip out of the battles folder.", 10f);
            else Say(imported.NewId ? "Imported. A battle with the same id was already here, so this copy has its own id." : "Imported.", 6f);
        });
    }

    private static void StartExport()
    {
        if (draft == null || !FinishTyping()) return;
        if (draft.Dirty && !Save()) return;
        var d = draft;
        string name = BattleFiles.SafeFolderName(d.Title) + BattlePackage.Extension;
        Run(FileDialogs.Save(FileDialogs.Purpose.BattlePacks, "Export the battle", name, BattlePackage.Extension),
            "Choose where to save it in the window that opened...", path =>
        {
            if (path == null) { Say("Export cancelled.", 2f); return; }
            string folder = d.Folder, root = Root;
            Run(Task.Run(() => BattleFiles.Export(folder, path, root)), "Exporting...", result =>
            {
                ModLog.Info($"Battle creator: exported {folder} to {path} ({result.Files} files, {result.Bytes / (1024.0 * 1024.0):0.0} MB).");
                Say($"Exported to {Path.GetFileName(path)}. Anyone with the mod can import it.", 6f);
            });
        });
    }

    private static void AskDelete()
    {
        if (draft == null || !FinishTyping()) return;
        var d = draft;
        // The second prompt has its Yes on the other row, so one spot clicked twice can't answer both.
        ShowPicker(Confirm($"Delete \"{d.Title}\"?", "No, keep it", "Yes, delete it", "Deleting moves the whole battle folder to the Recycle Bin.",
            () => ShowPicker(Confirm($"Really delete \"{d.Title}\"?", "No, keep it", "Yes, move it to the Recycle Bin",
                "Its charts, song and images go with it. You can get them back from the Recycle Bin.", () => DeleteBattle(d), BackFromPicker, yesFirst: true)),
            BackFromPicker));
    }

    private static void DeleteBattle(BattleDraft d)
    {
        string folder = d.Folder, root = Root;
        IntPtr owner = gameWindow;
        Run(OnShellThread(() => { BattleFiles.Recycle(folder, root, owner); return true; }), "Moving the battle to the Recycle Bin...", _ =>
        {
            ModLog.Info($"Battle creator: moved {folder} to the Recycle Bin.");
            StopPreview();
            ClearCardPreview();
            touched.Clear();
            draft = null;
            song = null;
            audioLoad = null;
            charts = null;
            summary = null;
            Rescan();
            listIndex = 0;
            ShowScreen(Screen.List);
            Say("The battle is in the Recycle Bin.", 5f);
            RefreshArcade();
        });
    }

    // ---- the song and the card ------------------------------------------------------------------------

    private static Task<(short[] Stereo, int Rate)>? audioLoad;
    private static (short[] Stereo, int Rate)? song;
    private static string audioState = "";
    private static EditorAudio? preview;
    private static double previewEnd;

    /// <summary>Decodes the battle's song in the background, for its length and the preview.</summary>
    private static void StartAudioLoad()
    {
        song = null;
        audioLoad = null;
        if (draft == null) return;
        string audio = draft.EffectiveAudio;
        string? name = PackageFiles.SafeName(audio);
        string path = name == null ? "" : Path.Combine(draft.Folder, name.Replace('/', Path.DirectorySeparatorChar));
        if (name == null || !File.Exists(path)) { audioState = name == null ? "no song file named" : $"{audio} is missing"; return; }
        if (new FileInfo(path).Length > BattlePackage.MaxAudioBytes) { audioState = "the song file is too big"; return; }
        audioState = "reading the song...";
        audioLoad = Task.Run(() => AudioFile.Decode(File.ReadAllBytes(path), path));
    }

    private static void FinishAudioLoad()
    {
        if (audioLoad == null || !audioLoad.IsCompleted) return;
        var task = audioLoad;
        audioLoad = null;
        if (task.IsFaulted || task.IsCanceled)
        {
            var reason = Unwrap(task.Exception ?? new Exception("cancelled"));
            audioState = "can't be read: " + reason.Message;
            ModLog.Error("Battle creator: the song couldn't be read: " + reason.Message);
            return;
        }
        song = task.Result;
        audioState = $"{FormatTime(song.Value.Stereo.Length / 2.0 / song.Value.Rate)} ({song.Value.Rate} Hz)";
    }

    private static string FormatTime(double seconds)
    {
        int m = (int)(seconds / 60);
        return $"{m}:{(seconds - m * 60).ToString("00.0", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Plays 10 seconds of the song from the preview start, or stops it.</summary>
    private static void TogglePreview()
    {
        if (preview != null) { StopPreview(); return; }
        if (draft == null || !FinishTyping()) return;
        if (song == null) { Say(audioLoad != null ? "The song is still loading." : "The song can't play: it " + audioState, 4f); return; }
        var (stereo, rate) = song.Value;
        double length = stereo.Length / 2.0 / rate;
        double start = Math.Clamp(draft.PreviewStart, 0, Math.Max(0, length - 0.5));
        try
        {
            preview = new EditorAudio(stereo, rate);
            preview.Seek(start);
            preview.Play();
            previewEnd = Math.Min(length, start + 10);
        }
        catch (Exception ex)
        {
            StopPreview();
            ModLog.Error("Battle creator: playing the preview failed: " + ex.Message);
            Say("The song can't play: " + ex.Message, 5f);
        }
    }

    private static void UpdatePreview()
    {
        if (preview == null) return;
        if (preview.Failed != null) { Say("The song stopped: " + preview.Failed, 4f); StopPreview(); return; }
        if (!preview.Playing || preview.Time >= previewEnd - 0.01) StopPreview();
    }

    private static void StopPreview()
    {
        preview?.Dispose();
        preview = null;
    }

    private static void ReplaceSong()
    {
        if (draft == null || !FinishTyping()) return;
        var d = draft;
        Run(FileDialogs.Open(FileDialogs.Purpose.Songs, "Choose the battle's new song"), "Choose a song in the window that opened...", path =>
        {
            if (path == null || draft != d) return;
            Run(Task.Run(() => BattleFiles.AddFile(d.Folder, path, "audio", BattlePackage.MaxAudioBytes)), "Copying the song...", added =>
            {
                if (draft != d) return;
                if (added.Copied) touched.Add(added.Path);
                // The old song goes after a save if nothing names it then (the same file under another spelling stays).
                string old = d.EffectiveAudio;
                if (old.Length > 0 && !old.Equals(added.Path, StringComparison.OrdinalIgnoreCase)) touched.Add(old);
                StopPreview();
                d.Audio = added.Path;
                StartAudioLoad();
                ModLog.Info($"Battle creator: new song {added.Path} for {d.Folder} (from {path}).");
                Say("New song in. Save to keep it. The charts stay as they are, so check their timing.", 6f);
            });
        });
    }

    private static void ChooseCard()
    {
        if (draft == null || !FinishTyping()) return;
        var d = draft;
        Run(FileDialogs.Open(FileDialogs.Purpose.CardImages, "Choose the battle's card image"), "Choose an image in the window that opened...", path =>
        {
            if (path == null || draft != d) return;
            // Cards show PNG and JPEG images only; anything else isn't copied in.
            if (!BattleFiles.IsCardImage(path))
            {
                Say($"{Path.GetFileName(path)} isn't a PNG or JPEG image, and cards only show those. Choose a .png or .jpg file.", 7f);
                return;
            }
            var (card, copied) = BattleFiles.AddFile(d.Folder, path, "images", BattlePackage.MaxImageBytes);
            if (copied) touched.Add(card);
            if (d.Card != null && !d.Card.Equals(card, StringComparison.OrdinalIgnoreCase)) touched.Add(d.Card);
            d.Card = card;
            LoadCardPreview();
            ModLog.Info($"Battle creator: card image {card} for {d.Folder} (from {path}).");
            Say("Card image set. Save to keep it.", 4f);
        });
    }

    private static void RemoveCard()
    {
        if (draft?.Card == null) return;
        touched.Add(draft.Card);
        draft.Card = null;
        LoadCardPreview();
        Say("Card image removed. Save to keep it that way.", 4f);
    }

    // ---- the enemy -------------------------------------------------------------------------------------

    private static void ChooseEnemy()
    {
        if (draft == null || !FinishTyping() || !EnemyEditable()) return;
        var d = draft;
        var list = EnemyChoices.List(d.Advanced);
        string current = EnemyChoices.Normalize(d.Placeholder);
        var rows = list.Select(c => c.Advanced ? c.Name + "  (advanced boss)" : c.Name).ToList();
        int index = list.FindIndex(c => c.Asset.Equals(current, StringComparison.OrdinalIgnoreCase));
        // An enemy the list leaves out (an advanced boss while they're off, or one it doesn't know)
        // gets a row of its own on top, where the list opens, so opening it to look changes nothing.
        int keep = index < 0 ? 1 : 0;
        if (keep == 1) rows.Insert(0, EnemyChoices.NameOf(current) + "  (current)");
        ShowPicker(new Picker
        {
            Heading = "The enemy: which game enemy stands in",
            Rows = rows,
            Hint = i => i < keep
                ? (EnemyChoices.Problem(current, d.Advanced) ?? "The enemy it is now.") + "  Esc goes back."
                : i - keep < list.Count ? EnemyHint(list[i - keep]) : "",
            Index = index + keep,
            Choose = i =>
            {
                if (i >= keep) d.Placeholder = list[i - keep].Asset;
                BackFromPicker();
            },
            Back = BackFromPicker,
        });
    }

    private static string EnemyHint(EnemyChoice c) =>
        $"Its own stats: HP {Num(c.Hp)}, damage {Num(c.Damage)}, energy per miss {Num(c.EnergyChargeOnMiss)}, passive energy {Num(c.PassiveEnergyCharge)}." +
        (c.Advanced ? " A scripted boss: it may not play well here." : "") + "  Esc goes back.";

    private static void ToggleAdvanced()
    {
        if (draft == null || !EnemyEditable()) return;
        draft.Advanced = !draft.Advanced;
        Say(draft.Advanced ? "Advanced bosses are in the enemy list now. They are built around scripted fights and may not play well." : "Advanced bosses are off.", 5f);
    }

    // Switching back to the battle's own boxes brings back what they held (until the creator closes).
    private static void ToggleOwnInfo()
    {
        if (draft == null || !EnemyEditable()) return;
        draft.SetOwnInfo(!draft.OwnInfo);
    }

    // ---- gear ------------------------------------------------------------------------------------------

    private static readonly GearSlot[] GearSlots = { GearSlot.MainHand, GearSlot.Body, GearSlot.Head, GearSlot.OffHand, GearSlot.Amulet, GearSlot.Consumable };
    private static bool showTestItems;

    // What the gear page shows for each slot, and the items the game doesn't have. Worked out when
    // the gear changes or the page opens, not every frame: reading the game's item list while it
    // isn't loaded yet would try again (and could report an error) each time.
    private static readonly Dictionary<GearSlot, string> gearNames = new();
    private static readonly List<string> gearUnknown = new();
    private static bool gearListed;

    private static void RefreshGear()
    {
        gearNames.Clear();
        gearUnknown.Clear();
        if (draft != null && draft.SetGear)
        {
            gearListed = GearCatalog.All.Count > 0;
            foreach (var slot in GearSlots)
            {
                string? id = draft.GearItem(slot);
                if (id == null) continue;
                var item = gearListed ? GearCatalog.Find(id) : null;
                if (item != null) gearNames[slot] = EditorUi.Escape(item.Name);
                else if (!gearListed) gearNames[slot] = EditorUi.Escape(id);
                else
                {
                    gearNames[slot] = $"<color=#F2B02E>unknown item {EditorUi.Escape(id)}</color>";
                    gearUnknown.Add($"{GearCatalog.LabelOf(slot)}: the game has no item \"{EditorUi.Escape(id)}\", so the battle leaves that slot empty.");
                }
            }
        }
        RefreshLevel();
    }

    private static void SetGearMode(bool set)
    {
        draft?.SetGearMode(set);
        RefreshGear();
    }

    // ---- level ------------------------------------------------------------------------------------------

    // The set level's stat gains and what the arcade will show, worked out with the gear (RefreshGear);
    // and about how many lines of the arcade's box are left for the lore (the Info page's hint).
    private static string levelGains = "", noticePreview = "";
    private static int loreRoom = BattleNotice.BoxLines;

    private static void RefreshLevel()
    {
        levelGains = "";
        noticePreview = "";
        loreRoom = BattleNotice.BoxLines;
        if (draft == null) return;
        try
        {
            if (draft.SetLevel)
            {
                int level = BattleGear.EffectiveLevel(draft.LevelValue) ?? draft.LevelValue;
                var gains = BattleGear.LevelGains(level);
                levelGains = gains is not { } g ? ""
                    : level <= 1 ? "Level 1 is where every player starts: no stat gains from levels."
                    : $"Compared with level 1: Strength +{g.Strength}, Regen +{g.Regen}, Critical +{g.Critical}.";
            }
            // What the arcade's box and the card will say, from the same text as the arcade's.
            var input = BattleNoticeArcade.InputFor(draft);
            string box = BattleNotice.Box(input, text => BattleNotice.FitsLines(text)) ?? "<color=#9D92B4>(nothing: the box stays hidden)</color>";
            noticePreview = $"{box}\n\n<color=#9D92B4>On the battle's card:</color> {BattleNotice.Badge(input) ?? "no tag"}";
            loreRoom = BattleNotice.LoreRoom(input);
        }
        catch (Exception ex) { ModLog.Error("Battle creator: working out the level page failed: " + ex); }
    }

    private static void SetLevelMode(bool set)
    {
        // "Set" starts at the player's own level when a save is loaded.
        draft?.SetLevelMode(set, BattleGear.PlayerLevel() ?? LevelDefinition.MinLevel);
        RefreshGear();
    }

    private static void StepLevel(int direction)
    {
        if (draft == null) return;
        int max = Math.Min(LevelDefinition.MaxLevel, BattleGear.GameMaxLevel() ?? LevelDefinition.MaxLevel);
        int next = Math.Clamp(draft.LevelValue + direction, LevelDefinition.MinLevel, max);
        if (next == draft.LevelValue && draft.SetLevel) return;
        draft.SetLevelValue(next);
        RefreshGear();
    }

    private static string GearText(GearSlot slot) => gearNames.TryGetValue(slot, out var name) ? name : "<color=#9D92B4>(empty)</color>";

    private static void ChooseGear(GearSlot slot)
    {
        if (draft == null) return;
        if (GearCatalog.All.Count == 0) { Say("The game's items aren't loaded yet, so they can't be listed now. Try again after loading a save.", 6f); return; }
        var d = draft;
        var items = GearCatalog.ForSlot(slot, showTestItems);
        string? currentId = d.GearItem(slot);
        var current = GearCatalog.Find(currentId);
        var rows = new List<string> { "(empty)" };
        rows.AddRange(items.Select(i => i.Debug ? i.Name + "  (test item)" : i.Name));
        int index = current == null ? -1 : items.FindIndex(i => i.Id == current.Id);
        // An item the list leaves out (a test item while they're hidden, or one the game doesn't
        // have) gets a row of its own after "(empty)", where the list opens, so opening it to look
        // changes nothing.
        int keep = currentId != null && index < 0 ? 1 : 0;
        if (keep == 1)
            rows.Insert(1, current != null ? current.Name + (current.Debug ? "  (test item, current)" : "  (current)") : $"unknown item {currentId}  (current)");
        ShowPicker(new Picker
        {
            Heading = $"{GearCatalog.LabelOf(slot)} for this battle",
            Rows = rows,
            Hint = i => i <= 0 ? "Nothing in this slot.  Esc goes back."
                : i <= keep ? (current != null ? ItemHint(current) : $"The game has no item \"{currentId}\", so the battle leaves this slot empty.  Esc goes back.")
                : i - keep <= items.Count ? ItemHint(items[i - keep - 1]) : "",
            Index = currentId == null ? 0 : keep == 1 ? 1 : index + 1,
            Choose = i =>
            {
                if (i == 0) d.SetGearItem(slot, null);
                else if (i > keep) d.SetGearItem(slot, items[i - keep - 1].Id);
                RefreshGear();
                BackFromPicker();
            },
            Back = BackFromPicker,
        });
    }

    private static readonly Regex Tags = new("<[^>]*>");

    private static string ItemHint(GearItem item)
    {
        string text = Tags.Replace(item.Description, "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length > 150) text = text.Substring(0, 147).TrimEnd() + "...";
        return (text.Length > 0 ? text + "  " : "") + $"(id {item.Id})";
    }

    // Health upgrades change what the arcade shows ("Health upgrades: 3."), so the preview is worked out again.
    private static void SetHealthMode(bool set)
    {
        if (draft == null) return;
        draft.SetExtraHealthMode(set);
        RefreshGear();
    }

    // Only while the battle sets them: "-" stops at 0 and "+" at the loader's limit, and neither
    // goes back to the player's own (the button above does that). A number above the limit, from
    // a battle.json written by hand, isn't lowered by "+".
    private static void StepExtraHealth(int direction)
    {
        if (draft?.ExtraHealth is not int n) return;
        int next = direction < 0 ? Math.Max(0, Math.Min(n, GearDefinition.MaxCount + 1) - 1) : n >= GearDefinition.MaxCount ? n : n + 1;
        draft.SetExtraHealth(next);
        RefreshGear();
    }
}
