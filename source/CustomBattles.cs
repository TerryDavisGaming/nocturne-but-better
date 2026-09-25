using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.AddressableAssets;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>
/// Custom battles in the game. Each package in the CustomBattles folder becomes a runtime SongData
/// (its six-difficulty chart, a score key of its own, no Wwise music), an EnemyData copied from
/// a game enemy with the package's stats and info boxes, and an arcade entry. They are built when
/// an arcade screen opens and kept for the session; a package that changes on disk is rebuilt
/// the next time. They only exist in the arcade: nothing puts them anywhere else.
/// </summary>
internal static class CustomBattles
{
    /// <summary>Names of the mod's runtime SongData and EnemyData start with this.</summary>
    internal const string RuntimePrefix = "NocturneButBetter/";
    private const string EnemyKeyPrefix = "NocturneButBetter/enemy/";
    /// <summary>Custom art is shown on this enemy's art prefab (Mantis_Art: one renderer and an animator).</summary>
    private const string RigEnemy = "EnemyData_Mantis";
    private const string RigGuid = "7641ac360d94a4c719df1f7933383fb8";

    /// <summary>What a custom-art enemy looks like, settled on its battle's first fight.</summary>
    internal enum ArtLook { Undecided, Rig, Placeholder }

    /// <summary>One custom battle and the game objects made for it.</summary>
    internal sealed class Battle
    {
        internal BattlePackage Package = null!;
        internal SongData Data = null!;
        internal EnemyData Enemy = null!;
        internal TextAsset Beatmap = null!;
        internal ArcadeSongInfo Info = null!;
        internal Sprite Card = null!;
        internal Texture2D? CardTexture;   // null for the shared placeholder card
        internal CustomMusic.Source Music = null!;
        // Custom art: the Mantis rig's art reference, the enemy's own (the game releases whatever
        // reference the enemy holds when a fight ends, so a shared one would be released twice).
        internal AssetReferenceGameObject? RigArt;
        internal bool RigDissolvesChildren;
        internal ArtLook Look;
        internal string? LookReason;

        internal string Title => Package.Title;
        internal int Lanes => Package.Lanes;
        internal int LastNoteRow => Package.LastNoteRow;
        internal string PlayableText => Package.PlayableText;
        internal ChartText Chart => Package.Chart;
    }

    private static readonly Dictionary<string, Battle> ById = new();
    private static readonly Dictionary<IntPtr, Battle> ByData = new();
    private static List<Battle> ordered = new();
    private static readonly HashSet<string> Reported = new();
    private static bool reportedFields, reportedBattleError, listed;

    /// <summary>The songs, by title.</summary>
    internal static IReadOnlyList<Battle> All => ordered;

    /// <summary>Changes whenever the list of songs or any song's objects change.</summary>
    internal static int Version { get; private set; }

    /// <summary>Whether custom battles' ends are kept from counting towards achievements (test play needs it).</summary>
    internal static bool EndGuarded { get; private set; }

    // A test play's battle, built from the chart editor's unsaved chart. It isn't in the arcade:
    // only Find knows it. Kept until the next test or until the editor closes (or, when the
    // editor closes during a test, until that test ends).
    private static Battle? test;

    internal static string Folder
    {
        get
        {
            string path = Path.Combine(Application.persistentDataPath, "NocturneButBetter", "CustomBattles");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        // Everything is looked up first, so a missing method installs nothing.
        var showColumns = AccessTools.DeclaredMethod(typeof(CombatNoteFieldView), "ShowColumns")
            ?? throw new MissingMethodException(typeof(CombatNoteFieldView).FullName, "ShowColumns");
        var removeColumn = AccessTools.DeclaredMethod(typeof(CombatNoteFieldView), "RemoveColumn")
            ?? throw new MissingMethodException(typeof(CombatNoteFieldView).FullName, "RemoveColumn");
        var combatEnded = AccessTools.DeclaredMethod(typeof(AchievementManager), "CombatEnded")
            ?? throw new MissingMethodException(typeof(AchievementManager).FullName, "CombatEnded");
        var arcade = CustomBattlesArcade.Methods();
        harmony.Patch(showColumns, postfix: new HarmonyMethod(typeof(CustomBattles), nameof(ShowColumnsPostfix)));
        harmony.Patch(removeColumn, prefix: new HarmonyMethod(typeof(CustomBattles), nameof(RemoveColumnPrefix)));
        harmony.Patch(combatEnded, prefix: new HarmonyMethod(typeof(CustomBattles), nameof(CombatEndedPrefix)),
            postfix: new HarmonyMethod(typeof(CustomBattles), nameof(CombatEndedPostfix)));
        EndGuarded = true;
        CustomBattlesArcade.Install(harmony, arcade);
    }

    // ---- the registry ----------------------------------------------------------------------------

    /// <summary>The custom battle a SongData was made for, or null for the game's own songs.</summary>
    internal static Battle? Find(SongData? data)
    {
        if (data == null || !data) return null;
        var t = test;
        if (t != null && t.Data && t.Data.Pointer == data.Pointer) return t;
        return ByData.TryGetValue(data.Pointer, out var song) ? song : null;
    }

    /// <summary>The custom battle (arcade or test) whose enemy this is, or null for the game's own enemies.</summary>
    internal static Battle? FindEnemy(EnemyData? enemy)
    {
        if (enemy == null || !enemy || !IsRuntimeName(enemy.name)) return null;
        var t = test;
        if (t != null && t.Enemy && t.Enemy.Pointer == enemy.Pointer) return t;
        foreach (var song in ordered)
            if (song.Enemy && song.Enemy.Pointer == enemy.Pointer) return song;
        return null;
    }

    /// <summary>
    /// Builds a test play's battle from a package loaded with the editor's chart, as the arcade's
    /// are built, and keeps it until the next test. <paramref name="music"/> (the editor's decoded
    /// song) replaces the package's file when given. Throws with the reason when it can't be built.
    /// </summary>
    internal static Battle BuildTest(BattlePackage package, CustomMusic.Source? music)
    {
        DropTest();
        var built = Build(package, GameEnemies(), rethrow: true)
            ?? throw new InvalidOperationException("the battle couldn't be built");
        if (music != null) built.Music = music;
        test = built;
        EnemyArt.Warm(built, "the chart editor's test");
        return built;
    }

    /// <summary>
    /// Destroys the last test's battle, unless it's still being played. While a test is starting
    /// or running it's kept (the game's start is about to use it); the test drops it when it ends.
    /// </summary>
    internal static void DropTest()
    {
        var t = test;
        if (t == null || TestPlay.Active) return;
        test = null;
        Retire(t);
    }

    /// <summary>Whether a SongData or EnemyData name is one of the mod's runtime objects.</summary>
    internal static bool IsRuntimeName(string? name) => name != null && name.StartsWith(RuntimePrefix, StringComparison.Ordinal);

    /// <summary>A custom battle's title, for screens that would show its SongData's name.</summary>
    internal static string? TitleOf(SongData? data) => Find(data)?.Title;

    /// <summary>The title of the custom battle whose SongData has this name, or null.</summary>
    internal static string? TitleOf(string? name)
    {
        if (name == null || !name.StartsWith(BattlePackage.ScoreKeyPrefix, StringComparison.Ordinal)) return null;
        return ById.TryGetValue(name.Substring(BattlePackage.ScoreKeyPrefix.Length), out var song) ? song.Title : null;
    }

    /// <summary>
    /// Reads the CustomBattles folder again and builds the songs that are new or changed. It runs
    /// every time an arcade screen is shown, so a battle whose files haven't changed isn't read again.
    /// </summary>
    internal static void Refresh()
    {
        List<BattlePackage> found;
        var known = new Dictionary<string, BattlePackage>(StringComparer.Ordinal);
        foreach (var built in ById.Values) known[built.Package.Location] = built.Package;
        try { found = BattlePackage.Scan(Folder, message => Note(message), known); }
        catch (Exception ex)
        {
            Note("Listing the custom battles failed: " + ex.Message);
            return;
        }
        bool changed = false;
        var seen = new HashSet<string>();
        Dictionary<string, EnemyData>? enemies = null;
        foreach (var package in found)
        {
            seen.Add(package.Id);
            ById.TryGetValue(package.Id, out var have);
            if (have != null && have.Package.Fingerprint == package.Fingerprint && Alive(have)) continue;
            enemies ??= GameEnemies();
            var built = Build(package, enemies);
            if (have != null) Retire(have);
            if (built != null) ById[package.Id] = built;
            else ById.Remove(package.Id);
            changed = true;
        }
        foreach (var id in ById.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            Retire(ById[id]);
            ById.Remove(id);
            changed = true;
        }
        if (!changed && listed) return;
        listed = true;
        ordered = ById.Values.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Package.Id).ToList();
        ByData.Clear();
        foreach (var song in ordered) ByData[song.Data.Pointer] = song;
        Version++;
        ModLog.Info($"Custom battles: {ordered.Count} in {Folder}.");
    }

    private static bool Alive(Battle song) => song.Data && song.Enemy && song.Beatmap && song.Card;

    /// <summary>The game's enemies by asset name, without the mod's copies.</summary>
    private static Dictionary<string, EnemyData> GameEnemies()
    {
        var map = new Dictionary<string, EnemyData>(StringComparer.OrdinalIgnoreCase);
        foreach (var enemy in Resources.FindObjectsOfTypeAll<EnemyData>())
            if (enemy && !IsRuntimeName(enemy.name) && !map.ContainsKey(enemy.name)) map[enemy.name] = enemy;
        return map;
    }

    // A replaced song's objects are destroyed, unless its battle is still going on.
    private static void Retire(Battle song)
    {
        if (ChartSwap.CurrentBattle == song) return;
        EnemyArt.Drop(song);
        foreach (Object? obj in new Object?[] { song.Beatmap, song.Data, song.Enemy, song.CardTexture != null ? song.Card : null, song.CardTexture })
            if (obj != null && obj) Object.Destroy(obj);
    }

    // ---- building ----------------------------------------------------------------------------------

    /// <param name="rethrow">Throw what went wrong instead of noting it and returning null.</param>
    private static Battle? Build(BattlePackage package, Dictionary<string, EnemyData> enemies, bool rethrow = false)
    {
        var made = new List<Object>();
        try
        {
            foreach (var problem in package.Problems) Note($"Custom battle {package.Title}: {problem}.");
            CheckChart(package);
            var enemy = BuildEnemy(package, enemies, made);
            var beatmap = new TextAsset(package.PlayableText) { name = package.ScoreKey, hideFlags = HideFlags.DontUnloadUnusedAsset };
            made.Add(beatmap);
            var data = BuildSongData(package, beatmap, enemy, made);
            enemy.songData = data;
            var (card, texture) = LoadCard(package, made);
            var song = new Battle
            {
                Package = package,
                Data = data,
                Enemy = enemy,
                Beatmap = beatmap,
                Card = card,
                CardTexture = texture,
                Info = BuildInfo(package, data, card),
                Music = MusicSource(package)
            };
            if (package.Art != null) SetUpRig(song, enemies);
            string difficulties = string.Join(", ", Enumerable.Range(0, package.Slots.Length)
                .Where(s => package.Slots[s] != null).Select(s => ChartText.GameDifficultyLabels[s]));
            string art = package.Art != null ? $"; custom art: {package.Art.Summary()}"
                : package.CustomArt ? $"; its custom art can't be used, so it looks like {package.EnemyPlaceholder}" : "";
            ModLog.Info($"Custom battle {package.DisplayName}: {package.Lanes} lanes, {difficulties}; enemy {enemy.name} from {package.EnemyPlaceholder}{art}.");
            return song;
        }
        catch (Exception ex)
        {
            foreach (var obj in made)
                if (obj) Object.Destroy(obj);
            if (rethrow) throw;
            bool expected = ex is InvalidDataException or IOException;
            Note($"Skipping custom battle {package.Title} ({package.Location}): {(expected ? ex.Message : ex.ToString())}");
            return null;
        }
    }

    // The game's own reader must take the chart, or the battle couldn't start.
    private static void CheckChart(BattlePackage package)
    {
        var built = NotesLoaderSM.Instance.LoadFromText(package.PlayableText);
        if (built == null || built.steps == null || built.steps.Count == 0 || built.timingData == null)
            throw new InvalidDataException("the game's chart reader found nothing playable in it");
        if (built.steps.Count != ChartText.GameDifficultyNames.Length)
            Note($"Custom battle {package.Title}: the game read {built.steps.Count} of its 6 difficulties.", error: false);
    }

    private static SongData BuildSongData(BattlePackage package, TextAsset beatmap, EnemyData enemy, List<Object> made)
    {
        SongData? data = null;
        try { data = ScriptableObject.CreateInstance(Il2CppType.Of<SongData>())?.TryCast<SongData>(); }
        catch (Exception ex) { Note("Custom battles: making a SongData failed, so a game song is copied instead: " + ex.Message); }
        bool created = data != null;
        data ??= CopyGameSong();
        made.Add(data);
        ReportFields(data, created);
        Scrub(data, force: !created);

        data.name = package.ScoreKey;
        data.songName = package.Title;
        // No localization term: the game shows songName.
        data.displayName = new LocalizedString();
        // Scores and unlocked melodies use HighScoreKey and the object's name; both are the key.
        data.overrideHighScoreKey = true;
        data.highScoreKey = package.ScoreKey;
        data.overridePlayEventInArcade = false;
        data.beatmaps = new Il2CppReferenceArray<TextAsset>(new[] { beatmap });
        // One melody: no melody buttons, no mid-song melody switch, no Wwise melody index.
        data.RandomizeSequence = false;
        data.setMelodyIndex = false;
        data.SongSplitMeasure = 0;
        data.overridePlayerStats = false;
        data.overrideColumnSpeeds = false;
        data.overrideMaxBpm = false;
        data.hideStaggerBonus = false;
        var enemies = new Il2CppSystem.Collections.Generic.List<EnemyData>();
        enemies.Add(enemy);
        data.Enemies = enemies;
        data.Items = new Il2CppReferenceArray<ItemData>(0);
        data.onInitializeCombat = AudioHook.CreateEmptyHook();
        data.onSongStart = AudioHook.CreateEmptyHook();
        data.tapNoteTypeRemapper = new Il2CppSystem.Collections.Generic.List<TapNoteTypeRemapper>();
        data.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return data;
    }

    // The fallback when a SongData can't be created: a copy of a game song, scrubbed of its music.
    private static SongData CopyGameSong()
    {
        foreach (var song in Resources.FindObjectsOfTypeAll<SongData>())
        {
            if (!song || IsRuntimeName(song.name)) continue;
            var copy = Object.Instantiate((Object)song)?.TryCast<SongData>();
            if (copy != null) return copy;
        }
        throw new InvalidOperationException("the game couldn't make or copy a song");
    }

    // The battle reads these without checking for null. SongData's constructor makes them all
    // (empty Wwise objects, no cues); a copied game song gets new empty ones, so none of its
    // music, cues or spawned objects come along.
    private static void Scrub(SongData data, bool force)
    {
        if (force || data.playEvent == null) data.playEvent = new WwiseEvent();
        if (force || data.arcadePlayEvent == null) data.arcadePlayEvent = new WwiseEvent();
        if (force || data.SongSwitch == null) data.SongSwitch = new WwiseSwitch();
        if (force || data.StepSwitch == null) data.StepSwitch = new WwiseSwitch();
        if (force || data.soundBank == null) data.soundBank = new WwiseBank();
        if (force || data.Cues == null) data.Cues = new Il2CppSystem.Collections.Generic.List<SongSectionMusicCue>();
        if (force || data.combatPrefabs == null) data.combatPrefabs = new Il2CppSystem.Collections.Generic.List<CombatSpawnPrefab>();
        if (data.statSheetData == null) data.statSheetData = new CharacterStatSheetData();
    }

    private static void ReportFields(SongData data, bool created)
    {
        if (reportedFields) return;
        reportedFields = true;
        static string Has(object? value) => value != null ? "set" : "missing";
        var cues = data.Cues;
        ModLog.Info($"Custom battles: a SongData from {(created ? "CreateInstance" : "a copied game song")} has playEvent {Has(data.playEvent)}, " +
            $"StepSwitch {Has(data.StepSwitch)}, soundBank {Has(data.soundBank)}, Cues {(cues == null ? "missing" : cues.Count + " cues")}, " +
            $"onInitializeCombat {Has(data.onInitializeCombat)}, onSongStart {Has(data.onSongStart)}.");
    }

    private static EnemyData BuildEnemy(BattlePackage package, Dictionary<string, EnemyData> enemies, List<Object> made)
    {
        var definition = package.Enemy;
        if (!enemies.TryGetValue(package.EnemyPlaceholder, out var template))
        {
            Note($"Custom battle {package.Title}: the game has no enemy {package.EnemyPlaceholder}; {EnemyPlaceholders.Default} stands in.");
            if (!enemies.TryGetValue(EnemyPlaceholders.Default, out template))
                throw new InvalidOperationException("the game's enemies aren't loaded");
        }
        var clone = Object.Instantiate((Object)template)?.TryCast<EnemyData>()
            ?? throw new InvalidOperationException("the game couldn't copy " + template.name);
        made.Add(clone);

        // The copy keeps the placeholder's art, sounds and abilities, under an id of its own.
        string id = EnemyKeyPrefix + package.Id;
        clone.name = id;
        clone.characterId = id;
        clone.boss = false;
        clone.experienceAward = 0;
        clone.useSpecialAttack = false;
        clone.energyBarSegments = 1;
        clone.lootTable = new LootTable { entries = new Il2CppSystem.Collections.Generic.List<LootTableEntry>() };
        // The enemy's own sound hooks could start music over the song.
        clone.combatInitialized = AudioHook.CreateEmptyHook();
        clone.combatSongStart = AudioHook.CreateEmptyHook();
        clone.statSheetData = Stats(template.statSheetData, definition.stats);
        // The loader leaves out numbers that aren't finite; Math.Clamp would pass a NaN on.
        if (definition.stats?.energyChargeOnMiss is double miss && double.IsFinite(miss)) clone.energyChargeOnMiss = (float)Math.Clamp(miss, 0, 1000);
        if (definition.stats?.attackWindupTime is double windup && double.IsFinite(windup)) clone.attackWindupTime = (float)Math.Clamp(windup, 0.05, 60);
        if (definition.info != null) clone.enemyInfoEntries = InfoEntries(definition);
        if (package.Lanes == 5) FitFiveLanes(package, clone);
        clone.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return clone;
    }

    // Custom art is shown on the Mantis rig. The enemy only switches to it when its art loads for
    // its first fight (EnemyArt), so until then it keeps the placeholder's own art.
    private static void SetUpRig(Battle song, Dictionary<string, EnemyData> enemies)
    {
        try
        {
            string? guid = null;
            if (enemies.TryGetValue(RigEnemy, out var mantis))
            {
                var reference = mantis.addressableArtPrefab;
                guid = reference != null ? reference.AssetGUID : null;
                song.RigDissolvesChildren = mantis.dissolveAffectChildRenderer;
            }
            song.RigArt = new AssetReferenceGameObject(string.IsNullOrEmpty(guid) ? RigGuid : guid);
        }
        catch (Exception ex)
        {
            Note($"Custom battle {song.Title}: the Mantis rig for its custom art couldn't be set up, so it looks like {song.Package.EnemyPlaceholder}: {ex.Message}");
            song.RigArt = null;
        }
    }

    private static EnemyStatSheet Stats(EnemyStatSheet? source, EnemyStats? stats)
    {
        var sheet = new EnemyStatSheet();
        if (source != null)
        {
            sheet.health = source.health;
            sheet.damage = source.damage;
            sheet.dexterity = source.dexterity;
            sheet.passiveEnergyCharge = source.passiveEnergyCharge;
            sheet.regen = source.regen;
            sheet.impact = source.impact;
            sheet.focus = source.focus;
        }
        if (stats?.hp is double hp && double.IsFinite(hp)) sheet.health = (float)Math.Clamp(hp, 1, 100000);
        if (stats?.damage is double damage && double.IsFinite(damage)) sheet.damage = (float)Math.Clamp(damage, 0, 1000);
        if (stats?.passiveEnergyCharge is double charge && double.IsFinite(charge)) sheet.passiveEnergyCharge = (float)Math.Clamp(charge, 0, 10000);
        return sheet;
    }

    // Info boxes with fallback text and no localization term, which the game shows as written.
    private static Il2CppSystem.Collections.Generic.List<EnemyInfoEntry> InfoEntries(EnemyDefinition definition)
    {
        var list = new Il2CppSystem.Collections.Generic.List<EnemyInfoEntry>();
        foreach (var box in definition.info!.Take(EnemyPlaceholders.MaxInfoBoxes))
        {
            if (box == null) continue;
            string title = (box.title ?? "").Trim();
            if (title.Length == 0) title = (definition.name ?? "").Trim();
            var entry = new EnemyInfoEntry();
            entry.infoName = new NocturneString(new LocalizedString(), title);
            entry.infoDescription = new NocturneString(new LocalizedString(), (box.description ?? "").Trim());
            list.Add(entry);
        }
        return list;
    }

    // Effects that pull, remove or cover lanes, or play animations on the note field: they are
    // made for 4 lanes and would undo the 5-lane layout.
    private static readonly HashSet<CombatEffectType> ColumnEffects = new()
    {
        CombatEffectType.ModifyCombatAnimationProperty, CombatEffectType.RemoveCombatColumn,
        CombatEffectType.TriggerRandomVine, CombatEffectType.ResetRandomVineBasedOnCharge
    };

    private static void FitFiveLanes(BattlePackage package, EnemyData clone)
    {
        // Column triggers like "Initialize Vine" move 5 lanes into 4-lane places.
        clone.combatStartColumnAnimationTrigger = "";
        clone.attackColumnAnimationTrigger = "";
        clone.specialAttackCombatEffects = new Il2CppSystem.Collections.Generic.List<CombatEffectData>();
        var effects = clone.combatEffects;
        if (effects == null) return;
        var kept = new Il2CppSystem.Collections.Generic.List<CombatEffectData>();
        var dropped = new List<string>();
        for (int i = 0; i < effects.Count; i++)
        {
            var data = effects[i];
            if (data == null) continue;
            if (ChangesColumns(data)) dropped.Add(data.name);
            else kept.Add(data);
        }
        clone.combatEffects = kept;
        if (dropped.Count > 0) Note($"Custom battle {package.Title}: 5 lanes leave out the enemy's {string.Join(", ", dropped)}.", error: false);
    }

    private static bool ChangesColumns(CombatEffectData data)
    {
        var effects = data.effects;
        if (effects == null) return false;
        for (int i = 0; i < effects.Count; i++)
        {
            var effect = effects[i];
            if (effect != null && ColumnEffects.Contains(effect.effectType)) return true;
        }
        return false;
    }

    private static ArcadeSongInfo BuildInfo(BattlePackage package, SongData data, Sprite card)
    {
        // Every reference is set: the arcade screens and the battle launch read them without checks.
        var info = new ArcadeSongInfo();
        info.songType = ArcadeSongType.Song;
        info.songData = data;
        info.sprite = card;
        info.lore = new NocturneString(new LocalizedString(), LoreText(package));
        info.timelineName = new NocturneString(new LocalizedString(), package.Title);
        info.highScoreKey = package.ScoreKey;
        info.soundBanks = new Il2CppSystem.Collections.Generic.List<WwiseBank>();
        info.preCombatHook = AudioHook.CreateEmptyHook();
        info.postCombatHook = AudioHook.CreateEmptyHook();
        info.showInHighscores = true;
        info.isRetro = false;
        info.skipContactEvent = false;
        return info;
    }

    /// <summary>A battle's lore, or who made its song and charts when it has none.</summary>
    internal static string LoreText(BattlePackage package) => BattleNotice.Credits(package.Lore, package.Artist, package.Author);

    private static CustomMusic.Source MusicSource(BattlePackage package)
    {
        var files = package.Files;
        string name = package.AudioPath;
        return new CustomMusic.Source
        {
            Name = $"{name} for {package.Title}",
            Key = $"{files.Describe(name)}|{files.Stamp(name)}",
            Read = () => files.ReadAllBytes(name, BattlePackage.MaxAudioBytes)
        };
    }

    // ---- card images -------------------------------------------------------------------------------

    private static (Sprite Card, Texture2D? Texture) LoadCard(BattlePackage package, List<Object> made)
    {
        if (package.CardPath != null)
        {
            try
            {
                var bytes = package.Files.ReadAllBytes(package.CardPath, BattlePackage.MaxImageBytes);
                // Cards are kept for the session, so only what the card shows is kept, at the size the arcade needs.
                var card = CardImages.Make(bytes, package.ScoreKey + "/card", package.CardLook, keepPart: true, out string? why);
                if (card != null)
                {
                    made.Add(card.Texture);
                    made.Add(card.Sprite);
                    return (card.Sprite, card.Texture);
                }
                Note($"Custom battle {package.Title}: {package.CardPath} {why}, so its card is plain for now.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Note($"Custom battle {package.Title}: the card image couldn't be read ({ex.Message}).");
            }
        }
        return (CardImages.Placeholder, null);
    }

    /// <summary>
    /// Card images as textures and sprites; the battle creator shows its card preview with them too.
    /// The arcade card's picture slot is square and the game fits a sprite whole into it, so what
    /// the card shows (the square it fills, or the whole picture), its size and its filter are all
    /// in the sprite and its texture (<see cref="CardLayout"/>).
    /// </summary>
    internal static class CardImages
    {
        // ImageConversion.LoadImage was stripped from the game's managed code, but Unity still
        // registers its native call, which is called directly here.
        private delegate byte LoadImageCall(IntPtr texture, IntPtr data, byte markNonReadable);
        private static LoadImageCall? loadImage;
        private static bool looked;
        private static Sprite? placeholder;

        /// <summary>The longest side an arcade card is kept at; a bigger image is scaled down to it (see <see cref="Make"/>).</summary>
        internal const int MaxCardSide = CardLayout.KeepSide;
        /// <summary>
        /// A card image bigger than this on a side isn't decoded at all (8192 x 8192 is 256 MB while
        /// decoding); a 50 MP phone photo (8160 x 6120) still is.
        /// </summary>
        internal const int MaxDecodeSide = 8192;
        private static bool reportedScaling;

        /// <summary>
        /// A card image as a texture, or null with why it can't be shown (<paramref name="why"/>,
        /// written to follow the file's name). The texture keeps no copy in system memory (nothing
        /// reads a card's pixels back), and an image whose header says it is over
        /// <see cref="MaxDecodeSide"/> on a side is refused before it's decoded.
        /// </summary>
        /// <param name="keepSide">
        /// When above 0, a bigger image is scaled down so neither side is over it; 0 keeps the image's
        /// own size (the battle creator's preview, which shows it).
        /// </param>
        /// <param name="scale">The texture's size over the image's: 1 unless it was scaled down.</param>
        internal static Texture2D? Decode(byte[] bytes, string name, int keepSide, out float scale, out string? why)
        {
            scale = 1f;
            var texture = Load(bytes, name, MaxDecodeSide, "card images", readable: false, out why);
            if (texture == null || keepSide <= 0 || (texture.width <= keepSide && texture.height <= keepSide)) return texture;
            float factor = keepSide / (float)Math.Max(texture.width, texture.height);
            var smaller = Resampled(texture, Math.Clamp((int)Math.Round(texture.width * factor), 1, keepSide),
                Math.Clamp((int)Math.Round(texture.height * factor), 1, keepSide), null);
            if (smaller != null)
            {
                scale = smaller.width / (float)texture.width;
                Object.Destroy(texture);
                texture = smaller;
            }
            return texture;
        }

        /// <summary>A card picture made for the arcade's square slot, and what was worked out for it.</summary>
        internal sealed class Card
        {
            internal Texture2D Texture = null!;
            internal Sprite Sprite = null!;
            /// <summary>The picture file's own size.</summary>
            internal int FileWidth, FileHeight;
            /// <summary>What was kept and shown (<see cref="CardLayout.Work"/>); its own size when making it smaller failed.</summary>
            internal CardLayout.Plan Plan;
        }

        /// <summary>
        /// A card picture as the arcade shows it, or null with why it can't be shown
        /// (<paramref name="why"/>, written to follow the file's name): at most
        /// <see cref="CardLayout.KeepSide"/> across the slot, showing the square it fills (or the
        /// whole picture), crisp or smooth, as <paramref name="look"/> says. The texture keeps no copy
        /// in system memory.
        /// </summary>
        /// <param name="keepPart">
        /// Keep only the part the card shows when the picture is big (the arcade, which keeps its
        /// cards for the session). Otherwise the texture is the whole picture at the kept size and
        /// the sprite shows the part (the battle creator's preview, whose square can move:
        /// <see cref="Reframe"/>). The pixels shown are the same either way.
        /// </param>
        internal static Card? Make(byte[] bytes, string name, CardLayout.Look look, bool keepPart, out string? why)
        {
            var texture = Load(bytes, name, MaxDecodeSide, "card images", readable: false, out why);
            if (texture == null) return null;
            int fileWidth = texture.width, fileHeight = texture.height;
            var plan = CardLayout.Work(fileWidth, fileHeight, look);
            var part = new Rect(plan.X, plan.Y, plan.PartWidth, plan.PartHeight);
            // A small picture is kept whole (its sprite shows the part); a long one, like 500 x 8000, only its part.
            if (plan.Smaller || (keepPart && plan.Cropped && Math.Max(plan.Width, plan.Height) > CardLayout.KeepSide))
            {
                var made = Resampled(texture, plan.Width, plan.Height, keepPart ? part : null);
                if (made != null)
                {
                    Object.Destroy(texture);
                    texture = made;
                    if (keepPart) part = new Rect(0f, 0f, made.width, made.height);
                }
                else
                {
                    // Kept at its own size (see Resampled); the sprite still shows only the card's part.
                    plan = CardLayout.Work(fileWidth, fileHeight, look, int.MaxValue);
                    part = new Rect(plan.X, plan.Y, plan.PartWidth, plan.PartHeight);
                }
            }
            texture.filterMode = plan.Crisp ? FilterMode.Point : FilterMode.Bilinear;
            var sprite = ToSprite(texture, part, texture.width / (float)fileWidth);
            return new Card { Texture = texture, Sprite = sprite, FileWidth = fileWidth, FileHeight = fileHeight, Plan = plan };
        }

        /// <summary>
        /// Shows another part of a card made with keepPart false (the battle creator's preview while
        /// its square moves, or its crispness changes): a new sprite, and the old one destroyed. The
        /// look must fill or not as it did when the card was made (the kept size depends on it).
        /// </summary>
        internal static void Reframe(Card card, CardLayout.Look look)
        {
            var plan = CardLayout.Work(card.FileWidth, card.FileHeight, look, card.Plan.Smaller ? CardLayout.KeepSide : int.MaxValue);
            var part = new Rect(plan.X, plan.Y, plan.PartWidth, plan.PartHeight);
            card.Plan = plan;
            var filter = plan.Crisp ? FilterMode.Point : FilterMode.Bilinear;
            if (card.Texture.filterMode != filter) card.Texture.filterMode = filter;
            if (card.Sprite && card.Sprite.rect == part) return;
            var old = card.Sprite;
            card.Sprite = ToSprite(card.Texture, part, card.Texture.width / (float)card.FileWidth);
            if (old) Object.Destroy(old);
        }

        /// <summary>
        /// A PNG or JPEG as a texture whose pixels can be read back (custom enemy art reads them for
        /// its feet, see-through colour and atlases), at its own size; the caller destroys it once
        /// it has them. Null with why (<paramref name="why"/>, written to follow the file's name).
        /// The decoder makes whatever size the file's header claims, so a header over
        /// <paramref name="maxSide"/> on a side is refused first; <paramref name="what"/> names the
        /// pictures in that limit.
        /// </summary>
        internal static Texture2D? DecodeReadable(byte[] bytes, string name, int maxSide, string what, out string? why) =>
            Load(bytes, name, maxSide, what, readable: true, out why);

        private static Texture2D? Load(byte[] bytes, string name, int maxSide, string what, bool readable, out string? why)
        {
            if (!looked)
            {
                looked = true;
                IntPtr call = IL2CPP.il2cpp_resolve_icall("UnityEngine.ImageConversion::LoadImage");
                if (call != IntPtr.Zero) loadImage = Marshal.GetDelegateForFunctionPointer<LoadImageCall>(call);
                else ModLog.Info("Custom battles: the game has no image decoder, so cards are plain.");
            }
            why = loadImage == null ? "can't be shown: the game has no image decoder" : bytes.Length == 0 ? "is empty" : MediaSniff.PictureProblem(bytes, maxSide, what);
            if (why != null) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = name, hideFlags = HideFlags.HideAndDontSave };
            var data = new Il2CppStructArray<byte>(bytes);
            // Not readable unless asked: the decoded pixels go to the graphics card and the copy in memory is freed.
            bool loaded = loadImage!(texture.Pointer, data.Pointer, readable ? (byte)0 : (byte)1) != 0;
            GC.KeepAlive(data);
            if (!loaded || texture.width < 1 || texture.height < 1)
            {
                Object.Destroy(texture);
                why = "couldn't be read by the game's PNG and JPEG decoder";
                return null;
            }
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            return texture;
        }

        // Scales a texture to width x height on the graphics card (halving it in steps first, so no
        // pixels are skipped) and reads the result, or only its part, into a new texture. Null (and
        // the full-size texture is used) if that fails.
        private static Texture2D? Resampled(Texture2D source, int width, int height, Rect? part)
        {
            var read = part ?? new Rect(0f, 0f, width, height);
            int readWidth = Math.Max(1, (int)read.width), readHeight = Math.Max(1, (int)read.height);
            RenderTexture? previous = null, current = null;
            Texture2D? result = null;
            try
            {
                previous = RenderTexture.active;
                Texture from = source;
                int w = source.width, h = source.height;
                while (w / 2 >= width && h / 2 >= height)
                {
                    w /= 2;
                    h /= 2;
                    current = Blit(from, w, h, current);
                    from = current;
                }
                current = Blit(from, width, height, current);
                RenderTexture.active = current;
                result = new Texture2D(readWidth, readHeight, TextureFormat.RGBA32, false) { name = source.name, hideFlags = HideFlags.HideAndDontSave };
                // Counted from the bottom left, like the texture's own pixels.
                result.ReadPixels(new Rect(read.x, read.y, readWidth, readHeight), 0, 0, false);
                // A copy that came out empty (nothing drawn) would leave the card blank; the full-size image is better.
                if (Blank(result)) throw new InvalidOperationException("the scaled image came out empty");
                // Uploaded, and the copy in memory freed.
                result.Apply(false, true);
                result.wrapMode = TextureWrapMode.Clamp;
                result.filterMode = FilterMode.Bilinear;
                return result;
            }
            catch (Exception ex)
            {
                if (result != null && result) Object.Destroy(result);
                if (!reportedScaling)
                {
                    reportedScaling = true;
                    ModLog.Error("Custom battles: scaling a card image down failed, so big cards keep their full size: " + ex);
                }
                return null;
            }
            finally
            {
                try
                {
                    RenderTexture.active = previous;
                    if (current != null) RenderTexture.ReleaseTemporary(current);
                }
                catch { }
            }
        }

        // Whether 64 pixels across the image are all fully transparent black.
        private static bool Blank(Texture2D texture)
        {
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    var c = texture.GetPixel((2 * x + 1) * texture.width / 16, (2 * y + 1) * texture.height / 16);
                    if (c.a > 0f || c.r > 0f || c.g > 0f || c.b > 0f) return false;
                }
            return true;
        }

        // One step: from into a new temporary render texture of the given size; the last step's is released.
        private static RenderTexture Blit(Texture from, int width, int height, RenderTexture? last)
        {
            var next = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            next.filterMode = FilterMode.Bilinear;
            Graphics.Blit(from, next);
            if (last != null) RenderTexture.ReleaseTemporary(last);
            return next;
        }

        /// <param name="scale">The texture's size over the image's, so a scaled-down card keeps the size the image would have.</param>
        internal static Sprite ToSprite(Texture2D texture, float scale = 1f) => ToSprite(texture, new Rect(0f, 0f, texture.width, texture.height), scale);

        /// <summary>A sprite of a part of a texture: the arcade fits that part, and only it, into the card's slot.</summary>
        internal static Sprite ToSprite(Texture2D texture, Rect part, float scale)
        {
            var sprite = Sprite.Create(texture, part, new Vector2(0.5f, 0.5f),
                100f * scale, 0u, SpriteMeshType.FullRect, Vector4.zero);
            sprite.name = texture.name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        /// <summary>A plain dark card for songs without a readable image; made once.</summary>
        internal static Sprite Placeholder
        {
            get
            {
                if (placeholder != null && placeholder) return placeholder;
                const int size = 16;
                var pixels = new Color32[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        bool edge = x == 0 || y == 0 || x == size - 1 || y == size - 1;
                        pixels[y * size + x] = edge ? new Color32(96, 96, 110, 255) : new Color32(52, 52, 62, 255);
                    }
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    name = RuntimePrefix + "card",
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    // Drawn 76 menu units wide: crisp keeps its edge a line, like the game's own pixel-art cards.
                    filterMode = FilterMode.Point
                };
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                placeholder = ToSprite(texture);
                return placeholder;
            }
        }
    }

    // ---- battles -------------------------------------------------------------------------------------

    // Arcade battles show the note field's 4-lane layout, which leaves the fifth lane off to the
    // side. The game's own 5-lane fights ask for the "Five" layout from their timelines; a 5-lane
    // battle that asks for no layout gets the same centred one here.
    private static void ShowColumnsPostfix(CombatNoteFieldView __instance, CombatOptions combatOptions)
    {
        try
        {
            if (!__instance || combatOptions == null) return;
            var settings = combatOptions.columnAnimatorSettings;
            if (settings != null && settings.Count > 0) return;
            // 0, 1 and 2 ask for the None, Single and Double layouts; anything else for the default one.
            int layout = combatOptions.ShowColumns;
            if (layout >= 0 && layout <= 2) return;
            if (FieldColumns(__instance) != 5) return;
            var animator = __instance.fieldAnimator;
            if (!animator) return;
            animator.ResetTrigger("Default");
            animator.ResetTrigger("Mobile");
            animator.SetTrigger("Five Instant");
            ModLog.Info("Five-lane battle: the lanes take the centred five-lane layout.");
        }
        catch (Exception ex) { ReportBattle(ex); }
    }

    private static int FieldColumns(CombatNoteFieldView view)
    {
        var song = ChartSwap.CurrentBattle;
        if (song != null) return song.Lanes;
        var field = view.noteFieldBehaviour;
        return field ? field.ActiveColumnCount : 0;
    }

    // A placeholder enemy's column effects are made for 4 lanes; removing a column past the note
    // field's removable ones would throw in the middle of the battle.
    private static bool RemoveColumnPrefix(CombatNoteFieldView __instance, int index)
    {
        try
        {
            var countdown = __instance.columnDisableCountdown;
            if (countdown == null || (index >= 0 && index < countdown.Length)) return true;
            Note($"Skipped removing lane {index + 1}: this note field has {countdown.Length} lanes that can be removed.");
            return false;
        }
        catch (Exception ex)
        {
            ReportBattle(ex);
            return true;
        }
    }

    // A custom battle is anyone's chart, so its battles don't count towards the game's (Steam)
    // achievements, which can't be taken back. The game checks them all when a battle ends. Nor
    // does a test play from the chart editor, whatever song it is. A custom difficulty's battle
    // is checked as usual, but with the song's own score key: its trophy ranks read every arcade
    // song's scores through that key, and the custom difficulty's score isn't the song's.
    private static bool CombatEndedPrefix(CombatSummary summary)
    {
        try
        {
            bool testing = TestPlay.Active;
            if (ChartSwap.PlayingBattle == null && !IsRuntimeName(summary?.EnemyId) && !testing)
            {
                if (ChartSwap.SuspendScoreKey()) Note("Custom difficulty: the achievement check reads the song's own scores, not this chart's.", error: false);
                return true;
            }
            Note(testing ? "Test play: test battles don't count towards achievements." : "Custom battles don't count towards achievements.", error: false);
            return false;
        }
        catch (Exception ex)
        {
            ReportBattle(ex);
            return true;
        }
    }

    // The custom difficulty's key goes back for the results screen; ExitCombat puts the song's own back for good.
    private static void CombatEndedPostfix()
    {
        try { ChartSwap.ResumeScoreKey(); }
        catch (Exception ex) { ReportBattle(ex); }
    }

    private static void ReportBattle(Exception ex)
    {
        if (reportedBattleError) return;
        reportedBattleError = true;
        ModLog.Error("Custom battle hooks failed: " + ex);
    }

    /// <summary>Logs a message once a session, so a broken package isn't reported on every arcade visit.</summary>
    private static void Note(string message, bool error = true)
    {
        if (!Reported.Add(message)) return;
        if (error) ModLog.Error(message);
        else ModLog.Info(message);
    }
}
