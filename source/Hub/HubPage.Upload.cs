using UnityEngine;
using static NocturneFlatScroll.EditorPageKit;
using static NocturneFlatScroll.EditorUi;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

// Upload (DESIGN-HUB 1.6): a battle (picked from the player's battle folders, with its card beside
// the list), custom difficulties for the game's songs (a song, then its difficulties, as many songs
// as wanted), or the pointer for an osu!mania beatmap (it's made into a battle in the creator
// first). Then the details (a description; a pack's title; the name on the hub at the first
// upload), the check and build on a worker with a summary of what goes in and what was left out
// or cleaned, the rules before every upload (the upload button on the second row, so a double
// click can't skip them), and the upload with its progress. It's live the moment it finishes.
internal static partial class HubPage
{
    private static readonly (string Title, string Sub)[] UploadRows =
    {
        ("Upload a battle", "A battle you made in the battle creator: its song, charts, enemy, pictures and dialogue."),
        ("Upload custom difficulties", "Difficulties you made for the game's own songs, sent as one pack."),
        ("An osu!mania beatmap you made (.osz)", "Turn it into a battle first (Options > Custom Charts > Battle creator > New battle from .osz), then upload that battle."),
    };

    private static int uploadIndex;

    /// <summary>What the player is putting together to upload.</summary>
    private sealed class UploadDraft
    {
        internal string Kind = "battle";
        internal string? Folder;
        internal string BattleTitle = "", BattleArtist = "", BattleAuthor = "";
        internal readonly List<HubPackChoice> Picked = new();
        internal string PackTitle = "";
        internal string Description = "";
        internal string DisplayName = "";
        /// <summary>A new version of this entry (one of the player's own), or null for a new entry.</summary>
        internal HubMyCard? Of;
    }

    /// <summary>An upload being sent: its progress, and the way to stop it.</summary>
    private sealed class Sending
    {
        internal string Title = "";
        internal readonly HubTransfer Transfer = new();
        internal CancellationTokenSource Cts = null!;
        /// <summary>The hub key the upload uses, once the worker has it (made there at the first upload).</summary>
        internal volatile HubIdentity? Key;
    }

    private static UploadDraft? draft;
    private static HubBuild? built;
    private static string? builtThumb;
    private static List<string> sendProblems = new();
    private static Sending? sending;
    private static List<HubPackChoice> packChoices = new();
    private static ProgressBar? sendBar;

    private static void ResetUpload()
    {
        uploadIndex = 0;
        draft = null;
        built = null;
        builtThumb = null;
        sendProblems = new List<string>();
        sending = null;
        packChoices = new List<HubPackChoice>();
        sendBar = null;
    }

    private static bool FirstUpload => identity?.UploaderId == null;

    /// <summary>Whether an upload can start now, and if not, why.</summary>
    private static bool UploadAllowed(out string why)
    {
        why = "";
        if (store == null || link is Link.Opening or Link.Connecting) why = "The hub isn't connected yet.";
        else if (link == Link.Down) why = linkProblem;
        else if (link == Link.TooOld) why = HubErrorsText.TooOld;
        else if (info != null && !info.UploadsOpen) why = "Uploads are closed right now. Downloads still work.";
        else if (identityProblem != null) why = "Your hub key can't be read: " + identityProblem + ". My uploads can use a saved key or make a new one.";
        else if (sending != null) why = "An upload is already being sent.";
        return why.Length == 0;
    }

    // ---- the tab ------------------------------------------------------------------------------------

    private static ThumbRow UploadRow(int i) => new()
    {
        NoPicture = true,
        Title = UploadRows[i].Title,
        Sub = UploadRows[i].Sub,
        Tag = i == 2 ? "IN THE CREATOR" : "",
        TagColor = DimText,
    };

    private static void UploadPanel(PanelView v)
    {
        v.NoPicture = true;
        int i = Math.Clamp(uploadIndex, 0, UploadRows.Length - 1);
        v.Title = UploadRows[i].Title;
        long max = info?.MaxPackageBytes ?? 100L * 1024 * 1024;
        v.Body = i switch
        {
            0 => "Pick one of your battle folders. It's checked and packed here first: only the files the battle uses go in, pictures lose their " +
                 "hidden details (like where a photo was taken), and paths with your Windows user name are caught. You see what goes in before anything is sent.\n\n" +
                 $"Up to {max / (1024 * 1024)} MB. Songs: .ogg, .wav or .mp3. Pictures: .png, .jpg or .gif. Videos: WebM (VP8), or frames.",
            1 => "Pick custom difficulties you made in the chart editor for the game's own songs; they go up as one pack. " +
                 "All of them must have the same lanes. A difficulty that plays its own song file can't go in a pack: make it a custom battle to share it.",
            _ => "The hub doesn't take .osz files: the mod turns a beatmap into a battle on your PC. In the battle creator, choose New battle from .osz, " +
                 "chart it as you like, then upload that battle here. Upload only beatmaps you mapped, or ones the mapper allowed.",
        };
        v.Facts = LimitsText();
        // Why an upload can't start now can hold the hub's own words: the note is plain text.
        v.Note = !UploadAllowed(out string why) ? why
            : FirstUpload ? "Your first upload registers this PC's hub key with the name you choose." : "";
    }

    // The panel's buttons, for the mouse: the first two rows' steps (the .osz row is only its words).
    private static List<PanelAction> UploadActions()
    {
        string enter = PadNames ? "A" : "Enter";
        return new List<PanelAction>
        {
            new() { Text = "Choose a battle...", Key = uploadIndex == 0 ? enter : "", Do = () => PickBattle(null) },
            new() { Text = "Choose difficulties...", Key = uploadIndex == 1 ? enter : "", Do = () => PickPack(null) },
        };
    }

    private static void UploadPrimary()
    {
        switch (uploadIndex)
        {
            case 0: PickBattle(null); break;
            case 1: PickPack(null); break;
            default: Say(UploadRows[2].Sub, 10f); break;
        }
    }

    // ---- a battle -----------------------------------------------------------------------------------

    /// <summary>A battle in the picker, with its card and who made it (read on a worker).</summary>
    private sealed class BattlePick
    {
        internal HubBattleChoice Choice = null!;
        internal string Title = "", Artist = "", Author = "";
        internal string? CardFile;
        internal CardLayout.Look Look = CardLayout.Look.Default;
    }

    private static List<BattlePick> ReadBattles(HubStore s, string? only)
    {
        var picks = new List<BattlePick>();
        foreach (var choice in HubUploadBuild.BattleChoices(BattleFiles.List(s.Paths.Battles), s))
        {
            if (only != null && !SameFolder(choice.Entry.Path, only)) continue;
            var pick = new BattlePick { Choice = choice, Title = choice.Entry.Title, Artist = choice.Entry.Artist };
            if (choice.Offered)
            {
                try
                {
                    var package = BattlePackage.Load(choice.Entry.Path);
                    pick.Author = package.Author;
                    pick.Look = package.CardLook;
                    if (package.CardPath != null && PackageFiles.SafeName(package.CardPath) is { } card)
                        pick.CardFile = Path.Combine(choice.Entry.Path, card.Replace('/', Path.DirectorySeparatorChar));
                }
                catch (Exception ex) when (BattleDraft.IsFileProblem(ex)) { }
            }
            picks.Add(pick);
        }
        return picks;
    }

    private static bool SameFolder(string a, string b)
    {
        try { return Path.GetFullPath(a).TrimEnd('\\', '/').Equals(Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>The battles the player can upload, in a picker with each one's card beside the list.</summary>
    private static void PickBattle(HubMyCard? of)
    {
        if (!UploadAllowed(out string why))
        {
            Say(why, 6f);
            return;
        }
        var s = store!;
        Work("Reading your battles...", Task.Run(() => ReadBattles(s, null)), picks =>
        {
            if (picks.Count == 0)
            {
                Say("You have no battles yet. Make one in the battle creator (Options > Custom Charts > Battle creator).", 8f);
                return;
            }
            // The one to update first, then the ones that can go, then the rest.
            var ordered = picks
                .OrderByDescending(p => of?.BattleId != null && p.Choice.Entry.Id.Equals(of.BattleId, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(p => p.Choice.Offered)
                .ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ShowBattlePicker(ordered, of, 0);
        });
    }

    private static void ShowBattlePicker(List<BattlePick> picks, HubMyCard? of, int index)
    {
        var rows = picks.Select(p =>
        {
            string name = TitleLine(p.Title.Length > 0 ? p.Title : Path.GetFileName(p.Choice.Entry.Path.TrimEnd('\\', '/')), p.Artist);
            return p.Choice.Offered
                ? $"{name}   ({p.Choice.Entry.Lanes} lanes; {string.Join(", ", p.Choice.Entry.Charted)})"
                : $"{name}   (can't be uploaded)";
        }).ToList();
        ShowPicker(new Picker
        {
            Plain = true,
            Heading = of != null ? $"Which battle is the new version of {of.Title}?" : "Upload which battle?",
            Rows = rows,
            Index = Math.Clamp(index, 0, rows.Count - 1),
            Jump = true,
            Hint = i =>
            {
                var p = picks[i];
                if (!p.Choice.Offered) return "It can't be uploaded: " + p.Choice.Why + ".";
                if (of == null && p.Choice.Previous is { } previous && LiveEntry(previous.Package) is { } entry)
                    return $"You uploaded this (v{entry.Version}); this makes v{entry.Version + 1}.";
                return "Choose it to check it and see what goes in. Nothing is sent yet.";
            },
            Face = i => Face(picks[i].CardFile, picks[i].Look),
            FaceNote = i => picks[i].Author.Length > 0 ? "charted by " + picks[i].Author : "",
            Choose = i =>
            {
                var p = picks[i];
                if (!p.Choice.Offered)
                {
                    Say("It can't be uploaded: " + p.Choice.Why + ".", 6f);
                    ShowBattlePicker(picks, of, i);
                    return;
                }
                BeginBattle(p, of);
            },
            Back = BackToMain,
        });
    }

    /// <summary>The battle creator's (or My uploads') battle, straight to the details.</summary>
    private static void StartBattleUpload(string folder, HubMyCard? of = null)
    {
        if (!UploadAllowed(out string why))
        {
            Say(why, 6f);
            return;
        }
        var s = store!;
        Work("Reading the battle...", Task.Run(() => ReadBattles(s, folder)), picks =>
        {
            var pick = picks.FirstOrDefault();
            if (pick == null)
            {
                Say("That battle isn't in the CustomBattles folder any more.", 6f);
                return;
            }
            if (!pick.Choice.Offered)
            {
                Say("It can't be uploaded: " + pick.Choice.Why + ".", 8f);
                return;
            }
            BeginBattle(pick, of);
        });
    }

    // This key's live entry with that id, from My uploads (loaded with the key).
    private static HubMyCard? LiveEntry(string package) => mine?.FirstOrDefault(c => c.Id == package && c.Status == "live");

    private static void BeginBattle(BattlePick pick, HubMyCard? of)
    {
        DiscardBuild();
        draft = new UploadDraft
        {
            Kind = "battle",
            Folder = pick.Choice.Entry.Path,
            BattleTitle = pick.Title,
            BattleArtist = pick.Artist,
            BattleAuthor = pick.Author,
            DisplayName = identity != null && identity.Name.Length > 0 ? identity.Name : defaultName,
            // Uploaded from this PC before, and still on the hub: this makes its next version.
            Of = of ?? (pick.Choice.Previous is { } previous ? LiveEntry(previous.Package) : null),
        };
        if (draft.Of != null) draft.Description = draft.Of.Description;
        ShowForm(FormKind.Details);
    }

    // The card beside the battle picker: only the highlighted battle's is loaded (released when it moves).
    private static Sprite? faceSprite;
    private static Texture2D? faceTexture;
    private static string? faceFile;
    private static int faceFrame;

    private static Sprite? Face(string? file, CardLayout.Look look)
    {
        faceFrame = Time.frameCount;
        if (file == faceFile) return faceSprite;
        ClearFaces();
        faceFile = file;
        if (file == null) return null;
        try
        {
            var fileInfo = new FileInfo(file);
            if (!fileInfo.Exists || fileInfo.Length > BattlePackage.MaxImageBytes) return null;
            var card = CustomBattles.CardImages.Make(File.ReadAllBytes(file), Path.GetFileName(file), look, keepPart: true, out _);
            if (card != null)
            {
                faceSprite = card.Sprite;
                faceTexture = card.Texture;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return faceSprite;
    }

    private static void ClearFaces()
    {
        kit?.ClearPickerFace();
        if (faceSprite != null && faceSprite) Object.Destroy(faceSprite);
        if (faceTexture != null && faceTexture) Object.Destroy(faceTexture);
        faceSprite = null;
        faceTexture = null;
        faceFile = null;
    }

    private static void ReleaseUnwantedFaces()
    {
        if (faceFile != null && Time.frameCount - faceFrame > 2) ClearFaces();
    }

    // ---- custom difficulties --------------------------------------------------------------------------

    /// <summary>The player's own custom difficulties (not the hub's downloads): a song, then its difficulties.</summary>
    private static void PickPack(HubMyCard? of)
    {
        if (!UploadAllowed(out string why))
        {
            Say(why, 6f);
            return;
        }
        try { packChoices = HubGame.PackChoices(store!.Paths); }
        catch (Exception ex)
        {
            ReportOnce("listing the custom difficulties", ex);
            Say("Your custom difficulties can't be read now (see the log).", 6f);
            return;
        }
        if (packChoices.Count == 0)
        {
            Say("You have no custom difficulties of your own yet. Make one in the chart editor (Options > Custom Charts > Chart editor).", 8f);
            return;
        }
        DiscardBuild();
        draft = new UploadDraft
        {
            Kind = "charts",
            Of = of,
            PackTitle = of?.Title ?? "",
            Description = of?.Description ?? "",
            DisplayName = identity != null && identity.Name.Length > 0 ? identity.Name : defaultName,
        };
        ShowSongPicker(0);
    }

    private static List<string> PackSongs() =>
        packChoices.Select(c => c.Song).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

    private static void ShowSongPicker(int index)
    {
        var d = draft;
        if (d == null) return;
        var songs = PackSongs();
        var rows = songs.Select(song =>
        {
            int total = packChoices.Count(c => c.Song.Equals(song, StringComparison.OrdinalIgnoreCase));
            int picked = d.Picked.Count(c => c.Song.Equals(song, StringComparison.OrdinalIgnoreCase));
            return $"{song}   ({picked} of {total} picked)";
        }).ToList();
        rows.Add(d.Picked.Count > 0 ? $"Next: the pack's title ({Plural(d.Picked.Count, "difficulty", "difficulties")})" : "Cancel");
        ShowPicker(new Picker
        {
            Plain = true,
            Heading = d.Of != null ? $"New version of {d.Of.Title}: pick its difficulties" : "Upload custom difficulties: pick a song",
            Rows = rows,
            Index = Math.Clamp(index, 0, rows.Count - 1),
            Jump = true,
            Hint = i => i < songs.Count ? "Choose it to pick its difficulties. More songs can go in the same pack." :
                d.Picked.Count > 0 ? "Then the pack's title and description, and the check." : "Back to the hub.",
            Choose = i =>
            {
                if (i < songs.Count) ShowDifficultyPicker(songs[i], i, 0);
                else if (d.Picked.Count > 0)
                {
                    if (d.PackTitle.Length == 0 && d.Picked.Select(c => c.Song).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                        d.PackTitle = HubText.CleanLine(d.Picked[0].Song + " difficulties", HubText.PackTitle) ?? "";
                    ShowForm(FormKind.Details);
                }
                else BackToMain();
            },
            Back = BackToMain,
        });
    }

    private static string DifficultyName(HubPackChoice c)
    {
        var block = c.BlockIndex >= 0 && c.BlockIndex < c.Chart.Blocks.Count ? c.Chart.Blocks[c.BlockIndex] : null;
        string name = block?.DisplayName is { Length: > 0 } shown ? shown : "Chart";
        string level = block?.Meter.Trim() ?? "";
        return level.Length > 0 && level != "0" ? $"{name} {level}" : name;
    }

    private static void ShowDifficultyPicker(string song, int songIndex, int index)
    {
        var d = draft;
        if (d == null) return;
        var choices = packChoices.Where(c => c.Song.Equals(song, StringComparison.OrdinalIgnoreCase)).ToList();
        var rows = choices.Select(c =>
        {
            string mark = d.Picked.Contains(c) ? "[x]" : "[  ]";
            string own = c.PlaysOwnSong ? "   plays its own song file" : "";
            return $"{mark} {DifficultyName(c)}   ({c.Lanes} lanes, from {Path.GetFileName(c.SourceFile)}){own}";
        }).ToList();
        rows.Add("Back to the songs");
        ShowPicker(new Picker
        {
            Plain = true,
            Heading = $"{song}: pick the difficulties to share",
            Rows = rows,
            Index = Math.Clamp(index, 0, rows.Count - 1),
            Hint = i => i < choices.Count
                ? (choices[i].PlaysOwnSong ? "This difficulty plays its own song file. Make it a custom battle to share it." : "Choose it to put it in the pack, or take it out.")
                : "Pick difficulties of other songs, or go on to the pack's title.",
            Choose = i =>
            {
                if (i >= choices.Count)
                {
                    ShowSongPicker(songIndex);
                    return;
                }
                var c = choices[i];
                if (d.Picked.Contains(c)) d.Picked.Remove(c);
                else if (c.PlaysOwnSong) Say("This difficulty plays its own song file. Make it a custom battle to share it.", 6f);
                else if (d.Picked.Count > 0 && d.Picked[0].Lanes != c.Lanes)
                    Say($"All the difficulties in a pack must have the same lanes ({d.Picked[0].Lanes} so far). Upload the {c.Lanes}-lane ones as a separate pack.", 7f);
                else d.Picked.Add(c);
                ShowDifficultyPicker(song, songIndex, i);
            },
            Back = () => ShowSongPicker(songIndex),
        });
    }

    // ---- the upload's forms: details, check, rules, sending ----------------------------------------------

    private static readonly TextField DescriptionField = new()
    {
        Label = "Description",
        Get = () => draft?.Description ?? "",
        Set = text => { if (draft != null) draft.Description = HubText.CleanText(text, HubText.Description) ?? ""; },
        Max = HubText.Description,
        MultiLine = true,
        Empty = () => "(optional: what it is, how hard it is, anything players should know)",
    };

    private static readonly TextField PackTitleField = new()
    {
        Label = "Pack title",
        Get = () => draft?.PackTitle ?? "",
        Set = text =>
        {
            string clean = HubText.CleanLine(text, HubText.PackTitle) ?? "";
            if (clean.Length == 0) throw new InvalidDataException("The pack needs a title.");
            if (draft != null) draft.PackTitle = clean;
        },
        Max = HubText.PackTitle,
        Empty = () => "(the pack's name on the hub)",
    };

    private static readonly TextField NameField = new()
    {
        Label = "Your name on the hub",
        Get = () => draft?.DisplayName ?? "",
        Set = text =>
        {
            string name = HubText.CleanName(text) ?? throw new InvalidDataException(HubErrorsWords.BadName);
            if (draft != null) draft.DisplayName = name;
        },
        Max = HubText.Name,
        Empty = () => "(shown with your uploads)",
    };

    private static void BuildUploadForms()
    {
        var details = NewForm(FormKind.Details, () => draft == null ? "Upload" : draft.Of != null ? "Upload a new version"
            : draft.Kind == "battle" ? "Upload a battle" : "Upload custom difficulties");
        float y = -96;
        FormPlain(details, 40, ref y, FormW - 80, 100, DetailsText, 21);
        Kit.AddText(details.Panel, 40, ref y, FormW - 80, 26, () => "The title, artist and charter come from the battle: change them in the battle creator.", 17,
            () => draft?.Kind == "battle");
        Kit.AddField(details.Panel, details.Controls, 40, ref y, 1000, DescriptionField, h: 200);
        Kit.AddField(details.Panel, details.Controls, 40, ref y, 1000, PackTitleField, () => draft?.Kind == "charts");
        Kit.AddField(details.Panel, details.Controls, 40, ref y, 1000, NameField, () => FirstUpload);
        Kit.AddText(details.Panel, 40, ref y, 1000, 26, () => "Your name is asked only at your first upload. It shows with a tag, like Name #7K2M.", 17, () => FirstUpload);
        float buttons = Math.Min(y, -600);
        Kit.AddButton(details.Panel, details.Controls, 40, buttons, 300, 52, "Check it", BuildUpload);
        var check = details.Controls[^1];
        Kit.AddButton(details.Panel, details.Controls, 352, buttons, 200, 52, "Back", CancelUpload);
        details.Back = CancelUpload;
        // A pad starts on Check it: the text fields need a keyboard (A on one starts typing, B leaves it).
        details.StartFocus = () => PadNames ? Math.Max(0, details.Controls.Where(c => c.Visible).ToList().IndexOf(check)) : 0;

        var summary = NewForm(FormKind.Summary, () => built == null ? "Check" : built.Ok ? "Ready to upload" : "It can't be uploaded yet");
        y = -96;
        FormPlain(summary, 40, ref y, FormW - 80, 560, SummaryText, 19);
        Kit.AddButton(summary.Panel, summary.Controls, 40, y, 340, 52, "Continue to the rules", () => ShowForm(FormKind.Rules), () => built?.Ok == true);
        Kit.AddButton(summary.Panel, summary.Controls, 392, y, 260, 52, "Back to the details", BackToDetails);
        summary.Back = BackToDetails;

        var rules = NewForm(FormKind.Rules, () => "Before you upload");
        y = -96;
        // As tall as the summary's text, so Back is where "Continue to the rules" was.
        FormPlain(rules, 40, ref y, FormW - 80, 560, RulesText, 21);
        Kit.AddButton(rules.Panel, rules.Controls, 40, y, 240, 52, "Back", () => ShowForm(FormKind.Summary));
        // On a second row, so the click that opened the rules (or a double click) can't press it.
        Kit.AddButton(rules.Panel, rules.Controls, 40, y - 64, 760, 52, "I made this and I have the right to share it: Upload", SendUpload);
        rules.Back = () => ShowForm(FormKind.Summary);
        rules.StartFocus = () => 0;

        var send = NewForm(FormKind.Sending, () => "Uploading");
        y = -96;
        FormPlain(send, 40, ref y, FormW - 80, 34, () => sending?.Title ?? built?.Title ?? "", 24);
        sendBar = new ProgressBar("Bar", send.Panel);
        PlaceTop(sendBar.Rect, 40, y - 8, FormW - 80, 44);
        y -= 72;
        Kit.AddText(send.Panel, 40, ref y, FormW - 80, 60, () =>
            $"It goes live as soon as it's sent. {(PadNames ? "B" : "Esc")} stops it, and then nothing is published.", 19);
        Kit.AddButton(send.Panel, send.Controls, 40, y, 280, 52, "Stop the upload", AskStopUpload);
        send.Back = AskStopUpload;
    }

    private static string DetailsText()
    {
        var d = draft;
        if (d == null) return "";
        var lines = new List<string>();
        if (d.Kind == "battle")
        {
            lines.Add(d.BattleTitle);
            if (d.BattleArtist.Length > 0) lines.Add(d.BattleArtist);
            if (d.BattleAuthor.Length > 0) lines.Add("charted by " + d.BattleAuthor);
        }
        else
        {
            var songs = d.Picked.Select(c => c.Song).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            lines.Add($"{Plural(d.Picked.Count, "difficulty", "difficulties")} for {Songs(songs)}, {d.Picked.FirstOrDefault()?.Lanes ?? 4} lanes");
        }
        if (d.Of != null) lines.Add($"A new version of {d.Of.Title}: v{d.Of.Version} becomes v{d.Of.Version + 1}.");
        return string.Join("\n", lines);
    }

    private static void CancelUpload()
    {
        if (!Kit.FinishTyping()) return;
        DiscardBuild();
        draft = null;
        BackToMain();
    }

    private static void BackToDetails()
    {
        DiscardBuild();
        ShowForm(FormKind.Details);
    }

    // The package waiting in Hub\work is removed when it won't be sent.
    private static void DiscardBuild()
    {
        var b = built;
        var p = paths;
        built = null;
        builtThumb = null;
        sendProblems = new List<string>();
        if (b != null && p != null) Task.Run(() => HubUploadBuild.Discard(b, p));
    }

    /// <summary>Checks and packs the upload on a worker (Esc stops it), then its thumbnail here, then the summary.</summary>
    private static void BuildUpload()
    {
        if (!Kit.FinishTyping()) return;
        var d = draft;
        if (d == null) return;
        if (d.Kind == "charts" && d.PackTitle.Length == 0)
        {
            Say("Give the pack a title.", 4f);
            return;
        }
        if (FirstUpload && HubText.CleanName(d.DisplayName) == null)
        {
            Say(HubErrorsWords.BadName, 6f);
            return;
        }
        if (!UploadAllowed(out string why))
        {
            Say(why, 6f);
            return;
        }
        DiscardBuild();
        var p = paths!;
        var limits = HubZipCheck.Limits.From(info);
        string user = Environment.UserName;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var picked = d.Picked.ToList();
        // A pack's author: the charter when every difficulty has the same one, else the name on the hub.
        var authors = picked.Select(c => c.Author.Trim()).Where(a => a.Length > 0).Distinct().ToList();
        string author = authors.Count == 1 ? authors[0] : d.DisplayName;
        Func<HubBuild> make = d.Kind == "battle"
            ? () => HubUploadBuild.BuildBattle(d.Folder!, p, limits, user, cts.Token)
            : () => HubUploadBuild.BuildPack(picked, d.PackTitle, author, p, limits, user, cts.Token);
        Work(d.Kind == "battle" ? "Checking the battle and packing it..." : "Checking the difficulties and packing them...", Task.Run(make, cts.Token), build =>
        {
            built = build;
            // The thumbnail is made here: the card drawn small, with the game's own JPEG encoder.
            builtThumb = build.Ok ? HubGame.Thumbnail(build) : null;
            ModLog.Info($"Hub: built an upload of {build.Kind}: {(build.Ok ? $"{build.Size} bytes, {build.Entries} files" : "refused: " + string.Join("; ", build.Problems))}.");
            ShowForm(FormKind.Summary);
        }, cancel: cts);
    }

    private static string SummaryText()
    {
        var b = built;
        var d = draft;
        if (b == null || d == null) return "";
        var lines = new List<string>();
        if (b.Ok)
        {
            string what = b.Kind == "battle"
                ? $"{Plural(b.Difficulties.Count, "difficulty", "difficulties")}, {b.Lanes} lanes" + (b.LengthSeconds is double s ? ", " + Clock(s) : "")
                : $"{Plural(b.Songs.Count, "song", "songs")}, {Plural(b.Difficulties.Count, "difficulty", "difficulties")}, {b.Lanes} lanes";
            lines.Add($"{Size(b.Size)} to upload, {Plural(b.Entries, "file", "files")}: {what}.");
            if (b.Kind == "battle") lines.Add("Inside: " + Inside(b.Contents) + ".");
            if (d.Of != null) lines.Add($"This makes v{d.Of.Version + 1} of {d.Of.Title}.");
            lines.Add(builtThumb != null ? "Its card is the listing's picture; it shows after the hub's owner checks it (or after 24 hours)."
                : "The listing shows a title tile" + (b.Kind == "battle" && b.CardBytes == null ? " (the battle has no card picture)." : "."));
        }
        foreach (var problem in sendProblems) lines.Add("The hub said: " + problem);
        if (b.Problems.Count > 0)
        {
            lines.Add("");
            lines.Add("It can't be uploaded because:");
            lines.AddRange(b.Problems.Take(8).Select(p => "- " + p));
            if (b.Problems.Count > 8) lines.Add($"- and {b.Problems.Count - 8} more (see the log)");
        }
        void Section(string title, List<string> items)
        {
            if (items.Count == 0) return;
            lines.Add("");
            lines.Add(title);
            lines.AddRange(items.Take(6).Select(i => "- " + i));
            if (items.Count > 6) lines.Add($"- and {items.Count - 6} more");
        }
        Section("Left out:", b.LeftOut);
        Section("Cleaned:", b.Cleaned);
        Section("Kept as it is:", b.Kept);
        return string.Join("\n", lines);
    }

    private static string RulesText()
    {
        string contact = info?.TakedownContact ?? "";
        string legal = info?.LegalUrl ?? "";
        string ask = contact.Length > 0 ? contact + (legal.Length > 0 ? $" (rules: {legal})" : "") : legal.Length > 0 ? "see " + legal : "see the hub's rules";
        return
            "- Upload only what you made. The charts have to be your own work. A battle made from an osu! beatmap counts only if you mapped it or the mapper allowed it.\n\n" +
            "- Songs, pictures and videos usually belong to someone else. Include them only if you're allowed to share them. " +
            "Many custom battles use commercial songs; if a song's owner asks, the battle is taken down.\n\n" +
            "- Your upload goes live straight away; your picture shows after a check. The hub's owner can remove anything, and players can report entries.\n\n" +
            "- A hub key that has uploads removed for copyright 3 times can't upload any more.\n\n" +
            "- What's stored: the files you upload, the details in the listing, the name you choose, and a scrambled form of your game's hub key. " +
            "No account, e-mail, Steam ID, IP address or Windows user name is stored.\n\n" +
            "- Takedown requests and questions: " + ask;
    }

    /// <summary>The rules are confirmed: the upload is sent (a worker), with its progress on the Sending form.</summary>
    private static void SendUpload()
    {
        var b = built;
        var d = draft;
        if (b == null || !b.Ok || d == null || info == null || api == null || store == null || paths == null) return;
        if (!UploadAllowed(out string why))
        {
            Say(why, 6f);
            return;
        }
        var a = api;
        var s = store;
        var i = info;
        var p = paths;
        var who = identity;
        string name = d.DisplayName;
        var send = new Sending { Title = b.Title, Cts = CancellationTokenSource.CreateLinkedTokenSource(Ct) };
        sending = send;
        sendProblems = new List<string>();
        var details = new HubUploadDetails { Description = d.Description, PackageId = d.Of?.Id, Thumb = builtThumb, DisplayName = name, RightsConfirmed = true };
        send.Transfer.Start(b.Size, "Starting");
        ShowForm(FormKind.Sending);
        ModLog.Info($"Hub: uploading {(d.Of != null ? "a new version of " + d.Of.Id : "a new " + b.Kind)} ({b.Size} bytes).");
        var ct = send.Cts.Token;
        jobs.Run(Task.Run(async () =>
        {
            // The key is made now if there isn't one yet; it's registered with the name at the first upload.
            // The page gets it even when the upload then fails, since it may be registered by then.
            var key = who ?? HubIdentity.LoadOrCreate(p.Identity, name);
            send.Key = key;
            var result = await HubUploadSend.SendAsync(a, s, key, b, details, i, send.Transfer, ct).ConfigureAwait(false);
            return (key, result);
        }, ct), done => Uploaded(send, done.key, done.result), ex => UploadFailed(send, ex));
    }

    private static void Uploaded(Sending send, HubIdentity key, HubUploadResult result)
    {
        if (sending == send) sending = null;
        identity = key;
        identityProblem = null;
        // Sent: its work folder went with it.
        built = null;
        builtThumb = null;
        var d = draft;
        draft = null;
        var c = result.Completed;
        ModLog.Info($"Hub: uploaded {c.PackageId} v{c.Version}{(result.FirstUpload ? " (the key's first upload)" : "")}.");
        tab = Tab.Mine;
        zone = Zone.List;
        pendingMineSelect = c.PackageId;
        mine = null;
        BackToMain();
        LoadMine(force: true);
        // The new entry is on the hub's list within a minute (lists are cached that long).
        string title = send.Title;
        // The key's backup is offered after its first upload, and once after an upload when it never was
        // (a first upload that failed after the key was registered, then went up on a later try).
        if (!result.FirstUpload && (store == null || store.Settings.KeyBackupOffered))
        {
            Say(d?.Of != null ? $"{title} v{c.Version} is on the hub now." : $"{title} is on the hub now.", 9f);
            return;
        }
        KeyBackupOffered();
        ShowPicker(new Picker
        {
            Plain = true,
            Heading = $"{title} is on the hub now",
            Rows = { "Back up my hub key now...", "Later" },
            Hint = _ => "Your uploads are tied to this PC's hub key. Back it up (My uploads > Back up key) so you can still manage them after reinstalling Windows.",
            Choose = choice =>
            {
                BackToMain();
                if (choice == 0) BackUpKey();
            },
            Back = BackToMain,
        });
    }

    // Remembered in Hub\settings.json: the key's backup was offered (or made), so it isn't offered again.
    private static void KeyBackupOffered()
    {
        if (store == null || store.Settings.KeyBackupOffered) return;
        store.Settings.KeyBackupOffered = true;
        SaveSettings();
    }

    private static void UploadFailed(Sending send, Exception ex)
    {
        if (sending == send) sending = null;
        ex = Unwrap(ex);
        if (!IsOpen) return;
        // A key made for this upload is this PC's key now (and may be registered): My uploads and the
        // next try use it, and a registered one isn't asked for a name again.
        if (identity == null && send.Key is { } made)
        {
            identity = made;
            identityProblem = null;
        }
        LogFailure("uploading", ex);
        if (ex is OperationCanceledException)
        {
            ShowForm(FormKind.Summary);
            Say("The upload stopped. Nothing was published.", 6f);
            return;
        }
        if (ex is HubException hub) sendProblems = hub.Problems.ToList();
        ShowForm(FormKind.Summary);
        Say(Words(ex), 12f);
    }

    private static void AskStopUpload()
    {
        var send = sending;
        if (send == null)
        {
            ShowForm(FormKind.Summary);
            return;
        }
        ShowPicker(new Picker
        {
            Plain = true,
            Heading = $"Stop uploading {send.Title}?",
            Rows = { "Keep uploading", "Stop the upload" },
            Hint = i => i == 0 ? "The upload goes on." : "Nothing is published, and what was sent so far is thrown away on the hub.",
            Choose = i =>
            {
                if (i == 1 && sending == send)
                {
                    try { send.Cts.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
                // Back to the progress until the upload has stopped (it tells the hub first).
                if (sending == send) ShowForm(FormKind.Sending);
                else ShowForm(FormKind.Summary);
            },
            Back = () =>
            {
                if (sending == send) ShowForm(FormKind.Sending);
                else ShowForm(FormKind.Summary);
            },
        });
    }
}
