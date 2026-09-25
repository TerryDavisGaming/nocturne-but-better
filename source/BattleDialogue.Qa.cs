using System.Text;
using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// A QA aid for in-game tests. With the environment variable NFS_QA_DIALOGUE=1, custom battle
/// dialogue logs numbered "Battle dialogue: SHOT-nn" lines at the moments worth a picture (1.2 s
/// after each line starts typing, and half a second after the ready prompt comes back or a break's
/// song goes on), for the screenshot helper of qa/Dialogue-Shots.ps1; "Battle dialogue: WAITING-KEY
/// &lt;section&gt; &lt;n&gt;" once a line that waits for a key has finished typing, for the helper that
/// presses Enter; and "Battle dialogue QA:" lines with the director's state at the battle's start,
/// after each block of lines, at the cleanup, with each live line's picture ("while a live line
/// shows") and when its see-through box has its own alpha back ("the live box is back", "a block
/// takes the box"), and each live line's time against the song's. Each state has the boxes'
/// background alpha ("live box alpha normal 0.72, narrator 1.00").
/// With NFS_QA_DIALOGUE=auto, every line that waits for a key goes on by itself after 2.5 s
/// instead, for hands-free runs (not the real key path). Nothing else changes.
/// </summary>
internal static partial class BattleDialogue
{
    private static readonly string? QaMode = Environment.GetEnvironmentVariable("NFS_QA_DIALOGUE");
    private static readonly bool QaOn = QaMode is "1" or "auto";
    private static readonly bool QaAuto = QaMode == "auto";
    /// <summary>How long a line that waits for a key shows with NFS_QA_DIALOGUE=auto.</summary>
    private const double QaAutoSeconds = 2.5;
    /// <summary>A line's picture is taken this long after it starts typing, so the box is mostly typed.</summary>
    private const float QaTypedShot = 1.2f;
    /// <summary>The ready prompt's picture, and the one after a break, this long after the lines.</summary>
    private const float QaAfterShot = 0.5f;
    /// <summary>
    /// A line counts as typed once its typing count has reached the text's length and stayed there
    /// QaSettled, or stayed put QaStill (in case it never reaches the length), or after QaTypingLimit.
    /// </summary>
    private const float QaSettled = 0.15f, QaStill = 1.5f, QaTypingLimit = 15f;
    private static int qaShot;

    /// <summary>How long a line waits: its own time, else (with NFS_QA_DIALOGUE=auto) QaAutoSeconds; null waits for a key.</summary>
    private static double? Seconds(DialogueLine line) => line.Duration ?? (QaAuto ? QaAutoSeconds : null);

    private sealed partial class Director
    {
        // Pictures to log: when, what, the block whose line it is (skipped once that block is over),
        // and the state dump to log with it, if one.
        private readonly List<(float At, string What, Block? Of, string? Dump)> qaShots = new();
        // The running block's lines, followed through the box's text: the one typing now, whether
        // it has been said to wait for its key, and the text's typing count and when it last moved.
        private Block? qaBlock;
        private int qaLine;
        private bool qaWaited;
        private float qaTypingAt, qaMovedAt;
        private int qaVisible;
        private readonly Dictionary<IntPtr, (int Visible, string Text)> qaSeen = new();
        private string? qaLive;

        private void QaShot(float delay, string what, Block? of = null, string? dump = null)
        {
            if (QaOn) qaShots.Add((Time.unscaledTime + delay, what, of, dump));
        }

        /// <summary>Every frame: the pictures that are due, and the running block's lines.</summary>
        private void QaUpdate()
        {
            if (!QaOn) return;
            try
            {
                float now = Time.unscaledTime;
                for (int i = 0; i < qaShots.Count;)
                {
                    var shot = qaShots[i];
                    if (shot.At > now)
                    {
                        i++;
                        continue;
                    }
                    qaShots.RemoveAt(i);
                    if (shot.Of != null && (Current != shot.Of || shot.Of.Finished)) continue;
                    ModLog.Info($"Battle dialogue: SHOT-{++qaShot:00} {shot.What}.");
                    if (shot.Dump != null) QaDump(shot.Dump);
                }
                QaFollow(now);
            }
            catch (Exception ex) { ModLog.Info($"Battle dialogue QA: following the lines failed ({ex.Message})."); }
        }

        // A line starts typing when its box's typing count drops (the game's typing starts it at 0)
        // or the box's text changes; it has finished once the count reaches the text's length.
        private void QaFollow(float now)
        {
            var b = Current;
            if (b == null || b.State == null || b.Finished)
            {
                qaBlock = null;
                return;
            }
            if (qaBlock != b)
            {
                qaBlock = b;
                qaLine = -1;
                qaWaited = true;
            }
            if (qaLine + 1 < b.Lines.Count && QaStarted(b.Lines[qaLine + 1].Narrator))
            {
                var said = b.Lines[++qaLine];
                qaWaited = Seconds(said.Line) != null;
                qaTypingAt = qaMovedAt = now;
                qaVisible = -1;
                string section = b.Section switch
                {
                    DialogueSection.Before => "before",
                    DialogueSection.During => "break",
                    DialogueSection.AfterWin => "win",
                    _ => "loss",
                };
                QaShot(QaTypedShot, $"{section} line {qaLine + 1} ({said.Who})", b);
            }
            if (qaWaited || qaLine < 0) return;
            var text = QaText(b.Lines[qaLine].Narrator);
            if (text == null) return;
            int visible = text.maxVisibleCharacters;
            if (visible != qaVisible)
            {
                qaVisible = visible;
                qaMovedAt = now;
            }
            int length = text.textInfo?.characterCount ?? 0;
            bool typed = (length > 0 && visible >= length && now - qaMovedAt >= QaSettled) || (visible > 0 && now - qaMovedAt >= QaStill) || now - qaTypingAt >= QaTypingLimit;
            if (!typed) return;
            qaWaited = true;
            ModLog.Info($"Battle dialogue: WAITING-KEY {DialogueReader.Key(b.Section)} {qaLine + 1}");
        }

        // Both boxes' text as it is just before a block runs (its first line can start typing at once).
        private void QaBaseline()
        {
            if (!QaOn) return;
            try
            {
                QaStarted(false);
                QaStarted(true);
            }
            catch (Exception ex) { ModLog.Info($"Battle dialogue QA: reading the boxes failed ({ex.Message})."); }
        }

        // Whether the box a line uses has started typing a new line since it was last looked at.
        private bool QaStarted(bool narrator)
        {
            var text = QaText(narrator);
            if (text == null) return false;
            int visible = text.maxVisibleCharacters;
            string shown = text.text ?? "";
            bool known = qaSeen.TryGetValue(text.Pointer, out var seen);
            qaSeen[text.Pointer] = (visible, shown);
            return known && (visible < seen.Visible || shown != seen.Text);
        }

        // The text of the Normal box or the Narrator's.
        private static TMP_Text? QaText(bool narrator)
        {
            var style = Style(narrator);
            if (narrator) return style.TryCast<DialogueStyleNarrator>()?.text;
            return style.TryCast<DialogueStyleNormal>()?.dialogueText;
        }

        /// <summary>A live line: logs its time against the song's when it fires, and its picture once it types.</summary>
        private void QaLiveFired(DialogueLine line, Said said, double now)
        {
            if (!QaOn) return;
            qaLive = $"live line at {DialogueReader.Clock(line.Time)} ({said.Who})";
            ModLog.Info($"Battle dialogue QA: the line at {DialogueReader.Clock(line.Time)} fired at song time {now:0.000} ({(now - line.Time) * 1000:0} ms late).");
        }

        private void QaLiveStarted()
        {
            // With a state dump, for the see-through box's alpha while the line shows.
            if (qaLive != null) QaShot(QaTypedShot, qaLive, dump: "while a live line shows");
            qaLive = null;
        }

        /// <summary>The director's state, the game's cutscenes and characters, and the save's seen scenes.</summary>
        internal void QaDump(string when)
        {
            if (!QaOn) return;
            var sb = new StringBuilder($"Battle dialogue QA: {when}: state {phase}");
            Qa(sb, "cutscenes busy", () => CutsceneManager.Instance is { } cutscenes && cutscenes ? cutscenes.Busy.ToString() : "no cutscene manager");
            Qa(sb, "characters", () =>
            {
                var list = DataUtility.NpcDatabase?.Data;
                if (list == null) return "not loaded";
                var ours = new List<string>();
                for (int i = 0; i < list.Count; i++)
                    if (list[i]?.characterId is { } id && id.StartsWith(SpeakerPrefix, StringComparison.Ordinal)) ours.Add(id);
                return $"{list.Count}" + (ours.Count > 0 ? $" ({string.Join(", ", ours)})" : ", none of the mod's");
            });
            Qa(sb, "seen scenes", () =>
            {
                var scenes = GameDataManager.Interactables;
                if (scenes == null) return "no save loaded";
                return $"\"{Caller}\" {scenes.HasViewedScene(Caller)}, \"Arcade\" {scenes.HasViewedScene("Arcade")}";
            });
            Qa(sb, "conductor paused", () => conductor ? conductor.Paused.ToString() : "gone");
            // The boxes' background alpha: LiveBoxAlpha of the game's while a live line shows, the game's own otherwise.
            Qa(sb, "live box alpha", () => $"normal {BoxAlphaText(Style(narrator: false))}, narrator {BoxAlphaText(Style(narrator: true))}");
            sb.Append($"; music held {CustomMusic.Held}; box raised {BubbleStyle != IntPtr.Zero}; pause menu off {GatesPause}.");
            ModLog.Info(sb.ToString());
        }

        private static void Qa(StringBuilder sb, string what, Func<string> read)
        {
            string value;
            try { value = read(); }
            catch (Exception ex) { value = $"unreadable ({ex.Message})"; }
            sb.Append($"; {what} {value}");
        }
    }
}
