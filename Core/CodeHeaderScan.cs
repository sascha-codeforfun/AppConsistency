using System.Text.RegularExpressions;

namespace AppConsistency.Core;

/// <summary>
/// Checks a header built in C# rather than XAML: finds the method that loads the header image
/// and counts the text elements it creates with their text set.
/// </summary>
public static class CodeHeaderScan
{
    // Strings, chars and comments; blanked out so braces inside them don't count.
    private static readonly Regex Noise = new(
        @"@""(?:""""|[^""])*""|\$?""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])+'|//[^\n]*|/\*.*?\*/",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TextElement = new(
        @"new\s+(?:System\.Windows\.Controls\.)?(?:TextBlock|Label)\s*(?:\(\s*\))?\s*\{(?<init>[^{}]*)\}",
        RegexOptions.Compiled);

    private static readonly Regex TextSet = new(
        @"\b(?:Text|Content)\s*=\s*(?!null\b|""""|string\.Empty\b)\S",
        RegexOptions.Compiled);

    private static readonly HashSet<string> BlockKeywords = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "using", "switch", "catch", "lock", "fixed", "when"
    };

    public sealed record Result(string? MethodName, int TextLines);

    /// <summary>Returns null when the image name isn't found in the code.</summary>
    public static Result? Inspect(string code, string imageName)
    {
        int reference = code.IndexOf(imageName, StringComparison.OrdinalIgnoreCase);
        if (reference < 0) return null;

        var masked = Mask(code);
        var (start, end, name) = EnclosingMethod(masked, reference);

        var body = code[start..end];
        int lines = TextElement.Matches(body).Count(m => TextSet.IsMatch(m.Groups["init"].Value));
        return new Result(name, lines);
    }

    private static (int Start, int End, string? Name) EnclosingMethod(string masked, int position)
    {
        int open = position;
        while (true)
        {
            open = EnclosingOpenBrace(masked, open);
            if (open < 0) return (0, masked.Length, null); // no method found; use the whole file

            var head = masked[..open].TrimEnd();
            if (head.EndsWith(')'))
            {
                int paren = MatchingOpenParen(head, head.Length - 1);
                if (paren > 0)
                {
                    var word = Regex.Match(head[..paren], @"(\w+)\s*(?:<[^<>()]*>)?\s*$").Groups[1].Value;
                    if (word.Length > 0 && !BlockKeywords.Contains(word))
                        return (open, MatchingCloseBrace(masked, open), word);
                }
            }
            // A control block, lambda, initializer or similar: keep walking outwards.
        }
    }

    private static int EnclosingOpenBrace(string text, int position)
    {
        int depth = 0;
        for (int i = position - 1; i >= 0; i--)
        {
            if (text[i] == '}') depth++;
            else if (text[i] == '{')
            {
                if (depth == 0) return i;
                depth--;
            }
        }
        return -1;
    }

    private static int MatchingCloseBrace(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i + 1;
        }
        return text.Length;
    }

    private static int MatchingOpenParen(string text, int close)
    {
        int depth = 0;
        for (int i = close; i >= 0; i--)
        {
            if (text[i] == ')') depth++;
            else if (text[i] == '(' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Blanks out strings, chars and comments, keeping length and line breaks.</summary>
    public static string Mask(string code) =>
        Noise.Replace(code, m =>
        {
            var chars = m.Value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] is not ('\r' or '\n')) chars[i] = ' ';
            return new string(chars);
        });
}
