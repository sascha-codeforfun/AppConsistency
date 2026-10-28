using System.Text.RegularExpressions;

namespace AppConsistency.Core;

/// <summary>Git and GitHub files in a snapshot. Folders (.git/, .github/) are listed once, with what's in them.</summary>
public sealed record GitFindings(List<string> Files, List<(string Folder, int FileCount)> Folders)
{
    public int Count => Files.Count + Folders.Count;
}

/// <summary>
/// What belongs in a snapshot zip. Your build chain builds straight from the zip and RepoPublisher
/// pushes its files through the GitHub API, so anything that is neither source nor used by the
/// build is dead weight: git and GitHub files always, everything else when you say so.
/// </summary>
public static class FileHygiene
{
    // ---------------- Git files ----------------

    private static readonly HashSet<string> GitFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gitignore", ".gitattributes", ".gitmodules", ".gitkeep"
    };

    private static readonly HashSet<string> GitFolderNames = new(StringComparer.OrdinalIgnoreCase) { ".git", ".github" };

    /// <summary>
    /// Every git or GitHub file at any depth: .gitignore, .gitattributes, .gitmodules, .gitkeep, and
    /// whole .git/ and .github/ folders. The "remove git files" fix removes exactly this.
    /// </summary>
    public static GitFindings GitFiles(RepoSnapshot s)
    {
        var folders = s.Directories
            .Where(d => GitFolderNames.Contains(RepoSnapshot.NameOf(d)))
            .ToList();
        folders = folders
            .Where(d => !folders.Any(o => o != d && RepoSnapshot.IsSameOrUnder(d, o)))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var files = s.Files
            .Where(f => GitFileNames.Contains(f.Name) && !folders.Any(d => RepoSnapshot.IsSameOrUnder(f.RelPath, d)))
            .Select(f => f.RelPath)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var withCounts = folders
            .Select(d => (d, s.Files.Count(f => RepoSnapshot.IsSameOrUnder(f.RelPath, d))))
            .ToList();

        return new GitFindings(files, withCounts);
    }

    public static bool IsGitPath(string relPath) =>
        GitFileNames.Contains(RepoSnapshot.NameOf(relPath))
        || relPath.Split('/').SkipLast(1).Any(GitFolderNames.Contains);

    // ---------------- Private files ----------------

    /// <summary>
    /// Your own working notes: kept in every zip you build and back up, left out only of the copy
    /// staged for publishing. HANDOVER.md is not one of them: it is content an app hands on, and ships.
    /// </summary>
    public static IReadOnlyList<string> PrivateNames => Standard.Current.PrivateFiles;

    public static bool IsPrivate(string relPath) => Standard.Current.IsPrivate(relPath);

    /// <summary>Every private file in the snapshot, at any depth (a core and a UI project may each have one).</summary>
    public static List<string> PrivateFiles(RepoSnapshot s) =>
        s.Files
            .Where(f => IsPrivate(f.RelPath) && !f.RelPath.Split('/').SkipLast(1).Any(OutputFolders.Contains))
            .Select(f => f.RelPath)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Every README line that links to or names a private file that is in the snapshot, as
    /// "README.md line 3: Docs/NOTES.md". A copy staged for publishing leaves those files out, so
    /// these lines would point at nothing, and tell the public the notes exist.
    /// </summary>
    public static List<string> PrivateMentions(RepoSnapshot s, IReadOnlyList<string> privateFiles)
    {
        var hits = new List<string>();
        if (privateFiles.Count == 0) return hits;

        foreach (var readmePath in ReadmeCollector.Find(s))
        {
            if (s.Find(readmePath)?.Text is not { } text) continue;
            var lines = text.Replace("\r\n", "\n").Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                var found = new List<string>();

                foreach (var target in LinkTargets(lines[i]))
                    if (Resolve(s, readmePath, target) is { } path && IsPrivate(path)) found.Add(path);

                foreach (var name in privateFiles.Select(RepoSnapshot.NameOf).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (found.Any(f => RepoSnapshot.NameOf(f).Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                    if (NamesFile(lines[i], name))
                        found.Add(privateFiles.First(p => RepoSnapshot.NameOf(p).Equals(name, StringComparison.OrdinalIgnoreCase)));
                }

                foreach (var path in found.Distinct(StringComparer.OrdinalIgnoreCase))
                    hits.Add($"{readmePath} line {i + 1}: {path}");
            }
        }

        return hits;
    }

    // ---------------- Whitelist ----------------

    /// <summary>The whitelist in words, for the removal picker.</summary>
    public static string WhitelistText =>
        string.Join(", ", Standard.Current.Whitelist) + ", the README and the license. Files the project uses in its build " +
        "(resources, content, copied files) count as listed too.";


    // Build output and tool folders have their own rule ("No build output").
    private static readonly HashSet<string> OutputFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", "node_modules"
    };

    // Item types whose files the build reads. None counts only when it is copied to the output.
    private static readonly HashSet<string> BuildItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compile", "Page", "ApplicationDefinition", "Resource", "EmbeddedResource", "Content",
        "SplashScreen", "AdditionalFiles", "None"
    };

    // Properties that name a file the build reads.
    private static readonly string[] BuildFileProperties = ["ApplicationIcon", "ApplicationManifest", "Win32Resource"];

    // MSBuild prefixes that just mean "this project's folder".
    private static readonly Regex ProjectDirPrefix = new(
        @"^\$\((?:MSBuildProjectDirectory|MSBuildThisFileDirectory|ProjectDir)\)[\\/]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsListed(string relPath) => Standard.Current.IsWhitelisted(relPath);

    /// <summary>
    /// Files that aren't on the whitelist and that no project uses in its build. Files other rules
    /// already deal with are left to them: build output, solution files, IDE files, git files,
    /// readmes, license files and your private files.
    /// </summary>
    public static List<SnapshotFile> Unlisted(RepoSnapshot s, IReadOnlyCollection<ProjectModel> projects, ISet<string> handledElsewhere)
    {
        var used = BuildReferenced(s, projects);

        return s.Files
            .Where(f => !IsListed(f.RelPath))
            .Where(f => !IsPrivate(f.RelPath))
            .Where(f => !handledElsewhere.Contains(f.RelPath))
            .Where(f => !IsGitPath(f.RelPath))
            .Where(f => !f.RelPath.Split('/').SkipLast(1).Any(OutputFolders.Contains))
            .Where(f => !used.Contains(f.RelPath))
            .OrderBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------- README references ----------------

    // ![alt](path "title"), [text](path), [label]: path, and src= / href= in inline HTML.
    private static readonly Regex MarkdownLink = new(@"\]\(\s*<?(?<path>[^)\s>]+)>?(?:\s+[""'][^""']*[""'])?\s*\)", RegexOptions.Compiled);
    private static readonly Regex ReferenceLink = new(@"^\s{0,3}\[[^\]]+\]:\s*<?(?<path>\S+?)>?(?:\s|$)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex HtmlLink = new(@"\b(?:src|href)\s*=\s*[""'](?<path>[^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Scheme = new(@"^[a-z][a-z0-9+.\-]*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Marks the unlisted files the README uses. A file the README links to or embeds counts, by its
    /// path relative to the README. A file it only names in the text counts too: when several files
    /// share that name, the one closest to the README (same folder first, then the shallowest).
    /// Every other file with the same name as a used one is a duplicate and keeps warning, with the
    /// file the README does use named beside it.
    /// </summary>
    public static List<UnlistedFile> MarkReadmeUse(RepoSnapshot s, IReadOnlyList<SnapshotFile> unlisted)
    {
        var readmes = ReadmeCollector.Find(s)
            .Select(p => s.Find(p))
            .Where(f => f?.Text is not null)
            .Select(f => f!)
            .ToList();

        var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var readme in readmes)
            foreach (var target in LinkTargets(readme.Text!))
                if (Resolve(s, readme.RelPath, target) is { } path) linked.Add(path);

        var used = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);   // path -> duplicate of (null when used)

        foreach (var group in unlisted.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var files = group.ToList();
            var chosen = files.Where(f => linked.Contains(f.RelPath)).Select(f => f.RelPath).ToList();

            if (chosen.Count == 0 && readmes.Any(r => NamesFile(r.Text!, group.Key)))
            {
                chosen.Add(files
                    .OrderByDescending(f => readmes.Any(r => RepoSnapshot.DirOf(r.RelPath)
                        .Equals(RepoSnapshot.DirOf(f.RelPath), StringComparison.OrdinalIgnoreCase)))
                    .ThenBy(f => f.RelPath.Count(c => c == '/'))
                    .ThenBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase)
                    .First().RelPath);
            }

            if (chosen.Count == 0) continue;
            foreach (var file in files)
                used[file.RelPath] = chosen.Contains(file.RelPath, StringComparer.OrdinalIgnoreCase) ? null : chosen[0];
        }

        return unlisted
            .Select(f => used.TryGetValue(f.RelPath, out var duplicateOf)
                ? new UnlistedFile(f.RelPath, f.Length, ReadmeUses: duplicateOf is null, DuplicateOf: duplicateOf)
                : new UnlistedFile(f.RelPath, f.Length))
            .ToList();
    }

    private static IEnumerable<string> LinkTargets(string text) =>
        MarkdownLink.Matches(text).Concat(ReferenceLink.Matches(text)).Concat(HtmlLink.Matches(text))
            .Select(m => m.Groups["path"].Value);

    /// <summary>A link target as a snapshot path, or null for web links, anchors and paths that aren't there.</summary>
    private static string? Resolve(RepoSnapshot s, string readmePath, string target)
    {
        if (Scheme.IsMatch(target) || target.StartsWith('#') || target.StartsWith("//", StringComparison.Ordinal)) return null;

        int cut = target.IndexOfAny(['#', '?']);
        if (cut >= 0) target = target[..cut];
        if (target.Length == 0) return null;
        try { target = Uri.UnescapeDataString(target); } catch { /* keep it as written */ }

        // A leading slash means the repo root: the wrapper folder, or the zip root when there is none.
        var baseDir = target.StartsWith('/') ? s.WrapperFolder ?? "" : RepoSnapshot.DirOf(readmePath);
        var path = RepoSnapshot.Combine(baseDir, target.TrimStart('/'));
        return s.Find(path)?.RelPath;
    }

    private static readonly Regex WebAddress = new(@"\b[a-z][a-z0-9+.\-]*://\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The file name appears in the text as a name of its own, not as part of a longer one, and not
    /// inside a web address (a notes.md on some website isn't this repo's notes.md).
    /// </summary>
    private static bool NamesFile(string text, string fileName) =>
        Regex.IsMatch(WebAddress.Replace(text, " "),
            $@"(?<![\w.\-]){Regex.Escape(fileName)}(?![\w\-]|\.\w)", RegexOptions.IgnoreCase);

    /// <summary>Snapshot files that some project reads while building: its items, wildcards included, and named files.</summary>
    public static HashSet<string> BuildReferenced(RepoSnapshot s, IReadOnlyCollection<ProjectModel> projects)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            foreach (var item in project.Items)
            {
                if (item.Mode == ItemMode.Remove || !BuildItemTypes.Contains(item.Type)) continue;
                if (item.Type.Equals("None", StringComparison.OrdinalIgnoreCase) && !item.Copies) continue;

                foreach (var part in item.Include.Split(';'))
                    Mark(s, project.Dir, part, used);
            }

            foreach (var property in BuildFileProperties)
                if (project.Prop(property) is { } value)
                    Mark(s, project.Dir, value, used);

            // The Web SDK takes wwwroot and the project's .json and .config files as content by itself.
            if (project.Sdk?.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) == true
                || project.Sdk?.StartsWith("Microsoft.NET.Sdk.Razor", StringComparison.OrdinalIgnoreCase) == true
                || project.Sdk?.StartsWith("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase) == true)
            {
                var wwwroot = project.Dir.Length == 0 ? "wwwroot" : project.Dir + "/wwwroot";
                foreach (var file in s.Files)
                {
                    if (!RepoSnapshot.IsSameOrUnder(RepoSnapshot.DirOf(file.RelPath), project.Dir)) continue;
                    if (RepoSnapshot.IsSameOrUnder(file.RelPath, wwwroot)
                        || file.Extension is ".json" or ".config" or ".cshtml" or ".razor")
                        used.Add(file.RelPath);
                }
            }
        }

        return used;
    }

    private static void Mark(RepoSnapshot s, string projectDir, string spec, HashSet<string> used)
    {
        var item = ProjectDirPrefix.Replace(spec.Trim(), "");
        if (item.Length == 0 || item.Contains("$(", StringComparison.Ordinal) || item.Contains("@(", StringComparison.Ordinal)) return;

        var path = RepoSnapshot.Combine(projectDir, item);
        if (path.Contains('*') || path.Contains('?'))
        {
            var glob = GlobToRegex(path);
            foreach (var file in s.Files)
                if (glob.IsMatch(file.RelPath)) used.Add(file.RelPath);
        }
        else if (s.Find(path) is { } file)
        {
            used.Add(file.RelPath);
        }
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
}
