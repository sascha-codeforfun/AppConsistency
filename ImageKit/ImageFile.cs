using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ImageKit;

/// <summary>
/// The entry points: what a file is and carries (Inspect), and doing one level to it (Process).
/// The format is recognised from the bytes. Nothing here throws on a bad file: Inspect reports the
/// problem, Process returns the original bytes with a note.
/// </summary>
public static class ImageFile
{
    public static ImageFormat Detect(ReadOnlySpan<byte> bytes) =>
        Png.IsPng(bytes) ? ImageFormat.Png :
        Jpeg.IsJpeg(bytes) ? ImageFormat.Jpeg :
        Ico.IsIco(bytes) ? ImageFormat.Ico :
        ImageFormat.Unknown;

    // PNG chunks that change how the picture looks or are part of it; Strip keeps these.
    private static readonly HashSet<string> PngDisplayed = new(StringComparer.Ordinal)
    {
        "IHDR", "PLTE", "tRNS", "IDAT", "IEND",
        "pHYs", "sRGB", "gAMA", "cHRM", "iCCP", "cICP", "mDCV", "cLLI",
        "acTL", "fcTL", "fdAT"   // animation (APNG)
    };

    private static readonly Dictionary<string, string> PngDisplayedNames = new(StringComparer.Ordinal)
    {
        ["pHYs"] = "Pixel density (pHYs)",
        ["sRGB"] = "Colour space (sRGB)",
        ["gAMA"] = "Gamma (gAMA)",
        ["cHRM"] = "Chromaticity (cHRM)",
        ["iCCP"] = "Colour profile (iCCP)",
        ["cICP"] = "Colour coding (cICP)",
        ["mDCV"] = "Mastering display (mDCV)",
        ["cLLI"] = "Light level (cLLI)",
        ["acTL"] = "Animation (APNG)"
    };

    // ---------------- Inspect ----------------

    public static ImageInfo Inspect(byte[] bytes)
    {
        try
        {
            return Detect(bytes) switch
            {
                ImageFormat.Png => InspectPng(bytes),
                ImageFormat.Jpeg => InspectJpeg(bytes),
                ImageFormat.Ico => InspectIco(bytes),
                _ => new ImageInfo(ImageFormat.Unknown, 0, 0, 0, "not a PNG, ICO or JPEG file", Array.Empty<MetadataItem>(), "Not an image file ImageKit reads.")
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return new ImageInfo(Detect(bytes), 0, 0, 0, "damaged", Array.Empty<MetadataItem>(), ex.Message);
        }
    }

    private static ImageInfo InspectPng(byte[] bytes)
    {
        var chunks = Png.ReadChunks(bytes, out int trailing);
        var h = Png.ReadHeader(chunks[0]);
        var items = PngMetadata(chunks);
        if (trailing > 0) items.Add(new MetadataItem("Data after the end of the file", "", trailing, false));
        string? problem = chunks.Any(c => c.IsCritical && c.Type is not ("IHDR" or "PLTE" or "IDAT" or "IEND"))
            ? "Has a critical chunk ImageKit doesn't know; it won't be changed." : null;
        return new ImageInfo(ImageFormat.Png, h.Width, h.Height, 1, "PNG, " + h.Describe(), items, problem);
    }

    /// <summary>Every ancillary chunk except the picture's own, as metadata items.</summary>
    private static List<MetadataItem> PngMetadata(List<PngChunk> chunks)
    {
        var items = new List<MetadataItem>();
        foreach (var c in chunks)
        {
            if (c.Type is "IHDR" or "PLTE" or "tRNS" or "IDAT" or "IEND" or "fcTL" or "fdAT") continue;
            if (PngDisplayedNames.TryGetValue(c.Type, out var shown))
            {
                string detail = c.Type == "iCCP" ? Latin1Until0(c.Data) : "";
                items.Add(new MetadataItem(shown, detail, c.Data.Length + 12, true));
                continue;
            }
            items.Add(PngItem(c));
        }
        return items;
    }

    private static MetadataItem PngItem(PngChunk c)
    {
        int size = c.Data.Length + 12;
        return c.Type switch
        {
            "caBX" => new MetadataItem("C2PA content credentials", C2paDetail(c.Data), size, false),
            "tEXt" => new MetadataItem("Text", TextDetail(c), size, false),
            "zTXt" => new MetadataItem("Text (compressed)", TextDetail(c), size, false),
            "iTXt" => new MetadataItem("Text (international)", TextDetail(c), size, false),
            "eXIf" => new MetadataItem("EXIF", Jpeg.ExifDetail(c.Data), size, false),
            "tIME" => new MetadataItem("Time stamp (tIME)", c.Data.Length == 7 ? $"{c.Data[0] << 8 | c.Data[1]:0000}-{c.Data[2]:00}-{c.Data[3]:00} {c.Data[4]:00}:{c.Data[5]:00}" : "", size, false),
            "bKGD" => new MetadataItem("Background colour (bKGD)", "", size, false),
            "sBIT" => new MetadataItem("Significant bits (sBIT)", "", size, false),
            "hIST" => new MetadataItem("Palette histogram (hIST)", "", size, false),
            "sPLT" => new MetadataItem("Suggested palette (sPLT)", Latin1Until0(c.Data), size, false),
            _ => new MetadataItem($"Private chunk ({c.Type})", "", size, false)
        };
    }

    private static string C2paDetail(byte[] data)
    {
        var parts = new List<string>();
        var s = Encoding.Latin1.GetString(data);
        foreach (var name in new[] { "Anthropic", "Adobe", "OpenAI", "Google", "Microsoft", "Midjourney" })
            if (s.Contains(name, StringComparison.Ordinal)) parts.Add("signed by " + name);
        return parts.Count == 0 ? "signed note on how the file was made" : string.Join(", ", parts.Distinct());
    }

    private static string TextDetail(PngChunk c)
    {
        try
        {
            int z = Array.IndexOf(c.Data, (byte)0);
            if (z < 0) return "";
            string key = Encoding.Latin1.GetString(c.Data, 0, z);
            string value = "";
            if (c.Type == "tEXt") value = Encoding.Latin1.GetString(c.Data, z + 1, c.Data.Length - z - 1);
            else if (c.Type == "zTXt") value = Encoding.Latin1.GetString(InflateSmall(c.Data.AsSpan(z + 2).ToArray()));
            else
            {
                bool compressed = c.Data[z + 1] == 1;
                int p = z + 3;
                p = Array.IndexOf(c.Data, (byte)0, p) + 1;      // language tag
                p = Array.IndexOf(c.Data, (byte)0, p) + 1;      // translated keyword
                if (p <= 0) return key;
                var raw = c.Data.AsSpan(p).ToArray();
                value = Encoding.UTF8.GetString(compressed ? InflateSmall(raw) : raw);
            }
            value = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (value.Length > 60) value = value[..57] + "…";
            return value.Length == 0 ? key : $"{key}: {value}";
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return "";
        }
    }

    private static byte[] InflateSmall(byte[] data)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        var buf = new byte[4096];
        int n = 0, r;
        while (n < buf.Length && (r = z.Read(buf, n, buf.Length - n)) > 0) n += r;
        return buf[..n];
    }

    private static string Latin1Until0(byte[] d)
    {
        int z = Array.IndexOf(d, (byte)0);
        return Encoding.Latin1.GetString(d, 0, z < 0 ? Math.Min(d.Length, 60) : Math.Min(z, 79));
    }

    private static ImageInfo InspectJpeg(byte[] bytes)
    {
        var parts = Jpeg.Parse(bytes, out int end);
        var (w, h, kind) = Jpeg.Frame(bytes, parts);
        var items = new List<MetadataItem>();
        foreach (var p in parts)
        {
            var (role, name, detail) = Jpeg.Classify(bytes, p);
            if (name.Length == 0) continue;
            items.Add(new MetadataItem(name, detail, p.Length, role == Jpeg.Role.Keep));
        }
        if (end < bytes.Length) items.Add(new MetadataItem("Data after the end of the image", "", bytes.Length - end, false));
        return new ImageInfo(ImageFormat.Jpeg, w, h, 1, "JPEG, " + kind, items, w == 0 ? "No frame header found." : null);
    }

    private static ImageInfo InspectIco(byte[] bytes)
    {
        var (type, images, unused) = Ico.Read(bytes);
        var items = new List<MetadataItem>();
        int png = 0;
        foreach (var im in images)
        {
            if (!im.IsPng) continue;
            png++;
            try
            {
                foreach (var it in PngMetadata(Png.ReadChunks(im.Data, out _)))
                    items.Add(it with { Detail = $"{im.Width}×{im.Height}" + (it.Detail.Length > 0 ? ", " + it.Detail : "") });
            }
            catch (InvalidDataException ex)
            {
                return new ImageInfo(ImageFormat.Ico, 0, 0, images.Count, "damaged", items, $"The {im.Width}×{im.Height} image: {ex.Message}");
            }
        }
        if (unused > 0) items.Add(new MetadataItem("Unused bytes in the file", "", unused, false));
        var largest = images.OrderByDescending(i => i.Width * i.Height).First();
        string what = type == 2 ? "cursor" : "ICO";
        return new ImageInfo(ImageFormat.Ico, largest.Width, largest.Height, images.Count,
            $"{what}, {images.Count} image{(images.Count == 1 ? "" : "s")} ({png} PNG, {images.Count - png} bitmap)", items, null);
    }

    // ---------------- Process ----------------

    public static ImageResult Process(byte[] bytes, ImageLevel level, ImageOptions? options = null)
    {
        options ??= new ImageOptions();
        var format = Detect(bytes);
        if (level == ImageLevel.AsIs || format == ImageFormat.Unknown)
            return Unchanged(bytes, format, level, format == ImageFormat.Unknown ? "Not an image file ImageKit reads." : null);
        try
        {
            return format switch
            {
                ImageFormat.Png => ProcessPng(bytes, level, options, rgba8Only: false),
                ImageFormat.Jpeg => ProcessJpeg(bytes, level, options),
                _ => ProcessIco(bytes, level, options)
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return Unchanged(bytes, format, level, "Left as it is: " + ex.Message);
        }
    }

    private static ImageResult Unchanged(byte[] bytes, ImageFormat format, ImageLevel level, string? note) =>
        new(format, level, ImageLevel.AsIs, bytes, bytes.Length, Array.Empty<string>(), true, null, false,
            note is null ? Array.Empty<string>() : new[] { note }, false);

    private static ImageResult ProcessPng(byte[] bytes, ImageLevel level, ImageOptions options, bool rgba8Only)
    {
        var chunks = Png.ReadChunks(bytes, out int trailing);
        var notes = new List<string>();
        if (chunks.Any(c => c.IsCritical && c.Type is not ("IHDR" or "PLTE" or "IDAT" or "IEND")))
            return Unchanged(bytes, ImageFormat.Png, level, "Left as it is: it has a critical chunk ImageKit doesn't know.");

        var removed = new List<string>();
        var kept = new List<PngChunk>();
        foreach (var c in chunks)
        {
            if (PngDisplayed.Contains(c.Type)) kept.Add(c);
            else removed.Add(PngItem(c).Name);
        }
        if (trailing > 0) removed.Add($"Data after the end of the file ({trailing:N0} bytes)");

        byte[] stripped = removed.Count == 0 ? bytes : Png.WriteChunks(kept);
        var current = new ImageResult(ImageFormat.Png, level, ImageLevel.Strip, stripped, bytes.Length, removed, true, null, false, notes, removed.Count > 0);
        if (level == ImageLevel.Strip) return current;

        if (chunks.Any(c => c.Type == "acTL"))
        {
            notes.Add("Animated PNG: frames kept as they are, metadata removed only.");
            return current;
        }

        var ancillary = kept.Where(c => c.Type is not ("IHDR" or "PLTE" or "tRNS" or "IDAT" or "IEND")).ToList();
        var original = Png.Decode(chunks);
        var encodeOptions = new PngEncodeOptions { Effort = options.Effort, Rgba8Only = rgba8Only, Keep = ancillary };

        var lossless = Png.Encode(original, encodeOptions);
        if (lossless.Length < stripped.Length)
        {
            string? why = Confirm(lossless, original, bytes, options, compareToOriginalFile: true);
            if (why is null)
                current = current with { Bytes = lossless, Applied = ImageLevel.Lossless, Changed = true };
            else
                notes.Add("Recompressed version not used: " + why);
        }
        else
        {
            current = current with { Applied = ImageLevel.Lossless };
            notes.Add("Already as small as this encoder gets it without loss.");
        }

        if (level != ImageLevel.Smallest || rgba8Only) return current;

        // Smallest: a palette, if the picture has more colours than a palette holds and the loss stays within the limit.
        int distinct = DistinctColours(original, 257);
        if (distinct <= 256) { notes.Add("Already 256 colours or fewer: the palette above loses nothing."); return current; }

        try
        {
            return WithPalette(current, original, encodeOptions, options, notes);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            notes.Add("Palette not tried: " + ex.Message);
            return current;
        }
    }

    private static ImageResult WithPalette(ImageResult current, PngImage original, PngEncodeOptions encodeOptions, ImageOptions options, List<string> notes)
    {
        var quantized = Palette.Quantize(original);
        if (quantized is null)
        {
            notes.Add($"Too many colours for a palette (over {Palette.MaxDistinctColours:N0}).");
            return current;
        }
        var stats = ColourDifference.Measure(original.ToRgba8(), quantized.Rgba8!, original.Width, DistinctColours(quantized, 257));
        current = current with { Palette = stats };
        var paletted = Png.Encode(quantized, encodeOptions);
        if (!stats.Within(options.LossLimit))
        {
            notes.Add($"Palette not used: {stats} is beyond the {(options.LossLimit == LossLimit.Invisible ? "invisible" : "slight")} limit.");
            return current with { PaletteCandidate = paletted };
        }
        if (paletted.Length >= current.Bytes.Length)
        {
            notes.Add("Palette not used: it isn't smaller.");
            return current;
        }
        string? fail = Confirm(paletted, quantized, null, options, compareToOriginalFile: false);
        if (fail is not null)
        {
            notes.Add("Palette not used: " + fail);
            return current;
        }
        return current with
        {
            Bytes = paletted, Applied = ImageLevel.Smallest, PixelsIdentical = false, PaletteApplied = true, Changed = true,
            PaletteCandidate = paletted
        };
    }

    /// <summary>
    /// Null when the new file decodes to the expected pixels, by ImageKit's decoder and, when one
    /// is given, by the outside decoder too; otherwise why not.
    /// </summary>
    private static string? Confirm(byte[] file, PngImage expected, byte[]? originalFile, ImageOptions options, bool compareToOriginalFile)
    {
        PngImage back;
        try { back = Png.Decode(file); }
        catch (InvalidDataException ex) { return "it didn't decode again (" + ex.Message + ")."; }
        if (!back.SamePixels(expected)) return "its pixels differ.";

        if (options.ExternalDecoder is null) return null;
        var outside = options.ExternalDecoder(file);
        if (outside is null) return "the second decoder couldn't read it.";
        if (compareToOriginalFile && originalFile is not null)
        {
            var before = options.ExternalDecoder(originalFile);
            if (before is null) return "the second decoder couldn't read the original.";
            return SameVisible(before, outside) ? null : "the second decoder sees different pixels.";
        }
        var want = new DecodedPixels(expected.Width, expected.Height, expected.ToRgba8());
        return SameVisible(want, outside) ? null : "the second decoder sees different pixels.";
    }

    /// <summary>Equal pixels; where both are fully transparent the colour doesn't count.</summary>
    private static bool SameVisible(DecodedPixels a, DecodedPixels b)
    {
        if (a.Width != b.Width || a.Height != b.Height || a.Rgba.Length != b.Rgba.Length) return false;
        var x = a.Rgba; var y = b.Rgba;
        for (int i = 0; i < x.Length; i += 4)
        {
            if (x[i + 3] == 0 && y[i + 3] == 0) continue;
            if (x[i] != y[i] || x[i + 1] != y[i + 1] || x[i + 2] != y[i + 2] || x[i + 3] != y[i + 3]) return false;
        }
        return true;
    }

    private static int DistinctColours(PngImage image, int stopAt)
    {
        var px = image.ToRgba8();
        var seen = new HashSet<uint>();
        for (int i = 0; i < px.Length && seen.Count < stopAt; i += 4)
            seen.Add((uint)(px[i] << 24 | px[i + 1] << 16 | px[i + 2] << 8 | px[i + 3]));
        return seen.Count;
    }

    private static ImageResult ProcessJpeg(byte[] bytes, ImageLevel level, ImageOptions options)
    {
        var notes = new List<string>();
        if (level > ImageLevel.Strip) notes.Add("JPEG: recompressing would lose quality, so only metadata is removed.");
        var stripped = Jpeg.Strip(bytes, out var removed);
        if (ReferenceEquals(stripped, bytes))
            return new ImageResult(ImageFormat.Jpeg, level, ImageLevel.Strip, bytes, bytes.Length, removed, true, null, false, notes, false);

        if (removed.Contains("EXIF") && Jpeg.Parse(stripped, out _).Any(p => p.Marker == 0xE1))
            notes.Add("Kept the EXIF orientation flag, so the picture still shows the right way up.");

        if (options.ExternalDecoder is { } decode)
        {
            var a = decode(bytes);
            var b = decode(stripped);
            if (a is null || b is null || !SameVisible(a, b))
                return Unchanged(bytes, ImageFormat.Jpeg, level, "Left as it is: the second decoder couldn't confirm the picture is unchanged.");
        }
        return new ImageResult(ImageFormat.Jpeg, level, ImageLevel.Strip, stripped, bytes.Length, removed, true, null, false, notes, true);
    }

    private static ImageResult ProcessIco(byte[] bytes, ImageLevel level, ImageOptions options)
    {
        var (type, images, unused) = Ico.Read(bytes);
        var notes = new List<string>();
        var removed = new List<string>();
        var outImages = new List<IcoImage>();
        bool changed = unused > 0;
        var applied = ImageLevel.Strip;
        if (level == ImageLevel.Smallest) notes.Add("Icons keep full colour: their PNG images get no palette.");

        foreach (var im in images)
        {
            if (!im.IsPng) { outImages.Add(im); continue; }
            var chunks = Png.ReadChunks(im.Data, out _);
            var h = Png.ReadHeader(chunks[0]);
            // Only RGBA 8-bit images are re-encoded, and only as RGBA 8-bit: what icon loaders expect.
            var frameLevel = level == ImageLevel.Smallest ? ImageLevel.Lossless : level;
            if (frameLevel == ImageLevel.Lossless && !(h.ColourType == 6 && h.BitDepth == 8)) frameLevel = ImageLevel.Strip;
            var r = ProcessPng(im.Data, frameLevel, options, rgba8Only: true);
            removed.AddRange(r.Removed.Select(n => $"{n} ({im.Width}×{im.Height})"));
            foreach (var n in r.Notes) if (r.Changed || frameLevel == ImageLevel.Strip) notes.Add($"{im.Width}×{im.Height}: {n}");
            if (r.Applied == ImageLevel.Lossless) applied = ImageLevel.Lossless;
            if (r.Changed && r.Bytes.Length < im.Data.Length) { outImages.Add(im with { Data = r.Bytes }); changed = true; }
            else outImages.Add(im);
        }
        if (unused > 0) removed.Add($"Unused bytes in the file ({unused:N0})");
        if (!changed) return new ImageResult(ImageFormat.Ico, level, applied, bytes, bytes.Length, removed, true, null, false, notes, false);

        var written = Ico.Write(type, outImages);
        if (written.Length >= bytes.Length) return new ImageResult(ImageFormat.Ico, level, applied, bytes, bytes.Length, Array.Empty<string>(), true, null, false, notes, false);
        return new ImageResult(ImageFormat.Ico, level, applied, written, bytes.Length, removed, true, null, false, notes, true);
    }
}
