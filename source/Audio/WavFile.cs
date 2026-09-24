namespace NocturneFlatScroll;

/// <summary>RIFF WAVE files: integer PCM and 32-bit float, read by the mod itself.</summary>
internal static class WavFile
{
    /// <summary>The format tag of a WAVE file (1 = PCM, 3 = float; extensible files give their sub-format), or -1.</summary>
    internal static int FormatTag(byte[] b)
    {
        var (fmt, size) = Chunk(b, "fmt ");
        if (fmt < 0 || size < 2) return -1;
        int format = BitConverter.ToUInt16(b, fmt);
        if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(b, fmt + 24);
        return format;
    }

    /// <summary>The body offset and size of the first chunk with this id, or (-1, 0).</summary>
    internal static (int Offset, int Size) Chunk(byte[] b, string id)
    {
        int pos = 12;
        while (pos + 8 <= b.Length)
        {
            int size = BitConverter.ToInt32(b, pos + 4);
            int body = pos + 8;
            if (size < 0 || (long)body + size > b.Length) size = b.Length - body;
            if (b[pos] == id[0] && b[pos + 1] == id[1] && b[pos + 2] == id[2] && b[pos + 3] == id[3]) return (body, size);
            pos = body + size + (size & 1);
        }
        return (-1, 0);
    }

    /// <summary>PCM WAV, 8/16/24/32-bit integer or 32-bit float, mono or stereo (or more; the first two channels).</summary>
    internal static (short[] Stereo, int Rate, int Channels) Decode(byte[] b)
    {
        int pos = 12, channels = 0, rate = 0, bits = 0, format = 0;
        int dataStart = -1, dataLength = 0;
        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4);
            int body = pos + 8;
            // In long arithmetic: a streamed file can claim a length near 2 GB, which must be
            // cut to the file rather than wrap around.
            if (size < 0 || (long)body + size > b.Length) size = b.Length - body;
            if (id == "fmt ")
            {
                format = BitConverter.ToUInt16(b, body);
                channels = BitConverter.ToUInt16(b, body + 2);
                rate = BitConverter.ToInt32(b, body + 4);
                bits = BitConverter.ToUInt16(b, body + 14);
                if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(b, body + 24); // extensible: sub-format
            }
            else if (id == "data") { dataStart = body; dataLength = size; }
            pos = body + size + (size & 1);
        }
        if (dataStart < 0 || channels <= 0 || rate <= 0) throw new InvalidDataException("the WAV file has no audio");
        bool isFloat = format == 3;
        if (!isFloat && format != 1) throw new NotSupportedException($"WAV format {format} isn't supported; use plain PCM");
        int bytesPer = bits / 8;
        if (bytesPer <= 0 || (isFloat && bits != 32)) throw new NotSupportedException($"{bits}-bit WAV isn't supported");
        int frames = dataLength / (bytesPer * channels);
        var stereo = new short[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            int at = dataStart + f * bytesPer * channels;
            short l = Sample(b, at, bits, isFloat);
            short r = channels > 1 ? Sample(b, at + bytesPer, bits, isFloat) : l;
            stereo[f * 2] = l;
            stereo[f * 2 + 1] = r;
        }
        return (stereo, rate, channels);
    }

    private static short Sample(byte[] b, int at, int bits, bool isFloat)
    {
        if (isFloat) return (short)Math.Clamp(BitConverter.ToSingle(b, at) * 32767f, -32768f, 32767f);
        return bits switch
        {
            8 => (short)((b[at] - 128) << 8),
            16 => BitConverter.ToInt16(b, at),
            24 => (short)((b[at + 1]) | (b[at + 2] << 8)),
            32 => (short)(BitConverter.ToInt32(b, at) >> 16),
            _ => 0,
        };
    }
}
