using System.Text.RegularExpressions;

namespace AppConsistency.Core;

/// <summary>
/// Applies the exemptions for one app during its scan. A finding an exemption covers turns into an
/// Exempt row: it shows the reason above the original detail and no longer makes the app warn or
/// fail. Only findings that warn or fail can be exempted. An exemption that covers nothing is
/// reported as Info, so the list doesn't keep entries nobody needs.
/// </summary>
public sealed class ExemptionMatcher
{
    private readonly List<ExemptionEntry> _entries;
    private readonly Dictionary<ExemptionEntry, int> _uses = new();
    private readonly HashSet<string> _siteRules = new(StringComparer.OrdinalIgnoreCase);

    public ExemptionMatcher(string appName)
    {
        _entries = Standard.Current.Exemptions
            .Where(e => e.App.Equals(appName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var e in _entries) _uses[e] = 0;
    }

    /// <summary>Findings covered per exemption, by its key; for the Settings window.</summary>
    public Dictionary<string, int> Uses => _uses.ToDictionary(u => u.Key.Key, u => u.Value);

    /// <summary>The rule name as reported, or a project's own row of it: "No debug information (Core)".</summary>
    public static bool RuleMatches(string entryRule, string rule) =>
        rule.Equals(entryRule, StringComparison.OrdinalIgnoreCase)
        || rule.StartsWith(entryRule + " (", StringComparison.OrdinalIgnoreCase);

    /// <summary>The end of the path at a folder boundary: Services/ConsolePreset.cs matches App/Services/ConsolePreset.cs.</summary>
    public static bool PathMatches(string pattern, string relPath)
    {
        var p = Normalize(pattern);
        var f = Normalize(relPath);
        return p.Length > 0 && (f.Equals(p, StringComparison.OrdinalIgnoreCase)
                                || f.EndsWith("/" + p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A whole word of the write's origin: ApplicationData matches "ApplicationData (Roaming)", not LocalApplicationData.</summary>
    public static bool OriginMatches(string origin, string via) =>
        Regex.IsMatch(via, $@"(?<![A-Za-z]){Regex.Escape(origin.Trim())}(?![A-Za-z])", RegexOptions.IgnoreCase);

    /// <summary>Exemptions covering one write; the rule is then handled site by site, not as a whole.</summary>
    public List<ExemptionEntry> ForSite(string rule, WriteSite site)
    {
        _siteRules.Add(rule);
        var hits = _entries.Where(e =>
                RuleMatches(e.Rule, rule)
                && (e.File is null || PathMatches(e.File, site.File))
                && (e.Method is null || string.Equals(e.Method, site.Method, StringComparison.Ordinal))
                && (e.Origin is null || OriginMatches(e.Origin, site.Via)))
            .ToList();
        foreach (var e in hits) _uses[e]++;
        return hits;
    }

    /// <summary>Every finding that warns or fails and that an exemption covers becomes Exempt.</summary>
    public void ApplyTo(List<CheckResult> results)
    {
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (r.Status is not (CheckStatus.Warn or CheckStatus.Fail) || r.ExemptReason is not null) continue;
            if (_siteRules.Any(rule => RuleMatches(rule, r.Rule))) continue;

            // Method and Origin only exist for writes, so an entry with them can't cover any other rule.
            var hits = _entries.Where(e =>
                    RuleMatches(e.Rule, r.Rule) && e.Method is null && e.Origin is null
                    && (e.File is null || MentionsFile(r.Detail, e.File)))
                .ToList();
            if (hits.Count == 0) continue;

            foreach (var e in hits) _uses[e]++;
            results[i] = Exempt(r, hits, r.Detail);
        }
    }

    /// <summary>One Info row per exemption that covered nothing in this scan.</summary>
    public void ReportUnused(List<CheckResult> results)
    {
        foreach (var e in _entries.Where(e => _uses[e] == 0))
            results.Add(new CheckResult("Exemptions", "Unused exemption", CheckStatus.Info,
                $"{e.Describe} matches no finding that warns or fails: the rule passes now, or a name is spelled " +
                "differently. Remove it in Settings once it's no longer needed."));
    }

    public static string Reasons(IEnumerable<ExemptionEntry> hits) =>
        string.Join(" · ", hits.Select(h => h.Reason).Distinct(StringComparer.Ordinal));

    public static CheckResult Exempt(CheckResult r, IEnumerable<ExemptionEntry> hits, string detail)
    {
        var reasons = Reasons(hits);
        return new CheckResult(r.Category, r.Rule, CheckStatus.Info, $"Exempt: {reasons}\n{detail}") { ExemptReason = reasons };
    }

    private static bool MentionsFile(string detail, string file)
    {
        var path = Normalize(file);
        if (path.Length == 0) return false;
        var pattern = @"(?<![\w.\-/\\])(?:[\w.\-]+[/\\])*" + Regex.Escape(path).Replace("/", @"[/\\]") + @"(?![\w/\\])";
        return Regex.IsMatch(detail, pattern, RegexOptions.IgnoreCase);
    }

    private static string Normalize(string path)
    {
        var p = path.Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.TrimStart('/');
    }
}
