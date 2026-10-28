using System.Windows;
using System.Windows.Controls;

namespace AppConsistency.Ui;

/// <summary>
/// The header row: first child on the left, last child on the right, and the middle child dead
/// centre on the window. When the window gets too narrow for true centre, the middle child slides
/// toward the free side; when there's no room at all it isn't shown rather than overlapping.
/// </summary>
public sealed class HeaderPanel : Panel
{
    private const double Gap = 24;

    protected override Size MeasureOverride(Size available)
    {
        var free = new Size(double.PositiveInfinity, available.Height);
        double height = 0, width = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(free);
            height = Math.Max(height, child.DesiredSize.Height);
            width += child.DesiredSize.Width;
        }
        return new Size(double.IsInfinity(available.Width) ? width : available.Width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = InternalChildren;
        if (children.Count == 0) return final;

        var left = children[0];
        var right = children.Count > 1 ? children[^1] : null;
        var middle = children.Count > 2 ? children[1] : null;

        double leftWidth = Math.Min(left.DesiredSize.Width, final.Width);
        left.Arrange(Centered(0, leftWidth, left.DesiredSize.Height, final.Height));

        double rightWidth = 0;
        if (right is not null)
        {
            rightWidth = right.DesiredSize.Width;
            right.Arrange(Centered(final.Width - rightWidth, rightWidth, right.DesiredSize.Height, final.Height));
        }

        if (middle is not null)
        {
            double width = middle.DesiredSize.Width;
            double min = leftWidth + Gap;
            double max = final.Width - rightWidth - Gap - width;

            if (width > 0 && max >= min)
            {
                double x = Math.Clamp((final.Width - width) / 2, min, max);
                middle.Arrange(Centered(x, width, middle.DesiredSize.Height, final.Height));
            }
            else
            {
                middle.Arrange(new Rect(0, 0, 0, 0));
            }
        }

        return final;
    }

    private static Rect Centered(double x, double width, double height, double rowHeight) =>
        new(x, Math.Max(0, (rowHeight - height) / 2), width, height);
}
