namespace NocturneFlatScroll;

/// <summary>
/// A starting grid for the creator's Sprite sheet choice, guessed from the clear lines between a
/// sheet's frames: the most columns and rows whose cell edges all fall on clear lines (alpha
/// under 8, or the corner colour on a sheet without see-through pixels), with cells of at least
/// 8 px and at least 3/4 of them holding something. It's only ever offered as a starting value
/// the player can change; a picture chosen as an Image is never cut up. Also finds which cells
/// of a grid hold anything. Bounded: at most 16 column and 16 row counts are tried, each a pass
/// over the picture. This file has no Unity or game dependencies.
/// </summary>
internal static class SheetGuess
{
    internal const int MinCell = 8;
    private const int ClearAlpha = 8, CornerTolerance = 12, MaxTries = 16;

    internal readonly record struct Grid(int Columns, int Rows, int First, int Frames);

    /// <summary>The guessed grid, with the first cell holding anything and the frames up to the last one; 1 x 1 when nothing fits.</summary>
    internal static Grid Guess(Picture p)
    {
        int w = p.Width, h = p.Height;
        var solid = Solid(p);
        var colSolid = new bool[w];
        var rowSolid = new bool[h];
        for (int y = 0; y < h; y++)
            for (int x = 0, i = y * w; x < w; x++, i++)
                if (solid[i]) { colSolid[x] = true; rowSolid[y] = true; }
        var bands = new Dictionary<int, bool[]>();
        int bestC = 1, bestR = 1;
        foreach (int c in Counts(w, colSolid))
            foreach (int r in Counts(h, rowSolid))
            {
                if (c * r <= 1) continue;
                if (Filled(solid, w, h, c, r, bands) < Math.Max(2, 0.75 * c * r)) continue;
                if (c * r > bestC * bestR) (bestC, bestR) = (c, r);
                break;
            }
        var (first, last) = Used(solid, w, h, bestC, bestR, bands);
        return first < 0 ? new Grid(bestC, bestR, 0, 1) : new Grid(bestC, bestR, first, last - first + 1);
    }

    /// <summary>The first and last cells (left to right, then down) holding anything in a grid; (-1, -1) when none do.</summary>
    internal static (int First, int Last) UsedCells(Picture p, int columns, int rows)
    {
        if (columns < 1 || rows < 1 || p.Width / columns < 1 || p.Height / rows < 1) return (-1, -1);
        return Used(Solid(p), p.Width, p.Height, columns, rows, new Dictionary<int, bool[]>());
    }

    // Which pixels hold something: alpha of at least 8, and, when the corner pixel is solid (a sheet
    // on a flat background), not within a few steps of the corner's colour.
    private static bool[] Solid(Picture p)
    {
        var rgba = p.Rgba;
        int n = p.Width * p.Height;
        var solid = new bool[n];
        bool flat = n > 0 && rgba[3] >= ClearAlpha;
        int cr = rgba.Length >= 4 ? rgba[0] : 0, cg = rgba.Length >= 4 ? rgba[1] : 0, cb = rgba.Length >= 4 ? rgba[2] : 0;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            if (rgba[o + 3] < ClearAlpha) continue;
            if (flat && Math.Abs(rgba[o] - cr) <= CornerTolerance && Math.Abs(rgba[o + 1] - cg) <= CornerTolerance && Math.Abs(rgba[o + 2] - cb) <= CornerTolerance) continue;
            solid[i] = true;
        }
        return solid;
    }

    // The counts that split a side evenly into cells of at least MinCell whose edges all fall on a
    // clear line (the line on either side of the edge), largest first.
    private static List<int> Counts(int n, bool[] solidLine)
    {
        var counts = new List<int>();
        for (int c = Math.Min(EnemyArtReader.MaxGrid, n / MinCell); c >= 1 && counts.Count < MaxTries; c--)
        {
            if (n % c != 0) continue;
            int cell = n / c;
            bool edges = true;
            for (int k = 1; k < c && edges; k++) edges = !solidLine[k * cell] || !solidLine[k * cell - 1];
            if (edges) counts.Add(c);
        }
        if (counts.Count == 0) counts.Add(1);
        return counts;
    }

    // For a row count: whether each column has something in each band of rows (worked out once per count).
    private static bool[] Band(bool[] solid, int w, int h, int rows, Dictionary<int, bool[]> bands)
    {
        if (bands.TryGetValue(rows, out var known)) return known;
        int ch = h / rows;
        var band = new bool[rows * w];
        for (int y = 0; y < rows * ch; y++)
        {
            int j = y / ch;
            for (int x = 0, i = y * w; x < w; x++, i++)
                if (solid[i]) band[j * w + x] = true;
        }
        return bands[rows] = band;
    }

    private static bool CellUsed(bool[] band, int w, int cw, int column, int row)
    {
        for (int x = column * cw, end = x + cw; x < end; x++)
            if (band[row * w + x]) return true;
        return false;
    }

    private static int Filled(bool[] solid, int w, int h, int columns, int rows, Dictionary<int, bool[]> bands)
    {
        var band = Band(solid, w, h, rows, bands);
        int cw = w / columns, filled = 0;
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < columns; i++)
                if (CellUsed(band, w, cw, i, j)) filled++;
        return filled;
    }

    private static (int First, int Last) Used(bool[] solid, int w, int h, int columns, int rows, Dictionary<int, bool[]> bands)
    {
        var band = Band(solid, w, h, rows, bands);
        int cw = w / columns, first = -1, last = -1;
        for (int cell = 0; cell < columns * rows; cell++)
        {
            if (!CellUsed(band, w, cw, cell % columns, cell / columns)) continue;
            if (first < 0) first = cell;
            last = cell;
        }
        return (first, last);
    }
}
