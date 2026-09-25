namespace NocturneFlatScroll;

/// <summary>
/// A custom battle's level ("level" in battle.json). The game keeps no level in the save, only
/// XP; every stat update works the level out from the XP with LevelProgression.GetLevel and turns
/// it into Strength, Regen and Critical. While a set-level battle runs, GetLevel answers with the
/// battle's level instead, so the game's own stat updates (the one in the battle's setup too) use
/// it. Nothing of the player's is written: not the XP, not a save object. When the battle ends,
/// on every way out and in the backstops, the override goes and the player's stats are worked out
/// again from their own XP. Arcade battles never award XP, so a battle's level can't reach the story.
/// </summary>
internal static partial class BattleGear
{
    // The level GetLevel answers with now (already within the game's levels), or null for the player's own.
    private static int? levelOverride;
    // The battle that set it, for the log.
    private static CustomBattles.Battle? levelBattle;

    /// <summary>Whether a battle's level can be set (the GetLevel hook is in).</summary>
    internal static bool LevelAvailable { get; private set; }

    /// <summary>Whether a battle's level is in effect now.</summary>
    internal static bool LevelSet => levelOverride != null;

    // Optional: without it, battles play at the player's own level and set gear still works.
    private static void InstallLevel(HarmonyLib.Harmony harmony)
    {
        try
        {
            harmony.Patch(Method(typeof(LevelProgression), "GetLevel"), postfix: Hook(nameof(GetLevelPostfix)));
            LevelAvailable = true;
        }
        catch (Exception ex) { ModLog.Error("Battle level: LevelProgression.GetLevel could not be patched; battles play at your own level: " + ex); }
    }

    // Every GetLevel call while a set-level battle runs. That is what the stat update caches as the
    // player's level and turns into stats. The value was checked against the game's levels when set.
    private static void GetLevelPostfix(ref int __result)
    {
        if (levelOverride is int level) __result = level;
    }

    /// <summary>The game's highest level (20 in 1.0.1), or null when its level table can't be read.</summary>
    internal static int? GameMaxLevel()
    {
        try
        {
            var progression = DataUtility.GameConfig?.LevelProgression;
            if (progression == null || !progression) return null;
            int max = progression.MaxLevel;
            return max >= LevelDefinition.MinLevel ? max : null;
        }
        catch { return null; }
    }

    /// <summary>The level a battle plays at: its own, kept within the game's levels (1 to 20 when they can't be read); null for the player's own.</summary>
    internal static int? EffectiveLevel(int? wanted) =>
        wanted is int level ? Math.Clamp(level, LevelDefinition.MinLevel, GameMaxLevel() ?? LevelDefinition.MaxLevel) : null;

    /// <summary>The player's level as the game has it now, or null when no player is loaded.</summary>
    internal static int? PlayerLevel()
    {
        try
        {
            // An interface call: reading the level is fine (its getter is shared code that is never patched).
            var player = GameDataManager.PlayerData;
            int level = player != null ? player.PlayerLevel : 0;
            return level >= LevelDefinition.MinLevel ? level : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// What each level adds to the player's stats compared with level 1, from the game's own level
    /// table: (Strength, Regen, Critical), or null when the table can't be read.
    /// </summary>
    internal static (int Strength, int Regen, int Critical)? LevelGains(int level)
    {
        try
        {
            var levels = DataUtility.GameConfig?.LevelProgression?.levels;
            if (levels == null) return null;
            int strength = 0, regen = 0, critical = 0;
            // Entry i is the step up to level i + 2.
            for (int i = 0; i < levels.Count && i < level - 1; i++)
            {
                var step = levels[i];
                if (step == null) continue;
                strength += step.strength;
                regen += step.regen;
                critical += step.critical;
            }
            return (strength, regen, critical);
        }
        catch { return null; }
    }

    /// <summary>Sets the battle's level, if it has one, for its stat updates. False when it plays at the player's own.</summary>
    private static bool SetLevel(CustomBattles.Battle battle)
    {
        int? wanted = battle.Package.Level.Level;
        if (wanted == null) return false;
        if (!LevelAvailable)
        {
            Note($"Battle level: {battle.Title}: setting the level isn't available (a game hook couldn't be installed), so you play at your own level.");
            return false;
        }
        try
        {
            // GetTotalStats has no bounds check: a level past the game's table would break the stat update.
            if (GameMaxLevel() is not int max)
            {
                Note($"Battle level: {battle.Title}: the game's level table can't be read, so you play at your own level.");
                return false;
            }
            int level = Math.Clamp(wanted.Value, LevelDefinition.MinLevel, max);
            if (level != wanted) Note($"Battle level: {battle.Title}: level {wanted} can't be used; it plays at level {level} (the game's levels are 1 to {max}).");
            int? own = PlayerLevel();
            levelOverride = level;
            levelBattle = battle;
            ModLog.Info($"Battle level: {battle.Title}: level {level} for this battle (yours is {(own is int l ? l.ToString() : "?")}).");
            return true;
        }
        catch (Exception ex)
        {
            levelOverride = null;
            levelBattle = null;
            ModLog.Error($"Battle level: {battle.Title}: the level couldn't be set, so you play at your own level: {ex}");
            return false;
        }
    }

    /// <summary>Takes the battle's level away and works the player's stats out again with their own. Safe to call again.</summary>
    private static void ClearLevel(string reason, bool backstop)
    {
        var battle = levelBattle;
        levelOverride = null;
        levelBattle = null;
        // Always: the game caches the level (a later story XP award starts from it), and the stats hold it.
        Recompute();
        string title = battle?.Title ?? "the battle";
        string level = PlayerLevel() is int l ? l.ToString() : "?";
        ModLog.Info(backstop
            ? $"Battle level: backstop: {reason}; your own level is back after {title} (level {level})."
            : $"Battle level: your own level is back after {title} (level {level}, {reason}).");
        if (backstop) Dump("after the backstop");
    }

    /// <summary>Has the game work out the player's stats (and level) again.</summary>
    private static void Recompute()
    {
        try { GameDataManager.PlayerData?.TryCast<PlayerDataManager>()?.UpdateStatModel(); }
        catch (Exception ex) { Note("Battle level: working out your stats again failed: " + ex); }
    }
}
