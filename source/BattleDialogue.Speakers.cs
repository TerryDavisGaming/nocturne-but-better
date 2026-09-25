using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>
/// The speakers of custom battle dialogue. Game characters are found in the game's character list
/// in any letter case, and their faces are loaded before the song (a face that isn't would stall a
/// frame mid-song). The battle's own speakers become characters of the mod's for the battle: each
/// is added to the game's character list with no portrait collection, and the game's one lookup of
/// every face (PortraitManager.GetExpressions) is given the speaker's own pictures. The box sizes a
/// portrait from its sprite (Image.SetNativeSize, one game pixel a canvas unit), so the sprites'
/// pixels per unit set how big each picture shows, and every face of a speaker shows at its default
/// face's size. The game's box keeps the last picture when a face is missing, so every line's face
/// is one its speaker has, and a speaker without pictures shows a clear one.
/// </summary>
internal static partial class BattleDialogue
{
    /// <summary>The battle's own speakers' ids start with this; the cleanup takes every such character out of the game's list.</summary>
    private const string SpeakerPrefix = CustomBattles.RuntimePrefix + "speaker/";

    // Every face the game's box shows comes through here. A speaker of the battle's own (and a game
    // character with no pictures at all) gets its own list; everyone else the game's.
    private static bool ExpressionsPrefix(CharacterData data, ref Il2CppSystem.Collections.Generic.List<Portrait> __result)
    {
        var d = director;
        if (d == null || data == null) return true;
        try
        {
            if (!d.Faces.TryGetValue(data.Pointer, out var faces)) return true;
            __result = faces;
            return false;
        }
        catch (Exception ex)
        {
            Fail(d, "giving a speaker's pictures", ex);
            return true;
        }
    }

    /// <summary>One of the battle's own speakers in the game: its character, and its faces as the game's Emotions.</summary>
    private sealed class OwnSpeaker
    {
        internal DialogueSpeaker Source = null!;
        internal string Id = "";
        internal CharacterData Data = null!;
        /// <summary>The Emotions value each face that could be shown has, by its name ("" for the default face).</summary>
        internal readonly Dictionary<string, Emotions> ByName = new(StringComparer.OrdinalIgnoreCase);

        internal Emotions Face(string? name) => name != null && ByName.TryGetValue(name, out var face) ? face : Emotions.Neutral1;
    }

    private sealed partial class Director
    {
        /// <summary>The faces handed to the game's box instead of its own, by character.</summary>
        internal readonly Dictionary<IntPtr, Il2CppSystem.Collections.Generic.List<Portrait>> Faces = new();
        // Game characters as written, and their ids in the game (null when there's no such character).
        private readonly Dictionary<string, string?> gameIds = new(StringComparer.OrdinalIgnoreCase);
        // A game character's faces, once they're loaded.
        private readonly Dictionary<string, List<Emotions>> gameFaces = new(StringComparer.Ordinal);
        private readonly Dictionary<string, OwnSpeaker> own = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PortraitScope> scopes = new();
        private readonly List<Object> made = new();
        private readonly HashSet<string> noted = new();
        private Sprite? clear;

        /// <summary>
        /// Finds the game characters the lines use and starts loading their faces, and makes the
        /// battle's own speakers. Returns the speakers for the log ("Karma, Narrator, The Warden (yours)").
        /// </summary>
        internal string PrepareSpeakers(List<DialogueLine> lines, List<string> notes)
        {
            var names = new List<string>();
            var database = DataUtility.NpcDatabase ?? throw new InvalidOperationException("the game's character list isn't loaded");
            var list = database.Data ?? throw new InvalidOperationException("the game's character list is empty");

            // Game characters: the first with the id as written, else the first in any letter case.
            var exact = new HashSet<string>(StringComparer.Ordinal);
            var loose = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < list.Count; i++)
            {
                string? id = list[i]?.characterId;
                if (string.IsNullOrEmpty(id)) continue;
                exact.Add(id!);
                loose.TryAdd(id!, id!);
            }
            foreach (var written in Data.GameSpeakers())
            {
                if (!lines.Any(l => l.SpeakerKind == DialogueSpeakerKind.Game && l.Speaker.Equals(written, StringComparison.OrdinalIgnoreCase))) continue;
                string key = written.Trim();
                string? id = exact.Contains(key) ? key : loose.TryGetValue(key, out var found) ? found : null;
                gameIds[written] = id;
                if (id == null)
                {
                    notes.Add($"no character \"{key}\"; its lines show as the Narrator.");
                    continue;
                }
                if (!names.Contains(id)) names.Add(id);
                scopes.Add(PortraitManager.AcquireScope(id));
            }
            if (lines.Any(l => l.SpeakerKind == DialogueSpeakerKind.Narrator || (l.SpeakerKind == DialogueSpeakerKind.Game && GameId(l.Speaker) == null)))
                names.Add(DialogueReader.Narrator);

            // The battle's own speakers, in the order written.
            foreach (var speaker in Data.Speakers)
            {
                if (!lines.Any(l => l.SpeakerKind == DialogueSpeakerKind.Custom && l.Speaker.Equals(speaker.Key, StringComparison.OrdinalIgnoreCase))) continue;
                var built = MakeSpeaker(speaker, notes);
                own[speaker.Key] = built;
                list.Add(built.Data);
                names.Add(speaker.Name + " (yours)");
            }
            return names.Count > 0 ? string.Join(", ", names) : "none";
        }

        private string? GameId(string written) => gameIds.TryGetValue(written, out var id) ? id : null;

        // A speaker of the battle's own: a character of the mod's with no portrait collection (so
        // the game has nothing to load for it), and its pictures.
        private OwnSpeaker MakeSpeaker(DialogueSpeaker source, List<string> notes)
        {
            string id = SpeakerPrefix + Battle.Package.Id + "/" + source.Key;
            var data = ScriptableObject.CreateInstance(Il2CppType.Of<CharacterData>())?.TryCast<CharacterData>()
                ?? throw new InvalidOperationException("the game couldn't make a character");
            made.Add(data);
            data.name = id;
            data.hideFlags = HideFlags.HideAndDontSave;
            data.characterId = id;
            data.dialogueStyle = DialogueStyles.Normal;
            data.portraitCollection = null;
            data.portraitOffset = new Vector2((float)source.OffsetX, (float)source.OffsetY);
            data.flipPortrait = source.Flip;
            var speaker = new OwnSpeaker { Source = source, Id = id, Data = data };
            Faces[data.Pointer] = MakeFaces(speaker, notes);
            return speaker;
        }

        // The default face and the others, as sprites sized so the box shows each at the default
        // face's shown size. A speaker whose default face can't be shown shows a clear picture.
        private Il2CppSystem.Collections.Generic.List<Portrait> MakeFaces(OwnSpeaker speaker, List<string> notes)
        {
            var faces = new Il2CppSystem.Collections.Generic.List<Portrait>();
            var source = speaker.Source;
            var first = source.Portrait;
            Sprite? sprite = null;
            (double Width, double Height, double Scale, bool Sharp) shown = default;
            if (first != null)
            {
                shown = DialogueReader.ShownSize(first.Width, first.Height);
                sprite = FaceSprite(speaker, first, shown.Width, notes);
            }
            if (sprite == null)
            {
                faces.Add(new Portrait { Emotion = Emotions.Neutral1, Face = Clear() });
                speaker.ByName[""] = Emotions.Neutral1;
                notes.Add($"{source.Name} shows without a picture.");
                return faces;
            }
            faces.Add(new Portrait { Emotion = Emotions.Neutral1, Face = sprite });
            speaker.ByName[""] = Emotions.Neutral1;
            // The other faces take the Emotions values after it, in the order written.
            for (int i = 0; i < source.Expressions.Count; i++)
            {
                var face = source.Expressions[i];
                var other = FaceSprite(speaker, face, shown.Width, notes);
                if (other == null) continue;
                var emotion = (Emotions)(i + 1);
                faces.Add(new Portrait { Emotion = emotion, Face = other });
                speaker.ByName[face.Name] = emotion;
            }
            string size = shown.Scale > 1 ? $" ({shown.Scale:0}x)" : shown.Scale < 1 ? $" (scaled down from {first!.Width} x {first.Height})" : "";
            notes.Add($"{source.Name}: {faces.Count} {(faces.Count == 1 ? "face" : "faces")} shown at {shown.Width:0} x {shown.Height:0} game pixels{size}.");
            return faces;
        }

        // One picture as a sprite the box shows <paramref name="shownWidth"/> game pixels wide (the
        // height follows its shape). A bigger picture is scaled down on the graphics card first; one
        // shown at a whole number of game pixels per pixel stays sharp. Null (noted) when it can't be read.
        private Sprite? FaceSprite(OwnSpeaker speaker, DialogueFace face, double shownWidth, List<string> notes)
        {
            string name = speaker.Id + "/" + (face.Name.Length == 0 ? "default" : face.Name);
            string? why;
            Texture2D? texture;
            try
            {
                byte[] bytes = Battle.Package.Files.ReadAllBytes(face.File, DialogueReader.MaxPortraitBytes);
                double k = shownWidth / Math.Max(1, face.Width);
                int keep = k < 1 ? (int)Math.Ceiling(Math.Max(face.Width, face.Height) * k) : 0;
                texture = CustomBattles.CardImages.Decode(bytes, name, keep, out _, out why);
                if (texture != null) texture.filterMode = k >= 1 && Math.Abs(k - Math.Round(k)) < 1e-6 ? FilterMode.Point : FilterMode.Bilinear;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                texture = null;
                why = $"couldn't be read ({ex.Message})";
            }
            if (texture == null)
            {
                notes.Add($"{speaker.Source.Name}: the picture {face.File} {why}, so {(face.Name.Length == 0 ? "it shows without a picture" : "its lines show the default picture")}.");
                return null;
            }
            made.Add(texture);
            float ppu = (float)(texture.width / shownWidth);
            var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), ppu, 0u, SpriteMeshType.FullRect, Vector4.zero);
            sprite.name = name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            made.Add(sprite);
            return sprite;
        }

        // A clear 1 x 1 picture, for a speaker the box shows without one.
        private Sprite Clear()
        {
            if (clear != null) return clear;
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                name = SpeakerPrefix + "clear",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
            };
            made.Add(texture);
            var pixel = new byte[4];
            var pin = GCHandle.Alloc(pixel, GCHandleType.Pinned);
            try { texture.LoadRawTextureData(pin.AddrOfPinnedObject(), pixel.Length); }
            finally { pin.Free(); }
            texture.Apply(false, true);
            clear = Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f, 0u, SpriteMeshType.FullRect, Vector4.zero);
            clear.name = texture.name;
            clear.hideFlags = HideFlags.HideAndDontSave;
            made.Add(clear);
            return clear;
        }

        /// <summary>Whether every game character in the lines has its faces loaded.</summary>
        private bool FacesReady(List<DialogueLine> lines)
        {
            foreach (var line in lines)
                if (line.SpeakerKind == DialogueSpeakerKind.Game && GameId(line.Speaker) is { } id && !PortraitManager.IsReady(id)) return false;
            return true;
        }

        /// <summary>A line as the game is given it: a game character that doesn't exist, or a speaker left out, is the Narrator.</summary>
        private Said Resolve(DialogueLine line)
        {
            var said = new Said { Line = line, Name = line.Name ?? "" };
            string? id = null;
            if (line.SpeakerKind == DialogueSpeakerKind.Custom && own.TryGetValue(line.Speaker, out var speaker))
            {
                id = speaker.Id;
                said.Face = speaker.Face(line.Face);
                said.Name = Data.NameOf(line) ?? speaker.Source.Name;
            }
            else if (line.SpeakerKind == DialogueSpeakerKind.Game && GameId(line.Speaker) is { } game)
            {
                id = game;
                said.Face = GameFace(game, line.Face);
                said.Name = line.Name ?? Localization.Get("Names/" + game, TidyName(game));
            }
            if (id == null) return said;
            said.Id = id;
            said.Narrator = false;
            said.Position = (DialogueSpeakerPosition)(int)Data.SideOf(line);
            return said;
        }

        // "NPC_Abbot" shows as "Abbot" when the game has no name for it.
        private static string TidyName(string id) => (id.StartsWith("NPC_", StringComparison.OrdinalIgnoreCase) ? id.Substring(4) : id).Replace('_', ' ');

        /// <summary>
        /// A game character's face: the one written when the character has it, else Neutral1 when it
        /// has that, else its first (noted once). Before its faces are loaded, the one written.
        /// </summary>
        private Emotions GameFace(string id, string? written)
        {
            Emotions? wanted = null;
            if (written != null && Enum.TryParse<Emotions>(written, true, out var parsed) && Enum.IsDefined(typeof(Emotions), parsed)) wanted = parsed;
            var faces = GameFaces(id);
            if (faces == null) return wanted ?? Emotions.Neutral1;
            if (faces.Count == 0) return Emotions.Neutral1;
            if (wanted is Emotions face && faces.Contains(face)) return face;
            var fallback = faces.Contains(Emotions.Neutral1) ? Emotions.Neutral1 : faces[0];
            if (written != null && noted.Add(id + "/" + written)) ModLog.Info($"Battle dialogue: {id} has no expression \"{written}\", so {fallback} shows.");
            return fallback;
        }

        // A game character's faces, read once they're loaded (null until then). One with none at
        // all is given the clear picture, so the box doesn't keep the last one it showed.
        private List<Emotions>? GameFaces(string id)
        {
            if (gameFaces.TryGetValue(id, out var known)) return known;
            if (!PortraitManager.IsReady(id)) return null;
            var faces = new List<Emotions>();
            var data = DataUtility.NpcDatabase?.Get(id);
            if (data != null)
            {
                var list = PortraitManager.GetExpressions(data);
                for (int i = 0; list != null && i < list.Count; i++)
                    if (list[i] != null) faces.Add(list[i].Emotion);
                if (faces.Count == 0)
                {
                    var none = new Il2CppSystem.Collections.Generic.List<Portrait>();
                    none.Add(new Portrait { Emotion = Emotions.Neutral1, Face = Clear() });
                    Faces[data.Pointer] = none;
                    ModLog.Info($"Battle dialogue: {id} has no pictures in the game, so it shows without one.");
                }
            }
            gameFaces[id] = faces;
            return faces;
        }

        /// <summary>
        /// Takes the battle's own speakers out of the game's character list (by the character, and
        /// anything with their id's start), lets go of the faces loaded for the game characters, and
        /// destroys the pictures. Returns how many were taken out.
        /// </summary>
        internal int RemoveSpeakers()
        {
            int removed = 0;
            try
            {
                var ours = new HashSet<IntPtr>(own.Values.Select(s => s.Data.Pointer));
                var list = DataUtility.NpcDatabase?.Data;
                for (int i = (list?.Count ?? 0) - 1; i >= 0; i--)
                {
                    var data = list![i];
                    if (data == null) continue;
                    if (!ours.Contains(data.Pointer) && data.characterId?.StartsWith(SpeakerPrefix, StringComparison.Ordinal) != true) continue;
                    list.RemoveAt(i);
                    removed++;
                }
            }
            finally
            {
                own.Clear();
                Faces.Clear();
                foreach (var scope in scopes)
                {
                    try { scope?.Dispose(); }
                    catch (Exception ex) { ModLog.Error($"Battle dialogue: letting go of a character's faces failed: {ex.Message}"); }
                }
                scopes.Clear();
                foreach (var obj in made)
                    if (obj) Object.Destroy(obj);
                made.Clear();
                clear = null;
            }
            return removed;
        }
    }
}
