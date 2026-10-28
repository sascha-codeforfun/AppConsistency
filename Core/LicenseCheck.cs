using System.Text.RegularExpressions;

namespace AppConsistency.Core;

/// <summary>What the license check found in one snapshot.</summary>
/// <param name="Files">Every license file, shallowest first.</param>
/// <param name="Checked">The file the verdict is about: the first one at the shallowest level.</param>
/// <param name="IsMit">The checked file is the standard MIT text with a valid copyright line.</param>
/// <param name="Detail">One line for the finding.</param>
public sealed record LicenseVerdict(IReadOnlyList<string> Files, string? Checked, bool IsMit, string Detail);

/// <summary>
/// Recognises the standard MIT license, word for word. Only the copyright line varies: a year or
/// an upward span (2026, 2026-2027) and a name of two or more words, any name. Whitespace and line
/// endings don't count, since editors re-wrap the paragraphs; every word and mark does.
/// </summary>
public static class LicenseCheck
{
    // Build output and tool folders: a license in there isn't the repo's own.
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules"
    };

    private static readonly Regex LicenseName = new(
        @"^(?:licen[cs]e|copying)(?:\.(?:md|txt))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string Title = "MIT License";

    /// <summary>The standard text after the copyright line, whitespace collapsed.</summary>
    private const string Body =
        "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and " +
        "associated documentation files (the \"Software\"), to deal in the Software without restriction, " +
        "including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, " +
        "and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, " +
        "subject to the following conditions: " +
        "The above copyright notice and this permission notice shall be included in all copies or substantial " +
        "portions of the Software. " +
        "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT " +
        "LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. " +
        "IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, " +
        "WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE " +
        "SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.";

    private const string BodyStart = "Permission is hereby granted, free of charge,";

    // The copyright line: "Copyright (c) " then the year or span, then the name.
    private static readonly Regex CopyrightLine = new(
        @"^Copyright \(c\) (?<years>\S+) (?<name>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex Years = new(@"^(?<from>\d{4})(?:-(?<to>\d{4}))?$", RegexOptions.Compiled);

    // Two or more words of letters; a word may carry hyphens, apostrophes or a full stop (Jean-Luc, O'Neil, J.).
    private static readonly Regex FullName = new(
        @"^[\p{L}][\p{L}\p{M}'’.\-]*(?: [\p{L}][\p{L}\p{M}'’.\-]*)+$",
        RegexOptions.Compiled);

    private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);

    public static bool IsLicenseName(string fileName) => LicenseName.IsMatch(fileName);

    public static List<string> Find(RepoSnapshot s) =>
        s.Files
            .Where(f => IsLicenseName(f.Name) && !InSkippedFolder(f.RelPath))
            .Select(f => f.RelPath)
            .OrderBy(p => p.Count(c => c == '/'))
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static LicenseVerdict Inspect(RepoSnapshot s)
    {
        var files = Find(s);
        if (files.Count == 0)
            return new LicenseVerdict(files, null, false, "No license file found. Expected the standard MIT license as LICENSE.txt.");

        var checkedPath = files[0];
        var also = files.Count > 1 ? $" Also found: {string.Join(", ", files.Skip(1))}." : "";
        var text = s.Find(checkedPath)?.Text;

        if (text is null)
            return new LicenseVerdict(files, checkedPath, false, $"{checkedPath}: couldn't be read as text.{also}");

        var (isMit, what) = Judge(text);
        return new LicenseVerdict(files, checkedPath, isMit, $"{checkedPath}: {what}.{also}");
    }

    /// <summary>
    /// The verdict on one license text: standard MIT ("MIT, 2026 Jane Doe"), MIT that isn't
    /// standard (and which part differs), or not MIT.
    /// </summary>
    public static (bool IsMit, string What) Judge(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(l => Space.Replace(l, " ").Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var flat = Space.Replace(text, " ").Trim().TrimStart('﻿');
        bool looksMit = flat.Contains(BodyStart, StringComparison.Ordinal)
                        || (lines.Count > 0 && lines[0].Equals(Title, StringComparison.OrdinalIgnoreCase));
        if (!looksMit) return (false, "not MIT");

        var problems = new List<string>();
        if (lines.Count == 0 || lines[0].TrimStart('﻿') != Title)
            problems.Add("title isn't \"MIT License\"");

        // The copyright line is the second non-empty line, between the title and the grant.
        var copyright = lines.Count > 1 ? lines[1] : "";
        var (holder, copyrightProblem) = ReadCopyright(copyright);
        if (copyrightProblem is not null) problems.Add(copyrightProblem);

        // Everything after the copyright line must be the standard text.
        var rest = string.Join(" ", lines.Skip(2));
        if (rest != Body) problems.Add(BodyDifference(rest));

        return problems.Count == 0
            ? (true, $"MIT, {holder}")
            : (false, $"MIT but not standard ({string.Join("; ", problems)})");
    }

    /// <summary>"2026 Jane Doe" from a valid copyright line, or why the line doesn't count.</summary>
    private static (string? Holder, string? Problem) ReadCopyright(string line)
    {
        if (!line.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase))
            return (null, "no copyright line after the title");

        var match = CopyrightLine.Match(line);
        if (!match.Success)
            return (null, line.Contains('©') ? "copyright line uses © instead of (c)" : "copyright line isn't \"Copyright (c) <year> <name>\"");

        var years = Years.Match(match.Groups["years"].Value);
        if (!years.Success)
            return (null, $"copyright year \"{match.Groups["years"].Value}\" isn't a year or a span like 2026-2027");

        int from = int.Parse(years.Groups["from"].Value);
        if (from is < 1970 or > 2100)
            return (null, $"copyright year {from} is out of range");
        if (years.Groups["to"].Success)
        {
            int to = int.Parse(years.Groups["to"].Value);
            if (to <= from) return (null, $"copyright span {from}-{to} doesn't go upwards");
            if (to > 2100) return (null, $"copyright year {to} is out of range");
        }

        var name = match.Groups["name"].Value.Trim();
        if (!FullName.IsMatch(name))
            return (null, name.Contains('[') || name.Contains('<')
                ? $"copyright name is still a placeholder ({name})"
                : $"copyright name \"{name}\" isn't a full name of two or more words");

        return ($"{match.Groups["years"].Value} {name}", null);
    }

    /// <summary>Where the text after the copyright line first leaves the standard wording.</summary>
    private static string BodyDifference(string actual)
    {
        if (actual.Length == 0) return "the license text after the copyright line is missing";

        int i = 0;
        while (i < actual.Length && i < Body.Length && actual[i] == Body[i]) i++;

        if (i == Body.Length) return "text added after the standard wording";
        if (i == actual.Length) return "the standard wording stops early";

        int start = Math.Max(0, actual.LastIndexOf(' ', Math.Max(0, i - 1)) + 1);
        var found = actual[start..Math.Min(actual.Length, start + 32)].Trim();
        return $"body text differs from the standard wording at \"{found}…\"";
    }

    private static bool InSkippedFolder(string relPath)
    {
        var parts = relPath.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
            if (SkipFolders.Contains(parts[i])) return true;
        return false;
    }
}
