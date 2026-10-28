using System.IO;
using System.IO.Compression;

namespace AppConsistency.Core;

/// <summary>
/// Finds the readmes in a snapshot and gathers the chosen ones from every app, with each app's
/// license files, into one zip in Downloads, one folder per app, so the publishing toolchain has
/// both. Every entry is dated the day of the collection at 00:00:00. The history folder is only
/// ever read; nothing is overwritten.
/// </summary>
public static class ReadmeCollector
{
    // Build output and tool folders: a readme in there isn't the repo's own.
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules"
    };

    /// <summary>2026-10-13-14-32-05-REPO-READMES.zip: the seconds keep every run apart.</summary>
    public static string CollectName(DateTime when) => Standard.FormatName(Standard.Current.Release.ReadmeCollectionName, when);

    /// <summary>One app's newest snapshot and the readmes and license files in it to collect (snapshot-relative paths).</summary>
    public sealed record Source(string AppName, string SnapshotPath, bool IsZip, IReadOnlyList<string> Readmes,
                                IReadOnlyList<string> Licenses)
    {
        public IEnumerable<string> Files => Readmes.Concat(Licenses).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record CollectResult(List<string> Added, List<string> Failed);

    /// <summary>README, README.md, readme.txt, Readme.markdown … in any folder outside build output.</summary>
    public static List<string> Find(RepoSnapshot s) =>
        s.Files
            .Where(f => IsReadmeName(f.Name) && !InSkippedFolder(f.RelPath))
            .Select(f => f.RelPath)
            .OrderBy(p => p.Count(c => c == '/'))
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsMarkdown(string relPath) =>
        Path.GetExtension(relPath).Equals(".md", StringComparison.OrdinalIgnoreCase);

    public static bool IsReadmeName(string name) =>
        Path.GetFileNameWithoutExtension(name).Equals("README", StringComparison.OrdinalIgnoreCase);

    private static bool InSkippedFolder(string relPath)
    {
        var parts = relPath.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
            if (SkipFolders.Contains(parts[i])) return true;
        return false;
    }

    /// <summary>
    /// Where a readme goes inside the collection: AppName/ plus its path in the repo, without the
    /// zip's wrapper folder, so two readmes from one app never collide.
    /// </summary>
    public static string EntryName(string appName, string relPath, string? wrapper)
    {
        var inner = wrapper is not null && relPath.StartsWith(wrapper + "/", StringComparison.OrdinalIgnoreCase)
            ? relPath[(wrapper.Length + 1)..]
            : relPath;
        return appName + "/" + inner;
    }

    /// <summary>
    /// Written to a temporary file and moved into place only when complete, so a half-written
    /// collection never sits under the real name. Throws when nothing at all could be added.
    /// </summary>
    public static CollectResult Collect(IReadOnlyList<Source> sources, string target, DateTime day, IProgress<string>? progress = null)
    {
        if (File.Exists(target)) throw new IOException($"{Path.GetFileName(target)} already exists.");

        var added = new List<string>();
        var failed = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var temp = target + ".partial";

        try
        {
            if (File.Exists(temp)) File.Delete(temp);

            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    var source = sources[i];
                    progress?.Report($"Collecting {source.AppName}'s readme and license ({i + 1} of {sources.Count})…");

                    try
                    {
                        if (source.IsZip) AddFromZip(zip, source, day, names, added, failed);
                        else AddFromFolder(zip, source, day, names, added, failed);
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{source.AppName} ({ex.Message})");
                    }
                }
            }

            if (added.Count == 0) throw new IOException("No readme or license could be added, so nothing was written.");
            File.Move(temp, target, overwrite: false);
            return new CollectResult(added, failed);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private static void AddFromZip(ZipArchive output, Source source, DateTime day, HashSet<string> names,
                                   List<string> added, List<string> failed)
    {
        using var input = SharedRead.OpenZip(source.SnapshotPath);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in input.Entries)
            if (entry.Name.Length > 0) entries.TryAdd(RepoSnapshot.Normalize(entry.FullName), entry);

        var wrapper = WrapperOf(entries.Keys);

        foreach (var rel in Ordered(source, wrapper))
        {
            if (!entries.TryGetValue(rel, out var entry))
            {
                failed.Add($"{source.AppName}/{rel} (no longer in the snapshot)");
                continue;
            }
            using var stream = entry.Open();
            Write(output, EntryName(source.AppName, rel, wrapper), stream, day, names, added, failed);
        }
    }

    private static void AddFromFolder(ZipArchive output, Source source, DateTime day, HashSet<string> names,
                                      List<string> added, List<string> failed)
    {
        foreach (var rel in Ordered(source, null))
        {
            var full = Path.Combine(source.SnapshotPath, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                failed.Add($"{source.AppName}/{rel} (no longer in the folder)");
                continue;
            }
            using var stream = SharedRead.Open(full);
            Write(output, EntryName(source.AppName, rel, null), stream, day, names, added, failed);
        }
    }

    /// <summary>An app's files in the order their entries are written: sorted by the name they get.</summary>
    private static IEnumerable<string> Ordered(Source source, string? wrapper) =>
        source.Files.OrderBy(rel => EntryName(source.AppName, rel, wrapper), StringComparer.Ordinal);

    private static void Write(ZipArchive output, string entryName, Stream content, DateTime day,
                              HashSet<string> names, List<string> added, List<string> failed)
    {
        if (!names.Add(entryName))
        {
            failed.Add($"{entryName} (same name as another entry)");
            return;
        }

        var entry = output.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = CleanZip.Stamp(day);
        using (var target = entry.Open()) content.CopyTo(target);
        added.Add(entryName);
    }

    /// <summary>The single top-level folder every entry sits under, or null when the zip isn't wrapped.</summary>
    private static string? WrapperOf(IEnumerable<string> paths)
    {
        string? wrapper = null;
        foreach (var path in paths)
        {
            int slash = path.IndexOf('/');
            if (slash < 0) return null;
            var top = path[..slash];
            if (wrapper is null) wrapper = top;
            else if (!wrapper.Equals(top, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return wrapper;
    }
}
