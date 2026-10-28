using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace AppConsistency.Core;

/// <summary>
/// Copies a snapshot to Downloads without its version number. Fixed copies go through FixPlanner.
/// Every zip written here carries clean release timestamps (CleanZip): one day, 00:00:00.
/// Never overwrites: callers check for an existing file first, and the writers refuse to overwrite.
/// The history folder is only ever read.
/// </summary>
public static class Staging
{
    private static readonly Regex VersionSuffix = new(@"[-_ ]?\d+$", RegexOptions.Compiled);

    /// <summary>EolInspector-0001.zip → EolInspector.zip</summary>
    public static string StagedName(string snapshotPath)
    {
        var name = Path.GetFileNameWithoutExtension(snapshotPath);
        var stripped = VersionSuffix.Replace(name, "");
        return (stripped.Length > 0 ? stripped : name) + ".zip";
    }

    /// <summary>EolInspector-0001.zip → EolInspector-PUBLISH.zip (the suffix is a setting): the copy without your private files.</summary>
    public static string PublishName(string snapshotPath) =>
        Path.GetFileNameWithoutExtension(StagedName(snapshotPath)) + Standard.Current.Release.PublishSuffix + ".zip";

    /// <summary>
    /// The same files and content, every entry dated the release day at 00:00:00. For publishing,
    /// every private file (the names in Settings, at any depth) is left out; otherwise all stay.
    /// </summary>
    public static void Stage(string snapshotPath, string target, DateTime releaseDay, bool forPublishing = false)
    {
        ZipChanges? changes = null;
        if (forPublishing)
        {
            changes = new ZipChanges();
            foreach (var name in Standard.Current.PrivateFiles) changes.RemoveNames.Add(name);
        }
        CleanZip.WriteFile(snapshotPath, target, releaseDay, changes);
    }

    /// <summary>2026-10-01-14-32-05-REPO-SOURCE-BACKUP.zip: the seconds keep every run apart.</summary>
    public static string BackupName(DateTime when) => Standard.FormatName(Standard.Current.Release.BackupName, when);

    public sealed record BackupResult(List<string> Added, List<string> Duplicates, List<string> Failed);

    /// <summary>
    /// One zip holding every app's newest snapshot under its staged name (EolInspector.zip). Inside,
    /// everything is dated the day of the backup at 00:00:00: each repo zip, and every file in it, so
    /// code taken from a backup is easy to tell apart from a working copy. The file name keeps the full
    /// time, so several backups on one day stay apart, and the backup file itself keeps its real time.
    /// Written to a temporary file and moved into place only when complete; never overwrites.
    /// </summary>
    public static BackupResult Backup(
        IReadOnlyList<(string AppName, string Source)> snapshots, string target, DateTime backupDay,
        IProgress<string>? progress = null)
    {
        if (File.Exists(target)) throw new IOException($"{Path.GetFileName(target)} already exists.");

        var added = new List<string>();
        var duplicates = new List<string>();
        var failed = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var temp = target + ".partial";

        try
        {
            if (File.Exists(temp)) File.Delete(temp);

            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                for (int i = 0; i < snapshots.Count; i++)
                {
                    var (appName, source) = snapshots[i];
                    progress?.Report($"Backing up {appName} ({i + 1} of {snapshots.Count})…");

                    var entryName = StagedName(source);
                    if (!names.Add(entryName))
                    {
                        duplicates.Add($"{appName} ({entryName})");
                        continue;
                    }

                    try
                    {
                        // Rewritten in memory first, so a snapshot that can't be read leaves no half entry.
                        var bytes = CleanZip.ToBytes(source, backupDay);
                        CleanZip.AddEntry(zip, entryName, bytes, backupDay, CompressionLevel.NoCompression);
                        added.Add(entryName);
                    }
                    catch (Exception ex)
                    {
                        names.Remove(entryName);
                        failed.Add($"{appName} ({ex.Message})");
                    }
                }
            }

            if (added.Count == 0) throw new IOException("No snapshot could be added, so no backup was written.");
            File.Move(temp, target, overwrite: false);
            return new BackupResult(added, duplicates, failed);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }
}
