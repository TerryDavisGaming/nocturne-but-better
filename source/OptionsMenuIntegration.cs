using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>Adds native gameplay-options rows without replacing an existing setting.</summary>
internal static class OptionsMenuIntegration
{
    // The rows appear directly above Speed Mod, in this order.
    private static readonly RowSpec[] Specs =
    {
        new("StateToggle_FlatNoteScrolling",
            "Note scrolling",
            "Choose the original layout, flat downscroll, or flat upscroll.",
            new[] { "Default", "2D Downscroll", "2D Upscroll" },
            () => (int)SettingsState.Mode,
            (direction, wrap) => SettingsState.SetMode((ScrollMode)Cycle((int)SettingsState.Mode, direction, 3))),
        new("StateToggle_FlatReceptorHeight",
            "Receptor height",
            "Moves the 2D receptors in from their screen edge: up in downscroll, down in upscroll.",
            Enumerable.Range(SettingsState.MinReceptorHeight,
                    SettingsState.MaxReceptorHeight - SettingsState.MinReceptorHeight + 1)
                .Select(SettingsState.FormatReceptorHeight).ToArray(),
            () => SettingsState.ReceptorHeight - SettingsState.MinReceptorHeight,
            ChangeReceptorHeight),
        Percent("StateToggle_FlatNoteSize", "Note size",
            "Makes the 2D notes and receptors smaller or larger.", SettingsState.NoteSize),
        Percent("StateToggle_FlatLaneSpacing", "Lane spacing",
            "Moves the 2D lanes closer together or further apart.", SettingsState.LaneSpacing),
        new("StateToggle_FlatNoteSkin",
            "Note skin",
            "Draw notes and receptors as the original bars, circles, or arrows.",
            new[] { "Default", "Circle", "Arrow" },
            () => (int)SettingsState.NoteSkin,
            (direction, wrap) => SettingsState.SetNoteSkin((NoteSkin)Cycle((int)SettingsState.NoteSkin, direction, 3))),
        new("StateToggle_FlatNoteFlares",
            "Note flares",
            "Shows the burst on the receptor when you hit or hold a note. Mine explosions always show.",
            new[] { "Off", "On" },
            () => SettingsState.NoteFlares ? 1 : 0,
            (direction, wrap) => SetNoteFlares(!SettingsState.NoteFlares)),
        new("StateToggle_FlatTimingBar",
            "Timing bar",
            "Shows whether each hit was early (rabbit) or late (turtle), next to the receptors.",
            new[] { "Off", "On" },
            () => SettingsState.TimingBar ? 1 : 0,
            (direction, wrap) => SettingsState.SetTimingBar(!SettingsState.TimingBar)),
        new("StateToggle_FlatTimingBarPosition",
            "Timing bar position",
            "Where the timing bar goes in 2D upscroll: under the receptors, or at the top of the screen above the enemy.",
            new[] { "Below enemy", "Above enemy" },
            () => SettingsState.TimingBarTop ? 1 : 0,
            (direction, wrap) => SettingsState.SetTimingBarTop(!SettingsState.TimingBarTop)),
        Percent("StateToggle_FlatEnemyAttackOpacity", "Enemy attack opacity",
            "Makes the enemy see-through while it attacks, so the notes behind it stay visible. 100% leaves it as it is.",
            SettingsState.EnemyAttackOpacity),
    };

    // The hit and miss sound rows are on the Audio page (AudioOptionsIntegration).
    private static RowSpec Percent(string name, string title, string help, PercentSetting setting) =>
        new(name, title, help, setting.Labels(), () => setting.Index, setting.Change);

    private static readonly Dictionary<int, MenuRows> Menus = new();
    private static bool installed;
    private static bool refreshing;

    // Qualified because MelonLoader also defines a legacy root namespace named Harmony.
    internal static void Install(HarmonyLib.Harmony harmony)
    {
        if (installed) return;
        var ready = new HarmonyMethod(typeof(OptionsMenuIntegration), nameof(MenuReadyPostfix));
        foreach (var method in new[] { "Awake", "Activate", "DidShow", "RefreshViews" })
        {
            var target = AccessTools.DeclaredMethod(typeof(GameplayOptionsMenu), method)
                ?? throw new MissingMethodException(typeof(GameplayOptionsMenu).FullName, method);
            harmony.Patch(target, postfix: ready);
        }
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(GameplayOptionsMenu), "ResetDefaults")
                ?? throw new MissingMethodException(typeof(GameplayOptionsMenu).FullName, "ResetDefaults"),
            postfix: new HarmonyMethod(typeof(OptionsMenuIntegration), nameof(ResetDefaultsPostfix)));
        installed = true;
    }

    /// <summary>Adds the rows to an options panel that was instantiated before the patches.</summary>
    internal static void AttachToExistingMenus()
    {
        foreach (var menu in Resources.FindObjectsOfTypeAll<GameplayOptionsMenu>())
        {
            if (menu && menu.gameObject.scene.IsValid()) MenuReadyPostfix(menu);
        }
    }

    /// <summary>Called after SettingsState changes, including changes outside this menu.</summary>
    internal static void RefreshAll()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            foreach (var pair in Menus.ToArray())
            {
                var rows = pair.Value;
                if (!rows.Menu || !rows.AllAlive)
                {
                    Menus.Remove(pair.Key);
                    continue;
                }
                RefreshRows(rows);
            }
            AudioOptionsIntegration.RefreshAll();
        }
        catch (Exception ex)
        {
            ModLog.Error($"Refreshing flat-scroll options failed: {ex}");
        }
        finally { refreshing = false; }
    }

    private static void MenuReadyPostfix(GameplayOptionsMenu __instance)
    {
        if (!__instance || refreshing) return;
        // MenuPanel.Awake can hide its object before this postfix runs. The subsequent
        // Activate/DidShow hook creates the rows once native child lifecycle methods can run.
        if (!__instance.gameObject.activeInHierarchy) return;
        try
        {
            var id = __instance.GetInstanceID();
            if (!Menus.TryGetValue(id, out var rows) || !rows.AllAlive)
            {
                if (rows != null) rows.Destroy();
                rows = CreateRows(__instance);
                if (rows == null) return;
                Menus[id] = rows;
            }
            RefreshRows(rows);
        }
        catch (Exception ex)
        {
            // A failed optional menu extension must not prevent the original menu opening.
            ModLog.Error($"Adding flat-scroll options failed: {ex}");
        }
    }

    private static MenuRows? CreateRows(GameplayOptionsMenu menu)
    {
        var template = menu.runModeButton;
        var anchor = menu.noteSpeedModButton;
        if (!template || !anchor) return null;
        var parent = anchor.transform.parent;
        if (!parent) return null;

        var created = new List<OptionRow>();
        try
        {
            // Each clone is inserted directly above Speed Mod, so creation order is display order.
            foreach (var spec in Specs) created.Add(CreateRow(template, parent, anchor, spec));
            if (parent.TryCast<RectTransform>() is { } content)
                LayoutRebuilder.MarkLayoutForRebuild(content);
            ModLog.Info("Added flat-scroll rows to Options > Gameplay.");
            return new MenuRows(menu, created);
        }
        catch
        {
            foreach (var row in created) row.Destroy();
            throw;
        }
    }

    private static OptionRow CreateRow(CustomButton template, Transform parent, CustomButton anchor, RowSpec spec)
    {
        GameObject? clone = null;
        try
        {
            clone = Object.Instantiate(template.gameObject, parent, false);
            clone.name = spec.Name;
            var button = clone.GetComponent<CustomButton>();
            var toggle = clone.GetComponentInChildren<CustomToggleState>(true);
            if (!button || !toggle)
                throw new InvalidOperationException("Native run-mode row has no button/state control.");
            EnsureToggleLayout(toggle);

            // Copied localization terms must not put the old row's text back on language changes.
            foreach (var localizer in clone.GetComponentsInChildren<Localize>(true))
            {
                localizer.enabled = false;
                Object.Destroy(localizer);
            }

            button.FirstSelection = false;
            button.RepeatOnHold = false;
            button.Text = spec.Title;
            button.ButtonHelpText = spec.Help;
            var helpKey = button.LocalizedHelpKey;
            helpKey.mTerm = string.Empty;
            button.LocalizedHelpKey = helpKey;
            button.DataContextOnClick.RemoveAllListeners();
            button.onClick = new Button.ButtonClickedEvent();

            toggle.localizeStates = false;
            var states = new Il2CppSystem.Collections.Generic.List<string>();
            foreach (var state in spec.States) states.Add(state);
            toggle.PopulateStates(states);

            var row = new OptionRow(clone, button, toggle, spec);
            // Replacing, rather than appending, prevents the cloned Run Mode action from firing.
            button.onClick.AddListener(row.Click);
            toggle.NextState = row.Next;
            toggle.PreviousState = row.Previous;

            clone.transform.SetSiblingIndex(anchor.transform.GetSiblingIndex());
            clone.SetActive(true);
            toggle.UpdatePreferredTextWidth();
            return row;
        }
        catch
        {
            if (clone) Object.Destroy(clone);
            throw;
        }
    }

    private static void RefreshRows(MenuRows rows)
    {
        foreach (var row in rows.Rows) RefreshRow(row);
        InsertIntoNavigation(rows);
    }

    private static void RefreshRow(OptionRow row)
    {
        EnsureToggleLayout(row.Toggle);
        row.Button.Text = row.Spec.Title;
        row.Button.ButtonHelpText = row.Spec.Help;
        row.Toggle.State = row.Spec.GetState();
        row.Toggle.Refresh();
        row.Toggle.UpdatePreferredTextWidth();
    }

    private static void EnsureToggleLayout(CustomToggleState toggle)
    {
        // This private, nonserialized cache is normally initialized by native Awake. Resolve
        // it explicitly because Unity can instantiate a row below an inactive parent.
        if (!toggle.label)
        {
            var value = toggle.transform.Find("Label_Value");
            if (value) toggle.label = value.GetComponent<TMP_Text>();
        }
        if (!toggle.label)
            throw new InvalidOperationException("Flat-scroll option state label is missing.");
        if (!toggle.overridePreferredWidth || toggle._layoutElement) return;
        var layout = toggle.label.GetComponent<LayoutElement>();
        if (!layout) layout = toggle.label.gameObject.AddComponent<LayoutElement>();
        toggle._layoutElement = layout;
    }

    private static void InsertIntoNavigation(MenuRows rows)
    {
        var anchor = rows.Menu.noteSpeedModButton;
        if (!anchor) return;
        var buttons = rows.Rows.Select(row => (Selectable)row.Button).ToList();

        // RefreshViews reconstructs a hardcoded native navigation list. Insert our rows again
        // after every refresh; using the actual predecessor also handles hidden native rows.
        var anchorNav = anchor.navigation;
        var previous = anchorNav.selectOnUp;
        for (int i = 0; i < buttons.Count && previous && IsOurs(buttons, previous); i++)
            previous = previous.navigation.selectOnUp;
        if (previous && IsOurs(buttons, previous)) previous = null;

        for (int i = 0; i < buttons.Count; i++)
            SetVertical(buttons[i], i == 0 ? previous : buttons[i - 1], i == buttons.Count - 1 ? anchor : buttons[i + 1]);
        anchorNav.mode = Navigation.Mode.Explicit;
        anchorNav.selectOnUp = buttons[buttons.Count - 1];
        anchor.navigation = anchorNav;
        if (previous)
        {
            var previousNav = previous.navigation;
            previousNav.mode = Navigation.Mode.Explicit;
            previousNav.selectOnDown = buttons[0];
            previous.navigation = previousNav;
        }
    }

    private static bool IsOurs(List<Selectable> buttons, Selectable candidate)
    {
        foreach (var button in buttons)
            if (candidate == button) return true;
        return false;
    }

    private static void SetVertical(Selectable row, Selectable? up, Selectable down)
    {
        var nav = row.navigation;
        nav.mode = Navigation.Mode.Explicit;
        nav.selectOnUp = up;
        nav.selectOnDown = down;
        nav.selectOnLeft = null;
        nav.selectOnRight = null;
        row.navigation = nav;
    }

    /// <summary>Choices with no natural order cycle in both directions, like native toggles.</summary>
    private static int Cycle(int value, int direction, int count) => ((value + direction) % count + count) % count;

    private static void ChangeReceptorHeight(int direction, bool wrap)
    {
        int value = SettingsState.ReceptorHeight + direction;
        if (value > SettingsState.MaxReceptorHeight)
            value = wrap ? SettingsState.MinReceptorHeight : SettingsState.MaxReceptorHeight;
        else if (value < SettingsState.MinReceptorHeight)
            value = wrap ? SettingsState.MaxReceptorHeight : SettingsState.MinReceptorHeight;
        if (value != SettingsState.ReceptorHeight) SettingsState.SetReceptorHeight(value);
    }

    private static void SetNoteFlares(bool value)
    {
        SettingsState.SetNoteFlares(value);
        NoteSkins.RefreshFlares();
    }

    private static void ResetDefaultsPostfix()
    {
        try
        {
            SettingsState.SetMode(ScrollMode.Default);
            SettingsState.SetReceptorHeight(0);
            SettingsState.SetNoteSkin(NoteSkin.Default);
            SettingsState.SetTimingBar(false);
            SettingsState.SetTimingBarTop(false);
            SettingsState.NoteSize.Set(SettingsState.NoteSize.Default);
            SettingsState.LaneSpacing.Set(SettingsState.LaneSpacing.Default);
            SettingsState.EnemyAttackOpacity.Set(SettingsState.EnemyAttackOpacity.Default);
            if (!SettingsState.NoteFlares) SetNoteFlares(true);
            RefreshAll();
        }
        catch (Exception ex)
        {
            ModLog.Error($"Resetting flat-scroll options failed: {ex}");
        }
    }

    private sealed class RowSpec
    {
        internal readonly string Name;
        internal readonly string Title;
        internal readonly string Help;
        internal readonly string[] States;
        internal readonly Func<int> GetState;
        private readonly Action<int, bool> _change;

        internal RowSpec(string name, string title, string help, string[] states,
                         Func<int> getState, Action<int, bool> change)
        {
            Name = name;
            Title = title;
            Help = help;
            States = states;
            GetState = getState;
            _change = change;
        }

        /// <param name="wrap">Clicking cycles through every value; left and right stop at the ends.</param>
        internal void Change(int direction, bool wrap)
        {
            try
            {
                _change(direction, wrap);
                RefreshAll();
            }
            catch (Exception ex)
            {
                ModLog.Error($"Changing {Title} failed: {ex}");
            }
        }
    }

    private sealed class MenuRows
    {
        internal readonly GameplayOptionsMenu Menu;
        internal readonly List<OptionRow> Rows;

        internal MenuRows(GameplayOptionsMenu menu, List<OptionRow> rows)
        {
            Menu = menu;
            Rows = rows;
        }

        internal bool AllAlive => Rows.All(row => row.IsAlive);

        internal void Destroy()
        {
            foreach (var row in Rows) row.Destroy();
        }
    }

    private sealed class OptionRow
    {
        internal readonly GameObject Root;
        internal readonly CustomButton Button;
        internal readonly CustomToggleState Toggle;
        internal readonly RowSpec Spec;
        // Retain the managed delegate wrappers while their IL2CPP listeners are registered.
        internal readonly UnityAction Click;
        internal readonly Il2CppSystem.Action Next;
        internal readonly Il2CppSystem.Action Previous;

        internal OptionRow(GameObject root, CustomButton button, CustomToggleState toggle, RowSpec spec)
        {
            Root = root;
            Button = button;
            Toggle = toggle;
            Spec = spec;
            Click = DelegateSupport.ConvertDelegate<UnityAction>((Action)(() => spec.Change(1, true)))
                ?? throw new InvalidOperationException("Could not create native click listener.");
            Next = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)(() => spec.Change(1, false)))
                ?? throw new InvalidOperationException("Could not create native next-state listener.");
            Previous = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)(() => spec.Change(-1, false)))
                ?? throw new InvalidOperationException("Could not create native previous-state listener.");
        }

        internal bool IsAlive => Root && Button && Toggle;

        internal void Destroy()
        {
            if (Root) Object.Destroy(Root);
        }
    }
}
