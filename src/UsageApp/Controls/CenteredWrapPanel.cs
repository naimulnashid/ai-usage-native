using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace UsageApp.Controls;

/// <summary>
/// Lays its children out in rows that wrap, each row centred - a chip legend.
/// WinUI has no WrapPanel of its own.
/// </summary>
public sealed class CenteredWrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 10;
    public double VerticalSpacing { get; set; } = 8;

    private List<(int From, int To, double Width, double Height)> Rows(double available)
    {
        var rows = new List<(int, int, double, double)>();
        int start = 0;
        double width = 0, height = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            var size = Children[i].DesiredSize;
            var needed = i == start ? size.Width : width + HorizontalSpacing + size.Width;
            if (i > start && needed > available)
            {
                rows.Add((start, i, width, height));
                start = i;
                width = size.Width;
                height = size.Height;
                continue;
            }
            width = needed;
            height = Math.Max(height, size.Height);
        }
        if (start < Children.Count) rows.Add((start, Children.Count, width, height));
        return rows;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var rows = Rows(availableSize.Width);
        var height = rows.Sum(r => r.Height) + Math.Max(0, rows.Count - 1) * VerticalSpacing;
        var width = rows.Count == 0 ? 0 : rows.Max(r => r.Width);
        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0.0;
        foreach (var (from, to, width, height) in Rows(finalSize.Width))
        {
            var x = Math.Max(0, (finalSize.Width - width) / 2);
            for (var i = from; i < to; i++)
            {
                var size = Children[i].DesiredSize;
                Children[i].Arrange(new Rect(x, y + (height - size.Height) / 2, Math.Min(size.Width, finalSize.Width), size.Height));
                x += size.Width + HorizontalSpacing;
            }
            y += height + VerticalSpacing;
        }
        return finalSize;
    }
}
