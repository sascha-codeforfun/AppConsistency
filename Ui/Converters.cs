using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AppConsistency.Core;

namespace AppConsistency.Ui;

/// <summary>Shows the package badge only when the app actually uses packages.</summary>
public sealed class PackageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PackageState state && state != PackageState.None ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Says which of the two package states the badge is showing.</summary>
public sealed class PackageTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PackageState.Foreign
            ? "Uses packages that aren't on the approved list"
            : "Uses packages from the approved list";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
