using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NocturnePlus;

/// <summary>
/// The Nocturne+ branding on the title screen. A "+" follows the nocturne logo, both on the title
/// and on the logo card of the startup intro, so the logo reads "nocturne+". On the title's menu,
/// the game's version at the bottom right ("Nocturne 1.0.1") gets two more lines in its own style:
/// "Nocturne+ 2.8.0" and "by TerryDavisGaming".
/// </summary>
internal static class TitleBranding
{
    private const string PlusText = "+";
    private const string PlusName = "NocturnePlus_Plus";
    private const string ShadowSuffix = "_Shadow";
    // Laid out like the game's own "Nocturne 1.0.1".
    private static string VersionText => $"{ModInfo.Name} {ModInfo.Version}";
    private static string CreditText => "by " + ModInfo.Author;
    // The game's pixel font is a 12-point bitmap face; whole multiples keep it crisp.
    private const float FontStep = 12f;
    private const float MinPlusSize = 24f;
    private const float MaxPlusSize = 96f;
    // A soft offset shadow keeps the text readable over the bright title background.
    private static readonly Vector2 ShadowOffset = new(1.5f, -1.5f);
    private const float ShadowAlpha = 0.55f;
    private static readonly Color LogoWhite = new(0.988f, 1f, 0.961f, 1f);
    // The mod's own canvases (its pages and messages) are named after it; their text is never the game's.
    private static bool ModCanvas(Canvas canvas)
    {
        string name = canvas.gameObject.name;
        return name.StartsWith("NocturneButBetter", StringComparison.Ordinal) || name.StartsWith("NocturnePlus", StringComparison.Ordinal);
    }

    private static Canvas? RootCanvas(Component component)
    {
        var canvas = component.GetComponentInParent<Canvas>();
        return canvas ? canvas.rootCanvas : null;
    }

    private static Vector3 Centre(Rect rect) => new(rect.x + rect.width / 2f, rect.y + rect.height / 2f, 0f);
    // A version number anywhere in the text: "Nocturne 1.0.1", "v1.0.1", "1.0.1 (25487568)".
    private static readonly Regex VersionPattern = new(@"\b\d+(\.\d+)+\b", RegexOptions.CultureInvariant);
    private static TMP_Text? fontSource;
    private static PropertyInfo? introScreen;
    private static TMP_Text? versionLabel;
    private static string? versionOriginal;
    private static bool versionMoved;
    private static float versionShift;
    private static int titleSeconds;

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
        // The version lines too, without waiting for the next second.
        Update();
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

    /// <summary>
    /// The part of the rect the sprite fills. A logo whose rect has another shape than its picture
    /// is taken to keep its picture's shape (Image's preserve aspect), centred on the pivot, since a
    /// stretched logo would look wrong; the game's Image settings themselves can't be read.
    /// </summary>
    private static Rect DrawnRect(RectTransform logo, Image? image)
    {
        var rect = logo.rect;
        if (!image || !image!.sprite) return rect;
        var size = image.sprite.rect.size;
        float x = rect.x, y = rect.y, w = rect.width, h = rect.height;
        if (size.x <= 0f || size.y <= 0f || w <= 0f || h <= 0f) return rect;
        float spriteRatio = size.x / size.y, rectRatio = w / h;
        if (Math.Abs(spriteRatio / rectRatio - 1f) < 0.02f) return rect;
        var pivot = logo.pivot;
        if (spriteRatio > rectRatio)
        {
            float fitted = w / spriteRatio;
            y += (h - fitted) * pivot.y;
            h = fitted;
        }
        else
        {
            float fitted = h * spriteRatio;
            x += (w - fitted) * pivot.x;
            w = fitted;
        }
        return new Rect(x, y, w, h);
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
            var texture = TextureOf(sprite);
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

    // Its own method: a game without Sprite.texture fails here, inside InkOf's try, and the "+" goes by the rect.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Texture2D TextureOf(Sprite sprite) => sprite.texture;

    // ---- the version at the bottom right ------------------------------------------------------

    /// <summary>
    /// Called once a second. On the title, finds the game's version ("Nocturne 1.0.1") at the bottom
    /// right and adds the mod's version and author to that same text, so they show in its font, size,
    /// colour and alignment, right under it. Leaving the title puts the game's text back, so nothing
    /// of it shows anywhere else. A text the game sets again gets the lines again.
    /// </summary>
    internal static void Update()
    {
        try
        {
            if (GameManager.GameState != GameStates.MainMenu)
            {
                RestoreVersion();
                titleSeconds = 0;
                return;
            }
            if (versionLabel != null && versionLabel)
            {
                string text = versionLabel.text ?? "";
                if (!text.EndsWith(VersionSuffix, StringComparison.Ordinal)) AddVersionLines(versionLabel);
                return;
            }
            versionLabel = null;
            var label = FindVersionLabel();
            if (label != null)
            {
                AddVersionLines(label);
                versionLabel = label;
                return;
            }
            // Say once what the title had, so a game whose version reads differently can be matched.
            if (IntroShowing() || ++titleSeconds != 5) return;
            ModLog.Error("Title text: the game's version at the bottom right wasn't found, so the Nocturne+ version isn't shown. Short texts on the title: "
                         + string.Join(" | ", ShortTexts()) + ".");
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    private static bool IntroShowing()
    {
        try { return TestPlay.IntroShowing(); }
        catch { return false; }
    }

    private static string VersionSuffix => $"\n{VersionText}\n{CreditText}";

    /// <summary>
    /// The game's version: an active text in the bottom right quarter of the screen with a version
    /// number in it, one that names nocturne first, then the furthest to the bottom right.
    /// </summary>
    private static TMP_Text? FindVersionLabel()
    {
        TMP_Text? best = null;
        float bestScore = float.MinValue;
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (!OnTitleScreen(text, out var value, out var point)) continue;
            if (value.Length > 40 || !VersionPattern.IsMatch(value) || value.Contains(ModInfo.Name)) continue;
            if (point.x < Screen.width * 0.5f || point.y > Screen.height * 0.5f) continue;
            float score = (value.IndexOf("nocturne", StringComparison.OrdinalIgnoreCase) >= 0 ? 100000f : 0f) + point.x - point.y;
            if (score > bestScore)
            {
                bestScore = score;
                best = text;
            }
        }
        return best;
    }

    /// <summary>Whether the text is the game's and showing, with its text and its middle on screen.</summary>
    private static bool OnTitleScreen(TMP_Text text, out string value, out Vector2 point)
    {
        value = "";
        point = default;
        if (!text || !text.gameObject.scene.IsValid() || !text.gameObject.activeInHierarchy) return false;
        if (text.name.StartsWith("NocturnePlus_", StringComparison.Ordinal)) return false;
        var root = RootCanvas(text);
        if (!root || ModCanvas(root!)) return false;
        value = text.text ?? "";
        if (value.Length == 0) return false;
        var camera = root!.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
        var rect = text.rectTransform;
        point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(Centre(rect.rect)));
        return true;
    }

    private static IEnumerable<string> ShortTexts()
    {
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            if (OnTitleScreen(text, out var value, out var point) && value.Length <= 40)
                yield return $"\"{value.Replace('\n', ' ')}\" at {point.x:0},{point.y:0}";
    }

    /// <summary>
    /// Adds "Nocturne+ 2.8.0" and "by TerryDavisGaming" as lines of the game's own version text.
    /// When the longer text would reach below the screen, the label moves up to make room.
    /// </summary>
    private static void AddVersionLines(TMP_Text label)
    {
        string text = label.text ?? "";
        int ours = text.IndexOf(VersionSuffix, StringComparison.Ordinal);
        string original = ours >= 0 ? text.Substring(0, ours) : text;
        if (versionLabel == null || !versionLabel || versionLabel.Pointer != label.Pointer)
        {
            RestoreVersion();
            versionOriginal = original;
            versionMoved = false;
            ModLog.Info($"Title text: wrote the {ModInfo.Name} version under the game's \"{original.Replace('\n', ' ')}\".");
        }
        else versionOriginal = original;
        // Nothing cut off or wrapped: the extra lines only ever add height.
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Overflow;
        label.text = original + VersionSuffix;
        if (!versionMoved) MakeRoom(label, label.text);
    }

    /// <summary>Puts the game's version text back as it was (the title is left, or another label took over).</summary>
    private static void RestoreVersion()
    {
        var label = versionLabel;
        if (label == null || !label || versionOriginal == null) return;
        try
        {
            string text = label.text ?? "";
            if (text.EndsWith(VersionSuffix, StringComparison.Ordinal)) label.text = versionOriginal;
            if (versionMoved)
            {
                var rect = label.rectTransform;
                var at = rect.anchoredPosition;
                rect.anchoredPosition = new Vector2(at.x, at.y - versionShift);
            }
        }
        catch (Exception ex) { ReportOnce(ex); }
        versionLabel = null;
        versionOriginal = null;
        versionMoved = false;
    }

    /// <summary>
    /// Moves the label up when the added lines would reach below the bottom of the screen. Where the
    /// text grows depends on its vertical alignment, read when the game has it (bottom-aligned text
    /// grows up, top-aligned down, the rest both ways); without it, both ways.
    /// </summary>
    private static void MakeRoom(TMP_Text label, string after)
    {
        var rect = label.rectTransform;
        var box = rect.rect;
        float full = label.GetPreferredValues(after).y;
        int vertical = VerticalAlignment(label);
        float bottom = vertical is 1024 or 2048 ? box.yMin
            : vertical is 256 or 8192 ? box.yMax - Math.Max(full, box.height)
            : box.y + box.height / 2f - Math.Max(full, box.height) / 2f;
        var canvas = RootCanvas(label);
        var camera = canvas && canvas!.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        var centre = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(Centre(box)));
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, new Vector2(centre.x, 0f), camera, out var edge)) return;
        float margin = label.fontSize * 0.5f;
        float shift = edge.y + margin - bottom;
        if (shift <= 0f) return;
        versionShift = shift * rect.localScale.y;
        var at = rect.anchoredPosition;
        rect.anchoredPosition = new Vector2(at.x, at.y + versionShift);
        versionMoved = true;
    }

    private static PropertyInfo? alignmentProperty;
    private static bool alignmentLooked;

    /// <summary>
    /// The text's vertical alignment (256 top, 512 middle, 1024 bottom, 2048 baseline, 4096 midline,
    /// 8192 capline), or 0 when the game's TextMeshPro can't say. Read by reflection, since not every
    /// build of the game keeps the getter.
    /// </summary>
    private static int VerticalAlignment(TMP_Text label)
    {
        try
        {
            if (!alignmentLooked)
            {
                alignmentLooked = true;
                alignmentProperty = typeof(TMP_Text).GetProperty("alignment", BindingFlags.Public | BindingFlags.Instance);
                if (alignmentProperty?.GetGetMethod() == null) alignmentProperty = null;
            }
            if (alignmentProperty == null) return 0;
            return Convert.ToInt32(alignmentProperty.GetValue(label)) & 0xFF00;
        }
        catch
        {
            alignmentProperty = null;
            return 0;
        }
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
