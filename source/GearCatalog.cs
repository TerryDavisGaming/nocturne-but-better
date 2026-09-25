namespace NocturneFlatScroll;

/// <summary>One item a custom battle's creator can pick: its id (what saves and battle.json use) and name.</summary>
internal sealed record GearItem(string Id, string Name, GearSlot Slot, string Description, string AssetName, bool Debug);

/// <summary>
/// The game's weapons, armor, other equipment and consumables, read from its item database
/// (DataUtility.ItemDatabase), for the battle creator's gear page and for checking a battle's gear.
/// Consumables are equipment in the game's own data: items of type Equipment in the Consumable slot.
/// </summary>
internal static class GearCatalog
{
    private static List<GearItem>? items;

    // The game's test and debug items, shown only when asked for.
    private static readonly string[] DebugPrefixes =
        { "ItemData_Test", "ItemData_Simulator", "ItemData_RiposteTest", "ItemData_MineShield" };

    /// <summary>battle.json's key for a slot.</summary>
    internal static string KeyOf(GearSlot slot) => slot switch
    {
        GearSlot.MainHand => "mainHand",
        GearSlot.Body => "body",
        GearSlot.Head => "head",
        GearSlot.OffHand => "offHand",
        GearSlot.Amulet => "amulet",
        _ => "consumable"
    };

    /// <summary>What the creator calls a slot.</summary>
    internal static string LabelOf(GearSlot slot) => slot switch
    {
        GearSlot.MainHand => "Weapon",
        GearSlot.Body => "Armor",
        GearSlot.Head => "Head",
        GearSlot.OffHand => "Off hand",
        GearSlot.Amulet => "Amulet",
        _ => "Consumable"
    };

    /// <summary>Every item that goes in a gear slot, or an empty list while the game's database isn't loaded.</summary>
    internal static IReadOnlyList<GearItem> All
    {
        get
        {
            if (items == null || items.Count == 0) items = Read();
            return items;
        }
    }

    /// <summary>The items for one slot, sorted by name; debug items only when asked for.</summary>
    internal static List<GearItem> ForSlot(GearSlot slot, bool includeDebug = false) =>
        All.Where(i => i.Slot == slot && (includeDebug || !i.Debug))
           .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>An item by its id, or by its asset name (ItemData_...) as a fallback.</summary>
    internal static GearItem? Find(string? idOrAssetName)
    {
        string key = (idOrAssetName ?? "").Trim();
        if (key.Length == 0) return null;
        return All.FirstOrDefault(i => i.Id.Equals(key, StringComparison.Ordinal))
            ?? All.FirstOrDefault(i => i.Id.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(i => i.AssetName.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private static List<GearItem> Read()
    {
        var list = new List<GearItem>();
        try
        {
            var db = DataUtility.ItemDatabase;
            if (!db || db.Data == null) return list;
            var data = db.Data;
            for (int i = 0; i < data.Count; i++)
            {
                var item = data[i];
                if (!item || item.ItemType != ItemTypes.Equipment) continue;
                var slot = ToSlot(item.EquipmentSlot);
                if (slot == null || string.IsNullOrEmpty(item.ItemId)) continue;
                string asset = item.name ?? "";
                string name = SafeText(() => item.DisplayName);
                list.Add(new GearItem(item.ItemId, name.Length > 0 ? name : asset, slot.Value,
                    SafeText(() => item.Description), asset,
                    DebugPrefixes.Any(p => asset.StartsWith(p, StringComparison.OrdinalIgnoreCase))));
            }
        }
        catch (Exception ex) { ModLog.Error("Reading the game's items failed: " + ex.Message); }
        return list;
    }

    private static GearSlot? ToSlot(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.MainHand => GearSlot.MainHand,
        EquipmentSlot.Body => GearSlot.Body,
        EquipmentSlot.Head => GearSlot.Head,
        EquipmentSlot.OffHand => GearSlot.OffHand,
        EquipmentSlot.Amulet => GearSlot.Amulet,
        EquipmentSlot.Consumable => GearSlot.Consumable,
        _ => null
    };

    // Localized text can throw before the game's languages load.
    private static string SafeText(Func<string> read)
    {
        try { return read()?.Trim() ?? ""; }
        catch { return ""; }
    }
}
