using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppConsistency.Core;
using HueThemeKit;
using ImageKit;

namespace AppConsistency;

/// <summary>
/// Shown when the image fix is queued: one row per image file with its level, which can be changed
/// here. Nothing is ticked to start with; only ticked files change. The selected file is previewed
/// on the right with what its level does to it, worked out in full with ImageKit and checked by
/// Windows' own decoder too. A palette (Smallest) is never used on its own: when it stays within
/// the loss limit, the original, the palette version and a map of what changed can be compared,
/// and "Use the palette" has to be ticked for that file. Changed levels can be remembered in
/// Settings. Cancel has the focus and answers Enter and Escape, as in the other pickers.
/// </summary>
public partial class ImagePickWindow : Window
{
    /// <summary>What the picker hands back: the new file versions, and the levels to remember.</summary>
    public sealed record Outcome(Dictionary<string, ImageChange> Changes, List<ImageLevelEntry> Remember);

    private enum View { Original, Palette, Difference }

    private sealed class Row
    {
        public required ImageFinding Finding { get; init; }
        public required ListBoxItem Item { get; init; }
        public required CheckBox Box { get; init; }
        public required ComboBox LevelBox { get; init; }
        public required TextBlock Note { get; init; }
        public ImageLevel Level { get; set; }
        public bool UsePalette { get; set; }
        public Dictionary<ImageLevel, Task<ImageResult>> Results { get; } = new();
        public bool Usable => Finding.Problem is null && Level != ImageLevel.AsIs;
    }

    private static readonly (ImageLevel Level, string Word)[] Levels =
    {
        (ImageLevel.AsIs, "As is"), (ImageLevel.Strip, "Strip"), (ImageLevel.Lossless, "Lossless"), (ImageLevel.Smallest, "Smallest")
    };

    private readonly string _app;
    private readonly string? _snapshotPath;
    private readonly List<Row> _rows = new();
    private readonly Dictionary<ListBoxItem, Row> _byItem = new();
    private Task<Dictionary<string, byte[]>>? _files;
    private int _previewVersion;
    private View _view = View.Original;
    private Row? _shown;
    private BitmapSource? _thumb;
    private Outcome? _outcome;
    private bool _preparing;

    private ImagePickWindow(string appName, IReadOnlyList<ImageFinding> images, string? snapshotPath)
    {
        InitializeComponent();
        HueTitleBar.Attach(this);
        _app = appName;
        _snapshotPath = snapshotPath;

        var work = SystemParameters.WorkArea;
        Height = Math.Min(Height, work.Height - 40);
        Width = Math.Min(Width, work.Width - 40);

        var standard = Standard.Current;
        HeadingText.Text = $"Choose the image files to change in {appName}";
        BodyText.Text =
            "Each file has a level: As is changes nothing, Strip removes metadata nothing displays, Lossless also recompresses " +
            "with every pixel kept, Smallest also offers a palette where the loss stays within the limit. Change a level here, " +
            "tick the files to change, and they're changed in the fixed copy only. A palette is used only where you tick " +
            "\"Use the palette\" after comparing. The history folder isn't changed.";
        SettingsText.Text =
            $"Default level: {Word(standard.ImageDefaultLevel)}. Palette limit: " +
            (standard.ImageLossLimit == LossLimit.Invisible
                ? "invisible (99 % of pixels within ΔE 1.0, no spot beyond 3.0)."
                : "slight (99 % of pixels within ΔE 2.3, no spot beyond 6.0).") +
            " Both, and the levels you keep, are in Settings.";

        AddGroup("To do", images.Where(i => i.NeedsWork && i.Problem is null));
        AddGroup("Meets its level", images.Where(i => !i.NeedsWork && i.Problem is null && i.Level != ImageLevel.AsIs));
        AddGroup("Kept as they are (As is)", images.Where(i => i.Problem is null && i.Level == ImageLevel.AsIs));
        AddGroup("Not readable", images.Where(i => i.Problem is not null));

        UpdateCounts();
        // While Confirm prepares the files, the window stays open: closing it then would cut the work off midway.
        Closing += (_, e) => { if (_preparing) e.Cancel = true; };
        Loaded += (_, _) =>
        {
            CancelButton.Focus();
            _files = Task.Run(LoadFiles);
            FileList.SelectedItem = _rows.FirstOrDefault()?.Item;
        };
    }

    /// <summary>The changes to queue, or null on cancel. Never empty.</summary>
    public static Outcome? Ask(Window owner, string appName, IReadOnlyList<ImageFinding> images, string? snapshotPath)
    {
        var window = new ImagePickWindow(appName, images, snapshotPath) { Owner = owner };
        return window.ShowDialog() == true ? window._outcome : null;
    }

    private static string Word(ImageLevel level) => Levels.First(l => l.Level == level).Word;

    // ---------------- List ----------------

    private void AddGroup(string heading, IEnumerable<ImageFinding> findings)
    {
        var list = findings.ToList();
        if (list.Count == 0) return;

        var title = new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        FileList.Items.Add(new ListBoxItem
        {
            Content = title, Focusable = false, IsHitTestVisible = false,
            Padding = new Thickness(10, FileList.Items.Count == 0 ? 4 : 14, 10, 4)
        });

        foreach (var f in list) AddRow(f);
    }

    private void AddRow(ImageFinding f)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The box takes no focus: the row keeps it, so Up/Down and Space work, and Enter reaches Cancel.
        var box = new CheckBox { Focusable = false, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 10, 0) };
        System.Windows.Automation.AutomationProperties.SetName(box, f.RelPath);
        grid.Children.Add(box);

        var text = new StackPanel();
        var path = new TextBlock { Text = f.RelPath, TextWrapping = TextWrapping.Wrap };
        if (TryFindResource("Text.Mono") is Style mono) path.Style = mono;
        path.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        text.Children.Add(path);
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        if (TryFindResource("Text.Caption") is Style caption) note.Style = caption;
        text.Children.Add(note);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var levelBox = new ComboBox { MinWidth = 120, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        foreach (var (_, word) in Levels) levelBox.Items.Add(word);
        levelBox.SelectedIndex = (int)f.Level;
        levelBox.IsEnabled = f.Problem is null;
        System.Windows.Automation.AutomationProperties.SetName(levelBox, "Level for " + f.RelPath);
        Grid.SetColumn(levelBox, 2);
        grid.Children.Add(levelBox);

        var item = new ListBoxItem { Content = grid, Padding = new Thickness(10, 6, 10, 6) };
        var row = new Row { Finding = f, Item = item, Box = box, LevelBox = levelBox, Note = note, Level = f.Level };
        box.IsEnabled = row.Usable;

        box.Checked += (_, _) => UpdateCounts();
        box.Unchecked += (_, _) => UpdateCounts();
        box.Click += (_, _) => { FileList.SelectedItem = item; item.Focus(); };
        levelBox.GotKeyboardFocus += (_, _) => FileList.SelectedItem = item;
        levelBox.SelectionChanged += (_, _) => LevelChanged(row);

        FileList.Items.Add(item);
        _rows.Add(row);
        _byItem[item] = row;
        UpdateNote(row);
    }

    private void LevelChanged(Row row)
    {
        if (row.LevelBox.SelectedIndex < 0) return;
        row.Level = Levels[row.LevelBox.SelectedIndex].Level;
        row.UsePalette = false;
        row.Box.IsEnabled = row.Usable;
        if (!row.Usable) row.Box.IsChecked = false;
        UpdateNote(row);
        UpdateCounts();
        if (FileList.SelectedItem == row.Item) _ = ShowPreview(row);
    }

    private void UpdateNote(Row row)
    {
        var f = row.Finding;
        var parts = new List<string> { Size(f.Length) };
        if (f.Problem is not null) parts.Add(f.Problem);
        else if (row.Level == ImageLevel.AsIs) parts.Add("nothing changes");
        else
        {
            if (f.Removable.Count > 0) parts.Add($"{f.Removable.Count} metadata item{(f.Removable.Count == 1 ? "" : "s")} to remove");
            if (row.Level >= ImageLevel.Lossless && f.Saving > 0 && f.Level >= ImageLevel.Lossless)
                parts.Add($"about {Size(f.Saving)} smaller");
            if (row.Level == ImageLevel.Smallest) parts.Add(row.UsePalette ? "palette ticked" : "palette not ticked");
        }
        if (row.Level != f.Level) parts.Add($"level changed from {Word(f.Level)}");
        else if (f.LevelFrom != "default") parts.Add($"level from Settings ({f.LevelFrom})");
        row.Note.Text = string.Join("  ·  ", parts);
    }

    private void UpdateCounts()
    {
        if (_preparing) return;
        int ticked = _rows.Count(r => r.Box.IsChecked == true);
        int changed = _rows.Count(r => r.Level != r.Finding.Level);
        CountText.Text = $"{ticked} of {_rows.Count(r => r.Usable)} ticked" +
                         (changed > 0 ? $"  ·  {changed} level{(changed == 1 ? "" : "s")} changed" : "");
        ConfirmButton.IsEnabled = ticked > 0 || (changed > 0 && RememberBox.IsChecked == true);
        RememberBox.IsEnabled = changed > 0;
    }

    private void Remember_Click(object sender, RoutedEventArgs e) => UpdateCounts();

    private void TickAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows.Where(r => r.Usable && r.Finding.NeedsWork)) r.Box.IsChecked = true;
    }

    private void UntickAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Box.IsChecked = false;
    }

    /// <summary>Space ticks or unticks the selected file; inside the level list it does what it does there.</summary>
    private void FileList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.OriginalSource is not ListBoxItem) return;
        if (FileList.SelectedItem is not ListBoxItem item || !_byItem.TryGetValue(item, out var row) || !row.Usable) return;
        row.Box.IsChecked = row.Box.IsChecked != true;
        e.Handled = true;
    }

    // ---------------- Reading and processing ----------------

    private Dictionary<string, byte[]> LoadFiles()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (_snapshotPath is null) return files;
        bool isZip = File.Exists(_snapshotPath);
        if (!isZip && !Directory.Exists(_snapshotPath)) return files;
        var wanted = new HashSet<string>(_rows.Where(r => r.Finding.Problem is null).Select(r => r.Finding.RelPath), StringComparer.OrdinalIgnoreCase);
        RepoSnapshot.Load(_snapshotPath, isZip).VisitFiles(wanted.Contains, long.MaxValue, (rel, bytes) => files[rel] = bytes);
        return files;
    }

    private async Task<byte[]?> BytesOf(Row row)
    {
        if (_files is null) return null;
        var files = await _files;
        return files.GetValueOrDefault(row.Finding.RelPath);
    }

    /// <summary>The full result of one level for one file, worked out once and kept.</summary>
    private Task<ImageResult> ResultFor(Row row, ImageLevel level)
    {
        if (row.Results.TryGetValue(level, out var existing)) return existing;
        var task = Task.Run(async () =>
        {
            var bytes = await BytesOf(row).ConfigureAwait(false) ?? throw new IOException("The file couldn't be read from the snapshot.");
            return ImageFile.Process(bytes, level, new ImageOptions
            {
                Effort = Effort.Full,
                LossLimit = Standard.Current.ImageLossLimit,
                ExternalDecoder = WindowsDecode
            });
        });
        row.Results[level] = task;
        return task;
    }

    /// <summary>
    /// Windows' own decoder (WIC, through WPF), as the second opinion ImageKit asks for: straight
    /// RGBA, colour profiles ignored, so it reads the stored values exactly as ImageKit does.
    /// </summary>
    internal static DecodedPixels? WindowsDecode(byte[] bytes)
    {
        try
        {
            var frame = Decode(bytes);
            if (frame is null) return null;
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);
            for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
            return new DecodedPixels(w, h, px);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The largest picture in the file, decoded and frozen, or null. Safe on any thread.</summary>
    internal static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    // ---------------- Preview ----------------

    private async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A level list's own SelectionChanged bubbles up to here; only the file list's counts.
        if (!ReferenceEquals(e.OriginalSource, FileList)) return;
        if (FileList.SelectedItem is not ListBoxItem item || !_byItem.TryGetValue(item, out var row)) return;
        FileList.ScrollIntoView(item);
        await ShowPreview(row);
    }

    private async Task ShowPreview(Row row)
    {
        int version = ++_previewVersion;
        _shown = row;
        var f = row.Finding;

        PreviewTitle.Text = f.RelPath;
        PreviewFacts.Text = string.Join("  ·  ", new[] { f.Detail, Size(f.Length) }.Where(s => s.Length > 0));
        MetadataText.Text =
            (f.Removable.Count > 0 ? "Nothing displays: " + string.Join(", ", f.Removable) + "." : "Nothing that isn't displayed.") +
            (f.Kept.Count > 0 ? " Kept, because it changes how the picture looks: " + string.Join(", ", f.Kept) + "." : "");
        PalettePanel.Visibility = Visibility.Collapsed;
        ViewPanel.Visibility = Visibility.Collapsed;
        ViewNote.Text = "";
        if (_view != View.Original) _view = View.Original;

        if (f.Problem is not null)
        {
            ResultText.Text = "Left as it is: " + f.Problem;
            await ShowImage(row, version, null);
            return;
        }

        ResultText.Text = row.Level == ImageLevel.AsIs ? "Nothing changes: every byte stays, metadata included." : "Working it out…";
        await ShowImage(row, version, null);
        if (row.Level == ImageLevel.AsIs || version != _previewVersion) return;

        ImageResult result;
        ImageResult? smallest = null;
        try
        {
            // Smallest shows the lossless result as the level's base, and the palette apart from it.
            result = await ResultFor(row, row.Level == ImageLevel.Smallest ? ImageLevel.Lossless : row.Level);
            if (row.Level == ImageLevel.Smallest) smallest = await ResultFor(row, ImageLevel.Smallest);
        }
        catch (Exception ex)
        {
            if (version == _previewVersion) ResultText.Text = "Couldn't work it out: " + ex.Message;
            return;
        }
        if (version != _previewVersion) return;

        ResultText.Text = Describe(result);

        if (smallest is null) return;
        var notes = smallest.Notes.Where(n => n.StartsWith("Palette", StringComparison.Ordinal) || n.StartsWith("Too many", StringComparison.Ordinal)
                                              || n.StartsWith("Already 256", StringComparison.Ordinal) || n.StartsWith("Icons", StringComparison.Ordinal)
                                              || n.StartsWith("JPEG", StringComparison.Ordinal)).ToList();
        PalettePanel.Visibility = Visibility.Visible;
        if (smallest.Palette is not { } stats)
        {
            PaletteText.Text = notes.Count > 0 ? string.Join(" ", notes) : "No palette for this file.";
            UsePaletteBox.Visibility = Visibility.Collapsed;
            return;
        }

        bool offered = smallest.PaletteApplied;
        var lossLimit = Standard.Current.ImageLossLimit;
        var limit = lossLimit == LossLimit.Invisible ? "invisible" : "slight";
        var notUsed = smallest.Notes.FirstOrDefault(n => n.StartsWith("Palette not used", StringComparison.Ordinal));
        PaletteText.Text = offered
            ? $"Palette of {stats.Colours} colours, within the {limit} limit: {Size(smallest.OriginalLength)} → {Size(smallest.Bytes.Length)} " +
              $"({Percent(smallest)}). {stats}. Compare below before you tick it."
            : !stats.Within(lossLimit)
                ? $"Palette of {stats.Colours} colours: {stats}, beyond the {limit} limit, so it isn't offered. You can still look at it below."
                : $"Palette of {stats.Colours} colours, within the {limit} limit ({stats}), but not offered. {notUsed ?? ""} You can still look at it below.";
        UsePaletteBox.Visibility = Visibility.Visible;
        UsePaletteBox.IsEnabled = offered;
        UsePaletteBox.IsChecked = offered && row.UsePalette;
        if (smallest.PaletteCandidate is not null) ViewPanel.Visibility = Visibility.Visible;
        UpdateViewButtons();
    }

    private static string Describe(ImageResult r)
    {
        var lines = new List<string>();
        if (!r.Changed) lines.Add("Nothing to change at this level.");
        else
        {
            var what = r.Applied == ImageLevel.Strip ? "metadata removed" : "recompressed, every pixel identical";
            lines.Add($"{Size(r.OriginalLength)} → {Size(r.Bytes.Length)} ({Percent(r)}), {what}.");
        }
        if (r.Removed.Count > 0) lines.Add("Removes: " + string.Join(", ", r.Removed) + ".");
        lines.AddRange(r.Notes);
        return string.Join(" ", lines);
    }

    private static string Percent(ImageResult r) =>
        r.OriginalLength == 0 ? "" : $"−{100.0 * (r.OriginalLength - r.Bytes.Length) / r.OriginalLength:0} %";

    private async Task ShowImage(Row row, int version, ImageResult? result)
    {
        ThumbButton.Visibility = Visibility.Collapsed;
        PreviewMessage.Visibility = Visibility.Visible;
        PreviewMessage.Text = _files is { IsCompleted: false } ? "Reading the snapshot…" : "";

        BitmapSource? picture = null;
        string? message = null;
        try
        {
            var bytes = await BytesOf(row);
            if (version != _previewVersion) return;
            if (bytes is null) message = "The file couldn't be read from the snapshot.";
            else
            {
                var r = result ?? (row.Level == ImageLevel.Smallest && row.Results.TryGetValue(ImageLevel.Smallest, out var t) && t.IsCompletedSuccessfully ? t.Result : null);
                picture = await Task.Run(() => _view switch
                {
                    View.Palette when r?.PaletteCandidate is { } pal => Decode(pal),
                    View.Difference when r?.PaletteCandidate is { } pal => DifferenceMap(bytes, pal),
                    _ => Decode(bytes)
                });
                if (picture is null) message = "Windows can't draw this file.";
            }
        }
        catch (Exception ex)
        {
            message = "Couldn't draw it: " + ex.Message;
        }
        if (version != _previewVersion) return;

        _thumb = picture;
        ThumbImage.Source = picture;
        if (picture is not null)
        {
            ThumbImage.MaxWidth = picture.PixelWidth;
            ThumbImage.MaxHeight = picture.PixelHeight;
            ThumbButton.Visibility = Visibility.Visible;
            PreviewMessage.Visibility = Visibility.Collapsed;
        }
        else
        {
            PreviewMessage.Text = message ?? "";
        }
    }

    /// <summary>
    /// Where the palette changed the picture: the original in dim grey, each changed pixel in red,
    /// brighter the larger its ΔE (full red at 10 and above).
    /// </summary>
    private static BitmapSource? DifferenceMap(byte[] original, byte[] palette)
    {
        try
        {
            var a = Png.Decode(original);
            var b = Png.Decode(palette);
            var pa = a.ToRgba8();
            var map = ColourDifference.Map(pa, b.ToRgba8());
            var px = new byte[pa.Length];
            for (int p = 0, i = 0; i < pa.Length; p++, i += 4)
            {
                double alpha = pa[i + 3] / 255.0;
                byte grey = (byte)((0.2126 * pa[i] + 0.7152 * pa[i + 1] + 0.0722 * pa[i + 2]) * alpha * 0.35 + 18);
                double heat = Math.Min(1, map[p] / 10.0);
                px[i] = (byte)(grey * (1 - heat));                                  // blue
                px[i + 1] = (byte)(grey * (1 - heat));                              // green
                px[i + 2] = (byte)Math.Min(255, grey * (1 - heat) + 255 * heat);    // red
                px[i + 3] = 255;
            }
            var bmp = BitmapSource.Create(a.Width, a.Height, 96, 96, PixelFormats.Bgra32, null, px, a.Width * 4);
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateViewButtons()
    {
        ViewOriginal.IsChecked = _view == View.Original;
        ViewPalette.IsChecked = _view == View.Palette;
        ViewDifference.IsChecked = _view == View.Difference;
        ViewNote.Text = _view switch
        {
            View.Palette => "The palette version. Click the picture to compare at full size.",
            View.Difference => "Red marks what the palette changed, brighter where the change is larger (full red at ΔE 10).",
            _ => "The original. Click the picture for full size."
        };
    }

    private async void View_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is null || (sender as FrameworkElement)?.Tag is not string tag || !Enum.TryParse<View>(tag, out var view)) return;
        _view = view;
        UpdateViewButtons();
        int version = ++_previewVersion;
        ImageResult? result = _shown.Results.TryGetValue(ImageLevel.Smallest, out var t) && t.IsCompletedSuccessfully ? t.Result : null;
        await ShowImage(_shown, version, result);
    }

    private void UsePalette_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is null) return;
        _shown.UsePalette = UsePaletteBox.IsChecked == true;
        if (_shown.UsePalette && _shown.Usable) _shown.Box.IsChecked = true;
        UpdateNote(_shown);
    }

    private void Thumb_Click(object sender, RoutedEventArgs e)
    {
        if (_thumb is null || _shown is null) return;
        var suffix = _view switch { View.Palette => " (palette)", View.Difference => " (difference)", _ => "" };
        new ImageZoomWindow(_thumb, _shown.Finding.RelPath + suffix) { Owner = this }.Show();
    }

    // ---------------- Confirm ----------------

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_preparing) return;
        var ticked = _rows.Where(r => r.Box.IsChecked == true && r.Usable).ToList();
        _preparing = true;
        SetInputEnabled(false);

        var changes = new Dictionary<string, ImageChange>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        int done = 0;
        foreach (var row in ticked)
        {
            CountText.Text = $"Preparing {++done} of {ticked.Count}: {row.Finding.RelPath}";
            try
            {
                // Smallest without a ticked palette is Lossless; a palette needs the tick.
                var level = row.Level == ImageLevel.Smallest && !row.UsePalette ? ImageLevel.Lossless : row.Level;
                var result = await ResultFor(row, level);
                if (level == ImageLevel.Smallest && !result.PaletteApplied)
                    result = await ResultFor(row, ImageLevel.Lossless);
                if (!result.Changed || result.Bytes.Length >= result.OriginalLength && result.Removed.Count == 0) continue;
                var source = await BytesOf(row) ?? throw new IOException("The file couldn't be read from the snapshot.");
                changes[row.Finding.RelPath] = new ImageChange(result.Bytes, Summary(result), ImageChange.Hash(source));
            }
            catch (Exception ex)
            {
                problems.Add($"{row.Finding.RelPath}: {ex.Message}");
            }
        }

        var remember = RememberBox.IsChecked == true
            ? _rows.Where(r => r.Level != r.Finding.Level).Select(r => new ImageLevelEntry
              {
                  App = _app, Path = r.Finding.RelPath, Level = r.Level.ToString()
              }).ToList()
            : new List<ImageLevelEntry>();

        _preparing = false;

        if (problems.Count > 0 && !ConfirmWindow.Ask(this, "Some files couldn't be prepared",
                "These files are left out. The others can be queued as they are.",
                string.Join("\n", problems), "Queue the others"))
        {
            SetInputEnabled(true);
            UpdateCounts();
            return;
        }

        if (changes.Count == 0 && remember.Count == 0)
        {
            SetInputEnabled(true);
            UpdateCounts();
            CountText.Text = "None of the ticked files changes at its level.";
            return;
        }

        _outcome = new Outcome(changes, remember);
        if (IsLoaded) DialogResult = true;
    }

    /// <summary>Everything that could change the choice, off while Confirm prepares the files.</summary>
    private void SetInputEnabled(bool on)
    {
        ConfirmButton.IsEnabled = on;
        CancelButton.IsEnabled = on;
        FileList.IsEnabled = on;
        TickAllButton.IsEnabled = on;
        UntickAllButton.IsEnabled = on;
        RememberBox.IsEnabled = on;
        PreviewPane.IsEnabled = on;
    }

    private static string Summary(ImageResult r)
    {
        var what = r.PaletteApplied ? $"palette, {r.Palette}" : r.Applied == ImageLevel.Strip ? "metadata removed" : "lossless, every pixel identical";
        var removed = r.Removed.Count > 0 ? "; removed " + string.Join(", ", r.Removed) : "";
        return $"{Size(r.OriginalLength)} → {Size(r.Bytes.Length)} ({what}){removed}";
    }

    private static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";
}
