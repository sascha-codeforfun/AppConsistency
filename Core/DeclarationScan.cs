using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CSharpLexing;

namespace AppConsistency.Core;

/// <summary>The intense things an app can do, each declared with its own labelled line in the README.</summary>
public enum DeclarationTopic
{
    NeedsAdministratorRights,
    ChangesSecuritySettings,
    StartsOnItsOwn,
    RunsCodeFromOutside,
    RunsCodeWhenYouBuild,
    StartsOtherPrograms,
    ChangesTheWholeComputer,
    TalksToOtherComputers,
    ReadsOrSendsPersonalData
}

/// <summary>
/// The "What this app changes on your system" section of a README, and the code that calls for it.
///
/// The section and its labels are a contract shared with RepoLegibility, which reads the same section the
/// same way: change the heading, a label or the parsing here, and change it there too.
///
/// The code side is plain text matching on code the lexer has cleaned: comments never count, and paths,
/// hosts and subcommands are only read from string literals. It doesn't follow values the way the write
/// check does. The job is to see that a topic is touched and point at where, not to prove it.
/// </summary>
public static class DeclarationScan
{
    public const string Heading = "Before you build or run it";

    public sealed record Topic(DeclarationTopic Id, string Label, bool Fails);

    /// <summary>
    /// The labels, in the order the README lists them. Each says, as a verb, what the app does, so it stays
    /// true whoever wrote it and can't be read as a reassurance: a declaration tells a reader the author
    /// meant it, it doesn't make it safe. The five that fail change how safe the machine is, keep code
    /// running after the app is gone, or run code nobody has read.
    /// </summary>
    public static readonly IReadOnlyList<Topic> Topics =
    [
        new(DeclarationTopic.NeedsAdministratorRights, "Needs administrator rights", true),
        new(DeclarationTopic.ChangesSecuritySettings, "Changes security settings", true),
        new(DeclarationTopic.StartsOnItsOwn, "Starts on its own", true),
        new(DeclarationTopic.RunsCodeFromOutside, "Runs code from outside the app", true),
        new(DeclarationTopic.RunsCodeWhenYouBuild, "Runs code when you build", true),
        new(DeclarationTopic.StartsOtherPrograms, "Starts other programs", false),
        new(DeclarationTopic.ChangesTheWholeComputer, "Changes the whole computer", false),
        new(DeclarationTopic.TalksToOtherComputers, "Talks to other computers", false),
        new(DeclarationTopic.ReadsOrSendsPersonalData, "Reads or sends personal data", false)
    ];

    /// <summary>The labels of the first version of the section, renamed. Not accepted: only named in the unknown-label warning.</summary>
    public static readonly IReadOnlyDictionary<string, string> RenamedLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Administrator rights"] = "Needs administrator rights",
        ["Security settings"] = "Changes security settings",
        ["Stays on the machine"] = "Starts on its own",
        ["Downloads or loads code"] = "Runs code from outside the app",
        ["Runs system tools"] = "Starts other programs",
        ["Machine-wide changes"] = "Changes the whole computer",
        ["Network"] = "Talks to other computers",
        ["Personal data"] = "Reads or sends personal data"
    };

    public static Topic TopicOf(DeclarationTopic id) => Topics.First(t => t.Id == id);

    // ================= Network use through a started program =================

    /// <summary>
    /// Programs that fetch from the internet when an app starts them. Only dotnet for now: a build, publish
    /// or restore pulls the packages its cache doesn't hold from nuget.org. More are added when an app needs them.
    /// </summary>
    private static readonly string[] FetchingToolNames = ["dotnet"];

    private static readonly Regex StartsPrograms = new(@"\bProcessStartInfo\b|\bProcess\s*\.\s*Start\b", RegexOptions.Compiled);

    /// <summary>
    /// An app that starts dotnet talks to other computers through it, though it makes no network call itself.
    /// The program is rarely started by its bare name: its path is looked up and handed on in a variable. So
    /// what is looked for is code that names the tool in a string (alone, or as the last part of a path) in
    /// an app that starts programs at all.
    /// </summary>
    private static void FindFetchingTools(IReadOnlyList<Source> sources, Found found)
    {
        if (!sources.Any(s => StartsPrograms.IsMatch(s.Masked))) return;

        foreach (var tool in FetchingToolNames)
            foreach (var source in sources)
            {
                var named = source.Literals.FirstOrDefault(l =>
                {
                    var value = source.Value(l).Trim().Trim('"').Replace('\\', '/');
                    var last = value[(value.LastIndexOf('/') + 1)..];
                    return last.Equals(tool, StringComparison.OrdinalIgnoreCase) || last.Equals(tool + ".exe", StringComparison.OrdinalIgnoreCase);
                });
                if (named is null) continue;
                found.FetchingTools.Add((tool, source.File.RelPath, source.LineOf(named.Start)));
                break;
            }
    }

    // ================= Linux =================

    /// <summary>The programs a Linux app asks for admin rights through. Starting one is what the manifest is on Windows.</summary>
    public static readonly IReadOnlyList<string> LinuxElevators = ["sudo", "pkexec", "doas", "su"];

    /// <summary>
    /// The topics whose Linux form the check can't see, or only in part, and what that part is. Most of what
    /// is matched for them are Windows names (netsh, the registry, Program Files), which find nothing in a
    /// Linux app whatever it does. So for these, a declared line that isn't borne out is not called out of
    /// date on Linux, and every Linux app's findings say what wasn't looked at.
    /// </summary>
    public static readonly IReadOnlyDictionary<DeclarationTopic, string> LinuxBlindSpots = new Dictionary<DeclarationTopic, string>
    {
        [DeclarationTopic.NeedsAdministratorRights] = "only a start of sudo, pkexec, doas or su is seen",
        [DeclarationTopic.ChangesSecuritySettings] = "only Windows' firewall, Defender, audit and registry forms are known, none of Linux's (ufw, iptables, nftables, SELinux, AppArmor, polkit)",
        [DeclarationTopic.StartsOnItsOwn] = "only systemctl enable, crontab and a file under ~/.config/autostart are seen, besides the Windows forms",
        [DeclarationTopic.ChangesTheWholeComputer] = "only Windows' machine-wide places are known (the registry, Program Files, ProgramData), not /etc, /usr, /opt or /var"
    };

    private static readonly Regex SystemctlEnable = Words(@"^(?:enable|reenable|link)$");
    private static readonly Regex AutostartFolder = Words(@"\.config[/\\]+autostart\b|/etc/xdg/autostart\b");

    /// <summary>
    /// What a Linux app does in Linux's own way, as far as it is cheap and sure to see: admin rights asked
    /// for by starting sudo or pkexec, and code that stays (a systemd unit enabled, a crontab installed, an
    /// autostart entry written). A tool's arguments are often added one by one (ArgumentList.Add), so they
    /// are looked for in the whole method, not only in the statement that starts the tool.
    /// </summary>
    private static void FindLinux(Source s, List<Start> starts, Found found)
    {
        foreach (var start in starts)
        {
            if (start.Tool is null) continue;
            if (LinuxElevators.Contains(start.Tool, StringComparer.OrdinalIgnoreCase))
                found.Add(DeclarationTopic.NeedsAdministratorRights, start.Tool, "asks for admin rights", s, start.Position);

            if (start.Tool.Equals("crontab", StringComparison.OrdinalIgnoreCase))
                found.Add(DeclarationTopic.StartsOnItsOwn, "crontab", "a cron job", s, start.Position);

            if (start.Tool.Equals("systemctl", StringComparison.OrdinalIgnoreCase))
            {
                var method = s.MethodAt(start.Position);
                bool enables = s.Literals.Any(l => s.MethodAt(l.Start) == method
                                                   && s.Value(l).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(w => SystemctlEnable.IsMatch(w.Trim('"'))));
                if (enables) found.Add(DeclarationTopic.StartsOnItsOwn, "systemctl enable", "a systemd unit", s, start.Position);
            }
        }

        // A file written under the autostart folder starts with the desktop session.
        foreach (var literal in s.Literals)
        {
            var value = s.Value(literal);
            if (!value.Contains("autostart", StringComparison.OrdinalIgnoreCase) || !AutostartFolder.IsMatch(value)) continue;
            var method = s.MethodAt(literal.Start);
            if (FileWrite.Matches(s.Masked).Any(w => s.MethodAt(w.Index) == method))
                found.Add(DeclarationTopic.StartsOnItsOwn, "~/.config/autostart", "an autostart entry", s, literal.Start);
        }
    }

    // ================= The README section =================

    /// <param name="Line">1-based line number in the README.</param>
    /// <param name="Topic">Null when the label is none of the topics.</param>
    public sealed record LabelLine(int Line, string Label, DeclarationTopic? Topic, string Text);

    /// <param name="Line">1-based line number of the heading.</param>
    public sealed record Section(int Line, IReadOnlyList<LabelLine> Labels);

    private static readonly Regex HeadingLine = new(@"^ {0,3}(?<level>#{1,6})(?:[ \t]+(?<text>.*?))?[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex Fence = new(@"^ {0,3}(?<fence>`{3,}|~{3,})", RegexOptions.Compiled);

    // "- **Label:** text" or "- **Label**: text". The colon is required, so an ordinary bold word isn't a label.
    private static readonly Regex Bullet = new(
        @"^\s*[-*+][ \t]+\*\*(?<label>[^*]+?)\s*(?::\*\*|\*\*\s*:)\s*(?<text>.*)$", RegexOptions.Compiled);

    /// <summary>The section, or null when the README has none. Headings and bullets inside fenced code blocks don't count.</summary>
    public static Section? ReadSection(string readme)
    {
        var lines = readme.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string? fence = null;
        int sectionLevel = 0, sectionLine = 0;
        List<LabelLine>? labels = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            var f = Fence.Match(line);
            if (fence is null && f.Success) { fence = f.Groups["fence"].Value; continue; }
            if (fence is not null)
            {
                if (f.Success && f.Groups["fence"].Value[0] == fence[0] && f.Groups["fence"].Length >= fence.Length
                    && line.Trim().Trim(fence[0]).Length == 0)
                    fence = null;
                continue;
            }

            var h = HeadingLine.Match(line);
            if (h.Success)
            {
                int level = h.Groups["level"].Length;
                if (labels is not null && level <= sectionLevel) break;   // the next section of the same or a higher level
                if (labels is null && IsSectionHeading(h.Groups["text"].Value))
                {
                    labels = [];
                    sectionLevel = level;
                    sectionLine = i + 1;
                }
                continue;
            }

            if (labels is null) continue;
            var b = Bullet.Match(line);
            if (!b.Success) continue;

            var label = Regex.Replace(b.Groups["label"].Value.Trim(), @"\s+", " ");
            var topic = Topics.FirstOrDefault(t => t.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
            labels.Add(new LabelLine(i + 1, label, topic?.Id, b.Groups["text"].Value.Trim()));
        }

        return labels is null ? null : new Section(sectionLine, labels);
    }

    /// <summary>
    /// Where new declaration lines go, as a line index to insert before, and whether the section has to be
    /// added first. With a section: right after its last labelled bullet, or after its last line when it
    /// has none (then BlankBefore asks for an empty line first). Without one: before the first
    /// second-level heading that starts with building or running (Build, Install, Requirements, Usage,
    /// Getting started…), or
    /// else before the first second-level heading, so it sits near the top as its name says; or at the end.
    /// Headings inside fenced code blocks don't count.
    /// </summary>
    public static (int Before, bool AddSection, bool BlankBefore) InsertionPoint(IReadOnlyList<string> lines)
    {
        string? fence = null;
        int sectionLevel = 0, lastInSection = -1, lastBullet = -1, firstLevel2 = -1, firstBuild = -1;
        bool inSection = false;

        (int, bool, bool) InSection() => lastBullet >= 0 ? (lastBullet + 1, false, false) : (lastInSection + 1, false, true);
        int End() => lines.Count > 0 && lines[^1].Length == 0 ? lines.Count - 1 : lines.Count;   // before a final newline

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var f = Fence.Match(line);
            if (fence is null && f.Success) { fence = f.Groups["fence"].Value; if (inSection) lastInSection = i; continue; }
            if (fence is not null)
            {
                if (f.Success && f.Groups["fence"].Value[0] == fence[0] && f.Groups["fence"].Length >= fence.Length
                    && line.Trim().Trim(fence[0]).Length == 0)
                    fence = null;
                if (inSection) lastInSection = i;
                continue;
            }

            var h = HeadingLine.Match(line);
            if (h.Success)
            {
                int level = h.Groups["level"].Length;
                if (inSection)
                {
                    if (level <= sectionLevel) return InSection();
                    lastInSection = i;
                    continue;
                }
                if (IsSectionHeading(h.Groups["text"].Value)) { inSection = true; sectionLevel = level; lastInSection = i; continue; }
                if (level == 2 && firstLevel2 < 0) firstLevel2 = i;
                if (level == 2 && firstBuild < 0 && BuildHeading.IsMatch(h.Groups["text"].Value)) firstBuild = i;
                continue;
            }
            if (inSection && line.Trim().Length > 0)
            {
                lastInSection = i;
                if (Bullet.IsMatch(line)) lastBullet = i;
            }
        }

        if (inSection) return InSection();
        if (firstBuild >= 0) return (firstBuild, true, false);
        if (firstLevel2 >= 0) return (firstLevel2, true, false);
        return (End(), true, false);
    }

    /// <summary>
    /// Removes the section's heading when nothing is left under it but blank lines, with the blank lines
    /// after it. Returns the heading removed, if any.
    /// </summary>
    public static List<string> RemoveEmptySection(List<string> lines)
    {
        string? fence = null;
        for (int i = 0; i < lines.Count; i++)
        {
            var f = Fence.Match(lines[i]);
            if (fence is null && f.Success) { fence = f.Groups["fence"].Value; continue; }
            if (fence is not null)
            {
                if (f.Success && f.Groups["fence"].Value[0] == fence[0] && lines[i].Trim().Trim(fence[0]).Length == 0) fence = null;
                continue;
            }
            var h = HeadingLine.Match(lines[i]);
            if (!h.Success || !IsSectionHeading(h.Groups["text"].Value)) continue;

            int level = h.Groups["level"].Length, end = i + 1;
            while (end < lines.Count && lines[end].Trim().Length == 0) end++;
            bool empty = end == lines.Count || HeadingLine.Match(lines[end]) is { Success: true } next && next.Groups["level"].Length <= level;
            if (!empty) return [];
            var heading = lines[i];
            // Keep one blank line between what came before and what comes after.
            int from = i, to = end;
            if (from > 0 && lines[from - 1].Trim().Length == 0 && to < lines.Count) from--;
            lines.RemoveRange(from, to - from);
            return [heading];
        }
        return [];
    }

    private static readonly Regex BuildHeading = new(
        @"^\s*(?:build(?:ing)?|install(?:ing|ation)?|requirements?|prerequisites|usage|getting started|how to (?:use|run|build)|running|setup|set up)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsSectionHeading(string text)
    {
        // Trailing #s, spaces, colons and full stops are ignored, as in RepoLegibility.
        var t = text.TrimEnd();
        t = Regex.Replace(t, @"[ \t]+#+$", "");
        t = t.TrimEnd(' ', '\t', '#', ':', '.');
        return Regex.Replace(t, @"\s+", " ").Equals(Heading, StringComparison.OrdinalIgnoreCase);
    }

    // ================= The code =================

    /// <param name="What">What was found, for the detail: "netsh advfirewall", "HttpClient".</param>
    /// <param name="Kind">What it stands for, for the line to paste: a tool's name, a host, "firewall".</param>
    public sealed record Evidence(DeclarationTopic Topic, string What, string Kind, string File, int Line);

    public sealed class Found
    {
        public List<Evidence> Evidence { get; } = [];

        /// <summary>Programs started whose name the check can't read, as "file line N".</summary>
        public List<string> UnnamedStarts { get; } = [];

        /// <summary>The hosts written in the files that use the network, for the detail and the line to paste.</summary>
        public List<string> Hosts { get; } = [];

        /// <summary>
        /// Programs the app starts that fetch from the internet on its behalf (dotnet restoring packages), each
        /// with where the code names it. Not evidence that demands a declaration: it bears out a README that
        /// declares "Talks to other computers" for an app that makes no network call of its own.
        /// </summary>
        public List<(string Tool, string File, int Line)> FetchingTools { get; } = [];

        public IEnumerable<Evidence> Of(DeclarationTopic topic) => Evidence.Where(e => e.Topic == topic);

        public bool Touches(DeclarationTopic topic) => Evidence.Any(e => e.Topic == topic);

        internal void Add(DeclarationTopic topic, string what, string kind, Source source, int position) =>
            Evidence.Add(new Evidence(topic, what, kind, source.File.RelPath, source.LineOf(position)));
    }

    /// <summary>The programs the check knows by name. Lower case; a start of anything else is listed by its literal name.</summary>
    private static readonly HashSet<string> KnownTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "netsh", "powershell", "pwsh", "cmd", "reg", "sc", "schtasks", "gpupdate", "auditpol", "bcdedit", "vssadmin",
        "wmic", "ipconfig", "wevtutil", "icacls", "takeown", "msiexec", "regsvr32", "rundll32", "certutil", "mshta",
        "wscript", "cscript", "dism", "sfc", "shutdown", "taskkill", "tasklist", "bitsadmin", "winget", "net",
        "netstat", "route", "arp", "nslookup", "whoami", "robocopy", "xcopy", "diskpart", "manage-bde", "cipher"
    };

    /// <summary>Tool names that are also everyday method names: a helper only counts with a prefix or suffix (RunReg, NetCommand).</summary>
    private static readonly HashSet<string> Ambiguous = new(StringComparer.OrdinalIgnoreCase)
    {
        "shutdown", "route", "net", "cipher", "arp", "sc", "reg", "cmd", "sfc", "dism"
    };

    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase) { "cmd", "powershell", "pwsh" };

    /// <summary>What a tool's arguments must contain for its start to change something, by topic.</summary>
    private sealed record Command(string Tool, Regex Arguments, DeclarationTopic Topic, string What, string Kind);

    private static Regex Words(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Command[] Commands =
    [
        new("netsh", Words(@"\badvfirewall\b.*\b(?:set|add|delete|import|reset)\b|\bfirewall\s+(?:set|add|delete)\b"),
            DeclarationTopic.ChangesSecuritySettings, "netsh advfirewall", "firewall"),
        new("auditpol", Words(@"/(?:set|clear|remove|restore)\b"), DeclarationTopic.ChangesSecuritySettings, "auditpol /set", "audit policy"),
        new("bcdedit", Words(@"/(?:set|deletevalue|delete|create|import)\b"), DeclarationTopic.ChangesSecuritySettings, "bcdedit", "boot settings"),
        new("vssadmin", Words(@"\b(?:delete\s+shadows|resize\s+shadowstorage)\b"), DeclarationTopic.ChangesSecuritySettings, "vssadmin delete", "shadow copies"),
        new("schtasks", Words(@"/(?:create|change)\b"), DeclarationTopic.StartsOnItsOwn, "schtasks /create", "a scheduled task"),
        new("sc", Words(@"^\s*create\b|\screate\s"), DeclarationTopic.StartsOnItsOwn, "sc create", "a service"),
        new("reg", Words(@"\bimport\b"), DeclarationTopic.ChangesTheWholeComputer, "reg import", "the registry under HKLM"),
        new("reg", Words(@"\b(?:add|delete)\b.*\b(?:HKLM|HKEY_LOCAL_MACHINE)\b|\b(?:HKLM|HKEY_LOCAL_MACHINE)\b.*\b(?:add|delete)\b"),
            DeclarationTopic.ChangesTheWholeComputer, "reg add HKLM", "the registry under HKLM")
    ];

    /// <summary>PowerShell commands, counted in a file that starts PowerShell (scripts are often built over several lines).</summary>
    private static readonly (Regex Pattern, DeclarationTopic Topic, string What, string Kind)[] Cmdlets =
    [
        (Words(@"\b(?:Set|Add|Remove)-MpPreference\b"), DeclarationTopic.ChangesSecuritySettings, "Set-MpPreference", "Defender"),
        (Words(@"\b(?:New|Set|Remove|Enable|Disable)-NetFirewall\w+\b"), DeclarationTopic.ChangesSecuritySettings, "NetFirewall cmdlets", "firewall"),
        (Words(@"\bRegister-ScheduledTask\b"), DeclarationTopic.StartsOnItsOwn, "Register-ScheduledTask", "a scheduled task"),
        (Words(@"\bNew-Service\b"), DeclarationTopic.StartsOnItsOwn, "New-Service", "a service"),
        // Invoke-Expression runs text as code. -EncodedCommand isn't here: an encoded script is about how
        // readable the code is, which RepoLegibility judges, not about where the code comes from.
        (Words(@"\bInvoke-Expression\b|(?:^|[\s;|(])iex\s"), DeclarationTopic.RunsCodeFromOutside, "Invoke-Expression", "PowerShell Invoke-Expression"),
    ];

    /// <summary>Registry paths that hold security settings. They count where the same method writes to the registry.</summary>
    private static readonly (Regex Pattern, string What, string Kind)[] SecurityPaths =
    [
        (Words(@"Policies\\+Microsoft\\+WindowsFirewall"), @"HKLM\…\WindowsFirewall", "firewall"),
        (Words(@"SharedAccess\\+Parameters\\+FirewallPolicy"), @"HKLM\…\FirewallPolicy", "firewall"),
        (Words(@"\\+Windows Defender\b"), @"…\Windows Defender", "Defender"),
        (Words(@"CurrentVersion\\+Policies\\+System\\+Audit\b"), @"…\Policies\System\Audit", "audit policy"),
        (Words(@"CurrentVersion\\+Policies\\+System\b(?!\\+Audit)"), @"…\Policies\System (UAC)", "UAC")
    ];

    private static readonly Regex RunKey = Words(@"CurrentVersion\\+Run(?:Once)?\\*$");

    /// <summary>
    /// A literal that tells what the code does, where it is used the way it says: a COM ProgID handed to
    /// GetTypeFromProgID, a browser profile path used as a path. Named anywhere else (a list of patterns,
    /// a message), it counts for nothing. Task Scheduler XML counts on its own: nothing else looks like it.
    /// </summary>
    private static readonly Regex ProgIdUse = new(@"\b(?:GetTypeFromProgID|GetTypeFromCLSID|CreateObject)\b", RegexOptions.Compiled);
    private static readonly Regex PathUse = new(@"\b(?:Path\.\w+|File\.\w+|Directory\.\w+|GetFolderPath|new\s+(?:DirectoryInfo|FileInfo|FileStream|StreamReader))\b", RegexOptions.Compiled);

    private enum Use { Anywhere, ProgId, Path }

    private static readonly (Regex Pattern, DeclarationTopic Topic, string What, string Kind, Use Use)[] TellingLiterals =
    [
        (Words(@"^HNetCfg\.FwPolicy2$"), DeclarationTopic.ChangesSecuritySettings, "HNetCfg.FwPolicy2", "firewall", Use.ProgId),
        (Words(@"^Schedule\.Service$"), DeclarationTopic.StartsOnItsOwn, "Schedule.Service", "a scheduled task", Use.ProgId),
        (Words(@"<Task\b[^>]*schemas\.microsoft\.com/windows/2004/02/mit/task"), DeclarationTopic.StartsOnItsOwn, "Task Scheduler XML", "a scheduled task", Use.Anywhere),
        (Words(@"Google\\+Chrome\\+User Data|Google/Chrome/User Data"), DeclarationTopic.ReadsOrSendsPersonalData, "Chrome profile", "browser profiles", Use.Path),
        (Words(@"Mozilla\\+Firefox\\+Profiles|Mozilla/Firefox/Profiles"), DeclarationTopic.ReadsOrSendsPersonalData, "Firefox profile", "browser profiles", Use.Path),
        (Words(@"Microsoft\\+Edge\\+User Data|Microsoft/Edge/User Data"), DeclarationTopic.ReadsOrSendsPersonalData, "Edge profile", "browser profiles", Use.Path)
    ];

    /// <summary>
    /// Calls and types matched in code (strings and comments blanked), always by the shape of the use:
    /// new X(…), X.Member, an import of a Windows function. A method, class or variable that is only named
    /// like an API (a method called NetworkCredential, a field called registry) is not evidence.
    /// </summary>
    private static readonly (Regex Pattern, DeclarationTopic Topic, string Kind)[] CodePatterns =
    [
        (new(@"\bnew\s+(?:System\.Net\.(?:Http\.|Sockets\.|Mail\.|NetworkInformation\.)?)?(?<what>HttpClient|WebClient|TcpClient|TcpListener|UdpClient|Socket|SmtpClient|Ping|HttpListener|ClientWebSocket)\s*\(", RegexOptions.Compiled),
            DeclarationTopic.TalksToOtherComputers, "network"),
        (new(@"\b(?<what>(?:Http)?WebRequest\.Create(?:Http)?|Dns\.GetHost\w*|Dns\.Resolve|WebApplication\.Create(?:Builder)?)\b", RegexOptions.Compiled),
            DeclarationTopic.TalksToOtherComputers, "network"),
        // A field or parameter of the factory type, and a web server set up with Kestrel.
        (new(@"\b(?<what>IHttpClientFactory)\s+[A-Za-z_]\w*|\.\s*(?<what>UseKestrel)\s*\(", RegexOptions.Compiled),
            DeclarationTopic.TalksToOtherComputers, "network"),
        (new(@"\b(?<what>Assembly\.(?:LoadFrom|LoadFile|UnsafeLoadFrom)|NativeLibrary\.(?:Load|TryLoad)|CSharpCompilation\.Create)\b", RegexOptions.Compiled),
            DeclarationTopic.RunsCodeFromOutside, "loads code at run time"),
        // A load context made or derived from, or asked to load a file; a method emitted at run time.
        (new(@"\bnew\s+(?:[\w.]+\.)?(?<what>AssemblyLoadContext|DynamicMethod)\s*\(|:\s*(?:[\w.]+\.)?(?<what>AssemblyLoadContext)\b|\.\s*(?<what>LoadFromAssemblyPath|LoadFromStream|LoadFromNativeImagePath)\s*\(", RegexOptions.Compiled),
            DeclarationTopic.RunsCodeFromOutside, "loads code at run time"),
        (new(@"\bnew\s+(?:[\w.]+\.)?(?<what>ServiceInstaller|ServiceProcessInstaller)\s*\(", RegexOptions.Compiled),
            DeclarationTopic.StartsOnItsOwn, "a service"),
        (new(@"\b(?<what>Environment\.UserName)\b", RegexOptions.Compiled), DeclarationTopic.ReadsOrSendsPersonalData, "the Windows user name"),
        // A credential made, the process's own credentials, or the Credential Manager imported (CredRead, CredEnumerate).
        (new(@"\bnew\s+(?:[\w.]+\.)?(?<what>NetworkCredential|CredentialCache)\s*\(|\b(?<what>CredentialCache\.Default(?:Network)?Credentials)\b|\b(?:extern|partial)\s+[^;{(]*?\b(?<what>Cred(?:Read|Enumerate)\w*)\s*\(", RegexOptions.Compiled),
            DeclarationTopic.ReadsOrSendsPersonalData, "stored credentials"),
        (new(@"\b(?<what>ProtectedData\.(?:Protect|Unprotect))\b", RegexOptions.Compiled), DeclarationTopic.ReadsOrSendsPersonalData, "protected data (DPAPI)"),
        (new(@"\b(?<what>Clipboard\.(?:GetText|GetData|GetDataObject|GetFileDropList|GetImage))\b", RegexOptions.Compiled),
            DeclarationTopic.ReadsOrSendsPersonalData, "the clipboard")
    ];

    // An Assembly.Load whose argument is code, not a single string literal: loading bytes, not a name.
    private static readonly Regex AssemblyLoad = new(@"\bAssembly\.Load\s*\(", RegexOptions.Compiled);
    private static readonly Regex Identity = new(@"\bWindowsIdentity\.GetCurrent\s*\(", RegexOptions.Compiled);
    private static readonly Regex IdentityRead = new(@"\.\s*(?:Name|User|Owner)\b", RegexOptions.Compiled);

    // Starts with the method name, so the engine can jump from one candidate to the next.
    private static readonly Regex RegistryWrite = new(
        @"\b(?:CreateSubKey|SetValue|DeleteValue|DeleteSubKey|DeleteSubKeyTree|SetAccessControl)\s*\(|\bOpenSubKey\s*\([^;]*?,\s*(?:writable\s*:\s*)?(?:true|RegistryKeyPermissionCheck\.ReadWriteSubTree|RegistryRights\.\w+)",
        RegexOptions.Compiled);
    private static readonly Regex LocalMachine = new(@"\b(?:Registry\.LocalMachine|RegistryHive\.LocalMachine)\b", RegexOptions.Compiled);
    private static readonly Regex MachineFolder = new(
        @"\bSpecialFolder\.(?<what>ProgramFiles|ProgramFilesX86|CommonProgramFiles|CommonProgramFilesX86|CommonApplicationData|Windows|System|SystemX86)\b",
        RegexOptions.Compiled);
    private static readonly Regex StartupFolder = new(@"\bSpecialFolder\.(?:Common)?Startup\b", RegexOptions.Compiled);
    private static readonly Regex FileWrite = new(
        @"\b(?:File\.(?:Write\w*|Create\w*|Copy|Move|AppendAll\w*|Replace)|Directory\.CreateDirectory|new\s+(?:StreamWriter|FileStream)|CreateShortcut)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex MachineStore = new(@"\bStoreLocation\.LocalMachine\b", RegexOptions.Compiled);
    private static readonly Regex StoreWrite = new(@"\bOpenFlags\.(?:ReadWrite|MaxAllowed)\b", RegexOptions.Compiled);
    private static readonly Regex Download = new(@"\b(?:DownloadFile\w*|DownloadData\w*|GetByteArrayAsync|GetStreamAsync)\s*\(", RegexOptions.Compiled);

    private static readonly Regex ProcessStart = new(@"\bProcess\s*\.\s*Start\s*\(", RegexOptions.Compiled);
    private static readonly Regex NewStartInfo = new(@"\bnew\s+(?:System\.Diagnostics\.)?ProcessStartInfo\s*(?<paren>\()?", RegexOptions.Compiled);
    private static readonly Regex FileNameSet = new(@"\bFileName\s*=(?!=)", RegexOptions.Compiled);
    private static readonly Regex VerbRunas = new(@"\bVerb\s*=(?!=)", RegexOptions.Compiled);
    private static readonly Regex IdentifierToken = new(@"(?<![\w@])@?[A-Za-z_]\w*", RegexOptions.Compiled);
    private static readonly Regex ConstantDeclaration = new(
        @"\b(?:const\s+string|static\s+readonly\s+string)\s+(?<name>[A-Za-z_]\w*)\s*=", RegexOptions.Compiled);


    private static readonly Regex Url = new(@"\b(?:https?|wss?|ftp)://(?<host>[A-Za-z0-9.-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BareHost = new(@"^(?:[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?\.)+(?<tld>[A-Za-z]{2,24})$", RegexOptions.Compiled);

    /// <summary>
    /// Top-level domains a bare host name must end in, so a resource key such as "icon.rdp" or
    /// "brush.textdim" isn't taken for a host. A URL names its host whatever the ending.
    /// </summary>
    private static readonly HashSet<string> Tlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "net", "org", "io", "ai", "dev", "app", "co", "gov", "edu", "mil", "int", "info", "biz", "me", "tv", "cloud",
        "tech", "online", "site", "xyz", "sh", "gg", "so", "to", "ly", "fm", "us", "uk", "de", "at", "ch", "eu", "fr", "nl",
        "be", "lu", "it", "es", "pt", "se", "no", "dk", "fi", "is", "ie", "pl", "cz", "sk", "hu", "ro", "bg", "gr", "ru", "ua",
        "tr", "il", "in", "jp", "cn", "kr", "tw", "hk", "sg", "au", "nz", "ca", "mx", "br", "ar", "cl", "za", "local", "lan",
        "arpa", "test", "example"
    };
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "exe", "dll", "txt", "log", "xml", "reg", "wfw", "png", "ico", "jpg", "jpeg", "gif", "bmp", "svg", "cs", "md",
        "zip", "csv", "ini", "config", "manifest", "xaml", "csproj", "props", "targets", "sln", "slnx", "ps1", "bat", "cmd",
        "html", "htm", "css", "js", "pdf", "msi", "sys", "inf", "cat", "evtx", "etl", "db", "sqlite", "bak", "tmp", "lnk",
        "resx", "baml", "pdb", "nupkg", "yml", "yaml", "toml", "dat", "bin", "wav", "mp3", "mp4", "ttf", "otf", "woff", "woff2"
    };

    private static readonly Regex TypeHeader = new(@"\b(?:class|struct|record|interface|enum|namespace)\b", RegexOptions.Compiled);

    /// <summary>One C# file, lexed, with what the detectors need.</summary>
    internal sealed class Source
    {
        public required SnapshotFile File { get; init; }
        public required string Text { get; init; }
        public required string Masked { get; init; }
        public required IReadOnlyList<CSharpLexer.Literal> Literals { get; init; }
        public required int[] LineStarts { get; init; }
        private readonly Dictionary<int, string> _methods = new();

        public int LineOf(int position)
        {
            int i = Array.BinarySearch(LineStarts, position);
            return (i >= 0 ? i : ~i - 1) + 1;
        }

        private readonly Dictionary<int, string> _blocks = new();
        private readonly Dictionary<string, bool> _startInfoVariables = new(StringComparer.Ordinal);

        /// <summary>
        /// The method a position is in, or "" outside any the locator can name. Every position in the same
        /// block of a method has the same answer, so it's worked out once per block; only positions directly
        /// in a type's body (fields, expression-bodied members) are asked one by one.
        /// </summary>
        public string MethodAt(int position)
        {
            if (_methods.TryGetValue(position, out var name)) return name;

            int block = InnermostBlock(position);
            if (block >= 0 && !IsTypeBody(block))
            {
                if (!_blocks.TryGetValue(block, out name))
                    _blocks[block] = name = MethodLocator.Enclosing(Masked, position) ?? "";
            }
            else name = MethodLocator.Enclosing(Masked, position) ?? "";

            _methods[position] = name;
            return name;
        }

        private int InnermostBlock(int position)
        {
            int depth = 0;
            for (int i = Math.Min(position, Masked.Length) - 1; i >= 0; i--)
            {
                char c = Masked[i];
                if (c == '}') depth++;
                else if (c == '{') { if (depth == 0) return i; depth--; }
            }
            return -1;
        }

        private bool IsTypeBody(int brace)
        {
            int start = Masked.LastIndexOfAny([';', '{', '}'], Math.Max(0, brace - 1)) + 1;
            return TypeHeader.IsMatch(Masked.AsSpan(start, brace - start));
        }

        /// <summary>Whether a variable holds a ProcessStartInfo, worked out once per name and file.</summary>
        public bool IsStartInfo(string name)
        {
            if (!_startInfoVariables.TryGetValue(name, out var yes))
                _startInfoVariables[name] = yes = IsStartInfoVariable(Masked, name);
            return yes;
        }

        private Dictionary<int, string>? _values;

        /// <summary>The literal's value, holes shown as spaces. Built once per literal: several detectors read every one.</summary>
        public string Value(CSharpLexer.Literal literal)
        {
            _values ??= new Dictionary<int, string>(Literals.Count);
            if (!_values.TryGetValue(literal.Start, out var value))
                _values[literal.Start] = value = literal.Text(Text, " ");
            return value;
        }

        /// <summary>The literals that start in a range. The list is sorted by position, so this is a binary search.</summary>
        public IEnumerable<CSharpLexer.Literal> LiteralsIn(int start, int end)
        {
            int lo = 0, hi = Literals.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Literals[mid].Start < start) lo = mid + 1; else hi = mid;
            }
            for (int i = lo; i < Literals.Count && Literals[i].Start < end; i++) yield return Literals[i];
        }

        /// <summary>The statement around a position: from the previous ; { } to the next, in code.</summary>
        public (int Start, int End) StatementAt(int position)
        {
            int start = Masked.LastIndexOfAny([';', '{', '}'], Math.Max(0, Math.Min(position, Masked.Length) - 1)) + 1;
            int end = Masked.IndexOfAny([';', '{', '}'], Math.Min(position, Masked.Length));
            return (start, end < 0 ? Masked.Length : end);
        }
    }

    /// <summary>
    /// What the code touches. Admin rights from the manifest are found by the caller (the existing rule
    /// reads the manifest) and passed in, so both read it the same way.
    /// </summary>
    /// <param name="linux">A Linux app: Linux's own ways of asking for admin rights and of staying are looked for as well.</param>
    public static Found ScanCode(IEnumerable<SnapshotFile> files, bool linux = false)
    {
        var sources = files
            .Where(f => f.Extension == ".cs" && f.Text is not null)
            .Select(f => new Source
            {
                File = f,
                Text = f.Text!,
                Masked = CSharpLexer.BlankCommentsAndStrings(f.Text!),
                Literals = CSharpLexer.Literals(f.Text!),
                LineStarts = LineStarts(f.Text!)
            })
            .ToList();

        var found = new Found();
        var constants = Constants(sources);

        // Program starts first: whether the code starts processes at all decides whether a helper named
        // after a tool (Netsh(…), Tool.Schtasks) stands for one.
        var startsBySource = sources.ToDictionary(s => s, s => Starts(s, literalConstructor: linux).ToList());
        bool startsPrograms = startsBySource.Values.Any(l => l.Count > 0);
        var shellFiles = new HashSet<Source>();

        foreach (var (source, starts) in startsBySource)
            foreach (var start in starts)
            {
                if (start.Tool is null)
                {
                    found.UnnamedStarts.Add($"{source.File.RelPath} line {source.LineOf(start.Position)}");
                    continue;
                }
                found.Add(DeclarationTopic.StartsOtherPrograms, start.Tool, start.Tool, source, start.Position);
                if (start.Tool is "powershell" or "pwsh") shellFiles.Add(source);
            }

        foreach (var source in sources)
        {
            var named = startsPrograms ? NamedTools(source).ToList() : [];
            foreach (var (tool, position) in named)
            {
                // A helper named after the tool, or a Tool.X member handed to a helper: counted where it's used.
                found.Add(DeclarationTopic.StartsOtherPrograms, tool, tool, source, position);
                if (tool is "powershell" or "pwsh") shellFiles.Add(source);
            }

            FindCommands(source, named, startsBySource[source], found);
            FindLiterals(source, found);
            FindCode(source, found);
            FindPairs(source, constants, found);
            FindRunAs(source, found);
            if (linux) FindLinux(source, startsBySource[source], found);
        }

        foreach (var source in shellFiles) FindPowerShell(source, found);
        WrittenThenStarted(startsBySource, found);
        FindHosts(sources, found);
        FindFetchingTools(sources, found);

        // The same .NET calls, worded for the system: on Linux there is no "Windows user name".
        if (linux)
            for (int i = 0; i < found.Evidence.Count; i++)
                if (found.Evidence[i].Kind == "the Windows user name")
                    found.Evidence[i] = found.Evidence[i] with { Kind = "the user name" };
        return found;
    }

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        return [.. starts];
    }

    // ---------------- Program starts ----------------

    private sealed record Start(int Position, string? Tool, int ProgramStart, int ProgramEnd);

    /// <summary>
    /// Every Process.Start, new ProcessStartInfo(…) and FileName set on a ProcessStartInfo, with the
    /// program it names. Starting a URL, a folder or Explorer is left out; a program the check can't
    /// name has a null Tool.
    /// </summary>
    /// <param name="literalConstructor">
    /// Also read new ProcessStartInfo("tool"), where the program is one string literal. Windows apps are read
    /// as before 1.0.30, which passed that form over: the literal is blank in the masked text and was taken
    /// for "no argument". Linux apps, new with 1.0.30, are read in full; both of them start xdg-open this way.
    /// </param>
    private static IEnumerable<Start> Starts(Source s, bool literalConstructor)
    {
        var m = s.Masked;
        foreach (Match call in ProcessStart.Matches(m))
        {
            var args = Arguments(m, call.Index + call.Length - 1);
            if (args.Count == 0) continue;
            var (a, b) = args[0];
            var first = m[a..b].Trim();
            // Process.Start(new ProcessStartInfo { … }) and Process.Start(psi): the start info names the program.
            if (first.StartsWith("new ", StringComparison.Ordinal) || IsIdentifier(first) && s.IsStartInfo(first)) continue;
            if (Program(s, a, b) is { } start) yield return start with { Position = call.Index };
        }

        foreach (Match info in NewStartInfo.Matches(m))
        {
            if (!info.Groups["paren"].Success) continue;
            var args = Arguments(m, info.Index + info.Length - 1);
            if (args.Count == 0) continue;
            bool blank = m[args[0].Start..args[0].End].Trim().Length == 0;
            if (blank && !(literalConstructor && s.LiteralsIn(args[0].Start, args[0].End).Any())) continue;
            if (Program(s, args[0].Start, args[0].End) is { } start) yield return start with { Position = info.Index };
        }

        if (!m.Contains("ProcessStartInfo", StringComparison.Ordinal) && !m.Contains("StartInfo", StringComparison.Ordinal)) yield break;
        foreach (Match set in FileNameSet.Matches(m))
        {
            var receiver = CalledOnSomething(m, set.Index) ? ReceiverBefore(m, set.Index) : null;
            bool onStartInfo = receiver is not null
                ? receiver == "StartInfo" || s.IsStartInfo(receiver)
                : InStartInfoInitializer(m, set.Index);
            if (!onStartInfo) continue;

            int a = set.Index + set.Length;
            int b = ExpressionEnd(m, a);
            if (Program(s, a, b) is { } start) yield return start with { Position = set.Index };
        }
    }

    /// <summary>The program an expression names, or null when it opens a URL, a folder or Explorer.</summary>
    private static Start? Program(Source s, int a, int b)
    {
        foreach (var literal in s.LiteralsIn(a, b))
        {
            var value = s.Value(literal).Trim().Trim('"');
            if (value.Length == 0) continue;
            if (value.Contains("://", StringComparison.Ordinal) || value.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                return null;
            var name = ProgramName(value);
            if (name is null) return null;                                     // a folder or a document
            if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return null;
            return new Start(a, name, a, b);
        }

        foreach (Match id in IdentifierToken.Matches(s.Masked[a..b]))
        {
            var word = id.Value.TrimStart('@');
            if (word.Equals("explorer", StringComparison.OrdinalIgnoreCase) || word.Equals("ExplorerExe", StringComparison.OrdinalIgnoreCase))
                return null;
            if (KnownTools.Contains(word)) return new Start(a, word.ToLowerInvariant(), a, b);
        }

        return new Start(a, null, a, b);
    }

    /// <summary>
    /// "netsh", "netsh.exe", "C:\Windows\System32\netsh.exe" → "netsh". Null for a folder or a document,
    /// which open in their own app rather than running a tool.
    /// </summary>
    private static string? ProgramName(string value)
    {
        var first = value.Trim();
        bool hasPath = first.Contains('\\') || first.Contains('/');
        // "cmd /c …" written as one string: the program is the first word.
        if (!hasPath && first.IndexOf(' ') is > 0 and var space) first = first[..space];
        var last = first.TrimEnd('\\', '/').Split('\\', '/')[^1].Trim();
        if (last.Length == 0) return null;
        var dot = last.LastIndexOf('.');
        var ext = dot < 0 ? "" : last[(dot + 1)..].ToLowerInvariant();
        if (ext.Length == 0) return hasPath ? null : last.ToLowerInvariant();
        return ext is "exe" or "com" or "bat" or "cmd" or "msc" or "ps1" or "vbs"
            ? (ext is "exe" or "com" ? last[..dot] : last).ToLowerInvariant()
            : null;
    }

    private static bool IsIdentifier(string text) =>
        text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') && text.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static bool IsStartInfoVariable(string masked, string name) =>
        IsIdentifier(name)
        && Regex.IsMatch(masked, $@"\bProcessStartInfo\s+{name}\b|\b{name}\s*=\s*new\s+(?:System\.Diagnostics\.)?ProcessStartInfo\b|\bvar\s+{name}\s*=\s*new\s+(?:System\.Diagnostics\.)?ProcessStartInfo\b");

    /// <summary>Whether a position is inside the initializer of new ProcessStartInfo { … } (or a Process's StartInfo = new() { … }).</summary>
    private static bool InStartInfoInitializer(string m, int position)
    {
        int depth = 0;
        for (int i = position - 1; i >= 0; i--)
        {
            char c = m[i];
            if (c is '}' or ')') depth++;
            else if (c == '(') { if (depth > 0) depth--; }
            else if (c == '{')
            {
                if (depth > 0) { depth--; continue; }
                var head = m[Math.Max(0, i - 120)..i].TrimEnd();
                return Regex.IsMatch(head, @"ProcessStartInfo\s*(?:\(\s*\))?$|StartInfo\s*=\s*new\s*(?:\(\s*\))?$");
            }
            else if (c == ';' && depth == 0) return false;
        }
        return false;
    }

    // ---------------- Tools named in code ----------------

    /// <summary>
    /// Helpers and members named after a known tool, where they're used: a call such as Netsh(…) or
    /// RunPowerShell(…), and a member such as Tool.Schtasks (handed to a helper that starts it). The
    /// helper's own definition doesn't count, nor does a plain variable.
    /// </summary>
    private static IEnumerable<(string Tool, int Position)> NamedTools(Source s)
    {
        var m = s.Masked;
        foreach (var (start, length) in Identifiers(m))
        {
            if (length < 2) continue;
            int after = start + length;
            while (after < m.Length && m[after] is ' ' or '\t') after++;
            bool isCall = after < m.Length && m[after] == '(';
            bool isMember = start > 0 && m[start - 1] == '.';
            if (!isCall && !isMember) continue;

            var span = m.AsSpan(start, length);
            string? tool = null;
            if (isMember && KnownToolNames.TryGetValue(span, out var known))
            {
                if (!Ambiguous.Contains(known) || QualifierBefore(m, start - 1).Contains("Tool", StringComparison.Ordinal)) tool = known;
            }
            else if (isCall && HelperTool(span) is { } name && !IsDefinition(m, start))
                tool = name;
            if (tool is not null) yield return (tool.ToLowerInvariant(), start);
        }
    }

    /// <summary>The identifiers in masked code, as spans: a plain scan, far cheaper than a regex match per word.</summary>
    private static IEnumerable<(int Start, int Length)> Identifiers(string m)
    {
        int i = 0;
        while (i < m.Length)
        {
            char c = m[i];
            if (char.IsAsciiLetter(c) || c == '_' || c > 127 && char.IsLetter(c))
            {
                int start = i;
                while (i < m.Length && IsWordChar(m[i])) i++;
                yield return (start, i - start);
            }
            else if (char.IsAsciiDigit(c))
                while (i < m.Length && IsWordChar(m[i])) i++;
            else i++;
        }
    }

    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_' || c > 127 && char.IsLetterOrDigit(c);

    private static readonly string[] HelperPrefixes = ["Run", "Start", "Call", "Invoke", "Exec", "Execute", "Launch"];
    private static readonly string[] HelperSuffixes = ["Async", "Command", "Cmd", "Process", "Exe"];
    private static readonly string[] ClearPrefixes = ["Run", "Exec", "Execute", "Launch"];
    private static readonly string[] ClearSuffixes = ["Command", "Cmd", "Exe", "Process"];

    /// <summary>Answers per name: the same call names come up again and again, in every file and every scan.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> HelperTools = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> HelperToolsBySpan =
        HelperTools.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> KnownToolNames = KnownTools.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The tool a helper's name stands for: Netsh, RunNetsh, NetshAsync, PowerShellCommand. Null for anything else.</summary>
    private static string? HelperTool(ReadOnlySpan<char> word)
    {
        if (!HelperToolsBySpan.TryGetValue(word, out var tool))
        {
            var name = word.ToString();
            tool = HelperTools.GetOrAdd(name, static w => FindHelperTool(w) ?? "");
        }
        return tool.Length == 0 ? null : tool;
    }

    private static string? FindHelperTool(string word)
    {
        foreach (var prefix in HelperPrefixes.Prepend(""))
        {
            if (!word.StartsWith(prefix, StringComparison.Ordinal) || word.Length == prefix.Length) continue;
            var rest = word[prefix.Length..];
            foreach (var suffix in HelperSuffixes.Prepend(""))
            {
                if (!rest.EndsWith(suffix, StringComparison.Ordinal) || rest.Length == suffix.Length) continue;
                var name = rest[..^suffix.Length];
                if (name.Length < 3 || !KnownTools.Contains(name)) continue;
                // An everyday word (Shutdown, Route) only counts with a clear run prefix or suffix: InvokeShutdown is WPF's.
                bool clearlyRuns = ClearPrefixes.Contains(prefix) || ClearSuffixes.Contains(suffix);
                if (clearlyRuns || !Ambiguous.Contains(name)) return name;
            }
        }
        return null;
    }

    /// <summary>Whether a name is reached through a dot: key.SetValue, psi.FileName.</summary>
    private static bool CalledOnSomething(string m, int index)
    {
        int i = index - 1;
        while (i >= 0 && m[i] is ' ' or '\t' or '\r' or '\n') i--;
        return i >= 0 && m[i] == '.';
    }

    /// <summary>The identifier before the dot in front of a name: "psi" in psi.FileName.</summary>
    private static string ReceiverBefore(string m, int index)
    {
        int i = index - 1;
        while (i >= 0 && m[i] is ' ' or '\t' or '\r' or '\n') i--;
        return QualifierBefore(m, i);
    }

    /// <summary>The identifier before a dot: "Tool" in WindowsTools.Tool.Netsh.</summary>
    private static string QualifierBefore(string m, int dot)
    {
        int end = dot;
        int start = end;
        while (start > 0 && (char.IsLetterOrDigit(m[start - 1]) || m[start - 1] == '_')) start--;
        return m[start..end];
    }

    /// <summary>Whether name( is a method being declared rather than called: a type or modifier stands before it.</summary>
    private static bool IsDefinition(string m, int index)
    {
        int i = index - 1;
        while (i >= 0 && m[i] is ' ' or '\t' or '\r' or '\n') i--;
        if (i < 0) return false;
        char c = m[i];
        if (c is '?' or '>' or ']') return true;
        if (!char.IsLetterOrDigit(c) && c != '_') return false;
        int end = i + 1;
        while (i >= 0 && (char.IsLetterOrDigit(m[i]) || m[i] == '_')) i--;
        var word = m[(i + 1)..end];
        if (i >= 0 && m[i] == '.') return false;   // a qualified type name can't precede a declaration's name directly
        return word is not ("return" or "await" or "else" or "throw" or "in" or "yield" or "case" or "new" or "is" or "as"
            or "when" or "and" or "or" or "not" or "nameof" or "typeof" or "out" or "ref");
    }

    // ---------------- Commands: a tool and a changing subcommand in one statement ----------------

    private static void FindCommands(Source s, List<(string Tool, int Position)> named, List<Start> starts, Found found)
    {
        // The tools each statement names: helpers, Tool.X members and program literals of a start.
        var byStatement = new Dictionary<(int, int), HashSet<string>>();
        void Name((int, int) statement, string tool)
        {
            if (!byStatement.TryGetValue(statement, out var set)) byStatement[statement] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(tool);
        }

        foreach (var (tool, position) in named) Name(s.StatementAt(position), tool);
        foreach (var start in starts)
            if (start.Tool is not null) Name(s.StatementAt(start.Position), start.Tool);

        foreach (var ((a, b), tools) in byStatement)
        {
            var values = s.LiteralsIn(a, b).Select(l => (Literal: l, Value: s.Value(l))).ToList();
            foreach (var command in Commands)
            {
                if (!tools.Contains(command.Tool)) continue;
                var hit = values.FirstOrDefault(v => command.Arguments.IsMatch(v.Value));
                if (hit.Literal is not null) found.Add(command.Topic, command.What, command.Kind, s, hit.Literal.Start);
            }

            // A shell runs a command line: the tools inside it count as if started on their own.
            if (!tools.Overlaps(Shells)) continue;
            foreach (var (literal, value) in values)
                foreach (var command in Commands)
                {
                    var inside = Regex.Match(value, $@"(?:^|[\s""'&|;(\\/]){Regex.Escape(command.Tool)}(?:\.exe)?\s+(?<args>.*)", RegexOptions.IgnoreCase);
                    if (inside.Success && command.Arguments.IsMatch(inside.Groups["args"].Value))
                        found.Add(command.Topic, command.What, command.Kind, s, literal.Start);
                }
        }
    }

    private static void FindPowerShell(Source s, Found found)
    {
        foreach (var literal in s.Literals)
        {
            var value = s.Value(literal);
            foreach (var (pattern, topic, what, kind) in Cmdlets)
                if (pattern.IsMatch(value)) found.Add(topic, what, kind, s, literal.Start);
        }
    }

    // ---------------- Literals that tell on their own ----------------

    private static void FindLiterals(Source s, Found found)
    {
        foreach (var literal in s.Literals)
        {
            var value = s.Value(literal);
            if (!(value.Contains("HNetCfg", StringComparison.OrdinalIgnoreCase) || value.Contains("Schedule.Service", StringComparison.OrdinalIgnoreCase)
                  || value.Contains("mit/task", StringComparison.OrdinalIgnoreCase) || value.Contains("User Data", StringComparison.OrdinalIgnoreCase)
                  || value.Contains("Profiles", StringComparison.OrdinalIgnoreCase)))
                continue;
            foreach (var (pattern, topic, what, kind, use) in TellingLiterals)
            {
                if (!pattern.IsMatch(value)) continue;
                var (a, b) = s.StatementAt(literal.Start);
                var statement = s.Masked[a..b];
                bool used = use switch
                {
                    Use.ProgId => ProgIdUse.IsMatch(statement),
                    Use.Path => PathUse.IsMatch(statement),
                    _ => true
                };
                if (used) found.Add(topic, what, kind, s, literal.Start);
            }
        }
    }

    // ---------------- Calls and types ----------------

    private static void FindCode(Source s, Found found)
    {
        var m = s.Masked;
        foreach (var (pattern, topic, kind) in CodePatterns)
            foreach (Match hit in pattern.Matches(m))
                found.Add(topic, Regex.Replace(hit.Groups["what"].Value, @"\s+", ""), kind, s, hit.Index);

        // Assembly.Load(bytes) runs code that isn't in the build; Assembly.Load("Name") loads a known assembly by name.
        foreach (Match load in AssemblyLoad.Matches(m))
        {
            var args = Arguments(m, load.Index + load.Length - 1);
            if (args.Count == 0) continue;
            var (a, b) = args[0];
            bool literalOnly = m[a..b].Trim().Length == 0 && s.LiteralsIn(a, b).Any();
            if (!literalOnly) found.Add(DeclarationTopic.RunsCodeFromOutside, "Assembly.Load", "loads code at run time", s, load.Index);
        }

        // The user's identity is personal data; an admin check (IsInRole) isn't.
        foreach (Match id in Identity.Matches(m))
        {
            var (_, end) = s.StatementAt(id.Index);
            if (IdentityRead.IsMatch(m[id.Index..end]))
                found.Add(DeclarationTopic.ReadsOrSendsPersonalData, "WindowsIdentity.GetCurrent().Name", "the Windows user name", s, id.Index);
        }

        // An X509 store of the machine, opened for writing: certificate trust.
        foreach (Match store in MachineStore.Matches(m))
        {
            var method = s.MethodAt(store.Index);
            if (StoreWrite.Matches(m).Any(w => s.MethodAt(w.Index) == method))
                found.Add(DeclarationTopic.ChangesSecuritySettings, "X509Store LocalMachine", "certificate trust", s, store.Index);
        }
    }

    // ---------------- Pairs within one method ----------------

    /// <summary>
    /// String constants (const string / static readonly string set to literals), by name, across the
    /// code: a path kept in a constant counts where the constant is used.
    /// </summary>
    private static Dictionary<string, List<string>> Constants(List<Source> sources)
    {
        var constants = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var s in sources)
            foreach (Match c in ConstantDeclaration.Matches(s.Masked))
            {
                int a = c.Index + c.Length;
                int b = ExpressionEnd(s.Masked, a);
                var value = string.Concat(s.LiteralsIn(a, b).Select(s.Value));
                if (value.Length == 0) continue;
                var name = c.Groups["name"].Value;
                if (!constants.TryGetValue(name, out var list)) constants[name] = list = [];
                list.Add(value);
            }
        return constants;
    }

    /// <summary>
    /// Registry paths and folders count where the same method also writes: a security path in a method
    /// that writes to the registry, HKLM in a method that writes to the registry, a machine folder or
    /// the Startup folder in a method that writes a file. Reading alone changes nothing.
    /// </summary>
    private static void FindPairs(Source s, Dictionary<string, List<string>> constants, Found found)
    {
        var m = s.Masked;
        var registryWriters = RegistryWrite.Matches(m)
            .Where(w => w.Value.StartsWith("OpenSubKey", StringComparison.Ordinal) || CalledOnSomething(m, w.Index))
            .Select(w => s.MethodAt(w.Index))
            .ToHashSet();
        var fileWriters = FileWrite.Matches(m).Select(w => s.MethodAt(w.Index)).ToHashSet();

        if (registryWriters.Count > 0)
        {
            // Paths written as literals in the method, or through a constant named there. A constant's
            // own declaration is not a use of it.
            // Only the few constants that can hold such a path are looked for, each with a fast text search.
            var paths = s.Literals
                .Select(l => (Position: l.Start, Value: s.Value(l), Literal: true))
                .Concat(constants
                    .SelectMany(c => c.Value.Where(MayBeRegistryPath).Select(v => (Name: c.Key, Value: v)))
                    .SelectMany(c => Uses(m, c.Name).Select(at => (Position: at, c.Value, Literal: false))));

            foreach (var (position, value, literal) in paths)
            {
                // The value first: most strings are no path, and finding the method is the slow part.
                if (!MayBeRegistryPath(value)) continue;
                var security = SecurityPaths.Where(p => p.Pattern.IsMatch(value)).ToList();
                bool runKey = RunKey.IsMatch(value.TrimEnd());
                if (security.Count == 0 && !runKey) continue;
                if (literal && InsideConstantDeclaration(s, position)) continue;
                if (!registryWriters.Contains(s.MethodAt(position))) continue;
                foreach (var (_, what, kind) in security) found.Add(DeclarationTopic.ChangesSecuritySettings, what, kind, s, position);
                if (runKey) found.Add(DeclarationTopic.StartsOnItsOwn, @"…\CurrentVersion\Run", "a startup entry (Run key)", s, position);
            }

            foreach (Match hklm in LocalMachine.Matches(m))
                if (registryWriters.Contains(s.MethodAt(hklm.Index)))
                    found.Add(DeclarationTopic.ChangesTheWholeComputer, "HKLM registry write", "the registry under HKLM", s, hklm.Index);
        }

        if (fileWriters.Count > 0)
        {
            foreach (Match folder in MachineFolder.Matches(m))
                if (fileWriters.Contains(s.MethodAt(folder.Index)))
                    found.Add(DeclarationTopic.ChangesTheWholeComputer, "SpecialFolder." + folder.Groups["what"].Value,
                        FolderKind(folder.Groups["what"].Value), s, folder.Index);

            foreach (Match startup in StartupFolder.Matches(m))
                if (fileWriters.Contains(s.MethodAt(startup.Index)))
                    found.Add(DeclarationTopic.StartsOnItsOwn, "Startup folder", "a file in the Startup folder", s, startup.Index);
        }
    }

    /// <summary>A quick test before the path patterns: every one of them contains one of these words.</summary>
    private static bool MayBeRegistryPath(string value) =>
        value.Contains("Firewall", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Defender", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Policies", StringComparison.OrdinalIgnoreCase)
        || value.Contains("CurrentVersion", StringComparison.OrdinalIgnoreCase);

    private static string FolderKind(string folder) => folder switch
    {
        "CommonApplicationData" => "files under ProgramData",
        "Windows" or "System" or "SystemX86" => "files under Windows",
        _ => "files under Program Files"
    };

    /// <summary>Where a name is used in masked code, as a whole word and not where it's declared.</summary>
    private static IEnumerable<int> Uses(string m, string name)
    {
        for (int at = m.IndexOf(name, StringComparison.Ordinal); at >= 0; at = m.IndexOf(name, at + name.Length, StringComparison.Ordinal))
        {
            bool whole = (at == 0 || !IsWordChar(m[at - 1])) && (at + name.Length == m.Length || !IsWordChar(m[at + name.Length]));
            if (whole && !IsDeclaration(m, at)) yield return at;
        }
    }

    private static bool IsDeclaration(string masked, int index) =>
        Regex.IsMatch(masked[Math.Max(0, index - 40)..index], @"\b(?:string|const\s+string|readonly\s+string)\s+$");

    private static bool InsideConstantDeclaration(Source s, int position)
    {
        var (a, b) = s.StatementAt(position);
        return ConstantDeclaration.IsMatch(s.Masked[a..Math.Min(b, s.Masked.Length)]);
    }

    // ---------------- runas ----------------

    private static void FindRunAs(Source s, Found found)
    {
        foreach (Match verb in VerbRunas.Matches(s.Masked))
        {
            int a = verb.Index + verb.Length;
            int b = ExpressionEnd(s.Masked, a);
            if (s.LiteralsIn(a, b).Any(l => s.Value(l).Trim().Equals("runas", StringComparison.OrdinalIgnoreCase)))
                found.Add(DeclarationTopic.NeedsAdministratorRights, "Verb = \"runas\"", "asks for admin rights", s, verb.Index);
        }
    }

    // ---------------- A program written or downloaded, then started ----------------

    /// <summary>A start whose program is a local the same method downloaded or wrote to: code that isn't in the build.</summary>
    private static void WrittenThenStarted(Dictionary<Source, List<Start>> startsBySource, Found found)
    {
        foreach (var (s, start) in startsBySource.SelectMany(p => p.Value.Select(start => (p.Key, start))))
        {
            if (start.Tool is not null && KnownTools.Contains(start.Tool)) continue;
            var locals = IdentifierToken.Matches(s.Masked[start.ProgramStart..start.ProgramEnd])
                .Select(id => id.Value)
                .Where(v => char.IsLower(v[0]))
                .ToHashSet();
            if (locals.Count == 0) continue;

            var method = s.MethodAt(start.Position);
            var writes = FileWrite.Matches(s.Masked).Cast<Match>().Concat(Download.Matches(s.Masked))
                .Where(w => w.Index < start.Position && s.MethodAt(w.Index) == method);
            foreach (var write in writes)
            {
                var args = Arguments(s.Masked, write.Index + write.Length - 1);
                // The path is the last argument of DownloadFile and the first of the file writes.
                var target = Download.IsMatch(write.Value) && args.Count > 1 ? args[^1] : args.FirstOrDefault();
                if (target == default) continue;
                if (IdentifierToken.Matches(s.Masked[target.Start..target.End]).Any(id => locals.Contains(id.Value)))
                {
                    found.Add(DeclarationTopic.RunsCodeFromOutside, "starts a file it wrote", "starts a program it downloaded or wrote", s, start.Position);
                    break;
                }
            }
        }
    }

    // ---------------- Masked-text helpers ----------------

    /// <summary>The top-level arguments of a call, from its opening parenthesis, as spans in the masked text.</summary>
    private static List<(int Start, int End)> Arguments(string m, int open)
    {
        var args = new List<(int, int)>();
        int depth = 0, from = open + 1;
        for (int i = open + 1; i < m.Length && i < open + 4000; i++)
        {
            char c = m[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    // An argument that is only a string literal is blank here but still an argument.
                    if (i > from || args.Count > 0) args.Add((from, i));
                    return args;
                }
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add((from, i));
                from = i + 1;
            }
            else if (c == ';') break;
        }
        return args;
    }

    /// <summary>Where an expression ends: the next , ; ) or } at its own depth.</summary>
    private static int ExpressionEnd(string m, int from)
    {
        int depth = 0;
        for (int i = from; i < m.Length; i++)
        {
            char c = m[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') { if (depth == 0) return i; depth--; }
            else if (c is ',' or ';' && depth == 0) return i;
        }
        return m.Length;
    }

    // ================= The build =================

    /// <summary>Tasks that only report. Setting properties and items (PropertyGroup, ItemGroup) isn't a task either.</summary>
    private static readonly HashSet<string> ReportOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "Message", "Warning", "Error", "PropertyGroup", "ItemGroup", "CallTarget", "OnError"
    };

    /// <summary>Targets the SDK runs by name: defining one hooks it in without BeforeTargets or AfterTargets.</summary>
    private static readonly Dictionary<string, string> NamedHooks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BeforeBuild"] = "before every build", ["AfterBuild"] = "after every build",
        ["BeforeCompile"] = "before every compile", ["AfterCompile"] = "after every compile",
        ["BeforeRebuild"] = "before every rebuild", ["AfterRebuild"] = "after every rebuild",
        ["BeforeClean"] = "before every clean", ["AfterClean"] = "after every clean",
        ["BeforeResolveReferences"] = "before references are resolved", ["AfterResolveReferences"] = "after references are resolved",
        ["BeforePublish"] = "before every publish", ["AfterPublish"] = "after every publish"
    };

    private static readonly Regex GeneratorAttribute = new(@"\[\s*(?:Microsoft\.CodeAnalysis\.)?(?:Generator|DiagnosticAnalyzer)\b", RegexOptions.Compiled);

    private sealed record TargetInfo(XElement Element, SnapshotFile File, string Name);

    /// <summary>
    /// Code that runs when someone builds the repo, read from every project, .props and .targets file in
    /// it: an Exec anywhere; a target that runs a real task and is hooked into the build (BeforeTargets,
    /// AfterTargets, a hook name such as BeforeBuild, InitialTargets, or pulled in by one of those through
    /// DependsOnTargets or CallTarget); an inline or compiled task (UsingTask); an analyzer reference; a
    /// project in the repo that runs inside the compiler. Whoever clones and builds runs all of these.
    /// </summary>
    public static void ScanBuild(IReadOnlyList<SnapshotFile> files, Found found)
    {
        var projectFiles = files
            .Where(f => f.Text is not null && f.Extension is ".csproj" or ".vbproj" or ".fsproj" or ".props" or ".targets")
            .Select(f => (File: f, Xml: Parse(f.Text!)))
            .Where(p => p.Xml is not null)
            .Select(p => (p.File, Xml: p.Xml!))
            .ToList();

        var targets = projectFiles
            .SelectMany(p => p.Xml.Descendants().Where(e => e.Name.LocalName == "Target")
                .Select(e => new TargetInfo(e, p.File, Attr(e, "Name") ?? "")))
            .Where(t => t.Name.Length > 0)
            .ToList();
        var byName = targets.ToLookup(t => t.Name, StringComparer.OrdinalIgnoreCase);

        // How each target joins the build; reachable ones get a reason, the rest none.
        var why = new Dictionary<TargetInfo, string>();
        var queue = new Queue<TargetInfo>();
        void Reach(TargetInfo target, string reason)
        {
            if (why.TryAdd(target, reason)) queue.Enqueue(target);
        }

        foreach (var target in targets)
        {
            if (Attr(target.Element, "BeforeTargets") is { Length: > 0 } before) Reach(target, $"before {List(before)}");
            else if (Attr(target.Element, "AfterTargets") is { Length: > 0 } after) Reach(target, $"after {List(after)}");
            else if (NamedHooks.TryGetValue(target.Name, out var hook)) Reach(target, hook);
        }
        foreach (var (file, xml) in projectFiles)
            foreach (var attribute in new[] { "InitialTargets", "DefaultTargets" })
                foreach (var name in Names(Attr(xml.Root!, attribute)))
                    foreach (var target in byName[name])
                        Reach(target, attribute == "InitialTargets" ? "first, on every build" : "on every build");

        while (queue.Count > 0)
        {
            var target = queue.Dequeue();
            var pulled = Names(Attr(target.Element, "DependsOnTargets"))
                .Concat(target.Element.Descendants()
                    .Where(e => e.Name.LocalName is "CallTarget" or "OnError")
                    .SelectMany(e => Names(Attr(e, "Targets") ?? Attr(e, "ExecuteTargets"))));
            foreach (var name in pulled)
                foreach (var dependency in byName[name])
                    Reach(dependency, $"as part of the {target.Name} target");
        }

        foreach (var target in targets)
        {
            bool reachable = why.TryGetValue(target, out var reason);
            var tasks = target.Element.Elements()
                .Where(e => !ReportOnly.Contains(e.Name.LocalName))
                .Where(e => reachable || e.Name.LocalName == "Exec")   // an Exec counts wherever it is
                .ToList();
            foreach (var task in tasks)
            {
                var what = task.Name.LocalName == "Exec"
                    ? $"`{Shorten(Attr(task, "Command") ?? "")}`"
                    : $"the {task.Name.LocalName} task";
                var kind = $"the {target.Name} target in {RepoSnapshot.NameOf(target.File.RelPath)} runs {what}"
                           + (reason is null ? "" : $" {reason}");
                found.Evidence.Add(new Evidence(DeclarationTopic.RunsCodeWhenYouBuild,
                    task.Name.LocalName == "Exec" ? $"Exec in the {target.Name} target" : $"{task.Name.LocalName} in the {target.Name} target",
                    kind, target.File.RelPath, Line(task)));
            }
        }

        foreach (var (file, xml) in projectFiles)
        {
            var name = RepoSnapshot.NameOf(file.RelPath);
            foreach (var usingTask in xml.Descendants().Where(e => e.Name.LocalName == "UsingTask"))
            {
                var task = Attr(usingTask, "TaskName") ?? "a task";
                if (Attr(usingTask, "TaskFactory") is { Length: > 0 })
                    found.Evidence.Add(new Evidence(DeclarationTopic.RunsCodeWhenYouBuild, $"inline task {task}",
                        $"{name} defines the inline task {task}, whose code is compiled and run during the build", file.RelPath, Line(usingTask)));
                else if (Attr(usingTask, "AssemblyFile") is { Length: > 0 } dll)
                    found.Evidence.Add(new Evidence(DeclarationTopic.RunsCodeWhenYouBuild, $"task {task} from {dll}",
                        $"{name} loads the task {task} from {Shorten(dll)} into the build", file.RelPath, Line(usingTask)));
            }

            foreach (var reference in xml.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                var type = Attr(reference, "OutputItemType")
                           ?? reference.Elements().FirstOrDefault(e => e.Name.LocalName == "OutputItemType")?.Value.Trim();
                if (!string.Equals(type, "Analyzer", StringComparison.OrdinalIgnoreCase)) continue;
                var include = Attr(reference, "Include") ?? "a project";
                found.Evidence.Add(new Evidence(DeclarationTopic.RunsCodeWhenYouBuild, $"analyzer {RepoSnapshot.NameOf(include.Replace('\\', '/'))}",
                    $"{name} runs {RepoSnapshot.NameOf(include.Replace('\\', '/'))} inside the compiler", file.RelPath, Line(reference)));
            }

            // A project of the repo's own that is an analyzer or source generator.
            if (file.Extension is not (".csproj" or ".vbproj" or ".fsproj")) continue;
            var codeAnalysis = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "PackageReference"
                && (Attr(e, "Include") ?? "").StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase));
            if (codeAnalysis is null) continue;
            bool roslyn = xml.Descendants().Any(e => e.Name.LocalName == "IsRoslynComponent" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
            var dir = RepoSnapshot.DirOf(file.RelPath);
            var marked = files.FirstOrDefault(f => f.Extension == ".cs" && f.Text is not null
                && RepoSnapshot.IsSameOrUnder(RepoSnapshot.DirOf(f.RelPath), dir)
                && GeneratorAttribute.IsMatch(CSharpLexer.BlankCommentsAndStrings(f.Text)));
            if (roslyn || marked is not null)
                found.Evidence.Add(new Evidence(DeclarationTopic.RunsCodeWhenYouBuild, $"{name} runs inside the compiler",
                    $"{name} is an analyzer or source generator that runs inside the compiler", file.RelPath, Line(codeAnalysis)));
        }
    }

    private static XDocument? Parse(string text)
    {
        try { return XDocument.Parse(text, LoadOptions.SetLineInfo); }
        catch (XmlException) { return null; }   // the project rules report a file that doesn't parse
    }

    private static string? Attr(XElement e, string name) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim();

    private static int Line(XElement e) => ((IXmlLineInfo)e).HasLineInfo() ? ((IXmlLineInfo)e).LineNumber : 0;

    /// <summary>Target names from a list attribute; $(…) and @(…) entries can't be followed and are left out.</summary>
    private static IEnumerable<string> Names(string? list) => (list ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(n => !n.Contains("$(", StringComparison.Ordinal) && !n.Contains("@(", StringComparison.Ordinal));

    private static string List(string targets) => Join(Names(targets).DefaultIfEmpty(targets.Trim()));

    private static string Shorten(string text)
    {
        var one = Regex.Replace(text, @"\s+", " ").Trim();
        return one.Length <= 60 ? one : one[..57] + "…";
    }

    // ================= The line to paste =================

    /// <summary>A ready line for a missing topic, filled in from what was found.</summary>
    public static string PasteLine(Topic topic, IReadOnlyList<Evidence> evidence, IReadOnlyList<string> hosts)
    {
        var kinds = evidence.Select(e => e.Kind).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string text = topic.Id switch
        {
            DeclarationTopic.NeedsAdministratorRights => evidence.Any(e => e.File.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                ? "runs as administrator; Windows asks when it starts."
                : "asks for administrator rights when needed.",
            DeclarationTopic.ChangesSecuritySettings => $"changes {Join(kinds.Select(SecurityWords))} settings.",
            DeclarationTopic.StartsOnItsOwn => $"creates {Join(kinds)}.",
            DeclarationTopic.StartsOtherPrograms => Join(kinds, "and") + ".",
            DeclarationTopic.ChangesTheWholeComputer => $"writes {Join(kinds)}.",
            DeclarationTopic.TalksToOtherComputers => hosts.Count > 0
                ? $"connects to {Join(hosts.Take(6))}{(hosts.Count > 6 ? " and others" : "")}."
                : "connects to the internet.",
            DeclarationTopic.RunsCodeFromOutside => $"{Join(kinds)}.",
            DeclarationTopic.RunsCodeWhenYouBuild => string.Join("; ", kinds.Take(3)) + (kinds.Count > 3 ? "; and more" : "") + ".",
            DeclarationTopic.ReadsOrSendsPersonalData => $"reads {Join(kinds)}.",
            _ => ""
        };
        return $"- **{topic.Label}:** {text}";
    }

    private static string SecurityWords(string kind) => kind switch
    {
        "firewall" => "Windows Firewall",
        "Defender" => "Microsoft Defender",
        _ => kind
    };

    private static string Join(IEnumerable<string> items, string last = "and")
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + $" {last} " + list[^1]
        };
    }

    /// <summary>
    /// The hosts written in each file that uses the network, so the detail and the paste line can name
    /// them. A URL in a file without network use names nothing.
    /// </summary>
    private static void FindHosts(List<Source> sources, Found found)
    {
        var networkFiles = found.Of(DeclarationTopic.TalksToOtherComputers).Select(e => e.File).ToHashSet(StringComparer.Ordinal);
        foreach (var s in sources.Where(s => networkFiles.Contains(s.File.RelPath)))
            foreach (var literal in s.Literals)
            {
                var value = s.Value(literal).Trim();
                foreach (Match url in Url.Matches(value)) Host(url.Groups["host"].Value);
                if (BareHost.Match(value) is { Success: true } bare && Tlds.Contains(bare.Groups["tld"].Value)
                    && !FileExtensions.Contains(bare.Groups["tld"].Value)) Host(value);
            }

        void Host(string host)
        {
            host = host.Trim('.').ToLowerInvariant();
            if (host.Length > 0 && !IsLocal(host) && !found.Hosts.Contains(host)) found.Hosts.Add(host);
        }
    }

    private static bool IsLocal(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.StartsWith("127.", StringComparison.Ordinal)
        || host.Equals("schemas.microsoft.com", StringComparison.OrdinalIgnoreCase);
}
