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
    ExeFolder,
    /// <summary>Outside any user's profile: the system drive's root, Windows, Program Files, ProgramData.</summary>
    MachineWide
}

/// <param name="Standard">A write next to the exe that is the app's own AssemblyName.json, your settings standard.</param>
/// <param name="Method">The method the write sits in, or null when it can't be told.</param>
/// <param name="Through">How the path got there, when it came through other names: "via ChangeSetsDirectory → BackupRoot at AppPaths.cs line 12".</param>
public sealed record WriteSite(string File, int Line, string Call, WriteTarget Target, string Via, bool Standard, string? Method = null,
                               string? Through = null);

/// <summary>
/// Reads the code for writes to fixed places: AppData, the temp folder and the exe's own folder.
/// For every call that writes a file or creates a folder, the path argument is followed back
/// through the variables, fields, properties and methods it is built from, until it reaches where
/// the folder comes from. A path the user picks (a dialog) or one passed in from elsewhere doesn't
/// lead to a fixed place, so it isn't reported. This reads the source, it doesn't run anything:
/// a path assembled in ways it can't follow is missed, never guessed. A name is followed to its own
/// definition: AppPaths.Root only to Root in AppPaths, and dialog.FileName not by name at all (dialog
/// is followed instead), so a same-named member elsewhere can't lend the path its folder. Paths built with string
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

    // Where a folder comes from. AppData counts when the path is rooted there: the AppData folders
    // themselves, or an "AppData\" path in the current user's profile. A bare "AppData\…" literal alone
    // (a path into another profile, a catalog of other apps' folders) places nothing.
    private static readonly Regex AppDataSource = new(
        @"SpecialFolder\.(?:LocalApplicationData|ApplicationData)\b" +
        @"|GetEnvironmentVariable\s*\(\s*""(?:LOCALAPPDATA|APPDATA)""" +
        @"|%(?:LOCALAPPDATA|APPDATA)%",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AppDataLiteral = new(@"AppData(?:\\\\|\\|/)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProfileSource = new(
        @"SpecialFolder\.UserProfile\b|GetEnvironmentVariable\s*\(\s*""USERPROFILE""|%USERPROFILE%",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Outside every user's profile.
    private static readonly Regex MachineSource = new(
        // The system drive by way of the Windows folder; GetPathRoot of any other path is that path's own drive.
        @"\bEnvironment\.SystemDirectory\b" +
        @"|SpecialFolder\.(?:Windows|System|SystemX86|ProgramFiles|ProgramFilesX86|CommonProgramFiles|CommonProgramFilesX86|CommonApplicationData|CommonDocuments|CommonDesktopDirectory|CommonStartMenu|CommonPrograms|CommonStartup)\b" +
        @"|%(?:ProgramData|ProgramFiles|ProgramFiles\(x86\)|SystemRoot|windir|SystemDrive|ALLUSERSPROFILE)%" +
        @"|GetEnvironmentVariable\s*\(\s*""(?:ProgramData|ProgramFiles|SystemRoot|windir|SystemDrive|ALLUSERSPROFILE)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A fixed path on a drive, outside the users' folders: "C:\", @"D:\Tools\x".
    private static readonly Regex DrivePath = new(@"^[A-Za-z]:[\\/](?![\\/]*Users[\\/])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A path the user picks: a file or folder dialog. The trace stops there; nothing fixed is reached.
    private static readonly Regex UserPicked = new(
        @"^\s*new\s+(?:[\w.]+\.)?(?:SaveFileDialog|OpenFileDialog|OpenFolderDialog|FolderBrowserDialog|CommonSaveFileDialog|CommonOpenFileDialog)\b",
        RegexOptions.Compiled);

    private static readonly Regex TypeDeclaration = new(@"\b(?:class|struct|record|interface|enum)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled);

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

    private static readonly Regex Identifier = new(@"(?<![\w.""])(?<qual>(?:\w+\.)*)(?<name>[A-Za-z_]\w*)(?<call>\s*\()?", RegexOptions.Compiled);

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
                    found.Follow(args[argIndex], file, call.Index, 0, []);
                    var target = found.AppData ? WriteTarget.AppData : found.Temp ? WriteTarget.Temp
                        : found.Exe ? WriteTarget.ExeFolder : found.Machine ? WriteTarget.MachineWide : (WriteTarget?)null;
                    if (target is null) continue;

                    int line = text.AsSpan(0, call.Index).Count('\n') + 1;
                    var via = target switch
                    {
                        WriteTarget.AppData => found.AppDataVia,
                        WriteTarget.Temp => "temp folder",
                        WriteTarget.ExeFolder => "next to the exe",
                        _ => found.MachineVia
                    };
                    bool standard = target == WriteTarget.ExeFolder && found.IsOwnJson(assemblyName);
                    var method = MethodLocator.Enclosing(masked, call.Index);
                    sites.Add(new WriteSite(file.RelPath, line, CallName(text, call), target.Value, via, standard, method, found.Through(target.Value)));
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
        private readonly Dictionary<(string Name, bool IsCall, string? Type), List<(string Expression, SnapshotFile File, int Position)>> _members = new();
        private Dictionary<string, List<(SnapshotFile File, int Start, int End)>>? _types;

        /// <summary>
        /// Where a type of this name is declared: its bodies (a partial type can have several), or null when
        /// the code declares none (a framework type).
        /// </summary>
        public List<(SnapshotFile File, int Start, int End)>? Declaring(string type)
        {
            if (_types is null)
            {
                _types = new Dictionary<string, List<(SnapshotFile, int, int)>>(StringComparer.Ordinal);
                foreach (var source in code)
                    foreach (Match m in TypeDeclaration.Matches(source.Masked))
                    {
                        // The body: from the type's opening brace to its closing one; a record without one has none.
                        int open = source.Masked.IndexOfAny(['{', ';'], m.Index + m.Length);
                        if (open < 0 || source.Masked[open] != '{') continue;
                        int close = MatchingBrace(source.Masked, open);
                        var name = m.Groups["name"].Value;
                        if (!_types.TryGetValue(name, out var bodies)) _types[name] = bodies = [];
                        bodies.Add((source.File, open, close < 0 ? source.Masked.Length : close));
                    }
            }
            return _types.GetValueOrDefault(type);
        }

        /// <summary>The innermost type whose body holds a position, or null outside any.</summary>
        public string? TypeAt(SnapshotFile file, int position)
        {
            Declaring("");   // make sure the table is built
            string? best = null;
            int size = int.MaxValue;
            foreach (var (name, bodies) in _types!)
                foreach (var (f, start, end) in bodies)
                    if (f == file && position > start && position < end && end - start < size)
                    {
                        best = name;
                        size = end - start;
                    }
            return best;
        }

        public int LineOf(SnapshotFile file, int position) => SourceOf(file).Text.AsSpan(0, Math.Min(position, SourceOf(file).Text.Length)).Count('\n') + 1;
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

        /// <summary>
        /// A field, property or method: what it's set to, or everything it returns. Across the code for a
        /// bare name; only in the files that declare the type for Type.Name.
        /// </summary>
        public List<(string Expression, SnapshotFile File, int Position)> Members(string name, bool isCall, string? type = null)
        {
            if (_members.TryGetValue((name, isCall, type), out var found)) return found;
            var within = type is null ? null : Declaring(type);

            var n = Regex.Escape(name);
            var member = isCall
                ? new Regex($@"\b{n}\s*\([^;{{)]*\)\s*(?:=>(?<rhs>[^;]+);|\{{)", RegexOptions.Singleline)
                : new Regex($@"\b{n}\s*(?:\{{\s*get;[^}}]*\}}\s*=|=>|=(?![=>]))(?<rhs>[^;]+);");

            var getter = isCall ? null
                : new Regex($@"\b{n}\s*\{{\s*get\s*(?:=>(?<rhs>[^;]+);|\{{)", RegexOptions.Singleline);

            found = new List<(string, SnapshotFile, int)>();
            foreach (var source in code)
            {
                var other = source.File;
                if (within is not null && !within.Any(b => b.File == other)) continue;
                foreach (Match m in member.Matches(source.Masked))
                {
                    if (within is not null && !within.Any(b => b.File == other && m.Index > b.Start && m.Index < b.End)) continue;
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

                // A property with a getter of its own: get => …; or get { … return …; }.
                if (getter is null) continue;
                foreach (Match g in getter.Matches(source.Masked))
                {
                    if (within is not null && !within.Any(b => b.File == other && g.Index > b.Start && g.Index < b.End)) continue;
                    if (g.Groups["rhs"].Success)
                    {
                        found.Add((Read(source, g.Groups["rhs"]), other, g.Index));
                        continue;
                    }
                    int end = MatchingBrace(source.Masked, g.Index + g.Length - 1);
                    if (end < 0) continue;
                    int bodyStart = g.Index + g.Length;
                    foreach (Match r in ReturnValue.Matches(source.Masked[bodyStart..end]))
                        found.Add((Read(source, r.Groups["rhs"], bodyStart), other, bodyStart + r.Index));
                }
            }
            _members[(name, isCall, type)] = found;
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

        // What was found, and the first place each was found: the names followed to get there and where.
        private bool _appDataRooted, _appDataLiteral, _profile;
        private string _appDataRootedVia = "AppData";
        private readonly Dictionary<string, string> _where = new();

        public bool AppData => _appDataRooted || _appDataLiteral && _profile;
        public bool Temp { get; private set; }
        public bool Exe { get; private set; }
        public bool Machine { get; private set; }
        public string AppDataVia => _appDataRooted ? _appDataRootedVia : "an AppData path in the user's profile";
        public string MachineVia { get; private set; } = "a machine-wide folder";

        public bool IsOwnJson(string assemblyName) =>
            _ownNameJson || _literals.Any(l => l.Equals(assemblyName + ".json", StringComparison.OrdinalIgnoreCase));

        /// <summary>"via ChangeSetsDirectory → BackupRoot at AppPaths.cs line 12", or null when the path was written in the call itself.</summary>
        public string? Through(WriteTarget target)
        {
            var key = target switch
            {
                WriteTarget.AppData => _appDataRooted ? "appdata" : "appdata-literal",
                WriteTarget.Temp => "temp",
                WriteTarget.ExeFolder => "exe",
                _ => "machine"
            };
            return _where.GetValueOrDefault(key);
        }

        private void Found(string key, IReadOnlyList<string> chain, SnapshotFile file, int position)
        {
            if (chain.Count == 0 || _where.ContainsKey(key)) return;
            _where[key] = $"via {string.Join(" → ", chain)} at {file.RelPath} line {lookups.LineOf(file, position)}";
        }

        public void Follow(string expression, SnapshotFile file, int position, int depth, IReadOnlyList<string> chain)
        {
            if (depth > MaxDepth) return;
            if (UserPicked.IsMatch(expression)) return;

            if (AppDataSource.Match(expression) is { Success: true } a)
            {
                if (!_appDataRooted)
                    _appDataRootedVia = a.Value.Contains("Local", StringComparison.OrdinalIgnoreCase) ? "LocalApplicationData" : "ApplicationData (Roaming)";
                _appDataRooted = true;
                Found("appdata", chain, file, position);
            }
            if (ProfileSource.IsMatch(expression)) _profile = true;
            if (TempSource.IsMatch(expression)) { Temp = true; Found("temp", chain, file, position); }
            if (ExeSource.IsMatch(expression)) { Exe = true; Found("exe", chain, file, position); }
            if (ExeNameJson.IsMatch(expression)) _ownNameJson = true;
            if (MachineSource.Match(expression) is { Success: true } machine)
            {
                if (!Machine) MachineVia = MachineWords(machine.Value);
                Machine = true;
                Found("machine", chain, file, position);
            }

            // File names from the literals, every kind. A hole ends a path segment, so
            // $"{AppContext.BaseDirectory}App.json" names App.json, and a name built in a hole matches nothing.
            foreach (var literal in CSharpLexer.Literals(expression))
            {
                var value = literal.Text(expression, "/");
                _literals.Add(value.Split('\\', '/')[^1]);
                if (AppDataLiteral.IsMatch(value)) { _appDataLiteral = true; Found("appdata-literal", chain, file, position); }
                if (DrivePath.IsMatch(literal.Text(expression, "")) && !AppDataLiteral.IsMatch(value))
                {
                    if (!Machine) MachineVia = "a folder on a fixed drive";
                    Machine = true;
                    Found("machine", chain, file, position);
                }
            }

            // Names from the code only, which includes the holes of interpolated strings: $"{folder}\\x.json" follows folder.
            var codeOnly = CSharpLexer.BlankCommentsAndStrings(expression);
            foreach (Match id in Identifier.Matches(codeOnly))
            {
                // A name being assigned (Title = …, FileName = … in an initializer) is not a value the path comes from.
                int after = id.Index + id.Length;
                while (after < codeOnly.Length && codeOnly[after] is ' ' or '\t' or '\r' or '\n') after++;
                if (!id.Groups["call"].Success && after < codeOnly.Length && codeOnly[after] == '='
                    && (after + 1 >= codeOnly.Length || codeOnly[after + 1] is not ('=' or '>')))
                    continue;

                var name = id.Groups["name"].Value;
                bool isCall = id.Groups["call"].Success;
                var qualifiers = id.Groups["qual"].Value.Split('.', StringSplitOptions.RemoveEmptyEntries)
                    .SkipWhile(q => q is "this" or "base").ToList();

                string? type = null;
                if (name == "_") continue;   // a discard or a throwaway parameter, never a value
                if (qualifiers.Count > 0)
                {
                    var first = qualifiers[0];
                    if (char.IsLower(first[0]) || first[0] == '_')
                    {
                        // dialog.FileName, _config.Folder: a member of a local, a parameter or a field. Its type
                        // isn't known, so the member can't be looked up, and the object isn't the path either:
                        // following where _config came from would lend the path the folder of a settings file.
                        continue;
                    }
                    else
                    {
                        // AppPaths.Root: only Root in AppPaths. A type the code doesn't declare (Environment,
                        // Path) has no definitions here; a same-named member elsewhere would be the wrong one.
                        type = qualifiers.LastOrDefault(q => lookups.Declaring(q) is not null);
                        if (type is null) continue;
                    }
                }

                if (NotData.Contains(name) || !_seen.Add(type is null ? name : type + "." + name)) continue;

                foreach (var (definition, defFile, defPos) in Definitions(name, isCall, type, file, position))
                    Follow(definition, defFile, defPos, depth + 1, [.. chain, name]);
            }
        }

        private static string MachineWords(string match)
        {
            if (match.Contains("SystemDirectory", StringComparison.OrdinalIgnoreCase) || match.Contains("GetPathRoot", StringComparison.OrdinalIgnoreCase)
                || match.Contains("SystemDrive", StringComparison.OrdinalIgnoreCase))
                return "the system drive";
            if (match.Contains("ProgramData", StringComparison.OrdinalIgnoreCase) || match.Contains("CommonApplicationData", StringComparison.OrdinalIgnoreCase)
                || match.Contains("ALLUSERSPROFILE", StringComparison.OrdinalIgnoreCase))
                return "ProgramData";
            if (match.Contains("ProgramFiles", StringComparison.OrdinalIgnoreCase)) return "Program Files";
            if (match.Contains("Common", StringComparison.OrdinalIgnoreCase)) return "a folder shared by all users";
            return "the Windows folder";
        }

        /// <summary>
        /// What a name stands for. A local (lower-case) is looked for before its use in the same file,
        /// nearest first. A member (field, property, method) is looked for across the code, or only in
        /// its type when the code names one. Definitions are matched in the masked text, so one written
        /// inside a string doesn't count and a semicolon inside a string doesn't end one; the expression
        /// is then read from the text at the same position.
        /// </summary>
        private IEnumerable<(string Expression, SnapshotFile File, int Position)> Definitions(
            string name, bool isCall, string? type, SnapshotFile file, int position)
        {
            if (!isCall && type is null && (char.IsLower(name[0]) || name[0] == '_' && LocalIn(name, file, position)))
            {
                // The nearest assignment that ends before the use.
                var nearest = lookups.Locals(name, file).LastOrDefault(l => l.End <= position);
                if (nearest.Expression is not null) return [(nearest.Expression, file, nearest.Index)];
                if (name[0] != '_') return [];
            }

            if (type is not null) return lookups.Members(name, isCall, type);

            // A bare name is the enclosing type's own member first. If that type has none, it's followed only
            // when the code defines it in exactly one type: with several candidates, which one is meant can't
            // be told from the text, and guessing is how a path gets the wrong folder.
            var own = lookups.TypeAt(file, position);
            if (own is not null && lookups.Members(name, isCall, own) is { Count: > 0 } mine) return mine;
            var all = lookups.Members(name, isCall);
            var owners = all.Select(d => lookups.TypeAt(d.File, d.Position)).Distinct().Count();
            return owners == 1 ? all : [];
        }

        private bool LocalIn(string name, SnapshotFile file, int position) =>
            lookups.Locals(name, file).Any(l => l.End <= position);
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
