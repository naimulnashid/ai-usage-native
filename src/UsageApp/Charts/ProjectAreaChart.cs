using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using UsageApp.Theme;
using UsageCore.View;
using Windows.Foundation;
using Windows.UI;

namespace UsageApp.Charts;

/// <summary>One band of the project chart: a project, or the summed remainder.</summary>
public sealed record ProjectSeries(string? Id, string Name, Color Color, double[] Values, int Projects);

/// <summary>
/// Daily spend stacked by project, as smooth areas - the largest project at the
/// bottom. Drawn with d3's monotone curve, like the combined chart, so the bands
/// are smooth between dates yet never overshoot: a run of idle days stays flat.
/// </summary>
/// <remarks>
/// Painted back to front: each series fills from its own cumulative top down
/// to the baseline, topmost series first, so every band shows exactly between
/// its top and the next one's. That leaves no hairline seams between bands,
/// which drawing each band as its own closed ring would. The fills are mixed
/// toward the panel rather than made translucent, since translucent layers
/// painted over one another would darken wherever they overlap.
/// </remarks>
public sealed class ProjectAreaChart : ChartSurface
{
    private const double Top = 10, Right = 12, Bottom = 4, Left = 4, YAxisWidth = 64, XAxisHeight = 30;

    private readonly List<string> _dates;
    private readonly List<ProjectSeries> _series;
    private readonly List<double> _xs = [];
    private Line? _cursor;
    private double _plotTop, _plotBottom;

    public ProjectAreaChart(IReadOnlyList<string> dates, IReadOnlyList<ProjectSeries> series, double height = 300, bool animate = true)
        : base(height, animate)
    {
        _dates = dates.ToList();
        _series = series.ToList();
    }

    protected override void Draw(bool animate)
    {
        _xs.Clear();
        var plotLeft = Left + YAxisWidth;
        var plotRight = W - Right;
        _plotTop = Top;
        _plotBottom = H - Bottom - XAxisHeight;
        var plotW = Math.Max(1, plotRight - plotLeft);
        var plotH = Math.Max(1, _plotBottom - _plotTop);
        var n = _dates.Count;

        var totals = Enumerable.Range(0, n).Select(i => _series.Sum(s => s.Values[i])).ToArray();
        var ticks = ChartKit.NiceTicks(n == 0 ? 0 : totals.Max());
        var top = ticks[^1];
        double Y(double v) => _plotBottom - (top > 0 ? v / top : 0) * plotH;

        foreach (var tick in ticks)
        {
            Canvas.Children.Add(ChartKit.HLine(plotLeft, plotRight, Y(tick), Palette.BorderBrush));
            ChartKit.Label(Canvas, Format.Usd(tick, compact: true), plotLeft - 8, Y(tick), 1);
        }
        for (var i = 0; i < n; i++) _xs.Add(n == 1 ? plotLeft + plotW / 2 : plotLeft + i * plotW / (n - 1));

        var layers = new Microsoft.UI.Xaml.Controls.Canvas();
        if (n > 0)
        {
            // Cumulative tops, bottom series first.
            var cumulative = new double[_series.Count][];
            var running = new double[n];
            for (var s = 0; s < _series.Count; s++)
            {
                for (var i = 0; i < n; i++) running[i] += _series[s].Values[i];
                cumulative[s] = (double[])running.Clone();
            }
            for (var s = _series.Count - 1; s >= 0; s--)
            {
                var points = Enumerable.Range(0, n).Select(i => new Point(_xs[i], Y(cumulative[s][i]))).ToList();
                var fillFigure = ChartKit.MonotoneFigure(points);
                fillFigure.Segments.Add(new LineSegment { Point = new Point(points[^1].X, _plotBottom) });
                fillFigure.Segments.Add(new LineSegment { Point = new Point(points[0].X, _plotBottom) });
                fillFigure.IsClosed = true;
                layers.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = new PathGeometry { Figures = { fillFigure } },
                    Fill = new SolidColorBrush(Palette.Mix(Palette.Surface, _series[s].Color, 0.78)),
                });
                layers.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = new PathGeometry { Figures = { ChartKit.MonotoneFigure(points) } },
                    Stroke = new SolidColorBrush(_series[s].Color),
                    StrokeThickness = 1.2,
                    StrokeLineJoin = PenLineJoin.Round,
                });
            }
        }
        Canvas.Children.Add(layers);
        Canvas.Children.Add(ChartKit.HLine(plotLeft, plotRight, _plotBottom, Palette.BorderBrush));

        var labels = _dates.Select(Format.DateShort).ToList();
        var widths = labels.Select(l => ChartKit.MeasureText(l)).ToList();
        var keep = ChartKit.ThinLabels(_xs, widths, 20, 0, W);
        for (var i = 0; i < n; i++)
        {
            if (keep.Contains(i)) ChartKit.Label(Canvas, labels[i], ChartKit.ClampCentre(_xs[i], widths[i], 0, W), _plotBottom + 6 + 11, 0);
        }

        _cursor = new Line { Stroke = Palette.Accent, StrokeThickness = 1, StrokeDashArray = [4, 4], Visibility = Visibility.Collapsed, Y1 = _plotTop, Y2 = _plotBottom };
        Canvas.Children.Add(_cursor);

        if (animate) Grow(layers, _plotBottom);
    }

    /// <summary>The areas rise from the baseline, as the columns elsewhere do.</summary>
    private static void Grow(UIElement layers, double baseline)
    {
        var scale = new ScaleTransform { ScaleY = 0, CenterY = baseline };
        layers.RenderTransform = scale;
        var story = new Storyboard();
        var grow = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(750)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(grow, scale);
        Storyboard.SetTargetProperty(grow, "ScaleY");
        story.Children.Add(grow);
        story.Begin();
    }

    protected override void OnHover(Point at)
    {
        if (_xs.Count == 0 || _cursor is null) return;
        if (at.Y < _plotTop || at.Y > _plotBottom)
        {
            ClearHover();
            return;
        }
        var index = 0;
        for (var i = 1; i < _xs.Count; i++)
        {
            if (Math.Abs(_xs[i] - at.X) < Math.Abs(_xs[index] - at.X)) index = i;
        }
        _cursor.X1 = _cursor.X2 = _xs[index];
        _cursor.Visibility = Visibility.Visible;

        // Largest first, and no zero rows: nine projects at "$0.00" bury the
        // one that mattered that day.
        var entries = _series
            .Select(s => (Series: s, Value: s.Values[index]))
            .Where(e => e.Value > 0)
            .OrderByDescending(e => e.Value)
            .ToList();
        var body = ChartTooltip.Stack();
        body.MinWidth = 230;
        var title = ChartTooltip.Title(Format.DateLong(_dates[index]));
        title.Margin = new Thickness(0, 0, 0, 9);
        body.Children.Add(title);
        foreach (var (series, value) in entries) body.Children.Add(ChartTooltip.Row(series.Color, series.Name, Format.Usd(value)));
        body.Children.Add(entries.Count == 0
            ? ChartTooltip.TotalRow("No activity", null)
            : ChartTooltip.TotalRow("Total", Format.Usd(entries.Sum(e => e.Value))));
        Tooltip.Show(body, at);
    }

    protected override void ClearHover()
    {
        base.ClearHover();
        if (_cursor is not null) _cursor.Visibility = Visibility.Collapsed;
    }
}
