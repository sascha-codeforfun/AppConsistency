using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AppConsistency.Core;

/// <summary>An approved third-party package. Switched off, it stays in the file without effect.</summary>
public sealed class PackageEntry
{
    public string Name { get; set; } = "";
    public string MinVersion { get; set; } = "";
    public string Reason { get; set; } = "";

    [JsonIgnore] public bool Enabled { get; set; } = true;

    /// <summary>In the file only when switched off: "Enabled": false.</summary>
    [JsonPropertyName("Enabled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnabledInFile
    {
        get => Enabled ? null : false;
        set => Enabled = value ?? true;
    }

    public PackageEntry Clone() => new() { Name = Name, MinVersion = MinVersion, Reason = Reason, Enabled = Enabled };
}

/// <summary>A name or pattern in a list. In the file it is plain text, or an object when switched off.</summary>
[JsonConverter(typeof(NameEntryConverter))]
public sealed class NameEntry
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public NameEntry Clone() => new() { Name = Name, Enabled = Enabled };
}

/// <summary>
/// One app allowed to break one rule, with the reason. File, Method and Origin narrow it: each one
/// given has to match. Switched off, it stays in the file without effect.
/// </summary>
public sealed class ExemptionEntry
{
    public string Rule { get; set; } = "";
    public string App { get; set; } = "";

    /// <summary>The end of a file's path, e.g. Services/ConsolePreset.cs.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? File { get; set; }

    /// <summary>The method a write sits in (write checks only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Method { get; set; }

    /// <summary>Where a write points, e.g. ApplicationData (write checks only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Origin { get; set; }

    public string Reason { get; set; } = "";

    [JsonIgnore] public bool Enabled { get; set; } = true;

    /// <summary>In the file only when switched off: "Enabled": false.</summary>
    [JsonPropertyName("Enabled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnabledInFile
    {
        get => Enabled ? null : false;
        set => Enabled = value ?? true;
    }

    /// <summary>What the entry matches, without the reason: the same key means the same exemption.</summary>
    [JsonIgnore]
    public string Key => string.Join("|", new[] { App, Rule, File, Method, Origin }.Select(v => (v ?? "").Trim().ToLowerInvariant()));

    /// <summary>"No AppData for EremitVM, in Services/ConsolePreset.cs, method Ensure, origin ApplicationData".</summary>
    [JsonIgnore]
    public string Describe
    {
        get
        {
            var parts = new List<string> { $"{Rule} for {App}" };
            if (!string.IsNullOrWhiteSpace(File)) parts.Add($"in {File}");
            if (!string.IsNullOrWhiteSpace(Method)) parts.Add($"method {Method}");
            if (!string.IsNullOrWhiteSpace(Origin)) parts.Add($"origin {Origin}");
            return string.Join(", ", parts);
        }
    }

    public ExemptionEntry Clone() => new()
    {
        Rule = Rule, App = App, File = File, Method = Method, Origin = Origin, Reason = Reason, Enabled = Enabled
    };
}

/// <summary>
/// What may be done to one app's image files: a file, or a pattern for several (the end of a path,
/// * within a folder name, ** across folders). Switched off, it stays in the file without effect.
/// </summary>
public sealed class ImageLevelEntry
{
    public string App { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>AsIs, Strip, Lossless or Smallest.</summary>
    public string Level { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Reason { get; set; }

    [JsonIgnore] public bool Enabled { get; set; } = true;

    /// <summary>In the file only when switched off: "Enabled": false.</summary>
    [JsonPropertyName("Enabled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnabledInFile
    {
        get => Enabled ? null : false;
        set => Enabled = value ?? true;
    }

    [JsonIgnore] public string Key => $"{App.Trim().ToLowerInvariant()}|{Path.Trim().Replace('\\', '/').ToLowerInvariant()}";

    /// <summary>True when Path has no wildcard: it names one file, and wins over any pattern.</summary>
    [JsonIgnore] public bool IsFile => Path.IndexOfAny(['*', '?']) < 0;

    public static bool TryLevel(string? text, out ImageKit.ImageLevel level) =>
        Enum.TryParse(text?.Trim().Replace(" ", ""), ignoreCase: true, out level) && Enum.IsDefined(level);

    public ImageLevelEntry Clone() => new() { App = App, Path = Path, Level = Level, Reason = Reason, Enabled = Enabled };
}

/// <summary>The level for image files no entry names, and how much loss a palette may cause.</summary>
public sealed class ImageSettings
{
    public string DefaultLevel { get; set; } = "";
    public string LossLimit { get; set; } = "";
}

public sealed class IconSettings
{
    public string ExeIcon { get; set; } = "";
    public string HeaderImage { get; set; } = "";
    public int HeaderImageSize { get; set; }
}

public sealed class PublishSettings
{
    /// <summary>
    /// Only read, from a file written before 1.0.31: the runtime differs by system and now sits in the
    /// Windows and Linux blocks. Migrate moves it to Windows.RuntimeIdentifier; it is never written again.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RuntimeIdentifier { get; set; }
    public string SelfContained { get; set; } = "";
    public string PublishSingleFile { get; set; } = "";
    public string IncludeNativeLibrariesForSelfExtract { get; set; } = "";
    public string EnableCompressionInSingleFile { get; set; } = "";
    public string DebugType { get; set; } = "";
}

public sealed class ReleaseSettings
{
    public string PublishSuffix { get; set; } = "";
    public string BackupName { get; set; } = "";
    public string ReadmeCollectionName { get; set; } = "";
}

/// <summary>
/// The "Hybrid" block of AppConsistency.json: packages approved for both systems, because apps of both
/// really ship them (an HTML parser the Windows and the Linux edition of one app share). A package only
/// one system uses belongs in that system's block.
/// </summary>
public sealed class HybridFile
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<PackageEntry>? ApprovedPackages { get; set; }

    [JsonIgnore] public bool IsEmpty => ApprovedPackages is null;
}

/// <summary>
/// The "Windows" block of AppConsistency.json: what holds for Windows apps only. The mirror of LinuxFile:
/// the packages only a Windows app may use, the lowest target framework, and the runtime the publish fix
/// writes. A setting left out (null) uses the built-in default.
/// </summary>
public sealed class WindowsFile
{
    /// <summary>Packages a Windows app may use on top of the Hybrid ones. Never applies to a Linux app.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<PackageEntry>? ApprovedPackages { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MinimumTargetFramework { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RuntimeIdentifier { get; set; }

    [JsonIgnore]
    public bool IsEmpty => ApprovedPackages is null && MinimumTargetFramework is null && RuntimeIdentifier is null;
}

/// <summary>
/// The "Linux" block of AppConsistency.json: what holds for Linux apps only (Avalonia, filed in a …LINUX
/// folder). A setting left out (null) uses the built-in default; a file without the block leaves Windows
/// apps exactly as they were.
/// </summary>
public sealed class LinuxFile
{
    /// <summary>Packages a Linux app may use on top of the Hybrid ones. Never applies to a Windows app.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<PackageEntry>? ApprovedPackages { get; set; }
    /// <summary>Packages a Linux app must not reference, approved or not.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<NameEntry>? ForbiddenPackages { get; set; }
    /// <summary>Avalonia packages with a version line of their own, left out of the one-version check.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<NameEntry>? OwnVersionPackages { get; set; }
    /// <summary>Files a Linux repo may hold on top of the whitelist.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<NameEntry>? Whitelist { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MinimumTargetFramework { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RuntimeIdentifier { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        ApprovedPackages is null && ForbiddenPackages is null && OwnVersionPackages is null && Whitelist is null
        && MinimumTargetFramework is null && RuntimeIdentifier is null;
}

/// <summary>
/// The standard as AppConsistency.json holds it: only what you've unlocked. A setting left out
/// (null) uses the built-in default. The built-in defaults are neutral: no approved packages and no
/// private files, so the public code carries none of your own choices.
///
/// What differs by system sits in three blocks: Hybrid (packages for both systems), Windows and Linux.
/// Everything else has nothing to do with the system and sits directly here.
/// </summary>
public sealed class StandardFile
{
    /// <summary>Packages approved for Windows and Linux apps alike.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public HybridFile? Hybrid { get; set; }
    /// <summary>What holds for Windows apps only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public WindowsFile? Windows { get; set; }
    /// <summary>What holds for Linux apps only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public LinuxFile? Linux { get; set; }

    /// <summary>Only read, from a file written before 1.0.31, when there was one list. Migrate moves it to Hybrid.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<PackageEntry>? ApprovedPackages { get; set; }
    /// <summary>Only read, from a file written before 1.0.31. Migrate moves it to Windows.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MinimumTargetFramework { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<NameEntry>? PrivateFiles { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<NameEntry>? Whitelist { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<NameEntry>? WebPackages { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IconSettings? Icon { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PublishSettings? Publish { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ReleaseSettings? Release { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LicenseHolder { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<ExemptionEntry>? Exemptions { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ImageSettings? Images { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<ImageLevelEntry>? ImageLevels { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        ApprovedPackages is null && PrivateFiles is null && Whitelist is null && WebPackages is null
        && MinimumTargetFramework is null && Icon is null && Publish is null && Release is null
        && LicenseHolder is null && Exemptions is null && Images is null && ImageLevels is null
        && Hybrid is not { IsEmpty: false } && Windows is not { IsEmpty: false } && Linux is not { IsEmpty: false };

    /// <summary>
    /// Brings a file written before 1.0.31 into today's layout, in place, changing nothing it means:
    /// the one approved list counted for every app, so it becomes the Hybrid list; the target framework and
    /// the runtime were a Windows app's, so they go to the Windows block. Where the file already has the new
    /// key as well, that one stands and the old list's other packages are added to it. Harmless to call
    /// twice. The file on disk changes the next time the settings are saved.
    /// </summary>
    public StandardFile Migrate()
    {
        if (ApprovedPackages is not null)
        {
            Hybrid ??= new HybridFile();
            if (Hybrid.ApprovedPackages is null)
                Hybrid.ApprovedPackages = ApprovedPackages;
            else
                Hybrid.ApprovedPackages.AddRange(ApprovedPackages.Where(old =>
                    !Hybrid.ApprovedPackages.Any(p => p.Name.Trim().Equals(old.Name.Trim(), StringComparison.OrdinalIgnoreCase))));
            ApprovedPackages = null;
        }

        if (MinimumTargetFramework is not null)
        {
            if (MinimumTargetFramework.Trim().Length > 0)
                (Windows ??= new WindowsFile()).MinimumTargetFramework ??= MinimumTargetFramework;
            MinimumTargetFramework = null;
        }

        if (Publish?.RuntimeIdentifier is { } runtime)
        {
            if (runtime.Trim().Length > 0)
                (Windows ??= new WindowsFile()).RuntimeIdentifier ??= runtime;
            Publish.RuntimeIdentifier = null;
        }

        return this;
    }
}

/// <summary>
/// The standard every rule reads: the built-in defaults with the file's settings on top, switched-off
/// entries left out. Immutable; a new one replaces it when the settings are saved.
/// </summary>
public sealed class Standard
{
    // ---------------- Built-in defaults: neutral, nothing personal ----------------

    /// <summary>Empty for all three lists (Hybrid, Windows, Linux): which packages to trust is yours to decide.</summary>
    public static IReadOnlyList<PackageEntry> DefaultApprovedPackages { get; } = [];
    public static IReadOnlyList<NameEntry> DefaultPrivateFiles { get; } = [];
    public static IReadOnlyList<ExemptionEntry> DefaultExemptions { get; } = [];
    public static IReadOnlyList<ImageLevelEntry> DefaultImageLevels { get; } = [];

    /// <summary>Lossless by default: metadata nothing displays goes, every pixel stays.</summary>
    public static ImageSettings DefaultImages => new() { DefaultLevel = "Lossless", LossLimit = "Invisible" };

    public static IReadOnlyList<NameEntry> DefaultWhitelist { get; } = Names(
        "*.cs", "*.xaml", "*.csproj", "*.slnx", "app.ico", "app.png", "app.svg", "app_*.png", "app.manifest", "Directory.Build.props");

    public static IReadOnlyList<NameEntry> DefaultWebPackages { get; } = Names("HtmlAgilityPack", "Microsoft.Web.WebView2");

    public const string DefaultMinimumTargetFramework = "net10.0-windows";

    /// <summary>No license holder by default: the MIT fix needs your name, and the public code has none.</summary>
    public const string DefaultLicenseHolder = "";

    public static IconSettings DefaultIcon => new() { ExeIcon = "app.ico", HeaderImage = "app_256.png", HeaderImageSize = 256 };

    /// <summary>The runtime the publish fix writes into a Windows app's .csproj. Any win- runtime passes the check.</summary>
    public const string DefaultWindowsRuntimeIdentifier = "win-x64";

    /// <summary>The publish properties every app sets, whatever its system. The runtime is the system's own.</summary>
    public static PublishSettings DefaultPublish => new()
    {
        SelfContained = "true",
        PublishSingleFile = "true",
        IncludeNativeLibrariesForSelfExtract = "true",
        EnableCompressionInSingleFile = "true",
        DebugType = "none"
    };

    public static ReleaseSettings DefaultRelease => new()
    {
        PublishSuffix = "-PUBLISH",
        BackupName = "{yyyy-MM-dd-HH-mm-ss}-REPO-SOURCE-BACKUP.zip",
        ReadmeCollectionName = "{yyyy-MM-dd-HH-mm-ss}-REPO-READMES.zip"
    };

    // ---------------- Built-in defaults for Linux apps ----------------

    /// <summary>
    /// Empty, like the other two lists: which versions of Avalonia to accept is yours to decide. Until the
    /// packages are listed in the file, a Linux app's Avalonia references fail as unapproved.
    /// </summary>
    public static IReadOnlyList<PackageEntry> DefaultLinuxApprovedPackages => DefaultApprovedPackages;

    /// <summary>The convenience package that brings the Windows and macOS backends along.</summary>
    public static IReadOnlyList<NameEntry> DefaultLinuxForbiddenPackages { get; } = Names("Avalonia.Desktop");

    /// <summary>The data grid is a package of its own since Avalonia 12, with its own version line.</summary>
    public static IReadOnlyList<NameEntry> DefaultLinuxOwnVersionPackages { get; } = Names("Avalonia.Controls.DataGrid");

    public static IReadOnlyList<NameEntry> DefaultLinuxWhitelist { get; } = Names("*.axaml");

    /// <summary>No platform suffix: a Linux app that targets net10.0-windows can't be built for Linux.</summary>
    public const string DefaultLinuxMinimumTargetFramework = "net10.0";

    public const string DefaultLinuxRuntimeIdentifier = "linux-x64";

    /// <summary>Why a forbidden package is forbidden, for the ones the standard knows; others get the plain sentence.</summary>
    public static string ForbiddenReason(string package) =>
        package.Equals("Avalonia.Desktop", StringComparison.OrdinalIgnoreCase)
            ? "it brings the Windows and macOS backends along; name the Linux backends (Avalonia.X11, Avalonia.Wayland) in Program.cs instead"
            : "it is on the list of packages a Linux app must not use (Settings, Linux: forbidden packages)";

    /// <summary>The standard in use right now. Set before a scan; read by the rules on any thread.</summary>
    public static Standard Current { get; set; } = From(null);

    // ---------------- Effective values ----------------

    /// <summary>Where it came from, for the header and the report: "built-in" or the file name.</summary>
    public string Source { get; private init; } = "built-in";

    /// <summary>Packages approved for apps of both systems.</summary>
    public IReadOnlyDictionary<string, PackageEntry> HybridApprovedPackages { get; private init; } =
        new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Packages a Windows app may use on top of the Hybrid ones.</summary>
    public IReadOnlyDictionary<string, PackageEntry> WindowsApprovedPackages { get; private init; } =
        new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> PrivateFiles { get; private init; } = [];
    public IReadOnlyList<string> Whitelist { get; private init; } = [];
    public IReadOnlyList<string> WebPackages { get; private init; } = [];
    /// <summary>The lowest target framework a Windows app may use. A Linux app's is LinuxMinimumTargetFramework.</summary>
    public string MinimumTargetFramework { get; private init; } = DefaultMinimumTargetFramework;
    public string WindowsRuntimeIdentifier { get; private init; } = DefaultWindowsRuntimeIdentifier;
    public IconSettings Icon { get; private init; } = DefaultIcon;
    public PublishSettings Publish { get; private init; } = DefaultPublish;
    public ReleaseSettings Release { get; private init; } = DefaultRelease;

    /// <summary>The name the MIT fix writes into the copyright line; empty means the fix isn't offered.</summary>
    public string LicenseHolder { get; private init; } = DefaultLicenseHolder;

    /// <summary>Active exemptions with a rule, an app and a reason; optional parts trimmed, blank ones null.</summary>
    public IReadOnlyList<ExemptionEntry> Exemptions { get; private init; } = [];

    /// <summary>The level for image files no entry names.</summary>
    public ImageKit.ImageLevel ImageDefaultLevel { get; private init; } = ImageKit.ImageLevel.Lossless;

    /// <summary>How much colour difference a palette may cause before it isn't offered.</summary>
    public ImageKit.LossLimit ImageLossLimit { get; private init; } = ImageKit.LossLimit.Invisible;

    /// <summary>Active image level entries with an app, a path and a valid level.</summary>
    public IReadOnlyList<ImageLevelEntry> ImageLevels { get; private init; } = [];

    private IReadOnlyList<Regex> _whitelistPatterns = [];

    // ---------------- Effective values for Linux apps ----------------

    /// <summary>Packages a Linux app may use on top of the Hybrid ones.</summary>
    public IReadOnlyDictionary<string, PackageEntry> LinuxApprovedPackages { get; private init; } =
        new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> LinuxForbiddenPackages { get; private init; } = [];
    public IReadOnlyList<string> LinuxOwnVersionPackages { get; private init; } = [];
    public IReadOnlyList<string> LinuxWhitelist { get; private init; } = [];
    public string LinuxMinimumTargetFramework { get; private init; } = DefaultLinuxMinimumTargetFramework;
    public string LinuxRuntimeIdentifier { get; private init; } = DefaultLinuxRuntimeIdentifier;

    private IReadOnlyList<Regex> _linuxWhitelistPatterns = [];

    /// <summary>The packages only apps of this system may use: the Windows list or the Linux list.</summary>
    public IReadOnlyDictionary<string, PackageEntry> SystemApprovedPackages(AppPlatform platform) =>
        platform == AppPlatform.Linux ? LinuxApprovedPackages : WindowsApprovedPackages;

    /// <summary>
    /// The approved entry for a package in an app of this system, and the list it is on: "Hybrid", or the
    /// system's own ("Windows", "Linux"). A package on the other system's list is not approved. Null when
    /// it is on neither list that counts.
    /// </summary>
    public PackageEntry? ApprovedFor(AppPlatform platform, string package, out string list)
    {
        list = "Hybrid";
        if (HybridApprovedPackages.TryGetValue(package, out var entry)) return entry;
        list = AppPlatforms.Word(platform);
        return SystemApprovedPackages(platform).TryGetValue(package, out entry) ? entry : null;
    }

    /// <summary>The lowest target framework an app of this system may use.</summary>
    public string MinimumTargetFrameworkFor(AppPlatform platform) =>
        platform == AppPlatform.Linux ? LinuxMinimumTargetFramework : MinimumTargetFramework;

    /// <summary>What a RuntimeIdentifier has to start with: win- or linux-.</summary>
    public static string RuntimePrefix(AppPlatform platform) => platform == AppPlatform.Linux ? "linux-" : "win-";

    /// <summary>The five publish properties for an app of this system: a Linux app gets the Linux runtime.</summary>
    public (string Property, string Value)[] PublishPropertiesFor(AppPlatform platform) =>
        platform == AppPlatform.Linux
            ? PublishProperties.Select(p => p.Property == "RuntimeIdentifier" ? (p.Property, LinuxRuntimeIdentifier) : p).ToArray()
            : PublishProperties;

    /// <summary>The whitelist in force for an app of this system.</summary>
    public IReadOnlyList<string> WhitelistFor(AppPlatform platform) =>
        platform == AppPlatform.Linux ? Whitelist.Concat(LinuxWhitelist).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : Whitelist;

    /// <summary>The five publish properties in the order they're written into a .csproj.</summary>
    public (string Property, string Value)[] PublishProperties =>
    [
        ("RuntimeIdentifier", WindowsRuntimeIdentifier),
        ("SelfContained", Publish.SelfContained),
        ("PublishSingleFile", Publish.PublishSingleFile),
        ("IncludeNativeLibrariesForSelfExtract", Publish.IncludeNativeLibrariesForSelfExtract),
        ("EnableCompressionInSingleFile", Publish.EnableCompressionInSingleFile),
    ];

    private static Dictionary<string, PackageEntry> PackageList(IEnumerable<PackageEntry>? entries) =>
        (entries ?? DefaultApprovedPackages)
            .Where(p => p.Enabled && p.Name.Trim().Length > 0)
            .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public static Standard From(StandardFile? file, string? source = null)
    {
        // A file from before 1.0.31 is read in today's layout; what it means doesn't change.
        file?.Migrate();
        var windows = file?.Windows;
        var images = file?.Images ?? DefaultImages;
        var icon = file?.Icon ?? DefaultIcon;
        var publish = file?.Publish ?? DefaultPublish;
        var release = file?.Release ?? DefaultRelease;
        var whitelist = Enabled(file?.Whitelist ?? DefaultWhitelist);
        var linux = file?.Linux;
        var linuxWhitelist = Enabled(linux?.Whitelist ?? DefaultLinuxWhitelist);

        return new Standard
        {
            Source = file is null || file.IsEmpty ? "built-in" : source ?? "AppConsistency.json",
            HybridApprovedPackages = PackageList(file?.Hybrid?.ApprovedPackages),
            WindowsApprovedPackages = PackageList(windows?.ApprovedPackages),
            LinuxApprovedPackages = PackageList(linux?.ApprovedPackages),
            // Any win- runtime passes the check; this is the one the fix writes. One that isn't Windows' falls back.
            WindowsRuntimeIdentifier = windows?.RuntimeIdentifier?.Trim() is { } winRid && winRid.StartsWith("win-", StringComparison.OrdinalIgnoreCase)
                ? winRid : DefaultWindowsRuntimeIdentifier,
            LinuxForbiddenPackages = Enabled(linux?.ForbiddenPackages ?? DefaultLinuxForbiddenPackages),
            LinuxOwnVersionPackages = Enabled(linux?.OwnVersionPackages ?? DefaultLinuxOwnVersionPackages),
            LinuxWhitelist = linuxWhitelist,
            _linuxWhitelistPatterns = linuxWhitelist.Select(Glob).ToList(),
            // A Linux framework never carries a platform: one that does falls back to the default.
            LinuxMinimumTargetFramework = ParseTargetFramework(linux?.MinimumTargetFramework) is { Platform.Length: 0 }
                ? linux!.MinimumTargetFramework!.Trim() : DefaultLinuxMinimumTargetFramework,
            LinuxRuntimeIdentifier = linux?.RuntimeIdentifier?.Trim() is { } rid && rid.StartsWith("linux-", StringComparison.OrdinalIgnoreCase)
                ? rid : DefaultLinuxRuntimeIdentifier,
            PrivateFiles = Enabled(file?.PrivateFiles ?? DefaultPrivateFiles),
            Whitelist = whitelist,
            _whitelistPatterns = whitelist.Select(Glob).ToList(),
            WebPackages = Enabled(file?.WebPackages ?? DefaultWebPackages),
            MinimumTargetFramework = string.IsNullOrWhiteSpace(windows?.MinimumTargetFramework)
                ? DefaultMinimumTargetFramework : windows!.MinimumTargetFramework!.Trim(),
            Icon = new IconSettings
            {
                ExeIcon = Or(icon.ExeIcon, DefaultIcon.ExeIcon),
                HeaderImage = Or(icon.HeaderImage, DefaultIcon.HeaderImage),
                HeaderImageSize = icon.HeaderImageSize > 0 ? icon.HeaderImageSize : DefaultIcon.HeaderImageSize
            },
            Publish = new PublishSettings
            {
                SelfContained = Or(publish.SelfContained, DefaultPublish.SelfContained),
                PublishSingleFile = Or(publish.PublishSingleFile, DefaultPublish.PublishSingleFile),
                IncludeNativeLibrariesForSelfExtract = Or(publish.IncludeNativeLibrariesForSelfExtract, DefaultPublish.IncludeNativeLibrariesForSelfExtract),
                EnableCompressionInSingleFile = Or(publish.EnableCompressionInSingleFile, DefaultPublish.EnableCompressionInSingleFile),
                DebugType = Or(publish.DebugType, DefaultPublish.DebugType)
            },
            Release = new ReleaseSettings
            {
                PublishSuffix = SafeName(release.PublishSuffix, DefaultRelease.PublishSuffix),
                BackupName = SafeName(release.BackupName, DefaultRelease.BackupName),
                ReadmeCollectionName = SafeName(release.ReadmeCollectionName, DefaultRelease.ReadmeCollectionName)
            },
            LicenseHolder = Collapse(file?.LicenseHolder),
            Exemptions = (file?.Exemptions ?? DefaultExemptions)
                .Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Rule) && !string.IsNullOrWhiteSpace(e.App)
                            && !string.IsNullOrWhiteSpace(e.Reason))
                .Select(e => new ExemptionEntry
                {
                    Rule = e.Rule.Trim(), App = e.App.Trim(), Reason = Collapse(e.Reason),
                    File = Blank(e.File), Method = Blank(e.Method), Origin = Blank(e.Origin)
                })
                .GroupBy(e => e.Key)
                .Select(g => g.First())
                .ToList(),
            ImageDefaultLevel = ImageLevelEntry.TryLevel(images.DefaultLevel, out var level) ? level : ImageKit.ImageLevel.Lossless,
            ImageLossLimit = Enum.TryParse<ImageKit.LossLimit>(images.LossLimit?.Trim(), true, out var limit) && Enum.IsDefined(limit)
                ? limit : ImageKit.LossLimit.Invisible,
            ImageLevels = (file?.ImageLevels ?? DefaultImageLevels)
                .Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.App) && !string.IsNullOrWhiteSpace(e.Path)
                            && ImageLevelEntry.TryLevel(e.Level, out _))
                .Select(e => new ImageLevelEntry
                {
                    App = e.App.Trim(), Path = e.Path.Trim().Replace('\\', '/'), Level = e.Level.Trim(), Reason = Blank(e.Reason)
                })
                .GroupBy(e => e.Key)
                .Select(g => g.First())
                .ToList()
        };
    }

    /// <summary>
    /// The level for one image file of one app, and the entry that set it (null: the default). An
    /// entry naming the file wins over a pattern; of several patterns, the longest wins.
    /// </summary>
    public (ImageKit.ImageLevel Level, ImageLevelEntry? Entry) ImageLevelFor(string app, string relPath)
    {
        ImageLevelEntry? best = null;
        foreach (var e in ImageLevels)
        {
            if (!e.App.Equals(app, StringComparison.OrdinalIgnoreCase)) continue;
            if (!GlobCache.GetOrAdd(e.Path, Glob).IsMatch(relPath)) continue;
            if (best is null || (e.IsFile && !best.IsFile) || (e.IsFile == best.IsFile && e.Path.Length > best.Path.Length))
                best = e;
        }
        return best is not null && ImageLevelEntry.TryLevel(best.Level, out var level)
            ? (level, best)
            : (ImageDefaultLevel, null);
    }

    // ---------------- Questions the rules ask ----------------

    public bool IsPrivate(string relPath) =>
        PrivateFiles.Contains(RepoSnapshot.NameOf(relPath), StringComparer.OrdinalIgnoreCase);

    public bool IsWhitelisted(string relPath) => _whitelistPatterns.Any(p => p.IsMatch(relPath));

    /// <summary>On the whitelist of an app of this system: a Linux app's adds its own patterns (*.axaml).</summary>
    public bool IsWhitelisted(string relPath, AppPlatform platform) =>
        IsWhitelisted(relPath) || platform == AppPlatform.Linux && _linuxWhitelistPatterns.Any(p => p.IsMatch(relPath));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> GlobCache = new();

    /// <summary>A whitelist pattern matches a file name, or a path when it holds a slash.</summary>
    public static Regex Glob(string pattern)
    {
        var p = pattern.Trim().Replace('\\', '/');
        var body = Regex.Escape(p).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
        return new Regex("(?:^|/)" + body + "$", RegexOptions.IgnoreCase);
    }

    /// <summary>A file name from a pattern like {yyyy-MM-dd-HH-mm-ss}-REPO-SOURCE-BACKUP.zip.</summary>
    public static string FormatName(string pattern, DateTime when) =>
        Regex.Replace(pattern, @"\{([^{}]+)\}", m =>
        {
            try { return when.ToString(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture); }
            catch (FormatException) { return m.Value; }
        });

    /// <summary>
    /// The minimum target framework as a version and a platform: net10.0-windows → (10.0, windows).
    /// A project passes with that version or newer and the same platform.
    /// </summary>
    public static (Version Version, string Platform)? ParseTargetFramework(string? tfm)
    {
        var m = Regex.Match(tfm?.Trim() ?? "", @"^net(?<v>\d+(?:\.\d+)?)(?:-(?<p>[a-z]+)[\d.]*)?$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var v = m.Groups["v"].Value;
        return Version.TryParse(v.Contains('.') ? v : v + ".0", out var version)
            ? (version, m.Groups["p"].Value.ToLowerInvariant())
            : null;
    }

    private static List<string> Enabled(IEnumerable<NameEntry> entries) =>
        entries.Where(e => e.Enabled && e.Name.Trim().Length > 0).Select(e => e.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Collapse(string? value) =>
        string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>A name pattern that can't make a usable file name falls back to the default.</summary>
    private static string SafeName(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var outside = Regex.Replace(value, @"\{[^{}]*\}", "");
        return outside.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || outside.Contains('/') || outside.Contains('\\')
            ? fallback : value.Trim();
    }

    private static IReadOnlyList<NameEntry> Names(params string[] names) =>
        names.Select(n => new NameEntry { Name = n }).ToList();
}

/// <summary>"NOTES.md", or { "Name": "NOTES.md", "Enabled": false } when switched off.</summary>
public sealed class NameEntryConverter : JsonConverter<NameEntry>
{
    public override NameEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new NameEntry { Name = reader.GetString() ?? "" };

        var entry = new NameEntry();
        using var doc = JsonDocument.ParseValue(ref reader);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.NameEquals("Name") && p.Value.ValueKind == JsonValueKind.String) entry.Name = p.Value.GetString() ?? "";
            else if (p.NameEquals("Enabled") && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) entry.Enabled = p.Value.GetBoolean();
        }
        return entry;
    }

    public override void Write(Utf8JsonWriter writer, NameEntry value, JsonSerializerOptions options)
    {
        if (value.Enabled)
        {
            writer.WriteStringValue(value.Name);
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("Name", value.Name);
        writer.WriteBoolean("Enabled", false);
        writer.WriteEndObject();
    }
}
