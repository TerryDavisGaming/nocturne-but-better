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
/// the game's version at the bottom right ("Nocturne 1.0.1") gets the mod's after it, in its own
/// style: "Nocturne 1.0.1 / Nocturne+ 2.9.0 by TerryDavisGaming".
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
    /// Puts a "+" just after the logo's lettering, as tall as its letters and centred on them, so the
    /// word reads "nocturne+". The edges come from the logo's own pixels (see <see cref="InkOf"/>):
    /// the "+" sits against the last letter, not the picture's see-through margin, and its height and
    /// middle are the last letter's, not the whole logo's (the ring and its glow reach well above).
    /// </summary>
    private static void AddPlus(RectTransform? logo, Image? image)
    {
        if (!logo || logo!.Find(PlusName)) return;
        var (ink, letter) = InkRect(logo, image);
        var font = FindFont();
        float gap = letter.height * 0.3f;
        float size;
        Vector2 at;
        bool onBaseline = false;
        if (PlusGlyph(font != null && font ? font.font : null) is { } glyph)
        {
            // As tall as the letter; the glyph's own middle on the letter's middle, set from the
            // baseline (the "+" sits above it by its bearing), and its left edge a gap after the letter.
            size = PlusSize(letter.height / glyph.Height);
            at = new Vector2(ink.xMax + gap - glyph.Left * size, letter.center.y - (glyph.Top - glyph.Height / 2f) * size);
            onBaseline = true;
        }
        else
        {
            // The font can't say: the "+" is about half the font's size, centred by its midline.
            size = PlusSize(letter.height * 2f);
            at = new Vector2(ink.xMax + gap, letter.center.y);
        }
        try
        {
            // The shadow first, so the lit "+" draws over it.
            AddPlusText(logo, PlusName + ShadowSuffix, size, at + ShadowOffset, Color.black, ShadowAlpha, onBaseline);
            AddPlusText(logo, PlusName, size, at, LogoWhite, 1f, onBaseline);
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

    /// <param name="onBaseline">The point is the text's baseline (baseline alignment puts it at the rect's middle); otherwise the glyph's midline.</param>
    private static void AddPlusText(RectTransform parent, string name, float size, Vector2 at, Color color, float alpha, bool onBaseline)
    {
        var label = MakeLabel(parent, name, PlusText, size, color, alpha, FindFont());
        var rect = label.rectTransform;
        // Anchored at the logo's pivot, so the position is in the logo's own units.
        rect.anchorMin = rect.anchorMax = parent.pivot;
        rect.pivot = new Vector2(0f, 0.5f);
        rect.anchoredPosition = at;
        rect.sizeDelta = new Vector2(size * 1.5f, size * 1.5f);
        label.alignment = onBaseline ? TextAlignmentOptions.BaselineLeft : TextAlignmentOptions.MidlineLeft;
    }

    // In whole steps of the pixel font, within bounds.
    private static float PlusSize(float size) => Mathf.Clamp(Mathf.Round(size / FontStep) * FontStep, MinPlusSize, MaxPlusSize);

    /// <summary>
    /// The font's "+" glyph, per point of font size: its height, its top above the baseline, and its
    /// left bearing. Null when the font can't say.
    /// </summary>
    private static (float Height, float Top, float Left)? PlusGlyph(TMP_FontAsset? font)
    {
        try
        {
            if (font == null || !font) return null;
            var table = font.characterLookupTable;
            if (table == null || !table.ContainsKey('+')) return null;
            var glyph = table['+'].glyph;
            if (glyph == null) return null;
            var metrics = glyph.metrics;
            var face = font.faceInfo;
            if (metrics.height <= 0f || face.pointSize <= 0) return null;
            float per = (face.scale > 0f ? face.scale : 1f) / face.pointSize;
            return (metrics.height * per, metrics.horizontalBearingY * per, metrics.horizontalBearingX * per);
        }
        catch { return null; }
    }

    /// <summary>
    /// Where the logo's lettering is drawn and where its last letter is, in the logo's local units.
    /// When the picture can't be read, its whole rect, and a band a third of its height through its middle.
    /// </summary>
    private static (Rect Ink, Rect Letter) InkRect(RectTransform logo, Image? image)
    {
        var drawn = DrawnRect(logo, image);
        var (ink, letter) = image && image!.sprite ? InkOf(image.sprite) : (WholeSprite, MiddleBand);
        Rect Place(Rect part) => new(drawn.x + part.x * drawn.width, drawn.y + part.y * drawn.height,
                                     part.width * drawn.width, part.height * drawn.height);
        return (Place(ink), Place(letter));
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

    private static readonly Dictionary<IntPtr, (Rect Ink, Rect Letter)> Inks = new();
    private static readonly Rect WholeSprite = new(0f, 0f, 1f, 1f);
    private static readonly Rect MiddleBand = new(0f, 1f / 3f, 1f, 1f / 3f);
    private const int MaxInkSide = 1024;
    private const byte InkAlpha = 64;
    // The last letter: the visible pixels in the right-most part of the lettering. The word runs
    // past the logo's ring on both sides, so only the last letter is there.
    private const float LastLetterPart = 0.08f;

    /// <summary>
    /// The box around the sprite's visible pixels, and the box around the visible pixels of its last
    /// letter, as parts (0 to 1) of the sprite's rect. The logo's texture isn't readable from the
    /// game's code, so it's copied through the graphics card once and read back. The whole sprite and
    /// its middle band when that fails. The sprite's rect is taken as its place in the texture, as it
    /// is for a sprite of its own (the logos aren't in an atlas).
    /// </summary>
    private static (Rect Ink, Rect Letter) InkOf(Sprite sprite)
    {
        if (Inks.TryGetValue(sprite.Pointer, out var known)) return known;
        var ink = WholeSprite;
        var letter = MiddleBand;
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
                {
                    ink = Rect.MinMaxRect(Mathf.Clamp01((float)minX / width), Mathf.Clamp01((float)minY / height),
                                          Mathf.Clamp01((float)(maxX + 1) / width), Mathf.Clamp01((float)(maxY + 1) / height));
                    int fromX = maxX - Math.Max(1, (int)((maxX - minX + 1) * LastLetterPart));
                    int lowY = height, highY = -1;
                    for (int y = 0; y < height; y++)
                        for (int x = Math.Max(fromX, 0); x <= maxX; x++)
                        {
                            if (pixels[y * width + x].a < InkAlpha) continue;
                            if (y < lowY) lowY = y;
                            if (y > highY) highY = y;
                        }
                    letter = highY >= lowY
                        ? Rect.MinMaxRect(Mathf.Clamp01((float)fromX / width), Mathf.Clamp01((float)lowY / height),
                                          Mathf.Clamp01((float)(maxX + 1) / width), Mathf.Clamp01((float)(highY + 1) / height))
                        : new Rect(ink.x, ink.center.y - ink.height / 6f, ink.width, ink.height / 3f);
                }
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Title text: reading the logo's edges failed, so the \"+\" goes by its rect: " + ex.Message);
            ink = WholeSprite;
            letter = MiddleBand;
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
        Inks[sprite.Pointer] = (ink, letter);
        return (ink, letter);
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
            ModLog.Error("Title text: the game's version at the bottom of the title wasn't found, so the Nocturne+ version isn't shown. Short texts on the title: "
                         + string.Join(" | ", ShortTexts()) + ".");
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    private static bool IntroShowing()
    {
        try { return TestPlay.IntroShowing(); }
        catch { return false; }
    }

    // On the same line: lines under it would push the game's version up behind the key hints above it.
    private static string VersionSuffix => $" / {VersionText} {CreditText}";

    /// <summary>
    /// The game's version: an active text in the bottom half of the screen with a version number in
    /// it, one that names nocturne first, then the nearest a bottom corner (it's at the bottom right).
    /// </summary>
    private static TMP_Text? FindVersionLabel()
    {
        TMP_Text? best = null;
        float bestScore = float.MinValue;
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (!OnTitleScreen(text, out var value, out var point)) continue;
            if (value.Length > 40 || !VersionPattern.IsMatch(value) || value.Contains(ModInfo.Name)) continue;
            if (point.y > Screen.height * 0.5f || !Drawn(text)) continue;
            float side = Math.Min(point.x, Screen.width - point.x);
            // The game's own build text (BuildInfoText, at the bottom right of the title's menu) first.
            float score = (IsBuildInfo(text) ? 1000000f : 0f) +
                          (value.IndexOf("nocturne", StringComparison.OrdinalIgnoreCase) >= 0 ? 100000f : 0f) - side - point.y;
            if (score > bestScore)
            {
                bestScore = score;
                best = text;
            }
        }
        return best;
    }

    private static bool IsBuildInfo(TMP_Text text)
    {
        try { return text.GetComponent<BuildInfoText>(); }
        catch { return false; }
    }

    /// <summary>Whether the text is drawn at all: not faded out by a canvas group above it.</summary>
    private static bool Drawn(TMP_Text text)
    {
        try
        {
            var renderer = text.canvasRenderer;
            return renderer == null || !renderer || renderer.GetInheritedAlpha() > 0.01f;
        }
        catch { return true; }
    }

    /// <summary>Whether the text is the game's and showing, with its text and its middle on screen.</summary>
    private static bool OnTitleScreen(TMP_Text text, out string value, out Vector2 point)
    {
        value = "";
        point = default;
        // Enabled too: a copy of the game's build text on a canvas of its own has its text switched off.
        if (!text || !text.gameObject.scene.IsValid() || !text.isActiveAndEnabled) return false;
        if (text.name.StartsWith("NocturnePlus_", StringComparison.Ordinal)) return false;
        var root = RootCanvas(text);
        if (!root || ModCanvas(root!)) return false;
        value = text.text ?? "";
        if (value.Length == 0) return false;
        point = OnScreen(text.rectTransform, root!);
        return true;
    }

    /// <summary>
    /// The middle of a rect as a point on the screen, worked out from its place on its root canvas,
    /// which fills the screen. (The title's canvases draw through a camera whose own screen points
    /// don't match the window.)
    /// </summary>
    private static Vector2 OnScreen(RectTransform rect, Canvas root)
    {
        var canvas = root.transform.TryCast<RectTransform>();
        if (canvas == null || !canvas) return default;
        var local = canvas.InverseTransformPoint(rect.TransformPoint(Centre(rect.rect)));
        var area = canvas.rect;
        if (area.width <= 0f || area.height <= 0f) return default;
        return new Vector2((local.x - area.xMin) / area.width * Screen.width, (local.y - area.yMin) / area.height * Screen.height);
    }

    private static IEnumerable<string> ShortTexts()
    {
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            if (OnTitleScreen(text, out var value, out var point) && value.Length <= 40)
                yield return $"\"{value.Replace('\n', ' ')}\" at {point.x:0},{point.y:0}";
    }

    /// <summary>
    /// Adds " / Nocturne+ 2.9.0 by TerryDavisGaming" to the game's own version text. When the longer
    /// text would reach below the screen (a label that wraps), the label moves up to make room.
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
            ModLog.Info($"Title text: wrote the {ModInfo.Name} version after the game's \"{original.Replace('\n', ' ')}\".");
        }
        else versionOriginal = original;
        // Nothing cut off or wrapped onto a line of its own.
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
        // Measured on the root canvas, whose bottom edge is the screen's.
        var root = RootCanvas(label);
        var canvas = root ? root!.transform.TryCast<RectTransform>() : null;
        var parent = rect.parent;
        if (canvas == null || !canvas || !parent) return;
        float textBottom = canvas.InverseTransformPoint(rect.TransformPoint(new Vector3(0f, bottom, 0f))).y;
        float lineHeight = canvas.InverseTransformVector(rect.TransformVector(new Vector3(0f, label.fontSize, 0f))).y;
        float shift = canvas.rect.yMin + lineHeight * 0.5f - textBottom;
        if (shift <= 0f) return;
        // From the canvas's units to the label's parent's, in which it's placed.
        float perParent = parent.InverseTransformVector(canvas.TransformVector(new Vector3(0f, shift, 0f))).y;
        if (perParent <= 0f) return;
        versionShift = perParent;
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
