using System.Globalization;
using System.Text.Json;

namespace NocturneFlatScroll;

/// <summary>
/// How a custom battle's card picture fills the arcade card's picture slot. The slot is square
/// (76 x 76 menu units: 228 px at 1080p, 457 px at 4K) and the game fits any sprite whole into it,
/// with see-through bars where it isn't square; it is the only place the game shows the picture.
/// So what the card shows is baked into its sprite: the square it fills (or the whole picture), at
/// the size the slot needs, crisp or smooth. battle.json's "cardFit", "cardFocus" and "cardSmooth"
/// say how; the loader and the battle creator both read them here and work the card out here, so
/// the creator's preview and the arcade can't disagree. Missing keys keep the whole picture, as
/// cards looked before the keys existed. This file has no Unity or game dependencies.
/// </summary>
internal static class CardLayout
{
    internal const string FitKey = "cardFit", FocusKey = "cardFocus", SmoothKey = "cardSmooth";
    /// <summary>The longest side kept of what the card shows (4K draws the slot 457 px wide).</summary>
    internal const int KeepSide = 512;
    /// <summary>A picture at most this many pixels across the slot is pixel art: crisp unless it says otherwise.</summary>
    internal const int CrispSide = 128;
    /// <summary>The slot's size in the arcade menu's units, and in screen pixels at 1080p.</summary>
    internal const double SlotUnits = 76.154, SlotPixels1080 = 228;

    /// <summary>battle.json's card keys, as read.</summary>
    internal readonly struct Look
    {
        /// <summary>True: the picture fills the square and what sticks out is cut off ("fill"). False: the whole picture ("fit").</summary>
        internal readonly bool Fill;
        /// <summary>Where the square sits, from 0 (left, top) to 1 (right, bottom), like CSS object-position.</summary>
        internal readonly double FocusX, FocusY;
        /// <summary>True smooth, false crisp, null worked out from the picture.</summary>
        internal readonly bool? Smooth;

        internal Look(bool fill, double focusX, double focusY, bool? smooth)
        {
            Fill = fill;
            FocusX = Math.Clamp(focusX, 0, 1);
            FocusY = Math.Clamp(focusY, 0, 1);
            Smooth = smooth;
        }

        /// <summary>No keys: the whole picture, a centred square if it's ever filled, crispness worked out.</summary>
        internal static Look Default => new(false, 0.5, 0.5, null);
    }

    internal enum Shape { Square, Wide, Tall }

    /// <summary>Square when the sides are within 1% of each other: filling and the whole picture look the same then.</summary>
    internal static Shape ShapeOf(int width, int height) =>
        Math.Abs(width - height) <= 0.01 * Math.Max(width, height) ? Shape.Square : width > height ? Shape.Wide : Shape.Tall;

    /// <summary>What the arcade keeps of a picture and shows of it.</summary>
    internal readonly struct Plan
    {
        /// <summary>The picture's size as kept: its own, or smaller when it's bigger than the slot needs.</summary>
        internal readonly int Width, Height;
        /// <summary>The part the card shows, in kept pixels, from the bottom left (the way Unity counts a texture's pixels).</summary>
        internal readonly int X, Y, PartWidth, PartHeight;
        /// <summary>Crisp (point) filtering.</summary>
        internal readonly bool Crisp;
        /// <summary>Whether the picture had to be made smaller.</summary>
        internal bool Smaller => Scale < 1;
        /// <summary>The kept size over the picture's own.</summary>
        internal readonly double Scale;

        internal Plan(int width, int height, int x, int y, int partWidth, int partHeight, bool crisp, double scale)
        {
            Width = width;
            Height = height;
            X = x;
            Y = y;
            PartWidth = partWidth;
            PartHeight = partHeight;
            Crisp = crisp;
            Scale = scale;
        }

        /// <summary>Whether the card shows less than the kept picture.</summary>
        internal bool Cropped => PartWidth != Width || PartHeight != Height;
    }

    /// <summary>The side of the picture that goes across the slot, in its own pixels: the short side when it fills the square, else the long side.</summary>
    internal static int Span(int width, int height, Look look) => look.Fill ? Math.Min(width, height) : Math.Max(width, height);

    /// <summary>Whether a picture shows crisp: as the look says, else when it's small enough to be pixel art.</summary>
    internal static bool Crisp(int width, int height, Look look) => look.Smooth is bool smooth ? !smooth : Span(width, height, look) <= CrispSide;

    /// <summary>
    /// What the arcade keeps and shows of a <paramref name="width"/> x <paramref name="height"/>
    /// picture: it is made smaller so the side that goes across the slot is at most
    /// <paramref name="keepSide"/>, and when it fills the square, the largest square in it,
    /// placed by the focus (0 at the left or top edge, 1 at the right or bottom).
    /// </summary>
    internal static Plan Work(int width, int height, Look look, int keepSide = KeepSide)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        int span = Span(width, height, look);
        double scale = span > keepSide ? keepSide / (double)span : 1;
        int w = scale < 1 ? Math.Max(1, (int)Math.Round(width * scale)) : width;
        int h = scale < 1 ? Math.Max(1, (int)Math.Round(height * scale)) : height;
        bool crisp = Crisp(width, height, look);
        if (!look.Fill) return new Plan(w, h, 0, 0, w, h, crisp, scale);
        int side = Math.Min(w, h);
        int x = (int)Math.Round((w - side) * look.FocusX);
        int top = (int)Math.Round((h - side) * look.FocusY);
        return new Plan(w, h, x, h - top - side, side, side, crisp, scale);
    }

    /// <summary>
    /// The focus that moves the square by <paramref name="pixels"/> of the picture's own along its
    /// long side (positive: right or down), for dragging it; the square stays inside the picture.
    /// </summary>
    internal static double MovedFocus(int width, int height, double focus, double pixels)
    {
        int room = Math.Abs(width - height);
        return room == 0 ? focus : Math.Clamp(focus + pixels / room, 0, 1);
    }

    // ---- battle.json -----------------------------------------------------------------------------

    /// <summary>
    /// Reads the card's keys. A value that can't be used is noted in <paramref name="problems"/>
    /// (null: not noted, as for a battle without a card to show) and its default is used; nothing
    /// here stops a battle from loading.
    /// </summary>
    internal static Look Read(JsonElement fit, JsonElement focus, JsonElement smooth, List<string>? problems)
    {
        var d = Look.Default;
        bool fill = d.Fill;
        double fx = d.FocusX, fy = d.FocusY;
        bool? smoothValue = d.Smooth;

        switch (fit.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                break;
            case JsonValueKind.String when "fill".Equals(fit.GetString()?.Trim(), StringComparison.OrdinalIgnoreCase):
                fill = true;
                break;
            case JsonValueKind.String when "fit".Equals(fit.GetString()?.Trim(), StringComparison.OrdinalIgnoreCase):
                fill = false;
                break;
            default:
                problems?.Add($"\"{FitKey}\" must be \"fill\" or \"fit\", so the card shows the whole picture");
                break;
        }

        if (focus.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            if (focus.ValueKind == JsonValueKind.Array && focus.GetArrayLength() == 2 && Number(focus[0]) is double x && Number(focus[1]) is double y)
            {
                if (x < 0 || x > 1 || y < 0 || y > 1)
                {
                    double wrong = x < 0 || x > 1 ? x : y;
                    problems?.Add($"\"{FocusKey}\" goes from 0 to 1, so {Num(wrong)} counts as {Num(Math.Clamp(wrong, 0, 1))}");
                }
                fx = Math.Clamp(x, 0, 1);
                fy = Math.Clamp(y, 0, 1);
            }
            else problems?.Add($"\"{FocusKey}\" must be two numbers from 0 to 1, like [0.5, 0.5], so the square is in the middle");
        }

        switch (smooth.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                break;
            case JsonValueKind.True:
                smoothValue = true;
                break;
            case JsonValueKind.False:
                smoothValue = false;
                break;
            default:
                problems?.Add($"\"{SmoothKey}\" must be true or false, so it's worked out from the picture");
                break;
        }
        return new Look(fill, fx, fy, smoothValue);
    }

    // As the loader reads numbers: written as numbers or as text, but never NaN or infinite.
    private static double? Number(JsonElement value)
    {
        double d;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out d) && double.IsFinite(d)) return d;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d)) return d;
        return null;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // ---- words for the battle creator ---------------------------------------------------------

    /// <summary>Where the square sits along the picture's long side, like "in the middle" or "30% from the left".</summary>
    internal static string FocusText(Shape shape, double focus)
    {
        bool tall = shape == Shape.Tall;
        int percent = (int)Math.Round(Math.Clamp(focus, 0, 1) * 100);
        return percent switch
        {
            0 => tall ? "at the top" : "at the left",
            50 => "in the middle",
            100 => tall ? "at the bottom" : "at the right",
            _ => $"{percent}% from the {(tall ? "top" : "left")}",
        };
    }
}
