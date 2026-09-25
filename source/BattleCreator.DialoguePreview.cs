using UnityEngine;
using UnityEngine.UI;
using static NocturneFlatScroll.EditorInput;
using static NocturneFlatScroll.EditorUi;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Key = UnityEngine.InputSystem.Key;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

// The Dialogue page's preview: the game's dialogue box on the battle's 480 x 270 screen (at 4/3,
// or close up at 2x), laid out as the game lays out its own (research: dialogue_tree.txt). The
// box is the bottom 81 game pixels; portraits stand centred 54 above the bottom, 30 either side of
// their side's middle, the right side mirrored, the speaker lit and the others grey. Names and
// text are in the game's own fonts. A game character's faces come from the game
// (PortraitManager), loaded only for the characters showing and let go when they stop showing; a
// speaker of the battle's own shows its pictures at the size the battle shows them. Lines during
// the song show over a faint note field, with the box where the battle puts it. Play runs the
// section from the chosen line with the box's typing.
internal static partial class BattleCreator
{
    // The box, in game pixels from the screen's bottom left (dialogue_tree.txt). The bubble's
    // background is drawn a little bigger than its text, like the game's translucent one.
    private const float LeftMiddle = 72, RightMiddle = 408, PortraitY = 54, PortraitStep = 30;
    private const float BodyX = 179, BodyY = 13.5f, BodyW = 144, BodyH = 39;
    private const float NameX = 179, NameY = 55.5f, NameW = 150, NameH = 10;
    private const float BubbleX = 164, BubbleY = 8, BubbleW = 172, BubbleH = 62;
    // How high the box sits in a battle: the Narrator's is raised, and a line during the song is
    // raised away from the receptors (except in 2D upscroll, where they're at the top).
    private const float NarratorLift = 110, LiveLift = 180;
    // The game's text sizes in game pixels (its fonts' own sizes; the game's settings aren't read).
    private const float BoxTextSize = 9, BoxNameSize = 7;
    // The box types 40 letters a second.
    private const float TypingSpeed = 40;
    private const float LaneW = 22, ReceptorsDown = 40, ReceptorsUp = 230;

    private static RectTransform? dialogueBox;
    private static Image? dialogueBubble, dialogueReceptors;
    private static readonly List<Image> dialogueLanes = new();
    private static readonly Image[] dialoguePortraits = new Image[4];
    private static TMP_Text? dialogueName, dialogueText, dialogueNote;
    private static bool dialogueCloseUp;
    // Why the preview stopped; the battle itself isn't affected.
    private static string dialoguePreviewFailed = "";
    // The canvas units per game pixel the box's text was last sized for (for measuring).
    private static float dialogueTextUnit = PreviewW / GameW;

    // ---- building -------------------------------------------------------------------------------

    private static void BuildDialoguePreview(Page p, float y)
    {
        dialogueLanes.Clear();
        dialogueBox = MakeImage("DialoguePreview", pagePanels[p], Hex(0x0B0A10)).rectTransform;
        PlaceTop(dialogueBox, Col2, y, PreviewW, PreviewH);
        // Portraits that run off the screen are cut at its edges, like the game's.
        try { dialogueBox.gameObject.AddComponent<RectMask2D>(); }
        catch (Exception ex) { ModLog.Error("Battle creator: the dialogue preview can't cut pictures at its edges: " + ex.Message); }
        for (int i = 0; i < 5; i++) dialogueLanes.Add(MakeImage("Lane", dialogueBox, Hex(0x9D92B4, 0.07f)));
        dialogueReceptors = MakeImage("Receptors", dialogueBox, Hex(0xEAE6F5, 0.3f));
        for (int i = 0; i < dialoguePortraits.Length; i++) dialoguePortraits[i] = MakeImage("Portrait", dialogueBox, Color.white);
        dialogueBubble = MakeImage("Bubble", dialogueBox, Hex(0x1C1827, 0.9f));
        dialogueName = MakeText("Name", dialogueBox, 10, TextAlignmentOptions.TopLeft);
        dialogueName.enableWordWrapping = false;
        dialogueName.richText = false;
        dialogueName.color = Hex(0xD9C8FF);
        dialogueText = MakeText("Text", dialogueBox, 12, TextAlignmentOptions.TopLeft);
        dialogueText.enableWordWrapping = true;
        dialogueText.overflowMode = TextOverflowModes.Overflow;
        dialogueText.richText = false;
        dialogueNote = MakeText("Note", dialogueBox, 16, TextAlignmentOptions.TopLeft);
        dialogueNote.color = DimText;
        Stretch(dialogueNote.rectTransform, 12, 12, 12, 8);
        foreach (var image in dialoguePortraits) image.gameObject.SetActive(false);
    }

    // ---- the frame ------------------------------------------------------------------------------

    private static void UpdateDialoguePreview(InputMouse? clicks)
    {
        if (dialoguePreviewFailed.Length > 0)
        {
            dialogueNote!.text = Escape($"The preview stopped ({dialoguePreviewFailed}). Open the page again to try again; the battle itself isn't affected.");
            return;
        }
        try
        {
            UpdateDialoguePlay(clicks);
            DrawDialoguePreview();
        }
        catch (Exception ex)
        {
            // A preview that fails must not close the creator (and lose unsaved changes) with it.
            ModLog.Error("Battle creator: the dialogue preview failed: " + ex);
            dialoguePreviewFailed = ex.Message;
            StopDialoguePlay();
            HideDialogueBox();
            foreach (var image in dialoguePortraits) image.gameObject.SetActive(false);
        }
        ReleaseUnwantedFaces();
    }

    /// <summary>Leaving the page, the battle or the creator: the preview lets go of the faces and pictures it loaded, and with <paramref name="forget"/> the page's choices.</summary>
    private static void StopDialoguePreview(bool forget)
    {
        StopDialoguePlay();
        ReleaseAllFaces();
        ClearDecodedFaces();
        dialoguePreviewFailed = "";
        if (!forget) return;
        dialogueTab = DialogueTab.Before;
        chosenLine = -1;
        lineFirst = 0;
        followedRow = -1;
        chosenSpeaker = null;
        chosenFace = -1;
        faceFirst = 0;
        lastSpeaker = DialogueReader.Narrator;
        dialogueRead = null;
        dialogueReadFor = null;
        ownChecksKey = "";
    }

    // What the preview shows this frame.
    private sealed class Shot
    {
        internal readonly List<(string Speaker, string? Face, DialogueSide Side, int Slot, bool Lit)> Cast = new();
        internal bool Box, Narration, Field;
        internal string Name = "", Text = "";
        // Letters typed so far; -1 shows them all.
        internal int Letters = -1;
        internal float Lift;
        internal string Note = "";
    }

    private static void DrawDialoguePreview()
    {
        FindGameFonts();
        var shot = PreviewShot();
        float unit = PreviewW / GameW * (dialogueCloseUp ? 2 : 1);
        // Close up, the view is the 240 x 135 around the box.
        var view = dialogueCloseUp
            ? new Vector2(BubbleX + BubbleW / 2 - GameW / 4, Mathf.Clamp(BubbleY + BubbleH / 2 + shot.Lift - GameH / 4, 0, GameH / 2))
            : Vector2.zero;
        void PlaceGame(RectTransform rect, float gx, float gy, float gw, float gh, Vector2 pivot)
        {
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = pivot;
            rect.anchoredPosition = new Vector2((gx - view.x) * unit, (gy - view.y) * unit);
            rect.sizeDelta = new Vector2(gw * unit, gh * unit);
        }

        // The note field, with the receptors where the player's scroll setting puts them.
        int lanes = shot.Field ? Math.Clamp(draft?.Lanes ?? 4, 4, 5) : 0;
        for (int i = 0; i < dialogueLanes.Count; i++)
        {
            bool show = i < lanes;
            if (dialogueLanes[i].gameObject.activeSelf != show) dialogueLanes[i].gameObject.SetActive(show);
            if (show) PlaceGame(dialogueLanes[i].rectTransform, GameW / 2 - lanes * LaneW / 2 + i * LaneW + 1, 0, LaneW - 2, GameH, Vector2.zero);
        }
        dialogueReceptors!.gameObject.SetActive(lanes > 0);
        if (lanes > 0)
            PlaceGame(dialogueReceptors.rectTransform, GameW / 2 - lanes * LaneW / 2, SettingsState.Mode == ScrollMode.Upscroll2D ? ReceptorsUp : ReceptorsDown, lanes * LaneW, 3, Vector2.zero);

        // The portraits: the speaker lit, the others grey, the right side mirrored.
        var notes = new List<string>();
        if (shot.Note.Length > 0) notes.Add(shot.Note);
        int used = 0;
        foreach (var (speaker, faceName, side, slot, lit) in shot.Cast)
        {
            var face = FaceOf(speaker, faceName);
            if (face.Sprite == null || used >= dialoguePortraits.Length)
            {
                if (face.Note.Length > 0 && !notes.Contains(face.Note)) notes.Add(face.Note);
                continue;
            }
            var image = dialoguePortraits[used++];
            if (image.sprite != face.Sprite) image.sprite = face.Sprite;
            float middle = side == DialogueSide.Left ? LeftMiddle : RightMiddle;
            // The first on a side stands outside, the second inside.
            float step = (slot == 0) == (side == DialogueSide.Left) ? -PortraitStep : PortraitStep;
            PlaceGame(image.rectTransform, middle + step + face.OffsetX, PortraitY + face.OffsetY, face.Width, face.Height, new Vector2(0.5f, 0.5f));
            bool mirrored = (side == DialogueSide.Right) != face.Flip;
            image.rectTransform.localScale = new Vector3(mirrored ? -1 : 1, 1, 1);
            image.color = lit ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f);
            if (!image.gameObject.activeSelf) image.gameObject.SetActive(true);
        }
        for (int i = used; i < dialoguePortraits.Length; i++)
            if (dialoguePortraits[i].gameObject.activeSelf) dialoguePortraits[i].gameObject.SetActive(false);

        // The box: the bubble, the name tag (not the Narrator's), and the text as far as it's typed.
        if (!shot.Box) HideDialogueBox();
        else
        {
            dialogueBubble!.gameObject.SetActive(true);
            PlaceGame(dialogueBubble.rectTransform, BubbleX, BubbleY + shot.Lift, BubbleW, BubbleH, Vector2.zero);
            dialogueName!.gameObject.SetActive(!shot.Narration);
            if (!shot.Narration)
            {
                dialogueName.fontSize = BoxNameSize * unit;
                PlaceGame(dialogueName.rectTransform, NameX, NameY + shot.Lift, NameW, NameH, Vector2.zero);
                if (dialogueName.text != shot.Name) dialogueName.text = shot.Name;
            }
            dialogueText!.gameObject.SetActive(true);
            dialogueText.fontSize = BoxTextSize * unit;
            dialogueTextUnit = unit;
            PlaceGame(dialogueText.rectTransform, BodyX, BodyY + shot.Lift, BodyW, BodyH, Vector2.zero);
            if (dialogueText.text != shot.Text) dialogueText.text = shot.Text;
            dialogueText.maxVisibleCharacters = shot.Letters < 0 ? 99999 : shot.Letters;
            dialogueText.color = TooLongForBox(shot.Text) ? Hex(0xF2B02E) : TextColor;
        }
        dialogueNote!.text = Escape(string.Join("\n", notes));
    }

    private static void HideDialogueBox()
    {
        if (dialogueBubble) dialogueBubble!.gameObject.SetActive(false);
        if (dialogueName) dialogueName!.gameObject.SetActive(false);
        if (dialogueText) dialogueText!.gameObject.SetActive(false);
    }

    // The chosen speaker on the Speakers tab; the chosen (or playing) line and its cast otherwise.
    private static Shot PreviewShot()
    {
        var shot = new Shot();
        if (draft == null) return shot;
        if (dialogueTab == DialogueTab.Speakers)
        {
            if (!HasChosenSpeaker)
            {
                shot.Note = "New speaker... adds a speaker of your own.";
                return shot;
            }
            string key = chosenSpeaker!;
            var faces = ChosenFaces();
            shot.Cast.Add((key, chosenFace >= 0 && chosenFace < faces.Count ? faces[chosenFace].Name : null, SpeakerSide(key), 0, true));
            shot.Box = true;
            shot.Name = SpeakerName(key);
            shot.Text = FirstLineOf(key) ?? $"{SpeakerName(key)}'s lines show here.";
            return shot;
        }
        var section = ShownSection;
        var play = dialoguePlay;
        int index = play != null ? play.Lines[play.At] : chosenLine;
        if (index < 0 || index >= draft.LineCount(section) || !draft.IsLine(section, index))
        {
            shot.Note = draft.LineCount(section) == 0 ? "No lines yet. Add line makes one." : "Choose a line to see it here.";
            return shot;
        }
        string speaker = (draft.LineText(section, index, "speaker") ?? "").Trim();
        var who = WhoIs(speaker);
        // As in the battle: a game character the game doesn't have, or a speaker of the battle's
        // own that can't be used, speaks as the Narrator.
        if (!Speaks(speaker))
        {
            shot.Note = who == Who.Custom ? $"{SpeakerName(speaker)} can't be used (see the problems), so the Narrator says this line."
                : $"The game has no character \"{speaker}\", so the Narrator says this line.";
            who = Who.Narrator;
        }
        bool live = section == DialogueSection.During && !draft.LineFlag(section, index, "pause");
        shot.Narration = who == Who.Narrator;
        shot.Field = section == DialogueSection.During;
        shot.Box = play == null || !(live && play.Gone);
        shot.Text = DialogueReader.CleanText(draft.LineText(section, index, "text"), out _);
        if (shot.Text.Length == 0) shot.Note = "This line has no text yet.";
        shot.Name = CleanName(draft.LineText(section, index, "name") ?? "") is { Length: > 0 } own ? own : SpeakerLabel(speaker);
        shot.Letters = play == null ? -1 : Math.Min(shot.Text.Length, (int)(play.Shown * TypingSpeed));
        float lift = shot.Narration ? NarratorLift : 0;
        shot.Lift = live && SettingsState.Mode != ScrollMode.Upscroll2D ? LiveLift : lift;
        if (!shot.Narration) CastOf(section, index, shot.Cast);
        return shot;
    }

    // Whether a speaker shows as themselves: not a game character the game doesn't have, nor a
    // speaker of the battle's own that the loader leaves out (both speak as the Narrator).
    private static bool Speaks(string speaker) => WhoIs(speaker) switch
    {
        Who.Custom => dialogueRead?.FindSpeaker(speaker) != null,
        Who.Game => GameCharacters() == null || FindGame(speaker) != null,
        _ => true,
    };

    private static string? FirstLineOf(string key)
    {
        foreach (DialogueSection section in Enum.GetValues(typeof(DialogueSection)))
            for (int i = 0; i < draft!.LineCount(section); i++)
                if (string.Equals(draft.LineText(section, i, "speaker")?.Trim(), key, StringComparison.OrdinalIgnoreCase)
                    && DialogueReader.CleanText(draft.LineText(section, i, "text"), out _) is { Length: > 0 } text)
                    return text;
        return null;
    }

    // The lines that show together with a line: a whole section before or after the fight; during
    // the song, the stopping lines at its time (a live line shows alone).
    private static List<int> BlockOf(DialogueSection section, int index)
    {
        if (section != DialogueSection.During) return lineRows.ToList();
        if (!draft!.LineFlag(section, index, "pause")) return new List<int> { index };
        double? time = LineTime(index);
        return lineRows.Where(i => draft.IsLine(section, i) && draft.LineFlag(section, i, "pause") && LineTime(i) == time).ToList();
    }

    /// <summary>
    /// Who shows in the box with a line, as the battle works it out: up to two a side from the
    /// line's block, first come first served, each with the face of their last line so far (else
    /// their first). The line's own speaker always shows, lit.
    /// </summary>
    private static void CastOf(DialogueSection section, int index, List<(string Speaker, string? Face, DialogueSide Side, int Slot, bool Lit)> cast)
    {
        var block = BlockOf(section, index);
        int upTo = block.IndexOf(index);
        var sides = new Dictionary<DialogueSide, List<string>> { [DialogueSide.Left] = new(), [DialogueSide.Right] = new() };
        string Id(int i) => (draft!.LineText(section, i, "speaker") ?? "").Trim();
        DialogueSide SideAt(int i) => SideOf(draft!.LineText(section, i, "side")) ?? UsualSide(Id(i));
        bool Talks(int i) => draft!.IsLine(section, i) && WhoIs(Id(i)) != Who.Narrator && Speaks(Id(i));
        foreach (int i in block)
        {
            if (!Talks(i)) continue;
            var list = sides[SideAt(i)];
            if (list.Count < 2 && !list.Contains(Id(i), StringComparer.OrdinalIgnoreCase)) list.Add(Id(i));
        }
        string speaker = Id(index);
        var mine = sides[SideAt(index)];
        if (!mine.Contains(speaker, StringComparer.OrdinalIgnoreCase))
        {
            if (mine.Count >= 2) mine.RemoveAt(0);
            mine.Insert(0, speaker);
        }
        foreach (var (side, list) in sides)
            for (int slot = 0; slot < list.Count; slot++)
            {
                string who = list[slot];
                var said = block.Where(i => Talks(i) && Id(i).Equals(who, StringComparison.OrdinalIgnoreCase)).ToList();
                int last = said.LastOrDefault(i => block.IndexOf(i) <= upTo, -1);
                int line = last >= 0 ? last : said.FirstOrDefault(-1);
                if (who.Equals(speaker, StringComparison.OrdinalIgnoreCase)) line = index;
                string? face = line >= 0 ? draft!.LineText(section, line, "expression")?.Trim() : null;
                cast.Add((who, string.IsNullOrEmpty(face) ? null : face, side, slot, who.Equals(speaker, StringComparison.OrdinalIgnoreCase)));
            }
    }

    // ---- Play -----------------------------------------------------------------------------------

    private sealed class DialoguePlay
    {
        internal DialogueSection Section;
        // The lines it plays (their places as written) and, during the song, when each starts after the first.
        internal readonly List<int> Lines = new();
        internal readonly List<double> Starts = new();
        internal int At;
        // How long the line has been showing, and during the song whether a live line's box has gone.
        internal float Shown;
        internal bool Gone;
        // During the song: seconds since the first line; it stops while a line stops the song.
        internal double Clock;
        // Enter was pressed.
        internal bool Advance;
    }

    private static DialoguePlay? dialoguePlay;

    /// <summary>Plays the section from the chosen line, as the battle does, or stops.</summary>
    private static void ToggleDialoguePlay()
    {
        if (dialoguePlay != null) { StopDialoguePlay(); return; }
        if (draft == null || dialogueTab == DialogueTab.Speakers || !FinishTyping()) return;
        RefreshDialogue();
        var section = ShownSection;
        var play = new DialoguePlay { Section = section };
        linesKept.TryGetValue(section, out var kept);
        for (int r = Math.Max(0, lineRows.IndexOf(chosenLine)); r < lineRows.Count; r++)
        {
            int i = lineRows[r];
            // Only what the battle plays: lines it leaves out (see the problems) are skipped.
            if (kept == null || !kept.Contains(i) || (section == DialogueSection.During && LineTime(i) == null)) continue;
            play.Lines.Add(i);
        }
        if (play.Lines.Count == 0)
        {
            Say("There's nothing to play from here: the battle leaves these lines out (see the problems).", 4f);
            return;
        }
        if (section == DialogueSection.During)
        {
            double first = LineTime(play.Lines[0]) ?? 0;
            foreach (int i in play.Lines) play.Starts.Add((LineTime(i) ?? first) - first);
        }
        dialoguePlay = play;
        chosenLine = play.Lines[0];
        Say(section == DialogueSection.During ? "Playing, with the gaps between the lines. Enter goes on from a line that stops the song. Esc stops." : "Playing. Enter goes on, as in the battle. Esc stops.", 4f);
    }

    private static void StopDialoguePlay() => dialoguePlay = null;

    /// <summary>While Play runs: Enter goes on, Esc and Space stop. True when a key was used.</summary>
    private static bool HandleDialoguePlayKeys(InputKeyboard k)
    {
        if (dialoguePlay == null || page != Page.Dialogue) return false;
        if (Pressed(k, Key.Escape) || Pressed(k, Key.Space)) StopDialoguePlay();
        else if (Pressed(k, Key.Enter) || Pressed(k, Key.NumpadEnter)) dialoguePlay.Advance = true;
        else return false;
        return true;
    }

    // Lines that wait go on with Enter or a click on the preview (the first finishes the typing), or
    // after their time; live lines show for theirs, and the next comes at its own time.
    private static void UpdateDialoguePlay(InputMouse? clicks)
    {
        var play = dialoguePlay;
        if (play == null || draft == null) return;
        bool advance = play.Advance || (clicks != null && clicks.leftButton.wasPressedThisFrame
                                         && RectTransformUtility.RectangleContainsScreenPoint(dialogueBox, clicks.position.ReadValue(), null));
        play.Advance = false;
        var section = play.Section;
        int line = play.Lines[play.At];
        if (!draft.IsLine(section, line)) { StopDialoguePlay(); return; }
        string text = DialogueReader.CleanText(draft.LineText(section, line, "text"), out _);
        double? duration = draft.LineNumber(section, line, "duration") is double d ? Math.Clamp(d, DialogueReader.MinDuration, DialogueReader.MaxDuration) : null;
        float dt = Time.unscaledDeltaTime;
        play.Shown += dt;
        bool typed = play.Shown * TypingSpeed >= text.Length;
        if (section != DialogueSection.During || draft.LineFlag(section, line, "pause"))
        {
            if (duration is double wait ? play.Shown >= wait : advance && typed) NextPlayLine(play);
            else if (advance && !typed && duration == null) play.Shown = text.Length / TypingSpeed;
            return;
        }
        play.Clock += dt;
        if (play.Shown >= (duration ?? DialogueReader.LiveSeconds(text))) play.Gone = true;
        if (play.At + 1 < play.Lines.Count ? play.Clock >= play.Starts[play.At + 1] : play.Gone) NextPlayLine(play);
    }

    private static void NextPlayLine(DialoguePlay play)
    {
        play.At++;
        if (play.At >= play.Lines.Count)
        {
            StopDialoguePlay();
            return;
        }
        play.Shown = 0;
        play.Gone = false;
        if (play.Section == DialogueSection.During) play.Clock = Math.Max(play.Clock, play.Starts[play.At]);
        chosenLine = play.Lines[play.At];
    }

    // ---- the game's characters and faces --------------------------------------------------------

    // One of the game's characters (DataUtility.NpcDatabase), each id once, as the game finds the first.
    private sealed class GameCharacter
    {
        internal string Id = "";
        internal string Name = "";
        /// <summary>Whether it has faces to load (a portrait collection).</summary>
        internal bool Portrait;
        internal CharacterData Data = null!;
    }

    private static List<GameCharacter>? gameCharacters;
    private static float gameCharactersTried = -100f;
    private static readonly Dictionary<string, string> gameNames = new(StringComparer.OrdinalIgnoreCase);
    private static bool reportedGameData;

    /// <summary>The game's characters, or null while the game hasn't loaded them (asked again every few seconds).</summary>
    private static List<GameCharacter>? GameCharacters()
    {
        if (gameCharacters != null) return gameCharacters;
        if (Time.unscaledTime < gameCharactersTried + 3f) return null;
        gameCharactersTried = Time.unscaledTime;
        try
        {
            var database = DataUtility.NpcDatabase;
            var data = database == null ? null : database.Data;
            if (data == null || data.Count == 0) return null;
            var list = new List<GameCharacter>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < data.Count; i++)
            {
                var character = data[i];
                if (!character) continue;
                string id = character.CharacterId ?? "";
                // The battles' own speakers are in the list only while a battle runs.
                if (id.Trim().Length == 0 || id.StartsWith(CustomBattles.RuntimePrefix, StringComparison.Ordinal) || !seen.Add(id)) continue;
                bool portrait;
                try { portrait = character.PortraitCollection != null && character.PortraitCollection.RuntimeKeyIsValid(); }
                catch (Exception) { portrait = false; }
                list.Add(new GameCharacter { Id = id, Name = GameName(id), Portrait = portrait, Data = character });
            }
            gameCharacters = list;
            ModLog.Info($"Battle creator: the game has {list.Count} characters for dialogue, {list.Count(c => c.Portrait)} of them with faces.");
        }
        catch (Exception ex)
        {
            if (!reportedGameData) ModLog.Error("Battle creator: reading the game's characters failed (the dialogue page says they aren't loaded yet): " + ex.Message);
            reportedGameData = true;
        }
        return gameCharacters;
    }

    /// <summary>A game character by its id in any letter case, as the battle finds it; null when the game has none (or hasn't loaded them).</summary>
    private static GameCharacter? FindGame(string? id)
    {
        string wanted = (id ?? "").Trim();
        var all = GameCharacters();
        return all?.FirstOrDefault(c => c.Id == wanted) ?? all?.FirstOrDefault(c => c.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A game character's name as the box shows it: the game's "Names/" translation, else the id tidied ("NPC_Abbot" is "Abbot").</summary>
    private static string GameName(string id)
    {
        id = id.Trim();
        if (gameNames.TryGetValue(id, out var known)) return known;
        string real = gameCharacters?.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Id ?? id;
        string tidy = (real.StartsWith("NPC_", StringComparison.Ordinal) ? real.Substring(4) : real).Replace('_', ' ').Trim();
        string name;
        try { name = Localization.Get("Names/" + real, tidy) ?? tidy; }
        catch (Exception) { name = tidy; }
        if (name.Trim().Length == 0) name = tidy;
        return gameNames[id] = name;
    }

    // A game character's faces, loaded (with PortraitManager) only while something shows them.
    private sealed class GameFaces
    {
        internal PortraitScope? Scope;
        internal float Asked;
        internal List<string>? Names;
        internal List<Sprite>? Sprites;
        internal string? Problem;
    }

    private static readonly Dictionary<string, GameFaces> gameFaces = new(StringComparer.Ordinal);
    // The characters whose faces were asked for since the last look; the others' are let go.
    private static readonly HashSet<string> facesWanted = new(StringComparer.Ordinal);
    // Every face name seen for a character this session, for the page's checks.
    private static readonly Dictionary<string, List<string>> knownFaceNames = new(StringComparer.Ordinal);
    private static bool reportedFaces;

    /// <summary>A game character's faces (by its id as the game has it): asked for now, and ready once <see cref="GameFaces.Names"/> is set.</summary>
    private static GameFaces FacesOf(string id)
    {
        facesWanted.Add(id);
        if (!gameFaces.TryGetValue(id, out var set))
        {
            gameFaces[id] = set = new GameFaces { Asked = Time.unscaledTime };
            try { set.Scope = PortraitManager.AcquireScope(id); }
            catch (Exception ex)
            {
                set.Problem = "its faces can't be loaded here";
                ReportFaces(ex);
            }
        }
        if (set.Names != null || set.Problem != null || set.Scope == null) return set;
        try
        {
            if (!PortraitManager.IsReady(id))
            {
                if (Time.unscaledTime > set.Asked + 10f) set.Problem = "its faces didn't load";
                return set;
            }
            var character = FindGame(id)?.Data;
            if (character == null || !character) { set.Problem = "the game has no such character"; return set; }
            var list = PortraitManager.GetExpressions(character);
            var names = new List<string>();
            var sprites = new List<Sprite>();
            for (int i = 0; list != null && i < list.Count; i++)
            {
                var portrait = list[i];
                if (portrait == null || !portrait.Face) continue;
                names.Add(portrait.Emotion.ToString());
                sprites.Add(portrait.Face);
            }
            set.Names = names;
            set.Sprites = sprites;
            knownFaceNames[id] = names;
        }
        catch (Exception ex)
        {
            set.Problem = "its faces can't be loaded here";
            ReportFaces(ex);
        }
        return set;
    }

    private static void ReportFaces(Exception ex)
    {
        if (!reportedFaces) ModLog.Error("Battle creator: loading the game's faces failed (the preview shows them without pictures): " + ex.Message);
        reportedFaces = true;
    }

    /// <summary>Lets go of the faces nothing asked for since the last look.</summary>
    private static void ReleaseUnwantedFaces()
    {
        foreach (var id in gameFaces.Keys.Where(id => !facesWanted.Contains(id)).ToList()) ReleaseFaces(id);
        facesWanted.Clear();
    }

    private static void ReleaseAllFaces()
    {
        foreach (var id in gameFaces.Keys.ToList()) ReleaseFaces(id);
        facesWanted.Clear();
    }

    private static void ReleaseFaces(string id)
    {
        if (!gameFaces.Remove(id, out var set)) return;
        try { set.Scope?.Dispose(); }
        catch (Exception ex) { ReportFaces(ex); }
    }

    // ---- faces as the box shows them ------------------------------------------------------------

    private sealed class FaceShown
    {
        internal Sprite? Sprite;
        // In game pixels.
        internal float Width, Height, OffsetX, OffsetY;
        internal bool Flip;
        // Why there's no picture, or what's happening.
        internal string Note = "";
    }

    /// <summary>
    /// A speaker's face as the box shows it: <paramref name="name"/> (null or "" for the usual
    /// one), its size in game pixels, and its nudge and mirror. No picture for the Narrator, a
    /// character the game doesn't have, or one whose faces are still loading (the note says which).
    /// </summary>
    private static FaceShown FaceOf(string speaker, string? name)
    {
        var shown = new FaceShown();
        switch (WhoIs(speaker))
        {
            case Who.Narrator:
                shown.Note = "The Narrator has no picture.";
                return shown;
            case Who.Custom:
                var own = dialogueRead?.FindSpeaker(speaker);
                if (own == null) { shown.Note = $"{SpeakerName(speaker.Trim())} can't be used yet (see the problems)."; return shown; }
                if (own.Portrait == null) { shown.Note = $"{own.Name} has no picture that can be shown (see the problems)."; return shown; }
                var face = string.IsNullOrEmpty(name) ? own.Portrait : own.Expression(name) ?? own.Portrait;
                // Every face shows at the main picture's size, as in the battle.
                var (w, h, k, sharp) = DialogueReader.ShownSize(own.Portrait.Width, own.Portrait.Height);
                var decoded = Decoded(face.File, k < 1 ? (int)Math.Ceiling(Math.Max(w, h)) : 0, sharp);
                shown.Sprite = decoded.Sprite;
                shown.Note = decoded.Problem ?? "";
                shown.Width = (float)w;
                shown.Height = (float)h;
                shown.OffsetX = (float)own.OffsetX;
                shown.OffsetY = (float)own.OffsetY;
                shown.Flip = own.Flip;
                return shown;
        }
        if (GameCharacters() == null) { shown.Note = "The game's characters aren't loaded yet. Try again in a moment."; return shown; }
        if (FindGame(speaker) is not { } character) { shown.Note = $"The game has no character \"{speaker.Trim()}\"."; return shown; }
        var set = FacesOf(character.Id);
        if (set.Names == null) { shown.Note = set.Problem != null ? $"{character.Name}: {set.Problem}." : $"Loading {character.Name}'s faces..."; return shown; }
        if (set.Names.Count == 0) { shown.Note = $"{character.Name} has no faces, so the box shows no picture."; return shown; }
        int at = string.IsNullOrEmpty(name) ? -1 : set.Names.FindIndex(n => n.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (at < 0) at = Math.Max(0, set.Names.FindIndex(n => n.Equals("Neutral1", StringComparison.OrdinalIgnoreCase)));
        var sprite = set.Sprites![at];
        if (!sprite) return shown;
        // The box sizes a game portrait to its sprite (1 pixel a unit in the game's canvas).
        float ppu = sprite.pixelsPerUnit > 0 ? sprite.pixelsPerUnit : 1f;
        shown.Sprite = sprite;
        shown.Width = sprite.rect.width / ppu;
        shown.Height = sprite.rect.height / ppu;
        try
        {
            if (character.Data)
            {
                shown.OffsetX = character.Data.portraitOffset.x;
                shown.OffsetY = character.Data.portraitOffset.y;
                shown.Flip = character.Data.flipPortrait;
            }
        }
        catch (Exception ex) { ReportFaces(ex); }
        return shown;
    }

    private static Sprite? FaceSprite(string speaker, string? name) => FaceOf(speaker, name).Sprite;

    /// <summary>The line under a picker's picture: what the face is, or why there's none.</summary>
    private static string FaceNote(string speaker)
    {
        var face = FaceOf(speaker, null);
        if (face.Sprite == null) return face.Note;
        if (WhoIs(speaker) == Who.Custom) return $"{SpeakerName(speaker.Trim())}\n{Num(Math.Round(face.Width))} x {Num(Math.Round(face.Height))} in the battle";
        var character = FindGame(speaker);
        int count = character != null && gameFaces.TryGetValue(character.Id, out var set) ? set.Names?.Count ?? 0 : 0;
        return $"{character?.Name ?? speaker}\n{count} face{(count == 1 ? "" : "s")}";
    }

    // A speaker of the battle's own's pictures, decoded for the preview (smaller when the battle
    // shows them smaller) and kept by file until the page closes.
    private sealed class DecodedFace
    {
        internal Texture2D? Texture;
        internal Sprite? Sprite;
        internal string? Problem;
    }

    private static readonly Dictionary<string, DecodedFace> decodedFaces = new(StringComparer.OrdinalIgnoreCase);

    private static DecodedFace Decoded(string file, int keepSide, bool sharp)
    {
        var files = PackageFiles.Folder(draft!.Folder);
        string key = $"{file}|{files.Stamp(file)}|{keepSide}|{sharp}";
        if (decodedFaces.TryGetValue(key, out var known)) return known;
        if (decodedFaces.Count >= 48) ClearDecodedFaces();
        var face = new DecodedFace();
        try
        {
            var texture = CustomBattles.CardImages.Decode(files.ReadAllBytes(file, DialogueReader.MaxPortraitBytes), "NocturneButBetter/creator/portrait", keepSide, out _, out string? why);
            if (texture == null) face.Problem = $"{file} {why}.";
            else
            {
                // Pixel art stays sharp at a whole number of game pixels, as in the battle.
                texture.filterMode = sharp ? FilterMode.Point : FilterMode.Bilinear;
                face.Texture = texture;
                face.Sprite = CustomBattles.CardImages.ToSprite(texture);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            face.Problem = $"{file} couldn't be read ({ex.Message}).";
        }
        decodedFaces[key] = face;
        return face;
    }

    private static void ClearDecodedFaces()
    {
        foreach (var image in dialoguePortraits)
            if (image) image.sprite = null;
        if (pickerFace) pickerFace!.sprite = null;
        foreach (var face in decodedFaces.Values)
        {
            if (face.Sprite != null && face.Sprite) Object.Destroy(face.Sprite);
            if (face.Texture != null && face.Texture) Object.Destroy(face.Texture);
        }
        decodedFaces.Clear();
    }

    // ---- the game's fonts, and whether a line fits ----------------------------------------------

    private static TMP_FontAsset? boxFont, nameFont;
    private static bool fontsLooked;

    // "Brief 9" for the box's text and "Match 7" for its names, looked for once (the menu font
    // otherwise), and put on the preview's texts once they're built.
    private static void FindGameFonts()
    {
        if (!fontsLooked)
        {
            fontsLooked = true;
            try
            {
                foreach (var font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                {
                    if (!font) continue;
                    if (boxFont == null && font.name.StartsWith("Brief 9", StringComparison.OrdinalIgnoreCase)) boxFont = font;
                    if (nameFont == null && font.name.StartsWith("Match 7", StringComparison.OrdinalIgnoreCase)) nameFont = font;
                }
                if (boxFont == null || nameFont == null) ModLog.Info("Battle creator: the game's dialogue fonts weren't found, so the preview uses the menu font.");
            }
            catch (Exception ex) { ModLog.Error("Battle creator: looking for the game's dialogue fonts failed: " + ex.Message); }
        }
        if (boxFont != null && boxFont && dialogueText && dialogueText!.font != boxFont)
        {
            dialogueText.font = boxFont;
            dialogueText.fontSharedMaterial = boxFont.material;
        }
        if (nameFont != null && nameFont && dialogueName && dialogueName!.font != nameFont)
        {
            dialogueName.font = nameFont;
            dialogueName.fontSharedMaterial = nameFont.material;
        }
    }

    /// <summary>Whether a line's text runs past the box's 144 x 39 game pixels in the game's font.</summary>
    private static bool TooLongForBox(string text)
    {
        if (!dialogueText || string.IsNullOrEmpty(text)) return false;
        try
        {
            FindGameFonts();
            float unit = dialogueTextUnit;
            dialogueText!.fontSize = BoxTextSize * unit;
            return dialogueText.GetPreferredValues(DialogueReader.CleanText(text, out _), BodyW * unit, 0).y > BodyH * unit + 0.5f;
        }
        catch (Exception ex)
        {
            // Measuring is only a hint: without it, nothing is said.
            if (!reportedMeasure) ModLog.Error("Battle creator: measuring a line for the dialogue box failed: " + ex.Message);
            reportedMeasure = true;
            return false;
        }
    }

    private static bool reportedMeasure;

    // The page's own checks of the shown section: game characters (and faces) the game doesn't
    // have, and text too long for the box. Worked out again only when something they use changed.
    private static string ownChecksKey = "";
    private static readonly List<string> ownChecks = new();

    private static List<string> OwnChecks()
    {
        if (draft == null || dialogueRead == null || dialogueTab == DialogueTab.Speakers) return new List<string>();
        var section = ShownSection;
        string key = $"{dialogueReadAt}|{section}|{knownFaceNames.Count}|{gameCharacters != null}|{boxFont != null}|{dialogueTextUnit}";
        if (key == ownChecksKey) return ownChecks;
        ownChecksKey = key;
        ownChecks.Clear();
        foreach (var line in dialogueRead.Lines(section))
        {
            string where = DialogueReader.LineName(section, line.Index, section == DialogueSection.During ? line.Time : null);
            if (line.SpeakerKind == DialogueSpeakerKind.Game && gameCharacters != null)
            {
                if (FindGame(line.Speaker) is not { } character) ownChecks.Add($"{where}: the game has no character \"{line.Speaker}\", so the Narrator says it");
                else if (line.Face != null && knownFaceNames.TryGetValue(character.Id, out var names) && !names.Contains(line.Face, StringComparer.OrdinalIgnoreCase))
                    ownChecks.Add($"{where}: {character.Name} has no expression \"{line.Face}\", so the usual face shows");
            }
            if (TooLongForBox(line.Text)) ownChecks.Add($"{where} is too long for the box, so its end may not show");
        }
        return ownChecks;
    }
}
