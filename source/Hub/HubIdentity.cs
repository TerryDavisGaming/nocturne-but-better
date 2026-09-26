using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NocturneFlatScroll;

/// <summary>
/// The player's hub key (DESIGN-HUB 2.8, 1.7): 32 random bytes as "nbbk1_" and 43 base64url
/// characters, made the first time the player uploads or reports. It lives in Hub\identity.json,
/// encrypted to the Windows user with DPAPI, so a copied mod folder or a zipped log doesn't carry a
/// usable key. The backup is a plain text file on purpose, so it works on another PC. The hub only
/// ever stores the key's SHA-256; the mod sends the key in one request's Authorization header and
/// never logs it. File work runs on worker threads.
/// This file has no Unity or game dependencies.
/// </summary>
internal sealed class HubIdentity
{
    internal const string Prefix = "nbbk1_";
    internal const int Format = 2;
    private const string BackupHeader = "# Nocturne But Better hub key";

    // DPAPI entropy: the blob only opens for this purpose, even for the same Windows user.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NocturneButBetter hub key v1");

    private HubIdentity(string key, string name, string? uploaderId, DateTime createdAt)
    {
        Key = key;
        Name = name;
        UploaderId = uploaderId;
        CreatedAt = createdAt;
    }

    /// <summary>The key itself. Never logged, never shown, only sent in Authorization.</summary>
    internal string Key { get; }
    /// <summary>The display name the player picked (or will register with at the first upload).</summary>
    internal string Name { get; set; }
    /// <summary>The hub's id for this key once it's registered ("u..."), for "Yours" and My uploads.</summary>
    internal string? UploaderId { get; set; }
    internal DateTime CreatedAt { get; }

    internal string Hash => HashOf(Key);

    /// <summary>A new identity with a fresh key (not saved yet).</summary>
    internal static HubIdentity Create(string name) => new(NewKey(), name, null, DateTime.UtcNow);

    internal static string NewKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Prefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Whether a text is a hub key: "nbbk1_" and 43 base64url characters.</summary>
    internal static bool IsKey(string? text)
    {
        if (text == null || text.Length != Prefix.Length + 43 || !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        for (int i = Prefix.Length; i < text.Length; i++)
        {
            char c = text[i];
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
        }
        return true;
    }

    /// <summary>The key's SHA-256 as the hub stores it: lower-case hex of the UTF-8 key.</summary>
    internal static string HashOf(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    // ---- identity.json --------------------------------------------------------------------------

    /// <summary>
    /// Reads identity.json; null when there isn't one yet. Throws InvalidDataException when it's
    /// there but can't be opened (damaged, or encrypted for another Windows user or install).
    /// </summary>
    internal static HubIdentity? Load(string path)
    {
        if (!File.Exists(path)) return null;
        JsonObject root;
        int format;
        string blobText;
        string? name, uploader, created;
        try
        {
            root = JsonNode.Parse(BattleDraft.ReadText(path, 64 * 1024)) as JsonObject ?? throw new InvalidDataException("the hub key file is damaged");
            format = (int?)root["format"] ?? 0;
            blobText = (string?)root["protected"] ?? "";
            name = (string?)root["name"];
            uploader = (string?)root["uploader"];
            created = (string?)root["createdAt"];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("the hub key file is damaged");
        }
        if (format < 1 || format > Format) throw new InvalidDataException(format > Format ? "the hub key file was made by a newer version of the mod" : "the hub key file is damaged");
        string key;
        try { key = Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(blobText))); }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidDataException("the hub key can't be opened on this Windows account (it was made on another PC or Windows install). Use a saved key");
        }
        if (!IsKey(key)) throw new InvalidDataException("the hub key file doesn't hold a hub key");
        if (!HubText.IsUploaderId(uploader)) uploader = null;
        DateTime made = DateTime.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var c)
            ? c : DateTime.UtcNow;
        return new HubIdentity(key, HubText.CleanLine(name, HubText.Name) ?? "", uploader, made);
    }

    /// <summary>Writes identity.json (the key encrypted to this Windows user), atomically.</summary>
    internal void Save(string path)
    {
        var root = new JsonObject
        {
            ["format"] = Format,
            ["protected"] = Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(Key))),
            ["name"] = Name,
            ["createdAt"] = CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        };
        if (UploaderId != null) root["uploader"] = UploaderId;
        BattleDraft.WriteAtomic(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// The identity to use, made and saved on the spot when there's none yet (the first upload or
    /// report). <paramref name="defaultName"/> is its name until the player picks one.
    /// </summary>
    internal static HubIdentity LoadOrCreate(string path, string defaultName)
    {
        var identity = Load(path);
        if (identity != null) return identity;
        identity = Create(HubText.CleanLine(defaultName, HubText.Name) ?? "");
        identity.Save(path);
        return identity;
    }

    // ---- backup and restore ---------------------------------------------------------------------

    /// <summary>The backup file's text ("Nocturne hub key.txt"): a warning and the key.</summary>
    internal string BackupText()
    {
        var sb = new StringBuilder();
        sb.Append(BackupHeader).Append("\r\n");
        sb.Append("# Keep this private. It works like a password for your uploads. Don't keep it in a synced folder such as OneDrive.\r\n");
        sb.Append("# In the game: Get Custom Battles, My uploads, Use a saved key.\r\n");
        if (Name.Length > 0) sb.Append("# Name: ").Append(Name.Replace('\r', ' ').Replace('\n', ' ')).Append("\r\n");
        sb.Append(Key).Append("\r\n");
        return sb.ToString();
    }

    internal void WriteBackup(string file) => BattleDraft.WriteAtomic(file, BackupText());

    /// <summary>The key in a backup file (its first line that is one). Throws when there's none.</summary>
    internal static string ReadBackup(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists) throw new FileNotFoundException("the file is missing", file);
        if (info.Length > 64 * 1024) throw new InvalidDataException("that file is too big to be a hub key backup");
        foreach (var raw in BattleDraft.ReadText(file, 64 * 1024).Split('\n'))
        {
            string line = raw.Trim();
            if (IsKey(line)) return line;
        }
        throw new InvalidDataException("that file has no hub key in it");
    }

    /// <summary>
    /// Makes <paramref name="next"/> this PC's identity. The one it replaces is kept, still
    /// encrypted, as identity-replaced-&lt;date&gt;.json next to it.
    /// </summary>
    internal static void Replace(string path, HubIdentity next)
    {
        if (File.Exists(path))
        {
            string kept = CustomFreeName(Path.Combine(Path.GetDirectoryName(path)!, $"identity-replaced-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"));
            File.Copy(path, kept, false);
        }
        next.Save(path);
    }

    /// <summary>An identity for a key read from a backup (its uploader and name come from the hub's answer).</summary>
    internal static HubIdentity FromKey(string key, string name, string? uploaderId)
    {
        if (!IsKey(key)) throw new ArgumentException("not a hub key", nameof(key));
        return new HubIdentity(key, HubText.CleanLine(name, HubText.Name) ?? "", HubText.IsUploaderId(uploaderId) ? uploaderId : null, DateTime.UtcNow);
    }

    // ---- rotating ---------------------------------------------------------------------------------

    private static string RotatingPath(string path) => Path.Combine(Path.GetDirectoryName(path)!, "identity-rotating.json");

    /// <summary>
    /// Makes a new key for this identity and tells the hub (only the new key's SHA-256 travels).
    /// The new key is saved before the hub is asked, as identity-rotating.json, so it isn't lost if
    /// the answer is; after the answer it becomes identity.json. <see cref="RecoverAsync"/> sorts
    /// out a rotation that stopped between the two.
    /// </summary>
    internal static async Task<HubIdentity> RotateAsync(HubApi api, string path, HubIdentity current, CancellationToken ct)
    {
        var next = new HubIdentity(NewKey(), current.Name, current.UploaderId, DateTime.UtcNow);
        string pending = RotatingPath(path);
        next.Save(pending);
        await api.RotateAsync(next.Hash, current.Key, ct).ConfigureAwait(false);
        next.Save(path);
        TryDelete(pending);
        return next;
    }

    /// <summary>
    /// After a rotation that stopped before its answer was saved: asks the hub which of the two keys
    /// works (GET /v1/me with each) and keeps that one. Returns the identity in use, or null when
    /// there was nothing to sort out. When the hub can't be reached, nothing changes (try again).
    /// </summary>
    internal static async Task<HubIdentity?> RecoverAsync(HubApi api, string path, CancellationToken ct)
    {
        string pending = RotatingPath(path);
        if (!File.Exists(pending)) return null;
        var rotated = Load(pending);
        var current = Load(path);
        if (rotated == null) return current;
        if (await Works(api, rotated, ct).ConfigureAwait(false))
        {
            rotated.Save(path);
            TryDelete(pending);
            return rotated;
        }
        if (current != null && await Works(api, current, ct).ConfigureAwait(false))
        {
            TryDelete(pending);
            return current;
        }
        return current;
    }

    private static async Task<bool> Works(HubApi api, HubIdentity identity, CancellationToken ct)
    {
        try
        {
            await api.MeAsync(identity.Key, ct).ConfigureAwait(false);
            return true;
        }
        catch (HubException ex) when (ex.Code is "unknown_key" or "revoked") { return false; }
    }

    // "name.json", or "name (2).json" and so on when that's taken.
    private static string CustomFreeName(string path)
    {
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        string candidate = path;
        for (int i = 2; File.Exists(candidate); i++) candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
        return candidate;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- DPAPI (crypt32; the ProtectedData package isn't in the .NET 6 shared framework) -------------

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private const int UiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>Encrypts bytes to the current Windows user (DPAPI).</summary>
    internal static byte[] Protect(byte[] plain) => Dpapi(plain, protect: true);

    internal static byte[] Unprotect(byte[] blob) => Dpapi(blob, protect: false);

    private static byte[] Dpapi(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = new DataBlob();
        try
        {
            var inBlob = new DataBlob { Size = input.Length, Data = inHandle.AddrOfPinnedObject() };
            var entropy = new DataBlob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref inBlob, "Nocturne But Better hub key", ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var bytes = new byte[output.Size];
            Marshal.Copy(output.Data, bytes, 0, output.Size);
            return bytes;
        }
        finally
        {
            inHandle.Free();
            entropyHandle.Free();
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
}
