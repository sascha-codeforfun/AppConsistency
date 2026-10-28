using System.Windows;
using HueThemeKit;

namespace AppConsistency;

/// <summary>What Stage to Downloads should write when the snapshot has private files.</summary>
public enum StageChoice
{
    Cancel,
    /// <summary>App.zip with everything, for your own use: it goes through RepoBuilder.</summary>
    WithPrivate,
    /// <summary>App-PUBLISH.zip without the private files, for RepoPublisher.</summary>
    ForPublishing
}

/// <summary>
/// Asked before staging a snapshot that holds private files: keep them (App.zip) or leave them out
/// for publishing (App-PUBLISH.zip). Cancel has the focus and answers Enter and Escape, as
/// ConfirmWindow does.
/// </summary>
public partial class StageChoiceWindow : Window
{
    private StageChoice _choice = StageChoice.Cancel;

    private StageChoiceWindow(string appName, IReadOnlyList<string> privateFiles, string withName, string publishName)
    {
        InitializeComponent();
        HueTitleBar.Attach(this);

        HeadingText.Text = $"Stage {appName} with or without your private files?";
        BodyText.Text =
            $"With private files writes {withName}, everything as it is. " +
            $"Without writes {publishName} for publishing: these files are left out wherever they are. " +
            "Either way every file is dated the release day at 00:00:00, and the history folder isn't changed.";
        DetailsText.Text = string.Join("\n", privateFiles);
        Loaded += (_, _) => CancelButton.Focus();
    }

    public static StageChoice Ask(Window owner, string appName, IReadOnlyList<string> privateFiles, string withName, string publishName)
    {
        var window = new StageChoiceWindow(appName, privateFiles, withName, publishName) { Owner = owner };
        return window.ShowDialog() == true ? window._choice : StageChoice.Cancel;
    }

    private void With_Click(object sender, RoutedEventArgs e)
    {
        _choice = StageChoice.WithPrivate;
        DialogResult = true;
    }

    private void Publish_Click(object sender, RoutedEventArgs e)
    {
        _choice = StageChoice.ForPublishing;
        DialogResult = true;
    }
}
