using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ImageKit;

/// <summary>One PNG chunk: its four-letter type and its data (length and CRC are derived).</summary>
public sealed record PngChunk(string Type, byte[] Data)
{
    /// <summary>Critical chunks (IHDR, PLTE, IDAT, IEND) start with a capital letter.</summary>
    public bool IsCritical => char.IsUpper(Type[0]);
}

/// <summary>Settings for Png.Encode.</summary>
public sealed class PngEncodeOptions
{
    public Effort Effort { get; init; } = Effort.Full;

    /// <summary>
    /// Write RGBA 8-bit only (colour type 6), never a palette, greyscale or fewer bits. For PNG
    /// images inside an .ico, where some icon loaders expect exactly that.
    /// </summary>
    public bool Rgba8Only { get; init; }

    /// <summary>Ancillary chunks to write before the image data, in this order.</summary>
    public IReadOnlyList<PngChunk> Keep { get; init; } = Array.Empty<PngChunk>();
}

/// <summary>
/// Reads and writes PNG files: the chunk structure, decoding of every colour type, bit depth and
/// interlacing to a PngImage, and an encoder that tries the lossless forms the pixels allow
/// (greyscale, no alpha, a palette when there are 256 colours or fewer, fewer bits per pixel),
/// several row filter strategies and zlib settings, and keeps the smallest. The encoder never
/// changes a pixel; it writes non-interlaced files.
/// </summary>
public static class Png
{
    public static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>Pictures larger than this many pixels are not decoded (36 megapixels, 6000 × 6000).</summary>
    public const long MaxPixels = 36_000_000;

    public static bool IsPng(ReadOnlySpan<byte> bytes) => bytes.Length >= 8 && bytes[..8].SequenceEqual(Signature);

    // ---------------- Chunks ----------------

    /// <summary>
    /// The chunks of a PNG file, in order, up to IEND. Throws InvalidDataException when the
    /// signature, a length or a CRC is wrong. Bytes after IEND are reported in trailing.
    /// </summary>
    public static List<PngChunk> ReadChunks(byte[] bytes, out int trailing)
    {
        if (!IsPng(bytes)) throw new InvalidDataException("Not a PNG file.");
        var chunks = new List<PngChunk>();
        int pos = 8;
        while (true)
        {
            if (pos + 12 > bytes.Length) throw new InvalidDataException("The file ends inside a chunk.");
            uint length = BE32(bytes, pos);
            if (length > int.MaxValue || pos + 12 + (long)length > bytes.Length) throw new InvalidDataException("A chunk is longer than the file.");
            var type = System.Text.Encoding.ASCII.GetString(bytes, pos + 4, 4);
            if (!type.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')) throw new InvalidDataException("A chunk has an invalid name.");
            uint crc = Crc32.Compute(bytes.AsSpan(pos + 4, 4 + (int)length));
            if (crc != BE32(bytes, pos + 8 + (int)length)) throw new InvalidDataException($"The {type} chunk is damaged (checksum mismatch).");
            chunks.Add(new PngChunk(type, bytes.AsSpan(pos + 8, (int)length).ToArray()));
            pos += 12 + (int)length;
            if (type == "IEND") break;
        }
        if (chunks[0].Type != "IHDR") throw new InvalidDataException("The file doesn't start with IHDR.");
        trailing = bytes.Length - pos;
        return chunks;
    }

    /// <summary>A PNG file from chunks: signature, then each chunk with its length and CRC.</summary>
    public static byte[] WriteChunks(IEnumerable<PngChunk> chunks)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);
        Span<byte> four = stackalloc byte[4];
        foreach (var c in chunks)
        {
            WriteBE32(four, (uint)c.Data.Length); ms.Write(four);
            var head = System.Text.Encoding.ASCII.GetBytes(c.Type);
            ms.Write(head);
            ms.Write(c.Data);
            uint crc = Crc32.Append(Crc32.Compute(head), c.Data);
            WriteBE32(four, crc); ms.Write(four);
        }
        return ms.ToArray();
    }

    // ---------------- Header ----------------

    internal readonly record struct Header(int Width, int Height, int BitDepth, int ColourType, int Interlace)
    {
        public int Channels => ColourType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        public int BitsPerPixel => Channels * BitDepth;
        public int FilterBpp => Math.Max(1, BitsPerPixel / 8);
        public long RowBytes(int width) => ((long)width * BitsPerPixel + 7) / 8;

        public string Describe() => ColourType switch
        {
            0 => $"greyscale {BitDepth}-bit",
            2 => $"RGB {BitDepth}-bit",
            3 => $"palette {BitDepth}-bit",
            4 => $"greyscale + alpha {BitDepth}-bit",
            6 => $"RGBA {BitDepth}-bit",
            _ => "unknown"
        } + (Interlace == 1 ? ", interlaced" : "");
    }

    internal static Header ReadHeader(PngChunk ihdr)
    {
        var d = ihdr.Data;
        if (d.Length != 13) throw new InvalidDataException("IHDR has the wrong length.");
        var h = new Header((int)BE32(d, 0), (int)BE32(d, 4), d[8], d[9], d[12]);
        if (BE32(d, 0) == 0 || BE32(d, 4) == 0 || BE32(d, 0) > int.MaxValue || BE32(d, 4) > int.MaxValue)
            throw new InvalidDataException("The image has no size.");
        if (d[10] != 0 || d[11] != 0 || d[12] > 1) throw new InvalidDataException("Unknown compression, filter or interlace method.");
        bool valid = h.ColourType switch
        {
            0 => h.BitDepth is 1 or 2 or 4 or 8 or 16,
            2 or 4 or 6 => h.BitDepth is 8 or 16,
            3 => h.BitDepth is 1 or 2 or 4 or 8,
            _ => false
        };
        if (!valid) throw new InvalidDataException("Invalid colour type and bit depth.");
        return h;
    }

    /// <summary>"RGBA 8-bit", "palette 4-bit, interlaced", …</summary>
    public static string Describe(List<PngChunk> chunks) => ReadHeader(chunks[0]).Describe();

    // ---------------- Decode ----------------

    public static PngImage Decode(byte[] bytes) => Decode(ReadChunks(bytes, out _));

    private static readonly (int X0, int Y0, int Dx, int Dy)[] Adam7 =
    {
        (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)
    };

    public static PngImage Decode(List<PngChunk> chunks)
    {
        var h = ReadHeader(chunks[0]);
        if ((long)h.Width * h.Height > MaxPixels) throw new InvalidDataException("The picture is too large to decode.");
        if (chunks.Any(c => c.IsCritical && c.Type is not ("IHDR" or "PLTE" or "IDAT" or "IEND")))
            throw new InvalidDataException("The file has a critical chunk this decoder doesn't know.");

        byte[]? plte = chunks.FirstOrDefault(c => c.Type == "PLTE")?.Data;
        byte[]? trns = chunks.FirstOrDefault(c => c.Type == "tRNS")?.Data;
        if (h.ColourType == 3 && (plte is null || plte.Length % 3 != 0 || plte.Length == 0)) throw new InvalidDataException("The palette is missing or damaged.");

        var passes = h.Interlace == 1
            ? Adam7.Select(p => (p.X0, p.Y0, p.Dx, p.Dy, W: (h.Width - p.X0 + p.Dx - 1) / p.Dx, H: (h.Height - p.Y0 + p.Dy - 1) / p.Dy)).ToArray()
            : new[] { (X0: 0, Y0: 0, Dx: 1, Dy: 1, W: h.Width, H: h.Height) };

        long expected = passes.Where(p => p.W > 0 && p.H > 0).Sum(p => p.H * (1 + h.RowBytes(p.W)));
        if (expected > int.MaxValue) throw new InvalidDataException("The picture is too large to decode.");
        var raw = Inflate(chunks.Where(c => c.Type == "IDAT").Select(c => c.Data), (int)expected);
        if (raw.Length < expected) throw new InvalidDataException("The image data is shorter than the picture.");

        int w = h.Width;
        byte[]? out8 = h.BitDepth == 16 ? null : new byte[(long)w * h.Height * 4];
        ushort[]? out16 = h.BitDepth == 16 ? new ushort[(long)w * h.Height * 4] : null;

        int pos = 0;
        int bpp = h.FilterBpp;
        foreach (var p in passes)
        {
            if (p.W == 0 || p.H == 0) continue;
            int rowBytes = (int)h.RowBytes(p.W);
            var prev = new byte[rowBytes];
            var cur = new byte[rowBytes];
            for (int py = 0; py < p.H; py++)
            {
                int filter = raw[pos++];
                Buffer.BlockCopy(raw, pos, cur, 0, rowBytes);
                pos += rowBytes;
                Unfilter(filter, cur, prev, bpp);
                int y = p.Y0 + py * p.Dy;
                for (int px = 0; px < p.W; px++)
                {
                    int x = p.X0 + px * p.Dx;
                    int o = (y * w + x) * 4;
                    if (out16 is not null) Pixel16(h, cur, px, trns, out16, o);
                    else Pixel8(h, cur, px, plte, trns, out8!, o);
                }
                (prev, cur) = (cur, prev);
            }
        }

        return out16 is not null ? new PngImage(w, h.Height, out16) : new PngImage(w, h.Height, out8!);
    }

    private static void Pixel8(Header h, byte[] row, int x, byte[]? plte, byte[]? trns, byte[] dst, int o)
    {
        switch (h.ColourType)
        {
            case 0:
            {
                int v = Sample(row, x, h.BitDepth);
                int max = (1 << h.BitDepth) - 1;
                byte g = (byte)(v * 255 / max);
                dst[o] = dst[o + 1] = dst[o + 2] = g;
                dst[o + 3] = trns is { Length: >= 2 } && ((trns[0] << 8) | trns[1]) == v ? (byte)0 : (byte)255;
                break;
            }
            case 2:
            {
                int i = x * 3;
                dst[o] = row[i]; dst[o + 1] = row[i + 1]; dst[o + 2] = row[i + 2];
                dst[o + 3] = trns is { Length: >= 6 } && trns[1] == row[i] && trns[3] == row[i + 1] && trns[5] == row[i + 2]
                             && trns[0] == 0 && trns[2] == 0 && trns[4] == 0 ? (byte)0 : (byte)255;
                break;
            }
            case 3:
            {
                int idx = Sample(row, x, h.BitDepth);
                if (idx * 3 + 2 >= plte!.Length) throw new InvalidDataException("A pixel points past the end of the palette.");
                dst[o] = plte[idx * 3]; dst[o + 1] = plte[idx * 3 + 1]; dst[o + 2] = plte[idx * 3 + 2];
                dst[o + 3] = trns is not null && idx < trns.Length ? trns[idx] : (byte)255;
                break;
            }
            case 4:
                dst[o] = dst[o + 1] = dst[o + 2] = row[x * 2];
                dst[o + 3] = row[x * 2 + 1];
                break;
            case 6:
                Buffer.BlockCopy(row, x * 4, dst, o, 4);
                break;
        }
    }

    private static void Pixel16(Header h, byte[] row, int x, byte[]? trns, ushort[] dst, int o)
    {
        int S(int i) => (row[i] << 8) | row[i + 1];
        switch (h.ColourType)
        {
            case 0:
            {
                int v = S(x * 2);
                dst[o] = dst[o + 1] = dst[o + 2] = (ushort)v;
                dst[o + 3] = trns is { Length: >= 2 } && ((trns[0] << 8) | trns[1]) == v ? (ushort)0 : (ushort)65535;
                break;
            }
            case 2:
            {
                int r = S(x * 6), g = S(x * 6 + 2), b = S(x * 6 + 4);
                dst[o] = (ushort)r; dst[o + 1] = (ushort)g; dst[o + 2] = (ushort)b;
                dst[o + 3] = trns is { Length: >= 6 } && ((trns[0] << 8) | trns[1]) == r && ((trns[2] << 8) | trns[3]) == g
                             && ((trns[4] << 8) | trns[5]) == b ? (ushort)0 : (ushort)65535;
                break;
            }
            case 4:
                dst[o] = dst[o + 1] = dst[o + 2] = (ushort)S(x * 4);
                dst[o + 3] = (ushort)S(x * 4 + 2);
                break;
            case 6:
                dst[o] = (ushort)S(x * 8); dst[o + 1] = (ushort)S(x * 8 + 2);
                dst[o + 2] = (ushort)S(x * 8 + 4); dst[o + 3] = (ushort)S(x * 8 + 6);
                break;
        }
    }

    private static int Sample(byte[] row, int x, int depth)
    {
        if (depth == 8) return row[x];
        int bit = x * depth;
        int shift = 8 - depth - (bit & 7);
        return (row[bit >> 3] >> shift) & ((1 << depth) - 1);
    }

    private static void Unfilter(int filter, byte[] cur, byte[] prev, int bpp)
    {
        int n = cur.Length;
        switch (filter)
        {
            case 0: break;
            case 1: for (int i = bpp; i < n; i++) cur[i] += cur[i - bpp]; break;
            case 2: for (int i = 0; i < n; i++) cur[i] += prev[i]; break;
            case 3:
                for (int i = 0; i < n; i++)
                    cur[i] += (byte)(((i >= bpp ? cur[i - bpp] : 0) + prev[i]) >> 1);
                break;
            case 4:
                for (int i = 0; i < n; i++)
                    cur[i] += Paeth(i >= bpp ? cur[i - bpp] : (byte)0, prev[i], i >= bpp ? prev[i - bpp] : (byte)0);
                break;
            default: throw new InvalidDataException("A row has an unknown filter type.");
        }
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] Inflate(IEnumerable<byte[]> parts, int expected)
    {
        using var input = new MemoryStream();
        foreach (var p in parts) input.Write(p);
        input.Position = 0;
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        var output = new byte[expected];
        int read = 0;
        try
        {
            while (read < expected)
            {
                int n = z.Read(output, read, expected - read);
                if (n == 0) break;
                read += n;
            }
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("The image data is damaged.");
        }
        return read == expected ? output : output[..read];
    }

    // ---------------- Encode ----------------

    /// <summary>
    /// The smallest lossless PNG of these pixels this encoder finds. The pixels decode back exactly;
    /// opts.Keep chunks are written before the image data, nothing else is.
    /// </summary>
    public static byte[] Encode(PngImage image, PngEncodeOptions? opts = null)
    {
        opts ??= new PngEncodeOptions();
        bool full = opts.Effort == Effort.Full;

        var forms = Forms(image, opts.Rgba8Only).ToList();
        byte[]? bestData = null;
        Form? bestForm = null;
        byte[]? bestFiltered = null;

        foreach (var form in forms)
        {
            var rows = form.Rows;
            foreach (var strategy in Strategies(form, full))
            {
                var filtered = Filter(rows, form.Header.FilterBpp, strategy);
                var data = Deflate(filtered, 6, ZLibCompressionStrategy.Default);
                if (bestData is null || data.Length < bestData.Length)
                {
                    bestData = data; bestForm = form; bestFiltered = filtered;
                }
            }
        }

        {
            var finals = full ? new[] { ZLibCompressionStrategy.Default, ZLibCompressionStrategy.Filtered } : new[] { ZLibCompressionStrategy.Default };
            foreach (var s in finals)
            {
                var data = Deflate(bestFiltered!, 9, s);
                if (data.Length < bestData!.Length) bestData = data;
            }
        }

        var f = bestForm!;
        var chunks = new List<PngChunk> { new("IHDR", IHDR(f.Header)) };
        chunks.AddRange(opts.Keep);
        if (f.Plte is not null) chunks.Add(new PngChunk("PLTE", f.Plte));
        if (f.Trns is not null) chunks.Add(new PngChunk("tRNS", f.Trns));
        chunks.Add(new PngChunk("IDAT", bestData!));
        chunks.Add(new PngChunk("IEND", Array.Empty<byte>()));
        return WriteChunks(chunks);
    }

    private sealed record Form(Header Header, byte[]? Plte, byte[]? Trns, byte[][] Rows);

    private enum Strategy { None, Sub, Up, Average, Paeth, Adaptive }

    private static IEnumerable<Strategy> Strategies(Form form, bool full)
    {
        bool packed = form.Header.ColourType == 3 || form.Header.BitDepth < 8;
        yield return Strategy.None;
        yield return Strategy.Adaptive;
        if (packed || !full) yield break;
        yield return Strategy.Sub;
        yield return Strategy.Up;
        yield return Strategy.Paeth;
    }

    /// <summary>Every lossless way to store these pixels that's worth trying.</summary>
    private static IEnumerable<Form> Forms(PngImage image, bool rgba8Only)
    {
        if (rgba8Only)
        {
            var as8 = image.ExactlyAs8() ?? image;
            if (!as8.Is16) { yield return Truecolour(as8, grey: false, alpha: true, depth: 8); yield break; }
            yield return Truecolour16(as8, grey: false, alpha: true);
            yield break;
        }

        var img = image.ExactlyAs8() ?? image;
        bool opaque = true, grey = true;

        if (img.Is16)
        {
            var p = img.Rgba16!;
            for (int i = 0; i < p.Length; i += 4)
            {
                if (p[i + 3] != 65535) opaque = false;
                if (p[i] != p[i + 1] || p[i] != p[i + 2]) grey = false;
            }
            yield return Truecolour16(img, grey, !opaque);
            yield break;
        }

        var px = img.Rgba8!;
        var counts = new Dictionary<uint, int>();
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] != 255) opaque = false;
            if (px[i] != px[i + 1] || px[i] != px[i + 2]) grey = false;
            if (counts.Count <= 256)
            {
                uint key = (uint)(px[i] << 24 | px[i + 1] << 16 | px[i + 2] << 8 | px[i + 3]);
                counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
            }
        }

        if (grey && opaque)
        {
            foreach (int depth in new[] { 1, 2, 4 })
                if (GreyFits(px, depth)) { yield return Truecolour(img, true, false, depth); break; }
        }
        yield return Truecolour(img, grey, !opaque, 8);
        if (counts.Count <= 256) yield return Palette(img, counts);
    }

    private static bool GreyFits(byte[] px, int depth)
    {
        int step = 255 / ((1 << depth) - 1);
        for (int i = 0; i < px.Length; i += 4)
            if (px[i] % step != 0) return false;
        return true;
    }

    private static Form Truecolour(PngImage img, bool grey, bool alpha, int depth)
    {
        int ct = grey ? (alpha ? 4 : 0) : (alpha ? 6 : 2);
        var h = new Header(img.Width, img.Height, depth, ct, 0);
        var px = img.Rgba8!;
        int rowBytes = (int)h.RowBytes(img.Width);
        var rows = new byte[img.Height][];
        int ch = h.Channels;
        int maxv = (1 << depth) - 1;
        for (int y = 0; y < img.Height; y++)
        {
            var row = new byte[rowBytes];
            for (int x = 0; x < img.Width; x++)
            {
                int o = (y * img.Width + x) * 4;
                if (depth < 8)
                {
                    int v = px[o] * maxv / 255;
                    int bit = x * depth;
                    row[bit >> 3] |= (byte)(v << (8 - depth - (bit & 7)));
                }
                else if (grey)
                {
                    row[x * ch] = px[o];
                    if (alpha) row[x * ch + 1] = px[o + 3];
                }
                else
                {
                    row[x * ch] = px[o]; row[x * ch + 1] = px[o + 1]; row[x * ch + 2] = px[o + 2];
                    if (alpha) row[x * ch + 3] = px[o + 3];
                }
            }
            rows[y] = row;
        }
        return new Form(h, null, null, rows);
    }

    private static Form Truecolour16(PngImage img, bool grey, bool alpha)
    {
        int ct = grey ? (alpha ? 4 : 0) : (alpha ? 6 : 2);
        var h = new Header(img.Width, img.Height, 16, ct, 0);
        var px = img.Rgba16!;
        int ch = h.Channels;
        var rows = new byte[img.Height][];
        for (int y = 0; y < img.Height; y++)
        {
            var row = new byte[img.Width * ch * 2];
            for (int x = 0; x < img.Width; x++)
            {
                int o = (y * img.Width + x) * 4;
                int b = x * ch * 2;
                void Put(int k, ushort v) { row[b + k * 2] = (byte)(v >> 8); row[b + k * 2 + 1] = (byte)v; }
                if (grey) { Put(0, px[o]); if (alpha) Put(1, px[o + 3]); }
                else { Put(0, px[o]); Put(1, px[o + 1]); Put(2, px[o + 2]); if (alpha) Put(3, px[o + 3]); }
            }
            rows[y] = row;
        }
        return new Form(h, null, null, rows);
    }

    /// <summary>
    /// A palette of exactly the colours used: transparent entries first (so tRNS stays short), then
    /// by how often each is used. 1, 2, 4 or 8 bits per pixel, as the count allows.
    /// </summary>
    private static Form Palette(PngImage img, Dictionary<uint, int> counts)
    {
        var entries = counts.OrderBy(kv => (kv.Key & 0xFF) == 255 ? 1 : 0)
                            .ThenBy(kv => kv.Key & 0xFF)
                            .ThenByDescending(kv => kv.Value)
                            .ThenBy(kv => kv.Key)
                            .Select(kv => kv.Key).ToList();
        int depth = entries.Count <= 2 ? 1 : entries.Count <= 4 ? 2 : entries.Count <= 16 ? 4 : 8;
        var index = new Dictionary<uint, int>();
        for (int i = 0; i < entries.Count; i++) index[entries[i]] = i;

        var plte = new byte[entries.Count * 3];
        int transparent = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            uint e = entries[i];
            plte[i * 3] = (byte)(e >> 24); plte[i * 3 + 1] = (byte)(e >> 16); plte[i * 3 + 2] = (byte)(e >> 8);
            if ((e & 0xFF) != 255) transparent = i + 1;
        }
        byte[]? trns = transparent == 0 ? null : entries.Take(transparent).Select(e => (byte)(e & 0xFF)).ToArray();

        var h = new Header(img.Width, img.Height, depth, 3, 0);
        int rowBytes = (int)h.RowBytes(img.Width);
        var px = img.Rgba8!;
        var rows = new byte[img.Height][];
        for (int y = 0; y < img.Height; y++)
        {
            var row = new byte[rowBytes];
            for (int x = 0; x < img.Width; x++)
            {
                int o = (y * img.Width + x) * 4;
                uint key = (uint)(px[o] << 24 | px[o + 1] << 16 | px[o + 2] << 8 | px[o + 3]);
                int v = index[key];
                if (depth == 8) row[x] = (byte)v;
                else
                {
                    int bit = x * depth;
                    row[bit >> 3] |= (byte)(v << (8 - depth - (bit & 7)));
                }
            }
            rows[y] = row;
        }
        return new Form(h, plte, trns, rows);
    }

    private static byte[] Filter(byte[][] rows, int bpp, Strategy strategy)
    {
        if (rows.Length == 0) return Array.Empty<byte>();
        int n = rows[0].Length;
        var output = new byte[rows.Length * (n + 1)];
        var zero = new byte[n];
        var trial = new byte[5][];
        for (int k = 0; k < 5; k++) trial[k] = new byte[n];

        for (int y = 0; y < rows.Length; y++)
        {
            var cur = rows[y];
            var prev = y > 0 ? rows[y - 1] : zero;
            int o = y * (n + 1);
            int chosen;
            if (strategy == Strategy.Adaptive)
            {
                long best = long.MaxValue;
                chosen = 0;
                for (int k = 0; k < 5; k++)
                {
                    ApplyFilter(k, cur, prev, bpp, trial[k]);
                    long sum = SignedSum(trial[k]);
                    if (sum < best) { best = sum; chosen = k; }
                }
                Buffer.BlockCopy(trial[chosen], 0, output, o + 1, n);
            }
            else
            {
                chosen = (int)strategy;
                ApplyFilter(chosen, cur, prev, bpp, trial[0]);
                Buffer.BlockCopy(trial[0], 0, output, o + 1, n);
            }
            output[o] = (byte)chosen;
        }
        return output;
    }

    private static void ApplyFilter(int filter, byte[] cur, byte[] prev, int bpp, byte[] dst)
    {
        int n = cur.Length;
        int lead = Math.Min(bpp, n);
        switch (filter)
        {
            case 0:
                Buffer.BlockCopy(cur, 0, dst, 0, n);
                break;
            case 1:
                for (int i = 0; i < lead; i++) dst[i] = cur[i];
                for (int i = bpp; i < n; i++) dst[i] = (byte)(cur[i] - cur[i - bpp]);
                break;
            case 2:
                for (int i = 0; i < n; i++) dst[i] = (byte)(cur[i] - prev[i]);
                break;
            case 3:
                for (int i = 0; i < lead; i++) dst[i] = (byte)(cur[i] - (prev[i] >> 1));
                for (int i = bpp; i < n; i++) dst[i] = (byte)(cur[i] - ((cur[i - bpp] + prev[i]) >> 1));
                break;
            default:
                for (int i = 0; i < lead; i++) dst[i] = (byte)(cur[i] - prev[i]);
                for (int i = bpp; i < n; i++) dst[i] = (byte)(cur[i] - Paeth(cur[i - bpp], prev[i], prev[i - bpp]));
                break;
        }
    }

    /// <summary>Sum of the bytes read as signed values, the usual measure of how well a filter worked.</summary>
    private static long SignedSum(byte[] row)
    {
        long sum = 0;
        foreach (var b in row) sum += b < 128 ? b : 256 - b;
        return sum;
    }

    private static byte[] Deflate(byte[] data, int level, ZLibCompressionStrategy strategy)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, new ZLibCompressionOptions { CompressionLevel = level, CompressionStrategy = strategy }, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    private static byte[] IHDR(Header h)
    {
        var d = new byte[13];
        WriteBE32(d.AsSpan(0), (uint)h.Width);
        WriteBE32(d.AsSpan(4), (uint)h.Height);
        d[8] = (byte)h.BitDepth; d[9] = (byte)h.ColourType; d[10] = 0; d[11] = 0; d[12] = 0;
        return d;
    }

    internal static uint BE32(byte[] b, int i) => (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);

    internal static void WriteBE32(Span<byte> b, uint v)
    {
        b[0] = (byte)(v >> 24); b[1] = (byte)(v >> 16); b[2] = (byte)(v >> 8); b[3] = (byte)v;
    }
}

/// <summary>CRC-32 as PNG uses it (ISO 3309, polynomial 0xEDB88320).</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = crc ^ 0xFFFFFFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
