using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Nocturne;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>Adds a native gameplay-options row without replacing an existing setting.</summary>
internal static class OptionsMenuIntegration
{
    private const string RowName = "StateToggle_FlatNoteScrolling";
    private const string Title = "Note scrolling";
    private const string Help = "Choose the original layout, flat downscroll, or flat upscroll.";
    private static readonly Dictionary<int, MenuRow> Rows = new();
    private static bool installed;
    private static bool refreshing;

    internal static void Install(Harmony harmony)
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
        // Also supports loading the plugin after an options panel was instantiated.
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
            foreach (var pair in Rows.ToArray())
            {
                var row = pair.Value;
                if (!row.Menu || !row.Button || !row.Toggle)
                {
                    Rows.Remove(pair.Key);
                    continue;
                }
                RefreshRow(row);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Refreshing note-scrolling option failed: {ex}");
        }
        finally { refreshing = false; }
    }

    private static void MenuReadyPostfix(GameplayOptionsMenu __instance)
    {
        if (!__instance || refreshing) return;
        // MenuPanel.Awake can hide its object before this postfix runs. The subsequent
        // Activate/DidShow hook creates the row once native child lifecycle methods can run.
        if (!__instance.gameObject.activeInHierarchy) return;
        try
        {
            var id = __instance.GetInstanceID();
            if (!Rows.TryGetValue(id, out var row) || !row.Button)
            {
                row = CreateRow(__instance);
                if (row == null) return;
                Rows[id] = row;
            }
            RefreshRow(row);
        }
        catch (Exception ex)
        {
            // A failed optional menu extension must not prevent the original menu opening.
            Plugin.Log.LogError($"Adding note-scrolling option failed: {ex}");
        }
    }

    private static MenuRow? CreateRow(GameplayOptionsMenu menu)
    {
        var template = menu.runModeButton;
        var anchor = menu.noteSpeedModButton;
        if (!template || !anchor) return null;
        var parent = anchor.transform.parent;
        if (!parent) return null;

        GameObject? clone = null;
        try
        {
            clone = Object.Instantiate(template.gameObject, parent, false);
            clone.name = RowName;
            var button = clone.GetComponent<CustomButton>();
            var toggle = clone.GetComponentInChildren<CustomToggleState>(true);
            if (!button || !toggle)
                throw new InvalidOperationException("Native run-mode row has no button/state control.");
            EnsureToggleLayout(toggle);

            // Copied localization terms must not put the old row's text back on language changes.
            foreach (var localizer in clone.GetComponentsInChildren<I2.Loc.Localize>(true))
            {
                localizer.enabled = false;
                Object.Destroy(localizer);
            }

            button.FirstSelection = false;
            button.RepeatOnHold = false;
            button.Text = Title;
            button.ButtonHelpText = Help;
            var helpKey = button.LocalizedHelpKey;
            helpKey.mTerm = string.Empty;
            button.LocalizedHelpKey = helpKey;
            button.DataContextOnClick.RemoveAllListeners();
            button.onClick = new Button.ButtonClickedEvent();

            toggle.localizeStates = false;
            var states = new Il2CppSystem.Collections.Generic.List<string>();
            states.Add("Default");
            states.Add("2D Downscroll");
            states.Add("2D Upscroll");
            toggle.PopulateStates(states);

            var row = new MenuRow(menu, button, toggle);
            // Replacing, rather than appending, prevents the cloned Run Mode action from firing.
            button.onClick.AddListener(row.Click);
            toggle.NextState = row.NextMode;
            toggle.PreviousState = row.PreviousMode;

            clone.transform.SetSiblingIndex(anchor.transform.GetSiblingIndex());
            clone.SetActive(true);
            toggle.UpdatePreferredTextWidth();
            if (parent.TryCast<RectTransform>() is { } content)
                LayoutRebuilder.MarkLayoutForRebuild(content);
            Plugin.Log.LogInfo("Added Note scrolling to Options > Gameplay.");
            return row;
        }
        catch
        {
            if (clone) Object.Destroy(clone);
            throw;
        }
    }

    private static void RefreshRow(MenuRow row)
    {
        EnsureToggleLayout(row.Toggle);
        row.Button.Text = Title;
        row.Button.ButtonHelpText = Help;
        row.Toggle.State = (int)SettingsState.Mode;
        row.Toggle.Refresh();
        row.Toggle.UpdatePreferredTextWidth();
        InsertIntoNavigation(row);
    }

    private static void EnsureToggleLayout(CustomToggleState toggle)
    {
        // This private, nonserialized cache is normally initialized by native Awake. Resolve
        // it explicitly because Unity can instantiate a row below an inactive parent.
        if (!toggle.label)
        {
            var value = toggle.transform.Find("Label_Value");
            if (value) toggle.label = value.GetComponent<TMPro.TMP_Text>();
        }
        if (!toggle.label)
            throw new InvalidOperationException("Note-scrolling state label is missing.");
        if (!toggle.overridePreferredWidth || toggle._layoutElement) return;
        var layout = toggle.label.GetComponent<LayoutElement>();
        if (!layout) layout = toggle.label.gameObject.AddComponent<LayoutElement>();
        toggle._layoutElement = layout;
    }

    private static void InsertIntoNavigation(MenuRow row)
    {
        var anchor = row.Menu.noteSpeedModButton;
        if (!anchor) return;

        // RefreshViews reconstructs a hardcoded native navigation list. Insert our row again
        // after every refresh; using the actual predecessor also handles hidden native rows.
        var anchorNav = anchor.navigation;
        var previous = anchorNav.selectOnUp;
        if (previous == row.Button) previous = row.Button.navigation.selectOnUp;
        var rowNav = row.Button.navigation;
        rowNav.mode = Navigation.Mode.Explicit;
        rowNav.selectOnUp = previous;
        rowNav.selectOnDown = anchor;
        rowNav.selectOnLeft = null;
        rowNav.selectOnRight = null;
        row.Button.navigation = rowNav;
        anchorNav.mode = Navigation.Mode.Explicit;
        anchorNav.selectOnUp = row.Button;
        anchor.navigation = anchorNav;
        if (previous && previous != row.Button)
        {
            var previousNav = previous.navigation;
            previousNav.mode = Navigation.Mode.Explicit;
            previousNav.selectOnDown = row.Button;
            previous.navigation = previousNav;
        }
    }

    private static void NextMode() => ChangeMode(1);
    private static void PreviousMode() => ChangeMode(-1);

    private static void ChangeMode(int direction)
    {
        try
        {
            var mode = (ScrollMode)(((int)SettingsState.Mode + direction + 3) % 3);
            SettingsState.SetMode(mode);
            RefreshAll();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Changing note-scrolling mode failed: {ex}");
        }
    }

    private static void ResetDefaultsPostfix()
    {
        try
        {
            SettingsState.SetMode(ScrollMode.Default);
            RefreshAll();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Resetting note-scrolling option failed: {ex}");
        }
    }

    private sealed class MenuRow
    {
        internal readonly GameplayOptionsMenu Menu;
        internal readonly CustomButton Button;
        internal readonly CustomToggleState Toggle;
        // Retain the managed delegate wrappers while their IL2CPP listeners are registered.
        internal readonly UnityAction Click;
        internal readonly Il2CppSystem.Action NextMode;
        internal readonly Il2CppSystem.Action PreviousMode;

        internal MenuRow(GameplayOptionsMenu menu, CustomButton button, CustomToggleState toggle)
        {
            Menu = menu;
            Button = button;
            Toggle = toggle;
            Click = DelegateSupport.ConvertDelegate<UnityAction>((Action)OptionsMenuIntegration.NextMode)
                ?? throw new InvalidOperationException("Could not create native click listener.");
            NextMode = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)OptionsMenuIntegration.NextMode)
                ?? throw new InvalidOperationException("Could not create native next-mode listener.");
            PreviousMode = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)OptionsMenuIntegration.PreviousMode)
                ?? throw new InvalidOperationException("Could not create native previous-mode listener.");
        }
    }
}
