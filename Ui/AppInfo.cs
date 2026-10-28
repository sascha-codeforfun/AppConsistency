using System.Reflection;

namespace AppConsistency.Ui;

/// <summary>
/// What the header shows on the right. The version is the csproj's &lt;Version&gt; (major.minor.build),
/// the SDK is recorded at build time through AssemblyMetadata, and the runtime is read at startup.
/// </summary>
public static class AppInfo
{
    private static readonly Assembly Self = typeof(AppInfo).Assembly;

    public static string Version
    {
        get
        {
            var v = Self.GetName().Version;
            return v is null ? "unknown" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    public static string? SdkVersion =>
        Self.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SdkVersion")?.Value is { Length: > 0 } sdk ? sdk : null;

    public static string RuntimeVersion => Environment.Version.ToString();
}
