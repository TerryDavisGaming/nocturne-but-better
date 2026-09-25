using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.UI;
using static NocturneFlatScroll.EditorUi;
using InputMouse = UnityEngine.InputSystem.Mouse;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

// The Info page's arcade card: the card as the arcade draws it, at the arcade's own size on this
// screen (the creator's canvas is 1920 x 1080 and the arcade menu's 640 x 360, both Expand, so 3
// creator units are one arcade unit at any window size). Its picture is made by the arcade's own
// code (CustomBattles.CardImages and CardLayout: the same kept size, square and filter) and fitted
// into the same rounded square; its title is cut where the arcade's list cuts it; its tag is the
// arcade's. Under it: choosing the picture, filling the square or showing it whole, where the
// square sits (steps, typing, or dragging the picture), and crisp pixels. The card's settings are
// draft changes like the page's others: saved with Save, dropped by leaving without saving.
internal static partial class BattleCreator
{
    // The arcade card in its menu's units (research\card), times 3.
    private const float CardUnit = 3f;
    private const float CardW = 197.58f * CardUnit, CardH = 78f * CardUnit;
    // The picture's rounded square, the picture in it, and the corner radius of the square.
    private const float SlotBox = 78f * CardUnit, SlotPicture = 76.154f * CardUnit;
    private const int SlotRadius = 27, FrameRadius = 7, FrameLine = 4;
    // The title's glyphs start 90.5 units in and are cut at the card's right edge; the melody row and the mod's tag.
    private const float TitleX = 90.5f * CardUnit, TitleY = 4f * CardUnit, TitleH = 12f * CardUnit, TitleSize = 10f * CardUnit;
    private const float MelodyX = 84f * CardUnit, MelodyY = 16f * CardUnit, MelodyW = 102.574f * CardUnit, MelodyH = 18f * CardUnit;
    private const float TagY = 35f * CardUnit, TagH = 10f * CardUnit, TagSize = 8f * CardUnit;
    private const string Amber = "#F2B02E";

    private static RectTransform? cardPreview;
    private static TMP_Text? cardTitle;
    // The picture as the arcade makes it (the whole picture at the kept size, its sprite showing the card's part), or null.
    private static CustomBattles.CardImages.Card? cardPicture;
    // Whether cardPicture was made to fill the square (its kept size depends on it).
    private static bool cardPictureFill;
    // What the state line says when there is no picture to show.
    private static string cardState = "";
    // The tag the arcade puts on the card (RefreshLevel), or null.
    private static string? cardTag;
    private static Sprite? roundFill, roundLine;
    private static bool reportedCardError;

    // ---- building -----------------------------------------------------------------------------

    private static void BuildCardSection(Page p)
    {
        float y = 0;
        AddHeader(p, Col2, ref y, Col2W, "Arcade card");
        BuildCardPreview(pagePanels[p], y);
        y -= CardH + 14;
        AddText(p, Col2, ref y, Col2W, 64, () =>
            "Best: a square picture, 76 x 76 for pixel art or 456 x 456 for drawings and photos. " +
            "See-through parts show the menu behind, and the corners are rounded off.", 16);
        float row = y;
        AddButton(p, Col2, row, 250, RowH, "Choose image...", ChooseCard);
        AddButton(p, Col2 + 262, row, 118, RowH, "Remove", RemoveCard, () => draft?.Card != null);
        y -= RowStep;
        Func<bool> picture = () => cardPicture != null;
        Func<bool> notSquare = () => cardPicture != null && CardShape() != CardLayout.Shape.Square;
        const float half = Col2W / 2 - 6;
        row = y;
        var fit = AddButton(p, Col2, row, half, RowH, "", ToggleCardFit, notSquare);
        fit.Text = () => CardLookNow().Fill ? "Fit: fill the square" : "Fit: whole picture";
        fit.Active = () => !CardLookNow().Fill;
        fit.Label.fontSize = 19;
        var crisp = AddButton(p, Col2 + half + 12, row, half, RowH, "", CycleCardCrisp, picture);
        crisp.Text = CardCrispText;
        crisp.Active = () => CardLookNow().Smooth == false;
        crisp.Label.fontSize = 19;
        y -= RowStep;
        PreviewStepper(p, Col2, ref y, Col2W, CardCropField, () => "Crop: " + CardLayout.FocusText(CardShape(), CardFocusAlong()),
            () => StepCardCrop(-1), () => StepCardCrop(1), () => notSquare() && CardLookNow().Fill);
        AddText(p, Col2, ref y, Col2W, 76, CardStateText, 17);
        var warnings = AddText(p, Col2, ref y, Col2W, 90, CardWarnings, 16);
        warnings.color = Hex(0xF2B02E);
    }

    private static void BuildCardPreview(RectTransform panel, float y)
    {
        var lavender = Hex(0x9D92B4);
        // What shows through the picture's see-through parts: the arcade's dark menu.
        cardPreview = MakeImage("CardPreview", panel, Hex(0x221E2D)).rectTransform;
        PlaceTop(cardPreview, Col2, y, CardW, CardH);
        // The arcade's list cuts the title at the card's right edge.
        try { cardPreview.gameObject.AddComponent<RectMask2D>(); }
        catch (Exception ex) { ReportCard("the card preview can't cut the title at the card's edge", ex); }

        // The picture's rounded square: a mask whose own graphic doesn't show, as the arcade's Image_Mask.
        var slot = MakeRect("Slot", cardPreview);
        PlaceTop(slot, 0, 0, SlotBox, SlotBox);
        try
        {
            var shape = slot.gameObject.AddComponent<Image>();
            shape.sprite = RoundFill();
            shape.type = Image.Type.Sliced;
            shape.raycastTarget = false;
            var mask = slot.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = false;
        }
        catch (Exception ex)
        {
            // Square corners then; the picture is the same.
            ReportCard("the card preview's rounded corners can't be made", ex);
            var shape = slot.gameObject.GetComponent<Image>();
            if (shape) shape.enabled = false;
        }
        // Fitted whole into the square, as the arcade fits the card's sprite (Image_Enemy: Simple, preserveAspect).
        cardImage = MakeImage("Picture", slot, Color.white);
        cardImage.preserveAspect = true;
        float inset = (SlotBox - SlotPicture) / 2;
        PlaceTop(cardImage.rectTransform, inset, -inset, SlotPicture, SlotPicture);
        cardImage.gameObject.SetActive(false);

        // The picture's frame and the card's outline, drawn over it.
        foreach (var (name, w) in new[] { ("Frame", SlotBox), ("Outline", CardW) })
        {
            var line = MakeImage(name, cardPreview, lavender);
            line.sprite = RoundLine();
            line.type = Image.Type.Sliced;
            line.fillCenter = false;
            PlaceTop(line.rectTransform, 0, 0, w, CardH);
        }

        // The title: the typed text while it's typed. Rich text is on, as it is in the arcade.
        cardTitle = MakeText("Title", cardPreview, TitleSize, TextAlignmentOptions.Left);
        cardTitle.color = Hex(0xFCFFF5);
        cardTitle.enableWordWrapping = false;
        cardTitle.overflowMode = TextOverflowModes.Overflow;
        PlaceTop(cardTitle.rectTransform, TitleX, -TitleY, CardW - TitleX, TitleH);
        Ui.AddLiveText(cardTitle, CardTitleNow);

        var melody = MakeImage("Melody", cardPreview, Hex(0x48425E));
        melody.sprite = RoundFill();
        melody.type = Image.Type.Sliced;
        melody.pixelsPerUnitMultiplier = 3f;
        PlaceTop(melody.rectTransform, MelodyX, -MelodyY, MelodyW, MelodyH);

        var tag = MakeText("Tag", cardPreview, TagSize, TextAlignmentOptions.Left);
        tag.color = Hex(0xF2B02E);
        tag.enableWordWrapping = false;
        tag.overflowMode = TextOverflowModes.Ellipsis;
        PlaceTop(tag.rectTransform, TitleX, -TagY, MelodyX + MelodyW - TitleX, TagH);
        Ui.AddLiveText(tag, () => cardTag ?? "").Visible = () => cardTag != null && BattleNoticeArcade.BadgeInstalled;
    }

    // The mask's rounded square, hard-edged like the game's (a mask keeps any pixel that isn't fully see-through).
    private static Sprite RoundFill() => roundFill != null && roundFill ? roundFill : roundFill = RoundedSprite("fill", SlotRadius, 0);

    // The frame's line, as thick as the arcade's (1.25 units) and with its corners.
    private static Sprite RoundLine() => roundLine != null && roundLine ? roundLine : roundLine = RoundedSprite("line", FrameRadius, FrameLine);

    /// <summary>A rounded square, sliced so it stretches to any size and keeps its corners: filled, or a line <paramref name="line"/> pixels thick.</summary>
    private static Sprite RoundedSprite(string name, int radius, int line)
    {
        int border = radius + 2, size = 2 * border + 2;
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float outer = RoundedDistance(x + 0.5f, y + 0.5f, size, radius);
                float alpha = line <= 0 ? (outer <= 0 ? 1f : 0f)
                    : Math.Clamp(0.5f - outer, 0f, 1f) * Math.Clamp(0.5f + RoundedDistance(x + 0.5f - line, y + 0.5f - line, size - 2 * line, Math.Max(0, radius - line)), 0f, 1f);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Math.Round(alpha * 255));
            }
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = CustomBattles.RuntimePrefix + "creator/round-" + name,
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        // At 100 pixels a unit on this canvas (whose reference is 100), a pixel of it is a unit.
        var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        sprite.name = texture.name;
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    // How far a point is outside a size x size square with rounded corners (negative inside).
    private static float RoundedDistance(float x, float y, float size, float radius)
    {
        float half = size / 2f;
        float dx = Math.Abs(x - half) - (half - radius), dy = Math.Abs(y - half) - (half - radius);
        float ox = Math.Max(dx, 0f), oy = Math.Max(dy, 0f);
        return (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(dx, dy), 0f) - radius;
    }

    // ---- the picture -----------------------------------------------------------------------------

    private static void LoadCardPreview()
    {
        ClearCardPreview();
        string? card = draft?.Card;
        if (draft == null || card == null) { cardState = "No card image: the arcade shows a plain card."; ShowPlainCard(); return; }
        string? name = PackageFiles.SafeName(card);
        string path = name == null ? "" : Path.Combine(draft.Folder, name.Replace('/', Path.DirectorySeparatorChar));
        if (name == null || !File.Exists(path)) { cardState = $"{card} is missing, so the arcade shows a plain card."; ShowPlainCard(); return; }
        try
        {
            var info = new FileInfo(path);
            if (info.Length > BattlePackage.MaxImageBytes)
            {
                cardState = $"{card} is too big (at most {BattlePackage.MaxImageBytes / (1024 * 1024)} MB), so the arcade shows a plain card.";
                ShowPlainCard();
                return;
            }
            // Made as the arcade makes it; its sprite shows the card's part of it, so the square can move.
            var look = CardLookNow();
            var made = CustomBattles.CardImages.Make(File.ReadAllBytes(path), "NocturneButBetter/creator/card", look, keepPart: false, out string? why);
            if (made == null) { cardState = $"{card}\nIt {why}, so the arcade shows a plain card."; ShowPlainCard(); return; }
            cardPicture = made;
            cardPictureFill = look.Fill;
            cardImage!.sprite = made.Sprite;
            cardImage.gameObject.SetActive(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            cardState = $"{card} couldn't be read: {ex.Message}";
            ShowPlainCard();
        }
    }

    // The arcade's card for a battle without a picture it can show.
    private static void ShowPlainCard()
    {
        if (!cardImage) return;
        cardImage!.sprite = CustomBattles.CardImages.Placeholder;
        cardImage.gameObject.SetActive(true);
    }

    private static void ClearCardPreview()
    {
        cardDragging = false;
        cardDragFocus = null;
        if (cardImage)
        {
            cardImage!.sprite = null;
            cardImage.gameObject.SetActive(false);
        }
        var old = cardPicture;
        cardPicture = null;
        if (old == null) return;
        if (old.Sprite) Object.Destroy(old.Sprite);
        if (old.Texture) Object.Destroy(old.Texture);
    }

    /// <summary>
    /// A new card picture, or none: the old one's square and crispness were for it, so they go;
    /// filling the square or showing it whole stays, and a battle's first picture fills the square.
    /// </summary>
    private static void SetCardPicture(BattleDraft d, string? card)
    {
        bool changed = !string.Equals(d.Card, card, StringComparison.OrdinalIgnoreCase);
        d.Card = card;
        if (changed)
        {
            d.SetCardKey(CardLayout.FocusKey, null);
            d.SetCardKey(CardLayout.SmoothKey, null);
        }
        if (card == null) d.SetCardKey(CardLayout.FitKey, null);
        else if (!d.HasCardKey(CardLayout.FitKey)) d.SetCardKey(CardLayout.FitKey, JsonValue.Create("fill"));
        if (draft == d) LoadCardPreview();
    }

    // ---- the look ---------------------------------------------------------------------------------

    // The draft's card keys, read again when the draft changes, with their problems.
    private static BattleDraft? cardLookDraft;
    private static int cardLookChanges = -1;
    private static CardLayout.Look cardLook = CardLayout.Look.Default;
    private static readonly List<string> cardLookProblems = new();

    private static CardLayout.Look CardLookNow()
    {
        var d = draft;
        if (d == null) return CardLayout.Look.Default;
        if (cardLookDraft != d || cardLookChanges != d.Changes)
        {
            cardLookDraft = d;
            cardLookChanges = d.Changes;
            cardLookProblems.Clear();
            cardLook = d.CardLook(cardLookProblems);
        }
        return cardLook;
    }

    private static CardLayout.Shape CardShape() =>
        cardPicture is { } p ? CardLayout.ShapeOf(p.FileWidth, p.FileHeight) : CardLayout.Shape.Square;

    // Only the long side's focus moves the largest square: x for a wide picture, y for a tall one.
    private static double CardFocusAlong()
    {
        if (cardDragFocus is double dragged) return dragged;
        var look = CardLookNow();
        return CardShape() == CardLayout.Shape.Tall ? look.FocusY : look.FocusX;
    }

    private static CardLayout.Look WithFocus(CardLayout.Look look, double along) =>
        CardShape() == CardLayout.Shape.Tall ? new CardLayout.Look(look.Fill, look.FocusX, along, look.Smooth) : new CardLayout.Look(look.Fill, along, look.FocusY, look.Smooth);

    private static void SetCardFocus(double along)
    {
        var d = draft;
        if (d == null || cardPicture == null) return;
        var look = WithFocus(CardLookNow(), Math.Clamp(Math.Round(along, 3), 0, 1));
        // The middle is the default, so it isn't written.
        d.SetCardKey(CardLayout.FocusKey, look.FocusX == 0.5 && look.FocusY == 0.5 ? null : BattleDraft.ArtPairNode((look.FocusX, look.FocusY)));
    }

    // 10% a step, to the next tenth.
    private static void StepCardCrop(int direction)
    {
        double tenths = CardFocusAlong() * 10;
        double next = direction > 0 ? Math.Floor(tenths + 1e-6) + 1 : Math.Ceiling(tenths - 1e-6) - 1;
        SetCardFocus(Math.Clamp(next / 10, 0, 1));
    }

    private static void ToggleCardFit()
    {
        if (draft == null || cardPicture == null) return;
        bool fill = !CardLookNow().Fill;
        draft.SetCardKey(CardLayout.FitKey, JsonValue.Create(fill ? "fill" : "fit"));
        LoadCardPreview();
        Say(fill ? "The picture fills the card's square; what sticks out is cut off. Crop picks the part."
            : "The card shows the whole picture, with bars where it isn't square.", 5f);
    }

    // Crisp pixels: worked out from the picture (automatic), on (crisp) or off (smooth), like the Art page's.
    private static string CardCrispText()
    {
        var look = CardLookNow();
        return look.Smooth switch
        {
            false => "Crisp pixels: on",
            true => "Crisp pixels: off",
            _ => $"Crisp pixels: auto ({(cardPicture is { } p && CardLayout.Crisp(p.FileWidth, p.FileHeight, look) ? "on" : "off")})",
        };
    }

    private static void CycleCardCrisp()
    {
        if (draft == null || cardPicture == null) return;
        bool? next = CardLookNow().Smooth switch { null => false, false => true, _ => null };
        draft.SetCardKey(CardLayout.SmoothKey, next is bool b ? JsonValue.Create(b) : null);
        Say(next switch
        {
            false => "Crisp pixels: every pixel stays sharp, like the game's own cards.",
            true => "Crisp pixels off: the picture is smoothed, which suits drawings and photos.",
            _ => "Crisp pixels: worked out from the picture (sharp when it's small, like pixel art).",
        }, 4f);
    }

    private static readonly TextField CardCropField = new()
    {
        Label = "Crop",
        Max = 6,
        Get = () => ((int)Math.Round(CardFocusAlong() * 100)).ToString(),
        Set = text => SetCardFocus((ParseNumber(text, 0, 100, "The crop") ?? 50) / 100),
        Hint = "Type where the square sits, from 0 (left or top) to 100 (right or bottom), then Enter. Empty is the middle. Esc cancels.",
    };

    // ---- every frame on the Info page -------------------------------------------------------------

    // Dragging a filled picture moves its square: the focus while the button is held (written when it's let go).
    private static bool cardDragging;
    private static Vector2 cardDragStart;
    private static double cardDragFrom;
    private static double? cardDragFocus;

    private static void UpdateCardPage(InputMouse? clicks)
    {
        try
        {
            DragCard(clicks);
            SyncCardPicture();
        }
        catch (Exception ex)
        {
            // A preview that fails must not close the creator (and lose unsaved changes) with it.
            cardDragging = false;
            cardDragFocus = null;
            ReportCard("the card preview failed", ex);
        }
    }

    // The picture follows the look: filling or not makes it again (the kept size depends on it); the square and crispness change only its sprite.
    private static void SyncCardPicture()
    {
        var p = cardPicture;
        if (p == null || !p.Texture) return;
        var look = CardLookNow();
        if (look.Fill != cardPictureFill) { LoadCardPreview(); return; }
        CustomBattles.CardImages.Reframe(p, cardDragFocus is double along ? WithFocus(look, along) : look);
        if (cardImage && cardImage!.sprite != p.Sprite) cardImage.sprite = p.Sprite;
    }

    private static void DragCard(InputMouse? clicks)
    {
        var mouse = InputMouse.current;
        var p = cardPicture;
        bool movable = p != null && draft != null && CardLookNow().Fill && CardShape() != CardLayout.Shape.Square;
        if (mouse == null || !movable || !cardImage)
        {
            cardDragging = false;
            cardDragFocus = null;
            return;
        }
        Vector2 pos = mouse.position.ReadValue();
        if (!cardDragging)
        {
            if (clicks == null || !clicks.leftButton.wasPressedThisFrame || typing != null) return;
            if (!RectTransformUtility.RectangleContainsScreenPoint(cardImage!.rectTransform, pos, null)) return;
            cardDragging = true;
            cardDragStart = pos;
            cardDragFrom = CardFocusAlong();
            return;
        }
        float scale = Ui.CanvasRect.localScale.x > 0 ? Ui.CanvasRect.localScale.x : 1f;
        // How far the picture moved along its long side (right or down), in its own pixels: the square spans the slot.
        bool tall = CardShape() == CardLayout.Shape.Tall;
        double units = (tall ? cardDragStart.y - pos.y : pos.x - cardDragStart.x) / scale;
        double pixels = units * Math.Min(p!.FileWidth, p.FileHeight) / SlotPicture;
        // Moving the picture right shows more of its left: the square goes the other way.
        cardDragFocus = Math.Round(CardLayout.MovedFocus(p.FileWidth, p.FileHeight, cardDragFrom, -pixels), 3);
        if (mouse.leftButton.isPressed) return;
        cardDragging = false;
        double end = cardDragFocus.Value;
        cardDragFocus = null;
        if (Math.Abs(end - cardDragFrom) < 0.0005) return;
        SetCardFocus(end);
        Say($"Crop: {CardLayout.FocusText(CardShape(), end)}.", 3f);
    }

    // ---- texts ------------------------------------------------------------------------------------

    private static string CardTitleNow() => draft == null ? "" : BattleDraft.CleanLine(typing == TitleField ? typed : draft.Title);

    // What the state line says: the picture's size and shape and what the card does with it.
    private static string CardStateText()
    {
        var p = cardPicture;
        if (draft == null) return "";
        if (p == null) return Escape(cardState);
        var look = CardLookNow();
        var shape = CardLayout.ShapeOf(p.FileWidth, p.FileHeight);
        string what = shape switch
        {
            CardLayout.Shape.Square => "square: it fills the card.",
            CardLayout.Shape.Wide => look.Fill ? "wider than square: its left and right are cut off." : "wider than square: it shows whole, with bars above and below.",
            _ => look.Fill ? "taller than square: its top and bottom are cut off." : "taller than square: it shows whole, with bars at the sides.",
        };
        string kept = "";
        if (p.Plan.Smaller)
        {
            // The arcade keeps only what the card shows.
            var plan = CardLayout.Work(p.FileWidth, p.FileHeight, look);
            kept = $" The arcade keeps it at {plan.PartWidth} x {plan.PartHeight}.";
        }
        return Escape($"{draft.Card}\n{p.FileWidth} x {p.FileHeight}, {what}{kept}");
    }

    // The card keys' problems, as the loader reads them, and crisp pixels on a big picture.
    private static string CardWarnings()
    {
        if (draft?.Card == null) return "";
        var look = CardLookNow();
        var lines = cardLookProblems.Select(problem => char.ToUpperInvariant(problem[0]) + problem.Substring(1) + ".").ToList();
        if (cardPicture is { } p && look.Smooth == false && CardLayout.Span(p.FileWidth, p.FileHeight, look) > 2 * CardLayout.CrispSide)
            lines.Add("Crisp pixels can look rough on a big picture; they suit pixel art.");
        return Escape(string.Join("\n", lines));
    }

    // ---- the title on the card --------------------------------------------------------------------

    // The last title measured, and what of it the card shows whole.
    private static string cardTitleMeasured = "\u0000", cardTitleShown = "";
    private static bool cardTitleFits = true;
    private static int cardTitleLetters;

    // The title's font has letters all the same width (the game's Bacteria 12): about 16 fit.
    private static int CardTitleLetters()
    {
        if (cardTitleLetters > 0) return cardTitleLetters;
        var text = cardTitle;
        float letter = text != null && text ? text.GetPreferredValues("MMMMMMMMMM").x / 10f : 0f;
        if (letter <= 0) return 16;
        return cardTitleLetters = Math.Max(1, (int)((CardW - TitleX) / letter));
    }

    /// <summary>
    /// Whether the arcade card shows the whole title, and the part it shows whole when it doesn't:
    /// the card's own title font and size, cut at the card's right edge (about 16 letters).
    /// </summary>
    private static (bool Fits, string Shown) TitleOnCard(string title)
    {
        if (title == cardTitleMeasured) return (cardTitleFits, cardTitleShown);
        cardTitleMeasured = title;
        float room = CardW - TitleX;
        Func<string, bool> fits;
        var text = cardTitle;
        if (text != null && text && text.GetPreferredValues("M").x > 0) fits = part => text.GetPreferredValues(part).x <= room + 0.5f;
        else fits = part => part.Length <= CardTitleLetters();
        cardTitleFits = title.Length == 0 || fits(title);
        cardTitleShown = title;
        if (!cardTitleFits)
        {
            // The longest start that fits (never half of a character written as two).
            int low = 0, high = title.Length;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (fits(title.Substring(0, mid))) low = mid;
                else high = mid - 1;
            }
            if (low > 0 && low < title.Length && char.IsHighSurrogate(title[low - 1])) low--;
            cardTitleShown = title.Substring(0, low).TrimEnd();
        }
        return (cardTitleFits, cardTitleShown);
    }

    // Under the Title field: how much of the title the card shows.
    private static string TitleCardText()
    {
        if (draft == null) return "";
        var (fits, shown) = TitleOnCard(CardTitleNow());
        return fits ? $"The arcade card shows about {CardTitleLetters()} letters of the title."
            : $"<color={Amber}>The arcade card cuts the title after \"{Escape(shown)}\". About {CardTitleLetters()} letters fit.</color>";
    }

    // The Title field's typing hint: "12 / 80, fits the card" or "20 / 80, the card shows "QA Creator Battl"".
    private static string TitleCardMeasure(string typedTitle)
    {
        var (fits, shown) = TitleOnCard(BattleDraft.CleanLine(typedTitle));
        return fits ? "fits the card" : $"the card shows \"{shown}\"";
    }

    private static void ReportCard(string what, Exception ex)
    {
        if (reportedCardError) return;
        reportedCardError = true;
        ModLog.Error($"Battle creator: {what}: {ex}");
    }
}
