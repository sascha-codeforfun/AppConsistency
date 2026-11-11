using System.IO;

namespace AppConsistency.Core;

/// <summary>
/// Severity order matters for Pass, Info, Warn and Fail: later values are worse. License and
/// Artwork are not verdicts and sit outside that order: they never make an app warn or fail, and
/// never change the dot beside it. License marks the one finding to look at for every repo;
/// Artwork marks image files that carry metadata or could be smaller, a matter for your eye.
/// </summary>
public enum CheckStatus
{
    Pass,
    Info,
    License,
    Artwork,
    Warn,
    Fail
}

/// <summary>
/// The system an app is for. A Linux app is filed in a history folder whose name ends in LINUX
/// (capitals, exactly) and is written with Avalonia; everything else is a Windows app, written with WPF.
/// </summary>
public enum AppPlatform
{
    Windows,
    Linux
}

public static class AppPlatforms
{
    /// <summary>The end of a Linux app's folder name in the history folder. Capitals only: PenguinLinux is a Windows app.</summary>
    public const string LinuxSuffix = "LINUX";

    /// <summary>The system the folder name files an app under.</summary>
    public static AppPlatform Filed(string? appName) =>
        appName is not null && appName.Length > LinuxSuffix.Length && appName.EndsWith(LinuxSuffix, StringComparison.Ordinal)
            ? AppPlatform.Linux
            : AppPlatform.Windows;

    /// <summary>The system a snapshot is filed under: its folder's name, as the scan names the app.</summary>
    public static AppPlatform FiledAt(string? snapshotPath)
    {
        if (string.IsNullOrEmpty(snapshotPath)) return AppPlatform.Windows;
        var folder = System.IO.Path.GetDirectoryName(snapshotPath.TrimEnd('\\', '/'));
        return Filed(string.IsNullOrEmpty(folder) ? null : System.IO.Path.GetFileName(folder.TrimEnd('\\', '/')));
    }

    public static string Word(AppPlatform platform) => platform == AppPlatform.Linux ? "Linux" : "Windows";
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
    ReleaseTimestamps,
    NoCommitInVersion,
    AddMitLicense,
    DeclareInReadme,
    UndeclareInReadme,
    OptimiseImages
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

/// <summary>
/// One image file (PNG, ICO or JPEG, recognised by content) and what its level asks for: the
/// metadata that would go and the estimated saving. Found at scan time, for the row and the picker.
/// </summary>
/// <param name="LevelFrom">"default" or the Settings entry that set the level, as its path pattern.</param>
/// <param name="Removable">Names of the metadata nothing displays, which Strip and above remove.</param>
/// <param name="Saving">Bytes the file's level would save, estimated (Strip exactly, recompression quickly).</param>
/// <param name="NeedsWork">Its level isn't met: metadata to remove, or a saving worth making.</param>
public sealed record ImageFinding(
    string RelPath,
    long Length,
    string Format,
    string Detail,
    ImageKit.ImageLevel Level,
    string LevelFrom,
    IReadOnlyList<string> Removable,
    IReadOnlyList<string> Kept,
    long Saving,
    bool NeedsWork,
    string? Problem);

/// <summary>A new version of an image file, made in the image picker, with one line saying what changed.</summary>
/// <param name="SourceHash">SHA-256 of the file it was made from: the planner skips the file when the snapshot's differs.</param>
public sealed record ImageChange(byte[] Bytes, string Summary, string SourceHash)
{
    public static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
}

/// <summary>Facts about a snapshot that aren't findings, shown as badges next to the app name.</summary>
public sealed class ScanFacts
{
    /// <summary>The system the app was judged by: the folder's, unless the project is plainly for the other one.</summary>
    public AppPlatform Platform { get; set; }
    public bool HasTests { get; set; }
    public PackageState Packages { get; set; }
    public List<PackageUse> PackageUses { get; } = new();
    /// <summary>Findings each exemption covered, by the exemption's key.</summary>
    public Dictionary<string, int> ExemptionUses { get; set; } = new();
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
    /// <summary>Every image file with its level and what it would take to meet it.</summary>
    public List<ImageFinding> Images { get; } = new();
    /// <summary>The zip file's size, or null for a folder snapshot.</summary>
    public long? PackedSize { get; set; }
    /// <summary>All files added up, as they are on disk once unpacked.</summary>
    public long UnpackedSize { get; set; }
    public int FileCount { get; set; }
}

/// <summary>A fix plus the project it applies to (null means the app project or the zip as a whole).</summary>
public sealed record FixTarget(FixKind Kind, string? Project);

public sealed record CheckResult(string Category, string Rule, CheckStatus Status, string Detail)
{
    public FixKind Fix { get; init; }
    public string? FixProject { get; init; }

    /// <summary>The word on a License pill: MIT or license. Other statuses use their own word.</summary>
    public string? PillWord { get; init; }

    /// <summary>Set when an exemption covers the finding: the reason. The row is then Info, with the word Exempt.</summary>
    public string? ExemptReason { get; init; }

    /// <summary>
    /// The rule has no counterpart on this app's system. The row is Info with the word N/A and says why, so
    /// it is plain the check was considered and not forgotten; it counts as neither passed nor failed.
    /// </summary>
    public bool NotApplicable { get; init; }

    public bool CanFix => Fix != FixKind.None;
    public FixTarget Target => new(Fix, FixProject);
    public string StatusLabel => Status == CheckStatus.License || ExemptReason is not null || NotApplicable
        ? StatusWord.ToUpperInvariant()
        : Status.ToString().ToUpperInvariant();

    /// <summary>The word on the status pill.</summary>
    public string StatusWord => ExemptReason is not null ? "Exempt" : NotApplicable ? "N/A" : Status switch
    {
        CheckStatus.Pass => "Pass",
        CheckStatus.Info => "Info",
        CheckStatus.License => PillWord ?? "license",
        CheckStatus.Artwork => "Artwork",
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

    /// <summary>The system the app was judged by. Until it has been checked: the one its folder name files it under.</summary>
    public AppPlatform Platform { get; set; }

    /// <summary>True for a Linux app: the list shows a tag, so the system needn't be read off the name.</summary>
    public bool IsLinux => Platform == AppPlatform.Linux;

    /// <summary>"Linux app" or "Windows app", for the details and the reports.</summary>
    public string PlatformLabel => AppPlatforms.Word(Platform) + " app";

    public bool HasTests { get; set; }
    public PackageState Packages { get; set; }
    public List<PackageUse> PackageUses { get; } = new();
    /// <summary>Findings each exemption covered in this app, by the exemption's key. Memory only.</summary>
    public Dictionary<string, int> ExemptionUses { get; } = new(StringComparer.Ordinal);
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
    /// <summary>Every image file at scan time, with its level and what meeting it would take.</summary>
    public List<ImageFinding> Images { get; } = new();
    /// <summary>The zip file's size, or null for a folder snapshot.</summary>
    public long? PackedSize { get; set; }
    /// <summary>All files added up, unpacked.</summary>
    public long UnpackedSize { get; set; }
    public int FileCount { get; set; }

    /// <summary>The snapshot couldn't be read because it is still being written; it is checked again.</summary>
    public bool Pending { get; set; }

    /// <summary>Listed while a scan runs but not checked yet. Replaced by the real result when its turn is done.</summary>
    public bool Waiting { get; init; }

    /// <summary>An app found by the scan, shown greyed in the list until it has been checked.</summary>
    public static AppAudit Placeholder(AppCandidate candidate) => new()
    {
        AppName = candidate.AppName,
        FolderPath = candidate.FolderPath,
        SnapshotPath = candidate.LatestPath,
        VersionText = candidate.VersionText,
        SnapshotCount = candidate.SnapshotCount,
        Platform = AppPlatforms.Filed(candidate.AppName),
        Waiting = true
    };

    /// <summary>The newest snapshot's path, size and time when it was checked, to notice a newer one.</summary>
    public SnapshotFingerprint? Fingerprint { get; set; }

    public bool Elevated => Results.Any(r => r.Rule == StandardsRules.ElevationRule && r.Status != CheckStatus.Pass);
    public List<CheckResult> Results { get; } = new();

    public int FailCount => Results.Count(r => r.Status == CheckStatus.Fail);
    public int WarnCount => Results.Count(r => r.Status == CheckStatus.Warn);
    public int PassCount => Results.Count(r => r.Status == CheckStatus.Pass);
    public int InfoCount => Results.Count(r => r.Status == CheckStatus.Info);
    public int LicenseCount => Results.Count(r => r.Status == CheckStatus.License);
    public int ArtworkCount => Results.Count(r => r.Status == CheckStatus.Artwork);
    public int ExemptCount => Results.Count(r => r.ExemptReason is not null);

    /// <summary>Not on a standard MIT license: no license file, another license, or MIT that isn't standard.</summary>
    public bool NeedsLicenseReview => !Skipped && LicenseCount > 0 && !LicenseIsMit;

    /// <summary>Image files that carry metadata nothing displays, or could be smaller, at their level.</summary>
    public bool NeedsArtworkReview => !Skipped && ArtworkCount > 0;

    /// <summary>Everything the findings say, to tell whether a rescan changed anything.</summary>
    public string Signature =>
        string.Join("\n", Results.Select(r => $"{r.Category}|{r.Rule}|{r.Status}|{r.PillWord}|{r.Detail}"));

    public string SnapshotName => SnapshotPath is null ? "" : Path.GetFileName(SnapshotPath);

    public bool IsZipSnapshot => SnapshotPath?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true;

    public CheckStatus Overall =>
        Waiting || Skipped ? CheckStatus.Info
        : FailCount > 0 ? CheckStatus.Fail
        : WarnCount > 0 ? CheckStatus.Warn
        : CheckStatus.Pass;

    public string Summary
    {
        get
        {
            if (Waiting) return "Waiting to be checked";
            if (Skipped) return "No numbered snapshots";
            if (Pending) return $"{VersionText}, still being written";
            var exempt = ExemptCount > 0 ? $", {ExemptCount} exempt" : "";
            if (FailCount + WarnCount == 0) return $"{VersionText}, all checks pass{exempt}";

            var parts = new List<string>();
            if (FailCount > 0) parts.Add($"{FailCount} failing");
            if (WarnCount > 0) parts.Add($"{WarnCount} warning{(WarnCount == 1 ? "" : "s")}");
            return $"{VersionText}, {string.Join(", ", parts)}{exempt}";
        }
    }
}
