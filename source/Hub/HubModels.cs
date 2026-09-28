using System.Text.Json;
using System.Text.Json.Serialization;

namespace NocturnePlus;

/// <summary>
/// What the hub's API sends and takes (server/API.md), read and written with System.Text.Json.
/// Fields the mod doesn't know are ignored, so the hub can add some within v1. Every text that
/// comes from the hub goes through <see cref="HubCard.Clean"/> (the text rules) before anything
/// shows it, and anything whose id or version isn't well formed is dropped.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class HubJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed class HubInfo
{
    public int Api { get; set; }
    public string Hub { get; set; } = "";
    public string MinClient { get; set; } = "";
    public bool UploadsOpen { get; set; }
    public bool Counts { get; set; }
    public long MaxPackageBytes { get; set; } = 100L * 1024 * 1024;
    public long MaxUnpackedBytes { get; set; } = 200L * 1024 * 1024;
    public long PartSize { get; set; } = 8L * 1024 * 1024;
    public int MaxEntries { get; set; } = 1000;
    public int MaxSongsPerPack { get; set; } = 40;
    public int MaxThumbB64 { get; set; } = 16384;
    public HubTextLimits Text { get; set; } = new();
    public string Message { get; set; } = "";
    public string TakedownContact { get; set; } = "";
    public string LegalUrl { get; set; } = "";
    public long Time { get; set; }

    /// <summary>Cleans the texts and keeps the limits within what the mod itself takes.</summary>
    internal HubInfo Clean()
    {
        Hub = HubText.CleanLine(Hub, 100) ?? "";
        Message = HubText.CleanText(Message, 500) ?? "";
        TakedownContact = HubText.CleanLine(TakedownContact, 200) ?? "";
        LegalUrl = HubText.CleanLine(LegalUrl, 200) ?? "";
        MinClient = HubText.CleanLine(MinClient, 20) ?? "";
        MaxPackageBytes = Math.Clamp(MaxPackageBytes, 0, BattleFiles.MaxZipBytes);
        MaxUnpackedBytes = Math.Clamp(MaxUnpackedBytes, 0, BattleFiles.MaxZipBytes);
        MaxEntries = Math.Clamp(MaxEntries, 0, BattleFiles.MaxZipEntries);
        MaxSongsPerPack = Math.Clamp(MaxSongsPerPack, 0, 1000);
        Text ??= new HubTextLimits();
        return this;
    }

    /// <summary>Whether this mod's version is too old for the hub (Browse and Upload then stay off).</summary>
    internal bool TooOld(string modVersion) => CompareVersions(modVersion, MinClient) < 0;

    /// <summary>Compares "2.8.0"-style versions number by number; a part that isn't a number counts as 0.</summary>
    internal static int CompareVersions(string a, string b)
    {
        var x = (a ?? "").Split('.');
        var y = (b ?? "").Split('.');
        for (int i = 0; i < 3; i++)
        {
            int p = i < x.Length && int.TryParse(x[i], out int px) ? px : 0;
            int q = i < y.Length && int.TryParse(y[i], out int qy) ? qy : 0;
            if (p != q) return p.CompareTo(q);
        }
        return 0;
    }
}

internal sealed class HubTextLimits
{
    public int Title { get; set; } = HubText.Title;
    public int Artist { get; set; } = HubText.Artist;
    public int Author { get; set; } = HubText.Author;
    public int PackTitle { get; set; } = HubText.PackTitle;
    public int Description { get; set; } = HubText.Description;
    public int Name { get; set; } = HubText.Name;
    public int Note { get; set; } = HubText.Note;
}

internal sealed class HubUploaderRef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Tag { get; set; } = "";
}

internal sealed class HubDifficulty
{
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int Notes { get; set; }
}

internal sealed class HubSongDifficulties
{
    public string Song { get; set; } = "";
    public List<HubDifficulty> Difficulties { get; set; } = new();
}

internal sealed class HubFlags
{
    public bool Gear { get; set; }
    public bool Level { get; set; }
    public bool Dialogue { get; set; }
    public bool Video { get; set; }
}

internal sealed class HubSource
{
    public string Kind { get; set; } = "";
    public string Mapper { get; set; } = "";
}

/// <summary>A list row: one entry on the hub (API.md "Card").</summary>
internal class HubCard
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Version { get; set; }
    public string Status { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Author { get; set; } = "";
    public HubUploaderRef Uploader { get; set; } = new();
    public int Lanes { get; set; }
    public List<HubDifficulty> Difficulties { get; set; } = new();
    public List<HubSongDifficulties>? Songs { get; set; }
    public string? BattleId { get; set; }
    public long Size { get; set; }
    public long? Downloads { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public double? LengthSeconds { get; set; }
    public double[]? Bpm { get; set; }
    public HubFlags Flags { get; set; } = new();
    public HubSource? Source { get; set; }
    public int Format { get; set; }
    public List<string> Requires { get; set; } = new();
    public string? Thumb { get; set; }

    internal bool IsBattle => Kind == "battle";

    /// <summary>
    /// Cleans every text with the hub's rules and checks the ids, kind, lanes and version. False
    /// means the entry is malformed and is ignored (never shown, downloaded or used in a path).
    /// </summary>
    internal virtual bool Clean()
    {
        if (!HubText.IsPackageId(Id) || Version < 1 || (Kind != "battle" && Kind != "charts")) return false;
        if (Lanes != 4 && Lanes != 5) return false;
        Title = HubText.CleanLine(Title, HubText.Title) ?? "";
        Artist = HubText.CleanLine(Artist, HubText.Artist) ?? "";
        Author = HubText.CleanLine(Author, HubText.Author) ?? "";
        Uploader ??= new HubUploaderRef();
        if (!HubText.IsUploaderId(Uploader.Id)) Uploader.Id = "";
        Uploader.Name = HubText.CleanLine(Uploader.Name, HubText.Name) ?? "";
        Uploader.Tag = HubText.CleanLine(Uploader.Tag, 4) ?? "";
        Difficulties = CleanDifficulties(Difficulties);
        if (Songs != null)
            Songs = Songs.Where(s => s != null).Take(1000).Select(s => new HubSongDifficulties
            {
                Song = HubText.CleanLine(s.Song, HubText.Song) ?? "",
                Difficulties = CleanDifficulties(s.Difficulties),
            }).ToList();
        BattleId = BattleId?.Trim().ToLowerInvariant();
        if (BattleId != null && !HubText.IsBattleId(BattleId)) BattleId = null;
        if (IsBattle && BattleId == null) return false;
        Size = Math.Max(0, Size);
        if (LengthSeconds is double length && !(length > 0 && double.IsFinite(length))) LengthSeconds = null;
        if (Bpm != null && (Bpm.Length != 2 || Bpm.Any(b => !double.IsFinite(b)))) Bpm = null;
        Flags ??= new HubFlags();
        if (Source != null)
        {
            Source.Kind = HubText.CleanLine(Source.Kind, 32) ?? "";
            Source.Mapper = HubText.CleanLine(Source.Mapper, HubText.Author) ?? "";
            if (Source.Kind.Length == 0) Source = null;
        }
        Requires = (Requires ?? new List<string>()).Where(r => r != null).Select(r => HubText.CleanLine(r, 32) ?? "").Take(16).ToList();
        if (Thumb != null && Thumb.Length > 16384) Thumb = null;
        return true;
    }

    private static List<HubDifficulty> CleanDifficulties(List<HubDifficulty>? list) =>
        (list ?? new List<HubDifficulty>()).Where(d => d != null).Take(64).Select(d => new HubDifficulty
        {
            Name = HubText.CleanLine(d.Name, HubText.Difficulty) ?? "",
            Level = Math.Clamp(d.Level, 0, 99),
            Notes = Math.Max(0, d.Notes),
        }).ToList();

    /// <summary>The "by Name #TAG" part of a row.</summary>
    internal string UploaderLabel => Uploader.Tag.Length > 0 ? $"{Uploader.Name} #{Uploader.Tag}" : Uploader.Name;
}

internal sealed class HubContents
{
    public int Files { get; set; }
    public long Unpacked { get; set; }
    public int Songs { get; set; }
    public int Charts { get; set; }
    public int Pictures { get; set; }
    public int Videos { get; set; }
    public int Json { get; set; }
    public int Other { get; set; }
    public int MediaChecked { get; set; }
    public int MediaTotal { get; set; }
}

internal sealed class HubFileFacts
{
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}

/// <summary>An entry's detail panel: a card with its description, contents and file facts.</summary>
internal sealed class HubDetail : HubCard
{
    public string Description { get; set; } = "";
    public HubContents Contents { get; set; } = new();
    public HubFileFacts File { get; set; } = new();

    internal override bool Clean()
    {
        if (!base.Clean()) return false;
        Description = HubText.CleanText(Description, HubText.Description) ?? "";
        Contents ??= new HubContents();
        File ??= new HubFileFacts();
        File.Sha256 = (File.Sha256 ?? "").ToLowerInvariant();
        File.Fingerprint = (File.Fingerprint ?? "").ToLowerInvariant();
        return HubText.IsHex64(File.Sha256) && HubText.IsHex64(File.Fingerprint) && File.Size > 0;
    }
}

/// <summary>One of the player's own entries (GET /v1/me/packages), whatever its status.</summary>
internal sealed class HubMyCard : HubCard
{
    public string Description { get; set; } = "";
    public string? RemovedReason { get; set; }
    public string? RemovedNote { get; set; }
    public long? RemovedAt { get; set; }
    public string PictureState { get; set; } = "";

    internal override bool Clean()
    {
        if (!base.Clean()) return false;
        Description = HubText.CleanText(Description, HubText.Description) ?? "";
        RemovedReason = HubText.CleanLine(RemovedReason, 32);
        RemovedNote = HubText.CleanText(RemovedNote, 1000);
        PictureState = HubText.CleanLine(PictureState, 16) ?? "";
        return true;
    }

    /// <summary>What My uploads says about the entry's status, in plain words.</summary>
    internal string StatusWords => Status switch
    {
        "live" => "On the hub",
        "hidden" => "Under review by the hub's owner",
        "removed" => "Removed by the hub's owner: " + HubText.ReasonWords(RemovedReason) + "." + (string.IsNullOrEmpty(RemovedNote) ? "" : " " + RemovedNote),
        "deleted" => "You deleted it from the hub",
        _ => Status,
    };
}

internal sealed class HubListPage
{
    public List<HubCard> Items { get; set; } = new();
    public string? Next { get; set; }
}

internal sealed class HubLookupItem
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "";
    public int Version { get; set; }
    public long UpdatedAt { get; set; }
    public string Title { get; set; } = "";
    public string? Sha256 { get; set; }
    public string? Reason { get; set; }
}

internal sealed class HubLookupAnswer
{
    public List<HubLookupItem> Items { get; set; } = new();
}

internal sealed class HubMyPackages
{
    public List<HubMyCard> Items { get; set; } = new();
}

internal sealed class HubUploader
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Tag { get; set; } = "";
    public long CreatedAt { get; set; }
    public string Status { get; set; } = "";
    public int Strikes { get; set; }
    public bool Trusted { get; set; }

    internal HubUploader Clean()
    {
        if (!HubText.IsUploaderId(Id)) Id = "";
        Name = HubText.CleanLine(Name, HubText.Name) ?? "";
        Tag = HubText.CleanLine(Tag, 4) ?? "";
        return this;
    }
}

internal sealed class HubLimits
{
    public bool Probation { get; set; }
    public long? ProbationUntil { get; set; }
    public int UploadsToday { get; set; }
    public int UploadsPerDay { get; set; }
    public int AttemptsToday { get; set; }
    public int AttemptsPerDay { get; set; }
    public long BytesToday { get; set; }
    public long BytesPerDay { get; set; }
    public int LivePackages { get; set; }
    public int LivePerKey { get; set; }
}

internal sealed class HubMe
{
    public HubUploader Uploader { get; set; } = new();
    public HubLimits Limits { get; set; } = new();
}

internal sealed class HubRegistered
{
    public HubUploader Uploader { get; set; } = new();
    public bool Created { get; set; }
}

internal sealed class HubStatusAnswer
{
    public string Status { get; set; } = "";
}

internal sealed class HubUploadFile
{
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string EntriesSha256 { get; set; } = "";
}

internal sealed class HubUploadMeta
{
    public string Description { get; set; } = "";
    public List<HubDifficulty>? Difficulties { get; set; }
    public List<HubSongDifficulties>? Songs { get; set; }
    public double? LengthSeconds { get; set; }
    public double[]? Bpm { get; set; }
    public List<string> Requires { get; set; } = new();
}

/// <summary>POST /v1/uploads.</summary>
internal sealed class HubUploadStart
{
    public string ClientUploadId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? PackageId { get; set; }
    public HubUploadFile File { get; set; } = new();
    public HubUploadMeta Meta { get; set; } = new();
    public string? Thumb { get; set; }
    public bool RightsConfirmed { get; set; }
    public string Client { get; set; } = "";
}

internal sealed class HubUploadStarted
{
    public string UploadId { get; set; } = "";
    public string PackageId { get; set; } = "";
    public int Version { get; set; }
    public long PartSize { get; set; }
    public int Parts { get; set; }
    public long ExpiresAt { get; set; }
}

internal sealed class HubPartAnswer
{
    public int N { get; set; }
    public string Etag { get; set; } = "";
}

internal sealed class HubCompleted
{
    public string PackageId { get; set; } = "";
    public int Version { get; set; }
    public string Status { get; set; } = "";
}

/// <summary>The body of every error answer: { error, message, ...extra }.</summary>
internal sealed class HubErrorBody
{
    public string? Error { get; set; }
    public string? Message { get; set; }
    public List<string>? Problems { get; set; }
    public string? PackageId { get; set; }
    public List<int>? Missing { get; set; }
    public double? RetryAfter { get; set; }
    public string? Reason { get; set; }
    public long? MaxPackageBytes { get; set; }
}
