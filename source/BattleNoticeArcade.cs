using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>
/// Shows in the arcade what a custom battle sets for the player, before they start it. When a
/// battle is selected, the box under the score on the right says so ("Sets your level and gear.",
/// the level, the items), in smaller text when that is what it takes to show it all, then shows the
/// battle's lore if there is room; the game has that box (its lore box) but keeps it hidden, so it
/// is shown for custom battles only. The battle's card gets a
/// short tag under its melody, like "Set gear &amp; level 12". The game reuses its score views and
/// cards for every song, so both are hidden again for the game's own songs. The text comes from
/// <see cref="BattleNotice"/>, as the battle creator's preview does.
/// </summary>
internal static class BattleNoticeArcade
{
    private const string BadgeName = "NbbBattleBadge";
    private const string BoxName = "InfoBox";
    // The box's text width in the game's layout (the column less the box's padding).
    private const float DefaultWidth = 162.8f;
    // The mod's warning colour: the tag says the battle changes your character.
    private static readonly Color BadgeColor = new(0xF2 / 255f, 0xB0 / 255f, 0x2E / 255f, 1f);

    // The boxes this turned on; only these are turned off again.
    private static readonly HashSet<IntPtr> ShownBoxes = new();
    // Each box label's own font size, from before this first changed it (a long notice shows smaller).
    private static readonly Dictionary<IntPtr, float> OwnSizes = new();
    private static readonly HashSet<string> Reported = new();

    /// <summary>Whether the box on the right shows what a battle sets.</summary>
    internal static bool BoxInstalled { get; private set; }
    /// <summary>Whether cards show a battle's tag.</summary>
    internal static bool BadgeInstalled { get; private set; }

    // Each part is optional: without it the battles still set their gear and level, and the log says so.
    internal static void Install(HarmonyLib.Harmony harmony)
    {
        BoxInstalled = TryPatch(harmony, typeof(ArcadeHighScoreViewV2), "SetScore", nameof(SetScorePostfix));
        BadgeInstalled = TryPatch(harmony, typeof(ArcadeSongGroup), "SetSongInfo", nameof(SetSongInfoPostfix));
    }

    private static bool TryPatch(HarmonyLib.Harmony harmony, Type type, string method, string postfix)
    {
        try
        {
            MethodInfo target = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.FullName, method);
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(BattleNoticeArcade), postfix));
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Error($"Arcade notice: {type.Name}.{method} could not be patched, so the arcade can't show what custom battles set: {ex}");
            return false;
        }
    }

    // ---- what a battle sets ---------------------------------------------------------------------------

    /// <summary>
    /// The notice's input for a battle as the arcade will play it. Only what the mod can set is
    /// promised; <paramref name="details"/> false leaves out what only the box needs.
    /// </summary>
    internal static NoticeInput InputFor(BattlePackage package, bool details = true) =>
        Input(package.Gear, package.Level.Level, details ? CustomBattles.LoreText(package) : "", details);

    /// <summary>The same for the battle creator's draft, saved or not.</summary>
    internal static NoticeInput InputFor(BattleDraft draft) =>
        Input(draft.Gear, draft.SetLevel ? draft.LevelValue : null, BattleNotice.Credits(draft.Lore, draft.Artist, draft.Author), true);

    private static NoticeInput Input(GearDefinition gear, int? level, string lore, bool details)
    {
        int? shown = BattleGear.Installed && BattleGear.LevelAvailable ? BattleGear.EffectiveLevel(level) : null;
        // While a battle's level is still in, the game's level isn't the player's own.
        int? yours = details && !BattleGear.LevelSet ? BattleGear.PlayerLevel() : null;
        return BattleNotice.Input(BattleGear.Installed ? gear : null, shown, yours, lore, details ? ItemName : (_, id) => id);
    }

    // As the battle will fill the slot: an unknown item, or one for another slot, leaves it empty.
    private static string? ItemName(GearSlot slot, string id)
    {
        if (GearCatalog.All.Count == 0) return id;
        var item = GearCatalog.Find(id);
        return item != null && item.Slot == slot ? item.Name : null;
    }

    // ---- the box on the right -------------------------------------------------------------------------

    // Whenever the score view shows a song (hover, select, a difficulty tab, the screen opening),
    // it has just written the song's lore into the hidden box; this shows the box for custom battles.
    private static void SetScorePostfix(ArcadeHighScoreViewV2 __instance, ArcadeSongInfo currentSong)
    {
        try
        {
            if (__instance == null || !__instance) return;
            var label = __instance.loreLabel;
            if (label == null || !label) return;
            var parent = label.transform.parent;
            var box = parent ? parent.gameObject : null;
            if (box == null || box.name != BoxName)
            {
                Note("Arcade notice: the score panel isn't laid out as expected, so custom battles' notes can't show there.");
                return;
            }
            var battle = currentSong != null ? CustomBattles.Find(currentSong.songData) : null;
            var input = battle != null ? InputFor(battle.Package) : null;
            if (input == null || BattleNotice.Whole(input) == null)
            {
                // A game song (the view is shared), or a battle with nothing to say.
                if (ShownBoxes.Remove(box.Pointer))
                {
                    if (box.activeSelf) box.SetActive(false);
                    if (OwnSizes.TryGetValue(label.Pointer, out float before)) label.fontSize = before;
                }
                return;
            }
            // On first, so the text measures the way it will draw.
            if (!box.activeSelf)
            {
                box.SetActive(true);
                ShownBoxes.Add(box.Pointer);
            }
            label.raycastTarget = false;
            var image = box.GetComponent<Image>();
            if (image) image.raycastTarget = false;
            float own = OwnSize(label);
            string text = BattleNotice.Box(input, Fitter(label, own), out float size) ?? "";
            label.fontSize = own * size / BattleNotice.BoxFontSize;
            label.text = text;
        }
        catch (Exception ex) { Report(ex); }
    }

    private static float OwnSize(TMP_Text label)
    {
        if (OwnSizes.TryGetValue(label.Pointer, out float own)) return own;
        own = label.fontSize;
        if (!(own > 0f)) own = BattleNotice.BoxFontSize;
        OwnSizes[label.Pointer] = own;
        return own;
    }

    // Whether a text fits the box above the arcade's footer at a size (in the notice's points, where
    // BattleNotice.BoxFontSize is the label's own size): no higher than the box's lines at the label's own size.
    private static Func<string, float, bool> Fitter(TMP_Text label, float own)
    {
        try
        {
            // Right after the box is first turned on, the game may not have laid it out yet: never
            // wider than the game's own layout makes it.
            float width = label.rectTransform.rect.width;
            width = width < 100f ? DefaultWidth : Math.Min(width, DefaultWidth);
            // One line is only as high as its letters; each line after it adds the font's whole line
            // height, gap included, so both are measured, at the label's own size: smaller text fits more
            // lines in the same room.
            label.fontSize = own;
            float one = label.GetPreferredValues("Ag", 1000f, 0f).y;
            float step = label.GetPreferredValues("Ag\nAg", 1000f, 0f).y - one;
            if (one > 0f && step > 0f)
            {
                float most = BattleNotice.MostHeight(one, step);
                return (text, size) =>
                {
                    label.fontSize = own * size / BattleNotice.BoxFontSize;
                    return label.GetPreferredValues(text, width, 0f).y <= most;
                };
            }
            Note("Arcade notice: the box's text measures 0 high, so its size is estimated.");
        }
        catch (Exception ex) { Note("Arcade notice: the box's text can't be measured, so its size is estimated: " + ex.Message); }
        return BattleNotice.FitsLines;
    }

    // ---- the card's tag --------------------------------------------------------------------------------

    // The arcade builds every card from a pool each time a screen opens.
    private static void SetSongInfoPostfix(ArcadeSongGroup __instance, ArcadeSongInfo song)
    {
        try
        {
            if (__instance == null || !__instance) return;
            var title = __instance.songTitle;
            if (title == null || !title) return;
            var parent = title.transform.parent;
            if (!parent) return;
            var found = parent.Find(BadgeName);
            var battle = song != null ? CustomBattles.Find(song.songData) : null;
            string? tag = battle != null ? BattleNotice.Badge(InputFor(battle.Package, details: false)) : null;
            if (tag == null)
            {
                // Cards are reused for other songs.
                if (found && found.gameObject.activeSelf) found.gameObject.SetActive(false);
                return;
            }
            var badge = found ? found.GetComponent<TMP_Text>() : MakeBadge(title, parent);
            if (badge == null || !badge) return;
            badge.text = tag;
            if (!badge.gameObject.activeSelf) badge.gameObject.SetActive(true);
        }
        catch (Exception ex) { Report(ex); }
    }

    // A copy of the card's title, smaller and in the mod's colour, under the melody row.
    private static TMP_Text? MakeBadge(TMP_Text title, Transform parent)
    {
        var copy = Object.Instantiate(title.gameObject, parent, false);
        copy.name = BadgeName;
        // Nothing may write over the tag.
        foreach (var localize in copy.GetComponentsInChildren<Localize>(true)) Object.Destroy(localize);
        var text = copy.GetComponent<TMP_Text>();
        if (text == null || !text)
        {
            Object.Destroy(copy);
            return null;
        }
        text.enableAutoSizing = false;
        text.fontSize = 8f;
        text.color = BadgeColor;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        var rect = text.rectTransform;
        rect.sizeDelta = new Vector2(rect.sizeDelta.x, 10f);
        var layout = copy.GetComponent<LayoutElement>();
        if (layout)
        {
            layout.minHeight = -1f;
            layout.preferredHeight = 10f;
        }
        // Last in the card's column: right under the one melody row (the column skips hidden rows).
        copy.transform.SetAsLastSibling();
        return text;
    }

    // ---- logging -------------------------------------------------------------------------------------

    private static bool reportedError;

    private static void Report(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        ModLog.Error("Arcade notice failed: " + ex);
    }

    private static void Note(string message)
    {
        if (Reported.Add(message)) ModLog.Error(message);
    }
}
