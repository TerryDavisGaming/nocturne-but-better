namespace NocturneFlatScroll;

/// <summary>
/// Decodes standard Vorbis I audio packets to planar float PCM, the way libvorbis and ffmpeg do:
/// the first packet only primes the overlap, and every later one finishes
/// (previous block size + current block size) / 4 frames. The overlap between two blocks comes
/// from the actual sizes of the neighbouring blocks (what ffmpeg does); in a valid stream that is
/// what the packets' window flags say too. Not thread safe; one decoder per stream.
/// </summary>
internal sealed class VorbisDecoder
{
    private readonly VorbisSetup setup;
    private readonly int channels;
    private readonly int bs0, bs1;
    private readonly VorbisImdct imdct0, imdct1;
    private readonly float[] slope0, slope1;   // rising window halves for overlaps of bs0/2 and bs1/2
    private readonly VorbisBitReader br = new();

    private readonly float[][] spectrum;   // per channel, bs1/2
    private readonly float[][] block;      // per channel, bs1 (IMDCT output)
    private readonly float[][] prevRight;  // per channel, bs1/2: right half of the previous block, unwindowed
    private readonly float[][] output;     // per channel, bs1/2: the frames the last packet finished
    private readonly int[][] fit;          // per channel floor1 post values
    private readonly bool[] floorUsed;
    private readonly bool[] noResidue;
    private readonly float[][] bundle;
    private readonly bool[] bundleSkip;
    private int[] classWords = Array.Empty<int>();
    private int[] partEntries = Array.Empty<int>();
    private int prevN;                     // size of the previous block, 0 before the first

    /// <summary>The frames the last <see cref="Decode"/> finished, one array per channel.</summary>
    internal float[][] Output => output;

    internal VorbisDecoder(int channels, int blockSize0, int blockSize1, VorbisSetup setup)
    {
        this.setup = setup;
        this.channels = channels;
        bs0 = blockSize0;
        bs1 = blockSize1;
        imdct0 = new VorbisImdct(bs0);
        imdct1 = bs1 == bs0 ? imdct0 : new VorbisImdct(bs1);
        slope0 = MakeSlope(bs0 / 2);
        slope1 = MakeSlope(bs1 / 2);

        int half1 = bs1 / 2;
        int maxPosts = 2;
        foreach (var f in setup.Floors) maxPosts = Math.Max(maxPosts, f.Posts);
        spectrum = NewJagged<float>(channels, half1);
        block = NewJagged<float>(channels, bs1);
        prevRight = NewJagged<float>(channels, half1);
        output = NewJagged<float>(channels, half1);
        fit = NewJagged<int>(channels, maxPosts);
        floorUsed = new bool[channels];
        noResidue = new bool[channels];
        bundle = new float[channels][];
        bundleSkip = new bool[channels];
    }

    private static T[][] NewJagged<T>(int a, int b)
    {
        var r = new T[a][];
        for (int i = 0; i < a; i++) r[i] = new T[b];
        return r;
    }

    /// <summary>The Vorbis window slope: w[i] = sin(pi/2 * sin^2((i + 0.5) / n * pi/2)), i &lt; n.</summary>
    private static float[] MakeSlope(int n)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++)
        {
            double s = Math.Sin((i + 0.5) / n * Math.PI / 2);
            w[i] = (float)Math.Sin(Math.PI / 2 * s * s);
        }
        return w;
    }

    /// <summary>Forgets the previous block, so the next packet only primes the overlap again (after a gap).</summary>
    internal void Reset() => prevN = 0;

    /// <summary>
    /// Decodes one audio packet. Returns the number of frames it finished (in <see cref="Output"/>),
    /// or -1 when it isn't a decodable audio packet; that one is skipped, as libvorbis does.
    /// </summary>
    internal int Decode(byte[] data, int offset, int length)
    {
        br.Init(data, offset, length);
        if (br.Read(1) != 0) return -1;              // a header packet, or empty
        int modeNum = br.Read(setup.ModeBits);
        if (modeNum < 0 || modeNum >= setup.Modes.Length) return -1;
        var mode = setup.Modes[modeNum];
        if (mode.BlockFlag)
        {
            // The previous and next window flags; the real neighbours decide the overlap instead.
            br.Read(1);
            if (br.Read(1) < 0) return -1;
        }
        int n = mode.BlockFlag ? bs1 : bs0;
        int n2 = n / 2;
        var mapping = setup.Mappings[mode.Mapping];

        // ---- floors ----
        for (int c = 0; c < channels; c++)
        {
            var floor = setup.Floors[mapping.SubmapFloor[mapping.Mux[c]]];
            floorUsed[c] = DecodeFloor(floor, fit[c]);
            noResidue[c] = !floorUsed[c];
            Array.Clear(spectrum[c], 0, n2);
        }

        // ---- a coupled channel with audio keeps its partner's residue ----
        for (int i = 0; i < mapping.CouplingSteps; i++)
        {
            int m = mapping.Magnitude[i], a = mapping.Angle[i];
            if (!noResidue[m] || !noResidue[a]) { noResidue[m] = false; noResidue[a] = false; }
        }

        // ---- residues ----
        for (int s = 0; s < mapping.Submaps; s++)
        {
            int count = 0;
            for (int c = 0; c < channels; c++)
            {
                if (mapping.Mux[c] != s) continue;
                bundle[count] = spectrum[c];
                bundleSkip[count] = noResidue[c];
                count++;
            }
            DecodeResidue(setup.Residues[mapping.SubmapResidue[s]], count, n2);
        }

        // ---- inverse channel coupling (square polar mapping) ----
        for (int i = mapping.CouplingSteps - 1; i >= 0; i--)
        {
            float[] mag = spectrum[mapping.Magnitude[i]];
            float[] ang = spectrum[mapping.Angle[i]];
            for (int j = 0; j < n2; j++)
            {
                float m = mag[j], a = ang[j];
                if (m > 0)
                {
                    if (a > 0) { ang[j] = m - a; }
                    else { ang[j] = m; mag[j] = m + a; }
                }
                else
                {
                    if (a > 0) { ang[j] = m + a; }
                    else { ang[j] = m; mag[j] = m - a; }
                }
            }
        }

        // ---- floor curve times residue, then the IMDCT ----
        var imdct = mode.BlockFlag ? imdct1 : imdct0;
        for (int c = 0; c < channels; c++)
        {
            if (!floorUsed[c])
            {
                Array.Clear(block[c], 0, n);
                continue;
            }
            var floor = setup.Floors[mapping.SubmapFloor[mapping.Mux[c]]];
            ApplyFloor(floor, fit[c], spectrum[c], n2);
            imdct.Inverse(spectrum[c], block[c]);
        }

        return Overlap(n);
    }

    /// <summary>
    /// Overlap-adds the current block (size n) with the saved right half of the previous one. The
    /// output runs from the previous block's centre to the current block's centre: prevN/4 + n/4
    /// frames, with an overlap of min(prevN, n)/2 frames.
    /// </summary>
    private int Overlap(int n)
    {
        int p = prevN;
        int outLen = 0;
        if (p != 0)
        {
            outLen = p / 4 + n / 4;
            int ov = Math.Min(p, n) / 2;
            int ovStart = (p - Math.Min(p, n)) / 4;
            int ovEnd = ovStart + ov;
            int shift = n / 4 - p / 4; // index into the current block = output index + shift
            float[] win = ov == bs1 / 2 ? slope1 : slope0;
            for (int c = 0; c < channels; c++)
            {
                float[] prev = prevRight[c], cur = block[c], o = output[c];
                for (int i = 0; i < ovStart; i++) o[i] = prev[i];
                for (int i = ovStart, t = 0; i < ovEnd; i++, t++)
                    o[i] = prev[i] * win[ov - 1 - t] + cur[i + shift] * win[t];
                for (int i = ovEnd; i < outLen; i++) o[i] = cur[i + shift];
            }
        }
        int h = n / 2;
        for (int c = 0; c < channels; c++) Array.Copy(block[c], h, prevRight[c], 0, h);
        prevN = n;
        return outLen;
    }

    // ---------------------------------------------------------------- floor 1

    /// <summary>libvorbis' floor1_inverse1: reads the posts and unwraps them. Bit 15 marks an unused post.</summary>
    private bool DecodeFloor(VorbisFloor1 f, int[] fitValues)
    {
        if (br.Read(1) != 1) return false;
        var books = setup.Codebooks;
        fitValues[0] = br.Read(f.YBits);
        fitValues[1] = br.Read(f.YBits);
        if (fitValues[1] < 0) return false;
        int j = 2;
        for (int i = 0; i < f.Partitions; i++)
        {
            int cls = f.PartitionClass[i];
            int cdim = f.ClassDim[cls];
            int cbits = f.ClassSubBits[cls];
            int csubMask = (1 << cbits) - 1;
            int cval = 0;
            if (cbits > 0)
            {
                cval = books[f.ClassMasterBook[cls]].DecodeScalar(br);
                if (cval < 0) return false;
            }
            int[] subs = f.SubclassBooks[cls];
            for (int k = 0; k < cdim; k++)
            {
                int book = subs[cval & csubMask];
                cval >>= cbits;
                if (book >= 0)
                {
                    int v = books[book].DecodeScalar(br);
                    if (v < 0) return false;
                    fitValues[j + k] = v;
                }
                else fitValues[j + k] = 0;
            }
            j += cdim;
        }

        int range = f.Range;
        int[] x = f.X;
        for (int i = 2; i < f.Posts; i++)
        {
            int lo = f.LowNeighbor[i], hi = f.HighNeighbor[i];
            int predicted = RenderPoint(x[lo], x[hi], fitValues[lo] & 0x7FFF, fitValues[hi] & 0x7FFF, x[i]);
            int hiroom = range - predicted;
            int loroom = predicted;
            int room = (hiroom < loroom ? hiroom : loroom) << 1;
            int val = fitValues[i];
            if (val != 0)
            {
                if (val >= room) val = hiroom > loroom ? val - loroom : -1 - (val - hiroom);
                else val = (val & 1) != 0 ? -((val + 1) >> 1) : val >> 1;
                fitValues[i] = (val + predicted) & 0x7FFF;
                fitValues[lo] &= 0x7FFF;
                fitValues[hi] &= 0x7FFF;
            }
            else
            {
                fitValues[i] = predicted | 0x8000;
            }
        }
        return true;
    }

    private static int RenderPoint(int x0, int x1, int y0, int y1, int x)
    {
        int dy = y1 - y0;
        int adx = x1 - x0;
        if (adx == 0) return y0;
        int ady = Math.Abs(dy);
        int err = ady * (x - x0);
        int off = err / adx;
        return dy < 0 ? y0 - off : y0 + off;
    }

    /// <summary>libvorbis' floor1_inverse2: draws the floor curve and multiplies it into the spectrum.</summary>
    private static void ApplyFloor(VorbisFloor1 f, int[] fitValues, float[] vec, int n)
    {
        int mult = f.Multiplier;
        int lx = 0, hx = 0;
        int ly = Clamp255(fitValues[0] * mult);
        int[] order = f.SortedOrder;
        for (int j = 1; j < f.Posts; j++)
        {
            int cur = order[j];
            int v = fitValues[cur];
            int hy = v & 0x7FFF;
            if (hy != v) continue; // post not used
            hx = f.X[cur];
            hy = Clamp255(hy * mult);
            RenderLine(n, lx, hx, ly, hy, vec);
            lx = hx;
            ly = hy;
        }
        float tail = VorbisTables.Floor1InverseDb[ly];
        for (int j = hx; j < n; j++) vec[j] *= tail;
    }

    private static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    private static void RenderLine(int n, int x0, int x1, int y0, int y1, float[] d)
    {
        int adx = x1 - x0;
        if (adx <= 0) return;
        int dy = y1 - y0;
        int ady = Math.Abs(dy);
        int bas = dy / adx;
        int sy = dy < 0 ? bas - 1 : bas + 1;
        int x = x0, y = y0, err = 0;
        ady -= Math.Abs(bas * adx);
        if (n > x1) n = x1;
        float[] t = VorbisTables.Floor1InverseDb;
        if (x < n) d[x] *= t[y];
        while (++x < n)
        {
            err += ady;
            if (err >= adx) { err -= adx; y += sy; }
            else y += bas;
            d[x] *= t[y];
        }
    }

    // ---------------------------------------------------------------- residue

    private void DecodeResidue(VorbisResidue r, int chCount, int n2)
    {
        if (chCount == 0) return;
        var books = setup.Codebooks;
        var classBook = books[r.ClassBook];
        int perWord = classBook.Dimensions;
        int psize = r.PartitionSize;

        if (r.Type == 2)
        {
            // Type 2: the channels interleaved into one vector of n2 * ch values, decoded like type 1.
            bool any = false;
            for (int c = 0; c < chCount; c++) if (!bundleSkip[c]) { any = true; break; }
            if (!any) return;
            int total = n2 * chCount;
            int end = Math.Min(r.End, total);
            int len = end - r.Begin;
            if (len <= 0) return;
            int partvals = len / psize;
            int partwords = (partvals + perWord - 1) / perWord;
            EnsureClassWords(partwords);
            for (int pass = 0; pass < 8; pass++)
            {
                for (int i = 0, l = 0; i < partvals; l++)
                {
                    if (pass == 0)
                    {
                        int temp = classBook.DecodeScalar(br);
                        if (temp < 0 || temp >= r.ClassWords) return;
                        classWords[l] = temp;
                    }
                    int[] map = r.DecodeMap[classWords[l]];
                    for (int k = 0; k < perWord && i < partvals; k++, i++)
                    {
                        int book = r.Books[map[k]][pass];
                        if (book < 0) continue;
                        if (!DecodeInterleaved(books[book], r.Begin + i * psize, psize, chCount)) return;
                    }
                }
            }
            return;
        }

        {
            int end = Math.Min(r.End, n2);
            int len = end - r.Begin;
            if (len <= 0) return;
            int partvals = len / psize;
            int partwords = (partvals + perWord - 1) / perWord;
            EnsureClassWords(partwords * chCount);
            for (int pass = 0; pass < 8; pass++)
            {
                for (int i = 0, l = 0; i < partvals; l++)
                {
                    if (pass == 0)
                    {
                        for (int c = 0; c < chCount; c++)
                        {
                            if (bundleSkip[c]) continue;
                            int temp = classBook.DecodeScalar(br);
                            if (temp < 0 || temp >= r.ClassWords) return;
                            classWords[c * partwords + l] = temp;
                        }
                    }
                    for (int k = 0; k < perWord && i < partvals; k++, i++)
                    {
                        int offset = r.Begin + i * psize;
                        for (int c = 0; c < chCount; c++)
                        {
                            if (bundleSkip[c]) continue;
                            int cls = r.DecodeMap[classWords[c * partwords + l]][k];
                            int book = r.Books[cls][pass];
                            if (book < 0) continue;
                            bool ok = r.Type == 0
                                ? DecodePartType0(books[book], bundle[c], offset, psize)
                                : DecodePartType1(books[book], bundle[c], offset, psize);
                            if (!ok) return;
                        }
                    }
                }
            }
        }
    }

    private void EnsureClassWords(int n)
    {
        if (classWords.Length < n) classWords = new int[n];
    }

    /// <summary>A residue 0 partition (libvorbis' decodevs_add: every entry is read before adding).</summary>
    private bool DecodePartType0(VorbisCodebook book, float[] v, int offset, int n)
    {
        int dim = book.Dimensions;
        int step = n / dim;
        if (partEntries.Length < step) partEntries = new int[step];
        for (int i = 0; i < step; i++)
        {
            int e = book.DecodeScalar(br);
            if (e < 0) return false;
            partEntries[i] = e * dim;
        }
        float[] vq = book.Vq!;
        for (int i = 0; i < step; i++)
        {
            int e = partEntries[i];
            for (int j = 0, o = offset + i; j < dim; j++, o += step) v[o] += vq[e + j];
        }
        return true;
    }

    /// <summary>A residue 1 partition (libvorbis' decodev_add).</summary>
    private bool DecodePartType1(VorbisCodebook book, float[] v, int offset, int n)
    {
        int dim = book.Dimensions;
        float[] vq = book.Vq!;
        int i = offset, end = offset + n;
        while (i < end)
        {
            int e = book.DecodeScalar(br);
            if (e < 0) return false;
            e *= dim;
            for (int j = 0; j < dim && i < end; j++) v[i++] += vq[e + j];
        }
        return true;
    }

    /// <summary>A residue 2 partition: a type 1 decode into the channel-interleaved vector.</summary>
    private bool DecodeInterleaved(VorbisCodebook book, int offset, int n, int ch)
    {
        int dim = book.Dimensions;
        float[] vq = book.Vq!;
        int c = offset % ch, idx = offset / ch;
        int remaining = n;
        float[][] vecs = bundle;
        while (remaining > 0)
        {
            int e = book.DecodeScalar(br);
            if (e < 0) return false;
            e *= dim;
            for (int j = 0; j < dim && remaining > 0; j++, remaining--)
            {
                vecs[c][idx] += vq[e + j];
                if (++c == ch) { c = 0; idx++; }
            }
        }
        return true;
    }
}
