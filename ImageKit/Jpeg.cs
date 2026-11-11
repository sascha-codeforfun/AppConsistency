using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ImageKit;

/// <summary>
/// Reads a JPEG file's marker segments and removes the metadata ones, without decoding or
/// re-encoding the picture: the compressed image data is copied byte for byte, so nothing about
/// the picture changes. Kept: what affects how it looks (JFIF header, ICC colour profile, Adobe
/// colour transform) and everything the decoder needs. Removed: EXIF, XMP, IPTC/Photoshop data,
/// C2PA credentials, comments, thumbnails, other application segments, and bytes after the end of
/// the image. When EXIF says the picture is rotated or mirrored, a minimal EXIF with only that
/// flag is written back, so the picture still shows the right way up.
/// </summary>
public static class Jpeg
{
    public static bool IsJpeg(ReadOnlySpan<byte> b) => b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    /// <summary>One part of the file: a marker segment, or the compressed data after a start of scan.</summary>
    internal sealed record Part(int Marker, int Offset, int Length)   // Marker -1: entropy-coded data
    {
        public bool IsSegment => Marker >= 0;
    }

    /// <summary>The parts after SOI, up to and including EOI. Throws InvalidDataException when the structure is broken.</summary>
    internal static List<Part> Parse(byte[] b, out int end)
    {
        if (!IsJpeg(b)) throw new InvalidDataException("Not a JPEG file.");
        var parts = new List<Part>();
        int pos = 2;
        while (true)
        {
            if (pos >= b.Length) throw new InvalidDataException("The file ends before the end-of-image marker.");
            if (b[pos] != 0xFF) throw new InvalidDataException("Unexpected bytes between segments.");
            int start = pos;
            while (pos < b.Length && b[pos] == 0xFF) pos++;   // fill bytes
            if (pos >= b.Length) throw new InvalidDataException("The file ends inside a marker.");
            int m = b[pos++];

            if (m == 0xD9) { parts.Add(new Part(m, start, pos - start)); end = pos; return parts; }
            if (m is >= 0xD0 and <= 0xD7 || m == 0x01 || m == 0xD8) { parts.Add(new Part(m, start, pos - start)); continue; }

            if (pos + 2 > b.Length) throw new InvalidDataException("A segment is cut off.");
            int len = (b[pos] << 8) | b[pos + 1];
            if (len < 2 || pos + len > b.Length) throw new InvalidDataException("A segment is longer than the file.");
            pos += len;
            parts.Add(new Part(m, start, pos - start));

            if (m == 0xDA)
            {
                int s = pos;
                while (pos < b.Length)
                {
                    if (b[pos] != 0xFF) { pos++; continue; }
                    if (pos + 1 >= b.Length) { pos = b.Length; break; }
                    int next = b[pos + 1];
                    if (next == 0x00 || next is >= 0xD0 and <= 0xD7) { pos += 2; continue; }
                    if (next == 0xFF) { pos++; continue; }
                    break;
                }
                parts.Add(new Part(-1, s, pos - s));
            }
        }
    }

    /// <summary>The payload of a segment part: the bytes after marker and length.</summary>
    internal static ReadOnlySpan<byte> Payload(byte[] b, Part p)
    {
        int head = 0;
        while (b[p.Offset + head] == 0xFF) head++;
        return b.AsSpan(p.Offset + head + 3, p.Length - head - 3);
    }

    internal enum Role { Keep, Remove }

    /// <summary>What a segment is and whether Strip keeps it.</summary>
    internal static (Role Role, string Name, string Detail) Classify(byte[] b, Part p)
    {
        int m = p.Marker;
        if (m < 0 || m is < 0xE0 or > 0xEF && m != 0xFE) return (Role.Keep, "", "");
        var pay = Payload(b, p).ToArray();
        bool Starts(string s) => pay.Length >= s.Length && Encoding.ASCII.GetString(pay, 0, s.Length) == s;

        return m switch
        {
            0xE0 when Starts("JFIF\0") => (Role.Keep, "JFIF header", "pixel density"),
            0xE0 when Starts("JFXX\0") => (Role.Remove, "Thumbnail (JFXX)", ""),
            0xE1 when Starts("Exif\0\0") && Exif.Read(pay[6..]) is { OnlyOrientation: true } e => (Role.Keep, "EXIF orientation", "orientation " + e.Orientation),
            0xE1 when Starts("Exif\0\0") => (Role.Remove, "EXIF", ExifDetail(pay[6..])),
            0xE1 when Starts("http://ns.adobe.com/xap/1.0/\0") => (Role.Remove, "XMP", XmpDetail(pay)),
            0xE1 when Starts("http://ns.adobe.com/xmp/extension/\0") => (Role.Remove, "XMP (extended)", ""),
            0xE2 when Starts("ICC_PROFILE\0") => (Role.Keep, "Colour profile (ICC)", ""),
            0xE2 when Starts("MPF\0") => (Role.Remove, "Multi-picture index (MPF)", "points to further pictures after the main one"),
            0xEB when IndexOf(pay, "jumb") >= 0 || IndexOf(pay, "c2pa") >= 0 => (Role.Remove, "C2PA content credentials", "signed note on how the file was made"),
            0xED when Starts("Photoshop 3.0\0") => (Role.Remove, "IPTC / Photoshop data", ""),
            0xEE when Starts("Adobe") => (Role.Keep, "Adobe colour transform", ""),
            0xFE => (Role.Remove, "Comment", Printable(pay)),
            _ => (Role.Remove, $"Application data (APP{m - 0xE0})", Printable(pay[..Math.Min(pay.Length, 24)]))
        };
    }

    internal static int IndexOf(ReadOnlySpan<byte> hay, string needle) => hay.IndexOf(Encoding.ASCII.GetBytes(needle));

    private static string Printable(ReadOnlySpan<byte> s)
    {
        var t = new string(Encoding.Latin1.GetString(s).Where(c => c >= ' ' && c < 0x7F).ToArray()).Trim();
        return t.Length <= 60 ? t : t[..57] + "…";
    }

    private static string XmpDetail(ReadOnlySpan<byte> pay)
    {
        var text = Encoding.UTF8.GetString(pay);
        var found = new List<string>();
        if (text.Contains("CreatorTool", StringComparison.Ordinal)) found.Add("creator tool");
        if (text.Contains("History", StringComparison.Ordinal)) found.Add("edit history");
        if (text.Contains("GPS", StringComparison.Ordinal)) found.Add("GPS position");
        if (text.Contains("dc:creator", StringComparison.Ordinal)) found.Add("author");
        if (text.Contains("dc:rights", StringComparison.Ordinal) || text.Contains("xmpRights", StringComparison.Ordinal)) found.Add("rights");
        return string.Join(", ", found);
    }

    // ---------------- EXIF ----------------

    /// <summary>What an EXIF block (TIFF structure, from its byte order mark) holds, in words.</summary>
    public static string ExifDetail(ReadOnlySpan<byte> tiff)
    {
        var e = Exif.Read(tiff.ToArray());
        if (e is null) return "unreadable";
        var parts = new List<string>();
        if (e.HasGps) parts.Add("GPS position");
        string camera = string.Join(" ", new[] { e.Make, e.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (camera.Length > 0) parts.Add("camera " + camera);
        if (e.Serial) parts.Add("serial number");
        if (!string.IsNullOrWhiteSpace(e.Software)) parts.Add("software " + e.Software);
        if (!string.IsNullOrWhiteSpace(e.Date)) parts.Add("date " + e.Date);
        if (!string.IsNullOrWhiteSpace(e.Artist)) parts.Add("artist " + e.Artist);
        if (!string.IsNullOrWhiteSpace(e.Copyright)) parts.Add("copyright " + e.Copyright);
        if (e.Thumbnail) parts.Add("thumbnail");
        if (e.Orientation is > 1 and <= 8) parts.Add("orientation " + e.Orientation);
        return string.Join(", ", parts);
    }

    /// <summary>The few EXIF fields that matter for privacy, licensing and display.</summary>
    internal sealed class Exif
    {
        public bool HasGps, Serial, Thumbnail;

        /// <summary>IFD0 holds the orientation tag and nothing else, no further IFDs: what Strip writes back.</summary>
        public bool OnlyOrientation;
        public string? Make, Model, Software, Date, Artist, Copyright;
        public int Orientation;

        public static Exif? Read(byte[] t)
        {
            try
            {
                if (t.Length < 8) return null;
                bool le = t[0] == 'I' && t[1] == 'I';
                if (!le && !(t[0] == 'M' && t[1] == 'M')) return null;
                int U16(int o) => le ? t[o] | t[o + 1] << 8 : t[o] << 8 | t[o + 1];
                int U32(int o) => le ? t[o] | t[o + 1] << 8 | t[o + 2] << 16 | t[o + 3] << 24 : t[o] << 24 | t[o + 1] << 16 | t[o + 2] << 8 | t[o + 3];
                if (U16(2) != 42) return null;

                var e = new Exif();
                var seen = new HashSet<int>();
                void Ifd(int off, bool first)
                {
                    if (off <= 0 || off + 2 > t.Length || !seen.Add(off)) return;
                    int n = U16(off);
                    for (int i = 0; i < n; i++)
                    {
                        int en = off + 2 + i * 12;
                        if (en + 12 > t.Length) return;
                        int tag = U16(en), type = U16(en + 2), count = U32(en + 4);
                        string? Ascii()
                        {
                            if (type != 2 || count <= 0 || count > 4096) return null;
                            int at = count <= 4 ? en + 8 : U32(en + 8);
                            if (at < 0 || at + count > t.Length) return null;
                            var s = Encoding.Latin1.GetString(t, at, count).TrimEnd('\0', ' ');
                            return s.Length <= 40 ? s : s[..37] + "…";
                        }
                        switch (tag)
                        {
                            case 0x010F: e.Make = Ascii(); break;
                            case 0x0110: e.Model = Ascii(); break;
                            case 0x0131: e.Software = Ascii(); break;
                            case 0x0132: e.Date ??= Ascii(); break;
                            case 0x9003: e.Date = Ascii(); break;
                            case 0x013B: e.Artist = Ascii(); break;
                            case 0x8298: e.Copyright = Ascii(); break;
                            case 0xA431: case 0xC62F: e.Serial = true; break;
                            case 0x8825: e.HasGps = true; break;
                            case 0x0112: if (type == 3) e.Orientation = U16(en + 8); break;
                            case 0x8769: Ifd(U32(en + 8), false); break;
                        }
                    }
                    if (first)
                    {
                        int nextAt = off + 2 + n * 12;
                        if (nextAt + 4 <= t.Length && U32(nextAt) != 0) e.Thumbnail = true;
                    }
                }
                int ifd0 = U32(4);
                Ifd(ifd0, true);
                if (ifd0 > 0 && ifd0 + 2 <= t.Length && U16(ifd0) == 1 && ifd0 + 14 <= t.Length && U16(ifd0 + 2) == 0x0112 && !e.Thumbnail)
                    e.OnlyOrientation = true;
                return e;
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
        }
    }

    /// <summary>An APP1 segment with an EXIF block holding only the orientation tag.</summary>
    internal static byte[] OrientationOnly(int orientation)
    {
        var tiff = new byte[]
        {
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8,        // big-endian TIFF header, IFD0 at 8
            0, 1,                                           // one entry
            0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0,   // Orientation, SHORT, 1 value
            0, 0, 0, 0                                      // no next IFD
        };
        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        int len = payload.Length + 2;
        return new byte[] { 0xFF, 0xE1, (byte)(len >> 8), (byte)len }.Concat(payload).ToArray();
    }

    // ---------------- Info and strip ----------------

    internal static (int Width, int Height, string Kind) Frame(byte[] b, List<Part> parts)
    {
        foreach (var p in parts)
        {
            if (p.Marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                var pay = Payload(b, p);
                if (pay.Length < 5) break;
                int h = pay[1] << 8 | pay[2], w = pay[3] << 8 | pay[4];
                string kind = p.Marker is 0xC2 or 0xC6 or 0xCA or 0xCE ? "progressive" : p.Marker is 0xC3 or 0xC7 or 0xCB or 0xCF ? "lossless" : "baseline";
                return (w, h, kind);
            }
        }
        return (0, 0, "no frame header");
    }

    /// <summary>
    /// The file without its metadata segments and trailing bytes. Removed lists what went. The
    /// image data, quantisation and Huffman tables, colour profile and frame stay byte for byte.
    /// </summary>
    public static byte[] Strip(byte[] b, out List<string> removed)
    {
        var parts = Parse(b, out int end);
        removed = new List<string>();
        int orientation = 0;
        var keep = new List<Part>();

        foreach (var p in parts)
        {
            var (role, name, _) = Classify(b, p);
            if (role == Role.Keep) { keep.Add(p); continue; }
            removed.Add(name);
            if (p.Marker == 0xE1 && name == "EXIF")
            {
                var e = Exif.Read(Payload(b, p)[6..].ToArray());
                if (e is { Orientation: > 1 and <= 8 }) orientation = e.Orientation;
            }
        }
        if (end < b.Length) removed.Add($"Data after the end of the image ({b.Length - end:N0} bytes)");
        if (removed.Count == 0) return b;

        using var ms = new MemoryStream();
        ms.Write(b, 0, 2);   // SOI
        bool orientationWritten = orientation == 0;
        foreach (var p in keep)
        {
            if (!orientationWritten && !(p.Marker == 0xE0))
            {
                ms.Write(OrientationOnly(orientation));
                orientationWritten = true;
            }
            ms.Write(b, p.Offset, p.Length);
        }
        return ms.ToArray();
    }
}
