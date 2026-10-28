using System.IO;

namespace AppConsistency.Core;

public static class Auditor
{
    /// <summary>A zip that can't be opened and changed this recently is taken to be still on its way in.</summary>
    private static readonly TimeSpan StillWriting = TimeSpan.FromMinutes(2);

    public static AppAudit Audit(AppCandidate candidate)
    {
        var audit = new AppAudit
        {
            AppName = candidate.AppName,
            FolderPath = candidate.FolderPath,
            SnapshotPath = candidate.LatestPath,
            VersionText = candidate.VersionText,
            SnapshotCount = candidate.SnapshotCount,
            // Taken before reading: if the file changes while it's read, the next look sees a difference.
            Fingerprint = RepoLocator.Fingerprint(candidate)
        };

        if (candidate.LatestPath is null)
        {
            audit.Skipped = true;
            return audit;
        }

        try
        {
            var snapshot = RepoSnapshot.Load(candidate.LatestPath, candidate.LatestIsZip);
            audit.Results.AddRange(StandardsRules.Run(snapshot, out var facts));
            audit.HasTests = facts.HasTests;
            audit.Packages = facts.Packages;
            audit.PackageUses.AddRange(facts.PackageUses);
            audit.Readmes.AddRange(facts.Readmes);
            audit.Licenses.AddRange(facts.Licenses);
            audit.LicenseIsMit = facts.LicenseIsMit;
            audit.AppVersion = facts.AppVersion;
            audit.Unlisted.AddRange(facts.Unlisted);
            audit.PrivateFiles.AddRange(facts.PrivateFiles);
            audit.PrivateMentions.AddRange(facts.PrivateMentions);
            audit.AllFiles.AddRange(facts.AllFiles);
        }
        catch (Exception ex) when (candidate.LatestIsZip && IsStillBeingWritten(candidate.LatestPath, ex))
        {
            // Not a finding: the zip is still arriving. The window checks it again once it is complete.
            audit.Results.Clear();
            audit.Pending = true;
            audit.Results.Add(new CheckResult("Package", "Open snapshot", CheckStatus.Info,
                $"{Path.GetFileName(candidate.LatestPath)} is still being written. It's checked again automatically once it's complete."));
        }
        catch (Exception ex)
        {
            audit.Results.Clear();
            audit.Results.Add(new CheckResult("Package", "Open snapshot", CheckStatus.Fail,
                $"Couldn't read {Path.GetFileName(candidate.LatestPath)}: {ex.Message}"));
        }

        return audit;
    }

    /// <summary>
    /// An unreadable zip that changed in the last couple of minutes, or that another app holds open
    /// for writing, is taken to be incomplete rather than broken.
    /// </summary>
    private static bool IsStillBeingWritten(string path, Exception ex)
    {
        if (ex is not (IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)) return false;

        try
        {
            var info = new FileInfo(path);
            return !info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc < StillWriting;
        }
        catch
        {
            return true;
        }
    }
}
