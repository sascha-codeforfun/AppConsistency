using System;
using System.Collections.Generic;
using System.IO;

namespace ImageKit;

/// <summary>One image in an .ico or .cur file, with its directory fields.</summary>
public sealed record IcoImage(byte WidthByte, byte HeightByte, byte Colours, byte Reserved, ushort Planes, ushort BitCount, byte[] Data)
{
    public int Width => WidthByte == 0 ? 256 : WidthByte;
    public int Height => HeightByte == 0 ? 256 : HeightByte;

    /// <summary>True for a PNG image; otherwise it is a bitmap (DIB) without metadata.</summary>
    public bool IsPng => Png.IsPng(Data);
}

/// <summary>
/// Reads and writes the .ico container: a directory of images, each either a PNG file or a
/// bitmap. Writing packs the images one after another in their original order, directory fields
/// unchanged except sizes and offsets.
/// </summary>
public static class Ico
{
    public static bool IsIco(ReadOnlySpan<byte> b)
    {
        if (b.Length < 22 || b[0] != 0 || b[1] != 0 || b[2] is not (1 or 2) || b[3] != 0) return false;
        int count = b[4] | b[5] << 8;
        if (count == 0 || b.Length < 6 + 16 * count) return false;
        for (int i = 0; i < count; i++)
        {
            int e = 6 + 16 * i;
            long size = (uint)(b[e + 8] | b[e + 9] << 8 | b[e + 10] << 16 | b[e + 11] << 24);
            long off = (uint)(b[e + 12] | b[e + 13] << 8 | b[e + 14] << 16 | b[e + 15] << 24);
            if (size == 0 || off < 6 + 16 * count || off + size > b.Length) return false;
        }
        return true;
    }

    /// <summary>The container type (1 icon, 2 cursor) and its images. Throws InvalidDataException when it isn't an .ico.</summary>
    public static (ushort Type, List<IcoImage> Images, int Unused) Read(byte[] b)
    {
        if (!IsIco(b)) throw new InvalidDataException("Not an icon file.");
        ushort type = (ushort)(b[2] | b[3] << 8);
        int count = b[4] | b[5] << 8;
        var images = new List<IcoImage>();
        long used = 6 + 16L * count;
        for (int i = 0; i < count; i++)
        {
            int e = 6 + 16 * i;
            int size = b[e + 8] | b[e + 9] << 8 | b[e + 10] << 16 | b[e + 11] << 24;
            int off = b[e + 12] | b[e + 13] << 8 | b[e + 14] << 16 | b[e + 15] << 24;
            images.Add(new IcoImage(b[e], b[e + 1], b[e + 2], b[e + 3],
                (ushort)(b[e + 4] | b[e + 5] << 8), (ushort)(b[e + 6] | b[e + 7] << 8),
                b.AsSpan(off, size).ToArray()));
            used += size;
        }
        return (type, images, (int)Math.Max(0, b.Length - used));
    }

    public static byte[] Write(ushort type, IReadOnlyList<IcoImage> images)
    {
        using var ms = new MemoryStream();
        void U16(int v) { ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8)); }
        void U32(int v) { U16(v & 0xFFFF); U16((int)((uint)v >> 16)); }
        U16(0); U16(type); U16(images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var im in images)
        {
            ms.WriteByte(im.WidthByte); ms.WriteByte(im.HeightByte); ms.WriteByte(im.Colours); ms.WriteByte(im.Reserved);
            U16(im.Planes); U16(im.BitCount); U32(im.Data.Length); U32(offset);
            offset += im.Data.Length;
        }
        foreach (var im in images) ms.Write(im.Data);
        return ms.ToArray();
    }
}
