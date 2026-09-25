using System.Globalization;

namespace NocturneFlatScroll;

/// <summary>A [TimingPoints] line. Red (uninherited) lines set the tempo; green ones the scroll speed.</summary>
internal readonly struct OsuTiming
{
    internal readonly double Time;        // ms, as osu! plays it (+24 for format v3/v4)
    internal readonly double BeatLength;  // red: ms per beat; green: -100 / speed
    internal readonly int Meter;
    internal readonly bool Uninherited;
    internal readonly int Order;          // its place in the file, for "which one at a time counts"

    internal OsuTiming(double time, double beatLength, int meter, bool uninherited, int order)
    {
        Time = time; BeatLength = beatLength; Meter = meter; Uninherited = uninherited; Order = order;
    }

    /// <summary>A red line that sets a tempo (a positive beat length).</summary>
    internal bool IsTempo => Uninherited && BeatLength > 0;

    /// <summary>The tempo as osu! plays it: it keeps a beat between 6 ms and 60 s long.</summary>
    internal double Bpm => 60000.0 / Math.Clamp(BeatLength, 6, 60000);

    /// <summary>A green line's speed as osu!mania uses it: -100 / beat length, kept in 0.01..10 (1 for a length that isn't negative).</summary>
    internal double Speed => BeatLength < 0 ? Math.Clamp(-100.0 / BeatLength, 0.01, 10) : 1;

    /// <summary>A green line whose speed osu! keeps at its 0.01 or 10 limit.</summary>
    internal bool SpeedOutsideOsu => !Uninherited && BeatLength < 0 && (-100.0 / BeatLength < 0.01 || -100.0 / BeatLength > 10);
}

/// <summary>A [HitObjects] line of a mania difficulty: a note, or a hold when EndTime is set.</summary>
internal readonly struct OsuObject
{
    internal readonly double X;
    internal readonly double Time;        // ms
    internal readonly double EndTime;     // ms; NaN for a note

    internal OsuObject(double x, double time, double endTime) { X = x; Time = time; EndTime = endTime; }

    internal bool IsHold => !double.IsNaN(EndTime);

    /// <summary>osu!mania's column: floor(x * keys / 512), kept inside the playfield (in double, so no x overflows).</summary>
    internal int Column(int keys) => (int)Math.Clamp(Math.Floor(X * keys / 512.0), 0, keys - 1);
}

/// <summary>
/// One osu! difficulty (a .osu file), read for an osu!mania import: only what a battle can use,
/// plus what the import tells the player it leaves out. Reading never throws for what the file
/// says: a line it can't use is skipped and counted. Long lines, too many timing points or
/// objects, and numbers that aren't finite or are far out of range are all bounded.
/// This file has no Unity or game dependencies.
/// </summary>
internal sealed class OsuFile
{
    internal const int MaxLineLength = 64 * 1024;
    internal const int MaxTimingPoints = 100_000;
    internal const int MaxObjects = 100_000;
    internal const int MaxBookmarks = 1000;
    internal const int MaxTextLength = 1000;
    internal const double MaxTime = 3 * 3600 * 1000.0;   // 3 hours, in ms
    /// <summary>osu! plays everything in format v3/v4 files 24 ms later (its old offset).</summary>
    internal const double EarlyVersionOffset = 24;

    internal string Entry = "";
    internal int FormatVersion = 14;
    internal bool HasHeader;
    /// <summary>How many sections osu! knows ([General], [HitObjects] and so on) the file has.</summary>
    internal int KnownSections;
    internal string AudioFilename = "";
    internal double AudioLeadIn;
    internal double PreviewTime = -1;     // ms; -1 = not set
    internal long Mode;                   // 0 osu!, 1 taiko, 2 catch, 3 mania
    internal string Title = "", TitleUnicode = "", Artist = "", ArtistUnicode = "", Creator = "", Version = "";
    internal long BeatmapSetId = -1;
    internal double CircleSize = double.NaN, OverallDifficulty = double.NaN, HpDrainRate = double.NaN;
    internal string Background = "", Video = "";
    internal bool HasBreaks, HasStoryboard, HasKiai, HasVolumeChanges;
    internal readonly List<double> Bookmarks = new();
    internal readonly List<OsuTiming> Timing = new();
    internal readonly List<OsuObject> Objects = new();
    /// <summary>Lines that couldn't be read (or were too long).</summary>
    internal int BadLines;
    /// <summary>Sliders and spinners, which osu!mania difficulties don't have of their own.</summary>
    internal int OtherObjects;
    /// <summary>Holds without a length, read as notes.</summary>
    internal int EmptyHolds;
    /// <summary>Timing points and objects past the limits.</summary>
    internal int TimingPastLimit, ObjectsPastLimit;

    /// <summary>The key count: CircleSize rounded (osu!mania), or 0 when the file doesn't give one from 1 to 18.</summary>
    internal int Keys => CircleSize >= 1 && CircleSize <= 18 ? (int)Math.Round(CircleSize) : 0;

    internal int RedLines
    {
        get
        {
            int n = 0;
            foreach (var t in Timing) if (t.IsTempo) n++;
            return n;
        }
    }

    internal int GreenLines
    {
        get
        {
            int n = 0;
            foreach (var t in Timing) if (!t.Uninherited) n++;
            return n;
        }
    }

    internal int Holds
    {
        get
        {
            int n = 0;
            foreach (var o in Objects) if (o.IsHold) n++;
            return n;
        }
    }

    private double lastVolume = double.NaN;

    /// <summary>Reads a .osu text. <paramref name="guard"/> ends a read that runs too long.</summary>
    internal static OsuFile Parse(string text, string entry, OszGuard? guard = null)
    {
        var f = new OsuFile { Entry = entry };
        string section = "";
        bool seenContent = false;
        int order = 0;
        int pos = 0;
        while (pos < text.Length)
        {
            guard?.Step();
            int nl = text.IndexOf('\n', pos);
            int end = nl < 0 ? text.Length : nl;
            int length = end - pos;
            string? raw = length <= MaxLineLength ? text.Substring(pos, length) : null;
            pos = end + 1;
            if (raw == null) { f.BadLines++; continue; }
            string line = raw.TrimEnd('\r').TrimStart('\uFEFF');
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
            if (!seenContent)
            {
                seenContent = true;
                if (trimmed.StartsWith("osu file format v", StringComparison.Ordinal))
                {
                    f.HasHeader = true;
                    int digits = 17;
                    while (digits < trimmed.Length && digits < 27 && char.IsDigit(trimmed[digits])) digits++;
                    if (int.TryParse(trimmed.Substring(17, digits - 17), NumberStyles.None, CultureInfo.InvariantCulture, out int v)) f.FormatVersion = v;
                    continue;
                }
            }
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                section = trimmed.Substring(1, trimmed.Length - 2).Trim();
                if (section is "General" or "Editor" or "Metadata" or "Difficulty" or "Events" or "TimingPoints" or "HitObjects" or "Colours")
                    f.KnownSections++;
                continue;
            }
            // Like osu!: "//" starts a comment anywhere except in [Metadata], where it may be part of a title.
            if (section != "Metadata")
            {
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment > 0) line = line.Substring(0, comment);
            }
            line = line.TrimEnd();
            switch (section)
            {
                case "General": f.ReadGeneral(line); break;
                case "Editor": f.ReadEditor(line); break;
                case "Metadata": f.ReadMetadata(line); break;
                case "Difficulty": f.ReadDifficulty(line); break;
                case "Events": f.ReadEvent(line); break;
                case "TimingPoints": f.ReadTiming(line, order++); break;
                case "HitObjects": f.ReadObject(line); break;
            }
        }
        return f;
    }

    private double Shift => FormatVersion < 5 ? EarlyVersionOffset : 0;

    private static bool KeyValue(string line, out string key, out string value)
    {
        int colon = line.IndexOf(':');
        key = colon > 0 ? line.Substring(0, colon).Trim() : "";
        value = colon > 0 ? line.Substring(colon + 1).Trim() : "";
        return colon > 0;
    }

    /// <summary>A finite number in invariant culture.</summary>
    internal static bool Number(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static bool Time(string text, out double value) => Number(text, out value) && Math.Abs(value) <= MaxTime;

    private static string Text(string value) => value.Length > MaxTextLength ? value.Substring(0, MaxTextLength) : value;

    private void ReadGeneral(string line)
    {
        if (!KeyValue(line, out var key, out var value)) { BadLines++; return; }
        switch (key)
        {
            case "AudioFilename": AudioFilename = CleanPath(value); break;
            case "AudioLeadIn": if (Number(value, out double lead)) AudioLeadIn = lead; break;
            case "PreviewTime":
                if (Time(value, out double preview)) PreviewTime = preview < 0 ? -1 : preview + Shift;
                break;
            case "Mode": if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long mode)) Mode = mode; break;
        }
    }

    private void ReadEditor(string line)
    {
        if (!KeyValue(line, out var key, out var value) || key != "Bookmarks") return;
        foreach (var part in value.Split(','))
            if (Bookmarks.Count < MaxBookmarks && Time(part, out double t)) Bookmarks.Add(t + Shift);
    }

    private void ReadMetadata(string line)
    {
        if (!KeyValue(line, out var key, out var value)) { BadLines++; return; }
        value = Text(value);
        switch (key)
        {
            case "Title": Title = value; break;
            case "TitleUnicode": TitleUnicode = value; break;
            case "Artist": Artist = value; break;
            case "ArtistUnicode": ArtistUnicode = value; break;
            case "Creator": Creator = value; break;
            case "Version": Version = value; break;
            case "BeatmapSetID":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && id >= 0) BeatmapSetId = id;
                break;
        }
    }

    private void ReadDifficulty(string line)
    {
        if (!KeyValue(line, out var key, out var value)) { BadLines++; return; }
        if (!Number(value, out double v)) return;
        switch (key)
        {
            case "CircleSize": CircleSize = v; break;
            case "OverallDifficulty": OverallDifficulty = v; break;
            case "HPDrainRate": HpDrainRate = v; break;
        }
    }

    // Background: 0,0,"file",x,y (or "Background"); video: Video,start,"file" (or 1); breaks: 2 (or
    // Break),start,end. Everything else in [Events] is the storyboard.
    private void ReadEvent(string line)
    {
        var parts = line.Split(',');
        string kind = parts[0].Trim();
        if (kind is "0" or "Background")
        {
            if (parts.Length >= 3 && Background.Length == 0) Background = CleanPath(parts[2]);
        }
        else if (kind is "1" or "Video")
        {
            if (parts.Length >= 3 && Video.Length == 0) Video = CleanPath(parts[2]);
        }
        else if (kind is "2" or "Break") HasBreaks = true;
        else HasStoryboard = true;
    }

    /// <summary>A file name as a .osu gives it: quotes and spaces off, '/' between folders.</summary>
    internal static string CleanPath(string path) => Text(path.Trim().Trim('"').Replace('\\', '/').Trim());

    // time,beatLength,meter,sampleSet,sampleIndex,volume,uninherited,effects (older files stop earlier).
    private void ReadTiming(string line, int order)
    {
        var p = line.Split(',');
        if (p.Length < 2 || !Time(p[0], out double time) || !Number(p[1], out double beatLength) || Math.Abs(beatLength) > int.MaxValue)
        {
            BadLines++;
            return;
        }
        if (Timing.Count >= MaxTimingPoints) { TimingPastLimit++; return; }
        int meter = p.Length > 2 && int.TryParse(p[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) && m >= 1 && m <= 1000 ? m : 4;
        // Without the uninherited field (older files), a positive beat length is a red line.
        bool uninherited = p.Length > 6 ? p[6].Trim().StartsWith("1", StringComparison.Ordinal) : beatLength > 0;
        if (p.Length > 5 && Number(p[5], out double volume))
        {
            if (!double.IsNaN(lastVolume) && volume != lastVolume) HasVolumeChanges = true;
            lastVolume = volume;
        }
        if (p.Length > 7 && int.TryParse(p[7].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int effects) && (effects & 1) != 0) HasKiai = true;
        Timing.Add(new OsuTiming(time + Shift, beatLength, meter, uninherited, order));
    }

    // x,y,time,type,hitSound,extras; a mania hold (type bit 7) has "endTime:hitSample" as its extras.
    private void ReadObject(string line)
    {
        var p = line.Split(',');
        if (p.Length < 4 || !Number(p[0], out double x) || !Time(p[2], out double time)
            || !int.TryParse(p[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int type))
        {
            BadLines++;
            return;
        }
        time += Shift;
        // osu! checks the circle bit first, then slider (2), spinner (8), hold (128).
        if ((type & 1) != 0) Add(new OsuObject(x, time, double.NaN));
        else if ((type & 2) != 0 || (type & 8) != 0) OtherObjects++;
        else if ((type & 128) != 0)
        {
            double endTime = double.NaN;
            if (p.Length > 5)
            {
                string extras = p[5];
                int colon = extras.IndexOf(':');
                if (Time(colon >= 0 ? extras.Substring(0, colon) : extras, out double e)) endTime = e + Shift;
            }
            if (!(endTime > time)) { EmptyHolds++; endTime = double.NaN; }
            Add(new OsuObject(x, time, endTime));
        }
        else BadLines++;
    }

    private void Add(OsuObject o)
    {
        if (Objects.Count >= MaxObjects) ObjectsPastLimit++;
        else Objects.Add(o);
    }
}
