namespace NocturneFlatScroll;

/// <summary>
/// A QA aid for in-game tests: with the environment variable NFS_QA_GEARDUMP=1, every arcade
/// battle logs the equipment, gear counts, level and stats the game reads, at its start (before
/// and after a set-gear swap or a set level) and after it ends, so the lead can compare them.
/// </summary>
internal static partial class BattleGear
{
    private static readonly bool QaDump = QaBuild.Env("NFS_QA_GEARDUMP") == "1";

    private static readonly (string Name, CharacterStatType Stat)[] DumpedStats =
    {
        ("Health", CharacterStatType.Health), ("Strength", CharacterStatType.Strength),
        ("Dexterity", CharacterStatType.Dexterity), ("Armor", CharacterStatType.Armor),
        ("Regen", CharacterStatType.Regen), ("Critical", CharacterStatType.Critical)
    };

    private static void Dump(string when)
    {
        if (!QaDump) return;
        try
        {
            var inventory = GameDataManager.Inventory;
            var manager = inventory?.TryCast<PlayingInventoryManager>();
            var data = manager?.dataProvider?.TryCast<GameDataScriptableObject>()?.Save?.player;
            if (manager == null || data == null)
            {
                ModLog.Info($"Battle gear QA ({when}): no inventory is loaded.");
                return;
            }
            var current = swap;
            string whose = current != null && manager.Pointer == current.Manager.Pointer ? "the battle's" : "the player's";

            var equipment = data.equipmentData;
            string equipped = equipment == null ? "none" :
                $"mainHand={Id(equipment.mainHand)} body={Id(equipment.body)} head={Id(equipment.head)} " +
                $"offHand={Id(equipment.offHand)} amulet={Id(equipment.amulet)} consumable={Id(equipment.consumable)} pet={Id(equipment.pet)}";

            var gearIds = new HashSet<string>(GearCatalog.All.Select(i => i.Id), StringComparer.Ordinal);
            var gear = new List<string>();
            int others = 0, otherCount = 0;
            if (data.inventory != null)
            {
                foreach (var pair in data.inventory)
                {
                    string id = pair.Key ?? "";
                    if (gearIds.Contains(id) || id == ExtraHealthId) gear.Add($"{id} x{pair.Value}");
                    else
                    {
                        others++;
                        otherCount += pair.Value;
                    }
                }
            }
            gear.Sort(StringComparer.Ordinal);
            ModLog.Info($"Battle gear QA ({when}): {whose} inventory (manager {manager.Pointer.ToInt64():X}); equipped {equipped}; " +
                        $"gear {(gear.Count == 0 ? "none" : string.Join(", ", gear))}; {others} other items ({otherCount} in all); stats {Stats()}.");
        }
        catch (Exception ex) { ModLog.Error($"Battle gear QA ({when}) failed: {ex.Message}"); }
    }

    private static string Id(string? id) => string.IsNullOrEmpty(id) ? "-" : id!;

    private static string Stats()
    {
        try
        {
            var player = GameDataManager.PlayerData?.TryCast<PlayerDataManager>();
            var model = player?.statModel;
            if (player == null || model == null) return "unknown";
            // The level the game has now (it works it out with the stats), first.
            return $"Level={player.PlayerLevel} " + string.Join(" ", DumpedStats.Select(s => $"{s.Name}={model[s.Stat]:0.##}"));
        }
        catch (Exception ex) { return "unreadable (" + ex.Message + ")"; }
    }

    // ---- for the QA drivers: reads only --------------------------------------------------------------

    /// <summary>The level the game's stat updates use now instead of the player's, or null.</summary>
    internal static int? QaLevelOverride => levelOverride;

    /// <summary>Whether achievements are held back now (a custom battle, or a test play).</summary>
    internal static bool QaHoldsAchievements => HoldsAchievements;

    /// <summary>The level a battle asking for <paramref name="level"/> would play at, or null when the game's levels can't be read. Changes nothing.</summary>
    internal static int? QaClampLevel(int level) =>
        GameMaxLevel() is int max ? Math.Clamp(level, LevelDefinition.MinLevel, max) : null;
}
