using System.Text.RegularExpressions;

namespace AppConsistency.Core;

/// <summary>Where a write in the code ends up.</summary>
public enum WriteTarget
{
    /// <summary>Under the user's AppData (Local, Roaming or LocalLow): files left behind for good.</summary>
    AppData,
    /// <summary>The temp folder.</summary>
    Temp,
    /// <summary>Next to the exe.</summary>
    ExeFolder
}

/// <param name="Standard">A write next to the exe that is the app's own AssemblyName.json, your settings standard.</param>
public sealed record WriteSite(string File, int Line, string Call, WriteTarget Target, string Via, bool Standard);

/// <summary>
/// Reads the code for writes to fixed places: AppData, the temp folder and the exe's own folder.
/// For every call that writes a file or creates a folder, the path argument is followed back
/// through the variables, fields, properties and methods it is built from, until it reaches where
/// the folder comes from. A path the user picks (a dialog) or one passed in from elsewhere doesn't
/// lead to a fixed place, so it isn't reported. This reads the source, it doesn't run anything:
/// a path assembled in ways it can't follow is missed, never guessed.
/// </summary>
public static class WriteScan
{
    // Strings, chars and comments, the same pattern CodeHeaderScan uses; only comments are blanked here.
    private static readonly Regex Noise = new(
        @"@""(?:""""|[^""])*""|\$?@?""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])+'|//[^\n]*|/\*.*?\*/",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Calls that write a file or create a folder. "arg" is the index of the destination argument.
    private static readonly (Regex Pattern, int Arg, string Name)[] WriteCalls =
    [
        (new(@"\bFile\.(?:WriteAll(?:Text|Bytes|Lines)(?:Async)?|AppendAll(?:Text|Lines)(?:Async)?|Create(?:Text)?|AppendText|OpenWrite)\s*\(", RegexOptions.Compiled), 0, "File write"),
        (new(@"\bFile\.(?:Copy|Move|Replace)\s*\(", RegexOptions.Compiled), 1, "File copy or move"),
        (new(@"\bDirectory\.CreateDirectory\s*\(", RegexOptions.Compiled), 0, "Directory.CreateDirectory"),
        (new(@"\bnew\s+(?:System\.IO\.)?StreamWriter\s*\(", RegexOptions.Compiled), 0, "new StreamWriter"),
        (new(@"\bnew\s+(?:System\.IO\.)?FileStream\s*\(", RegexOptions.Compiled), 0, "new FileStream"),
        (new(@"\bZipFile\.(?:CreateFromDirectory|ExtractToDirectory)\s*\(", RegexOptions.Compiled), 1, "ZipFile"),
        (new(@"\bZipFile\.Open\s*\(", RegexOptions.Compiled), 0, "ZipFile.Open"),
        (new(@"\.Save(?:As)?\s*\(", RegexOptions.Compiled), 0, "Save")
    ];

    // Where a folder comes from.
    private static readonly Regex AppDataSource = new(
        @"SpecialFolder\.(?:LocalApplicationData|ApplicationData)\b" +
        @"|GetEnvironmentVariable\s*\(\s*""(?:LOCALAPPDATA|APPDATA)""" +
        @"|%(?:LOCALAPPDATA|APPDATA)%" +
        @"|AppData(?:\\\\|\\|/)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TempSource = new(
        @"\bPath\.GetTemp(?:Path|FileName)\s*\(|GetEnvironmentVariable\s*\(\s*""TE?MP""|%TE?MP%",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExeSource = new(
        @"\bAppContext\.BaseDirectory\b|\bAppDomain\.CurrentDomain\.BaseDirectory\b|\bEnvironment\.ProcessPath\b|\.MainModule\b",
        RegexOptions.Compiled);

    // Path.ChangeExtension(Environment.ProcessPath, ".json"): the exe's own name with .json.
    private static readonly Regex ExeNameJson = new(
        @"ChangeExtension\s*\([^;]*(?:ProcessPath|MainModule)[^;]*""\.json""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Identifier = new(@"(?<![\w.""])(?:\w+\.)*(?<name>[A-Za-z_]\w*)(?<call>\s*\()?", RegexOptions.Compiled);
    private static readonly Regex StringLiteral = new(@"""(?:\\.|[^""\\\n])*""", RegexOptions.Compiled);

    private static readonly HashSet<string> NotData = new(StringComparer.Ordinal)
    {
        "var", "new", "string", "null", "true", "false", "this", "base", "await", "return", "nameof", "typeof",
        "Path", "Combine", "Join", "GetDirectoryName", "GetFileName", "GetFullPath", "File", "Directory",
        "Environment", "GetFolderPath", "SpecialFolder", "AppContext", "StringComparison", "Encoding",
        "FileMode", "FileAccess", "FileShare", "ToString", "Format", "Concat", "Trim", "Replace"
    };

    private const int MaxDepth = 8;

    public static List<WriteSite> Scan(IReadOnlyList<SnapshotFile> files, string assemblyName)
    {
        var code = files
            .Where(f => f.Extension == ".cs" && f.Text is not null)
            .Select(f => (File: f, Text: BlankComments(f.Text!)))
            .ToList();

        var sites = new List<WriteSite>();
        foreach (var (file, text) in code)
        {
            foreach (var (pattern, argIndex, name) in WriteCalls)
            {
                foreach (Match call in pattern.Matches(text))
                {
                    var args = Arguments(text, call.Index + call.Length);
                    if (args.Count <= argIndex) continue;
                    if (name == "new FileStream" && !WritesStream(args)) continue;
                    if (name == "ZipFile.Open" && !(args.Count > 1 && Regex.IsMatch(args[1], @"\b(?:Create|Update)\b"))) continue;

                    var found = new Trace(code);
                    found.Follow(args[argIndex], file, call.Index, 0);
                    var target = found.AppData ? WriteTarget.AppData : found.Temp ? WriteTarget.Temp : found.Exe ? WriteTarget.ExeFolder : (WriteTarget?)null;
                    if (target is null) continue;

                    int line = text.AsSpan(0, call.Index).Count('\n') + 1;
                    var via = found.AppData ? found.AppDataVia : found.Temp ? "temp folder" : "next to the exe";
                    bool standard = target == WriteTarget.ExeFolder && found.IsOwnJson(assemblyName);
                    sites.Add(new WriteSite(file.RelPath, line, CallName(text, call), target.Value, via, standard));
                }
            }
        }

        return sites
            .GroupBy(s => (s.File, s.Line, s.Target))
            .Select(g => g.First())
            .OrderBy(s => s.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Line)
            .ToList();
    }

    /// <summary>Follows a path expression back to where its folder comes from.</summary>
    private sealed class Trace(List<(SnapshotFile File, string Text)> code)
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly List<string> _literals = new();
        private bool _ownNameJson;

        public bool AppData { get; private set; }
        public bool Temp { get; private set; }
        public bool Exe { get; private set; }
        public string AppDataVia { get; private set; } = "AppData";

        public bool IsOwnJson(string assemblyName) =>
            _ownNameJson || _literals.Any(l => l.Equals(assemblyName + ".json", StringComparison.OrdinalIgnoreCase));

        public void Follow(string expression, SnapshotFile file, int position, int depth)
        {
            if (depth > MaxDepth) return;

            if (AppDataSource.Match(expression) is { Success: true } a)
            {
                AppData = true;
                AppDataVia = a.Value.Contains("Local", StringComparison.OrdinalIgnoreCase) ? "LocalApplicationData"
                    : a.Value.Contains("ApplicationData", StringComparison.Ordinal) || a.Value.Contains("APPDATA", StringComparison.Ordinal) ? "ApplicationData (Roaming)"
                    : "an AppData path";
            }
            if (TempSource.IsMatch(expression)) Temp = true;
            if (ExeSource.IsMatch(expression)) Exe = true;
            if (ExeNameJson.IsMatch(expression)) _ownNameJson = true;
            foreach (Match s in StringLiteral.Matches(expression))
                _literals.Add(s.Value.Trim('"').Split('\\', '/')[^1]);

            var withoutStrings = StringLiteral.Replace(expression, "\"\"");
            foreach (Match id in Identifier.Matches(withoutStrings))
            {
                var name = id.Groups["name"].Value;
                if (NotData.Contains(name) || !_seen.Add(name)) continue;

                foreach (var (definition, defFile, defPos) in Definitions(name, id.Groups["call"].Success, file, position))
                    Follow(definition, defFile, defPos, depth + 1);
            }
        }

        /// <summary>
        /// What a name stands for. A local (lower-case) is looked for before its use in the same file,
        /// nearest first. A member (field, property, method) is looked for across the code.
        /// </summary>
        private IEnumerable<(string Expression, SnapshotFile File, int Position)> Definitions(
            string name, bool isCall, SnapshotFile file, int position)
        {
            var n = Regex.Escape(name);

            if (!isCall && char.IsLower(name[0]))
            {
                var text = code.First(c => c.File == file).Text;
                var local = new Regex($@"\b{n}\s*=(?![=>])\s*(?<rhs>[^;]+);");
                var nearest = local.Matches(text[..Math.Min(position, text.Length)]).LastOrDefault();
                if (nearest is not null) yield return (nearest.Groups["rhs"].Value, file, nearest.Index);
                yield break;
            }

            var member = isCall
                ? new Regex($@"\b{n}\s*\([^;{{)]*\)\s*(?:=>\s*(?<rhs>[^;]+);|\{{)", RegexOptions.Singleline)
                : new Regex($@"\b{n}\s*(?:\{{\s*get;[^}}]*\}}\s*=|=>|=(?![=>]))\s*(?<rhs>[^;]+);");

            foreach (var (other, text) in code)
            {
                foreach (Match m in member.Matches(text))
                {
                    if (m.Groups["rhs"].Success)
                    {
                        yield return (m.Groups["rhs"].Value, other, m.Index);
                        continue;
                    }

                    // A method with a body: everything it returns.
                    int end = MatchingBrace(text, m.Index + m.Length - 1);
                    if (end < 0) continue;
                    var body = text[(m.Index + m.Length)..end];
                    foreach (Match r in Regex.Matches(body, @"\breturn\s+(?<rhs>[^;]+);"))
                        yield return (r.Groups["rhs"].Value, other, m.Index + m.Length + r.Index);
                }
            }
        }
    }

    /// <summary>A FileStream that writes: a mode or access that creates, appends or writes.</summary>
    private static bool WritesStream(List<string> args) =>
        args.Skip(1).Any(a => Regex.IsMatch(a, @"\bFileMode\.(?:Create|CreateNew|OpenOrCreate|Append|Truncate)\b|\bFileAccess\.(?:Write|ReadWrite)\b"));

    private static string CallName(string text, Match call)
    {
        var raw = call.Value.TrimEnd('(', ' ');
        if (raw.StartsWith('.'))
        {
            // obj.Save(...): name it with what's saved, as far as the text shows.
            int start = call.Index;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '.')) start--;
            raw = text[start..call.Index] + raw;
        }
        return Regex.Replace(raw, @"\s+", " ");
    }

    /// <summary>The top-level arguments of a call, from just after its opening parenthesis.</summary>
    private static List<string> Arguments(string text, int start)
    {
        var args = new List<string>();
        int depth = 0, from = start;
        for (int i = start; i < text.Length && i < start + 4000; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                // Skip a string literal, including verbatim ones.
                bool verbatim = i > 0 && text[i - 1] == '@';
                for (i++; i < text.Length; i++)
                {
                    if (verbatim && text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                    if (!verbatim && text[i] == '\\') { i++; continue; }
                    if (text[i] == '"') break;
                }
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    if (i > from || args.Count > 0) args.Add(text[from..i].Trim());
                    return args;
                }
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(text[from..i].Trim());
                from = i + 1;
            }
            else if (c == ';') break;
        }
        return args;
    }

    private static int MatchingBrace(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Blanks comments, keeping strings, length and line breaks.</summary>
    private static string BlankComments(string code) =>
        Noise.Replace(code, m =>
        {
            if (!m.Value.StartsWith("//", StringComparison.Ordinal) && !m.Value.StartsWith("/*", StringComparison.Ordinal))
                return m.Value;
            var chars = m.Value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] is not ('\r' or '\n')) chars[i] = ' ';
            return new string(chars);
        });
}
