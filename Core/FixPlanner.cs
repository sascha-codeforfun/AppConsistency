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

    /// <summary>New files: zip entry name → bytes.</summary>
    public Dictionary<string, byte[]> AddedFiles { get; } = new(StringComparer.Ordinal);

    /// <summary>The release-timestamps fix was queued: it counts as a change on its own.</summary>
    public bool Retime { get; set; }

    /// <summary>Human-readable preview, one section per file or fix.</summary>
    public List<(string Heading, List<string> Lines)> Sections { get; } = new();

    /// <summary>Queued fixes that turned out not to apply, with the reason.</summary>
    public List<string> Skipped { get; } = new();

    public bool HasChanges => RemoveEntries.Count > 0 || RemoveFolders.Count > 0 || EditedFiles.Count > 0 || AddedFiles.Count > 0 || Retime;

    /// <summary>Number of separate changes, for the summary message.</summary>
    public int ChangeCount { get; set; }

    public ZipChanges ToChanges()
    {
        var changes = new ZipChanges();
        foreach (var rel in RemoveEntries) changes.RemoveFiles.Add(rel);
        changes.RemoveFolders.AddRange(RemoveFolders);
        foreach (var (rel, bytes) in EditedFiles) changes.Replace[rel] = bytes;
        foreach (var (name, bytes) in AddedFiles) changes.Add[name] = bytes;
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
    /// <param name="imageChanges">The image files ticked in the image picker, already processed there.</param>
    public static FixPlan Plan(string source, string target, IEnumerable<FixTarget> fixes,
                               IReadOnlyCollection<string>? unlistedPicks, DateTime day,
                               IReadOnlyDictionary<string, ImageChange>? imageChanges = null)
    {
        var plan = new FixPlan { Source = source, Target = target, Day = day };
        var snapshot = RepoSnapshot.Load(source, isZip: true);
        // The fixes follow the system the scan judged the app by: the snapshot's folder says which it is filed under.
        var filed = AppPlatforms.FiledAt(source);
        var platform = StandardsRules.PlatformOf(snapshot, filed);
        snapshot.Platform = platform;
        var (main, apps, _) = StandardsRules.SelectProjects(snapshot);
        var ordered = fixes.Distinct().OrderBy(f => f.Kind).ThenBy(f => f.Project, StringComparer.OrdinalIgnoreCase).ToList();
        var edits = new Dictionary<string, FileEdit>(StringComparer.OrdinalIgnoreCase);
        var declarations = new List<string>();
        int images = 0;
        var undeclarations = new List<string>();

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
                    PlanUnlistedRemoval(plan, snapshot, unlistedPicks, filed);
                    break;

                case FixKind.PublishSettings:
                    if (main is null) plan.Skipped.Add("Publish settings: no app project found in the snapshot.");
                    else PlanPublishSettings(plan, main, EditFor(plan, snapshot, edits, main.RelPath), platform);
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

                case FixKind.OptimiseImages:
                    images = PlanImages(plan, snapshot, imageChanges);
                    break;

                case FixKind.AddMitLicense:
                    PlanMitLicense(plan, snapshot, day);
                    break;

                case FixKind.DeclareInReadme:
                    if (fix.Project is not null) declarations.Add(fix.Project);
                    break;

                case FixKind.UndeclareInReadme:
                    if (fix.Project is not null) undeclarations.Add(fix.Project);
                    break;

                case FixKind.NoCommitInVersion:
                    var revisionProject = apps.FirstOrDefault(a => a.RelPath.Equals(fix.Project, StringComparison.OrdinalIgnoreCase));
                    if (revisionProject is null) plan.Skipped.Add($"Commit in the version: {fix.Project} isn't in the snapshot.");
                    else PlanNoCommit(plan, snapshot, revisionProject, EditFor(plan, snapshot, edits, revisionProject.RelPath));
                    break;
            }
        }

        int declared = declarations.Count + undeclarations.Count == 0 ? 0 : PlanDeclarations(plan, snapshot, declarations, undeclarations, filed);

        foreach (var (path, edit) in edits.Where(e => e.Value.Lines.Count > 0))
        {
            plan.EditedFiles[path] = CsprojEditor.Encode(edit.Editor.Text, edit.Encoding);
            plan.Sections.Add(($"Changes in {path}", edit.Lines));
        }

        // Every fixed copy gets the release timestamps; it only counts as a change when it was queued.
        plan.Sections.Add(("Release timestamps",
            [$"{(plan.Retime ? "~ " : "")}every entry dated {day:yyyy-MM-dd} 00:00:00, in sorted order, without NTFS or extended timestamps"]));

        plan.ChangeCount = declared + images + plan.RemoveEntries.Count + plan.RemoveFolders.Count + plan.AddedFiles.Count + (plan.Retime ? 1 : 0)
                           + edits.Values.Sum(e => e.Lines.Count(l => !l.StartsWith("  ", StringComparison.Ordinal)));
        return plan;
    }

    /// <summary>The image files ticked in the picker, replaced by the versions made there.</summary>
    private static int PlanImages(FixPlan plan, RepoSnapshot snapshot, IReadOnlyDictionary<string, ImageChange>? changes)
    {
        if (changes is null || changes.Count == 0)
        {
            plan.Skipped.Add("Image files: none ticked.");
            return 0;
        }
        var lines = new List<string>();
        long saved = 0;
        foreach (var (rel, change) in changes.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
        {
            var file = snapshot.Find(rel);
            if (file is null)
            {
                plan.Skipped.Add($"Image files: {rel} isn't in the snapshot.");
                continue;
            }
            if (plan.RemoveEntries.Contains(file.RelPath, StringComparer.OrdinalIgnoreCase))
            {
                plan.Skipped.Add($"Image files: {file.RelPath} is removed as an unlisted file, so it isn't changed.");
                continue;
            }
            // The new version was made from the file as the picker read it; a newer snapshot may differ.
            var current = snapshot.ReadBytes(file.RelPath);
            if (current is null || ImageChange.Hash(current) != change.SourceHash)
            {
                plan.Skipped.Add($"Image files: {file.RelPath} changed since the picker read it; open the fix again.");
                continue;
            }
            plan.EditedFiles[file.RelPath] = change.Bytes;
            saved += file.Length - change.Bytes.Length;
            lines.Add($"~ {file.RelPath}: {change.Summary}");
        }
        if (lines.Count == 0) return 0;
        lines.Add($"  {lines.Count} file{(lines.Count == 1 ? "" : "s")}, {saved:N0} bytes smaller in all");
        plan.Sections.Add(("Image files", lines));
        return lines.Count - 1;
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

    private static void PlanUnlistedRemoval(FixPlan plan, RepoSnapshot snapshot, IReadOnlyCollection<string>? picks, AppPlatform filed)
    {
        if (picks is null || picks.Count == 0)
        {
            plan.Skipped.Add("Remove unlisted files: nothing was ticked.");
            return;
        }

        // Only files that are in the snapshot and still unlisted: a pick can never remove a source file.
        StandardsRules.Run(snapshot, out var facts, "", filed);
        var unlisted = facts.Unlisted.Select(u => u.RelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remove = picks.Where(unlisted.Contains).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var gone in picks.Where(p => !unlisted.Contains(p)))
            plan.Skipped.Add($"Remove unlisted files: {gone} isn't an unlisted file in this snapshot, so it stays.");

        if (remove.Count == 0) return;
        plan.RemoveEntries.AddRange(remove);
        plan.Sections.Add(("Remove the unlisted files you ticked", remove.Select(p => "- " + p).ToList()));
    }

    private static void PlanPublishSettings(FixPlan plan, ProjectModel project, FileEdit? edit, AppPlatform platform)
    {
        if (edit is null) return;

        // A Linux app gets the Linux runtime: the same five properties, with linux-x64 where a Windows app has win-x64.
        foreach (var (property, value) in FixCatalog.PublishPropertiesFor(platform))
        {
            var current = project.Prop(property);

            // A runtime that isn't the standard's (win-arm64, linux-arm64) may be deliberate; never rewrite it.
            if (property == "RuntimeIdentifier" && current is not null)
            {
                if (!current.StartsWith(Standard.RuntimePrefix(platform), StringComparison.OrdinalIgnoreCase))
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
    /// Adds the standard MIT license as LICENSE.txt in the wrapper folder, with the release date's year
    /// and the license holder from the settings. Only when the snapshot has no license file at all.
    /// </summary>
    /// <summary>
    /// Brings the README's "Before you build or run it" section in line with the code, in one edit: removes
    /// the lines of queued topics the code no longer bears out, then adds a draft line for each queued topic
    /// it does but the README doesn't declare, adding the section if there is none. The added lines are the
    /// ones the rule suggests, worked out again from the same snapshot, so the preview shows exactly what the
    /// rows showed; the wording is a draft for the author to reword. A section left with nothing in it goes
    /// too. Returns the number of lines added and removed.
    /// </summary>
    private static int PlanDeclarations(FixPlan plan, RepoSnapshot snapshot, List<string> topics, List<string> removals, AppPlatform filed)
    {
        var readmes = ReadmeCollector.Find(snapshot);
        if (readmes.Count != 1)
        {
            plan.Skipped.Add("README declarations: the snapshot needs exactly one README (see the README rule).");
            return 0;
        }
        var path = readmes[0];
        var bytes = snapshot.ReadBytes(path);
        if (bytes is null)
        {
            plan.Skipped.Add($"{path} couldn't be read from the zip.");
            return 0;
        }

        // The rows as the scan reports them now; only topics still undeclared get a line, and only lines
        // still not borne out are removed.
        var current = StandardsRules.Run(snapshot, out _, "", filed);
        var rows = current
            .Where(r => r.Fix == FixKind.DeclareInReadme && r.FixProject is not null)
            .ToDictionary(r => r.FixProject!, StringComparer.OrdinalIgnoreCase);
        var stale = current
            .Where(r => r.Fix == FixKind.UndeclareInReadme && r.FixProject is not null)
            .Select(r => r.FixProject!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var drafts = DeclarationScan.Topics
            .Where(t => topics.Contains(t.Label, StringComparer.OrdinalIgnoreCase))
            .Select(t => (Topic: t, Line: rows.TryGetValue(t.Label, out var row) ? StandardsRules.DraftLine(row) : null))
            .ToList();
        foreach (var (topic, _) in drafts.Where(d => d.Line is null))
            plan.Skipped.Add($"README declarations: {topic.Label} is declared already, or no longer found.");
        var lines = drafts.Where(d => d.Line is not null).Select(d => d.Line!).ToList();
        foreach (var label in removals.Where(l => !stale.Contains(l)))
            plan.Skipped.Add($"README declarations: {label} is borne out by the code again; its line stays.");
        var remove = removals.Where(stale.Contains).ToList();
        if (lines.Count == 0 && remove.Count == 0) return 0;

        var (text, encoding) = CsprojEditor.Decode(bytes);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var existing = text.Replace("\r\n", "\n").Split('\n').ToList();
        var removed = new List<string>();

        if (remove.Count > 0 && DeclarationScan.ReadSection(text) is { } section)
        {
            var gone = section.Labels
                .Where(l => remove.Contains(l.Label, StringComparer.OrdinalIgnoreCase))
                .Select(l => l.Line - 1)
                .OrderByDescending(i => i)
                .ToList();
            foreach (var index in gone)
            {
                removed.Insert(0, existing[index]);
                existing.RemoveAt(index);
            }
            removed.AddRange(DeclarationScan.RemoveEmptySection(existing).Select(l => l + "  (the section is empty now)"));
        }

        if (lines.Count == 0)
        {
            plan.EditedFiles[path] = CsprojEditor.Encode(string.Join(newline, existing), encoding);
            plan.Sections.Add(($"Changes in {path}", removed.Select(l => "- " + l).ToList()));
            return removed.Count(l => !l.EndsWith("(the section is empty now)", StringComparison.Ordinal));
        }

        var (before, addSection, blankBefore) = DeclarationScan.InsertionPoint(existing);

        var insert = new List<string>();
        if (blankBefore) insert.Add("");
        if (addSection)
        {
            if (before > 0 && existing[before - 1].Trim().Length > 0) insert.Add("");
            insert.Add($"## {DeclarationScan.Heading}");
            insert.Add("");
        }
        insert.AddRange(lines);
        if (addSection && before < existing.Count && existing[before].Trim().Length > 0) insert.Add("");

        existing.InsertRange(before, insert);
        plan.EditedFiles[path] = CsprojEditor.Encode(string.Join(newline, existing), encoding);
        var preview = removed.Select(l => "- " + l).Concat(insert.Where(l => l.Length > 0).Select(l => "+ " + l))
            .Append("  draft wording from what the check found; reword it in the README as you see fit");
        if (blankBefore)
            preview = preview.Append("  the section's own text stays as it is: check it still fits next to these lines");
        plan.Sections.Add(($"Changes in {path}", preview.ToList()));
        return lines.Count + removed.Count(l => !l.EndsWith("(the section is empty now)", StringComparison.Ordinal));
    }

    private static void PlanMitLicense(FixPlan plan, RepoSnapshot snapshot, DateTime day)
    {
        var holder = Standard.Current.LicenseHolder;
        var existing = LicenseCheck.Find(snapshot);

        if (existing.Count > 0)
            plan.Skipped.Add($"MIT license: the snapshot already has {string.Join(", ", existing)}, which the Fix never replaces.");
        else if (!LicenseCheck.IsFullName(holder))
            plan.Skipped.Add("MIT license: set the license holder in Settings to a full name first.");
        else if (snapshot.WrapperFolder is not { } wrapper)
            plan.Skipped.Add("MIT license: the zip has no single wrapper folder to put it in.");
        else
        {
            var name = $"{wrapper}/{FixCatalog.LicenseFileName}";
            plan.AddedFiles[name] = new UTF8Encoding(false).GetBytes(LicenseCheck.StandardText(day.Year, holder));
            plan.Sections.Add(("Add the MIT license", [$"+ {name} (MIT, {day.Year} {holder})"]));
        }
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
