using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using UsageApp.Theme;
using UsageCore.Model;
using UsageCore.View;
using Windows.Foundation;

namespace UsageApp.Charts;

/// <summary>Estimated spend per model as horizontal bars, dearest first.</summary>
public sealed class CostByModelChart : ChartSurface
{
    private const double Top = 4, Right = 26, Bottom = 4, Left = 4, CategoryWidth = 116, XAxisHeight = 30, BarSize = 30;

    private sealed record Row(string Model, string Label, double Cost, long Tokens, double Runtime, bool Unpriced, double Share);

    private readonly List<Row> _rows;
    private readonly List<(double Y, double H)> _bands = [];
    private Rectangle? _wash;
    private double _plotLeft, _plotRight;

    public CostByModelChart(IReadOnlyDictionary<string, UsageCell> perModel, double height = 260, bool animate = true)
        : base(height, animate)
    {
        // Shares are of the spend this chart draws, so they add to 100 on
        // screen. An unpriced model contributes no cost and gets no share.
        var total = perModel.Values.Where(c => !c.Unpriced).Sum(c => c.CostUsd);
        _rows = perModel
            .Select(kv => new Row(kv.Key, Format.Model(kv.Key), kv.Value.CostUsd, kv.Value.TotalTokens, kv.Value.RuntimeSeconds, kv.Value.Unpriced, total > 0 ? kv.Value.CostUsd / total * 100 : 0))
            .Where(r => r.Cost > 0 || r.Tokens > 0)
            .OrderByDescending(r => r.Cost)
            .ToList();
    }

    public bool IsEmpty => _rows.Count == 0;

    protected override void Draw(bool animate)
    {
        _bands.Clear();
        // At least the original's 116px, and wider when a label needs it:
        // Codex's "auto-review (5.3 Codex)" ran off the left edge at 116.
        // Capped at a third of the chart, so the bars keep most of it.
        var widest = _rows.Count == 0 ? 0 : _rows.Max(r => ChartKit.MeasureText(r.Label, 14));
        _plotLeft = Left + Math.Min(Math.Max(CategoryWidth, widest + 12), W / 3);
        _plotRight = W - Right;
        var plotTop = Top;
        var plotBottom = H - Bottom - XAxisHeight;
        var plotW = Math.Max(1, _plotRight - _plotLeft);
        var plotH = Math.Max(1, plotBottom - plotTop);

        var ticks = ChartKit.NiceTicks(_rows.Count == 0 ? 0 : _rows.Max(r => r.Cost));
        var top = ticks[^1];
        double X(double v) => _plotLeft + (top > 0 ? v / top : 0) * plotW;

        foreach (var tick in ticks)
        {
            Canvas.Children.Add(ChartKit.VLine(X(tick), plotTop, plotBottom, Palette.BorderBrush));
            ChartKit.Label(Canvas, Format.Usd(tick, compact: true), X(tick), plotBottom + 6 + 11, 0);
        }
        Canvas.Children.Add(ChartKit.HLine(_plotLeft, _plotRight, plotBottom, Palette.BorderBrush));

        _wash = new Rectangle { Fill = Palette.HoverWashBrush, Visibility = Visibility.Collapsed, Width = plotW };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_wash, _plotLeft);
        Canvas.Children.Add(_wash);

        var band = plotH / Math.Max(1, _rows.Count);
        var bars = new Microsoft.UI.Xaml.Controls.Canvas();
        for (var i = 0; i < _rows.Count; i++)
        {
            var y = plotTop + i * band;
            _bands.Add((y, band));
            var centre = y + band / 2;
            ChartKit.Label(Canvas, _rows[i].Label, _plotLeft - 8, centre, 1, 14, Palette.TextMutedBrush);

            var width = Math.Max(0, X(_rows[i].Cost) - _plotLeft);
            if (width <= 0) continue;
            var bar = new Microsoft.UI.Xaml.Shapes.Path
            {
                Fill = Palette.ModelBrush(_rows[i].Model),
                Data = RoundedRight(new Rect(_plotLeft, centre - BarSize / 2, width, BarSize), Math.Min(6, width / 2)),
            };
            bars.Children.Add(bar);
        }
        Canvas.Children.Add(bars);

        if (animate)
        {
            var scale = new ScaleTransform { ScaleX = 0, CenterX = _plotLeft };
            bars.RenderTransform = scale;
            var story = new Storyboard();
            var grow = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(850)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(grow, scale);
            Storyboard.SetTargetProperty(grow, "ScaleX");
            story.Children.Add(grow);
            story.Begin();
        }
    }

    /// <summary>A bar with its right-hand corners rounded, as the original drew them.</summary>
    private static Geometry RoundedRight(Rect r, double radius)
    {
        var figure = new PathFigure { StartPoint = new Point(r.Left, r.Top), IsClosed = true };
        figure.Segments.Add(new LineSegment { Point = new Point(r.Right - radius, r.Top) });
        figure.Segments.Add(new ArcSegment { Point = new Point(r.Right, r.Top + radius), Size = new Size(radius, radius), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = new Point(r.Right, r.Bottom - radius) });
        figure.Segments.Add(new ArcSegment { Point = new Point(r.Right - radius, r.Bottom), Size = new Size(radius, radius), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = new Point(r.Left, r.Bottom) });
        return new PathGeometry { Figures = { figure } };
    }

    protected override void OnHover(Point at)
    {
        if (_wash is null) return;
        var index = _bands.FindIndex(b => at.Y >= b.Y && at.Y < b.Y + b.H);
        if (index < 0 || at.X < _plotLeft || at.X > _plotRight)
        {
            ClearHover();
            return;
        }
        _wash.Height = _bands[index].H;
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_wash, _bands[index].Y);
        _wash.Visibility = Visibility.Visible;

        var row = _rows[index];
        var body = ChartTooltip.Stack();
        var head = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 9, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(Ui.Swatch(Palette.ModelColor(row.Model)));
        head.Children.Add(Ui.Text(row.Label, 14, 600));
        body.Children.Add(head);
        body.Children.Add(ChartTooltip.Big(row.Unpriced ? "unpriced" : Format.Usd(row.Cost), Palette.ModelBrush(row.Model)));
        if (!row.Unpriced) body.Children.Add(ChartTooltip.Muted($"{row.Share:0.0}% of total cost", 4, 13.5));
        body.Children.Add(ChartTooltip.Muted($"{Format.Tokens(row.Tokens)} tokens · {Format.Duration(row.Runtime)}"));
        Tooltip.Show(body, at);
    }

    protected override void ClearHover()
    {
        base.ClearHover();
        if (_wash is not null) _wash.Visibility = Visibility.Collapsed;
    }
}
