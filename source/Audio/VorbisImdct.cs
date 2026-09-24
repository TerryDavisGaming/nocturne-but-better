namespace NocturneFlatScroll;

/// <summary>
/// The Vorbis inverse MDCT of size N (N/2 coefficients in, N samples out), unscaled as in the spec
/// and libvorbis:
///   y[n] = sum over k of X[k] * cos(2*pi/N * (n + 1/2 + N/4) * (k + 1/2))
/// It runs as a DCT-IV of size N/2 through an N/4-point complex FFT with twiddles before and after,
/// then unfolds by the DCT-IV symmetries. The arithmetic is in double precision.
/// </summary>
internal sealed class VorbisImdct
{
    internal readonly int N;
    private readonly int m, l;
    private readonly double[] preCos, preSin, postCos, postSin;
    private readonly double[] fftCos, fftSin;
    private readonly int[] bitRev;
    private readonly double[] re, im, u;

    internal VorbisImdct(int n)
    {
        if (n < 16 || (n & (n - 1)) != 0) throw new ArgumentException("the IMDCT size must be a power of two of at least 16");
        N = n;
        m = n / 2;
        l = n / 4;
        preCos = new double[l]; preSin = new double[l];
        postCos = new double[l]; postSin = new double[l];
        for (int k = 0; k < l; k++)
        {
            double a = -Math.PI * k / m;                 // exp(-i*pi*k/M)
            preCos[k] = Math.Cos(a); preSin[k] = Math.Sin(a);
            double b = -Math.PI * (k + 0.25) / m;        // exp(-i*pi*(k+1/4)/M)
            postCos[k] = Math.Cos(b); postSin[k] = Math.Sin(b);
        }
        fftCos = new double[l / 2]; fftSin = new double[l / 2];
        for (int k = 0; k < l / 2; k++)
        {
            double a = -2 * Math.PI * k / l;
            fftCos[k] = Math.Cos(a); fftSin[k] = Math.Sin(a);
        }
        int bits = 0;
        while ((1 << bits) < l) bits++;
        bitRev = new int[l];
        for (int i = 0; i < l; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            bitRev[i] = r;
        }
        re = new double[l]; im = new double[l]; u = new double[m];
    }

    /// <summary>x: N/2 spectral coefficients; y: N output samples.</summary>
    internal void Inverse(float[] x, float[] y)
    {
        // z[k] = X[2k] + i*X[M-1-2k], times exp(-i*pi*k/M), stored bit-reversed for the FFT.
        for (int k = 0; k < l; k++)
        {
            double a = x[2 * k], b = x[m - 1 - 2 * k];
            double c = preCos[k], s = preSin[k];
            int r = bitRev[k];
            re[r] = a * c - b * s;
            im[r] = a * s + b * c;
        }

        // In-place radix-2 decimation-in-time FFT (forward, exp(-2*pi*i*jk/L)).
        for (int size = 2; size <= l; size <<= 1)
        {
            int half = size >> 1, step = l / size;
            for (int start = 0; start < l; start += size)
            {
                for (int j = 0, t = 0; j < half; j++, t += step)
                {
                    int p = start + j, q = p + half;
                    double wr = fftCos[t], wi = fftSin[t];
                    double xr = re[q] * wr - im[q] * wi;
                    double xi = re[q] * wi + im[q] * wr;
                    re[q] = re[p] - xr; im[q] = im[p] - xi;
                    re[p] += xr; im[p] += xi;
                }
            }
        }

        // After the twiddle c[j] = T[j] * exp(-i*pi*(j+1/4)/M): u[2j] = Re c, u[M-1-2j] = -Im c.
        for (int j = 0; j < l; j++)
        {
            double c = postCos[j], s = postSin[j];
            double cr = re[j] * c - im[j] * s;
            double ci = re[j] * s + im[j] * c;
            u[2 * j] = cr;
            u[m - 1 - 2 * j] = -ci;
        }

        // Unfold the DCT-IV u (size M) into the N-point IMDCT:
        //   y[n] = U(n + M/2), U(k) = u[k] (k < M), -u[2M-1-k] (M <= k < 2M), -u[k-2M] (k >= 2M).
        int h = m / 2;
        for (int n = 0; n < h; n++) y[n] = (float)u[n + h];
        for (int n = h; n < 3 * h; n++) y[n] = (float)-u[3 * h - 1 - n];
        for (int n = 3 * h; n < 2 * m; n++) y[n] = (float)-u[n - 3 * h];
    }
}
