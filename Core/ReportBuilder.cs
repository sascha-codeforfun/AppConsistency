using System.Text;

namespace AppConsistency.Core;

/// <summary>Plain-text report of every warning and failure, plus every license to review, for pasting elsewhere.</summary>
public static class ReportBuilder
{
    /// <summary>One app's findings, ready to paste into the conversation that built it.</summary>
    public static string BuildApp(AppAudit audit, IReadOnlyList<CheckResult> shown, string? filterNote)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"AppConsistency findings for {audit.AppName}" + (audit.AppVersion is { } v ? $" {v}" : ""));

        if (audit.Skipped)
        {
            sb.AppendLine("No numbered snapshots in " + audit.FolderPath);
            return sb.ToString();
        }

        sb.AppendLine($"Snapshot {audit.SnapshotName} (newest of {audit.SnapshotCount})");
        sb.AppendLine(audit.SnapshotPath ?? audit.FolderPath);
        sb.AppendLine($"Checked {DateTime.Now:yyyy-MM-dd HH:mm}, standard: {Standard.Current.Source}");
        sb.AppendLine($"{audit.PassCount} good, {audit.InfoCount} info, {audit.LicenseCount} license, " +
                      $"{audit.WarnCount} to review, {audit.FailCount} need attention");
        if (filterNote is not null) sb.AppendLine(filterNote);

        string? category = null;
        foreach (var result in shown)
        {
            if (result.Category != category)
            {
                category = result.Category;
                sb.AppendLine();
                sb.AppendLine(category);
            }
            sb.AppendLine($"  {result.StatusLabel,-4}  {result.Rule}: {Indent(result.Detail, 8)}");
        }

        return sb.ToString();
    }

    public static string Build(string root, IEnumerable<AppAudit> apps)
    {
        var list = apps.ToList();
        var sb = new StringBuilder();

        sb.AppendLine($"AppConsistency report for {root}");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}, standard: {Standard.Current.Source}");
        sb.AppendLine(
            $"{list.Count(a => a.Overall == CheckStatus.Pass)} passed, " +
            $"{list.Count(a => a.Overall == CheckStatus.Warn)} warned, " +
            $"{list.Count(a => a.Overall == CheckStatus.Fail)} failed, " +
            $"{list.Count(a => a.Skipped)} without snapshots, " +
            $"{list.Count(a => a.NeedsLicenseReview)} with a license to review");
        sb.AppendLine();

        foreach (var app in list)
        {
            var label = app.Skipped ? "SKIP" : app.Overall.ToString().ToUpperInvariant();
            var source = app.Skipped ? "no numbered snapshots" : app.SnapshotName;
            var version = app.AppVersion is { } v ? $" {v}" : "";
            var marks = (app.Elevated ? ", runs elevated" : "")
                        + (app.HasTests ? ", has tests" : "")
                        + app.Packages switch
                        {
                            PackageState.Allowed => ", uses allowed packages",
                            PackageState.Foreign => ", uses packages off the approved list",
                            _ => ""
                        };
            sb.AppendLine($"[{label}] {app.AppName}{version} ({source}){marks}");

            foreach (var r in app.Results)
            {
                if (r.Status is CheckStatus.Fail or CheckStatus.Warn)
                    sb.AppendLine($"    {r.StatusLabel,-4}  {r.Category} / {r.Rule}: {Indent(r.Detail, 10)}");
                else if (r.Status == CheckStatus.License && app.NeedsLicenseReview)
                    sb.AppendLine($"    LIC   {r.Category} / {r.Rule}: review license. {Indent(r.Detail, 10)}");
            }
        }

        return sb.ToString();
    }

    /// <summary>A detail that lists files on extra lines keeps them under its own row.</summary>
    private static string Indent(string detail, int spaces) =>
        detail.Replace("\n", "\n" + new string(' ', spaces));
}
