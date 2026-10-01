using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NocturnePlus;

/// <summary>
/// Where the custom charts show up in the game's menus: a Custom Charts button on the main menu
/// and a Custom Charts tab in Options (the gameplay page showing only the chart rows), plus a
/// custom difficulty entry and a Custom Charts entry in the Difficulty screen. The main menu's Get
/// Custom Battles box (the online hub, HubPage) is made here too, right after the Custom Charts
/// button, so the two never wait on each other.
/// </summary>
internal static class CustomChartsMenu
{
    private const string MainButtonName = "Button_FlatCustomCharts";
    private const string TabName = "Tab_FlatCustomCharts";
    private const string DifficultyEntryName = "Difficulty_FlatCustom";
    private const string Title = "Custom Charts";
    // The page has the custom difficulties and the Battle creator.
    private const string HelpText = "Import, pick, and share custom difficulties, or make your own battles.";
    private const string HubButtonName = "Button_FlatGetCustomBattles";
    private const string HubTitle = "Get Custom Battles";
    private const string HubHelpText = "Find, download and share custom battles and custom difficulties.";
    // The hub's box, in the mod's colour instead of the Steam green it's cloned from.
    private static readonly Color HubBoxColor = new(0.55f, 0.29f, 0.16f, 0.9f);
    // QA builds only: NFS_QA_HUB_TITLE=row puts Get Custom Battles in the title's list after Custom
    // Charts instead of the box above Get Soundtrack, for the QA picture that decides between them.
    private static readonly bool HubAsRow = QaBuild.Env("NFS_QA_HUB_TITLE") == "row";
    private static readonly bool QaLog = QaBuild.On;

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
                button.ButtonHelpText = HelpText;
            }
            button.gameObject.SetActive(options.gameObject.activeSelf);
            InsertBelow(options, button);
            HubButton(__instance, button);
        }
        catch (Exception ex) { ReportOnce("main menu", ex); }
    }

    /// <summary>
    /// Get Custom Battles: a third box above Get Soundtrack (the title's list has no room for a tenth
    /// row; the bottom-aligned list moves up a row to make room), cloned from Get Soundtrack with its
    /// Steam look and click taken off. Keyboards and pads reach it below Quit. Hidden when the hub is
    /// off or the build has no hub, and while Options is (as the Custom Charts button is).
    /// </summary>
    private static void HubButton(MainMenu menu, CustomButton customCharts)
    {
        try
        {
            bool row = HubAsRow || !menu.soundtrackButton;
            var template = row ? menu.optionsButton : menu.soundtrackButton;
            var parent = template.transform.parent;
            var existing = parent.Find(HubButtonName);
            CustomButton button;
            if (existing) button = existing.GetComponent<CustomButton>();
            else
            {
                button = CloneButton(template, parent, HubButtonName, HubTitle, HubPage.OpenFromTitle);
                button.ButtonHelpText = HubHelpText;
                if (row) button.transform.SetSiblingIndex(customCharts.transform.GetSiblingIndex() + 1);
                else
                {
                    // Just before Get Soundtrack in the list's layout group, so the three boxes stay together.
                    button.transform.SetSiblingIndex(template.transform.GetSiblingIndex());
                    PlainBox(button);
                }
            }
            bool show = HubGame.Enabled && menu.optionsButton.gameObject.activeSelf;
            if (button.gameObject.activeSelf != show) button.gameObject.SetActive(show);
            if (!show)
            {
                RemoveFromNavigation(button);
                return;
            }
            if (row) InsertBelow(customCharts, button);
            else if (menu.quitButton) InsertBelow(menu.quitButton, button);
        }
        catch (Exception ex) { ReportOnce("hub button", ex); }
    }

    // The Get Soundtrack box carries the Steam logo and a Steam-only switch. The hub's box shows no
    // icon (any picture of its own is hidden, and the box is plain in the mod's colour), and the
    // build switch that could hide it is taken off.
    private static void PlainBox(CustomButton button)
    {
        var own = button.GetComponent<Image>();
        var names = new List<string>();
        foreach (var image in button.GetComponentsInChildren<Image>(true))
        {
            if (!image || (own && image.Pointer == own.Pointer)) continue;
            string name = image.gameObject.name;
            names.Add(name);
            if (name.StartsWith("SelectionHighlight") || name.StartsWith("ActiveTabBackground")) continue;
            image.enabled = false;
        }
        foreach (var raw in button.GetComponentsInChildren<RawImage>(true))
            if (raw) raw.enabled = false;
        string sprite = own && own.sprite ? own.sprite.name : "";
        if (own)
        {
            own.sprite = null;
            own.type = Image.Type.Simple;
            own.color = HubBoxColor;
        }
        var flag = button.GetComponent<BuildFlagObject>();
        if (flag) Object.Destroy(flag);
        if (QaLog) ModLog.Info($"QA hub: the title box is cloned from Get Soundtrack (its sprite was '{sprite}'; pictures inside: {string.Join(", ", names)}).");
    }

    /// <summary>Takes a hidden button out of explicit up/down navigation, joining its neighbours again.</summary>
    private static void RemoveFromNavigation(Selectable removed)
    {
        var nav = removed.navigation;
        if (nav.mode != Navigation.Mode.Explicit) return;
        var above = nav.selectOnUp;
        var below = nav.selectOnDown;
        if (above && above.navigation.mode == Navigation.Mode.Explicit && above.navigation.selectOnDown == removed)
        {
            var aboveNav = above.navigation;
            aboveNav.selectOnDown = below;
            above.navigation = aboveNav;
        }
        if (below && below.navigation.mode == Navigation.Mode.Explicit && below.navigation.selectOnUp == removed)
        {
            var belowNav = below.navigation;
            belowNav.selectOnUp = above;
            below.navigation = belowNav;
        }
    }

    private static void OpenFromMainMenu(MainMenu menu)
    {
        pageRequestedAt = Time.unscaledTime;
        menu.optionsButton.onClick.Invoke();
    }

    // ---- options tab -----------------------------------------------------------------------

    private static void OptionsPanelPostfix(OptionsPanel __instance)
    {
        try
        {
            Remember(__instance);
            EnsureTab(__instance);
        }
        catch (Exception ex) { ReportOnce("options tab", ex); }
    }

    private static void OptionsPanelShownPostfix(OptionsPanel __instance)
    {
        // A fresh Options screen opens on its usual page unless the main menu button asked for ours.
        if (pageRequestedAt < 0f) SetChartsPage(false);
        OptionsPanelPostfix(__instance);
        // Its calibration previews may be new: Optimized looks for them again, and for the conductors.
        LayoutDriver.Rescan();
        conductorsAt = -1;
    }

    private static CustomButton? EnsureTab(OptionsPanel panel)
    {
        if (!panel || !panel.gameplayButton) return null;
        var gameplay = panel.gameplayButton;
        var parent = gameplay.transform.parent;
        var existing = parent.Find(TabName);
        if (existing) return existing.GetComponent<CustomButton>();
        var tab = CloneButton(gameplay, parent, TabName, Title, () => OpenPage(panel));
        tab.ButtonHelpText = HelpText;
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
        if (!value && conductorChecks > 0)
        {
            ModLog.Info($"Custom charts: the page looked for the game's conductors {conductorSearches} times in {conductorChecks} checks.");
            conductorChecks = conductorSearches = 0;
        }
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
            if (ActiveOptionsPanel() == null || InBattle()) SetChartsPage(false);
        }
        if (pageRequestedAt < 0f) return;
        if (Time.unscaledTime - pageRequestedAt > 5f) { pageRequestedAt = -1f; return; }
        if (Time.unscaledTime - pageRequestedAt < 0.2f) return;
        var open = ActiveOptionsPanel();
        if (open != null) OpenPage(open);
    }

    // ---- difficulty screen -----------------------------------------------------------------

    private static void DifficultyMenuPostfix(DifficultyMenu __instance)
    {
        try { RefreshDifficultyMenu(__instance); }
        catch (Exception ex) { ReportOnce("difficulty screen", ex); }
        LayoutDriver.Rescan();
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
            if (Playing(conductor)) return conductor.CurrentSong.name;
        return null;
    }

    private static bool Playing(WwiseConductor conductor) =>
        conductor && conductor.gameObject.activeInHierarchy && conductor.initializedSong && conductor.CurrentSong;

    // Optimized: the page's check every half second reuses the conductors the last search found
    // rather than searching every object. A conductor can only be in a battle once it has started a
    // song (WwiseConductor.Initialize, counted by CustomMusic.ConductorStarts), so one the search
    // didn't find can't count until that count changes, and then they're looked for again; also
    // when one is gone, when Options shows, and every ConductorSearchEvery as a backstop.
    private const float ConductorSearchEvery = 10f;
    private static readonly List<WwiseConductor> Conductors = new();
    private static int conductorsAt = -1;   // ConductorStarts at the last search; -1 for none
    private static float nextConductorSearch;
    private static int conductorChecks, conductorSearches;

    /// <summary>Whether a battle's conductor is playing a song (BattleSong() != null, without its name).</summary>
    private static bool InBattle()
    {
        if (!Performance.Optimizing || !ChartSwap.Installed) return BattleSong() != null;
        float now = Time.unscaledTime;
        bool search = conductorsAt != CustomMusic.ConductorStarts || now >= nextConductorSearch;
        if (!search)
            foreach (var conductor in Conductors)
                if (!conductor)
                {
                    search = true;
                    break;
                }
        conductorChecks++;
        if (search)
        {
            conductorSearches++;
            Conductors.Clear();
            foreach (var conductor in Resources.FindObjectsOfTypeAll<WwiseConductor>()) Conductors.Add(conductor);
            conductorsAt = CustomMusic.ConductorStarts;
            nextConductorSearch = now + ConductorSearchEvery;
        }
        bool playing = false;
        foreach (var conductor in Conductors)
            if (Playing(conductor))
            {
                playing = true;
                break;
            }
        if (QaConductorCheck && playing != (BattleSong() != null) && !qaConductorMismatch)
        {
            qaConductorMismatch = true;
            ModLog.Info($"QA custom charts: the kept conductors say {(playing ? "a battle" : "no battle")}, a full search says otherwise.");
        }
        return playing;
    }

    // QA builds only: NFS_QA_CONDUCTOR_CHECK=1 also searches every object on each check, and logs
    // the first time the two answers differ (they never should).
    private static readonly bool QaConductorCheck = QaBuild.Env("NFS_QA_CONDUCTOR_CHECK") == "1";
    private static bool qaConductorMismatch;

    // The Options screens the game has made, noted as they wake or show (and once from a search for
    // any made before the mod's hooks), so finding the open one doesn't search every object.
    private static readonly List<OptionsPanel> Panels = new();
    private static bool panelsSearched;

    private static void Remember(OptionsPanel panel)
    {
        if (!panel) return;
        for (int i = Panels.Count - 1; i >= 0; i--)
        {
            var known = Panels[i];
            if (!known) Panels.RemoveAt(i);
            else if (known.Pointer == panel.Pointer) return;
        }
        Panels.Add(panel);
    }

    private static OptionsPanel? ActiveOptionsPanel()
    {
        if (!panelsSearched)
        {
            panelsSearched = true;
            foreach (var panel in Resources.FindObjectsOfTypeAll<OptionsPanel>()) Remember(panel);
        }
        foreach (var panel in Panels)
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
        // A custom battle shows its title rather than its SongData's name.
        string shown = song == null ? "" : CustomBattles.TitleOf(song) ?? song;
        string name, desc;
        if (song == null || charts.Count == 0)
        {
            name = "Custom";
            // A custom battle never takes custom charts (CustomCharts.SharesScore); it plays its own.
            desc = song == null ? "No custom charts yet"
                : CustomBattles.IsRuntimeName(song) ? "Custom battles play their own charts"
                : $"No custom charts for {shown} yet";
        }
        else
        {
            name = picked == null ? "Custom: Off" : Shorten(picked.DisplayName, 22);
            desc = $"{shown}: left/right to choose" + (BattleSong() != null ? ", starts next try" : "");
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
