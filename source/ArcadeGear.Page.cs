using UnityEngine;
using static NocturneFlatScroll.EditorUi;
using EventSystem = UnityEngine.EventSystems.EventSystem;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;

namespace NocturneFlatScroll;

/// <summary>
/// The Arcade gear page: a full-screen list like the battle creator's, over the main-menu arcade.
/// It has a row for each slot with what the next battle uses there, a picker for each slot, and a
/// way back to the story gear. It works with the mouse, the keyboard (Enter or Z chooses, Esc or X
/// goes back) and a pad (A chooses, B goes back). The arcade can't be reached under it:
/// EditorOverlay locks the arcade's navigation and Back, the page's canvas takes every pointer
/// event, and the arcade's difficulty tabs, which read their keys themselves, are off until it
/// closes. The arcade never closes meanwhile, so it's on the same chapter, tab and card afterwards.
/// </summary>
internal static partial class ArcadeGear
{
    private enum Screen { Slots, Pick, Confirm }

    private enum RowKind { Slot, Adopt, Clear, Done }

    // Names the page to EditorOverlay, which keeps the arcade locked while it is open.
    private static readonly object OverlayOwner = "arcade gear";

    private static EditorUi? ui;
    // The page kit, built with the screen: keys and pad buttons on the frame a screen opens belong
    // to what opened it, and clicks wait a moment after the screen changes, as in the battle creator.
    private static EditorPageKit? kit;
    private static EditorPageKit Kit => kit ?? throw new InvalidOperationException("the arcade gear page isn't open");
    private static Screen screen;
    private static int slotIndex, pickIndex, confirmIndex;
    private static GearSlot pickSlot;
    private static bool pickHasItems;
    private static List<PickRow> picks = new();
    // The slots as the next battle fills them; worked out on open and after each change.
    private static SlotGear[] slots = Array.Empty<SlotGear>();
    private static int ownLevel, ownHealth;
    // Why gear can't be listed now, or null.
    private static string? pageProblem;
    private static TMP_Text? note;
    private static GameObject? selectedBefore;
    private static TabbedButtonBar? pausedTabs;

    /// <summary>Whether the page is open.</summary>
    internal static bool PageOpen => ui != null && ui.IsAlive;

    // ---- opening and closing ----------------------------------------------------------------------

    private static void OpenPage()
    {
        if (ui != null || !session || !ArcadeSession.Active || ArcadeUtility.IsRunning || EditorOverlay.IsOpen || TestPlay.Active) return;
        if (menu == null || !menu || !menu.Active || !menu.IsFocused) return;
        try
        {
            ui = new EditorUi("NocturneButBetter Arcade Gear", blockGameClicks: true);
            kit = new EditorPageKit(ui, "Arcade gear");
            ui.BuildList();
            BuildBack();
            BuildNote();
            EditorOverlay.Enter(OverlayOwner);
            selectedBefore = selectedNow ?? selectedLastFrame;
            PauseTabs();
            ReadRows();
            slotIndex = 0;
            ShowScreen(Screen.Slots);
            string? message = readProblem != null
                ? $"{FileName} couldn't be read (see the log), so the arcade uses your story gear. Changing a slot starts a new file; the old one is kept as {FileName}.bad."
                : ReadOnly ? $"{FileName} was made by a newer version of the mod, so it can't be changed here, and the arcade uses your story gear."
                : EarlierGame ? EarlierText()
                : SettingsState.ArcadeGearAllItems && !AllItemsAvailable
                ? "All items is on, but it can't work: a game hook couldn't be installed (see the log). Only items this save owns are listed."
                : null;
            if (message != null) ui.Say(message, 12f);
            ModLog.Info($"Arcade gear: opened (save slot {saveSlot}).");
        }
        catch (Exception ex)
        {
            ModLog.Error("Arcade gear: opening the page failed: " + ex);
            ClosePage("it failed to open");
        }
    }

    // Every way out comes here: the keys and buttons, the end of the visit, the checks each frame, and errors.
    private static void ClosePage(string reason)
    {
        if (ui == null) return;
        ui.Destroy();
        ui = null;
        kit = null;
        note = null;
        picks = new List<PickRow>();
        // Gives the cursor back; the arcade's input comes back once the key that closed the page is let go.
        EditorOverlay.Leave(OverlayOwner);
        ResumeTabs();
        Reselect();
        selectedBefore = null;
        closedAt = Time.unscaledTime;
        if (!ArcadeUtility.IsRunning)
        {
            Refresh();
            RefreshArcadeBox();
        }
        ModLog.Info($"Arcade gear: closed ({reason}).");
    }

    // The arcade's difficulty tabs read their keys themselves (Left Ctrl, Left Shift, LB, RB), past
    // the menu lock; their only reader is their Update, so they're switched off while the page is open.
    private static void PauseTabs()
    {
        pausedTabs = null;
        try
        {
            var tabs = menu != null && menu ? menu.difficultyTabBar : null;
            if (tabs == null || !tabs || !tabs.enabled) return;
            tabs.enabled = false;
            pausedTabs = tabs;
        }
        catch (Exception ex) { Note("Arcade gear: the arcade's difficulty tabs couldn't be paused while the page is open: " + ex.Message); }
    }

    // Only what PauseTabs switched off is switched back on.
    private static void ResumeTabs()
    {
        var tabs = pausedTabs;
        pausedTabs = null;
        try { if (tabs != null && tabs) tabs.enabled = true; }
        catch (Exception ex) { Note("Arcade gear: the arcade's difficulty tabs couldn't be switched back on: " + ex.Message); }
    }

    // A click on the page can leave the arcade with no card selected; the one it had is selected again.
    private static void Reselect()
    {
        try
        {
            var before = selectedBefore;
            var events = EventSystem.current;
            if (before == null || !before || !before.activeInHierarchy || events == null || !events) return;
            if (menu == null || !menu || !menu.Active || !menu.IsFocused) return;
            var now = events.currentSelectedGameObject;
            if (now == null || !now) events.SetSelectedGameObject(before);
        }
        catch (Exception ex) { Note("Arcade gear: the arcade's card couldn't be selected again: " + ex.Message); }
    }

    // The box on the right says when the arcade gear keeps scores out; the game shows the selected
    // card's score again (with no card selected it would hide the score view, so it's left alone then).
    private static void RefreshArcadeBox()
    {
        try
        {
            if (menu == null || !menu || !menu.Active) return;
            var card = menu.selectedSongGroup;
            if (card != null && card && menu.selectedMelodyIndex >= 0) menu.RefreshHighScore();
        }
        catch (Exception ex) { Note("Arcade gear: the arcade's score box couldn't be shown again: " + ex.Message); }
    }

    private static void ShowScreen(Screen next)
    {
        screen = next;
        Kit.IgnoreKeysNow();
        Kit.DelayClicks();
    }

    // Worked out on open and after each change.
    private static void ReadRows()
    {
        pageProblem = null;
        slots = Array.Empty<SlotGear>();
        if (GearCatalog.All.Count == 0)
        {
            pageProblem = "The game's items aren't loaded, so gear can't be listed now.";
            return;
        }
        var own = BattleGear.OwnPlayerData(out string? why);
        if (own == null)
        {
            pageProblem = $"Your save can't be read now ({why}), so gear can't be listed.";
            return;
        }
        slots = Resolve(own);
        ownHealth = Counts(own).TryGetValue(BattleGear.ExtraHealthId, out int health) ? health : 0;
        ownLevel = BattleGear.PlayerLevel() ?? 0;
    }

    // ---- per frame -----------------------------------------------------------------------------------

    private static void UpdatePage()
    {
        EditorOverlay.Update();
        if (ui == null) return;
        if (!ui.IsAlive)
        {
            ModLog.Error("Arcade gear: the page's screen was destroyed from outside; closing it.");
            ClosePage("its screen was destroyed");
            return;
        }
        // The page belongs to the arcade: it goes when the arcade can't take input any more.
        string? gone = !session || !ArcadeSession.Active ? "the arcade session ended"
            : ArcadeUtility.IsRunning ? "a song started"
            : menu == null || !menu || !menu.Active || !menu.IsFocused ? "the arcade isn't taking input"
            : null;
        if (gone != null)
        {
            ClosePage(gone);
            return;
        }
        var keyboard = InputKeyboard.current;
        if (keyboard == null) return;
        var mouse = InputMouse.current;
        switch (screen)
        {
            case Screen.Slots: UpdateSlots(keyboard, mouse); break;
            case Screen.Pick: UpdatePick(keyboard, mouse); break;
            case Screen.Confirm: UpdateConfirm(keyboard, mouse); break;
        }
    }

    // What a choice or change says in the hint line, where the row's hint would go.
    private static void Say(string text, float seconds = 6f) => ui?.Say(text, seconds);

    private static string ChooseKey => PadInput.InUse ? "A" : "Enter";

    // ---- the slots -------------------------------------------------------------------------------------

    private static List<(RowKind Kind, GearSlot Slot)> SlotRows()
    {
        var rows = new List<(RowKind, GearSlot)>();
        if (pageProblem == null)
        {
            foreach (var g in slots) rows.Add((RowKind.Slot, g.Slot));
            if (EarlierGame) rows.Add((RowKind.Adopt, default));
            rows.Add((RowKind.Clear, default));
        }
        rows.Add((RowKind.Done, default));
        return rows;
    }

    private static List<string> SlotLines(List<(RowKind Kind, GearSlot Slot)> rows, out List<string?> tags)
    {
        var lines = new List<string>();
        tags = new List<string?>();
        foreach (var row in rows)
        {
            var gear = row.Kind == RowKind.Slot ? slots.FirstOrDefault(g => g.Slot == row.Slot) : null;
            lines.Add(row.Kind switch
            {
                RowKind.Slot => gear != null ? ArcadeGearRules.RowText(gear) : GearCatalog.LabelOf(row.Slot),
                RowKind.Adopt => "Use the gear I set before",
                RowKind.Clear => "Use my story gear in every slot",
                _ => "Done"
            });
            tags.Add(gear != null ? ArcadeGearRules.Tag(gear) : null);
        }
        return lines;
    }

    private static void UpdateSlots(InputKeyboard keyboard, InputMouse? mouse)
    {
        if (Kit.BackPressed(keyboard, mouse))
        {
            ClosePage("Done");
            return;
        }
        var rows = SlotRows();
        slotIndex = Math.Clamp(slotIndex, 0, rows.Count - 1);
        int before = slotIndex;
        if (Kit.MoveAndChoose(keyboard, rows.Count, ref slotIndex))
        {
            ChooseSlotRow(rows[slotIndex]);
            if (!PageOpen || screen != Screen.Slots) return;
            rows = SlotRows();
            slotIndex = Math.Clamp(slotIndex, 0, rows.Count - 1);
        }
        // A message shows where the hint goes; moving to another row brings back that row's hint.
        else if (slotIndex != before && ui!.MessageShowing) ui.ClearMessage();
        var lines = SlotLines(rows, out var tags);
        string hint = ui!.MessageShowing ? ui.Message : SlotRowHint(rows[slotIndex]);
        DrawNote(rows.Count);
        ui.DrawList($"Arcade gear (save slot {saveSlot})", Escape(hint), lines, slotIndex, tags);
    }

    private static string SlotRowHint((RowKind Kind, GearSlot Slot) row)
    {
        switch (row.Kind)
        {
            case RowKind.Slot:
                // The key that changes a slot is in the note, so an item's description fits the hint's two lines.
                var gear = slots.FirstOrDefault(g => g.Slot == row.Slot);
                return gear != null ? ArcadeGearRules.SlotHint(gear, GearCatalog.Find) : "";
            case RowKind.Adopt:
                return "Keeps the gear saved for this save slot and uses it from now on.";
            case RowKind.Clear:
                return "Every slot goes back to what your story save has equipped.";
            default:
                return pageProblem ?? "Back to the arcade.";
        }
    }

    private static void ChooseSlotRow((RowKind Kind, GearSlot Slot) row)
    {
        string? message;
        switch (row.Kind)
        {
            case RowKind.Slot:
                if (ReadOnly) Say($"{FileName} was made by a newer version of the mod, so it can't be changed here.");
                else OpenPicker(row.Slot);
                return;
            case RowKind.Adopt:
                if (Adopt(out message)) ReadRows();
                Say(message ?? "The gear you set before is back, for this game from now on.");
                return;
            case RowKind.Clear:
                confirmIndex = 0;
                ShowScreen(Screen.Confirm);
                return;
            default:
                ClosePage("Done");
                return;
        }
    }

    private static string EarlierText()
    {
        double since = Entry.Since ?? 0;
        return $"This save slot's arcade gear is from an earlier game ({Clock(since)} played then, {Clock(playTime)} now), so it isn't used. " +
               "Change a slot to start over, or pick Use the gear I set before.";
    }

    private static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 1e9));
        return $"{(int)time.TotalHours}:{time.Minutes:00}";
    }

    // ---- a slot's picker ----------------------------------------------------------------------------

    private static void OpenPicker(GearSlot slot)
    {
        var own = BattleGear.OwnPlayerData(out string? why);
        if (own == null)
        {
            Say($"Your save can't be read now ({why}).");
            return;
        }
        var counts = Counts(own);
        var items = GearCatalog.ForSlot(slot, includeDebug: true);
        Choices.TryGetValue(slot, out string? choice);
        pickSlot = slot;
        pickHasItems = items.Any(i => AllItems || ArcadeGearRules.Owned(visitStart, i.Id));
        picks = ArcadeGearRules.Picks(slot, items, choice, StoryItem(own.equipmentData, slot), visitStart, id => LeftThisVisit(counts, id),
                                      GearCatalog.Find, AllItems);
        // It opens on the current choice.
        pickIndex = Math.Max(0, picks.FindIndex(p => p.Current));
        ShowScreen(Screen.Pick);
    }

    private static void UpdatePick(InputKeyboard keyboard, InputMouse? mouse)
    {
        if (Kit.BackPressed(keyboard, mouse) || picks.Count == 0)
        {
            ShowScreen(Screen.Slots);
            return;
        }
        int before = pickIndex;
        if (Kit.MoveAndChoose(keyboard, picks.Count, ref pickIndex))
        {
            ChoosePick(picks[pickIndex]);
            return;
        }
        if (pickIndex != before && ui!.MessageShowing) ui.ClearMessage();
        string hint = ui!.MessageShowing ? ui.Message : PickHint(picks[pickIndex]);
        DrawNote(0);
        ui.DrawList($"{GearCatalog.LabelOf(pickSlot)} for the arcade (save slot {saveSlot})", Escape(hint),
                    picks.Select(p => p.Text).ToList(), pickIndex, picks.Select(p => p.Tag).ToList());
    }

    // The current choice changes nothing (so an earlier game's choices aren't started over just by looking);
    // anything else is written at once.
    private static void ChoosePick(PickRow row)
    {
        string? message = null;
        if (!row.Current && Pick(pickSlot, row.Value, out message)) ReadRows();
        ShowScreen(Screen.Slots);
        if (message != null) Say(message, 8f);
    }

    private static string PickHint(PickRow row)
    {
        string none = pickHasItems ? "" : $"  This save has no {ArcadeGearRules.ItemsOf(pickSlot)} yet.";
        if (row.Value == null) return "Uses whatever your story has in this slot, now and later." + none;
        if (row.Value.Length == 0) return "Nothing in this slot." + none;
        var item = row.Item;
        if (item == null) return $"The game has no item \"{row.Value}\", so your story gear is used. Your choice is kept until you pick another.";
        if (item.Slot != pickSlot) return $"{item.Name} doesn't go in the {GearCatalog.LabelOf(pickSlot)} slot, so your story gear is used.";
        if (row.Unowned && !AllItems) return ArcadeGearRules.NotOwnedHint(item.Name, "so your story gear is used");
        // An item the save doesn't own is tagged so; the hint stays the item's own, to fit its two lines.
        string hint = GearCatalog.Hint(item);
        if (row.UsedUp) hint += "  None left this visit. It's full again the next time you open the arcade.";
        return hint;
    }

    // ---- "Use my story gear in every slot" -------------------------------------------------------------

    private static void UpdateConfirm(InputKeyboard keyboard, InputMouse? mouse)
    {
        if (Kit.BackPressed(keyboard, mouse))
        {
            ShowScreen(Screen.Slots);
            return;
        }
        var rows = new List<string> { "Keep my arcade gear", "Use my story gear" };
        if (Kit.MoveAndChoose(keyboard, rows.Count, ref confirmIndex))
        {
            string? message = null;
            if (confirmIndex == 1 && ClearAll(out message))
            {
                ReadRows();
                message ??= "Every slot uses your story gear again.";
            }
            ShowScreen(Screen.Slots);
            if (message != null) Say(message);
            return;
        }
        DrawNote(0);
        ui!.DrawList("Use your story gear in every slot?",
                     Escape("Every slot goes back to what your story save has equipped. Your story save doesn't change."), rows, confirmIndex);
    }

    // ---- the button and the note ------------------------------------------------------------------------

    // The mouse's way back: Done on the slots, Back on a picker or the prompt. Esc, X and B do the same.
    private static void BuildBack()
    {
        var b = ui!.MakeButton(ui.ListPanel!, "", () =>
        {
            if (!Kit.Live || !Kit.ClicksLive) return;
            if (screen == Screen.Slots) ClosePage("Done");
            else ShowScreen(Screen.Slots);
        });
        b.Text = () => (screen == Screen.Slots ? "Done" : "Back") + $" <size=65%><color=#9D92B4>{(PadInput.InUse ? "B" : "Esc/X")}</color></size>";
        Place(b.Rect, new Vector2(1, 0), new Vector2(-16, 20), new Vector2(170, 52), new Vector2(1, 0));
    }

    // Under the slots: the key that changes one, the level and health upgrades (the save's own), and the rules.
    private static void BuildNote()
    {
        note = MakeText("Note", ui!.ListPanel!, 20, TextAlignmentOptions.Top);
        note.color = DimText;
        note.enableWordWrapping = true;
        Place(note.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1100, 170), new Vector2(0.5f, 1));
        note.gameObject.SetActive(false);
    }

    private static void DrawNote(int rows)
    {
        if (note == null || !note) return;
        bool show = screen == Screen.Slots && pageProblem == null && rows > 0 && rows <= ListRowsVisible;
        if (note.gameObject.activeSelf != show) note.gameObject.SetActive(show);
        if (!show) return;
        // The list's box is 900 high around the middle; its rows start 150 down and are 45 apart.
        note.rectTransform.anchoredPosition = new Vector2(0, 450 - 150 - rows * 45 - 25);
        note.text = NoteText();
    }

    private static string NoteText()
    {
        string own = (ownLevel > 0 ? $"Level {ownLevel} and {ownHealth}" : ownHealth.ToString()) +
                     $" health upgrade{(ownHealth == 1 ? "" : "s")}, from your save. ";
        var lines = new List<string>
        {
            ReadOnly ? $"{FileName} is from a newer version of the mod, so the slots can't be changed." : $"{ChooseKey} changes a slot.",
            own + (AllItems ? "All items is on (Options > Gameplay): every item is listed."
                : SettingsState.ArcadeGearAllItems ? "Only items this save owns are listed: all items can't work (see the log)."
                : "Only items this save owns are listed."),
            "Your story save never changes: this gear is used only in this arcade."
        };
        if (AllItems) lines.Add("All items: in a battle with an item this save doesn't own, scores aren't saved and achievements don't count.");
        return string.Join("\n", lines);
    }

    // ---- for the QA drivers: reads only --------------------------------------------------------------

    /// <summary>Whether the page is open.</summary>
    internal static bool QaPageOpen => PageOpen;

    /// <summary>The page's screen: slots, pick or confirm; "closed" when it isn't open.</summary>
    internal static string QaScreen => PageOpen ? screen.ToString().ToLowerInvariant() : "closed";

    /// <summary>The slot rows as the page shows them, each with its tag after " | ".</summary>
    internal static List<string> QaRows
    {
        get
        {
            var lines = SlotLines(SlotRows(), out var tags);
            return lines.Select((line, i) => tags[i] is string tag ? $"{line} | {tag}" : line).ToList();
        }
    }
}
