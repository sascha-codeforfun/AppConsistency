using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AppConsistency.Core;

public enum EvalState
{
    NotSet,
    Set,
    Unknown
}

/// <param name="Value">The effective value when Set; the value under the unreadable condition when Unknown.</param>
/// <param name="Source">File that set the value (or holds the unreadable condition).</param>
/// <param name="Condition">The condition that couldn't be evaluated, when Unknown.</param>
public sealed record EvalResult(EvalState State, string? Value, string? Source, string? Condition);

/// <summary>
/// Works out a property's effective value for a Release build, the way MSBuild would for the files
/// AppConsistency can see: Directory.Build.props from the repo root down, then the project; conditions
/// on the PropertyGroup and on the element both count; the last assignment that applies wins.
/// Conditions other than simple Configuration / Configuration|Platform tests are reported as Unknown.
/// </summary>
public static class PropertyEvaluator
{
    private const string BuildConfiguration = "Release";
    private const string BuildPlatform = "AnyCPU";

    private static readonly Regex ConfigTest = new(
        @"^\s*['""]\s*\$\(Configuration\)\s*['""]\s*(?<op>==|!=)\s*['""](?<value>[^'""]*)['""]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ConfigPlatformTest = new(
        @"^\s*['""]\s*\$\(Configuration\)\s*\|\s*\$\(Platform\)\s*['""]\s*(?<op>==|!=)\s*['""](?<config>[^'""|]*)\|(?<platform>[^'""]*)['""]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static EvalResult Evaluate(RepoSnapshot snapshot, ProjectModel project, string property)
    {
        var files = snapshot.Files
            .Where(f => f.Name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
                        && f.Text is not null
                        && RepoSnapshot.IsSameOrUnder(project.Dir, RepoSnapshot.DirOf(f.RelPath)))
            .OrderBy(f => f.RelPath.Length)
            .ToList();
        if (snapshot.Find(project.RelPath) is { Text: not null } projectFile) files.Add(projectFile);

        var result = new EvalResult(EvalState.NotSet, null, null, null);

        foreach (var file in files)
        {
            XDocument doc;
            try { doc = XDocument.Parse(file.Text!.TrimStart('\uFEFF', ' ', '\t', '\r', '\n')); }
            catch { continue; }
            if (doc.Root is null) continue;

            foreach (var element in doc.Root.Descendants().Where(e => e.Name.LocalName.Equals(property, StringComparison.OrdinalIgnoreCase)))
            {
                var group = element.Parent;
                if (group?.Name.LocalName != "PropertyGroup") continue;

                // Choose/When blocks aren't evaluated; anything nested in one is Unknown.
                if (group.Parent != doc.Root)
                {
                    result = new EvalResult(EvalState.Unknown, element.Value.Trim(), file.RelPath, "inside a Choose/When block");
                    continue;
                }

                var applies = And(Applies(group.Attribute("Condition")?.Value), Applies(element.Attribute("Condition")?.Value));
                if (applies == false) continue;

                var condition = group.Attribute("Condition")?.Value ?? element.Attribute("Condition")?.Value;
                result = applies == true
                    ? new EvalResult(EvalState.Set, element.Value.Trim(), file.RelPath, null)
                    : new EvalResult(EvalState.Unknown, element.Value.Trim(), file.RelPath, condition);
            }
        }

        return result;
    }

    private static bool? And(bool? a, bool? b) =>
        a == false || b == false ? false : a == true && b == true ? true : null;

    /// <summary>true / false when the condition is understood for a Release|AnyCPU build, null otherwise.</summary>
    private static bool? Applies(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;

        var config = ConfigTest.Match(condition);
        if (config.Success)
            return Compare(config.Groups["op"].Value, config.Groups["value"].Value.Equals(BuildConfiguration, StringComparison.OrdinalIgnoreCase));

        var both = ConfigPlatformTest.Match(condition);
        if (both.Success)
        {
            var platform = both.Groups["platform"].Value.Replace(" ", "");
            bool equal = both.Groups["config"].Value.Equals(BuildConfiguration, StringComparison.OrdinalIgnoreCase)
                         && (platform.Equals(BuildPlatform, StringComparison.OrdinalIgnoreCase)
                             || platform.Equals("Any CPU", StringComparison.OrdinalIgnoreCase));
            return Compare(both.Groups["op"].Value, equal);
        }

        return null;
    }

    private static bool Compare(string op, bool equal) => op == "==" ? equal : !equal;
}
