using System.Globalization;

namespace UsageCore.View;

public enum ProjectColorSource
{
    Custom,
    Logo,
    Auto,
}

/// <summary>
/// A colour per project, for the charts split by project: the share-of-spend
/// donut and Daily spend by project.
/// </summary>
/// <remarks>
/// <para>Models have a colour that MEANS something (darker is dearer); a
/// project has no such fact, so its colour is identity. In order:</para>
/// <list type="number">
/// <item>the colour chosen from its menu (<see cref="Config.ProjectColorSettings"/>), used exactly as chosen;</item>
/// <item>its logo's dominant colour, the logo's background left out (<see cref="DominantColor"/>);</item>
/// <item>a fallback from <see cref="Fallbacks"/>, handed out in rank order.</item>
/// </list>
/// <para>(2) and (3) are fitted into a luminance band that clears 3:1 against
/// BOTH themes' panels (<see cref="FitForCharts"/>), so one colour serves dark
/// and light alike - a mostly-black logo would otherwise draw an invisible band
/// on the dark theme, and a pale one on the light theme.</para>
/// </remarks>
public static class ProjectColors
{
    /// <summary>Luminance band that clears 3:1 on #0A0A0C and on white.</summary>
    public const double LuminanceMin = 0.12, LuminanceMax = 0.29;

    /// <summary>
    /// Vivid and mutually distinct, ordered so neighbours contrast. None is
    /// terracotta or ChatGPT green, so a fallback never reads as the accent.
    /// Every entry already sits in the chart band.
    /// </summary>
    public static readonly IReadOnlyList<string> Fallbacks =
    [
        "#3B82F6", "#D946EF", "#0D9488", "#D97706", "#8B5CF6", "#DB2777",
        "#0891B2", "#65A30D", "#6366F1", "#E11D48", "#C38504", "#7C3AED",
    ];

    public static bool IsHex(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    /// <summary>What a user typed, as <c>#RRGGBB</c>; accepts <c>#abc</c>, no hash, spaces. Null if not a colour.</summary>
    public static string? Parse(string? input)
    {
        var value = (input ?? "").Trim().TrimStart('#');
        if (value.Length == 3 && value.All(Uri.IsHexDigit)) value = string.Concat(value.Select(c => $"{c}{c}"));
        return value.Length == 6 && value.All(Uri.IsHexDigit) ? "#" + value.ToUpperInvariant() : null;
    }

    private static (double R, double G, double B) Rgb(string hex) =>
        (int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber),
         int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber),
         int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber));

    private static string ToHex(double r, double g, double b)
    {
        static int C(double v) => (int)Math.Round(Math.Clamp(v, 0, 255));
        return $"#{C(r):X2}{C(g):X2}{C(b):X2}";
    }

    private static double Lin(double c)
    {
        var v = c / 255;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    /// <summary>WCAG relative luminance.</summary>
    public static double Luminance(string hex)
    {
        var (r, g, b) = Rgb(hex);
        return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
    }

    public static double Contrast(string a, string b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>Hue and saturation kept; lightness moved just far enough into the band.</summary>
    public static string FitForCharts(string hex)
    {
        var y = Luminance(hex);
        if (y is >= LuminanceMin and <= LuminanceMax) return hex.ToUpperInvariant();
        var target = y < LuminanceMin ? LuminanceMin + 0.005 : LuminanceMax - 0.005;
        var (h, s, _) = ToHsl(Rgb(hex));
        double lo = 0, hi = 1;
        for (var i = 0; i < 30; i++)
        {
            var mid = (lo + hi) / 2;
            if (Luminance(FromHsl(h, s, mid)) < target) lo = mid;
            else hi = mid;
        }
        return FromHsl(h, s, (lo + hi) / 2);
    }

    private static (double H, double S, double L) ToHsl((double R, double G, double B) c)
    {
        var (r, g, b) = (c.R / 255, c.G / 255, c.B / 255);
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static string FromHsl(double h, double s, double l)
    {
        if (s == 0) return ToHex(l * 255, l * 255, l * 255);
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        double F(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        return ToHex(F(h + 1.0 / 3) * 255, F(h) * 255, F(h - 1.0 / 3) * 255);
    }

    /// <summary>
    /// Every project's colour, in the order above. <paramref name="projectIds"/>
    /// must be in a stable order (the report's, largest first): that order hands
    /// out the fallbacks, so a project has the same colour on every chart.
    /// </summary>
    public static Dictionary<string, (string Hex, ProjectColorSource Source)> Assign(
        IEnumerable<string> projectIds,
        IReadOnlyDictionary<string, string> custom,
        Func<string, string?> fromLogo)
    {
        var result = new Dictionary<string, (string, ProjectColorSource)>(StringComparer.Ordinal);
        var next = 0;
        foreach (var id in projectIds)
        {
            if (custom.TryGetValue(id, out var chosen) && IsHex(chosen))
            {
                result[id] = (chosen.ToUpperInvariant(), ProjectColorSource.Custom);
            }
            else if (fromLogo(id) is { } logo)
            {
                result[id] = (FitForCharts(logo), ProjectColorSource.Logo);
            }
            else
            {
                result[id] = (Fallbacks[next % Fallbacks.Count], ProjectColorSource.Auto);
                next++;
            }
        }
        return result;
    }

    private sealed class Bin
    {
        public int N;
        public double R, G, B;

        public void Add(byte r, byte g, byte b)
        {
            N++;
            R += r;
            G += g;
            B += b;
        }

        public double Chroma => N == 0 ? 0 : (Math.Max(R, Math.Max(G, B)) - Math.Min(R, Math.Min(G, B))) / N / 255;

        public string Hex => ToHex(R / N, G / N, B / N);
    }

    /// <summary>
    /// The dominant colour of a logo from its straight (not premultiplied) RGBA
    /// pixels, or null when there is none (fully transparent).
    /// </summary>
    /// <remarks>
    /// <para><b>The background is left out.</b> Transparent pixels never count,
    /// and the colours filling the image's outer BAND - a few pixels deep, not
    /// just the outermost ring, since an app-icon tile has a 1px border around a
    /// fill of another colour and both are background - are excluded wherever
    /// they appear. Reading only the ring called such a border the background
    /// and let the black fill win, and two real logos came out grey.</para>
    /// <para>Of what is left, coloured pixels win (an orange mark with white
    /// lettering is orange); a mark with no colour at all is answered in grey;
    /// and a plain glyph on a COLOURED tile returns the tile, since that tile is
    /// the identity.</para>
    /// </remarks>
    public static string? DominantColor(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4) return null;
        var band = Math.Max(1, (int)Math.Round(Math.Min(width, height) * 0.06));
        static int Key(ReadOnlySpan<byte> d, int o) => ((d[o] >> 5) << 6) | ((d[o + 1] >> 5) << 3) | (d[o + 2] >> 5);

        var edge = new Dictionary<int, Bin>();
        int bandTotal = 0, bandOpaque = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (x >= band && y >= band && x < width - band && y < height - band) continue;
                bandTotal++;
                var o = (y * width + x) * 4;
                if (rgba[o + 3] < 128) continue;
                bandOpaque++;
                var key = Key(rgba, o);
                if (!edge.TryGetValue(key, out var bin)) edge[key] = bin = new Bin();
                bin.Add(rgba[o], rgba[o + 1], rgba[o + 2]);
            }
        }
        var background = new HashSet<int>();
        if (bandOpaque >= bandTotal * 0.5)
        {
            foreach (var (key, bin) in edge)
            {
                if (bin.N >= bandOpaque * 0.2) background.Add(key);
            }
        }

        var fg = new Dictionary<int, Bin>();
        var fgTotal = 0;
        for (var i = 0; i < width * height; i++)
        {
            var o = i * 4;
            if (rgba[o + 3] < 128) continue;
            var key = Key(rgba, o);
            if (background.Contains(key)) continue;
            fgTotal++;
            if (!fg.TryGetValue(key, out var bin)) fg[key] = bin = new Bin();
            bin.Add(rgba[o], rgba[o + 1], rgba[o + 2]);
        }

        var chromatic = fg.Values.Where(b => b.Chroma >= 0.18).ToList();
        if (chromatic.Count > 0 && chromatic.Sum(b => b.N) >= fgTotal * 0.04)
        {
            return chromatic.MaxBy(b => b.N * (0.5 + b.Chroma))!.Hex;
        }
        var tile = background.Select(k => edge[k]).Where(b => b.Chroma >= 0.18).ToList();
        if (tile.Count > 0) return tile.MaxBy(b => b.N)!.Hex;
        return fgTotal == 0 ? null : fg.Values.MaxBy(b => b.N)!.Hex;
    }
}
