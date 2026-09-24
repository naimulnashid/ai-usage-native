using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using UsageApp.Theme;
using UsageCore.View;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace UsageApp.Charts;

public sealed record DonutSlice(string Id, string Name, double Cost, long Tokens, double Share, Color Fill, bool Remainder, int Projects, int Hidden);

/// <summary>
/// A ring of slices with the total in the hole. Hovering a slice fades every
/// other one - and <see cref="ActiveChanged"/> lets the legend beside it fade in
/// step, which is the whole point of the pairing: a ring of ten slivers is hard
/// to map onto a legend without it.
/// </summary>
public sealed class DonutChart : Grid
{
    private readonly List<DonutSlice> _slices;
    private readonly List<Path> _paths = [];
    private readonly Canvas _canvas = new();
    private readonly ChartTooltip _tooltip;
    private readonly bool _animate;
    private string? _active;

    public event Action<string?>? ActiveChanged;

    public DonutChart(IReadOnlyList<DonutSlice> slices, double total, int projectCount, double size = 260, bool animate = true)
    {
        _slices = slices.ToList();
        _animate = animate && Motion.Enabled;
        Width = size;
        Height = size;
        Children.Add(_canvas);
        _tooltip = new ChartTooltip(this);

        // The total sits in the hole, in the real type scale.
        var centre = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 2, IsHitTestVisible = false };
        var value = Ui.Text(Format.Usd(total), 25, 650, Palette.Accent, -0.02, numeric: true);
        value.HorizontalAlignment = HorizontalAlignment.Center;
        // Every project, not the slice count: a collapsed tail is still projects.
        var label = Ui.Text($"across {projectCount} {(projectCount == 1 ? "project" : "projects")}", 12.5, 400, Palette.TextFaintBrush);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        centre.Children.Add(value);
        centre.Children.Add(label);
        Children.Add(centre);

        Loaded += (_, _) => Draw();
    }

    private void Draw()
    {
        _canvas.Children.Clear();
        _paths.Clear();
        var radius = Math.Min(Width, Height) / 2;
        var outer = radius * 0.94;
        var inner = radius * 0.62;
        var cx = Width / 2;
        var cy = Height / 2;
        const double padding = 1.2;

        var total = _slices.Sum(s => s.Cost);
        var nonZero = _slices.Count(s => s.Cost > 0);
        var available = 360 - nonZero * padding;
        var angle = 0.0;
        var first = true;
        foreach (var slice in _slices)
        {
            if (slice.Cost <= 0) continue;
            if (!first) angle += padding;
            first = false;
            var sweep = total > 0 ? slice.Cost / total * available : 0;
            var path = new Path
            {
                Data = Sector(cx, cy, inner, outer, angle, angle + sweep),
                Fill = new SolidColorBrush(slice.Fill),
                Stroke = Palette.SurfaceBrush,
                StrokeThickness = 2,
                Tag = slice.Id,
            };
            path.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(170) };
            var captured = slice;
            path.PointerEntered += (_, _) => SetActive(captured.Id, raise: true);
            path.PointerMoved += (_, e) => ShowTip(captured, e.GetCurrentPoint(this).Position);
            path.PointerExited += (_, _) =>
            {
                SetActive(null, raise: true);
                _tooltip.Hide();
            };
            _paths.Add(path);
            _canvas.Children.Add(path);
            angle += sweep;
        }

        if (_animate)
        {
            var scale = new ScaleTransform { CenterX = cx, CenterY = cy, ScaleX = 0.86, ScaleY = 0.86 };
            _canvas.RenderTransform = scale;
            _canvas.Opacity = 0;
            var story = new Storyboard();
            foreach (var (target, property, from) in new (DependencyObject, string, double)[] { (scale, "ScaleX", 0.86), (scale, "ScaleY", 0.86), (_canvas, "Opacity", 0) })
            {
                var anim = new DoubleAnimation { From = from, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(850)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                Storyboard.SetTarget(anim, target);
                Storyboard.SetTargetProperty(anim, property);
                story.Children.Add(anim);
            }
            story.Begin();
        }
    }

    /// <summary>
    /// Angles run anticlockwise from three o'clock, as Recharts' pie does, so
    /// the largest project starts at the right and the ring reads upwards.
    /// </summary>
    private static Geometry Sector(double cx, double cy, double inner, double outer, double startDeg, double endDeg)
    {
        Point At(double r, double deg)
        {
            var rad = deg * Math.PI / 180;
            return new Point(cx + r * Math.Cos(rad), cy - r * Math.Sin(rad));
        }
        var large = endDeg - startDeg > 180;
        if (endDeg - startDeg >= 359.99) endDeg = startDeg + 359.99;
        var figure = new PathFigure { StartPoint = At(outer, startDeg), IsClosed = true };
        figure.Segments.Add(new ArcSegment { Point = At(outer, endDeg), Size = new Size(outer, outer), IsLargeArc = large, SweepDirection = SweepDirection.Counterclockwise });
        figure.Segments.Add(new LineSegment { Point = At(inner, endDeg) });
        figure.Segments.Add(new ArcSegment { Point = At(inner, startDeg), Size = new Size(inner, inner), IsLargeArc = large, SweepDirection = SweepDirection.Clockwise });
        return new PathGeometry { Figures = { figure } };
    }

    /// <summary>Fades every slice but <paramref name="id"/>; null restores them all.</summary>
    public void SetActive(string? id, bool raise = false)
    {
        if (_active == id) return;
        _active = id;
        foreach (var path in _paths)
        {
            path.Opacity = id is null || (string)path.Tag == id ? 1 : 0.28;
        }
        if (raise) ActiveChanged?.Invoke(id);
    }

    private void ShowTip(DonutSlice slice, Point at)
    {
        var body = ChartTooltip.Stack();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(Ui.Swatch(slice.Fill));
        head.Children.Add(Ui.Text(slice.Name, 14, 600));
        body.Children.Add(head);
        body.Children.Add(ChartTooltip.Big($"{slice.Share:0.0}%", Palette.Accent));
        body.Children.Add(ChartTooltip.Muted($"{Format.Usd(slice.Cost)} · {Format.Tokens(slice.Tokens)} tokens"));
        if (slice.Remainder)
        {
            var note = Ui.Text(RemainderNote(slice), 13, 400, Palette.TextFaintBrush);
            note.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(note);
        }
        _tooltip.Show(body, at);
    }

    /// <summary>"the 3 smallest projects and 2 hidden, combined".</summary>
    public static string RemainderNote(DonutSlice slice)
    {
        var small = slice.Projects - slice.Hidden;
        var smallest = small == 1 ? "the smallest project" : $"the {small} smallest projects";
        if (slice.Hidden == 0) return $"{smallest}, combined";
        if (small == 0) return slice.Hidden == 1 ? "1 hidden project" : $"{slice.Hidden} hidden projects, combined";
        return $"{smallest} and {slice.Hidden} hidden, combined";
    }
}
