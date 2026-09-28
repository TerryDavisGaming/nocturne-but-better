using System.Text;
using System.Text.Json.Nodes;
using UnityEngine;

namespace NocturnePlus;

/// <summary>
/// The main-menu arcade's own gear: the weapon, armor, head, off hand, amulet and consumable the
/// player picks for the arcade opened from the main menu, on the Arcade gear page
/// (ArcadeGear.Page.cs), which the Gear control in the arcade's title bar opens
/// (ArcadeGear.Control.cs). The choices are kept per save slot in NocturneButBetter\ArcadeGear.json,
/// and every battle there whose gear a custom battle doesn't set is fought with them
/// (BattleGear.Loadout.cs). The story save is never written, not even in memory: the page only
/// reads it, and a battle runs on a copy, as a set-gear battle does. Only items the save owned
/// when the arcade opened can be used, unless "All items (arcade gear)" is on; a battle with an
/// item the save doesn't own then saves no score and counts for no achievements. The story's own
/// arcade cabinet is unchanged. The rules are in ArcadeGear.Rules.cs.
/// </summary>
internal static partial class ArcadeGear
{
    internal const string FileName = "ArcadeGear.json";
    // A few hundred bytes in use; anything this big isn't the mod's.
    private const long MaxFileBytes = 1 << 20;
    private static readonly IReadOnlyDictionary<GearSlot, string> NoChoices = new Dictionary<GearSlot, string>();

    // Between SessionStarted and SessionEnded: a main-menu arcade visit.
    private static bool session;
    private static string? path;
    private static JsonObject root = ArcadeGearRules.NewRoot();
    private static int format = ArcadeGearRules.Format;
    // Why the file couldn't be read. It's left alone until a change, which moves it aside first.
    private static string? readProblem;
    private static int saveSlot = 1;
    private static double playTime;
    // The save's item counts when the visit began: what it owns.
    private static readonly Dictionary<string, int> visitStart = new(StringComparer.Ordinal);
    // Consumables the arcade gear used up this visit. Never written anywhere.
    private static readonly Dictionary<string, int> used = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Reported = new();

    /// <summary>What the Gear control says, worked out when the visit starts, when the page closes and after each battle.</summary>
    internal static GearLabel Label { get; private set; }

    /// <summary>
    /// Whether the next battle with the arcade gear keeps its score out of the save (it has an item
    /// the save doesn't own). Only when the arcade gear is put in at all, which the battle and the control check too.
    /// </summary>
    internal static bool ScoresOff => Label.AllItems && Applies;

    /// <summary>The save slot of this visit, as its scores are written.</summary>
    internal static int SaveSlot => saveSlot;

    // ---- the visit ---------------------------------------------------------------------------------

    /// <summary>A main-menu arcade visit started, on the save just read from disk.</summary>
    internal static void SessionStarted(int slot)
    {
        ClosePage("a new arcade visit started");
        session = true;
        saveSlot = slot;
        used.Clear();
        visitStart.Clear();
        playTime = 0;
        // Application.persistentDataPath: main thread only.
        path = Path.Combine(Application.persistentDataPath, "NocturneButBetter", FileName);
        Read();
        TakeSnapshot();
        menu = null;
        nextMenuSearch = 0;
        wasRunning = false;
        Refresh();
        // Quiet for players who never chose any.
        if (Entry.Choices.Count > 0 || readProblem != null)
            ModLog.Info($"Arcade gear: save slot {saveSlot}: {BattleNotice.StripTags(ArcadeGearRules.ControlText(Label))}" +
                        (EarlierGame ? " (the choices saved for it are from an earlier game in this slot)" : "") + ".");
    }

    /// <summary>The visit ended: the page closes and the control goes; the file stays as it is.</summary>
    internal static void SessionEnded()
    {
        ClosePage("the arcade session ended");
        session = false;
        used.Clear();
        visitStart.Clear();
        Label = default;
        HideControl();
    }

    // The save as the visit began: its item counts (what it owns) and its play time.
    private static void TakeSnapshot()
    {
        try
        {
            var save = ArcadeSession.SaveFile()?.currentSaveData?.saveData;
            playTime = save?.world?.playTime ?? 0;
            var inventory = save?.player?.inventory;
            if (inventory == null) return;
            foreach (var pair in inventory)
                if (!string.IsNullOrEmpty(pair.Key)) visitStart[pair.Key] = pair.Value;
        }
        catch (Exception ex) { Note("Arcade gear: the save couldn't be read, so no item counts as owned this visit: " + ex.Message); }
    }

    private static void Read()
    {
        root = ArcadeGearRules.NewRoot();
        format = ArcadeGearRules.Format;
        readProblem = null;
        try
        {
            var info = new FileInfo(path!);
            if (!info.Exists) return;
            string? problem;
            if (info.Length > MaxFileBytes) problem = "it's too big";
            else
            {
                var parsed = ArcadeGearRules.Parse(File.ReadAllText(path!, Encoding.UTF8), out problem);
                if (parsed != null)
                {
                    root = parsed;
                    format = ArcadeGearRules.FormatOf(parsed);
                    if (ReadOnly)
                        ModLog.Error($"Arcade gear: {FileName} was made by a newer version of the mod (format {format}), so the arcade uses your story gear and the file isn't changed.");
                    return;
                }
            }
            readProblem = problem;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            readProblem = ex.Message;
        }
        ModLog.Error($"Arcade gear: {FileName} couldn't be read ({readProblem}), so the arcade uses your story gear. It's left as it is until you change a slot.");
    }

    // ---- the choices ---------------------------------------------------------------------------------

    /// <summary>A file from a newer version of the mod: only read, and not used.</summary>
    internal static bool ReadOnly => format > ArcadeGearRules.Format;

    private static SaveEntry Entry => ArcadeGearRules.Entry(root, saveSlot);

    /// <summary>Whether this save slot's entry was set in an earlier game in the slot, so it isn't used.</summary>
    internal static bool EarlierGame => session && ArcadeGearRules.FromEarlierGame(Entry, playTime);

    /// <summary>The choices this visit uses: none from an earlier game, or from a newer file.</summary>
    internal static IReadOnlyDictionary<GearSlot, string> Choices => !session || ReadOnly || EarlierGame ? NoChoices : Entry.Choices;

    // The save slot has choices that aren't used at all now.
    private static bool IgnoredEntry => session && (EarlierGame || ReadOnly) && Entry.Choices.Count > 0;

    /// <summary>Whether "all items" can work: the score save and the achievements can be held back.</summary>
    internal static bool AllItemsAvailable => TestPlay.ScoresGuarded && BattleGear.AchievementsGuarded && CustomBattles.EndGuarded;

    /// <summary>Whether items the save doesn't own may be used now.</summary>
    internal static bool AllItems => SettingsState.ArcadeGearAllItems && AllItemsAvailable;

    /// <summary>
    /// Whether a battle now is fought with the arcade gear: in the main-menu arcade, when this save
    /// has choices, or when the arcade gear used up a consumable this visit (so its count stays right).
    /// </summary>
    internal static bool Applies => session && ArcadeSession.Active && BattleGear.Installed && (Choices.Count > 0 || used.Count > 0);

    /// <summary>What each slot holds in the next battle, from the player's save data in memory (read only).</summary>
    internal static SlotGear[] Resolve(SavedPlayerData own)
    {
        var counts = Counts(own);
        var equipment = own.equipmentData;
        return ArcadeGearRules.Resolve(Choices, slot => StoryItem(equipment, slot), visitStart, id => LeftThisVisit(counts, id),
                                       GearCatalog.Find, AllItems);
    }

    /// <summary>The save's item counts now.</summary>
    private static Dictionary<string, int> Counts(SavedPlayerData own)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var inventory = own.inventory;
        if (inventory == null) return counts;
        foreach (var pair in inventory)
            if (!string.IsNullOrEmpty(pair.Key)) counts[pair.Key] = pair.Value;
        return counts;
    }

    // What's left of an item this visit: the save's count less what the arcade gear used up.
    private static int LeftThisVisit(Dictionary<string, int> counts, string id) =>
        Math.Max(0, (counts.TryGetValue(id, out int count) ? count : 0) - UsedCount(id));

    private static string? StoryItem(EquipmentData? equipment, GearSlot slot)
    {
        if (equipment == null) return null;
        return slot switch
        {
            GearSlot.MainHand => equipment.mainHand,
            GearSlot.Body => equipment.body,
            GearSlot.Head => equipment.head,
            GearSlot.OffHand => equipment.offHand,
            GearSlot.Amulet => equipment.amulet,
            _ => equipment.consumable
        };
    }

    /// <summary>How many of an item the arcade gear used up this visit.</summary>
    internal static int UsedCount(string id) => used.TryGetValue(id, out int count) ? count : 0;

    /// <summary>A battle with the arcade gear used a consumable: noted for the rest of the visit when the save owns it.</summary>
    internal static void NoteUsed(string? id, int amount)
    {
        if (!session || string.IsNullOrEmpty(id) || amount <= 0 || !ArcadeGearRules.Owned(visitStart, id!)) return;
        used[id!] = UsedCount(id!) + amount;
        ModLog.Info($"Arcade gear: used {id} ({used[id!]} this visit, back when you leave the arcade).");
    }

    // ---- changing them ---------------------------------------------------------------------------------

    /// <summary>Sets a slot's choice (null: story gear) and writes the file. False, with the reason in <paramref name="message"/>, when nothing changed.</summary>
    private static bool Pick(GearSlot slot, string? value, out string? message) =>
        Change(r => ArcadeGearRules.SetChoice(r, saveSlot, slot, value, playTime),
               $"{GearCatalog.LabelOf(slot)} is {(value == null ? "story gear" : value.Length == 0 ? "empty" : value)}", out message);

    /// <summary>"Use the gear I set before": makes an earlier game's choices this game's.</summary>
    private static bool Adopt(out string? message) =>
        Change(r => ArcadeGearRules.Adopt(r, saveSlot, playTime), "kept the gear set before", out message);

    /// <summary>"Use my story gear in every slot".</summary>
    private static bool ClearAll(out string? message) =>
        Change(r => ArcadeGearRules.ClearSave(r, saveSlot), "story gear in every slot", out message);

    // A change goes into the file at once. When it can't be written, it's still used until the visit ends.
    private static bool Change(Action<JsonObject> change, string what, out string? message)
    {
        message = null;
        if (!session || path == null)
        {
            message = "The arcade isn't open.";
            return false;
        }
        if (ReadOnly)
        {
            message = $"{FileName} was made by a newer version of the mod, so it can't be changed here.";
            return false;
        }
        if (readProblem != null)
        {
            // The unreadable file is kept beside the new one.
            try
            {
                if (File.Exists(path)) File.Move(path, path + ".bad", true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                message = $"{FileName} can't be read, and it couldn't be moved aside to start a new one ({ex.Message}).";
                ModLog.Error("Arcade gear: " + message);
                return false;
            }
            ModLog.Info($"Arcade gear: moved the unreadable {FileName} to {FileName}.bad.");
            root = ArcadeGearRules.NewRoot();
            readProblem = null;
        }
        change(root);
        try
        {
            BattleDraft.WriteAtomic(path, ArcadeGearRules.Text(root));
            ModLog.Info($"Arcade gear: save slot {saveSlot}: {what}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            message = $"Your choice couldn't be saved ({ex.Message}); it's used until you leave the arcade.";
            ModLog.Error($"Arcade gear: save slot {saveSlot}: {what}, but {FileName} couldn't be written: {ex.Message}");
        }
        return true;
    }

    // ---- per frame ---------------------------------------------------------------------------------

    /// <summary>Called every frame: the Gear control, then the page.</summary>
    internal static void Update()
    {
        try { UpdateControl(); }
        catch (Exception ex) { Report("the Gear control", ex); }
        if (ui == null) return;
        try { UpdatePage(); }
        catch (Exception ex)
        {
            Report("the page", ex);
            ClosePage("it failed");
        }
    }

    /// <summary>
    /// Works out the label again from the save in memory. Not during a battle, when a battle's
    /// inventory may be in: the label then keeps what it said.
    /// </summary>
    private static void Refresh()
    {
        lastAllItems = SettingsState.ArcadeGearAllItems;
        if (!session)
        {
            Label = default;
            return;
        }
        // Without the save or the items, choices can't be checked: they show as not used ("Gear: story*").
        var unknown = new GearLabel(false, Choices.Count > 0 || IgnoredEntry, false);
        try
        {
            var own = BattleGear.OwnPlayerData(out _);
            Label = own == null || GearCatalog.All.Count == 0 ? unknown : ArcadeGearRules.Label(Resolve(own), IgnoredEntry);
        }
        catch (Exception ex)
        {
            Label = unknown;
            Report("working out the arcade gear", ex);
        }
    }

    private static void Report(string what, Exception ex)
    {
        if (Reported.Add(what)) ModLog.Error($"Arcade gear: {what} failed: {ex}");
    }

    private static void Note(string message)
    {
        if (Reported.Add(message)) ModLog.Error(message);
    }

    // ---- for the QA drivers: reads only --------------------------------------------------------------

    /// <summary>How many of an item the arcade gear used up this visit.</summary>
    internal static int QaUsed(string id) => UsedCount(id);

    /// <summary>This save slot's choices in the file, as "slotKey=value" pairs, whether this visit uses them or not.</summary>
    internal static string QaChoices => string.Join(" ", Entry.Choices.Select(c => $"{GearCatalog.KeyOf(c.Key)}={c.Value}"));

    /// <summary>The Gear control's text as it shows now, without its rich text.</summary>
    internal static string QaLabel => BattleNotice.StripTags(shownText);
}
