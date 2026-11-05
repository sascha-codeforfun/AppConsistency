using System.Text.RegularExpressions;
using CSharpLexing;

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
/// <param name="Method">The method the write sits in, or null when it can't be told.</param>
public sealed record WriteSite(string File, int Line, string Call, WriteTarget Target, string Via, bool Standard, string? Method = null);

/// <summary>
/// Reads the code for writes to fixed places: AppData, the temp folder and the exe's own folder.
/// For every call that writes a file or creates a folder, the path argument is followed back
/// through the variables, fields, properties and methods it is built from, until it reaches where
/// the folder comes from. A path the user picks (a dialog) or one passed in from elsewhere doesn't
/// lead to a fixed place, so it isn't reported. This reads the source, it doesn't run anything:
/// a path assembled in ways it can't follow is missed, never guessed. Paths built with string
/// interpolation are followed through their holes. Calls, definitions and the brackets that
/// delimit them are only counted where they are code, never inside a string or a comment.
/// </summary>
public static class WriteScan
{
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

    private static readonly HashSet<string> NotData = new(StringComparer.Ordinal)
    {
        "var", "new", "string", "null", "true", "false", "this", "base", "await", "return", "nameof", "typeof",
        "Path", "Combine", "Join", "GetDirectoryName", "GetFileName", "GetFullPath", "File", "Directory",
        "Environment", "GetFolderPath", "SpecialFolder", "AppContext", "StringComparison", "Encoding",
        "FileMode", "FileAccess", "FileShare", "ToString", "Format", "Concat", "Trim", "Replace"
    };

    private const int MaxDepth = 8;

    /// <summary>
    /// A source file twice over, at identical positions: Text with comments blanked, where paths and
    /// file names are read; Masked with strings blanked as well, where calls, definitions and
    /// brackets are found.
    /// </summary>
    private sealed record Source(SnapshotFile File, string Text, string Masked);

    public static List<WriteSite> Scan(IReadOnlyList<SnapshotFile> files, string assemblyName)
    {
        var code = files
            .Where(f => f.Extension == ".cs" && f.Text is not null)
            .Select(f => new Source(f, CSharpLexer.BlankComments(f.Text!), CSharpLexer.BlankCommentsAndStrings(f.Text!)))
            .ToList();

        var lookups = new Lookups(code);
        var sites = new List<WriteSite>();
        foreach (var source in code)
        {
            var (file, text, masked) = source;
            foreach (var (pattern, argIndex, name) in WriteCalls)
            {
                // Found in the masked text: a call written inside a string isn't a call.
                foreach (Match call in pattern.Matches(masked))
                {
                    var args = Arguments(source, call.Index + call.Length);
                    if (args.Count <= argIndex) continue;
                    if (name == "new FileStream" && !WritesStream(args)) continue;
                    if (name == "ZipFile.Open" && !(args.Count > 1 && Regex.IsMatch(args[1], @"\b(?:Create|Update)\b"))) continue;

                    var found = new Trace(lookups);
                    found.Follow(args[argIndex], file, call.Index, 0);
                    var target = found.AppData ? WriteTarget.AppData : found.Temp ? WriteTarget.Temp : found.Exe ? WriteTarget.ExeFolder : (WriteTarget?)null;
                    if (target is null) continue;

                    int line = text.AsSpan(0, call.Index).Count('\n') + 1;
                    var via = found.AppData ? found.AppDataVia : found.Temp ? "temp folder" : "next to the exe";
                    bool standard = target == WriteTarget.ExeFolder && found.IsOwnJson(assemblyName);
                    var method = MethodLocator.Enclosing(masked, call.Index);
                    sites.Add(new WriteSite(file.RelPath, line, CallName(text, call), target.Value, via, standard, method));
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

    /// <summary>
    /// Where names are defined, found once per scan and shared by every trace in it. Each write call
    /// starts its own trace, and they mostly follow the same names; without this, every one of them
    /// built the same patterns again and ran them over all the code again.
    /// </summary>
    private sealed class Lookups(List<Source> code)
    {
        private readonly Dictionary<(string Name, bool IsCall), List<(string Expression, SnapshotFile File, int Position)>> _members = new();
        private readonly Dictionary<(string Name, SnapshotFile File), List<(int Index, int End, string Expression)>> _locals = new();

        public Source SourceOf(SnapshotFile file) => code.First(c => c.File == file);

        /// <summary>
        /// The assignments to a local in one file, in order. A match lies wholly before a position
        /// exactly when it would have been found in the text cut off there: "[^;]+;" can't reach past
        /// the cut, so the matches before it are the same either way.
        /// </summary>
        public List<(int Index, int End, string Expression)> Locals(string name, SnapshotFile file)
        {
            if (_locals.TryGetValue((name, file), out var found)) return found;

            var source = SourceOf(file);
            var local = new Regex($@"\b{Regex.Escape(name)}\s*=(?![=>])(?<rhs>[^;]+);");
            found = local.Matches(source.Masked)
                .Select(m => (m.Index, m.Index + m.Length, Read(source, m.Groups["rhs"])))
                .ToList();
            _locals[(name, file)] = found;
            return found;
        }

        /// <summary>A field, property or method across the code: what it's set to, or everything it returns.</summary>
        public List<(string Expression, SnapshotFile File, int Position)> Members(string name, bool isCall)
        {
            if (_members.TryGetValue((name, isCall), out var found)) return found;

            var n = Regex.Escape(name);
            var member = isCall
                ? new Regex($@"\b{n}\s*\([^;{{)]*\)\s*(?:=>(?<rhs>[^;]+);|\{{)", RegexOptions.Singleline)
                : new Regex($@"\b{n}\s*(?:\{{\s*get;[^}}]*\}}\s*=|=>|=(?![=>]))(?<rhs>[^;]+);");

            found = new List<(string, SnapshotFile, int)>();
            foreach (var source in code)
            {
                var other = source.File;
                foreach (Match m in member.Matches(source.Masked))
                {
                    if (m.Groups["rhs"].Success)
                    {
                        found.Add((Read(source, m.Groups["rhs"]), other, m.Index));
                        continue;
                    }

                    // A method with a body: everything it returns.
                    int end = MatchingBrace(source.Masked, m.Index + m.Length - 1);
                    if (end < 0) continue;
                    int bodyStart = m.Index + m.Length;
                    foreach (Match r in ReturnValue.Matches(source.Masked[bodyStart..end]))
                        found.Add((Read(source, r.Groups["rhs"], bodyStart), other, bodyStart + r.Index));
                }
            }
            _members[(name, isCall)] = found;
            return found;
        }
    }

    private static readonly Regex ReturnValue = new(@"\breturn\b(?<rhs>[^;]+);", RegexOptions.Compiled);

    /// <summary>Follows a path expression back to where its folder comes from.</summary>
    private sealed class Trace(Lookups lookups)
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
            // File names from the literals, every kind. A hole ends a path segment, so
            // $"{AppContext.BaseDirectory}App.json" names App.json, and a name built in a hole matches nothing.
            foreach (var literal in CSharpLexer.Literals(expression))
                _literals.Add(literal.Text(expression, "/").Split('\\', '/')[^1]);

            // Names from the code only, which includes the holes of interpolated strings: $"{folder}\\x.json" follows folder.
            var codeOnly = CSharpLexer.BlankCommentsAndStrings(expression);
            foreach (Match id in Identifier.Matches(codeOnly))
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
        /// Definitions are matched in the masked text, so one written inside a string doesn't count and
        /// a semicolon inside a string doesn't end one; the expression is then read from the text at
        /// the same position.
        /// </summary>
        private IEnumerable<(string Expression, SnapshotFile File, int Position)> Definitions(
            string name, bool isCall, SnapshotFile file, int position)
        {
            if (!isCall && char.IsLower(name[0]))
            {
                // The nearest assignment that ends before the use.
                var nearest = lookups.Locals(name, file).LastOrDefault(l => l.End <= position);
                return nearest.Expression is null ? [] : [(nearest.Expression, file, nearest.Index)];
            }

            return lookups.Members(name, isCall);
        }
    }

    /// <summary>The text at a group found in the masked copy: the same span, with its strings in place.</summary>
    private static string Read(Source source, Group group, int offset = 0) =>
        source.Text.Substring(offset + group.Index, group.Length).Trim();

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

    /// <summary>
    /// The top-level arguments of a call, from just after its opening parenthesis. Brackets, commas
    /// and semicolons are counted in the masked text, where strings, chars and comments are blank;
    /// the arguments are read from the text at the same positions.
    /// </summary>
    private static List<string> Arguments(Source source, int start)
    {
        var (_, text, masked) = source;
        var args = new List<string>();
        int depth = 0, from = start;
        for (int i = start; i < masked.Length && i < start + 4000; i++)
        {
            char c = masked[i];
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

    /// <summary>The closing brace for an opening one, counted in masked text so braces in strings and chars don't count.</summary>
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
}
