using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace NocturneFlatScroll;

/// <summary>
/// Writes "but better" and "by TerryDavisGaming" under the nocturne logo, both on the title
/// screen and on the logo card of the startup intro.
/// </summary>
internal static class TitleBranding
{
    private const string MainText = "but better";
    private const string CreditText = "by TerryDavisGaming";
    private const string MainName = "FlatScroll_ButBetter";
    private const string CreditName = "FlatScroll_Credit";
    // The game's pixel font is a 12-point bitmap face; whole multiples keep it crisp.
    private const float MainSize = 24f;
    private const float CreditSize = 12f;
    private const string ShadowSuffix = "_Shadow";
    // A soft offset shadow keeps the text readable over the bright title background.
    private static readonly Vector2 ShadowOffset = new(1.5f, -1.5f);
    private const float ShadowAlpha = 0.55f;
    private static readonly Color LogoWhite = new(0.988f, 1f, 0.961f, 1f);
    private static TMP_Text? fontSource;
    private static PropertyInfo? introScreen;

    internal static void InstallTitle(HarmonyLib.Harmony harmony) =>
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(MainMenu), "Activate")
                ?? throw new MissingMethodException(typeof(MainMenu).FullName, "Activate"),
            postfix: new HarmonyMethod(typeof(TitleBranding), nameof(MainMenuPostfix)));

    internal static void InstallIntro(HarmonyLib.Harmony harmony)
    {
        // The intro coroutine's class is compiler generated, and its number can change when
        // the game adds methods, so look it up by prefix instead of naming it.
        var sequence = typeof(TitleScreen).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(type => type.Name.StartsWith("_RunTitleSequence_d__", StringComparison.Ordinal))
            ?? throw new MissingMemberException(typeof(TitleScreen).FullName, "RunTitleSequence");
        introScreen = AccessTools.Property(sequence, "__4__this")
            ?? throw new MissingMemberException(sequence.FullName, "__4__this");
        harmony.Patch(
            AccessTools.DeclaredMethod(sequence, "MoveNext")
                ?? throw new MissingMethodException(sequence.FullName, "MoveNext"),
            postfix: new HarmonyMethod(typeof(TitleBranding), nameof(IntroPostfix)));
    }

    /// <summary>Covers a title menu that was shown before the patches were installed.</summary>
    internal static void AttachToExisting()
    {
        foreach (var menu in Resources.FindObjectsOfTypeAll<MainMenu>())
            if (menu && menu.gameObject.scene.IsValid()) Decorate(menu);
    }

    private static void MainMenuPostfix(MainMenu __instance)
    {
        try { if (__instance) Decorate(__instance); }
        catch (Exception ex) { ReportOnce(ex); }
    }

    private static void Decorate(MainMenu menu)
    {
        var content = menu.Content ? menu.Content.transform : menu.transform.Find("Contents");
        if (!content) return;
        RememberFont(menu);
        // Story progress swaps between several logo variants; label each so the text
        // follows whichever one is showing.
        for (int i = 0; i < content.childCount; i++)
        {
            var logo = content.GetChild(i);
            if (logo.name.StartsWith("Logo_", StringComparison.Ordinal)) AddLines(logo, -1f);
        }
    }

    /// <summary>Runs every frame of the intro so the text fades with the logo card.</summary>
    private static void IntroPostfix(object __instance)
    {
        try
        {
            var screen = introScreen?.GetValue(__instance) as TitleScreen;
            if (!screen) return;
            var logo = screen.splashImage2;
            if (!logo) return;
            AddLines(logo.transform, -4f);
            // The card fades through Image.color, which child text does not inherit.
            float alpha = logo.color.a;
            SetAlpha(logo.transform.Find(MainName), alpha);
            SetAlpha(logo.transform.Find(CreditName), alpha * 0.8f);
            SetAlpha(logo.transform.Find(MainName + ShadowSuffix), alpha * ShadowAlpha);
            SetAlpha(logo.transform.Find(CreditName + ShadowSuffix), alpha * ShadowAlpha);
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    private static void AddLines(Transform logo, float top)
    {
        if (logo.Find(MainName)) return;
        float creditTop = top - MainSize - 2f;
        try
        {
            // Shadows first, so the lit text draws over them.
            AddText(logo, MainName + ShadowSuffix, MainText, MainSize, top, Color.black, ShadowAlpha, ShadowOffset);
            AddText(logo, CreditName + ShadowSuffix, CreditText, CreditSize, creditTop, Color.black, ShadowAlpha, ShadowOffset);
            AddText(logo, MainName, MainText, MainSize, top, LogoWhite, 1f, Vector2.zero);
            AddText(logo, CreditName, CreditText, CreditSize, creditTop, LogoWhite, 0.8f, Vector2.zero);
        }
        catch
        {
            // Clear a half-made set, or the next frame of the intro would add another.
            foreach (var name in new[] { MainName + ShadowSuffix, CreditName + ShadowSuffix, MainName, CreditName })
            {
                var partial = logo.Find(name);
                if (partial) UnityEngine.Object.Destroy(partial.gameObject);
            }
            throw;
        }
    }

    private static void AddText(Transform parent, string name, string text, float size, float y,
                                Color color, float alpha, Vector2 offset)
    {
        var go = new GameObject(name);
        go.layer = parent.gameObject.layer;
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        // Centered under the logo's bottom edge.
        rect.anchorMin = new Vector2(0.5f, 0f);
        rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(offset.x, y + offset.y);
        rect.sizeDelta = new Vector2(320f, size + 4f);
        var label = go.AddComponent<TextMeshProUGUI>();
        var font = FindFont();
        if (font)
        {
            label.font = font!.font;
            label.fontSharedMaterial = font.fontSharedMaterial;
        }
        label.text = text;
        label.fontSize = size;
        label.alignment = TextAlignmentOptions.Top;
        label.enableWordWrapping = false;
        label.raycastTarget = false;
        label.color = new Color(color.r, color.g, color.b, alpha);
    }

    private static void SetAlpha(Transform? text, float alpha)
    {
        if (!text) return;
        var label = text!.GetComponent<TextMeshProUGUI>();
        if (!label) return;
        var c = label.color;
        if (!Mathf.Approximately(c.a, alpha)) label.color = new Color(c.r, c.g, c.b, alpha);
    }

    private static void RememberFont(MainMenu menu)
    {
        if (fontSource) return;
        var button = menu.continueButton;
        if (button) fontSource = button.GetComponentInChildren<TMP_Text>(true);
    }

    /// <summary>The main menu's pixel font ("Bacteria 12"), or any text using it.</summary>
    private static TMP_Text? FindFont()
    {
        if (fontSource) return fontSource;
        foreach (var menu in Resources.FindObjectsOfTypeAll<MainMenu>())
        {
            if (!menu) continue;
            RememberFont(menu);
            if (fontSource) return fontSource;
        }
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (text && text.font && text.font.name.StartsWith("Bacteria", StringComparison.Ordinal))
            {
                fontSource = text;
                break;
            }
        }
        return fontSource;
    }

    private static bool reportedError;

    private static void ReportOnce(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        ModLog.Error("Title text failed: " + ex);
    }
}
