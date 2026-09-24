using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UsageApp.Imaging;
using UsageApp.Theme;
using UsageCore.View;

namespace UsageApp.Controls;

/// <summary>
/// The mark beside a project name, or its monogram when there is none - or
/// when the file cannot be drawn, so a bad logo is a plain row, never a broken
/// image. The box is its final size from the first frame, so nothing reflows
/// when the image arrives.
/// </summary>
public static class ProjectLogoView
{
    public static Border Create(string name, string? path, double size = 38)
    {
        var box = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size >= 30 ? 9 : 6),
            Background = Palette.SurfaceHoverBrush,
            BorderBrush = Palette.BorderBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Monogram(name, size),
        };
        if (path is not null)
        {
            box.Loaded += async (_, _) =>
            {
                var source = await ImageLoader.LoadAsync(path, size, box.XamlRoot?.RasterizationScale ?? 1);
                if (source is null) return;
                box.Child = new Image { Source = source, Stretch = Stretch.Uniform, Margin = new Thickness(2) };
            };
        }
        return box;
    }

    private static TextBlock Monogram(string name, double size)
    {
        var text = Ui.Text(ProjectLogos.Initials(name), Math.Round(size * 0.36), 640, Palette.TextFaintBrush, 0.02);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        return text;
    }
}
