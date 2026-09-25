namespace NocturneFlatScroll;

// A new battle from an osu!mania beatmap (beta, not recommended): the .osz is read on a worker
// (read-only, bounded, nothing written), then a summary shows what the battle would be. The player
// can change the slots, the lanes, whose speed changes it gets and the 5-lane player attacks, and
// Make builds it (BattleFiles.CreateFromOsz) and opens it like any other battle. The summary's
// texts are OszSummary's; this is only the screens and what choosing a row does.
internal static partial class BattleCreator
{
    // The .osz read and what the player picked; null when no import is showing.
    private static OszPlan? osz;
    private static OszChoices? oszChoices;

    private static void StartOszImport() =>
        Run(FileDialogs.Open(FileDialogs.Purpose.OsuBeatmaps, OszSummary.DialogTitle), "Choose a .osz file in the window that opened...", OszChosen);

    /// <summary>The .osz picked, or null when the dialog was cancelled. QA calls this with a path instead of the dialog.</summary>
    private static void OszChosen(string? path)
    {
        if (path == null) return;
        ModLog.Info("Battle creator: " + OszSummary.ReadingLine(path));
        Run(Task.Run(() => OszImport.Read(path)), "Reading the beatmap...", plan =>
        {
            if (plan.Error != null)
            {
                string line = "Battle creator: " + OszSummary.RefusedLine(path, plan);
                if (plan.Bug != null) ModLog.Error(line + "\n" + plan.Bug);
                else ModLog.Info(line);
                Say("Can't import that beatmap: " + plan.Error, 9f);
                return;
            }
            osz = plan;
            oszChoices = OszChoices.Default(plan, plan.DefaultLanes);
            foreach (var line in OszSummary.LogLines(plan)) ModLog.Info("Battle creator: " + line);
            ShowOszSummary(OszRow.Make, null);
        });
    }

    private static void ClearOsz()
    {
        osz = null;
        oszChoices = null;
    }

    // ---- the summary ------------------------------------------------------------------------------

    /// <summary>Shows the summary as the plan and choices are now, with the cursor on the row that shows <paramref name="kind"/> (and <paramref name="about"/>).</summary>
    private static void ShowOszSummary(OszRow kind, OszDifficulty? about)
    {
        var plan = osz;
        var choices = oszChoices;
        if (plan == null || choices == null) { CancelOsz(); return; }
        var rows = OszSummary.Rows(plan, choices);
        // The row again, or the difficulty's new row (a slot change moves it between "Elite <-" and "Left out <-").
        int index = rows.FindIndex(r => r.Kind == kind && r.Difficulty == about);
        if (index < 0 && about != null) index = rows.FindIndex(r => r.Difficulty == about);
        if (index < 0) index = rows.FindIndex(r => r.Kind == kind);
        ShowPicker(new Picker
        {
            Heading = OszSummary.Heading,
            Rows = rows.Select(r => r.Text).ToList(),
            Hint = i => i >= 0 && i < rows.Count ? rows[i].Hint : "",
            Index = Math.Max(0, index),
            Choose = i => { if (i >= 0 && i < rows.Count) ChooseOszRow(rows[i]); },
            Back = CancelOsz,
        });
    }

    private static void ChooseOszRow(OszSummaryRow row)
    {
        var plan = osz;
        var choices = oszChoices;
        if (plan == null || choices == null) { CancelOsz(); return; }
        switch (row.Kind)
        {
            case OszRow.Make: MakeOszBattle(); return;
            case OszRow.Cancel: CancelOsz(); return;
            case OszRow.Lanes when row.Say == null: ShowOszLanes(plan); return;
            case OszRow.Slot or OszRow.Excluded when row.Difficulty != null: ShowOszSlot(row.Difficulty); return;
            case OszRow.Speed when row.Say == null: ShowOszSpeeds(plan, choices); return;
            case OszRow.Details: ShowOszDetails(plan, choices); return;
            case OszRow.Attacks:
                choices.PlayerAttacks = !choices.PlayerAttacks;
                ShowOszSummary(OszRow.Attacks, null);
                return;
        }
        // Rows that only tell: the summary stays, with the row's words for a while.
        ShowOszSummary(row.Kind, row.Difficulty);
        Say(row.Say ?? row.Hint, 5f);
    }

    /// <summary>Back to the list; nothing was written.</summary>
    private static void CancelOsz()
    {
        if (osz != null) ModLog.Info("Battle creator: " + OszSummary.CancelledLine(osz.SourcePath));
        ClearOsz();
        picker = null;
        ShowScreen(Screen.List);
    }

    // ---- the lists behind the summary's rows -----------------------------------------------------------

    private static void ShowOszSlot(OszDifficulty d)
    {
        var choices = oszChoices!;
        int current = choices.SlotOf(d);
        var rows = OszSummary.SlotRows(choices);
        ShowPicker(new Picker
        {
            Heading = OszSummary.SlotHeading(d),
            Rows = rows,
            Hint = _ => OszSummary.SlotHint,
            Index = current >= 0 ? current : rows.Count - 1,
            Choose = i =>
            {
                if (oszChoices == choices) choices.SetSlot(d, i < choices.Slots.Length ? i : -1);
                ShowOszSummary(OszRow.Slot, d);
            },
            Back = () => ShowOszSummary(OszRow.Slot, d),
        });
    }

    private static void ShowOszLanes(OszPlan plan)
    {
        ShowPicker(new Picker
        {
            Heading = OszSummary.LanesHeading,
            Rows = OszSummary.LaneRows(plan),
            Hint = _ => OszSummary.LanesHint,
            Index = oszChoices?.Lanes == 5 ? 1 : 0,
            Choose = i =>
            {
                // Everything for both lane counts was worked out when the .osz was read.
                int lanes = i == 0 ? 4 : 5;
                if (osz == plan && oszChoices != null && oszChoices.Lanes != lanes && plan.Group(lanes) != null) oszChoices = OszChoices.Default(plan, lanes);
                ShowOszSummary(OszRow.Lanes, null);
            },
            Back = () => ShowOszSummary(OszRow.Lanes, null),
        });
    }

    private static void ShowOszSpeeds(OszPlan plan, OszChoices choices)
    {
        var list = OszSummary.SpeedChoices(plan, choices);
        ShowPicker(new Picker
        {
            Heading = OszSummary.SpeedHeading,
            Rows = list.Select(c => c.Text).ToList(),
            Hint = _ => OszSummary.SpeedHint,
            Index = OszSummary.SpeedIndex(plan, choices, list),
            Choose = i =>
            {
                if (oszChoices == choices && i >= 0 && i < list.Count) choices.SpeedsFrom = list[i].From;
                ShowOszSummary(OszRow.Speed, null);
            },
            Back = () => ShowOszSummary(OszRow.Speed, null),
        });
    }

    private static void ShowOszDetails(OszPlan plan, OszChoices choices)
    {
        var items = OszConvert.Details(plan, choices);
        ShowPicker(new Picker
        {
            Heading = OszSummary.DetailsHeading,
            Rows = items,
            Hint = i => (i >= 0 && i < items.Count ? items[i] + "  " : "") + "Enter or Esc goes back.",
            Choose = _ => ShowOszSummary(OszRow.Details, null),
            Back = () => ShowOszSummary(OszRow.Details, null),
        });
    }

    // ---- making the battle ----------------------------------------------------------------------------

    private static void MakeOszBattle()
    {
        var plan = osz;
        var choices = oszChoices?.Copy();
        if (plan == null || choices == null) { CancelOsz(); return; }
        // The summary stays while it's made, so a refusal leaves the player where they can fix it.
        ShowOszSummary(OszRow.Make, null);
        if (OszSummary.MakeProblem(plan, choices) is { } problem)
        {
            Say(problem, 7f);
            return;
        }
        string root = Root;
        Run(Task.Run(() => BattleFiles.CreateFromOsz(root, plan, choices)), "Making the battle...", made =>
        {
            ModLog.Info("Battle creator: " + OszSummary.CreatedLine(made.Folder, choices.Lanes, plan.SourcePath, made.Summary));
            ClearOsz();
            picker = null;
            Rescan();
            SelectInList(made.Folder);
            OpenBattle(made.Folder);
            RefreshArcade();
            // OpenBattle said why when it couldn't open it; the list shows it.
            if (draft == null) return;
            SetPage(Page.Charts);
            // The arcade lists only a battle the game's own chart reader takes (checked here, on the main thread).
            string? reader = ChartEditor.GameReaderProblem(made.Chart, choices.Lanes);
            if (reader != null)
            {
                ModLog.Error($"Battle creator: the game's chart reader can't read {made.Folder}: {reader}.");
                Say($"Made from the osu!mania beatmap (beta), but the game's chart reader can't read it ({reader}), so the arcade won't list it yet.", 9f);
            }
            else Say(OszSummary.Made, 9f);
        });
    }
}
