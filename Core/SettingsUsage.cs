using System.Text.RegularExpressions;

namespace AppConsistency.Core;

/// <summary>How much one setting entry matters in the last scan: how many apps it touches, and which.</summary>
/// <param name="Apps">Apps the entry affects.</param>
/// <param name="Total">Apps in the scan that have a snapshot.</param>
/// <param name="Lines">One line per affected app, e.g. "CrawlerSuite (in CrawlerSuite.Core)".</param>
/// <param name="Summary">"used by 1 of 41 apps", "matches 47 files in 41 apps" …</param>
public sealed record Usage(int Apps, int Total, IReadOnlyList<string> Lines, string Summary)
{
    public double Share => Total == 0 ? 0 : (double)Apps / Total;
}

/// <summary>
/// The numbers beside each entry in the Settings window, worked out from the apps already in the list.
/// Nothing is written anywhere and nothing is read from disk: it's the last scan, in memory.
/// </summary>
public static class SettingsUsage
{
    private static readonly Regex InProject = new(@", in (?<project>.+)$", RegexOptions.Compiled);

    /// <summary>Apps that use a package in a project that ships, not just in tests.</summary>
    public static Usage Package(string name, IReadOnlyList<AppAudit> apps)
    {
        var scanned = Scanned(apps);
        var lines = new List<string>();
        foreach (var app in scanned)
        {
            var uses = app.PackageUses
                .Where(u => u.State != PackageUseState.TestOnly && u.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (uses.Count == 0) continue;

            var where = uses
                .Select(u => InProject.Match(u.Note) is { Success: true } m ? m.Groups["project"].Value : null)
                .Where(p => p is not null)
                .Distinct()
                .ToList();
            var versions = string.Join(", ", uses.Select(u => u.Version).Where(v => v is not null).Distinct());
            lines.Add(app.AppName + (versions.Length > 0 ? $" {versions}" : "") + (where.Count > 0 ? $" (in {string.Join(", ", where)})" : ""));
        }
        return new Usage(lines.Count, scanned.Count, lines, $"used by {lines.Count} of {Apps(scanned.Count)}");
    }

    /// <summary>Apps with a file of exactly this name, at any depth.</summary>
    public static Usage FileName(string name, IReadOnlyList<AppAudit> apps)
    {
        var scanned = Scanned(apps);
        var lines = new List<string>();
        foreach (var app in scanned)
        {
            var hits = app.AllFiles.Where(f => RepoSnapshot.NameOf(f).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count > 0) lines.Add($"{app.AppName}: {string.Join(", ", hits)}");
        }
        return new Usage(lines.Count, scanned.Count, lines, $"found in {lines.Count} of {Apps(scanned.Count)}");
    }

    /// <summary>Apps with files matching a whitelist pattern, and how many.</summary>
    public static Usage Pattern(string pattern, IReadOnlyList<AppAudit> apps)
    {
        var scanned = Scanned(apps);
        var glob = Standard.Glob(pattern);
        var lines = new List<string>();
        int files = 0;
        foreach (var app in scanned)
        {
            int count = app.AllFiles.Count(f => glob.IsMatch(f));
            if (count == 0) continue;
            files += count;
            lines.Add($"{app.AppName}: {count} file{(count == 1 ? "" : "s")}");
        }
        return new Usage(lines.Count, scanned.Count, lines,
            $"matches {files:N0} file{(files == 1 ? "" : "s")} in {lines.Count} of {Apps(scanned.Count)}");
    }

    private static List<AppAudit> Scanned(IReadOnlyList<AppAudit> apps) =>
        apps.Where(a => !a.Skipped && !a.Pending && a.SnapshotPath is not null).ToList();

    private static string Apps(int count) => $"{count} app{(count == 1 ? "" : "s")}";
}
