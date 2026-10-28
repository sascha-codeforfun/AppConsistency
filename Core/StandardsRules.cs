using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Xml.Linq;

namespace AppConsistency.Core;

/// <summary>
/// The built-in app standards. Each rule reports Pass, Info, Warn or Fail with a one-line reason.
/// Order of categories here is the order they appear in the UI.
/// </summary>
public static class StandardsRules
{
    private const string Package = "Package";
    private const string Project = "Project";
    private const string Publish = "Publish";
    private const string Icons = "Icon and header";
    private const string Dark = "Dark mode";
    private const string Deps = "Dependencies";
    private const string Security = "Security";
    private const string Leaks = "Build leaks";
    private const string Code = "Code";

    /// <summary>Rule name for the elevation check; the app list uses it to mark elevated apps.</summary>
    public const string ElevationRule = "Admin rights";

    // Packages that bring web content into the app; risky when combined with elevation. A setting.
    private static IReadOnlyList<string> WebPackages => Standard.Current.WebPackages;

    private static readonly Regex XmlComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ExecutionLevel = new(
        @"<\s*(?:\w+:)?requestedExecutionLevel\b[^>]*\blevel\s*=\s*[""'](?<level>\w+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The icon and header names are settings (app.ico and app_256.png by default).
    private static string IconName => Standard.Current.Icon.ExeIcon;
    private static string HeaderImageName => Standard.Current.Icon.HeaderImage;
    private static int HeaderImageSize => Standard.Current.Icon.HeaderImageSize;

    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly HashSet<string> JunkFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".vs" };

    private static readonly Regex DarkModeAttribute = new(
        @"IMMERSIVE_DARK_MODE|DarkMode|DwmSetWindowAttribute\s*\([^,]+,\s*(?:19|20)\s*,",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AssemblyLocationUse = new(
        @"Assembly(?:\(\))?\s*\??\.\s*Location\b",
        RegexOptions.Compiled);

    // Third-party packages accepted as known exceptions, from the settings. The built-in list is empty:
    // which packages to trust is yours to decide, and the public code carries no one's choice.
    private static IReadOnlyDictionary<string, PackageEntry> AllowedPackages => Standard.Current.ApprovedPackages;

    private static readonly Regex VersionNumber = new(@"\d+(?:\.\d+){0,3}", RegexOptions.Compiled);

    /// <summary>The approved list, for showing in the app.</summary>
    public static IEnumerable<(string Name, string MinVersion, string Reason)> Allowlist =>
        AllowedPackages
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => (p.Key, p.Value.MinVersion, p.Value.Reason));

    /// <summary>The note an unapproved package gets when no approved packages are set up at all.</summary>
    private static string EmptyListNote =>
        AllowedPackages.Count == 0 ? " No approved packages are configured yet: add them in Settings." : "";

    // Package ids (prefix match) that mark a project as a test project.
    private static readonly string[] TestPackagePrefixes =
        ["Microsoft.NET.Test.Sdk", "xunit", "NUnit", "MSTest", "TUnit"];

    // AppName.Tests, AppName.UnitTests, AppName-Test… (a separator is required so "SpeedTest" isn't caught).
    private static readonly Regex TestProjectName = new(
        @"[.\-_](?:Unit|Integration)?Tests?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CodeWindowClass = new(
        @"\bclass\s+\w+\s*:\s*(?:System\.Windows\.)?Window\b",
        RegexOptions.Compiled);

    private static readonly Regex ResourceReference = new(
        @"^\{\s*(?:StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?(?<key>[^\s}]+)\s*\}$",
        RegexOptions.Compiled);

    public static List<CheckResult> Run(RepoSnapshot snapshot, out ScanFacts facts)
    {
        var results = new List<CheckResult>();
        facts = new ScanFacts();
        facts.AllFiles.AddRange(snapshot.Files.Select(f => f.RelPath));

        PackageRules(snapshot, results, facts);
        var (project, tests) = ProjectRules(snapshot, results);
        facts.HasTests = tests.Count > 0;
        ReadmeRule(snapshot, results, facts);
        PrivateMentionsRule(snapshot, results, facts);
        LicenseRule(snapshot, results, facts);
        if (project is not null) VersionRule(snapshot, project, results, facts);

        // Source in test project folders doesn't ship, so the app-level rules skip it.
        var testDirs = tests
            .Select(t => t.Dir)
            .Where(d => d.Length > 0 && (project is null || !d.Equals(project.Dir, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var appFiles = snapshot.Files
            .Where(f => !testDirs.Any(d => RepoSnapshot.IsSameOrUnder(RepoSnapshot.DirOf(f.RelPath), d)))
            .ToList();

        if (project is not null)
        {
            TestIsolationRule(project, tests, results);
            PublishRules(snapshot, project, results);
            IconRules(snapshot, appFiles, project, results);
        }
        else
        {
            results.Report(Publish, "Publish settings", CheckStatus.Info, "Skipped: no readable app project.");
            results.Report(Icons, "Icon wiring", CheckStatus.Info, "Skipped: no readable app project.");
        }

        HeaderRule(appFiles, results);
        DarkTitleBarRule(appFiles, results);
        DarkBackgroundRule(appFiles, results);

        if (project is not null)
        {
            var shipped = ShippedProjects(snapshot, project);
            DependencyRules(snapshot, project, shipped, tests, results, facts);
            ElevationRules(snapshot, appFiles, project, shipped, results);
        }

        ReproducibleRules(snapshot, appFiles, SelectProjects(snapshot).Apps, results);
        LeakRules(snapshot, appFiles, results);
        CodeRules(appFiles, results);
        WriteRules(appFiles, project, results);

        return results;
    }

    // ================= Package =================

    private static void PackageRules(RepoSnapshot s, List<CheckResult> r, ScanFacts facts)
    {
        const string wrapperRule = "Single wrapper folder";
        var rootDirs = s.RootDirs;
        var rootFiles = s.RootFiles;

        if (!s.IsZip)
            r.Report(Package, wrapperRule, CheckStatus.Info, "Folder snapshot; this rule only applies to zips.");
        else if (rootFiles.Count == 0 && rootDirs.Count == 1)
            r.Report(Package, wrapperRule, CheckStatus.Pass, $"Everything sits inside {rootDirs[0]}/.");
        else if (rootDirs.Count == 0)
            r.Report(Package, wrapperRule, CheckStatus.Warn,
                "Files sit directly at the zip root with no wrapper folder. RepoBuilder copes, but it breaks your zip convention.");
        else
            r.Report(Package, wrapperRule, CheckStatus.Warn,
                $"The zip root holds {Plural(rootDirs.Count, "folder")} and {Plural(rootFiles.Count, "file")}: " +
                Sample(rootDirs.Select(d => d + "/").Concat(rootFiles)) +
                ". RepoBuilder copes, but your convention is one wrapper folder.");

        const string cleanRule = "No build output";
        var junk = s.Directories.Where(d => JunkFolders.Contains(RepoSnapshot.NameOf(d))).ToList();
        junk = junk.Where(d => !junk.Any(o => o != d && d.StartsWith(o + "/", StringComparison.OrdinalIgnoreCase))).ToList();

        if (!s.IsZip)
            r.Report(Package, cleanRule, CheckStatus.Info, "Folder snapshot; local build output is expected.");
        else if (junk.Count == 0)
            r.Report(Package, cleanRule, CheckStatus.Pass, "No bin, obj or .vs folders in the zip.");
        else
            r.Report(Package, cleanRule, CheckStatus.Warn,
                $"Contains {Sample(junk.Select(j => j + "/"))}. These bloat the zip and can confuse the offline build.");

        // Per-user IDE state: not needed to build and can carry local paths, but it doesn't break
        // the build, so it warns. Files inside bin, obj and .vs are listed too, even though
        // "No build output" already flags those folders.
        const string ideRule = "No obsolete IDE files";
        var ideFiles = ObsoleteIdeFiles(s);

        if (!s.IsZip)
            r.Report(Package, ideRule, CheckStatus.Info, "Folder snapshot; local IDE files are expected.");
        else if (ideFiles.Count == 0)
            r.Report(Package, ideRule, CheckStatus.Pass, "No .user, .suo, .editorconfig, publish profiles or similar files in the zip.");
        else
            r.Report(Package, ideRule, CheckStatus.Warn,
                $"Found {Plural(ideFiles.Count, "obsolete IDE file")}, not needed to build — remove from the repo." +
                string.Concat(ideFiles.Select(f => $"\n  {f.RelPath}  ({f.Label})")),
                FixKind.RemoveIdeFiles);

        GitFilesRule(s, r);
        UnlistedFilesRule(s, r, facts, ideFiles.Select(f => f.RelPath));
        PrivateFilesRule(s, r, facts);
        TimestampRule(s, r);
    }

    /// <summary>
    /// Git and GitHub files are dead weight in a snapshot: the build reads the zip, and RepoPublisher
    /// pushes the files through the GitHub API and adds its own workflow. The fix removes exactly the list.
    /// </summary>
    private static void GitFilesRule(RepoSnapshot s, List<CheckResult> r)
    {
        const string rule = "No git files";
        var git = FileHygiene.GitFiles(s);

        var lines = git.Folders.Select(f => $"\n  {f.Folder}/  (whole folder, {Plural(f.FileCount, "file")})")
            .Concat(git.Files.Select(f => $"\n  {f}"));

        if (git.Count == 0)
            r.Report(Package, rule, CheckStatus.Pass, "No .gitignore, .gitattributes, .git or .github in the snapshot.");
        else if (!s.IsZip)
            r.Report(Package, rule, CheckStatus.Info,
                $"Folder snapshot; {Plural(git.Count, "git file or folder", "git files or folders")} here, expected in a working copy." + string.Concat(lines));
        else
            r.Report(Package, rule, CheckStatus.Warn,
                $"Found {Plural(git.Count, "git file or folder", "git files or folders")}. The build reads the zip and RepoPublisher " +
                "pushes through the GitHub API with its own workflow, so none of these are used." + string.Concat(lines),
                FixKind.RemoveGitFiles);
    }

    /// <summary>
    /// Every file that isn't on the whitelist and isn't used by the build. Files the README uses
    /// (links to, embeds or names) are documentation and only noted; the rest warn, as does a
    /// second copy of a file the README uses. Whether a file belongs depends on the repo, so the
    /// fix removes only the files you tick; nothing is ticked to start with.
    /// </summary>
    private static void UnlistedFilesRule(RepoSnapshot s, List<CheckResult> r, ScanFacts facts, IEnumerable<string> ideFiles)
    {
        const string rule = "Unlisted files";

        var handled = new HashSet<string>(ideFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var path in SolutionFiles(s).Removable) handled.Add(path);
        foreach (var path in ReadmeCollector.Find(s)) handled.Add(path);
        foreach (var path in LicenseCheck.Find(s)) handled.Add(path);

        var projects = s.Files
            .Where(f => f.Extension == ".csproj" && f.Text is not null)
            .Select(f => ProjectModel.Load(s, f))
            .ToList();

        var unlisted = FileHygiene.MarkReadmeUse(s, FileHygiene.Unlisted(s, projects, handled));
        // The ones to look at first, then the ones the README uses.
        facts.Unlisted.AddRange(unlisted.Where(f => !f.ReadmeUses).Concat(unlisted.Where(f => f.ReadmeUses)));

        if (unlisted.Count == 0)
        {
            r.Report(Package, rule, CheckStatus.Pass, "Every file is source, art, the README, the license or used by the build.");
            return;
        }

        var loose = unlisted.Where(f => !f.ReadmeUses).ToList();
        var documented = unlisted.Where(f => f.ReadmeUses).ToList();

        string Line(UnlistedFile f) =>
            $"\n    {f.RelPath}  ({Size(f.Length)})" + (f.DuplicateOf is { } of ? $"  same name as {of}, which the README uses" : "");

        var lists = (loose.Count > 0 ? "\n  Not referenced:" + string.Concat(loose.Select(Line)) : "")
                    + (documented.Count > 0 ? "\n  Used by the README:" + string.Concat(documented.Select(Line)) : "");

        var counts = loose.Count == 0
            ? documented.Count == 1
                ? "1 file not on the whitelist, used by the README, so it's documentation."
                : $"{documented.Count} files not on the whitelist, all used by the README, so they're documentation."
            : documented.Count == 0
                ? $"{Plural(loose.Count, "file")} not on the whitelist and not used by the build or the README."
                : $"{Plural(loose.Count, "file")} not on the whitelist and not used by the build or the README; " +
                  $"{Plural(documented.Count, "more file", "more files")} the README uses.";

        if (!s.IsZip)
        {
            r.Report(Package, rule, CheckStatus.Info, "Folder snapshot; " + char.ToLowerInvariant(counts[0]) + counts[1..] + lists);
            return;
        }

        r.Report(Package, rule, loose.Count > 0 ? CheckStatus.Warn : CheckStatus.Info,
            counts + " Whether they belong depends on the repo: the fix lets you tick the ones to remove." + lists,
            FixKind.RemoveUnlistedFiles);
    }

    /// <summary>
    /// Your private notes (the names in Settings) are recognised for what they are: never a defect,
    /// never unlisted, kept in every zip except the copy staged for publishing.
    /// </summary>
    private static void PrivateFilesRule(RepoSnapshot s, List<CheckResult> r, ScanFacts facts)
    {
        const string rule = "Private files";
        var files = FileHygiene.PrivateFiles(s);
        facts.PrivateFiles.AddRange(files);

        var names = Core.Standard.Current.PrivateFiles;
        if (names.Count == 0)
            r.Report(Package, rule, CheckStatus.Pass, "No private file names are configured (Settings).");
        else if (files.Count == 0)
            r.Report(Package, rule, CheckStatus.Pass, $"No {string.Join(" or ", names)}.");
        else
            r.Report(Package, rule, CheckStatus.Info,
                $"{Plural(files.Count, "private file")}, kept in the zip and the backup, left out when you stage for publishing." +
                string.Concat(files.Select(f => $"\n  {f}")));
    }

    /// <summary>
    /// A copy staged for publishing leaves the private files out, so a README that names one would
    /// point at nothing, and tell the public the notes exist. Only you can reword it, so there's no fix.
    /// </summary>
    private static void PrivateMentionsRule(RepoSnapshot s, List<CheckResult> r, ScanFacts facts)
    {
        var mentions = FileHygiene.PrivateMentions(s, FileHygiene.PrivateFiles(s));
        facts.PrivateMentions.AddRange(mentions);
        if (mentions.Count == 0) return;

        r.Report(Project, "README mentions private files", CheckStatus.Warn,
            $"{Plural(mentions.Count, "line names", "lines name")} a private file. The copy staged for publishing leaves it out, " +
            "so the public README would point at nothing. Reword these lines, or move them into the private file." +
            string.Concat(mentions.Select(m => $"\n  {m}")));
    }

    /// <summary>
    /// A release zip has one timestamp: every entry dated the same day at 00:00:00, and no NTFS or
    /// extended timestamps, which file managers would show instead of it.
    /// </summary>
    private static void TimestampRule(RepoSnapshot s, List<CheckResult> r)
    {
        const string rule = "Release timestamps";

        if (!s.IsZip)
        {
            r.Report(Package, rule, CheckStatus.Info, "Folder snapshot; release timestamps apply to zips.");
            return;
        }

        if (s.EntryTimes is null)
        {
            r.Report(Package, rule, CheckStatus.Info, "Couldn't read the entry times from the zip.");
            return;
        }

        var files = s.EntryTimes.Where(e => !e.IsDirectory).ToList();
        if (files.Count == 0) return;

        var times = files.Select(f => f.Modified).Distinct().ToList();
        int extra = s.EntryTimes.Count(e => e.HasExtraTime);
        var only = times.Count == 1 ? times[0] : null;

        if (only is { } t && t.TimeOfDay == TimeSpan.Zero && extra == 0)
        {
            r.Report(Package, rule, CheckStatus.Pass, $"All {Plural(files.Count, "file")} dated {t:yyyy-MM-dd} 00:00:00.");
            return;
        }

        var parts = new List<string>();
        if (only is { } single)
            parts.Add($"All files dated {single:yyyy-MM-dd HH:mm:ss}, not 00:00:00");
        else
        {
            var known = times.Where(x => x is not null).Select(x => x!.Value).OrderBy(x => x).ToList();
            parts.Add(known.Count == 0
                ? "The entry times can't be read as dates"
                : $"{times.Count} different times, from {known[0]:yyyy-MM-dd HH:mm:ss} to {known[^1]:yyyy-MM-dd HH:mm:ss}");
        }
        if (extra > 0)
            parts.Add($"{Plural(extra, "entry carries", "entries carry")} NTFS or extended timestamps, which file managers show instead");

        r.Report(Package, rule, CheckStatus.Warn,
            string.Join("; ", parts) + ". The fixed copy dates every entry on the release day at 00:00:00.",
            FixKind.ReleaseTimestamps);
    }

    /// <summary>
    /// Every per-user IDE file in the snapshot with what kind it is, sorted by path.
    /// The "remove obsolete IDE files" fix removes exactly this list.
    /// </summary>
    public static List<(string RelPath, string Label)> ObsoleteIdeFiles(RepoSnapshot s) =>
        s.Files
            .Select(f => (f.RelPath, Label: IdeFileLabel(f.RelPath)))
            .Where(f => f.Label is not null)
            .Select(f => (f.RelPath, f.Label!))
            .OrderBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Name endings of per-user IDE files, longest first so .DotSettings.user beats .user.
    // A bare name counts too: Visual Studio writes the solution options as .vs/App/v17/.suo.
    private static readonly (string Suffix, string Label)[] IdeFileSuffixes =
    [
        (".DotSettings.user", "ReSharper per-user settings"),
        (".sln.docstates", "Visual Studio open-document state"),
        (".userosscache", "Visual Studio per-user cache"),
        (".userprefs", "Visual Studio for Mac / MonoDevelop user preferences"),
        (".VC.opendb", "C++ IntelliSense database"),
        (".rsuser", "Rider/ReSharper per-user settings"),
        (".vspscc", "old SourceSafe/TFS source-control binding"),
        (".vssscc", "old SourceSafe/TFS source-control binding"),
        (".VC.db", "C++ IntelliSense database"),
        (".user", "Visual Studio per-user settings"),
        (".suo", "Visual Studio solution user options"),
        (".sdf", "C++ IntelliSense database"),
        (".pubxml", "Visual Studio publish profile"),
        (".editorconfig", "editor settings, not used to build from the zip"),
    ];

    /// <summary>What kind of per-user IDE file this is, or null when it isn't one.</summary>
    private static string? IdeFileLabel(string relPath)
    {
        if (relPath.Equals(".idea/workspace.xml", StringComparison.OrdinalIgnoreCase)
            || relPath.EndsWith("/.idea/workspace.xml", StringComparison.OrdinalIgnoreCase))
            return "Rider per-user workspace";

        var name = RepoSnapshot.NameOf(relPath);
        foreach (var (suffix, label) in IdeFileSuffixes)
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return label;
        return null;
    }

    // ================= Project =================

    private static (ProjectModel? Main, List<ProjectModel> Tests) ProjectRules(RepoSnapshot s, List<CheckResult> r)
    {
        const string fileRule = "Project file";
        var (selected, apps, tests) = SelectProjects(s);

        if (apps.Count + tests.Count == 0)
        {
            r.Report(Project, fileRule, CheckStatus.Fail, "No .csproj in the snapshot.");
            return (null, []);
        }

        if (selected is null)
        {
            r.Report(Project, fileRule, CheckStatus.Fail,
                $"Only test projects found ({Sample(tests.Select(t => t.RelPath))}); no app project to check.");
            return (null, tests);
        }

        var main = selected;

        if (main.ParseError is not null)
        {
            r.Report(Project, fileRule, CheckStatus.Fail, $"{main.RelPath} couldn't be read: {main.ParseError}");
            return (null, tests);
        }

        // RepoBuilder searches every subfolder for projects and builds the one that makes an exe,
        // asking when there's more than one. So location doesn't matter; the number of exe projects does.
        var exes = apps.Where(IsExeProject).ToList();
        var libraries = apps.Except(exes).ToList();

        var notes = "";
        if (libraries.Count > 0)
            notes += $", with {Plural(libraries.Count, "library project")} ({Sample(libraries.Select(l => l.RelPath))})";
        if (tests.Count > 0)
            notes += $", plus {Plural(tests.Count, "test project")} ({Sample(tests.Select(t => t.RelPath))})";

        if (exes.Count == 0)
            r.Report(Project, fileRule, CheckStatus.Fail,
                $"No project builds an exe (OutputType WinExe or Exe), so RepoBuilder has nothing to build. Checking {main.RelPath}{notes}.");
        else if (exes.Count == 1)
            r.Report(Project, fileRule, CheckStatus.Pass, main.RelPath + notes + ".");
        else
            r.Report(Project, fileRule, CheckStatus.Info,
                $"{exes.Count} exe projects ({Sample(exes.Select(e => e.RelPath))}); RepoBuilder asks which one to build. Checking {main.RelPath}{notes}.");

        // The build chain doesn't use solution files. A .slnx in a repo with more than one project
        // is kept for the IDE and only noted; an old .sln is clutter either way.
        const string solutionRule = "No solution files";
        var (removable, kept, projectCount) = SolutionFiles(s);
        var keptNote = kept.Count == 0 ? ""
            : $"Keeps {Sample(kept)}: with {Plural(projectCount, "project")} in the repo, a .slnx is fine.";

        if (removable.Count == 0 && kept.Count == 0)
            r.Report(Project, solutionRule, CheckStatus.Pass, "No .sln or .slnx files.");
        else if (removable.Count == 0)
            r.Report(Project, solutionRule, CheckStatus.Info, keptNote);
        else
            r.Report(Project, solutionRule, CheckStatus.Warn,
                $"Contains {Sample(removable)}. Your build chain doesn't use solution files." +
                (keptNote.Length > 0 ? " " + keptNote : ""),
                s.IsZip ? FixKind.RemoveSolutionFiles : FixKind.None);

        const string tfmRule = "Target framework";
        var tfm = main.Prop("TargetFramework");
        var tfms = main.Prop("TargetFrameworks");
        var minimum = Core.Standard.Current.MinimumTargetFramework;
        var wanted = Core.Standard.ParseTargetFramework(minimum);
        var found = Core.Standard.ParseTargetFramework(tfm);
        bool meets = wanted is { } w && found is { } f && f.Version >= w.Version
                     && (w.Platform.Length == 0 || f.Platform == w.Platform);
        if (tfm is not null && meets)
            r.Report(Project, tfmRule, CheckStatus.Pass, tfm);
        else if (tfm is null && tfms is not null)
            r.Report(Project, tfmRule, CheckStatus.Warn, $"Multi-targets {tfms}. Expected a single {minimum} or newer.");
        else
            r.Report(Project, tfmRule, CheckStatus.Fail, $"Found {tfm ?? "nothing"}. Expected {minimum} or newer.");

        if (main.IsTrue("UseWPF"))
            r.Report(Project, "WPF", CheckStatus.Pass, "UseWPF is true.");
        else
            r.Report(Project, "WPF", CheckStatus.Fail, "UseWPF isn't set to true.");

        var outputType = main.Prop("OutputType");
        if (string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase))
            r.Report(Project, "Output type", CheckStatus.Pass, "WinExe");
        else if (string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase))
            r.Report(Project, "Output type", CheckStatus.Warn, "Exe opens a console window next to the app. Expected WinExe.");
        else
            r.Report(Project, "Output type", CheckStatus.Fail, $"Found {outputType ?? "nothing"}. Expected WinExe.");

        return (main, tests);
    }

    /// <summary>
    /// One README.md per repo, in any folder and any letter case. Several readmes (often an old
    /// README.txt left next to the new .md) or a single one in another format both warn.
    /// No fix: only you know which one is current. Collect README reads the same list.
    /// </summary>
    private static void ReadmeRule(RepoSnapshot s, List<CheckResult> r, ScanFacts facts)
    {
        const string rule = "README";
        var readmes = ReadmeCollector.Find(s);
        facts.Readmes.AddRange(readmes);

        if (readmes.Count == 0)
            r.Report(Project, rule, CheckStatus.Warn, "No README found. Expected one README.md.");
        else if (readmes.Count > 1)
            r.Report(Project, rule, CheckStatus.Warn,
                $"{readmes.Count} readmes: {Sample(readmes, 6)}. Keep one README.md.");
        else if (!ReadmeCollector.IsMarkdown(readmes[0]))
            r.Report(Project, rule, CheckStatus.Warn, $"Found {readmes[0]}. Expected README.md.");
        else
            r.Report(Project, rule, CheckStatus.Pass, readmes[0]);
    }

    /// <summary>
    /// Whether the repo carries the standard MIT license. Not a verdict: whatever it finds is shown in
    /// indigo, as MIT or as license, and never makes the app warn or fail. Any name counts.
    /// </summary>
    private static void LicenseRule(RepoSnapshot s, List<CheckResult> r, ScanFacts facts)
    {
        var verdict = LicenseCheck.Inspect(s);
        facts.Licenses.AddRange(verdict.Files);
        facts.LicenseIsMit = verdict.IsMit;
        r.Add(new CheckResult(Project, "License", CheckStatus.License, verdict.Detail) { PillWord = verdict.IsMit ? "MIT" : "license" });
    }

    /// <summary>The header shows the app's version, so the project has to set one.</summary>
    private static void VersionRule(RepoSnapshot s, ProjectModel p, List<CheckResult> r, ScanFacts facts)
    {
        const string rule = "Version";
        var name = p.Prop("Version") is not null ? "Version" : p.Prop("VersionPrefix") is not null ? "VersionPrefix" : null;

        if (name is not null)
        {
            var value = p.Prop(name)!;
            facts.AppVersion = value;
            var source = p.PropSource.TryGetValue(name, out var file) && !file.Equals(p.RelPath, StringComparison.OrdinalIgnoreCase)
                ? $" (set in {file})"
                : "";
            r.Report(Project, rule, CheckStatus.Pass, $"{value}{(name == "VersionPrefix" ? " as VersionPrefix" : "")}{source}.");
            return;
        }

        r.Report(Project, rule, CheckStatus.Warn,
            $"{RepoSnapshot.NameOf(p.RelPath)} sets no <Version>, so every build is 1.0.0 and the header has no version to show.",
            CanEdit(s, p, "Version", "1.0.0", out _) ? FixKind.AddVersion : FixKind.None);
    }

    /// <summary>
    /// Solution files split into the ones to remove and the ones to keep. Every .csproj counts
    /// (app, library and test); with more than one, a .slnx stays. A .sln never does.
    /// The "remove solution files" fix uses the same split, so it never deletes a kept file.
    /// </summary>
    public static (List<string> Removable, List<string> Kept, int ProjectCount) SolutionFiles(RepoSnapshot s)
    {
        int projects = s.Files.Count(f => f.Extension == ".csproj");
        bool keepSlnx = projects > 1;

        var removable = new List<string>();
        var kept = new List<string>();
        foreach (var file in s.Files.Where(f => f.Extension is ".sln" or ".slnx"))
        {
            if (keepSlnx && file.Extension == ".slnx") kept.Add(file.RelPath);
            else removable.Add(file.RelPath);
        }
        return (removable, kept, projects);
    }

    /// <summary>Splits the snapshot's projects into app and test projects and picks the app project to check.</summary>
    public static (ProjectModel? Main, List<ProjectModel> Apps, List<ProjectModel> Tests) SelectProjects(RepoSnapshot s)
    {
        var models = s.Files
            .Where(f => f.Extension == ".csproj" && f.Text is not null)
            .Select(f => ProjectModel.Load(s, f))
            .ToList();
        var tests = models.Where(IsTestProject).ToList();
        var apps = models.Except(tests).ToList();

        var main = apps
            .OrderByDescending(m => m.IsTrue("UseWPF"))
            .ThenByDescending(m => string.Equals(m.Prop("OutputType"), "WinExe", StringComparison.OrdinalIgnoreCase))
            .ThenBy(m => m.RelPath.Length)
            .FirstOrDefault();

        return (main, apps, tests);
    }

    private static bool IsExeProject(ProjectModel m) =>
        m.Prop("OutputType") is { } type
        && (type.Equals("WinExe", StringComparison.OrdinalIgnoreCase) || type.Equals("Exe", StringComparison.OrdinalIgnoreCase));

    private static bool IsTestProject(ProjectModel m)
    {
        if (m.IsTrue("IsTestProject")) return true;
        if (m.Sdk?.StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase) == true) return true;

        if (m.Items.Any(i => i.Type == "PackageReference" && i.Mode != ItemMode.Remove
                             && TestPackagePrefixes.Any(p => i.Include.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
            return true;

        return TestProjectName.IsMatch(Path.GetFileNameWithoutExtension(RepoSnapshot.NameOf(m.RelPath)));
    }

    /// <summary>
    /// An SDK-style project compiles every .cs under its folder. A test project nested inside the app
    /// folder must be excluded, or the app build pulls in the test code.
    /// </summary>
    private static void TestIsolationRule(ProjectModel main, List<ProjectModel> tests, List<CheckResult> r)
    {
        if (tests.Count == 0) return;
        const string rule = "Tests kept out of the app build";

        var excludes = main.Items
            .Where(i => i.Mode == ItemMode.Remove && i.Type is "Compile" or "Page")
            .SelectMany(i => i.Include.Split(';'))
            .Concat((main.Prop("DefaultItemExcludes") ?? "").Split(';'))
            .Select(x => x.Trim().Replace('\\', '/'))
            .Where(x => x.Length > 0 && !x.StartsWith("$(", StringComparison.Ordinal))
            .Select(GlobToRegex)
            .ToList();

        var sameFolder = new List<string>();
        var leaking = new List<string>();
        int nested = 0;

        foreach (var test in tests)
        {
            if (test.Dir.Equals(main.Dir, StringComparison.OrdinalIgnoreCase))
            {
                sameFolder.Add(test.RelPath);
                continue;
            }
            if (!RepoSnapshot.IsSameOrUnder(test.Dir, main.Dir)) continue;

            nested++;
            var rel = main.Dir.Length == 0 ? test.Dir : test.Dir[(main.Dir.Length + 1)..];
            string[] probes = [rel + "/Probe.cs", rel + "/Sub/Probe.cs"];
            if (!probes.All(probe => excludes.Any(x => x.IsMatch(probe))))
                leaking.Add(rel);
        }

        if (sameFolder.Count > 0)
        {
            r.Report(Project, rule, CheckStatus.Fail,
                $"{Sample(sameFolder)} shares a folder with {RepoSnapshot.NameOf(main.RelPath)}. Both compile the same files and dotnet publish there sees two projects.");
        }
        else if (leaking.Count > 0)
        {
            var folder = leaking[0].Replace('/', '\\');
            r.Report(Project, rule, CheckStatus.Fail,
                $"{Sample(leaking.Select(l => l + "/"))} sits inside the app folder and isn't excluded, so the app build compiles the test code. " +
                $"Add <DefaultItemExcludes>$(DefaultItemExcludes);{folder}\\**</DefaultItemExcludes> to {RepoSnapshot.NameOf(main.RelPath)}.");
        }
        else if (nested > 0)
        {
            r.Report(Project, rule, CheckStatus.Pass, "Nested test folders are excluded from the app project.");
        }
        else
        {
            r.Report(Project, rule, CheckStatus.Pass, "Test projects sit outside the app folder.");
        }
    }

    // ================= Publish =================

    /// <summary>
    /// The five properties that make a single self-contained exe. They are one decision, so they are
    /// one row. RepoBuilder passes them on the command line today, but a batch that mixes projects
    /// with different publish intents can't, so the .csproj has to carry them.
    /// </summary>
    private static void PublishRules(RepoSnapshot s, ProjectModel p, List<CheckResult> r)
    {
        const string rule = "Publish settings";

        var missing = new List<string>();
        var notes = new List<string>();
        bool fixable = false;

        foreach (var (property, expected) in FixCatalog.PublishProperties)
        {
            var value = p.Prop(property);
            if (value is not null && Matches(property, value, expected)) continue;

            missing.Add(value is null ? $"{property} isn't set" : $"{property} is {value}");

            // A Windows RID that isn't the expected one may be deliberate, so it's never rewritten.
            if (property == "RuntimeIdentifier" && value is not null)
            {
                notes.Add($"{property} is left alone, since {value} may be deliberate.");
                continue;
            }
            if (CanEdit(s, p, property, expected, out var reason)) fixable = true;
            else if (reason is not null) notes.Add(reason);
        }

        if (missing.Count == 0)
        {
            r.Report(Publish, rule, CheckStatus.Pass, "All five set: win-x64, self-contained, single file, native libraries bundled, compressed.");
            return;
        }

        var detail = $"{FixCatalog.PublishProperties.Length - missing.Count} of {FixCatalog.PublishProperties.Length} set. " +
                     $"Missing: {string.Join("; ", missing)}. " +
                     "RepoBuilder passes these at publish time, but a batch with mixed publish intents can't, so the .csproj needs them.";
        if (notes.Count > 0) detail += " " + string.Join(" ", notes);

        r.Report(Publish, rule, CheckStatus.Fail, detail, fixable ? FixKind.PublishSettings : FixKind.None);
    }

    private static bool Matches(string property, string value, string expected) =>
        property == "RuntimeIdentifier"
            ? value.StartsWith("win-", StringComparison.OrdinalIgnoreCase)
            : value.Equals(expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the property can be set safely in the app's own .csproj.</summary>
    private static bool CanEdit(RepoSnapshot s, ProjectModel p, string property, string value, out string? reason)
    {
        reason = null;
        if (!s.IsZip) return false;

        if (p.PropSource.TryGetValue(property, out var source)
            && !source.Equals(p.RelPath, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"{property} is set in {source}, which fixes don't change.";
            return false;
        }

        var text = s.Find(p.RelPath)?.Text;
        if (text is null) return false;

        var outcome = new CsprojEditor(text).SetProperty(property, value);
        if (outcome.Kind == EditKind.Blocked)
        {
            reason = outcome.Reason;
            return false;
        }
        return outcome.Kind is EditKind.Added or EditKind.Changed;
    }

    // ================= Icon and header =================

    private static void IconRules(RepoSnapshot s, IReadOnlyList<SnapshotFile> files, ProjectModel p, List<CheckResult> r)
    {
        // Exe icon wiring
        var icon = p.Prop("ApplicationIcon");
        if (icon is null)
            r.Report(Icons, "Exe icon", CheckStatus.Fail, $"ApplicationIcon isn't set. Expected {IconName}.");
        else if (RepoSnapshot.NameOf(RepoSnapshot.Normalize(icon)).Equals(IconName, StringComparison.OrdinalIgnoreCase))
            r.Report(Icons, "Exe icon", CheckStatus.Pass, $"ApplicationIcon is {icon}.");
        else
            r.Report(Icons, "Exe icon", CheckStatus.Warn, $"ApplicationIcon is {icon}. Expected {IconName}.");

        // Icon file itself
        var iconPath = RepoSnapshot.Combine(p.Dir, icon ?? IconName);
        var iconFile = s.Find(iconPath);
        if (iconFile is null)
        {
            r.Report(Icons, "Icon file", CheckStatus.Warn,
                $"{iconPath} isn't in the snapshot. The build fails until the icon set is added.");
        }
        else
        {
            var (status, detail) = InspectIco(iconFile);
            r.Report(Icons, "Icon file", status, detail);
        }

        // Header image included as a WPF resource
        var pngFiles = files.Where(f => f.Name.Equals(HeaderImageName, StringComparison.OrdinalIgnoreCase)).ToList();
        var pngRelToProject = pngFiles
            .Where(f => RepoSnapshot.IsSameOrUnder(RepoSnapshot.DirOf(f.RelPath), p.Dir))
            .Select(f => p.Dir.Length == 0 ? f.RelPath : f.RelPath[(p.Dir.Length + 1)..])
            .ToList();

        var pngItems = p.Items.Where(i => i.Mode != ItemMode.Remove && IncludesFile(i.Include, HeaderImageName, pngRelToProject)).ToList();
        var resource = pngItems.FirstOrDefault(i => i.Type == "Resource");
        var embedded = pngItems.FirstOrDefault(i => i.Type == "EmbeddedResource");
        var other = pngItems.FirstOrDefault();

        const string resourceRule = "Header image resource";
        if (resource is not null)
            r.Report(Icons, resourceRule, CheckStatus.Pass, $"Included as <Resource Include=\"{resource.Include}\" />.");
        else if (embedded is not null && files.Any(f => f.Extension == ".cs"
                     && f.Text?.Contains("GetManifestResourceStream", StringComparison.Ordinal) == true))
            r.Report(Icons, resourceRule, CheckStatus.Pass,
                "Included as EmbeddedResource and read with GetManifestResourceStream.");
        else if (embedded is not null)
            r.Report(Icons, resourceRule, CheckStatus.Warn,
                "Included as EmbeddedResource, but no code reads it with GetManifestResourceStream, and pack://application URIs need a <Resource> item.");
        else if (other is not null)
            r.Report(Icons, resourceRule, CheckStatus.Warn,
                $"Included as {other.Type}. Use <Resource> so it's compiled into the exe.");
        else
            r.Report(Icons, resourceRule, CheckStatus.Fail, $"{HeaderImageName} isn't included as a <Resource> item.");

        // Header image file itself
        var png = pngFiles.OrderBy(f => f.RelPath.Length).FirstOrDefault();
        if (png is null)
        {
            r.Report(Icons, "Header image file", CheckStatus.Warn,
                $"{HeaderImageName} isn't in the snapshot. The build fails until the icon set is added.");
        }
        else
        {
            var (status, detail) = InspectPng(png);
            r.Report(Icons, "Header image file", status, detail);
        }
    }

    private static (CheckStatus, string) InspectIco(SnapshotFile file)
    {
        var h = file.Head;
        if (h is null || h.Length < 6 || h[0] != 0 || h[1] != 0 || h[2] != 1 || h[3] != 0)
            return (CheckStatus.Fail, $"{file.RelPath} isn't a valid .ico file.");

        int count = h[4] | (h[5] << 8);
        var sizes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int offset = 6 + 16 * i;
            if (offset >= h.Length) break;
            sizes.Add(h[offset] == 0 ? 256 : h[offset]);
        }

        var list = string.Join(", ", sizes.Distinct().OrderBy(x => x));
        return sizes.Contains(256)
            ? (CheckStatus.Pass, $"Valid icon with {Plural(count, "image")} ({list} px).")
            : (CheckStatus.Warn, $"Valid icon but no 256 px image (has {list} px).");
    }

    private static (CheckStatus, string) InspectPng(SnapshotFile file)
    {
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var h = file.Head;
        if (h is null || h.Length < 24 || !h.Take(8).SequenceEqual(signature))
            return (CheckStatus.Fail, $"{file.RelPath} isn't a valid PNG.");

        int width = (h[16] << 24) | (h[17] << 16) | (h[18] << 8) | h[19];
        int height = (h[20] << 24) | (h[21] << 16) | (h[22] << 8) | h[23];

        return width == HeaderImageSize && height == HeaderImageSize
            ? (CheckStatus.Pass, $"Valid PNG, {width} × {height}.")
            : (CheckStatus.Warn, $"Valid PNG but {width} × {height}. Expected {HeaderImageSize} × {HeaderImageSize}.");
    }

    private static bool IncludesFile(string include, string fileName, IReadOnlyList<string> candidates)
    {
        foreach (var part in include.Split(';'))
        {
            var item = part.Trim().Replace('\\', '/');
            if (item.Length == 0) continue;

            if (RepoSnapshot.NameOf(item).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return true;

            if (item.Contains('*') || item.Contains('?'))
            {
                var glob = GlobToRegex(item);
                if (candidates.Any(c => glob.IsMatch(c))) return true;
            }
        }
        return false;
    }

    private static Regex GlobToRegex(string glob)
    {
        var pattern = Regex.Escape(glob)
            .Replace(@"\*\*/", "(?:.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]");
        return new Regex("^" + pattern + "$", RegexOptions.IgnoreCase);
    }

    private static void HeaderRule(IReadOnlyList<SnapshotFile> files, List<CheckResult> r)
    {
        const string rule = "Header block";

        // Comments are masked out first: a theme dictionary that only mentions app_256.png in a
        // comment isn't a header, and must not be mistaken for the file that carries one.
        var xamlRefs = files
            .Where(f => f.Extension == ".xaml" && f.Text is not null
                        && XmlComment.Replace(f.Text, "").Contains(HeaderImageName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (xamlRefs.Count == 0)
        {
            var code = files.FirstOrDefault(f =>
                f.Extension == ".cs" && f.Text?.Contains(HeaderImageName, StringComparison.OrdinalIgnoreCase) == true);

            if (code is not null)
            {
                var scan = CodeHeaderScan.Inspect(code.Text!, HeaderImageName);
                var where = scan?.MethodName is { } method ? $"{method}() in {code.Name}" : code.Name;

                if (scan is { TextLines: >= 2 })
                    r.Report(Icons, rule, CheckStatus.Pass,
                        $"Image with name and description built in code in {where}.");
                else
                    r.Report(Icons, rule, CheckStatus.Info,
                        $"{HeaderImageName} is loaded in {where}, but the text beside it couldn't be confirmed " +
                        $"({Plural(scan?.TextLines ?? 0, "text line")} found there). Check it by eye.");
            }
            else
                r.Report(Icons, rule, CheckStatus.Fail, $"No XAML references {HeaderImageName}, so there's no header image.");
            return;
        }

        // Every referencing file is scored and the best result wins. Deciding on whichever file
        // came first in the zip made the verdict depend on entry order.
        SnapshotFile? best = null;
        int bestLines = -1;

        foreach (var file in xamlRefs)
        {
            var doc = TryParse(file);
            if (doc is null) continue;

            // Order matters. A Window's Icon attribute also names app_256.png, and it is the taskbar
            // icon: it never has text beside it, so it must never be taken for the header image.
            var images = doc.Descendants().Where(e => e.Name.LocalName == "Image").ToList();
            var candidates = images
                .Where(References)                                    // <Image Source="...app_256.png">
                .Concat(images.Where(e => !References(e)))            // an <Image> whose Source is set elsewhere
                .Concat(doc.Descendants().Where(e => e != doc.Root && e.Name.LocalName != "Image" && References(e)))
                .ToList();
            if (candidates.Count == 0) continue;

            int lines = 0;
            foreach (var candidate in candidates)
            {
                // Climb towards the window looking for the container that holds both the image and
                // the text. Apps wrap the icon in a clickable Border or two, so it can be several
                // levels up; CountTextLines only looks a little way back down, so reaching a large
                // container can't start counting the whole view.
                var container = candidate.Parent;
                for (int up = 0; container is not null && up < HeaderClimb && lines < 2; up++, container = container.Parent)
                    lines = Math.Max(lines, CountTextLines(container));
                if (lines >= 2) break;
            }

            if (lines > bestLines) (best, bestLines) = (file, lines);
            if (bestLines >= 2) break;
        }

        if (best is null)
            r.Report(Icons, rule, CheckStatus.Fail,
                $"No <Image> shows {HeaderImageName}; the only references are the window icon or a comment, which aren't a header.");
        else if (bestLines >= 2)
            r.Report(Icons, rule, CheckStatus.Pass, $"Image with name and description in {best.RelPath}.");
        else
            r.Report(Icons, rule, CheckStatus.Warn,
                $"The header image in {best.Name} has {Plural(bestLines, "text line")} beside it. Expected the app name and a description.");
    }

    private static bool References(XElement element) =>
        element.Attributes().Any(a => a.Value.Contains(HeaderImageName, StringComparison.OrdinalIgnoreCase))
        || (!element.HasElements && element.Value.Contains(HeaderImageName, StringComparison.OrdinalIgnoreCase));

    /// <summary>How far above the image to look for its text, and how far back down to count.</summary>
    private const int HeaderClimb = 5;
    private const int HeaderReach = 3;

    /// <summary>
    /// Text elements at most HeaderReach levels below the container. Bounded so that climbing
    /// towards the window root can't start counting text from the whole view.
    /// </summary>
    private static int CountTextLines(XElement container)
    {
        int count = 0;
        Walk(container, 1);
        return count;

        void Walk(XElement parent, int depth)
        {
            if (depth > HeaderReach) return;
            foreach (var child in parent.Elements())
            {
                if (child.Name.LocalName is "TextBlock" or "Label" && HasText(child)) count++;
                Walk(child, depth + 1);
            }
        }
    }

    private static bool HasText(XElement e) =>
        !string.IsNullOrWhiteSpace(e.Attribute("Text")?.Value)
        || !string.IsNullOrWhiteSpace(e.Attribute("Content")?.Value)
        || !string.IsNullOrWhiteSpace(e.Value);

    // ================= Dark mode =================

    private static void DarkTitleBarRule(IReadOnlyList<SnapshotFile> files, List<CheckResult> r)
    {
        const string rule = "Dark title bar";
        var calls = files
            .Where(f => f.Extension == ".cs" && f.Text?.Contains("DwmSetWindowAttribute", StringComparison.Ordinal) == true)
            .ToList();

        if (calls.Count == 0)
        {
            r.Report(Dark, rule, CheckStatus.Fail, "No DwmSetWindowAttribute call, so the title bar stays light.");
            return;
        }

        var dark = calls.FirstOrDefault(f => DarkModeAttribute.IsMatch(f.Text!));
        if (dark is not null)
            r.Report(Dark, rule, CheckStatus.Pass, $"Immersive dark mode is set in {dark.RelPath}.");
        else
            r.Report(Dark, rule, CheckStatus.Warn,
                $"DwmSetWindowAttribute is called in {calls[0].Name}, but not with the dark mode attribute (19 or 20).");
    }

    private static void DarkBackgroundRule(IReadOnlyList<SnapshotFile> files, List<CheckResult> r)
    {
        const string rule = "Dark window background";

        var docs = files
            .Where(f => f.Extension == ".xaml" && f.Text is not null)
            .Select(f => (File: f, Doc: TryParse(f)))
            .Where(x => x.Doc?.Root is not null)
            .Select(x => (x.File, Doc: x.Doc!))
            .ToList();

        // Every keyed resource across the app, so {StaticResource X} can be followed.
        var resources = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var (_, doc) in docs)
            foreach (var element in doc.Descendants())
                if (element.Attribute(XamlNs + "Key")?.Value is { } key)
                    resources.TryAdd(key, element);

        // A Window-targeted style's Background counts for windows that don't set their own.
        string? styleValue = null;
        XElement? styleElement = null;
        foreach (var (_, doc) in docs)
        {
            foreach (var style in doc.Descendants().Where(e => e.Name.LocalName == "Style" && IsWindowType(e.Attribute("TargetType")?.Value)))
            {
                var setter = style.Elements().FirstOrDefault(e =>
                    e.Name.LocalName == "Setter" && e.Attribute("Property")?.Value == "Background");
                if (setter is null) continue;

                styleValue = setter.Attribute("Value")?.Value;
                styleElement = setter.Elements().FirstOrDefault(e => e.Name.LocalName == "Setter.Value")?.Elements().FirstOrDefault();
                break;
            }
        }

        var windows = docs.Where(d =>
            d.Doc.Root!.Name.LocalName.EndsWith("Window", StringComparison.Ordinal)).ToList();

        if (windows.Count == 0)
        {
            var codeWindow = files.FirstOrDefault(f => f.Extension == ".cs" && f.Text is not null && CodeWindowClass.IsMatch(f.Text));
            if (codeWindow is not null)
                r.Report(Dark, rule, CheckStatus.Info,
                    $"Windows are built in code ({codeWindow.Name}), so their background colour isn't checked.");
            else
                r.Report(Dark, rule, CheckStatus.Warn, "No window XAML found to check.");
            return;
        }

        var light = new List<string>();
        var missing = new List<string>();
        var unresolved = new List<string>();

        foreach (var (file, doc) in windows)
        {
            var root = doc.Root!;
            Color? color;
            bool found = true;

            var attribute = root.Attribute("Background");
            var propertyElement = root.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith(".Background", StringComparison.Ordinal));

            if (attribute is not null)
                color = ResolveColor(attribute.Value, resources, 0);
            else if (propertyElement?.Elements().FirstOrDefault() is { } inner)
                color = ResolveElement(inner, resources, 0);
            else if (styleValue is not null)
                color = ResolveColor(styleValue, resources, 0);
            else if (styleElement is not null)
                color = ResolveElement(styleElement, resources, 0);
            else
            {
                color = null;
                found = false;
            }

            if (!found) missing.Add(file.Name);
            else if (color is null || color.Value.A < 128) unresolved.Add(file.Name);
            else if (Luminance(color.Value) >= 0.35) light.Add($"{file.Name} ({Hex(color.Value)})");
        }

        if (light.Count > 0 || missing.Count > 0)
        {
            var parts = new List<string>();
            if (light.Count > 0) parts.Add("light background in " + Sample(light));
            if (missing.Count > 0) parts.Add("no background set (WPF defaults to white) in " + Sample(missing));
            r.Report(Dark, rule, CheckStatus.Fail, Capitalize(string.Join("; ", parts)) + ".");
        }
        else if (unresolved.Count > 0)
        {
            r.Report(Dark, rule, CheckStatus.Warn,
                $"Couldn't work out the background colour of {Sample(unresolved)} (bound, transparent or custom).");
        }
        else
        {
            r.Report(Dark, rule, CheckStatus.Pass,
                windows.Count == 1 ? $"{windows[0].File.Name} has a dark background." : $"All {windows.Count} windows have dark backgrounds.");
        }
    }

    private static bool IsWindowType(string? targetType) =>
        targetType is not null
        && (targetType == "Window" || Regex.IsMatch(targetType, @"^\{\s*x:Type\s+(?:\w+:)?Window\s*\}$"));

    private static Color? ResolveColor(string? raw, Dictionary<string, XElement> resources, int depth)
    {
        if (raw is null || depth > 8) return null;
        raw = raw.Trim();

        var match = ResourceReference.Match(raw);
        if (match.Success)
            return resources.TryGetValue(match.Groups["key"].Value, out var element)
                ? ResolveElement(element, resources, depth + 1)
                : null;

        if (raw.StartsWith('{')) return null; // Binding or other markup extension

        try
        {
            return ColorConverter.ConvertFromString(raw) is Color color ? color : null;
        }
        catch
        {
            return null;
        }
    }

    private static Color? ResolveElement(XElement element, Dictionary<string, XElement> resources, int depth)
    {
        switch (element.Name.LocalName)
        {
            case "Color":
                return ResolveColor(element.Value, resources, depth);

            case "SolidColorBrush":
                var value = element.Attribute("Color")?.Value
                            ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == "SolidColorBrush.Color")?.Value;
                return ResolveColor(value, resources, depth);

            case "LinearGradientBrush":
            case "RadialGradientBrush":
                var stops = element.Descendants()
                    .Where(e => e.Name.LocalName == "GradientStop")
                    .Select(e => ResolveColor(e.Attribute("Color")?.Value, resources, depth))
                    .Where(c => c is not null)
                    .Select(c => c!.Value)
                    .ToList();
                if (stops.Count == 0) return null;
                return Color.FromArgb(
                    (byte)stops.Average(c => (int)c.A),
                    (byte)stops.Average(c => (int)c.R),
                    (byte)stops.Average(c => (int)c.G),
                    (byte)stops.Average(c => (int)c.B));

            default:
                return null;
        }
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ================= Dependencies =================

    /// <summary>
    /// Every project that ends up in the exe: the app project and every project it references,
    /// followed through ProjectReference items. A package in a library ships just the same.
    /// Test projects are never part of it.
    /// </summary>
    private static List<ProjectModel> ShippedProjects(RepoSnapshot s, ProjectModel main)
    {
        var (_, apps, _) = SelectProjects(s);
        var byPath = apps.ToDictionary(a => a.RelPath, StringComparer.OrdinalIgnoreCase);
        byPath[main.RelPath] = main;

        var shipped = new List<ProjectModel> { main };
        var seen = new HashSet<string>([main.RelPath], StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < shipped.Count; i++)
        {
            foreach (var reference in shipped[i].Items.Where(x => x.Type == "ProjectReference" && x.Mode != ItemMode.Remove))
            {
                var path = RepoSnapshot.Combine(shipped[i].Dir, reference.Include);
                if (byPath.TryGetValue(path, out var project) && seen.Add(project.RelPath))
                    shipped.Add(project);
            }
        }
        return shipped;
    }

    private static void DependencyRules(RepoSnapshot s, ProjectModel p, List<ProjectModel> shipped, List<ProjectModel> tests,
                                        List<CheckResult> r, ScanFacts facts)
    {
        // A package from a library is named with the project it comes from.
        string ProjectName(ProjectModel m) => Path.GetFileNameWithoutExtension(m.RelPath);
        var packages = shipped
            .SelectMany(m => m.Items
                .Where(i => i.Type == "PackageReference" && i.Mode != ItemMode.Remove)
                .Select(i => (Item: i, From: m == p ? null : ProjectName(m))))
            .GroupBy(x => (x.Item.Include.ToLowerInvariant(), x.Item.Version), x => x)
            .Select(g => g.First())
            .ToList();
        bool packagesConfig = s.Files.Any(f =>
            f.Name.Equals("packages.config", StringComparison.OrdinalIgnoreCase)
            && shipped.Any(m => RepoSnapshot.DirOf(f.RelPath).Equals(m.Dir, StringComparison.OrdinalIgnoreCase)));

        string Describe((ProjectItem Item, string? From) x) =>
            StandardsRules.Describe(x.Item) + (x.From is null ? "" : $" (in {x.From})");
        string Where(string? from) => from is null ? "" : $", in {from}";

        const string packageRule = "Third-party packages";
        var allowed = new List<(ProjectItem Item, string? From)>();
        var outdated = new List<string>();
        var blocked = new List<string>();

        foreach (var package in packages)
        {
            var (item, from) = package;
            if (!AllowedPackages.TryGetValue(item.Include, out var rule))
            {
                blocked.Add(Describe(package));
                facts.PackageUses.Add(new PackageUse(item.Include, item.Version, PackageUseState.NotListed, "not on the approved list" + Where(from)));
            }
            else if (IsOlderThan(item.Version, rule.MinVersion))
            {
                outdated.Add($"{Describe(package)} (approved from {rule.MinVersion})");
                facts.PackageUses.Add(new PackageUse(item.Include, item.Version, PackageUseState.Old, $"approved from {rule.MinVersion}" + Where(from)));
            }
            else
            {
                allowed.Add(package);
                facts.PackageUses.Add(new PackageUse(item.Include, item.Version, PackageUseState.Ok, $"approved from {rule.MinVersion}" + Where(from)));
            }
        }

        var allowedText = string.Join("; ", allowed.Select(x =>
            $"{(x.Item.Version is null ? x.Item.Include + " (version not set in the project)" : Describe(x))} ({AllowedPackages[x.Item.Include].Reason})"));

        facts.Packages = packages.Count == 0 && !packagesConfig ? PackageState.None
            : blocked.Count == 0 && outdated.Count == 0 && !packagesConfig ? PackageState.Allowed
            : PackageState.Foreign;

        if (packages.Count == 0 && !packagesConfig)
        {
            r.Report(Deps, packageRule, CheckStatus.Pass, "None: the app is all your own code plus the framework.");
        }
        else if (blocked.Count == 0 && outdated.Count == 0 && !packagesConfig)
        {
            r.Report(Deps, packageRule, CheckStatus.Info,
                $"On the approved list and current enough: {allowedText}. Watch these for security advisories.");
        }
        else
        {
            var detail = blocked.Count > 0 ? "Not on the approved list: " + Sample(blocked) + "." + EmptyListNote : "";
            if (outdated.Count > 0) detail += " Older than the approved minimum: " + string.Join("; ", outdated) + ".";
            if (packagesConfig) detail += " A packages.config file is present.";
            if (allowed.Count > 0) detail += $" Allowed as well: {allowedText}.";
            r.Report(Deps, packageRule, CheckStatus.Fail, detail.Trim());
        }

        var dlls = shipped.SelectMany(m => m.Items)
            .Where(i => i.Type == "Reference" && i.Mode != ItemMode.Remove && i.HintPath is not null).ToList();
        if (dlls.Count == 0)
            r.Report(Deps, "No loose DLL references", CheckStatus.Pass, "No references to DLL files on disk.");
        else
            r.Report(Deps, "No loose DLL references", CheckStatus.Warn,
                "References " + Sample(dlls.Select(d => d.Include.Split(',')[0].Trim())) + " from disk.");

        // Test packages don't ship in the exe, so they're listed for visibility only.
        if (tests.Count > 0)
        {
            var testItems = tests
                .SelectMany(t => t.Items)
                .Where(i => i.Type == "PackageReference" && i.Mode != ItemMode.Remove)
                .GroupBy(i => i.Include, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            foreach (var item in testItems)
                facts.PackageUses.Add(new PackageUse(item.Include, item.Version, PackageUseState.TestOnly, "not checked; never reaches the exe"));

            var testPackages = testItems.Select(StandardsRules.Describe).ToList();

            r.Report(Deps, "Test project packages", CheckStatus.Info,
                testPackages.Count == 0
                    ? "Test projects reference no NuGet packages."
                    : $"Used by tests only, not shipped: {Sample(testPackages, 6)}.");
        }
    }

    /// <summary>
    /// True only when a version is known and clearly below the minimum. Floating versions (1.11.*),
    /// ranges ([1.11.72,)) and prerelease tags are read by their leading number.
    /// </summary>
    private static bool IsOlderThan(string? version, string minimum)
    {
        if (version is null) return false;
        var match = VersionNumber.Match(version);
        if (!match.Success) return false;

        return Version.TryParse(PadVersion(match.Value), out var actual)
               && Version.TryParse(PadVersion(minimum), out var min)
               && actual < min;
    }

    private static string PadVersion(string version) => version.Contains('.') ? version : version + ".0";

    private static string Describe(ProjectItem package) =>
        package.Version is null ? package.Include : $"{package.Include} {package.Version}";

    // ================= Security =================

    private static void ElevationRules(RepoSnapshot s, IReadOnlyList<SnapshotFile> files, ProjectModel p, List<ProjectModel> shipped,
                                       List<CheckResult> r)
    {
        // Only a manifest wired up through ApplicationManifest is embedded in the exe.
        var manifestProp = p.Prop("ApplicationManifest");
        SnapshotFile? manifest = manifestProp is null ? null : s.Find(RepoSnapshot.Combine(p.Dir, manifestProp));

        if (manifestProp is not null && manifest?.Text is null)
        {
            r.Report(Security, ElevationRule, CheckStatus.Warn,
                $"ApplicationManifest points to {manifestProp}, but that file isn't in the snapshot.");
            return;
        }

        if (manifest is null)
        {
            var unused = files.FirstOrDefault(f => f.Extension == ".manifest"
                && RepoSnapshot.IsSameOrUnder(RepoSnapshot.DirOf(f.RelPath), p.Dir));
            r.Report(Security, ElevationRule, CheckStatus.Pass,
                unused is null
                    ? "No app manifest, so the app runs with the user's normal rights."
                    : $"Runs with the user's normal rights. {unused.Name} exists but isn't referenced by ApplicationManifest, so it isn't used.");
            return;
        }

        // The default manifest template has the elevated levels in comments; ignore those.
        var match = ExecutionLevel.Match(XmlComment.Replace(manifest.Text!, ""));
        var level = match.Success ? match.Groups["level"].Value : "asInvoker";

        if (level.Equals("asInvoker", StringComparison.OrdinalIgnoreCase))
        {
            r.Report(Security, ElevationRule, CheckStatus.Pass, $"{manifest.Name} runs the app with the user's normal rights (asInvoker).");
            return;
        }

        var what = level.Equals("requireAdministrator", StringComparison.OrdinalIgnoreCase)
            ? "Always asks for admin rights (requireAdministrator)"
            : $"Asks for the highest rights the user has ({level}), which means admin rights for administrators";
        r.Report(Security, ElevationRule, CheckStatus.Info, $"{what}. Confirm this app needs it.");

        // Any project that ships in the exe counts: a web package in a library runs elevated too.
        var web = shipped.SelectMany(m => m.Items)
            .Where(i => i.Type == "PackageReference" && i.Mode != ItemMode.Remove
                        && WebPackages.Contains(i.Include, StringComparer.OrdinalIgnoreCase))
            .Select(i => i.Include)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (web.Count > 0)
            r.Report(Security, "Web content with admin rights", CheckStatus.Warn,
                $"Runs elevated and uses {string.Join(" and ", web)}, so web content is handled with admin rights.");
    }

    // ================= Reproducible build =================

    private static readonly Regex CallerFilePathUse = new(
        @"\bCallerFilePath(?:Attribute)?\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Byte-identical rebuilds need every project that ends up in the exe (the app and its libraries)
    /// to carry no debug information, and no source paths baked in through [CallerFilePath].
    /// Test projects never reach the exe and are skipped.
    /// </summary>
    private static void ReproducibleRules(RepoSnapshot s, IReadOnlyList<SnapshotFile> files, List<ProjectModel> apps, List<CheckResult> r)
    {
        foreach (var project in apps.OrderBy(a => a.RelPath, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(project.RelPath);
            var rule = apps.Count == 1 ? "No debug information" : $"No debug information ({name})";
            var result = PropertyEvaluator.Evaluate(s, project, "DebugType");
            var fix = s.IsZip ? FixKind.NoDebugInfo : FixKind.None;

            switch (result.State)
            {
                case EvalState.Set when result.Value!.Equals(FixCatalog.DebugType, StringComparison.OrdinalIgnoreCase):
                    r.Report(Leaks, rule, CheckStatus.Pass, $"DebugType is {result.Value} (set in {result.Source}).");
                    break;

                case EvalState.Set:
                    r.Report(Leaks, rule, CheckStatus.Fail,
                        $"DebugType is {result.Value} (set in {result.Source}). Symbols record the source paths, so the same source built in another folder gives a different assembly.",
                        fix, project.RelPath);
                    break;

                case EvalState.NotSet:
                    r.Report(Leaks, rule, CheckStatus.Fail,
                        "DebugType isn't set, so the SDK default (portable) emits symbols that record the source paths, and the same source built in another folder gives a different assembly.",
                        fix, project.RelPath);
                    break;

                default:
                    r.Report(Leaks, rule, CheckStatus.Info,
                        $"Couldn't be determined: DebugType {result.Value} is set in {result.Source} under a condition AppConsistency can't evaluate ({result.Condition}).",
                        fix, project.RelPath);
                    break;
            }
        }

        // [CallerFilePath] bakes the caller's full source path into the code as a string, whatever DebugType says.
        const string callerRule = "No source paths in the code";
        var uses = new List<(SnapshotFile File, int Line, ProjectModel? Project)>();
        foreach (var file in files.Where(f => f.Extension == ".cs" && f.Text is not null))
        {
            if (!file.Text!.Contains("CallerFilePath", StringComparison.Ordinal)) continue;

            var masked = CodeHeaderScan.Mask(file.Text);
            var match = CallerFilePathUse.Match(masked);
            if (!match.Success) continue;

            int line = masked.AsSpan(0, match.Index).Count('\n') + 1;
            var owner = apps
                .Where(a => RepoSnapshot.IsSameOrUnder(RepoSnapshot.DirOf(file.RelPath), a.Dir))
                .OrderByDescending(a => a.Dir.Length)
                .FirstOrDefault();
            uses.Add((file, line, owner));
        }

        if (uses.Count == 0)
        {
            r.Report(Leaks, callerRule, CheckStatus.Pass, "No [CallerFilePath] in the code.");
            return;
        }

        bool Covered((SnapshotFile File, int Line, ProjectModel? Project) use) =>
            use.Project is not null
            && PropertyEvaluator.Evaluate(s, use.Project, "ContinuousIntegrationBuild") is { State: EvalState.Set } ci
            && string.Equals(ci.Value, "true", StringComparison.OrdinalIgnoreCase);

        var open = uses.Where(u => !Covered(u)).ToList();
        var locations = Sample(uses.Select(u => $"{u.File.Name} line {u.Line}"));

        if (open.Count == 0)
        {
            r.Report(Leaks, callerRule, CheckStatus.Pass,
                $"[CallerFilePath] is used ({locations}), and ContinuousIntegrationBuild is set, so the paths are rewritten to /_/.");
        }
        else
        {
            r.Report(Leaks, callerRule, CheckStatus.Warn,
                $"[CallerFilePath] puts the full source path into the exe ({Sample(open.Select(u => $"{u.File.Name} line {u.Line}"))}). " +
                "RepoBuilder rewrites it, but a plain dotnet publish doesn't. Add <ContinuousIntegrationBuild>true</ContinuousIntegrationBuild> " +
                "to that project so the zip alone builds reproducibly.");
        }
    }

    // ================= Build-location and build-time leaks =================

    // A1: the [GeneratedRegex] attribute, in any spelling.
    private static readonly Regex GeneratedRegexAttribute = new(
        @"\[\s*(?:System\.Text\.RegularExpressions\.)?GeneratedRegex(?:Attribute)?\b",
        RegexOptions.Compiled);

    // A2: the `file` modifier on a type declaration. "file" is also an ordinary identifier,
    // so only the declaration form counts.
    private static readonly Regex FileScopedType = new(
        @"^[ \t]*(?:(?:public|internal|sealed|static|abstract|partial|readonly|ref|unsafe)[ \t]+)*file[ \t]+(?:(?:sealed|static|abstract|partial|readonly|ref|unsafe)[ \t]+)*(?:class|struct|record|interface|enum)\b",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // A3: a compiler-generated file-local type name, as it appears in an assembly.
    private static readonly Regex PathHashedTypeName = new(@"F[0-9A-F]{64}__\w+", RegexOptions.Compiled);

    // B: MSBuild property functions that read the clock, the machine or a random value.
    private static readonly Regex BuildTimeValue = new(
        @"\$\((?<call>\[System\.(?:DateTime\]::(?:Now|UtcNow|Today)|Guid\]::NewGuid|IO\.Path\]::GetRandomFileName|Environment\]::(?:MachineName|UserName)))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WildcardVersion = new(
        @"<(AssemblyVersion|AssemblyFileVersion|FileVersion|Version)>[^<]*\*[^<]*</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DeterministicOff = new(
        @"<Deterministic>\s*false\s*</Deterministic>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // B: build paths that must not reach anything compiled in.
    private static readonly Regex BuildPathProperty = new(
        @"\$\((?:MSBuildProjectDirectory|MSBuildThisFileDirectory|MSBuildProjectFullPath|ProjectDir|OutDir|IntermediateOutputPath)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string GuardTarget = "RejectPathHashedTypeNames";

    /// <summary>Item attributes whose value reaches the assembly, as a name or as content.</summary>
    private static readonly string[] ItemCarriers = ["Include", "Update", "Link", "LogicalName", "Value"];

    /// <summary>
    /// Anything that puts the build folder or the build clock into the exe. These are what make
    /// the same source produce different bytes in another folder or at another time; the checks
    /// read declarations, and a pass means nothing was declared that leaks, not that a build
    /// reproduces.
    /// </summary>
    private static void LeakRules(RepoSnapshot s, IReadOnlyList<SnapshotFile> files, List<CheckResult> r)
    {
        var code = files.Where(f => f.Extension == ".cs" && f.Text is not null).ToList();
        var build = files
            .Where(f => f.Extension is ".csproj" or ".props" or ".targets" && f.Text is not null)
            .ToList();

        PathHashedTypeRule(code, r);
        GuardRule(build, r);
        CompiledOutputRule(s, r);
        BuildTimeRule(build, r);
        DeterministicRule(build, r);
        BuildPathRule(build, r);
    }

    /// <summary>
    /// A1 and A2. The compiler names a file-scoped type with a hash of its source file's full
    /// path, so the build folder ends up inside the assembly without ever appearing as text.
    /// </summary>
    private static void PathHashedTypeRule(List<SnapshotFile> code, List<CheckResult> r)
    {
        const string rule = "No path-hashed type names";

        var generated = Locate(code, GeneratedRegexAttribute);
        var handWritten = Locate(code, FileScopedType);

        if (generated.Count + handWritten.Count == 0)
        {
            r.Report(Leaks, rule, CheckStatus.Pass, "No [GeneratedRegex] and no file-scoped types.");
            return;
        }

        var parts = new List<string>();
        if (generated.Count > 0)
            parts.Add($"[GeneratedRegex] at {Sample(generated)} — replace with a static readonly Regex, or hand-written parsing");
        if (handWritten.Count > 0)
            parts.Add($"file-scoped types at {Sample(handWritten)} — make them private or internal");

        r.Report(Leaks, rule, CheckStatus.Fail,
            "The compiler names these types with a hash of their source file's full path, so the build folder " +
            "ends up in the assembly and the same source built elsewhere gives different bytes. " +
            string.Join("; ", parts) + ".");
    }

    /// <summary>A4: the build-time guard that fails the build if a path-hashed name survives.</summary>
    private static void GuardRule(List<SnapshotFile> build, List<CheckResult> r)
    {
        const string rule = "Path-hash guard";

        var host = build.FirstOrDefault(f => f.Text!.Contains($"\"{GuardTarget}\"", StringComparison.Ordinal)
                                             || f.Text!.Contains($"'{GuardTarget}'", StringComparison.Ordinal));

        if (host is not null)
        {
            r.Report(Leaks, rule, CheckStatus.Pass, $"The {GuardTarget} target is in {host.RelPath}.");
            return;
        }

        // Name the checked app's own project file, never another app's: the shallowest .csproj
        // that isn't a test project is the one the target belongs in.
        var project = build
            .Where(f => f.Extension == ".csproj")
            .Where(f => !TestProjectName.IsMatch(Path.GetFileNameWithoutExtension(RepoSnapshot.NameOf(f.RelPath))))
            .OrderBy(f => f.RelPath.Count(c => c is '/' or '\\'))
            .ThenBy(f => f.RelPath.Length)
            .FirstOrDefault();
        var into = project is null ? "the app's .csproj" : RepoSnapshot.NameOf(project.RelPath);

        r.Report(Leaks, rule, CheckStatus.Info,
            $"No {GuardTarget} target. It fails the build if a path-hashed name reaches the assembly, " +
            $"and can be copied unchanged into {into}.");
    }

    /// <summary>
    /// A3: the definitive check, when build output is in the zip at all. A compliant snapshot has
    /// none, so this normally has nothing to look at; on the built exe it belongs in RepoBuilder.
    /// </summary>
    private static void CompiledOutputRule(RepoSnapshot s, List<CheckResult> r)
    {
        const string rule = "No path-hashed names in build output";
        const long maxScan = 16L * 1024 * 1024;

        var binaries = s.Files.Where(f => f.Extension is ".dll" or ".exe").ToList();
        if (binaries.Count == 0) return;   // nothing to scan; the row would only be noise

        var hits = new List<string>();
        var skipped = 0;

        foreach (var file in binaries)
        {
            if (file.Length > maxScan) { skipped++; continue; }

            try
            {
                var bytes = s.ReadBytes(file.RelPath);
                if (bytes is null) continue;

                // The name is stored as ASCII in the metadata strings heap.
                var text = Encoding.ASCII.GetString(bytes);
                var match = PathHashedTypeName.Match(text);
                if (match.Success) hits.Add($"{file.RelPath} ({match.Value})");
            }
            catch
            {
                skipped++;
            }
        }

        if (hits.Count > 0)
            r.Report(Leaks, rule, CheckStatus.Fail,
                $"Build output in the zip carries the build folder in a type name: {Sample(hits)}.");
        else
            r.Report(Leaks, rule, CheckStatus.Info,
                $"Scanned {Plural(binaries.Count - skipped, "compiled file")} in the zip; no path-hashed names. " +
                "Build output doesn't belong in a snapshot in any case.");
    }

    /// <summary>B: a clock, a machine name or a random value read while the project is evaluated.</summary>
    private static void BuildTimeRule(List<SnapshotFile> build, List<CheckResult> r)
    {
        const string rule = "No build-time or machine values";
        var hits = Locate(build, BuildTimeValue, withMatch: true);

        if (hits.Count == 0)
            r.Report(Leaks, rule, CheckStatus.Pass, "No clock, machine or random values in the project files.");
        else
            r.Report(Leaks, rule, CheckStatus.Warn,
                $"Read while the project is evaluated: {Sample(hits)}. Flagged even where nothing in the build " +
                "consumes it, because a value that varies per run is one reference away from reaching the output.");
    }

    /// <summary>B: the compiler told to vary — a wildcard version, or determinism switched off.</summary>
    private static void DeterministicRule(List<SnapshotFile> build, List<CheckResult> r)
    {
        const string rule = "Deterministic compilation";

        var off = Locate(build, DeterministicOff);
        var wildcard = Locate(build, WildcardVersion, withMatch: true);

        if (off.Count + wildcard.Count == 0)
        {
            r.Report(Leaks, rule, CheckStatus.Pass, "Deterministic is on and no version uses a wildcard.");
            return;
        }

        var parts = new List<string>();
        if (off.Count > 0) parts.Add($"Deterministic is false at {Sample(off)}");
        if (wildcard.Count > 0) parts.Add($"a wildcard version at {Sample(wildcard)}, which changes on every build");

        r.Report(Leaks, rule, off.Count > 0 ? CheckStatus.Fail : CheckStatus.Warn,
            Capitalize(string.Join("; ", parts)) + ".");
    }

    /// <summary>
    /// B: a build path reaching something that is compiled in, and items pulled from outside the
    /// project folder, whose paths no source-root mapping covers.
    /// </summary>
    private static void BuildPathRule(List<SnapshotFile> build, List<CheckResult> r)
    {
        const string rule = "No build paths in the compilation";
        var compiled = new[] { "DefineConstants", "AssemblyMetadata", "AssemblyAttribute", "EmbeddedResource", "Resource", "Compile" };

        var intoCompilation = new List<string>();
        var fromOutside = new List<string>();

        foreach (var file in build)
        {
            var doc = TryParse(file);
            if (doc?.Root is null) continue;

            foreach (var element in doc.Root.Descendants())
            {
                var name = element.Name.LocalName;
                if (!compiled.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                var spec = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
                // Only the attributes that reach the compilation. A Condition or an Exclude is
                // build-time arithmetic: it decides whether an item exists, and lands in nothing.
                var values = element.Attributes()
                    .Where(a => ItemCarriers.Contains(a.Name.LocalName, StringComparer.OrdinalIgnoreCase))
                    .Select(a => a.Value)
                    .Append(element.HasElements ? "" : element.Value);
                var text = string.Join(" ", values);

                // Name the property and the item, so the reader can judge it without opening the file.
                if (BuildPathProperty.Match(text) is { Success: true } match)
                    intoCompilation.Add(spec is null
                        ? $"{file.Name}: <{name}> carries {match.Value}"
                        : $"{file.Name}: <{name} Include=\"{spec}\"> carries {match.Value}");

                if (spec is not null && spec.Replace('\\', '/').Contains("../", StringComparison.Ordinal))
                    fromOutside.Add($"{file.Name}: <{name} Include=\"{spec}\">");
            }
        }

        if (intoCompilation.Count + fromOutside.Count == 0)
        {
            r.Report(Leaks, rule, CheckStatus.Pass, "Nothing compiled in carries a build path, and no item comes from outside its project.");
            return;
        }

        var parts = new List<string>();
        if (intoCompilation.Count > 0)
            parts.Add($"{Sample(intoCompilation)}. An absolute item spec changes the name the resource is compiled under, " +
                      "so it moves with the build folder; a relative one doesn't");
        if (fromOutside.Count > 0)
            parts.Add($"{Sample(fromOutside)} comes from outside the project folder, where a source-root mapping doesn't reach");

        r.Report(Leaks, rule, CheckStatus.Warn, Capitalize(string.Join("; ", parts)) + ".");
    }

    /// <summary>Blanks XML comments, keeping length and line breaks.</summary>
    private static string BlankComments(string xml) =>
        XmlComment.Replace(xml, m =>
        {
            var chars = m.Value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] is not ('\r' or '\n')) chars[i] = ' ';
            return new string(chars);
        });

    /// <summary>Files and lines where a pattern matches, with comments and strings masked out.</summary>
    private static List<string> Locate(IEnumerable<SnapshotFile> files, Regex pattern, bool withMatch = false)
    {
        var hits = new List<string>();

        foreach (var file in files)
        {
            // Blanked, not removed: the line numbers in a finding have to stay true.
            var masked = file.Extension == ".cs" ? CodeHeaderScan.Mask(file.Text!) : BlankComments(file.Text!);

            foreach (Match match in pattern.Matches(masked))
            {
                int line = masked.AsSpan(0, match.Index).Count('\n') + 1;
                var where = $"{file.RelPath} line {line}";

                // Quote the "call" group when the pattern defines one: the whole match can be a
                // prefix of a longer expression, and printing that reads as unbalanced brackets.
                var quoted = match.Groups["call"].Success ? match.Groups["call"].Value : match.Value;
                hits.Add(withMatch ? $"{where} ({quoted.Trim()})" : where);
            }
        }

        return hits;
    }

    // ================= Code =================

    private static void CodeRules(IReadOnlyList<SnapshotFile> files, List<CheckResult> r)
    {
        const string rule = "Single-file safe paths";
        var hits = files
            .Where(f => f.Extension == ".cs" && f.Text is not null && AssemblyLocationUse.IsMatch(f.Text))
            .Select(f => f.Name)
            .ToList();

        if (hits.Count == 0)
            r.Report(Code, rule, CheckStatus.Pass, "No lookups of an assembly's Location.");
        else
            r.Report(Code, rule, CheckStatus.Warn,
                $"{Sample(hits)} read an assembly's Location, which is empty in single-file apps. Use AppContext.BaseDirectory.");
    }

    /// <summary>
    /// Where the app writes. Never under AppData: files written there are left behind for good. Settings
    /// belong in AssemblyName.json next to the exe; anything else written there, and temp files, are
    /// listed so you see them. Read from the code; nothing is run.
    /// </summary>
    private static void WriteRules(IReadOnlyList<SnapshotFile> files, ProjectModel? project, List<CheckResult> r)
    {
        var assembly = project?.Prop("AssemblyName")
                       ?? (project is null ? "" : Path.GetFileNameWithoutExtension(project.RelPath));
        // Code the project leaves out of its build (<Compile Remove="Tests/**" />) never runs.
        var removed = project is null ? []
            : project.Items
                .Where(i => i.Mode == ItemMode.Remove && i.Type == "Compile")
                .SelectMany(i => i.Include.Split(';'))
                .Select(x => x.Trim())
                .Where(x => x.Length > 0 && !x.Contains("$(", StringComparison.Ordinal))
                .Select(x => GlobToRegex(RepoSnapshot.Combine(project.Dir, x)))
                .ToList();
        var compiled = files.Where(f => !removed.Any(x => x.IsMatch(f.RelPath))).ToList();
        var sites = WriteScan.Scan(compiled, assembly);
        var settings = assembly.Length == 0 ? "AssemblyName.json" : assembly + ".json";

        string Line(WriteSite w) => $"\n  {w.File} line {w.Line}: {w.Call} → {w.Via}";

        var appData = sites.Where(w => w.Target == WriteTarget.AppData).ToList();
        var temp = sites.Where(w => w.Target == WriteTarget.Temp).ToList();
        var tempNote = temp.Count == 0 ? ""
            : $"\nAlso writes to the temp folder ({Plural(temp.Count, "place")}), listed for information:" + string.Concat(temp.Select(Line));

        const string appDataRule = "No AppData";
        if (appData.Count > 0)
            r.Report(Code, appDataRule, CheckStatus.Fail,
                $"Writes under the user's AppData in {Plural(appData.Count, "place")}. Files there are left behind when the app " +
                $"is gone. Store settings in {settings} next to the exe (AppContext.BaseDirectory) instead." +
                string.Concat(appData.Select(Line)) + tempNote);
        else if (temp.Count > 0)
            r.Report(Code, appDataRule, CheckStatus.Info,
                "Nothing written under AppData. Writes to the temp folder, which is up to the app to clean up:" + string.Concat(temp.Select(Line)));
        else
            r.Report(Code, appDataRule, CheckStatus.Pass, "Nothing written under AppData or the temp folder.");

        const string exeRule = "Writes next to the exe";
        var exe = sites.Where(w => w.Target == WriteTarget.ExeFolder).ToList();
        var other = exe.Where(w => !w.Standard).ToList();
        if (other.Count > 0)
            r.Report(Code, exeRule, CheckStatus.Info,
                $"Writes next to the exe besides {settings}, listed for information. A read-only folder (Program Files) makes these fail." +
                string.Concat(other.Select(Line)));
        else if (exe.Count > 0)
            r.Report(Code, exeRule, CheckStatus.Pass, $"Only {settings}, the settings standard.");
        else
            r.Report(Code, exeRule, CheckStatus.Pass, "Nothing written next to the exe.");
    }

    // ================= Helpers =================

    private static XDocument? TryParse(SnapshotFile file)
    {
        try
        {
            return XDocument.Parse(file.Text!.TrimStart('\uFEFF', ' ', '\t', '\r', '\n'));
        }
        catch
        {
            return null;
        }
    }

    private static void Report(this List<CheckResult> results, string category, string rule, CheckStatus status, string detail,
                               FixKind fix = FixKind.None, string? fixProject = null)
        => results.Add(new CheckResult(category, rule, status, detail) { Fix = fix, FixProject = fixProject });

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    private static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Sample(IEnumerable<string> items, int max = 4)
    {
        var list = items.ToList();
        if (list.Count <= max) return string.Join(", ", list);
        return string.Join(", ", list.Take(max)) + $" and {list.Count - max} more";
    }
}
