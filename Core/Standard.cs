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

public sealed class IconSettings
{
    public string ExeIcon { get; set; } = "";
    public string HeaderImage { get; set; } = "";
    public int HeaderImageSize { get; set; }
}

public sealed class PublishSettings
{
    public string RuntimeIdentifier { get; set; } = "";
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
/// The standard as AppConsistency.json holds it: only what you've unlocked. A setting left out
/// (null) uses the built-in default. The built-in defaults are neutral: no approved packages and no
/// private files, so the public code carries none of your own choices.
/// </summary>
public sealed class StandardFile
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<PackageEntry>? ApprovedPackages { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<NameEntry>? PrivateFiles { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<NameEntry>? Whitelist { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<NameEntry>? WebPackages { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MinimumTargetFramework { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IconSettings? Icon { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PublishSettings? Publish { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ReleaseSettings? Release { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        ApprovedPackages is null && PrivateFiles is null && Whitelist is null && WebPackages is null
        && MinimumTargetFramework is null && Icon is null && Publish is null && Release is null;
}

/// <summary>
/// The standard every rule reads: the built-in defaults with the file's settings on top, switched-off
/// entries left out. Immutable; a new one replaces it when the settings are saved.
/// </summary>
public sealed class Standard
{
    // ---------------- Built-in defaults: neutral, nothing personal ----------------

    public static IReadOnlyList<PackageEntry> DefaultApprovedPackages { get; } = [];
    public static IReadOnlyList<NameEntry> DefaultPrivateFiles { get; } = [];

    public static IReadOnlyList<NameEntry> DefaultWhitelist { get; } = Names(
        "*.cs", "*.xaml", "*.csproj", "*.slnx", "app.ico", "app.png", "app.svg", "app_*.png", "app.manifest", "Directory.Build.props");

    public static IReadOnlyList<NameEntry> DefaultWebPackages { get; } = Names("HtmlAgilityPack", "Microsoft.Web.WebView2");

    public const string DefaultMinimumTargetFramework = "net10.0-windows";

    public static IconSettings DefaultIcon => new() { ExeIcon = "app.ico", HeaderImage = "app_256.png", HeaderImageSize = 256 };

    public static PublishSettings DefaultPublish => new()
    {
        RuntimeIdentifier = "win-x64",
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

    /// <summary>The standard in use right now. Set before a scan; read by the rules on any thread.</summary>
    public static Standard Current { get; set; } = From(null);

    // ---------------- Effective values ----------------

    /// <summary>Where it came from, for the header and the report: "built-in" or the file name.</summary>
    public string Source { get; private init; } = "built-in";

    public IReadOnlyDictionary<string, PackageEntry> ApprovedPackages { get; private init; } =
        new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> PrivateFiles { get; private init; } = [];
    public IReadOnlyList<string> Whitelist { get; private init; } = [];
    public IReadOnlyList<string> WebPackages { get; private init; } = [];
    public string MinimumTargetFramework { get; private init; } = DefaultMinimumTargetFramework;
    public IconSettings Icon { get; private init; } = DefaultIcon;
    public PublishSettings Publish { get; private init; } = DefaultPublish;
    public ReleaseSettings Release { get; private init; } = DefaultRelease;

    private IReadOnlyList<Regex> _whitelistPatterns = [];

    /// <summary>The five publish properties in the order they're written into a .csproj.</summary>
    public (string Property, string Value)[] PublishProperties =>
    [
        ("RuntimeIdentifier", Publish.RuntimeIdentifier),
        ("SelfContained", Publish.SelfContained),
        ("PublishSingleFile", Publish.PublishSingleFile),
        ("IncludeNativeLibrariesForSelfExtract", Publish.IncludeNativeLibrariesForSelfExtract),
        ("EnableCompressionInSingleFile", Publish.EnableCompressionInSingleFile),
    ];

    public static Standard From(StandardFile? file, string? source = null)
    {
        var icon = file?.Icon ?? DefaultIcon;
        var publish = file?.Publish ?? DefaultPublish;
        var release = file?.Release ?? DefaultRelease;
        var whitelist = Enabled(file?.Whitelist ?? DefaultWhitelist);

        return new Standard
        {
            Source = file is null || file.IsEmpty ? "built-in" : source ?? "AppConsistency.json",
            ApprovedPackages = (file?.ApprovedPackages ?? DefaultApprovedPackages)
                .Where(p => p.Enabled && p.Name.Trim().Length > 0)
                .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
            PrivateFiles = Enabled(file?.PrivateFiles ?? DefaultPrivateFiles),
            Whitelist = whitelist,
            _whitelistPatterns = whitelist.Select(Glob).ToList(),
            WebPackages = Enabled(file?.WebPackages ?? DefaultWebPackages),
            MinimumTargetFramework = string.IsNullOrWhiteSpace(file?.MinimumTargetFramework)
                ? DefaultMinimumTargetFramework : file!.MinimumTargetFramework!.Trim(),
            Icon = new IconSettings
            {
                ExeIcon = Or(icon.ExeIcon, DefaultIcon.ExeIcon),
                HeaderImage = Or(icon.HeaderImage, DefaultIcon.HeaderImage),
                HeaderImageSize = icon.HeaderImageSize > 0 ? icon.HeaderImageSize : DefaultIcon.HeaderImageSize
            },
            Publish = new PublishSettings
            {
                RuntimeIdentifier = Or(publish.RuntimeIdentifier, DefaultPublish.RuntimeIdentifier),
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
            }
        };
    }

    // ---------------- Questions the rules ask ----------------

    public bool IsPrivate(string relPath) =>
        PrivateFiles.Contains(RepoSnapshot.NameOf(relPath), StringComparer.OrdinalIgnoreCase);

    public bool IsWhitelisted(string relPath) => _whitelistPatterns.Any(p => p.IsMatch(relPath));

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
