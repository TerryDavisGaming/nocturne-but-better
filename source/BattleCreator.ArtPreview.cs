using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.UI;
using static NocturneFlatScroll.EditorInput;
using static NocturneFlatScroll.EditorUi;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;

namespace NocturneFlatScroll;

// The Art page's preview: the enemy as the battle will draw it. It's loaded with the battle's own
// pipeline (EnemyArt.ArtSet: the same decoders, atlases, feet, size and timing) and played with
// the same frame-hold code, on the battle's screen (480 x 270 game pixels, feet 72 below the top)
// or close up. Moving and resizing show at once; other settings load the art again after a short
// pause, and the old art stays until the new one is ready. Under it: the timeline with the parry
// window and the hit, and the whole enemy's size, place, mirror, pixels and shadow.
internal static partial class BattleCreator
{
    private const float PreviewW = 640, PreviewH = 360;
    // The battle's screen in game pixels (a game pixel is 4 screen pixels at 1080p).
    private const float GameW = 480, GameH = 270;
    // Settings wait this long before the art loads again, so a few quick clicks load it once.
    private const float PreviewDelay = 0.3f;
    // A clip that doesn't loop holds its last frame this long, then plays again.
    private const float PreviewRest = 0.6f;
    private const int MaxTicks = 120;
    // The idle shows faintly behind the other animations, to line them up with it.
    private const float GhostAlpha = 0.35f;

    private static RectTransform? previewBox, previewBarRect;
    private static RawImage? previewArt, previewGhost;
    private static Texture? previewArtTexture, previewGhostTexture;
    private static Image? previewGround, previewFeet, previewParry, previewHitMark, previewHead;
    private static readonly List<Image> previewTicks = new();
    private static TMP_Text? previewHitText, previewNote;

    private static EnemyArt.ArtSet? previewSet, previewNext;
    private static string previewJson = "", previewNextJson = "";
    private static float previewDue = -1;
    // Pictures decoded for the preview (and the grid guess), kept between loads by file and stamp.
    private static readonly Dictionary<string, Picture> previewPictures = new();
    private static float previewTime, previewRest;
    // The video showing was seen playing since it last started (a stopped one has then reached its end).
    private static bool previewVideoRan;
    private static bool previewPlaying = true, previewCloseUp;
    // The animation shown last frame (another one starts from its beginning), and its clip (a new
    // load of the same animation keeps its place).
    private static string previewShowing = "";
    private static EnemyArt.Clip? previewClipShown;
    // Dragging the art moves it: where the drag started, and the move so far in game pixels.
    private static bool dragging;
    private static string dragAnim = "";
    private static Vector2 dragStart, dragGame;
    // Why the preview stopped, and the art it stopped on (it tries again once the art changes).
    private static string previewError = "", previewErrorJson = "";

    // ---- building -----------------------------------------------------------------------------

    private static void BuildArtPreview(Page p, Func<bool> custom)
    {
        var panel = pagePanels[p];
        float y = 0;
        AddHeader(p, Col2, ref y, Col2W, "Preview", custom);
        previewBox = MakeImage("PreviewBox", panel, Hex(0x0B0A10)).rectTransform;
        PlaceTop(previewBox, Col2, y, PreviewW, PreviewH);
        // Art bigger than the screen is cut at its edges, like the battle's.
        try { previewBox.gameObject.AddComponent<RectMask2D>(); }
        catch (Exception ex) { ModLog.Error("Battle creator: the art preview can't cut off art at its edges: " + ex.Message); }
        previewGround = MakeImage("Ground", previewBox, Hex(0x9D92B4, 0.35f));
        previewFeet = MakeImage("Feet", previewBox, Hex(0x4FD1A5, 0.9f));
        previewGhost = MakeRawImage("Ghost", previewBox);
        previewGhost.color = new Color(1, 1, 1, GhostAlpha);
        previewArt = MakeRawImage("Art", previewBox);
        previewHitText = MakeText("Hit", previewBox, 34, TextAlignmentOptions.TopRight);
        previewHitText.color = Accent;
        previewHitText.text = "HIT";
        Stretch(previewHitText.rectTransform, 12, 12, 16, 10);
        previewNote = MakeText("Note", previewBox, 19, TextAlignmentOptions.Center);
        previewNote.color = DimText;
        Stretch(previewNote.rectTransform, 30, 30, 30, 30);
        y -= PreviewH + 8;

        AddButton(p, Col2, y, 56, RowH, "<", () => StepPreview(-1), custom);
        var play = AddButton(p, Col2 + 62, y, 110, RowH, "", TogglePreviewPlay, custom);
        play.Text = () => previewPlaying ? "Pause" : "Play";
        AddButton(p, Col2 + 178, y, 56, RowH, ">", () => StepPreview(1), custom);
        var clock = MakeText("Clock", panel, 18, TextAlignmentOptions.Left);
        clock.color = DimText;
        PlaceTop(clock.rectTransform, Col2 + 246, y, 240, RowH);
        Ui.AddLiveText(clock, PreviewClock).Visible = custom;
        AddButton(p, Col2 + PreviewW - 140, y, 140, RowH, "Hit here", HitHere, () => custom() && artSelected == "attack" && PreviewClip(previewSet, "attack") != null);
        y -= RowStep;

        // The timeline: frame starts, the parry window and the hit (attack), and where it's playing.
        previewBarRect = MakeImage("Timeline", panel, ButtonColor).rectTransform;
        PlaceTop(previewBarRect, Col2, y, PreviewW, 20);
        for (int i = 0; i < MaxTicks; i++)
        {
            var tick = MakeImage("Tick", previewBarRect, Hex(0x9D92B4, 0.6f));
            tick.gameObject.SetActive(false);
            previewTicks.Add(tick);
        }
        previewParry = MakeImage("Parry", previewBarRect, Hex(0xF27333, 0.35f));
        previewHitMark = MakeImage("HitMark", previewBarRect, Accent);
        previewHead = MakeImage("Playhead", previewBarRect, TextColor);
        y -= 28;

        var view = AddButton(p, Col2, y, 250, RowH, "", () => previewCloseUp = !previewCloseUp, custom);
        view.Text = () => previewCloseUp ? "View: close up" : "View: the battle's screen";
        view.Label.fontSize = 19;
        var help = MakeText("Help", panel, 15, TextAlignmentOptions.Left);
        help.color = DimText;
        PlaceTop(help.rectTransform, Col2 + 262, y, PreviewW - 262, RowH);
        Ui.AddLiveText(help, () => "Drag the art to move it. Space plays or pauses; Left and Right step.").Visible = custom;
        y -= RowStep;

        AddHeader(p, Col2, ref y, Col2W, "Whole enemy", custom);
        Func<bool> hasIdle = () => custom() && (draft?.HasArt("idle") ?? false);
        PreviewStepper(p, Col2, ref y, Col2W, ArtSizeField, ArtSizeText, () => StepArtSize(-1), () => StepArtSize(1), hasIdle);
        float row = y;
        const float half = Col2W / 2 - 6;
        PreviewStepper(p, Col2, ref row, half, ArtOffsetXField, () => $"Sideways {ArtEditing.Num(WholeOffset().X)}", () => StepWholeOffset(-1, 0), () => StepWholeOffset(1, 0), hasIdle);
        PreviewStepper(p, Col2 + half + 12, ref y, half, ArtOffsetYField, () => $"Up/down {ArtEditing.Num(WholeOffset().Y)}", () => StepWholeOffset(0, -1), () => StepWholeOffset(0, 1), hasIdle);
        float w3 = (Col2W - 2 * 10) / 3f;
        var mirror = AddButton(p, Col2, y, w3, RowH, "", () => ToggleWholeFlip(), hasIdle);
        mirror.Text = () => $"Mirror: {((draft?.ArtBool("", "flip") ?? false) ? "on" : "off")}";
        mirror.Active = () => draft?.ArtBool("", "flip") ?? false;
        mirror.Label.fontSize = 19;
        var crisp = AddButton(p, Col2 + w3 + 10, y, w3, RowH, "", CycleCrisp, hasIdle);
        crisp.Text = CrispText;
        crisp.Active = () => draft?.ArtBool("", "smooth") == false;
        crisp.Label.fontSize = 19;
        var shadow = AddButton(p, Col2 + 2 * (w3 + 10), y, w3, RowH, "", ToggleShadow, hasIdle);
        shadow.Text = () => $"Shadow: {((draft?.ArtBool("", "shadow") ?? true) ? "on" : "off")}";
        shadow.Active = () => draft?.ArtBool("", "shadow") ?? true;
        shadow.Label.fontSize = 19;
        y -= RowStep;
        var warnings = AddText(p, Col2, ref y, Col2W, 150, PreviewWarnings, 16, custom);
        warnings.color = Hex(0xF2B02E);
    }

    // A stepper at a fixed place (the preview's column doesn't move), with a value that can be typed.
    private static void PreviewStepper(Page p, float x, ref float y, float w, TextField field, Func<string> text, Action less, Action more, Func<bool> shown)
    {
        AddButton(p, x, y, 56, RowH, "-", less, shown);
        var value = AddButton(p, x + 60, y, w - 120, RowH, "", () => StartTyping(field), shown);
        value.Text = () => typing == field ? $"{field.Label}: {Escape(typed)}_" : text();
        value.Active = () => typing == field;
        value.Label.fontSize = 19;
        AddButton(p, x + w - 56, y, 56, RowH, "+", more, shown);
        y -= RowStep;
    }

    // ---- loading ------------------------------------------------------------------------------

    /// <summary>Every frame on the Art page: the art's lines, the preview's loading, and drawing it.</summary>
    private static void UpdateArtPage(InputMouse? clicks)
    {
        RefreshArt();
        LayoutArtLines();
        EnemyArt.RunSteps(4);
        bool custom = CustomArtEnemy();
        if (previewBox!.gameObject.activeSelf != custom) previewBox.gameObject.SetActive(custom);
        if (previewBarRect!.gameObject.activeSelf != custom) previewBarRect.gameObject.SetActive(custom);
        if (!custom)
        {
            DropPreview();
            return;
        }
        if (previewError.Length > 0 && previewErrorJson == artJson)
        {
            previewNote!.text = Escape($"The preview stopped ({previewError}). Change a setting to try again; the battle itself isn't affected.");
            return;
        }
        previewError = "";
        try
        {
            LoadPreview();
            Drag(clicks);
            DrawPreview();
        }
        catch (Exception ex)
        {
            // A preview that fails must not close the creator (and lose unsaved changes) with it.
            ModLog.Error("Battle creator: the art preview failed: " + ex);
            DropPreview();
            HideArt(previewArt!);
            HideArt(previewGhost!);
            previewError = ex.Message;
            previewErrorJson = artJson;
        }
    }

    /// <summary>The art the preview should show: the draft's, once it has an idle (the rest is sized and placed from it).</summary>
    private static string PreviewWanted() => artSpec != null && artSpec.Animations.ContainsKey("idle") ? artJson : "";

    private static void LoadPreview()
    {
        // A finished load takes over once its videos are ready (or given up on).
        var next = previewNext;
        if (next != null)
        {
            next.WatchVideos();
            if (next.Done && next.Clips.Values.All(c => c.Video == null || c.Video.Prepared || c.Video.Failed)) FinishPreviewLoad(next);
        }
        previewSet?.WatchVideos();
        string want = PreviewWanted();
        if (want.Length == 0)
        {
            if (previewSet != null || previewNext != null) DropPreview();
            return;
        }
        if (previewSet != null && want == previewJson)
        {
            if (previewNext != null) ReleasePreview(ref previewNext);
            previewDue = -1;
            return;
        }
        if (previewNext != null && want == previewNextJson) return;
        if (previewDue < 0)
        {
            previewDue = Time.unscaledTime + PreviewDelay;
            return;
        }
        // Not while a file is being picked, copied or cut up: the draft is about to change again.
        if (Time.unscaledTime < previewDue || Busy) return;
        previewDue = -1;
        ReleasePreview(ref previewNext);
        var set = new EnemyArt.ArtSet(draft!.Title, artSpec!, PackageFiles.Folder(draft.Folder), LookName(), EnemyArt.CacheFolder())
        {
            Pictures = previewPictures,
            LogName = "Battle creator: art preview",
        };
        set.Begin();
        previewNext = set;
        previewNextJson = want;
    }

    private static void FinishPreviewLoad(EnemyArt.ArtSet next)
    {
        previewNext = null;
        if (next.Disposed) return;
        var old = previewSet;
        previewSet = next;
        previewJson = previewNextJson;
        previewNextJson = "";
        old?.Dispose();
        var r = next.Result;
        if (next.Work.IsFaulted && next.Work.Exception?.GetBaseException() is not OperationCanceledException)
            ModLog.Error("Battle creator: the art preview failed: " + next.Work.Exception?.GetBaseException());
        foreach (var error in r.Errors) ModLog.Error("Battle creator: art preview: " + error);
        foreach (var (anim, why) in r.Failed) ModLog.Info($"Battle creator: art preview: the {anim} can't be used ({why}).");
        ModLog.Info($"Battle creator: art preview ready in {next.Clock.ElapsedMilliseconds} ms: {r.Describe()}.");
        artTimelines.Clear();
        TrimPictures(next.Spec);
        // Filled in only from the draft's art as it is now; a newer load is on its way otherwise.
        RefreshArt();
        if (previewJson == artJson && !Busy) ApplyArtFills(next);
    }

    // Only the pictures the art still uses stay decoded.
    private static void TrimPictures(EnemyArtSpec spec)
    {
        var used = new HashSet<string>(spec.Animations.Values.Select(a => a.File), StringComparer.OrdinalIgnoreCase);
        lock (previewPictures)
            foreach (var key in previewPictures.Keys.ToList())
                if (!used.Contains(key.Substring(0, Math.Max(0, key.LastIndexOf('|'))))) previewPictures.Remove(key);
    }

    private static void ReleasePreview(ref EnemyArt.ArtSet? set)
    {
        set?.Dispose();
        set = null;
    }

    /// <summary>Lets go of the preview's art (its textures and videos).</summary>
    private static void DropPreview()
    {
        ReleasePreview(ref previewNext);
        ReleasePreview(ref previewSet);
        previewJson = previewNextJson = "";
        previewDue = -1;
        previewShowing = "";
        previewClipShown = null;
        dragging = false;
        previewArtTexture = previewGhostTexture = null;
        if (previewArt) previewArt!.texture = null;
        if (previewGhost) previewGhost!.texture = null;
    }

    /// <summary>
    /// Leaving the Art page, the battle or the creator: the preview's art goes, and with
    /// <paramref name="forget"/> (another battle) what it kept and the fills still waiting too.
    /// </summary>
    private static void StopArtPreview(bool forget)
    {
        DropPreview();
        lock (previewPictures) previewPictures.Clear();
        previewError = previewErrorJson = "";
        if (!forget) return;
        artFills.Clear();
        artMedia.Clear();
        artSizeAuto = artHitAuto = false;
        artSelected = "idle";
        artReadFor = null;
        artSpec = null;
    }

    // ---- drawing ------------------------------------------------------------------------------

    private static EnemyArt.Clip? PreviewClip(EnemyArt.ArtSet? set, string anim) =>
        set != null && set.Clips.TryGetValue(anim, out var clip) && clip.Usable && clip.Texture != null ? clip : null;

    /// <summary>The animation the preview plays: the selected one, or what stands in for it in the battle.</summary>
    private static string PreviewAnim(EnemyArt.ArtSet set)
    {
        string anim = artSelected;
        if (PreviewClip(set, anim) != null) return anim;
        if (anim == "defeat" && PreviewClip(set, "hurt") != null) return "hurt";
        return "idle";
    }

    private static void DrawPreview()
    {
        var set = previewSet;
        string note = "";
        if (PreviewWanted().Length == 0) note = "Choose the idle to see the enemy here. The other animations are sized and placed from it.";
        else if (set == null) note = "Loading...";
        string anim = set == null ? "" : PreviewAnim(set);
        var clip = set == null ? null : PreviewClip(set, anim);
        if (set != null && clip == null)
        {
            var video = set.Clips.TryGetValue("idle", out var idleClip) ? idleClip.Video : null;
            note = set.Result.Failed.TryGetValue("idle", out var why) ? $"The idle can't be used: {why}"
                : video is { Failed: true } ? $"The idle's video can't play: {video.Error ?? "it wasn't ready in time"}"
                : "The preview couldn't show the idle; the log says why.";
        }
        else if (set != null && anim != artSelected)
            note = draft?.HasArt(artSelected) == true ? "" : $"The {artSelected} isn't set, so {(anim == "idle" ? "the idle" : "the hurt")} plays instead.";
        if (set != null && draft?.HasArt(artSelected) == true && PreviewClip(set, artSelected) == null && previewJson == artJson && set.Result.Failed.TryGetValue(artSelected, out var failed))
            note = $"The {artSelected} can't be used: {failed}";
        previewNote!.text = Escape(note);
        if (clip == null)
        {
            HideArt(previewArt!);
            HideArt(previewGhost!);
            DrawGround(false);
            DrawTimeline(null, false);
            previewHitText!.gameObject.SetActive(false);
            return;
        }
        var t = LiveTimeline(anim, clip);
        if (previewShowing != anim)
        {
            previewShowing = anim;
            previewTime = 0;
            previewRest = 0;
        }
        if (!ReferenceEquals(previewClipShown, clip))
        {
            // Another animation, or the same one loaded again: a video carries on from the same time.
            previewClipShown = clip;
            previewVideoRan = false;
            if (clip.Video is { Prepared: true, Failed: false } fresh && fresh.Player)
            {
                fresh.Player.time = previewTime * Math.Max(0.1, clip.Spec.Speed);
                fresh.LastFrame = -1;
            }
        }
        PlayVideos(set!, clip);
        int frame = Advance(clip, t);
        PlaceArt(previewArt!, ref previewArtTexture, clip, frame, anim);
        var idle = PreviewClip(set, "idle");
        if (anim != "idle" && idle != null && idle.Video == null) PlaceArt(previewGhost!, ref previewGhostTexture, idle, 0, "idle");
        else HideArt(previewGhost!);
        DrawGround(true);
        bool attack = anim == "attack";
        DrawTimeline(t, attack);
        bool hit = attack && t.Hit >= 0 && previewTime >= t.Hit && previewTime < t.Hit + 0.2;
        if (previewHitText!.gameObject.activeSelf != hit) previewHitText.gameObject.SetActive(hit);
    }

    /// <summary>
    /// The clip's timeline with the draft's timing now (its speed, frame times, hit), when it
    /// still cuts the same frames; the settings show at once, before the art loads again.
    /// </summary>
    private static ArtTimeline LiveTimeline(string anim, EnemyArt.Clip clip)
    {
        var loaded = previewSet?.Result.Measured.TryGetValue(anim, out var m) == true ? m : null;
        var now = artSpec?.Get(anim);
        if (loaded != null && now != null && ArtEditing.SameFrames(now, loaded.Spec) && ArtTimelineOf(anim) is { } live && live.Holds.Length == clip.Rects.Length && clip.Video == null)
            return live;
        if (loaded != null && now != null && clip.Video != null && ArtTimelineOf(anim) is { } video) return video;
        return loaded?.Timeline ?? new ArtTimeline { Length = clip.Length, Holds = clip.Holds, Keep = new[] { 0 }, Hit = clip.Hit, Parry = clip.Parry };
    }

    // Moves the clock on and picks the frame; a clip that doesn't loop rests on its last frame, then plays again.
    private static int Advance(EnemyArt.Clip clip, ArtTimeline t)
    {
        float length = (float)Math.Max(0.01, t.Length);
        bool loop = t.Loop;
        if (clip.Video is { } v)
        {
            if (v.Prepared && !v.Failed && v.Player)
            {
                double speed = Math.Max(0.1, clip.Spec.Speed);
                bool running = v.Player.isPlaying;
                previewVideoRan |= running;
                previewTime = (float)(v.Player.time / speed);
                if (!loop && previewPlaying && ((previewVideoRan && !running) || previewTime >= length - 0.02f))
                {
                    previewRest += Time.unscaledDeltaTime;
                    if (previewRest >= PreviewRest) RestartVideo(v);
                }
            }
            return 0;
        }
        if (previewPlaying)
        {
            if (loop || previewTime < length) previewTime += Time.unscaledDeltaTime;
            else
            {
                previewRest += Time.unscaledDeltaTime;
                if (previewRest >= PreviewRest)
                {
                    previewTime = 0;
                    previewRest = 0;
                }
            }
            if (loop && previewTime >= length) previewTime %= length;
        }
        return ArtTimeline.FrameAt(t.Holds, length, loop, previewTime);
    }

    private static void RestartVideo(EnemyArt.VideoArt v)
    {
        previewRest = 0;
        previewTime = 0;
        previewVideoRan = false;
        if (!v.Prepared || v.Failed || !v.Player) return;
        v.Player.time = 0;
        if (previewPlaying)
        {
            v.Player.Play();
            v.Playing = true;
        }
    }

    // Only the video that shows plays, and only while the preview plays.
    private static void PlayVideos(EnemyArt.ArtSet set, EnemyArt.Clip shown)
    {
        foreach (var clip in set.Clips.Values)
        {
            var v = clip.Video;
            if (v == null || !v.Prepared || v.Failed || !v.Player) continue;
            bool play = clip == shown && previewPlaying;
            if (play && !v.Playing)
            {
                v.Player.Play();
                v.Playing = true;
            }
            else if (!play && v.Playing)
            {
                v.Player.Pause();
                v.Playing = false;
            }
        }
    }

    /// <summary>Canvas units per game pixel: the battle's screen fills the box, or close up the idle fills most of its height.</summary>
    private static float PreviewUnit()
    {
        float screen = PreviewW / GameW;
        if (!previewCloseUp) return screen;
        var idle = previewSet?.Result.Measured.TryGetValue("idle", out var m) == true ? m : null;
        double size = previewSet?.Result.Size ?? 0;
        if (idle == null || size <= 0) return screen * 2;
        double tall = Math.Max(1, idle.VisibleH * size / idle.FrameH * LiveSize());
        return Mathf.Clamp((float)(PreviewH * 0.6 / tall), screen, 8 * screen);
    }

    /// <summary>Where the feet stand with no move, in canvas units from the box's top-left (y down).</summary>
    private static Vector2 PreviewFeet(float unit) => previewCloseUp
        ? new Vector2(PreviewW / 2, PreviewH * 0.82f)
        : new Vector2(GameW / 2 * unit, (float)ArtEditing.FeetBelowTop * unit);

    // The draft's size compared to the loaded one's (the art loads again at the new size shortly).
    private static float LiveSize()
    {
        double loaded = previewSet?.Result.Size ?? 0;
        double now = draft?.ArtNumber("", "size") ?? loaded;
        return loaded > 0 && now > 0 ? (float)(now / loaded) : 1f;
    }

    private static (double X, double Y) WholeOffset() => draft?.ArtPair("", "offset") ?? (0, 0);

    private static void PlaceArt(RawImage image, ref Texture? shown, EnemyArt.Clip clip, int frame, string anim)
    {
        var texture = clip.Texture;
        if (texture == null || !texture)
        {
            HideArt(image);
            return;
        }
        if (!ReferenceEquals(shown, texture))
        {
            image.texture = texture;
            shown = texture;
        }
        var (x, y, w, h) = clip.Rects[Math.Clamp(frame, 0, clip.Rects.Length - 1)];
        float tw = Math.Max(1, clip.TextureW), th = Math.Max(1, clip.TextureH);
        image.uvRect = new Rect(x / tw, y / th, w / tw, h / th);
        float unit = PreviewUnit();
        var loaded = previewSet?.Spec.Get(anim);
        float scale = LiveSize();
        if (anim != "idle" && loaded != null) scale *= (float)((draft?.ArtNumber(anim, "scale") ?? 1) / Math.Max(0.01, loaded.Scale));
        var rect = image.rectTransform;
        rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
        rect.pivot = new Vector2(clip.PivotX, clip.PivotY);
        rect.sizeDelta = new Vector2(w * clip.Scale * unit * scale, h * clip.Scale * unit * scale);
        // The whole enemy's move, and this animation's own move beyond what's loaded (it's in the pivot).
        var (wx, wy) = WholeOffset();
        var own = draft?.ArtPair(anim, "offset") ?? (0, 0);
        double dx = wx + own.X - (loaded?.OffsetX ?? 0), dy = wy + own.Y - (loaded?.OffsetY ?? 0);
        if (dragging && dragAnim == anim) (dx, dy) = (dx + dragGame.x, dy + dragGame.y);
        var feet = PreviewFeet(unit);
        rect.anchoredPosition = new Vector2(feet.x + (float)dx * unit, -(feet.y - (float)dy * unit));
        bool flip = (draft?.ArtBool("", "flip") ?? false) ^ (draft?.ArtBool(anim, "flip") ?? false);
        rect.localScale = new Vector3(flip ? -1 : 1, 1, 1);
        if (!image.gameObject.activeSelf) image.gameObject.SetActive(true);
    }

    private static void HideArt(RawImage image)
    {
        if (image.gameObject.activeSelf) image.gameObject.SetActive(false);
    }

    // The ground line and the feet mark: where the enemy stands (the whole enemy's move moves them, as it does the shadow).
    private static void DrawGround(bool show)
    {
        previewGround!.gameObject.SetActive(show);
        previewFeet!.gameObject.SetActive(show);
        if (!show) return;
        float unit = PreviewUnit();
        var (wx, wy) = WholeOffset();
        if (dragging && dragAnim == "idle") (wx, wy) = (wx + dragGame.x, wy + dragGame.y);
        var feet = PreviewFeet(unit) + new Vector2((float)wx * unit, -(float)wy * unit);
        PlaceTop(previewGround.rectTransform, 0, -feet.y, PreviewW, 2);
        PlaceTop(previewFeet.rectTransform, feet.x - 1, -feet.y + 8, 2, 16);
    }

    private static void DrawTimeline(ArtTimeline? t, bool attack)
    {
        bool show = t != null && t.Length > 0;
        int count = 0;
        if (show)
        {
            float length = (float)t!.Length, at = 0;
            int every = Math.Max(1, (t.Holds.Length + MaxTicks - 1) / MaxTicks);
            for (int i = 0; i < t.Holds.Length - 1 && count < MaxTicks; i++)
            {
                at += t.Holds[i];
                if ((i + 1) % every != 0 || at >= length) continue;
                var tick = previewTicks[count++];
                PlaceTop(tick.rectTransform, at / length * PreviewW - 1, 0, 2, 20);
                if (!tick.gameObject.activeSelf) tick.gameObject.SetActive(true);
            }
        }
        for (int i = count; i < previewTicks.Count; i++)
            if (previewTicks[i].gameObject.activeSelf) previewTicks[i].gameObject.SetActive(false);
        bool hit = show && attack && t!.Hit >= 0;
        previewParry!.gameObject.SetActive(hit);
        previewHitMark!.gameObject.SetActive(hit);
        previewHead!.gameObject.SetActive(show);
        if (!show) return;
        float w = PreviewW / (float)t!.Length;
        if (hit)
        {
            PlaceTop(previewParry.rectTransform, (float)t.Parry * w, 0, Math.Max(2, (float)(t.Hit - t.Parry) * w), 20);
            PlaceTop(previewHitMark.rectTransform, (float)t.Hit * w - 2, 0, 4, 20);
        }
        PlaceTop(previewHead.rectTransform, Math.Min(PreviewW - 2, previewTime * w), 0, 2, 20);
    }

    private static string PreviewClock()
    {
        var set = previewSet;
        if (set == null) return "";
        string anim = PreviewAnim(set);
        var clip = PreviewClip(set, anim);
        if (clip == null) return "";
        var t = LiveTimeline(anim, clip);
        if (clip.Video != null) return $"{ArtEditing.Sec(previewTime)} / {ArtEditing.Sec(t.Length)} s";
        int frame = ArtTimeline.FrameAt(t.Holds, (float)Math.Max(0.01, t.Length), t.Loop, previewTime);
        int n = Math.Max(1, t.Holds.Length);
        int file = frame < t.Keep.Length ? t.Keep[frame] + 1 : frame + 1;
        return $"Frame {file} / {Math.Max(n, t.SourceFrames)}   {ArtEditing.Sec(previewTime)} s";
    }

    private static string PreviewWarnings()
    {
        var set = previewSet;
        if (draft == null || set == null || previewJson != artJson) return "";
        var lines = new List<string>();
        var r = set.Result;
        if (r.Measured.TryGetValue("idle", out var idle) && !r.Failed.ContainsKey("idle"))
        {
            double size = draft.ArtNumber("", "size") ?? r.Size;
            if (ArtEditing.OffTop(idle, size, WholeOffset().Y) > 0.5)
                lines.Add("The top of the enemy is off the top of the screen. Make it smaller or move it down.");
            if (idle.Video != null)
            {
                if (idle.Spec.Media.Type == MediaType.Mp4)
                    lines.Add("MP4 videos have no see-through parts, so they show as a rectangle. Use a WebM with transparency, a GIF, PNGs or a sprite sheet for a cut-out enemy.");
                else if (!idle.Video.Alpha) lines.Add("This WebM has no see-through parts, so it shows as a rectangle.");
            }
            else if (!idle.FeetFound && !idle.Spec.HasKey)
                lines.Add("The idle has no see-through parts, so it shows as a rectangle. See-through colour can cut out a flat background.");
        }
        return string.Join("\n", lines.Select(Escape));
    }

    // ---- playing, stepping, the hit, dragging ---------------------------------------------------

    private static void TogglePreviewPlay()
    {
        previewPlaying = !previewPlaying;
        previewRest = 0;
    }

    /// <summary>Pauses and moves one frame (a video: a tenth of a second) back or on.</summary>
    private static void StepPreview(int direction)
    {
        var set = previewSet;
        if (set == null) return;
        string anim = PreviewAnim(set);
        var clip = PreviewClip(set, anim);
        if (clip == null) return;
        previewPlaying = false;
        previewRest = 0;
        var t = LiveTimeline(anim, clip);
        if (clip.Video is { } v)
        {
            if (!v.Prepared || v.Failed || !v.Player) return;
            double speed = Math.Max(0.1, clip.Spec.Speed);
            double time = Math.Clamp(previewTime + direction * 0.1, 0, Math.Max(0, t.Length - 0.01));
            v.Player.time = time * speed;
            previewTime = (float)time;
            return;
        }
        int n = t.Holds.Length;
        if (n == 0) return;
        int frame = ArtTimeline.FrameAt(t.Holds, (float)Math.Max(0.01, t.Length), t.Loop, previewTime);
        frame = ((frame + direction) % n + n) % n;
        previewTime = t.Holds.Take(frame).Sum() + 0.0001f;
    }

    /// <summary>"Hit here": the attack's hit on the frame (or at the time) showing now.</summary>
    private static void HitHere()
    {
        var set = previewSet;
        var clip = PreviewClip(set, "attack");
        if (draft == null || clip == null || artSelected != "attack" || !EnemyEditable()) return;
        var t = LiveTimeline("attack", clip);
        if (HitByFrame(out var planned) && clip.Video == null)
        {
            int frame = ArtTimeline.FrameAt(t.Holds, (float)Math.Max(0.01, t.Length), false, previewTime);
            int file = frame < t.Keep.Length ? t.Keep[frame] : frame;
            SetHitFrame(file);
            Say($"The hit lands on frame {file + 1} now (of {planned!.SourceFrames}).", 4f);
        }
        else
        {
            double speed = clip.Video != null ? Math.Max(0.1, clip.Spec.Speed) : 1;
            SetHitTime(previewTime * speed);
            Say($"The hit lands at {ArtEditing.Sec(previewTime)} s now.", 4f);
        }
    }

    // Dragging the art in the box moves it: the idle moves the whole enemy, another animation only itself.
    private static void Drag(InputMouse? clicks)
    {
        var mouse = InputMouse.current;
        if (mouse == null || previewSet == null)
        {
            dragging = false;
            return;
        }
        Vector2 pos = mouse.position.ReadValue();
        if (!dragging)
        {
            if (clicks == null || !clicks.leftButton.wasPressedThisFrame || typing != null) return;
            if (!RectTransformUtility.RectangleContainsScreenPoint(previewBox!, pos, null)) return;
            string anim = PreviewAnim(previewSet);
            if (anim != artSelected || draft == null || !draft.HasArt(anim) || !EnemyEditable()) return;
            dragging = true;
            dragAnim = anim;
            dragStart = pos;
            dragGame = Vector2.zero;
            return;
        }
        float scale = Ui.CanvasRect.localScale.x > 0 ? Ui.CanvasRect.localScale.x : 1f;
        float unit = PreviewUnit();
        dragGame = new Vector2(Mathf.Round((pos.x - dragStart.x) / scale / unit), Mathf.Round((pos.y - dragStart.y) / scale / unit));
        if (mouse.leftButton.isPressed) return;
        dragging = false;
        if (dragGame == Vector2.zero || draft == null) return;
        if (dragAnim == "idle")
        {
            var (x, y) = WholeOffset();
            SetWholeOffset(x + dragGame.x, y + dragGame.y);
        }
        else
        {
            var (x, y) = draft.ArtPair(dragAnim, "offset") ?? (0, 0);
            string was = artSelected;
            artSelected = dragAnim;
            SetOwnOffset(x + dragGame.x, y + dragGame.y);
            artSelected = was;
        }
        dragGame = Vector2.zero;
    }

    /// <summary>Space plays or pauses the preview; Left and Right step through it.</summary>
    private static bool HandleArtKeys(InputKeyboard k)
    {
        if (page != Page.Art || !CustomArtEnemy()) return false;
        if (Pressed(k, Key.Space)) TogglePreviewPlay();
        else if (Pressed(k, Key.LeftArrow)) StepPreview(-1);
        else if (Pressed(k, Key.RightArrow)) StepPreview(1);
        else return false;
        return true;
    }

    // ---- the whole enemy ----------------------------------------------------------------------

    private static bool WholeEditable(out BattleDraft d)
    {
        d = draft!;
        return draft != null && EnemyEditable() && draft.HasArt("idle");
    }

    private static string ArtSizeText()
    {
        double? size = draft?.ArtNumber("", "size");
        var idle = previewSet?.Result.Measured.TryGetValue("idle", out var m) == true ? m : null;
        double shown = size ?? previewSet?.Result.Size ?? 0;
        string looks = idle != null && shown > 0 ? $" (looks {ArtEditing.Num(Math.Round(idle.VisibleH * shown / idle.FrameH))} px tall; Mantis looks 47)" : "";
        if (size is double s) return $"Idle frame {ArtEditing.Num(s)} px{looks}";
        return shown > 0 ? $"Idle frame {ArtEditing.Num(Math.Round(shown))} px, automatic{looks}" : "Size: automatic";
    }

    private static void StepArtSize(int by)
    {
        double now = draft?.ArtNumber("", "size") ?? Math.Round(previewSet?.Result.Size ?? 64);
        SetArtSize(Math.Round(now / 4) * 4 + by * 4);
    }

    private static void SetArtSize(double? size)
    {
        if (!WholeEditable(out var d)) return;
        artSizeAuto = false;
        d.SetArtValue("", "size", size is double s ? BattleDraft.ArtNumberNode(Math.Clamp(Math.Round(s), EnemyArtReader.MinSize, EnemyArtReader.MaxSize)) : null);
    }

    private static void StepWholeOffset(int x, int y)
    {
        var (ox, oy) = WholeOffset();
        SetWholeOffset(ox + x, oy + y);
    }

    private static void SetWholeOffset(double x, double y)
    {
        if (!WholeEditable(out var d)) return;
        x = Math.Clamp(Math.Round(x), -EnemyArtReader.MaxOffset, EnemyArtReader.MaxOffset);
        y = Math.Clamp(Math.Round(y), -EnemyArtReader.MaxOffset, EnemyArtReader.MaxOffset);
        d.SetArtValue("", "offset", x == 0 && y == 0 ? null : BattleDraft.ArtPairNode((x, y)));
    }

    private static void ToggleWholeFlip()
    {
        if (!WholeEditable(out var d)) return;
        bool now = !(d.ArtBool("", "flip") ?? false);
        d.SetArtValue("", "flip", now ? JsonValue.Create(true) : null);
    }

    // Crisp pixels: worked out from the art (automatic), on (crisp) or off (smooth).
    private static string CrispText() => draft?.ArtBool("", "smooth") switch
    {
        false => "Crisp pixels: on",
        true => "Crisp pixels: off",
        _ => $"Crisp pixels: auto ({(previewSet?.Result.Smooth == true ? "off" : "on")})",
    };

    private static void CycleCrisp()
    {
        if (!WholeEditable(out var d)) return;
        bool? next = d.ArtBool("", "smooth") switch { null => false, false => true, _ => null };
        d.SetArtValue("", "smooth", next is bool b ? JsonValue.Create(b) : null);
        Say(next switch
        {
            false => "Crisp pixels: every pixel stays sharp, like the game's own enemies.",
            true => "Crisp pixels off: the art is smoothed, which suits drawings and photos.",
            _ => "Crisp pixels: worked out from the art (sharp when it's drawn at whole pixels).",
        }, 4f);
    }

    private static void ToggleShadow()
    {
        if (!WholeEditable(out var d)) return;
        bool now = !(d.ArtBool("", "shadow") ?? true);
        d.SetArtValue("", "shadow", now ? null : JsonValue.Create(false));
    }

    private static readonly TextField ArtSizeField = ArtField("Idle frame (px)", () => ArtEditing.Num(draft?.ArtNumber("", "size") ?? Math.Round(previewSet?.Result.Size ?? 64)),
        SetArtSize, EnemyArtReader.MinSize, EnemyArtReader.MaxSize,
        "Type how tall the idle's frame is in game pixels (the screen is 270), then Enter. Empty works it out. Esc cancels.", optional: true);
    private static readonly TextField ArtOffsetXField = ArtField("Sideways", () => ArtEditing.Num(WholeOffset().X), v => SetWholeOffset(v ?? 0, WholeOffset().Y),
        -EnemyArtReader.MaxOffset, EnemyArtReader.MaxOffset, "Type how many game pixels to move the enemy right (left is negative), then Enter. Esc cancels.", optional: true);
    private static readonly TextField ArtOffsetYField = ArtField("Up/down", () => ArtEditing.Num(WholeOffset().Y), v => SetWholeOffset(WholeOffset().X, v ?? 0),
        -EnemyArtReader.MaxOffset, EnemyArtReader.MaxOffset, "Type how many game pixels to move the enemy up (down is negative), then Enter. Esc cancels.", optional: true);
}
