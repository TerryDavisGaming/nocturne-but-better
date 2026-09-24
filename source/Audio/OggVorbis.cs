using System.Buffers.Binary;
using System.Text;

namespace NocturneFlatScroll;

/// <summary>
/// An Ogg Vorbis file: the Ogg pages split into packets, the three Vorbis headers, and the audio
/// packets decoded with the start and end trimmed by granule position, as libvorbis does. A first
/// audio page whose granule position is less than the audio it holds starts that much into the
/// stream (the beginning is dropped); the end-of-stream page's granule position cuts the last
/// block short. Audio lost in a damaged file is replaced by silence so the rest stays in time.
/// </summary>
internal sealed class OggVorbis
{
    internal int Channels { get; private set; }
    internal int Rate { get; private set; }
    internal int BlockSize0 { get; private set; }
    internal int BlockSize1 { get; private set; }
    internal string Vendor { get; private set; } = "";
    /// <summary>The last granule position in the file, about the song's length in frames (-1 if none).</summary>
    internal long LastGranule => ogg.LastGranule;
    internal int DamagedPages => ogg.DamagedPages;
    /// <summary>Places where pages of the stream are missing (a damaged page makes one too).</summary>
    internal int Gaps => ogg.Gaps;
    internal bool Truncated => !ogg.SawEndOfStream;

    // Filled by DecodeTo.
    internal long TrimmedStart, TrimmedEnd, FilledGaps, SkippedPackets;

    private readonly OggReader ogg;
    private readonly VorbisSetup setup;

    private OggVorbis(byte[] bytes)
    {
        ogg = new OggReader(bytes, IsVorbisId);
        if (ogg.Packets.Count == 0) throw new InvalidDataException(NoVorbisReason(ogg));
        var p = ogg.Packets;
        ParseIdentification(p[0]);
        if (p.Count < 3) throw new InvalidDataException("the Ogg Vorbis headers are incomplete");
        ParseComment(p[1]);
        var header = p[2];
        if (!HasSignature(header.Data, header.Offset, header.Length, 5)) throw new InvalidDataException("the Ogg Vorbis setup header is missing");
        var br = new VorbisBitReader(header.Data, header.Offset + 7, header.Length - 7);
        setup = VorbisSetup.Parse(br, Channels);
    }

    /// <summary>Reads the headers and splits the audio into packets; decode with <see cref="DecodeTo"/>.</summary>
    internal static OggVorbis Open(byte[] bytes) => new(bytes);

    private static bool HasSignature(byte[] d, int offset, int length, byte type) =>
        length >= 7 && d[offset] == type && d[offset + 1] == 'v' && d[offset + 2] == 'o' && d[offset + 3] == 'r'
        && d[offset + 4] == 'b' && d[offset + 5] == 'i' && d[offset + 6] == 's';

    private static bool IsVorbisId(byte[] d, int offset, int length) => HasSignature(d, offset, length, 1);

    private static string NoVorbisReason(OggReader reader)
    {
        foreach (var first in reader.OtherStreams)
        {
            string head = Encoding.ASCII.GetString(first, 0, Math.Min(8, first.Length));
            if (head.StartsWith("OpusHead")) return "it's an Ogg Opus file, and only Ogg Vorbis is supported; convert it to .ogg (Vorbis), .mp3 or .wav";
            if (first.Length >= 5 && first[0] == 0x7F && head.Substring(1, 4) == "FLAC") return "it's FLAC inside Ogg; save it as a plain .flac file instead";
            if (head.StartsWith("Speex")) return "it's an Ogg Speex file, and only Ogg Vorbis is supported";
        }
        return "it has no Vorbis audio";
    }

    private void ParseIdentification(OggPacket p)
    {
        var d = p.Data.AsSpan(p.Offset, p.Length);
        if (d.Length < 30) throw new InvalidDataException("the Ogg Vorbis identification header is too short");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(7));
        Channels = d[11];
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(12));
        int b0 = d[28] & 0x0F, b1 = d[28] >> 4;
        if (version != 0) throw new NotSupportedException($"Vorbis version {version} isn't supported");
        if (Channels == 0) throw new InvalidDataException("the Ogg Vorbis file has no channels");
        if (rate == 0 || rate > 768000) throw new InvalidDataException($"the Ogg Vorbis sample rate {rate} isn't valid");
        if (b0 < 6 || b1 > 13 || b0 > b1 || (d[29] & 1) == 0) throw new InvalidDataException("the Ogg Vorbis identification header is damaged");
        Rate = (int)rate;
        BlockSize0 = 1 << b0;
        BlockSize1 = 1 << b1;
    }

    // Only the vendor string is kept, for the log.
    private void ParseComment(OggPacket p)
    {
        if (!HasSignature(p.Data, p.Offset, p.Length, 3)) throw new InvalidDataException("the Ogg Vorbis comment header is missing");
        if (p.Length < 11) return;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(p.Data.AsSpan(p.Offset + 7));
        if (length <= (uint)(p.Length - 11)) Vendor = Encoding.UTF8.GetString(p.Data, p.Offset + 11, (int)length);
    }

    /// <summary>
    /// The frames the audio packets make before any trimming, counted from each packet's mode
    /// (its block size) without decoding. The buffer is sized from this when the last granule
    /// position is larger: a stream cut out of a longer one (a radio recording, say) keeps
    /// counting granules from the original start.
    /// </summary>
    internal long CountFrames()
    {
        var br = new VorbisBitReader();
        var packets = ogg.Packets;
        long total = 0;
        int prev = 0;
        for (int i = 3; i < packets.Count; i++)
        {
            var p = packets[i];
            if (p.AfterGap) prev = 0;
            br.Init(p.Data, p.Offset, p.Length);
            if (br.Read(1) != 0) continue;
            int mode = br.Read(setup.ModeBits);
            if (mode < 0 || mode >= setup.Modes.Length) continue;
            bool longBlock = setup.Modes[mode].BlockFlag;
            if (longBlock && br.Read(2) < 0) continue;
            int n = longBlock ? BlockSize1 : BlockSize0;
            if (prev != 0) total += prev / 4 + n / 4;
            prev = n;
        }
        return total;
    }

    /// <summary>Decodes every audio packet into <paramref name="writer"/>.</summary>
    internal void DecodeTo(IPcmWriter writer)
    {
        var decoder = new VorbisDecoder(Channels, BlockSize0, BlockSize1, setup);
        var packets = ogg.Packets;
        long decoded = 0;      // frames from the packets before the first granule position
        long granule = -1;     // the stream position at the end of the output, once known
        long gapAt = -1, gapGranule = 0, sinceGap = 0;
        long earlyGapAt = -1;  // where audio went missing before the first granule position

        for (int i = 3; i < packets.Count; i++)
        {
            var p = packets[i];
            if (p.AfterGap)
            {
                decoder.Reset();
                if (granule >= 0 && gapAt < 0)
                {
                    gapAt = writer.Frames;
                    gapGranule = granule;
                    sinceGap = 0;
                }
                else if (granule < 0 && earlyGapAt < 0)
                {
                    earlyGapAt = writer.Frames;
                }
            }
            int n = decoder.Decode(p.Data, p.Offset, p.Length);
            if (n < 0)
            {
                SkippedPackets++;
                continue;
            }
            if (granule >= 0 && gapAt < 0 && p.EndOfStream && p.Granule >= 0 && granule + n > p.Granule)
            {
                // The usual end: the last block is cut at the final granule position. Cutting
                // before writing lets a writer sized to the file's length fill up exactly.
                long extra = Math.Min(granule + n - p.Granule, n);
                writer.Write(decoder.Output, (int)(n - extra));
                TrimmedEnd += extra;
                granule = p.Granule;
                continue;
            }
            writer.Write(decoder.Output, n);

            if (granule < 0)
            {
                decoded += n;
                if (p.Granule < 0) continue;
                granule = p.Granule;
                if (decoded > granule)
                {
                    // The first page says fewer frames than it holds: the stream starts part way
                    // into it, unless it's also the last page, when the end is cut instead.
                    long extra = Math.Min(decoded - granule, writer.Frames);
                    if (p.EndOfStream) { writer.TrimEnd(extra); TrimmedEnd += extra; }
                    else { writer.TrimStart(extra); TrimmedStart += extra; }
                }
                else if (earlyGapAt >= 0 && decoded < granule && granule - decoded <= Rate * 60L)
                {
                    // The first audio page was lost: the granule position says how much audio
                    // came before what survived, so silence takes its place and the song keeps
                    // its timing.
                    writer.InsertSilence(earlyGapAt, granule - decoded);
                    FilledGaps += granule - decoded;
                }
            }
            else if (gapAt >= 0)
            {
                sinceGap += n;
                if (p.Granule < 0) continue;
                long missing = p.Granule - (gapGranule + sinceGap);
                // More than a minute missing means the granule positions can't be trusted.
                if (missing > 0 && missing <= Rate * 60L)
                {
                    writer.InsertSilence(gapAt, missing);
                    FilledGaps += missing;
                }
                else if (missing < 0 && p.EndOfStream)
                {
                    long extra = Math.Min(-missing, n);
                    writer.TrimEnd(extra);
                    TrimmedEnd += extra;
                }
                granule = p.Granule;
                gapAt = -1;
            }
            else
            {
                // A page whose granule position disagrees with the count is believed, as libvorbis
                // does; only the end-of-stream page cuts audio (above).
                granule += n;
                if (p.Granule >= 0) granule = p.Granule;
            }
        }
    }
}
