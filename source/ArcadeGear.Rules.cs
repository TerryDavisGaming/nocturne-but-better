using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NocturneFlatScroll;

/// <summary>Where a slot's item comes from in the main-menu arcade.</summary>
internal enum GearSource
{
    /// <summary>No arcade choice: the story's item.</summary>
    Story,
    /// <summary>The arcade choice: an item, or "(empty)".</summary>
    Arcade,
    /// <summary>The choice is an item the save doesn't own, so the story's item is used.</summary>
    NotOwned,
    /// <summary>The choice is an id the game doesn't have, so the story's item is used.</summary>
    NoSuchItem,
    /// <summary>The choice is an item for another slot, so the story's item is used.</summary>
    OtherSlot
}

/// <summary>
/// One slot as the next battle fills it. <see cref="Id"/> is the item equipped, null for an empty slot;
/// <see cref="Name"/> names it (or the used-up consumable). <see cref="Count"/> is how many of a consumable
/// the battle has. <see cref="Choice"/> is the arcade choice ("" for empty, null for none) and
/// <see cref="ChoiceName"/> its item's name. <see cref="UsedUp"/>: the consumable has none left this visit,
/// so the slot is empty. <see cref="Unowned"/>: an item the save doesn't own, allowed by "all items".
/// </summary>
internal sealed record SlotGear(GearSlot Slot, string? Id, string? Name, int Count, string? Choice, string? ChoiceName,
                                GearSource Source, bool UsedUp, bool Unowned);

/// <summary>A save slot's entry in ArcadeGear.json: its choices, and the play time it was started at.</summary>
internal sealed record SaveEntry(IReadOnlyDictionary<GearSlot, string> Choices, double? Since);

/// <summary>
/// What the arcade's Gear control says: <see cref="Arcade"/> when a slot uses an arcade choice,
/// <see cref="Unused"/> when choices can't be used now, <see cref="AllItems"/> when an item the save
/// doesn't own is in (its battle's score isn't saved).
/// </summary>
internal readonly record struct GearLabel(bool Arcade, bool Unused, bool AllItems);

/// <summary>A row of a slot's picker. <see cref="Value"/> is null for story gear, "" for empty, else an item id.</summary>
internal sealed record PickRow(string? Value, string Text, string? Tag, bool Current, GearItem? Item, bool UsedUp, bool Unowned);

/// <summary>What a main-menu arcade battle is fought with.</summary>
internal enum BattleGearKind
{
    /// <summary>The player's own gear, as the game has it (no swap).</summary>
    Own,
    /// <summary>A custom battle's set gear.</summary>
    SetGear,
    /// <summary>The arcade gear.</summary>
    Arcade
}

/// <summary>
/// The rules of the main-menu arcade's own gear (ArcadeGear): the ArcadeGear.json format, what
/// each slot holds for the next battle, the labels, tags and hints the page and the arcade show,
/// and the picker's rows. The page, the arcade's Gear control and the battles all use these, so
/// they always agree. Ownership reads the save as it was when the arcade opened; what's left of a
/// consumable reads the save now, less what the arcade gear used up this visit.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class ArcadeGearRules
{
    /// <summary>The file format this version writes; a higher one is only read.</summary>
    internal const int Format = 1;
    internal const string FormatKey = "format", SavesKey = "saves", SinceKey = "since";
    /// <summary>
    /// How many of a consumable the save doesn't own a battle gets (with "all items" on). The game
    /// allows one use per battle, and each battle has its own.
    /// </summary>
    internal const int UnownedConsumableCount = 1;
    /// <summary>How far an entry's start may be above the save's play time and still be this game's, in seconds.</summary>
    internal const double SinceSlack = 1;

    /// <summary>The slots, in the page's order.</summary>
    internal static readonly GearSlot[] Slots =
        { GearSlot.MainHand, GearSlot.Body, GearSlot.Head, GearSlot.OffHand, GearSlot.Amulet, GearSlot.Consumable };

    // Read the way battle.json is: any letter case, comments and trailing commas allowed.
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ---- the file --------------------------------------------------------------------------------

    /// <summary>A file with no choices.</summary>
    internal static JsonObject NewRoot() => new(NodeOptions) { [FormatKey] = Format };

    /// <summary>
    /// The file's tree; a missing or blank file has no choices. Null, with the reason in
    /// <paramref name="problem"/>, when it can't be read.
    /// </summary>
    internal static JsonObject? Parse(string? text, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(text)) return NewRoot();
        try
        {
            if (JsonNode.Parse(text!, NodeOptions, DocumentOptions) is not JsonObject root)
            {
                problem = "it isn't a JSON object";
                return null;
            }
            Settle(root);
            if (FormatOf(root) < 1)
            {
                problem = "its \"format\" isn't a whole number";
                return null;
            }
            return root;
        }
        catch (JsonException ex)
        {
            problem = ex.Message;
            return null;
        }
        catch (ArgumentException)
        {
            // Two keys that differ only in letter case.
            problem = "it has a key twice";
            return null;
        }
        catch (InvalidOperationException ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    // An object's keys are read only when first used, and a key given twice throws then; reading
    // them all now finds that while parsing, not in the middle of a change.
    private static void Settle(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj) Settle(pair.Value);
                break;
            case JsonArray array:
                foreach (var item in array) Settle(item);
                break;
        }
    }

    /// <summary>The file's format: 1 when it has none, -1 when it isn't a whole number.</summary>
    internal static int FormatOf(JsonObject root)
    {
        var node = root[FormatKey];
        if (node == null) return Format;
        return NumberOf(node) is double d && d >= 1 && d <= int.MaxValue && d == Math.Floor(d) ? (int)d : -1;
    }

    /// <summary>The file's text, as it is written.</summary>
    internal static string Text(JsonObject root) => root.ToJsonString(WriteOptions) + "\n";

    /// <summary>A save slot's key in "saves": a save without a slot is slot 1, as its scores are.</summary>
    internal static string SlotKey(int saveSlot) => (saveSlot > 0 ? saveSlot : 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>A save slot's choices and start. A slot key that isn't a string counts as missing.</summary>
    internal static SaveEntry Entry(JsonObject root, int saveSlot)
    {
        var choices = new Dictionary<GearSlot, string>();
        double? since = null;
        if (EntryObject(root, saveSlot) is JsonObject entry)
        {
            foreach (var slot in Slots)
                if (StringOf(entry[GearCatalog.KeyOf(slot)]) is string value) choices[slot] = value.Trim();
            since = NumberOf(entry[SinceKey]);
        }
        return new SaveEntry(choices, since);
    }

    /// <summary>
    /// Whether an entry was started by an earlier game in its save slot: its start is above the
    /// loaded save's play time. An entry with no start counts as this game's.
    /// </summary>
    internal static bool FromEarlierGame(SaveEntry entry, double playTime) => entry.Since is double since && since > playTime + SinceSlack;

    /// <summary>
    /// Sets a slot's choice (null removes it: story gear). A new entry, or one from an earlier game,
    /// starts over at <paramref name="playTime"/> and its old choices go; later changes keep its
    /// start. An entry left with no choices goes, and so do empty "saves". Other keys are kept.
    /// </summary>
    internal static void SetChoice(JsonObject root, int saveSlot, GearSlot slot, string? value, double playTime)
    {
        var entry = EntryObject(root, saveSlot, create: value != null);
        if (entry == null) return;
        var current = Entry(root, saveSlot);
        if (current.Choices.Count == 0 || FromEarlierGame(current, playTime))
        {
            foreach (var s in Slots) entry.Remove(GearCatalog.KeyOf(s));
            entry[SinceKey] = Math.Round(playTime, 3);
        }
        string key = GearCatalog.KeyOf(slot);
        if (value == null) entry.Remove(key);
        else entry[key] = value;
        Tidy(root, saveSlot);
    }

    /// <summary>"Use the gear I set before": keeps an earlier game's choices and makes them this game's.</summary>
    internal static void Adopt(JsonObject root, int saveSlot, double playTime)
    {
        if (EntryObject(root, saveSlot) is JsonObject entry && Entry(root, saveSlot).Choices.Count > 0)
            entry[SinceKey] = Math.Round(playTime, 3);
    }

    /// <summary>"Use my story gear in every slot": removes the save slot's choices (other keys are kept).</summary>
    internal static void ClearSave(JsonObject root, int saveSlot)
    {
        if (EntryObject(root, saveSlot) is not JsonObject entry) return;
        foreach (var s in Slots) entry.Remove(GearCatalog.KeyOf(s));
        Tidy(root, saveSlot);
    }

    private static JsonObject? EntryObject(JsonObject root, int saveSlot, bool create = false)
    {
        if (root[SavesKey] is not JsonObject saves)
        {
            if (!create) return null;
            // A "saves" that isn't an object holds no entries.
            saves = new JsonObject(NodeOptions);
            root[SavesKey] = saves;
        }
        string key = SlotKey(saveSlot);
        if (saves[key] is JsonObject entry) return entry;
        if (!create) return null;
        entry = new JsonObject(NodeOptions);
        saves[key] = entry;
        return entry;
    }

    // An entry without choices loses its start, and goes when nothing else is in it; so does an empty "saves".
    private static void Tidy(JsonObject root, int saveSlot)
    {
        if (root[SavesKey] is not JsonObject saves) return;
        string key = SlotKey(saveSlot);
        if (saves[key] is JsonObject entry && Entry(root, saveSlot).Choices.Count == 0)
        {
            entry.Remove(SinceKey);
            if (entry.Count == 0) saves.Remove(key);
        }
        if (saves.Count == 0) root.Remove(SavesKey);
    }

    private static string? StringOf(JsonNode? node)
    {
        try { return node is JsonValue value && value.TryGetValue(out string? text) ? text : null; }
        catch (InvalidOperationException) { return null; }
    }

    // A number read from the file, or one this code set (which is kept as the type it was set as).
    private static double? NumberOf(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        try
        {
            if (value.TryGetValue(out double number)) return double.IsFinite(number) ? number : null;
            if (value.TryGetValue(out int whole)) return whole;
            return value.TryGetValue(out long big) ? big : null;
        }
        catch (InvalidOperationException) { return null; }
    }

    // ---- what the next battle has -----------------------------------------------------------------

    /// <summary>Whether the save owned an item when the arcade opened.</summary>
    internal static bool Owned(IReadOnlyDictionary<string, int> visitStart, string id) => visitStart.TryGetValue(id, out int count) && count > 0;

    /// <summary>
    /// What each slot holds in the next battle, in <see cref="Slots"/> order. A missing choice is the
    /// story's item; "" is empty; an item for the slot that the save owned when the arcade opened
    /// (<paramref name="visitStart"/>) is that item, and so is one it didn't own when
    /// <paramref name="allItems"/> is on; anything else is the story's item, with the reason. A
    /// consumable the save owns has what's left this visit (<paramref name="leftThisVisit"/>), and none
    /// left leaves the slot empty: a used-up choice never falls back to the story's consumable. One
    /// the save doesn't own has <see cref="UnownedConsumableCount"/>.
    /// </summary>
    internal static SlotGear[] Resolve(IReadOnlyDictionary<GearSlot, string> choices, Func<GearSlot, string?> story,
                                       IReadOnlyDictionary<string, int> visitStart, Func<string, int> leftThisVisit,
                                       Func<string, GearItem?> find, bool allItems)
    {
        var result = new SlotGear[Slots.Length];
        for (int i = 0; i < Slots.Length; i++)
        {
            var slot = Slots[i];
            choices.TryGetValue(slot, out string? choice);
            var source = GearSource.Story;
            string? id = null, choiceName = null;
            bool unowned = false;
            if (choice == null) id = story(slot);
            else if (choice.Length == 0) source = GearSource.Arcade;
            else
            {
                var item = find(choice);
                choiceName = item?.Name;
                if (item == null) source = GearSource.NoSuchItem;
                else if (item.Slot != slot) source = GearSource.OtherSlot;
                else if (Owned(visitStart, item.Id)) source = GearSource.Arcade;
                else if (allItems)
                {
                    source = GearSource.Arcade;
                    unowned = true;
                }
                else source = GearSource.NotOwned;
                id = source == GearSource.Arcade ? item!.Id : story(slot);
            }
            if (string.IsNullOrEmpty(id)) id = null;
            string? name = id == null ? null : find(id)?.Name ?? id;
            int count = id == null ? 0 : 1;
            bool usedUp = false;
            if (slot == GearSlot.Consumable && id != null)
            {
                if (unowned) count = UnownedConsumableCount;
                else
                {
                    count = Math.Max(0, leftThisVisit(id));
                    if (count == 0)
                    {
                        usedUp = true;
                        id = null;
                    }
                }
            }
            result[i] = new SlotGear(slot, id, name, count, choice, choiceName, source, usedUp, unowned);
        }
        return result;
    }

    /// <summary>
    /// What a battle is fought with: a custom battle's set gear wins; otherwise the arcade gear when
    /// it applies (the main-menu arcade, and choices or used consumables this visit); otherwise the
    /// player's own.
    /// </summary>
    internal static BattleGearKind KindFor(bool setsGear, bool arcadeGearApplies) =>
        setsGear ? BattleGearKind.SetGear : arcadeGearApplies ? BattleGearKind.Arcade : BattleGearKind.Own;

    /// <summary>
    /// Whether a battle with this gear keeps its score out of the save and counts for no
    /// achievements: it has an item the save doesn't own ("all items").
    /// </summary>
    internal static bool HoldsScores(IEnumerable<SlotGear> gear) => gear.Any(g => g.Unowned);

    /// <summary>What the Gear control says; <paramref name="ignoredEntry"/>: the save slot has choices that aren't used at all now.</summary>
    internal static GearLabel Label(IReadOnlyList<SlotGear> gear, bool ignoredEntry) => new(
        gear.Any(g => g.Source == GearSource.Arcade),
        ignoredEntry || gear.Any(g => g.Source is GearSource.NotOwned or GearSource.NoSuchItem or GearSource.OtherSlot),
        HoldsScores(gear));

    /// <summary>The Gear control's text (rich text, without the key).</summary>
    internal static string ControlText(GearLabel label) =>
        label.AllItems ? "Gear: arcade <color=#F2B02E>(all items: scores aren't saved)</color>"
        : label.Arcade ? "Gear: arcade"
        : label.Unused ? "Gear: story<color=#9D92B4>*</color>"
        : "Gear: story";

    /// <summary>The page's amber tag after a slot's row; null for plain story gear.</summary>
    internal static string? Tag(SlotGear g) => g.Source switch
    {
        GearSource.Arcade => g.Unowned ? "arcade, not owned" : g.UsedUp ? "arcade, used up this visit" : "arcade",
        GearSource.NotOwned => $"{g.ChoiceName ?? g.Choice} not owned: story gear",
        GearSource.NoSuchItem or GearSource.OtherSlot => "no such item: story gear",
        _ => g.UsedUp ? "used up this visit" : null
    };

    /// <summary>A slot's row on the page, like "Consumable: Mega Potion x2".</summary>
    internal static string RowText(SlotGear g) =>
        $"{GearCatalog.LabelOf(g.Slot)}: " + (g.Id != null && g.Slot == GearSlot.Consumable ? $"{g.Name} x{g.Count}" : g.Name ?? "(empty)");

    /// <summary>A slot row's hint (without the key that changes it); <paramref name="find"/> gives the item for its description.</summary>
    internal static string SlotHint(SlotGear g, Func<string, GearItem?> find)
    {
        const string UsedUpText = "None left this visit. It's full again the next time you open the arcade.";
        string instead = g.Id != null ? $"so your story gear is used ({g.Name})"
            : g.UsedUp ? $"so your story gear is used ({g.Name}, none left this visit)"
            : "so your story gear is used (nothing in this slot)";
        switch (g.Source)
        {
            case GearSource.NotOwned:
                return $"You don't have {g.ChoiceName ?? g.Choice} in this save any more, {instead}. Your choice is kept, and comes back if the save gets the item again.";
            case GearSource.NoSuchItem:
                return $"The game has no item \"{g.Choice}\", {instead}. Your choice is kept.";
            case GearSource.OtherSlot:
                return $"{g.ChoiceName ?? g.Choice} doesn't go in the {GearCatalog.LabelOf(g.Slot)} slot, {instead}.";
        }
        if (g.UsedUp) return UsedUpText;
        if (g.Id == null) return "Nothing in this slot.";
        string about = find(g.Id) is GearItem item ? GearCatalog.Hint(item) : $"(id {g.Id})";
        return g.Unowned ? about + "  Not owned: scores aren't saved and achievements don't count with it." : about;
    }

    /// <summary>The log's line for the gear, like "Weapon Ancient Katana (RPA2) [arcade], ... Consumable empty [arcade, Mega Potion used up this visit]".</summary>
    internal static string Describe(IEnumerable<SlotGear> gear) => string.Join(", ", gear.Select(DescribeSlot));

    private static string DescribeSlot(SlotGear g)
    {
        string what = g.Id == null ? "empty" : $"{g.Name} ({g.Id})" + (g.Slot == GearSlot.Consumable ? " x" + g.Count : "");
        var notes = new List<string> { g.Source == GearSource.Arcade ? "arcade" : "story" };
        if (g.Unowned) notes.Add("not owned, all items");
        switch (g.Source)
        {
            case GearSource.NotOwned: notes.Add($"{g.ChoiceName ?? g.Choice} not owned"); break;
            case GearSource.NoSuchItem: notes.Add($"no item \"{g.Choice}\""); break;
            case GearSource.OtherSlot: notes.Add($"{g.ChoiceName ?? g.Choice} is for another slot"); break;
        }
        if (g.UsedUp) notes.Add($"{g.Name} used up this visit");
        return $"{GearCatalog.LabelOf(g.Slot)} {what} [{string.Join(", ", notes)}]";
    }

    // ---- the slot's picker ---------------------------------------------------------------------------

    /// <summary>
    /// A slot's picker: "Story gear: ..." (clears the choice), "(empty)", then the slot's items the save
    /// owned when the arcade opened, or all of them with <paramref name="allItems"/>, in
    /// <paramref name="items"/>' order (a consumable with what's left this visit). A current choice
    /// the list doesn't hold (not owned, or no such item) gets a row of its own after "(empty)", so
    /// opening the picker just to look changes nothing.
    /// </summary>
    internal static List<PickRow> Picks(GearSlot slot, IEnumerable<GearItem> items, string? choice, string? storyId,
                                        IReadOnlyDictionary<string, int> visitStart, Func<string, int> leftThisVisit,
                                        Func<string, GearItem?> find, bool allItems)
    {
        string storyName = string.IsNullOrEmpty(storyId) ? "(empty)" : find(storyId!)?.Name ?? storyId!;
        var rows = new List<PickRow>
        {
            new(null, "Story gear: " + storyName, choice == null ? "current" : null, choice == null, null, false, false),
            new("", "(empty)", choice == "" ? "current" : null, choice == "", null, false, false),
        };
        var listed = items.Where(i => i.Slot == slot && (allItems || Owned(visitStart, i.Id))).ToList();
        var current = string.IsNullOrEmpty(choice) ? null : find(choice!);
        if (!string.IsNullOrEmpty(choice) && (current == null || !listed.Any(i => i.Id == current.Id)))
        {
            string why = current == null || current.Slot != slot ? "no such item" : "not owned";
            rows.Add(new PickRow(choice, current == null ? $"unknown item {choice}" : ItemText(current), "current, " + why, true, current, false,
                                 current != null && why == "not owned"));
        }
        foreach (var item in listed)
        {
            bool owned = Owned(visitStart, item.Id), isCurrent = current != null && current.Id == item.Id, usedUp = false;
            string text = ItemText(item);
            if (slot == GearSlot.Consumable && owned)
            {
                int left = Math.Max(0, leftThisVisit(item.Id));
                text += " x" + left;
                usedUp = left == 0;
            }
            var tags = new List<string>();
            if (isCurrent) tags.Add("current");
            if (!owned) tags.Add("not owned");
            if (usedUp) tags.Add("used up this visit");
            rows.Add(new PickRow(item.Id, text, tags.Count > 0 ? string.Join(", ", tags) : null, isCurrent, item, usedUp, !owned));
        }
        return rows;
    }

    private static string ItemText(GearItem item) => item.Debug ? item.Name + "  (test item)" : item.Name;

    /// <summary>What the page calls a slot's items, as in "This save has no amulets yet."</summary>
    internal static string ItemsOf(GearSlot slot) => slot switch
    {
        GearSlot.MainHand => "weapons",
        GearSlot.Body => "armor",
        GearSlot.Head => "head gear",
        GearSlot.OffHand => "off-hand items",
        GearSlot.Amulet => "amulets",
        _ => "consumables"
    };
}
