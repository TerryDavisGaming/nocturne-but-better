using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace NocturnePlus;

/// <summary>
/// The player's own note color palettes, made on the Custom note colors page (CustomNoteColors.Page.cs)
/// and listed in the game's Note Colors row after the game's own palettes and Akuma. A palette gives
/// the four lanes and the five-lane middle a color each: a body, accents (also the hold) and line
/// work, the last two worked out from the body unless the player picks them. They're kept in
/// NocturneButBetter\NoteColors.json.
/// </summary>
/// <remarks>
/// Like Akuma, a palette is a <c>NoteStyle</c> added to the game's list, whose three sets (edge lanes,
/// inner lanes, five-lane middle) are marks: the prefix on the note's SetColors
/// (<see cref="AkumaNoteColors.SetColorsPrefix"/>) swaps a mark for its lane's color. There can be many
/// palettes, so a mark counts only for the palette that's picked. Missed, mine and critical notes
/// never carry a mark (a mark that would match one is nudged). Without the mod, the game reads a
/// saved palette name as its first palette (Karma).
/// </remarks>
internal static partial class CustomNoteColors
{
    internal const string FileName = "NoteColors.json";
    private const int FileFormat = 1;
    private const long MaxFileBytes = 256 * 1024;
    internal const int MaxPalettes = 24;
    internal const int MaxName = 24;

    /// <summary>Lanes 1 to 4 (a four-lane chart's lanes, and a five-lane chart's side lanes), then the five-lane middle.</summary>
    internal const int LaneCount = 5;
    internal const int MiddleLane = 4;
    internal static readonly string[] LaneNames = { "Lane 1", "Lane 2", "Lane 3", "Lane 4", "Middle (5 lanes)" };

    internal static readonly char[] NameRefused = { '<', '>' };

    // The game's palettes, Akuma, and names the game's own text already has a name for.
    private static readonly string[] Reserved = { "Karma", "Forest", "Kimothy", "Blackout", "Chaos", "Reverse", "Yako", "Hoshii", AkumaNoteColors.Id };

    /// <summary>One lane's colors. Accents and line work left null are worked out from the body.</summary>
    internal sealed class Lane
    {
        internal Color Body;
        internal Color? Accents, Lines;

        internal Lane Copy() => new() { Body = Body, Accents = Accents, Lines = Lines };

        internal Color AccentsShown => Accents ?? NoteColorMath.Accents(Body);
        internal Color LinesShown => Lines ?? NoteColorMath.Lines(Body);

        /// <summary>As the game tints a note: body, accents and the hold, line work.</summary>
        internal CombatNoteColorSet Set => new() { color1 = Body, color2 = AccentsShown, color3 = LinesShown };
    }

    internal sealed class Palette
    {
        internal string Name = "";
        internal readonly Lane[] Lanes = new Lane[LaneCount];
        internal NoteStyle? Style;
        // By lane, as a battle shows them (see Refresh).
        internal readonly CombatNoteColorSet[] Sets = new CombatNoteColorSet[LaneCount];
        // What the game hands out from the palette, by the lane's place: the edges, the inner lanes, the middle.
        internal CombatNoteColorSet Outer, Inner, Middle;

        internal Palette Copy(string name)
        {
            var copy = new Palette { Name = name };
            for (int i = 0; i < LaneCount; i++) copy.Lanes[i] = Lanes[i].Copy();
            copy.Refresh();
            return copy;
        }

        /// <summary>After any lane changes: the sets, the marks, and the game's copy of them.</summary>
        internal void Refresh()
        {
            for (int i = 0; i < LaneCount; i++) Sets[i] = Lanes[i].Set;
            Outer = UniqueMark(Sets[0]);
            Inner = UniqueMark(Sets[1]);
            Middle = UniqueMark(Sets[MiddleLane]);
            if (Style != null && Style)
            {
                Style.outerNoteColor = Outer;
                Style.innerNoteColor = Inner;
                Style.middleNoteColor = Middle;
            }
            Version++;
        }

        /// <summary>The mark the game gives a note in <paramref name="column"/> (NoteStyle.GetColorsForColumn).</summary>
        internal CombatNoteColorSet MarkFor(int column, int count) =>
            column == 0 || column == count - 1 ? Outer : count == 5 && column == 2 ? Middle : Inner;

        /// <summary>The colors a note in <paramref name="column"/> of a <paramref name="count"/>-lane chart shows.</summary>
        internal CombatNoteColorSet SetFor(int column, int count) => Sets[LaneFor(column, count)];
    }

    /// <summary>
    /// Which of a palette's lanes a column shows. A five-lane chart's side lanes are the four-lane
    /// lanes and its middle is the middle; other lane counts (the game has none) go round the four
    /// lanes, with an odd count's middle in the middle color.
    /// </summary>
    internal static int LaneFor(int column, int count)
    {
        if (count == 4) return Math.Clamp(column, 0, 3);
        if (count == 5) return column == 2 ? MiddleLane : column < 2 ? column : column - 1;
        int middle = count % 2 == 1 ? count / 2 : -1;
        if (column == middle) return MiddleLane;
        return (middle >= 0 && column > middle ? column - 1 : column) % 4;
    }

    private static readonly List<Palette> palettes = new();
    // Styles of palettes that were deleted, still to take out of the game's list.
    private static readonly List<NoteStyle> retired = new();
    // Names changed, so the menus' Note Colors rows need their values again.
    private static bool menusStale;
    private static bool loaded;
    private static bool reportedError;

    /// <summary>Goes up whenever a palette's colors change, so the previews know to redraw.</summary>
    internal static int Version { get; private set; }

    /// <summary>The palettes, in the order they were made.</summary>
    internal static IReadOnlyList<Palette> Palettes
    {
        get
        {
            Load();
            return palettes;
        }
    }

    /// <summary>The file was made by a newer version of the mod: its palettes work, but it isn't changed.</summary>
    internal static bool ReadOnly { get; private set; }

    /// <summary>Why the file couldn't be read, or null. Saving then keeps the old one as .bad and starts again.</summary>
    internal static string? ReadProblem { get; private set; }

    internal static string FilePath => Path.Combine(Application.persistentDataPath, "NocturneButBetter", FileName);

    // ---- the game's list and its Note Colors row ------------------------------------------------------

    /// <summary>Called once a second: keeps the palettes in the game's list.</summary>
    internal static void Update()
    {
        try
        {
            Load();
            EnsureRegistered();
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    /// <summary>Called with Akuma's, for the options menu's Awake, Activate, DidShow and RefreshViews.</summary>
    internal static void SyncMenu(GameplayOptionsMenu menu)
    {
        try
        {
            Load();
            var styles = EnsureRegistered();
            if (styles != null) SyncRow(menu, styles);
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    /// <summary>
    /// Takes deleted palettes out of the game's list and puts the others in it (after the game's own,
    /// so the game's indexes don't move), then refills the Note Colors rows when anything changed.
    /// Returns the game's list, or null before the game has loaded it.
    /// </summary>
    private static Il2CppSystem.Collections.Generic.List<NoteStyle>? EnsureRegistered()
    {
        Il2CppSystem.Collections.Generic.List<NoteStyle>? styles;
        // For the first moments after start-up the game has no data yet, and asking for it throws.
        try { styles = DataUtility.GameConfig?.combatConfig?.noteStyles; }
        catch { return null; }
        if (styles == null || styles.Count == 0) return null;
        bool changed = false;
        foreach (var old in retired)
        {
            for (int i = styles.Count - 1; i >= 0; i--)
            {
                var entry = styles[i];
                if (entry != null && entry.Pointer == old.Pointer)
                {
                    styles.RemoveAt(i);
                    changed = true;
                }
            }
        }
        retired.Clear();
        foreach (var palette in palettes)
        {
            if (palette.Style == null || !palette.Style)
            {
                // Now that the game's colors are there to keep the marks apart from.
                palette.Refresh();
                palette.Style = Create(palette);
            }
            bool listed = false;
            for (int i = styles.Count - 1; i >= 0 && !listed; i--)
            {
                var entry = styles[i];
                listed = entry != null && entry.Pointer == palette.Style.Pointer;
            }
            if (listed) continue;
            styles.Add(palette.Style);
            changed = true;
        }
        if (changed || menusStale)
        {
            menusStale = false;
            foreach (var menu in Resources.FindObjectsOfTypeAll<GameplayOptionsMenu>())
                if (menu && menu.gameObject.scene.IsValid()) SyncRow(menu, styles);
        }
        return styles;
    }

    private static NoteStyle Create(Palette palette)
    {
        var created = ScriptableObject.CreateInstance(Il2CppType.Of<NoteStyle>())?.TryCast<NoteStyle>()
            ?? throw new InvalidOperationException("the game couldn't make a note style");
        created.name = "NoteStyle_NocturnePlus_" + palette.Name;
        // Kept for the whole session, through scene loads and the clean-up of unused assets.
        created.hideFlags = HideFlags.HideAndDontSave;
        created.id = palette.Name;
        created.outerNoteColor = palette.Outer;
        created.innerNoteColor = palette.Inner;
        created.middleNoteColor = palette.Middle;
        return created;
    }

    /// <summary>
    /// The game fills its Note Colors row only in Awake, with each palette's id; it's filled again when
    /// the ids differ (one added, taken out or renamed), and the value shown is set again.
    /// </summary>
    private static void SyncRow(GameplayOptionsMenu menu, Il2CppSystem.Collections.Generic.List<NoteStyle> styles)
    {
        var toggle = menu.noteStyleToggle;
        if (!toggle) return;
        var states = toggle.states;
        if (states == null) return;
        var ids = new List<string>();
        for (int i = 0; i < styles.Count; i++)
        {
            var entry = styles[i];
            if (entry != null) ids.Add(entry.id ?? "");
        }
        bool same = states.Count == ids.Count;
        for (int i = 0; same && i < ids.Count; i++) same = states[i] == ids[i];
        if (same) return;
        menu.PopulateNoteStyleStates();
        // That also switches the row on, so only for a row that's showing: the custom charts page
        // hides it, and the page's next refresh puts the value right.
        var row = menu.noteStyleButton;
        if (row && row.gameObject.activeSelf) menu.RefreshNoteStyleToggle();
    }

    // ---- the notes ------------------------------------------------------------------------------------

    // The palette that's picked, looked up at most once a frame (every note asks).
    private static int activeFrame = -1;
    private static Palette? active;

    private static Palette? Active()
    {
        if (palettes.Count == 0) return null;
        if (Time.frameCount == activeFrame) return active;
        activeFrame = Time.frameCount;
        active = null;
        string id;
        try { id = NoteStyleManager.CurrentStyleId; }
        catch { return null; }
        foreach (var palette in palettes)
            if (palette.Style != null && palette.Name == id) { active = palette; break; }
        return active;
    }

    /// <summary>
    /// The colors for a note in <paramref name="column"/> of a <paramref name="count"/>-lane chart: its
    /// lane's colors when <paramref name="colors"/> is the picked palette's mark for that lane, and
    /// <paramref name="colors"/> unchanged otherwise.
    /// </summary>
    internal static CombatNoteColorSet Apply(CombatNoteColorSet colors, int column, int count)
    {
        if (palettes.Count == 0 || column < 0 || column >= count) return colors;
        var palette = Active();
        if (palette == null || !Same(colors, palette.MarkFor(column, count))) return colors;
        return palette.SetFor(column, count);
    }

    // A mark that happens to equal the missed, mine or critical colors would recolor those notes too, and
    // one equal to Akuma's (a palette started from Akuma) would be taken by Akuma's swap first; such a mark
    // gets its line work a step bluer (only the palette's stand-in: a note shows the real set).
    private static CombatNoteColorSet UniqueMark(CombatNoteColorSet set)
    {
        for (int tries = 0; tries < 4 && Taken(set); tries++)
        {
            var c = set.color3;
            set.color3 = new Color(c.r, c.g, c.b >= 0.5f ? c.b - 1f / 255f : c.b + 1f / 255f, c.a);
        }
        return set;
    }

    private static bool Taken(CombatNoteColorSet set)
    {
        if (AkumaNoteColors.IsMark(set)) return true;
        try
        {
            // Throws in the first moments after start-up; the mark is worked out again when the palette is added to the game's list.
            var config = DataUtility.GameConfig?.combatConfig;
            return config != null && (Same(set, config.missedNoteColor) || Same(set, config.mineNoteColor) || Same(set, config.criticalNoteColor));
        }
        catch { return false; }
    }

    // Exact, not Color's approximate ==: a mark comes back from the game float for float.
    internal static bool Same(in CombatNoteColorSet a, in CombatNoteColorSet b) =>
        Same(a.color1, b.color1) && Same(a.color2, b.color2) && Same(a.color3, b.color3);

    private static bool Same(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

    // ---- changes, from the page -----------------------------------------------------------------------

    /// <summary>Whether <paramref name="name"/> can name a palette; <paramref name="self"/> is the one being renamed.</summary>
    internal static string? NameProblem(string name, Palette? self)
    {
        if (name.Length == 0) return "A palette needs a name.";
        if (name.Length > MaxName) return $"A name can be {MaxName} letters at most.";
        if (name.Any(char.IsControl)) return "A name can't have control characters.";
        // The game's Note Colors row shows the name as rich text.
        if (name.IndexOfAny(NameRefused) >= 0) return "A name can't have < or >.";
        if (Reserved.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase))) return $"\"{name}\" is one of the game's palettes.";
        if (palettes.Any(p => p != self && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) return $"You already have a palette called \"{name}\".";
        // Another mod or a newer game could add palettes of its own.
        try
        {
            var styles = DataUtility.GameConfig?.combatConfig?.noteStyles;
            for (int i = 0; styles != null && i < styles.Count; i++)
            {
                var entry = styles[i];
                if (entry == null || (self?.Style != null && entry.Pointer == self.Style.Pointer) || palettes.Any(p => p.Style != null && p.Style.Pointer == entry.Pointer)) continue;
                if (string.Equals(entry.id, name, StringComparison.OrdinalIgnoreCase)) return $"\"{name}\" is one of the game's palettes.";
            }
        }
        catch { }
        return null;
    }

    /// <summary>The first free name of "<paramref name="stem"/>", "<paramref name="stem"/> 2" and so on.</summary>
    internal static string FreeName(string stem)
    {
        stem = stem.Trim();
        if (stem.Length > MaxName - 3) stem = stem.Substring(0, MaxName - 3).TrimEnd();
        if (NameProblem(stem, null) == null) return stem;
        for (int n = 2; n < 1000; n++)
            if (NameProblem($"{stem} {n}", null) == null) return $"{stem} {n}";
        return stem + " " + Guid.NewGuid().ToString("N").Substring(0, 4);
    }

    internal static Palette Add(Palette palette)
    {
        palette.Refresh();
        palettes.Add(palette);
        Version++;
        EnsureRegistered();
        return palette;
    }

    internal static void Remove(Palette palette)
    {
        if (!palettes.Remove(palette)) return;
        if (palette.Style != null && palette.Style) retired.Add(palette.Style);
        palette.Style = null;
        Version++;
        EnsureRegistered();
    }

    /// <summary>Renames a palette, and keeps it picked when it was.</summary>
    internal static void Rename(Palette palette, string name)
    {
        bool picked = IsPicked(palette);
        palette.Name = name;
        if (palette.Style != null && palette.Style)
        {
            palette.Style.id = name;
            palette.Style.name = "NoteStyle_NocturnePlus_" + name;
        }
        menusStale = true;
        activeFrame = -1;
        // Saved first, so the rows refilled next show the new name as the one in use.
        if (picked) NoteStyleManager.SetStyle(name);
        EnsureRegistered();
        RefreshToggles();
        Version++;
    }

    /// <summary>
    /// The game's Note Colors rows show the palette in use again (the game sets their value only when the
    /// row itself changes it, or when the page refreshes), for rows that are showing.
    /// </summary>
    internal static void RefreshToggles()
    {
        try
        {
            foreach (var menu in Resources.FindObjectsOfTypeAll<GameplayOptionsMenu>())
            {
                if (!menu || !menu.gameObject.scene.IsValid()) continue;
                var row = menu.noteStyleButton;
                if (row && row.gameObject.activeSelf) menu.RefreshNoteStyleToggle();
            }
        }
        catch (Exception ex) { ReportOnce(ex); }
    }

    internal static bool IsPicked(Palette palette)
    {
        try { return NoteStyleManager.CurrentStyleId == palette.Name && palette.Style != null; }
        catch { return false; }
    }

    /// <summary>Picks the palette for the notes, as its value in the Note Colors row would.</summary>
    internal static void Pick(Palette palette)
    {
        EnsureRegistered();
        activeFrame = -1;
        NoteStyleManager.SetStyle(palette.Name);
        RefreshToggles();
    }

    /// <summary>Puts the notes back on the game's first palette (Karma), as the gameplay page's reset does.</summary>
    internal static void PickDefault()
    {
        activeFrame = -1;
        NoteStyleManager.SetStyle(string.Empty);
        RefreshToggles();
    }

    internal static void Changed(Palette palette)
    {
        palette.Refresh();
        Version++;
    }

    // ---- the file -------------------------------------------------------------------------------------

    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static void Load()
    {
        if (loaded) return;
        loaded = true;
        string path = FilePath;
        try
        {
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes) throw new InvalidDataException($"it's {info.Length / 1024} KB, more than a palettes file can be");
            if (JsonNode.Parse(File.ReadAllText(path), null, ReadOptions) is not JsonObject root) throw new InvalidDataException("it isn't a JSON object");
            int format = root["format"] is JsonValue f && f.TryGetValue(out int n) ? n : 0;
            if (format > FileFormat)
            {
                ReadOnly = true;
                ModLog.Error($"Custom note colors: {FileName} was made by a newer version of the mod (format {format}), so its palettes are used but it isn't changed.");
            }
            int skipped = 0;
            if (root["palettes"] is JsonArray list)
            {
                foreach (var node in list)
                {
                    var palette = node is JsonObject o ? ReadPalette(o) : null;
                    if (palette == null || palettes.Count >= MaxPalettes || NameProblem(palette.Name, null) != null) { skipped++; continue; }
                    palette.Refresh();
                    palettes.Add(palette);
                }
            }
            SkippedOnLoad = skipped;
            ModLog.Info($"Custom note colors: {palettes.Count} palettes from {FileName}" + (skipped > 0 ? $", {skipped} left out (unreadable, too many, or a name taken); the file is kept as a .bak copy before it's next saved." : "."));
        }
        // Anything at all (a hand-edited file with a key twice throws ArgumentException when it's read): the
        // palettes start empty and the file is left alone, then kept as .bad when the first change is saved.
        catch (Exception ex)
        {
            palettes.Clear();
            ReadProblem = ex.Message;
            ModLog.Error($"Custom note colors: {FileName} couldn't be read ({ex.Message}), so there are no palettes of your own. It's left as it is until you change one.");
        }
    }

    /// <summary>How many palettes in the file couldn't be used when it was read (they're kept in a .bak copy).</summary>
    internal static int SkippedOnLoad { get; private set; }

    private static bool keptSkipped;

    // A backup name that doesn't overwrite an earlier one: .bak, .bak2, .bak3 and so on.
    private static string FreeBackup(string path, string suffix)
    {
        string candidate = path + suffix;
        for (int n = 2; File.Exists(candidate) && n < 100; n++) candidate = path + suffix + n;
        return candidate;
    }

    private static Palette? ReadPalette(JsonObject o)
    {
        string name = o["name"] is JsonValue v && v.TryGetValue(out string? s) && s != null ? s.Trim() : "";
        if (name.Length == 0 || o["lanes"] is not JsonArray lanes || lanes.Count != LaneCount) return null;
        var palette = new Palette { Name = name };
        for (int i = 0; i < LaneCount; i++)
        {
            if (lanes[i] is not JsonObject lane || !TryColor(lane["body"], out var body)) return null;
            palette.Lanes[i] = new Lane
            {
                Body = body,
                Accents = TryColor(lane["accents"], out var accents) ? accents : null,
                Lines = TryColor(lane["lines"], out var lines) ? lines : null,
            };
        }
        return palette;
    }

    private static bool TryColor(JsonNode? node, out Color color)
    {
        color = default;
        return node is JsonValue v && v.TryGetValue(out string? s) && s != null && NoteColorMath.TryParseHex(s, out color);
    }

    /// <summary>Writes the file; false with the reason when it can't be (a newer version's file, or an error).</summary>
    internal static bool Save(out string? problem)
    {
        problem = null;
        if (ReadOnly)
        {
            problem = $"{FileName} was made by a newer version of the mod, so it isn't changed.";
            return false;
        }
        string path = FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (ReadProblem != null && File.Exists(path))
            {
                string bad = FreeBackup(path, ".bad");
                File.Move(path, bad, true);
                ModLog.Info($"Custom note colors: moved the unreadable {FileName} to {Path.GetFileName(bad)}.");
            }
            else if (SkippedOnLoad > 0 && !keptSkipped && File.Exists(path))
            {
                // The palettes that couldn't be used would be gone from the file after this save.
                string bak = FreeBackup(path, ".bak");
                File.Copy(path, bak, false);
                ModLog.Info($"Custom note colors: kept the file as it was, with the {SkippedOnLoad} palettes that couldn't be used, as {Path.GetFileName(bak)}.");
            }
            keptSkipped = true;
            ReadProblem = null;
            var list = new JsonArray();
            foreach (var palette in palettes)
            {
                var lanes = new JsonArray();
                foreach (var lane in palette.Lanes)
                {
                    var entry = new JsonObject { ["body"] = "#" + EditorUi.HexOf(lane.Body) };
                    if (lane.Accents is { } a) entry["accents"] = "#" + EditorUi.HexOf(a);
                    if (lane.Lines is { } l) entry["lines"] = "#" + EditorUi.HexOf(l);
                    lanes.Add(entry);
                }
                list.Add(new JsonObject { ["name"] = palette.Name, ["lanes"] = lanes });
            }
            var root = new JsonObject { ["format"] = FileFormat, ["palettes"] = list };
            BattleDraft.WriteAtomic(path, root.ToJsonString(WriteOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"{FileName} couldn't be written: {ex.Message}";
            ModLog.Error("Custom note colors: " + problem);
            return false;
        }
    }

    // ---- starting points for a new palette ---------------------------------------------------------------

    /// <summary>A palette's five lanes from a game palette: its edge set on lanes 1 and 4, inner on 2 and 3, middle on the middle.</summary>
    internal static Lane[] LanesFrom(NoteStyle style)
    {
        var lanes = new Lane[LaneCount];
        for (int i = 0; i < LaneCount; i++)
        {
            var set = i == MiddleLane ? style.middleNoteColor : i == 0 || i == 3 ? style.outerNoteColor : style.innerNoteColor;
            lanes[i] = new Lane { Body = Opaque(set.color1), Accents = Opaque(set.color2), Lines = Opaque(set.color3) };
        }
        return lanes;
    }

    /// <summary>
    /// A palette's five lanes as a battle would show them now for the palette with <paramref name="id"/>
    /// (Akuma's follow the pad), or null when there's no such palette.
    /// </summary>
    internal static Lane[]? LanesShown(string id)
    {
        var own = palettes.FirstOrDefault(p => p.Name == id);
        if (own != null) return own.Lanes.Select(l => l.Copy()).ToArray();
        if (id == AkumaNoteColors.Id)
        {
            var lanes = new Lane[LaneCount];
            for (int i = 0; i < LaneCount; i++)
            {
                var set = AkumaNoteColors.LaneSet(i == MiddleLane ? 2 : i, i == MiddleLane ? 5 : 4);
                lanes[i] = new Lane { Body = Opaque(set.color1), Accents = Opaque(set.color2), Lines = Opaque(set.color3) };
            }
            return lanes;
        }
        foreach (var (styleId, style) in GameStyles())
            if (styleId == id) return LanesFrom(style);
        return null;
    }

    /// <summary>The game's list of palettes, as (id, style); empty before the game has loaded it.</summary>
    internal static List<(string Id, NoteStyle Style)> GameStyles()
    {
        var result = new List<(string, NoteStyle)>();
        try
        {
            var styles = DataUtility.GameConfig?.combatConfig?.noteStyles;
            for (int i = 0; styles != null && i < styles.Count; i++)
            {
                var entry = styles[i];
                if (entry != null && entry) result.Add((entry.id ?? "", entry));
            }
        }
        catch { }
        return result;
    }

    private static Color Opaque(Color c) => new(c.r, c.g, c.b, 1f);

    private static void ReportOnce(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        ModLog.Error("Custom note colors failed: " + ex);
    }
}
