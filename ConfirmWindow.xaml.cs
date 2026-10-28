using System.Windows;
using System.Windows.Documents;
using HueThemeKit;

namespace AppConsistency;

/// <summary>
/// A confirmation that shows exactly what an action will do, line by line, and the same window
/// in read-only form for listings. HueConfirm.Ask covers a title and a sentence; these need a
/// monospace preview under it, so they get their own Hue.Window.
/// Cancel has the focus and answers Enter and Escape, as HueConfirm does.
/// </summary>
public partial class ConfirmWindow : Window
{
    private ConfirmWindow(string heading, string body, string details, string confirmText)
    {
        InitializeComponent();
        HueTitleBar.Attach(this);

        HeadingText.Text = heading;
        BodyText.Text = body;
        DetailsText.Text = details;
        ConfirmButton.Content = confirmText;
        Loaded += (_, _) => CancelButton.Focus();
    }

    public static bool Ask(Window owner, string heading, string body, string details, string confirmText) =>
        new ConfirmWindow(heading, body, details, confirmText) { Owner = owner }.ShowDialog() == true;

    /// <summary>Read-only, with each line in its own theme brush (a brush key, or null for the default).</summary>
    public static void ShowLines(Window owner, string heading, string body, IEnumerable<(string Text, string? BrushKey)> lines)
    {
        var window = Reading(owner, heading, body);

        bool first = true;
        foreach (var (text, brushKey) in lines)
        {
            if (!first) window.DetailsText.Inlines.Add(new LineBreak());
            first = false;

            var run = new Run(text);
            if (brushKey is not null) run.SetResourceReference(TextElement.ForegroundProperty, brushKey);
            window.DetailsText.Inlines.Add(run);
        }

        window.ShowDialog();
    }

    /// <summary>Read-only: one Close button, nothing to confirm.</summary>
    public static void Show(Window? owner, string heading, string body, string details)
    {
        var window = Reading(owner, heading, body);
        window.DetailsText.Text = details;
        window.ShowDialog();
    }

    private static ConfirmWindow Reading(Window? owner, string heading, string body)
    {
        var window = new ConfirmWindow(heading, body, "", "Close") { Owner = owner };
        if (owner is null) window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Nothing to cancel: the one button closes the window.
        window.CancelButton.Visibility = Visibility.Collapsed;
        window.ConfirmButton.IsCancel = true;
        window.ConfirmButton.IsDefault = true;
        window.Loaded += (_, _) => window.ConfirmButton.Focus();
        return window;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmButton.IsCancel) DialogResult = true;
    }
}
