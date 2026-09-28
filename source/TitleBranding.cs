using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NocturnePlus;

/// <summary>
/// The Nocturne+ branding on the title screen. A "+" follows the nocturne logo, both on the title
/// and on the logo card of the startup intro, so the logo reads "nocturne+". The mod's version and
/// its author go at the bottom right, under the game's own version number.
/// </summary>
internal static class TitleBranding
{
    private const string PlusText = "+";
    private const string PlusName = "NocturnePlus_Plus";
    private const string ShadowSuffix = "_Shadow";
    private const string VersionName = "NocturnePlus_Version";
    private const string CreditName = "NocturnePlus_Credit";
    // Made only when the game's version number can't be found (see CornerFallback).
    private const string CornerName = "NocturnePlus_Corner";
    private static string VersionText => $"{ModInfo.Name} v{ModInfo.Version}";
    private static string CreditText => "by " + ModInfo.Author;
    // The game's pixel font is a 12-point bitmap face; whole multiples keep it crisp.
    private const float FontStep = 12f;
    private const float MinPlusSize = 24f;
    private const float MaxPlusSize = 96f;
    private const float CornerSize = 12f;
    // A soft offset shadow keeps the text readable over the bright title background.
    private static readonly Vector2 ShadowOffset = new(1.5f, -1.5f);
    private const float ShadowAlpha = 0.55f;
    private const float CreditAlpha = 0.8f;
    private static readonly Color LogoWhite = new(0.988f, 1f, 0.961f, 1f);
    // The mod's own pages (EditorUi) draw at this sorting order and above; their text is never the game's.
    private const int ModPageOrder = 30000;
    // "v1.0.1", "1.0.1", "Version 1.0.1 (build 25487568)" and the like.
    private static readonly Regex VersionPattern = new(@"^\s*(v|ver\.?|version)?\s*\d+(\.\d+)+\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static TMP_Text? fontSource;
    private static PropertyInfo? introScreen;
    private static TMP_Text? versionLabel;
    private static GameObject? corner;
    private static int missedVersion;

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
        // Story progress swaps between several logo variants; mark each so the "+" follows
        // whichever one is showing.
        for (int i = 0; i < content.childCount; i++)
        {
            var logo = content.GetChild(i);
            if (!logo.name.StartsWith("Logo_", StringComparison.Ordinal)) continue;
            var image = logo.GetComponent<Image>();
            if (!image) image = logo.GetComponentInChildren<Image>(true);
            AddPlus(image ? image.rectTransform : logo.TryCast<RectTransform>(), image);
        }
    }

    /// <summary>Runs every frame of the intro so the "+" fades with the logo card.</summary>
    private static void IntroPostfix(object __instance)
    {
        try
        {
            var screen = introScreen?.GetValue(__instance) as TitleScreen;
            if (!screen) return;
            var logo = screen.splashImage2;
            if (!logo) return;
            AddPlus(logo.rectTransform, logo);
            // The card fades through Image.color, which child text does not inherit.
            float alpha = logo.color.a;
            SetAlpha(logo.transform.Find(PlusName), alpha);
            SetAlpha(logo.transform.Find(PlusName + ShadowSuffix), alpha * ShadowAlpha);
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    // ---- the "+" after the logo -----------------------------------------------------------------

    /// <summary>
    /// Puts a "+" just after the logo's lettering, raised like a superscript. The lettering's edges
    /// come from the logo's own pixels (see <see cref="InkOf"/>), so the "+" sits against the last
    /// letter and not against the picture's see-through margin.
    /// </summary>
    private static void AddPlus(RectTransform? logo, Image? image)
    {
        if (!logo || logo!.Find(PlusName)) return;
        var ink = InkRect(logo, image);
        float size = Mathf.Clamp(Mathf.Round(ink.height * 0.5f / FontStep) * FontStep, MinPlusSize, MaxPlusSize);
        // Left edge a little after the last letter; middle a little under the lettering's top.
        var at = new Vector2(ink.xMax + size * 0.08f, ink.yMax - size * 0.35f);
        try
        {
            // The shadow first, so the lit "+" draws over it.
            AddPlusText(logo, PlusName + ShadowSuffix, size, at + ShadowOffset, Color.black, ShadowAlpha);
            AddPlusText(logo, PlusName, size, at, LogoWhite, 1f);
        }
        catch
        {
            // Clear a half-made pair, or the next frame of the intro would add another.
            foreach (var name in new[] { PlusName + ShadowSuffix, PlusName })
            {
                var partial = logo.Find(name);
                if (partial) Object.Destroy(partial.gameObject);
            }
            throw;
        }
    }

    private static void AddPlusText(RectTransform parent, string name, float size, Vector2 at, Color color, float alpha)
    {
        var label = MakeLabel(parent, name, PlusText, size, color, alpha, FindFont());
        var rect = label.rectTransform;
        // Anchored at the logo's pivot, so the position is in the logo's own units.
        rect.anchorMin = rect.anchorMax = parent.pivot;
        rect.pivot = new Vector2(0f, 0.5f);
        rect.anchoredPosition = at;
        rect.sizeDelta = new Vector2(size * 1.5f, size * 1.5f);
        // Midline centres the glyph itself, not the font's line box, on the point.
        label.alignment = TextAlignmentOptions.MidlineLeft;
    }

    /// <summary>Where the logo's lettering is drawn, in the logo's local units; its whole rect when that can't be read.</summary>
    private static Rect InkRect(RectTransform logo, Image? image)
    {
        var drawn = DrawnRect(logo, image);
        if (!image || !image!.sprite) return drawn;
        var ink = InkOf(image.sprite);
        return new Rect(drawn.x + ink.x * drawn.width, drawn.y + ink.y * drawn.height,
                        ink.width * drawn.width, ink.height * drawn.height);
    }

    /// <summary>The part of the rect the sprite fills, as Image draws a simple sprite.</summary>
    private static Rect DrawnRect(RectTransform logo, Image? image)
    {
        var rect = logo.rect;
        if (!image || !image!.sprite || !image.preserveAspect || image.type != Image.Type.Simple) return rect;
        var size = image.sprite.rect.size;
        if (size.x <= 0f || size.y <= 0f || rect.width <= 0f || rect.height <= 0f) return rect;
        float spriteRatio = size.x / size.y;
        var pivot = logo.pivot;
        if (spriteRatio > rect.width / rect.height)
        {
            float height = rect.width / spriteRatio;
            rect.y += (rect.height - height) * pivot.y;
            rect.height = height;
        }
        else
        {
            float width = rect.height * spriteRatio;
            rect.x += (rect.width - width) * pivot.x;
            rect.width = width;
        }
        return rect;
    }

    private static readonly Dictionary<IntPtr, Rect> Inks = new();
    private static readonly Rect WholeSprite = new(0f, 0f, 1f, 1f);
    private const int MaxInkSide = 1024;
    private const byte InkAlpha = 64;

    /// <summary>
    /// The box around the sprite's visible pixels, as a part (0 to 1) of the sprite's rect. The
    /// logo's texture isn't readable from the game's code, so it's copied through the graphics card
    /// once and read back. The whole sprite when that fails. The sprite's rect is taken as its place
    /// in the texture, as it is for a sprite of its own (the logos aren't in an atlas).
    /// </summary>
    private static Rect InkOf(Sprite sprite)
    {
        if (Inks.TryGetValue(sprite.Pointer, out var known)) return known;
        var ink = WholeSprite;
        RenderTexture? previous = null, target = null;
        Texture2D? readable = null;
        try
        {
            var texture = sprite.texture;
            var part = sprite.rect;
            if (texture && part.width >= 1f && part.height >= 1f)
            {
                float scale = Math.Min(1f, MaxInkSide / Math.Max(part.width, part.height));
                int width = Math.Max(1, (int)Math.Round(part.width * scale)), height = Math.Max(1, (int)Math.Round(part.height * scale));
                previous = RenderTexture.active;
                target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                Graphics.Blit(texture, target, new Vector2(part.width / texture.width, part.height / texture.height),
                              new Vector2(part.x / texture.width, part.y / texture.height));
                RenderTexture.active = target;
                readable = new Texture2D(width, height, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                readable.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                var pixels = readable.GetPixels32();
                int minX = width, minY = height, maxX = -1, maxY = -1;
                // Bottom row first, like the texture's own pixels.
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        if (pixels[y * width + x].a < InkAlpha) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                if (maxX >= 0)
                    ink = Rect.MinMaxRect(Mathf.Clamp01((float)minX / width), Mathf.Clamp01((float)minY / height),
                                          Mathf.Clamp01((float)(maxX + 1) / width), Mathf.Clamp01((float)(maxY + 1) / height));
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Title text: reading the logo's edges failed, so the \"+\" goes by its rect: " + ex.Message);
            ink = WholeSprite;
        }
        finally
        {
            try
            {
                RenderTexture.active = previous;
                if (target != null) RenderTexture.ReleaseTemporary(target);
            }
            catch { }
            if (readable != null && readable) Object.Destroy(readable);
        }
        Inks[sprite.Pointer] = ink;
        return ink;
    }

    // ---- the version at the bottom right ------------------------------------------------------

    /// <summary>
    /// Called once a second. On the title, finds the game's version number at the bottom right and
    /// writes the mod's version and author under it. Scenes come and go, so a new title gets them too.
    /// </summary>
    internal static void Update()
    {
        try
        {
            if (GameManager.GameState != GameStates.MainMenu)
            {
                missedVersion = 0;
                return;
            }
            // The lines are the label's children, so they hide and show with it.
            if (versionLabel != null && versionLabel) return;
            versionLabel = null;
            // With the corner lines up, look for the game's version less often.
            if (corner != null && corner && ++missedVersion % 5 != 0) return;
            var label = FindVersionLabel();
            if (label != null)
            {
                missedVersion = 0;
                RemoveCorner();
                AddVersionLines(label);
                versionLabel = label;
                return;
            }
            // Not every title shows its version at once; give it a few seconds past the intro.
            if (IntroShowing() || ++missedVersion < 3) return;
            CornerFallback();
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    private static bool IntroShowing()
    {
        try { return TestPlay.IntroShowing(); }
        catch { return false; }
    }

    /// <summary>The game's version number: a short text in the bottom right quarter of the screen that reads like a version, the furthest to the bottom right.</summary>
    private static TMP_Text? FindVersionLabel()
    {
        TMP_Text? best = null;
        float bestScore = float.MinValue;
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (!text || !text.gameObject.scene.IsValid() || !text.gameObject.activeInHierarchy) continue;
            if (text.name.StartsWith("NocturnePlus_", StringComparison.Ordinal)) continue;
            string value = text.text ?? "";
            if (value.Length == 0 || value.Length > 60) continue;
            if (!VersionPattern.IsMatch(value)) continue;
            var canvas = text.canvas;
            if (!canvas) continue;
            var root = canvas.rootCanvas;
            if (root && root.sortingOrder >= ModPageOrder) continue;
            var camera = root && root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
            var rect = text.rectTransform;
            var point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
            if (point.x < Screen.width * 0.5f || point.y > Screen.height * 0.5f) continue;
            float score = point.x - point.y;
            if (score > bestScore)
            {
                bestScore = score;
                best = text;
            }
        }
        return best;
    }

    /// <summary>
    /// Two lines under the game's version, in its font, size, colour and horizontal alignment. When they
    /// would run off the bottom of the screen, the game's version moves up to make room.
    /// </summary>
    private static void AddVersionLines(TMP_Text label)
    {
        var parent = label.rectTransform;
        if (parent.Find(VersionName)) return;
        float size = label.fontSize > 0f ? label.fontSize : CornerSize;
        float line = Mathf.Round(size * 1.2f);
        float gap = Mathf.Round(size * 0.2f);
        // Under the text itself, which can sit at the top, middle or bottom of a tall rect.
        var box = parent.rect;
        float height = Math.Min(label.GetPreferredValues(label.text).y, box.height);
        string alignment = label.alignment.ToString();
        float top = alignment.StartsWith("Top", StringComparison.Ordinal) ? box.yMax - height
            : alignment.StartsWith("Bottom", StringComparison.Ordinal) ? box.yMin
            : box.center.y - height / 2f;
        var align = alignment.Contains("Right") ? TextAlignmentOptions.TopRight
            : alignment.Contains("Left") ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.Top;
        var color = label.color;
        var lines = new[] { (VersionName, VersionText, color.a), (CreditName, CreditText, color.a * CreditAlpha) };
        try
        {
            for (int i = 0; i < lines.Length; i++)
            {
                var (name, text, alpha) = lines[i];
                var made = MakeLabel(parent, name, text, size, color, alpha, label);
                made.alignment = align;
                made.fontStyle = label.fontStyle;
                var rect = made.rectTransform;
                // As wide as the game's version, so the lines line up with it the same way.
                rect.anchorMin = new Vector2(0f, parent.pivot.y);
                rect.anchorMax = new Vector2(1f, parent.pivot.y);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.sizeDelta = new Vector2(0f, line);
                rect.anchoredPosition = new Vector2(0f, top - gap - i * line);
            }
        }
        catch
        {
            foreach (var name in new[] { VersionName, CreditName })
            {
                var partial = parent.Find(name);
                if (partial) Object.Destroy(partial.gameObject);
            }
            throw;
        }
        MakeRoom(label, top - gap - lines.Length * line, top, size * 0.5f);
        ModLog.Info($"Title text: wrote the {ModInfo.Name} version under the game's \"{label.text}\".");
    }

    /// <summary>
    /// Moves the game's version up when the lines under it would leave the screen, keeping at
    /// least <paramref name="margin"/> (or the game's own margin, if smaller) below them.
    /// </summary>
    private static void MakeRoom(TMP_Text label, float bottom, float textBottom, float margin)
    {
        var rect = label.rectTransform;
        var canvas = label.canvas ? label.canvas.rootCanvas : null;
        var camera = canvas && canvas!.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        var center = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, new Vector2(center.x, 0f), camera, out var edge)) return;
        float own = textBottom - edge.y;
        float keep = Math.Max(0f, Math.Min(own, margin));
        float shift = keep - (bottom - edge.y);
        if (shift <= 0f) return;
        rect.anchoredPosition += new Vector2(0f, shift * rect.localScale.y);
    }

    /// <summary>
    /// When the game's version number can't be found, the lines go in the title's own bottom
    /// right corner instead.
    /// </summary>
    private static void CornerFallback()
    {
        foreach (var menu in Resources.FindObjectsOfTypeAll<MainMenu>())
        {
            if (!menu || !menu.gameObject.scene.IsValid() || !menu.gameObject.activeInHierarchy) continue;
            var canvas = menu.GetComponentInParent<Canvas>();
            if (!canvas) continue;
            var root = canvas.rootCanvas.transform;
            if (root.Find(CornerName)) return;
            RememberFont(menu);
            corner = new GameObject(CornerName);
            corner.layer = root.gameObject.layer;
            var rect = corner.AddComponent<RectTransform>();
            rect.SetParent(root, false);
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            float line = CornerSize + 4f;
            rect.sizeDelta = new Vector2(320f, line * 2f);
            rect.anchoredPosition = new Vector2(-6f, 6f);
            var lines = new[] { (VersionName, VersionText, 1f), (CreditName, CreditText, CreditAlpha) };
            for (int i = 0; i < lines.Length; i++)
            {
                var (name, text, alpha) = lines[i];
                var made = MakeLabel(rect, name, text, CornerSize, LogoWhite, alpha, FindFont());
                made.alignment = TextAlignmentOptions.TopRight;
                var lineRect = made.rectTransform;
                lineRect.anchorMin = new Vector2(0f, 1f);
                lineRect.anchorMax = new Vector2(1f, 1f);
                lineRect.pivot = new Vector2(0.5f, 1f);
                lineRect.sizeDelta = new Vector2(0f, line);
                lineRect.anchoredPosition = new Vector2(0f, -i * line);
            }
            ModLog.Info($"Title text: the game's version number wasn't found, so the {ModInfo.Name} version is in the title's corner.");
            return;
        }
    }

    private static void RemoveCorner()
    {
        if (corner != null && corner) Object.Destroy(corner);
        corner = null;
    }

    // ---- shared -------------------------------------------------------------------------------

    private static TextMeshProUGUI MakeLabel(Transform parent, string name, string text, float size, Color color, float alpha, TMP_Text? font)
    {
        var go = new GameObject(name);
        go.layer = parent.gameObject.layer;
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        var label = go.AddComponent<TextMeshProUGUI>();
        if (font != null && font)
        {
            label.font = font.font;
            label.fontSharedMaterial = font.fontSharedMaterial;
        }
        label.text = text;
        label.fontSize = size;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        label.color = new Color(color.r, color.g, color.b, alpha);
        return label;
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
    internal static TMP_Text? FindFont()
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
