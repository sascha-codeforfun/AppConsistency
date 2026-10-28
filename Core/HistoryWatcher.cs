using System.Collections.Concurrent;
using System.IO;

namespace AppConsistency.Core;

/// <summary>
/// Notices changes in the history folder and says which app folders they touched. It opens no
/// files: a single change notification on the folder, which Windows opens with full sharing, so
/// other apps can still create, write, rename and delete everything in it. The window decides what
/// to do with a touched folder (wait until it's quiet, then rescan just that app).
/// Events arrive on a thread-pool thread; everything here is safe to call from any thread.
/// </summary>
public sealed class HistoryWatcher : IDisposable
{
    private readonly string _root;
    private readonly bool _singleApp;
    private readonly FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, DateTime> _touched = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _missedEvents;

    public HistoryWatcher(string root, bool singleApp)
    {
        _root = Path.GetFullPath(root).TrimEnd('\\', '/');
        _singleApp = singleApp;

        try
        {
            _watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };
            _watcher.Created += (_, e) => Touch(e.FullPath);
            _watcher.Changed += (_, e) => Touch(e.FullPath);
            _watcher.Deleted += (_, e) => Touch(e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                Touch(e.OldFullPath);
                Touch(e.FullPath);
            };
            // Too many changes at once, or the share dropped the connection: look at everything.
            _watcher.Error += (_, _) => _missedEvents = true;
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // Some shares can't be watched. The periodic check still notices new snapshots.
            _watcher?.Dispose();
            _watcher = null;
            _missedEvents = true;
        }
    }

    /// <summary>False when the folder couldn't be watched; then only the periodic check runs.</summary>
    public bool IsWatching => _watcher is not null;

    /// <summary>True once, after the watcher lost events; the caller then checks every app.</summary>
    public bool TakeMissedEvents()
    {
        bool missed = _missedEvents;
        _missedEvents = false;
        return missed;
    }

    /// <summary>Marks an app folder as changed at the given time (the periodic check uses this too).</summary>
    public void MarkFolder(string appFolder, DateTime utc) =>
        _touched.AddOrUpdate(appFolder, utc, (_, old) => utc > old ? utc : old);

    /// <summary>App folders changed since they were last cleared, with the time of the latest change.</summary>
    public List<(string Folder, DateTime LastChange)> Touched() =>
        _touched.Select(p => (p.Key, p.Value)).ToList();

    /// <summary>Forgets a folder, unless it changed again after the time the caller looked at.</summary>
    public void Clear(string appFolder, DateTime seen) =>
        _touched.TryRemove(new KeyValuePair<string, DateTime>(appFolder, seen));

    public void Dispose() => _watcher?.Dispose();

    private void Touch(string fullPath)
    {
        var folder = AppFolderOf(fullPath);
        if (folder is not null) MarkFolder(folder, DateTime.UtcNow);
    }

    /// <summary>The app folder a changed path belongs to: the first folder under the history folder.</summary>
    private string? AppFolderOf(string fullPath)
    {
        if (_singleApp) return _root;

        var rel = Path.GetRelativePath(_root, fullPath);
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;

        var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (first.Length == 0 || first.StartsWith('.')) return null;
        return Path.Combine(_root, first);
    }
}
