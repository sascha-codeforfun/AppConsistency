using System.IO;
using System.Text.RegularExpressions;

namespace AppConsistency.Core;

/// <summary>
/// The newest snapshot of an app as it was when checked: which one, how big, and when it last
/// changed. A different fingerprint later means a newer snapshot arrived, or this one changed.
/// Read from the directory listing only; no file is opened.
/// </summary>
public sealed record SnapshotFingerprint(string? Path, long Length, DateTime WriteUtc, int Count);

/// <summary>
/// Works out what to check from a folder:
/// - a history folder (one subfolder per app) gives one candidate per app;
/// - a single app folder (holding App-0001.zip, App-0002.zip, …) gives just that app.
/// In both cases the snapshot with the highest trailing number wins; older ones are ignored.
/// </summary>
public static class RepoLocator
{
    private static readonly Regex ZipNumber = new(@"(?<num>\d+)$", RegexOptions.Compiled);
    private static readonly Regex DirNumber = new(@"[-_ ](?<num>\d+)$", RegexOptions.Compiled);

    // Not looked into when fingerprinting an unzipped snapshot folder.
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules"
    };

    public static List<AppCandidate> Locate(string root)
    {
        // Numbered zips sitting directly in the chosen folder: it's a single app's folder.
        var self = FindLatest(root, allowFolders: false);
        if (self is not null) return [self];

        var list = new List<AppCandidate>();
        foreach (var dir in Directory.EnumerateDirectories(root)
                     .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.')) continue;

            list.Add(FindLatest(dir, allowFolders: true)
                     ?? new AppCandidate(name, dir, null, false, "", 0));
        }
        return list;
    }

    /// <summary>
    /// One app folder looked at again, as Locate would see it. Null when the folder is gone or
    /// isn't an app folder (it starts with a dot).
    /// </summary>
    public static AppCandidate? LocateApp(string folder, bool singleApp)
    {
        if (!Directory.Exists(folder)) return null;
        if (singleApp) return FindLatest(folder, allowFolders: false);

        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (name.StartsWith('.')) return null;
        return FindLatest(folder, allowFolders: true) ?? new AppCandidate(name, folder, null, false, "", 0);
    }

    /// <summary>The candidate's newest snapshot as it is on disk right now.</summary>
    public static SnapshotFingerprint Fingerprint(AppCandidate candidate)
    {
        try
        {
            if (candidate.LatestPath is null) return new SnapshotFingerprint(null, 0, default, candidate.SnapshotCount);

            if (candidate.LatestIsZip)
            {
                var info = new FileInfo(candidate.LatestPath);
                return new SnapshotFingerprint(candidate.LatestPath, info.Exists ? info.Length : -1,
                    info.Exists ? info.LastWriteTimeUtc : default, candidate.SnapshotCount);
            }

            // An unzipped snapshot changes when any file in it does: the newest time and the total size.
            long total = 0;
            var newest = Directory.GetLastWriteTimeUtc(candidate.LatestPath);
            foreach (var file in EnumerateSnapshotFiles(candidate.LatestPath))
            {
                total += file.Length;
                if (file.LastWriteTimeUtc > newest) newest = file.LastWriteTimeUtc;
            }
            return new SnapshotFingerprint(candidate.LatestPath, total, newest, candidate.SnapshotCount);
        }
        catch
        {
            // Unreadable right now; a different value from any real one, so it's looked at again.
            return new SnapshotFingerprint(candidate.LatestPath, -1, default, candidate.SnapshotCount);
        }
    }

    private static IEnumerable<FileInfo> EnumerateSnapshotFiles(string folder)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(folder));
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in dir.EnumerateDirectories())
                if (!SkipFolders.Contains(sub.Name)) pending.Push(sub);
            foreach (var file in dir.EnumerateFiles())
                yield return file;
        }
    }

    private static AppCandidate? FindLatest(string folder, bool allowFolders)
    {
        var snapshots = new List<(string Location, bool IsZip, long Number, string Text)>();

        foreach (var zip in Directory.EnumerateFiles(folder, "*.zip"))
        {
            if (!Path.GetExtension(zip).Equals(".zip", StringComparison.OrdinalIgnoreCase)) continue;

            var match = ZipNumber.Match(Path.GetFileNameWithoutExtension(zip));
            if (match.Success && long.TryParse(match.Groups["num"].Value, out var number))
                snapshots.Add((zip, true, number, match.Groups["num"].Value));
        }

        // Unzipped snapshot folders (App-0003) are only considered when there are no zips.
        if (snapshots.Count == 0 && allowFolders)
        {
            foreach (var dir in Directory.EnumerateDirectories(folder))
            {
                var match = DirNumber.Match(Path.GetFileName(dir));
                if (match.Success && long.TryParse(match.Groups["num"].Value, out var number))
                    snapshots.Add((dir, false, number, match.Groups["num"].Value));
            }
        }

        if (snapshots.Count == 0) return null;

        var latest = snapshots
            .OrderByDescending(s => s.Number)
            .ThenByDescending(s => s.IsZip)
            .First();

        var appName = Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(appName)) appName = folder;

        return new AppCandidate(appName, folder, latest.Location, latest.IsZip, latest.Text, snapshots.Count);
    }
}
