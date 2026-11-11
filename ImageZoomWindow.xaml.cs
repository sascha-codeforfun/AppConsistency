using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using HueThemeKit;

namespace AppConsistency;

/// <summary>
/// A picture at full size, with a zoom slider and scrollbars, opened from the thumbnail in the
/// removal picker. The thumbnail is sized to the pane, which is too small to read a complex
/// diagram or a screenshot; this window opens fitted to its size and goes to 100 % on 1.
/// Same controls as FileMaintenance's zoom window.
/// </summary>
public partial class ImageZoomWindow : Window
{
    public ImageZoomWindow(BitmapSource source, string name)
    {
        InitializeComponent();
        HueTitleBar.Attach(this);

        Title = $"{name} — AppConsistency";
        NameText.Text = name;
        NameText.ToolTip = name;
        Picture.Source = source;
        // Sized by pixels, not by the DPI written in the file: 100 % is one image pixel per screen
        // unit, whether the file says 72 or 300 dpi.
        Picture.Width = source.PixelWidth;
        Picture.Height = source.PixelHeight;
        ZoomLabel.Text = "100 %";

        var work = SystemParameters.WorkArea;
        Width = Math.Min(Width, work.Width - 40);
        Height = Math.Min(Height, work.Height - 40);

        Loaded += (_, _) => FitToWindow();
    }

    /// <summary>Fits the picture into the window, never enlarging a small one past 100 %.</summary>
    private void FitToWindow()
    {
        if (Picture.Source is null || Picture.Width <= 0 || Picture.Height <= 0) return;
        double fit = Math.Min(
            (Scroller.ViewportWidth - 4) / Picture.Width,
            (Scroller.ViewportHeight - 4) / Picture.Height);
        if (fit > 0 && !double.IsInfinity(fit) && !double.IsNaN(fit))
            ZoomSlider.Value = Math.Clamp(Math.Min(fit, 1), ZoomSlider.Minimum, ZoomSlider.Maximum);
        else
            ZoomLabel.Text = $"{ZoomSlider.Value * 100:0} %";
    }

    private void OnZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires while the markup is still loading, before Scale and ZoomLabel exist.
        if (Scale is null || ZoomLabel is null) return;
        Scale.ScaleX = Scale.ScaleY = e.NewValue;
        ZoomLabel.Text = $"{e.NewValue * 100:0} %";
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value * factor, ZoomSlider.Minimum, ZoomSlider.Maximum);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                ZoomSlider.Value = Math.Min(ZoomSlider.Value * 1.15, ZoomSlider.Maximum); e.Handled = true; break;
            case Key.OemMinus or Key.Subtract:
                ZoomSlider.Value = Math.Max(ZoomSlider.Value / 1.15, ZoomSlider.Minimum); e.Handled = true; break;
            case Key.D0 or Key.NumPad0:
                FitToWindow(); e.Handled = true; break;
            case Key.D1 or Key.NumPad1:
                ZoomSlider.Value = 1; e.Handled = true; break;
            case Key.Escape:
                Close(); e.Handled = true; break;
        }
    }
}
