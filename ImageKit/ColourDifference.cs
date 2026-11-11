using System;
using System.Collections.Generic;

namespace ImageKit;

/// <summary>
/// How different two pictures of the same size look, pixel by pixel, as CIEDE2000 ΔE: about 1 is
/// the smallest difference a trained eye sees side by side, about 2.3 what most people notice.
/// A pixel with transparency is judged over white and over black and the larger ΔE counts, so a
/// changed alpha shows as a difference too.
/// </summary>
public static class ColourDifference
{
    /// <summary>ΔE of every pixel, in row order. Both buffers are RGBA 8-bit of the same length.</summary>
    public static float[] Map(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("The pictures differ in size.");
        var map = new float[a.Length / 4];
        var cache = new Dictionary<ulong, float>();
        for (int p = 0, i = 0; i < a.Length; p++, i += 4)
        {
            uint ca = (uint)(a[i] << 24 | a[i + 1] << 16 | a[i + 2] << 8 | a[i + 3]);
            uint cb = (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);
            if (ca == cb) continue;
            ulong key = (ulong)ca << 32 | cb;
            if (!cache.TryGetValue(key, out var de))
            {
                de = (float)Math.Max(Pixel(a, i, b, i, 1.0), Pixel(a, i, b, i, 0.0));
                cache[key] = de;
            }
            map[p] = de;
        }
        return map;
    }

    /// <summary>Mean, 99th percentile, worst pixel and worst spot of two pictures of the given width.</summary>
    public static LossStats Measure(byte[] a, byte[] b, int width, int colours)
    {
        var map = Map(a, b);
        if (map.Length == 0) return new LossStats(0, 0, 0, 0, colours);
        double sum = 0, max = 0;
        foreach (var d in map) { sum += d; if (d > max) max = d; }

        // 99th percentile from a histogram in steps of 0.01 up to 100: exact enough, no sort.
        var hist = new int[10001];
        foreach (var d in map) hist[Math.Min(10000, (int)(d * 100))]++;
        long need = (long)Math.Ceiling(map.Length * 0.99), seen = 0;
        double p99 = 0;
        for (int k = 0; k < hist.Length; k++)
        {
            seen += hist[k];
            if (seen >= need) { p99 = (k + 1) / 100.0; break; }
        }
        if (p99 > max) p99 = max;
        return new LossStats(sum / map.Length, p99, max, WorstSpot(a, b, map, width), colours);
    }

    /// <summary>
    /// The largest ΔE after averaging both pictures over 3 × 3 pixels in linear light, over white
    /// and over black. Only places with a pixel difference above 0.5 nearby are computed; elsewhere
    /// the averaged difference can't be larger.
    /// </summary>
    public static double WorstSpot(byte[] a, byte[] b, float[] map, int width)
    {
        int height = map.Length / width;
        double worst = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool near = false;
                for (int dy = -1; dy <= 1 && !near; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= width || yy >= height) continue;
                        if (map[yy * width + xx] > 0.5f) { near = true; break; }
                    }
                if (!near) continue;
                foreach (double backdrop in new[] { 1.0, 0.0 })
                {
                    var la = Averaged(a, x, y, width, height, backdrop);
                    var lb = Averaged(b, x, y, width, height, backdrop);
                    double d = Ciede2000(la, lb);
                    if (d > worst) worst = d;
                }
            }
        }
        return worst;
    }

    private static (double L, double A, double B) Averaged(byte[] px, int x, int y, int width, int height, double backdrop)
    {
        double r = 0, g = 0, bl = 0; int n = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= width || yy >= height) continue;
                int i = (yy * width + xx) * 4;
                double al = px[i + 3] / 255.0;
                r += Linear[px[i]] * al + backdrop * (1 - al);
                g += Linear[px[i + 1]] * al + backdrop * (1 - al);
                bl += Linear[px[i + 2]] * al + backdrop * (1 - al);
                n++;
            }
        return LabLinear(r / n, g / n, bl / n);
    }

    private static double Pixel(byte[] a, int i, byte[] b, int j, double backdrop)
    {
        var la = Lab(a[i], a[i + 1], a[i + 2], a[i + 3], backdrop);
        var lb = Lab(b[j], b[j + 1], b[j + 2], b[j + 3], backdrop);
        return Ciede2000(la, lb);
    }

    private static readonly double[] Linear = BuildLinear();

    private static double[] BuildLinear()
    {
        var t = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            t[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return t;
    }

    /// <summary>CIE Lab (D65) of an sRGB pixel composited over a grey level backdrop (0 black, 1 white).</summary>
    public static (double L, double A, double B) Lab(byte r, byte g, byte b, byte alpha, double backdrop)
    {
        double al = alpha / 255.0;
        double bg = backdrop;   // white and black are the same in sRGB and linear
        return LabLinear(Linear[r] * al + bg * (1 - al), Linear[g] * al + bg * (1 - al), Linear[b] * al + bg * (1 - al));
    }

    /// <summary>CIE Lab (D65) of linear-light RGB.</summary>
    public static (double L, double A, double B) LabLinear(double R, double G, double B)
    {
        double x = (0.4124564 * R + 0.3575761 * G + 0.1804375 * B) / 0.95047;
        double y = 0.2126729 * R + 0.7151522 * G + 0.0721750 * B;
        double z = (0.0193339 * R + 0.1191920 * G + 0.9503041 * B) / 1.08883;

        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>CIEDE2000 colour difference (Sharma, Wu and Dalal's formulation), kL = kC = kH = 1.</summary>
    public static double Ciede2000((double L, double A, double B) x, (double L, double A, double B) y)
    {
        const double Deg = Math.PI / 180;
        double c1 = Math.Sqrt(x.A * x.A + x.B * x.B), c2 = Math.Sqrt(y.A * y.A + y.B * y.B);
        double cMean = (c1 + c2) / 2;
        double c7 = Math.Pow(cMean, 7);
        double g = 0.5 * (1 - Math.Sqrt(c7 / (c7 + Math.Pow(25, 7))));
        double a1 = (1 + g) * x.A, a2 = (1 + g) * y.A;
        double c1p = Math.Sqrt(a1 * a1 + x.B * x.B), c2p = Math.Sqrt(a2 * a2 + y.B * y.B);
        double h1p = Hue(x.B, a1), h2p = Hue(y.B, a2);

        double dL = y.L - x.L;
        double dC = c2p - c1p;
        double dh = 0;
        if (c1p * c2p != 0)
        {
            dh = h2p - h1p;
            if (dh > 180) dh -= 360;
            else if (dh < -180) dh += 360;
        }
        double dH = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(dh / 2 * Deg);

        double lMean = (x.L + y.L) / 2;
        double cpMean = (c1p + c2p) / 2;
        double hMean;
        if (c1p * c2p == 0) hMean = h1p + h2p;
        else if (Math.Abs(h1p - h2p) <= 180) hMean = (h1p + h2p) / 2;
        else hMean = h1p + h2p < 360 ? (h1p + h2p + 360) / 2 : (h1p + h2p - 360) / 2;

        double t = 1 - 0.17 * Math.Cos((hMean - 30) * Deg) + 0.24 * Math.Cos(2 * hMean * Deg)
                     + 0.32 * Math.Cos((3 * hMean + 6) * Deg) - 0.20 * Math.Cos((4 * hMean - 63) * Deg);
        double dTheta = 30 * Math.Exp(-Math.Pow((hMean - 275) / 25, 2));
        double cp7 = Math.Pow(cpMean, 7);
        double rc = 2 * Math.Sqrt(cp7 / (cp7 + Math.Pow(25, 7)));
        double l50 = (lMean - 50) * (lMean - 50);
        double sl = 1 + 0.015 * l50 / Math.Sqrt(20 + l50);
        double sc = 1 + 0.045 * cpMean;
        double sh = 1 + 0.015 * cpMean * t;
        double rt = -Math.Sin(2 * dTheta * Deg) * rc;

        double tl = dL / sl, tc = dC / sc, th = dH / sh;
        return Math.Sqrt(tl * tl + tc * tc + th * th + rt * tc * th);
    }

    private static double Hue(double b, double a)
    {
        if (a == 0 && b == 0) return 0;
        double h = Math.Atan2(b, a) * 180 / Math.PI;
        return h < 0 ? h + 360 : h;
    }
}
