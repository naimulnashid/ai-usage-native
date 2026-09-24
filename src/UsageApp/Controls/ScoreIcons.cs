using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace UsageApp.Controls;

/// <summary>
/// The twelve line icons on the Activity score cards, drawn for the one size
/// they are used at.
/// </summary>
/// <remarks>
/// Three rules keep them a set: one 16-unit grid, one 1.5 stroke with round
/// caps and joins, never filled; three strokes or fewer, because at 15px
/// detail turns to mush; and they take their colour from the card, so a hover
/// can tint the whole set to the agent's accent.
/// </remarks>
public static class ScoreIcons
{
    private static readonly Dictionary<string, string[]> Paths = new()
    {
        // A terminal window - a session is a transcript of one.
        ["sessions"] = ["M4.2,2.9 H11.8 A2.2,2.2 0 0 1 14,5.1 V10.9 A2.2,2.2 0 0 1 11.8,13.1 H4.2 A2.2,2.2 0 0 1 2,10.9 V5.1 A2.2,2.2 0 0 1 4.2,2.9 Z", "M2,6.3 H14"],
        // Speech bubble.
        ["messages"] = ["M4.6,2.4 H11.4 A2.2,2.2 0 0 1 13.6,4.6 V8.2 A2.2,2.2 0 0 1 11.4,10.4 H7.2 L4.2,13.2 V10.4 A2.2,2.2 0 0 1 2,8.2 V4.6 A2.2,2.2 0 0 1 4.2,2.4 Z"],
        // Hourglass: elapsed time, against the clock face used for time of day.
        ["hourglass"] = ["M4.6,2 H11.4 M4.6,14 H11.4 M5.4,2 V4.3 C5.4,5.7 8,6.6 8,8 C8,9.4 5.4,10.3 5.4,11.7 V14 M10.6,2 V4.3 C10.6,5.7 8,6.6 8,8 C8,9.4 10.6,10.3 10.6,11.7 V14"],
        // Four-point sparkle.
        ["model"] = ["M8,1.8 L9.5,6.5 L14.2,8 L9.5,9.5 L8,14.2 L6.5,9.5 L1.8,8 L6.5,6.5 Z"],
        // Arrow down onto a line, and its mirror - a pair, read as opposites.
        ["input"] = ["M8,2.4 V9.8 M4.9,6.7 L8,9.8 L11.1,6.7 M2.6,13.4 H13.4"],
        ["output"] = ["M8,9.8 V2.4 M4.9,5.5 L8,2.4 L11.1,5.5 M2.6,13.4 H13.4"],
        // Stacked store.
        ["cache"] = ["M2.8,4 A5.2,2.2 0 1 0 13.2,4 A5.2,2.2 0 1 0 2.8,4 Z", "M2.8,4 V12 C2.8,13.2 5.1,14.2 8,14.2 C10.9,14.2 13.2,13.2 13.2,12 V4", "M2.8,8 C2.8,9.2 5.1,10.2 8,10.2 C10.9,10.2 13.2,9.2 13.2,8"],
        // A line with one spike well above the rest.
        ["peak"] = ["M1.8,12.8 L5.1,8.8 L7.4,11.3 L11,3.6 L14.2,12.8"],
        ["calendar"] = ["M4.2,3.5 H11.8 A2,2 0 0 1 13.8,5.5 V11.8 A2,2 0 0 1 11.8,13.8 H4.2 A2,2 0 0 1 2.2,11.8 V5.5 A2,2 0 0 1 4.2,3.5 Z", "M2.2,6.9 H13.8 M5.6,2 V4.6 M10.4,2 V4.6"],
        // One path; the lick on the right edge keeps it from reading as a drop.
        ["flame"] = ["M9.2,1.6 C9.5,3.8 8.2,4.8 7,6 C5.6,7.3 4.4,8.5 4.4,10.3 A3.9,3.9 0 0 0 8.3,14.2 A3.9,3.9 0 0 0 12.2,10 C12.2,8 11,6.8 10,5.8 C9.8,6.9 9.3,7.5 8.7,7.9 C9.2,5.8 9.4,3.6 9.2,1.6 Z"],
        ["trophy"] = ["M4.9,2.4 H11.1 V5.5 A3.1,3.1 0 0 1 4.9,5.5 V2.4 Z", "M4.9,3.6 H3.3 V4.5 A2.2,2.2 0 0 0 5.2,6.7 M11.1,3.6 H12.7 V4.5 A2.2,2.2 0 0 1 10.8,6.7", "M8,8.6 V11.4 M5.6,13.6 H10.4"],
        ["clock"] = ["M2.2,8 A5.8,5.8 0 1 0 13.8,8 A5.8,5.8 0 1 0 2.2,8 Z", "M8,4.7 V8 L10.4,9.5"],
    };

    /// <summary>A 15px icon stroked with <paramref name="brush"/>.</summary>
    public static Viewbox Create(string name, Brush brush)
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        foreach (var data in Paths[name])
        {
            canvas.Children.Add(new Path
            {
                Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data),
                Stroke = brush,
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            });
        }
        return new Viewbox { Width = 15, Height = 15, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }
}
