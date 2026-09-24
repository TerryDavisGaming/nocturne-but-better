using System.Globalization;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using UnityEngine;
using Il2CppGeneric = Il2CppSystem.Collections.Generic;

namespace NocturneFlatScroll;

/// <summary>
/// The arcade's own scores: ArcadeScores.json next to the custom charts, laid out like the game's
/// .score files plus a version, and not Steam-synced. In memory the file is one of the game's own
/// SavedScoresData objects, so the game's score code reads and writes it directly. Custom songs'
/// keys ("NocturneButBetter/song/...") always go here, in or out of an arcade session: while the
/// score manager handles such a key it is pointed at this object instead of the story's scores.
/// </summary>
internal static class ArcadeScoreStore
{
    internal const string SongKeyPrefix = "NocturneButBetter/song/";
    private const int FormatVersion = 1;
    private const long MaxFileBytes = 32 * 1024 * 1024;

    private static SavedScoresData? scores;
    private static bool loaded, dirty, readOnly, mainBroken;
    private static int routeDepth;

    // One ScoreManager exists; its data object is looked up once.
    private static IntPtr cachedManager;
    private static GameDataScriptableObject? cachedData;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    internal static string FilePath => Path.Combine(Application.persistentDataPath, "NocturneButBetter", "ArcadeScores.json");

    internal static bool IsCustomSongKey(string? key) => key != null && key.StartsWith(SongKeyPrefix, StringComparison.Ordinal);

    /// <summary>The store as the game's own score object, read from the file on first use.</summary>
    internal static SavedScoresData Scores
    {
        get
        {
            EnsureLoaded();
            return scores!;
        }
    }

    // ---- key routing -----------------------------------------------------------------------

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        // Every ScoreManager call that takes a song key; HasAnyScore has two overloads. Each one's
        // key is its first argument. The prefix runs last so it sees the key after ChartSwap's
        // renaming.
        var names = new[] { "TryRecordScore", "IsNewHighScore", "HasAnyScore", "GetHighScore", "HasHighScore",
            "GetUnlockedMelodiesForSong", "TryGetScoreData" };
        foreach (var name in names)
        {
            // Recording is what keeps custom songs out of the story's scores, so without it the
            // arcade isn't installed. A lookup that can't be routed only misses custom songs.
            bool required = name == "TryRecordScore";
            try
            {
                var methods = AccessTools.GetDeclaredMethods(typeof(ScoreManager)).Where(m => m.Name == name).ToList();
                if (methods.Count == 0) throw new MissingMethodException(typeof(ScoreManager).FullName, name);
                foreach (var method in methods)
                    harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(ArcadeScoreStore), nameof(RoutePrefix)) { priority = Priority.Last },
                        postfix: required ? new HarmonyMethod(typeof(ArcadeScoreStore), nameof(RecordPostfix)) : null,
                        finalizer: new HarmonyMethod(typeof(ArcadeScoreStore), nameof(RouteFinalizer)));
            }
            catch (Exception ex) when (!required)
            {
                ModLog.Error($"Arcade: custom song scores can't be looked up through ScoreManager.{name}: {ex}");
            }
        }
    }

    /// <summary>Where a routed call found the scores, to put them back when it returns.</summary>
    private sealed class Route
    {
        internal readonly GameDataScriptableObject Data;
        internal readonly SavedScoresData? Previous;

        internal Route(GameDataScriptableObject data, SavedScoresData? previous)
        {
            Data = data;
            Previous = previous;
        }
    }

    private static void RoutePrefix(ScoreManager __instance, string __0, out Route? __state)
    {
        __state = null;
        if (!IsCustomSongKey(__0)) return;
        try
        {
            var data = DataOf(__instance);
            if (data == null) return;
            var store = Scores;
            var current = data.scoresData;
            // A call made from inside a routed call (TryRecordScore asks GetHighScore) is already there.
            if (current != null && current.Pointer == store.Pointer) return;
            data.scoresData = store;
            routeDepth++;
            __state = new Route(data, current);
        }
        catch (Exception ex) { ReportOnce("routing a custom song's score", ex); }
    }

    // Runs even when the game's method throws, so the story's scores always come back.
    private static void RouteFinalizer(Route? __state)
    {
        if (__state == null) return;
        try { __state.Data.scoresData = __state.Previous; }
        catch (Exception ex) { ReportOnce("routing a custom song's score", ex); }
        finally { routeDepth--; }
    }

    private static void RecordPostfix(string __0, int __1, CombatPlayerScore __2, Route? __state)
    {
        try
        {
            if (IsCustomSongKey(__0))
            {
                // The game recorded it straight into the store.
                if (__state != null)
                {
                    dirty = true;
                    Flush();
                }
                return;
            }
            // In an arcade session the game recorded into the merged view; the store gets the
            // played score on its own terms, so it only ever holds arcade results.
            if (!ArcadeSession.Active || __0 == null || __2 == null) return;
            Record(__0, __1, __2);
            Flush();
        }
        catch (Exception ex) { ReportOnce("recording an arcade score", ex); }
    }

    private static GameDataScriptableObject? DataOf(ScoreManager manager)
    {
        if (manager.Pointer == cachedManager && cachedData) return cachedData;
        var data = manager.dataProvider?.TryCast<GameDataScriptableObject>();
        cachedManager = manager.Pointer;
        cachedData = data;
        return data;
    }

    /// <summary>How many routed score calls are still running; 0 outside the game's score code.</summary>
    internal static int RouteDepth => routeDepth;

    // ---- scores ----------------------------------------------------------------------------

    /// <summary>
    /// Records a played score into the store with the game's own rule (ScoreManager.TryRecordScore):
    /// a higher score replaces the entry for its melodies, a lower one can only add a full combo,
    /// and a full combo, once earned, stays.
    /// </summary>
    internal static void Record(string key, int difficulty, CombatPlayerScore played)
    {
        var list = ScoresFor(Scores, key, difficulty, create: true)!;
        bool full = played.IsValidForDisplay() && played.combo == played.totalNotes;
        var old = HighScore(list, played.melody1, played.melody2);
        if (old != null && played.score <= old.score)
        {
            if (full && !old.fullCombo)
            {
                old.fullCombo = true;
                dirty = true;
            }
            return;
        }
        bool wasFull = old != null && old.IsFullCombo;
        RemoveMelodies(list, played.melody1, played.melody2);
        var copy = CopyScore(played);
        copy.fullCombo = full || wasFull;
        list.Add(copy);
        dirty = true;
    }

    /// <summary>
    /// A deep copy of the story's scores with the store's results merged in: for each song,
    /// difficulty and pair of melodies the higher score is kept and a full combo from either side
    /// counts. Custom songs stay out of it; their calls always read the store itself.
    /// </summary>
    internal static SavedScoresData BuildView(SavedScoresData? story, out int storySongs, out int arcadeEntries)
    {
        var view = new SavedScoresData();
        storySongs = 0;
        arcadeEntries = 0;
        if (story != null)
        {
            if (story.meta != null && view.meta != null) view.meta.slotIndex = story.meta.slotIndex;
            var songs = story.songScores;
            foreach (var key in KeysOf(songs))
            {
                var song = songs![key];
                if (song == null) continue;
                view.songScores[key] = CopySong(song, key);
                storySongs++;
            }
        }
        var store = Scores;
        var stored = store.songScores;
        foreach (var key in KeysOf(stored))
        {
            if (IsCustomSongKey(key)) continue;
            var song = stored![key];
            var difficulties = song?.difficultyData;
            foreach (int difficulty in KeysOf(difficulties))
            {
                var played = difficulties![difficulty]?.scores;
                if (played == null) continue;
                var list = ScoresFor(view, key, difficulty, create: true)!;
                for (int i = 0; i < played.Count; i++)
                {
                    var score = played[i];
                    if (score == null) continue;
                    Merge(list, score);
                    arcadeEntries++;
                }
            }
        }
        return view;
    }

    private static void Merge(Il2CppGeneric.List<CombatPlayerScore> list, CombatPlayerScore stored)
    {
        int m1 = stored.melody1, m2 = stored.melody2;
        bool anyFull = stored.IsFullCombo;
        CombatPlayerScore? best = null;
        bool bestValid = false;
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s == null || s.melody1 != m1 || s.melody2 != m2) continue;
            if (s.IsFullCombo) anyFull = true;
            bool valid = s.IsValidForDisplay();
            if (best == null || Better(valid, s.score, bestValid, best.score)) { best = s; bestValid = valid; }
        }
        if (best == null || Better(stored.IsValidForDisplay(), stored.score, bestValid, best.score))
        {
            RemoveMelodies(list, m1, m2);
            var copy = CopyScore(stored);
            copy.fullCombo = anyFull;
            list.Add(copy);
        }
        else if (anyFull) best.fullCombo = true;
    }

    // The game only shows scores that are valid for display, so a valid one wins over a higher invalid one.
    private static bool Better(bool valid, int score, bool thanValid, int thanScore) =>
        valid != thanValid ? valid : score > thanScore;

    /// <summary>
    /// What ScoreManager.GetHighScore returns for these melodies: the highest-scoring entry that is
    /// valid for display (the first one on a tie), or null.
    /// </summary>
    private static CombatPlayerScore? HighScore(Il2CppGeneric.List<CombatPlayerScore> list, int m1, int m2)
    {
        CombatPlayerScore? best = null;
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s == null || s.melody1 != m1 || s.melody2 != m2 || !s.IsValidForDisplay()) continue;
            if (best == null || s.score > best.score) best = s;
        }
        return best;
    }

    private static void RemoveMelodies(Il2CppGeneric.List<CombatPlayerScore> list, int m1, int m2)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var s = list[i];
            if (s != null && s.melody1 == m1 && s.melody2 == m2) list.RemoveAt(i);
        }
    }

    /// <summary>The score list for a song and difficulty, created on the way when asked to.</summary>
    private static Il2CppGeneric.List<CombatPlayerScore>? ScoresFor(SavedScoresData data, string key, int difficulty, bool create)
    {
        var songs = data.songScores;
        if (songs == null)
        {
            if (!create) return null;
            data.songScores = songs = new Il2CppGeneric.Dictionary<string, SongScoreData>();
        }
        var song = songs.ContainsKey(key) ? songs[key] : null;
        if (song == null)
        {
            if (!create) return null;
            song = new SongScoreData { songId = key };
            songs[key] = song;
        }
        var difficulties = song.difficultyData;
        if (difficulties == null)
        {
            if (!create) return null;
            song.difficultyData = difficulties = new Il2CppGeneric.Dictionary<int, SongDifficultyData>();
        }
        var entry = difficulties.ContainsKey(difficulty) ? difficulties[difficulty] : null;
        if (entry == null)
        {
            if (!create) return null;
            entry = new SongDifficultyData();
            difficulties[difficulty] = entry;
        }
        if (entry.scores == null)
        {
            if (!create) return null;
            entry.scores = new Il2CppGeneric.List<CombatPlayerScore>();
        }
        return entry.scores;
    }

    private static SongScoreData CopySong(SongScoreData song, string key)
    {
        var copy = new SongScoreData { songId = song.songId ?? key };
        var difficulties = song.difficultyData;
        foreach (int difficulty in KeysOf(difficulties))
        {
            var entry = difficulties![difficulty];
            if (entry == null) continue;
            var copied = new SongDifficultyData();
            var list = entry.scores;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null) copied.scores.Add(CopyScore(list[i]));
            copy.difficultyData[difficulty] = copied;
        }
        return copy;
    }

    // The game's own Clone throws on a score without lists, so the copy is made field by field.
    private static CombatPlayerScore CopyScore(CombatPlayerScore s)
    {
        var copy = NewScore();
        copy.melody1 = s.melody1;
        copy.melody2 = s.melody2;
        copy.totalNotes = s.totalNotes;
        copy.maxScore = s.maxScore;
        copy.score = s.score;
        copy.combo = s.combo;
        copy.fullCombo = s.fullCombo;
        var grades = s.grades;
        if (grades != null)
            for (int i = 0; i < grades.Count; i++)
            {
                var grade = grades[i];
                if (grade != null) copy.grades.Add(new NoteGradeScore { index = grade.index, count = grade.count });
            }
        var difficulties = s.difficulties;
        if (difficulties != null)
            for (int i = 0; i < difficulties.Count; i++) copy.difficulties.Add(difficulties[i]);
        return copy;
    }

    private static CombatPlayerScore NewScore()
    {
        var score = new CombatPlayerScore();
        if (score.grades == null) score.grades = new Il2CppGeneric.List<NoteGradeScore>();
        if (score.difficulties == null) score.difficulties = new Il2CppGeneric.List<NocturneDifficulty>();
        return score;
    }

    private static List<TKey> KeysOf<TKey, TValue>(Il2CppGeneric.Dictionary<TKey, TValue>? dictionary)
    {
        var keys = new List<TKey>();
        if (dictionary == null) return keys;
        foreach (var key in dictionary.Keys) keys.Add(key);
        return keys;
    }

    // ---- the file --------------------------------------------------------------------------

    internal sealed class StoredFile
    {
        public int version { get; set; } = FormatVersion;
        public StoredMeta meta { get; set; } = new();
        public Dictionary<string, StoredSong> songScores { get; set; } = new();
    }

    internal sealed class StoredMeta
    {
        public int slotIndex { get; set; }
        public DateTime timeSaved { get; set; }
    }

    internal sealed class StoredSong
    {
        public string songId { get; set; } = "";
        public Dictionary<string, StoredDifficulty> difficultyData { get; set; } = new();
    }

    internal sealed class StoredDifficulty
    {
        public List<StoredScore> scores { get; set; } = new();
    }

    internal sealed class StoredScore
    {
        public int melody1 { get; set; }
        public int melody2 { get; set; }
        public List<StoredGrade>? grades { get; set; }
        public int totalNotes { get; set; }
        public int maxScore { get; set; }
        public int score { get; set; }
        public int combo { get; set; }
        public bool fullCombo { get; set; }
        public List<int>? difficulties { get; set; }
        // Written like the game's own files do; ignored when read.
        public bool IsFullCombo => fullCombo || combo == totalNotes;
        public float ScorePercentage => maxScore > 0 ? (float)score / maxScore : 0f;
    }

    internal sealed class StoredGrade
    {
        public int index { get; set; }
        public int count { get; set; }
    }

    private static void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        scores = new SavedScoresData();
        string path;
        try { path = FilePath; }
        catch (Exception ex)
        {
            ModLog.Error("Arcade: can't find the arcade scores folder, so arcade scores aren't kept: " + ex.Message);
            readOnly = true;
            return;
        }
        StoredFile? file = null;
        // A missing file is a fresh start. A broken one is kept aside on the next save, and its
        // backup (the version before the last save) is used meanwhile.
        if (File.Exists(path))
        {
            file = TryRead(path, out string? error);
            if (file == null)
            {
                mainBroken = true;
                ModLog.Error($"Arcade: {path} could not be read ({error}); trying its backup.");
                string backup = path + ".bak";
                if (File.Exists(backup))
                {
                    file = TryRead(backup, out string? backupError);
                    if (file != null)
                    {
                        dirty = true;
                        ModLog.Info("Arcade: arcade scores restored from " + backup + ".");
                    }
                    else ModLog.Error($"Arcade: the backup could not be read either ({backupError}); starting with no arcade scores.");
                }
            }
        }
        if (file == null) return;
        if (file.version > FormatVersion)
        {
            readOnly = true;
            ModLog.Error($"Arcade: {path} was written by a newer version of the mod (version {file.version}); " +
                         "its scores are shown but new ones aren't saved until the mod is updated.");
        }
        try
        {
            scores = FromFile(file, out int entries);
            ModLog.Info($"Arcade: loaded {entries} arcade scores for {file.songScores?.Count ?? 0} songs from {path}.");
        }
        catch (Exception ex)
        {
            scores = new SavedScoresData();
            readOnly = true;
            ModLog.Error("Arcade: reading the arcade scores failed, so they aren't saved this time: " + ex);
        }
    }

    private static StoredFile? TryRead(string path, out string? error)
    {
        error = null;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes) { error = "the file is too big"; return null; }
            string json = File.ReadAllText(path, Encoding.UTF8);
            var file = JsonSerializer.Deserialize<StoredFile>(json, ReadOptions);
            if (file == null) error = "the file is empty";
            return file;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static SavedScoresData FromFile(StoredFile file, out int entries)
    {
        var data = new SavedScoresData();
        entries = 0;
        if (file.songScores == null) return data;
        foreach (var (key, song) in file.songScores)
        {
            if (string.IsNullOrEmpty(key) || song?.difficultyData == null) continue;
            foreach (var (difficultyKey, difficulty) in song.difficultyData)
            {
                if (difficulty?.scores == null ||
                    !int.TryParse(difficultyKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) continue;
                var list = ScoresFor(data, key, index, create: true)!;
                foreach (var stored in difficulty.scores)
                {
                    if (stored == null) continue;
                    list.Add(FromStored(stored));
                    entries++;
                }
            }
        }
        return data;
    }

    private static CombatPlayerScore FromStored(StoredScore stored)
    {
        var score = NewScore();
        score.melody1 = stored.melody1;
        score.melody2 = stored.melody2;
        score.totalNotes = stored.totalNotes;
        score.maxScore = stored.maxScore;
        score.score = stored.score;
        score.combo = stored.combo;
        score.fullCombo = stored.fullCombo;
        if (stored.grades != null)
            foreach (var grade in stored.grades)
                if (grade != null) score.grades.Add(new NoteGradeScore { index = grade.index, count = grade.count });
        if (stored.difficulties != null)
            foreach (int difficulty in stored.difficulties) score.difficulties.Add((NocturneDifficulty)difficulty);
        return score;
    }

    private static StoredFile ToFile(SavedScoresData data, out int entries)
    {
        var file = new StoredFile { meta = new StoredMeta { slotIndex = 0, timeSaved = DateTime.Now } };
        entries = 0;
        var songs = data.songScores;
        foreach (var key in KeysOf(songs))
        {
            var song = songs![key];
            if (song == null) continue;
            var stored = new StoredSong { songId = song.songId ?? key };
            var difficulties = song.difficultyData;
            foreach (int difficulty in KeysOf(difficulties))
            {
                var list = difficulties![difficulty]?.scores;
                if (list == null) continue;
                var copied = new StoredDifficulty();
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null) copied.scores.Add(ToStored(list[i]));
                if (copied.scores.Count == 0) continue;
                stored.difficultyData[difficulty.ToString(CultureInfo.InvariantCulture)] = copied;
                entries += copied.scores.Count;
            }
            if (stored.difficultyData.Count > 0) file.songScores[key] = stored;
        }
        return file;
    }

    private static StoredScore ToStored(CombatPlayerScore s)
    {
        var stored = new StoredScore
        {
            melody1 = s.melody1, melody2 = s.melody2, totalNotes = s.totalNotes, maxScore = s.maxScore,
            score = s.score, combo = s.combo, fullCombo = s.fullCombo,
            grades = new List<StoredGrade>(), difficulties = new List<int>()
        };
        var grades = s.grades;
        if (grades != null)
            for (int i = 0; i < grades.Count; i++)
            {
                var grade = grades[i];
                if (grade != null) stored.grades.Add(new StoredGrade { index = grade.index, count = grade.count });
            }
        var difficulties = s.difficulties;
        if (difficulties != null)
            for (int i = 0; i < difficulties.Count; i++) stored.difficulties.Add((int)difficulties[i]);
        return stored;
    }

    /// <summary>Writes the store if it changed since the last save. Never throws.</summary>
    internal static void Flush()
    {
        if (!loaded || !dirty || scores == null) return;
        if (readOnly)
        {
            ReportOnceMessage("read only", "Arcade: arcade scores aren't saved this run (see the earlier message).");
            return;
        }
        try
        {
            string path = FilePath;
            var file = ToFile(scores, out int entries);
            WriteAtomically(path, JsonSerializer.Serialize(file, WriteOptions));
            dirty = false;
            ModLog.Info($"Arcade: saved {entries} arcade scores for {file.songScores.Count} songs to {path}.");
        }
        catch (Exception ex) { ModLog.Error("Arcade: saving the arcade scores failed; trying again after the next battle: " + ex.Message); }
    }

    /// <summary>
    /// Writes a temporary file and then swaps it in, keeping the previous file as .bak, so a crash
    /// mid-write never leaves a half-written score file.
    /// </summary>
    private static void WriteAtomically(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp", backup = path + ".bak";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(json);
            writer.Flush();
            stream.Flush(true);
        }
        if (!File.Exists(path))
        {
            File.Move(temp, path);
            return;
        }
        if (mainBroken)
        {
            // Kept for the player to look at, instead of replacing the good backup with it.
            string aside = CustomCharts.FreeName(Path.Combine(Path.GetDirectoryName(path)!,
                $"ArcadeScores.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
            File.Move(path, aside);
            File.Move(temp, path);
            mainBroken = false;
            ModLog.Info("Arcade: the unreadable arcade scores file was kept as " + aside + ".");
            return;
        }
        try { File.Replace(temp, path, backup, true); }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            File.Copy(path, backup, true);
            File.Move(temp, path, true);
        }
    }

    private static readonly HashSet<string> Reported = new();

    private static void ReportOnce(string where, Exception ex)
    {
        if (Reported.Add(where)) ModLog.Error($"Arcade: {where} failed: {ex}");
    }

    private static void ReportOnceMessage(string key, string message)
    {
        if (Reported.Add(key)) ModLog.Error(message);
    }
}
