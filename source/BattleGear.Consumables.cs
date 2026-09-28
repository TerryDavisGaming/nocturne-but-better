namespace NocturnePlus;

/// <summary>
/// Consumables in arcade battles. Using one takes it out of the inventory at once (the game's
/// RemoveConsumable effect calls RemoveItem). With "Infinite consumables (arcade)" on, that
/// removal is skipped for the player's own inventory while the arcade runs. The game's other
/// limits stay: one use per battle and the cooldown. A set-gear battle uses up its own
/// consumables as usual; they are the battle's, not the player's. The arcade gear's copy of the
/// player's inventory follows the setting too; with it off, a use counts down the copy and is
/// noted for the rest of the visit (ArcadeGear.NoteUsed), so every later battle has one fewer.
/// </summary>
internal static partial class BattleGear
{
    // Set while the game uses the equipped consumable (CombatSimulator.UseConsumable).
    private static bool usingConsumable;

    private static void UseConsumablePrefix() => usingConsumable = true;

    private static void UseConsumablePostfix() => usingConsumable = false;

    private static bool RemoveItemPrefix(PlayingInventoryManager __instance, ItemData item, int amount)
    {
        if (!usingConsumable) return true;
        try
        {
            if (__instance == null || item == null) return true;
            var current = swap;
            if (current != null && __instance.Pointer == current.Manager.Pointer)
            {
                // The set-gear battle's own inventory counts down as usual.
                if (!current.Loadout) return true;
                if (SettingsState.InfiniteArcadeConsumables)
                {
                    ModLog.Info($"Infinite consumables: kept {ItemName(item)}.");
                    return false;
                }
                // The copy counts down, and the visit remembers it (items the save owns only).
                ArcadeGear.NoteUsed(item.ItemId, amount);
                return true;
            }
            if (setBattle != null)
            {
                // A set-gear battle never uses up the player's own items, even when its gear couldn't be set.
                Note($"Battle gear: kept your {ItemName(item)}; a set-gear battle doesn't use up your own items.", error: false);
                return false;
            }
            if (!SettingsState.InfiniteArcadeConsumables || !ArcadeUtility.IsRunning) return true;
            ModLog.Info($"Infinite consumables: kept {ItemName(item)}.");
            return false;
        }
        catch (Exception ex)
        {
            Report(ex);
            return true;
        }
    }

    private static string ItemName(ItemData item)
    {
        string id = item.ItemId ?? "?";
        try
        {
            string name = item.DisplayName?.Trim() ?? "";
            return name.Length > 0 ? $"{name} ({id})" : id;
        }
        catch { return id; }
    }
}
