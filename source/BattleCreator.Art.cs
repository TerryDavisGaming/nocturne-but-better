using System.Text.Json;
using System.Text.Json.Nodes;
using UnityEngine;
using static NocturneFlatScroll.EditorUi;

namespace NocturneFlatScroll;

// The enemy's custom art: the Enemy page's "Game enemy's art | Custom art" switch, and the Art
// page. Each animation (idle, attack, hurt, defeat) is first given what it is (Image, GIF, Video
// or Sprite sheet: an Image is one flat picture and is never cut into frames), then its file from
// the Windows picker, which lists only that kind's files, then its settings. Files are checked
// from their first bytes and copied into the battle's art folder. The art is written into the
// draft's battle.json like the enemy's stats, and read back through the battle's own loader
// (EnemyArtReader), so the page shows the problems the arcade would. The live preview and the
// whole enemy's settings are in BattleCreator.ArtPreview.cs.
internal static partial class BattleCreator
{
    private static readonly string[] ArtAnims = EnemyArtReader.Names;

    /// <summary>The animation whose settings show, and which the preview plays.</summary>
    private static string artSelected = "idle";

    // The draft's art as the battle's loader reads it, read again whenever the draft changes.
    private static EnemyArtSpec? artSpec;
    private static readonly List<string> artProblems = new();
    private static string? artUnusable;
    private static string artJson = "";
    private static int artReadAt = -1;
    private static BattleDraft? artReadFor;
    // Each animation's timeline for the rows and steppers, worked out once per read (and per preview load).
    private static readonly Dictionary<string, ArtTimeline?> artTimelines = new();
    // What the picked files' first bytes said (for a picture's size when its animation can't be read).
    private static readonly Dictionary<string, MediaInfo> artMedia = new(StringComparer.OrdinalIgnoreCase);

    // The idle's size was set by the creator, not the player, so a new idle works it out again.
    private static bool artSizeAuto;
    // The attack's hit was filled in by the creator, not the player, so it follows the attack's
    // frames and speed when they change (it's filled in again once they're loaded).
    private static bool artHitAuto;

    /// <summary>Settings to fill in once the preview has loaded a newly chosen file, and what to say then.</summary>
    private sealed class ArtFill
    {
        internal string File = "";
        internal bool Size;
        /// <summary>Only the hit again, after the attack's frames changed: nothing to say.</summary>
        internal bool Quiet;
        internal readonly List<string> Notes = new();
    }

    private static readonly Dictionary<string, ArtFill> artFills = new();

    // The settings' lines move up and down as settings show and hide, so there are no gaps.
    private sealed class FlowLine
    {
        internal Func<bool> Shown = () => true;
        internal readonly List<RectTransform> Rects = new();
        internal float Height = RowStep;
    }

    private static readonly List<FlowLine> artLines = new();
    private static float artLinesTop;

    // ---- the Enemy page's part ------------------------------------------------------------------

    /// <summary>"Game enemy's art" (false) or "Custom art" (true). The art stays in the battle either way.</summary>
    private static void SetArtMode(bool custom)
    {
        if (draft == null || !FinishTyping() || !EnemyEditable() || draft.CustomArt == custom && draft.EnemyMode.Length > 0) return;
        draft.CustomArt = custom;
        string name = LookName();
        if (!custom) Say($"The enemy looks like {name} again. Its custom art stays in the battle, so switching back brings it back.", 6f);
        else if (!draft.HasArt("idle")) Say($"Custom art is on. It fights like {name}; set how it looks on the Art page, starting with its idle.", 6f);
        else Say($"Custom art is on. It fights like {name} and looks like its own art.", 5f);
        ModLog.Info($"Battle creator: the enemy of {draft.Folder} {(custom ? "uses custom art" : "uses the game enemy's art")}.");
    }

    /// <summary>The Enemy page's Art row: which animations are set.</summary>
    private static string ArtRowSummary()
    {
        if (draft == null) return "";
        if (!draft.HasArt("idle")) return $"<color=#F2B02E>no idle yet, so it looks like {Escape(LookName())}</color>   <color=#9D92B4>Edit art...</color>";
        var set = ArtAnims.Where(draft.HasArt).Select(Cap).ToList();
        var unset = ArtAnims.Where(a => !draft.HasArt(a)).Select(Cap).ToList();
        return string.Join(", ", set) + " set" + (unset.Count > 0 ? $" <color=#9D92B4>({string.Join(", ", unset)}: not set)</color>" : "") + "   <color=#9D92B4>Edit art...</color>";
    }

    /// <summary>The Enemy page's warning for a custom-art enemy, or null for none.</summary>
    private static string? ArtWarning()
    {
        if (draft == null || !draft.CustomArt) return null;
        if (EnemyPlaceholders.IsAdvanced(draft.Placeholder)) return $"Scripted bosses can't take custom art; {EnemyChoices.NameOf(EnemyPlaceholders.Default)} fights instead. Pick another enemy above.";
        RefreshArt();
        if (artSpec == null || !artSpec.Animations.ContainsKey("idle"))
            return $"Custom art needs an idle. Until it has one, the enemy looks like {LookName()}.";
        return artProblems.Count > 0 ? ArtProblemText(artProblems[0]) : null;
    }

    private static string LookName() => EnemyChoices.NameOf(draft?.Placeholder);

    private static string Cap(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

    // ---- reading the draft's art --------------------------------------------------------------

    /// <summary>Reads the draft's art again when it changed since the last look (or when <paramref name="force"/>).</summary>
    private static void RefreshArt(bool force = false)
    {
        if (draft == null)
        {
            artSpec = null;
            artReadFor = null;
            return;
        }
        if (!force && artReadFor == draft && artReadAt == draft.Changes) return;
        artReadFor = draft;
        artReadAt = draft.Changes;
        artJson = draft.ArtJson() ?? "";
        artProblems.Clear();
        artTimelines.Clear();
        artSpec = null;
        artUnusable = null;
        if (artJson.Length == 0) return;
        try
        {
            using var doc = JsonDocument.Parse(artJson);
            artSpec = EnemyArtReader.Read(doc.RootElement.Clone(), PackageFiles.Folder(draft.Folder), LookName(), artProblems, new List<string>(),
                out artUnusable, evenWithoutIdle: true);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            artProblems.Add($"the art couldn't be read ({ex.Message})");
        }
    }

    /// <summary>What an animation is: as the loader read it, else as written, else from its file's name.</summary>
    private static ArtKind? ArtKindOf(string anim)
    {
        string? file = draft?.ArtFile(anim);
        if (file == null) return null;
        RefreshArt();
        if (artSpec?.Get(anim) is { } a) return a.Kind;
        if (ArtEditing.Parse(draft!.ArtText(anim, "kind")) is ArtKind written) return written;
        return Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".gif" => ArtKind.Gif,
            ".mp4" or ".m4v" or ".mov" or ".webm" or ".mkv" => ArtKind.Video,
            _ => ArtKind.Image,
        };
    }

    /// <summary>What an animation's file is, from the loader or the file's first bytes; null when it can't be told.</summary>
    private static MediaInfo? ArtMedia(string anim)
    {
        RefreshArt();
        if (artSpec?.Get(anim) is { } a) return a.Media;
        string? file = draft?.ArtFile(anim);
        if (draft == null || file == null) return null;
        if (artMedia.TryGetValue(file, out var known)) return known;
        try
        {
            var files = PackageFiles.Folder(draft.Folder);
            var head = files.ReadHead(file, EnemyArtReader.HeadBytes);
            return artMedia[file] = MediaSniff.Probe(head, files.Length(file) <= head.Length);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>An animation's timeline, when its frames are known (see ArtEditing.Timeline).</summary>
    private static ArtTimeline? ArtTimelineOf(string anim)
    {
        RefreshArt();
        if (artTimelines.TryGetValue(anim, out var known)) return known;
        var a = artSpec?.Get(anim);
        MeasuredAnimation? loaded = null;
        if (previewSet != null && previewSet.Result.Measured.TryGetValue(anim, out var m)) loaded = m;
        return artTimelines[anim] = a == null ? null : ArtEditing.Timeline(a, loaded);
    }

    /// <summary>A problem as the page shows it: "Attack: ..." rather than "the enemy art's attack: ...".</summary>
    private static string ArtProblemText(string problem)
    {
        foreach (var anim in ArtAnims)
        {
            foreach (var start in new[] { $"the enemy art's {anim}", $"the enemy's {anim}" })
                if (problem.StartsWith(start, StringComparison.Ordinal)) return Cap(anim) + problem.Substring(start.Length) + ".";
        }
        if (problem.StartsWith("the enemy art has no idle", StringComparison.Ordinal)) return "No idle yet" + problem.Substring("the enemy art has no idle".Length) + ".";
        return Cap(problem.Replace("the enemy art's ", "").Replace("the art's ", "")) + ".";
    }

    // ---- the page -----------------------------------------------------------------------------

    private static void BuildArtPage()
    {
        const Page p = Page.Art;
        Func<bool> custom = CustomArtEnemy;
        Func<bool> gameArt = () => draft != null && !CustomArtEnemy();

        // "Game enemy's art": only a note and the way to switch.
        float g = 0;
        AddHeader(p, 0, ref g, Col1W, "Art", gameArt);
        var note = AddText(p, 0, ref g, Col1W, 60, () => $"This enemy uses {Escape(LookName())}'s own art. Choose Custom art to use your own pictures, GIFs, videos or sprite sheets.", 19, gameArt);
        note.color = TextColor;
        AddButton(p, 0, g, 300, RowH, "Use custom art", () => SetArtMode(true), gameArt);

        float y = 0;
        AddHeader(p, 0, ref y, Col1W, "Animations", custom);
        foreach (var anim in ArtAnims)
        {
            string a = anim;
            var row = AddButton(p, 0, y, 540, RowH, "", () => SelectArt(a), custom);
            row.Text = () => ArtRowText(a);
            row.Active = () => artSelected == a;
            row.Label.alignment = TextAlignmentOptions.Left;
            row.Label.margin = new Vector4(16, 0, 12, 0);
            row.Label.fontSize = 18;
            AddButton(p, 552, y, 160, RowH, "Choose...", () => ChooseArt(a), custom);
            AddButton(p, 724, y, Col1W - 724, RowH, "Remove", () => RemoveArtAnimation(a), () => custom() && (draft?.HasArt(a) ?? false));
            y -= RowStep;
        }
        var header = MakeText("Selected", pagePanels[p], 15, TextAlignmentOptions.Left);
        header.color = DimText;
        PlaceTop(header.rectTransform, 2, y, Col1W, 20);
        Ui.AddLiveText(header, () => $"{artSelected.ToUpperInvariant()} SETTINGS").Visible = custom;
        y -= 28;
        artLines.Clear();
        artLinesTop = y;
        BuildArtSettings(custom);
        BuildArtPreview(p, custom);
    }

    // The selected animation's settings, a line each; only the lines that apply show.
    private static void BuildArtSettings(Func<bool> custom)
    {
        Func<bool> isSet = () => custom() && ArtKindOf(artSelected) != null;
        Func<ArtKind, bool> kindIs = k => isSet() && ArtKindOf(artSelected) == k;
        const float half = Col1W / 2 - 6, right = Col1W / 2 + 6;

        // What it is: one button per kind. For an animation that isn't set, a kind opens the Windows picker.
        var kinds = ArtLine(custom);
        float w = (Col1W - 3 * 8) / 4f;
        for (int i = 0; i < ArtEditing.Kinds.Length; i++)
        {
            var kind = ArtEditing.Kinds[i];
            var b = LineButton(kinds, i * (w + 8), w, ArtEditing.Label(kind), () => SetArtKind(artSelected, kind));
            b.Active = () => ArtKindOf(artSelected) == kind;
        }
        var unset = ArtLine(() => custom() && ArtKindOf(artSelected) == null, 36);
        LineText(unset, 0, Col1W, 30, () => $"The {artSelected} isn't set. Pick what it is above (or Choose...), then its file." +
            (artSelected == "idle" ? "" : $" Until then {StandInName(artSelected)} shows instead."), 16);

        var grid = ArtLine(() => kindIs(ArtKind.Sheet));
        LineStepper(grid, 0, half, ArtColumnsField, () => $"Columns {Whole(Sel("columns")) ?? "1"}", () => StepGrid(true, -1), () => StepGrid(true, 1));
        LineStepper(grid, right, half, ArtRowsField, () => $"Rows {Whole(Sel("rows")) ?? "1"}", () => StepGrid(false, -1), () => StepGrid(false, 1));
        var cells = ArtLine(() => kindIs(ArtKind.Sheet));
        LineStepper(cells, 0, half, ArtFirstField, () => $"First frame {(int)(Sel("first") ?? 0) + 1}", () => StepFirst(-1), () => StepFirst(1));
        LineStepper(cells, right, half, ArtFramesField, FramesText, () => StepFrames(-1), () => StepFrames(1));

        var timing = ArtLine(() => isSet() && (ArtKindOf(artSelected) != ArtKind.Image || artSelected != "idle"));
        LineStepper(timing, 0, Col1W, ArtTimingField, TimingText, () => StepTiming(-1), () => StepTiming(1), label: TimingLabel);
        var hit = ArtLine(() => isSet() && artSelected == "attack");
        LineStepper(hit, 0, Col1W, ArtHitField, HitText, () => StepHit(-1), () => StepHit(1), label: () => HitByFrame(out _) ? "Hit on frame" : "Hit at (s)");
        var scale = ArtLine(() => isSet() && artSelected != "idle");
        LineStepper(scale, 0, Col1W, ArtScaleField, () => $"Size compared to Idle: {ArtEditing.Percent(Sel("scale") ?? 1)}", () => StepScale(-1), () => StepScale(1));
        // Its own move. The idle's moves it over its shadow (the whole enemy's, in the preview's
        // column, moves the shadow too): a video, or art with no see-through parts, stands on its
        // frame's bottom edge until it's moved down onto the shadow.
        var move = ArtLine(isSet);
        LineStepper(move, 0, half, ArtMoveXField, () => $"{OwnMoveLabel("Sideways")} {ArtEditing.Num(OwnOffset().X)}", () => StepOwnOffset(-1, 0), () => StepOwnOffset(1, 0));
        LineStepper(move, right, half, ArtMoveYField, () => $"{OwnMoveLabel("Up/down")} {ArtEditing.Num(OwnOffset().Y)}", () => StepOwnOffset(0, -1), () => StepOwnOffset(0, 1));

        // A video can't take a see-through colour; turned into frames (BattleCreator.ArtBake.cs) it's a
        // sheet that can. Until a save, the sheet can go back to the video.
        var frames = ArtLine(BakeLineShown);
        LineButton(frames, 0, 260, "", BakeLineClicked).Text = BakeButtonText;
        LineText(frames, 272, Col1W - 272, RowH, BakeLineText, 16);

        var key = ArtLine(() => isSet() && ArtKindOf(artSelected) != ArtKind.Video);
        var keyButton = LineButton(key, 0, half, "", CycleKey);
        keyButton.Text = () => $"See-through colour: {Escape(ArtEditing.KeyName(draft?.ArtText(artSelected, "keyColor")))}";
        keyButton.Active = () => draft?.ArtText(artSelected, "keyColor") != null;
        LineStepper(key, right, half, ArtRangeField, () => $"Range {ArtEditing.Percent(Sel("keyRange") ?? 0.15)}", () => StepRange(-1), () => StepRange(1),
            () => draft?.ArtText(artSelected, "keyColor") != null);

        var toggles = ArtLine(isSet);
        var mirror = LineButton(toggles, 0, half, "", () => ToggleArtBool(artSelected, "flip", "mirrored", "not mirrored"));
        mirror.Text = () => $"Mirror this one: {((draft?.ArtBool(artSelected, "flip") ?? false) ? "on" : "off")}";
        mirror.Active = () => draft?.ArtBool(artSelected, "flip") ?? false;
        var loop = LineButton(toggles, right, half, "", () => ToggleArtBool("defeat", "loop", "loops", "plays once and holds its last frame"), () => artSelected == "defeat");
        loop.Text = () => $"Defeat loops: {((draft?.ArtBool("defeat", "loop") ?? false) ? "on" : "off")}";
        loop.Active = () => draft?.ArtBool("defeat", "loop") ?? false;

        var problems = ArtLine(custom, 150);
        var text = LineText(problems, 0, Col1W, 146, ArtProblemsText, 16);
        text.color = Hex(0xF2B02E);
    }

    private static FlowLine ArtLine(Func<bool> shown, float height = RowStep)
    {
        var line = new FlowLine { Shown = shown, Height = height };
        artLines.Add(line);
        return line;
    }

    private static UiButton LineButton(FlowLine line, float x, float w, string text, Action click, Func<bool>? shown = null)
    {
        Func<bool> visible = shown == null ? line.Shown : () => line.Shown() && shown();
        var b = AddButton(Page.Art, x, 0, w, RowH, text, click, visible);
        b.Label.fontSize = 19;
        line.Rects.Add(b.Rect);
        return b;
    }

    private static TMP_Text LineText(FlowLine line, float x, float w, float h, Func<string> text, float size)
    {
        float y = 0;
        var t = AddText(Page.Art, x, ref y, w, h, text, size, line.Shown);
        line.Rects.Add(t.rectTransform);
        return t;
    }

    /// <summary>"-", the value (click it to type one), and "+". <paramref name="label"/> names what's typed when that depends on the kind.</summary>
    private static void LineStepper(FlowLine line, float x, float w, TextField field, Func<string> text, Action less, Action more, Func<bool>? shown = null,
        Func<string>? label = null)
    {
        Func<bool> visible = shown == null ? line.Shown : () => line.Shown() && shown();
        line.Rects.Add(AddButton(Page.Art, x, 0, 56, RowH, "-", less, visible).Rect);
        var value = AddButton(Page.Art, x + 60, 0, w - 120, RowH, "", () =>
        {
            if (label != null) field.Label = label();
            StartTyping(field);
        }, visible);
        value.Text = () => typing == field ? $"{field.Label}: {Escape(typed)}_" : text();
        value.Active = () => typing == field;
        value.Label.fontSize = 19;
        line.Rects.Add(value.Rect);
        line.Rects.Add(AddButton(Page.Art, x + w - 56, 0, 56, RowH, "+", more, visible).Rect);
    }

    /// <summary>Every frame on the Art page: the settings' lines close up around the ones that show.</summary>
    private static void LayoutArtLines()
    {
        float y = artLinesTop;
        foreach (var line in artLines)
        {
            if (!line.Shown()) continue;
            foreach (var rect in line.Rects)
            {
                if (!rect) continue;
                var at = rect.anchoredPosition;
                if (Math.Abs(at.y - y) > 0.01f) rect.anchoredPosition = new Vector2(at.x, y);
            }
            y -= line.Height;
        }
    }

    private static string ArtRowText(string anim)
    {
        RefreshArt();
        string label = $"<color=#9D92B4>{Cap(anim)}</color><pos=16%>";
        string? file = draft?.ArtFile(anim);
        if (file == null)
            return label + (anim == "idle" ? "<color=#F2B02E>(not set: needed)</color>" : $"<color=#9D92B4>(not set: shows {StandInName(anim)})</color>");
        string what;
        if ((artSpec != null && artSpec.Dropped.ContainsKey(anim)) || PreviewFailure(anim) != null)
            what = "<color=#F2B02E>can't be used</color>";
        else what = artSpec?.Get(anim) is { } a ? Escape(ArtEditing.Summary(a, ArtTimelineOf(anim))) : "";
        return $"{label}{Escape(Path.GetFileName(file))}   <color=#9D92B4>{what}</color>";
    }

    /// <summary>What shows while an animation isn't set, as the battle does it.</summary>
    private static string StandInName(string anim) => anim switch
    {
        "defeat" => draft?.HasArt("hurt") == true ? "Hurt" : "Idle",
        _ => "Idle",
    };

    /// <summary>
    /// Why the preview's load of the draft's art as it is now can't use an animation: its file, or
    /// a video that can't play (an error, or not ready in time). Null when it's fine or not loaded.
    /// </summary>
    private static string? PreviewFailure(string anim)
    {
        var set = previewSet;
        if (set == null || previewJson != artJson) return null;
        if (set.Result.Failed.TryGetValue(anim, out var why)) return why;
        if (set.Clips.TryGetValue(anim, out var clip) && clip.Video is { Failed: true } v)
            return $"{v.File} can't play ({v.Error ?? "it wasn't ready in time"})";
        return null;
    }

    private static string ArtProblemsText()
    {
        RefreshArt();
        var lines = artProblems.Select(ArtProblemText).ToList();
        var set = previewSet;
        if (set != null && previewJson == artJson)
        {
            foreach (var anim in ArtAnims)
                if (PreviewFailure(anim) is { } why) lines.Add($"{Cap(anim)} can't be used: {why}.");
            foreach (var note in set.Result.Notes) lines.Add(ArtProblemText(note));
        }
        // The selected animation's first.
        string mine = Cap(artSelected);
        var ordered = lines.Distinct().OrderBy(l => l.StartsWith(mine, StringComparison.Ordinal) ? 0 : 1).Take(5);
        return string.Join("\n", ordered.Select(Escape));
    }

    // ---- choosing files -----------------------------------------------------------------------

    private static void SelectArt(string anim)
    {
        if (!FinishTyping()) return;
        artSelected = anim;
    }

    private static FileDialogs.Purpose PurposeFor(ArtKind kind) => kind switch
    {
        ArtKind.Image => FileDialogs.Purpose.EnemyImage,
        ArtKind.Gif => FileDialogs.Purpose.EnemyGif,
        ArtKind.Video => FileDialogs.Purpose.EnemyVideo,
        _ => FileDialogs.Purpose.EnemySheet,
    };

    /// <summary>Asks what an animation is (the four kinds), then opens the Windows picker for that kind's files.</summary>
    private static void ChooseArt(string anim)
    {
        if (draft == null || !FinishTyping() || !EnemyEditable()) return;
        artSelected = anim;
        var kinds = ArtEditing.Kinds;
        int current = ArtKindOf(anim) is ArtKind k ? Array.IndexOf(kinds, k) : 0;
        ShowPicker(new Picker
        {
            Heading = $"What is the {anim}?",
            Rows = kinds.Select(kind => $"{ArtEditing.Label(kind)}: {ArtEditing.Hint(kind)}").ToList(),
            Hint = i => (i >= 0 && i < kinds.Length && kinds[i] == ArtKind.Image ? "An image stays one flat picture; it's never cut into frames. " : "") +
                        "Then choose the file in the window that opens.  Esc goes back.",
            Index = Math.Max(0, current),
            Choose = i => PickArtFile(anim, kinds[i]),
            Back = BackFromPicker,
        });
    }

    /// <summary>Opens the Windows picker for an animation, listing only the files <paramref name="kind"/> takes.</summary>
    private static void PickArtFile(string anim, ArtKind kind)
    {
        if (draft == null || !EnemyEditable()) return;
        var d = draft;
        artSelected = anim;
        Run(FileDialogs.Open(PurposeFor(kind), ArtEditing.PickerTitle(anim, kind)), "Choose a file in the window that opened...", path => ArtFileChosen(d, anim, kind, path));
    }

    /// <summary>The file picked for an animation: checked against its kind and the limits, copied into the battle's art folder, and set.</summary>
    private static void ArtFileChosen(BattleDraft d, string anim, ArtKind kind, string? path)
    {
        if (path == null || draft != d) return;
        string folder = d.Folder;
        Run(Task.Run(() => AddArtFile(folder, anim, kind, path)), "Copying the file...", added => ArtFileAdded(d, anim, kind, path, added));
    }

    private sealed class AddedArt
    {
        internal string? Problem;
        internal string Path = "";
        internal bool Copied;
        internal MediaInfo? Info;
        /// <summary>A sheet's file, for the grid guess.</summary>
        internal byte[]? Bytes;
    }

    // On a worker: the file's first bytes are checked before anything is copied.
    private static AddedArt AddArtFile(string folder, string anim, ArtKind kind, string source)
    {
        var file = new FileInfo(source);
        if (!file.Exists) return new AddedArt { Problem = $"{file.Name} is missing." };
        byte[] head;
        using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            head = ReadHead(stream, (int)Math.Min(EnemyArtReader.HeadBytes, file.Length));
        string? problem = ArtEditing.CheckPicked(anim, kind, file.Name, head, file.Length, out var info);
        if (problem == null && info?.Video is { IndexFound: false })
        {
            // An MP4 whose index comes after its start: the probe reads only the index from the whole file.
            try
            {
                using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                info.Video = VideoProbe.Read(stream);
                (info.Width, info.Height) = (info.Video.Width, info.Video.Height);
                problem = ArtEditing.CheckVideo(anim, file.Name, info.Video);
            }
            catch (InvalidDataException ex) { problem = $"{file.Name} {ex.Message}."; }
        }
        if (problem != null || info == null) return new AddedArt { Problem = problem ?? $"{file.Name} can't be read." };
        var (path, copied) = BattleFiles.AddFile(folder, source, "art", info.IsVideo ? EnemyArtReader.MaxVideoBytes : EnemyArtReader.MaxPictureBytes);
        byte[]? bytes = kind == ArtKind.Sheet ? File.ReadAllBytes(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar))) : null;
        return new AddedArt { Path = path, Copied = copied, Info = info, Bytes = bytes };
    }

    private static byte[] ReadHead(Stream stream, int count)
    {
        var buffer = new byte[Math.Max(0, count)];
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return total == buffer.Length ? buffer : buffer[..total];
    }

    private static void ArtFileAdded(BattleDraft d, string anim, ArtKind kind, string source, AddedArt added)
    {
        if (draft != d) return;
        if (added.Problem != null)
        {
            ModLog.Info($"Battle creator: {source} wasn't used for the {anim}: {added.Problem}");
            Say(added.Problem, 8f);
            return;
        }
        if (!EnemyEditable()) return;
        if (added.Copied) touched.Add(added.Path);
        // The old file goes after a save if nothing names it then (another animation may still use it).
        string? old = d.ArtFile(anim);
        if (old != null && !old.Equals(added.Path, StringComparison.OrdinalIgnoreCase)) touched.Add(old);
        bool size = anim == "idle" && (artSizeAuto || d.ArtNumber("", "size") == null);
        d.SetArtAnimation(anim, added.Path, ArtEditing.Json(kind));
        // A new idle's size is worked out again from its first frame once the preview has it.
        if (size) d.SetArtValue("", "size", null);
        if (added.Info != null) artMedia[added.Path] = added.Info;
        artSelected = anim;
        var fill = new ArtFill { File = added.Path, Size = size };
        foreach (var note in added.Info?.Notes ?? new List<string>()) fill.Notes.Add($"{Path.GetFileName(added.Path)} {note}.");
        artFills[anim] = fill;
        ModLog.Info($"Battle creator: {anim} art {added.Path} ({ArtEditing.Json(kind)}) for {d.Folder} (from {source}).");
        Say(d.HasArt("idle") ? $"{Cap(anim)}: reading {Path.GetFileName(added.Path)}..." : $"{Cap(anim)} set. Set the idle too: the preview and the battle need it.", 4f);
        if (kind == ArtKind.Sheet) SetUpSheet(d, anim, added.Path, added.Bytes, fill);
    }

    /// <summary>
    /// A sheet's starting grid: another animation's grid when it cuts the same file (starting after
    /// its frames), else the guess from the clear lines between the frames. Either way it's only a
    /// starting value; the player changes it with Columns and Rows.
    /// </summary>
    private static void SetUpSheet(BattleDraft d, string anim, string file, byte[]? bytes, ArtFill fill)
    {
        foreach (var other in ArtAnims)
        {
            if (other == anim || !file.Equals(d.ArtFile(other), StringComparison.OrdinalIgnoreCase) || ArtKindOf(other) != ArtKind.Sheet) continue;
            int columns = (int)(d.ArtNumber(other, "columns") ?? 1), rows = (int)(d.ArtNumber(other, "rows") ?? 1);
            int first = (int)(d.ArtNumber(other, "first") ?? 0);
            int frames = (int)(d.ArtNumber(other, "frames") ?? ArtTimelineOf(other)?.SourceFrames ?? 1);
            WriteGrid(d, anim, columns, rows, Math.Min(first + frames, columns * rows - 1), null);
            fill.Notes.Add($"Same sheet as {Cap(other)}: pick this animation's frames with First frame and Frames.");
            return;
        }
        Picture picture;
        try { picture = SheetPicture(d, file, bytes); }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            fill.Notes.Add($"Its grid couldn't be guessed ({ex.Message}); set it with Columns and Rows.");
            return;
        }
        Run(Task.Run(() => SheetGuess.Guess(picture)), "Looking at the sheet...", guess =>
        {
            if (draft != d || !file.Equals(d.ArtFile(anim), StringComparison.OrdinalIgnoreCase) || !EnemyEditable()) return;
            WriteGrid(d, anim, guess.Columns, guess.Rows, guess.First, guess.Frames);
            int cw = picture.Width / guess.Columns, ch = picture.Height / guess.Rows;
            fill.Notes.Add(guess.Columns * guess.Rows > 1
                ? $"Its grid looks like {guess.Columns} x {guess.Rows} (frames of {cw} x {ch}); change Columns and Rows if that's wrong."
                : "Set its grid with Columns and Rows (frames go left to right, then down).");
            ModLog.Info($"Battle creator: the sheet {file} looks like {guess.Columns} x {guess.Rows}, frames {guess.First + 1} to {guess.First + guess.Frames}.");
        });
    }

    // Decoded with Unity's own decoder (its size was checked from its header), and kept for the preview.
    private static Picture SheetPicture(BattleDraft d, string file, byte[]? bytes)
    {
        var files = PackageFiles.Folder(d.Folder);
        string key = file + "|" + files.Stamp(file);
        lock (previewPictures)
            if (previewPictures.TryGetValue(key, out var known)) return known;
        var picture = EnemyArt.ReadPicture(bytes ?? files.ReadAllBytes(file, EnemyArtReader.MaxPictureBytes), file);
        lock (previewPictures) previewPictures[key] = picture;
        return picture;
    }

    private static void WriteGrid(BattleDraft d, string anim, int columns, int rows, int first, int? frames)
    {
        d.SetArtValue(anim, "columns", BattleDraft.ArtNumberNode(columns));
        d.SetArtValue(anim, "rows", BattleDraft.ArtNumberNode(rows));
        d.SetArtValue(anim, "first", BattleDraft.ArtNumberNode(first));
        d.SetArtValue(anim, "frames", BattleDraft.ArtNumberNode(frames));
    }

    // Keys that only one of a picture's two kinds reads.
    private static readonly string[] SheetOnlyKeys = { "columns", "rows", "first", "frames", "fps", "times", "hitFrame" };
    private static readonly string[] ImageOnlyKeys = { "seconds", "hitTime" };

    /// <summary>
    /// The kind buttons. The same picture can switch between Image and Sprite sheet in place;
    /// any other kind needs its own file, so the picker opens (also for an animation not set yet).
    /// </summary>
    private static void SetArtKind(string anim, ArtKind kind)
    {
        if (draft == null || !FinishTyping() || !EnemyEditable()) return;
        var d = draft;
        artSelected = anim;
        var current = ArtKindOf(anim);
        string? file = d.ArtFile(anim);
        if (current == kind) return;
        if (file == null || current is not (ArtKind.Image or ArtKind.Sheet) || kind is not (ArtKind.Image or ArtKind.Sheet))
        {
            PickArtFile(anim, kind);
            return;
        }
        d.SetArtValue(anim, "kind", JsonValue.Create(ArtEditing.Json(kind)));
        foreach (var key in kind == ArtKind.Image ? SheetOnlyKeys : ImageOnlyKeys) d.SetArtValue(anim, key, null);
        if (kind == ArtKind.Image)
        {
            artFills.Remove(anim);
            if (anim == "attack") artFills[anim] = new ArtFill { File = file, Quiet = true };
            Say($"{Cap(anim)} is one still image now: {Path.GetFileName(file)} shows as it is.", 5f);
            return;
        }
        var fill = new ArtFill { File = file };
        fill.Notes.Add($"{Cap(anim)} is a sprite sheet now.");
        artFills[anim] = fill;
        SetUpSheet(d, anim, file, null, fill);
    }

    private static void RemoveArtAnimation(string anim)
    {
        if (draft == null || !FinishTyping() || !EnemyEditable() || !draft.HasArt(anim)) return;
        var d = draft;
        if (anim == "idle")
        {
            ShowPicker(Confirm("Remove the idle?", "Keep it", "Remove it", $"Without an idle the enemy looks like {LookName()}.", () => DropArt(d, anim), BackFromPicker));
            return;
        }
        DropArt(d, anim);
    }

    private static void DropArt(BattleDraft d, string anim)
    {
        if (draft != d || !EnemyEditable()) return;
        string? file = d.ArtFile(anim);
        if (file != null) touched.Add(file);
        d.RemoveArt(anim);
        artFills.Remove(anim);
        Say($"{Cap(anim)} removed. Save to keep it that way.", 4f);
        ModLog.Info($"Battle creator: removed the {anim} art ({file}) of {d.Folder}.");
    }

    // ---- filling in once the preview has the file ---------------------------------------------

    /// <summary>
    /// A newly chosen file is loaded: the creator fills in what it works out from its frames (a
    /// new idle's size and a move down for tall art, the attack's hit, a scale to match the
    /// idle) and says what was set.
    /// </summary>
    private static void ApplyArtFills(EnemyArt.ArtSet set)
    {
        if (draft == null || artFills.Count == 0) return;
        var d = draft;
        var r = set.Result;
        var said = new List<string>();
        foreach (var anim in ArtAnims)
        {
            if (!artFills.TryGetValue(anim, out var fill)) continue;
            // The choice was replaced since, or the loaded art is older than it.
            if (!fill.File.Equals(d.ArtFile(anim), StringComparison.OrdinalIgnoreCase) || set.Spec.Get(anim) is not { } loaded
                || !loaded.File.Equals(fill.File, StringComparison.OrdinalIgnoreCase))
            {
                if (!fill.File.Equals(d.ArtFile(anim), StringComparison.OrdinalIgnoreCase)) artFills.Remove(anim);
                continue;
            }
            artFills.Remove(anim);
            string? failed = r.Failed.TryGetValue(anim, out var why) ? why
                : set.Clips.TryGetValue(anim, out var clip) && clip.Video is { Failed: true } video ? $"{video.File} can't play ({video.Error ?? "it wasn't ready in time"})" : null;
            if (!r.Measured.TryGetValue(anim, out var m) || failed != null)
            {
                said.Add($"{Cap(anim)} can't be used: {failed ?? "it couldn't be read"}.");
                continue;
            }
            if (!EnemyEditable()) return;
            var notes = new List<string>(fill.Notes);
            if (anim == "idle" && fill.Size && d.ArtNumber("", "size") == null)
            {
                double size = ArtEditing.IdleSize(m);
                d.SetArtValue("", "size", BattleDraft.ArtNumberNode(size));
                artSizeAuto = true;
                double down = ArtEditing.TallOffset(m, size);
                var offset = d.ArtPair("", "offset") ?? (0, 0);
                if (down < 0 && offset.Y == 0)
                {
                    d.SetArtValue("", "offset", BattleDraft.ArtPairNode((offset.X, down)));
                    notes.Add($"It's tall, so it's moved down {ArtEditing.Num(-down)} px to stay on screen.");
                }
            }
            if (!fill.Quiet && anim != "idle" && r.Measured.TryGetValue("idle", out var idle) && d.ArtNumber(anim, "scale") == null
                && ArtEditing.MatchScale(idle.FrameH, m.FrameH) is double scale)
            {
                d.SetArtValue(anim, "scale", BattleDraft.ArtNumberNode(scale));
                notes.Add($"Its frames are {ArtEditing.Num((double)m.FrameH / idle.FrameH)}x the idle's height, so they're resized to match; change it with Size compared to Idle.");
            }
            if (anim == "attack" && d.ArtNumber(anim, "hitFrame") == null && d.ArtNumber(anim, "hitTime") == null)
            {
                var (frame, time) = ArtEditing.HitOf(m.Spec, m.Timeline);
                if (frame is int f) d.SetArtValue(anim, "hitFrame", BattleDraft.ArtNumberNode(f));
                else if (time is double t) d.SetArtValue(anim, "hitTime", BattleDraft.ArtNumberNode(t));
                artHitAuto = true;
            }
            if (fill.Quiet)
            {
                RefreshArt(force: true);
                continue;
            }
            if (m.Video == null && !m.FeetFound && !m.Spec.HasKey)
                notes.Add("It has no see-through parts, so it shows as a rectangle; to cut out a flat background, turn on See-through colour: corner colour.");
            else if (m.Video is { Alpha: false })
                notes.Add("A video has no see-through parts; Turn into frames makes it a sprite sheet that can cut out a flat background.");
            RefreshArt(force: true);
            string summary = artSpec?.Get(anim) is { } a ? ArtEditing.Summary(a, ArtTimelineOf(anim)) : ArtEditing.Label(ArtKindOf(anim) ?? ArtKind.Image);
            said.Add($"{Cap(anim)} set: {summary}.{(notes.Count > 0 ? " " + string.Join(" ", notes) : "")}");
        }
        if (said.Count > 0) Say(string.Join(" ", said) + " Save to keep it.", 9f);
    }

    // ---- the settings -------------------------------------------------------------------------

    /// <summary>The draft, when the enemy can be changed; says why not otherwise.</summary>
    private static bool ArtEditable(out BattleDraft d)
    {
        d = draft!;
        return draft != null && EnemyEditable() && draft.HasArt(artSelected);
    }

    private static double? Sel(string key) => draft?.ArtNumber(artSelected, key);

    private static string? Whole(double? value) => value is double v ? ArtEditing.Num(v) : null;

    private static void SetSel(string key, double? value)
    {
        if (!ArtEditable(out var d)) return;
        d.SetArtValue(artSelected, key, BattleDraft.ArtNumberNode(value));
        if (key is not ("scale" or "keyRange")) RefillHit();
    }

    /// <summary>
    /// The attack's frames or speed changed: a hit the creator filled in goes back to automatic
    /// and is filled in again once the new frames are loaded. A hit the player set stays.
    /// </summary>
    private static void RefillHit()
    {
        if (artSelected != "attack" || !artHitAuto || draft?.ArtFile("attack") is not string file) return;
        draft.SetArtValue("attack", "hitFrame", null);
        draft.SetArtValue("attack", "hitTime", null);
        if (!artFills.ContainsKey("attack")) artFills["attack"] = new ArtFill { File = file, Quiet = true };
    }

    private static int Cells() => (int)(Sel("columns") ?? 1) * (int)(Sel("rows") ?? 1);

    private static void StepGrid(bool columns, int by)
    {
        int c = (int)(Sel("columns") ?? 1), r = (int)(Sel("rows") ?? 1);
        if (columns) c += by;
        else r += by;
        SetGrid(c, r);
    }

    private static void SetGrid(int columns, int rows)
    {
        if (!ArtEditable(out var d)) return;
        columns = Math.Clamp(columns, 1, EnemyArtReader.MaxGrid);
        rows = Math.Clamp(rows, 1, EnemyArtReader.MaxGrid);
        if (ArtMedia(artSelected) is { Width: > 0 } media && (media.Width / columns < EnemyArtReader.MinCell || media.Height / rows < EnemyArtReader.MinCell))
        {
            Say($"That makes frames smaller than {EnemyArtReader.MinCell} px: the sheet is {media.Width} x {media.Height}.", 4f);
            return;
        }
        d.SetArtValue(artSelected, "columns", BattleDraft.ArtNumberNode(columns));
        d.SetArtValue(artSelected, "rows", BattleDraft.ArtNumberNode(rows));
        // First frame and Frames stay inside the grid.
        int cells = columns * rows, first = (int)(Sel("first") ?? 0);
        if (first > cells - 1) d.SetArtValue(artSelected, "first", BattleDraft.ArtNumberNode(first = cells - 1));
        if (Sel("frames") is double frames && frames > cells - first) d.SetArtValue(artSelected, "frames", BattleDraft.ArtNumberNode(cells - first));
        RefillHit();
    }

    private static void StepFirst(int by) => SetFirst((int)(Sel("first") ?? 0) + by);

    private static void SetFirst(int first)
    {
        if (!ArtEditable(out var d)) return;
        int cells = Cells();
        first = Math.Clamp(first, 0, cells - 1);
        d.SetArtValue(artSelected, "first", BattleDraft.ArtNumberNode(first));
        if (Sel("frames") is double frames && frames > cells - first) d.SetArtValue(artSelected, "frames", BattleDraft.ArtNumberNode(cells - first));
        RefillHit();
    }

    // The frames when "frames" isn't set: up to the last cell with anything in it (as loaded), else the rest of the grid.
    private static int AutoFrames()
    {
        int first = (int)(Sel("first") ?? 0);
        return ArtTimelineOf(artSelected)?.SourceFrames ?? Math.Max(1, Cells() - first);
    }

    private static string FramesText() => Sel("frames") is double f ? $"Frames {ArtEditing.Num(f)}" : $"Frames {AutoFrames()} (to the last)";

    private static void StepFrames(int by) => SetFrames((int)(Sel("frames") ?? AutoFrames()) + by);

    private static void SetFrames(int frames) => SetSel("frames", Math.Clamp(frames, 1, Math.Max(1, Cells() - (int)(Sel("first") ?? 0))));

    // Timing: a sheet's frames a second, a GIF's or video's speed, a still's seconds.
    private static string TimingText()
    {
        switch (ArtKindOf(artSelected))
        {
            case ArtKind.Sheet:
                if (TimesWritten()) return "Frame times from battle.json (- or + sets a speed)";
                double fps = Sel("fps") ?? ArtTimeline.DefaultSheetFps;
                return $"{ArtEditing.Num(fps)} frames a second ({ArtEditing.Num(Math.Round(1000 / fps))} ms each)";
            case ArtKind.Gif:
                return $"Speed {ArtEditing.Percent(Sel("speed") ?? 1)}" + (Sel("fps") is double gifFps ? $" ({ArtEditing.Num(gifFps)} frames a second)" : " (the GIF's own timing)");
            case ArtKind.Video:
                return $"Speed {ArtEditing.Percent(Sel("speed") ?? 1)}";
            default:
                return $"Shows for {ArtEditing.Num(Sel("seconds") ?? ArtTimeline.StillSeconds)} s";
        }
    }

    // Per-frame times written by hand (the creator itself writes a speed).
    private static bool TimesWritten()
    {
        RefreshArt();
        return artSpec?.Get(artSelected)?.Times != null;
    }

    /// <summary>The timing as it's typed: a sheet's frames a second, a GIF's or video's speed in %, a still's seconds.</summary>
    private static string TimingValue() => ArtKindOf(artSelected) switch
    {
        ArtKind.Sheet => ArtEditing.Num(Sel("fps") ?? ArtTimeline.DefaultSheetFps),
        ArtKind.Gif or ArtKind.Video => ArtEditing.Num((Sel("speed") ?? 1) * 100),
        _ => ArtEditing.Num(Sel("seconds") ?? ArtTimeline.StillSeconds),
    };

    private static string TimingLabel() => ArtKindOf(artSelected) switch
    {
        ArtKind.Sheet => "Frames a second",
        ArtKind.Gif or ArtKind.Video => "Speed (%)",
        _ => "Seconds",
    };

    private static void StepTiming(int by)
    {
        if (!ArtEditable(out var d)) return;
        switch (ArtKindOf(artSelected))
        {
            case ArtKind.Sheet:
                double fps = Sel("fps") ?? ArtTimeline.DefaultSheetFps;
                if (TimesWritten())
                {
                    // Written frame times give way to a speed near their average.
                    var t = ArtTimelineOf(artSelected);
                    if (t != null && t.Length > 0 && t.SourceFrames > 0) fps = Math.Round(t.SourceFrames / t.Length);
                    d.SetArtValue(artSelected, "times", null);
                    by = 0;
                    RefillHit();
                }
                SetSel("fps", Math.Clamp(fps + by, 1, 60));
                break;
            case ArtKind.Gif:
            case ArtKind.Video:
                double speed = ArtEditing.Step(ArtEditing.SpeedSteps, Sel("speed") ?? 1, by);
                SetSel("speed", Math.Abs(speed - 1) < 1e-9 ? null : speed);
                break;
            default:
                SetSel("seconds", Math.Clamp(Math.Round((Sel("seconds") ?? ArtTimeline.StillSeconds) + by * 0.1, 2), 0.05, 10));
                break;
        }
    }

    // A typed timing; a value out of its kind's range is refused (the field stays open with why).
    private static void SetTiming(double? value)
    {
        switch (ArtKindOf(artSelected))
        {
            case ArtKind.Sheet:
                if (value is < 1 or > 60) throw new InvalidDataException("Frames a second has to be between 1 and 60.");
                if (value != null && ArtEditable(out var d)) d.SetArtValue(artSelected, "times", null);
                SetSel("fps", value);
                break;
            case ArtKind.Gif:
            case ArtKind.Video:
                if (value is < 10 or > 1000) throw new InvalidDataException("The speed has to be between 10% and 1000%.");
                SetSel("speed", value is double percent && Math.Abs(percent - 100) > 1e-9 ? percent / 100 : null);
                break;
            default:
                if (value is < 0.05 or > 10) throw new InvalidDataException("A still shows for 0.05 to 10 seconds.");
                SetSel("seconds", value);
                break;
        }
    }

    // The attack's hit: on a frame for sheets and GIFs, at a time for stills and videos.
    private static bool HitByFrame(out ArtTimeline? t)
    {
        t = ArtTimelineOf("attack");
        return ArtKindOf("attack") is ArtKind.Sheet or ArtKind.Gif && t != null && t.SourceFrames > 1;
    }

    private static string HitText()
    {
        bool auto = draft?.ArtNumber("attack", "hitFrame") == null && draft?.ArtNumber("attack", "hitTime") == null;
        string how = auto ? ", automatic" : "";
        if (HitByFrame(out var t)) return $"Hit on frame {t!.HitFrame + 1} of {t.SourceFrames} ({ArtEditing.Sec(t.Hit)} s{how})";
        return t != null && t.Hit >= 0 ? $"Hit at {ArtEditing.Sec(t.Hit)} s (parry from {ArtEditing.Sec(t.Parry)} s{how})" : "Hit: when the attack's frames are read";
    }

    // A time hit is shown, typed and stepped in the battle's seconds (after a video's speed).
    private static string HitValue()
    {
        if (HitByFrame(out var t)) return (t!.HitFrame + 1).ToString();
        return t != null && t.Hit >= 0 ? ArtEditing.Sec(t.Hit) : "";
    }

    private static void StepHit(int by)
    {
        if (!ArtEditable(out var d)) return;
        if (HitByFrame(out var t)) SetHitFrame(t!.HitFrame + by);
        else if (t != null) SetHitAt(t.Hit + by * 0.05);
        else Say("The attack's frames aren't read yet.", 3f);
    }

    private static void SetHitFrame(int frame)
    {
        if (!ArtEditable(out var d) || !HitByFrame(out var t)) return;
        artHitAuto = false;
        artFills.Remove("attack");
        d.SetArtValue("attack", "hitFrame", BattleDraft.ArtNumberNode(Math.Clamp(frame, 0, t!.SourceFrames - 1)));
        d.SetArtValue("attack", "hitTime", null);
    }

    /// <summary>
    /// The hit <paramref name="seconds"/> into the attack in the battle (the time the page shows);
    /// battle.json keeps it in the file's own seconds (a video's before its speed).
    /// </summary>
    private static void SetHitAt(double seconds)
    {
        if (!ArtEditable(out var d)) return;
        var a = artSpec?.Get("attack");
        if (a == null)
        {
            Say("The attack can't be read yet, so its hit can't be set.", 3f);
            return;
        }
        artHitAuto = false;
        artFills.Remove("attack");
        d.SetArtValue("attack", "hitTime", BattleDraft.ArtNumberNode(ArtEditing.HitTimeAt(a, seconds, ArtTimelineOf("attack"))));
        d.SetArtValue("attack", "hitFrame", null);
    }

    private static void StepScale(int by)
    {
        double scale = ArtEditing.Step(ArtEditing.ScaleSteps, Sel("scale") ?? 1, by);
        SetSel("scale", Math.Abs(scale - 1) < 1e-9 ? null : scale);
    }

    private static (double X, double Y) OwnOffset() => draft?.ArtPair(artSelected, "offset") ?? (0, 0);

    // The idle's own move is over its shadow; another animation's is its own.
    private static string OwnMoveLabel(string what) => artSelected == "idle" ? "On shadow: " + what.ToLowerInvariant() : what;

    private static void StepOwnOffset(int x, int y)
    {
        var (ox, oy) = OwnOffset();
        SetOwnOffset(ox + x, oy + y);
    }

    private static void SetOwnOffset(double x, double y)
    {
        if (!ArtEditable(out var d)) return;
        x = Math.Clamp(Math.Round(x), -EnemyArtReader.MaxOffset, EnemyArtReader.MaxOffset);
        y = Math.Clamp(Math.Round(y), -EnemyArtReader.MaxOffset, EnemyArtReader.MaxOffset);
        d.SetArtValue(artSelected, "offset", x == 0 && y == 0 ? null : BattleDraft.ArtPairNode((x, y)));
    }

    private static void CycleKey()
    {
        if (!ArtEditable(out var d)) return;
        string? next = ArtEditing.NextKey(d.ArtText(artSelected, "keyColor"));
        d.SetArtValue(artSelected, "keyColor", next == null ? null : JsonValue.Create(next));
        if (next == null) d.SetArtValue(artSelected, "keyRange", null);
        // A sheet made from a video on a flat background (a green screen): its corner colour takes a
        // range wide enough for all of that background.
        else if (next == "corner" && Sel("keyRange") == null && d.ArtFile(artSelected) is { } file && bakedBackgrounds.TryGetValue(file, out var background)
                 && background.Range > 0.15 + 1e-9)
            d.SetArtValue(artSelected, "keyRange", BattleDraft.ArtNumberNode(background.Range));
        Say(next == null ? "See-through colour off." : $"See-through colour: {ArtEditing.KeyName(next)}. Pixels near it become see-through; Range sets how near.", 4f);
    }

    private static void StepRange(int by) => SetSel("keyRange", Math.Clamp(Math.Round((Sel("keyRange") ?? 0.15) + by * 0.05, 2), 0, 1));

    private static void ToggleArtBool(string anim, string key, string on, string off)
    {
        if (draft == null || !EnemyEditable() || !draft.HasArt(anim)) return;
        bool now = !(draft.ArtBool(anim, key) ?? false);
        draft.SetArtValue(anim, key, now ? JsonValue.Create(true) : null);
        Say($"{Cap(anim)} {(now ? on : off)}.", 3f);
    }

    // ---- typed values (click a stepper's value to type one) ------------------------------------

    private static TextField ArtField(string label, Func<string> get, Action<double?> set, double min, double max, string hint, bool optional = false)
    {
        var field = new TextField { Label = label, Max = 10, Enemy = true, Get = get, Hint = hint };
        field.Set = text =>
        {
            double? value = ParseNumber(text, min, max, field.Label);
            if (value == null && !optional) throw new InvalidDataException($"{field.Label} needs a number, like {Num(Math.Max(min, 1))}.");
            set(value);
        };
        return field;
    }

    private static readonly TextField ArtColumnsField = ArtField("Columns", () => Whole(Sel("columns")) ?? "1", v => SetGrid((int)v!.Value, (int)(Sel("rows") ?? 1)),
        1, EnemyArtReader.MaxGrid, "Type how many frames go across the sheet, then Enter. Esc cancels.");
    private static readonly TextField ArtRowsField = ArtField("Rows", () => Whole(Sel("rows")) ?? "1", v => SetGrid((int)(Sel("columns") ?? 1), (int)v!.Value),
        1, EnemyArtReader.MaxGrid, "Type how many rows of frames the sheet has, then Enter. Esc cancels.");
    private static readonly TextField ArtFirstField = ArtField("First frame", () => ((int)(Sel("first") ?? 0) + 1).ToString(), v => SetFirst((int)v!.Value - 1),
        1, EnemyArtReader.MaxGrid * EnemyArtReader.MaxGrid, "Type the frame this animation starts on, counting from 1 at the top left, then Enter. Esc cancels.");
    private static readonly TextField ArtFramesField = ArtField("Frames", () => Whole(Sel("frames")) ?? AutoFrames().ToString(), v => { if (v is double f) SetFrames((int)f); else SetSel("frames", null); },
        1, EnemyArtReader.MaxGrid * EnemyArtReader.MaxGrid, "Type how many frames it has, then Enter. Empty goes to the last frame with anything in it. Esc cancels.", optional: true);
    private static readonly TextField ArtTimingField = ArtField("Speed", TimingValue, SetTiming, 0.05, 1000,
        "Type a sheet's frames a second, a GIF's or video's speed in % (100 is its own), or a still's seconds, then Enter. Esc cancels.", optional: true);
    private static readonly TextField ArtHitField = ArtField("Hit", HitValue, v =>
        {
            if (v == null)
            {
                if (ArtEditable(out var d))
                {
                    // Automatic: the creator fills it in again from the attack's frames.
                    d.SetArtValue("attack", "hitFrame", null);
                    d.SetArtValue("attack", "hitTime", null);
                    artHitAuto = true;
                    artFills["attack"] = new ArtFill { File = d.ArtFile("attack")!, Quiet = true };
                }
            }
            else if (HitByFrame(out var t))
            {
                int frames = t!.SourceFrames;
                if (v < 1 || v > frames) throw new InvalidDataException($"The attack has frames 1 to {frames}.");
                SetHitFrame((int)v.Value - 1);
            }
            else SetHitAt(v.Value);
        }, 0, 600, "Type the frame the hit lands on (a sheet or GIF), or how many seconds into the attack it lands in the battle, then Enter. Empty puts it back to automatic. Esc cancels.", optional: true);
    private static readonly TextField ArtScaleField = ArtField("Size compared to Idle (%)", () => ArtEditing.Num((Sel("scale") ?? 1) * 100), v => SetSel("scale", v is double p && Math.Abs(p - 100) > 1e-9 ? p / 100 : null),
        5, 2000, "Type the size compared to the idle in %, then Enter. Esc cancels.", optional: true);
    private static readonly TextField ArtMoveXField = ArtField("Sideways", () => ArtEditing.Num(OwnOffset().X), v => SetOwnOffset(v ?? 0, OwnOffset().Y),
        -EnemyArtReader.MaxOffset, EnemyArtReader.MaxOffset, "Type how many game pixels to move it right (left is negative), then Enter. Esc cancels. The idle's moves it over its shadow.", optional: true);
    private static readonly TextField ArtMoveYField = ArtField("Up/down", () => ArtEditing.Num(OwnOffset().Y), v => SetOwnOffset(OwnOffset().X, v ?? 0),
        -EnemyArtReader.MaxOffset, EnemyArtReader.MaxOffset, "Type how many game pixels to move it up (down is negative), then Enter. Esc cancels. The idle's moves it over its shadow.", optional: true);
    private static readonly TextField ArtRangeField = ArtField("Range (%)", () => ArtEditing.Num((Sel("keyRange") ?? 0.15) * 100), v => SetSel("keyRange", v is double p ? p / 100 : null),
        0, 100, "Type how near a colour must be to the see-through colour to vanish, in % (15 is usual), then Enter. Esc cancels.", optional: true);
}
