using System.Globalization;

namespace NocturneFlatScroll;

/// <summary>
/// The battle creator's custom-art helpers that need no Unity: the four kinds an animation can be
/// and the files each takes, checking a picked file against its kind and the limits before it's
/// copied, the one-line summaries, the stepper values, and the settings the creator fills in once
/// a file is chosen (the idle's size, the attack's hit, a scale to match the idle). Messages are
/// short and plain, and show in the creator as they are.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class ArtEditing
{
    /// <summary>The kinds in the order the creator offers them.</summary>
    internal static readonly ArtKind[] Kinds = { ArtKind.Image, ArtKind.Gif, ArtKind.Video, ArtKind.Sheet };

    /// <summary>Where a game enemy's feet are: this many game pixels below the top of a 16:9 screen (480 x 270 game pixels).</summary>
    internal const double FeetBelowTop = 72;
    /// <summary>Art taller than this above its feet is moved down when its idle is chosen, by at most MaxTallMove.</summary>
    internal const double TallTop = 66, MaxTallMove = 50;

    internal static string Label(ArtKind kind) => kind switch
    {
        ArtKind.Image => "Image",
        ArtKind.Gif => "GIF",
        ArtKind.Video => "Video",
        _ => "Sprite sheet",
    };

    /// <summary>The kind as battle.json writes it.</summary>
    internal static string Json(ArtKind kind) => kind switch
    {
        ArtKind.Image => "image",
        ArtKind.Gif => "gif",
        ArtKind.Video => "video",
        _ => "sheet",
    };

    /// <summary>A written "kind", as the loader reads it; null when it's missing or unknown.</summary>
    internal static ArtKind? Parse(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "image" or "picture" or "still" => ArtKind.Image,
        "sheet" or "spritesheet" or "sprite sheet" => ArtKind.Sheet,
        "gif" or "animated" => ArtKind.Gif,
        "video" => ArtKind.Video,
        _ => null,
    };

    /// <summary>What each kind is, for the list the creator asks from.</summary>
    internal static string Hint(ArtKind kind) => kind switch
    {
        ArtKind.Image => "one still picture (PNG or JPEG), shown as it is",
        ArtKind.Gif => "an animated GIF, played with its own timing",
        ArtKind.Video => "an MP4 (H.264) or WebM (VP8) video",
        _ => "one PNG or JPEG with the frames in a grid, read left to right, then down, like the game's own",
    };

    /// <summary>The Windows picker's title for a kind.</summary>
    internal static string PickerTitle(string anim, ArtKind kind) => kind switch
    {
        ArtKind.Image => $"Choose the {anim}: one picture (PNG or JPEG)",
        ArtKind.Gif => $"Choose the {anim}: an animated GIF",
        ArtKind.Video => $"Choose the {anim}: a video (MP4 or WebM)",
        _ => $"Choose the {anim}: a sprite sheet (PNG or JPEG)",
    };

    // ---- a picked file ----------------------------------------------------------------------

    /// <summary>
    /// Checks a file picked as <paramref name="kind"/> for <paramref name="anim"/>, from its first
    /// bytes (<paramref name="head"/>; all of it when it holds <paramref name="length"/> bytes).
    /// Null when it can be used, else why not. Never throws on what the bytes hold.
    /// </summary>
    internal static string? CheckPicked(string anim, ArtKind kind, string name, byte[] head, long length, out MediaInfo? info)
    {
        info = null;
        var type = MediaSniff.TypeOf(head);
        switch (type)
        {
            case MediaType.WebP:
            case MediaType.Bmp:
            case MediaType.Tiff:
            case MediaType.Heic:
                return $"{name} is a {MediaSniff.Name(type)} picture, which can't be read yet. Save it as a PNG{(kind == ArtKind.Gif ? " or a GIF" : "")} first.";
            case MediaType.Unknown:
                return $"{name} isn't a picture, GIF or video the game can show.";
        }
        bool video = type is MediaType.Mp4 or MediaType.WebM, gif = type == MediaType.Gif;
        string what = video ? "a video" : gif ? "a GIF" : $"a {MediaSniff.Name(type)} picture";
        string fits = video ? "Video" : gif ? "GIF" : "Image or Sprite sheet";
        if (kind is ArtKind.Image or ArtKind.Sheet && (video || gif)) return $"{name} is {what}. {Label(kind)} takes a PNG or JPEG; choose {fits} for this file.";
        if (kind == ArtKind.Gif && !gif) return $"{name} is {what}, not a GIF. Choose {fits} for this file.";
        if (kind == ArtKind.Video && !video) return $"{name} is {what}, not a video. Choose {fits} for this file.";
        long max = video ? EnemyArtReader.MaxVideoBytes : EnemyArtReader.MaxPictureBytes;
        if (length > max)
            return $"{name} is {length / (1024 * 1024)} MB. {(video ? "Videos" : "Pictures and GIFs")} can be at most {max / (1024 * 1024)} MB.";
        try { info = MediaSniff.Probe(head, head.LongLength >= length); }
        catch (InvalidDataException ex) { return $"{name} {ex.Message}."; }
        if (!video && !gif && (info.Width > EnemyArtReader.MaxPictureSide || info.Height > EnemyArtReader.MaxPictureSide))
            return $"{name} is {info.Width} x {info.Height}. Pictures can be at most {EnemyArtReader.MaxPictureSide} on a side.";
        if (kind == ArtKind.Sheet && (info.Width < EnemyArtReader.MinCell || info.Height < EnemyArtReader.MinCell))
            return $"{name} is only {info.Width} x {info.Height}, too small to cut into frames. Choose Image for it.";
        if (video && info.Video is { IndexFound: true } facts) return CheckVideo(anim, name, facts);
        return null;
    }

    /// <summary>The video limits, from the video's facts (read from the whole file when its index comes late).</summary>
    internal static string? CheckVideo(string anim, string name, VideoFacts facts)
    {
        string? problem = facts.Problem();
        if (problem != null) return $"{name} {problem}.";
        if (facts.Width < 1 || facts.Height < 1) return $"{name} doesn't say how big its picture is.";
        if (facts.Width > EnemyArtReader.MaxVideoSide || facts.Height > EnemyArtReader.MaxVideoSide || (long)facts.Width * facts.Height > EnemyArtReader.MaxVideoPixels)
            return $"{name} is {facts.Width} x {facts.Height}. Videos can be at most 1920 x 1080.";
        double limit = anim == "idle" ? EnemyArtReader.MaxIdleVideoSeconds : EnemyArtReader.MaxOtherVideoSeconds;
        if (anim != "attack" && facts.Seconds > limit)
            return $"{name} plays for {Sec(facts.Seconds)} s. The {anim} can be at most {limit:0} s.";
        return null;
    }

    // ---- what the rows and steppers show ---------------------------------------------------

    /// <summary>
    /// The timeline an animation would have, when its frame count is known: from the file's own
    /// facts (a sheet's frames, a GIF read in full, a video's length), else from what was loaded
    /// for it with the same frames (<paramref name="loaded"/>). Null when it isn't known yet.
    /// </summary>
    internal static ArtTimeline? Timeline(ArtAnimationSpec a, MeasuredAnimation? loaded)
    {
        var notes = new List<string>();
        bool same = loaded != null && SameFrames(a, loaded.Spec);
        switch (a.Kind)
        {
            case ArtKind.Image:
                return ArtTimeline.Plan(a, 1, null, 0, notes);
            case ArtKind.Sheet:
                if (a.Frames != null) return ArtTimeline.Plan(a, a.Frames.Value, null, 0, notes);
                return same ? ArtTimeline.Plan(a, loaded!.Timeline.SourceFrames, null, 0, notes) : null;
            case ArtKind.Gif:
                if (a.Media.Frames > 0) return ArtTimeline.Plan(a, a.Media.Frames, a.Media.FrameMs, 0, notes);
                return same ? ArtTimeline.Plan(a, loaded!.Timeline.SourceFrames, loaded.OwnMs, 0, notes) : null;
            default:
                if (a.Media.Video is { IndexFound: true } facts) return ArtTimeline.Plan(a, 1, null, facts.Seconds, notes);
                return same && loaded!.Video != null ? ArtTimeline.Plan(a, 1, null, loaded.Video.Seconds, notes) : null;
        }
    }

    /// <summary>Whether two readings of an animation cut the same frames from the same file (only timing may differ).</summary>
    internal static bool SameFrames(ArtAnimationSpec a, ArtAnimationSpec b) =>
        a.File.Equals(b.File, StringComparison.OrdinalIgnoreCase) && a.Kind == b.Kind && a.Media.Width == b.Media.Width && a.Media.Height == b.Media.Height &&
        (a.Kind != ArtKind.Sheet || (a.Columns == b.Columns && a.Rows == b.Rows && a.First == b.First && a.Frames == b.Frames &&
                                     (a.Frames != null || (a.KeyCorner == b.KeyCorner && a.KeyRgb == b.KeyRgb && a.KeyRange == b.KeyRange))));

    /// <summary>"GIF, 12 frames, 1.04 s, hit on frame 7": an animation's row (frames counted from 1).</summary>
    internal static string Summary(ArtAnimationSpec a, ArtTimeline? t)
    {
        var parts = new List<string>();
        switch (a.Kind)
        {
            case ArtKind.Image: parts.Add($"Image {a.Media.Width} x {a.Media.Height}"); break;
            case ArtKind.Sheet: parts.Add($"Sheet {a.Columns} x {a.Rows}"); break;
            case ArtKind.Gif: parts.Add("GIF"); break;
            default: parts.Add(a.Media.Width > 0 ? $"{a.Media.TypeName} {a.Media.Width} x {a.Media.Height}" : a.Media.TypeName); break;
        }
        if (t == null) return string.Join(", ", parts);
        if (a.Kind is ArtKind.Sheet or ArtKind.Gif) parts.Add(Frames(t.SourceFrames));
        if (a.Kind != ArtKind.Image || a.Name != "idle") parts.Add($"{Sec(t.Length)} s");
        if (t.Hit >= 0) parts.Add(t.HitFrame >= 0 ? $"hit on frame {t.HitFrame + 1}" : $"hit at {Sec(t.Hit)} s");
        return string.Join(", ", parts);
    }

    internal static string Frames(int n) => n == 1 ? "1 frame" : $"{n} frames";

    internal static string Sec(double seconds) => seconds.ToString("0.00", CultureInfo.InvariantCulture);

    internal static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    internal static string Percent(double share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>A GIF's or video's playback speeds, and an animation's size compared to the idle.</summary>
    internal static readonly double[] SpeedSteps = { 0.1, 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4, 6, 8, 10 };
    internal static readonly double[] ScaleSteps = { 0.05, 0.1, 0.2, 0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10, 15, 20 };

    /// <summary>The next value along <paramref name="ladder"/> from <paramref name="value"/>, which may sit between its steps.</summary>
    internal static double Step(double[] ladder, double value, int direction)
    {
        if (direction > 0)
        {
            foreach (double v in ladder)
                if (v > value + 1e-9) return v;
            return ladder[^1];
        }
        for (int i = ladder.Length - 1; i >= 0; i--)
            if (ladder[i] < value - 1e-9) return ladder[i];
        return ladder[0];
    }

    /// <summary>The see-through colours the creator cycles through, as battle.json writes them.</summary>
    internal static readonly (string Name, string? Json)[] KeyColours =
    {
        ("off", null), ("corner colour", "corner"), ("green", "#00FF00"), ("blue", "#0000FF"), ("black", "#000000"), ("white", "#FFFFFF"), ("magenta", "#FF00FF"),
    };

    /// <summary>The name of a written see-through colour ("#12AB34" for one that isn't in the list).</summary>
    internal static string KeyName(string? json)
    {
        int i = KeyIndex(json);
        return i >= 0 ? KeyColours[i].Name : json!.Trim().ToUpperInvariant();
    }

    /// <summary>The colour after a written one in the cycle (a colour not in the list goes to off).</summary>
    internal static string? NextKey(string? json) => KeyColours[(KeyIndex(json) + 1) % KeyColours.Length].Json;

    private static int KeyIndex(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        for (int i = 1; i < KeyColours.Length; i++)
            if (KeyColours[i].Json!.Equals(json.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // ---- what the creator fills in once a file is chosen -----------------------------------

    /// <summary>A new idle's size (its frame's height in game pixels): the loader's default, in whole pixels.</summary>
    internal static double IdleSize(MeasuredAnimation idle) =>
        Math.Clamp(Math.Round(ArtDecode.DefaultSize(idle)), EnemyArtReader.MinSize, EnemyArtReader.MaxSize);

    /// <summary>How far the idle's visible top is above its feet, in game pixels at <paramref name="size"/>.</summary>
    internal static double TopAboveFeet(MeasuredAnimation idle, double size) => (idle.FeetY - idle.VisibleTop) * size / Math.Max(1, idle.FrameH);

    /// <summary>
    /// The move down (negative game pixels) for art whose top would leave the screen: more than
    /// TallTop above the feet moves it down by the rest, at most MaxTallMove (the game's own tall
    /// enemies sit 10 to 50 lower). 0 when it fits.
    /// </summary>
    internal static double TallOffset(MeasuredAnimation idle, double size)
    {
        double top = TopAboveFeet(idle, size);
        return top > TallTop ? -Math.Min(MaxTallMove, Math.Ceiling(top - TallTop)) : 0;
    }

    /// <summary>How many game pixels of the idle are above the top of the screen (0 when none), with the whole enemy moved up by <paramref name="offsetY"/>.</summary>
    internal static double OffTop(MeasuredAnimation idle, double size, double offsetY) =>
        Math.Max(0, TopAboveFeet(idle, size) - (FeetBelowTop - offsetY));

    /// <summary>
    /// A size compared to the idle that shows another animation's frames as tall as the idle's,
    /// when they're over twice or under half its height; null when they're close enough.
    /// </summary>
    internal static double? MatchScale(int idleFrameH, int frameH)
    {
        if (idleFrameH < 1 || frameH < 1) return null;
        double ratio = (double)frameH / idleFrameH;
        if (ratio <= 2 && ratio >= 0.5) return null;
        return Math.Clamp(Math.Round((double)idleFrameH / frameH, 3), 0.05, 20);
    }

    /// <summary>
    /// The hit as battle.json writes it, from a planned timeline: a sheet's or GIF's frame, or a
    /// still's or video's time in the file's own seconds (before its speed).
    /// </summary>
    internal static (int? HitFrame, double? HitTime) HitOf(ArtAnimationSpec a, ArtTimeline t)
    {
        if (t.Hit < 0) return (null, null);
        if (a.Kind is ArtKind.Sheet or ArtKind.Gif && t.HitFrame >= 0) return (t.HitFrame, null);
        double speed = a.Kind == ArtKind.Video ? a.Speed : 1;
        return (null, Math.Round(t.Hit * speed, 2));
    }
}
