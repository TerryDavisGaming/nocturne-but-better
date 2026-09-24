using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>
/// Where the custom charts show up in the game's menus: a Custom Charts button on the main menu
/// and a Custom Charts tab in Options (the gameplay page showing only the chart rows), plus a
/// custom difficulty entry and a Custom Charts entry in the Difficulty screen.
/// </summary>
internal static class CustomChartsMenu
{
    private const string MainButtonName = "Button_FlatCustomCharts";
    private const string TabName = "Tab_FlatCustomCharts";
    private const string DifficultyEntryName = "Difficulty_FlatCustom";
    private const string Title = "Custom Charts";

    /// <summary>True while the gameplay page is showing the chart rows for the Custom Charts tab.</summary>
    internal static bool ChartsPage { get; private set; }

    private static bool openingPage;
    private static float pageRequestedAt = -1f;
    private static float nextPageCheck;
    // Managed delegates kept alive while their IL2CPP listeners are registered.
    private static readonly List<UnityAction> Listeners = new();
    private static CustomButton? difficultyEntry;
    private static DifficultyMenu? difficultyMenu;

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        Patch(harmony, typeof(MainMenu), "Activate", postfix: nameof(MainMenuPostfix));
        Patch(harmony, typeof(MainMenu), "UpdateButtonState", postfix: nameof(MainMenuPostfix));
        Patch(harmony, typeof(OptionsPanel), "Awake", postfix: nameof(OptionsPanelPostfix));
        Patch(harmony, typeof(OptionsPanel), "WillShow", postfix: nameof(OptionsPanelShownPostfix));
        Patch(harmony, typeof(OptionsPanel), "SetActiveTabButton", postfix: nameof(ActiveTabPostfix));
        foreach (var open in new[] { "OpenAudioMenu", "OpenGameplayMenu", "OpenVideoMenu", "OpenKeybindingMenu",
                     "OpenCalibrationMenu", "OpenAccessiblityMenu", "OpenLanguageMenu", "OpenPrivacySettingsMenu" })
            Patch(harmony, typeof(OptionsPanel), open, prefix: nameof(OtherTabPrefix));
        Patch(harmony, typeof(DifficultyMenu), "Activate", postfix: nameof(DifficultyMenuPostfix));
        Patch(harmony, typeof(CustomButton), "OnMove", prefix: nameof(ButtonMovePrefix));
        Patch(harmony, typeof(CustomToggleState), "UpdatePreferredTextWidth", postfix: nameof(ValueWidthPostfix));
    }

    // A value row is as wide as its longest value, so a long chart name would push the row's right
    // arrow under the scroll bar. The chart rows are capped and cut longer values short instead.
    private static void ValueWidthPostfix(CustomToggleState __instance)
    {
        try
        {
            if (!__instance) return;
            var row = __instance.GetComponentInParent<CustomButton>();
            if (!row || !row.name.StartsWith("StateToggle_FlatChart")) return;
            var layout = __instance._layoutElement;
            var label = __instance.label;
            if (!layout || !label) return;
            float cap = label.GetPreferredValues("Choose file...").x;
            if (cap <= 0 || layout.preferredWidth <= cap) return;
            layout.preferredWidth = cap;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
        }
        catch (Exception ex) { ReportOnce("value width", ex); }
    }

    private static void Patch(HarmonyLib.Harmony harmony, Type type, string method, string? prefix = null, string? postfix = null)
    {
        var target = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.FullName, method);
        harmony.Patch(target,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(CustomChartsMenu), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(CustomChartsMenu), postfix));
    }

    // ---- main menu -------------------------------------------------------------------------

    private static void MainMenuPostfix(MainMenu __instance)
    {
        try
        {
            if (!__instance || !__instance.optionsButton) return;
            var options = __instance.optionsButton;
            var parent = options.transform.parent;
            var existing = parent.Find(MainButtonName);
            CustomButton button;
            if (existing) button = existing.GetComponent<CustomButton>();
            else
            {
                button = CloneButton(options, parent, MainButtonName, Title, () => OpenFromMainMenu(__instance));
                button.transform.SetSiblingIndex(options.transform.GetSiblingIndex() + 1);
                button.ButtonHelpText = "Import, pick, and share custom difficulties.";
            }
            button.gameObject.SetActive(options.gameObject.activeSelf);
            InsertBelow(options, button);
        }
        catch (Exception ex) { ReportOnce("main menu", ex); }
    }

    private static void OpenFromMainMenu(MainMenu menu)
    {
        pageRequestedAt = Time.unscaledTime;
        menu.optionsButton.onClick.Invoke();
    }

    // ---- options tab -----------------------------------------------------------------------

    private static void OptionsPanelPostfix(OptionsPanel __instance)
    {
        try { EnsureTab(__instance); }
        catch (Exception ex) { ReportOnce("options tab", ex); }
    }

    private static void OptionsPanelShownPostfix(OptionsPanel __instance)
    {
        // A fresh Options screen opens on its usual page unless the main menu button asked for ours.
        if (pageRequestedAt < 0f) SetChartsPage(false);
        OptionsPanelPostfix(__instance);
    }

    private static CustomButton? EnsureTab(OptionsPanel panel)
    {
        if (!panel || !panel.gameplayButton) return null;
        var gameplay = panel.gameplayButton;
        var parent = gameplay.transform.parent;
        var existing = parent.Find(TabName);
        if (existing) return existing.GetComponent<CustomButton>();
        var tab = CloneButton(gameplay, parent, TabName, Title, () => OpenPage(panel));
        tab.ButtonHelpText = "Import, pick, and share custom difficulties.";
        // After Accessibility, the last settings page before Privacy.
        var after = panel.accessibilityButton ? panel.accessibilityButton : gameplay;
        tab.transform.SetSiblingIndex(after.transform.GetSiblingIndex() + 1);
        InsertBelow(after, tab);
        return tab;
    }

    /// <summary>
    /// The game only resets the tabs it knows about, so the Custom Charts tab's highlight is
    /// turned off by hand when another tab becomes the active one.
    /// </summary>
    private static void ActiveTabPostfix(OptionsPanel __instance, CustomButton button)
    {
        try
        {
            var tab = EnsureTab(__instance);
            if (!tab || (button && button.Pointer == tab!.Pointer)) return;
            var objects = tab.ActiveTabObjects;
            if (objects == null) return;
            foreach (var o in objects)
                if (o && o.gameObject.activeSelf) o.gameObject.SetActive(false);
        }
        catch (Exception ex) { ReportOnce("options tab highlight", ex); }
    }

    // The tab is a clone of Gameplay's, highlight included, and the game only switches the
    // highlights of the tabs it knows. So the tab is lit exactly while its page is open.
    private static float nextHighlightCheck;

    private static void KeepTabHighlight()
    {
        if (Time.unscaledTime < nextHighlightCheck) return;
        nextHighlightCheck = Time.unscaledTime + 0.2f;
        try
        {
            var panel = ActiveOptionsPanel();
            if (panel == null) return;
            var tab = panel.gameplayButton ? panel.gameplayButton.transform.parent.Find(TabName)?.GetComponent<CustomButton>() : null;
            var objects = tab ? tab!.ActiveTabObjects : null;
            if (objects == null) return;
            foreach (var o in objects)
                if (o && o.gameObject.activeSelf != ChartsPage) o.gameObject.SetActive(ChartsPage);
            // On the charts page the Gameplay tab (whose page it borrows) isn't the lit one.
            if (ChartsPage && panel.gameplayButton.ActiveTabObjects is { } gameplay)
                foreach (var o in gameplay)
                    if (o && o.gameObject.activeSelf) o.gameObject.SetActive(false);
        }
        catch (Exception ex) { ReportOnce("options tab highlight", ex); }
    }

    private static void OtherTabPrefix()
    {
        if (!openingPage) SetChartsPage(false);
    }

    /// <summary>Opens the gameplay page as the Custom Charts page.</summary>
    internal static void OpenPage(OptionsPanel panel)
    {
        pageRequestedAt = -1f;
        var tab = EnsureTab(panel);
        SetChartsPage(true);
        openingPage = true;
        try { panel.OpenGameplayMenu(); }
        finally { openingPage = false; }
        if (tab) panel.SetActiveTabButton(tab);
        OptionsMenuIntegration.RefreshAll();
    }

    private static void SetChartsPage(bool value)
    {
        if (ChartsPage == value) return;
        ChartsPage = value;
        OptionsMenuIntegration.RefreshAll();
    }

    /// <summary>Called every frame: opens the page once Options is up after the main menu button.</summary>
    internal static void Update()
    {
        KeepFontsMatched();
        KeepTabHighlight();
        // Leaving Options ends the page, so the gameplay page elsewhere (the battle's pause menu)
        // shows its usual rows.
        if (ChartsPage && Time.unscaledTime >= nextPageCheck)
        {
            nextPageCheck = Time.unscaledTime + 0.5f;
            if (ActiveOptionsPanel() == null || BattleSong() != null) SetChartsPage(false);
        }
        if (pageRequestedAt < 0f) return;
        if (Time.unscaledTime - pageRequestedAt > 5f) { pageRequestedAt = -1f; return; }
        if (Time.unscaledTime - pageRequestedAt < 0.2f) return;
        foreach (var panel in Resources.FindObjectsOfTypeAll<OptionsPanel>())
        {
            if (!panel || !panel.gameObject.activeInHierarchy) continue;
            OpenPage(panel);
            return;
        }
    }

    // ---- difficulty screen -----------------------------------------------------------------

    private static void DifficultyMenuPostfix(DifficultyMenu __instance)
    {
        try { RefreshDifficultyMenu(__instance); }
        catch (Exception ex) { ReportOnce("difficulty screen", ex); }
    }

    private static void RefreshDifficultyMenu(DifficultyMenu menu)
    {
        var buttons = menu.buttons;
        if (buttons == null || buttons.Count == 0) return;
        var last = buttons[buttons.Count - 1];
        if (!last) return;
        var list = last.transform.parent;
        difficultyMenu = menu;

        if (!difficultyEntry || difficultyEntry!.transform.parent != list)
        {
            var existing = list.Find(DifficultyEntryName);
            difficultyEntry = existing ? existing.GetComponent<CustomButton>()
                : CloneButton(last, list, DifficultyEntryName, "Custom", ClickDifficultyEntry);
            difficultyEntry!.transform.SetSiblingIndex(last.transform.GetSiblingIndex() + 1);
            difficultyEntry.ButtonHelpText = "Left and right pick a custom difficulty for the song. Select opens Custom Charts.";
        }
        RefreshDifficultyEntry();
        InsertBelow(last, difficultyEntry);
        if (list.TryCast<RectTransform>() is { } listRect) LayoutRebuilder.MarkLayoutForRebuild(listRect);
    }

    /// <summary>The song the custom difficulty entry is about: the battle's, else the Custom Charts page's.</summary>
    private static string? EntrySong() => BattleSong() ?? CustomChartOptions.CurrentSong;

    private static string? BattleSong()
    {
        foreach (var conductor in Resources.FindObjectsOfTypeAll<WwiseConductor>())
            if (conductor && conductor.gameObject.activeInHierarchy && conductor.initializedSong && conductor.CurrentSong)
                return CustomSongs.TitleOf(conductor.CurrentSong) ?? conductor.CurrentSong.name;
        return null;
    }

    private static OptionsPanel? ActiveOptionsPanel()
    {
        foreach (var panel in Resources.FindObjectsOfTypeAll<OptionsPanel>())
            if (panel && panel.gameObject.activeInHierarchy) return panel;
        return null;
    }

    /// <summary>
    /// Whether selecting the entry can go to the Custom Charts page: not in a battle, and not from
    /// the difficulty prompt of a new game or the gauntlet, where there is no Options to go back to.
    /// </summary>
    private static bool CanOpenPage()
    {
        if (BattleSong() != null) return false;
        var options = difficultyMenu ? difficultyMenu!.currentOptions : null;
        if (options != null && (options.NewGame || options.Gauntlet)) return false;
        return OptionsOnStack();
    }

    private static void RefreshDifficultyEntry()
    {
        if (!difficultyEntry) return;
        string? song = EntrySong();
        var charts = song == null ? new List<CustomCharts.CustomChart>() : CustomCharts.ForSong(song).ToList();
        var picked = song == null ? null : CustomCharts.Selected(song);
        bool page = CanOpenPage();
        string name, desc;
        if (song == null || charts.Count == 0)
        {
            name = "Custom";
            desc = song == null ? "No custom charts yet" : $"No custom charts for {song} yet";
        }
        else
        {
            name = picked == null ? "Custom: Off" : Shorten(picked.DisplayName, 22);
            desc = $"{song}: left/right to choose" + (BattleSong() != null ? ", starts next try" : "");
        }
        if (page) desc += ", select for Custom Charts";
        SetTexts(difficultyEntry!, name, desc);
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text.Substring(0, max - 3).TrimEnd() + "...";

    private static void ClickDifficultyEntry()
    {
        if (!CanOpenPage()) { ChangeDifficultyEntry(1); return; }
        // Back to the gameplay page, then over to the Custom Charts tab once Options is up again.
        if (!CloseDifficultyMenu()) return;
        pageRequestedAt = Time.unscaledTime;
    }

    /// <summary>Whether an Options screen is somewhere on the game's menu stack, under the Difficulty screen.</summary>
    private static bool OptionsOnStack()
    {
        foreach (var presenter in Resources.FindObjectsOfTypeAll<PanelStackPresenter>())
        {
            if (!presenter) continue;
            var stack = (presenter._menuSystem ?? presenter._menuSystemProvider?.MenuSystem)?.CurrentStack;
            if (stack == null) continue;
            for (int i = 0; i < stack.Count; i++)
                if (stack.Get(i)?.Controller?.TryCast<OptionsPanel>() != null) return true;
        }
        return false;
    }

    /// <summary>Takes the Difficulty screen off the game's panel stack, as its Back action does.</summary>
    private static bool CloseDifficultyMenu()
    {
        foreach (var presenter in Resources.FindObjectsOfTypeAll<PanelStackPresenter>())
        {
            if (!presenter) continue;
            // The presenter gets its menu system from the provider; the cached field can be empty.
            var system = presenter._menuSystem ?? presenter._menuSystemProvider?.MenuSystem;
            var stack = system?.CurrentStack;
            var top = stack != null && stack.Count > 0 ? stack.Top?.Controller : null;
            if (top == null || !top.TryCast<DifficultyMenu>()) continue;
            stack!.Pop();
            return true;
        }
        ModLog.Info("Custom charts: couldn't find the Difficulty screen on the menu stack to close it.");
        return false;
    }

    private static void ChangeDifficultyEntry(int direction)
    {
        string? song = EntrySong();
        if (song == null) return;
        var charts = CustomCharts.ForSong(song).ToList();
        if (charts.Count == 0) return;
        var picked = CustomCharts.Selected(song);
        int index = picked == null ? 0 : charts.IndexOf(picked) + 1;
        int next = ((index + direction) % (charts.Count + 1) + charts.Count + 1) % (charts.Count + 1);
        CustomCharts.Select(song, next == 0 ? null : charts[next - 1]);
        RefreshDifficultyEntry();
        OptionsMenuIntegration.RefreshAll();
    }

    /// <summary>Left and right on the custom difficulty entry pick the chart instead of moving.</summary>
    private static bool ButtonMovePrefix(CustomButton __instance, AxisEventData eventData)
    {
        if (!__instance || !difficultyEntry || __instance.Pointer != difficultyEntry!.Pointer || eventData == null) return true;
        if (eventData.moveDir == MoveDirection.Left) { ChangeDifficultyEntry(-1); return false; }
        if (eventData.moveDir == MoveDirection.Right) { ChangeDifficultyEntry(1); return false; }
        return true;
    }

    // ---- shared ----------------------------------------------------------------------------

    private static CustomButton CloneButton(CustomButton template, Transform parent, string name, string text, Action onClick)
    {
        var clone = Object.Instantiate(template.gameObject, parent, false);
        clone.name = name;
        foreach (var localizer in clone.GetComponentsInChildren<Localize>(true))
        {
            localizer.enabled = false;
            Object.Destroy(localizer);
        }
        var button = clone.GetComponent<CustomButton>();
        button.FirstSelection = false;
        button.DataContextOnClick.RemoveAllListeners();
        button.onClick = new Button.ButtonClickedEvent();
        var listener = DelegateSupport.ConvertDelegate<UnityAction>(onClick)
            ?? throw new InvalidOperationException("Could not create a click listener.");
        Listeners.Add(listener);
        button.onClick.AddListener(listener);
        var help = button.LocalizedHelpKey;
        help.mTerm = string.Empty;
        button.LocalizedHelpKey = help;
        SetTexts(button, text, null);
        clone.SetActive(true);
        MatchFont(template, button);
        return button;
    }

    /// <summary>Sets a button's main label, and for difficulty entries the description under it.</summary>
    private static void SetTexts(CustomButton button, string name, string? description)
    {
        var nameLabel = button.transform.Find("Image_Header/Label_Name")?.GetComponent<TMP_Text>();
        if (nameLabel)
        {
            nameLabel!.text = name;
            nameLabel.enableWordWrapping = false;
        }
        else
        {
            // Straight onto the label: going through CustomButton.Text can restyle it.
            var label = button.GetComponentInChildren<TMP_Text>(true);
            if (label) label!.text = name;
            else button.Text = name;
        }
        if (description == null) return;
        var descLabel = button.transform.Find("Image_Lower/Label_Desc")?.GetComponent<TMP_Text>();
        if (descLabel)
        {
            descLabel!.text = description;
            descLabel.enableWordWrapping = false;
            descLabel.overflowMode = TextOverflowModes.Ellipsis;
        }
    }

    // Cloned labels and the originals they copy. The game's localization sets the originals' font
    // for the language after they're cloned (the clones have no localizer), so they're re-matched.
    private static readonly List<(TMP_Text From, TMP_Text To)> matchedFonts = new();
    private static float nextFontCheck;

    /// <summary>Gives a cloned button's labels the same font, size and style as the original's.</summary>
    private static void MatchFont(CustomButton template, CustomButton clone)
    {
        var from = template.GetComponentsInChildren<TMP_Text>(true);
        var to = clone.GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < Math.Min(from.Length, to.Length); i++)
        {
            if (!from[i] || !to[i]) continue;
            CopyFont(from[i], to[i]);
            matchedFonts.Add((from[i], to[i]));
        }
    }

    private static void CopyFont(TMP_Text from, TMP_Text to)
    {
        to.font = from.font;
        to.fontSharedMaterial = from.fontSharedMaterial;
        to.fontSize = from.fontSize;
        to.fontStyle = from.fontStyle;
        to.characterSpacing = from.characterSpacing;
        to.enableAutoSizing = from.enableAutoSizing;
    }

    private static IntPtr Id(Object? o) => o ? o!.Pointer : IntPtr.Zero;

    private static void KeepFontsMatched()
    {
        if (matchedFonts.Count == 0 || Time.unscaledTime < nextFontCheck) return;
        nextFontCheck = Time.unscaledTime + 0.5f;
        matchedFonts.RemoveAll(p => !p.From || !p.To);
        foreach (var (from, to) in matchedFonts)
            if (Id(to.font) != Id(from.font) || Id(to.fontSharedMaterial) != Id(from.fontSharedMaterial)) CopyFont(from, to);
    }

    /// <summary>Puts <paramref name="added"/> right below <paramref name="above"/> in explicit up/down navigation.</summary>
    private static void InsertBelow(Selectable above, Selectable added)
    {
        var aboveNav = above.navigation;
        if (aboveNav.mode != Navigation.Mode.Explicit) return; // automatic navigation finds it by position
        var below = aboveNav.selectOnDown;
        if (below == added) return;
        var addedNav = added.navigation;
        addedNav.mode = Navigation.Mode.Explicit;
        addedNav.selectOnUp = above;
        addedNav.selectOnDown = below;
        addedNav.selectOnLeft = null;
        addedNav.selectOnRight = null;
        added.navigation = addedNav;
        aboveNav.selectOnDown = added;
        above.navigation = aboveNav;
        if (below)
        {
            var belowNav = below.navigation;
            if (belowNav.mode == Navigation.Mode.Explicit && belowNav.selectOnUp == above)
            {
                belowNav.selectOnUp = added;
                below.navigation = belowNav;
            }
        }
    }

    private static readonly HashSet<string> Reported = new();

    private static void ReportOnce(string where, Exception ex)
    {
        if (Reported.Add(where)) ModLog.Error($"Custom charts {where} failed: {ex}");
    }
}
