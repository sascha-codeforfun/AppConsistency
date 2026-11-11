using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using ImageKit;

namespace AppConsistency.Core;

/// <summary>
/// Finds an app's image files and works out what each one's level asks for, with ImageKit: the
/// metadata that would go, and how much smaller the file would get. Recompression is estimated
/// with ImageKit's quick setting; the Fix does the full work. Estimates are kept in memory by the
/// file's content, so a rescan of an unchanged snapshot doesn't encode anything again.
/// </summary>
public static class ImageScan
{
    /// <summary>Files with these extensions are read; what they are is then decided by their bytes.</summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".ico", ".cur", ".jpg", ".jpeg", ".jfif"
    };

    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".vs", ".git" };

    /// <summary>Larger files are listed but not estimated.</summary>
    private const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>A saving counts when it is at least this many bytes and this share of the file.</summary>
    public const long MinSavingBytes = 4096;
    public const double MinSavingShare = 0.10;

    private static readonly ConcurrentDictionary<string, long> Estimates = new();

    public static bool IsCandidate(string relPath) =>
        Extensions.Contains(Path.GetExtension(relPath)) &&
        !relPath.Split('/').SkipLast(1).Any(SkipFolders.Contains);

    public static bool SavingCounts(long saving, long length) =>
        saving >= MinSavingBytes && saving >= length * MinSavingShare;

    public static List<ImageFinding> Scan(RepoSnapshot s, string app)
    {
        var found = new List<ImageFinding>();
        var standard = Standard.Current;

        var files = new List<(string Rel, byte[] Bytes)>();
        s.VisitFiles(IsCandidate, MaxBytes, (rel, bytes) => files.Add((rel, bytes)));

        foreach (var (rel, bytes) in files)
        {
            var (level, entry) = standard.ImageLevelFor(app, rel);
            var finding = Inspect(rel, bytes, level, entry is null ? "default" : entry.Path);
            if (finding is not null) found.Add(finding);
        }

        // Oversized candidates are listed, so nothing goes unmentioned.
        foreach (var f in s.Files.Where(f => IsCandidate(f.RelPath) && f.Length > MaxBytes))
        {
            var (level, entry) = standard.ImageLevelFor(app, f.RelPath);
            found.Add(new ImageFinding(f.RelPath, f.Length, "", "", level, entry is null ? "default" : entry.Path,
                Array.Empty<string>(), Array.Empty<string>(), 0, false, "Too large to check here."));
        }

        return found.OrderBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One file at its level, or null when it isn't a PNG, ICO or JPEG after all.</summary>
    public static ImageFinding? Inspect(string rel, byte[] bytes, ImageLevel level, string levelFrom)
    {
        var info = ImageFile.Inspect(bytes);
        if (info.Format == ImageFormat.Unknown) return null;

        var removable = info.Metadata.Where(m => !m.Displayed).Select(m => m.Name).Distinct().ToList();
        var kept = info.Metadata.Where(m => m.Displayed).Select(m => m.Name).Distinct().ToList();
        if (info.Problem is not null)
            return new ImageFinding(rel, bytes.Length, info.Format.ToString(), info.Detail, level, levelFrom, removable, kept, 0, false, info.Problem);

        long saving = level switch
        {
            ImageLevel.AsIs => 0,
            _ => Estimate(bytes, level == ImageLevel.Strip ? ImageLevel.Strip : ImageLevel.Lossless)
        };

        bool needsWork = level != ImageLevel.AsIs
                         && (removable.Count > 0 || (level >= ImageLevel.Lossless && SavingCounts(saving, bytes.Length)));
        return new ImageFinding(rel, bytes.Length, info.Format.ToString(), info.Detail, level, levelFrom,
            removable, kept, saving, needsWork, null);
    }

    private static long Estimate(byte[] bytes, ImageLevel level)
    {
        var key = Convert.ToHexString(SHA256.HashData(bytes)) + "|" + level;
        return Estimates.GetOrAdd(key, _ =>
        {
            var r = ImageFile.Process(bytes, level, new ImageOptions { Effort = Effort.Quick });
            return Math.Max(0, r.SavedBytes);
        });
    }
}
