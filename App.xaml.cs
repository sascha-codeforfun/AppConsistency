using System.Windows;
using HueThemeKit;

namespace AppConsistency;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        HueContrast.Follow(this);

        DispatcherUnhandledException += (_, args) =>
        {
            // A themed window rather than MessageBox, which Windows draws light.
            ConfirmWindow.Show(Application.Current.MainWindow, "Something went wrong",
                "AppConsistency carried on, but the last action didn't finish.", args.Exception.Message);
            args.Handled = true;
        };
    }
}
