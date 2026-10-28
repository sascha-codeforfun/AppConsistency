namespace AppConsistency.Core;

/// <summary>What each agreed fix does. Publish fixes set one .csproj property each.</summary>
public static class FixCatalog
{
    /// <summary>The single-file publish block from the standard, in the order it is written into a .csproj.</summary>
    public static (string Property, string Value)[] PublishProperties => Standard.Current.PublishProperties;

    /// <summary>The DebugType the standard expects (none by default).</summary>
    public static string DebugType => Standard.Current.Publish.DebugType;

    /// <summary>The group the debug-info fix appends; {0} is the indent of top-level elements, {1} of their children.</summary>
    public static string[] NoDebugInfoBlock =>
    [
        "{0}<!-- Reproducible builds: no debug information. The PDB path would carry the",
        "{0}     random build folder into the exe, so identical sources would give different bytes. -->",
        "{0}<PropertyGroup>",
        "{1}<DebugType>" + DebugType + "</DebugType>",
        "{0}</PropertyGroup>",
    ];

    /// <summary>The version the "add a version" fix writes into a project that has none.</summary>
    public const string FirstVersion = "1.0.0";

    public static string Title(FixKind kind) => kind switch
    {
        FixKind.RemoveSolutionFiles => "Remove solution files",
        FixKind.NoDebugInfo => "Turn off debug information",
        FixKind.PublishSettings => "Complete the publish settings",
        FixKind.RemoveIdeFiles => "Remove obsolete IDE files",
        FixKind.RemoveGitFiles => "Remove git files",
        FixKind.RemoveUnlistedFiles => "Remove the unlisted files you tick",
        FixKind.AddVersion => "Add <Version>1.0.0</Version>",
        FixKind.ReleaseTimestamps => "Set release timestamps",
        _ => kind.ToString()
    };
}
