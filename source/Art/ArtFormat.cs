using System.Globalization;
using System.Text.Json;

namespace NocturneFlatScroll;

internal enum ArtKind { Image, Sheet, Gif, Video }

/// <summary>One animation of a custom-art enemy (idle, attack, hurt or defeat), as checked at package load.</summary>
internal sealed class ArtAnimationSpec
{
    internal string Name = "";
    /// <summary>The file's path inside the battle.</summary>
    internal string File = "";
    internal ArtKind Kind;
    internal MediaInfo Media = new();
    internal long Bytes;
    // Sprite sheets: the grid, and the cells used (left to right, then down, like the game's sheets).
    internal int Columns = 1, Rows = 1, First;
    /// <summary>Null: up to the last cell with anything in it.</summary>
    internal int? Frames;
    internal double? Fps;
    /// <summary>How long each frame shows, in ms, as written (checked against the frames at load).</summary>
    internal int[]? Times;
    internal double Speed = 1;
    /// <summary>How long a still picture shows (not the idle, which holds it).</summary>
    internal double? Seconds;
    internal bool Loop;
    internal int? HitFrame;
    internal double? HitTime;
    /// <summary>Size compared to the idle's source pixels.</summary>
    internal double Scale = 1;
    internal double OffsetX, OffsetY;
    internal bool Flip;
    /// <summary>Where the character stands, in the frame's pixels from its top-left; null finds it.</summary>
    internal (double X, double Y)? Feet;
    internal bool KeyCorner;
    internal int? KeyRgb;
    internal double KeyRange = 0.15;

    internal bool HasKey => KeyCorner || KeyRgb != null;
    internal int Cells => Columns * Rows;
    /// <summary>One frame's size in the file's pixels (a sheet's cell); 0 when not known yet.</summary>
    internal int FrameWidth => Kind == ArtKind.Sheet ? Media.Width / Columns : Media.Width;
    internal int FrameHeight => Kind == ArtKind.Sheet ? Media.Height / Rows : Media.Height;

    internal string KindName => Kind switch
    {
        ArtKind.Image => "picture",
        ArtKind.Sheet => "sheet",
        ArtKind.Gif => "GIF",
        _ => Media.Type == MediaType.WebM ? "WebM" : "MP4",
    };

    /// <summary>Whether this animation's frames come from the same file and grid as <paramref name="other"/>'s.</summary>
    internal bool SameSheet(ArtAnimationSpec other) =>
        File.Equals(other.File, StringComparison.OrdinalIgnoreCase) && Kind == other.Kind && Columns == other.Columns && Rows == other.Rows;
}

/// <summary>A custom-art enemy's art as checked at package load. Null from the reader means no usable idle.</summary>
internal sealed class EnemyArtSpec
{
    internal readonly Dictionary<string, ArtAnimationSpec> Animations = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Animations that were set but can't be used, and why.</summary>
    internal readonly Dictionary<string, string> Dropped = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The idle frame's height in game pixels; null works it out from the idle.</summary>
    internal double? Size;
    internal double OffsetX, OffsetY;
    internal bool Flip;
    /// <summary>Null: crisp when every frame is drawn at a whole number of game pixels per pixel.</summary>
    internal bool? Smooth;
    internal bool Shadow = true;

    internal ArtAnimationSpec Idle => Animations["idle"];

    internal ArtAnimationSpec? Get(string name) => Animations.TryGetValue(name, out var a) ? a : null;

    /// <summary>"idle GIF, attack sheet, hurt picture", for the log.</summary>
    internal string Summary() =>
        string.Join(", ", EnemyArtReader.Names.Where(Animations.ContainsKey).Select(n => $"{n} {Animations[n].KindName}"));
}

/// <summary>
/// Reads and checks battle.json's enemy "art" (custom-art mode). It never throws: a bad value is
/// noted and left out or kept in range, a bad animation is noted and dropped (its stand-in
/// plays), and a bad idle means the enemy looks like its game enemy. Only the start of each art
/// file is read here; the pixels are decoded when a fight loads. Stats and info boxes are never
/// affected. This file has no Unity or game dependencies.
/// </summary>
internal static class EnemyArtReader
{
    internal static readonly string[] Names = { "idle", "attack", "hurt", "defeat" };
    private static readonly string[] Reserved = { "windup", "stunned", "staggered", "victory", "intro" };
    private static readonly HashSet<string> ArtKeys = new(StringComparer.OrdinalIgnoreCase) { "size", "offset", "flip", "smooth", "shadow" };
    private static readonly HashSet<string> AnimationKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "file", "kind", "columns", "rows", "first", "frames", "fps", "times", "speed", "seconds", "loop",
        "hitFrame", "hitTime", "scale", "offset", "flip", "feet", "keyColor", "keyRange"
    };

    internal const long MaxPictureBytes = 32L * 1024 * 1024;
    internal const long MaxVideoBytes = 256L * 1024 * 1024;
    /// <summary>How much of each art file is read at package load.</summary>
    internal const int HeadBytes = 256 * 1024;
    internal const int MaxPictureSide = 4096;
    internal const int MaxGrid = 64;
    internal const int MinCell = 4;
    internal const int MaxVideoSide = 1920;
    internal const long MaxVideoPixels = 1920L * 1080;
    internal const double MaxIdleVideoSeconds = 60, MaxOtherVideoSeconds = 10;
    internal const double MinSize = 8, MaxSize = 480, MaxOffset = 200;

    /// <summary>Reads the art as the other overload does, adding each art file's stamp to <paramref name="stamps"/>.</summary>
    internal static EnemyArtSpec? Read(JsonElement art, PackageFiles files, string lookName, List<string> problems, List<string> stamps, out string? unusable,
        bool evenWithoutIdle = false) =>
        Read(art, files, lookName, problems, name => stamps.Add(files.Stamp(name)), out unusable, evenWithoutIdle);

    /// <param name="lookName">The game enemy it looks like when the art can't be used, for messages.</param>
    /// <param name="stamp">
    /// Called with each art file's name before it's read (a missing one too), so the package's
    /// fingerprint covers the art and a battle whose art changes or turns up is loaded again.
    /// </param>
    /// <param name="unusable">Why there's no usable art, when the result is null (or has no idle).</param>
    /// <param name="evenWithoutIdle">
    /// For the battle creator: the other animations are read and returned even when there's no
    /// usable idle (the result then has no "idle"; <paramref name="unusable"/> says why).
    /// </param>
    internal static EnemyArtSpec? Read(JsonElement art, PackageFiles files, string lookName, List<string> problems, Action<string> stamp, out string? unusable,
        bool evenWithoutIdle = false)
    {
        unusable = null;
        if (art.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            unusable = "it has no \"art\"";
            problems.Add($"the enemy is set to custom art but has no \"art\", so it looks like {lookName}");
            return null;
        }
        if (art.ValueKind != JsonValueKind.Object)
        {
            unusable = "its \"art\" isn't an object";
            problems.Add($"the enemy's \"art\" must be an object, so it looks like {lookName}");
            return null;
        }
        var spec = new EnemyArtSpec();
        foreach (var p in art.EnumerateObject())
        {
            if (ArtKeys.Contains(p.Name) || Names.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) continue;
            problems.Add(Reserved.Contains(p.Name, StringComparer.OrdinalIgnoreCase)
                ? $"the enemy art's \"{p.Name}\" isn't used yet (it's kept in the file)"
                : $"the enemy art's \"{p.Name}\" isn't used (it's kept in the file)");
        }

        foreach (var name in Names)
        {
            var node = Prop(art, name);
            if (node.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
            var notes = new List<string>();
            try
            {
                var a = ReadAnimation(name, node, files, notes, stamp);
                spec.Animations[name] = a;
                foreach (var note in notes) problems.Add($"the enemy art's {name}: {note}");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
            {
                foreach (var note in notes) problems.Add($"the enemy art's {name}: {note}");
                spec.Dropped[name] = ex.Message;
            }
        }

        if (!spec.Animations.ContainsKey("idle"))
        {
            bool dropped = spec.Dropped.TryGetValue("idle", out var why);
            unusable = dropped ? "idle: " + why : "it has no idle";
            problems.Add(dropped
                ? $"the enemy's idle can't be used ({why}), so it looks like {lookName}"
                : $"the enemy art has no idle, so it looks like {lookName}");
            if (!evenWithoutIdle) return null;
        }
        foreach (var dropped in spec.Dropped.Where(d => d.Key != "idle"))
            problems.Add($"the enemy art's {dropped.Key} can't be used ({dropped.Value}); {StandIn(dropped.Key, spec)}");

        // The whole enemy: size, place, facing.
        spec.Size = Number(art, "size", MinSize, MaxSize, null, "the art's \"size\"", problems);
        (spec.OffsetX, spec.OffsetY) = Pair(art, "offset", MaxOffset, "the art's \"offset\"", problems) ?? (0, 0);
        spec.Flip = Bool(art, "flip", false, "the art's \"flip\"", problems);
        var smooth = Prop(art, "smooth");
        spec.Smooth = smooth.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        if (spec.Smooth == null && smooth.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            problems.Add("the art's \"smooth\" must be true or false, so it's worked out from the art");
        spec.Shadow = Bool(art, "shadow", true, "the art's \"shadow\"", problems);

        // The timing, where the file's frames are already known, so problems show before a fight.
        foreach (var a in spec.Animations.Values)
        {
            var notes = new List<string>();
            if (a.Kind == ArtKind.Image || (a.Kind == ArtKind.Sheet && a.Frames != null))
                ArtTimeline.Plan(a, a.Kind == ArtKind.Image ? 1 : a.Frames!.Value, null, 0, notes);
            else if (a.Kind == ArtKind.Gif && a.Media.Frames > 0)
                ArtTimeline.Plan(a, a.Media.Frames, a.Media.FrameMs, 0, notes);
            else if (a.Kind == ArtKind.Video && a.Media.Seconds > 0)
                ArtTimeline.Plan(a, 1, null, a.Media.Seconds, notes);
            foreach (var note in notes) problems.Add($"the enemy art's {a.Name}: {note}");
        }
        return spec;
    }

    /// <summary>What shows instead of an animation that's missing or can't be used.</summary>
    internal static string StandIn(string name, EnemyArtSpec spec) => name switch
    {
        "attack" => $"the idle stands in, hit at {ArtLoadResult.Sec(ArtTimeline.NoArtHit)} s",
        "hurt" => "the idle shows instead",
        "defeat" => spec.Animations.ContainsKey("hurt") ? "the hurt shows instead" : "the idle holds instead",
        // Once the enemy is on the rig (a later fight, or a video that fails after the fight started).
        "idle" => Names.Any(n => n != "idle" && spec.Animations.ContainsKey(n))
            ? "another animation's first frame stands in" : "the rig's own picture shows",
        _ => "",
    };

    private static ArtAnimationSpec ReadAnimation(string name, JsonElement node, PackageFiles files, List<string> notes, Action<string> stamp)
    {
        if (node.ValueKind == JsonValueKind.String)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string?> { ["file"] = node.GetString() }));
            node = doc.RootElement.Clone();
        }
        if (node.ValueKind != JsonValueKind.Object) throw new InvalidDataException("it must be a file name or an object with a \"file\"");
        foreach (var p in node.EnumerateObject())
            if (!AnimationKeys.Contains(p.Name)) notes.Add($"\"{p.Name}\" isn't used (it's kept in the file)");

        var a = new ArtAnimationSpec { Name = name };
        string file = Text(node, "file") ?? throw new InvalidDataException("it names no \"file\"");
        a.File = PackageFiles.SafeName(file) ?? throw new InvalidDataException($"\"{file}\" must be a file inside the battle");
        // A missing file is stamped too, so the battle is loaded again when it turns up.
        stamp(a.File);
        if (!files.Exists(a.File)) throw new InvalidDataException($"{a.File} is missing");
        a.Bytes = files.Length(a.File);
        byte[] head = files.ReadHead(a.File, HeadBytes);
        bool complete = a.Bytes <= head.Length;
        try { a.Media = MediaSniff.Probe(head, complete); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{a.File} {ex.Message}"); }
        foreach (var note in a.Media.Notes) notes.Add($"{a.File} {note}");
        long max = a.Media.IsVideo ? MaxVideoBytes : MaxPictureBytes;
        if (a.Bytes > max)
            throw new InvalidDataException($"{a.File} is {a.Bytes / (1024 * 1024)} MB; {(a.Media.IsVideo ? "videos" : "pictures and GIFs")} can be at most {max / (1024 * 1024)} MB");

        // The kind: what the file is, or what's written when a still picture can be either. A
        // picture is only cut into frames when "kind" says "sheet": one without it stays one
        // flat picture, whatever it looks like.
        bool grid = Prop(node, "columns").ValueKind != JsonValueKind.Undefined || Prop(node, "rows").ValueKind != JsonValueKind.Undefined;
        string? written = Text(node, "kind")?.Trim().ToLowerInvariant();
        ArtKind actual = a.Media.IsVideo ? ArtKind.Video : a.Media.Type == MediaType.Gif ? ArtKind.Gif : ArtKind.Image;
        ArtKind? wanted = written switch
        {
            null or "" => null,
            "image" or "picture" or "still" => ArtKind.Image,
            "sheet" or "spritesheet" or "sprite sheet" => ArtKind.Sheet,
            "gif" or "animated" => ArtKind.Gif,
            "video" => ArtKind.Video,
            _ => Unknown(written, notes),
        };
        a.Kind = actual;
        if (wanted is ArtKind w && w != actual)
        {
            if (w is ArtKind.Image or ArtKind.Sheet && actual is ArtKind.Image or ArtKind.Sheet) a.Kind = w;
            else notes.Add($"{a.File} is {Article(a.Media.TypeName)} {a.Media.TypeName}, so it's used as one (\"kind\" says {written})");
        }

        switch (a.Kind)
        {
            case ArtKind.Image:
                CheckPicture(a);
                a.Seconds = Number(node, "seconds", 0.05, 10, null, "\"seconds\"", notes);
                if (name == "idle" && a.Seconds != null) notes.Add("the idle holds its picture, so \"seconds\" isn't used");
                break;
            case ArtKind.Sheet:
                CheckPicture(a);
                ReadSheet(node, a, notes);
                break;
            case ArtKind.Gif:
                a.Fps = Number(node, "fps", 1, 60, null, "\"fps\"", notes);
                a.Times = Times(node, notes);
                a.Speed = Number(node, "speed", 0.1, 10, null, "\"speed\"", notes) ?? 1;
                break;
            case ArtKind.Video:
                a.Speed = Number(node, "speed", 0.1, 10, null, "\"speed\"", notes) ?? 1;
                CheckVideo(a);
                break;
        }
        if (a.Kind == ArtKind.Image && grid)
            notes.Add($"\"columns\" and \"rows\" are for sprite sheets, so {a.File} shows as one picture (add \"kind\": \"sheet\" to cut it into frames)");
        else if (a.Kind != ArtKind.Sheet && grid) notes.Add("\"columns\" and \"rows\" are for sprite sheets, so they aren't used");
        if (a.Kind is not ArtKind.Gif and not ArtKind.Video && Has(node, "speed")) notes.Add("\"speed\" is for GIFs and videos, so it isn't used");
        if (a.Kind is ArtKind.Image or ArtKind.Video && (Has(node, "fps") || Has(node, "times"))) notes.Add("\"fps\" and \"times\" are for sheets and GIFs, so they aren't used");
        if (a.Kind != ArtKind.Image && Has(node, "seconds")) notes.Add("\"seconds\" is for still pictures, so it isn't used");

        // The idle always loops; attacks and hurts play once each time; a defeat holds its last frame unless it loops.
        var loop = Prop(node, "loop");
        a.Loop = name == "idle" || (name == "defeat" && loop.ValueKind == JsonValueKind.True);
        if (name == "idle" && loop.ValueKind == JsonValueKind.False) notes.Add("the idle always loops");
        if (name is "attack" or "hurt" && loop.ValueKind == JsonValueKind.True) notes.Add($"the {name} plays once each time, so \"loop\" isn't used");

        if (name == "attack")
        {
            if (Has(node, "hitFrame"))
            {
                if (a.Kind is ArtKind.Sheet or ArtKind.Gif) a.HitFrame = (int?)Number(node, "hitFrame", 0, 100000, 0, "\"hitFrame\"", notes);
                else notes.Add("a picture's or a video's hit is set with \"hitTime\", so \"hitFrame\" isn't used");
            }
            a.HitTime = Number(node, "hitTime", 0, 600, null, "\"hitTime\"", notes);
        }
        else if (Has(node, "hitFrame") || Has(node, "hitTime")) notes.Add($"only the attack has a hit, so the {name}'s isn't used");

        if (name != "idle") a.Scale = Number(node, "scale", 0.05, 20, null, "\"scale\"", notes) ?? 1;
        else if (Has(node, "scale")) notes.Add("the idle's \"scale\" isn't used (set the art's \"size\" instead)");
        (a.OffsetX, a.OffsetY) = Pair(node, "offset", MaxOffset, "\"offset\"", notes) ?? (0, 0);
        a.Flip = Bool(node, "flip", false, "\"flip\"", notes);
        ReadFeet(node, a, notes);
        ReadKey(node, a, notes);
        return a;
    }

    private static string Article(string typeName) => typeName == "MP4" ? "an" : "a";

    private static ArtKind? Unknown(string kind, List<string> notes)
    {
        notes.Add($"\"kind\" must be image, sheet, gif or video, not \"{kind}\", so the file's own kind is used");
        return null;
    }

    private static void CheckPicture(ArtAnimationSpec a)
    {
        if (a.Media.Width > MaxPictureSide || a.Media.Height > MaxPictureSide)
            throw new InvalidDataException($"{a.File} says it is {a.Media.Width} x {a.Media.Height}; pictures can be at most {MaxPictureSide} on a side");
    }

    private static void CheckVideo(ArtAnimationSpec a)
    {
        var facts = a.Media.Video;
        // An MP4 whose index comes late is checked when a fight loads it.
        if (facts == null || !facts.IndexFound) return;
        CheckVideoFacts(a, facts);
    }

    /// <summary>
    /// Whether an animation's video must say how long it plays: the attack's hit and the hurt's
    /// time on screen come from it. The idle loops and the defeat holds, so theirs can be missing
    /// (browser recordings and some fragmented MP4s leave it out).
    /// </summary>
    internal static bool NeedsLength(string anim) => anim is "attack" or "hurt";

    /// <summary>The video limits, also checked again when a fight loads a video whose index came late.</summary>
    internal static void CheckVideoFacts(ArtAnimationSpec a, VideoFacts facts)
    {
        string? problem = facts.Problem();
        if (problem != null) throw new InvalidDataException($"{a.File} {problem}");
        if (facts.Width > MaxVideoSide || facts.Height > MaxVideoSide || (long)facts.Width * facts.Height > MaxVideoPixels)
            throw new InvalidDataException($"{a.File} is {facts.Width} x {facts.Height}; videos can be at most 1920 x 1080");
        if (facts.Width < 1 || facts.Height < 1) throw new InvalidDataException($"{a.File} doesn't say how big its picture is");
        if (facts.Seconds <= 0 && NeedsLength(a.Name))
            throw new InvalidDataException($"{a.File} doesn't say how long it plays, and the {a.Name} needs that for its timing; re-save it first (for example with ffmpeg)");
        double seconds = facts.Seconds / a.Speed;
        double limit = a.Name == "idle" ? MaxIdleVideoSeconds : MaxOtherVideoSeconds;
        if (a.Name != "attack" && seconds > limit)
            throw new InvalidDataException($"{a.File} plays for {ArtTimeline.Num(seconds)} s; the {a.Name} can be at most {limit:0} s");
    }

    private static void ReadSheet(JsonElement node, ArtAnimationSpec a, List<string> notes)
    {
        int w = a.Media.Width, h = a.Media.Height;
        a.Columns = (int)(Number(node, "columns", 1, MaxGrid, 0, "\"columns\"", notes) ?? 1);
        a.Rows = (int)(Number(node, "rows", 1, MaxGrid, 0, "\"rows\"", notes) ?? 1);
        int cw = w / a.Columns, ch = h / a.Rows;
        if (cw < MinCell || ch < MinCell)
            throw new InvalidDataException($"{a.File} ({w} x {h}) split into {a.Columns} x {a.Rows} gives {cw} x {ch} frames; frames must be at least {MinCell} px");
        if (w % a.Columns != 0)
            notes.Add($"{a.File} doesn't split evenly into {a.Columns} columns; each frame is {cw} px wide and the last {w % a.Columns} px are left out");
        if (h % a.Rows != 0)
            notes.Add($"{a.File} doesn't split evenly into {a.Rows} rows; each frame is {ch} px tall and the last {h % a.Rows} px are left out");
        int cells = a.Cells;
        a.First = (int)(Number(node, "first", 0, cells - 1, 0, "\"first\"", notes) ?? 0);
        a.Frames = (int?)Number(node, "frames", 1, cells - a.First, 0, "\"frames\"", notes);
        a.Fps = Number(node, "fps", 1, 60, null, "\"fps\"", notes);
        a.Times = Times(node, notes);
    }

    private static int[]? Times(JsonElement node, List<string> notes)
    {
        var times = Prop(node, "times");
        if (times.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (times.ValueKind == JsonValueKind.Array && times.GetArrayLength() <= 1000)
        {
            var list = new List<int>();
            foreach (var t in times.EnumerateArray())
            {
                if (!TryNumber(t, out double ms) || ms < 10 || ms > 10000) { list = null; break; }
                list.Add((int)Math.Round(ms));
            }
            if (list != null && list.Count > 0) return list.ToArray();
        }
        notes.Add("\"times\" must be a list of numbers from 10 to 10000 (ms), so it isn't used");
        return null;
    }

    private static void ReadFeet(JsonElement node, ArtAnimationSpec a, List<string> notes)
    {
        var feet = Pair(node, "feet", double.MaxValue, "\"feet\"", notes);
        if (feet == null) return;
        int w = a.FrameWidth, h = a.FrameHeight;
        var (x, y) = feet.Value;
        // Known sizes are checked now; a video whose size comes later is checked by the load.
        if (w > 0 && h > 0 && (x < -w || x > 2 * w || y < -h || y > 2 * h))
        {
            notes.Add($"\"feet\" [{ArtTimeline.Num(x)}, {ArtTimeline.Num(y)}] is far outside the {w} x {h} frame, so the feet are found from the picture");
            return;
        }
        a.Feet = (x, y);
    }

    private static void ReadKey(JsonElement node, ArtAnimationSpec a, List<string> notes)
    {
        string? key = Text(node, "keyColor")?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            if (Has(node, "keyColor")) notes.Add("\"keyColor\" must be like \"#00FF00\" or \"corner\", so it isn't used");
            return;
        }
        if (a.Kind == ArtKind.Video)
        {
            notes.Add("see-through colours work on pictures, sheets and GIFs, not on videos; Turn into frames on the Art page makes the video a sprite sheet that can have one");
            return;
        }
        if (key.Equals("corner", StringComparison.OrdinalIgnoreCase)) a.KeyCorner = true;
        else if (key.Length == 7 && key[0] == '#' && int.TryParse(key.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) a.KeyRgb = rgb;
        else
        {
            notes.Add("\"keyColor\" must be like \"#00FF00\" or \"corner\", so it isn't used");
            return;
        }
        a.KeyRange = Number(node, "keyRange", 0, 1, null, "\"keyRange\"", notes) ?? 0.15;
    }

    // ---- JSON helpers: keys in any letter case, numbers as numbers or strings ----------------------

    internal static JsonElement Prop(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return default;
        foreach (var p in obj.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return default;
    }

    private static bool Has(JsonElement obj, string name) => Prop(obj, name).ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);

    private static string? Text(JsonElement obj, string name)
    {
        var v = Prop(obj, name);
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static bool TryNumber(JsonElement v, out double value)
    {
        value = 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetDouble(out value) && double.IsFinite(value);
        if (v.ValueKind == JsonValueKind.String)
            return double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        return false;
    }

    /// <summary>A number in range, or null when missing. A wrong one is noted and left out; one out of range is kept in range.</summary>
    private static double? Number(JsonElement obj, string name, double min, double max, int? decimals, string what, List<string> notes)
    {
        var v = Prop(obj, name);
        if (v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (!TryNumber(v, out double value))
        {
            notes.Add($"{what} must be a number, so it isn't used");
            return null;
        }
        if (decimals is int d) value = Math.Round(value, d);
        if (value < min || value > max)
        {
            notes.Add($"{what} is {ArtTimeline.Num(value)}; it's kept between {ArtTimeline.Num(min)} and {ArtTimeline.Num(max)}");
            value = Math.Clamp(value, min, max);
        }
        return value;
    }

    private static (double, double)? Pair(JsonElement obj, string name, double limit, string what, List<string> notes)
    {
        var v = Prop(obj, name);
        if (v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != 2 || !TryNumber(v[0], out double x) || !TryNumber(v[1], out double y))
        {
            notes.Add($"{what} must be two numbers, like [0, -10], so it isn't used");
            return null;
        }
        if (Math.Abs(x) > limit || Math.Abs(y) > limit)
        {
            notes.Add($"{what} is kept within {ArtTimeline.Num(limit)} game pixels");
            return (Math.Clamp(x, -limit, limit), Math.Clamp(y, -limit, limit));
        }
        return (x, y);
    }

    private static bool Bool(JsonElement obj, string name, bool fallback, string what, List<string> notes)
    {
        var v = Prop(obj, name);
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        if (v.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) notes.Add($"{what} must be true or false, so it isn't used");
        return fallback;
    }
}
