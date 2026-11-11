using System;
using System.Collections.Generic;

namespace ImageKit;

/// <summary>
/// Reduces a picture to at most 256 colours, choosing them by how the eye sees colour (OKLab,
/// with alpha), not by RGB distance. Median cut finds a first palette, k-means refines it. Rare
/// colours are weighted by the square root of their count, not the count itself, so a small
/// detail in a colour of its own (a red dot, a thin highlight) keeps a palette entry instead of
/// being swallowed by large areas. No dithering: every pixel gets its nearest palette colour,
/// which keeps flat areas flat and makes the loss measurable per pixel.
/// </summary>
public static class Palette
{
    /// <summary>Pictures with more distinct colours than this are not reduced (photos and the like).</summary>
    public const int MaxDistinctColours = 1 << 18;

    private const int Iterations = 10;

    /// <summary>
    /// The picture with at most maxColours distinct colours, 8 bits per channel, or null when it has
    /// more distinct colours than MaxDistinctColours. Fully transparent pixels all become (0,0,0,0).
    /// </summary>
    public static PngImage? Quantize(PngImage image, int maxColours = 256)
    {
        var px = image.ToRgba8();
        var counts = new Dictionary<uint, int>();
        for (int i = 0; i < px.Length; i += 4)
        {
            uint key = px[i + 3] == 0 ? 0u : (uint)(px[i] << 24 | px[i + 1] << 16 | px[i + 2] << 8 | px[i + 3]);
            counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
            if (counts.Count > MaxDistinctColours) return null;
        }

        int n = counts.Count;
        var keys = new uint[n];
        var weight = new double[n];
        var count = new int[n];
        var feat = new double[n * 4];
        int k0 = 0;
        foreach (var (key, c) in counts)
        {
            keys[k0] = key; count[k0] = c; weight[k0] = Math.Sqrt(c);
            Features(key, feat, k0 * 4);
            k0++;
        }

        uint[] palette;
        if (n <= maxColours)
        {
            palette = keys;
        }
        else
        {
            var centres = MedianCut(feat, weight, n, maxColours);
            int k = centres.Length / 4;
            var assign = new int[n];
            for (int i = 0; i < n; i++) assign[i] = Nearest(feat, i, centres, k);

            for (int it = 0; it < Iterations; it++)
            {
                var sum = new double[k * 4];
                var wsum = new double[k];
                for (int i = 0; i < n; i++)
                {
                    int c = assign[i];
                    for (int d = 0; d < 4; d++) sum[c * 4 + d] += feat[i * 4 + d] * weight[i];
                    wsum[c] += weight[i];
                }
                for (int c = 0; c < k; c++)
                {
                    if (wsum[c] > 0)
                        for (int d = 0; d < 4; d++) centres[c * 4 + d] = sum[c * 4 + d] / wsum[c];
                }
                // An entry nobody uses goes to the colour that is worst off now.
                for (int c = 0; c < k; c++)
                {
                    if (wsum[c] > 0) continue;
                    int worst = 0; double worstD = -1;
                    for (int i = 0; i < n; i++)
                    {
                        double dd = Dist(feat, i * 4, centres, assign[i] * 4);
                        if (dd > worstD) { worstD = dd; worst = i; }
                    }
                    for (int d = 0; d < 4; d++) centres[c * 4 + d] = feat[worst * 4 + d];
                    assign[worst] = c;
                }

                bool moved = Assign(feat, n, centres, k, assign);
                if (!moved) break;
            }

            // Each entry becomes the count-weighted mean of its members, in 8-bit RGBA.
            var acc = new double[k * 4];
            var cnt = new double[k];
            for (int i = 0; i < n; i++)
            {
                int c = assign[i];
                uint key = keys[i];
                acc[c * 4] += (key >> 24) * (double)count[i];
                acc[c * 4 + 1] += ((key >> 16) & 0xFF) * (double)count[i];
                acc[c * 4 + 2] += ((key >> 8) & 0xFF) * (double)count[i];
                acc[c * 4 + 3] += (key & 0xFF) * (double)count[i];
                cnt[c] += count[i];
            }
            var list = new List<uint>();
            for (int c = 0; c < k; c++)
            {
                if (cnt[c] == 0) continue;
                byte r = (byte)Math.Round(acc[c * 4] / cnt[c]), g = (byte)Math.Round(acc[c * 4 + 1] / cnt[c]);
                byte b = (byte)Math.Round(acc[c * 4 + 2] / cnt[c]), a = (byte)Math.Round(acc[c * 4 + 3] / cnt[c]);
                list.Add(a == 0 ? 0u : (uint)(r << 24 | g << 16 | b << 8 | a));
            }
            palette = list.ToArray();
        }

        // Every distinct colour to its nearest palette colour.
        int pk = palette.Length;
        var pfeat = new double[pk * 4];
        for (int c = 0; c < pk; c++) Features(palette[c], pfeat, c * 4);
        var map = new Dictionary<uint, uint>(n);
        for (int i = 0; i < n; i++) map[keys[i]] = palette[Nearest(feat, i, pfeat, pk)];

        var dst = new byte[px.Length];
        for (int i = 0; i < px.Length; i += 4)
        {
            uint key = px[i + 3] == 0 ? 0u : (uint)(px[i] << 24 | px[i + 1] << 16 | px[i + 2] << 8 | px[i + 3]);
            uint v = map[key];
            dst[i] = (byte)(v >> 24); dst[i + 1] = (byte)(v >> 16); dst[i + 2] = (byte)(v >> 8); dst[i + 3] = (byte)v;
        }
        return new PngImage(image.Width, image.Height, dst);
    }

    /// <summary>OKLab of the colour, multiplied by alpha, then alpha: transparent colours meet at zero.</summary>
    private static void Features(uint key, double[] f, int o)
    {
        double alpha = (key & 0xFF) / 255.0;
        double r = Lin((byte)(key >> 24)), g = Lin((byte)(key >> 16)), b = Lin((byte)(key >> 8));
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        f[o] = (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s) * alpha;
        f[o + 1] = (1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s) * alpha;
        f[o + 2] = (0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s) * alpha;
        f[o + 3] = alpha;
    }

    private static readonly double[] LinTable = BuildLin();
    private static double Lin(byte v) => LinTable[v];

    private static double[] BuildLin()
    {
        var t = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            t[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return t;
    }

    private static double Dist(double[] a, int ao, double[] b, int bo)
    {
        double d0 = a[ao] - b[bo], d1 = a[ao + 1] - b[bo + 1], d2 = a[ao + 2] - b[bo + 2], d3 = a[ao + 3] - b[bo + 3];
        return d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3;
    }

    private static int Nearest(double[] feat, int i, double[] centres, int k)
    {
        int best = 0; double bestD = double.MaxValue;
        for (int c = 0; c < k; c++)
        {
            double d = Dist(feat, i * 4, centres, c * 4);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    /// <summary>
    /// Moves each colour to its nearest centre. Skips centres that can't be nearer: when the
    /// distance between two centres is at least twice the colour's distance to its current one.
    /// </summary>
    private static bool Assign(double[] feat, int n, double[] centres, int k, int[] assign)
    {
        var half = new double[k * k];
        for (int a = 0; a < k; a++)
            for (int b = a + 1; b < k; b++)
            {
                double d = Math.Sqrt(Dist(centres, a * 4, centres, b * 4)) / 2;
                half[a * k + b] = half[b * k + a] = d;
            }

        bool moved = false;
        for (int i = 0; i < n; i++)
        {
            int best = assign[i];
            double bestD = Math.Sqrt(Dist(feat, i * 4, centres, best * 4));
            for (int c = 0; c < k; c++)
            {
                if (c == best || half[best * k + c] >= bestD) continue;
                double d = Math.Sqrt(Dist(feat, i * 4, centres, c * 4));
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best != assign[i]) { assign[i] = best; moved = true; }
        }
        return moved;
    }

    /// <summary>
    /// Splits the colours into boxes until there are k: always the box with the largest weighted
    /// squared error, along the dimension where it spreads most, at its weighted median.
    /// </summary>
    private static double[] MedianCut(double[] feat, double[] weight, int n, int k)
    {
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        var boxes = new List<(int Start, int End, double Error, int Dim)>();
        boxes.Add(Box(feat, weight, order, 0, n));

        while (boxes.Count < k)
        {
            int pick = -1; double worst = 0;
            for (int b = 0; b < boxes.Count; b++)
                if (boxes[b].End - boxes[b].Start > 1 && boxes[b].Error > worst) { worst = boxes[b].Error; pick = b; }
            if (pick < 0) break;

            var (s, e, _, dim) = boxes[pick];
            var keysForSort = new double[e - s];
            var segment = new int[e - s];
            for (int i = s; i < e; i++) { keysForSort[i - s] = feat[order[i] * 4 + dim]; segment[i - s] = order[i]; }
            Array.Sort(keysForSort, segment);
            Array.Copy(segment, 0, order, s, e - s);

            double total = 0;
            for (int i = s; i < e; i++) total += weight[order[i]];
            double acc = 0; int cut = e - 1;
            for (int i = s; i < e - 1; i++)
            {
                acc += weight[order[i]];
                if (acc >= total / 2) { cut = i + 1; break; }
            }
            cut = Math.Clamp(cut, s + 1, e - 1);
            boxes[pick] = Box(feat, weight, order, s, cut);
            boxes.Add(Box(feat, weight, order, cut, e));
        }

        var centres = new double[boxes.Count * 4];
        for (int b = 0; b < boxes.Count; b++)
        {
            var (s, e, _, _) = boxes[b];
            double w = 0;
            for (int i = s; i < e; i++)
            {
                int c = order[i];
                for (int d = 0; d < 4; d++) centres[b * 4 + d] += feat[c * 4 + d] * weight[c];
                w += weight[c];
            }
            for (int d = 0; d < 4; d++) centres[b * 4 + d] /= w;
        }
        return centres;
    }

    private static (int, int, double, int) Box(double[] feat, double[] weight, int[] order, int s, int e)
    {
        var sum = new double[4];
        var sq = new double[4];
        double w = 0;
        for (int i = s; i < e; i++)
        {
            int c = order[i];
            double wc = weight[c];
            for (int d = 0; d < 4; d++)
            {
                double v = feat[c * 4 + d];
                sum[d] += v * wc; sq[d] += v * v * wc;
            }
            w += wc;
        }
        double error = 0; int dim = 0; double maxVar = -1;
        for (int d = 0; d < 4; d++)
        {
            double variance = sq[d] - sum[d] * sum[d] / w;
            error += variance;
            if (variance > maxVar) { maxVar = variance; dim = d; }
        }
        return (s, e, error, dim);
    }
}
