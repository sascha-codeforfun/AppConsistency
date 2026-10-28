using System.Windows;
using System.Windows.Controls;
using HueThemeKit;

namespace AppConsistency;

/// <summary>
/// Shown before Collect README when some apps have more than one readme: one tick box per readme,
/// all ticked to start with, so you pick what goes into the collection until the repo is cleaned up.
/// Cancel has the focus and answers Enter and Escape, as ConfirmWindow does.
/// </summary>
public partial class ReadmePickWindow : Window
{
    private readonly List<(string AppName, string Path, CheckBox Box)> _boxes = new();

    /// <summary>Apps with a single readme or only a license: they go in regardless, so Collect stays available.</summary>
    private readonly int _singles;

    private ReadmePickWindow(IReadOnlyList<(string AppName, IReadOnlyList<string> Readmes)> apps, int singleCount)
    {
        InitializeComponent();
        HueTitleBar.Attach(this);
        _singles = singleCount;

        BodyText.Text =
            $"{Plural(apps.Count, "app has", "apps have")} more than one readme. Untick the ones to leave out. " +
            singleCount switch
            {
                0 => "",
                1 => "The one other app goes in as it is.",
                _ => $"The {singleCount} other apps go in as they are."
            } + " License files always go in.";

        bool first = true;
        foreach (var (appName, readmes) in apps)
        {
            var heading = new TextBlock
            {
                Text = appName,
                Margin = new Thickness(0, first ? 0 : 14, 0, 6),
                FontWeight = FontWeights.SemiBold
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            ListPanel.Children.Add(heading);
            first = false;

            foreach (var path in readmes)
            {
                var box = new CheckBox
                {
                    IsChecked = true,
                    Margin = new Thickness(4, 0, 0, 4),
                    Content = new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap }
                };
                if (box.Content is TextBlock label && TryFindResource("Text.Mono") is Style mono) label.Style = mono;
                box.Click += (_, _) => UpdateConfirm();
                ListPanel.Children.Add(box);
                _boxes.Add((appName, path, box));
            }
        }

        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>The ticked readmes per app (an app with none ticked is left out), or null on cancel.</summary>
    public static Dictionary<string, List<string>>? Ask(
        Window owner, IReadOnlyList<(string AppName, IReadOnlyList<string> Readmes)> apps, int singleCount)
    {
        var window = new ReadmePickWindow(apps, singleCount) { Owner = owner };
        if (window.ShowDialog() != true) return null;

        return window._boxes
            .Where(b => b.Box.IsChecked == true)
            .GroupBy(b => b.AppName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(b => b.Path).ToList(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>With nothing ticked and no single-readme apps, there'd be nothing to collect.</summary>
    private void UpdateConfirm() =>
        ConfirmButton.IsEnabled = _singles > 0 || _boxes.Any(b => b.Box.IsChecked == true);

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";
}
