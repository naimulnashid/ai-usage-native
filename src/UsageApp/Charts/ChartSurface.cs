using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace UsageApp.Charts;

/// <summary>
/// The base every chart builds on: a fixed-height canvas that redraws itself
/// to its width, plus the hover plumbing.
/// </summary>
/// <remarks>
/// Charts are drawn with plain XAML shapes rather than a chart library, so the
/// details that carry meaning here - a band's shade encoding its price, a
/// legend sharing its percentage's denominator, a hovered slice fading the
/// rest - are ours to get exactly right. The largest chart is ~1,000 shapes.
/// </remarks>
public abstract class ChartSurface : Grid
{
    protected readonly Canvas Canvas = new() { Background = Theme.Palette.TransparentBrush };
    protected readonly ChartTooltip Tooltip;
    private double _lastWidth = -1;
    private bool _animateNext;

    protected ChartSurface(double height, bool animate)
    {
        Height = height;
        _animateNext = animate && Theme.Motion.Enabled;
        Children.Add(Canvas);
        Tooltip = new ChartTooltip(this);
        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - _lastWidth) < 0.5) return;
            _lastWidth = e.NewSize.Width;
            Redraw();
        };
        Canvas.PointerMoved += (_, e) => OnHover(e.GetCurrentPoint(Canvas).Position);
        Canvas.PointerExited += (_, _) => ClearHover();
        Canvas.PointerCanceled += (_, _) => ClearHover();
    }

    protected double W => ActualWidth;
    protected double H => Height;

    private void Redraw()
    {
        Canvas.Children.Clear();
        if (W <= 0) return;
        Draw(_animateNext);
        // Animate the first draw only; a resize just redraws in place.
        _animateNext = false;
    }

    /// <summary>Rebuilds every shape for the current size.</summary>
    protected abstract void Draw(bool animate);

    protected virtual void OnHover(Point at) { }

    protected virtual void ClearHover() => Tooltip.Hide();
}
