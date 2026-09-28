using UnityEngine;
using Object = UnityEngine.Object;

namespace NocturnePlus;

/// <summary>
/// The hub page's thumbnails (DESIGN-HUB 1.2): decoded on the main thread by the game's own
/// LoadImage (<see cref="CustomBattles.CardImages.Decode"/>), only for the rows in view, at most a
/// few a frame, and destroyed once they've been out of view for a moment (the battle creator's
/// FacesOf pattern). A thumbnail is checked by its JPEG markers first
/// (<see cref="HubZipCheck.ThumbProblem"/>): a baseline JPEG of at most 256 px, so a picture made
/// to stall the decoder never reaches it. One that can't be shown stays null, and the row shows a
/// title tile instead.
/// </summary>
internal sealed class HubThumbs
{
    private const int DecodesPerFrame = 4, KeepFrames = 90;

    private sealed class Entry
    {
        internal Texture2D? Texture;
        internal bool Tried;
        internal int Seen;
        internal Func<byte[]?> Bytes = null!;
    }

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private static bool reported;

    /// <summary>
    /// The picture for <paramref name="key"/>, asked for this frame; null until it's decoded (or when
    /// it can't be). <paramref name="bytes"/> gives the checked JPEG, or null for none; it's asked
    /// once, when the picture is decoded.
    /// </summary>
    internal Texture? Want(string key, Func<byte[]?> bytes)
    {
        if (!entries.TryGetValue(key, out var entry)) entries[key] = entry = new Entry { Bytes = bytes };
        entry.Seen = Time.frameCount;
        return entry.Texture != null && entry.Texture ? entry.Texture : null;
    }

    /// <summary>Called once a frame after drawing: decodes a few of the pictures in view, and lets go of those out of view.</summary>
    internal void Update()
    {
        int decoded = 0, now = Time.frameCount;
        List<string>? gone = null;
        foreach (var (key, entry) in entries)
        {
            if (now - entry.Seen > KeepFrames)
            {
                (gone ??= new List<string>()).Add(key);
                continue;
            }
            if (entry.Tried || entry.Seen != now || decoded >= DecodesPerFrame) continue;
            entry.Tried = true;
            decoded++;
            entry.Texture = Decode(entry.Bytes);
        }
        if (gone == null) return;
        foreach (var key in gone)
        {
            Destroy(entries[key]);
            entries.Remove(key);
        }
    }

    private static Texture2D? Decode(Func<byte[]?> source)
    {
        try
        {
            var bytes = source();
            if (bytes == null) return null;
            var texture = CustomBattles.CardImages.Decode(bytes, "hub thumbnail", 0, out _, out string? why);
            if (texture != null) texture.wrapMode = TextureWrapMode.Clamp;
            else if (!reported)
            {
                // Once a session: a picture that passed the marker check but not the decoder says why.
                reported = true;
                ModLog.Info($"Hub: a thumbnail {why ?? "couldn't be decoded"}, so its row shows a title tile.");
            }
            return texture;
        }
        catch (Exception ex)
        {
            // A picture is only a help: the row shows its title tile instead.
            if (!reported) ModLog.Error("Hub: a thumbnail couldn't be shown: " + ex.Message);
            reported = true;
            return null;
        }
    }

    private static void Destroy(Entry entry)
    {
        if (entry.Texture != null && entry.Texture) Object.Destroy(entry.Texture);
        entry.Texture = null;
    }

    /// <summary>Lets go of every picture (the page closes).</summary>
    internal void Clear()
    {
        foreach (var entry in entries.Values) Destroy(entry);
        entries.Clear();
    }

    /// <summary>A listing thumbnail's bytes when it passes the check (base64 from the hub), else null.</summary>
    internal static byte[]? Checked(string? b64) => HubZipCheck.ThumbProblem(b64, out var bytes) == null ? bytes : null;

    /// <summary>A saved thumbnail's bytes (Hub\thumbs\&lt;id&gt;.jpg) when it's there and passes the same check, else null.</summary>
    internal static byte[]? CheckedFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > HubZipCheck.MaxThumbB64) return null;
            return Checked(Convert.ToBase64String(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---- title tiles ------------------------------------------------------------------------------

    private static readonly Color[] TileColors =
    {
        EditorUi.Hex(0x5B3A7A), EditorUi.Hex(0x7A3A4E), EditorUi.Hex(0x3A5A7A), EditorUi.Hex(0x3A6A5A), EditorUi.Hex(0x7A5A2E), EditorUi.Hex(0x4A4A6A),
    };

    /// <summary>The tile a row shows without a picture: the title's first letters, on a colour picked by the entry's id.</summary>
    internal static (string Letters, Color Color) Tile(string id, string title)
    {
        var letters = new System.Text.StringBuilder();
        foreach (var word in title.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = 0;
            while (at < word.Length && !char.IsLetterOrDigit(word, at)) at++;
            if (at >= word.Length) continue;
            letters.Append(char.IsSurrogatePair(word, at) ? word.Substring(at, 2) : word[at].ToString());
            if (letters.Length >= 2) break;
        }
        uint hash = 2166136261;
        foreach (char c in id) hash = (hash ^ c) * 16777619;
        return (letters.Length > 0 ? letters.ToString().ToUpperInvariant() : "?", TileColors[hash % (uint)TileColors.Length]);
    }
}
