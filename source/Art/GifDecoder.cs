namespace NocturneFlatScroll;

/// <summary>
/// A bounded GIF decoder for custom enemy art. Scan reads the block structure (sizes, frames,
/// delays) without decompressing anything; Decode composites frame by frame into one reused RGBA
/// canvas (top row first) and hands each frame to a callback, so no list of full frames is kept.
/// It works like browsers do: disposal 2 clears the frame's rectangle to transparent, disposal 3
/// puts back the canvas from before the frame, delays of 0 or 1 hundredths play at 100 ms,
/// frames are clipped to the logical screen, and a GIF that ends early keeps its whole frames.
/// Every limit is checked before anything big is allocated.
/// This file has no Unity or game dependencies, so it runs on a worker thread.
/// </summary>
internal static class GifDecoder
{
    internal sealed class Limits
    {
        /// <summary>The logical screen, and each frame's rectangle, at most this on a side.</summary>
        internal int MaxSide = 2048;
        /// <summary>Frames read; the rest are left out.</summary>
        internal int MaxFrames = 1000;
        /// <summary>Composited pixels (frames x screen), and frame rectangles in all.</summary>
        internal long MaxPixels = 128L * 1024 * 1024;
    }

    /// <summary>Why reading stopped before the GIF's end.</summary>
    internal enum Stop { None, Cut, Frames, Pixels }

    internal sealed class Info
    {
        internal int Width, Height, Frames;
        /// <summary>0 = forever (NETSCAPE2.0), else how many times it plays.</summary>
        internal int LoopCount = 1;
        internal int[] DelaysMs = Array.Empty<int>();
        internal int TotalMs;
        /// <summary>Some frame has a transparent colour.</summary>
        internal bool Transparent;
        internal Stop Stopped;
        /// <summary>Only the start of the file was given, and it ended before the GIF did.</summary>
        internal bool Partial;
    }

    private struct FrameHeader
    {
        internal int X, Y, W, H, Disposal, Transparent, DelayCs;
        internal bool Interlaced;
        internal int PaletteOffset, PaletteSize;      // the colour table's offset in the file, and its entries
        internal int DataOffset;                      // the LZW minimum code size byte
    }

    /// <summary>
    /// Reads the structure only. <paramref name="partial"/>: the bytes are only the file's start,
    /// so running out of them isn't a cut. Throws InvalidDataException with a plain reason.
    /// </summary>
    internal static Info Scan(byte[] data, Limits limits, bool partial = false) => Walk(data, limits, partial, null, out _, out _);

    /// <summary>
    /// Decodes the frames in order. onFrame(index, canvas) gets the composited canvas (Width x
    /// Height RGBA, top row first); the array is reused, so copy what is kept. Returning false
    /// stops decoding.
    /// </summary>
    internal static Info Decode(byte[] data, Limits limits, Func<int, byte[], bool> onFrame)
    {
        var frames = new List<FrameHeader>();
        var info = Walk(data, limits, false, frames, out int globalPal, out int globalSize);
        byte[] canvas = new byte[checked(info.Width * info.Height * 4)];
        byte[]? saved = null;
        byte[] indices = Array.Empty<byte>();
        var lzw = new Lzw();
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            int palOff = f.PaletteSize > 0 ? f.PaletteOffset : globalPal;
            int palSize = f.PaletteSize > 0 ? f.PaletteSize : globalSize;
            if (f.Disposal == 3)
            {
                saved ??= new byte[canvas.Length];
                Buffer.BlockCopy(canvas, 0, saved, 0, canvas.Length);
            }
            int count = f.W * f.H;
            if (count > 0)
            {
                if (indices.Length < count) indices = new byte[count];
                int got = lzw.Decode(data, f.DataOffset, indices, count);
                Blit(data, canvas, info.Width, info.Height, f, indices, got, palOff, palSize);
            }
            if (!onFrame(i, canvas)) break;
            if (f.Disposal == 2) ClearRect(canvas, info.Width, info.Height, f);
            else if (f.Disposal == 3 && saved != null) Buffer.BlockCopy(saved, 0, canvas, 0, canvas.Length);
        }
        return info;
    }

    private static Info Walk(byte[] d, Limits limits, bool partial, List<FrameHeader>? keep, out int globalPal, out int globalSize)
    {
        if (d.Length < 13 || d[0] != 'G' || d[1] != 'I' || d[2] != 'F' || d[3] != '8' || (d[4] != '7' && d[4] != '9') || d[5] != 'a')
            throw new InvalidDataException("isn't a GIF");
        var info = new Info();
        int sw = d[6] | d[7] << 8, sh = d[8] | d[9] << 8;
        if (sw > limits.MaxSide || sh > limits.MaxSide)
            throw new InvalidDataException($"is a {sw} x {sh} GIF; GIFs can be at most {limits.MaxSide} on a side");
        int flags = d[10];
        int p = 13;
        globalPal = 0;
        globalSize = 0;
        if ((flags & 0x80) != 0)
        {
            globalSize = 2 << (flags & 7);
            globalPal = p;
            p += 3 * globalSize;
            if (p > d.Length) throw new InvalidDataException("is cut off inside its colour table");
        }
        long area = (long)sw * sh, rects = 0;
        int maxRight = 0, maxBottom = 0;
        var frames = keep ?? new List<FrameHeader>();
        int disposal = 0, transparent = -1, delay = 0;
        while (true)
        {
            if (p >= d.Length) { End(info, partial); break; }
            byte b = d[p++];
            if (b == 0x3B) break;                          // trailer
            if (b == 0x21)                                 // extension
            {
                if (p >= d.Length) { End(info, partial); break; }
                byte label = d[p++];
                if (label == 0xF9 && p + 4 < d.Length && d[p] >= 4)
                {
                    int pk = d[p + 1];
                    disposal = (pk >> 2) & 7;
                    if (disposal > 3) disposal = 0;
                    delay = d[p + 2] | d[p + 3] << 8;
                    transparent = (pk & 1) != 0 ? d[p + 4] : -1;
                }
                else if (label == 0xFF && p + 16 < d.Length && d[p] == 11 && Matches(d, p + 1, "NETSCAPE2.0") && d[p + 12] >= 3 && d[p + 13] == 1)
                    info.LoopCount = d[p + 14] | d[p + 15] << 8;
                if (!SkipSubBlocks(d, ref p)) { End(info, partial); break; }
                continue;
            }
            if (b != 0x2C) { info.Stopped = Stop.Cut; break; } // an unknown block: browsers stop there
            if (p + 9 > d.Length) { End(info, partial); break; }
            var f = new FrameHeader
            {
                X = d[p] | d[p + 1] << 8, Y = d[p + 2] | d[p + 3] << 8,
                W = d[p + 4] | d[p + 5] << 8, H = d[p + 6] | d[p + 7] << 8,
                Disposal = disposal, Transparent = transparent, DelayCs = delay,
            };
            int lf = d[p + 8];
            p += 9;
            f.Interlaced = (lf & 0x40) != 0;
            if ((lf & 0x80) != 0)
            {
                f.PaletteSize = 2 << (lf & 7);
                f.PaletteOffset = p;
                p += 3 * f.PaletteSize;
                if (p > d.Length) { End(info, partial); break; }
            }
            if (p >= d.Length) { End(info, partial); break; }
            f.DataOffset = p;
            p++;                                           // the LZW minimum code size
            bool whole = SkipSubBlocks(d, ref p);
            disposal = 0; transparent = -1; delay = 0;     // a control block is for one frame only
            // A frame cut off in the middle isn't shown.
            if (!whole) { End(info, partial); break; }
            // Decoding work goes by frame rectangle, not by screen, so both are bounded.
            if ((long)f.W * f.H > (long)limits.MaxSide * limits.MaxSide)
                throw new InvalidDataException($"has a {f.W} x {f.H} frame; GIFs can be at most {limits.MaxSide} on a side");
            if (frames.Count >= limits.MaxFrames) { info.Stopped = Stop.Frames; break; }
            rects += (long)f.W * f.H;
            if (rects > limits.MaxPixels || (area > 0 && (frames.Count + 1) * area > limits.MaxPixels)) { info.Stopped = Stop.Pixels; break; }
            if (f.Transparent >= 0) info.Transparent = true;
            frames.Add(f);
            maxRight = Math.Max(maxRight, Math.Min(f.X + f.W, 65535));
            maxBottom = Math.Max(maxBottom, Math.Min(f.Y + f.H, 65535));
        }
        if (frames.Count == 0)
        {
            if (info.Partial) { info.Width = sw; info.Height = sh; return info; }
            throw new InvalidDataException(info.Stopped == Stop.Cut ? "is cut off before its first picture ends" : "has no pictures");
        }
        // A zero logical screen happens in the wild; it's sized from the frames then.
        info.Width = sw > 0 ? sw : maxRight;
        info.Height = sh > 0 ? sh : maxBottom;
        if (info.Width < 1 || info.Height < 1) throw new InvalidDataException("has no size");
        if (info.Width > limits.MaxSide || info.Height > limits.MaxSide)
            throw new InvalidDataException($"is a {info.Width} x {info.Height} GIF; GIFs can be at most {limits.MaxSide} on a side");
        // A screen sized from the frames is only known now.
        long canvas = (long)info.Width * info.Height;
        if (frames.Count * canvas > limits.MaxPixels)
        {
            int fit = (int)Math.Max(1, limits.MaxPixels / canvas);
            frames.RemoveRange(fit, frames.Count - fit);
            info.Stopped = Stop.Pixels;
        }
        info.Frames = frames.Count;
        info.DelaysMs = new int[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            int ms = DelayMs(frames[i].DelayCs);
            info.DelaysMs[i] = ms;
            info.TotalMs += ms;
        }
        return info;
    }

    /// <summary>Browsers show a delay of 0 or 1 hundredths as 100 ms.</summary>
    private static int DelayMs(int cs) => cs < 2 ? 100 : cs * 10;

    // Running out of bytes: a cut GIF, or just the end of the part that was read.
    private static void End(Info info, bool partial)
    {
        if (partial) info.Partial = true;
        else info.Stopped = Stop.Cut;
    }

    private static bool Matches(byte[] d, int p, string s)
    {
        for (int i = 0; i < s.Length; i++) if (d[p + i] != s[i]) return false;
        return true;
    }

    // Skips data sub-blocks up to and including the zero terminator; false if the file ends first.
    private static bool SkipSubBlocks(byte[] d, ref int p)
    {
        while (p < d.Length)
        {
            int n = d[p++];
            if (n == 0) return true;
            p += n;
        }
        p = d.Length;
        return false;
    }

    private static void Blit(byte[] d, byte[] canvas, int cw, int ch, in FrameHeader f, byte[] idx, int got, int palOff, int palSize)
    {
        int row = 0, pass = 0, step = f.Interlaced ? 8 : 1;
        for (int r = 0; r < f.H; r++)
        {
            int y = f.Y + row;
            int src = r * f.W;
            if (src >= got) break;                         // the rest never decoded: the canvas stays
            if (y < ch)
            {
                int n = Math.Min(f.W, got - src);
                for (int c = 0; c < n; c++)
                {
                    int x = f.X + c;
                    if (x >= cw) break;
                    int k = idx[src + c];
                    if (k == f.Transparent) continue;
                    int o = (y * cw + x) * 4;
                    if (k < palSize)
                    {
                        int q = palOff + 3 * k;
                        canvas[o] = d[q]; canvas[o + 1] = d[q + 1]; canvas[o + 2] = d[q + 2];
                    }
                    else { canvas[o] = 0; canvas[o + 1] = 0; canvas[o + 2] = 0; } // past the table (or no table): black
                    canvas[o + 3] = 255;
                }
            }
            if (f.Interlaced)
            {
                row += step;
                while (row >= f.H && pass < 3)
                {
                    pass++;
                    row = pass == 1 ? 4 : pass == 2 ? 2 : 1;
                    step = pass == 1 ? 8 : pass == 2 ? 4 : 2;
                }
            }
            else row++;
        }
    }

    private static void ClearRect(byte[] canvas, int cw, int ch, in FrameHeader f)
    {
        int x1 = Math.Min(cw, f.X + f.W), y1 = Math.Min(ch, f.Y + f.H);
        for (int y = f.Y; y < y1; y++)
            if (f.X < x1) Array.Clear(canvas, (y * cw + f.X) * 4, (x1 - f.X) * 4);
    }

    /// <summary>GIF LZW with 12-bit codes, read straight from the sub-blocks.</summary>
    private sealed class Lzw
    {
        private readonly short[] prefix = new short[4096];
        private readonly byte[] suffix = new byte[4096];
        private readonly byte[] first = new byte[4096];
        private readonly byte[] stack = new byte[4097];

        // Returns how many indices were written (up to count). Stops at the end code, at the end of
        // the data, or at a code that can't be valid; never reads or writes out of bounds.
        internal int Decode(byte[] d, int p, byte[] output, int count)
        {
            if (p >= d.Length) return 0;
            int minCode = d[p++];
            if (minCode < 1 || minCode > 11) return 0;
            int clear = 1 << minCode, end = clear + 1;
            int size = minCode + 1, mask = (1 << size) - 1, next = end + 1;
            int old = -1, written = 0;
            int acc = 0, bits = 0, blockLeft = 0;
            for (int i = 0; i < clear; i++) { prefix[i] = -1; suffix[i] = (byte)i; first[i] = (byte)i; }
            while (written < count)
            {
                while (bits < size)
                {
                    if (blockLeft == 0)
                    {
                        if (p >= d.Length) return written;
                        blockLeft = d[p++];
                        if (blockLeft == 0) return written;  // the data ended without an end code
                    }
                    if (p >= d.Length) return written;
                    acc |= d[p++] << bits;
                    bits += 8;
                    blockLeft--;
                }
                int code = acc & mask;
                acc >>= size;
                bits -= size;
                if (code == clear)
                {
                    size = minCode + 1; mask = (1 << size) - 1; next = end + 1; old = -1;
                    continue;
                }
                if (code == end) return written;
                if (old == -1)
                {
                    if (code >= clear) return written;       // the first code after a clear must be a literal
                    output[written++] = (byte)code;
                    old = code;
                    continue;
                }
                int top = 0, cur = code;
                byte firstChar;
                if (code < next) firstChar = first[code];
                else if (code == next) { firstChar = first[old]; stack[top++] = firstChar; cur = old; }
                else return written;                         // a code from the future: damaged data
                while (cur >= clear) { stack[top++] = suffix[cur]; cur = prefix[cur]; }
                stack[top++] = (byte)cur;
                while (top > 0 && written < count) output[written++] = stack[--top];
                if (next < 4096)
                {
                    prefix[next] = (short)old;
                    suffix[next] = firstChar;
                    first[next] = first[old];
                    next++;
                    if (next > mask && size < 12) { size++; mask = (1 << size) - 1; }
                }
                old = code;
            }
            return written;
        }
    }
}
