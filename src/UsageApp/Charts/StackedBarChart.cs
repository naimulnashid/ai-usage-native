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
/// Daily columns stacked by model - tokens or spend.
/// </summary>
/// <remarks>
/// Bands are ordered most-expensive-first from the bottom, so the darkest
/// shades sit at the base of each column: the dearer share of a day is
/// readable at a glance. The shade is the price encoding (see ModelColors),
/// so colour alone is never how a band is read - the tooltip names each one.
/// </remarks>
public sealed class StackedBarChart : ChartSurface
{
    private const double Top = 10, Right = 12, Bottom = 4, Left = 4, YAxisWidth = 64, XAxisHeight = 30;

    private readonly List<DailyEntry> _days;
    private readonly List<string> _models;
    private readonly Func<UsageCell, double> _value;
    private readonly Func<double, string> _axisFormat;
    private readonly Func<double, string> _valueFormat;
    private readonly List<(double X, double Width)> _bands = [];
    private Rectangle? _wash;
    private double _plotTop, _plotBottom;

    public StackedBarChart(
        IReadOnlyList<DailyEntry> days,
        IReadOnlyList<string> modelsByPriceDesc,
        Func<UsageCell, double> value,
        Func<double, string> axisFormat,
        Func<double, string> valueFormat,
        double height = 300,
        bool animate = true)
        : base(height, animate)
    {
        _days = days.ToList();
        _models = modelsByPriceDesc.ToList();
        _value = value;
        _axisFormat = axisFormat;
        _valueFormat = valueFormat;
    }

    private double ValueOf(DailyEntry day, string model) =>
        day.PerModel.TryGetValue(model, out var cell) ? _value(cell) : 0;

    protected override void Draw(bool animate)
    {
        _bands.Clear();
        var plotLeft = Left + YAxisWidth;
        var plotRight = W - Right;
        _plotTop = Top;
        _plotBottom = H - Bottom - XAxisHeight;
        var plotW = Math.Max(1, plotRight - plotLeft);
        var plotH = Math.Max(1, _plotBottom - _plotTop);

        var maxTotal = _days.Count == 0 ? 0 : _days.Max(d => _models.Sum(m => ValueOf(d, m)));
        var ticks = ChartKit.NiceTicks(maxTotal);
        var top = ticks[^1];
        double Y(double v) => _plotBottom - (top > 0 ? v / top : 0) * plotH;

        foreach (var tick in ticks)
        {
            Canvas.Children.Add(ChartKit.HLine(plotLeft, plotRight, Y(tick), Palette.BorderBrush));
            ChartKit.Label(Canvas, _axisFormat(tick), plotLeft - 8, Y(tick), 1);
        }

        // The hover wash sits behind the bars.
        _wash = new Rectangle { Fill = Palette.HoverWashBrush, Visibility = Visibility.Collapsed, Height = plotH };
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_wash, _plotTop);
        Canvas.Children.Add(_wash);

        var n = _days.Count;
        var band = plotW / Math.Max(1, n);
        var gap = band * 0.1; // Recharts' default 10% category gap each side
        var barW = Math.Max(1, band - 2 * gap);

        var bars = new Microsoft.UI.Xaml.Controls.Canvas();
        for (var i = 0; i < n; i++)
        {
            var x = plotLeft + i * band;
            _bands.Add((x, band));
            double stacked = 0;
            foreach (var model in _models)
            {
                var v = ValueOf(_days[i], model);
                if (v <= 0) continue;
                var y0 = Y(stacked);
                var y1 = Y(stacked + v);
                stacked += v;
                var rect = new Rectangle
                {
                    Width = barW,
                    Height = Math.Max(0, y0 - y1),
                    Fill = Palette.ModelBrush(model),
                };
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(rect, x + gap);
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(rect, y1);
                bars.Children.Add(rect);
            }
        }
        Canvas.Children.Add(bars);
        Canvas.Children.Add(ChartKit.HLine(plotLeft, plotRight, _plotBottom, Palette.BorderBrush));

        var labels = _days.Select(d => Format.DateShort(d.Date)).ToList();
        var centres = Enumerable.Range(0, n).Select(i => plotLeft + (i + 0.5) * band).ToList();
        var labelWidths = labels.Select(l => ChartKit.MeasureText(l)).ToList();
        var keep = ChartKit.ThinLabels(centres, labelWidths, 20, 0, W);
        for (var i = 0; i < n; i++)
        {
            if (keep.Contains(i)) ChartKit.Label(Canvas, labels[i], ChartKit.ClampCentre(centres[i], labelWidths[i], 0, W), _plotBottom + 6 + 11, 0);
        }

        if (animate) Grow(bars, _plotBottom);
    }

    /// <summary>The columns rise from the baseline.</summary>
    private static void Grow(UIElement bars, double baseline)
    {
        var scale = new ScaleTransform { ScaleY = 0, CenterY = baseline };
        bars.RenderTransform = scale;
        var story = new Storyboard();
        var grow = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(850)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(grow, scale);
        Storyboard.SetTargetProperty(grow, "ScaleY");
        story.Children.Add(grow);
        story.Begin();
    }

    protected override void OnHover(Point at)
    {
        if (_bands.Count == 0 || _wash is null) return;
        var index = _bands.FindIndex(b => at.X >= b.X && at.X < b.X + b.Width);
        if (index < 0 || at.Y < _plotTop || at.Y > _plotBottom)
        {
            ClearHover();
            return;
        }
        _wash.Width = _bands[index].Width;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_wash, _bands[index].X);
        _wash.Visibility = Visibility.Visible;

        var day = _days[index];
        var entries = _models
            .Select(m => (Model: m, Value: ValueOf(day, m)))
            .Where(e => e.Value > 0)
            .OrderByDescending(e => e.Value)
            .ToList();
        var body = ChartTooltip.Stack();
        body.MinWidth = 220;
        var title = ChartTooltip.Title(Format.DateLong(day.Date));
        title.Margin = new Thickness(0, 0, 0, 9);
        body.Children.Add(title);
        foreach (var (model, value) in entries)
        {
            body.Children.Add(ChartTooltip.Row(Palette.ModelColor(model), Format.Model(model), _valueFormat(value)));
        }
        // A window has a column for every day, idle ones included.
        body.Children.Add(entries.Count == 0
            ? ChartTooltip.TotalRow("No activity", null)
            : ChartTooltip.TotalRow("Total", _valueFormat(entries.Sum(e => e.Value))));
        Tooltip.Show(body, at);
    }

    protected override void ClearHover()
    {
        base.ClearHover();
        if (_wash is not null) _wash.Visibility = Visibility.Collapsed;
    }
}
