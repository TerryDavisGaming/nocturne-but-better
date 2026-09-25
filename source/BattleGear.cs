using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>
/// Gear for custom battles. A battle whose battle.json sets its gear ("mode": "set") is fought
/// with exactly those items. When the battle starts, the game's inventory manager is swapped for
/// one that reads a throwaway save holding the battle's gear; everything that isn't gear (key
/// items, followers, the pet, money) is copied from the player's, and level-based stats stay the
/// player's. The player's own save objects are never written to, and the game's saves always
/// write its own save object, so a save during the battle still holds the player's own gear.
/// The player's manager goes back when the battle is left, and the player's stats are worked out
/// again from their own gear. Backstops put it back too, if that is ever missed.
/// </summary>
internal static partial class BattleGear
{
    private const string ExtraHealthId = "Item_ExtraHealth";

    private static readonly GearSlot[] Slots =
        { GearSlot.MainHand, GearSlot.Body, GearSlot.Head, GearSlot.OffHand, GearSlot.Amulet, GearSlot.Consumable };

    /// <summary>A set-gear battle's inventory, and what it replaced.</summary>
    private sealed class Swap
    {
        internal string Title = "";
        internal PlayingGameDataManager Game = null!;
        internal PlayerDataManager Player = null!;
        internal IInventoryManager Own = null!;              // the player's manager, put back afterwards
        internal PlayingInventoryManager Manager = null!;    // the battle's
        internal GameDataScriptableObject Holder = null!;    // the throwaway save the battle's manager reads
    }

    private static Swap? swap;
    // The set-gear battle running now, even when its gear couldn't be set.
    private static CustomBattles.Battle? setBattle;
    // An arcade battle has started and not been left yet.
    private static bool inBattle;
    private static readonly HashSet<string> Reported = new();

    /// <summary>Whether achievements can be held back during a battle (test play needs it).</summary>
    internal static bool AchievementsGuarded { get; private set; }

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        // Everything required is looked up first, so a missing method installs nothing.
        var start = Method(typeof(CombatManagerV3), "StartCombat");
        var exit = Method(typeof(CombatManagerV3), "ExitCombat");
        // Evaluate (the achievement check) takes an "in" parameter; blocking the one method it
        // unlocks through does the same without patching it.
        var unlock = Method(typeof(AchievementManager), "UnlockAchievement");
        var use = Method(typeof(CombatSimulator), "UseConsumable");
        var remove = Method(typeof(PlayingInventoryManager), "RemoveItem");
        // The guards and the restore come first, so if one of them can't be patched the swap isn't either.
        harmony.Patch(unlock, prefix: Hook(nameof(UnlockAchievementPrefix)));
        AchievementsGuarded = true;
        harmony.Patch(use, prefix: Hook(nameof(UseConsumablePrefix)), postfix: Hook(nameof(UseConsumablePostfix)));
        harmony.Patch(remove, prefix: Hook(nameof(RemoveItemPrefix)));
        harmony.Patch(exit, postfix: Hook(nameof(ExitCombatPostfix)));

        // Backstops: the battle's end puts the player's gear back; these only matter if it's missed.
        // Before a save, first, so the arcade's save block can't skip it.
        Optional(harmony, typeof(SaveFileManager), "SaveGameData", prefix: nameof(SavePrefix), first: true);
        foreach (var autoSave in AccessTools.GetDeclaredMethods(typeof(SaveFileManager)).Where(m => m.Name == "AutoSave"))
            Optional(harmony, autoSave, prefix: nameof(SavePrefix), first: true);
        foreach (var name in new[] { "SetCurrentGameData", "LoadFilename", "CreateNewData", "EraseAllData" })
            Optional(harmony, typeof(SaveFileManager), name, prefix: nameof(LoadPrefix));
        foreach (var name in new[] { "Continue", "LoadGame", "NewGame", "Gauntlet" })
            Optional(harmony, typeof(MainMenu), name, prefix: nameof(TitlePrefix));
        Optional(harmony, typeof(ArcadeMenuV2), "Deactivate", postfix: nameof(ArcadeClosedPostfix));

        // The swap itself comes last.
        harmony.Patch(start, prefix: Hook(nameof(StartCombatPrefix)));
    }

    private static MethodInfo Method(Type type, string name) =>
        AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);

    private static HarmonyMethod Hook(string name) => new(typeof(BattleGear), name);

    private static void Optional(HarmonyLib.Harmony harmony, Type type, string method, string? prefix = null, string? postfix = null,
                                 bool first = false)
    {
        var target = AccessTools.DeclaredMethod(type, method);
        if (target == null) ModLog.Error($"Battle gear: {type.Name}.{method} is missing; the gear works without that backstop.");
        else Optional(harmony, target, prefix, postfix, first);
    }

    private static void Optional(HarmonyLib.Harmony harmony, MethodInfo target, string? prefix = null, string? postfix = null,
                                 bool first = false)
    {
        try
        {
            HarmonyMethod? before = null;
            if (prefix != null)
            {
                before = Hook(prefix);
                if (first) before.priority = Priority.First;
            }
            harmony.Patch(target, prefix: before, postfix: postfix == null ? null : Hook(postfix));
        }
        catch (Exception ex)
        {
            ModLog.Error($"Battle gear: {target.DeclaringType?.Name}.{target.Name} could not be patched; the gear works without that backstop: {ex}");
        }
    }

    // ---- the battle ------------------------------------------------------------------------------

    // Runs after the game's own ExitCombat at the start of every battle, and before the battle
    // reads the player's gear, stats and consumable.
    private static void StartCombatPrefix(CombatOptions combatOptions)
    {
        usingConsumable = false;
        try
        {
            if (swap != null) Restore("a new battle started", backstop: true);
            setBattle = null;
            inBattle = ArcadeUtility.IsRunning;
            if (!inBattle) return;
            Dump("battle start");
            var battle = combatOptions != null ? CustomBattles.Find(combatOptions.Song) : null;
            if (battle == null || !battle.Package.Gear.IsSet) return;
            setBattle = battle;
            if (swap != null)
            {
                // The last battle's inventory couldn't be taken out. Another one on top of it would
                // lose the player's own, so this battle is played as if its swap failed.
                ModLog.Error($"Battle gear: {battle.Title}: the last battle's gear is still in, so this battle's gear can't be set " +
                             "(your consumables are kept).");
                Dump("battle start, the swap failed");
                return;
            }
            try
            {
                SetGear(battle);
            }
            catch (Exception ex)
            {
                if (swap != null) Restore("the battle's gear couldn't be set", backstop: false);
                ModLog.Error($"Battle gear: {battle.Title}: the battle's gear couldn't be set, so you fight with your own gear " +
                             $"(your consumables are kept): {ex}");
            }
            Dump(swap != null ? "battle start, after the swap" : "battle start, the swap failed");
        }
        catch (Exception ex) { Report(ex); }
    }

    // After the game's own ExitCombat: the end of every battle (won, lost, quit), and the start
    // of the next one.
    private static void ExitCombatPostfix()
    {
        usingConsumable = false;
        try
        {
            bool ended = inBattle, swapped = swap != null;
            inBattle = false;
            setBattle = null;
            if (swapped) Restore("the battle ended", backstop: false);
            if (ended) Dump(swapped ? "battle end, after the restore" : "battle end");
        }
        catch (Exception ex) { Report(ex); }
    }

    /// <summary>Gives the game an inventory with only the battle's gear, for this battle.</summary>
    private static void SetGear(CustomBattles.Battle battle)
    {
        var gear = battle.Package.Gear;
        // Swapping twice would lose the player's own inventory behind the first swap.
        if (swap != null) throw new InvalidOperationException("the last battle's gear is still in");
        // Without the item list, gear can't be told from other items.
        if (GearCatalog.All.Count == 0) throw new InvalidOperationException("the game's items aren't loaded");
        var game = GameDataManager.Instance?.TryCast<PlayingGameDataManager>()
            ?? throw new InvalidOperationException("the game's data isn't loaded");
        var player = game.playerDataManager ?? throw new InvalidOperationException("the game has no player data");
        var own = game.inventoryManager ?? throw new InvalidOperationException("the game has no inventory");
        var ownManager = own.TryCast<PlayingInventoryManager>()
            ?? throw new InvalidOperationException("the inventory isn't the game's own kind");
        var ownSave = ownManager.dataProvider?.TryCast<GameDataScriptableObject>()
            ?? throw new InvalidOperationException("the player's save isn't loaded");
        if (CustomBattles.IsRuntimeName(ownSave.name))
            throw new InvalidOperationException("the game reads a battle's inventory, not the player's");
        var ownData = ownSave.Save?.player ?? throw new InvalidOperationException("the player's save isn't loaded");
        var database = ownManager.Database ?? DataUtility.ItemDatabase;

        // A fresh save: its constructor makes the player's inventory and equipment.
        var save = new SavedGameDataV105();
        var data = save.player ?? throw new InvalidOperationException("a new save has no player data");
        int ownHealth = CopyNonGear(ownData, data);
        CopyCurrency(ownManager, data, battle);

        var equipment = data.equipmentData ?? throw new InvalidOperationException("a new save has no equipment");
        var parts = new List<string>();
        foreach (var slot in Slots)
        {
            var item = Pick(battle, slot);
            if (item == null)
            {
                parts.Add(GearCatalog.LabelOf(slot) + " empty");
                continue;
            }
            // Equipped items are in the inventory too, as the game keeps them.
            int count = slot == GearSlot.Consumable ? gear.ConsumableCount : 1;
            data.inventory[item.Id] = count;
            SetSlot(equipment, slot, item.Id);
            parts.Add($"{GearCatalog.LabelOf(slot)} {item.Name} ({item.Id}){(slot == GearSlot.Consumable ? " x" + count : "")}");
        }
        equipment.pet = ownData.equipmentData?.pet;

        // Health upgrades count as items; the battle's number, or the player's own.
        int health = ownHealth;
        if (gear.extraHealth is int wanted)
        {
            health = Math.Clamp(wanted, 0, GearDefinition.MaxCount);
            if (health != wanted) Note($"Battle gear: {battle.Title}: {wanted} health upgrades can't be used; it plays with {health}.");
        }
        if (health > 0) data.inventory[ExtraHealthId] = health;
        parts.Add($"health upgrades {health}{(gear.extraHealth == null ? " (your own)" : "")}");

        var holder = ScriptableObject.CreateInstance(Il2CppType.Of<GameDataScriptableObject>())?.TryCast<GameDataScriptableObject>()
            ?? throw new InvalidOperationException("the game couldn't make a save holder");
        holder.name = CustomBattles.RuntimePrefix + "gear";
        holder.hideFlags = HideFlags.HideAndDontSave;
        holder.SetData(save, new SavedScoresData());
        PlayingInventoryManager manager;
        try { manager = new PlayingInventoryManager(database, new ISaveGameDataProvider(holder.Pointer)); }
        catch
        {
            Object.Destroy(holder);
            throw;
        }

        // Noted before anything is written, so a failure part-way puts the player's manager back.
        swap = new Swap { Title = battle.Title, Game = game, Player = player, Own = own, Manager = manager, Holder = holder };
        var battleInventory = new IInventoryManager(manager.Pointer);
        game.inventoryManager = battleInventory;
        player.inventory = battleInventory;
        var now = GameDataManager.Inventory;
        if (now == null || now.Pointer != manager.Pointer)
            throw new InvalidOperationException("the game still reads the player's inventory after the swap");
        // The battle works this out again when it starts; done here too so the stats are right from now on.
        player.UpdateStatModel();
        ModLog.Info($"Battle gear: {battle.Title}: set gear for this battle: {string.Join(", ", parts)}.");
    }

    /// <summary>
    /// Copies the player's items that aren't gear (key items, followers, the pet) into the
    /// battle's inventory. Returns the player's health upgrades, which are set on their own.
    /// </summary>
    private static int CopyNonGear(SavedPlayerData own, SavedPlayerData data)
    {
        int health = 0;
        var inventory = own.inventory;
        if (inventory == null) return 0;
        var gear = new HashSet<string>(GearCatalog.All.Select(i => i.Id), StringComparer.Ordinal);
        foreach (var pair in inventory)
        {
            string id = pair.Key;
            if (string.IsNullOrEmpty(id)) continue;
            if (id == ExtraHealthId) health = pair.Value;
            else if (!gear.Contains(id)) data.inventory[id] = pair.Value;
        }
        return health;
    }

    // Money isn't gear, and screens during the battle (the pause menu) may show it.
    private static void CopyCurrency(PlayingInventoryManager own, SavedPlayerData data, CustomBattles.Battle battle)
    {
        try
        {
            foreach (var type in new[] { CurrencyType.Tokens, CurrencyType.Coupons })
            {
                int amount = own.GetCurrency(type);
                if (amount > 0) data.currency[type] = amount;
            }
        }
        catch (Exception ex) { Note($"Battle gear: {battle.Title}: your money couldn't be copied, so the battle shows none: {ex.Message}"); }
    }

    /// <summary>The item battle.json names for a slot, or null for an empty slot. Unknown items are logged once.</summary>
    private static GearItem? Pick(CustomBattles.Battle battle, GearSlot slot)
    {
        string? id = battle.Package.Gear.ItemFor(slot);
        if (id == null) return null;
        var item = GearCatalog.Find(id);
        string label = GearCatalog.LabelOf(slot);
        if (item == null)
        {
            Note($"Battle gear: {battle.Title}: the game has no item \"{id}\", so the {label} slot is empty.");
            return null;
        }
        if (item.Slot != slot)
        {
            Note($"Battle gear: {battle.Title}: {item.Name} ({item.Id}) goes in the {GearCatalog.LabelOf(item.Slot)} slot, not {label}, so the {label} slot is empty.");
            return null;
        }
        return item;
    }

    private static void SetSlot(EquipmentData equipment, GearSlot slot, string id)
    {
        switch (slot)
        {
            case GearSlot.MainHand: equipment.mainHand = id; break;
            case GearSlot.Body: equipment.body = id; break;
            case GearSlot.Head: equipment.head = id; break;
            case GearSlot.OffHand: equipment.offHand = id; break;
            case GearSlot.Amulet: equipment.amulet = id; break;
            default: equipment.consumable = id; break;
        }
    }

    /// <summary>Puts the player's own inventory back and works out their stats again. Safe to call again.</summary>
    private static void Restore(string reason, bool backstop)
    {
        var current = swap;
        if (current == null) return;
        try
        {
            current.Game.inventoryManager = current.Own;
            current.Player.inventory = current.Own;
        }
        catch (Exception ex)
        {
            // Kept, so the next backstop tries again.
            Note("Battle gear: putting your own gear back failed; it's tried again later: " + ex);
            return;
        }
        swap = null;
        try { current.Player.UpdateStatModel(); }
        catch (Exception ex) { Note("Battle gear: working out your stats again failed: " + ex); }
        try { if (current.Holder) Object.Destroy(current.Holder); }
        catch (Exception ex) { Report(ex); }
        var now = GameDataManager.Inventory;
        if (now == null || now.Pointer != current.Own.Pointer)
            ModLog.Error("Battle gear: WARNING: the game doesn't read your own inventory after putting it back.");
        ModLog.Info(backstop
            ? $"Battle gear: backstop: {reason}; your own gear is back after {current.Title}."
            : $"Battle gear: your own gear is back after {current.Title} ({reason}).");
        if (backstop) Dump("after the backstop");
    }

    // ---- backstops -------------------------------------------------------------------------------

    // Something of a set-gear battle is still in: its inventory, or its holds on achievements and item use.
    private static bool Pending => swap != null || setBattle != null || usingConsumable;

    /// <summary>Ends what's left of a set-gear battle when no arcade battle can be running any more.</summary>
    internal static void Backstop(string reason)
    {
        if (!Pending) return;
        try
        {
            if (!ArcadeUtility.IsRunning) EndBattle(reason);
        }
        catch (Exception ex) { Report(ex); }
    }

    /// <summary>Called every frame; cheap unless a set-gear battle is running or wasn't ended.</summary>
    internal static void Update()
    {
        if (Pending) Backstop("the arcade isn't running any more");
    }

    /// <summary>Puts the player's gear back and lifts the battle's holds on achievements and item use.</summary>
    private static void EndBattle(string reason)
    {
        var battle = setBattle;
        setBattle = null;
        usingConsumable = false;
        inBattle = false;
        if (swap != null) Restore(reason, backstop: true);
        else if (battle != null)
            ModLog.Info($"Battle gear: backstop: {reason}; {battle.Title} is over, so achievements and items work as usual again.");
    }

    // A save only ever writes the player's own save object, so it can't hold the battle's gear.
    private static void SavePrefix(MethodBase __originalMethod)
    {
        if (!Pending) return;
        Backstop("a save started");
        if (swap != null)
            Note($"Battle gear: {__originalMethod?.Name} ran during a set-gear battle; the save holds your own gear, not the battle's.", error: false);
    }

    private static void LoadPrefix(MethodBase __originalMethod) => Backstop($"a save is loading ({__originalMethod?.Name})");

    // Nothing on the title screen runs during a battle.
    private static void TitlePrefix(MethodBase __originalMethod)
    {
        if (!Pending) return;
        try { EndBattle($"MainMenu.{__originalMethod?.Name}"); }
        catch (Exception ex) { Report(ex); }
    }

    private static void ArcadeClosedPostfix(bool exiting)
    {
        if (exiting) Backstop("the arcade menu closed");
    }

    // ---- achievements ------------------------------------------------------------------------------

    // The game checks achievements on many events, not only at a battle's end; none of them may
    // count the battle's gear as the player's. A test play from the chart editor counts for none.
    private static bool UnlockAchievementPrefix(string achievementId)
    {
        try
        {
            bool testing = TestPlay.Active;
            if (setBattle == null && swap == null && !testing) return true;
            Note(testing
                ? $"Test play: held back achievement {achievementId}."
                : $"Battle gear: held back achievement {achievementId}: set-gear battles don't count towards achievements.", error: false);
            return false;
        }
        catch (Exception ex)
        {
            Report(ex);
            return true;
        }
    }

    // ---- logging -----------------------------------------------------------------------------------

    private static bool reportedError;

    private static void Report(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        ModLog.Error("Battle gear failed: " + ex);
    }

    /// <summary>Logs a message once a session.</summary>
    private static void Note(string message, bool error = true)
    {
        if (!Reported.Add(message)) return;
        if (error) ModLog.Error(message);
        else ModLog.Info(message);
    }
}
