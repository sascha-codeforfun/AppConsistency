using System.IO;
using System.IO.Compression;

namespace AppConsistency.Core;

/// <summary>What to leave out of, or change in, a rewritten copy. Paths are snapshot-relative, '/'-separated.</summary>
public sealed class ZipChanges
{
    public HashSet<string> RemoveFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whole folders to leave out, with everything under them (.git/, .github/).</summary>
    public List<string> RemoveFolders { get; } = new();

    /// <summary>File names to leave out wherever they are (the private files, for a publish copy).</summary>
    public HashSet<string> RemoveNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>New content for existing files (edited project files).</summary>
    public Dictionary<string, byte[]> Replace { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Removes(string rel) =>
        RemoveFiles.Contains(rel)
        || RemoveNames.Contains(RepoSnapshot.NameOf(rel))
        || RemoveFolders.Any(folder => RepoSnapshot.IsSameOrUnder(rel, folder));
}

/// <summary>
/// Writes every zip AppConsistency produces: the same files with the same content, in sorted
/// order, every entry dated one day at 00:00:00 and without the NTFS or extended timestamps
/// that file managers would show instead. The same content and day always give the same bytes.
/// The source is only read; output goes to a temporary file that is moved into place when
/// complete, and an existing file is never overwritten.
/// </summary>
public static class CleanZip
{
    /// <summary>The release time for a day: always 00:00:00.</summary>
    public static DateTimeOffset Stamp(DateTime day) =>
        new(DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified));

    /// <summary>Zip headers can only hold dates from 1980 to 2107.</summary>
    public static bool IsValidDay(DateTime day) => day.Year is >= 1980 and <= 2107;

    /// <summary>A rewritten copy of a snapshot zip, written to a new file.</summary>
    public static void WriteFile(string sourceZip, string target, DateTime day, ZipChanges? changes = null)
    {
        if (File.Exists(target)) throw new IOException($"{Path.GetFileName(target)} already exists.");

        var temp = target + ".partial";
        try
        {
            if (File.Exists(temp)) File.Delete(temp);

            using (var source = SharedRead.OpenZip(sourceZip))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                Write(source, output, day, changes);

            File.Move(temp, target, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    /// <summary>A rewritten copy of a snapshot zip, in memory (for the backup, which nests them).</summary>
    public static byte[] ToBytes(string sourceZip, DateTime day, ZipChanges? changes = null)
    {
        using var source = SharedRead.OpenZip(sourceZip);
        using var output = new MemoryStream();
        Write(source, output, day, changes);
        return output.ToArray();
    }

    public static void Write(ZipArchive source, Stream output, DateTime day, ZipChanges? changes = null)
    {
        var stamp = Stamp(day);
        var entries = source.Entries
            .Select(e => (Entry: e, Rel: RepoSnapshot.Normalize(e.FullName), IsDir: e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')))
            .Where(e => e.Rel.Length > 0)
            .ToList();

        var keptFiles = entries
            .Where(e => !e.IsDir && (changes is null || !changes.Removes(e.Rel)))
            .ToList();

        // A folder entry stays unless the fix removed it, or removed every file that was in it.
        // A folder that held no files to begin with stays as it was.
        var keptDirs = entries
            .Where(e => e.IsDir)
            .Where(e => changes is null || !changes.RemoveFolders.Any(f => RepoSnapshot.IsSameOrUnder(e.Rel, f)))
            .Where(e =>
                !entries.Any(o => !o.IsDir && RepoSnapshot.IsSameOrUnder(o.Rel, e.Rel))
                || keptFiles.Any(o => RepoSnapshot.IsSameOrUnder(o.Rel, e.Rel)))
            .ToList();

        var names = new HashSet<string>(StringComparer.Ordinal);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var item in keptFiles.Concat(keptDirs).OrderBy(e => e.Entry.FullName, StringComparer.Ordinal))
        {
            if (!names.Add(item.Entry.FullName)) continue;   // a duplicate name would only confuse the extract

            if (item.IsDir)
            {
                var folder = zip.CreateEntry(item.Entry.FullName, CompressionLevel.NoCompression);
                folder.LastWriteTime = stamp;
                continue;
            }

            var entry = zip.CreateEntry(item.Entry.FullName, CompressionLevel.Optimal);
            entry.LastWriteTime = stamp;
            using var target = entry.Open();

            if (changes is not null && changes.Replace.TryGetValue(item.Rel, out var bytes))
            {
                target.Write(bytes);
            }
            else
            {
                using var content = item.Entry.Open();
                content.CopyTo(target);
            }
        }
    }

    /// <summary>One file into an open archive, dated like everything else in it.</summary>
    public static void AddEntry(ZipArchive zip, string name, ReadOnlySpan<byte> content, DateTime day, CompressionLevel level)
    {
        var entry = zip.CreateEntry(name, level);
        entry.LastWriteTime = Stamp(day);
        using var stream = entry.Open();
        stream.Write(content);
    }
}
