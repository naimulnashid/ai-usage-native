using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using UsageApp.Theme;
using UsageCore.View;
using Windows.Foundation;

namespace UsageApp.Charts;

/// <summary>
/// One strip of the activity heat map: 26 week columns, a weekday label
/// column and a month label row, laid out on one set of tracks so the three
/// stay registered. Cells are fluid - the strip fills the panel's width and
/// stays square - and every strip is the same shape, so stacked strips on the
/// full-history page keep the same cell size.
/// </summary>
public sealed class HeatmapStrip : Canvas
{
    private const double DayColumn = 34, Gap = 3, MonthRow = 15, MinCell = 11, Gutter = 7;

    private readonly UsageCore.View.HeatmapStrip _strip;
    private readonly string[] _dayLabels;
    private readonly double _max;
    private double _lastWidth = -1;

    public HeatmapStrip(UsageCore.View.HeatmapStrip strip, string[] dayLabels, double max)
    {
        _strip = strip;
        _dayLabels = dayLabels;
        _max = max;
        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - _lastWidth) < 0.5) return;
            _lastWidth = e.NewSize.Width;
            Draw();
        };
        Height = CellSize(600) * 7 + Gap * 7 + MonthRow + Gutter * 2;
    }

    private static double CellSize(double width) =>
        Math.Max(MinCell, (width - Gutter * 2 - DayColumn - Gap * Heatmap.Weeks) / Heatmap.Weeks);

    private void Draw()
    {
        Children.Clear();
        var width = ActualWidth;
        if (width <= 0) return;
        var cell = CellSize(width);
        Height = Gutter + MonthRow + Gap + 7 * cell + 6 * Gap + Gutter + 3;
        double X(int column) => Gutter + DayColumn + Gap + column * (cell + Gap);
        double Y(int row) => Gutter + MonthRow + Gap + row * (cell + Gap);

        foreach (var (label, column) in _strip.Months)
        {
            var text = Ui.Text(label, 12, 500, Palette.TextFaintBrush);
            SetLeft(text, X(column));
            SetTop(text, Gutter - 2);
            Children.Add(text);
        }
        for (var row = 0; row < 7; row++)
        {
            var text = Ui.Text(_dayLabels[row], 11, 400, Palette.TextFaintBrush);
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            SetLeft(text, Gutter);
            SetTop(text, Y(row) + (cell - text.DesiredSize.Height) / 2);
            Children.Add(text);
        }

        foreach (var c in _strip.Cells)
        {
            if (c.Outside) continue;
            var cost = c.Entry?.Combined.CostUsd ?? 0;
            var rect = new Rectangle
            {
                Width = cell,
                Height = cell,
                RadiusX = 3,
                RadiusY = 3,
                Fill = Palette.Heat[ModelColors.HeatStep(cost, _max)],
                StrokeThickness = 1,
                Stroke = Palette.TransparentBrush,
                CenterPoint = new Vector3((float)cell / 2, (float)cell / 2, 0),
                ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(140) },
            };
            SetLeft(rect, X(c.Column));
            SetTop(rect, Y(c.Row));
            // Kept gentle so the growth stays inside the gutter.
            rect.PointerEntered += (_, _) =>
            {
                rect.Scale = new Vector3(1.14f, 1.14f, 1);
                rect.Stroke = Palette.TextMutedBrush;
                Canvas.SetZIndex(rect, 1);
            };
            rect.PointerExited += (_, _) =>
            {
                rect.Scale = Vector3.One;
                rect.Stroke = Palette.TransparentBrush;
                Canvas.SetZIndex(rect, 0);
            };
            var tip = c.Entry is { } e
                ? $"{Format.DateLong(c.Date)} — {Format.Usd(cost)} · {Format.Tokens(e.Combined.TotalTokens)} tokens · {Format.Duration(e.Combined.RuntimeSeconds)}"
                : $"{Format.DateLong(c.Date)} — no usage";
            ToolTipService.SetToolTip(rect, Ui.TipContent(tip));
            Children.Add(rect);
        }
    }
}

/// <summary>
/// The heat map figure both pages use: one or more strips, then the legend row
/// with the total, an optional action (Expand), and the Less-More scale.
/// </summary>
public static class HeatmapFigure
{
    public static StackPanel Build(HeatmapLayout layout, UsageCore.Model.WeekStart weekStart, string legendSuffix, bool labelStrips, UIElement? action)
    {
        var root = new StackPanel();
        var labels = Heatmap.RowLabels(weekStart);
        for (var i = 0; i < layout.Strips.Count; i++)
        {
            var strip = layout.Strips[i];
            var holder = new StackPanel { Margin = new Thickness(0, i == 0 ? 0 : 14, 0, 0) };
            if (labelStrips)
            {
                var label = Ui.Text(Heatmap.StripLabel(strip), 13, 560, Palette.TextMutedBrush);
                label.Margin = new Thickness(7, 0, 0, 0);
                holder.Children.Add(label);
            }
            holder.Children.Add(new HeatmapStrip(strip, labels, layout.Max));
            root.Children.Add(holder);
        }

        var legend = new Grid { Margin = new Thickness(0, 16, 0, 0), ColumnSpacing = 16 };
        legend.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        legend.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        legend.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var days = layout.ActiveDays;
        var summary = Ui.Text($"{Format.Usd(layout.Total)} across {days} active {(days == 1 ? "day" : "days")} {legendSuffix}", 13.5, 400, Palette.TextFaintBrush);
        summary.VerticalAlignment = VerticalAlignment.Center;
        legend.Children.Add(summary);
        if (action is FrameworkElement a)
        {
            Grid.SetColumn(a, 1);
            legend.Children.Add(a);
        }
        var scale = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        scale.Children.Add(Ui.Text("Less", 13.5, 400, Palette.TextFaintBrush));
        foreach (var brush in Palette.Heat)
        {
            scale.Children.Add(new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(3), Background = brush, VerticalAlignment = VerticalAlignment.Center });
        }
        scale.Children.Add(Ui.Text("More", 13.5, 400, Palette.TextFaintBrush));
        Grid.SetColumn(scale, 2);
        legend.Children.Add(scale);
        root.Children.Add(legend);
        return root;
    }
}
