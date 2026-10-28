using System.Xml.Linq;

namespace AppConsistency.Core;

public enum ItemMode
{
    Include,
    Update,
    Remove
}

/// <summary>
/// One item from an ItemGroup. Include holds the item spec whichever attribute it came from.
/// Copies is true when the item is copied to the output or publish folder (CopyToOutputDirectory
/// or CopyToPublishDirectory set to anything but Never).
/// </summary>
public sealed record ProjectItem(string Type, string Include, string? Version, string? HintPath, string SourceFile, ItemMode Mode,
                                 bool Copies = false);

/// <summary>
/// A flattened view of a project: properties (last one wins, like MSBuild) and items.
/// Directory.Build.props files above the project are applied first. Conditions and
/// $(Property) expansions are not evaluated.
/// </summary>
public sealed class ProjectModel
{
    public required string RelPath { get; init; }
    public required string Dir { get; init; }
    public Dictionary<string, string> Props { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which file set each property last (the .csproj or a Directory.Build.props).</summary>
    public Dictionary<string, string> PropSource { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ProjectItem> Items { get; } = new();
    public string? ParseError { get; private set; }

    /// <summary>The Sdk attribute of the project's root element, e.g. Microsoft.NET.Sdk or MSTest.Sdk/3.6.1.</summary>
    public string? Sdk { get; private set; }

    public string? Prop(string name) =>
        Props.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    public bool IsTrue(string name) => string.Equals(Prop(name), "true", StringComparison.OrdinalIgnoreCase);

    public static ProjectModel Load(RepoSnapshot snapshot, SnapshotFile project)
    {
        var dir = RepoSnapshot.DirOf(project.RelPath);
        var model = new ProjectModel { RelPath = project.RelPath, Dir = dir };

        var buildProps = snapshot.Files
            .Where(f => f.Name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
                        && f.Text is not null
                        && RepoSnapshot.IsSameOrUnder(dir, RepoSnapshot.DirOf(f.RelPath)))
            .OrderBy(f => f.RelPath.Length);

        foreach (var props in buildProps) model.Merge(props, isProject: false);
        model.Merge(project, isProject: true);
        return model;
    }

    private void Merge(SnapshotFile file, bool isProject)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(file.Text!.TrimStart('\uFEFF', ' ', '\t', '\r', '\n'));
        }
        catch (Exception ex)
        {
            if (isProject) ParseError = ex.Message;
            return;
        }

        if (doc.Root is null) return;
        if (isProject) Sdk = doc.Root.Attribute("Sdk")?.Value;

        foreach (var group in doc.Root.Elements())
        {
            switch (group.Name.LocalName)
            {
                case "PropertyGroup":
                    foreach (var property in group.Elements())
                    {
                        Props[property.Name.LocalName] = property.Value;
                        PropSource[property.Name.LocalName] = file.RelPath;
                    }
                    break;

                case "ItemGroup":
                    foreach (var item in group.Elements())
                    {
                        var (spec, mode) =
                            item.Attribute("Include") is { } inc ? (inc.Value, ItemMode.Include)
                            : item.Attribute("Update") is { } upd ? (upd.Value, ItemMode.Update)
                            : item.Attribute("Remove") is { } rem ? (rem.Value, ItemMode.Remove)
                            : ((string?)null, ItemMode.Include);
                        if (string.IsNullOrWhiteSpace(spec)) continue;

                        var version = item.Attribute("Version")?.Value ?? Child(item, "Version");
                        bool copies = IsCopied(item, "CopyToOutputDirectory") || IsCopied(item, "CopyToPublishDirectory");
                        Items.Add(new ProjectItem(item.Name.LocalName, spec.Trim(), version, Child(item, "HintPath"), file.RelPath, mode, copies));
                    }
                    break;
            }
        }
    }

    private static bool IsCopied(XElement item, string name)
    {
        var value = item.Attribute(name)?.Value ?? Child(item, name);
        return !string.IsNullOrWhiteSpace(value) && !value.Trim().Equals("Never", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Child(XElement element, string name) =>
        element.Elements().FirstOrDefault(c => c.Name.LocalName == name)?.Value;
}
