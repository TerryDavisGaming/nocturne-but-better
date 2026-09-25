namespace NocturneFlatScroll;

/// <summary>A game enemy the battle creator offers as a placeholder, with its own stats (shown when the battle keeps them).</summary>
internal sealed record EnemyChoice(string Asset, string Name, double Hp, double Damage, double PassiveEnergyCharge, double EnergyChargeOnMiss, double AttackWindupTime)
{
    /// <summary>A boss built around scripted fights; it only plays as a placeholder when the enemy says "advanced".</summary>
    internal bool Advanced => EnemyChoices.IsAdvanced(Asset);

    /// <summary>The enemy's own value for one of BattleDraft's stat keys.</summary>
    internal double OwnStat(string key) => key switch
    {
        BattleDraft.Hp => Hp,
        BattleDraft.Damage => Damage,
        BattleDraft.PassiveEnergyCharge => PassiveEnergyCharge,
        BattleDraft.EnergyChargeOnMiss => EnergyChargeOnMiss,
        _ => AttackWindupTime
    };
}

/// <summary>
/// The game's enemies (EnemyData assets) that can stand in for a custom battle's enemy, with
/// plain names. Which ones can be used, and which need "advanced", follows EnemyPlaceholders.
/// The stats are the game's own, read from its data.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class EnemyChoices
{
    internal static readonly EnemyChoice[] All = Usable(new EnemyChoice[]
    {
        new("EnemyData_Ant", "Ant", 58, 1, 90, 5, 0.75),
        new("EnemyData_Antlion", "Antlion", 35, 1, 65, 4, 0.75),
        new("EnemyData_Beetle", "Beetle", 45, 1, 90, 5, 0.75),
        new("EnemyData_Boar", "Boar", 80, 1, 90, 5, 0.75),
        new("EnemyData_CagedWei", "Caged Wei", 300, 2, 120, 6, 0.75),
        new("EnemyData_CorruptCreature1", "Corrupt creature 1", 100, 3, 160, 7, 0.75),
        new("EnemyData_CorruptCreature2", "Corrupt creature 2", 240, 3, 160, 7, 0.75),
        new("EnemyData_CorruptCreature3", "Corrupt creature 3", 270, 3, 160, 7, 0.75),
        new("EnemyData_MantisCorrupted", "Corrupted mantis", 70, 2, 60, 6, 0.75),
        new("EnemyData_ScorpionCorrupted", "Corrupted scorpion", 80, 2, 80, 6, 0.75),
        new("EnemyData_Ellen", "Ellen", 240, 2, 90, 5, 0.75),
        new("EnemyData_EllenEpilogue", "Ellen (epilogue)", 190, 4, 90, 7, 0.75),
        new("EnemyData_Firefly", "Firefly", 12, 1, 55, 3, 0.75),
        new("EnemyData_FireflyRetro", "Firefly (retro)", 12, 1, 55, 100, 0.75),
        new("EnemyData_Fox", "Fox", 60, 1, 55, 3, 0.75),
        new("EnemyData_Gauntlet", "Gauntlet", 80, 1, 65, 4, 0.75),
        new("EnemyData_GauntletArcade", "Gauntlet (arcade)", 60, 1, 65, 4, 0.75),
        new("EnemyData_Gauntlet_1", "Gauntlet mantis 1", 80, 1, 65, 4, 0.75),
        new("EnemyData_Gauntlet_4", "Gauntlet mantis 2", 80, 1, 65, 4, 0.75),
        new("EnemyData_Gauntlet_3", "Gauntlet pillbug", 70, 1, 110, 8, 0.75),
        new("EnemyData_Gauntlet_2", "Gauntlet scorpion", 80, 1, 65, 4, 0.75),
        new("EnemyData_Glaucus", "Glaucus", 170, 1, 60, 5, 0.75),
        new("EnemyData_Guard_Heavy", "Heavy guard", 100, 2, 90, 5, 0.75),
        new("EnemyData_Guard_Light", "Light guard", 120, 2, 90, 5, 0.75),
        new("EnemyData_IslandEnemy1", "Island enemy 1", 160, 3, 110, 6, 0.75),
        new("EnemyData_IslandEnemy2", "Island enemy 2", 160, 3, 110, 6, 0.75),
        new("EnemyData_IslandEnemy3", "Island enemy 3", 160, 3, 110, 6, 0.75),
        new("EnemyData_Kai", "Kai", 140, 1, 90, 5, 0.75),
        new("EnemyData_KaiEpilogue", "Kai (epilogue)", 170, 4, 90, 7, 0.75),
        new("EnemyData_TutorialKimothy", "Kimothy (tutorial)", 8, 1, 54, 20, 0.75),
        new("EnemyData_Kitsune", "Kitsune", 180, 2, 60, 6, 0.75),
        new("EnemyData_Ladybug", "Ladybug", 90, 0, 75, 5, 0.75),
        new("EnemyData_Lizard", "Lizard", 30, 1, 65, 4, 0.75),
        new("EnemyData_Mantis", "Mantis", 10, 1, 55, 3, 0.75),
        new("EnemyData_Mantis_Gauntlet", "Mantis (gauntlet)", 80, 1, 65, 4, 0.75),
        new("EnemyData_Moth", "Moth", 28, 1, 55, 3, 0.75),
        new("EnemyData_Nocturne", "Nocturne", 460, 3, 90, 7, 0.75),
        new("EnemyData_Owl", "Owl", 80, 1, 90, 5, 0.75),
        new("EnemyData_Pillbug", "Pillbug", 22, 1, 55, 3, 0.75),
        new("EnemyData_Pillbug_Gauntlet", "Pillbug (gauntlet)", 70, 1, 65, 4, 0.75),
        new("EnemyData_Pitcher", "Pitcher", 140, 1, 90, 5, 0.75),
        new("EnemyData_PrototypeAnt", "Prototype ant", 75, 1, 300, 6, 0.75),
        new("EnemyData_TutorialSatoru", "Satoru (tutorial)", 150, 1, 55, 6, 0.75),
        new("EnemyData_Scorpion", "Scorpion", 16, 1, 55, 3, 0.75),
        new("EnemyData_Scorpion_Gauntlet", "Scorpion (gauntlet)", 80, 1, 65, 4, 0.75),
        new("EnemyData_Snake", "Snake", 36, 1, 65, 4, 0.75),
        new("EnemyData_Stag", "Stag", 120, 1, 90, 5, 0.75),
        new("EnemyData_Sue", "Sue", 320, 3, 120, 7, 0.75),
        new("EnemyData_Tardigrade", "Tardigrade", 62, 1, 90, 5, 0.75),
        new("EnemyData_TestingCH2", "Test enemy, chapter 2", 30, 1, 65, 4, 0.75),
        new("EnemyData_TestingCH3", "Test enemy, chapter 3", 51, 1, 80, 4, 0.75),
        new("EnemyData_TestingCH4", "Test enemy, chapter 4", 70, 2, 60, 5, 0.75),
        new("EnemyData_TestingCH5", "Test enemy, chapter 5", 104, 2, 90, 5, 0.75),
        new("EnemyData_TestingCH6", "Test enemy, chapter 6", 140, 2, 110, 6, 0.75),
        new("EnemyData_TestingCH7", "Test enemy, chapter 7", 188, 3, 90, 6, 0.75),
        new("EnemyData_TestingCH8", "Test enemy, chapter 8", 240, 3, 120, 6, 0.75),
        new("EnemyData_VMKarma1", "Karma 1", 50, 2, 90, 5, 0.75),
        new("EnemyData_VMKarma2", "Karma 2", 90, 2, 90, 5, 0.75),
        new("EnemyData_VMWei1", "Wei 1", 140, 2, 110, 6, 0.75),
        new("EnemyData_VMWei2", "Wei 2", 60, 2, 110, 6, 0.75),
        new("EnemyData_VMWei3", "Wei 3", 125, 2, 120, 6, 0.75),
        new("EnemyData_WeiEpilogue", "Wei (epilogue)", 210, 4, 90, 7, 0.75),
        new("EnemyData_WingedWei", "Winged Wei", 260, 3, 80, 5, 0.75),
        new("EnemyData_Yako", "Yako", 60, 1, 55, 3, 0.75),
        new("EnemyData_VMYako1", "Yako 1", 88, 2, 60, 6, 0.75),
        new("EnemyData_VMYako2", "Yako 2", 64, 2, 60, 6, 0.75),
        new("EnemyData_YakoEpilogue", "Yako (epilogue)", 150, 4, 90, 7, 0.75),
    });

    // Enemies EnemyPlaceholders refuses outright (no art) are left out of the list.
    private static EnemyChoice[] Usable(EnemyChoice[] choices) =>
        choices.Where(c => !IsBroken(c.Asset)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The choices to list: the plain enemies, and the advanced bosses too when asked for.</summary>
    internal static List<EnemyChoice> List(bool advanced) => All.Where(c => advanced || !c.Advanced).ToList();

    /// <summary>A choice by its asset name (with or without "EnemyData_"), or null.</summary>
    internal static EnemyChoice? Find(string? asset)
    {
        string name = Normalize(asset);
        return All.FirstOrDefault(c => c.Asset.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A placeholder's plain name: the listed one, else its asset name without "EnemyData_".</summary>
    internal static string NameOf(string? asset)
    {
        string name = Normalize(asset);
        return Find(name)?.Name ?? (name.StartsWith("EnemyData_", StringComparison.OrdinalIgnoreCase) ? name.Substring("EnemyData_".Length) : name);
    }

    /// <summary>The asset name the loader uses: "" is Mantis, and "EnemyData_" is added when missing.</summary>
    internal static string Normalize(string? asset)
    {
        string name = (asset ?? "").Trim();
        if (name.Length == 0) return EnemyPlaceholders.Default;
        return name.StartsWith("EnemyData_", StringComparison.OrdinalIgnoreCase) ? name : "EnemyData_" + name;
    }

    /// <summary>Whether the loader only plays this enemy with "advanced": true.</summary>
    internal static bool IsAdvanced(string asset)
    {
        string name = Normalize(asset);
        return !IsBroken(name) && EnemyPlaceholders.Resolve(name, false, new List<string>()) != name;
    }

    /// <summary>Whether the loader never plays this enemy (it has no art).</summary>
    internal static bool IsBroken(string asset)
    {
        string name = Normalize(asset);
        return EnemyPlaceholders.Resolve(name, true, new List<string>()) != name;
    }

    /// <summary>What the loader says about the placeholder, or null when it plays as picked.</summary>
    internal static string? Problem(string? asset, bool advanced)
    {
        var problems = new List<string>();
        EnemyPlaceholders.Resolve(asset, advanced, problems);
        return problems.Count > 0 ? problems[0] : null;
    }
}
