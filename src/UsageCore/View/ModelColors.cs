using UsageCore.Model;

namespace UsageCore.View;

/// <summary>
/// Model colours: shades of the owning agent's accent, ordered by price.
/// </summary>
/// <remarks>
/// <para>The shade IS the encoding: darker means more expensive, so a dark band
/// low in a stacked chart is premium spend. One table serves both agents because
/// their model strings cannot collide (<c>claude-*</c> against <c>gpt-*</c> and
/// <c>codex-*</c>).</para>
/// <para>Every shade clears 3:1 against the panel (WCAG 1.4.11). That floor
/// compresses the ramp - neighbours differ by ~1.2:1 - so colour is never the
/// only way a band can be read: every chart has a legend and a tooltip.</para>
/// <para>Each agent's block must stay ordered by price. Adding a model, or a
/// material rate change, means re-checking that a dearer model is darker.</para>
/// <para><b>The user can override a model's shade</b> from the Model prices
/// table, and give one to a model this table has never heard of. The choice is
/// limited to <see cref="Palettes"/>, the agent's own ramp, so an overridden
/// band still clears 3:1 and still reads as that agent's colour.</para>
/// </remarks>
public static class ModelColors
{
    public sealed record Shade(string Hex, double OutputPrice);

    /// <summary>In ramp order, which is also the tie-break for equal prices.</summary>
    public static readonly IReadOnlyList<(string Model, Shade Shade)> Shades =
    [
        // $50 output - deepest
        ("claude-fable-5", new("#A14324", 50)),
        ("claude-mythos-5", new("#BA4D2A", 50)),
        // $25 - the Opus family
        ("claude-opus-5", new("#D0562F", 25)),
        ("claude-opus-4-7", new("#D56743", 25)),
        ("claude-opus-4-8", new("#D97757", 25)),
        ("claude-opus-4-6", new("#DF8B70", 25)),
        // $20 - Opus 5.5
        ("claude-opus-5-5", new("#E19278", 20)),
        // $15 / $10 - Sonnet
        ("claude-sonnet-4-6", new("#E39981", 15)),
        ("claude-sonnet-5", new("#E7A893", 10)),
        // $5 - lightest
        ("claude-haiku-4-5", new("#EDC0B1", 5)),

        // Codex: shades of the ChatGPT teal-green. $30 deepest.
        ("gpt-5.6-sol", new("#0B6D55", 30)),
        // $14. The auto-review band is the same model wearing a job title; it
        // gets its own lighter tone so self-review spend stays legible.
        ("gpt-5.3-codex", new("#10A37F", 14)),
        ("codex-auto-review", new("#13C69A", 14)),
    ];

    private static readonly Dictionary<string, (Shade Shade, int Order)> Lookup =
        Shades.Select((s, i) => (s.Model, s.Shade, i)).ToDictionary(x => x.Model, x => (x.Shade, x.i), StringComparer.Ordinal);

    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.Ordinal)
    {
        ["gpt-5.6-sol"] = "GPT-5.6 Sol",
        ["gpt-5.3-codex"] = "GPT-5.3 Codex",
        ["codex-auto-review"] = "auto-review (5.3 Codex)",
    };

    /// <summary>
    /// The shades a model's colour can be set to, per agent, deepest first.
    /// Every one clears 3:1 against the panel, and the ramp above is a subset,
    /// so every default is also a choice.
    /// </summary>
    public static readonly IReadOnlyDictionary<ProviderId, IReadOnlyList<string>> Palettes =
        new Dictionary<ProviderId, IReadOnlyList<string>>
        {
            [ProviderId.Claude] =
            [
                "#A14324", "#AE4827", "#BA4D2A", "#C5512C", "#D0562F", "#D56743",
                "#D97757", "#DF8B70", "#E19278", "#E39981", "#E7A893", "#EDC0B1",
            ],
            [ProviderId.Codex] =
            [
                "#0B6D55", "#0C7A5F", "#0E8A6C", "#0F9674", "#10A37F",
                "#12B48C", "#13C69A", "#3FD2AC", "#6CDDBF", "#98E8D3",
            ],
        };

    /// <summary>
    /// The same palettes for the light theme, index for index. A chosen shade is
    /// stored as its dark hex, so it cannot follow the theme by itself;
    /// <see cref="Themed"/> swaps it for the twin at the same index at draw time.
    /// The dark ramp gets LIGHTER toward the cheap end, since light stands out on
    /// black; on white that would fade the cheap bands into the panel, so each
    /// light ramp runs from a deep tone to the agent's accent, every step above
    /// 3:1 on white. Darker still means dearer in both themes.
    /// </summary>
    public static readonly IReadOnlyDictionary<ProviderId, IReadOnlyList<string>> LightPalettes =
        new Dictionary<ProviderId, IReadOnlyList<string>>
        {
            [ProviderId.Claude] =
            [
                "#712E19", "#79321B", "#82361D", "#8C3A1F", "#963E21", "#A14224",
                "#AD4727", "#B94C29", "#C6512C", "#D25A33", "#D56845", "#D97657",
            ],
            [ProviderId.Codex] =
            [
                "#074B3B", "#085340", "#095B47", "#0A624D", "#0B6C54",
                "#0C755C", "#0D8164", "#0E8C6D", "#0F9876", "#10A580",
            ],
        };

    private static readonly Dictionary<string, string> LightTwin = Palettes
        .SelectMany(p => p.Value.Select((hex, i) => (hex, twin: LightPalettes[p.Key][i])))
        .ToDictionary(x => x.hex, x => x.twin, StringComparer.OrdinalIgnoreCase);

    /// <summary>Synthetic's light-theme twin: 3.19:1 on white.</summary>
    public const string SyntheticLight = "#8C909B";

    /// <summary>
    /// A model colour as it is drawn in the light theme when <paramref name="light"/>.
    /// Anything not in a palette - Unknown clears 3:1 on both grounds - is kept.
    /// </summary>
    public static string Themed(string hex, bool light) =>
        !light ? hex : hex == Synthetic ? SyntheticLight : LightTwin.GetValueOrDefault(hex, hex);

    /// <summary>True when <paramref name="color"/> is one of the agent's palette shades (any case).</summary>
    public static bool IsPaletteColor(ProviderId provider, string color) =>
        Palettes[provider].Any(shade => string.Equals(shade, color, StringComparison.OrdinalIgnoreCase));

    /// <summary>No API call, no cost - deliberately outside the accent family.</summary>
    public const string Synthetic = "#5D5D68";

    /// <summary>Unrecognised models: a desaturated mid-tone that reads as "unknown".</summary>
    public const string Unknown = "#9C7A6C";

    public static string? DisplayName(string model) => DisplayNames.GetValueOrDefault(model);

    public static string Hex(string model) =>
        model == "<synthetic>" ? Synthetic : Lookup.TryGetValue(model, out var s) ? s.Shade.Hex : Unknown;

    /// <summary>A model's colour: the user's chosen shade when there is one, else its default.</summary>
    public static string Hex(string model, IReadOnlyDictionary<string, string>? overrides) =>
        overrides is not null && overrides.TryGetValue(model, out var chosen) ? chosen : Hex(model);

    /// <summary>
    /// Most expensive first, so legends and stacks read deep to light. Equal
    /// prices (the whole Opus family) break on the ramp's own order, so the
    /// visual order always matches the colour order.
    /// </summary>
    public static int ByPriceDesc(string a, string b)
    {
        var (pa, ia) = Lookup.TryGetValue(a, out var sa) ? (sa.Shade.OutputPrice, sa.Order) : (-1, int.MaxValue);
        var (pb, ib) = Lookup.TryGetValue(b, out var sb) ? (sb.Shade.OutputPrice, sb.Order) : (-1, int.MaxValue);
        if (pa != pb) return pb.CompareTo(pa);
        if (ia != ib) return ia.CompareTo(ib);
        return string.CompareOrdinal(a, b);
    }

    public static readonly Comparer<string> PriceDescending = Comparer<string>.Create(ByPriceDesc);

    /// <summary>Heat-map step 0-5: 0 is no activity, 5 the dearest days.</summary>
    public static int HeatStep(double value, double max)
    {
        if (value <= 0 || max <= 0) return 0;
        var ratio = value / max;
        return ratio <= 0.2 ? 1 : ratio <= 0.4 ? 2 : ratio <= 0.65 ? 3 : ratio <= 0.85 ? 4 : 5;
    }
}
