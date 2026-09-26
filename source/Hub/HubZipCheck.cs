using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NocturneFlatScroll;

/// <summary>A package the hub rules refuse; <see cref="Problems"/> are plain words (at most 20).</summary>
internal sealed class HubZipProblem : IOException
{
    internal HubZipProblem(IEnumerable<string> problems) : this(problems.Take(20).ToList()) { }

    private HubZipProblem(List<string> problems) : base(problems.Count > 0 ? problems[0] : "the package isn't one the hub takes") =>
        Problems = problems;

    internal HubZipProblem(string problem) : this(new List<string> { problem }) { }

    internal IReadOnlyList<string> Problems { get; }
}

/// <summary>One file (or folder) in a package's central directory.</summary>
internal sealed class HubZipEntry
{
    internal int Index;
    internal int VersionNeeded, Flags, Method, NameLength, ExtraLength, CommentLength, DiskStart;
    internal uint Crc;
    internal long CompressedSize, Size, Offset;
    internal byte[] NameBytes = Array.Empty<byte>();
    /// <summary>The name as stored (UTF-8 or ASCII); null when it's neither.</summary>
    internal string? Name;
    internal bool IsFolder;
    /// <summary>The first part of the path, and whether it is exactly "Top/name".</summary>
    internal string First = "";
    internal bool OneDeep;
    /// <summary>The name inside the package's folder, and its lower-case extension.</summary>
    internal string Relative = "";
    internal string Extension = "";
    /// <summary>Where its data starts, from its local header.</summary>
    internal long DataStart;
}

/// <summary>What a checked package is, from its own files (DESIGN-HUB 2.6 step 5, 2.7).</summary>
internal sealed class HubPackageFacts
{
    internal string Kind = "";
    internal int Format;
    internal string? BattleId;
    internal string Title = "", Artist = "", Author = "";
    internal int Lanes;
    /// <summary>A pack's songs, in its manifest's order.</summary>
    internal List<string> Songs = new();
    internal HubFlags Flags = new();
    internal HubSource? Source;
    internal string Fingerprint = "";
    internal HubContents Contents = new();
    internal int Entries;
    internal long Unpacked;
    internal string Prefix = "";
    internal List<HubZipEntry> Files = new();
    /// <summary>battle.json's "audio" and "card", as entries' names inside the folder.</summary>
    internal string? Audio, Card;
    /// <summary>What the check noted without refusing (for the log).</summary>
    internal List<string> Notes = new();
}

/// <summary>
/// The check every downloaded package passes before it's installed, and every package the upload
/// builder makes passes before it's sent (DESIGN-HUB 2.7, the client column, and server/API.md
/// "the package rules"): the zip's structure (nothing before it or after its end record, no
/// comment, no ZIP64, a bounded file list), every entry's name (no way out of the folder, no names
/// Windows would read another way), no local extra fields and no overlapping entries (read from the
/// local headers), the extension allow-list and per-file limits, every entry inflated in full with
/// its size and CRC-32 checked (so a zip bomb stops at its declared size), and every file checked
/// by its bytes for the decoder it would reach: songs the mod's own WAV and Ogg Vorbis readers or
/// MP3, pictures PNG, JPEG or GIF, videos WebM with VP8, and .sm/.json files plain text. It also
/// reads battle.json or manifest.json (strict JSON) for the facts the listing must match. Nothing
/// is unpacked to disk and nothing is run. Runs on a worker thread.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class HubZipCheck
{
    internal const int MaxCentralBytes = 1024 * 1024;
    internal const int MaxPath = 200;
    internal const int MaxFolders = 6;
    internal const int MaxRootJson = 256 * 1024;
    internal const int MaxHeavyMedia = 16;
    private const int TextHead = 64;

    internal static readonly HashSet<string> BattleExtensions = new(StringComparer.Ordinal) { "json", "sm", "ogg", "wav", "mp3", "png", "jpg", "jpeg", "gif", "webm" };
    internal static readonly HashSet<string> PackExtensions = new(StringComparer.Ordinal) { "json", "sm" };
    internal static readonly HashSet<string> AudioExtensions = new(StringComparer.Ordinal) { "ogg", "wav", "mp3" };
    internal static readonly HashSet<string> PictureExtensions = new(StringComparer.Ordinal) { "png", "jpg", "jpeg", "gif" };
    internal static readonly HashSet<string> VideoExtensions = new(StringComparer.Ordinal) { "webm" };

    /// <summary>The hub's size limits, from /v1/info (the defaults are the hub's own).</summary>
    internal sealed class Limits
    {
        internal long MaxPackage = 100L * 1024 * 1024;
        internal long MaxUnpacked = 200L * 1024 * 1024;
        internal int MaxEntries = 1000;
        internal int MaxSongs = 40;

        internal static Limits From(HubInfo? info) => info == null ? new Limits() : new Limits
        {
            MaxPackage = info.MaxPackageBytes > 0 ? info.MaxPackageBytes : 100L * 1024 * 1024,
            MaxUnpacked = info.MaxUnpackedBytes > 0 ? info.MaxUnpackedBytes : 200L * 1024 * 1024,
            MaxEntries = info.MaxEntries > 0 ? info.MaxEntries : 1000,
            MaxSongs = info.MaxSongsPerPack > 0 ? info.MaxSongsPerPack : 40,
        };
    }

    private static long U16(byte[] b, int at) => b[at] | (b[at + 1] << 8);
    private static long U32(byte[] b, int at) => (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));

    private static byte[] ReadAt(FileStream file, long offset, int count)
    {
        var buffer = new byte[count];
        file.Position = offset;
        int total = 0;
        while (total < count)
        {
            int n = file.Read(buffer, total, count - total);
            if (n <= 0) throw new HubZipProblem("The package is cut short.");
            total += n;
        }
        return buffer;
    }

    private static string MB(long bytes) => Math.Ceiling(bytes / (1024.0 * 1024.0)).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Checks a package file of <paramref name="kind"/> ("battle" or "charts") in full. Throws
    /// <see cref="HubZipProblem"/> with every problem found (in plain words) when it's refused.
    /// </summary>
    internal static HubPackageFacts Check(string path, string kind, Limits limits, CancellationToken ct = default)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        long size = file.Length;
        if (size > limits.MaxPackage) throw new HubZipProblem($"The package is {MB(size)} MB; the hub takes up to {limits.MaxPackage / (1024 * 1024)} MB.");
        if (size < 22 + 30) throw new HubZipProblem("The file is too small to be a package.");
        if (U32(ReadAt(file, 0, 4), 0) != 0x04034b50) throw new HubZipProblem("The file doesn't start like a zip (something is stuck in front of it, or it isn't a zip).");
        var end = ReadAt(file, size - 22, 22);
        if (U32(end, 0) != 0x06054b50) throw new HubZipProblem("The zip's end record isn't where it must be (a zip comment, or bytes stuck after the zip).");
        long disk = U16(end, 4), cdDisk = U16(end, 6), onDisk = U16(end, 8), total = U16(end, 10);
        long cdSize = U32(end, 12), cdOffset = U32(end, 16), commentLength = U16(end, 20);
        if (commentLength != 0) throw new HubZipProblem("The zip has a comment; hub packages have none.");
        if (total == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF) throw new HubZipProblem("The zip uses ZIP64, which the hub doesn't take.");
        if (disk != 0 || cdDisk != 0 || onDisk != total) throw new HubZipProblem("The zip is split over several disks.");
        if (total == 0) throw new HubZipProblem("The zip is empty.");
        if (total > limits.MaxEntries) throw new HubZipProblem($"The zip has {total} files; the hub takes up to {limits.MaxEntries}.");
        if (cdSize > MaxCentralBytes) throw new HubZipProblem("The zip's file list is over 1 MB.");
        if (cdOffset + cdSize != size - 22) throw new HubZipProblem("The zip's file list doesn't end where its end record starts.");
        if (cdOffset < 30) throw new HubZipProblem("The zip's file list starts before any file.");
        // A second opinion from the mod's own check of what .NET's zip reader would walk.
        file.Position = 0;
        if (OszImport.CheckDirectory(file) is string walk) throw new HubZipProblem("The zip can't be read safely: " + walk);

        var entries = ParseCentral(ReadAt(file, cdOffset, (int)cdSize), (int)total);
        var facts = new HubPackageFacts { Kind = kind, Entries = entries.Count };
        CheckEntries(entries, kind, cdOffset, limits, facts);
        CheckLocalHeaders(file, entries, cdOffset);
        facts.Fingerprint = Fingerprint(entries.Select(e => (e.Name!, e.Size, e.Crc)));
        ct.ThrowIfCancellationRequested();

        // Every file inflated in full: its size and CRC-32, and its bytes checked by what reads them.
        var root = facts.Files.First(e => e.Relative.Equals(kind == "battle" ? BattlePackage.ManifestName : "manifest.json", StringComparison.OrdinalIgnoreCase));
        byte[]? rootBytes = null;
        var problems = new List<string>();
        int heavy = 0;
        long unpacked = 0;
        foreach (var e in facts.Files)
        {
            ct.ThrowIfCancellationRequested();
            bool keep = e == root || e.Extension is "json" or "sm" || AudioExtensions.Contains(e.Extension)
                || PictureExtensions.Contains(e.Extension) || VideoExtensions.Contains(e.Extension);
            byte[]? bytes;
            try { bytes = Inflate(file, e, keep, limits.MaxUnpacked - unpacked); }
            catch (HubZipProblem ex)
            {
                problems.AddRange(ex.Problems);
                continue;
            }
            unpacked += e.Size;
            if (e == root)
            {
                rootBytes = bytes;
                continue;
            }
            if (bytes == null) continue;
            string? why = e.Extension switch
            {
                "json" or "sm" => TextProblem(bytes.AsSpan(0, Math.Min(bytes.Length, TextHead))),
                _ when AudioExtensions.Contains(e.Extension) => AudioProblem(bytes),
                _ when PictureExtensions.Contains(e.Extension) => PictureProblem(bytes),
                _ when VideoExtensions.Contains(e.Extension) => VideoProblem(bytes),
                _ => null,
            };
            if (AudioExtensions.Contains(e.Extension) || VideoExtensions.Contains(e.Extension)) heavy++;
            if (why != null) problems.Add($"{e.Relative} {why}.");
        }
        if (heavy > MaxHeavyMedia) problems.Add($"The battle has {heavy} song and video files; the hub takes up to {MaxHeavyMedia}.");
        if (problems.Count > 0) throw new HubZipProblem(problems);
        if (rootBytes == null) throw new HubZipProblem($"{root.Relative} can't be read.");

        using (var doc = ParseRoot(root.Relative, rootBytes))
        {
            if (kind == "battle") BattleFacts(doc.RootElement, facts);
            else PackFacts(doc.RootElement, facts, limits.MaxSongs);
        }
        facts.Contents = Summary(facts.Files, facts.Unpacked);
        facts.Contents.MediaChecked = facts.Contents.MediaTotal = facts.Files.Count(e =>
            AudioExtensions.Contains(e.Extension) || PictureExtensions.Contains(e.Extension) || VideoExtensions.Contains(e.Extension));
        return facts;
    }

    // ---- the central directory ------------------------------------------------------------------

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static List<HubZipEntry> ParseCentral(byte[] cd, int count)
    {
        var entries = new List<HubZipEntry>(count);
        int at = 0;
        for (int i = 0; i < count; i++)
        {
            if (at + 46 > cd.Length || U32(cd, at) != 0x02014b50) throw new HubZipProblem("The zip's file list is damaged.");
            var e = new HubZipEntry
            {
                Index = i,
                VersionNeeded = (int)U16(cd, at + 6),
                Flags = (int)U16(cd, at + 8),
                Method = (int)U16(cd, at + 10),
                Crc = (uint)U32(cd, at + 16),
                CompressedSize = U32(cd, at + 20),
                Size = U32(cd, at + 24),
                NameLength = (int)U16(cd, at + 28),
                ExtraLength = (int)U16(cd, at + 30),
                CommentLength = (int)U16(cd, at + 32),
                DiskStart = (int)U16(cd, at + 34),
                Offset = U32(cd, at + 42),
            };
            int nameStart = at + 46;
            int next = nameStart + e.NameLength + e.ExtraLength + e.CommentLength;
            if (next > cd.Length) throw new HubZipProblem("The zip's file list is damaged.");
            e.NameBytes = cd.AsSpan(nameStart, e.NameLength).ToArray();
            bool ascii = e.NameBytes.All(b => b < 0x80);
            if (ascii) e.Name = Encoding.ASCII.GetString(e.NameBytes);
            else if ((e.Flags & 0x800) != 0)
            {
                try { e.Name = StrictUtf8.GetString(e.NameBytes); }
                catch (DecoderFallbackException) { e.Name = null; }
            }
            // A ZIP64 or damaged extra field in the directory.
            int x = nameStart + e.NameLength, xEnd = x + e.ExtraLength;
            while (x < xEnd)
            {
                if (x + 4 > xEnd || x + 4 + U16(cd, x + 2) > xEnd)
                {
                    e.ExtraLength = -1;
                    break;
                }
                if (U16(cd, x) == 0x0001) e.CompressedSize = 0xFFFFFFFF;
                x += 4 + (int)U16(cd, x + 2);
            }
            entries.Add(e);
            at = next;
        }
        if (at != cd.Length) throw new HubZipProblem("The zip's file list has bytes after its last entry.");
        return entries;
    }

    /// <summary>Why a name inside a package is refused (the hub's rules), or null. Folders end with "/".</summary>
    internal static string? NameProblem(string? name)
    {
        if (name == null) return "has a name that isn't UTF-8 (or plain ASCII)";
        string path = name.EndsWith("/") ? name.Substring(0, name.Length - 1) : name;
        if (path.Length == 0) return "has an empty name";
        if (name.Length > MaxPath && HubText.Length(name) > MaxPath) return $"has a path over {MaxPath} characters";
        if (path[0] == '/') return "starts with / (an absolute path)";
        foreach (char c in path)
            if (c < 0x20 || (c >= 0x7F && c <= 0x9F) || c is '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*')
                return "has a character Windows doesn't allow in names (\\ : < > \" | ? * or a control character)";
        for (int i = 0; i < path.Length; i++)
        {
            int cp = char.IsHighSurrogate(path[i]) && i + 1 < path.Length ? char.ConvertToUtf32(path[i], path[i + 1]) : path[i];
            if (HubText.IsInvisible(cp)) return "has invisible or text-direction characters in its name";
        }
        var parts = path.Split('/');
        if (parts.Length > MaxFolders + 1) return $"is more than {MaxFolders} folders deep";
        foreach (var part in parts)
        {
            if (part.Length == 0) return "has an empty folder name (//)";
            if (part is "." or "..") return "has a . or .. in its path";
            if (part[^1] is ' ' or '.') return "has a name part ending in a dot or a space";
            if (IsDevice(part)) return "uses a Windows device name (like CON or COM1)";
        }
        return null;
    }

    private static bool IsDevice(string part)
    {
        string stem = part.Split('.')[0].ToLowerInvariant();
        if (stem is "con" or "prn" or "aux" or "nul") return true;
        if (stem.Length == 4 && (stem.StartsWith("com") || stem.StartsWith("lpt")))
            return stem[3] is >= '0' and <= '9' or (char)0xB9 or (char)0xB2 or (char)0xB3;
        return false;
    }

    /// <summary>The lower-case extension of a name ("" when it has none).</summary>
    internal static string ExtensionOf(string name)
    {
        string last = name.Substring(name.LastIndexOf('/') + 1);
        int dot = last.LastIndexOf('.');
        return dot <= 0 ? "" : last.Substring(dot + 1).ToLowerInvariant();
    }

    private static void CheckEntries(List<HubZipEntry> entries, string kind, long cdOffset, Limits limits, HubPackageFacts facts)
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            string label = e.Name == null ? $"File {e.Index + 1}" : e.Name.Length > 120 ? e.Name.Substring(0, 120) : e.Name;
            if ((e.Flags & 0x1) != 0 || (e.Flags & 0x40) != 0 || (e.Flags & 0x2000) != 0) problems.Add($"{label} is encrypted.");
            if (e.Method != 0 && e.Method != 8) problems.Add($"{label} uses a compression method other than store or deflate.");
            if (e.VersionNeeded > 20) problems.Add($"{label} needs zip features the hub doesn't take.");
            if (e.CompressedSize == 0xFFFFFFFF || e.Size == 0xFFFFFFFF || e.Offset == 0xFFFFFFFF) problems.Add($"{label} uses ZIP64.");
            if (e.ExtraLength < 0) problems.Add($"{label} has a damaged extra field.");
            if (e.CommentLength != 0) problems.Add($"{label} has a comment; hub packages have none.");
            if (e.DiskStart != 0) problems.Add($"{label} is on another disk.");
            if (e.Method == 0 && e.CompressedSize != e.Size) problems.Add($"{label} is stored but its sizes differ.");
            // Deflate can't expand more than about 1032 to 1, so a bigger declared size is a lie.
            if (e.Method == 8 && e.Size > e.CompressedSize * 1032 + 1024) problems.Add($"{label} declares a size its compressed data can't have.");
            string? why = NameProblem(e.Name);
            if (why != null)
            {
                problems.Add($"{label} {why}.");
                continue;
            }
            string name = e.Name!;
            e.IsFolder = name.EndsWith("/");
            if (e.IsFolder && e.Size != 0) problems.Add($"{label} is a folder with data in it.");
            string clean = e.IsFolder ? name.Substring(0, name.Length - 1) : name;
            int slash = clean.IndexOf('/');
            e.First = slash < 0 ? clean : clean.Substring(0, slash);
            e.OneDeep = slash >= 0 && clean.IndexOf('/', slash + 1) < 0;
            // Only A-Z count as the same letters in two cases, as the hub compares them.
            if (!seen.Add(UpperAscii(clean))) problems.Add($"{label} is in the zip twice (names are compared ignoring case).");
        }
        if (problems.Count > 0) throw new HubZipProblem(problems);

        string rootName = kind == "battle" ? BattlePackage.ManifestName : "manifest.json";
        string? prefix = null;
        if (entries.Any(e => !e.IsFolder && e.Name!.Equals(rootName, StringComparison.OrdinalIgnoreCase))) prefix = "";
        else if (kind == "battle")
        {
            var tops = new HashSet<string>(entries.Select(e => e.First), StringComparer.Ordinal);
            var candidates = entries.Where(e => !e.IsFolder && e.OneDeep && e.Name!.Length == e.First.Length + 1 + rootName.Length
                && e.Name.EndsWith("/" + rootName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count > 1) throw new HubZipProblem("The zip has more than one battle.json.");
            if (candidates.Count == 1 && tops.Count == 1) prefix = candidates[0].First + "/";
            else if (candidates.Count == 1) throw new HubZipProblem("Everything in a battle must be inside its one folder.");
        }
        if (prefix == null) throw new HubZipProblem($"The zip has no {rootName}{(kind == "battle" ? " at its root or in its one folder" : " at its root")}.");

        long totalSize = 0;
        var allowed = kind == "battle" ? BattleExtensions : PackExtensions;
        foreach (var e in entries)
        {
            if (!e.Name!.StartsWith(prefix, StringComparison.Ordinal))
            {
                problems.Add($"{Cut(e.Name)} is outside the battle's folder.");
                continue;
            }
            totalSize += e.Size;
            if (e.IsFolder) continue;
            e.Relative = e.Name.Substring(prefix.Length);
            e.Extension = ExtensionOf(e.Relative);
            if (!allowed.Contains(e.Extension))
            {
                problems.Add(e.Extension == "mp4" ? $"{Cut(e.Relative)} is an MP4 video; the hub takes WebM (VP8) videos or frames."
                    : e.Extension.Length == 0 ? $"{Cut(e.Relative)} has no file type."
                    : $"{Cut(e.Relative)} is a .{e.Extension} file, which the hub doesn't take{(kind == "charts" ? " in a difficulty pack" : "")}.");
                continue;
            }
            // The loader's own limits (battles only have art/ and portraits/ folders).
            if (kind == "battle" && BattleFiles.SafeEntryName(e.Relative) != e.Relative) problems.Add($"{Cut(e.Relative)} has a name a battle can't have.");
            if (e.Size > BattleFiles.LimitFor(e.Relative)) problems.Add($"{Cut(e.Relative)} is too big ({MB(e.Size)} MB).");
            facts.Files.Add(e);
        }
        if (totalSize > limits.MaxUnpacked)
            problems.Add($"The package unpacks to {MB(totalSize)} MB; the hub takes up to {limits.MaxUnpacked / (1024 * 1024)} MB.");
        if (problems.Count > 0) throw new HubZipProblem(problems);
        facts.Prefix = prefix;
        facts.Unpacked = totalSize;
    }

    private static string Cut(string text) => text.Length > 120 ? text.Substring(0, 120) : text;

    private static string UpperAscii(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] is >= 'a' and <= 'z') chars[i] = (char)(chars[i] - 32);
        return new string(chars);
    }

    /// <summary>
    /// Each local header must agree with its directory entry and have no extra field (one would move
    /// the data without the directory saying so), and no entry may reach into the next one or into
    /// the file list, counted from the local header (the overlapping-entries zip bomb).
    /// </summary>
    private static void CheckLocalHeaders(FileStream file, List<HubZipEntry> entries, long cdOffset)
    {
        var byOffset = entries.OrderBy(e => e.Offset).ToList();
        if (byOffset[0].Offset != 0) throw new HubZipProblem("The zip's first file doesn't start at the beginning.");
        for (int i = 0; i < byOffset.Count; i++)
        {
            var e = byOffset[i];
            if (e.Offset + 30 + e.NameLength > cdOffset) throw new HubZipProblem($"{Cut(e.Name!)} runs into the zip's file list.");
            var h = ReadAt(file, e.Offset, 30 + e.NameLength);
            string label = Cut(e.Name!);
            if (U32(h, 0) != 0x04034b50) throw new HubZipProblem($"{label} has a damaged local header.");
            long flags = U16(h, 6), method = U16(h, 8), crc = U32(h, 14), csize = U32(h, 18), usize = U32(h, 22);
            long nameLength = U16(h, 26), extraLength = U16(h, 28);
            if (extraLength != 0) throw new HubZipProblem($"{label} has an extra field in its local header; hub packages have none.");
            if (method != e.Method || (flags & 0x1) != (e.Flags & 0x1) || (flags & 0x8) != (e.Flags & 0x8))
                throw new HubZipProblem($"{label} has local and central headers that disagree.");
            if ((flags & 0x8) == 0 && (crc != e.Crc || csize != e.CompressedSize || usize != e.Size))
                throw new HubZipProblem($"{label} has local and central headers that disagree.");
            if (nameLength != e.NameLength || !h.AsSpan(30, e.NameLength).SequenceEqual(e.NameBytes))
                throw new HubZipProblem($"{label} has local and central names that disagree.");
            e.DataStart = e.Offset + 30 + nameLength + extraLength;
            long endOfEntry = e.DataStart + e.CompressedSize + ((e.Flags & 0x8) != 0 ? 16 : 0);
            long limit = i + 1 < byOffset.Count ? byOffset[i + 1].Offset : cdOffset;
            if (endOfEntry > limit) throw new HubZipProblem("Files in the zip overlap each other (the shape of a zip bomb).");
        }
    }

    // ---- the fingerprint and the summary --------------------------------------------------------------

    /// <summary>
    /// The directory fingerprint (API.md): SHA-256 of one line per entry, "name \0 size \0 crc-32 \n",
    /// sorted by the name with only A-Z lowered, compared as UTF-16 code units.
    /// </summary>
    internal static string Fingerprint(IEnumerable<(string Name, long Size, uint Crc)> entries)
    {
        var lines = entries
            .Select(e => (Key: LowerAscii(e.Name), Line: $"{e.Name}\0{e.Size.ToString(CultureInfo.InvariantCulture)}\0{e.Crc:x8}\n"))
            .OrderBy(l => l.Key, StringComparer.Ordinal)
            .Select(l => l.Line);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines)))).ToLowerInvariant();
    }

    private static string LowerAscii(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] is >= 'A' and <= 'Z') chars[i] = (char)(chars[i] + 32);
        return new string(chars);
    }

    /// <summary>What's inside, counted from the file list (the detail panel's "what's inside").</summary>
    internal static HubContents Summary(IEnumerable<HubZipEntry> files, long unpacked)
    {
        var c = new HubContents { Unpacked = unpacked };
        foreach (var e in files)
        {
            c.Files++;
            if (AudioExtensions.Contains(e.Extension)) c.Songs++;
            else if (e.Extension == "sm") c.Charts++;
            else if (PictureExtensions.Contains(e.Extension)) c.Pictures++;
            else if (VideoExtensions.Contains(e.Extension)) c.Videos++;
            else if (e.Extension == "json") c.Json++;
            else c.Other++;
        }
        return c;
    }

    // ---- inflating ------------------------------------------------------------------------------

    /// <summary>
    /// Inflates an entry in full and checks it holds exactly its declared size and CRC-32; reading
    /// stops one byte past the declared size (or the package's limit). The bytes come back when
    /// <paramref name="keep"/>, for the checks that read them.
    /// </summary>
    private static byte[]? Inflate(FileStream file, HubZipEntry e, bool keep, long budget)
    {
        if (e.Size > budget) throw new HubZipProblem($"{e.Relative} is past what the package may unpack to.");
        file.Position = e.DataStart;
        using var raw = new BoundedStream(file, e.CompressedSize);
        using Stream data = e.Method == 8 ? new DeflateStream(raw, CompressionMode.Decompress, leaveOpen: true) : raw;
        var kept = keep ? new MemoryStream((int)Math.Min(e.Size, 64L * 1024 * 1024)) : null;
        var buffer = new byte[81920];
        uint crc = Crc32.Start;
        long read = 0;
        try
        {
            int n;
            while ((n = data.Read(buffer, 0, (int)Math.Min(buffer.Length, e.Size - read + 1))) > 0)
            {
                read += n;
                if (read > e.Size) throw new HubZipProblem($"{e.Relative} holds more than its declared size (a zip bomb, or damaged).");
                crc = Crc32.Update(crc, buffer.AsSpan(0, n));
                kept?.Write(buffer, 0, n);
            }
        }
        catch (InvalidDataException)
        {
            throw new HubZipProblem($"{e.Relative} has damaged compressed data.");
        }
        if (read != e.Size) throw new HubZipProblem($"{e.Relative} doesn't unpack to its declared size.");
        if (Crc32.Finish(crc) != e.Crc) throw new HubZipProblem($"{e.Relative} doesn't match its checksum (CRC-32).");
        return kept?.ToArray();
    }

    /// <summary>At most <c>length</c> bytes of a stream from where it is.</summary>
    private sealed class BoundedStream : Stream
    {
        private readonly Stream inner;
        private long left;

        internal BoundedStream(Stream inner, long length)
        {
            this.inner = inner;
            left = length;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (left <= 0) return 0;
            int n = inner.Read(buffer, offset, (int)Math.Min(count, left));
            if (n <= 0) throw new HubZipProblem("The package is cut short.");
            left -= n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- media by decoder -----------------------------------------------------------------------

    private const string ConvertHint = "convert the song to .ogg (Vorbis) or .mp3";

    /// <summary>
    /// Why a song file's bytes would reach a decoder the hub doesn't allow, or null. The mod picks
    /// the decoder from the whole file's bytes (<see cref="AudioFile.Detect"/>), so this asks it:
    /// only PCM or float WAV and Ogg Vorbis (the mod's own readers) and MP3 are allowed.
    /// </summary>
    internal static string? AudioProblem(byte[] bytes)
    {
        switch (AudioFile.Detect(bytes))
        {
            case AudioFile.Kind.Wav:
                int tag = WavFile.FormatTag(bytes);
                if (tag is 1 or 3) return null;
                return tag < 0 ? "is a WAV file without a format chunk" : $"is a WAV file in format {tag}, which goes to Windows' own decoders; {ConvertHint}, or save it as plain PCM WAV";
            case AudioFile.Kind.Ogg:
                return OggFirstPacketIsVorbis(bytes) ? null : $"is an Ogg file that isn't Ogg Vorbis; {ConvertHint}";
            case AudioFile.Kind.Mp3:
                return null;
            case AudioFile.Kind.Flac: return $"is a FLAC file; {ConvertHint}";
            case AudioFile.Kind.Adts: return $"is AAC (ADTS); {ConvertHint}";
            case AudioFile.Kind.Mp4: return $"is an MP4/M4A file; {ConvertHint}";
            case AudioFile.Kind.Asf: return $"is a WMA file; {ConvertHint}";
            case AudioFile.Kind.Matroska:
            case AudioFile.Kind.WebM: return $"is Matroska or WebM audio; {ConvertHint}";
            case AudioFile.Kind.Aiff: return $"is an AIFF file; {ConvertHint}";
            default: return "isn't a song file the hub takes (it takes WAV, Ogg Vorbis and MP3)";
        }
    }

    // The first page's first packet must be a Vorbis identification header ("\x01vorbis").
    private static bool OggFirstPacketIsVorbis(byte[] b)
    {
        if (b.Length < 28 || b[0] != 'O' || b[1] != 'g' || b[2] != 'g' || b[3] != 'S') return false;
        int start = 27 + b[26];
        if (start + 7 > b.Length) return false;
        return b[start] == 1 && Encoding.ASCII.GetString(b, start + 1, 6) == "vorbis";
    }

    /// <summary>A picture must be PNG, JPEG or GIF by its bytes (what the game's decoder and the mod's GIF reader take).</summary>
    internal static string? PictureProblem(byte[] bytes) =>
        MediaSniff.TypeOf(bytes) is MediaType.Png or MediaType.Jpeg or MediaType.Gif ? null : "isn't a PNG, JPEG or GIF picture";

    /// <summary>A video must be WebM (doctype "webm") with VP8 video: Unity plays that itself.</summary>
    internal static string? VideoProblem(byte[] bytes)
    {
        if (!VideoProbe.IsEbml(bytes)) return "isn't a WebM video (the hub takes WebM with VP8 video)";
        VideoFacts facts;
        try { facts = VideoProbe.Read(bytes); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException) { return "is a damaged WebM video"; }
        if (!facts.DocType.Equals("webm", StringComparison.Ordinal)) return "isn't a WebM video (a Matroska file, or damaged)";
        if (!facts.HasVideo) return "is a WebM file with no video in it";
        if (facts.Codec != "V_VP8") return $"is a {facts.CodecName} WebM video; the game plays only VP8 in WebM";
        return null;
    }

    /// <summary>
    /// A .sm or .json file must hold text: its first 64 bytes have no control character but tab and
    /// line breaks, and don't start like a sound, picture or video (a file is read by its bytes, and
    /// any file can be named as a song, a picture or enemy art).
    /// </summary>
    internal static string? TextProblem(ReadOnlySpan<byte> head)
    {
        if (MediaSignature(head)) return "holds a sound, picture or video file, not text";
        foreach (byte x in head)
            if ((x < 0x20 && x != 0x09 && x != 0x0A && x != 0x0D) || x == 0x7F) return "isn't a text file";
        return null;
    }

    private static bool Ascii(ReadOnlySpan<byte> b, int at, string s)
    {
        if (at < 0 || at + s.Length > b.Length) return false;
        for (int i = 0; i < s.Length; i++)
            if (b[at + i] != s[i]) return false;
        return true;
    }

    private static bool MediaSignature(ReadOnlySpan<byte> b)
    {
        if (Ascii(b, 0, "RIFF") || Ascii(b, 0, "OggS") || Ascii(b, 0, "fLaC") || Ascii(b, 0, "FORM") || Ascii(b, 0, "ID3")) return true;
        foreach (var box in new[] { "ftyp", "moov", "mdat", "wide", "free", "skip" })
            if (Ascii(b, 4, box)) return true;
        if (b.Length >= 4 && b[0] == 0x30 && b[1] == 0x26 && b[2] == 0xB2 && b[3] == 0x75) return true;
        if (b.Length >= 4 && b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3) return true;
        if (b.Length >= 2 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0) return true;
        if (b.Length >= 2 && ((b[0] == 0x89 && b[1] == 0x50) || (b[0] == 0xFF && b[1] == 0xD8))) return true;
        return Ascii(b, 0, "GIF8");
    }

    // ---- the listing thumbnail ------------------------------------------------------------------

    internal const int MaxThumbB64 = 16384;

    /// <summary>
    /// Why a listing thumbnail isn't one the game will be given, or null (with its bytes): a baseline
    /// JPEG (SOF0) by its markers, at most 256 px each way and 20 segments before its scan, ending with
    /// EOI, in at most 16 KB of base64. It's read by its markers only, never decoded here.
    /// </summary>
    internal static string? ThumbProblem(string? b64, out byte[]? bytes)
    {
        bytes = null;
        if (string.IsNullOrEmpty(b64)) return "no thumbnail";
        if (b64.Length > MaxThumbB64) return "the thumbnail is over 16 KB of base64";
        if (b64.Length % 4 != 0 || b64.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '='))) return "the thumbnail isn't base64";
        byte[] b;
        try { b = Convert.FromBase64String(b64); }
        catch (FormatException) { return "the thumbnail isn't base64"; }
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return "the thumbnail isn't a JPEG";
        if (b[^2] != 0xFF || b[^1] != 0xD9) return "the thumbnail JPEG is cut short";
        int at = 2, segments = 0;
        bool frame = false;
        while (true)
        {
            if (at + 4 > b.Length || b[at] != 0xFF) return "the thumbnail JPEG is damaged";
            int marker = b[at + 1];
            while (marker == 0xFF && at + 2 < b.Length) marker = b[++at + 1];
            if (marker == 0xD9) return "the thumbnail JPEG has no picture data";
            if (marker >= 0xD0 && marker <= 0xD7) return "the thumbnail JPEG is damaged";
            if (at + 4 > b.Length) return "the thumbnail JPEG is damaged";
            int length = b[at + 2] << 8 | b[at + 3];
            if (length < 2 || at + 2 + length > b.Length) return "the thumbnail JPEG is damaged";
            if (++segments > 20) return "the thumbnail JPEG has too many segments";
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (marker != 0xC0) return "the thumbnail must be a baseline JPEG (not progressive)";
                if (length < 8) return "the thumbnail JPEG is damaged";
                int height = b[at + 5] << 8 | b[at + 6], width = b[at + 7] << 8 | b[at + 8];
                if (width == 0 || height == 0 || width > 256 || height > 256) return "the thumbnail must be at most 256 by 256 pixels";
                frame = true;
            }
            if (marker == 0xDA)
            {
                if (!frame) return "the thumbnail JPEG has no frame header";
                bytes = b;
                return null;
            }
            at += 2 + length;
        }
    }

    // ---- battle.json and manifest.json ------------------------------------------------------------

    private static JsonDocument ParseRoot(string name, byte[] bytes)
    {
        if (bytes.Length > MaxRootJson) throw new HubZipProblem($"{name} is over 256 KB.");
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            StrictUtf8.GetString(bytes, start, bytes.Length - start);
            var doc = JsonDocument.Parse(bytes.AsMemory(start), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 64 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                throw new HubZipProblem($"{name} must hold a JSON object.");
            }
            return doc;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or ArgumentException)
        {
            throw new HubZipProblem($"{name} must be plain JSON as the mod's upload writes it (no comments, no trailing commas).");
        }
    }

    /// <summary>A key looked up ignoring case, like the mod's reader (an exact match first).</summary>
    internal static JsonElement Field(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return default;
        if (obj.TryGetProperty(name, out var exact)) return exact;
        foreach (var p in obj.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return default;
    }

    private static string? Text(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static int? WholeNumber(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int n) ? n : null;

    private static bool ModeIs(JsonElement obj, string mode) =>
        Text(Field(obj, "mode")) is string m && m.Trim().Equals(mode, StringComparison.OrdinalIgnoreCase);

    private static HubZipEntry? EntryFor(HubPackageFacts facts, string? name)
    {
        if (name == null) return null;
        string wanted = name.Replace('\\', '/').Trim();
        return facts.Files.FirstOrDefault(e => e.Relative.Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static void BattleFacts(JsonElement j, HubPackageFacts facts)
    {
        var problems = new List<string>();
        int? format = WholeNumber(Field(j, "format"));
        if (format != HubConfig.BattleFormat)
            problems.Add(format > HubConfig.BattleFormat ? "battle.json was made for a newer version of the mod." : "battle.json isn't format 2.");
        if (!(Text(Field(j, "kind")) is string kind && kind.Trim().Equals("battle", StringComparison.OrdinalIgnoreCase))) problems.Add("battle.json's \"kind\" isn't \"battle\".");
        string id = (Text(Field(j, "id")) ?? "").Trim().ToLowerInvariant();
        if (!HubText.IsBattleId(id)) problems.Add("battle.json's id isn't a GUID.");
        string title = HubText.CleanLine(Text(Field(j, "title")), HubText.Title) ?? "";
        if (title.Length == 0) problems.Add("The battle has no title.");
        var artistNode = Field(j, "artist");
        var authorNode = Field(j, "author");
        if ((artistNode.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.String)) || (authorNode.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.String)))
            problems.Add("battle.json's artist and author must be text.");
        int? lanes = WholeNumber(Field(j, "lanes"));
        if (lanes is not (4 or 5)) problems.Add("battle.json must say its lanes (4 or 5).");
        var song = EntryFor(facts, Text(Field(j, "audio")));
        if (song == null || !AudioExtensions.Contains(song.Extension))
            problems.Add("battle.json's \"audio\" must name the song file in the package (an .ogg, .wav or .mp3).");
        var cardNode = Field(j, "card");
        HubZipEntry? card = null;
        if (cardNode.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) && !(cardNode.ValueKind == JsonValueKind.String && cardNode.GetString() == ""))
        {
            card = EntryFor(facts, Text(cardNode));
            if (card == null || !PictureExtensions.Contains(card.Extension))
                problems.Add("battle.json's \"card\" must name a picture in the package (a .png, .jpg or .gif).");
        }
        if (problems.Count > 0) throw new HubZipProblem(problems);

        facts.Format = format!.Value;
        facts.BattleId = id;
        facts.Title = title;
        facts.Artist = HubText.CleanLine(Text(artistNode) ?? "", HubText.Artist) ?? "";
        facts.Author = HubText.CleanLine(Text(authorNode) ?? "", HubText.Author) ?? "";
        facts.Lanes = lanes!.Value;
        facts.Audio = song!.Relative;
        facts.Card = card?.Relative;
        var source = Field(j, "source");
        if (source.ValueKind == JsonValueKind.Object && HubText.CleanLine(Text(Field(source, "kind")), 32) is { Length: > 0 } sourceKind)
            facts.Source = new HubSource { Kind = sourceKind, Mapper = HubText.CleanLine(Text(Field(source, "mapper")) ?? "", HubText.Author) ?? "" };
        var gear = Field(j, "gear");
        var level = Field(j, "level");
        var dialogue = Field(j, "dialogue");
        facts.Flags = new HubFlags
        {
            Gear = gear.ValueKind == JsonValueKind.Object && ModeIs(gear, "set"),
            Level = level.ValueKind == JsonValueKind.Number
                || (level.ValueKind == JsonValueKind.Object && ModeIs(level, "set") && Field(level, "value").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)),
            Dialogue = dialogue.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null),
            Video = facts.Files.Any(e => e.Relative.StartsWith("art/", StringComparison.OrdinalIgnoreCase) && e.Extension == "webm"),
        };
    }

    private static void PackFacts(JsonElement j, HubPackageFacts facts, int maxSongs)
    {
        var problems = new List<string>();
        int? format = WholeNumber(Field(j, "format"));
        if (format is not int f || f < 1 || f > HubConfig.PackFormat)
            problems.Add(format > HubConfig.PackFormat ? "manifest.json was made for a newer version of the mod." : "manifest.json isn't format 1.");
        string title = HubText.CleanLine(Text(Field(j, "title")), HubText.PackTitle) ?? "";
        if (title.Length == 0) problems.Add("The difficulty pack has no title.");
        var authorNode = Field(j, "author");
        if (authorNode.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.String)) problems.Add("manifest.json's author must be text.");
        int? lanes = WholeNumber(Field(j, "lanes"));
        if (lanes is not (4 or 5)) problems.Add("manifest.json must say its lanes (4 or 5).");
        var charts = Field(j, "charts");
        var songs = new List<string>();
        if (charts.ValueKind != JsonValueKind.Array || charts.GetArrayLength() == 0) problems.Add("manifest.json lists no charts.");
        else
        {
            foreach (var c in charts.EnumerateArray())
            {
                string source = Text(Field(c, "source")) ?? (Field(c, "source").ValueKind == JsonValueKind.Undefined ? "game" : "");
                string song = HubText.CleanLine(Text(Field(c, "song")), HubText.Song) ?? "";
                string? chartFile = Text(Field(c, "file"));
                if (!source.Trim().Equals("game", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add("A difficulty pack on the hub can only have charts for the game's own songs.");
                    break;
                }
                if (song.Length == 0)
                {
                    problems.Add("A chart in manifest.json has no song.");
                    break;
                }
                var entry = EntryFor(facts, chartFile);
                if (chartFile == null || !chartFile.EndsWith(".sm", StringComparison.OrdinalIgnoreCase) || entry == null)
                {
                    problems.Add($"The chart for {song} names a file that isn't in the pack.");
                    break;
                }
                if (!songs.Contains(song)) songs.Add(song);
            }
            if (songs.Count > maxSongs) problems.Add($"The pack covers {songs.Count} songs; the hub takes up to {maxSongs}.");
        }
        if (problems.Count > 0) throw new HubZipProblem(problems);
        facts.Format = format!.Value;
        facts.Title = title;
        facts.Author = HubText.CleanLine(Text(authorNode) ?? "", HubText.Author) ?? "";
        facts.Lanes = lanes!.Value;
        facts.Songs = songs;
    }
}
