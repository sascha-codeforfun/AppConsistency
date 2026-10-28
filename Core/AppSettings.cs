using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppConsistency.Core;

/// <summary>
/// AppConsistency.json next to the exe: the settings standard, nothing left behind in AppData.
/// It holds the last scanned folder and the parts of the standard you've unlocked; everything else
/// is the built-in default and never appears in the file. Written only when something changes. In a
/// folder that can't be written (Program Files), nothing is remembered; it never falls back to AppData.
/// A file that can't be read is never overwritten: the built-in standard is used and the window says so.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string? LastPath { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StandardFile? Standard { get; set; }

    /// <summary>The file exists but couldn't be read: it is left untouched, and the built-in standard applies.</summary>
    [JsonIgnore] public string? Problem { get; private set; }

    [JsonIgnore] public static string FilePath => Path.Combine(AppContext.BaseDirectory, "AppConsistency.json");

    private string? _savedPath;

    /// <summary>Where 1.0.12 and earlier kept the folder. Only ever read, once, so it isn't lost; you can delete it.</summary>
    private static string OldFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppConsistency", "settings.json");

    public static AppSettings Load()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
                settings._savedPath = settings.LastPath;
                return settings;
            }
            catch (Exception ex)
            {
                return new AppSettings { Problem = $"{Path.GetFileName(FilePath)} couldn't be read ({ex.Message}). The built-in standard is used, and the file is left as it is." };
            }
        }

        // Carried over from the old place; saved next to the exe with the next scan.
        try
        {
            if (File.Exists(OldFilePath))
                return new AppSettings { LastPath = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(OldFilePath))?.LastPath };
        }
        catch
        {
            // Unreadable old settings just mean starting fresh.
        }
        return new AppSettings();
    }

    /// <summary>The standard these settings describe.</summary>
    public Standard ToStandard() => Core.Standard.From(Standard, Path.GetFileName(FilePath));

    /// <summary>Remembers the folder: written only when it changed, and never over a file that couldn't be read.</summary>
    public void SavePath()
    {
        if (Problem is not null || string.Equals(LastPath, _savedPath, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            Write();
        }
        catch
        {
            // A read-only folder just means the folder isn't remembered.
        }
    }

    /// <summary>Saves everything, from the Settings window. Throws when the file can't be written.</summary>
    public void SaveAll()
    {
        Write();
        Problem = null;
    }

    private void Write()
    {
        if (Standard is { IsEmpty: true }) Standard = null;
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        _savedPath = LastPath;
    }
}
