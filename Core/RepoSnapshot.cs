using System.IO;
using System.IO.Compression;
using System.Text;

namespace AppConsistency.Core;

public sealed class SnapshotFile
{
    /// <summary>Path relative to the snapshot root, '/'-separated.</summary>
    public required string RelPath { get; init; }
    public long Length { get; init; }
    /// <summary>Full text for source/project files.</summary>
    public string? Text { get; init; }
    /// <summary>First bytes of icon/image files, for format checks.</summary>
    public byte[]? Head { get; init; }

    public string Name => RepoSnapshot.NameOf(RelPath);
    public string Extension => Path.GetExtension(Name).ToLowerInvariant();
}

/// <summary>An in-memory view of one snapshot: file list, directory list, and the contents that matter.</summary>
public sealed class RepoSnapshot
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".sln", ".slnx", ".props", ".targets",
        ".json", ".xml", ".manifest", ".config", ".resx", ".pubxml"
    };

    private static readonly HashSet<string> HeadExtensions = new(StringComparer.OrdinalIgnoreCase) { ".ico", ".png" };

    // Not descended into when the snapshot is a plain folder.
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules"
    };

    private const long MaxTextBytes = 4 * 1024 * 1024;
    private const int HeadBytes = 4096;

    private readonly Dictionary<string, SnapshotFile> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public required string SourcePath { get; init; }
    public bool IsZip { get; init; }

    /// <summary>
    /// Every zip entry's stored time, read from the central directory; null for folder snapshots
    /// or when the directory couldn't be read.
    /// </summary>
    public List<ZipEntryTime>? EntryTimes { get; private set; }

    // The zip as read from disk, when it was small enough to keep in memory: later reads of a
    // single entry come from here, so the file isn't opened again.
    private byte[]? _zipBytes;
    public List<SnapshotFile> Files { get; } = new();
    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> RootDirs =>
        Directories.Where(d => !d.Contains('/')).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();

    public List<string> RootFiles =>
        Files.Where(f => !f.RelPath.Contains('/')).Select(f => f.RelPath).ToList();

    /// <summary>The single top-level folder of a zip, or null if the zip isn't wrapped.</summary>
    public string? WrapperFolder
    {
        get
        {
            if (!IsZip) return null;
            var dirs = RootDirs;
            return RootFiles.Count == 0 && dirs.Count == 1 ? dirs[0] : null;
        }
    }

    public SnapshotFile? Find(string relPath) => _byPath.GetValueOrDefault(relPath);

    /// <summary>
    /// Reads one file's bytes on demand. Only a rule that needs a binary calls this, so compiled
    /// output is never held in memory during an ordinary scan.
    /// </summary>
    public byte[]? ReadBytes(string relPath)
    {
        try
        {
            if (!IsZip)
            {
                var full = Path.Combine(SourcePath, relPath.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(full) ? SharedRead.ReadAll(full) : null;
            }

            using var zip = _zipBytes is not null
                ? new ZipArchive(new MemoryStream(_zipBytes, writable: false), ZipArchiveMode.Read)
                : SharedRead.OpenZip(SourcePath);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Length > 0 && Normalize(e.FullName).Equals(relPath, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;

            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static RepoSnapshot Load(string path, bool isZip)
    {
        var snapshot = new RepoSnapshot { SourcePath = path, IsZip = isZip };
        if (isZip) snapshot.LoadZip();
        else snapshot.LoadFolder(path, "");
        return snapshot;
    }

    private void LoadZip()
    {
        using var zip = SharedRead.OpenZip(SourcePath, out var bytes);
        _zipBytes = bytes;
        if (bytes is not null)
        {
            EntryTimes = ZipTimes.Read(bytes);
        }
        else
        {
            using var stream = SharedRead.Open(SourcePath);
            EntryTimes = ZipTimes.Read(stream);
        }

        foreach (var entry in zip.Entries)
        {
            var raw = entry.FullName.Replace('\\', '/');
            bool isDir = raw.EndsWith('/');
            var rel = Normalize(raw);
            if (rel.Length == 0) continue;

            AddParents(rel, includeSelf: isDir);
            if (isDir) continue;

            AddFile(rel, entry.Length, () => entry.Open());
        }
    }

    private void LoadFolder(string absolute, string rel)
    {
        foreach (var dir in Directory.EnumerateDirectories(absolute))
        {
            var name = Path.GetFileName(dir);
            var childRel = rel.Length == 0 ? name : rel + "/" + name;
            Directories.Add(childRel);
            if (SkipFolders.Contains(name)) continue;
            LoadFolder(dir, childRel);
        }

        foreach (var file in Directory.EnumerateFiles(absolute))
        {
            var name = Path.GetFileName(file);
            var childRel = rel.Length == 0 ? name : rel + "/" + name;
            AddFile(childRel, new FileInfo(file).Length, () => SharedRead.Open(file));
        }
    }

    private void AddFile(string rel, long length, Func<Stream> open)
    {
        var ext = Path.GetExtension(rel);
        string? text = null;
        byte[]? head = null;

        // License files and readmes are read as text too: the license check reads the one, and the
        // unlisted-files rule looks in the other for the files it uses.
        if ((TextExtensions.Contains(ext) || LicenseCheck.IsLicenseName(NameOf(rel)) || ReadmeCollector.IsReadmeName(NameOf(rel)))
            && length <= MaxTextBytes)
        {
            using var stream = open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        else if (HeadExtensions.Contains(ext))
        {
            using var stream = open();
            head = ReadUpTo(stream, HeadBytes);
        }

        var file = new SnapshotFile { RelPath = rel, Length = length, Text = text, Head = head };
        Files.Add(file);
        _byPath[rel] = file;
    }

    private void AddParents(string rel, bool includeSelf)
    {
        var parts = rel.Split('/');
        int count = includeSelf ? parts.Length : parts.Length - 1;
        for (int i = 1; i <= count; i++)
            Directories.Add(string.Join('/', parts, 0, i));
    }

    private static byte[] ReadUpTo(Stream stream, int max)
    {
        var buffer = new byte[max];
        int total = 0, read;
        while (total < max && (read = stream.Read(buffer, total, max - total)) > 0)
            total += read;
        Array.Resize(ref buffer, total);
        return buffer;
    }

    // ---------- Path helpers ('/'-separated, relative) ----------

    public static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.Trim('/');
    }

    public static string DirOf(string rel)
    {
        int i = rel.LastIndexOf('/');
        return i < 0 ? "" : rel[..i];
    }

    public static string NameOf(string rel)
    {
        int i = rel.LastIndexOf('/');
        return i < 0 ? rel : rel[(i + 1)..];
    }

    /// <summary>Resolves a project-relative path (may contain .. and backslashes).</summary>
    public static string Combine(string dir, string relative)
    {
        var parts = new List<string>();
        if (dir.Length > 0) parts.AddRange(dir.Split('/'));

        foreach (var segment in relative.Replace('\\', '/').Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(segment);
            }
        }
        return string.Join('/', parts);
    }

    public static bool IsSameOrUnder(string dir, string ancestor) =>
        ancestor.Length == 0
        || dir.Equals(ancestor, StringComparison.OrdinalIgnoreCase)
        || dir.StartsWith(ancestor + "/", StringComparison.OrdinalIgnoreCase);
}
