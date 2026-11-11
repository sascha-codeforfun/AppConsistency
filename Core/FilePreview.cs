using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AppConsistency.Core;

/// <summary>What a file's bytes are, judged by its content and never by its extension.</summary>
public enum ContentKind { Text, Image, Binary }

/// <summary>A file's content, read for the removal picker's preview.</summary>
public sealed record ContentSniffResult(
    ContentKind Kind,
    string? Text,           // Text: the decoded text
    string Encoding,        // Text: "UTF-8", "UTF-8 with BOM", "ASCII", "UTF-16 LE", "8-bit (not UTF-8)"
    string? ImageFormat);   // Image: "PNG", "JPEG", "GIF", "BMP", "ICO"

/// <summary>
/// Tells text, images and other binaries apart by their bytes. Text is anything that decodes
/// cleanly (UTF-8 or UTF-16 with a byte order mark, UTF-8 or 8-bit without one) and holds no NUL
/// and almost no other control characters, so a .ged, a .txt or a file with no extension at all
/// shows as text when it is text. Images are recognised by their signature, for the formats WPF
/// can draw.
/// </summary>
public static class ContentSniff
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static ContentSniffResult Sniff(byte[] bytes)
    {
        if (ImageFormat(bytes) is { } format) return new(ContentKind.Image, null, "", format);
        if (AsText(bytes) is { } text) return new(ContentKind.Text, text.Text, text.Encoding, null);
        return new(ContentKind.Binary, null, "", null);
    }

    public static string? ImageFormat(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A) return "PNG";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "JPEG";
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8' && (b[4] == '7' || b[4] == '9') && b[5] == 'a') return "GIF";
        if (b.Length >= 26 && b[0] == 'B' && b[1] == 'M' && BitConverter.ToUInt32(b.Slice(2, 4)) == b.Length) return "BMP";
        if (b.Length >= 6 && b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0 && b[4] + b[5] * 256 is > 0 and < 256) return "ICO";
        return null;
    }

    public static (string Text, string Encoding)? AsText(byte[] b)
    {
        string text;
        string encoding;

        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        {
            if (!TryDecode(StrictUtf8, b, 3, out text)) return null;
            encoding = "UTF-8 with BOM";
        }
        else if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
        {
            if (!TryDecode(new UnicodeEncoding(false, false, true), b, 2, out text)) return null;
            encoding = "UTF-16 LE";
        }
        else if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
        {
            if (!TryDecode(new UnicodeEncoding(true, false, true), b, 2, out text)) return null;
            encoding = "UTF-16 BE";
        }
        else
        {
            if (Array.IndexOf(b, (byte)0) >= 0) return null;
            if (TryDecode(StrictUtf8, b, 0, out text))
                encoding = b.All(x => x < 0x80) ? "ASCII" : "UTF-8";
            else
            {
                text = System.Text.Encoding.Latin1.GetString(b);
                encoding = "8-bit (not UTF-8)";
            }
        }

        return LooksLikeText(text) ? (text, encoding) : null;
    }

    private static bool TryDecode(System.Text.Encoding encoding, byte[] b, int offset, out string text)
    {
        try
        {
            text = encoding.GetString(b, offset, b.Length - offset);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    /// <summary>
    /// No NUL, and control characters other than tab, line breaks, form feed and a trailing
    /// end-of-file mark are rare: at most one in 200 characters, and never more than a handful in
    /// a short file.
    /// </summary>
    private static bool LooksLikeText(string text)
    {
        int sample = Math.Min(text.Length, 256 * 1024);
        int control = 0;
        for (int i = 0; i < sample; i++)
        {
            char c = text[i];
            if (c == '\0') return false;
            if (c < 0x20 && c is not ('\t' or '\n' or '\r' or '\f' or '\v') && !(c == '\x1A' && i == text.Length - 1))
                control++;
        }
        return control <= Math.Max(2, sample / 200);
    }

    public static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        int lines = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') lines++;
            else if (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')) lines++;
        }
        if (text[^1] is '\n' or '\r') lines--;
        return lines;
    }

    /// <summary>"CRLF", "LF", "CR", "mixed line endings", or "" for a single line.</summary>
    public static string LineEndings(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (text[i] == '\n') lf++;
        }
        int kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        return kinds > 1 ? "mixed line endings" : crlf > 0 ? "CRLF" : lf > 0 ? "LF" : cr > 0 ? "CR" : "";
    }

    /// <summary>The first lines of a text, for showing: at most maxLines lines and maxChars characters.</summary>
    public static (string Shown, bool Cut) Head(string text, int maxLines, int maxChars)
    {
        int lines = 0, i = 0, limit = Math.Min(text.Length, maxChars);
        for (; i < limit; i++)
        {
            if (text[i] == '\n' && ++lines == maxLines) { i++; break; }
        }
        return i >= text.Length ? (text, false) : (text[..i], true);
    }

    /// <summary>The first bytes as a hex dump, 16 to a line, with the printable ones beside.</summary>
    public static string HexHead(byte[] bytes, int max)
    {
        var sb = new StringBuilder();
        int count = Math.Min(bytes.Length, max);
        for (int row = 0; row < count; row += 16)
        {
            sb.Append(row.ToString("X6")).Append("  ");
            for (int i = 0; i < 16; i++)
            {
                if (row + i < count) sb.Append(bytes[row + i].ToString("X2")).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int i = 0; i < 16 && row + i < count; i++)
            {
                byte x = bytes[row + i];
                sb.Append(x is >= 0x20 and < 0x7F ? (char)x : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>One line elsewhere in the repo that names a file.</summary>
public sealed record FileMention(string RelPath, int Line, string Text);

/// <summary>
/// Every text file of a snapshot, read once, so the picker can say for any file where the repo
/// names it and which paths inside it no longer exist. Text is judged by content, as the preview
/// does. Build output and tool folders are left out: a name in obj/ isn't a reference.
/// </summary>
public sealed class RepoTextIndex
{
    private const long MaxBytes = 4 * 1024 * 1024;

    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules"
    };

    private static readonly Regex WebAddress = new(@"\b[a-z][a-z0-9+.\-]*://\S+|\bwww\.\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A run of name characters, optionally with / or \ between parts and a trailing separator.
    private static readonly Regex PathToken = new(@"[\w.\-]+(?:[/\\][\w.\-]+)*[/\\]?", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _texts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _encodings = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase);

    public RepoSnapshot Snapshot { get; }

    private RepoTextIndex(RepoSnapshot snapshot) => Snapshot = snapshot;

    public static RepoTextIndex Build(RepoSnapshot s)
    {
        var index = new RepoTextIndex(s);
        foreach (var f in s.Files)
        {
            index._files.Add(f.RelPath);
            index._names.Add(f.Name);
            var ext = Path.GetExtension(f.Name);
            if (ext.Length > 1 && ext.Skip(1).Any(char.IsLetter)) index._extensions.Add(ext);
        }
        foreach (var d in s.Directories) index._dirs.Add(d);

        s.VisitFiles(MaxBytes, (rel, bytes) =>
        {
            if (rel.Split('/').SkipLast(1).Any(SkipFolders.Contains)) return;
            if (ContentSniff.ImageFormat(bytes) is not null) return;
            if (ContentSniff.AsText(bytes) is { } text)
            {
                index._texts[rel] = text.Text;
                index._encodings[rel] = text.Encoding;
            }
        });
        return index;
    }

    /// <summary>The file's text when it was read as text, else null.</summary>
    public string? TextOf(string relPath) => _texts.GetValueOrDefault(relPath);

    /// <summary>How that text was encoded, as ContentSniff names it.</summary>
    public string EncodingOf(string relPath) => _encodings.GetValueOrDefault(relPath) ?? "";

    /// <summary>
    /// Lines in other text files that name this file: its name as a word of its own, not inside a
    /// web address. Sorted by path and line.
    /// </summary>
    public List<FileMention> Mentions(string relPath)
    {
        var name = RepoSnapshot.NameOf(relPath);
        var pattern = new Regex($@"(?<![\w.\-]){Regex.Escape(name)}(?![\w\-]|\.\w)", RegexOptions.IgnoreCase);
        var hits = new List<FileMention>();

        foreach (var (path, text) in _texts.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (path.Equals(relPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (text.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;

            int lineNo = 0;
            foreach (var line in text.Split('\n'))
            {
                lineNo++;
                if (line.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!pattern.IsMatch(WebAddress.Replace(line, " "))) continue;
                hits.Add(new FileMention(path, lineNo, Trim(line)));
            }
        }
        return hits;
    }

    /// <summary>
    /// Paths the text names that aren't in the repo. A word counts as a path when it has a
    /// separator, or when it ends in an extension some file in this repo has; so "Process.Start"
    /// or "1.0.15" are not taken for paths, and the extensions come from the repo, not from a list.
    /// Web addresses and rooted paths (C:\…, /usr/…, %APPDATA%\…) are not the repo's and are skipped.
    /// A path is found when a file or folder of the repo has it as its path or its ending, from the
    /// text's own folder or anywhere, so a layout note written from a different root still matches.
    /// </summary>
    public (List<string> Missing, int Checked) StalePaths(string relPath, string text)
    {
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int checkedCount = 0;
        var ownDir = RepoSnapshot.DirOf(relPath);
        var clean = WebAddress.Replace(text, m => new string(' ', m.Length));

        foreach (Match m in PathToken.Matches(clean))
        {
            var token = m.Value.TrimEnd('.', '-');
            if (token.Length < 3) continue;
            if (m.Index > 0 && clean[m.Index - 1] is '/' or '\\' or ':' or '%' or '$' or '~' or '@' or '*' or '{' or '}') continue;
            if (token.StartsWith('-') || token.StartsWith("...", StringComparison.Ordinal)) continue;
            if (token.Contains(':')) continue;

            bool hasSep = token.IndexOfAny(['/', '\\']) >= 0;
            bool isFolder = token.EndsWith('/') || token.EndsWith('\\');
            var normal = RepoSnapshot.Normalize(token);
            if (normal.Length == 0) continue;

            // A folder is a word followed by a separator and then a space, the line's end or closing
            // punctuation: "Docs/" in a layout block, not "other/" in other/"*".
            if (isFolder)
            {
                int after = m.Index + m.Length;
                if (after < clean.Length && !char.IsWhiteSpace(clean[after]) && clean[after] is not ('`' or ')' or ',' or ';' or ':' or '.' or ']'))
                    continue;
            }

            // Build output and tool folders are named in notes on purpose and never kept.
            if (normal.Split('/').Any(SkipFolders.Contains)) continue;

            var last = RepoSnapshot.NameOf(normal);
            var ext = Path.GetExtension(last);
            // ".Png" or ".Path" with a capital and then small letters is a member (ImageFormat.Png), not an extension.
            bool memberLike = ext.Length > 2 && char.IsUpper(ext[1]) && ext.Skip(2).Any(char.IsLower);
            bool knownExt = ext.Length > 1 && !memberLike && _extensions.Contains(ext);

            if (!isFolder && !knownExt) continue;
            // ".csproj" on its own names a kind of file, not a file.
            if (!hasSep && last.StartsWith('.') && _extensions.Contains(last)) continue;

            if (!seen.Add(normal)) continue;
            checkedCount++;

            if (!Exists(normal, ownDir, hasSep)) missing.Add(isFolder ? normal + "/" : normal);
        }

        return (missing, checkedCount);
    }

    private bool Exists(string path, string ownDir, bool hasSep)
    {
        if (!hasSep)
            return _names.Contains(path) || _dirs.Any(d => RepoSnapshot.NameOf(d).Equals(path, StringComparison.OrdinalIgnoreCase));

        var fromOwn = RepoSnapshot.Combine(ownDir, path);
        if (_files.Contains(fromOwn) || _dirs.Contains(fromOwn)) return true;
        if (_files.Contains(path) || _dirs.Contains(path)) return true;

        var tail = "/" + path;
        return _files.Any(f => f.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
            || _dirs.Any(d => d.EndsWith(tail, StringComparison.OrdinalIgnoreCase));
    }

    private static string Trim(string line)
    {
        var t = line.Trim();
        return t.Length <= 140 ? t : t[..137] + "…";
    }
}
