using System;

namespace ImageKit;

/// <summary>
/// Decoded pixels in one canonical form: RGBA, not premultiplied, 8 or 16 bits per channel. Every
/// PNG colour type and bit depth decodes to this, so two files can be compared pixel for pixel
/// whatever form each was stored in. A transparency key (tRNS on grey or RGB) becomes alpha 0 with
/// the stored colour kept.
/// </summary>
public sealed class PngImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>True when the channels are 16-bit (Rgba16 is set); otherwise Rgba8 is set.</summary>
    public bool Is16 { get; }

    public byte[]? Rgba8 { get; }
    public ushort[]? Rgba16 { get; }

    public PngImage(int width, int height, byte[] rgba8)
    {
        if (rgba8.Length != (long)width * height * 4) throw new ArgumentException("Pixel buffer doesn't match the size.");
        Width = width; Height = height; Rgba8 = rgba8;
    }

    public PngImage(int width, int height, ushort[] rgba16)
    {
        if (rgba16.Length != (long)width * height * 4) throw new ArgumentException("Pixel buffer doesn't match the size.");
        Width = width; Height = height; Rgba16 = rgba16; Is16 = true;
    }

    /// <summary>
    /// The 8-bit form when it says exactly the same thing: every 16-bit value is v * 257, the way
    /// decoders widen 8 bits to 16. Null when some value would change.
    /// </summary>
    public PngImage? ExactlyAs8()
    {
        if (!Is16) return this;
        var src = Rgba16!;
        var dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            int v = src[i];
            if ((v >> 8) != (v & 0xFF)) return null;
            dst[i] = (byte)(v >> 8);
        }
        return new PngImage(Width, Height, dst);
    }

    /// <summary>8 bits per channel, rounding 16-bit values; the form colour measurement works on.</summary>
    public byte[] ToRgba8()
    {
        if (!Is16) return Rgba8!;
        var src = Rgba16!;
        var dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i++) dst[i] = (byte)((src[i] * 255 + 32767) / 65535);
        return dst;
    }

    /// <summary>Exactly the same pixels: same size and every channel equal, compared at 16 bits.</summary>
    public bool SamePixels(PngImage other)
    {
        if (Width != other.Width || Height != other.Height) return false;
        int n = Width * Height * 4;
        for (int i = 0; i < n; i++)
            if (Sample16(i) != other.Sample16(i)) return false;
        return true;
    }

    private int Sample16(int i) => Is16 ? Rgba16![i] : Rgba8![i] * 257;
}
