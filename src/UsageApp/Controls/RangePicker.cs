using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UsageApp.Theme;
using UsageCore.View;

namespace UsageApp.Controls;

/// <summary>
/// The range picker in a daily chart's panel head: Last 30 days (the
/// default), Last 60 days, All days. One per chart - a control inside a panel
/// changes that panel only. Every page load starts at 30 days.
/// </summary>
public static class RangePicker
{
    public static ComboBox Create(string automationName, Action<DayRange> onChange)
    {
        var box = new ComboBox
        {
            FontFamily = Fonts.Sans,
            FontSize = 14,
            FontWeight = Fonts.Weight(550),
            Foreground = Palette.TextMutedBrush,
            Background = Palette.SurfaceBrush,
            BorderBrush = Palette.BorderBrightBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Ui.RadiusSmall),
            Padding = new Thickness(12, 5, 8, 5),
            MinHeight = 0,
            MinWidth = 0,
        };
        box.Resources["ComboBoxBackgroundPointerOver"] = Palette.SurfaceBrush;
        box.Resources["ComboBoxBorderBrushPointerOver"] = Palette.Accent;
        box.Resources["ComboBoxForegroundPointerOver"] = Palette.TextBrush;
        box.Resources["ComboBoxBackgroundPressed"] = Palette.SurfaceBrush;
        box.Resources["ComboBoxBorderBrushPressed"] = Palette.Accent;
        box.Resources["ComboBoxDropDownBackground"] = Palette.TooltipBgBrush;
        box.Resources["ComboBoxDropDownBorderBrush"] = Palette.BorderBrightBrush;
        foreach (var range in new[] { DayRange.Last30, DayRange.Last60, DayRange.All })
        {
            box.Items.Add(new ComboBoxItem { Content = DayRanges.Label(range), Tag = range, FontFamily = Fonts.Sans, FontSize = 14 });
        }
        box.SelectedIndex = 0;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: DayRange range }) onChange(range);
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, automationName);
        return box;
    }
}
