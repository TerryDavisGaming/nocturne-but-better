using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>
/// A row under Options > Gameplay > Note Colors that shows the chosen colors on notes in the
/// current skin: a four-lane chord with a hold, the middle note of five-lane charts, and a
/// mine. Mines keep their own red whatever the colors, and several palettes come close to it.
/// </summary>
internal sealed class NoteColorPreview
{
    private const string RootName = "FlatScrollNoteColorPreview";
    // The page's canvas is 640 x 360 units; notes are drawn at this many units per field unit.
    private const float Scale = 1f;
    private const float Height = 48f;
    // The lanes fill the top of the row and the captions sit under them.
    private const float LaneHeight = 32f;
    private const float NoteY = -16f;
    private const float CaptionY = -40f;
    private const float LaneStep = 30f * Scale;
    private const float LaneWidth = 22f * Scale;
    // Lined up with the right edge of the rows' values.
    private const float MineX = 374f;
    private const float DividerX = MineX - 26f;
    private const float MiddleX = DividerX - 26f;
    private const float FirstLaneX = MiddleX - 40f - 3f * LaneStep;
    // The game's hold is drawn this wide behind the bar note, with its pattern a little narrower.
    private const float HoldWidth = 21.5f * Scale;
    private const float HoldPatternWidth = 19f * Scale;
    // The mine's colors from the game's note prefab; no note style changes them.
    private static readonly Color MineBody = new(0.8396226f, 0.2797073f, 0.2019847f, 1f);
    private static readonly Color MineMarks = new(1f, 0.5254902f, 0.5058824f, 1f);
    private static readonly Color MineLight = new(1f, 0.8313726f, 0.8f, 1f);

    private readonly GameplayOptionsMenu _menu;
    private readonly GameObject _root;
    // Lanes 0 to 3 of a four-lane chart, then the middle of a five-lane chart.
    private readonly Note[] _notes = new Note[5];
    private readonly Image[] _lanes = new Image[6];
    private readonly Image[] _edges = new Image[12];
    private readonly Image _holdBody, _holdPattern;
    private string? _shown;

    private static bool _installed;

    /// <summary>Refreshes the previews whenever the note colors change, from the row or a reset.</summary>
    internal static void Install(HarmonyLib.Harmony harmony)
    {
        if (_installed) return;
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(NoteStyleManager), "SetStyle")
                ?? throw new MissingMethodException(typeof(NoteStyleManager).FullName, "SetStyle"),
            postfix: new HarmonyMethod(typeof(NoteColorPreview), nameof(SetStylePostfix)));
        _installed = true;
    }

    private static void SetStylePostfix() => OptionsMenuIntegration.RefreshPreviews();

    private NoteColorPreview(GameplayOptionsMenu menu, GameObject root)
    {
        _menu = menu;
        _root = root;
        var rect = root.GetComponent<RectTransform>();
        float[] laneX = { FirstLaneX, FirstLaneX + LaneStep, FirstLaneX + 2f * LaneStep, FirstLaneX + 3f * LaneStep, MiddleX, MineX };
        for (int i = 0; i < _lanes.Length; i++)
        {
            _lanes[i] = AddImage(rect, "Lane" + i, laneX[i], -LaneHeight * 0.5f, LaneWidth, LaneHeight);
            _edges[i * 2] = AddImage(rect, "Lane" + i + "Left", laneX[i] - LaneWidth * 0.5f, -LaneHeight * 0.5f, 0.5f, LaneHeight);
            _edges[i * 2 + 1] = AddImage(rect, "Lane" + i + "Right", laneX[i] + LaneWidth * 0.5f, -LaneHeight * 0.5f, 0.5f, LaneHeight);
        }
        // A hold in the first lane, reaching up from its head.
        _holdBody = AddImage(rect, "HoldBody", FirstLaneX, NoteY * 0.5f, HoldWidth, -NoteY);
        _holdPattern = AddImage(rect, "HoldPattern", FirstLaneX, NoteY * 0.5f, HoldPatternWidth, -NoteY);
        for (int i = 0; i < _notes.Length; i++)
            _notes[i] = new Note(rect, "Note" + i, i < 4 ? laneX[i] : MiddleX);

        var divider = AddImage(rect, "Divider", DividerX, -LaneHeight * 0.5f, 0.5f, LaneHeight - 6f);
        divider.color = new Color(1f, 1f, 1f, 0.25f);
        var mine = new Note(rect, "Mine", MineX);
        mine.ShowMine();

        // Text in the rows' own font and size, in the color of a row that isn't selected.
        var row = menu.noteStyleButton;
        var title = row.transform.Find("Label");
        var source = title ? title!.GetComponent<TMP_Text>() : null;
        var style = row.style;
        var text = style ? style!.NormalTextColor : source ? source!.color : Color.white;
        AddText(rect, "Title", "Preview", source, new Vector2(0f, 1f), new Vector2(1f, 1f), 0f, -LaneHeight, TextAlignmentOptions.Left, text);
        AddText(rect, "MiddleCaption", "5 lanes", source, new Vector2(0f, 1f), new Vector2(0f, 1f), MiddleX, CaptionY, TextAlignmentOptions.Center, new Color(text.r, text.g, text.b, text.a * 0.75f));
        AddText(rect, "MineCaption", "mine", source, new Vector2(0f, 1f), new Vector2(0f, 1f), MineX, CaptionY, TextAlignmentOptions.Center, MineBody);
    }

    internal bool IsAlive => _root;

    internal static NoteColorPreview? Create(GameplayOptionsMenu menu)
    {
        var row = menu.noteStyleButton;
        if (!row) return null;
        var parent = row.transform.parent;
        if (!parent) return null;
        GameObject? root = null;
        try
        {
            root = new GameObject(RootName);
            root.layer = parent.gameObject.layer;
            var rect = root.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            // The page's rows are 400 units wide and laid out from the top left by the list.
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            var rowRect = row.transform.TryCast<RectTransform>();
            rect.sizeDelta = new Vector2(rowRect ? rowRect!.sizeDelta.x : 400f, Height);
            rect.SetSiblingIndex(row.transform.GetSiblingIndex() + 1);
            var preview = new NoteColorPreview(menu, root);
            if (parent.TryCast<RectTransform>() is { } content) LayoutRebuilder.MarkLayoutForRebuild(content);
            return preview;
        }
        catch
        {
            if (root) Object.Destroy(root);
            throw;
        }
    }

    /// <summary>Shows the current note colors and skin; cheap when nothing changed.</summary>
    internal void Refresh()
    {
        if (!_root) return;
        var row = _menu ? _menu.noteStyleButton : null;
        // The game hides the Note Colors row when it has no colors to offer.
        bool show = row && row!.gameObject.activeSelf;
        if (_root.activeSelf != show) _root.SetActive(show);
        if (!show) return;
        int rowIndex = row!.transform.GetSiblingIndex();
        int index = _root.transform.GetSiblingIndex();
        if (index != rowIndex + 1) _root.transform.SetSiblingIndex(index < rowIndex ? rowIndex : rowIndex + 1);

        var skin = SettingsState.NoteSkin;
        string key = NoteStyleManager.CurrentStyleId + "|" + ColumnStyleManager.CurrentStyleId + "|" + skin;
        if (key == _shown) return;

        var lanes = ColumnStyleManager.CurrentColumnColors.defaultColors;
        foreach (var lane in _lanes) lane.color = lanes.color2;
        foreach (var edge in _edges) edge.color = new Color(lanes.color1.r, lanes.color1.g, lanes.color1.b, 0.5f);
        for (int i = 0; i < _notes.Length; i++)
        {
            int column = i < 4 ? i : 2, count = i < 4 ? 4 : 5;
            _notes[i].Show(skin, column, count, NoteStyleManager.GetColorsForColumn(column, count));
        }
        // The hold body takes the second color and its pattern the first, as in battle.
        var hold = NoteStyleManager.GetColorsForColumn(0, 4);
        float width = skin == NoteSkin.Default ? 1f : NoteSkins.HoldWidthFactor;
        SetWidth(_holdBody, HoldWidth * width);
        SetWidth(_holdPattern, HoldPatternWidth * width);
        _holdBody.color = hold.color2;
        _holdPattern.color = new Color(hold.color1.r, hold.color1.g, hold.color1.b, 0.6f);
        _shown = key;
    }

    internal void Destroy()
    {
        if (_root) Object.Destroy(_root);
    }

    private static void SetWidth(Image image, float width)
    {
        var rect = image.rectTransform;
        rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);
    }

    private static Image AddImage(RectTransform parent, string name, float x, float y, float width, float height)
    {
        var go = new GameObject(name);
        go.layer = parent.gameObject.layer;
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(width, height);
        var image = go.AddComponent<Image>();
        // Not a menu target: the pointer passes through to the list.
        image.raycastTarget = false;
        return image;
    }

    private static void AddText(RectTransform parent, string name, string text, TMP_Text? source,
                                Vector2 anchorMin, Vector2 anchorMax, float x, float y,
                                TextAlignmentOptions alignment, Color color)
    {
        var go = new GameObject(name);
        go.layer = parent.gameObject.layer;
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        var label = go.AddComponent<TextMeshProUGUI>();
        float size = 12f;
        if (source)
        {
            label.font = source!.font;
            label.fontSharedMaterial = source.fontSharedMaterial;
            size = source.fontSize;
            label.margin = source.margin;
        }
        if (anchorMin.x != anchorMax.x)
        {
            // The title takes the same band and left edge as the row titles.
            var sourceRect = source ? source!.rectTransform : null;
            rect.pivot = new Vector2(0f, 1f);
            rect.offsetMin = new Vector2(sourceRect ? sourceRect!.offsetMin.x : 16f, y);
            rect.offsetMax = new Vector2(sourceRect ? sourceRect!.offsetMax.x : 0f, 0f);
        }
        else
        {
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(80f, size + 4f);
            label.margin = Vector4.zero;
        }
        label.text = text;
        label.fontSize = size;
        label.alignment = alignment;
        label.enableWordWrapping = false;
        label.raycastTarget = false;
        label.color = color;
    }

    /// <summary>One note drawn from three tinted layers, like the game's notes and the mod's skins.</summary>
    private sealed class Note
    {
        private readonly RectTransform _rect;
        private readonly Image _body, _glyph, _accent;

        internal Note(RectTransform parent, string name, float x)
        {
            var go = new GameObject(name);
            go.layer = parent.gameObject.layer;
            _rect = go.AddComponent<RectTransform>();
            _rect.SetParent(parent, false);
            _rect.anchorMin = new Vector2(0f, 1f);
            _rect.anchorMax = new Vector2(0f, 1f);
            _rect.pivot = new Vector2(0.5f, 0.5f);
            _rect.anchoredPosition = new Vector2(x, NoteY);
            _body = Layer("Body");
            _glyph = Layer("Glyph");
            _accent = Layer("Accent");
        }

        private Image Layer(string name)
        {
            var go = new GameObject(name);
            go.layer = _rect.gameObject.layer;
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(_rect, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var image = go.AddComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        internal void Show(NoteSkin skin, int column, int count, CombatNoteColorSet colors)
        {
            if (skin == NoteSkin.Default)
            {
                _body.sprite = SkinSprites.BarBody;
                _glyph.sprite = SkinSprites.BarGlyph;
                _accent.sprite = SkinSprites.BarAccent;
                SetBarSize();
                _rect.localRotation = Quaternion.identity;
            }
            else
            {
                var shape = NoteSkins.ShapeFor(skin, column, count);
                _body.sprite = SkinSprites.NoteBody(shape);
                _glyph.sprite = SkinSprites.NoteGlyph(shape);
                _accent.sprite = SkinSprites.NoteAccent(shape);
                float size = NoteSkins.NoteScale * Scale;
                _rect.sizeDelta = new Vector2(size, size);
                // On screen the arrows read left, down, up, right in both scroll directions.
                _rect.localRotation = Quaternion.Euler(0f, 0f, NoteSkins.AngleFor(skin, column, count, false));
            }
            _body.color = colors.color1;
            _glyph.color = colors.color3;
            _accent.color = colors.color2;
        }

        internal void ShowMine()
        {
            _body.sprite = SkinSprites.MineBody;
            _glyph.sprite = SkinSprites.MineLight;
            _accent.sprite = SkinSprites.MineMarks;
            SetBarSize();
            _body.color = MineBody;
            _glyph.color = MineLight;
            _accent.color = MineMarks;
        }

        private void SetBarSize() =>
            _rect.sizeDelta = new Vector2(SkinArt.BarMaskWidth / 8f * Scale, SkinArt.BarMaskHeight / 8f * Scale);
    }
}
