using Il2CppInterop.Runtime;
using UnityEngine;
using Rebinding = UnityEngine.InputSystem.InputActionRebindingExtensions;

namespace NocturneFlatScroll;

/// <summary>
/// Akuma, a palette of the mod's own in the game's Note Colors row. Each lane takes the color of the
/// pad button it's bound to, like the frets of a guitar controller: A green, B red, X blue and
/// Y yellow. A lane on another button, and every lane on a keyboard, has its color in the default
/// pad layout. The middle (attack) lane of five-lane charts is orange, like a guitar's fifth fret.
/// </summary>
/// <remarks>
/// A game palette (<c>NoteStyle</c>) has one color set for the edge lanes, one for the inner lanes
/// and one for the five-lane middle, so it can't give four lanes four colors. Akuma's own sets are
/// marks: a prefix on the note's SetColors, which the skins patch already, swaps a mark for its
/// lane's color. Missed, mine and critical notes and the other palettes never carry a mark, so the
/// check also means "Akuma is picked". Without the mod, the game reads the saved "Akuma" as its
/// first palette (Karma).
/// </remarks>
internal static class AkumaNoteColors
{
    internal const string Id = "Akuma";

    // sRGB, like the game's palettes: body, accents and the hold, line work. The red is a crimson:
    // a true B-button red is almost the mines' red (delta E 2000 of 4.6), and this one is 16 or
    // more from the mines, the enemy's health bar and attacked lanes.
    private static readonly CombatNoteColorSet Green = Set(0x2BB14A, 0x6CDB6E, 0xD2F9CB);
    private static readonly CombatNoteColorSet Red = Set(0xCC1650, 0xF2467A, 0xFFC4D6);
    // Dark line work on the pale note, as the game draws its pale Forest note.
    private static readonly CombatNoteColorSet Yellow = Set(0xF0C814, 0xFFE45C, 0x7A4E00);
    private static readonly CombatNoteColorSet Blue = Set(0x2C6FE6, 0x5AA8FF, 0xBFE0FF);
    private static readonly CombatNoteColorSet Orange = Set(0xF7931E, 0xFFBC5C, 0xFFF0D6);
    // A guitar controller's frets in order. The face buttons give these four.
    private static readonly CombatNoteColorSet[] Frets = { Green, Red, Yellow, Blue };
    private const int GreenFret = 0, RedFret = 1, YellowFret = 2, BlueFret = 3;
    // The palette's own sets, which the game hands out by lane. They are also what shows if the
    // prefix isn't in: green edges, red inner lanes, an orange middle.
    private static readonly CombatNoteColorSet OuterMark = Green;
    private static readonly CombatNoteColorSet InnerMark = Red;
    private static readonly CombatNoteColorSet MiddleMark = Orange;

    // The game's default pad buttons for lanes 1 to 4, used when a binding can't be read.
    private static readonly string[] DefaultPaths = { "leftShoulder", "dpad/right", "buttonWest", "rightShoulder" };
    // The default layout's colors: X gives lane 3 blue, and the lanes on LB, d-pad right and RB
    // take the rest in the frets' order.
    private static readonly int[] DefaultFrets = { GreenFret, RedFret, BlueFret, YellowFret };
    private static readonly string[] Paths = new string[4];
    // The fret of each lane of a four-lane chart; a five-lane chart's side lanes are the same
    // actions. Until the bindings are read: the default layout's colors.
    private static readonly int[] LaneFrets = (int[])DefaultFrets.Clone();

    private static NoteStyle? style;
    private static string? readPaths;
    private static bool readPad;
    private static float nextRead;
    private static int drawnVersion;
    private static bool reportedError;

    /// <summary>Goes up whenever the lane colors change, so the preview knows to redraw.</summary>
    internal static int Version { get; private set; }

    /// <summary>
    /// Called once a second: keeps Akuma in the game's list, and while it's picked, rereads the lanes'
    /// pad buttons, so a rebind or a reset of the bindings shows within a second.
    /// </summary>
    internal static void Update()
    {
        try
        {
            if (EnsureRegistered() == null || NoteStyleManager.CurrentStyleId != Id) return;
            ReadLanes();
            // By version: a note checks the device too, so it may have read the change first.
            if (Version == drawnVersion) return;
            drawnVersion = Version;
            OptionsMenuIntegration.RefreshPreviews();
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    /// <summary>
    /// Called for the options menu's Awake, Activate, DidShow and RefreshViews. The game fills its
    /// Note Colors row only in Awake, from the list as it was then.
    /// </summary>
    internal static void SyncMenu(GameplayOptionsMenu menu)
    {
        try
        {
            var styles = EnsureRegistered();
            if (styles != null) SyncRow(menu, styles.Count);
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    /// <summary>
    /// Prefix on <c>NocturneCombatNoteView.SetColors(CombatNoteColorSet)</c>, in the same patch as the
    /// skins' postfix: a note with Akuma's mark gets its lane's color before the game's shapes, its
    /// hold, and the skins are tinted with it.
    /// </summary>
    internal static void SetColorsPrefix(NocturneCombatNoteView __instance, ref CombatNoteColorSet colors)
    {
        // Other palettes, misses, mines and criticals: nothing to read from the note.
        if (style == null || !IsMark(colors)) return;
        try
        {
            if (!__instance) return;
            colors = Apply(colors, __instance.column, __instance.columnCount);
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    /// <summary>
    /// The colors for a note in <paramref name="column"/> of a <paramref name="count"/>-lane chart:
    /// its lane's color when <paramref name="colors"/> is Akuma's mark for that lane, and
    /// <paramref name="colors"/> unchanged otherwise.
    /// </summary>
    internal static CombatNoteColorSet Apply(CombatNoteColorSet colors, int column, int count)
    {
        if (style == null || column < 0 || column >= count || !Same(colors, MarkFor(column, count))) return colors;
        return ColorsFor(column, count);
    }

    /// <summary>
    /// Checks the device in use, and reads the lanes' pad buttons again at most once a second unless
    /// the device changed. <see cref="Version"/> goes up when the lane colors change.
    /// </summary>
    internal static void ReadLanes()
    {
        bool pad = UsingPad();
        float now = Time.unscaledTime;
        if (readPaths != null && pad == readPad && now < nextRead) return;
        nextRead = now + 1f;
        for (int lane = 0; lane < Paths.Length; lane++) Paths[lane] = PadPath(lane);
        string joined = string.Join("|", Paths);
        if (joined == readPaths && pad == readPad) return;
        readPaths = joined;
        readPad = pad;

        // A lane on a face button takes that button's color, and any other lane keeps its color in
        // the default layout, so a rebind changes only its own lane. On a keyboard the pad's
        // bindings aren't what's pressed, so every lane has its default color.
        for (int lane = 0; lane < LaneFrets.Length; lane++)
        {
            int fret = pad ? FretOf(Paths[lane]) : -1;
            LaneFrets[lane] = fret >= 0 ? fret : DefaultFrets[lane];
        }
        Version++;
    }

    /// <summary>
    /// Puts Akuma at the end of the game's note styles, so the game's own indexes don't move, and
    /// refills the Note Colors row of every menu that already exists. Returns the game's list, or null
    /// before the game has loaded it.
    /// </summary>
    private static Il2CppSystem.Collections.Generic.List<NoteStyle>? EnsureRegistered()
    {
        Il2CppSystem.Collections.Generic.List<NoteStyle>? styles;
        // For the first moments after start-up the game has no data yet, and asking for it throws.
        try { styles = DataUtility.GameConfig?.combatConfig?.noteStyles; }
        catch { return null; }
        if (styles == null || styles.Count == 0) return null;
        // Akuma is last, so this usually stops at once.
        if (style != null && style)
        {
            for (int i = styles.Count - 1; i >= 0; i--)
            {
                var entry = styles[i];
                if (entry != null && entry.Pointer == style.Pointer) return styles;
            }
        }
        else
        {
            // One the game has itself (none in 1.0.1) is left as it is.
            for (int i = 0; i < styles.Count; i++)
            {
                var entry = styles[i];
                if (entry != null && entry && entry.id == Id) return styles;
            }
            style = Create();
            ModLog.Info("Added the Akuma note colors to Options > Gameplay > Note Colors.");
        }
        // New, or the game loaded its list again without it.
        styles.Add(style);
        foreach (var menu in Resources.FindObjectsOfTypeAll<GameplayOptionsMenu>())
            if (menu && menu.gameObject.scene.IsValid()) SyncRow(menu, styles.Count);
        return styles;
    }

    private static NoteStyle Create()
    {
        var created = ScriptableObject.CreateInstance(Il2CppType.Of<NoteStyle>())?.TryCast<NoteStyle>()
            ?? throw new InvalidOperationException("the game couldn't make a note style");
        created.name = "NoteStyle_" + Id;
        // Kept for the whole session, through scene loads and the clean-up of unused assets.
        created.hideFlags = HideFlags.HideAndDontSave;
        created.id = Id;
        created.outerNoteColor = OuterMark;
        created.innerNoteColor = InnerMark;
        created.middleNoteColor = MiddleMark;
        return created;
    }

    private static void SyncRow(GameplayOptionsMenu menu, int count)
    {
        var toggle = menu.noteStyleToggle;
        if (!toggle) return;
        var states = toggle.states;
        if (states == null || states.Count == count) return;
        menu.PopulateNoteStyleStates();
        // That also switches the row on, so only for a row that's showing: the custom charts page
        // hides it, and the page's next refresh puts the value right.
        var row = menu.noteStyleButton;
        if (row && row.gameObject.activeSelf) menu.RefreshNoteStyleToggle();
    }

    /// <summary>The set the game gives a lane from a palette (<c>NoteStyle.GetColorsForColumn</c>).</summary>
    private static CombatNoteColorSet MarkFor(int column, int count) =>
        column == 0 || column == count - 1 ? OuterMark : count == 5 && column == 2 ? MiddleMark : InnerMark;

    private static bool IsMark(in CombatNoteColorSet colors) =>
        Same(colors, OuterMark) || Same(colors, InnerMark) || Same(colors, MiddleMark);

    private static CombatNoteColorSet ColorsFor(int column, int count)
    {
        // Every note checks the device, so a switch between keyboard and pad shows from the next one.
        ReadLanes();
        if (count == 4) return Frets[LaneFrets[column]];
        // A five-lane chart's side lanes are the four-lane actions; its middle is the attack.
        if (count == 5) return column == 2 ? Orange : Frets[LaneFrets[column < 2 ? column : column - 1]];
        // Other lane counts (the game has none): frets in order, with an odd count's middle orange.
        int middle = count % 2 == 1 ? count / 2 : -1;
        if (column == middle) return Orange;
        return Frets[(middle >= 0 && column > middle ? column - 1 : column) % Frets.Length];
    }

    /// <summary>
    /// The control a lane's gamepad binding points at, after any rebind: "buttonSouth", "dpad/right"
    /// and so on. It's the button's place on the pad, so a PlayStation pad's cross counts as A, and a
    /// guitar controller's frets as A, B, Y, X and LB.
    /// </summary>
    private static string PadPath(int lane)
    {
        try
        {
            var action = NocturneInput.Combat.GetColumnInputAction(lane, false);
            if (action == null) return DefaultPaths[lane];
            int index = Rebinding.GetBindingIndex(action, "Gamepad", null);
            if (index < 0) return DefaultPaths[lane];
            Rebinding.GetBindingDisplayString(action, index, out _, out string controlPath, default);
            return controlPath ?? string.Empty;
        }
        catch (Exception ex)
        {
            ReportOnce(ex);
            return DefaultPaths[lane];
        }
    }

    // The game's control scheme, which follows the device used last: "Gamepad" or the keyboard's.
    private static bool UsingPad()
    {
        try { return NocturneInput.IsUsingGamepad; }
        catch (Exception ex)
        {
            ReportOnce(ex);
            return false;
        }
    }

    // Written with the pad's own names for its controls, or the aliases a binding can use.
    private static int FretOf(string path) => path.ToLowerInvariant() switch
    {
        "buttonsouth" or "a" or "cross" => GreenFret,
        "buttoneast" or "b" or "circle" => RedFret,
        "buttonnorth" or "y" or "triangle" => YellowFret,
        "buttonwest" or "x" or "square" => BlueFret,
        _ => -1
    };

    // Exact, not Color's approximate ==: a mark comes back from the game float for float.
    private static bool Same(in CombatNoteColorSet a, in CombatNoteColorSet b) =>
        Same(a.color1, b.color1) && Same(a.color2, b.color2) && Same(a.color3, b.color3);

    private static bool Same(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

    private static CombatNoteColorSet Set(int body, int accents, int lines) =>
        new() { color1 = Rgb(body), color2 = Rgb(accents), color3 = Rgb(lines) };

    private static Color Rgb(int rgb) =>
        new((rgb >> 16 & 0xFF) / 255f, (rgb >> 8 & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

    private static void ReportOnce(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        ModLog.Error("Akuma note colors failed: " + ex);
    }
}
