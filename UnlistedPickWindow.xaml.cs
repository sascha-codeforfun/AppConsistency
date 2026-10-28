using System.Windows;
using System.Windows.Controls;
using AppConsistency.Core;
using HueThemeKit;

namespace AppConsistency;

/// <summary>
/// Shown when the "remove unlisted files" fix is queued: one tick box per file that isn't on the
/// whitelist. Nothing is ticked to start with, so a file only goes when you tick it; the removal
/// is queued only when at least one is ticked. Cancel has the focus and answers Enter and Escape,
/// as ConfirmWindow does.
/// </summary>
public partial class UnlistedPickWindow : Window
{
    private readonly List<(string Path, CheckBox Box)> _boxes = new();

    private UnlistedPickWindow(string appName, IReadOnlyList<UnlistedFile> files)
    {
        InitializeComponent();
        HueTitleBar.Attach(this);

        HeadingText.Text = $"Choose the files to remove from {appName}";
        BodyText.Text =
            "These files aren't on the whitelist and no project uses them in its build. Tick the ones to leave out " +
            "of the fixed copy; anything you don't tick stays. Files the README uses are listed apart: removing " +
            "one leaves the README pointing at nothing. The history folder isn't changed.";
        WhitelistText.Text = "On the whitelist: " + FileHygiene.WhitelistText;

        string? group = null;
        foreach (var file in files)
        {
            var heading = file.ReadmeUses ? "Used by the README" : "Not referenced";
            if (heading != group)
            {
                var title = new TextBlock
                {
                    Text = heading,
                    Margin = new Thickness(0, group is null ? 0 : 14, 0, 6),
                    FontWeight = FontWeights.SemiBold
                };
                title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
                ListPanel.Children.Add(title);
                group = heading;
            }

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock { Text = file.RelPath, TextWrapping = TextWrapping.Wrap };
            if (TryFindResource("Text.Mono") is Style mono) label.Style = mono;
            row.Children.Add(label);

            var note = file.DuplicateOf is { } of ? $"same name as {of}  ·  " : "";
            var size = new TextBlock { Text = note + Size(file.Length), Margin = new Thickness(16, 0, 0, 0) };
            if (TryFindResource("Text.Caption") is Style caption) size.Style = caption;
            Grid.SetColumn(size, 1);
            row.Children.Add(size);

            var box = new CheckBox
            {
                IsChecked = false,
                Margin = new Thickness(4, 0, 0, 4),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = row
            };
            box.Click += (_, _) => UpdateConfirm();
            ListPanel.Children.Add(box);
            _boxes.Add((file.RelPath, box));
        }

        UpdateConfirm();
        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>The ticked files, or null on cancel. Never empty: Queue removal needs at least one tick.</summary>
    public static List<string>? Ask(Window owner, string appName, IReadOnlyList<UnlistedFile> files)
    {
        var window = new UnlistedPickWindow(appName, files) { Owner = owner };
        if (window.ShowDialog() != true) return null;

        var picked = window._boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Path).ToList();
        return picked.Count == 0 ? null : picked;
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
