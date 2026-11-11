using System;
using System.Collections.Generic;

namespace ImageKit;

// ImageKit makes image files smaller and removes the metadata nothing displays, for PNG, ICO and
// JPEG. It works on bytes in and bytes out, needs nothing beyond the .NET base library
// (System.IO.Compression for zlib) and holds no state, so the folder can be copied into another
// project as is. Entry points: ImageFile.Inspect and ImageFile.Process. The pieces (Png, Palette,
// ColourDifference, Jpeg, Ico) can be used on their own.

/// <summary>What may be done to a file. Each level includes the one before it.</summary>
public enum ImageLevel
{
    /// <summary>Nothing: every byte stays, metadata included.</summary>
    AsIs,
    /// <summary>Metadata nothing displays is removed; the compressed image data stays as it is.</summary>
    Strip,
    /// <summary>Strip, then recompress. Every pixel stays identical, which is checked.</summary>
    Lossless,
    /// <summary>Lossless, plus a palette of up to 256 colours where the loss stays within the limit.</summary>
    Smallest
}

/// <summary>The image formats ImageKit reads, recognised by their bytes, never by a file name.</summary>
public enum ImageFormat
{
    Unknown,
    Png,
    Ico,
    Jpeg
}

/// <summary>How much colour difference a palette may cause, measured as CIEDE2000 ΔE per pixel.</summary>
public enum LossLimit
{
    /// <summary>99 % of pixels within ΔE 1.0 and no spot beyond 3.0: not visible side by side.</summary>
    Invisible,
    /// <summary>99 % of pixels within ΔE 2.3 and no spot beyond 6.0: visible only when looked for.</summary>
    Slight
}

/// <summary>How hard the encoder tries.</summary>
public enum Effort
{
    /// <summary>The two filter strategies that win most often, one zlib setting: a fast estimate of Full.</summary>
    Quick,
    /// <summary>Every candidate form and filter strategy, then the strongest zlib settings.</summary>
    Full
}

/// <summary>Pixels as an outside decoder sees them: 8 bits per channel, RGBA, not premultiplied.</summary>
public sealed record DecodedPixels(int Width, int Height, byte[] Rgba);

public sealed class ImageOptions
{
    public LossLimit LossLimit { get; init; } = LossLimit.Invisible;
    public Effort Effort { get; init; } = Effort.Full;

    /// <summary>
    /// An optional second decoder, so the result isn't checked only by the code that wrote it. It is
    /// given a PNG or JPEG file and returns its pixels, or null when it can't read them. When set,
    /// a lossless result must decode to the same pixels as the original (fully transparent pixels
    /// may differ in colour), and a palette result must decode to the pixels that were measured.
    /// A result it can't confirm is not used.
    /// </summary>
    public Func<byte[], DecodedPixels?>? ExternalDecoder { get; init; }
}

/// <summary>One piece of metadata found in a file.</summary>
public sealed record MetadataItem(
    string Name,          // "C2PA content credentials", "EXIF", "Text", …
    string Detail,        // what it holds, as far as it can be read: "GPS position, camera Canon EOS 5D"
    int Bytes,
    bool Displayed);      // true for what changes how the image looks (colour profile, gamma, DPI)

/// <summary>What a file is and what it carries. Reading only; nothing is changed.</summary>
public sealed record ImageInfo(
    ImageFormat Format,
    int Width,
    int Height,
    int Frames,                         // images in an .ico; 1 otherwise
    string Detail,                      // "PNG, RGBA 8-bit", "ICO, 7 images (5 PNG, 2 bitmap)", "JPEG, baseline"
    IReadOnlyList<MetadataItem> Metadata,
    string? Problem);                   // why the file can't be processed, or null

/// <summary>
/// How far a palette result is from the original, in CIEDE2000 ΔE. Mean and P99 are per pixel.
/// Max is the worst single pixel. WorstSpot is the worst after both pictures are averaged over
/// 3 × 3 pixels, the way the eye blends neighbouring pixels at normal size: a lone edge pixel that
/// shifted shade counts for little there, a changed area counts in full. The limits use WorstSpot.
/// </summary>
public sealed record LossStats(double Mean, double P99, double Max, double WorstSpot, int Colours)
{
    public bool Within(LossLimit limit) => limit switch
    {
        LossLimit.Invisible => P99 <= 1.0 && WorstSpot <= 3.0,
        _ => P99 <= 2.3 && WorstSpot <= 6.0
    };

    public override string ToString() =>
        $"ΔE mean {Mean:0.00}, 99 % within {P99:0.00}, worst spot {WorstSpot:0.0}, worst pixel {Max:0.0}";
}

/// <summary>The outcome of ImageFile.Process.</summary>
public sealed record ImageResult(
    ImageFormat Format,
    ImageLevel Requested,
    ImageLevel Applied,                 // what was actually done: a JPEG never goes past Strip
    byte[] Bytes,                       // the new file, or the original bytes when nothing changed
    int OriginalLength,
    IReadOnlyList<string> Removed,      // the metadata that went, by name
    bool PixelsIdentical,               // true unless a palette was applied
    LossStats? Palette,                 // the palette attempt, applied or refused; null when none was made
    bool PaletteApplied,
    IReadOnlyList<string> Notes,
    bool Changed)                       // false when Bytes are the original bytes
{
    public int SavedBytes => OriginalLength - Bytes.Length;

    /// <summary>The palette version of a PNG, applied or refused, so it can be shown next to the original.</summary>
    public byte[]? PaletteCandidate { get; init; }
}
