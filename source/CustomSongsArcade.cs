using System.Reflection;
using HarmonyLib;

namespace NocturneFlatScroll;

/// <summary>
/// Lists the custom songs in the Arcade and High Scores screens as one more chapter, "Custom
/// Songs", after the game's own. Both screens rebuild their chapter tabs and song cards from the
/// ArcadeDatabase each time they open, so the chapter is put into the database's list right
/// before that. It is marked debug-only like the game's own test chapter: the discovery
/// percentage and the trophy ranks skip such chapters, so custom songs change neither, and the
/// screen is told to show it anyway. Its songs are always unlocked, with their one melody.
/// </summary>
internal static class CustomSongsArcade
{
    private const string ChapterName = "Custom Songs";
    private const string ChapterShortName = "Custom";

    private static ArcadeCategory? chapter;
    private static IntPtr chapterPointer;
    // Every chapter object ever made, to find an old one still in a list.
    private static readonly HashSet<IntPtr> Chapters = new();
    private static readonly HashSet<IntPtr> SongInfos = new();
    private static int builtVersion = -1;
    private static bool reportedError;

    internal sealed class Targets
    {
        internal MethodInfo Activate = null!, ShouldShowCategory = null!, IsArcadeSongUnlocked = null!, GetUnlockedMelodyCount = null!,
            PopulateCategoryButton = null!, GetUnlockedMelodiesForSong = null!;
    }

    /// <summary>The methods patched, looked up before anything is installed.</summary>
    internal static Targets Methods() => new()
    {
        Activate = Method(typeof(GenericArcadeMenuV2), "Activate"),
        ShouldShowCategory = Method(typeof(GenericArcadeMenuV2), "ShouldShowCategory"),
        IsArcadeSongUnlocked = Method(typeof(ArcadeUtility), "IsArcadeSongUnlocked"),
        GetUnlockedMelodyCount = Method(typeof(ArcadeUtility), "GetUnlockedMelodyCount"),
        PopulateCategoryButton = Method(typeof(GenericArcadeMenuV2), "PopulateCategoryButton"),
        GetUnlockedMelodiesForSong = Method(typeof(ScoreManager), "GetUnlockedMelodiesForSong")
    };

    private static MethodInfo Method(Type type, string name) =>
        AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);

    internal static void Install(HarmonyLib.Harmony harmony, Targets targets)
    {
        harmony.Patch(targets.Activate, prefix: Hook(nameof(ActivatePrefix)));
        harmony.Patch(targets.ShouldShowCategory, prefix: Hook(nameof(ShouldShowCategoryPrefix)));
        harmony.Patch(targets.IsArcadeSongUnlocked, prefix: Hook(nameof(IsArcadeSongUnlockedPrefix)));
        harmony.Patch(targets.GetUnlockedMelodyCount, prefix: Hook(nameof(GetUnlockedMelodyCountPrefix)));
        harmony.Patch(targets.PopulateCategoryButton, postfix: Hook(nameof(PopulateCategoryButtonPostfix)));
        harmony.Patch(targets.GetUnlockedMelodiesForSong, postfix: Hook(nameof(UnlockedMelodiesPostfix)));
    }

    // The chapter tab buttons are sized for "Ch. 1"; the chapter's name shrinks to one line.
    private static void PopulateCategoryButtonPostfix(CustomButton categoryButton, ArcadeCategory category)
    {
        try
        {
            if (category == null || !Chapters.Contains(category.Pointer) || !categoryButton) return;
            var label = categoryButton.GetComponentInChildren<TMP_Text>(true);
            if (!label) return;
            label!.enableWordWrapping = false;
            label.enableAutoSizing = true;
            label.fontSizeMax = label.fontSize;
            label.fontSizeMin = Math.Max(1f, label.fontSize * 0.5f);
        }
        catch (Exception ex) { Report(ex); }
    }

    // A custom song has one melody. The results screen counts the melodies a score unlocks,
    // which would otherwise announce a second one.
    private static void UnlockedMelodiesPostfix(string highScoreKey, ref int __result)
    {
        if (__result > 1 && highScoreKey != null && highScoreKey.StartsWith(SongPackage.ScoreKeyPrefix, StringComparison.Ordinal))
            __result = 1;
    }

    private static HarmonyMethod Hook(string name) => new(typeof(CustomSongsArcade), name);

    // Runs before the screen builds its tabs and cards from the database.
    private static void ActivatePrefix(GenericArcadeMenuV2 __instance)
    {
        ArcadeDatabase? database = null;
        try
        {
            if (!__instance) return;
            database = __instance.arcadeDatabase;
            if (database == null || !database) return;
            CustomSongs.Refresh();
            Put(database);
        }
        catch (Exception ex)
        {
            Report(ex);
            // A half-made chapter must not break the screen: without it the arcade is the game's own.
            try { if (database != null && database) Take(database.songCategories); }
            catch { }
        }
    }

    /// <summary>Puts the chapter at the end of the database's list, once, or takes it out when there are no songs.</summary>
    private static void Put(ArcadeDatabase database)
    {
        var list = database.songCategories;
        if (list == null) return;
        Take(list);
        var songs = CustomSongs.All;
        if (songs.Count == 0) return;
        var built = Chapter();
        // After every game chapter, whatever their numbers.
        int index = list.Count;
        for (int i = 0; i < list.Count; i++)
            if (list[i] != null) index = Math.Max(index, list[i].index + 1);
        built.index = index;
        if (builtVersion != CustomSongs.Version)
        {
            var infos = new Il2CppSystem.Collections.Generic.List<ArcadeSongInfo>();
            SongInfos.Clear();
            for (int i = 0; i < songs.Count; i++)
            {
                var info = songs[i].Info;
                info.songIndex = i;
                infos.Add(info);
                SongInfos.Add(info.Pointer);
            }
            built.arcadeSongs = infos;
            builtVersion = CustomSongs.Version;
        }
        list.Add(built);
    }

    private static void Take(Il2CppSystem.Collections.Generic.List<ArcadeCategory>? list)
    {
        if (list == null) return;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var category = list[i];
            if (category != null && Chapters.Contains(category.Pointer)) list.RemoveAt(i);
        }
    }

    private static ArcadeCategory Chapter()
    {
        if (chapter != null) return chapter;
        // Every field is set: the screens read the names and the unlock condition without checks.
        var made = new ArcadeCategory();
        made.categoryDisplayName = new NocturneString(new LocalizedString(), ChapterName);
        made.shortDisplayName = new NocturneString(new LocalizedString(), ChapterShortName);
        made.arcadeSongs = new Il2CppSystem.Collections.Generic.List<ArcadeSongInfo>();
        made.isDemoAchievement = false;
        made.isDebugOnly = true;
        // No flag: the condition isn't valid, which the game reads as always unlocked.
        made.unlockCondition = new ProgressFlagCondition();
        chapter = made;
        chapterPointer = made.Pointer;
        Chapters.Add(chapterPointer);
        return made;
    }

    private static bool ShouldShowCategoryPrefix(ArcadeCategory category, ref bool __result)
    {
        try
        {
            if (category == null || chapterPointer == IntPtr.Zero || category.Pointer != chapterPointer) return true;
            __result = true;
            return false;
        }
        catch (Exception ex) { Report(ex); return true; }
    }

    private static bool IsArcadeSongUnlockedPrefix(ArcadeSongInfo song, ref bool __result)
    {
        try
        {
            if (!IsCustom(song)) return true;
            __result = true;
            return false;
        }
        catch (Exception ex) { Report(ex); return true; }
    }

    private static bool GetUnlockedMelodyCountPrefix(ArcadeSongInfo song, ref int __result)
    {
        try
        {
            if (!IsCustom(song)) return true;
            __result = 1;
            return false;
        }
        catch (Exception ex) { Report(ex); return true; }
    }

    private static bool IsCustom(ArcadeSongInfo? song) => song != null && SongInfos.Count > 0 && SongInfos.Contains(song.Pointer);

    private static void Report(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        ModLog.Error("Custom songs in the arcade failed: " + ex);
    }
}
