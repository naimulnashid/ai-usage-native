using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using UsageCore;
using UsageCore.Model;
using Windows.UI;

namespace UsageApp.Theme;

/// <summary>
/// The colour tokens: a true-black OLED dark theme (the default) and a light
/// one, one accent per agent in each.
/// </summary>
/// <remarks>
/// <para><b>Every brush is a shared instance whose colour is swapped in
/// place</b> - by <see cref="ApplyProvider"/> when the agent changes and by
/// <see cref="ApplyTheme"/> when the theme does. Everything drawn with them -
/// text, borders, chart strokes, the shell - re-themes without being rebuilt,
/// the way the original re-themed by redefining CSS variables. The page is
/// rebuilt after a theme change anyway, for the few colours read as values.
/// Nothing outside this file may hard-code a colour: a literal is now wrong in
/// one of the two themes by construction.</para>
/// <para><b>Light is not dark inverted.</b> White cards on a grey page,
/// separated by a border and a shadow; accents darkened to carry text at AA on
/// white; a heat map that darkens as spend rises; model shades swapped for
/// their light twins (<see cref="UsageCore.View.ModelColors.Themed"/>).</para>
/// <para>Contrast is measured, not judged: <c>TextFaint</c> carries most of the
/// small text and clears 4.5:1 on every background it sits on, in both.</para>
/// </remarks>
public static class Palette
{
    public static Color Hex(string hex, double alpha = 1)
    {
        var h = hex.TrimStart('#');
        return Color.FromArgb(
            (byte)Math.Round(alpha * 255),
            Convert.ToByte(h[..2], 16),
            Convert.ToByte(h[2..4], 16),
            Convert.ToByte(h[4..6], 16));
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t),
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private sealed record Tokens(
        string Bg, string Surface, string SurfaceHover, string Border, string BorderBright,
        string Text, string TextMuted, string TextFaint, string TooltipBg,
        string Warn, double WarnDim, double WarnBorder, string Good,
        Color RowBorder, string Track, Color HoverWash);

    private static readonly Tokens Dark = new(
        "#000000", "#0A0A0C", "#111114", "#1E1E24", "#2E2E37",
        "#FAFAFA", "#9A9AA4", "#7D7D87", "#131317",
        "#F87171", 0.12, 0.34, "#4ADE80",
        Hex("#1E1E24", 0.6), "#141418", Hex("#FFFFFF", 0.04));

    /// <summary>A cool grey page, white cards; both greys of text clear 4.5:1 on each backdrop.</summary>
    private static readonly Tokens Light = new(
        // SurfaceHover was #F3F4F7, 1.07:1 on white: a hovered row showed nothing.
        "#F4F5F8", "#FFFFFF", "#E8EBF0", "#E3E5EA", "#CDD1D9",
        "#111318", "#3F4451", "#5F6472", "#FFFFFF",
        "#C62828", 0.07, 0.32, "#15803D",
        Hex("#ECEEF2"), "#ECEEF2", Hex("#111318", 0.09));

    /// <summary>True while the light theme is on.</summary>
    public static bool IsLight { get; private set; }

    public static Color Bg { get; private set; }
    public static Color Surface { get; private set; }
    public static Color SurfaceHover { get; private set; }
    public static Color Border { get; private set; }
    public static Color BorderBright { get; private set; }
    public static Color Text { get; private set; }
    public static Color TextMuted { get; private set; }
    public static Color TextFaint { get; private set; }
    public static Color TooltipBg { get; private set; }
    public static Color Warn { get; private set; }
    public static Color Good { get; private set; }
    public static Color RowBorder { get; private set; }
    public static Color Track { get; private set; }

    public static readonly SolidColorBrush BgBrush = new();
    public static readonly SolidColorBrush SurfaceBrush = new();
    public static readonly SolidColorBrush SurfaceHoverBrush = new();
    public static readonly SolidColorBrush BorderBrush = new();
    public static readonly SolidColorBrush BorderBrightBrush = new();
    public static readonly SolidColorBrush TextBrush = new();
    public static readonly SolidColorBrush TextMutedBrush = new();
    public static readonly SolidColorBrush TextFaintBrush = new();
    public static readonly SolidColorBrush TooltipBgBrush = new();
    public static readonly SolidColorBrush WarnBrush = new();
    public static readonly SolidColorBrush WarnDimBrush = new();
    public static readonly SolidColorBrush WarnBorderBrush = new();
    public static readonly SolidColorBrush GoodBrush = new();
    public static readonly SolidColorBrush RowBorderBrush = new();
    public static readonly SolidColorBrush TrackBrush = new();
    public static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);
    public static readonly SolidColorBrush HoverWashBrush = new();

    // The accent family. Mutated in place by ApplyProvider.
    public static readonly SolidColorBrush Accent = new();
    public static readonly SolidColorBrush AccentBright = new();
    public static readonly SolidColorBrush AccentDim = new();
    public static readonly SolidColorBrush AccentGlow = new();
    public static readonly SolidColorBrush AccentBorder = new();
    public static readonly SolidColorBrush AccentBorderStrong = new();
    public static readonly SolidColorBrush AccentFill = new();

    /// <summary>Heat-map ramp: step 0 is "no activity", 1-5 rise with spend.</summary>
    public static readonly SolidColorBrush[] Heat = [new(), new(), new(), new(), new(), new()];

    public static Color AccentColor { get; private set; }

    private static ProviderMeta _meta = Providers.Claude;

    /// <summary>Raised after the theme changes, once every brush holds its new colour.</summary>
    public static event Action? ThemeChanged;

    /// <summary>Swaps every token for the theme's value, accents included.</summary>
    public static void ApplyTheme(bool light)
    {
        IsLight = light;
        var t = light ? Light : Dark;
        Bg = Hex(t.Bg);
        Surface = Hex(t.Surface);
        SurfaceHover = Hex(t.SurfaceHover);
        Border = Hex(t.Border);
        BorderBright = Hex(t.BorderBright);
        Text = Hex(t.Text);
        TextMuted = Hex(t.TextMuted);
        TextFaint = Hex(t.TextFaint);
        TooltipBg = Hex(t.TooltipBg);
        Warn = Hex(t.Warn);
        Good = Hex(t.Good);
        RowBorder = t.RowBorder;
        Track = Hex(t.Track);

        BgBrush.Color = Bg;
        SurfaceBrush.Color = Surface;
        SurfaceHoverBrush.Color = SurfaceHover;
        BorderBrush.Color = Border;
        BorderBrightBrush.Color = BorderBright;
        TextBrush.Color = Text;
        TextMutedBrush.Color = TextMuted;
        TextFaintBrush.Color = TextFaint;
        TooltipBgBrush.Color = TooltipBg;
        WarnBrush.Color = Warn;
        WarnDimBrush.Color = Hex(t.Warn, t.WarnDim);
        WarnBorderBrush.Color = Hex(t.Warn, t.WarnBorder);
        GoodBrush.Color = Good;
        RowBorderBrush.Color = RowBorder;
        TrackBrush.Color = Track;
        HoverWashBrush.Color = t.HoverWash;
        ApplyProvider(_meta);
        ThemeChanged?.Invoke();
    }

    /// <summary>Re-themes every accent-drawn element for an agent, in the current theme.</summary>
    public static void ApplyProvider(ProviderMeta meta)
    {
        _meta = meta;
        var codex = meta.Id == ProviderId.Codex;
        var hex = IsLight ? meta.AccentLight : meta.Accent;
        var accent = Hex(hex);
        AccentColor = accent;
        Accent.Color = accent;
        AccentBright.Color = Hex(IsLight ? meta.AccentBrightLight : meta.AccentBright);
        // Tints sit on white in the light theme, where a smaller share reads.
        AccentDim.Color = Hex(hex, IsLight ? 0.08 : codex ? 0.15 : 0.14);
        AccentGlow.Color = Hex(hex, IsLight ? 0.18 : codex ? 0.30 : 0.28);
        AccentBorder.Color = Hex(hex, IsLight ? 0.30 : codex ? 0.34 : 0.32);
        AccentBorderStrong.Color = Hex(hex, IsLight ? 0.45 : codex ? 0.46 : 0.42);
        AccentFill.Color = Hex(hex, IsLight ? 0.14 : codex ? 0.24 : 0.22);
        var ramp = IsLight ? meta.HeatRampLight : meta.HeatRamp;
        for (var i = 0; i < Heat.Length; i++) Heat[i].Color = Hex(ramp[i]);
        SetModelColors(UsageCore.Config.ModelSettings.Load(meta.Id).Colors);
    }

    static Palette() => ApplyTheme(light: false);

    private static IReadOnlyDictionary<string, string>? _modelColors;

    /// <summary>
    /// The user's chosen shades for the current agent, from the Model prices
    /// table. Set with the agent, and again after a colour is saved; the page is
    /// rebuilt after either, so every swatch and band picks it up.
    /// </summary>
    public static void SetModelColors(IReadOnlyDictionary<string, string>? colors) => _modelColors = colors;

    /// <summary>A model's shade: darker means more expensive. See ModelColors.</summary>
    public static SolidColorBrush ModelBrush(string model) => new(ModelColor(model));

    public static Color ModelColor(string model) =>
        Hex(UsageCore.View.ModelColors.Themed(UsageCore.View.ModelColors.Hex(model, _modelColors), IsLight));

    /// <summary>A stored palette shade as the current theme draws it.</summary>
    public static Color PaletteShade(string hex) => Hex(UsageCore.View.ModelColors.Themed(hex, IsLight));

    /// <summary>
    /// "Others" / "Other" sits outside every palette: a remainder has no
    /// identity, and its summed value can exceed the slice beside it.
    /// </summary>
    public static Color OthersColor => Mix(Surface, TextFaint, 0.55);
}
