using System.Text;

namespace NocturneFlatScroll;

/// <summary>What a custom battle sets for the player, as the arcade's notice shows it.</summary>
internal sealed class NoticeInput
{
    /// <summary>The battle sets the player's gear (and the mod can set it).</summary>
    internal bool GearSet;
    /// <summary>The set gear's item names in slot order; the consumable as "Potion x2" when there are more than one.</summary>
    internal List<string> Items = new();
    /// <summary>The set health upgrades, or null for the player's own.</summary>
    internal int? ExtraHealth;
    /// <summary>The level the battle plays at (kept within the game's levels), or null for the player's own.</summary>
    internal int? Level;
    /// <summary>The player's level now, or null when it isn't known.</summary>
    internal int? YourLevel;
    /// <summary>The battle's lore, or who made its song and charts.</summary>
    internal string Lore = "";
}

/// <summary>
/// The text that tells players what a custom battle sets before they start it: the box on the
/// right of the arcade when the battle is selected, the tag on its card, and the battle creator's
/// list and preview all come from here, so they always agree. The text is plain ASCII (and "…"),
/// which the arcade's fonts can all draw.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class BattleNotice
{
    /// <summary>The slots in the order the notice lists them.</summary>
    internal static readonly GearSlot[] SlotOrder =
        { GearSlot.MainHand, GearSlot.Body, GearSlot.Head, GearSlot.OffHand, GearSlot.Amulet, GearSlot.Consumable };

    /// <summary>
    /// The most a card's tag holds at its size: its font is monospaced, and the QA pictures show 18 characters whole
    /// ("Set gear &amp; level 5") and 19 cut ("Set gear &amp; level 12" showed as "Set gear &amp; level …").
    /// </summary>
    internal const int BadgeMax = 18;

    /// <summary>The box's own font size in the arcade (its label's): the sizes here are in these points.</summary>
    internal const float BoxFontSize = 7f;
    /// <summary>The smallest the box's text shrinks to so that what a battle sets shows whole.</summary>
    internal const float MinFontSize = 5f;
    /// <summary>How much smaller each size the box tries is.</summary>
    internal const float FontSizeStep = 0.25f;
    /// <summary>At its own size the box holds 5 lines above the arcade's footer...</summary>
    internal const int BoxLines = 5;
    /// <summary>
    /// ...of about 35 characters: the arcade draws the box in a monospaced font, 4.55 units a character at size 7,
    /// in a line of 162.8 (measured in the QA pictures).
    /// </summary>
    internal const int BoxLineChars = 35;

    internal const string Ellipsis = "\u2026";
    private const string HeaderColor = "#EAE6F5";

    /// <summary>
    /// The notice's input from a battle's gear and level. <paramref name="name"/> gives an item's
    /// name for its slot, or null when the battle would leave that slot empty (an unknown item, or
    /// one for another slot). A null <paramref name="gear"/> means the gear isn't set.
    /// </summary>
    internal static NoticeInput Input(GearDefinition? gear, int? level, int? yourLevel, string lore, Func<GearSlot, string, string?> name)
    {
        var input = new NoticeInput { Level = level, YourLevel = yourLevel, Lore = lore ?? "" };
        if (gear == null || !gear.IsSet) return input;
        input.GearSet = true;
        foreach (var slot in SlotOrder)
        {
            string? id = gear.ItemFor(slot);
            if (id == null) continue;
            string? shown = name(slot, id);
            if (string.IsNullOrWhiteSpace(shown)) continue;
            shown = shown!.Trim();
            if (slot == GearSlot.Consumable && gear.ConsumableCount > 1) shown += " x" + gear.ConsumableCount;
            input.Items.Add(shown);
        }
        if (gear.extraHealth is int health) input.ExtraHealth = Math.Clamp(health, 0, GearDefinition.MaxCount);
        return input;
    }

    /// <summary>A battle's lore, or who made its song and charts when it has none.</summary>
    internal static string Credits(string? lore, string? artist, string? author)
    {
        string text = (lore ?? "").Trim();
        if (text.Length > 0) return text;
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist)) lines.Add("Music by " + artist!.Trim());
        if (!string.IsNullOrWhiteSpace(author)) lines.Add("Chart by " + author!.Trim());
        return string.Join("\n", lines);
    }

    // ---- the box ----------------------------------------------------------------------------------

    /// <summary>
    /// The box's text (rich text), and in <paramref name="size"/> the size it shows at (in the box's
    /// points; <see cref="BoxFontSize"/> is its own). <paramref name="fits"/> says whether a text fits
    /// the box at a size. What the battle sets always shows whole: at the box's own size when it fits,
    /// else at the largest smaller size that fits, down to <see cref="MinFontSize"/>. Only when it
    /// doesn't fit even then is its gear list cut from the end, with "…" (the other lines stay). The lore
    /// follows at that size when there is room, cut word by word or left out. A battle that sets
    /// nothing shows its lore alone at the box's own size. Null when there is nothing to show.
    /// </summary>
    internal static string? Box(NoticeInput n, Func<string, float, bool> fits, out float size)
    {
        size = BoxFontSize;
        string lore = (n.Lore ?? "").Trim();
        string? sets = Sets(n);
        if (sets == null)
        {
            if (lore.Length == 0) return null;
            return fits(lore, BoxFontSize) ? lore : CutWords(lore, text => fits(text, BoxFontSize));
        }
        float? whole = null;
        foreach (float candidate in FontSizes())
        {
            if (!fits(sets, candidate)) continue;
            whole = candidate;
            break;
        }
        if (whole is not float at)
        {
            // The last resort, at the smallest size: the gear list from its end.
            size = MinFontSize;
            for (int shown = n.Items.Count - 1; shown >= 1; shown--)
            {
                string shorter = Compose(n, shown);
                if (fits(shorter, MinFontSize)) return shorter;
            }
            return Compose(n, Math.Min(1, n.Items.Count));
        }
        size = at;
        if (lore.Length == 0) return sets;
        string all = sets + "\n" + lore;
        if (fits(all, at)) return all;
        string? cut = CutWords(lore, words => fits(sets + "\n" + words, at));
        return cut != null ? sets + "\n" + cut : sets;
    }

    /// <summary>The box's whole text, as nothing trims it: what the battle sets, then all its lore. Null when there is nothing to show.</summary>
    internal static string? Whole(NoticeInput n) => Box(n, (_, _) => true, out _);

    /// <summary>What the battle sets, whole (rich text, no lore); null when it sets neither gear nor level.</summary>
    internal static string? Sets(NoticeInput n) => n.GearSet || n.Level != null ? Compose(n, n.Items.Count) : null;

    // The header, the level, the gear (the first `shown` items) and the last line.
    private static string Compose(NoticeInput n, int shown)
    {
        bool level = n.Level != null, gear = n.GearSet;
        string header = level && gear ? "Sets your level and gear." : level ? "Sets your level." : "Sets your gear.";
        string last = level && gear ? "Yours come back afterward." : level ? "Your level comes back afterward." : "Your gear comes back afterward.";
        return Join($"<color={HeaderColor}>{header}</color>", level ? LevelLine(n.Level!.Value, n.YourLevel) : null, gear ? GearLine(n, shown) : null, last);
    }

    internal static string LevelLine(int level, int? yours) =>
        yours is not int own ? $"Level {level}."
        : own == level ? $"Level {level} (same as yours)."
        : $"Level {level} (yours: {own}).";

    // All the items when shown == Items.Count, else the first ones and "…"; then the health upgrades when the battle sets them.
    // The battle empties every slot it doesn't fill, so a list that leaves slots out says "only": without it, "Gear: Pool
    // Noodle." reads as if only the weapon changed and the rest stayed yours.
    private static string GearLine(NoticeInput n, int shown)
    {
        var line = new StringBuilder("Gear: ");
        if (n.Items.Count == 0) line.Append("none.");
        else
        {
            if (n.Items.Count < SlotOrder.Length) line.Append("only ");
            if (shown < n.Items.Count) line.Append(string.Join(", ", n.Items.Take(Math.Max(1, shown)))).Append(Ellipsis);
            else line.Append(string.Join(", ", n.Items)).Append('.');
        }
        if (n.ExtraHealth is int h) line.Append($" Health upgrades: {h}.");
        return line.ToString();
    }

    private static string Join(params string?[] lines) => string.Join("\n", lines.Where(l => !string.IsNullOrEmpty(l)));

    /// <summary>The most words of <paramref name="text"/> that fit, ending in "…"; null when not even one does.</summary>
    internal static string? CutWords(string text, Func<string, bool> fits)
    {
        // Where each word ends.
        var ends = new List<int>();
        for (int i = 0; i < text.Length; i++)
            if (!char.IsWhiteSpace(text[i]) && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]))) ends.Add(i + 1);
        // Fewer words never take more room, so the most that fit is found by halving.
        int lo = 0, hi = ends.Count - 1;
        string? best = null;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            string candidate = text.Substring(0, ends[mid]) + Ellipsis;
            if (fits(candidate))
            {
                best = candidate;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }
        return best;
    }

    /// <summary>
    /// About how many of the box's lines are left for the lore under what the battle sets, by the
    /// estimate (<see cref="FitsLines"/>), at the size what it sets shows at; 0 when that fills the box.
    /// </summary>
    internal static int LoreRoom(NoticeInput n)
    {
        var notice = new NoticeInput { GearSet = n.GearSet, Items = n.Items, ExtraHealth = n.ExtraHealth, Level = n.Level, YourLevel = n.YourLevel };
        string? text = Box(notice, FitsLines, out float size);
        return text == null ? BoxLines : Math.Max(0, LinesAt(size) - CountLines(text, CharsAt(size)));
    }

    // ---- the card and the creator's list ----------------------------------------------------------

    /// <summary>
    /// The card's tag, like "Set gear, level 12" (the creator's list row in its words); null when the battle sets
    /// neither. Never longer than <see cref="BadgeMax"/>, so the level always shows.
    /// </summary>
    internal static string? Badge(NoticeInput n) =>
        n.GearSet && n.Level is int both ? $"Set gear, level {both}"
        : n.GearSet ? "Set gear"
        : n.Level is int level ? $"Set level {level}"
        : null;

    /// <summary>The battle creator's list row suffix, like "set gear, level 12"; empty when the battle sets neither.</summary>
    internal static string Summary(bool gearSet, int? level) =>
        gearSet && level is int both ? $"set gear, level {both}"
        : gearSet ? "set gear"
        : level is int only ? $"level {only}"
        : "";

    internal static string Summary(NoticeInput n) => Summary(n.GearSet, n.Level);

    // ---- measuring ------------------------------------------------------------------------------------

    /// <summary>The sizes the box tries, from its own down to the smallest.</summary>
    internal static IEnumerable<float> FontSizes()
    {
        for (int k = 0; BoxFontSize - k * FontSizeStep >= MinFontSize - 0.001f; k++) yield return BoxFontSize - k * FontSizeStep;
    }

    /// <summary>
    /// The most the box's text may measure in the arcade, whatever its size: <see cref="BoxLines"/> lines
    /// at the box's own size, from how high one line measures (its letters only) and how much each line
    /// after it adds (the font's line height, gap included).
    /// </summary>
    internal static float MostHeight(float oneLine, float lineStep) => oneLine + (BoxLines - 1) * lineStep + 0.5f;

    /// <summary>About how many lines the box holds at a size: smaller text fits more lines in the same height.</summary>
    internal static int LinesAt(float size) => (int)Math.Floor(BoxLines * BoxFontSize / size + 0.001f);

    /// <summary>About how many characters make a line of the box at a size.</summary>
    internal static int CharsAt(float size) => (int)Math.Floor(BoxLineChars * BoxFontSize / size + 0.001f);

    /// <summary>
    /// Whether rich text fits the box at a size, word-wrapped: an estimate of the arcade's box for
    /// where the game's own text measure isn't at hand (the battle creator's preview, and tests).
    /// </summary>
    internal static bool FitsLines(string text, float size = BoxFontSize) => CountLines(text, CharsAt(size)) <= LinesAt(size);

    internal static int CountLines(string text, int width)
    {
        int count = 0;
        foreach (var paragraph in StripTags(text).Split('\n'))
        {
            int used = 0;
            count++;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int length = word.Length;
                if (used == 0)
                {
                    used = length;
                }
                else if (used + 1 + length <= width) used += 1 + length;
                else
                {
                    count++;
                    used = length;
                }
                // A word longer than a line takes more than one.
                while (used > width)
                {
                    count++;
                    used -= width;
                }
            }
        }
        return count;
    }

    /// <summary>The text without its rich text tags, as it reads.</summary>
    internal static string StripTags(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '<')
            {
                int close = text.IndexOf('>', i);
                if (close > i)
                {
                    i = close + 1;
                    continue;
                }
            }
            sb.Append(text[i++]);
        }
        return sb.ToString();
    }
}
