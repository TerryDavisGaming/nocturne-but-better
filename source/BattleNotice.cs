using System.Text;

namespace NocturneFlatScroll;

/// <summary>What a custom battle sets for the player, as the arcade's notice shows it.</summary>
internal sealed class NoticeInput
{
    /// <summary>The battle sets the player's gear (and the mod can set it).</summary>
    internal bool GearSet;
    /// <summary>The set gear's item names in slot order; the consumable as "Potion x2" when there are more than one.</summary>
    internal List<string> Items = new();
    /// <summary>How many of the six slots hold an item.</summary>
    internal int FilledSlots;
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

    /// <summary>The most a card's tag holds at its size.</summary>
    internal const int BadgeMax = 21;
    /// <summary>The box holds about this many lines above the arcade's footer.</summary>
    internal const int BoxLines = 5;
    /// <summary>About this many characters make a line of the box.</summary>
    internal const int BoxLineChars = 52;

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
            input.FilledSlots++;
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
    /// The box's text (rich text): what the battle sets, then its lore. Null when there is nothing
    /// to show. When it doesn't <paramref name="fits"/>, the lore is cut word by word (and left out
    /// if not even a word fits), then the last line, then the gear list; what the battle sets and
    /// its level always stay.
    /// </summary>
    internal static string? Box(NoticeInput n, Func<string, bool> fits)
    {
        bool level = n.Level != null, gear = n.GearSet;
        string? header = level && gear ? "This battle sets your level and gear."
            : level ? "This battle sets your level."
            : gear ? "This battle sets your gear."
            : null;
        string? levelLine = level ? LevelLine(n.Level!.Value, n.YourLevel) : null;
        string? footer = level && gear ? "Yours come back when it ends."
            : level ? "Your own level comes back when it ends."
            : gear ? "Your own gear comes back when it ends."
            : null;
        string lore = (n.Lore ?? "").Trim();
        if (header == null && lore.Length == 0) return null;

        string Compose(string? gearLine, string? last, string? loreText) =>
            Join(header == null ? null : $"<color={HeaderColor}>{header}</color>", levelLine, gearLine, last, loreText);

        string? fullGear = gear ? GearLine(n, n.Items.Count) : null;
        string full = Compose(fullGear, footer, lore.Length > 0 ? lore : null);
        if (fits(full)) return full;

        // 1. The lore, word by word.
        if (lore.Length > 0)
        {
            string? cut = CutWords(lore, words => fits(Compose(fullGear, footer, words)));
            if (cut != null) return Compose(fullGear, footer, cut);
            if (header == null) return null;
            string noLore = Compose(fullGear, footer, null);
            if (fits(noLore)) return noLore;
        }
        // 2. The last line.
        string noFooter = Compose(fullGear, null, null);
        if (fits(noFooter) || !gear) return noFooter;
        // 3. The gear list, from its end.
        for (int shown = n.Items.Count - 1; shown >= 1; shown--)
        {
            string shorter = Compose(GearLine(n, shown), null, null);
            if (fits(shorter)) return shorter;
        }
        return Compose(n.Items.Count > 0 ? GearLine(n, 1) : fullGear, null, null);
    }

    internal static string LevelLine(int level, int? yours) =>
        yours is not int own ? $"Level {level}."
        : own == level ? $"Level {level} (the same as yours)."
        : $"Level {level} (you're level {own}).";

    // All the items when shown == Items.Count; else the first ones and "…".
    private static string GearLine(NoticeInput n, int shown)
    {
        if (n.Items.Count == 0)
            return "Gear: none. Every slot is empty." + (n.ExtraHealth is int h0 ? $" Health upgrades: {h0}." : "");
        if (shown < n.Items.Count) return "Gear: " + string.Join(", ", n.Items.Take(Math.Max(1, shown))) + Ellipsis;
        var line = new StringBuilder("Gear: ").Append(string.Join(", ", n.Items)).Append('.');
        if (n.FilledSlots < SlotOrder.Length) line.Append(" Nothing else.");
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

    // ---- the card and the creator's list ----------------------------------------------------------

    /// <summary>The card's tag, like "Set gear &amp; level 12"; null when the battle sets neither.</summary>
    internal static string? Badge(NoticeInput n) =>
        n.GearSet && n.Level is int both ? $"Set gear & level {both}"
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

    // ---- measuring without the game -----------------------------------------------------------------

    /// <summary>
    /// Whether rich text fits in <paramref name="lines"/> lines of about <paramref name="width"/>
    /// characters, word-wrapped: an estimate of the arcade's box for where the game's own text
    /// measure isn't at hand (the battle creator's preview, and tests).
    /// </summary>
    internal static bool FitsLines(string text, int width = BoxLineChars, int lines = BoxLines) => CountLines(text, width) <= lines;

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
