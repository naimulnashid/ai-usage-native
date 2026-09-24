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

    /// <summary>No API call, no cost - deliberately outside the accent family.</summary>
    public const string Synthetic = "#5D5D68";

    /// <summary>Unrecognised models: a desaturated mid-tone that reads as "unknown".</summary>
    public const string Unknown = "#9C7A6C";

    public static string? DisplayName(string model) => DisplayNames.GetValueOrDefault(model);

    public static string Hex(string model) =>
        model == "<synthetic>" ? Synthetic : Lookup.TryGetValue(model, out var s) ? s.Shade.Hex : Unknown;

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
