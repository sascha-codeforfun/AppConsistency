using System.IO;
using System.Text;

namespace AppConsistency.Core;

/// <summary>Everything a batch of queued fixes will do, worked out before anything is written.</summary>
public sealed class FixPlan
{
    public required string Source { get; init; }
    public required string Target { get; init; }

    /// <summary>The release day: every entry in the fixed copy is dated this day at 00:00:00.</summary>
    public required DateTime Day { get; init; }

    /// <summary>Relative paths of zip entries to delete.</summary>
    public List<string> RemoveEntries { get; } = new();

    /// <summary>Folders to delete with everything in them (.git/, .github/).</summary>
    public List<string> RemoveFolders { get; } = new();

    /// <summary>Edited project files: relative path → new bytes.</summary>
    public Dictionary<string, byte[]> EditedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The release-timestamps fix was queued: it counts as a change on its own.</summary>
    public bool Retime { get; set; }

    /// <summary>Human-readable preview, one section per file or fix.</summary>
    public List<(string Heading, List<string> Lines)> Sections { get; } = new();

    /// <summary>Queued fixes that turned out not to apply, with the reason.</summary>
    public List<string> Skipped { get; } = new();

    public bool HasChanges => RemoveEntries.Count > 0 || RemoveFolders.Count > 0 || EditedFiles.Count > 0 || Retime;

    /// <summary>Number of separate changes, for the summary message.</summary>
    public int ChangeCount { get; set; }

    public ZipChanges ToChanges()
    {
        var changes = new ZipChanges();
        foreach (var rel in RemoveEntries) changes.RemoveFiles.Add(rel);
        changes.RemoveFolders.AddRange(RemoveFolders);
        foreach (var (rel, bytes) in EditedFiles) changes.Replace[rel] = bytes;
        return changes;
    }
}

public static class FixPlanner
{
    private sealed class FileEdit
    {
        public required CsprojEditor Editor { get; init; }
        public required Encoding Encoding { get; init; }
        public List<string> Lines { get; } = new();
    }

    /// <param name="unlistedPicks">The files ticked in the removal picker; only these are removed.</param>
    public static FixPlan Plan(string source, string target, IEnumerable<FixTarget> fixes,
                               IReadOnlyCollection<string>? unlistedPicks, DateTime day)
    {
        var plan = new FixPlan { Source = source, Target = target, Day = day };
        var snapshot = RepoSnapshot.Load(source, isZip: true);
        var (main, apps, _) = StandardsRules.SelectProjects(snapshot);
        var ordered = fixes.Distinct().OrderBy(f => f.Kind).ThenBy(f => f.Project, StringComparer.OrdinalIgnoreCase).ToList();
        var edits = new Dictionary<string, FileEdit>(StringComparer.OrdinalIgnoreCase);

        foreach (var fix in ordered)
        {
            switch (fix.Kind)
            {
                case FixKind.RemoveSolutionFiles:
                    PlanSolutionRemoval(plan, snapshot);
                    break;

                case FixKind.RemoveIdeFiles:
                    PlanIdeFileRemoval(plan, snapshot);
                    break;

                case FixKind.RemoveGitFiles:
                    PlanGitFileRemoval(plan, snapshot);
                    break;

                case FixKind.RemoveUnlistedFiles:
                    PlanUnlistedRemoval(plan, snapshot, unlistedPicks);
                    break;

                case FixKind.PublishSettings:
                    if (main is null) plan.Skipped.Add("Publish settings: no app project found in the snapshot.");
                    else PlanPublishSettings(plan, main, EditFor(plan, snapshot, edits, main.RelPath));
                    break;

                case FixKind.AddVersion:
                    if (main is null) plan.Skipped.Add("Version: no app project found in the snapshot.");
                    else PlanVersion(plan, main, EditFor(plan, snapshot, edits, main.RelPath));
                    break;

                case FixKind.NoDebugInfo:
                    var project = apps.FirstOrDefault(a => a.RelPath.Equals(fix.Project, StringComparison.OrdinalIgnoreCase));
                    if (project is null) plan.Skipped.Add($"Debug information: {fix.Project} isn't in the snapshot.");
                    else PlanNoDebugInfo(plan, snapshot, project, EditFor(plan, snapshot, edits, project.RelPath));
                    break;

                case FixKind.ReleaseTimestamps:
                    plan.Retime = true;
                    break;

                case FixKind.NoCommitInVersion:
                    var revisionProject = apps.FirstOrDefault(a => a.RelPath.Equals(fix.Project, StringComparison.OrdinalIgnoreCase));
                    if (revisionProject is null) plan.Skipped.Add($"Commit in the version: {fix.Project} isn't in the snapshot.");
                    else PlanNoCommit(plan, snapshot, revisionProject, EditFor(plan, snapshot, edits, revisionProject.RelPath));
                    break;
            }
        }

        foreach (var (path, edit) in edits.Where(e => e.Value.Lines.Count > 0))
        {
            plan.EditedFiles[path] = CsprojEditor.Encode(edit.Editor.Text, edit.Encoding);
            plan.Sections.Add(($"Changes in {path}", edit.Lines));
        }

        // Every fixed copy gets the release timestamps; it only counts as a change when it was queued.
        plan.Sections.Add(("Release timestamps",
            [$"{(plan.Retime ? "~ " : "")}every entry dated {day:yyyy-MM-dd} 00:00:00, in sorted order, without NTFS or extended timestamps"]));

        plan.ChangeCount = plan.RemoveEntries.Count + plan.RemoveFolders.Count + (plan.Retime ? 1 : 0)
                           + edits.Values.Sum(e => e.Lines.Count(l => !l.StartsWith("  ", StringComparison.Ordinal)));
        return plan;
    }

    private static FileEdit? EditFor(FixPlan plan, RepoSnapshot snapshot, Dictionary<string, FileEdit> edits, string path)
    {
        if (edits.TryGetValue(path, out var existing)) return existing;

        var bytes = snapshot.ReadBytes(path);
        if (bytes is null)
        {
            plan.Skipped.Add($"{path} couldn't be read from the zip.");
            return null;
        }

        var (text, encoding) = CsprojEditor.Decode(bytes);
        var edit = new FileEdit { Editor = new CsprojEditor(text), Encoding = encoding };
        edits[path] = edit;
        return edit;
    }

    private static void PlanSolutionRemoval(FixPlan plan, RepoSnapshot snapshot)
    {
        // Same split as the rule: a .slnx kept in a multi-project repo is never removed.
        var solutions = StandardsRules.SolutionFiles(snapshot).Removable;
        if (solutions.Count == 0)
        {
            plan.Skipped.Add("Remove solution files: the snapshot has none to remove.");
            return;
        }
        plan.RemoveEntries.AddRange(solutions);
        plan.Sections.Add(("Remove solution files", solutions.Select(p => "- " + p).ToList()));
    }

    private static void PlanIdeFileRemoval(FixPlan plan, RepoSnapshot snapshot)
    {
        // Same list as the rule shows; none of these files is needed to build.
        var files = StandardsRules.ObsoleteIdeFiles(snapshot);
        if (files.Count == 0)
        {
            plan.Skipped.Add("Remove obsolete IDE files: the snapshot has none.");
            return;
        }
        plan.RemoveEntries.AddRange(files.Select(f => f.RelPath));
        plan.Sections.Add(("Remove obsolete IDE files", files.Select(f => $"- {f.RelPath}  ({f.Label})").ToList()));
    }

    private static void PlanGitFileRemoval(FixPlan plan, RepoSnapshot snapshot)
    {
        // Same list as the rule shows.
        var git = FileHygiene.GitFiles(snapshot);
        if (git.Count == 0)
        {
            plan.Skipped.Add("Remove git files: the snapshot has none.");
            return;
        }
        plan.RemoveFolders.AddRange(git.Folders.Select(f => f.Folder));
        plan.RemoveEntries.AddRange(git.Files);
        plan.Sections.Add(("Remove git files",
            git.Folders.Select(f => $"- {f.Folder}/  (whole folder, {f.FileCount} file{(f.FileCount == 1 ? "" : "s")})")
                .Concat(git.Files.Select(f => "- " + f))
                .ToList()));
    }

    private static void PlanUnlistedRemoval(FixPlan plan, RepoSnapshot snapshot, IReadOnlyCollection<string>? picks)
    {
        if (picks is null || picks.Count == 0)
        {
            plan.Skipped.Add("Remove unlisted files: nothing was ticked.");
            return;
        }

        // Only files that are in the snapshot and still unlisted: a pick can never remove a source file.
        StandardsRules.Run(snapshot, out var facts);
        var unlisted = facts.Unlisted.Select(u => u.RelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remove = picks.Where(unlisted.Contains).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var gone in picks.Where(p => !unlisted.Contains(p)))
            plan.Skipped.Add($"Remove unlisted files: {gone} isn't an unlisted file in this snapshot, so it stays.");

        if (remove.Count == 0) return;
        plan.RemoveEntries.AddRange(remove);
        plan.Sections.Add(("Remove the unlisted files you ticked", remove.Select(p => "- " + p).ToList()));
    }

    private static void PlanPublishSettings(FixPlan plan, ProjectModel project, FileEdit? edit)
    {
        if (edit is null) return;

        foreach (var (property, value) in FixCatalog.PublishProperties)
        {
            var current = project.Prop(property);

            // A Windows RID that isn't win-x64 may be deliberate; never rewrite it.
            if (property == "RuntimeIdentifier" && current is not null)
            {
                if (!current.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
                    plan.Skipped.Add($"RuntimeIdentifier: left as {current}, since it may be deliberate.");
                continue;
            }

            if (!InProject(plan, project, property)) continue;
            Apply(plan, edit, property, value);
        }
    }

    private static void PlanVersion(FixPlan plan, ProjectModel project, FileEdit? edit)
    {
        if (edit is null) return;
        if (project.Prop("Version") is { } current)
        {
            plan.Skipped.Add($"Version: {project.RelPath} already sets {current}.");
            return;
        }
        if (!InProject(plan, project, "Version")) return;
        Apply(plan, edit, "Version", FixCatalog.FirstVersion);
    }

    private static bool InProject(FixPlan plan, ProjectModel project, string property)
    {
        if (project.PropSource.TryGetValue(property, out var source)
            && !source.Equals(project.RelPath, StringComparison.OrdinalIgnoreCase))
        {
            plan.Skipped.Add($"{property}: set in {source}, which fixes don't change.");
            return false;
        }
        return true;
    }

    private static void Apply(FixPlan plan, FileEdit edit, string property, string value)
    {
        var outcome = edit.Editor.SetProperty(property, value);
        switch (outcome.Kind)
        {
            case EditKind.Added: edit.Lines.Add("+ " + outcome.Line); break;
            case EditKind.Changed: edit.Lines.Add($"~ {property}: {outcome.OldValue} → {value}"); break;
            case EditKind.Blocked: plan.Skipped.Add($"{property}: {outcome.Reason}"); break;
        }
    }

    private static void PlanNoDebugInfo(FixPlan plan, RepoSnapshot snapshot, ProjectModel project, FileEdit? edit)
    {
        if (edit is null) return;

        var current = PropertyEvaluator.Evaluate(snapshot, project, "DebugType");
        if (current is { State: EvalState.Set, Value: { } v } && v.Equals(FixCatalog.DebugType, StringComparison.OrdinalIgnoreCase))
        {
            plan.Skipped.Add($"Debug information: {project.RelPath} already has DebugType {FixCatalog.DebugType}.");
            return;
        }

        var outcome = edit.Editor.AppendBlock(FixCatalog.NoDebugInfoBlock);
        if (outcome.Kind != EditKind.Added)
        {
            plan.Skipped.Add($"Debug information: {outcome.Reason}");
            return;
        }

        // First line counts as the change; the rest of the block is indented beneath it.
        var lines = outcome.Line.Split('\n');
        edit.Lines.Add("+ " + lines[0]);
        edit.Lines.AddRange(lines.Skip(1).Select(l => "  " + l));
    }

    /// <summary>
    /// Sets IncludeSourceRevisionInInformationalVersion to false: changes it where the project sets it,
    /// otherwise adds it next to DebugType (including a DebugType this same batch adds), otherwise in a group of its own.
    /// </summary>
    private static void PlanNoCommit(FixPlan plan, RepoSnapshot snapshot, ProjectModel project, FileEdit? edit)
    {
        if (edit is null) return;
        const string property = FixCatalog.SourceRevisionProperty;

        var current = PropertyEvaluator.Evaluate(snapshot, project, property);
        if (current is { State: EvalState.Set, Value: { } v } && v.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            plan.Skipped.Add($"Commit in the version: {project.RelPath} already has {property} false.");
            return;
        }

        if (current.State != EvalState.NotSet)
        {
            // Set somewhere already: change it in place, and only in the project file itself.
            if (!InProject(plan, project, property)) return;
            Apply(plan, edit, property, "false");
            return;
        }

        var outcome = edit.Editor.AddAfter("DebugType", property, "false", FixCatalog.NoCommitComment);
        if (outcome.Kind == EditKind.Added)
        {
            edit.Lines.Add("+ " + outcome.Line);
            return;
        }

        outcome = edit.Editor.AppendBlock(FixCatalog.NoCommitBlock);
        if (outcome.Kind != EditKind.Added)
        {
            plan.Skipped.Add($"Commit in the version: {outcome.Reason}");
            return;
        }

        var lines = outcome.Line.Split('\n');
        edit.Lines.Add("+ " + lines[0]);
        edit.Lines.AddRange(lines.Skip(1).Select(l => "  " + l));
    }

    /// <summary>
    /// Writes the fixed copy: the snapshot rewritten with the plan applied and every entry dated the
    /// release day at 00:00:00. Goes to a temporary file first, so a half-fixed copy never sits under
    /// the real name, and never overwrites.
    /// </summary>
    public static void Apply(FixPlan plan) =>
        CleanZip.WriteFile(plan.Source, plan.Target, plan.Day, plan.ToChanges());
}
