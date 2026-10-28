using System.IO;
using System.Text;

namespace AppConsistency.Core;

/// <summary>One entry's stored time as file managers see it.</summary>
/// <param name="Modified">The DOS date and time in the entry header (local time, 2-second steps), or null when invalid.</param>
/// <param name="HasExtraTime">
/// The entry also carries an NTFS or extended timestamp. 7-Zip, Explorer and others show that one
/// instead of the DOS time, so a zip only looks clean when there are none.
/// </param>
public sealed record ZipEntryTime(string Name, bool IsDirectory, DateTime? Modified, bool HasExtraTime);

/// <summary>
/// Reads every entry's timestamps straight from a zip's central directory. ZipArchive doesn't
/// expose the extra fields, and those are what decide the time most tools display.
/// </summary>
public static class ZipTimes
{
    private const uint EndOfCentralDirectory = 0x06054b50;
    private const uint Zip64Locator = 0x07064b50;
    private const uint Zip64End = 0x06064b50;
    private const uint CentralHeader = 0x02014b50;

    // Extra-field ids that carry a modification time: NTFS, Info-ZIP extended and old Info-ZIP Unix.
    private static readonly HashSet<ushort> TimeFields = [0x000A, 0x5455, 0x5855];

    public static List<ZipEntryTime>? Read(byte[] zip) => Read(new MemoryStream(zip, writable: false));

    /// <summary>The entries' times, or null when the central directory can't be read.</summary>
    public static List<ZipEntryTime>? Read(Stream stream)
    {
        try
        {
            if (!stream.CanSeek || stream.Length < 22) return null;
            var (count, offset) = FindDirectory(stream);
            if (count < 0) return null;

            var list = new List<ZipEntryTime>((int)Math.Min(count, 100_000));
            stream.Position = offset;
            var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            for (long i = 0; i < count; i++)
            {
                if (reader.ReadUInt32() != CentralHeader) return null;
                reader.ReadUInt16();                   // version made by
                reader.ReadUInt16();                   // version needed
                var flags = reader.ReadUInt16();
                reader.ReadUInt16();                   // method
                var time = reader.ReadUInt16();
                var date = reader.ReadUInt16();
                reader.ReadUInt32();                   // crc
                reader.ReadUInt32();                   // compressed size
                reader.ReadUInt32();                   // size
                var nameLength = reader.ReadUInt16();
                var extraLength = reader.ReadUInt16();
                var commentLength = reader.ReadUInt16();
                reader.ReadUInt16();                   // disk
                reader.ReadUInt16();                   // internal attributes
                reader.ReadUInt32();                   // external attributes
                reader.ReadUInt32();                   // local header offset

                var nameBytes = reader.ReadBytes(nameLength);
                var extra = reader.ReadBytes(extraLength);
                reader.ReadBytes(commentLength);

                var name = (flags & 0x0800) != 0 ? Encoding.UTF8.GetString(nameBytes) : Encoding.Latin1.GetString(nameBytes);
                bool isDir = name.EndsWith('/') || name.EndsWith('\\');
                list.Add(new ZipEntryTime(name, isDir, FromDos(date, time), HasTimeField(extra)));
            }

            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A DOS date and time as written in a zip header, or null when the fields don't form a date.</summary>
    public static DateTime? FromDos(ushort date, ushort time)
    {
        int day = date & 0x1F, month = (date >> 5) & 0x0F, year = 1980 + (date >> 9);
        int second = (time & 0x1F) * 2, minute = (time >> 5) & 0x3F, hour = time >> 11;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        if (hour > 23 || minute > 59 || second > 59) return null;
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    }

    private static bool HasTimeField(byte[] extra)
    {
        int i = 0;
        while (i + 4 <= extra.Length)
        {
            ushort id = (ushort)(extra[i] | (extra[i + 1] << 8));
            int size = extra[i + 2] | (extra[i + 3] << 8);
            if (TimeFields.Contains(id)) return true;
            i += 4 + size;
        }
        return false;
    }

    /// <summary>Entry count and central-directory offset, following a Zip64 record when there is one.</summary>
    private static (long Count, long Offset) FindDirectory(Stream stream)
    {
        int window = (int)Math.Min(stream.Length, 22 + 65535);
        var tail = new byte[window];
        stream.Position = stream.Length - window;
        stream.ReadExactly(tail);

        for (int i = window - 22; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(tail, i) != EndOfCentralDirectory) continue;

            long count = BitConverter.ToUInt16(tail, i + 10);
            long offset = BitConverter.ToUInt32(tail, i + 16);
            long eocdPosition = stream.Length - window + i;

            if ((count == 0xFFFF || offset == 0xFFFFFFFF) && eocdPosition >= 20)
            {
                var locator = new byte[20];
                stream.Position = eocdPosition - 20;
                stream.ReadExactly(locator);
                if (BitConverter.ToUInt32(locator, 0) == Zip64Locator)
                {
                    var record = new byte[56];
                    stream.Position = (long)BitConverter.ToUInt64(locator, 8);
                    stream.ReadExactly(record);
                    if (BitConverter.ToUInt32(record, 0) != Zip64End) return (-1, 0);
                    count = (long)BitConverter.ToUInt64(record, 32);
                    offset = (long)BitConverter.ToUInt64(record, 48);
                }
            }
            return (count, offset);
        }
        return (-1, 0);
    }
}
