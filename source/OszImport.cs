using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace NocturneFlatScroll;

// The osu!mania import (beta): reads a .osz (an osu! beatmap set, a zip) into a plan the battle
// creator shows before anything is written. Reading is read-only and bounded in size and time;
// every problem with the file ends in one plain sentence. Nothing from the zip is ever used as a
// path: names are only matched, and the song and card are copied under the battle's own names.
// This file has no Unity or game dependencies.

/// <summary>Ends a read that has run too long: checked in every loop over the file's contents.</summary>
internal sealed class OszGuard
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly TimeSpan limit;
    private int steps;

    internal OszGuard(TimeSpan limit) => this.limit = limit;

    internal long ElapsedMs => clock.ElapsedMilliseconds;

    /// <summary>Counts a step; every 1024th looks at the clock.</summary>
    internal void Step()
    {
        if ((++steps & 1023) == 0) Check();
    }

    internal void Check()
    {
        if (clock.Elapsed > limit) throw new OszRefused($"reading it took too long (over {limit.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds).");
    }
}

/// <summary>Why a .osz can't be imported, in plain words for the player.</summary>
internal sealed class OszRefused : Exception
{
    internal OszRefused(string message) : base(message) { }
}

/// <summary>What reading a .osz found, and what the import would make of it.</summary>
internal sealed class OszPlan
{
    internal string SourcePath = "", SourceName = "";
    /// <summary>The .osz's size and time when it was read, to notice a change before making the battle.</summary>
    internal long SourceLength;
    internal DateTime SourceWriteUtc;
    /// <summary>Why it can't be imported (plain words), or null.</summary>
    internal string? Error;
    /// <summary>An exception nobody expected, for the log.</summary>
    internal Exception? Bug;
    internal string Title = "", Artist = "", Mapper = "";
    internal long BeatmapSetId;
    internal OszSong? Song;
    internal OszCard? Card;
    /// <summary>Why there's no card (a sentence), when the beatmap has a background that can't be one.</summary>
    internal string? CardProblem;
    /// <summary>Every .osu read (or not readable), in zip order.</summary>
    internal readonly List<OszDifficulty> Difficulties = new();
    internal OszLaneGroup? Four, Five;
    internal int DefaultLanes = 4;
    /// <summary>What osu! has that a battle doesn't use, and other things not about one difficulty (sentences).</summary>
    internal readonly List<string> Details = new();
    /// <summary>.osu files past the limit, not read.</summary>
    internal int SkippedOsuFiles;
    /// <summary>What the decoder or the zip reader said when the file couldn't be read, for the log.</summary>
    internal string? Detail;
    internal long ReadMs;

    internal OszLaneGroup? Group(int lanes) => lanes == 5 ? Five : lanes == 4 ? Four : null;
}

/// <summary>The beatmap's song.</summary>
internal sealed class OszSong
{
    /// <summary>The zip entry: its place in the zip, its name and its size, to copy it at Make.</summary>
    internal int EntryIndex;
    internal string EntryName = "";
    internal long Length;
    /// <summary>The name the difficulties give (AudioFilename).</summary>
    internal string Named = "";
    /// <summary>No entry had that name, so the set's only song file is used.</summary>
    internal bool Guessed;
    internal double Seconds;
    /// <summary>Seconds taken off osu!'s times (<see cref="OsuAudio.Shift"/>), and why, for the log.</summary>
    internal double Shift;
    internal string ShiftHow = "";
    /// <summary>The song's path in the battle, like "audio/audio.mp3".</summary>
    internal string FileName = "";
}

/// <summary>The background image, copied as the battle's card.</summary>
internal sealed class OszCard
{
    internal int EntryIndex;
    internal string EntryName = "";
    internal long Length;
    /// <summary>The card's path in the battle, like "images/bg.png".</summary>
    internal string FileName = "";
}

/// <summary>One .osu in the .osz.</summary>
internal sealed class OszDifficulty
{
    /// <summary>The osu! Version (the difficulty's name), else the file's name.</summary>
    internal string Name = "";
    internal string Entry = "";
    internal int Order;
    /// <summary>Null when the file couldn't be read.</summary>
    internal OsuFile? File;
    internal int Keys;
    internal long Mode;
    /// <summary>Why it can't be in a battle (plain words), or null.</summary>
    internal string? Unusable;
    internal double OverallDifficulty => File == null || double.IsNaN(File.OverallDifficulty) ? 0 : File.OverallDifficulty;
}

/// <summary>
/// The difficulties with one key count (4 or 5 lanes), all worked out when the .osz is read: the
/// shared timing, each one's notes and speed changes, and the default slots.
/// </summary>
internal sealed class OszLaneGroup
{
    internal int Lanes;
    /// <summary>Easiest first.</summary>
    internal readonly List<OszDifficulty> Usable = new();
    /// <summary>The difficulty with the most notes, whose tempo lines every chart shares.</summary>
    internal OszDifficulty TimingFrom = null!;
    internal OszTiming Timing = null!;
    internal readonly Dictionary<OszDifficulty, OszChart> Charts = new();
    internal readonly Dictionary<OszDifficulty, OszSpeeds> Speeds = new();
    /// <summary>"No speed changes": only the stretched sections' ratios, which keep them looking even.</summary>
    internal OszSpeeds BaseScrolls = null!;
    internal readonly OszDifficulty?[] DefaultSlots = new OszDifficulty?[6];
    /// <summary>Whose speed changes the battle has at first; null for none.</summary>
    internal OszDifficulty? DefaultSpeedsFrom;
    /// <summary>The timing difficulty's bookmarks, in the song's seconds.</summary>
    internal readonly List<double> Bookmarks = new();
    internal double PreviewStart;

    /// <summary>Whether a difficulty has speed changes of its own (more than the stretched sections').</summary>
    internal bool HasSpeedChanges(OszDifficulty d) => Speeds.TryGetValue(d, out var s) && s.Own.Count > 0 && s.Tag != BaseScrolls.Tag;
}

/// <summary>What the player picked on the import's summary.</summary>
internal sealed class OszChoices
{
    internal int Lanes = 4;
    internal readonly OszDifficulty?[] Slots = new OszDifficulty?[6];
    /// <summary>Whose speed changes the battle has; null for none.</summary>
    internal OszDifficulty? SpeedsFrom;
    /// <summary>5 lanes: a player attack every 8 bars (osu! has none, and the enemy can't be hurt without them).</summary>
    internal bool PlayerAttacks;
    /// <summary>Leave out the notes in the song's first <see cref="OszConvert.OpeningSeconds"/>, which come before a battle has shown them.</summary>
    internal bool LeaveOutOpening;

    internal static OszChoices Default(OszPlan plan, int lanes)
    {
        var choices = new OszChoices { Lanes = lanes, PlayerAttacks = lanes == 5 };
        var group = plan.Group(lanes);
        if (group == null) return choices;
        Array.Copy(group.DefaultSlots, choices.Slots, 6);
        choices.SpeedsFrom = group.DefaultSpeedsFrom;
        return choices;
    }

    internal OszChoices Copy()
    {
        var copy = new OszChoices { Lanes = Lanes, SpeedsFrom = SpeedsFrom, PlayerAttacks = PlayerAttacks, LeaveOutOpening = LeaveOutOpening };
        Array.Copy(Slots, copy.Slots, 6);
        return copy;
    }

    /// <summary>The slot a difficulty is in, or -1.</summary>
    internal int SlotOf(OszDifficulty d) => Array.IndexOf(Slots, d);

    /// <summary>
    /// Puts a difficulty in a slot (-1 leaves it out). A difficulty already there takes this one's
    /// old slot, or is left out when this one had none.
    /// </summary>
    internal void SetSlot(OszDifficulty d, int slot)
    {
        int old = SlotOf(d);
        if (slot == old) return;
        if (slot < 0)
        {
            Slots[old] = null;
            return;
        }
        var other = Slots[slot];
        Slots[slot] = d;
        if (old >= 0) Slots[old] = other;
    }
}

internal static class OszImport
{
    internal static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(30);
    internal const int MaxOsuFiles = 100;
    internal const long MaxOsuTextBytes = 64L * 1024 * 1024;
    internal const long MaxDirectoryBytes = 16L * 1024 * 1024;
    internal const int MaxNotes = 50_000;
    internal const int MaxRedLines = 2000;
    internal const long MinGuessedSongBytes = 100 * 1024;
    internal const int MaxBookmarks = 1000;
    /// <summary>A song file bigger than this that would unpack to more than <see cref="MaxSongPacking"/> times its packed size is refused.</summary>
    internal const long MinPackedSongBytes = 16L * 1024 * 1024;
    internal const int MaxSongPacking = 20;
    /// <summary>The longest song the import takes (an hour).</summary>
    internal const double MaxSongSeconds = 3600;
    /// <summary>The importer's version, kept in battle.json's "source".</summary>
    internal const int Version = 1;

    internal const string NotAZip = "it isn't a .osz file (it can't be opened as a zip).";
    private const string CantUnpack = "it can't be unpacked (it's damaged or packed in a way that can't be read)";

    private static readonly string[] SongExtensions = { ".mp3", ".ogg", ".wav", ".flac" };

    /// <summary>A file in the zip that the import may read: its place, the entry, and its name with '/'.</summary>
    private readonly record struct ZipFileEntry(int Index, ZipArchiveEntry Entry, string Name);

    /// <summary>
    /// Reads a .osz and works out the whole import. Never throws: a problem with the file is
    /// <see cref="OszPlan.Error"/> in plain words (and <see cref="OszPlan.Bug"/> keeps an
    /// exception nobody expected, for the log). Writes nothing.
    /// </summary>
    internal static OszPlan Read(string path, TimeSpan? limit = null)
    {
        var plan = new OszPlan { SourcePath = path, SourceName = Path.GetFileName(path) };
        var guard = new OszGuard(limit ?? TimeLimit);
        try
        {
            ReadInto(plan, guard);
        }
        catch (OszRefused ex)
        {
            plan.Error = ex.Message;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            plan.Error = "the file isn't there any more.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            plan.Error = $"it can't be read ({Shorten(ex.Message.Trim().TrimEnd('.'), 120)}).";
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            plan.Error = "it's damaged or packed in a way that can't be read.";
            plan.Detail = ex.Message;
        }
        catch (Exception ex)
        {
            plan.Error = "the importer ran into something it didn't expect (see the log).";
            plan.Bug = ex;
        }
        plan.ReadMs = guard.ElapsedMs;
        if (plan.Error != null)
        {
            plan.Four = plan.Five = null;
            plan.Song = null;
            plan.Card = null;
        }
        return plan;
    }

    private static void ReadInto(OszPlan plan, OszGuard guard)
    {
        var info = new FileInfo(plan.SourcePath);
        if (!info.Exists) throw new OszRefused("the file isn't there any more.");
        plan.SourceLength = info.Length;
        plan.SourceWriteUtc = info.LastWriteTimeUtc;
        if (info.Length > BattleFiles.MaxZipBytes)
            throw new OszRefused($"it's too big ({TooBig(info.Length, BattleFiles.MaxZipBytes)}).");
        using var stream = OpenRead(plan.SourcePath);
        using var zip = OpenZip(stream);
        ReadZip(plan, zip, guard);
    }

    /// <summary>The file, read-only, even while another program has it open.</summary>
    internal static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Opens a .osz as a zip after checking what it is: a lone .osu, something else, or a zip whose
    /// list of files is too long to read (see <see cref="CheckDirectory"/>).
    /// </summary>
    internal static ZipArchive OpenZip(FileStream stream)
    {
        var head = new byte[64];
        stream.Position = 0;
        int n = ReadFull(stream, head, head.Length);
        bool zipStart = n >= 4 && head[0] == 'P' && head[1] == 'K' && ((head[2] == 3 && head[3] == 4) || (head[2] == 5 && head[3] == 6));
        if (!zipStart)
        {
            if (LooksLikeOsu(head, n)) throw new OszRefused("that's a single .osu difficulty, not a whole beatmap. Choose the .osz file.");
            throw new OszRefused(NotAZip);
        }
        string? problem = CheckDirectory(stream, out long directoryBytes);
        if (problem != null) throw new OszRefused(problem);
        stream.Position = 0;
        var capped = new CappedStream(stream);
        ZipArchive? zip = null;
        try
        {
            zip = new ZipArchive(capped, ZipArchiveMode.Read, leaveOpen: true);
            // The list of files is read here, where a damaged one still means "not a zip". .NET
            // reads no more of it than the check above walked; if it ever did (a file changed in
            // between, or a way to the list the check doesn't know), the read stops there.
            capped.Budget = directoryBytes + 4096;
            _ = zip.Entries.Count;
            capped.Budget = long.MaxValue;
            return zip;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or NotSupportedException or OverflowException)
        {
            zip?.Dispose();
            throw new OszRefused(NotAZip);
        }
    }

    /// <summary>The .osz as the zip reader sees it, with a limit on how much a read may take for a while.</summary>
    private sealed class CappedStream : Stream
    {
        private readonly Stream inner;
        internal long Budget = long.MaxValue;

        internal CappedStream(Stream inner) => this.inner = inner;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            if ((Budget -= n) < 0) throw new InvalidDataException("the list of files is longer than the check found");
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static bool LooksLikeOsu(byte[] head, int n)
    {
        int p = 0;
        if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) p = 3;
        while (p < n && (head[p] == ' ' || head[p] == '\t' || head[p] == '\r' || head[p] == '\n')) p++;
        const string start = "osu file format";
        if (p + start.Length > n) return false;
        for (int i = 0; i < start.Length; i++)
            if (head[p + i] != start[i]) return false;
        return true;
    }

    /// <summary>
    /// .NET reads every record of a zip's central directory into memory when it opens the zip, and
    /// it reads records until one doesn't start like one, whatever count the zip gives. So a
    /// crafted file could make it take any amount. This walks the records the same way first,
    /// from every place .NET could start them, and stops past 4000 records or 16 MB. Returns why
    /// it's refused, or null.
    /// </summary>
    internal static string? CheckDirectory(Stream stream) => CheckDirectory(stream, out _);

    /// <param name="directoryBytes">The most any walk took, so the open can hold .NET to it.</param>
    internal static string? CheckDirectory(Stream stream, out long directoryBytes)
    {
        directoryBytes = 0;
        long length = stream.Length;
        int tail = (int)Math.Min(length, 22 + 65535);
        var buffer = new byte[tail];
        stream.Position = length - tail;
        if (ReadFull(stream, buffer, tail) < tail) return NotAZip;
        int eocd = -1;
        for (int i = tail - 22; i >= 0; i--)
            if (buffer[i] == 'P' && buffer[i + 1] == 'K' && buffer[i + 2] == 5 && buffer[i + 3] == 6) { eocd = i; break; }
        if (eocd < 0) return NotAZip;
        long eocdAt = length - tail + eocd;
        long entries = U16(buffer, eocd + 10);
        long size = U32(buffer, eocd + 12);
        var starts = new List<long> { U32(buffer, eocd + 16) };
        // Zip64: .NET goes to the Zip64 record when the End record's disk number, entry count or
        // offset is at its maximum, through a locator it looks for anywhere in the 32 bytes before
        // the End record's last 16 (so starting 48 to 20 bytes before it). Every locator there is
        // followed, whatever the End record says, so the walk below covers wherever .NET starts.
        var locator = new byte[20];
        var record = new byte[56];
        for (long at = eocdAt - 20; at >= Math.Max(0, eocdAt - 48); at--)
        {
            stream.Position = at;
            if (ReadFull(stream, locator, 20) < 20 || locator[0] != 'P' || locator[1] != 'K' || locator[2] != 6 || locator[3] != 7) continue;
            long recordAt = (long)Math.Min(U64(locator, 8), long.MaxValue);
            if (recordAt < 0 || recordAt > length - 56) continue;
            stream.Position = recordAt;
            if (ReadFull(stream, record, 56) < 56 || record[0] != 'P' || record[1] != 'K' || record[2] != 6 || record[3] != 6) continue;
            entries = Math.Max(entries, (long)Math.Min(U64(record, 32), long.MaxValue));
            size = Math.Max(size, (long)Math.Min(U64(record, 40), long.MaxValue));
            starts.Add((long)Math.Min(U64(record, 48), long.MaxValue));
        }
        if (entries > BattleFiles.MaxZipEntries) return TooManyFiles;
        if (size > MaxDirectoryBytes) return DirectoryTooBig;
        var header = new byte[46];
        foreach (long start in starts)
        {
            if (start < 0 || start >= length) continue;
            stream.Position = start;
            long walked = 0;
            int count = 0;
            while (ReadFull(stream, header, 46) == 46 && header[0] == 'P' && header[1] == 'K' && header[2] == 1 && header[3] == 2)
            {
                if (++count > BattleFiles.MaxZipEntries) return TooManyFiles;
                long bytes = 46L + U16(header, 28) + U16(header, 30) + U16(header, 32);
                walked += bytes;
                if (walked > MaxDirectoryBytes) return DirectoryTooBig;
                stream.Position += bytes - 46;
            }
            directoryBytes = Math.Max(directoryBytes, walked);
        }
        return null;
    }

    private static readonly string TooManyFiles = $"it has more than {BattleFiles.MaxZipEntries} files in it.";
    private static readonly string DirectoryTooBig = $"its list of files is too big to read (over {MaxDirectoryBytes / (1024 * 1024)} MB).";

    private static long U16(byte[] b, int at) => b[at] | (b[at + 1] << 8);
    private static long U32(byte[] b, int at) => (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
    private static ulong U64(byte[] b, int at) => (ulong)U32(b, at) | ((ulong)U32(b, at + 4) << 32);

    private static int ReadFull(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = stream.Read(buffer, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    // Only for matching and reading: nothing is written by these names. Folders named "..", rooted
    // names and drive letters are skipped (and listed), as are control characters.
    private static bool NameOk(string name)
    {
        if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains(':')) return false;
        foreach (char c in name)
            if (char.IsControl(c)) return false;
        foreach (var part in name.Split('/'))
            if (part.Length == 0 || part == "." || part == "..") return false;
        return true;
    }

    private static string Leaf(string name) => name.Substring(name.LastIndexOf('/') + 1);

    private static void ReadZip(OszPlan plan, ZipArchive zip, OszGuard guard)
    {
        // ---- the files -------------------------------------------------------------------------
        var files = new List<ZipFileEntry>();
        long total = 0;
        int unsafeNames = 0, index = -1;
        string? unsafeExample = null;
        bool battleJson = false;
        foreach (var entry in zip.Entries)
        {
            index++;
            guard.Step();
            string name = entry.FullName.Replace('\\', '/');
            if (name.Length == 0 || name.EndsWith("/", StringComparison.Ordinal)) continue;
            if (name.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!NameOk(name))
            {
                unsafeNames++;
                unsafeExample ??= name;
                continue;
            }
            if (entry.Length < 0 || (total += entry.Length) > BattleFiles.MaxZipBytes)
                throw new OszRefused($"it unpacks to more than {BattleFiles.MaxZipBytes / (1024 * 1024)} MB.");
            files.Add(new ZipFileEntry(index, entry, name));
            if (Leaf(name).Equals(BattlePackage.ManifestName, StringComparison.OrdinalIgnoreCase) && name.Count(c => c == '/') <= 1) battleJson = true;
        }
        var osus = files.Where(f => f.Name.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)).ToList();
        if (osus.Count == 0)
            throw new OszRefused(battleJson
                ? "it's a Nocturne But Better battle. Use \"Import a .nbbbattle file...\" for it."
                : "there are no difficulties (.osu files) in it.");

        // ---- the difficulties ------------------------------------------------------------------
        long text = 0;
        foreach (var f in osus)
        {
            guard.Step();
            if (plan.Difficulties.Count >= MaxOsuFiles) { plan.SkippedOsuFiles++; continue; }
            var d = new OszDifficulty { Entry = f.Name, Order = plan.Difficulties.Count, Name = Path.GetFileNameWithoutExtension(Leaf(f.Name)) };
            plan.Difficulties.Add(d);
            if (f.Entry.Length > BattlePackage.MaxChartBytes)
            {
                d.Unusable = $"the file is too big ({TooBig(f.Entry.Length, BattlePackage.MaxChartBytes)})";
                continue;
            }
            if (text + f.Entry.Length > MaxOsuTextBytes)
            {
                d.Unusable = $"not read (the beatmap's difficulties come to more than {MaxOsuTextBytes / (1024 * 1024)} MB)";
                continue;
            }
            text += f.Entry.Length;
            string content;
            try { content = BattleFiles.ReadEntryText(f.Entry, BattlePackage.MaxChartBytes); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                d.Unusable = CantUnpack;
                continue;
            }
            var file = OsuFile.Parse(content, f.Name, guard);
            d.File = file;
            d.Keys = file.Keys;
            d.Mode = file.Mode;
            if (file.Version.Trim().Length > 0) d.Name = file.Version.Trim();
            d.Unusable = Unusable(file);
        }
        var usable = plan.Difficulties.Where(d => d.Unusable == null).ToList();
        if (usable.Count == 0) throw new OszRefused(NoneUsable(plan));

        // ---- the song: the one most difficulties use -------------------------------------------
        var bySong = usable.GroupBy(d => d.File!.AudioFilename, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Sum(d => d.File!.Objects.Count)).ToList();
        string named = bySong[0].Key;
        foreach (var other in bySong.Skip(1))
            foreach (var d in other) d.Unusable = $"uses another song file ({Shorten(Leaf(other.Key), 60)})";
        usable = bySong[0].ToList();

        var songEntry = FindSong(files, named, out bool guessed)
            ?? throw new OszRefused(named.Length == 0 ? "it doesn't name its song file." : $"the song file it names ({Shorten(Leaf(named), 60)}) isn't in it.");
        string songLeaf = Shorten(Leaf(songEntry.Name), 60);
        if (songEntry.Entry.Length > BattlePackage.MaxAudioBytes)
            throw new OszRefused($"its song file is too big ({TooBig(songEntry.Entry.Length, BattlePackage.MaxAudioBytes)}).");
        // Songs hardly pack down (MP3, OGG and FLAC not at all, WAV a little). One that would unpack
        // to many times what it takes in the .osz is a zip bomb, however small the file.
        if (songEntry.Entry.Length > MinPackedSongBytes && songEntry.Entry.Length > MaxSongPacking * songEntry.Entry.CompressedLength)
            throw new OszRefused($"its song ({songLeaf}) is packed in a way no real song is (it would unpack to over {MaxSongPacking} times its size).");
        byte[] bytes;
        try { bytes = ReadEntryBytes(songEntry.Entry); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new OszRefused($"its song ({songLeaf}) can't be unpacked (it's damaged or packed in a way that can't be read).");
        }
        guard.Check();
        // A song over an hour is refused before it's decoded, where its header says how long it is.
        if (SongSecondsFromHeader(bytes) is double claimed && claimed > MaxSongSeconds)
            throw new OszRefused(TooLong(songLeaf, claimed));
        var song = new OszSong
        {
            EntryIndex = songEntry.Index,
            EntryName = songEntry.Entry.FullName,
            Length = songEntry.Entry.Length,
            Named = named,
            Guessed = guessed,
            FileName = "audio/" + BattleFileName(Leaf(songEntry.Name), "song"),
        };
        song.Shift = OsuAudio.Shift(bytes, out song.ShiftHow);
        try
        {
            var (stereo, rate) = AudioFile.Decode(bytes, Leaf(songEntry.Name));
            song.Seconds = rate > 0 ? stereo.Length / 2.0 / rate : 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            plan.Detail = ex.Message;
            throw new OszRefused($"its song ({songLeaf}) can't be played{SongProblem(ex, Leaf(songEntry.Name), AudioFile.IsMp3(bytes))}.");
        }
        bytes = Array.Empty<byte>();
        guard.Check();
        if (!(song.Seconds > 0)) throw new OszRefused($"its song ({songLeaf}) can't be played (it has no sound in it).");
        if (song.Seconds > MaxSongSeconds) throw new OszRefused(TooLong(songLeaf, song.Seconds));
        plan.Song = song;

        // ---- the lane groups -------------------------------------------------------------------
        plan.Four = LaneGroup(usable.Where(d => d.Keys == 4).ToList(), 4, song, guard);
        plan.Five = LaneGroup(usable.Where(d => d.Keys == 5).ToList(), 5, song, guard);
        if (plan.Four == null && plan.Five == null) throw new OszRefused(NoneUsable(plan));
        plan.DefaultLanes = plan.Four == null ? 5 : plan.Five == null ? 4 : plan.Five.Usable.Count > plan.Four.Usable.Count ? 5 : 4;
        var main = plan.Group(plan.DefaultLanes)!;

        // ---- the title, artist and mapper ------------------------------------------------------
        var from = main.TimingFrom.File!;
        plan.Title = Cut(FirstText(from.Title, from.TitleUnicode, TitleFromFileName(plan.SourceName)), 200);
        plan.Artist = Cut(FirstText(from.Artist, from.ArtistUnicode), 200);
        plan.Mapper = Cut(from.Creator, 100);
        plan.BeatmapSetId = Math.Max(0, from.BeatmapSetId);

        // ---- the card --------------------------------------------------------------------------
        var withSong = usable.OrderByDescending(d => d == main.TimingFrom).ThenBy(d => d.Order).ToList();
        string background = withSong.Select(d => d.File!.Background).FirstOrDefault(b => b.Length > 0) ?? "";
        if (background.Length > 0) ReadCard(plan, files, background);

        // ---- what a battle doesn't use ---------------------------------------------------------
        Describe(plan, files, withSong, songEntry, unsafeNames, unsafeExample);
    }

    /// <summary>Why a difficulty can't be in a battle, or null.</summary>
    internal static string? Unusable(OsuFile f)
    {
        if (!f.HasHeader && f.KnownSections == 0) return "it isn't an osu! difficulty file";
        if (f.Mode != 3) return OszConvert.ModeName(f.Mode) + ", not osu!mania";
        if (f.Keys == 0) return "it doesn't give a key count";
        if (f.Keys != 4 && f.Keys != 5) return $"{f.Keys}K; a battle has 4 or 5 lanes";
        int reds = f.RedLines;
        if (reds == 0) return "no tempo line (red line), so its notes can't be put on beats";
        if (f.Objects.Count == 0) return "no notes";
        if (f.Objects.Count > MaxNotes || f.ObjectsPastLimit > 0) return $"more than {OszConvert.N(MaxNotes)} notes";
        if (reds > MaxRedLines) return $"more than {OszConvert.N(MaxRedLines)} tempo lines";
        return null;
    }

    // The whole import's refusal when no difficulty can be used.
    private static string NoneUsable(OszPlan plan)
    {
        var read = plan.Difficulties.Where(d => d.File != null && (d.File.HasHeader || d.File.KnownSections > 0)).ToList();
        if (read.Count > 0 && read.All(d => d.Mode != 3))
        {
            string modes = Join(read.Select(d => OszConvert.ModeName(d.Mode)).Distinct().ToList());
            return $"it has no osu!mania difficulties ({(read.Count == 1 ? "its difficulty is" : "they're")} {modes}). Only osu!mania beatmaps can be imported.";
        }
        var mania = read.Where(d => d.Mode == 3).ToList();
        if (mania.Count > 0 && mania.All(d => d.Keys != 0 && d.Keys != 4 && d.Keys != 5))
        {
            string keys = Join(mania.Select(d => d.Keys).Distinct().OrderBy(k => k).Select(k => k + "K").ToList());
            return mania.Count == 1
                ? $"its osu!mania difficulty is {keys}. A battle has 4 or 5 lanes, so it can't be used."
                : $"its osu!mania difficulties are {keys}. A battle has 4 or 5 lanes, so none of them can be used.";
        }
        var first = plan.Difficulties.FirstOrDefault(d => d.Unusable != null);
        string why = first == null ? "" : $" ({OszConvert.Quote(first.Name)}: {first.Unusable})";
        return plan.Difficulties.Count == 1 ? $"its only difficulty can't be used{why}." : $"none of its difficulties can be used{why}.";
    }

    private static string Join(List<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    // The song: the same path in any letter case, else the only file with its name in any folder,
    // else the set's only song file (a name stored in another code page won't match).
    private static ZipFileEntry? FindSong(List<ZipFileEntry> files, string named, out bool guessed)
    {
        guessed = false;
        var found = FindFile(files, named);
        if (found != null) return found;
        var songs = files.Where(f =>
        {
            string leaf = Leaf(f.Name);
            return SongExtensions.Any(e => leaf.EndsWith(e, StringComparison.OrdinalIgnoreCase)) && f.Entry.Length >= MinGuessedSongBytes
                && !leaf.StartsWith("normal-", StringComparison.OrdinalIgnoreCase) && !leaf.StartsWith("soft-", StringComparison.OrdinalIgnoreCase)
                && !leaf.StartsWith("drum-", StringComparison.OrdinalIgnoreCase);
        }).ToList();
        if (songs.Count != 1) return null;
        guessed = true;
        return songs[0];
    }

    // A file a .osu names: osu! paths are relative to the set and ignore letter case.
    private static ZipFileEntry? FindFile(List<ZipFileEntry> files, string reference)
    {
        string wanted = OsuFile.CleanPath(reference).TrimStart('/');
        if (wanted.Length == 0) return null;
        foreach (var f in files)
            if (f.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return f;
        string leaf = Leaf(wanted);
        ZipFileEntry? only = null;
        foreach (var f in files)
        {
            if (!Leaf(f.Name).Equals(leaf, StringComparison.OrdinalIgnoreCase)) continue;
            if (only != null) return null;
            only = f;
        }
        return only;
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        // Room for what the zip says, up to 64 MB at first: an entry can say more than it holds.
        using var copy = new MemoryStream((int)Math.Min(entry.Length, 64L * 1024 * 1024));
        BattleFiles.CopyAtMost(input, copy, entry.Length, entry.FullName);
        return copy.Length == copy.Capacity ? copy.GetBuffer() : copy.ToArray();
    }

    /// <summary>
    /// How long a song says it is, without decoding it: a WAV's data size (a WAV decodes to up to
    /// four times its size), or an MP3's frame count when it has a Xing, Info or VBRI header. Null
    /// when it doesn't say; the length after decoding is checked too.
    /// </summary>
    internal static double? SongSecondsFromHeader(byte[] b)
    {
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'A')
        {
            var (fmt, fmtSize) = WavFile.Chunk(b, "fmt ");
            var (_, dataSize) = WavFile.Chunk(b, "data");
            if (fmt < 0 || fmtSize < 16) return null;
            long channels = U16(b, fmt + 2), rate = U32(b, fmt + 4), bits = U16(b, fmt + 14);
            long frameBytes = channels * (bits / 8);
            return frameBytes > 0 && rate > 0 ? dataSize / frameBytes / (double)rate : null;
        }
        if (AudioFile.IsMp3(b) && Mp3Info.Parse(b) is { Frames: > 0, SampleRate: > 0 } mp3)
            return mp3.Frames * (double)mp3.SamplesPerFrame / mp3.SampleRate;
        return null;
    }

    private static string TooLong(string songLeaf, double seconds) =>
        $"its song ({songLeaf}) is {OszConvert.Clock(seconds)} long; a battle's song can be at most {MaxSongSeconds / 60:0} minutes.";

    /// <summary>
    /// Why the decoder can't play the song, to go after "can't be played": what Windows is
    /// missing, else the first part of the decoder's own reason in brackets. Nothing when that
    /// reason is .NET's own.
    /// </summary>
    internal static string SongProblem(Exception ex, string fileName, bool mp3)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e.Message == MediaFoundationAudio.MissingMessage)
                return mp3
                    ? ": this copy of Windows has no MP3 decoder (Windows N needs the Media Feature Pack)"
                    : ": this copy of Windows can't decode it (Windows N needs the Media Feature Pack)";
        string reason = ex.Message;
        if (reason.StartsWith(fileName + ": ", StringComparison.Ordinal)) reason = reason.Substring(fileName.Length + 2);
        foreach (string end in new[] { " (", ";" })
        {
            int cut = reason.IndexOf(end, StringComparison.Ordinal);
            if (cut >= 0) reason = reason.Substring(0, cut);
        }
        reason = reason.Trim().TrimEnd('.');
        // "song.ogg couldn't be decoded (...)" is the decoder's own trouble, and says nothing new.
        if (reason.Length == 0 || reason.StartsWith(fileName, StringComparison.Ordinal) || reason.Contains("Exception") || reason.Contains("System.")) return "";
        if (reason.StartsWith("isn't ", StringComparison.Ordinal) || reason.StartsWith("is ", StringComparison.Ordinal) || reason.StartsWith("has ", StringComparison.Ordinal))
            reason = "it " + reason;
        return $" ({Shorten(reason, 100)})";
    }

    /// <summary>A name for a copied file in the battle: safe on Windows and in an .sm tag.</summary>
    private static string BattleFileName(string leaf, string fallback)
    {
        string ext = Path.GetExtension(leaf).ToLowerInvariant();
        string stem = Path.GetFileNameWithoutExtension(leaf);
        // A name stored in another code page reads as U+FFFD characters; the file gets a plain name instead.
        if (stem.Trim().Length == 0 || leaf.Contains('\uFFFD')) return BattleFiles.SafeFileName(fallback + ext);
        return BattleFiles.SafeFileName(leaf);
    }

    // ---- a lane group ----------------------------------------------------------------------------

    private static OszLaneGroup? LaneGroup(List<OszDifficulty> diffs, int lanes, OszSong song, OszGuard guard)
    {
        double shiftMs = song.Shift * 1000, endMs = (song.Seconds + song.Shift) * 1000;
        // Only notes the song plays over count for the timing: from its start to its end.
        double first = double.PositiveInfinity, last = double.NegativeInfinity;
        var times = new List<double>();
        foreach (var d in diffs.ToList())
        {
            double dFirst = double.PositiveInfinity, dLast = double.NegativeInfinity;
            int before = times.Count;
            foreach (var o in d.File!.Objects)
            {
                guard.Step();
                if (o.Time < shiftMs - 0.5 || o.Time >= endMs) continue;
                dFirst = Math.Min(dFirst, o.Time);
                dLast = Math.Max(dLast, o.IsHold ? Math.Min(o.EndTime, endMs) : o.Time);
                times.Add(o.Time);
                if (o.IsHold && o.EndTime < endMs) times.Add(o.EndTime);
            }
            if (double.IsPositiveInfinity(dFirst))
            {
                d.Unusable = "all its notes are before the song starts or after it ends";
                diffs.Remove(d);
                times.RemoveRange(before, times.Count - before);
                continue;
            }
            first = Math.Min(first, dFirst);
            last = Math.Max(last, dLast);
        }
        if (diffs.Count == 0) return null;
        // Every time a note starts or a hold ends, once: where a filler beat goes depends on them.
        times.Sort();
        var noteMs = new List<double>(times.Count);
        foreach (double t in times)
            if (noteMs.Count == 0 || noteMs[^1] != t) noteMs.Add(t);

        var group = new OszLaneGroup { Lanes = lanes };
        diffs.Sort((a, b) => a.File!.Objects.Count != b.File!.Objects.Count ? a.File.Objects.Count.CompareTo(b.File.Objects.Count)
            : a.OverallDifficulty != b.OverallDifficulty ? a.OverallDifficulty.CompareTo(b.OverallDifficulty) : a.Order.CompareTo(b.Order));
        group.TimingFrom = diffs[^1];
        group.Timing = OszConvert.Timing(group.TimingFrom.File!, first, last, song.Shift, noteMs.ToArray(), guard);
        var clock = group.Timing.Clock;

        int firstRow = int.MaxValue, lastRow = -1;
        foreach (var d in diffs)
        {
            var chart = OszConvert.Notes(d.File!, lanes, group.Timing, song.Shift, song.Seconds, guard);
            if (chart.Notes.Count == 0)
            {
                d.Unusable = "all its notes are before the song starts or after it ends";
                continue;
            }
            group.Charts[d] = chart;
            group.Usable.Add(d);
            firstRow = Math.Min(firstRow, chart.FirstRow);
            lastRow = Math.Max(lastRow, chart.LastRow);
        }
        if (group.Usable.Count == 0) return null;
        if (!group.Charts.ContainsKey(group.TimingFrom)) group.TimingFrom = group.Usable[^1];

        // Slots by note density (the same span for every difficulty, so harder charts have more notes).
        double span = Math.Max(1, clock.RowToSeconds(lastRow) - clock.RowToSeconds(firstRow));
        foreach (var d in group.Usable) group.Charts[d].Density = group.Charts[d].Notes.Count / span;
        group.Usable.Sort((a, b) => group.Charts[a].Notes.Count != group.Charts[b].Notes.Count
            ? group.Charts[a].Notes.Count.CompareTo(group.Charts[b].Notes.Count)
            : a.OverallDifficulty != b.OverallDifficulty ? a.OverallDifficulty.CompareTo(b.OverallDifficulty) : a.Order.CompareTo(b.Order));
        var slots = OszConvert.AssignSlots(group.Usable.Select(d => OszConvert.WantedSlot(group.Charts[d].Density, lanes)).ToArray());
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] >= 0) group.DefaultSlots[slots[i]] = group.Usable[i];

        // Speed changes: each difficulty's own, and the fillers' alone for "none".
        foreach (var d in group.Usable) group.Speeds[d] = OszConvert.Speeds(d.File!, group.Timing, song.Shift, last, guard);
        group.BaseScrolls = OszConvert.Speeds(null, group.Timing, song.Shift, last, guard);
        // At first: the included difficulty with the most notes that has any.
        for (int i = group.Usable.Count - 1; i >= 0 && group.DefaultSpeedsFrom == null; i--)
        {
            var d = group.Usable[i];
            if (Array.IndexOf(group.DefaultSlots, d) >= 0 && group.HasSpeedChanges(d)) group.DefaultSpeedsFrom = d;
        }

        // The timing difficulty's bookmarks and preview time, in the song's seconds.
        var from = group.TimingFrom.File!;
        foreach (double ms in from.Bookmarks)
        {
            double t = Math.Round(ms / 1000 - song.Shift, 3);
            if (t >= 0 && t < song.Seconds && !group.Bookmarks.Contains(t) && group.Bookmarks.Count < MaxBookmarks) group.Bookmarks.Add(t);
        }
        group.Bookmarks.Sort();
        group.PreviewStart = from.PreviewTime >= 0
            ? Math.Round(Math.Clamp(from.PreviewTime / 1000 - song.Shift, 0, Math.Max(0, song.Seconds - 1)), 3)
            : Math.Round(song.Seconds * 0.4, 3);   // osu!'s own rule
        return group;
    }

    // ---- the card ------------------------------------------------------------------------------

    private static void ReadCard(OszPlan plan, List<ZipFileEntry> files, string background)
    {
        string named = Shorten(Leaf(OsuFile.CleanPath(background)), 60);
        var found = FindFile(files, background);
        if (found == null)
        {
            plan.CardProblem = $"The background {named} isn't in the .osz, so the battle has no card.";
            return;
        }
        var entry = found.Value;
        string leaf = Leaf(entry.Name), shown = Shorten(leaf, 60);
        string ext = Path.GetExtension(leaf).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg"))
        {
            plan.CardProblem = $"The background {shown} isn't a PNG or JPEG, so the battle has no card.";
            return;
        }
        if (entry.Entry.Length > BattlePackage.MaxImageBytes)
        {
            plan.CardProblem = $"{shown} is too big for a card ({TooBig(entry.Entry.Length, BattlePackage.MaxImageBytes)}).";
            return;
        }
        var head = new byte[8];
        int n;
        try
        {
            using var s = entry.Entry.Open();
            n = ReadFull(s, head, head.Length);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            plan.CardProblem = $"The background {shown} can't be unpacked, so the battle has no card.";
            return;
        }
        bool png = n >= 8 && head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G' && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A;
        bool jpeg = n >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
        if (!png && !jpeg)
        {
            plan.CardProblem = $"The background {shown} isn't a PNG or JPEG, so the battle has no card.";
            return;
        }
        plan.Card = new OszCard
        {
            EntryIndex = entry.Index,
            EntryName = entry.Entry.FullName,
            Length = entry.Entry.Length,
            FileName = "images/" + BattleFileName(leaf, "card"),
        };
    }

    // ---- what the battle leaves out ----------------------------------------------------------------

    private static void Describe(OszPlan plan, List<ZipFileEntry> files, List<OszDifficulty> song, ZipFileEntry songEntry, int unsafeNames, string? unsafeExample)
    {
        var items = plan.Details;
        string video = song.Select(d => d.File!.Video).FirstOrDefault(v => v.Length > 0) ?? "";
        if (video.Length > 0) items.Add($"The background video ({Shorten(Leaf(video), 60)}): battles don't play videos.");
        // Sound files other than the songs the difficulties name are hit sounds.
        var songs = new HashSet<int> { songEntry.Index };
        foreach (var name in plan.Difficulties.Where(d => d.File != null).Select(d => d.File!.AudioFilename).Distinct(StringComparer.OrdinalIgnoreCase))
            if (FindFile(files, name) is { } named) songs.Add(named.Index);
        int sounds = files.Count(f => !songs.Contains(f.Index) && SongExtensions.Any(e => f.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
        if (sounds > 0) items.Add($"Hit sounds ({OszConvert.Count(sounds, "file", "files")}): battles don't use osu! hit sounds.");
        if (song.Any(d => d.File!.HasStoryboard) || files.Any(f => f.Name.EndsWith(".osb", StringComparison.OrdinalIgnoreCase)))
            items.Add("The storyboard.");
        var extras = new List<string>();
        if (song.Any(d => d.File!.HasBreaks)) extras.Add("break times");
        if (song.Any(d => d.File!.HasKiai)) extras.Add("kiai");
        if (song.Any(d => d.File!.HasVolumeChanges)) extras.Add("volume changes");
        if (extras.Count > 0)
        {
            string joined = Join(extras);
            items.Add(char.ToUpperInvariant(joined[0]) + joined.Substring(1) + ".");
        }
        double od = song[0].File!.OverallDifficulty;
        items.Add(double.IsNaN(od)
            ? "osu!'s judgement and HP drain: the game's own timing windows and health apply."
            : $"osu!'s judgement (OD {od.ToString("0.#", CultureInfo.InvariantCulture)}) and HP drain: the game's own timing windows and health apply.");
        if (unsafeNames > 0)
        {
            string example = Shorten(unsafeExample ?? "", 60);
            items.Add(unsafeNames == 1
                ? $"1 file with an unsafe name was skipped ({example})."
                : $"{OszConvert.N(unsafeNames)} files with unsafe names were skipped (like {example}).");
        }
        if (plan.SkippedOsuFiles > 0)
            items.Add($"Only the first {MaxOsuFiles} difficulties (.osu files) were read; {OszConvert.Count(plan.SkippedOsuFiles, "more was", "more were")} left out.");
        foreach (var d in song.Where(d => !d.File!.HasHeader))
            items.Add($"{OszConvert.Quote(d.Name)} has no \"osu file format\" line, so it was read as the current format.");
        if (plan.CardProblem != null) items.Add(plan.CardProblem);
        if (plan.Song!.Guessed)
            items.Add($"The song's name in the .osz didn't match \"{Shorten(Leaf(plan.Song.Named), 60)}\", so its only song file was used.");
    }

    // ---- text ----------------------------------------------------------------------------------

    /// <summary>"9 MB; at most 8 MB": the size in whole MB, or to a tenth when that wouldn't show it's over.</summary>
    private static string TooBig(long bytes, long limit)
    {
        double mb = bytes / (1024.0 * 1024.0);
        long whole = (long)Math.Round(mb), most = limit / (1024 * 1024);
        string size = whole > most ? whole.ToString(CultureInfo.InvariantCulture) : (Math.Ceiling(mb * 10) / 10).ToString("0.0", CultureInfo.InvariantCulture);
        return $"{size} MB; at most {most} MB";
    }

    private static string FirstText(params string[] texts)
    {
        foreach (var t in texts)
        {
            string clean = BattleDraft.CleanLine(t);
            if (clean.Length > 0) return clean;
        }
        return "";
    }

    private static string Cut(string text, int max) => text.Length > max ? text.Substring(0, max).TrimEnd() : text;

    /// <summary>Text from the beatmap for a message: control characters out, and cut short with "..." when long.</summary>
    internal static string Shorten(string text, int max)
    {
        var sb = new StringBuilder(Math.Min(text.Length, max + 3));
        foreach (char c in text)
        {
            if (sb.Length >= max) { sb.Append("..."); break; }
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString();
    }

    // "123456 Artist - Title.osz" (a set number first, as osu! names its downloads) -> "Artist - Title".
    private static string TitleFromFileName(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName).Trim();
        int i = 0;
        while (i < name.Length && char.IsDigit(name[i])) i++;
        if (i > 0 && i < name.Length && name[i] == ' ') name = name.Substring(i).Trim();
        return name.Length > 0 ? name : "osu!mania beatmap";
    }
}
