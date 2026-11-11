using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AppConsistency.Core;
using HueThemeKit;

namespace AppConsistency;

/// <summary>
/// Shown when the "remove unlisted files" fix is queued: one tick box per file that isn't on the
/// whitelist. Nothing is ticked to start with, so a file only goes when you tick it; the removal
/// is queued only when at least one is ticked. Cancel has the focus and answers Enter and Escape,
/// as ConfirmWindow does.
///
/// The file selected in the list is previewed on the right, so a stray old file can be told from
/// one that is needed: where the repo names it, which paths inside it no longer exist, and its
/// content (text by content, not by extension; images as a thumbnail that opens at full size;
/// anything else as its first bytes). Clicking a row selects it; only the box ticks it.
/// </summary>
public partial class UnlistedPickWindow : Window
{
    private const long MaxPreviewBytes = 64L * 1024 * 1024;
    private const int MaxShownLines = 2000;
    private const int MaxShownChars = 150_000;
    private const int MaxMentions = 6;
    private const int MaxMissing = 8;

    private readonly List<(string Path, CheckBox Box)> _boxes = new();
    private readonly Dictionary<ListBoxItem, UnlistedFile> _rows = new();
    private readonly string? _snapshotPath;

    private Task<RepoTextIndex?>? _index;
    private int _previewVersion;
    private CancellationTokenSource? _previewCancel;
    private BitmapSource? _image;
    private string _imageName = "";

    private UnlistedPickWindow(string appName, IReadOnlyList<UnlistedFile> files, string? snapshotPath, AppPlatform platform)
    {
        InitializeComponent();
        HueTitleBar.Attach(this);
        _snapshotPath = snapshotPath;

        var work = SystemParameters.WorkArea;
        Height = Math.Min(Height, work.Height - 40);
        Width = Math.Min(Width, work.Width - 40);

        HeadingText.Text = $"Choose the files to remove from {appName}";
        BodyText.Text =
            "These files aren't on the whitelist and no project names them. Tick the ones to leave out of the " +
            "fixed copy; anything you don't tick stays. Click a file to preview it. Files the README uses are " +
            "listed apart: removing one leaves the README pointing at nothing. The history folder isn't changed.";
        // A Linux app's whitelist has its own patterns on top (*.axaml).
        WhitelistText.Text = "On the whitelist: " + FileHygiene.WhitelistTextFor(platform);

        string? group = null;
        foreach (var file in files)
        {
            var heading = file.ReadmeUses ? "Used by the README" : "Not referenced";
            if (heading != group)
            {
                var title = new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold };
                title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
                FileList.Items.Add(new ListBoxItem
                {
                    Content = title,
                    Focusable = false,
                    IsHitTestVisible = false,
                    Padding = new Thickness(10, group is null ? 4 : 14, 10, 4)
                });
                group = heading;
            }

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // The box takes no focus: the row keeps it, so Up/Down and Space work after a click, and
            // Enter still reaches Cancel.
            var box = new CheckBox { IsChecked = false, Focusable = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            box.Checked += (_, _) => UpdateConfirm();
            box.Unchecked += (_, _) => UpdateConfirm();
            System.Windows.Automation.AutomationProperties.SetName(box, file.RelPath);
            row.Children.Add(box);

            var label = new TextBlock { Text = file.RelPath, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            if (TryFindResource("Text.Mono") is Style mono) label.Style = mono;
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            var note = file.DuplicateOf is { } of ? $"same name as {of}  ·  " : "";
            var size = new TextBlock { Text = note + Size(file.Length), Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            if (TryFindResource("Text.Caption") is Style caption) size.Style = caption;
            Grid.SetColumn(size, 2);
            row.Children.Add(size);

            var item = new ListBoxItem { Content = row, Padding = new Thickness(10, 6, 10, 6) };
            box.Click += (_, _) =>
            {
                FileList.SelectedItem = item;
                item.Focus();
            };
            FileList.Items.Add(item);
            _rows[item] = file;
            _boxes.Add((file.RelPath, box));
        }

        UpdateConfirm();
        Loaded += (_, _) =>
        {
            CancelButton.Focus();
            _index = Task.Run(LoadIndex);
            FileList.SelectedItem = _rows.Keys.FirstOrDefault();
        };
    }

    /// <summary>The ticked files, or null on cancel. Never empty: Queue removal needs at least one tick.</summary>
    public static List<string>? Ask(Window owner, string appName, IReadOnlyList<UnlistedFile> files, string? snapshotPath,
                                    AppPlatform platform = AppPlatform.Windows)
    {
        var window = new UnlistedPickWindow(appName, files, snapshotPath, platform) { Owner = owner };
        if (window.ShowDialog() != true) return null;

        var picked = window._boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Path).ToList();
        return picked.Count == 0 ? null : picked;
    }

    // ---------------- Preview ----------------

    private RepoTextIndex? LoadIndex()
    {
        try
        {
            if (_snapshotPath is null) return null;
            bool isZip = File.Exists(_snapshotPath);
            if (!isZip && !Directory.Exists(_snapshotPath)) return null;
            return RepoTextIndex.Build(RepoSnapshot.Load(_snapshotPath, isZip));
        }
        catch
        {
            return null;
        }
    }

    private sealed record Preview(
        UnlistedFile File,
        string Facts,
        List<FileMention>? Mentions,
        (List<string> Missing, int Checked)? Paths,
        string? Text,
        bool Cut,
        int Lines,
        BitmapSource? Image,
        string? Message);

    private async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not ListBoxItem item || !_rows.TryGetValue(item, out var file)) return;
        FileList.ScrollIntoView(item);

        int version = ++_previewVersion;
        _previewCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        _previewCancel = cancel;
        ShowLoading(file);

        Preview preview;
        try
        {
            // Holding Down runs through rows quickly; only the row it stops on is read.
            await Task.Delay(120, cancel.Token);
            var index = _index is null ? null : await _index;
            if (version != _previewVersion) return;
            preview = await Task.Run(() => Build(file, index, cancel.Token), cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            preview = new Preview(file, Size(file.Length), null, null, null, false, 0, null, "Couldn't read the file: " + ex.Message);
        }

        if (version == _previewVersion) Show(preview);
    }

    private void ShowLoading(UnlistedFile file)
    {
        PreviewTitle.Text = file.RelPath;
        PreviewFacts.Text = Size(file.Length);
        NamedLabel.Visibility = NamedText.Visibility = Visibility.Collapsed;
        PathsLabel.Visibility = PathsText.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Collapsed;
        ThumbButton.Visibility = Visibility.Collapsed;
        PreviewMessage.Visibility = Visibility.Visible;
        PreviewMessage.Text = _index is { IsCompleted: false } ? "Reading the snapshot…" : "Reading the file…";
    }

    /// <summary>Runs off the UI thread: reads and sniffs the file, finds its mentions and stale paths.</summary>
    private static Preview Build(UnlistedFile file, RepoTextIndex? index, CancellationToken cancel)
    {
        if (index is null)
            return new Preview(file, Size(file.Length), null, null, null, false, 0, null,
                "The snapshot couldn't be read, so there is nothing to preview.");

        var mentions = index.Mentions(file.RelPath);
        cancel.ThrowIfCancellationRequested();

        if (file.Length > MaxPreviewBytes)
            return new Preview(file, Size(file.Length), mentions, null, null, false, 0, null,
                $"Too large to preview ({Size(file.Length)}).");

        string? text = index.TextOf(file.RelPath);
        string encoding = index.EncodingOf(file.RelPath);

        if (text is null)
        {
            var bytes = index.Snapshot.ReadBytes(file.RelPath);
            if (bytes is null)
                return new Preview(file, Size(file.Length), mentions, null, null, false, 0, null, "Couldn't read the file.");

            cancel.ThrowIfCancellationRequested();
            var sniff = ContentSniff.Sniff(bytes);
            if (sniff.Kind == ContentKind.Image)
            {
                var image = Decode(bytes);
                if (image is not null)
                    return new Preview(file, $"{sniff.ImageFormat}  ·  {image.PixelWidth} × {image.PixelHeight} px  ·  {Size(file.Length)}",
                        mentions, null, null, false, 0, image, null);
                return new Preview(file, $"{sniff.ImageFormat}, couldn't be drawn  ·  {Size(file.Length)}  ·  first {Math.Min(bytes.Length, 512)} bytes",
                    mentions, null, ContentSniff.HexHead(bytes, 512), false, 0, null, null);
            }
            if (sniff.Kind == ContentKind.Binary)
                return new Preview(file, $"Binary  ·  {Size(file.Length)}  ·  first {Math.Min(bytes.Length, 512)} bytes",
                    mentions, null, ContentSniff.HexHead(bytes, 512), false, 0, null, null);

            text = sniff.Text!;
            encoding = sniff.Encoding;
        }

        cancel.ThrowIfCancellationRequested();
        int lines = ContentSniff.CountLines(text);
        var (shown, cut) = ContentSniff.Head(text, MaxShownLines, MaxShownChars);
        var endings = ContentSniff.LineEndings(text);
        var facts = string.Join("  ·  ", new[] { encoding, endings, $"{lines:N0} {(lines == 1 ? "line" : "lines")}", Size(file.Length) }
            .Where(s => s.Length > 0));

        return new Preview(file, facts, mentions, index.StalePaths(file.RelPath, text), shown, cut, lines, null, null);
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            // An .ico holds several sizes; the largest is the one worth looking at.
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    private void Show(Preview p)
    {
        PreviewTitle.Text = p.File.RelPath;
        PreviewFacts.Text = p.Facts;

        NamedLabel.Visibility = NamedText.Visibility = p.Mentions is null ? Visibility.Collapsed : Visibility.Visible;
        if (p.Mentions is { } mentions)
        {
            NamedText.Text = mentions.Count == 0
                ? "Named nowhere else in the repo."
                : string.Join("\n", mentions.Take(MaxMentions).Select(m => $"{m.RelPath}:{m.Line}   {m.Text}"))
                  + (mentions.Count > MaxMentions ? $"\n(+{mentions.Count - MaxMentions} more)" : "");
        }

        PathsLabel.Visibility = PathsText.Visibility = p.Paths is null ? Visibility.Collapsed : Visibility.Visible;
        if (p.Paths is { } paths)
        {
            PathsText.Text = paths.Checked == 0
                ? "Names no file or folder paths."
                : paths.Missing.Count == 0
                    ? $"All {paths.Checked} paths it names exist in the repo."
                    : $"{paths.Missing.Count} of {paths.Checked} aren't in the repo: " +
                      string.Join(", ", paths.Missing.Take(MaxMissing)) +
                      (paths.Missing.Count > MaxMissing ? $" (+{paths.Missing.Count - MaxMissing} more)" : "");
        }

        _image = p.Image;
        _imageName = p.File.RelPath;
        ThumbButton.Visibility = p.Image is null ? Visibility.Collapsed : Visibility.Visible;
        ThumbImage.Source = p.Image;
        if (p.Image is not null)
        {
            ThumbImage.MaxWidth = p.Image.PixelWidth;
            ThumbImage.MaxHeight = p.Image.PixelHeight;
        }

        // A hex dump keeps its columns: no wrapping, a horizontal scrollbar instead.
        bool hex = p.Text is not null && p.Lines == 0;
        PreviewText.TextWrapping = hex ? TextWrapping.NoWrap : TextWrapping.Wrap;
        PreviewText.HorizontalScrollBarVisibility = hex ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        PreviewText.Visibility = p.Text is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewText.Text = p.Text is null ? "" : p.Cut ? p.Text + $"\n\n… showing the start of {p.Lines:N0} lines." : p.Text;
        PreviewText.ScrollToHome();

        PreviewMessage.Visibility = p.Message is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewMessage.Text = p.Message ?? "";
    }

    private void Thumb_Click(object sender, RoutedEventArgs e)
    {
        if (_image is null) return;
        new ImageZoomWindow(_image, _imageName) { Owner = this }.Show();
    }

    // ---------------- Ticks ----------------

    /// <summary>Space ticks or unticks the selected file, as it would on a plain tick box.</summary>
    private void FileList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.OriginalSource is CheckBox) return;
        if (FileList.SelectedItem is not ListBoxItem item || !_rows.ContainsKey(item)) return;
        var box = _boxes.First(b => b.Path == _rows[item].RelPath).Box;
        box.IsChecked = box.IsChecked != true;
        UpdateConfirm();
        e.Handled = true;
    }

    private void UpdateConfirm()
    {
        int ticked = _boxes.Count(b => b.Box.IsChecked == true);
        ConfirmButton.IsEnabled = ticked > 0;
        CountText.Text = $"{ticked} of {_boxes.Count} ticked";
    }

    private void TickAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (_, box) in _boxes) box.IsChecked = true;
        UpdateConfirm();
    }

    private void UntickAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (_, box) in _boxes) box.IsChecked = false;
        UpdateConfirm();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";
}
