namespace NocturneFlatScroll;

/// <summary>Where a decoder puts its audio: planar float blocks, trimmed or padded afterwards.</summary>
internal interface IPcmWriter
{
    /// <summary>Appends <paramref name="frames"/> frames, one array per channel.</summary>
    void Write(float[][] planar, int frames);
    /// <summary>Frames kept so far.</summary>
    long Frames { get; }
    /// <summary>Drops frames from the beginning.</summary>
    void TrimStart(long frames);
    /// <summary>Drops frames from the end.</summary>
    void TrimEnd(long frames);
    /// <summary>Inserts silence before frame <paramref name="at"/> (to cover audio lost in a damaged file).</summary>
    void InsertSilence(long at, long frames);
}

/// <summary>
/// Collects decoded audio as interleaved 16-bit stereo, the format the mod's player takes. More
/// than two channels are folded down (the centre and surrounds at -3 dB, the LFE left out, then
/// scaled so the result can't clip); mono is copied to both sides.
/// </summary>
internal sealed class StereoPcm : IPcmWriter
{
    // Two shorts per frame, within the largest array .NET allows.
    private static readonly int MaxFrames = Array.MaxLength / 2;

    private readonly int channels;
    private readonly float[] toLeft, toRight;   // per source channel
    private short[] buffer;
    private int count;                          // frames in buffer, including trimmed ones at the front
    private int start;                          // frames trimmed from the front

    /// <param name="roles">The speaker of each source channel, or null when unknown (then the first two are used).</param>
    /// <param name="expectedFrames">
    /// The length the file claims, to allocate once; when it's exact, <see cref="ToArray"/> needs no copy.
    /// Keep it to what the file's size makes plausible, since a damaged header can claim anything.
    /// </param>
    internal StereoPcm(int channels, SpeakerRole[]? roles, long expectedFrames)
    {
        if (channels < 1) throw new InvalidDataException("the song has no audio channels");
        this.channels = channels;
        (toLeft, toRight) = Matrix(channels, roles);
        buffer = new short[(expectedFrames > 0 ? Math.Min(expectedFrames, MaxFrames) : 65536) * 2];
    }

    /// <summary>A cap for a claimed length: no compressed audio holds more than 64 frames per byte.</summary>
    internal static long Plausible(long frames, int fileBytes) => Math.Min(frames, fileBytes * 64L);

    public long Frames => count - start;

    /// <summary>The source channel count.</summary>
    internal int Channels => channels;

    /// <summary>The kept frames. Returns the buffer itself when it's exactly full, otherwise a copy.</summary>
    internal short[] ToArray()
    {
        if (start == 0 && count * 2 == buffer.Length) return buffer;
        var result = new short[(count - start) * 2];
        Array.Copy(buffer, start * 2, result, 0, result.Length);
        return result;
    }

    private void Reserve(long extra)
    {
        long need = count + extra;
        if (need > MaxFrames) throw new InvalidDataException("the song is too long to load");
        if (need * 2 <= buffer.Length) return;
        long size = Math.Min(Math.Max(need, (long)buffer.Length), MaxFrames); // doubles the frame capacity
        Array.Resize(ref buffer, (int)size * 2);
    }

    public void Write(float[][] planar, int frames)
    {
        if (frames <= 0) return;
        Reserve(frames);
        int d = count * 2;
        if (channels == 1)
        {
            float[] m = planar[0];
            for (int i = 0; i < frames; i++) { short s = ToShort(m[i]); buffer[d++] = s; buffer[d++] = s; }
        }
        else if (channels == 2)
        {
            float[] l = planar[0], r = planar[1];
            for (int i = 0; i < frames; i++) { buffer[d++] = ToShort(l[i]); buffer[d++] = ToShort(r[i]); }
        }
        else
        {
            for (int i = 0; i < frames; i++)
            {
                float left = 0, right = 0;
                for (int c = 0; c < channels; c++)
                {
                    float v = planar[c][i];
                    left += v * toLeft[c];
                    right += v * toRight[c];
                }
                buffer[d++] = ToShort(left);
                buffer[d++] = ToShort(right);
            }
        }
        count += frames;
    }

    /// <summary>Appends interleaved 16-bit frames with <see cref="Channels"/> channels.</summary>
    internal void WriteInterleaved(short[] source, int frames)
    {
        if (frames <= 0) return;
        Reserve(frames);
        int d = count * 2;
        if (channels == 2)
        {
            Array.Copy(source, 0, buffer, d, frames * 2);
        }
        else if (channels == 1)
        {
            for (int i = 0; i < frames; i++) { buffer[d++] = source[i]; buffer[d++] = source[i]; }
        }
        else
        {
            for (int i = 0, s = 0; i < frames; i++, s += channels)
            {
                float left = 0, right = 0;
                for (int c = 0; c < channels; c++)
                {
                    float v = source[s + c];
                    left += v * toLeft[c];
                    right += v * toRight[c];
                }
                buffer[d++] = Clamp16(MathF.Round(left));
                buffer[d++] = Clamp16(MathF.Round(right));
            }
        }
        count += frames;
    }

    public void TrimStart(long frames)
    {
        if (frames > 0) start = (int)Math.Min(count, start + frames);
    }

    public void TrimEnd(long frames)
    {
        if (frames > 0) count = (int)Math.Max(start, count - frames);
    }

    public void InsertSilence(long at, long frames)
    {
        if (frames <= 0) return;
        Reserve(frames);
        int from = start + (int)Math.Clamp(at, 0, Frames);
        int n = (int)frames;
        Array.Copy(buffer, from * 2, buffer, (from + n) * 2, (count - from) * 2);
        Array.Clear(buffer, from * 2, n * 2);
        count += n;
    }

    // libvorbis' ov_read conversion: scale by 32768, round to nearest, clip.
    private static short ToShort(float v) => Clamp16(MathF.Round(v * 32768f));

    private static short Clamp16(float v) => v >= 32767f ? (short)32767 : v <= -32768f ? (short)-32768 : (short)v;

    /// <summary>The fold-down gains from each source channel to left and right.</summary>
    private static (float[] Left, float[] Right) Matrix(int channels, SpeakerRole[]? roles)
    {
        var left = new float[channels];
        var right = new float[channels];
        if (channels == 1)
        {
            left[0] = right[0] = 1;
            return (left, right);
        }
        if (roles == null || roles.Length != channels)
        {
            left[0] = 1;
            right[1] = 1;
            return (left, right);
        }
        const float side = 0.70710678f;
        for (int c = 0; c < channels; c++)
        {
            switch (roles[c])
            {
                case SpeakerRole.FrontLeft: left[c] = 1; break;
                case SpeakerRole.FrontRight: right[c] = 1; break;
                case SpeakerRole.Center: left[c] = right[c] = side; break;
                case SpeakerRole.Left: left[c] = side; break;
                case SpeakerRole.Right: right[c] = side; break;
                case SpeakerRole.Lfe: break;
            }
        }
        float sumLeft = 0, sumRight = 0;
        for (int c = 0; c < channels; c++) { sumLeft += left[c]; sumRight += right[c]; }
        float scale = Math.Max(sumLeft, sumRight);
        if (scale > 1)
        {
            for (int c = 0; c < channels; c++) { left[c] /= scale; right[c] /= scale; }
        }
        return (left, right);
    }
}

/// <summary>Where a source channel goes when folding down to stereo.</summary>
internal enum SpeakerRole
{
    FrontLeft,
    FrontRight,
    Center,   // front, back or top centre
    Left,     // any other speaker on the left
    Right,    // any other speaker on the right
    Lfe,
}

internal static class SpeakerLayouts
{
    private const SpeakerRole FL = SpeakerRole.FrontLeft, FR = SpeakerRole.FrontRight, C = SpeakerRole.Center,
                              L = SpeakerRole.Left, R = SpeakerRole.Right, LFE = SpeakerRole.Lfe;

    /// <summary>The Vorbis I channel order (spec section 4.3.9) for 1 to 8 channels; null beyond.</summary>
    internal static SpeakerRole[]? Vorbis(int channels) => channels switch
    {
        1 => new[] { C },
        2 => new[] { FL, FR },
        3 => new[] { FL, C, FR },
        4 => new[] { FL, FR, L, R },
        5 => new[] { FL, C, FR, L, R },
        6 => new[] { FL, C, FR, L, R, LFE },
        7 => new[] { FL, C, FR, L, R, C, LFE },
        8 => new[] { FL, C, FR, L, R, L, R, LFE },
        _ => null,
    };

    /// <summary>
    /// The WAVE channel order from a speaker mask (SPEAKER_FRONT_LEFT = 1 and so on); with no mask,
    /// Windows' default layout for the channel count.
    /// </summary>
    internal static SpeakerRole[]? Wave(int channels, uint mask)
    {
        if (mask == 0)
        {
            mask = channels switch
            {
                1 => 0x4u,      // mono: front centre
                2 => 0x3u,
                3 => 0x7u,      // FL FR FC
                4 => 0x33u,     // quad: FL FR BL BR
                5 => 0x37u,     // FL FR FC BL BR
                6 => 0x3Fu,     // 5.1
                7 => 0x13Fu,    // 6.1: 5.1 + back centre
                8 => 0x63Fu,    // 7.1: 5.1 + side left/right
                _ => 0u,
            };
        }
        var roles = new List<SpeakerRole>();
        for (int bit = 0; bit < 32 && roles.Count < channels; bit++)
        {
            if ((mask & (1u << bit)) == 0) continue;
            roles.Add(bit switch
            {
                0 => FL,           // front left
                1 => FR,           // front right
                2 => C,            // front centre
                3 => LFE,
                4 or 6 or 9 or 12 or 15 => L,    // back left, front left of centre, side left, top front left, top back left
                5 or 7 or 10 or 14 or 17 => R,   // back right, front right of centre, side right, top front right, top back right
                _ => C,            // back centre, top centre, top front centre, top back centre
            });
        }
        return roles.Count == channels ? roles.ToArray() : null;
    }
}
