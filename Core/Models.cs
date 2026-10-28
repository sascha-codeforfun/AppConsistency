using System.IO;

namespace AppConsistency.Core;

/// <summary>
/// Severity order matters for Pass, Info, Warn and Fail: later values are worse. License is not a
/// verdict and sits outside that order: it never makes an app warn or fail, and never changes the
/// dot beside it. It marks the one finding to look at for every repo, whatever the outcome.
/// </summary>
public enum CheckStatus
{
    Pass,
    Info,
    License,
    Warn,
    Fail
}

/// <summary>Fixes the app can apply to a staged copy. Each one is agreed individually before it's added.</summary>
public enum FixKind
{
    None,
    RemoveSolutionFiles,
    PublishSettings,
    NoDebugInfo,
    RemoveIdeFiles,
    RemoveGitFiles,
    RemoveUnlistedFiles,
    AddVersion,
    ReleaseTimestamps
}

/// <summary>Whether the app uses NuGet packages, and whether they're all on the approved list.</summary>
public enum PackageState
{
    None,
    Allowed,
    Foreign
}

/// <summary>How one package the app references stands against the approved list.</summary>
public enum PackageUseState
{
    Ok,
    Old,
    NotListed,
    TestOnly
}

public sealed record PackageUse(string Name, string? Version, PackageUseState State, string Note);

/// <summary>A file in the snapshot that isn't on the whitelist, with its size, for the removal picker.</summary>
/// <param name="ReadmeUses">The README links to it, embeds it or names it: documentation, so it doesn't warn.</param>
/// <param name="DuplicateOf">Another file with the same name is the one the README uses; this copy warns.</param>
public sealed record UnlistedFile(string RelPath, long Length, bool ReadmeUses = false, string? DuplicateOf = null);

/// <summary>Facts about a snapshot that aren't findings, shown as badges next to the app name.</summary>
public sealed class ScanFacts
{
    public bool HasTests { get; set; }
    public PackageState Packages { get; set; }
    public List<PackageUse> PackageUses { get; } = new();
    /// <summary>Every README.* in the snapshot, as snapshot-relative paths.</summary>
    public List<string> Readmes { get; } = new();
    /// <summary>Every LICENSE / LICENCE / COPYING file in the snapshot, snapshot-relative.</summary>
    public List<string> Licenses { get; } = new();
    /// <summary>True when the checked license file is the standard MIT text with a valid copyright line.</summary>
    public bool LicenseIsMit { get; set; }
    /// <summary>The app project's version, from the .csproj or a Directory.Build.props above it.</summary>
    public string? AppVersion { get; set; }
    /// <summary>Files that aren't on the whitelist and aren't used by the build.</summary>
    public List<UnlistedFile> Unlisted { get; } = new();
    /// <summary>Your private files (the names in Settings) at any depth, snapshot-relative.</summary>
    public List<string> PrivateFiles { get; } = new();
    /// <summary>README lines that name a private file, as "README.md line 3: Docs/NOTES.md".</summary>
    public List<string> PrivateMentions { get; } = new();
    /// <summary>Every file in the snapshot, snapshot-relative: kept in memory for the Settings window's usage counts.</summary>
    public List<string> AllFiles { get; } = new();
}

/// <summary>A fix plus the project it applies to (null means the app project or the zip as a whole).</summary>
public sealed record FixTarget(FixKind Kind, string? Project);

public sealed record CheckResult(string Category, string Rule, CheckStatus Status, string Detail)
{
    public FixKind Fix { get; init; }
    public string? FixProject { get; init; }

    /// <summary>The word on a License pill: MIT or license. Other statuses use their own word.</summary>
    public string? PillWord { get; init; }

    public bool CanFix => Fix != FixKind.None;
    public FixTarget Target => new(Fix, FixProject);
    public string StatusLabel => Status == CheckStatus.License ? StatusWord.ToUpperInvariant() : Status.ToString().ToUpperInvariant();

    /// <summary>The word on the status pill.</summary>
    public string StatusWord => Status switch
    {
        CheckStatus.Pass => "Pass",
        CheckStatus.Info => "Info",
        CheckStatus.License => PillWord ?? "license",
        CheckStatus.Warn => "Warn",
        _ => "Fail"
    };
}

/// <summary>An app folder and the snapshot picked for checking.</summary>
public sealed record AppCandidate(
    string AppName,
    string FolderPath,
    string? LatestPath,
    bool LatestIsZip,
    string VersionText,
    int SnapshotCount);

public sealed class AppAudit
{
    public required string AppName { get; init; }
    public required string FolderPath { get; init; }
    public string? SnapshotPath { get; init; }
    public string VersionText { get; init; } = "";
    public int SnapshotCount { get; init; }
    public bool Skipped { get; set; }
    public bool HasTests { get; set; }
    public PackageState Packages { get; set; }
    public List<PackageUse> PackageUses { get; } = new();
    /// <summary>Every README.* found in the snapshot at scan time, snapshot-relative.</summary>
    public List<string> Readmes { get; } = new();
    /// <summary>Every license file found in the snapshot at scan time, snapshot-relative.</summary>
    public List<string> Licenses { get; } = new();
    public bool LicenseIsMit { get; set; }
    /// <summary>The app project's version, or null when the project doesn't set one.</summary>
    public string? AppVersion { get; set; }
    public List<UnlistedFile> Unlisted { get; } = new();
    /// <summary>Private files found at scan time: kept everywhere except in a copy staged for publishing.</summary>
    public List<string> PrivateFiles { get; } = new();
    /// <summary>README lines that name a private file, found at scan time.</summary>
    public List<string> PrivateMentions { get; } = new();
    /// <summary>Every file in the snapshot at scan time, snapshot-relative. Memory only, never written anywhere.</summary>
    public List<string> AllFiles { get; } = new();

    /// <summary>The snapshot couldn't be read because it is still being written; it is checked again.</summary>
    public bool Pending { get; set; }

    /// <summary>The newest snapshot's path, size and time when it was checked, to notice a newer one.</summary>
    public SnapshotFingerprint? Fingerprint { get; set; }

    public bool Elevated => Results.Any(r => r.Rule == StandardsRules.ElevationRule && r.Status != CheckStatus.Pass);
    public List<CheckResult> Results { get; } = new();

    public int FailCount => Results.Count(r => r.Status == CheckStatus.Fail);
    public int WarnCount => Results.Count(r => r.Status == CheckStatus.Warn);
    public int PassCount => Results.Count(r => r.Status == CheckStatus.Pass);
    public int InfoCount => Results.Count(r => r.Status == CheckStatus.Info);
    public int LicenseCount => Results.Count(r => r.Status == CheckStatus.License);

    /// <summary>Not on a standard MIT license: no license file, another license, or MIT that isn't standard.</summary>
    public bool NeedsLicenseReview => !Skipped && LicenseCount > 0 && !LicenseIsMit;

    /// <summary>Everything the findings say, to tell whether a rescan changed anything.</summary>
    public string Signature =>
        string.Join("\n", Results.Select(r => $"{r.Category}|{r.Rule}|{r.Status}|{r.PillWord}|{r.Detail}"));

    public string SnapshotName => SnapshotPath is null ? "" : Path.GetFileName(SnapshotPath);

    public bool IsZipSnapshot => SnapshotPath?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true;

    public CheckStatus Overall =>
        Skipped ? CheckStatus.Info
        : FailCount > 0 ? CheckStatus.Fail
        : WarnCount > 0 ? CheckStatus.Warn
        : CheckStatus.Pass;

    public string Summary
    {
        get
        {
            if (Skipped) return "No numbered snapshots";
            if (Pending) return $"{VersionText}, still being written";
            if (FailCount + WarnCount == 0) return $"{VersionText}, all checks pass";

            var parts = new List<string>();
            if (FailCount > 0) parts.Add($"{FailCount} failing");
            if (WarnCount > 0) parts.Add($"{WarnCount} warning{(WarnCount == 1 ? "" : "s")}");
            return $"{VersionText}, {string.Join(", ", parts)}";
        }
    }
}
