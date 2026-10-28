using System.Text;
using System.Text.RegularExpressions;

namespace AppConsistency.Core;

public enum EditKind
{
    Added,
    Changed,
    Unchanged,
    Blocked
}

public sealed record EditOutcome(EditKind Kind, string Line, string? OldValue = null, string? Reason = null);

/// <summary>
/// Minimal text edits to a .csproj that leave everything else as it was: no reformatting,
/// comments kept, indentation and line endings matched. Only unconditional PropertyGroups are touched.
/// </summary>
public sealed class CsprojEditor
{
    private static readonly Regex Comment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex GroupOpen = new(
        @"<PropertyGroup(?<attrs>\s[^>]*?)?\s*(?<self>/)?>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ChildIndent = new(@"\n(?<ws>[ \t]*)<", RegexOptions.Compiled);

    private sealed record Group(int Start, int BodyStart, int BodyEnd, bool Conditional, bool SelfClosing);

    public CsprojEditor(string text) => Text = text;

    public string Text { get; private set; }

    private string NewLine => Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>
    /// Sets an unconditional property: changes its value where it already is, or adds it at the end of the
    /// first PropertyGroup without a condition. Refuses when it's only set conditionally.
    /// </summary>
    public EditOutcome SetProperty(string name, string value)
    {
        var line = $"<{name}>{value}</{name}>";
        var masked = Mask(Text);
        var groups = FindGroups(masked);

        var escaped = Regex.Escape(name);
        var property = new Regex($@"<{escaped}(?<attrs>\s[^>]*?)?>(?<value>[^<]*)</{escaped}\s*>", RegexOptions.IgnoreCase);

        var hits = new List<(Match Match, bool Conditional)>();
        foreach (var group in groups.Where(g => !g.SelfClosing))
        {
            foreach (Match match in property.Matches(masked, group.BodyStart))
            {
                if (match.Index + match.Length > group.BodyEnd) break;
                bool conditional = group.Conditional
                                   || match.Groups["attrs"].Value.Contains("Condition", StringComparison.OrdinalIgnoreCase);
                hits.Add((match, conditional));
            }
        }

        var unconditional = hits.Where(h => !h.Conditional).Select(h => h.Match).ToList();

        if (unconditional.Count > 0)
        {
            // MSBuild uses the last definition, so that's the one to change.
            var valueGroup = unconditional[^1].Groups["value"];
            var current = Text.Substring(valueGroup.Index, valueGroup.Length).Trim();
            if (current.Equals(value, StringComparison.OrdinalIgnoreCase))
                return new EditOutcome(EditKind.Unchanged, line, current);

            Text = Text[..valueGroup.Index] + value + Text[(valueGroup.Index + valueGroup.Length)..];
            return new EditOutcome(EditKind.Changed, line, current);
        }

        if (hits.Count > 0)
            return new EditOutcome(EditKind.Blocked, line,
                Reason: $"{name} is only set inside a conditional group, which fixes don't change.");

        var target = groups.FirstOrDefault(g => !g.Conditional && !g.SelfClosing);
        if (target is null)
            return new EditOutcome(EditKind.Blocked, line,
                Reason: "The project has no PropertyGroup without a condition to add it to.");

        var indent = DetectIndent(masked, target);

        // Insert after the last real content of the group, so the existing line break and
        // indentation before </PropertyGroup> stay where they are.
        int insertAt = target.BodyEnd;
        while (insertAt > target.BodyStart && char.IsWhiteSpace(Text[insertAt - 1])) insertAt--;

        Text = Text.Insert(insertAt, NewLine + indent + line);
        return new EditOutcome(EditKind.Added, line);
    }

    /// <summary>
    /// Appends a block as the last thing before &lt;/Project&gt;, separated by a blank line.
    /// Template lines use {0} for the file's top-level indent and {1} for the indent of their children.
    /// </summary>
    public EditOutcome AppendBlock(IReadOnlyList<string> template)
    {
        var masked = Mask(Text);
        int close = masked.LastIndexOf("</Project", StringComparison.OrdinalIgnoreCase);
        if (close < 0)
            return new EditOutcome(EditKind.Blocked, "", Reason: "The project file has no closing </Project> tag.");

        var (outer, inner) = DetectLevels(masked);
        var lines = template.Select(l => string.Format(l, outer, inner)).ToList();

        int insertAt = close;
        while (insertAt > 0 && char.IsWhiteSpace(Text[insertAt - 1])) insertAt--;

        var block = NewLine + NewLine + string.Join(NewLine, lines);
        Text = Text.Insert(insertAt, block);
        return new EditOutcome(EditKind.Added, string.Join("\n", lines.Select(l => l.Trim())));
    }

    private static (string Outer, string Inner) DetectLevels(string masked)
    {
        var groups = FindGroups(masked).Where(g => !g.SelfClosing).ToList();
        if (groups.Count > 0)
        {
            var first = groups[0];
            int lineStart = masked.LastIndexOf('\n', Math.Max(0, first.Start - 1)) + 1;
            var outer = new string(masked[lineStart..first.Start].TakeWhile(c => c is ' ' or '\t').ToArray());
            var child = ChildIndent.Match(masked[first.BodyStart..first.BodyEnd]);
            if (child.Success && child.Groups["ws"].Value.Length > outer.Length)
                return (outer, child.Groups["ws"].Value);
            return (outer, outer + (outer.Length == 0 ? "  " : outer));
        }
        return ("  ", "    ");
    }

    private static List<Group> FindGroups(string masked)
    {
        var list = new List<Group>();
        foreach (Match open in GroupOpen.Matches(masked))
        {
            bool conditional = open.Groups["attrs"].Value.Contains("Condition", StringComparison.OrdinalIgnoreCase);
            int bodyStart = open.Index + open.Length;

            if (open.Groups["self"].Success)
            {
                list.Add(new Group(open.Index, bodyStart, bodyStart, conditional, SelfClosing: true));
                continue;
            }

            int close = masked.IndexOf("</PropertyGroup", bodyStart, StringComparison.OrdinalIgnoreCase);
            if (close < 0) break;
            list.Add(new Group(open.Index, bodyStart, close, conditional, SelfClosing: false));
        }
        return list;
    }

    private static string DetectIndent(string masked, Group group)
    {
        // Use the indentation of an existing child element when there is one…
        var body = masked[group.BodyStart..group.BodyEnd];
        var child = ChildIndent.Match(body);
        if (child.Success) return child.Groups["ws"].Value;

        // …otherwise one step deeper than the <PropertyGroup> line itself.
        int lineStart = masked.LastIndexOf('\n', Math.Max(0, group.Start - 1)) + 1;
        var groupIndent = new string(masked[lineStart..group.Start].TakeWhile(c => c is ' ' or '\t').ToArray());
        return groupIndent + (groupIndent.Contains('\t') ? "\t" : "  ");
    }

    /// <summary>Blanks out comments (same length, line breaks kept) so commented-out XML is ignored.</summary>
    private static string Mask(string text) =>
        Comment.Replace(text, m =>
        {
            var chars = m.Value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] is not ('\r' or '\n')) chars[i] = ' ';
            return new string(chars);
        });

    // ---------- Encoding round trip ----------

    public static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(false, true));
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(true, true));
        return (new UTF8Encoding(false).GetString(bytes), new UTF8Encoding(false));
    }

    public static byte[] Encode(string text, Encoding encoding) =>
        [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
}
