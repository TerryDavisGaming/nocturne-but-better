namespace NocturnePlus;

/// <summary>
/// Keeps the score of an arcade battle fought with an item the save doesn't own ("All items
/// (arcade gear)") out of the save slot's .score file. The game records a battle's score in the
/// scores in memory (the save's GameDataScriptableObject.scoresData, which ScoreManager reads on
/// every call), and leaving the battle writes them to the .score file with SaveFileManager.SaveScores
/// just before ExitCombat. So for such a battle the scores in memory are swapped for a copy when it
/// starts: the game records into the copy, the results screen shows the score as usual, SaveScores
/// is refused while the copy is in (TestPlay's hook, see <see cref="ScoresHeld"/>), and the save's
/// own scores go back when the battle is left and in every backstop. The next battle's save then
/// writes the save's own scores, without this one.
/// </summary>
internal static partial class BattleGear
{
    private sealed class HeldScores
    {
        internal string Title = "";
        internal GameDataScriptableObject Data = null!;   // the save in memory, whose scores were swapped
        internal SavedScoresData Own = null!;              // its own scores, put back afterwards
        internal SavedScoresData Copy = null!;             // what the battle records into
    }

    private static HeldScores? heldScores;
    private static bool loggedScoresBlocked;

    /// <summary>
    /// Whether a battle's scores are kept out of the save now: nothing may write the scores then. That's
    /// also so for a battle fought on "all items" gear that couldn't be taken out, even when its scores
    /// couldn't be held.
    /// </summary>
    internal static bool ScoresHeld => heldScores != null || (inBattle && swap is { AllItems: true });

    /// <summary>Called when SaveFileManager.SaveScores is refused because <see cref="ScoresHeld"/>.</summary>
    internal static void NoteScoresBlocked()
    {
        if (loggedScoresBlocked) return;
        loggedScoresBlocked = true;
        ModLog.Info($"Arcade gear: blocked SaveFileManager.SaveScores during {heldScores?.Title ?? swap?.Title ?? "the battle"}; its score isn't saved.");
    }

    /// <summary>Swaps the scores in memory for a copy for this battle. Throws with the reason, having changed nothing, when it can't.</summary>
    private static void HoldScores(string title)
    {
        if (!TestPlay.ScoresGuarded) throw new InvalidOperationException("the score save hook isn't installed");
        var saveFile = GameDataManager.SaveFile?.TryCast<SaveFileManager>()
            ?? throw new InvalidOperationException("the game's save manager isn't ready");
        var data = saveFile.currentSaveData;
        if (data == null || !data) throw new InvalidOperationException("no save is loaded");
        var own = data.scoresData ?? throw new InvalidOperationException("the save has no scores");
        var copy = CopyScores(own);
        // Noted before the swap, so SaveScores is refused from the moment the copy is in.
        heldScores = new HeldScores { Title = title, Data = data, Own = own, Copy = copy };
        loggedScoresBlocked = false;
        data.scoresData = copy;
        ModLog.Info($"Arcade gear: {title}: an item your save doesn't own is in, so this battle's score isn't saved and it counts for no achievements.");
    }

    // A battle fought on the last battle's "all items" gear, which couldn't be taken out, records into
    // a copy too. Without one, saving the scores is still refused while it runs (ScoresHeld).
    private static void HoldLeftoverScores(string title)
    {
        try { HoldScores(title); }
        catch (Exception ex)
        {
            ModLog.Error($"Arcade gear: {title}: the last battle's gear, with an item your save doesn't own, is still in, and this battle's " +
                         $"scores couldn't be kept apart; saving scores is refused until it ends: {ex}");
        }
    }

    /// <summary>Puts the save's own scores back. Kept when that fails, so saving the scores stays refused and a backstop tries again.</summary>
    private static void ReleaseScores(string reason, bool backstop)
    {
        var held = heldScores;
        if (held == null) return;
        string where = backstop ? $"backstop: {reason}; " : "";
        try
        {
            var now = held.Data ? held.Data.scoresData : null;
            if (now != null && now.Pointer == held.Copy.Pointer)
            {
                held.Data.scoresData = held.Own;
                ModLog.Info($"Arcade gear: {where}your saved scores are back after {held.Title}, without its score{(backstop ? "" : $" ({reason})")}.");
            }
            else
            {
                // The save was read again during the battle: what's in memory now came from the disk.
                ModLog.Info($"Arcade gear: {where}the save was read again during {held.Title}, so its scores are the ones on disk.");
            }
        }
        catch (Exception ex)
        {
            Note("Arcade gear: putting your saved scores back failed; saving scores stays off and it's tried again later: " + ex);
            return;
        }
        heldScores = null;
    }

    /// <summary>
    /// A copy of the scores that shares nothing the game changes when it records one: every song's
    /// entry, every difficulty's list and every score is new. The game's own constructors make the
    /// collections, and CombatPlayerScore.Clone copies a score.
    /// </summary>
    private static SavedScoresData CopyScores(SavedScoresData own)
    {
        var copy = new SavedScoresData();
        copy.meta = own.meta;
        var songs = copy.songScores ?? throw new InvalidOperationException("new scores have no song list");
        if (own.songScores == null) return copy;
        foreach (var song in own.songScores)
        {
            var from = song.Value;
            if (from == null)
            {
                songs[song.Key] = null!;
                continue;
            }
            var to = new SongScoreData();
            to.songId = from.songId;
            var levels = to.difficultyData ?? throw new InvalidOperationException("a new song score has no difficulty list");
            if (from.difficultyData != null)
            {
                foreach (var level in from.difficultyData)
                {
                    var scores = level.Value?.scores;
                    var added = new SongDifficultyData();
                    var list = added.scores ?? throw new InvalidOperationException("a new difficulty has no score list");
                    if (scores != null)
                        for (int i = 0; i < scores.Count; i++)
                        {
                            var score = scores[i];
                            list.Add(score != null ? score.Clone() : null!);
                        }
                    levels[level.Key] = level.Value == null ? null! : added;
                }
            }
            songs[song.Key] = to;
        }
        return copy;
    }
}
