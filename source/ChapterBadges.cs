using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>
/// In Arcade and High Scores, the game marks an encounter whose melodies are all full-combo'd
/// with gold notes and two sparkles on its card. This does the same for a chapter button once
/// every encounter in that chapter has its sparkles: a gold label and the same two sparkles.
/// </summary>
internal static class ChapterBadges
{
    private const string SparklesName = "FlatChapterSparkles";
    private const float RefreshSeconds = 0.25f;

    // Chapter buttons and the ArcadeCategory they open, filled as the game builds them.
    private static readonly Dictionary<IntPtr, ArcadeCategory> ButtonCategories = new();
    // Labels this turned gold, with the colour they had before.
    private static readonly Dictionary<IntPtr, Color> GoldLabels = new();
    // The Arcade and High Scores screens seen so far; there are only a couple.
    private static readonly List<GenericArcadeMenuV2> Menus = new();
    private static float nextRefresh;
    private static bool reportedError;

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        Patch(harmony, typeof(GenericArcadeMenuV2), "PopulateCategoryButton", nameof(PopulatePostfix));
        Patch(harmony, typeof(GenericArcadeMenuV2), "Activate", nameof(ActivatePostfix));
    }

    private static void Patch(HarmonyLib.Harmony harmony, Type type, string method, string postfix)
    {
        var target = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.FullName, method);
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(ChapterBadges), postfix));
    }

    private static void PopulatePostfix(CustomButton categoryButton, ArcadeCategory category)
    {
        if (categoryButton && category != null) ButtonCategories[categoryButton.Pointer] = category;
    }

    private static void ActivatePostfix(GenericArcadeMenuV2 __instance)
    {
        if (!__instance) return;
        Menus.RemoveAll(menu => !menu);
        foreach (var menu in Menus) if (menu.Pointer == __instance.Pointer) return;
        Menus.Add(__instance);
    }

    /// <summary>
    /// Called every frame; four times a second it rechecks an open Arcade or High Scores screen,
    /// which also follows its difficulty tabs and newly set ranks.
    /// </summary>
    internal static void Update()
    {
        if (Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + RefreshSeconds;
        if (Menus.Count == 0) return;
        try
        {
            foreach (var menu in Menus)
                if (menu && menu.gameObject.activeInHierarchy) Refresh(menu);
        }
        catch (Exception ex)
        {
            if (reportedError) return;
            reportedError = true;
            ModLog.Error("Chapter badges failed: " + ex);
        }
    }

    private static void Refresh(GenericArcadeMenuV2 menu)
    {
        var groups = menu._songGroupList;
        var parent = menu.categoryButtonParent;
        if (groups == null || !parent) return;

        // A chapter is complete when it has encounters and every one of them shows its sparkles.
        var total = new Dictionary<int, int>();
        var starred = new Dictionary<int, int>();
        ArcadeSongGroup? sample = null;
        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (!group || !group.gameObject.activeInHierarchy || !group.starGroup) continue;
            sample ??= group;
            int category = group.CategoryIndex;
            total[category] = total.GetValueOrDefault(category) + 1;
            if (group.starGroup.activeSelf) starred[category] = starred.GetValueOrDefault(category) + 1;
        }
        if (sample == null) return;

        for (int i = 0; i < parent.childCount; i++)
        {
            var button = parent.GetChild(i).GetComponent<CustomButton>();
            if (!button || !button.gameObject.activeInHierarchy || !ButtonCategories.TryGetValue(button.Pointer, out var category) || category == null) continue;
            int index = category.Index;
            // Every song the screen lists for the chapter needs a card with its sparkles; a song
            // without a card (still locked, say) keeps the chapter from counting as mastered.
            int songs = 0;
            var list = category.arcadeSongs;
            if (list != null)
                for (int s = 0; s < list.Count; s++)
                    if (list[s] != null && menu.ShouldShowSong(list[s])) songs++;
            int cards = total.GetValueOrDefault(index);
            bool complete = cards > 0 && cards >= songs && starred.GetValueOrDefault(index) == cards;
            Apply(button, complete, sample);
        }
    }

    private static void Apply(CustomButton button, bool complete, ArcadeSongGroup sample)
    {
        var label = button.GetComponentInChildren<TMP_Text>(true);
        if (label)
        {
            if (complete)
            {
                if (!GoldLabels.ContainsKey(label.Pointer)) GoldLabels[label.Pointer] = label.color;
                if (label.color != sample.fullComboMelodyColor) label.color = sample.fullComboMelodyColor;
            }
            else if (GoldLabels.TryGetValue(label.Pointer, out var before))
            {
                label.color = before;
                GoldLabels.Remove(label.Pointer);
            }
        }

        var sparkles = button.transform.Find(SparklesName);
        if (!complete)
        {
            if (sparkles) sparkles.gameObject.SetActive(false);
            return;
        }
        if (!sparkles) sparkles = CreateSparkles(button, sample);
        if (sparkles && !sparkles.gameObject.activeSelf) sparkles.gameObject.SetActive(true);
    }

    /// <summary>Copies the card's two coloured stars onto the chapter button's corners.</summary>
    private static Transform? CreateSparkles(CustomButton button, ArcadeSongGroup sample)
    {
        var source = sample.starGroup.transform;
        var copy = Object.Instantiate(source.gameObject, button.transform, false);
        copy.name = SparklesName;
        var rect = copy.GetComponent<RectTransform>();
        var buttonRect = button.GetComponent<RectTransform>();
        if (!rect || !buttonRect) { Object.Destroy(copy); return null; }
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = buttonRect.rect.size;
        copy.transform.localScale = Vector3.one;
        var layout = copy.GetComponent<LayoutElement>();
        if (!layout) layout = copy.AddComponent<LayoutElement>();
        layout.ignoreLayout = true;

        float halfW = buttonRect.rect.width / 2f, halfH = buttonRect.rect.height / 2f;
        for (int i = 0; i < copy.transform.childCount; i++)
        {
            var star = copy.transform.GetChild(i);
            var starRect = star.GetComponent<RectTransform>();
            // The coloured stars show; the dimmed "NotSelected" copies are for unselected cards.
            bool coloured = !star.name.Contains("NotSelected");
            star.gameObject.SetActive(coloured);
            if (!starRect) continue;
            // Same corners as on the card: the first star top-left, the second bottom-right.
            bool second = star.name.StartsWith("Star2");
            starRect.anchorMin = starRect.anchorMax = new Vector2(0.5f, 0.5f);
            starRect.anchoredPosition = second ? new Vector2(halfW - 2f, -halfH + 2f) : new Vector2(-halfW + 2f, halfH - 2f);
            starRect.localScale = Vector3.one * 0.8f;
        }
        foreach (var graphic in copy.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
        copy.SetActive(true);
        return copy.transform;
    }
}
