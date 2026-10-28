using System.IO;
using System.IO.Compression;

namespace AppConsistency.Core;

/// <summary>
/// Every read of the history folder goes through here, so AppConsistency never gets in the way of
/// the apps that write snapshots. Files are opened read-only with full sharing: while one is open
/// here, any other app can still read, write, rename or delete it. Snapshot zips up to
/// <see cref="InMemoryLimit"/> are read into memory in one go and the file is closed straight away,
/// so the handle is open for milliseconds rather than for the whole scan.
/// </summary>
public static class SharedRead
{
    /// <summary>Above this a zip is read from disk while it is checked (still shared, just open longer).</summary>
    public const long InMemoryLimit = 200L * 1024 * 1024;

    private const FileShare Everyone = FileShare.ReadWrite | FileShare.Delete;

    public static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, Everyone, 81920, FileOptions.SequentialScan);

    /// <summary>The whole file, read in one go. Throws EndOfStreamException if it shrinks while being read.</summary>
    public static byte[] ReadAll(string path)
    {
        using var stream = Open(path);
        var length = stream.Length;
        if (length > int.MaxValue) throw new IOException($"{Path.GetFileName(path)} is too large to read into memory.");

        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>
    /// A zip to read. Small ones (nearly all snapshots) come back as a copy in memory, so the file on
    /// disk is already closed again; larger ones keep a shared handle until the archive is disposed.
    /// </summary>
    public static ZipArchive OpenZip(string path) => OpenZip(path, out _);

    /// <summary>As <see cref="OpenZip(string)"/>, also handing back the bytes when they were read into memory.</summary>
    public static ZipArchive OpenZip(string path, out byte[]? bytes)
    {
        var length = new FileInfo(path).Length;
        if (length <= InMemoryLimit)
        {
            bytes = ReadAll(path);
            return new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        }

        bytes = null;
        var stream = Open(path);
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
