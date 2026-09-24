using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using UsageApp.Theme;
using UsageCore.Model;
using UsageCore.View;
using Windows.Foundation;

namespace UsageApp.Charts;

/// <summary>
/// Daily combined spend: a smooth area over every recorded day, with the days
/// that sit well above trend marked in the warning colour.
/// </summary>
public sealed class SpendAreaChart : ChartSurface
{
    private const double Top = 10, Right = 12, Bottom = 4, Left = 4, YAxisWidth = 62, XAxisHeight = 30;

    private readonly List<DailyEntry> _days;
    private readonly bool[] _spikes;
    private readonly List<Point> _points = [];
    private Line? _cursor;
    private Ellipse? _activeDot;

    public SpendAreaChart(IReadOnlyList<DailyEntry> daily, double height = 300, bool animate = true)
        : base(height, animate)
    {
        _days = daily.ToList();
        _spikes = MarkSpikes(_days.Select(d => d.Combined.CostUsd).ToArray());
    }

    /// <summary>
    /// Days whose spend sits well above the run of the series, so the warning
    /// colour marks a real anomaly rather than decorating the chart.
    /// </summary>
    public static bool[] MarkSpikes(double[] values)
    {
        if (values.Length < 4) return new bool[values.Length];
        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Length;
        var threshold = mean + 1.6 * Math.Sqrt(variance);
        return values.Select(v => v > threshold && v > mean * 1.5).ToArray();
    }

    protected override void Draw(bool animate)
    {
        _points.Clear();
        var plotLeft = Left + YAxisWidth;
        var plotRight = W - Right;
        var plotTop = Top;
        var plotBottom = H - Bottom - XAxisHeight;
        var plotW = Math.Max(1, plotRight - plotLeft);
        var plotH = Math.Max(1, plotBottom - plotTop);

        var ticks = ChartKit.NiceTicks(_days.Count == 0 ? 0 : _days.Max(d => d.Combined.CostUsd));
        var top = ticks[^1];
        double Y(double v) => plotBottom - (top > 0 ? v / top : 0) * plotH;

        foreach (var tick in ticks)
        {
            Canvas.Children.Add(ChartKit.HLine(plotLeft, plotRight, Y(tick), Palette.BorderBrush));
            ChartKit.Label(Canvas, Format.Usd(tick, compact: true), plotLeft - 8, Y(tick), 1);
        }

        var n = _days.Count;
        for (var i = 0; i < n; i++)
        {
            var x = n == 1 ? plotLeft + plotW / 2 : plotLeft + i * plotW / (n - 1);
            _points.Add(new Point(x, Y(_days[i].Combined.CostUsd)));
        }

        // X labels, thinned so none overlap, the last one always kept.
        var labels = _days.Select(d => Format.DateShort(d.Date)).ToList();
        var widths = labels.Select(l => ChartKit.MeasureText(l)).ToList();
        var keep = ChartKit.ThinLabels(_points.Select(p => p.X).ToList(), widths, 22, 0, W);
        for (var i = 0; i < n; i++)
        {
            if (keep.Contains(i)) ChartKit.Label(Canvas, labels[i], ChartKit.ClampCentre(_points[i].X, widths[i], 0, W), plotBottom + 6 + 11, 0);
        }

        if (n > 0)
        {
            // The fill: the curve, then down to the baseline and back.
            var fillFigure = ChartKit.MonotoneFigure(_points);
            fillFigure.Segments.Add(new LineSegment { Point = new Point(_points[^1].X, plotBottom) });
            fillFigure.Segments.Add(new LineSegment { Point = new Point(_points[0].X, plotBottom) });
            fillFigure.IsClosed = true;
            var fill = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = new PathGeometry { Figures = { fillFigure } },
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = WithAlpha(Palette.AccentColor, 0.42), Offset = 0 },
                        new GradientStop { Color = WithAlpha(Palette.AccentColor, 0.02), Offset = 1 },
                    },
                },
            };
            var stroke = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = new PathGeometry { Figures = { ChartKit.MonotoneFigure(_points) } },
                Stroke = Palette.Accent,
                StrokeThickness = 2.4,
                StrokeLineJoin = PenLineJoin.Round,
            };
            Canvas.Children.Add(fill);
            Canvas.Children.Add(stroke);
            Canvas.Children.Add(ChartKit.HLine(plotLeft, plotRight, plotBottom, Palette.BorderBrush));

            for (var i = 0; i < n; i++)
            {
                if (!_spikes[i]) continue;
                Canvas.Children.Add(Dot(_points[i], 4.5, Palette.WarnBrush));
            }

            if (animate) Reveal(fill, stroke, plotLeft, plotW);
        }

        _cursor = new Line { Stroke = Palette.Accent, StrokeThickness = 1, StrokeDashArray = [4, 4], Visibility = Visibility.Collapsed, Y1 = plotTop, Y2 = plotBottom };
        _activeDot = Dot(new Point(0, 0), 5, Palette.Accent);
        _activeDot.Visibility = Visibility.Collapsed;
        Canvas.Children.Add(_cursor);
        Canvas.Children.Add(_activeDot);
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color c, double a) =>
        Windows.UI.Color.FromArgb((byte)Math.Round(a * 255), c.R, c.G, c.B);

    private static Ellipse Dot(Point at, double r, Brush fill)
    {
        var dot = new Ellipse { Width = r * 2, Height = r * 2, Fill = fill, Stroke = Palette.BgBrush, StrokeThickness = 2 };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(dot, at.X - r);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(dot, at.Y - r);
        return dot;
    }

    /// <summary>Draws the line in from the left, the way the original grew in.</summary>
    private static void Reveal(UIElement fill, UIElement stroke, double left, double width)
    {
        var full = left + width + 20;
        foreach (var element in new[] { fill, stroke })
        {
            // A clip that widens from the plot's left edge to its right.
            var transform = new ScaleTransform { ScaleX = left / full, CenterX = 0 };
            element.Clip = new RectangleGeometry { Rect = new Rect(0, -10, full, 10_000), Transform = transform };
            var story = new Storyboard();
            var grow = new DoubleAnimation
            {
                From = left / full,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(900)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(grow, transform);
            Storyboard.SetTargetProperty(grow, "ScaleX");
            story.Children.Add(grow);
            story.Begin();
        }
    }

    protected override void OnHover(Point at)
    {
        if (_points.Count == 0 || _cursor is null || _activeDot is null) return;
        var index = 0;
        var best = double.MaxValue;
        for (var i = 0; i < _points.Count; i++)
        {
            var d = Math.Abs(_points[i].X - at.X);
            if (d < best)
            {
                best = d;
                index = i;
            }
        }
        var p = _points[index];
        _cursor.X1 = _cursor.X2 = p.X;
        _cursor.Visibility = Visibility.Visible;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_activeDot, p.X - 5);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_activeDot, p.Y - 5);
        _activeDot.Visibility = Visibility.Visible;

        var day = _days[index];
        var body = ChartTooltip.Stack();
        body.Children.Add(ChartTooltip.Title(Format.DateLong(day.Date)));
        body.Children.Add(ChartTooltip.Big(Format.Usd(day.Combined.CostUsd), Palette.Accent));
        body.Children.Add(ChartTooltip.Muted($"{Format.Tokens(day.Combined.TotalTokens)} tokens · {Format.Duration(day.Combined.RuntimeSeconds)}"));
        if (_spikes[index])
        {
            var warn = Ui.Text("Well above trend", 13, 600, Palette.WarnBrush);
            warn.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(warn);
        }
        Tooltip.SetWarn(_spikes[index]);
        Tooltip.Show(body, at);
    }

    protected override void ClearHover()
    {
        base.ClearHover();
        if (_cursor is not null) _cursor.Visibility = Visibility.Collapsed;
        if (_activeDot is not null) _activeDot.Visibility = Visibility.Collapsed;
    }
}
