using System.Diagnostics;
using System.Text.Json.Nodes;
using static NocturneFlatScroll.EditorInput;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using Key = UnityEngine.InputSystem.Key;

namespace NocturneFlatScroll;

// The Art page's "Turn into frames": a video animation becomes a sprite sheet (a PNG in the
// battle's art folder), so a see-through colour works on it and it plays on any PC, with no video
// decoder. The frame rate is picked first (12 a second like the game's own enemies, 15 or 24).
// Then the video plays once through a player the size of the sheet's frames and each frame is
// read as it passes (EnemyArt.VideoFrames), a little each game frame with a count on the status
// line, and the sheet is saved on a worker (PngWriter). Esc, or leaving the page, stops it and
// nothing changes. Once it's saved the animation becomes a Sprite sheet through the draft like
// the page's other settings, keeping its timing (the attack's hit on the matching frame), move,
// mirror and feet (VideoBake.Settings); the video file goes to the Recycle Bin after a save once
// nothing uses it, like any replaced file. Creator only.
internal static partial class BattleCreator
{
    private enum BakeStage { Probe, Ready, Read, Save }

    /// <summary>A video being turned into frames.</summary>
    private sealed class Bake
    {
        internal BattleDraft Draft = null!;
        internal string Anim = "", Video = "", Folder = "";
        internal int Fps;
        internal ArtAnimationSpec Spec = null!;
        internal BakeStage Stage;
        /// <summary>The whole enemy's size when battle.json doesn't say (the preview's), kept when the idle becomes a sheet.</summary>
        internal double? Size;
        /// <summary>The attack's written hit, in the battle's seconds; null when it's automatic.</summary>
        internal double? Hit;
        internal VideoFacts? Facts;
        internal VideoBake.Plan? Plan;
        internal EnemyArt.VideoFrames? Frames;
        internal byte[]? Sheet;
        internal VideoBake.Background? Background;
        internal Task<(string Path, VideoFacts Facts)>? Probe;
        internal Task<string>? Saving;
        internal string Said = "";
        internal readonly CancellationTokenSource Cancel = new();
        internal readonly TaskCompletionSource<BakeResult> Done = new();
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
    }

    private sealed class BakeResult
    {
        /// <summary>Why it failed, or why it stopped (Esc, the page closed); null when the sheet is saved.</summary>
        internal string? Problem, Stopped;
        /// <summary>The sheet's path in the battle.</summary>
        internal string File = "";
    }

    private static Bake? bake;
    private static bool bakePreviewPlaying;
    // What each sheet made from a video found about its background, by file: a see-through corner colour takes its range.
    private static readonly Dictionary<string, VideoBake.Background> bakedBackgrounds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the selected animation is a video the page can turn into frames (its button shows then).</summary>
    private static bool BakeShown() => draft != null && CustomArtEnemy() && ArtKindOf(artSelected) == ArtKind.Video;

    /// <summary>The "Turn into frames" button: asks for the frame rate, then turns the selected video into a sheet.</summary>
    private static void AskBake()
    {
        if (draft == null || !FinishTyping() || !EnemyEditable() || !draft.HasArt(artSelected)) return;
        string anim = artSelected;
        if (BakeSpec(anim, out string? why) == null)
        {
            Say(why!, 7f);
            return;
        }
        ShowPicker(new Picker
        {
            Heading = $"Turn the {anim} into frames",
            Rows = VideoBake.Rates.Select(fps => BakeRow(anim, fps)).ToList(),
            Hint = _ => "The video becomes a sprite sheet in the battle's art folder, which can take a see-through colour and plays on any PC. " +
                        "It plays through once to be read.  Esc goes back.",
            Choose = i => StartBake(anim, VideoBake.Rates[i]),
            Back = BackFromPicker,
        });
    }

    // "12 frames a second: 36 frames of 192 x 256 (like the game's own enemies)", when the video's size and length are known.
    private static string BakeRow(string anim, int fps)
    {
        string row = $"{fps} frames a second";
        var a = artSpec?.Get(anim);
        var facts = a?.Media.Video;
        if (a != null && facts is { IndexFound: true, Seconds: > 0, Width: > 0, Height: > 0 })
        {
            try
            {
                var plan = VideoBake.Timing(anim, facts.Seconds, a.Speed, fps);
                var (capW, capH) = BakeCap(anim, facts);
                if (VideoBake.Lay(plan, facts.Width, facts.Height, capW, capH)) row += $": {ArtEditing.Frames(plan.Count)} of {plan.CellW} x {plan.CellH}";
            }
            catch (InvalidDataException) { }
        }
        return row + (fps == VideoBake.Rates[0] ? " (like the game's own enemies)" : fps == VideoBake.Rates[^1] ? " (smoother, a bigger file)" : "");
    }

    /// <summary>The animation as the loader reads it, when it's a video that can be turned into frames; else null and why not.</summary>
    private static ArtAnimationSpec? BakeSpec(string anim, out string? why)
    {
        RefreshArt();
        why = null;
        var a = artSpec?.Get(anim);
        if (artSpec != null && artSpec.Dropped.TryGetValue(anim, out var dropped)) why = $"The {anim} can't be used ({dropped}), so it can't be turned into frames.";
        else if (a == null || a.Kind != ArtKind.Video) why = $"The {anim} isn't a video.";
        else if (PreviewFailure(anim) is { } failed) why = $"The {anim} can't play ({failed}), so it can't be turned into frames.";
        return why == null ? a : null;
    }

    // The frame size the preview's video of this animation uses, else its own size up to 720 tall.
    private static (int W, int H) BakeCap(string anim, VideoFacts facts)
    {
        if (previewSet != null && previewJson == artJson && previewSet.Result.Videos.TryGetValue(anim, out var shown) && shown.TextureW > 0 && shown.TextureH > 0)
            return (shown.TextureW, shown.TextureH);
        int h = Math.Min(facts.Height, ArtDecode.MaxVideoTexture);
        return ((int)Math.Max(1, Math.Round((double)facts.Width * h / facts.Height)), h);
    }

    private static void StartBake(string anim, int fps)
    {
        if (draft == null || bake != null || !EnemyEditable()) return;
        var a = BakeSpec(anim, out string? why);
        if (a == null)
        {
            Say(why!, 7f);
            return;
        }
        var d = draft;
        var files = PackageFiles.Folder(d.Folder);
        // Unity's folders are only asked on the main thread; the rest is read on a worker.
        string cache = EnemyArt.CacheFolder();
        var b = new Bake
        {
            Draft = d,
            Anim = anim,
            Video = a.File,
            Folder = d.Folder,
            Fps = fps,
            Spec = a,
            Size = d.ArtNumber("", "size") == null && previewSet != null && previewJson == artJson && previewSet.Result.Size > 0 ? previewSet.Result.Size : null,
        };
        var token = b.Cancel.Token;
        b.Probe = Task.Run(() => ProbeVideo(files, a, cache), token);
        bake = b;
        // The preview's own videos rest meanwhile.
        bakePreviewPlaying = previewPlaying;
        previewPlaying = false;
        artSelected = anim;
        ModLog.Info($"Battle creator: turning the {anim} video {a.File} of {d.Folder} into frames, {fps} a second.");
        Run(b.Done.Task, "Getting the video ready...  Esc stops.", result => BakeFinished(b, result));
    }

    // On a worker: a path the player can open, and the video's facts from the whole file, checked as a fight's load checks them.
    private static (string Path, VideoFacts Facts) ProbeVideo(PackageFiles files, ArtAnimationSpec a, string cache)
    {
        string path = ArtLoader.PlayablePath(files, a, cache, new List<string>());
        VideoFacts facts;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            facts = VideoProbe.Read(stream);
        }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{a.File} {ex.Message}"); }
        EnemyArtReader.CheckVideoFacts(a, facts);
        if (EnemyArt.VideoUnsupported(facts) is { } unsupported) throw new InvalidDataException($"{a.File} {unsupported}");
        return (path, facts);
    }

    /// <summary>Every frame on the Art page: the video being turned into frames moves on, and Esc stops it.</summary>
    private static void UpdateBake()
    {
        var b = bake;
        if (b == null) return;
        var keyboard = InputKeyboard.current;
        if (keyboard != null && Pressed(keyboard, Key.Escape))
        {
            StopBake("Esc was pressed");
            return;
        }
        try { StepBake(b); }
        catch (Exception ex)
        {
            bool expected = ex is InvalidDataException or IOException or UnauthorizedAccessException;
            if (!expected) ModLog.Error("Battle creator: turning the video into frames failed: " + ex);
            EndBake(b, new BakeResult { Problem = expected ? ex.Message : $"something went wrong ({ex.GetType().Name}: {ex.Message})" });
        }
    }

    private static void StepBake(Bake b)
    {
        switch (b.Stage)
        {
            case BakeStage.Probe:
            {
                if (!b.Probe!.IsCompleted) return;
                if (b.Probe.IsFaulted) throw Unwrap(b.Probe.Exception!);
                var (path, facts) = b.Probe.Result;
                b.Facts = facts;
                // The frames are at most the size the preview's video uses: no more detail than the battle shows.
                var (capW, capH) = BakeCap(b.Anim, facts);
                // A video that doesn't say how long it plays (an idle or a defeat may not) is planned for
                // the most frames; the player's own length gives the real count once it's ready.
                var plan = facts.Seconds > 0 ? VideoBake.Timing(b.Anim, facts.Seconds, b.Spec.Speed, b.Fps)
                    : new VideoBake.Plan { Count = ArtTimeline.MaxFrames };
                if (!VideoBake.Lay(plan, facts.Width, facts.Height, capW, capH))
                    throw new InvalidDataException($"{b.Video}'s {plan.Count} frames don't fit in one sprite sheet");
                b.Plan = plan;
                if (b.Anim == "attack" && b.Draft.ArtNumber("attack", "hitTime") != null && facts.Seconds > 0)
                    b.Hit = ArtTimeline.Plan(b.Spec, 1, null, facts.Seconds, new List<string>()).Hit;
                b.Frames = new EnemyArt.VideoFrames(path, b.Video, plan.CellW, plan.CellH);
                b.Stage = BakeStage.Ready;
                return;
            }
            case BakeStage.Ready:
            {
                var frames = b.Frames!;
                if (frames.Watch()) return;
                if (frames.Error != null) throw new InvalidDataException($"{b.Video} can't play ({frames.Error})");
                var plan = b.Plan!;
                if (b.Facts!.Seconds <= 0)
                {
                    if (!(frames.Length > 0)) throw new InvalidDataException($"{b.Video} doesn't say how long it plays");
                    var timed = VideoBake.Timing(b.Anim, frames.Length, b.Spec.Speed, b.Fps);
                    (timed.VideoW, timed.VideoH, timed.CellW, timed.CellH) = (plan.VideoW, plan.VideoH, plan.CellW, plan.CellH);
                    if (!VideoBake.Regrid(timed)) throw new InvalidDataException($"{b.Video}'s frames don't fit in one sprite sheet");
                    b.Plan = plan = timed;
                }
                b.Sheet = new byte[plan.SheetW * plan.SheetH * 4];
                frames.Start(plan.ReadAt, plan.Count > 1 ? plan.ReadAt.Zip(plan.ReadAt.Skip(1)).Min(t => t.Second - t.First) : 1);
                b.Stage = BakeStage.Read;
                SayBake(b, $"Turning the video into frames: 0 of {plan.Count}...  Esc stops.");
                return;
            }
            case BakeStage.Read:
            {
                var frames = b.Frames!;
                var plan = b.Plan!;
                bool going = frames.Update((i, frame) =>
                {
                    if (i == 0) b.Background = VideoBake.Look(frame, plan.CellW, plan.CellH);
                    VideoBake.Put(b.Sheet!, plan, i, frame);
                });
                SayBake(b, $"Turning the video into frames: {frames.Read} of {plan.Count}...  Esc stops.");
                if (going) return;
                if (frames.Error != null) throw new InvalidDataException($"{b.Video} stopped playing ({frames.Error})");
                // Its counts stay for the log; its player and textures go now.
                frames.Dispose();
                var sheet = b.Sheet!;
                string folder = b.Folder, anim = b.Anim;
                var token = b.Cancel.Token;
                b.Saving = Task.Run(() => SaveSheet(folder, anim, sheet, plan, token), token);
                b.Stage = BakeStage.Save;
                SayBake(b, "Saving the frames as a sprite sheet...  Esc stops.");
                return;
            }
            default:
            {
                if (!b.Saving!.IsCompleted) return;
                if (b.Saving.IsFaulted) throw Unwrap(b.Saving.Exception!);
                if (b.Saving.IsCanceled) throw new OperationCanceledException();
                EndBake(b, new BakeResult { File = b.Saving.Result });
                return;
            }
        }
    }

    // On a worker: the sheet as a PNG under a new name in the art folder (nothing is overwritten).
    // It's written beside its name first, so a stop or a failure never leaves half a picture.
    private static string SaveSheet(string folder, string anim, byte[] sheet, VideoBake.Plan plan, CancellationToken cancel)
    {
        var png = PngWriter.Encode(sheet, plan.SheetW, plan.SheetH, cancel);
        if (png.Length > EnemyArtReader.MaxPictureBytes)
            throw new InvalidDataException($"the sprite sheet would be {png.Length / (1024 * 1024)} MB; pictures can be at most {EnemyArtReader.MaxPictureBytes / (1024 * 1024)} MB");
        Directory.CreateDirectory(Path.Combine(folder, "art"));
        string file = VideoBake.FreeName(folder, anim);
        string full = Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar)), part = full + ".part";
        try
        {
            File.WriteAllBytes(part, png);
            cancel.ThrowIfCancellationRequested();
            File.Move(part, full);
        }
        finally
        {
            if (File.Exists(part)) File.Delete(part);
        }
        return file;
    }

    private static void SayBake(Bake b, string text)
    {
        if (b.Said == text) return;
        b.Said = text;
        Say(text, 3600f);
    }

    /// <summary>Stops a video being turned into frames (Esc, or the page, battle or creator closing): nothing changes.</summary>
    private static void StopBake(string why)
    {
        var b = bake;
        if (b == null) return;
        b.Cancel.Cancel();
        // A sheet saved just as it stopped is its own new file, which nothing names: it goes.
        b.Saving?.ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            try { File.Delete(Path.Combine(b.Folder, t.Result.Replace('/', Path.DirectorySeparatorChar))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
        EndBake(b, new BakeResult { Stopped = why });
    }

    private static void EndBake(Bake b, BakeResult result)
    {
        if (bake != b) return;
        bake = null;
        b.Frames?.Dispose();
        b.Sheet = null;
        previewPlaying = bakePreviewPlaying;
        if (result.Problem != null) b.Cancel.Cancel();
        b.Done.TrySetResult(result);
    }

    // Once it's saved (or stopped, or failed): the animation becomes the sheet through the draft, and the page says what happened.
    private static void BakeFinished(Bake b, BakeResult r)
    {
        var d = b.Draft;
        string anim = b.Anim;
        if (r.Stopped != null)
        {
            ModLog.Info($"Battle creator: turning the {anim} video {b.Video} into frames stopped ({r.Stopped}).");
            Say($"Stopped. The {anim} is still the video; nothing changed.", 5f);
            return;
        }
        if (r.Problem != null)
        {
            ModLog.Info($"Battle creator: the {anim} video {b.Video} couldn't be turned into frames: {r.Problem}.");
            Say($"The {anim} couldn't be turned into frames: {r.Problem.TrimEnd('.')}. Nothing changed.", 9f);
            return;
        }
        if (draft != d)
        {
            ModLog.Info($"Battle creator: {r.File} was made for {b.Folder}, which isn't open any more; it stays there unused.");
            return;
        }
        // The new file is the battle's own now: it goes after a save (or leaving without one) if nothing names it.
        touched.Add(r.File);
        if (!EnemyEditable() || !b.Video.Equals(d.ArtFile(anim), StringComparison.OrdinalIgnoreCase))
        {
            Say($"The {anim} changed while it was being turned into frames, so the sheet ({r.File}) isn't used.", 7f);
            return;
        }
        ApplyBake(b, r);
    }

    /// <summary>The animation becomes the sheet, in one go, through the draft (Save keeps it, "Don't save" drops it and the sheet).</summary>
    private static void ApplyBake(Bake b, BakeResult r)
    {
        var d = b.Draft;
        var plan = b.Plan!;
        string anim = b.Anim;
        // The video goes after a save, when nothing names it any more (another animation may use it too).
        touched.Add(b.Video);
        // Keys in any letter case, as the draft reads them.
        var video = JsonNode.Parse(d.ArtJson() ?? "{}", new JsonNodeOptions { PropertyNameCaseInsensitive = true })?[anim] as JsonObject;
        d.SetArtAnimation(anim, r.File, ArtEditing.Json(ArtKind.Sheet));
        foreach (var (key, value) in VideoBake.Settings(plan, anim, video, b.Hit)) d.SetArtValue(anim, key, value);
        if (anim == "idle")
        {
            // The others' sizes are measured against the idle's pixels, so they follow smaller frames;
            // and a size worked out from the video stays, not one worked out from the new frames.
            foreach (var other in ArtAnims.Where(o => o != "idle" && d.HasArt(o)))
                if (VideoBake.OtherScale(plan, d.ArtNumber(other, "scale")) is double scale)
                    d.SetArtValue(other, "scale", BattleDraft.ArtNumberNode(Math.Abs(scale - 1) < 1e-9 ? null : scale));
            if (b.Size is double size && d.ArtNumber("", "size") == null) d.SetArtValue("", "size", BattleDraft.ArtNumberNode(size));
        }
        var look = b.Background;
        if (look is { Flat: true }) bakedBackgrounds[r.File] = look;
        artSelected = anim;
        var frames = b.Frames;
        ModLog.Info($"Battle creator: turned the {anim} video {b.Video} into {r.File}: {plan.Count} frames of {plan.CellW} x {plan.CellH} " +
                    $"({plan.Columns} x {plan.Rows}) at {plan.Fps} a second{(plan.Thinned ? $", {plan.Count} of {plan.Frames}" : "")}, " +
                    $"{(frames?.Busy ?? 0)} repeated while the game was busy, {(frames?.PastEnd ?? 0)} past the video's end, in {b.Clock.ElapsedMilliseconds} ms; " +
                    $"background {(look is { Flat: true } ? $"{look.Name}, range {ArtEditing.Num(look.Range)}" : "not flat")}.");
        var said = new List<string> { $"{Cap(anim)} is a sprite sheet now: {ArtEditing.Frames(plan.Count)}, {plan.Fps} a second ({Path.GetFileName(r.File)})." };
        if (plan.Thinned) said.Add($"The video is long, so {plan.Count} of its {plan.Frames} frames are kept.");
        if (plan.CellH < plan.VideoH) said.Add($"Each frame is {plan.CellW} x {plan.CellH} (the video is {plan.VideoW} x {plan.VideoH}).");
        if ((frames?.Busy ?? 0) > 0) said.Add($"{frames!.Busy} frames repeat the one before as the game was busy; turn the video into frames again if it looks jumpy.");
        if (d.ArtText(anim, "keyColor") == null)
            said.Add(look is { Flat: true }
                ? $"Its background is {look.Name}: set See-through colour to corner colour to cut it out."
                : "See-through colour can cut out a flat background now.");
        said.Add("Save to keep it; the video then goes to the Recycle Bin if nothing else uses it.");
        Say(string.Join(" ", said), 12f);
    }
}
