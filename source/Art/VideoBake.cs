using System.Globalization;
using System.Text.Json.Nodes;

namespace NocturneFlatScroll;

/// <summary>
/// The battle creator's "Turn into frames" for a video animation, the parts that need no Unity:
/// which moments of the video become frames (a fixed rate of the battle's time, at most
/// ArtTimeline.MaxFrames, thinned evenly like a long GIF), how big a frame can be so the sheet is
/// one picture the battle loads without shrinking it again, where each frame goes in the sheet,
/// the attack's hit as a frame, and whether the video stands on one flat colour (a green screen)
/// that a see-through colour can cut out. The Unity half plays the video and reads the frames
/// (EnemyArt.Bake.cs); PngWriter saves the sheet (media.md 6.6).
/// This file has no Unity or game dependencies.
/// </summary>
internal static class VideoBake
{
    /// <summary>The frame rates offered (frames a second of the battle); the first is the default, like the game's own enemies.</summary>
    internal static readonly int[] Rates = { 12, 15, 24 };
    /// <summary>Room kept in the picture limit for the PNG's own bytes: a filter byte a row, zlib's blocks and the chunks.</summary>
    private const long PngSlack = 1024 * 1024;
    /// <summary>Edge pixels this close to the corner's colour count as the same background.</summary>
    private const double SameBackground = 0.2;
    /// <summary>How much of the edge must be that colour for the background to count as flat.</summary>
    private const double FlatShare = 0.85;

    /// <summary>Which frames a video gives, and how they're laid out in the sheet.</summary>
    internal sealed class Plan
    {
        internal int Fps;
        /// <summary>The animation's speed: the battle's seconds are the video's own divided by it.</summary>
        internal double Speed = 1;
        /// <summary>How much of the battle the frames cover, in seconds (an attack's first 6 s at most).</summary>
        internal double Seconds;
        /// <summary>Frames at the rate, and how many of them are kept (the sheet's frames).</summary>
        internal int Frames, Count;
        /// <summary>For each kept frame: when it's read (the video's own seconds), and when it starts in the battle.</summary>
        internal double[] ReadAt = Array.Empty<double>(), StartAt = Array.Empty<double>();
        /// <summary>Each kept frame's time in ms (battle.json's "times") when frames were left out; null when each is 1 / Fps.</summary>
        internal int[]? TimesMs;
        internal int VideoW, VideoH, CellW, CellH, Columns, Rows;

        internal int SheetW => Columns * CellW;
        internal int SheetH => Rows * CellH;
        internal bool Thinned => Count < Frames;
        /// <summary>A frame's height compared to the video's (below 1 when the frames were made smaller).</summary>
        internal double Shrink => VideoH > 0 ? (double)CellH / VideoH : 1;
    }

    /// <summary>
    /// The frames for an animation whose video plays <paramref name="videoSeconds"/> of its own
    /// seconds at <paramref name="speed"/>, at <paramref name="fps"/> frames a second of the
    /// battle. Over MaxFrames, every so many are kept and each kept frame shows until the next
    /// one (as a long GIF is thinned), so the animation keeps its length.
    /// </summary>
    internal static Plan Timing(string anim, double videoSeconds, double speed, int fps)
    {
        if (!(videoSeconds > 0) || double.IsInfinity(videoSeconds)) throw new InvalidDataException("doesn't say how long it plays");
        if (fps < 1) throw new ArgumentOutOfRangeException(nameof(fps));
        speed = speed > 0 ? speed : 1;
        double seconds = videoSeconds / speed;
        // An attack shows at most its first MaxAttack seconds, so the rest isn't read.
        if (anim == "attack") seconds = Math.Min(seconds, ArtTimeline.MaxAttack);
        int frames = (int)Math.Max(1, Math.Min(100000, Math.Round(seconds * fps)));
        int kept = Math.Min(frames, ArtTimeline.MaxFrames);
        var keep = new int[kept];
        int j = -1;
        for (int i = 0; i < frames; i++)
        {
            int slot = (int)((long)i * kept / frames);
            if (slot == j) continue;
            j = slot;
            keep[j] = i;
        }
        var plan = new Plan { Fps = fps, Speed = speed, Seconds = seconds, Frames = frames, Count = kept, ReadAt = new double[kept], StartAt = new double[kept] };
        for (int k = 0; k < kept; k++)
        {
            plan.StartAt[k] = (double)keep[k] / fps;
            plan.ReadAt[k] = plan.StartAt[k] * speed;
        }
        if (kept < frames)
        {
            // Rounded from the frames' ends, so the times add up to the whole length.
            plan.TimesMs = new int[kept];
            for (int k = 0; k < kept; k++)
            {
                int end = k + 1 < kept ? keep[k + 1] : frames;
                plan.TimesMs[k] = (int)(Math.Round(end * 1000.0 / fps) - Math.Round(keep[k] * 1000.0 / fps));
            }
        }
        return plan;
    }

    /// <summary>
    /// Fills in the plan's frame size and grid: the biggest frame, at most maxW x maxH and in the
    /// video's shape, at which every frame fits one sheet that's a picture the battle takes (at
    /// most MaxPictureSide a side, MaxGrid columns and rows, and small enough that its PNG stays
    /// under MaxPictureBytes whatever the pictures hold), and that the battle's load packs at
    /// full size. False when even the smallest frames don't fit.
    /// </summary>
    internal static bool Lay(Plan plan, int videoW, int videoH, int maxW, int maxH)
    {
        plan.VideoW = videoW;
        plan.VideoH = videoH;
        if (videoW < 1 || videoH < 1 || plan.Count < 1) return false;
        double fit = Math.Min(1, Math.Min((double)Math.Max(1, maxW) / videoW, (double)Math.Max(1, maxH) / videoH));
        int top = Math.Max(1, (int)Math.Floor(videoH * fit + 1e-9));
        for (int h = top; h >= EnemyArtReader.MinCell; h--)
        {
            int w = (int)Math.Max(1, Math.Min(Math.Max(1, maxW), Math.Round((double)videoW * h / videoH)));
            if (w < EnemyArtReader.MinCell) break;
            if (Grid(plan.Count, w, h) is not (int columns, int rows)) continue;
            // Stored as a PNG, the sheet is never much bigger than its raw pixels plus a byte a row.
            long raw = (long)columns * w * rows * h * 4 + (long)rows * h;
            if (raw > EnemyArtReader.MaxPictureBytes - PngSlack) continue;
            if (FrameAtlas.Plan(w, h, plan.Count, ArtDecode.MaxAtlasSide, ArtDecode.MaxAtlasBytes, 1, 1) == null) continue;
            (plan.CellW, plan.CellH, plan.Columns, plan.Rows) = (w, h, columns, rows);
            return true;
        }
        return false;
    }

    /// <summary>The columns and rows closest to a square sheet for <paramref name="count"/> frames of w x h; null when none fits.</summary>
    internal static (int Columns, int Rows)? Grid(int count, int w, int h)
    {
        (int, int)? best = null;
        long bestSide = long.MaxValue, bestArea = long.MaxValue;
        for (int columns = 1; columns <= Math.Min(count, EnemyArtReader.MaxGrid); columns++)
        {
            int rows = (count + columns - 1) / columns;
            long sw = (long)columns * w, sh = (long)rows * h;
            if (sw > EnemyArtReader.MaxPictureSide) break;
            if (rows > EnemyArtReader.MaxGrid || sh > EnemyArtReader.MaxPictureSide) continue;
            long side = Math.Max(sw, sh), area = sw * sh;
            if (side > bestSide || (side == bestSide && area >= bestArea)) continue;
            (best, bestSide, bestArea) = ((columns, rows), side, area);
        }
        return best;
    }

    /// <summary>A plan made for more frames than the video turned out to have keeps its frame size and takes a grid for its own count.</summary>
    internal static bool Regrid(Plan plan)
    {
        if (Grid(plan.Count, plan.CellW, plan.CellH) is not (int columns, int rows)) return false;
        (plan.Columns, plan.Rows) = (columns, rows);
        return true;
    }

    /// <summary>A frame of the plan's frame size (RGBA, top row first) into its cell of the sheet (RGBA, top row first).</summary>
    internal static void Put(byte[] sheet, Plan plan, int index, byte[] frame)
    {
        int stride = plan.CellW * 4, sheetStride = plan.SheetW * 4;
        int left = index % plan.Columns * stride, top = index / plan.Columns * plan.CellH;
        for (int y = 0; y < plan.CellH; y++)
            Buffer.BlockCopy(frame, y * stride, sheet, (top + y) * sheetStride + left, stride);
    }

    /// <summary>Rows read bottom row first (as Unity reads a texture) into a buffer top row first (as pictures are stored).</summary>
    internal static void Flip(byte[] bottomUp, int w, int h, byte[] topDown)
    {
        int stride = w * 4;
        for (int y = 0; y < h; y++) Buffer.BlockCopy(bottomUp, (h - 1 - y) * stride, topDown, y * stride, stride);
    }

    /// <summary>The frame whose start is nearest a hit <paramref name="seconds"/> into the attack in the battle (0-based, as "hitFrame").</summary>
    internal static int HitFrame(Plan plan, double seconds)
    {
        int best = 0;
        for (int k = 1; k < plan.StartAt.Length; k++)
            if (Math.Abs(plan.StartAt[k] - seconds) < Math.Abs(plan.StartAt[best] - seconds) - 1e-9) best = k;
        return best;
    }

    // A video's settings that don't carry over to its sheet (the sheet's own are written instead),
    // and the two that carry over moved to the sheet's frame size.
    private static readonly HashSet<string> Replaced = new(StringComparer.OrdinalIgnoreCase)
    {
        "file", "kind", "speed", "hitTime", "hitFrame", "columns", "rows", "first", "frames", "fps", "times", "seconds", "feet", "scale",
    };

    /// <summary>
    /// An animation's settings once its video is a sheet (besides its "file" and "kind"): the
    /// grid, the frames and their rate (each frame's time when some were left out), the attack's
    /// hit on the frame nearest <paramref name="hitSeconds"/> (battle seconds; null leaves it
    /// automatic); and from the video's own settings (<paramref name="video"/>), its move, mirror,
    /// loop, see-through colour and anything the loader doesn't know as they were, with its feet
    /// and size moved to the sheet's frames, so it stands and shows as the video did (a video
    /// moved by hand keeps standing on its frame's bottom edge).
    /// </summary>
    internal static List<(string Key, JsonNode Value)> Settings(Plan plan, string anim, JsonObject? video, double? hitSeconds)
    {
        var set = new List<(string, JsonNode)>
        {
            ("columns", Number(plan.Columns)), ("rows", Number(plan.Rows)), ("first", Number(0)), ("frames", Number(plan.Count)),
        };
        if (plan.TimesMs != null) set.Add(("times", new JsonArray(plan.TimesMs.Select(ms => (JsonNode?)Number(ms)).ToArray())));
        else set.Add(("fps", Number(plan.Fps)));
        if (anim == "attack" && hitSeconds is double hit) set.Add(("hitFrame", Number(HitFrame(plan, hit))));
        if (video == null) return set;
        foreach (var (key, value) in video)
        {
            if (value == null) continue;
            if (key.Equals("feet", StringComparison.OrdinalIgnoreCase) && value is JsonArray feet && feet.Count == 2 && Num(feet[0]) is double x && Num(feet[1]) is double y)
            {
                set.Add((key, new JsonArray(Number(Math.Round(x * plan.CellW / plan.VideoW, 2)), Number(Math.Round(y * plan.CellH / plan.VideoH, 2)))));
                continue;
            }
            // Smaller frames are drawn bigger to show the same size (the idle's size is in game pixels already).
            if (key.Equals("scale", StringComparison.OrdinalIgnoreCase) && anim != "idle" && Num(value) is double scale)
            {
                set.Add((key, Number(Math.Clamp(Math.Round(scale / plan.Shrink, 3), 0.05, 20))));
                continue;
            }
            if (Replaced.Contains(key)) continue;
            set.Add((key, JsonNode.Parse(value.ToJsonString())!));
        }
        if (anim != "idle" && plan.Shrink < 0.9995 && !set.Any(s => s.Item1.Equals("scale", StringComparison.OrdinalIgnoreCase)))
            set.Add(("scale", Number(Math.Clamp(Math.Round(1 / plan.Shrink, 3), 0.05, 20))));
        // A video stands on its frame's bottom edge, and its own move was set for that; once a
        // see-through colour finds the character's feet, the move would count twice. So it keeps
        // standing where it did.
        bool feetSet = set.Any(s => s.Item1.Equals("feet", StringComparison.OrdinalIgnoreCase));
        if (!feetSet && video.Any(p => p.Key.Equals("offset", StringComparison.OrdinalIgnoreCase)))
            set.Add(("feet", new JsonArray(Number(plan.CellW / 2.0), Number(plan.CellH))));
        return set;
    }

    /// <summary>
    /// Another animation's size compared to the idle, once the idle's frames were made smaller: it's
    /// measured against the idle's pixels, so it shrinks with them to show the same. Null when it stays.
    /// </summary>
    internal static double? OtherScale(Plan idle, double? scale) =>
        idle.Shrink < 0.9995 ? Math.Clamp(Math.Round((scale ?? 1) * idle.Shrink, 3), 0.05, 20) : null;

    // Whole numbers without ".0", as the creator writes them.
    private static JsonNode Number(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15 ? JsonValue.Create((long)value) : JsonValue.Create(Math.Round(value, 3));

    private static double? Num(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d)) return d;
        return null;
    }

    /// <summary>A free name in the battle's art folder for an animation's frames: art/idle-frames.png, then "idle-frames (2).png" and on.</summary>
    internal static string FreeName(string folder, string anim)
    {
        string dir = Path.Combine(folder, "art"), stem = anim + "-frames", name = stem + ".png";
        for (int i = 2; File.Exists(Path.Combine(dir, name)) || Directory.Exists(Path.Combine(dir, name)) || File.Exists(Path.Combine(dir, name + ".part")); i++)
            name = $"{stem} ({i}).png";
        return "art/" + name;
    }

    /// <summary>What a frame's background looks like, for the see-through colour.</summary>
    internal sealed class Background
    {
        /// <summary>Most of the frame's edge is one colour, the corner's.</summary>
        internal bool Flat;
        internal int Rgb;
        /// <summary>A see-through range that takes the whole background (at least the usual 15%), in 5% steps.</summary>
        internal double Range = 0.15;
        /// <summary>"green", "blue", "black", "white" or "one colour".</summary>
        internal string Name = "";
    }

    /// <summary>
    /// Looks at a frame's edge pixels (RGBA, top row first): when most are close to its top-left
    /// pixel's colour, it stands on a flat background, like a green screen. The range is how far
    /// those pixels stray from it, measured as the see-through colour measures, with a margin.
    /// </summary>
    internal static Background Look(byte[] frame, int w, int h)
    {
        var look = new Background();
        if (w < 2 || h < 2 || frame.Length < w * h * 4 || frame[3] < FrameAtlas.VisibleAlpha) return look;
        look.Rgb = frame[0] << 16 | frame[1] << 8 | frame[2];
        var near = new List<double>();
        int edge = 0;
        void At(int x, int y)
        {
            int o = (y * w + x) * 4;
            edge++;
            if (frame[o + 3] < FrameAtlas.VisibleAlpha) return;
            double d = FrameAtlas.Distance(look.Rgb, frame[o], frame[o + 1], frame[o + 2]);
            if (d < SameBackground) near.Add(d);
        }
        for (int x = 0; x < w; x++)
        {
            At(x, 0);
            At(x, h - 1);
        }
        for (int y = 1; y < h - 1; y++)
        {
            At(0, y);
            At(w - 1, y);
        }
        look.Flat = near.Count >= FlatShare * edge;
        if (look.Flat)
        {
            near.Sort();
            double far = near[Math.Min(near.Count - 1, (int)Math.Floor(near.Count * 0.95))];
            look.Range = Math.Clamp(Math.Ceiling((far + 0.04) * 20 - 1e-9) / 20, 0.15, 0.4);
        }
        int r = frame[0], g = frame[1], b = frame[2];
        look.Name = g > r + 40 && g > b + 40 ? "green" : b > r + 40 && b > g + 40 ? "blue" : Math.Max(r, Math.Max(g, b)) < 40 ? "black"
            : Math.Min(r, Math.Min(g, b)) > 215 ? "white" : "one colour";
        return look;
    }
}
