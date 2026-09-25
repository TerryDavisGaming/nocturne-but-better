namespace NocturneFlatScroll;

internal sealed class VorbisFloor1
{
    internal int Partitions;
    internal int[] PartitionClass = Array.Empty<int>();
    internal int[] ClassDim = Array.Empty<int>();
    internal int[] ClassSubBits = Array.Empty<int>();
    internal int[] ClassMasterBook = Array.Empty<int>();
    internal int[][] SubclassBooks = Array.Empty<int[]>(); // -1 = unused
    internal int Multiplier;
    internal int RangeBits;
    internal int[] X = Array.Empty<int>();                 // post positions in header order
    internal int Posts;
    internal int[] SortedOrder = Array.Empty<int>();       // post indices sorted by X
    internal int[] LowNeighbor = Array.Empty<int>();
    internal int[] HighNeighbor = Array.Empty<int>();
    internal int Range;      // quant_q: 256, 128, 86, 64
    internal int YBits;      // ilog(Range - 1)

    internal void Finish()
    {
        Posts = X.Length;
        SortedOrder = new int[Posts];
        for (int i = 0; i < Posts; i++) SortedOrder[i] = i;
        // Insertion sort by X; there are at most 65 posts.
        for (int i = 1; i < Posts; i++)
        {
            int v = SortedOrder[i], j = i - 1;
            while (j >= 0 && X[SortedOrder[j]] > X[v]) { SortedOrder[j + 1] = SortedOrder[j]; j--; }
            SortedOrder[j + 1] = v;
        }
        for (int i = 1; i < Posts; i++)
            if (X[SortedOrder[i]] == X[SortedOrder[i - 1]]) throw new InvalidDataException("a Vorbis floor repeats a post");
        LowNeighbor = new int[Posts];
        HighNeighbor = new int[Posts];
        for (int i = 2; i < Posts; i++)
        {
            int lo = 0, hi = 1, lx = -1, hx = int.MaxValue;
            for (int j = 0; j < i; j++)
            {
                if (X[j] > lx && X[j] < X[i]) { lx = X[j]; lo = j; }
                if (X[j] < hx && X[j] > X[i]) { hx = X[j]; hi = j; }
            }
            LowNeighbor[i] = lo;
            HighNeighbor[i] = hi;
        }
        Range = new[] { 256, 128, 86, 64 }[Multiplier - 1];
        YBits = VorbisCodebook.ILog(Range - 1);
    }
}

internal sealed class VorbisResidue
{
    internal int Type;
    internal int Begin, End, PartitionSize, Classifications, ClassBook;
    internal int[][] Books = Array.Empty<int[]>();       // [class][pass] -> codebook, -1 = none
    internal int ClassWords;                             // valid classification words
    internal int[][] DecodeMap = Array.Empty<int[]>();   // classification word -> class per partition
}

internal sealed class VorbisMapping
{
    internal int Submaps;
    internal int CouplingSteps;
    internal int[] Magnitude = Array.Empty<int>();
    internal int[] Angle = Array.Empty<int>();
    internal int[] Mux = Array.Empty<int>();             // channel -> submap
    internal int[] SubmapFloor = Array.Empty<int>();
    internal int[] SubmapResidue = Array.Empty<int>();
}

internal struct VorbisMode
{
    internal bool BlockFlag;
    internal int Mapping;
}

/// <summary>The decoder configuration from a standard Vorbis I setup header (the third header packet).</summary>
internal sealed class VorbisSetup
{
    internal VorbisCodebook[] Codebooks = Array.Empty<VorbisCodebook>();
    internal VorbisFloor1[] Floors = Array.Empty<VorbisFloor1>();
    internal VorbisResidue[] Residues = Array.Empty<VorbisResidue>();
    internal VorbisMapping[] Mappings = Array.Empty<VorbisMapping>();
    internal VorbisMode[] Modes = Array.Empty<VorbisMode>();
    internal int ModeBits;

    /// <summary>
    /// The most values the codebooks' VQ tables and the residues' class maps may hold together
    /// (16 MB of floats). Each codebook alone stays under 16M values (libvorbis' limit), but a few
    /// bytes of header can ask for one that big, and a setup can have 256 codebooks and 64
    /// residues. Files from libvorbis and ffmpeg use under 70,000 in all.
    /// </summary>
    internal const long MaxTableValues = 4 * 1024 * 1024;

    /// <summary>Parses the setup header after its "\x05vorbis" signature.</summary>
    internal static VorbisSetup Parse(VorbisBitReader br, int channels)
    {
        var s = new VorbisSetup();

        // ---- codebooks ----
        int cbCount = (int)br.ReadHeader(8) + 1;
        s.Codebooks = new VorbisCodebook[cbCount];
        long totalEntries = 0, tableValues = 0;
        for (int i = 0; i < cbCount; i++)
        {
            s.Codebooks[i] = VorbisCodebook.Read(br, MaxTableValues - tableValues);
            totalEntries += s.Codebooks[i].Entries;
            tableValues += s.Codebooks[i].Vq?.Length ?? 0;
            if (totalEntries > 4 * VorbisCodebook.MaxEntries) throw new InvalidDataException("the Vorbis codebooks are too large");
        }

        // ---- time domain transforms: placeholders, always zero ----
        int timeCount = (int)br.ReadHeader(6) + 1;
        for (int i = 0; i < timeCount; i++)
            if (br.ReadHeader(16) != 0) throw new InvalidDataException("the Vorbis setup has a bad time transform");

        // ---- floors ----
        int floorCount = (int)br.ReadHeader(6) + 1;
        s.Floors = new VorbisFloor1[floorCount];
        for (int i = 0; i < floorCount; i++)
        {
            uint type = br.ReadHeader(16);
            // Floor 0 was only used by encoders from before libvorbis 1.0 (2002).
            if (type == 0) throw new NotSupportedException("it uses Vorbis floor 0 (an encoder from before 2002), which isn't supported; re-encode the song");
            if (type != 1) throw new InvalidDataException("the Vorbis setup has a bad floor type");
            var f = new VorbisFloor1 { Partitions = (int)br.ReadHeader(5) };
            f.PartitionClass = new int[f.Partitions];
            int maxClass = -1;
            for (int j = 0; j < f.Partitions; j++)
            {
                f.PartitionClass[j] = (int)br.ReadHeader(4);
                maxClass = Math.Max(maxClass, f.PartitionClass[j]);
            }
            int classes = maxClass + 1;
            f.ClassDim = new int[classes];
            f.ClassSubBits = new int[classes];
            f.ClassMasterBook = new int[classes];
            f.SubclassBooks = new int[classes][];
            for (int j = 0; j < classes; j++)
            {
                f.ClassDim[j] = (int)br.ReadHeader(3) + 1;
                f.ClassSubBits[j] = (int)br.ReadHeader(2);
                if (f.ClassSubBits[j] != 0)
                {
                    f.ClassMasterBook[j] = (int)br.ReadHeader(8);
                    CheckBook(s, f.ClassMasterBook[j]);
                }
                int subs = 1 << f.ClassSubBits[j];
                f.SubclassBooks[j] = new int[subs];
                for (int k = 0; k < subs; k++)
                {
                    int b = (int)br.ReadHeader(8) - 1;
                    if (b >= 0) CheckBook(s, b);
                    f.SubclassBooks[j][k] = b;
                }
            }
            f.Multiplier = (int)br.ReadHeader(2) + 1;
            f.RangeBits = (int)br.ReadHeader(4);
            int posts = 2;
            for (int j = 0; j < f.Partitions; j++) posts += f.ClassDim[f.PartitionClass[j]];
            if (posts > 65) throw new InvalidDataException("a Vorbis floor has too many posts");
            f.X = new int[posts];
            f.X[0] = 0;
            f.X[1] = 1 << f.RangeBits;
            int p = 2;
            for (int j = 0; j < f.Partitions; j++)
                for (int k = 0; k < f.ClassDim[f.PartitionClass[j]]; k++)
                    f.X[p++] = (int)br.ReadHeader(f.RangeBits);
            f.Finish();
            s.Floors[i] = f;
        }

        // ---- residues ----
        int resCount = (int)br.ReadHeader(6) + 1;
        s.Residues = new VorbisResidue[resCount];
        for (int i = 0; i < resCount; i++)
        {
            var r = new VorbisResidue { Type = (int)br.ReadHeader(16) };
            if (r.Type > 2) throw new InvalidDataException("the Vorbis setup has a bad residue type");
            r.Begin = (int)br.ReadHeader(24);
            r.End = (int)br.ReadHeader(24);
            r.PartitionSize = (int)br.ReadHeader(24) + 1;
            r.Classifications = (int)br.ReadHeader(6) + 1;
            r.ClassBook = (int)br.ReadHeader(8);
            CheckBook(s, r.ClassBook);
            var cascade = new int[r.Classifications];
            for (int j = 0; j < r.Classifications; j++)
            {
                int low = (int)br.ReadHeader(3);
                int high = br.ReadHeader(1) != 0 ? (int)br.ReadHeader(5) : 0;
                cascade[j] = high * 8 + low;
            }
            r.Books = new int[r.Classifications][];
            for (int j = 0; j < r.Classifications; j++)
            {
                r.Books[j] = new int[8];
                for (int k = 0; k < 8; k++)
                {
                    if ((cascade[j] & (1 << k)) != 0)
                    {
                        int b = (int)br.ReadHeader(8);
                        CheckBook(s, b);
                        if (s.Codebooks[b].Vq == null) throw new InvalidDataException("a Vorbis residue book has no vectors");
                        r.Books[j][k] = b;
                    }
                    else r.Books[j][k] = -1;
                }
            }
            // Classification word -> class of each partition (libvorbis' decodemap).
            var classBook = s.Codebooks[r.ClassBook];
            int dim = classBook.Dimensions;
            long words = 1;
            for (int k = 0; k < dim; k++) { words *= r.Classifications; if (words > classBook.Entries) break; }
            r.ClassWords = (int)Math.Min(words, classBook.Entries);
            tableValues += (long)r.ClassWords * dim;
            if (tableValues > MaxTableValues) throw new InvalidDataException("the Vorbis setup's tables are too large");
            r.DecodeMap = new int[r.ClassWords][];
            for (int w = 0; w < r.ClassWords; w++)
            {
                var map = new int[dim];
                int t = w;
                for (int k = dim - 1; k >= 0; k--) { map[k] = t % r.Classifications; t /= r.Classifications; }
                r.DecodeMap[w] = map;
            }
            s.Residues[i] = r;
        }

        // ---- mappings ----
        int mapCount = (int)br.ReadHeader(6) + 1;
        s.Mappings = new VorbisMapping[mapCount];
        int chBits = VorbisCodebook.ILog(channels - 1);
        for (int i = 0; i < mapCount; i++)
        {
            if (br.ReadHeader(16) != 0) throw new InvalidDataException("the Vorbis setup has a bad mapping type");
            var m = new VorbisMapping { Submaps = br.ReadHeader(1) != 0 ? (int)br.ReadHeader(4) + 1 : 1 };
            if (br.ReadHeader(1) != 0)
            {
                m.CouplingSteps = (int)br.ReadHeader(8) + 1;
                m.Magnitude = new int[m.CouplingSteps];
                m.Angle = new int[m.CouplingSteps];
                for (int j = 0; j < m.CouplingSteps; j++)
                {
                    m.Magnitude[j] = (int)br.ReadHeader(chBits);
                    m.Angle[j] = (int)br.ReadHeader(chBits);
                    if (m.Magnitude[j] == m.Angle[j] || m.Magnitude[j] >= channels || m.Angle[j] >= channels)
                        throw new InvalidDataException("the Vorbis setup has bad channel coupling");
                }
            }
            if (br.ReadHeader(2) != 0) throw new InvalidDataException("the Vorbis setup has a nonzero reserved field");
            m.Mux = new int[channels];
            if (m.Submaps > 1)
            {
                for (int j = 0; j < channels; j++)
                {
                    m.Mux[j] = (int)br.ReadHeader(4);
                    if (m.Mux[j] >= m.Submaps) throw new InvalidDataException("the Vorbis setup has a bad channel mux");
                }
            }
            m.SubmapFloor = new int[m.Submaps];
            m.SubmapResidue = new int[m.Submaps];
            for (int j = 0; j < m.Submaps; j++)
            {
                br.ReadHeader(8); // unused time configuration
                m.SubmapFloor[j] = (int)br.ReadHeader(8);
                m.SubmapResidue[j] = (int)br.ReadHeader(8);
                if (m.SubmapFloor[j] >= floorCount || m.SubmapResidue[j] >= resCount)
                    throw new InvalidDataException("the Vorbis setup has a bad submap");
            }
            s.Mappings[i] = m;
        }

        // ---- modes ----
        int modeCount = (int)br.ReadHeader(6) + 1;
        s.Modes = new VorbisMode[modeCount];
        for (int i = 0; i < modeCount; i++)
        {
            s.Modes[i].BlockFlag = br.ReadHeader(1) != 0;
            uint windowType = br.ReadHeader(16), transformType = br.ReadHeader(16);
            s.Modes[i].Mapping = (int)br.ReadHeader(8);
            if (windowType != 0 || transformType != 0 || s.Modes[i].Mapping >= mapCount)
                throw new InvalidDataException("the Vorbis setup has a bad mode");
        }
        s.ModeBits = VorbisCodebook.ILog(modeCount - 1);
        if (br.ReadHeader(1) != 1) throw new InvalidDataException("the Vorbis setup has no framing bit");
        return s;
    }

    private static void CheckBook(VorbisSetup s, int b)
    {
        if ((uint)b >= (uint)s.Codebooks.Length) throw new InvalidDataException("the Vorbis setup names a codebook it doesn't have");
    }
}
