using UnityEngine;
using UnityEngine.UI;
using static NocturneFlatScroll.EditorUi;

namespace NocturneFlatScroll;

// The screen's extra pieces for a custom battle: the difficulty tabs above the playfield, the
// panel on a difficulty that isn't charted yet, the Timing tab's tempo and offset tools, the
// Setup tab's difficulty actions, the unsaved-changes prompt, and the 5-lane attack lane.
internal static partial class ChartEditor
{
    private const float DifficultyH = 52f;
    private static readonly Color AttackLaneColor = Hex(0x2C1A27);

    private static RectTransform? difficultyBar, notChartedPanel, promptLayer;
    private static TMP_Text? notChartedText, attackLabel;
    private static readonly List<Image> tabDots = new();
    private static readonly List<UiButton> overlayCopyButtons = new(), setupCopyButtons = new(), promptButtons = new();

    // A 5-lane battle's middle lane is played with the attack key (Space by default). -1 with 4
    // lanes, and for game songs, whose editor looks as it always has.
    private static int AttackLane => battle != null && chart != null && chart.Lanes == 5 ? 2 : -1;

    private static void ClearBattleWidgets()
    {
        difficultyBar = notChartedPanel = promptLayer = null;
        notChartedText = attackLabel = null;
        tabDots.Clear();
        overlayCopyButtons.Clear();
        setupCopyButtons.Clear();
        promptButtons.Clear();
    }

    private static void BuildBattleWidgets()
    {
        // The difficulty tabs, in a strip above the playfield; a dot marks the ones with notes.
        difficultyBar = MakeImage("Difficulties", editPanel!, PanelColor).rectTransform;
        difficultyBar.anchorMin = new Vector2(0, 1);
        difficultyBar.anchorMax = new Vector2(1, 1);
        difficultyBar.pivot = new Vector2(0.5f, 1);
        difficultyBar.offsetMin = new Vector2(LeftW, -TopH - DifficultyH);
        difficultyBar.offsetMax = new Vector2(-RightW, -TopH);
        Ui.AddSolidPanel(difficultyBar);
        for (int i = 0; i < BattleChartFile.SlotCount; i++)
        {
            int s = i;
            var b = Ui.MakeButton(difficultyBar, SlotName(s), () => ShowDifficulty(s));
            b.Active = () => slot == s;
            b.Label.fontSize = 18;
            b.Rect.anchorMin = new Vector2(s / (float)BattleChartFile.SlotCount, 0);
            b.Rect.anchorMax = new Vector2((s + 1) / (float)BattleChartFile.SlotCount, 1);
            b.Rect.offsetMin = new Vector2(4, 7);
            b.Rect.offsetMax = new Vector2(-4, -7);
            var dot = MakeImage("Charted", b.Rect, Hex(0x4FD1A5));
            Place(dot.rectTransform, new Vector2(1, 0.5f), new Vector2(-12, 0), new Vector2(10, 10), new Vector2(1, 0.5f));
            tabDots.Add(dot);
        }

        // Over the playfield while the difficulty shown has no chart.
        notChartedPanel = MakeImage("NotCharted", fieldArea!, new Color(PanelColor.r, PanelColor.g, PanelColor.b, 0.97f)).rectTransform;
        Place(notChartedPanel, new Vector2(0.5f, 0.5f), new Vector2(0, 60), new Vector2(560, 380));
        Ui.AddSolidPanel(notChartedPanel);
        notChartedText = MakeText("Text", notChartedPanel, 24, TextAlignmentOptions.Top);
        PlaceTop(notChartedText.rectTransform, 24, -22, 512, 120);
        var start = Ui.MakeButton(notChartedPanel, "Start empty", StartEmpty);
        PlaceTop(start.Rect, 24, -146, 512, 44);
        for (int i = 0; i < BattleChartFile.SlotCount; i++)
        {
            int s = i;
            var b = Ui.MakeButton(notChartedPanel, $"Copy from {SlotName(s)}", () => CopyFromTab(s));
            b.Visible = () => s != slot && NoteCount(s) > 0;
            b.Label.fontSize = 18;
            overlayCopyButtons.Add(b);
        }
        notChartedPanel.gameObject.SetActive(false);

        BuildClosePrompt();
    }

    private static void BuildBattleTiming(float width, float y)
    {
        var tempo = new (string Label, Action Do)[]
        {
            ("Set BPM", () => StartTyping(TextField.Bpm)), ("Tempo change here", () => StartTyping(TextField.BpmChange)),
            ("Remove tempo change", RemoveTempoChange), ("First beat here", FirstBeatHere),
        };
        for (int i = 0; i < tempo.Length; i++)
        {
            var (label, doIt) = tempo[i];
            var b = Ui.MakeButton(rightPanel!, label, doIt);
            b.Visible = () => tab == Tab.Timing;
            b.Label.fontSize = 17;
            PlaceTop(b.Rect, 16 + (i % 2) * (width / 2 + 4), y - (i / 2) * 46, width / 2 - 4, 40);
        }
        y -= (tempo.Length / 2) * 46;

        // Every beat earlier or later against the music: #OFFSET in small steps.
        double[] nudges = { -10, -1, 1, 10 };
        float quarter = (width - 3 * 6) / 4;
        for (int i = 0; i < nudges.Length; i++)
        {
            double ms = nudges[i];
            var b = Ui.MakeButton(rightPanel!, $"{(ms > 0 ? "+" : "")}{ms:0} ms", () => NudgeOffset(ms));
            b.Visible = () => tab == Tab.Timing;
            b.Label.fontSize = 17;
            PlaceTop(b.Rect, 16 + i * (quarter + 6), y, quarter, 40);
        }
        y -= 46;

        // On this tab the note ticks' key taps.
        var tap = Ui.MakeButton(rightPanel!, "", Tap);
        tap.Text = () => $"Tap tempo <size=62%><color=#9D92B4>{ShortKey(EditorAction.NoteTicks)}</color></size>";
        tap.Visible = () => tab == Tab.Timing;
        tap.Label.fontSize = 17;
        PlaceTop(tap.Rect, 16, y, width / 2 - 4, 40);
        var loop = Ui.MakeButton(rightPanel!, "", ToggleLoop);
        loop.Text = () => $"Loop 2 bars: {(looping ? "on" : "off")}";
        loop.Active = () => looping;
        loop.Visible = () => tab == Tab.Timing;
        loop.Label.fontSize = 17;
        PlaceTop(loop.Rect, 16 + width / 2 + 4, y, width / 2 - 4, 40);
        y -= 46;

        // Once the taps make a tempo: use it rounded (Enter) or as tapped (Shift+Enter).
        var rounded = Ui.MakeButton(rightPanel!, "", () => ApplyTaps(exact: false));
        rounded.Text = () => taps.Bpm is double bpm ? $"Use {Math.Round(bpm):0} BPM <size=62%><color=#9D92B4>Enter</color></size>" : "";
        rounded.Visible = () => tab == Tab.Timing && taps.Bpm != null;
        rounded.Label.fontSize = 17;
        PlaceTop(rounded.Rect, 16, y, width / 2 - 4, 40);
        var exact = Ui.MakeButton(rightPanel!, "", () => ApplyTaps(exact: true));
        exact.Text = () => taps.Bpm is double bpm ? $"Use {bpm:0.00} <size=62%><color=#9D92B4>Shift+Enter</color></size>" : "";
        exact.Visible = () => tab == Tab.Timing && taps.Bpm != null;
        exact.Label.fontSize = 17;
        PlaceTop(exact.Rect, 16 + width / 2 + 4, y, width / 2 - 4, 40);
    }

    private static void BuildBattleSetup(float width)
    {
        var files = new (string Label, Action Do)[] { ("Save", () => Save()), ("Open folder", OpenBattleFolder) };
        for (int i = 0; i < files.Length; i++)
        {
            var (label, doIt) = files[i];
            var b = Ui.MakeButton(rightPanel!, label, doIt);
            b.Visible = () => tab == Tab.Setup;
            PlaceTop(b.Rect, 16 + (i % 2) * (width / 2 + 4), -270, width / 2 - 4, 40);
        }
        var chartIt = Ui.MakeButton(rightPanel!, "", () => { if (tabCharted) UnchartTab(); else StartEmpty(); });
        chartIt.Text = () => tabCharted ? $"Delete the {SlotName(slot)} chart" : $"Start {SlotName(slot)} empty";
        chartIt.Visible = () => tab == Tab.Setup;
        PlaceTop(chartIt.Rect, 16, -316, width, 40);
        for (int i = 0; i < BattleChartFile.SlotCount; i++)
        {
            int s = i;
            var b = Ui.MakeButton(rightPanel!, $"Copy from {SlotName(s)}", () => CopyFromTab(s));
            b.Visible = () => tab == Tab.Setup && s != slot && NoteCount(s) > 0;
            b.Label.fontSize = 17;
            setupCopyButtons.Add(b);
        }
    }

    private static void BuildClosePrompt()
    {
        promptLayer = MakeRect("UnsavedChanges", editPanel!);
        Stretch(promptLayer, 0, 0, 0, 0);
        var dim = MakeImage("Dim", promptLayer, new Color(0f, 0f, 0f, 0.6f)).rectTransform;
        Stretch(dim, 0, 0, 0, 0);
        Ui.AddSolidPanel(dim);
        var box = MakeImage("Box", promptLayer, PanelColor).rectTransform;
        box.sizeDelta = new Vector2(760, 250);
        var text = MakeText("Text", box, 28, TextAlignmentOptions.Center);
        Place(text.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -26), new Vector2(700, 110));
        text.text = "Unsaved changes\n<size=70%><color=#9D92B4>Save the chart before closing?</color></size>";
        var choices = new (string Label, string Key, Action Do)[]
        {
            ("Save", "Enter", PromptSave), ("Discard", "D", Close), ("Cancel", "Esc", () => closePrompt = false),
        };
        for (int i = 0; i < choices.Length; i++)
        {
            var (label, key, doIt) = choices[i];
            var b = Ui.MakeButton(box, $"{label} <size=65%><color=#9D92B4>{key}</color></size>", doIt);
            Place(b.Rect, new Vector2(0.5f, 0), new Vector2((i - 1) * 240, 30), new Vector2(220, 54), new Vector2(0.5f, 0));
            promptButtons.Add(b);
        }
        promptLayer.gameObject.SetActive(false);
    }

    /// <summary>The battle pieces' per-frame state: tab dots, the not-charted panel, button places, the prompt.</summary>
    private static void DrawBattlePanels()
    {
        for (int s = 0; s < tabDots.Count; s++)
        {
            bool has = NoteCount(s) > 0;
            if (tabDots[s] && tabDots[s].gameObject.activeSelf != has) tabDots[s].gameObject.SetActive(has);
        }
        if (notChartedPanel)
        {
            bool show = !tabCharted;
            if (notChartedPanel!.gameObject.activeSelf != show) notChartedPanel.gameObject.SetActive(show);
            if (show)
            {
                // Until then the arcade plays the nearest charted difficulty here (-1: none is).
                int standIn = BattleChartFile.PlaysInstead(ChartedTabs(), slot);
                notChartedText!.text = $"{SlotName(slot)} isn't charted\n<size=70%><color=#9D92B4>" +
                    (standIn >= 0
                        ? $"Click Start empty, or copy another difficulty's notes.\nUntil then, the arcade plays the {SlotName(standIn)} chart on {SlotName(slot)}."
                        : "Click Start empty to chart it.") + "</color></size>";
                FlowButtons(overlayCopyButtons, 24, -202, 512, 40, 2, 8);
            }
        }
        if (tab == Tab.Setup) FlowButtons(setupCopyButtons, 16, -362, RightW - 32, 40, 2, 8);
        if (promptLayer && promptLayer!.gameObject.activeSelf != closePrompt) promptLayer.gameObject.SetActive(closePrompt);
    }

    /// <summary>Places the buttons that are showing one after another in rows, so hidden ones leave no gaps.</summary>
    private static void FlowButtons(List<UiButton> buttons, float x, float y, float width, float height, int columns, float gap)
    {
        float w = (width - gap * (columns - 1)) / columns;
        int n = 0;
        foreach (var b in buttons)
        {
            if (!(b.Visible?.Invoke() ?? true)) continue;
            PlaceTop(b.Rect, x + (n % columns) * (w + gap), y - (n / columns) * (height + gap), w, height);
            n++;
        }
    }

    // ---- the attack lane --------------------------------------------------------------------

    private static void BuildAttackLabel()
    {
        attackLabel = null;
        if (AttackLane < 0) return;
        attackLabel = MakeText("AttackLane", field!, 15, TextAlignmentOptions.Center);
        attackLabel.rectTransform.sizeDelta = new Vector2(LaneWidth, 44);
        attackLabel.enableWordWrapping = false;
        attackLabel.color = Hex(0xE58A5C, 0.85f);
        attackLabel.text = "ATTACK\nSpace";
    }

    private static void PlaceAttackLabel()
    {
        if (!attackLabel) return;
        attackLabel!.rectTransform.anchoredPosition = new Vector2(LaneX(AttackLane) - FieldCentre, JudgeY - 40);
        attackLabel.transform.SetAsLastSibling();
    }
}
