using System.Globalization;
using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// Loads a song's battle music for the chart editor, mixed onto the chart's clock. The game's
/// music is Wwise interactive music; MusicMapData lists, for each song and melody, which files play
/// where (worked out from the game's banks). They are all plain 16-bit PCM, so this reads them
/// straight from the game's files, resamples to 48 kHz stereo, applies the clips' fades, and mixes.
/// </summary>
internal static class SongAudio
{
    internal const int Rate = 48000;

    internal sealed class Result
    {
        internal short[] Stereo = Array.Empty<short>();
        internal int SampleRate = Rate;
        // Chart time, in seconds, of the first sample (negative when the music starts before beat 0).
        internal double Origin;
        // Loudness (0-1) every 1/PeakRate seconds of the result, for the editor's waveform.
        internal float[] Peaks = Array.Empty<float>();
        internal float PeakRate;
    }

    private sealed class Piece
    {
        internal string Source = "";
        internal double PlaceMs, TrimMs, PlayMs, FadeStartMs;
        internal readonly List<(string Type, (double T, double V, int Interp)[] Points)> Fades = new();
    }

    private static Dictionary<string, List<Piece>>? pieces;

    /// <summary>Whether the game's music map knows the song and melody.</summary>
    internal static bool Has(string song, int melody) => Map().ContainsKey(song + "\n" + melody);

    /// <summary>The game's Wwise folder. Read it on the main thread; Unity's paths aren't thread safe.</summary>
    internal static string MusicRoot => Path.Combine(Application.streamingAssetsPath, "Audio", "GeneratedSoundBanks", "Windows");

    /// <summary>Can run on any thread.</summary>
    internal static Result Load(string song, int melody, string root)
    {
        if (!Map().TryGetValue(song + "\n" + melody, out var list) || list.Count == 0)
            throw new InvalidDataException("the game has no battle music listed for this song");

        double start = Math.Min(0, list.Min(p => p.PlaceMs)) / 1000.0;
        double end = list.Max(p => p.PlaceMs + p.PlayMs) / 1000.0;
        long frames = (long)Math.Ceiling((end - start) * Rate) + 1;
        if (frames > int.MaxValue / 2) throw new InvalidDataException("the song is too long");
        var mix = new int[frames * 2];
        foreach (var piece in list) MixPiece(root, piece, start, mix);

        var result = new Result { Origin = start, Stereo = new short[mix.Length] };
        for (int i = 0; i < mix.Length; i++) result.Stereo[i] = (short)Math.Clamp(mix[i], short.MinValue, short.MaxValue);
        const int peakFrames = Rate / 100;
        result.PeakRate = Rate / (float)peakFrames;
        result.Peaks = new float[(int)(frames / peakFrames) + 1];
        for (int p = 0; p < result.Peaks.Length; p++)
        {
            int peak = 0;
            int from = p * peakFrames * 2, to = Math.Min(result.Stereo.Length, from + peakFrames * 2);
            for (int i = from; i < to; i++) peak = Math.Max(peak, Math.Abs((int)result.Stereo[i]));
            result.Peaks[p] = peak / 32768f;
        }
        return result;
    }

    private static void MixPiece(string root, Piece piece, double start, int[] mix)
    {
        string path;
        long riffStart = 0;
        int at = piece.Source.IndexOf('@');
        if (at >= 0)
        {
            path = Path.Combine(root, piece.Source.Substring(0, at));
            riffStart = long.Parse(piece.Source.Substring(at + 1), CultureInfo.InvariantCulture);
        }
        else path = Path.Combine(root, "Media", piece.Source);

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var (channels, rate, dataStart, dataBytes) = ReadPcmHeader(file, riffStart);
        long sourceFrames = dataBytes / (channels * 2);
        long first = (long)Math.Round(piece.TrimMs * rate / 1000.0);
        long count = Math.Min(sourceFrames - first, (long)Math.Round(piece.PlayMs * rate / 1000.0));
        if (first < 0 || count <= 0) return;

        // Read the piece's source frames, then place them on the 48 kHz timeline.
        var bytes = new byte[count * channels * 2];
        file.Seek(dataStart + first * channels * 2, SeekOrigin.Begin);
        int read = 0;
        while (read < bytes.Length)
        {
            int n = file.Read(bytes, read, bytes.Length - read);
            if (n <= 0) break;
            read += n;
        }
        count = read / (channels * 2);
        long outFirst = (long)Math.Round((piece.PlaceMs / 1000.0 - start) * Rate);
        long outCount = (long)Math.Floor(count * (double)Rate / rate);
        double step = rate / (double)Rate;
        long totalFrames = mix.Length / 2;
        for (long f = 0; f < outCount; f++)
        {
            long o = outFirst + f;
            if (o < 0 || o >= totalFrames) continue;
            double pos = f * step;
            long i0 = (long)pos;
            if (i0 >= count - 1) i0 = count - 2;
            if (i0 < 0) break;
            float frac = (float)(pos - i0);
            float l0 = Sample(bytes, i0, 0, channels), l1 = Sample(bytes, i0 + 1, 0, channels);
            float r0 = channels > 1 ? Sample(bytes, i0, 1, channels) : l0, r1 = channels > 1 ? Sample(bytes, i0 + 1, 1, channels) : l1;
            float gain = piece.Fades.Count == 0 ? 1f : Gain(piece, piece.PlaceMs + f * 1000.0 / Rate);
            mix[o * 2] += (int)((l0 + (l1 - l0) * frac) * gain);
            mix[o * 2 + 1] += (int)((r0 + (r1 - r0) * frac) * gain);
        }
    }

    private static float Sample(byte[] bytes, long frame, int channel, int channels) =>
        (short)(bytes[(frame * channels + channel) * 2] | (bytes[(frame * channels + channel) * 2 + 1] << 8));

    /// <summary>The clip's fade curves at a chart time (ms): each point holds or ramps to the next.</summary>
    private static float Gain(Piece piece, double chartMs)
    {
        double t = (chartMs - piece.FadeStartMs) / 1000.0;
        double gain = 1;
        foreach (var (type, points) in piece.Fades)
        {
            double v = points[^1].V;
            if (t <= points[0].T) v = points[0].V;
            else
                for (int i = 0; i < points.Length - 1; i++)
                {
                    if (t >= points[i + 1].T) continue;
                    var a = points[i];
                    var b = points[i + 1];
                    // Interpolation 9 is Wwise's "constant": the value holds until the next point.
                    v = a.Interp == 9 || b.T <= a.T ? a.V : a.V + (b.V - a.V) * (t - a.T) / (b.T - a.T);
                    break;
                }
            // Volume automation runs from 0 (unchanged) down to -1 (silent); fades run from 0 to 1.
            gain *= type == "volume" ? Math.Clamp(1 + v, 0, 1) : Math.Clamp(v, 0, 1);
        }
        return (float)gain;
    }

    /// <summary>Reads a RIFF/WAVE header (plain or Wwise "extensible" PCM) starting at <paramref name="riffStart"/>.</summary>
    private static (int Channels, int Rate, long DataStart, long DataBytes) ReadPcmHeader(FileStream file, long riffStart)
    {
        var reader = new BinaryReader(file);
        file.Seek(riffStart, SeekOrigin.Begin);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("not a RIFF file");
        long riffEnd = riffStart + 8 + reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("not a WAVE file");
        int channels = 0, rate = 0, bits = 0, format = 0;
        while (file.Position + 8 <= riffEnd)
        {
            string id = new string(reader.ReadChars(4));
            uint size = reader.ReadUInt32();
            long next = file.Position + size + (size & 1);
            if (id == "fmt ")
            {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = (int)reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
            }
            else if (id == "data")
            {
                if ((format != 1 && format != 0xFFFE) || bits != 16 || channels < 1 || channels > 2 || rate <= 0)
                    throw new InvalidDataException($"unsupported music format (codec 0x{format:X}, {bits} bit, {channels} channels)");
                return (channels, rate, file.Position, size);
            }
            file.Seek(next, SeekOrigin.Begin);
        }
        throw new InvalidDataException("no audio data in the music file");
    }

    private static Dictionary<string, List<Piece>> Map()
    {
        if (pieces != null) return pieces;
        var map = new Dictionary<string, List<Piece>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in MusicMapData.Pieces.Split('\n'))
        {
            var f = line.TrimEnd('\r').Split('\t');
            if (f.Length < 7) continue;
            var piece = new Piece
            {
                Source = f[2],
                PlaceMs = double.Parse(f[3], CultureInfo.InvariantCulture),
                TrimMs = double.Parse(f[4], CultureInfo.InvariantCulture),
                PlayMs = double.Parse(f[5], CultureInfo.InvariantCulture),
                FadeStartMs = double.Parse(f[6], CultureInfo.InvariantCulture),
            };
            if (f.Length > 7 && f[7].Length > 0)
                foreach (var fade in f[7].Split('|'))
                {
                    int colon = fade.IndexOf(':');
                    var points = fade.Substring(colon + 1).Split(';').Select(p =>
                    {
                        var v = p.Split(',');
                        return (double.Parse(v[0], CultureInfo.InvariantCulture), double.Parse(v[1], CultureInfo.InvariantCulture), int.Parse(v[2], CultureInfo.InvariantCulture));
                    }).ToArray();
                    piece.Fades.Add((fade.Substring(0, colon), points));
                }
            string key = f[0] + "\n" + f[1];
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<Piece>();
            list.Add(piece);
        }
        return pieces = map;
    }
}
