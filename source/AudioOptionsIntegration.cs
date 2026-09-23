using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>
/// Adds the hit sound and miss sound settings to Options > Audio, under Sound Effects. Each
/// is an on/off switch (a copy of the Gameplay page's own switch) and a volume slider (a copy
/// of the Audio page's own slider).
/// </summary>
internal static class AudioOptionsIntegration
{
    // The rows appear under UI SFX, in this order.
    private static readonly SoundSpec[] Specs =
    {
        new("FlatHitSound",
            "Hit sound",
            "Plays a short tick each time you hit a note.",
            "Hit sound volume",
            () => SettingsState.HitSound,
            SettingsState.SetHitSound,
            SettingsState.HitSoundVolume,
            HitSound.Preview,
            HitSound.CancelPreview,
            null),
        // The switch is the game's own "Note Miss Sounds" setting, so it also changes that row.
        new("FlatMissSound",
            "Miss sound",
            "Plays the game's miss sound when you miss a note. The same setting as Note Miss Sounds under Gameplay.",
            "Miss sound volume",
            () => MissSound.Enabled,
            MissSound.SetEnabled,
            SettingsState.MissSoundVolume,
            MissSound.Preview,
            MissSound.CancelPreview,
            gameplay =>
            {
                var toggle = gameplay.missSoundEffectToggle;
                if (toggle && toggle.IsOn != MissSound.Enabled) toggle.SetValueInstant(MissSound.Enabled);
            }),
    };

    private static readonly Dictionary<int, AudioRows> Menus = new();
    private static bool syncing;

    internal static void Install(HarmonyLib.Harmony harmony) =>
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(AudioOptionsMenu), "Activate")
                ?? throw new MissingMethodException(typeof(AudioOptionsMenu).FullName, "Activate"),
            postfix: new HarmonyMethod(typeof(AudioOptionsIntegration), nameof(ActivatePostfix)));

    /// <summary>Covers an audio page that is open when the mod attaches.</summary>
    internal static void AttachToExisting()
    {
        foreach (var menu in Resources.FindObjectsOfTypeAll<AudioOptionsMenu>())
            if (menu && menu.gameObject.scene.IsValid()) ActivatePostfix(menu);
    }

    /// <summary>Called after the sound settings change anywhere, so the rows show them.</summary>
    internal static void RefreshAll()
    {
        foreach (var pair in Menus.ToArray())
        {
            if (!pair.Value.IsAlive) { Menus.Remove(pair.Key); continue; }
            pair.Value.Sync();
        }
    }

    private static void ActivatePostfix(AudioOptionsMenu __instance)
    {
        // The page's rows exist only once its contents are active, which Activate ensures.
        if (!__instance || !__instance.gameObject.activeInHierarchy) return;
        try
        {
            int id = __instance.GetInstanceID();
            if (!Menus.TryGetValue(id, out var rows) || !rows.IsAlive)
            {
                rows?.Destroy();
                rows = AudioRows.Create(__instance);
                if (rows == null) return;
                Menus[id] = rows;
                ModLog.Info("Added hit and miss sound rows to Options > Audio.");
            }
            rows.Sync();
            rows.Link();
        }
        catch (Exception ex)
        {
            // A failed optional row must not stop the audio page from opening.
            ModLog.Error($"Adding sound options failed: {ex}");
        }
    }

    private sealed class SoundSpec
    {
        internal readonly string Id, Title, Help, VolumeTitle;
        internal readonly Func<bool> IsOn;
        internal readonly Action<bool> SetOn;
        internal readonly PercentSetting Volume;
        internal readonly Action Preview, CancelPreview;
        // Updates the game's own row for the same setting, if there is one.
        internal readonly Action<GameplayOptionsMenu>? SyncNative;

        internal SoundSpec(string id, string title, string help, string volumeTitle, Func<bool> isOn,
                           Action<bool> setOn, PercentSetting volume, Action preview, Action cancelPreview,
                           Action<GameplayOptionsMenu>? syncNative)
        {
            Id = id;
            Title = title;
            Help = help;
            VolumeTitle = volumeTitle;
            IsOn = isOn;
            SetOn = setOn;
            Volume = volume;
            Preview = preview;
            CancelPreview = cancelPreview;
            SyncNative = syncNative;
        }
    }

    private sealed class AudioRows
    {
        private readonly AudioOptionsMenu _menu;
        private readonly List<SoundRows> _rows;

        private AudioRows(AudioOptionsMenu menu, List<SoundRows> rows)
        {
            _menu = menu;
            _rows = rows;
        }

        internal bool IsAlive => _menu && _rows.All(row => row.IsAlive);

        internal static AudioRows? Create(AudioOptionsMenu menu)
        {
            var ui = menu.uiSfxVolumeSlider;
            var scroll = menu.scrollRect;
            if (!ui || !scroll || !menu.masterVolumeSlider) return null;
            var content = scroll.content;
            var uiRow = ui.transform.parent ? ui.transform.parent.TryCast<RectTransform>() : null;
            if (!content || !uiRow || uiRow!.parent != content) return null;
            // The switches are copies of the Gameplay page's own on/off row ("Note Miss Sounds").
            // That page is a sibling panel, inactive while it is not shown.
            var gameplay = menu.transform.parent ? menu.transform.parent.GetComponentInChildren<GameplayOptionsMenu>(true) : null;
            var template = gameplay ? gameplay!.missSoundEffectButton : null;
            if (!gameplay || !template) return null;
            var uiRect = ui.transform.TryCast<RectTransform>();
            float indent = uiRect ? uiRect!.offsetMin.x : 20f;

            var rows = new List<SoundRows>();
            try
            {
                int index = uiRow.GetSiblingIndex() + 1;
                foreach (var spec in Specs)
                {
                    var row = SoundRows.Create(spec, gameplay!, content, uiRow, template!, indent, index);
                    rows.Add(row);
                    index = row.NextSiblingIndex;
                }
                LayoutRebuilder.MarkLayoutForRebuild(content);
                return new AudioRows(menu, rows);
            }
            catch
            {
                foreach (var row in rows) row.Destroy();
                throw;
            }
        }

        internal void Sync()
        {
            foreach (var row in _rows) row.Sync();
        }

        /// <summary>Chains the rows after UI SFX; the last one wraps back to Master volume.</summary>
        internal void Link()
        {
            var ui = _menu.uiSfxVolumeSlider;
            var master = _menu.masterVolumeSlider;
            var chain = new List<Selectable> { ui };
            foreach (var row in _rows)
            {
                chain.Add(row.Button);
                chain.Add(row.Slider);
            }
            chain.Add(master);
            for (int i = 1; i < chain.Count - 1; i++) SetVertical(chain[i], chain[i - 1], chain[i + 1]);
            var nav = ui.navigation;
            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnDown = chain[1];
            ui.navigation = nav;
            nav = master.navigation;
            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnUp = chain[chain.Count - 2];
            master.navigation = nav;
        }

        private static void SetVertical(Selectable row, Selectable up, Selectable down)
        {
            var nav = row.navigation;
            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnUp = up;
            nav.selectOnDown = down;
            nav.selectOnLeft = null;
            nav.selectOnRight = null;
            row.navigation = nav;
        }

        internal void Destroy()
        {
            foreach (var row in _rows) row.Destroy();
        }
    }

    /// <summary>One sound's switch and volume slider.</summary>
    private sealed class SoundRows
    {
        private readonly SoundSpec _spec;
        private readonly GameplayOptionsMenu _gameplay;
        private readonly GameObject _toggleRoot, _sliderRoot;
        private readonly CustomToggleButton _toggle;
        private readonly CustomSliderSegments? _segments;
        // The native callbacks hold these wrappers; keep them alive as long as the rows.
        private readonly UnityAction _click;
        private readonly Il2CppSystem.Action _toggled;
        private readonly UnityAction<int> _volumeChanged;
        private readonly Il2CppSystem.Func<float, string> _formatVolume;
        internal readonly CustomButton Button;
        internal readonly CustomSlider Slider;

        private SoundRows(SoundSpec spec, GameplayOptionsMenu gameplay, GameObject toggleRoot, CustomButton button,
                          CustomToggleButton toggle, GameObject sliderRoot, CustomSlider slider)
        {
            _spec = spec;
            _gameplay = gameplay;
            _toggleRoot = toggleRoot;
            Button = button;
            _toggle = toggle;
            _sliderRoot = sliderRoot;
            Slider = slider;
            _segments = slider.GetComponent<CustomSliderSegments>();
            _click = DelegateSupport.ConvertDelegate<UnityAction>((Action)Clicked)
                ?? throw new InvalidOperationException($"Could not create the {spec.Title} click listener.");
            _toggled = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)Toggled)
                ?? throw new InvalidOperationException($"Could not create the {spec.Title} switch listener.");
            _volumeChanged = DelegateSupport.ConvertDelegate<UnityAction<int>>((Action<int>)VolumeChanged)
                ?? throw new InvalidOperationException($"Could not create the {spec.VolumeTitle} listener.");
            _formatVolume = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<float, string>>((Func<float, string>)FormatVolume)
                ?? throw new InvalidOperationException($"Could not create the {spec.VolumeTitle} label.");
            Button.onClick.AddListener(_click);
            _toggle.OnToggleValue = _toggled;
            Slider.onValueChangedInt.AddListener(_volumeChanged);
            // The page's own label shows the slider's fraction of its range, which is only
            // the volume when the range ends at 100.
            if (_segments) _segments!.FormatTextFunc = _formatVolume;
        }

        internal bool IsAlive => _toggleRoot && _sliderRoot && Button && _toggle && Slider;

        internal int NextSiblingIndex => _sliderRoot.transform.GetSiblingIndex() + 1;

        internal static SoundRows Create(SoundSpec spec, GameplayOptionsMenu gameplay, RectTransform content,
                                         RectTransform uiRow, CustomButton template, float indent, int siblingIndex)
        {
            GameObject? toggleRoot = null, sliderRoot = null;
            try
            {
                // Switch row: a wrapper the size of the page's rows, holding the Gameplay switch.
                toggleRoot = new GameObject("Toggle_" + spec.Id);
                toggleRoot.layer = content.gameObject.layer;
                var wrapper = toggleRoot.AddComponent<RectTransform>();
                wrapper.SetParent(content, false);
                wrapper.anchorMin = uiRow.anchorMin;
                wrapper.anchorMax = uiRow.anchorMax;
                wrapper.pivot = uiRow.pivot;
                wrapper.sizeDelta = uiRow.sizeDelta;
                wrapper.SetSiblingIndex(siblingIndex);
                var row = Object.Instantiate(template.gameObject, wrapper, false);
                row.name = "OnOffToggle_" + spec.Id;
                var rowRect = row.GetComponent<RectTransform>();
                rowRect.anchorMin = Vector2.zero;
                rowRect.anchorMax = Vector2.one;
                rowRect.pivot = new Vector2(0f, 0.5f);
                rowRect.offsetMin = new Vector2(indent, 0f);
                rowRect.offsetMax = Vector2.zero;
                StripLabelLocalization(row);
                var button = row.GetComponent<CustomButton>();
                var toggle = row.GetComponent<CustomToggleButton>();
                if (!button || !toggle) throw new InvalidOperationException("The on/off row has no button or switch.");
                button.FirstSelection = false;
                button.RepeatOnHold = false;
                button.Text = spec.Title;
                button.ButtonHelpText = spec.Help;
                var helpKey = button.LocalizedHelpKey;
                helpKey.mTerm = string.Empty;
                button.LocalizedHelpKey = helpKey;
                button.DataContextOnClick.RemoveAllListeners();
                button.onClick = new Button.ButtonClickedEvent();

                // Volume row: a copy of the page's UI SFX slider row.
                sliderRoot = Object.Instantiate(uiRow.gameObject, content, false);
                sliderRoot.name = "Slider_Sfx_" + spec.Id + "Volume";
                sliderRoot.transform.SetSiblingIndex(wrapper.GetSiblingIndex() + 1);
                StripLabelLocalization(sliderRoot);
                var slider = sliderRoot.GetComponentInChildren<CustomSlider>(true);
                if (!slider) throw new InvalidOperationException("The volume row has no slider.");
                slider.gameObject.name = "Slider_" + spec.Id + "Volume";
                slider.FirstSelection = false;
                slider.wholeNumbers = true;
                slider.minValue = 0f;
                slider.maxValue = spec.Volume.Max;
                slider.SetStepSize(spec.Volume.Step);
                // The preview replaces the slider's own tick.
                slider.PlayValueChangeSfx = false;
                slider.Text = spec.VolumeTitle;

                return new SoundRows(spec, gameplay, toggleRoot, button, toggle, sliderRoot, slider);
            }
            catch
            {
                if (toggleRoot) Object.Destroy(toggleRoot);
                if (sliderRoot) Object.Destroy(sliderRoot);
                throw;
            }
        }

        /// <summary>
        /// Copied labels would switch back to the original row's title when the language changes.
        /// Localizers with the term "-" only pick the font, so they stay.
        /// </summary>
        private static void StripLabelLocalization(GameObject row)
        {
            foreach (var localizer in row.GetComponentsInChildren<Localize>(true))
            {
                if (localizer.mTerm == "-") continue;
                localizer.enabled = false;
                Object.Destroy(localizer);
            }
        }

        internal void Sync()
        {
            syncing = true;
            try
            {
                // Left, right and clicks have already started the switch's own slide; only snap it
                // when the setting changed somewhere else.
                bool on = _spec.IsOn();
                if (_toggle.IsOn != on) _toggle.SetValueInstant(on);
                Slider.SetValueWithoutNotify(_spec.Volume.Value);
                if (_segments) _segments!.Refresh();
            }
            finally { syncing = false; }
        }

        private string FormatVolume(float fraction) => PercentSetting.Format(Mathf.RoundToInt(Slider.value));

        private void Clicked()
        {
            try
            {
                _spec.SetOn(!_spec.IsOn());
                _toggle.IsOn = _spec.IsOn();
                Changed();
            }
            catch (Exception ex) { ModLog.Error($"Changing {_spec.Title} failed: {ex}"); }
        }

        /// <summary>Left and right on the switch; the game calls this even when nothing changed.</summary>
        private void Toggled()
        {
            try
            {
                if (syncing || _toggle.IsOn == _spec.IsOn()) return;
                _spec.SetOn(_toggle.IsOn);
                Changed();
            }
            catch (Exception ex) { ModLog.Error($"Changing {_spec.Title} failed: {ex}"); }
        }

        private void Changed()
        {
            if (_spec.IsOn()) _spec.Preview();
            else _spec.CancelPreview();
            if (_spec.SyncNative != null && _gameplay) _spec.SyncNative(_gameplay);
            OptionsMenuIntegration.RefreshAll();
        }

        private void VolumeChanged(int value)
        {
            if (syncing) return;
            try
            {
                var setting = _spec.Volume;
                int clamped = Math.Clamp((value + setting.Step / 2) / setting.Step * setting.Step, setting.Min, setting.Max);
                if (clamped != value)
                {
                    // The slider starts at 0; the volume stops at its minimum.
                    syncing = true;
                    try
                    {
                        Slider.SetValueWithoutNotify(clamped);
                        if (_segments) _segments!.Refresh();
                    }
                    finally { syncing = false; }
                }
                if (clamped == setting.Value) return;
                setting.Set(clamped);
                _spec.Preview();
                OptionsMenuIntegration.RefreshAll();
            }
            catch (Exception ex) { ModLog.Error($"Changing {_spec.VolumeTitle} failed: {ex}"); }
        }

        internal void Destroy()
        {
            if (_toggleRoot) Object.Destroy(_toggleRoot);
            if (_sliderRoot) Object.Destroy(_sliderRoot);
        }
    }
}
