using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UsageApp.Imaging;
using UsageApp.Theme;
using UsageCore.Model;

namespace UsageApp.Controls;

/// <summary>An agent's mark at any size, as the rail draws it. "All agents" is the app's own icon.</summary>
public static class AgentMark
{
    /// <summary>
    /// Each agent's mark file and the size it is drawn at for a 24px footprint.
    /// The vendors pad their files differently: Anthropic's artwork fills its
    /// viewBox, OpenAI's fills half of it. An image drawn larger and pulled back
    /// by a negative margin keeps the footprint and makes the marks look the same
    /// size. Those files are untouched. Antigravity ships only an app icon, its
    /// glyph on a white tile; antigravity.png is that glyph alone, white turned
    /// to alpha and cropped to it, so it sits beside the other two.
    /// </summary>
    public static (string File, double Size, bool BlackOnLight) For(ProviderId id) => id switch
    {
        ProviderId.Claude => (Path.Combine("AgentMarks", "claude-code.svg"), 24, false),
        ProviderId.Codex => (Path.Combine("AgentMarks", "codex.svg"), 44, true),
        ProviderId.Antigravity => (Path.Combine("AgentMarks", "antigravity.png"), 24, false),
        _ => ("app-icon.svg", 24, false),
    };

    public static string FileOf(ProviderId id) => Path.Combine(AppContext.BaseDirectory, "Assets", For(id).File);

    /// <summary>
    /// OpenAI's file is its white mark, invisible on a light ground, so there it
    /// is drawn black: OpenAI's own black variant of the same mark.
    /// </summary>
    public static async Task Load(Image mark, string file, bool blackOnLight) =>
        mark.Source = await ImageLoader.LoadAsync(file, mark.Width, mark.XamlRoot?.RasterizationScale ?? 1, black: blackOnLight && Palette.IsLight);

    /// <summary>A mark in a square <paramref name="footprint"/> wide, for a page. Pages rebuild on a theme change, which reloads it.</summary>
    public static FrameworkElement Create(ProviderId id, double footprint)
    {
        var (_, size, blackOnLight) = For(id);
        var drawn = size * footprint / 24;
        var image = new Image { Width = drawn, Height = drawn, Margin = new Thickness((footprint - drawn) / 2) };
        var file = FileOf(id);
        image.Loaded += async (_, _) => await Load(image, file, blackOnLight);
        var box = new Grid { Width = footprint, Height = footprint, VerticalAlignment = VerticalAlignment.Center };
        box.Children.Add(image);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, UsageCore.Providers.Get(id).Label);
        return box;
    }
}
