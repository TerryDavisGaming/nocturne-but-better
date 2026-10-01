using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NocturnePlus;

/// <summary>
/// Adds the Performance row (Normal, Optimized, Potato; see Performance) to Options > Graphics,
/// under Corruption Effects. It's a copy of that row, and the page can also be opened from a
/// battle's pause menu.
/// </summary>
internal static class GraphicsOptionsIntegration
{
    private const string RowName = "StateToggle_PlusPerformance";
    private static readonly string[] States = { "Normal", "Optimized", "Potato" };
    private static readonly Dictionary<int, PerformanceRow> Menus = new();

    internal static void Install(HarmonyLib.Harmony harmony) =>
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(GraphicsOptionsMenu), "Activate")
                ?? throw new MissingMethodException(typeof(GraphicsOptionsMenu).FullName, "Activate"),
            postfix: new HarmonyMethod(typeof(GraphicsOptionsIntegration), nameof(ActivatePostfix)));

    private static void ActivatePostfix(GraphicsOptionsMenu __instance)
    {
        // The page's rows exist only once it's active, which Activate ensures.
        if (!__instance || !__instance.gameObject.activeInHierarchy) return;
        try
        {
            int id = __instance.GetInstanceID();
            if (!Menus.TryGetValue(id, out var row) || !row.IsAlive)
            {
                row?.Destroy();
                row = PerformanceRow.Create(__instance);
                if (row == null) return;
                Menus[id] = row;
                ModLog.Info("Added the Performance row to Options > Graphics.");
            }
            row.Refresh();
            row.Link();
        }
        catch (Exception ex)
        {
            // A failed optional row must not stop the graphics page from opening.
            ModLog.Error($"Adding the Performance row failed: {ex}");
        }
    }

    /// <summary>Shows the setting's current value on every Performance row.</summary>
    internal static void RefreshAll()
    {
        foreach (var pair in Menus.ToArray())
        {
            if (!pair.Value.IsAlive) { Menus.Remove(pair.Key); continue; }
            pair.Value.Refresh();
        }
    }

    /// <summary>After Potato changed the game's own settings, the page's rows show their new values.</summary>
    internal static void RefreshGameRows()
    {
        foreach (var row in Menus.Values)
            if (row.IsAlive && row.Menu.gameObject.activeInHierarchy) row.Menu.RefreshText();
    }

    private static void Change(int direction, bool wrap)
    {
        try
        {
            int current = (int)SettingsState.Performance, count = States.Length;
            int next = wrap ? ((current + direction) % count + count) % count : Math.Clamp(current + direction, 0, count - 1);
            if (next != current) Performance.SetMode((PerformanceMode)next);
            RefreshAll();
            RefreshGameRows();
        }
        catch (Exception ex) { ModLog.Error($"Changing Performance failed: {ex}"); }
    }

    private sealed class PerformanceRow
    {
        internal readonly GraphicsOptionsMenu Menu;
        private readonly GameObject root;
        private readonly CustomButton button;
        private readonly CustomToggleState toggle;
        // The row the Corruption Effects row led down to before this row was linked in.
        private Selectable? next;
        // The native callbacks hold these wrappers; keep them alive as long as the row.
        private readonly UnityAction click;
        private readonly Il2CppSystem.Action nextState, previousState;

        private PerformanceRow(GraphicsOptionsMenu menu, GameObject root, CustomButton button, CustomToggleState toggle)
        {
            Menu = menu;
            this.root = root;
            this.button = button;
            this.toggle = toggle;
            click = DelegateSupport.ConvertDelegate<UnityAction>((Action)(() => Change(1, true)))
                ?? throw new InvalidOperationException("Could not create the Performance click listener.");
            nextState = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)(() => Change(1, false)))
                ?? throw new InvalidOperationException("Could not create the Performance next listener.");
            previousState = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)(() => Change(-1, false)))
                ?? throw new InvalidOperationException("Could not create the Performance previous listener.");
            button.onClick.AddListener(click);
            toggle.NextState = nextState;
            toggle.PreviousState = previousState;
        }

        internal bool IsAlive => Menu && root && button && toggle;

        internal static PerformanceRow? Create(GraphicsOptionsMenu menu)
        {
            var template = menu.corruptionEffectsButton;
            if (!template) return null;
            var clone = Object.Instantiate(template.gameObject, template.transform.parent, false);
            try
            {
                clone.name = RowName;
                var button = clone.GetComponent<CustomButton>();
                var toggle = clone.GetComponentInChildren<CustomToggleState>(true);
                if (!button || !toggle) throw new InvalidOperationException("The Corruption Effects row has no button or value.");
                OptionsMenuIntegration.EnsureToggleLayout(toggle);
                // Copied localization terms would put the old row's text back on a language change.
                foreach (var localizer in clone.GetComponentsInChildren<Localize>(true))
                {
                    localizer.enabled = false;
                    Object.Destroy(localizer);
                }
                button.FirstSelection = false;
                button.RepeatOnHold = false;
                button.Text = "Performance";
                var helpKey = button.LocalizedHelpKey;
                helpKey.mTerm = string.Empty;
                button.LocalizedHelpKey = helpKey;
                button.DataContextOnClick.RemoveAllListeners();
                // Replacing, rather than adding to, the click event keeps the copied row's action from firing.
                button.onClick = new Button.ButtonClickedEvent();
                toggle.localizeStates = false;
                var list = new Il2CppSystem.Collections.Generic.List<string>();
                foreach (var state in States) list.Add(state);
                toggle.PopulateStates(list);
                var row = new PerformanceRow(menu, clone, button, toggle);
                clone.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);
                clone.SetActive(true);
                if (clone.transform.parent.TryCast<RectTransform>() is { } content) LayoutRebuilder.MarkLayoutForRebuild(content);
                return row;
            }
            catch
            {
                Object.Destroy(clone);
                throw;
            }
        }

        internal void Refresh()
        {
            OptionsMenuIntegration.EnsureToggleLayout(toggle);
            button.Text = "Performance";
            button.ButtonHelpText = Help();
            toggle.State = (int)SettingsState.Performance;
            toggle.Refresh();
            toggle.UpdatePreferredTextWidth();
        }

        private string Help()
        {
            string backdrop = Label(Menu.combatBackdropButton, "Combat Backgrounds"), corruption = Label(Menu.corruptionEffectsButton, "Corruption Effects");
            return SettingsState.Performance switch
            {
                PerformanceMode.Optimized => "Optimized: the mod does less work each frame, and loading gets more of each frame behind the black screen. The game looks and plays exactly as it does on Normal.",
                PerformanceMode.Potato => $"Potato, for weak PCs: quicker fades and loading screens, lighter bloom, blur and 2D lights, a lower resolution on 4K screens, and the lighter {backdrop} and {corruption} until you leave Potato.",
                _ => "Normal: the game as it ships. Optimized does less work each frame and looks exactly the same. Potato is for weak PCs.",
            };
        }

        private static string Label(CustomButton row, string fallback)
        {
            try
            {
                string text = row ? row.Text : "";
                return string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
            }
            catch { return fallback; }
        }

        /// <summary>
        /// Links the row under Corruption Effects, handing on whatever that row led down to (the page
        /// wraps to its top row). The game never rebuilds this page's links, but this runs on every
        /// Activate anyway.
        /// </summary>
        internal void Link()
        {
            var above = Menu.corruptionEffectsButton;
            if (!above) return;
            var below = above.navigation.selectOnDown;
            if (below && below == button) below = next;
            else next = below;
            var nav = button.navigation;
            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnUp = above;
            nav.selectOnDown = below ? below : null;
            nav.selectOnLeft = null;
            nav.selectOnRight = null;
            button.navigation = nav;
            var aboveNav = above.navigation;
            aboveNav.mode = Navigation.Mode.Explicit;
            aboveNav.selectOnDown = button;
            above.navigation = aboveNav;
            if (below && below!.navigation.selectOnUp == above)
            {
                var belowNav = below.navigation;
                belowNav.mode = Navigation.Mode.Explicit;
                belowNav.selectOnUp = button;
                below.navigation = belowNav;
            }
        }

        internal void Destroy()
        {
            if (root) Object.Destroy(root);
        }
    }
}
