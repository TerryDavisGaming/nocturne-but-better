using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NocturnePlus;

/// <summary>
/// The main-menu arcade's own gear in a battle (ArcadeGear). It goes in the way set gear does: the
/// battle runs on a throwaway inventory, here a copy of everything the player has (less the
/// consumables the arcade gear used up this visit) with the arcade gear equipped. The level, the
/// health upgrades and the pet stay the player's own. Nothing of the player's is written, and the
/// battle's end and the backstops take the copy out again. An item the save doesn't own ("all
/// items") also keeps the battle's score out of the save (BattleGear.Scores.cs) and holds its
/// achievements back.
/// </summary>
internal static partial class BattleGear
{
    /// <summary>The player's side of the game's data, which a swap replaces and puts back.</summary>
    private sealed class OwnSide
    {
        internal PlayingGameDataManager Game = null!;
        internal PlayerDataManager Player = null!;
        internal IInventoryManager Inventory = null!;
        internal PlayingInventoryManager Manager = null!;
        internal SavedPlayerData Data = null!;
        internal ItemDatabase Database = null!;
    }

    /// <summary>The player's own inventory and save data; throws with the reason when there is none, or a battle's is in.</summary>
    private static OwnSide FindOwn()
    {
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
        return new OwnSide { Game = game, Player = player, Inventory = own, Manager = ownManager, Data = ownData, Database = database };
    }

    /// <summary>
    /// The player's own save data in memory, for reading only (the arcade gear page); null, with the
    /// reason in <paramref name="why"/>, when there is none or a battle's inventory is in.
    /// </summary>
    internal static SavedPlayerData? OwnPlayerData(out string? why)
    {
        why = null;
        try { return FindOwn().Data; }
        catch (Exception ex)
        {
            why = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Puts a battle's inventory in: a holder for <paramref name="save"/> and a new manager reading
    /// it, noted as the swap before anything is written, then the game's two references to the
    /// player's manager pointed at it, and the stats worked out again.
    /// </summary>
    private static void PutIn(OwnSide own, SavedGameDataV105 save, string title, bool loadout, bool allItems, string holderName)
    {
        var holder = ScriptableObject.CreateInstance(Il2CppType.Of<GameDataScriptableObject>())?.TryCast<GameDataScriptableObject>()
            ?? throw new InvalidOperationException("the game couldn't make a save holder");
        holder.name = CustomBattles.RuntimePrefix + holderName;
        holder.hideFlags = HideFlags.HideAndDontSave;
        holder.SetData(save, new SavedScoresData());
        PlayingInventoryManager manager;
        try { manager = new PlayingInventoryManager(own.Database, new ISaveGameDataProvider(holder.Pointer)); }
        catch
        {
            Object.Destroy(holder);
            throw;
        }

        // Noted before anything is written, so a failure part-way puts the player's manager back.
        swap = new Swap
        {
            Title = title, Loadout = loadout, AllItems = allItems, Game = own.Game, Player = own.Player, Own = own.Inventory,
            Manager = manager, Holder = holder
        };
        var battleInventory = new IInventoryManager(manager.Pointer);
        own.Game.inventoryManager = battleInventory;
        own.Player.inventory = battleInventory;
        var now = GameDataManager.Inventory;
        if (now == null || now.Pointer != manager.Pointer)
            throw new InvalidOperationException("the game still reads the player's inventory after the swap");
        // The battle works this out again when it starts; done here too so the stats are right from now on.
        own.Player.UpdateStatModel();
    }

    // The arcade gear for this battle, or the story gear when it can't be put in.
    private static void SetArcadeGear(string title)
    {
        if (swap != null)
        {
            // The last battle's inventory couldn't be taken out. Another one on top of it would lose the
            // player's own, so this battle is fought with the last battle's gear (StartCombatPrefix keeps
            // its score out when that gear has items the save doesn't own).
            ModLog.Error($"Arcade gear: {title}: the last battle's gear is still in, so you fight with that gear this time.");
            return;
        }
        try { SetLoadout(title); }
        catch (Exception ex)
        {
            if (heldScores != null) ReleaseScores("the arcade gear couldn't be put in", backstop: false);
            if (swap != null) Restore("the arcade gear couldn't be put in", backstop: false);
            ModLog.Error($"Arcade gear: {title}: the arcade gear couldn't be put in, so you fight with your story gear: {ex}");
        }
    }

    /// <summary>Gives the game a copy of the player's inventory with the arcade gear equipped, for this battle.</summary>
    private static void SetLoadout(string title)
    {
        var own = FindOwn();
        var gear = ArcadeGear.Resolve(own.Data);
        bool allItems = ArcadeGearRules.HoldsScores(gear);

        // A fresh save: its constructor makes the player's inventory and equipment.
        var save = new SavedGameDataV105();
        var data = save.player ?? throw new InvalidOperationException("a new save has no player data");
        // Everything the player has, gear, health upgrades, key items and followers included, less
        // what the arcade gear used up this visit.
        var inventory = own.Data.inventory;
        if (inventory != null)
        {
            foreach (var pair in inventory)
            {
                string id = pair.Key;
                if (string.IsNullOrEmpty(id)) continue;
                int count = pair.Value - ArcadeGear.UsedCount(id);
                if (count > 0) data.inventory[id] = count;
            }
        }
        CopyCurrency(own.Manager, data, title);

        var equipment = data.equipmentData ?? throw new InvalidOperationException("a new save has no equipment");
        foreach (var slot in gear)
        {
            // An empty slot keeps the fresh save's empty value, as set gear leaves it.
            if (slot.Id == null) continue;
            // Equipped items are in the inventory too, as the game keeps them: an item the save doesn't own is added.
            if (slot.Unowned || !data.inventory.ContainsKey(slot.Id)) data.inventory[slot.Id] = Math.Max(1, slot.Count);
            SetSlot(equipment, slot.Slot, slot.Id);
        }
        equipment.pet = own.Data.equipmentData?.pet;

        // The level before the swap's stat update, which a battle's level would change.
        string level = levelOverride is int set ? $"level {set} is the battle's" : $"level {(PlayerLevel() is int l ? l.ToString() : "?")} is your own";
        int health = data.inventory.ContainsKey(ExtraHealthId) ? data.inventory[ExtraHealthId] : 0;
        PutIn(own, save, title, loadout: true, allItems, "arcadegear");
        ModLog.Info($"Arcade gear: {title}: {ArcadeGearRules.Describe(gear)}; health upgrades {health} are your own and {level}.");
        // Its score isn't saved, and its achievements are held back while the swap is in.
        if (allItems) HoldScores(title);
    }

    // A game song's name for the log (its SongData's asset name).
    private static string SongTitle(SongData? song)
    {
        try { return song != null && song ? song.name : "the battle"; }
        catch { return "the battle"; }
    }
}
