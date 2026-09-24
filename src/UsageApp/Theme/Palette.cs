using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using UsageCore;
using UsageCore.Model;
using Windows.UI;

namespace UsageApp.Theme;

/// <summary>
/// The colour tokens. True-black OLED theme, one accent per agent.
/// </summary>
/// <remarks>
/// <para><b>The accent brushes are shared instances whose colour is swapped in
/// place</b> when the agent changes (<see cref="ApplyProvider"/>). Everything
/// drawn with them - text, borders, chart strokes - re-themes without being
/// rebuilt, the same way the original re-themed by redefining CSS variables.
/// Nothing outside this file may hard-code an accent colour: a literal
/// terracotta in a tooltip is how Codex once ended up with Claude Code's accent
/// in the middle of a teal page.</para>
/// <para>Contrast is measured, not judged: <c>TextFaint</c> carries most of the
/// small text and clears 4.5:1 on every background it sits on.</para>
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

    public static readonly Color Bg = Hex("#000000");
    public static readonly Color Surface = Hex("#0A0A0C");
    public static readonly Color SurfaceHover = Hex("#111114");
    public static readonly Color Border = Hex("#1E1E24");
    public static readonly Color BorderBright = Hex("#2E2E37");
    public static readonly Color Text = Hex("#FAFAFA");
    public static readonly Color TextMuted = Hex("#9A9AA4");
    public static readonly Color TextFaint = Hex("#7D7D87");
    public static readonly Color TooltipBg = Hex("#131317");
    public static readonly Color Warn = Hex("#F87171");
    public static readonly Color Good = Hex("#4ADE80");
    public static readonly Color RowBorder = Hex("#1E1E24", 0.6);
    public static readonly Color Track = Hex("#141418");

    public static readonly SolidColorBrush BgBrush = new(Bg);
    public static readonly SolidColorBrush SurfaceBrush = new(Surface);
    public static readonly SolidColorBrush SurfaceHoverBrush = new(SurfaceHover);
    public static readonly SolidColorBrush BorderBrush = new(Border);
    public static readonly SolidColorBrush BorderBrightBrush = new(BorderBright);
    public static readonly SolidColorBrush TextBrush = new(Text);
    public static readonly SolidColorBrush TextMutedBrush = new(TextMuted);
    public static readonly SolidColorBrush TextFaintBrush = new(TextFaint);
    public static readonly SolidColorBrush TooltipBgBrush = new(TooltipBg);
    public static readonly SolidColorBrush WarnBrush = new(Warn);
    public static readonly SolidColorBrush WarnDimBrush = new(Hex("#F87171", 0.12));
    public static readonly SolidColorBrush WarnBorderBrush = new(Hex("#F87171", 0.34));
    public static readonly SolidColorBrush GoodBrush = new(Good);
    public static readonly SolidColorBrush RowBorderBrush = new(RowBorder);
    public static readonly SolidColorBrush TrackBrush = new(Track);
    public static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);
    public static readonly SolidColorBrush HoverWashBrush = new(Hex("#FFFFFF", 0.04));

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

    /// <summary>Re-themes every accent-drawn element for an agent.</summary>
    public static void ApplyProvider(ProviderMeta meta)
    {
        var accent = Hex(meta.Accent);
        AccentColor = accent;
        var codex = meta.Id == ProviderId.Codex;
        Accent.Color = accent;
        AccentBright.Color = Hex(meta.AccentBright);
        AccentDim.Color = Hex(meta.Accent, codex ? 0.15 : 0.14);
        AccentGlow.Color = Hex(meta.Accent, codex ? 0.30 : 0.28);
        AccentBorder.Color = Hex(meta.Accent, codex ? 0.34 : 0.32);
        AccentBorderStrong.Color = Hex(meta.Accent, codex ? 0.46 : 0.42);
        AccentFill.Color = Hex(meta.Accent, codex ? 0.24 : 0.22);
        for (var i = 0; i < Heat.Length; i++) Heat[i].Color = Hex(meta.HeatRamp[i]);
    }

    /// <summary>A model's shade: darker means more expensive. See ModelColors.</summary>
    public static SolidColorBrush ModelBrush(string model) => new(Hex(UsageCore.View.ModelColors.Hex(model)));

    public static Color ModelColor(string model) => Hex(UsageCore.View.ModelColors.Hex(model));

    /// <summary>
    /// A project's donut shade by RANK: the largest takes the accent at full
    /// strength and each one below is mixed further back toward the panel.
    /// A project has no intrinsic fact to encode the way a model's price is.
    /// </summary>
    public static Color RankColor(int index, int count)
    {
        var t = count > 1 ? index / (double)(count - 1) : 0;
        var strength = Math.Round(100 - t * 68) / 100; // 100% down to 32%
        return Mix(Surface, AccentColor, strength);
    }

    /// <summary>"Others" sits outside the rank ramp: a remainder has no rank.</summary>
    public static Color OthersColor => Mix(Surface, TextFaint, 0.55);
}
