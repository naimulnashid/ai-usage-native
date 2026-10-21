using System.Text.Json;
using System.Text.Json.Nodes;
using UsageCore.Model;
using UsageCore.View;

namespace UsageCore.Config;

/// <summary>One agent's per-model settings: custom rates, chosen colours and names, and the table's order.</summary>
public sealed class ModelSettingsData
{
    public Dictionary<string, ModelRate> Pricing { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Colors { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);

    /// <summary>The Model prices table's order, as dragged. Empty: dearest first.</summary>
    public List<string> Order { get; } = [];
}

/// <summary>
/// The user's own per-model settings, set from the app: a rate for a model the
/// rate card does not price (or prices wrongly), and a colour and a display
/// name for any model.
/// One file per agent: <c>model-settings.json</c> and
/// <c>codex-model-settings.json</c> in the data folder.
/// </summary>
/// <remarks>
/// <para><b>Why not the rate card.</b> A card in the config folder REPLACES the
/// built-in one wholesale, so writing one to price a single new model would
/// freeze every other rate at today's values. These rates are laid over
/// whichever card is in use, one model at a time, and win until reset.</para>
/// <para><b>Rates change the numbers; colours do not.</b> A rate feeds
/// <c>CostOf</c>, so saving one is followed by a fresh parse. A colour is a view
/// preference, read by the charts directly.</para>
/// <para>Colours are limited to the agent's own <see cref="ModelColors.Palettes"/>,
/// which is what keeps every band above 3:1 against the panel whatever is
/// picked.</para>
/// <para>A name is a label, like a project's rename: every setting stays keyed
/// by the model string the transcripts carry. The order is the Model prices
/// table's only; the charts keep stacking the dearest at the bottom, which is
/// what their shades encode.</para>
/// </remarks>
public static class ModelSettings
{
    public const int MaxModelLength = 200;
    public const double MaxRate = 10_000;
    public const int MaxModels = 500;
    public const int MaxNameLength = 60;

    public static string PathFor(ProviderId provider, string? dataDir = null) =>
        Path.Combine(dataDir ?? AppPaths.DataDir, provider == ProviderId.Claude ? "model-settings.json" : "codex-model-settings.json");

    /// <summary>A model string that can be a key: <c>_</c> keys are documentation in the rate cards.</summary>
    public static bool IsValidModelName(string? value) =>
        value is { Length: > 0 and <= MaxModelLength } && !value.StartsWith('_') && value.Trim() == value;

    public static bool IsValidDisplayName(string? value) =>
        value is { Length: > 0 and <= MaxNameLength } && value.Trim() == value && !value.Any(char.IsControl);

    public static bool IsValidRate(ModelRate rate) =>
        new[] { rate.Input, rate.CacheWrite5m, rate.CacheWrite1h, rate.CacheRead, rate.Output }
            .All(v => double.IsFinite(v) && v >= 0 && v <= MaxRate);

    /// <summary>One agent's settings. Fails soft to none: the card's prices and the default colours.</summary>
    public static ModelSettingsData Load(ProviderId provider, string? dataDir = null)
    {
        var result = new ModelSettingsData();
        var file = PathFor(provider, dataDir);
        try
        {
            if (!File.Exists(file)) return result;
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root) return result;
            if (root["pricing"] is JsonObject pricing)
            {
                foreach (var (model, node) in pricing.Take(MaxModels))
                {
                    if (IsValidModelName(model) && ReadRate(node) is { } rate) result.Pricing[model] = rate;
                }
            }
            if (root["colors"] is JsonObject colors)
            {
                foreach (var (model, node) in colors.Take(MaxModels))
                {
                    if (IsValidModelName(model) && node is JsonValue v && v.TryGetValue<string>(out var hex) && ModelColors.IsPaletteColor(provider, hex))
                    {
                        result.Colors[model] = hex.ToUpperInvariant();
                    }
                }
            }
            if (root["names"] is JsonObject names)
            {
                foreach (var (model, node) in names.Take(MaxModels))
                {
                    if (IsValidModelName(model) && node is JsonValue v && v.TryGetValue<string>(out var name) && IsValidDisplayName(name))
                    {
                        result.Names[model] = name;
                    }
                }
            }
            if (root["order"] is JsonArray order)
            {
                foreach (var node in order.Take(MaxModels))
                {
                    if (node is JsonValue v && v.TryGetValue<string>(out var model) && IsValidModelName(model) && !result.Order.Contains(model))
                    {
                        result.Order.Add(model);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ModelSettingsData();
        }
        return result;
    }

    private static ModelRate? ReadRate(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        double? Get(string name) => o[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        if (Get("input") is not { } input || Get("cacheWrite5m") is not { } w5 || Get("cacheWrite1h") is not { } w1
            || Get("cacheRead") is not { } read || Get("output") is not { } output) return null;
        var rate = new ModelRate(input, w5, w1, read, output);
        return IsValidRate(rate) ? rate : null;
    }

    /// <summary>
    /// Changes one model's rate, colour and/or name and saves. A null argument
    /// leaves that field alone; <paramref name="clearRate"/> / <paramref name="clearColor"/>
    /// / <paramref name="clearName"/> remove it. Written through a temporary file,
    /// so a crash leaves the old file. Throws with a readable message on bad
    /// input or a failed write.
    /// </summary>
    public static ModelSettingsData Save(
        ProviderId provider,
        string model,
        ModelRate? rate = null,
        string? color = null,
        bool clearRate = false,
        bool clearColor = false,
        string? name = null,
        bool clearName = false,
        string? dataDir = null)
    {
        if (!IsValidModelName(model)) throw new ArgumentException("Not a model name.");
        if (rate is not null && !IsValidRate(rate)) throw new ArgumentException("Every rate must be a number from 0 to 10,000.");
        if (color is not null && !ModelColors.IsPaletteColor(provider, color)) throw new ArgumentException("That colour is not one of this agent's shades.");
        if (name is not null && !IsValidDisplayName(name)) throw new ArgumentException($"A name needs 1 to {MaxNameLength} characters, with no space at either end.");

        var current = Load(provider, dataDir);
        if (clearRate) current.Pricing.Remove(model);
        else if (rate is not null) current.Pricing[model] = rate;
        if (clearColor) current.Colors.Remove(model);
        else if (color is not null) current.Colors[model] = color.ToUpperInvariant();
        if (clearName) current.Names.Remove(model);
        else if (name is not null) current.Names[model] = name;
        Write(provider, current, dataDir);
        return current;
    }

    /// <summary>Saves the Model prices table's order. An empty list goes back to dearest first.</summary>
    public static ModelSettingsData SaveOrder(ProviderId provider, IReadOnlyList<string> order, string? dataDir = null)
    {
        if (order.Any(m => !IsValidModelName(m))) throw new ArgumentException("Not a model name.");
        var current = Load(provider, dataDir);
        current.Order.Clear();
        current.Order.AddRange(order.Distinct(StringComparer.Ordinal));
        Write(provider, current, dataDir);
        return current;
    }

    /// <summary>
    /// <paramref name="models"/>, already in the default order, rearranged by
    /// <paramref name="order"/>: the listed ones first, as listed, then any it does
    /// not name (a model first seen since the drag) in their default order.
    /// </summary>
    public static List<string> Arrange(IEnumerable<string> models, IReadOnlyList<string> order)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
        return models
            .Select((model, i) => (Model: model, Index: i))
            .OrderBy(x => rank.TryGetValue(x.Model, out var r) ? r : int.MaxValue)
            .ThenBy(x => x.Index)
            .Select(x => x.Model)
            .ToList();
    }

    private static void Write(ProviderId provider, ModelSettingsData current, string? dataDir)
    {
        if (current.Pricing.Count > MaxModels || current.Colors.Count > MaxModels || current.Names.Count > MaxModels || current.Order.Count > MaxModels)
        {
            throw new InvalidOperationException($"No more than {MaxModels} models can have settings.");
        }

        var root = new JsonObject
        {
            ["version"] = 1,
            ["pricing"] = new JsonObject(current.Pricing.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
                KeyValuePair.Create<string, JsonNode?>(kv.Key, new JsonObject
                {
                    ["input"] = kv.Value.Input,
                    ["cacheWrite5m"] = kv.Value.CacheWrite5m,
                    ["cacheWrite1h"] = kv.Value.CacheWrite1h,
                    ["cacheRead"] = kv.Value.CacheRead,
                    ["output"] = kv.Value.Output,
                }))),
            ["colors"] = new JsonObject(current.Colors.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
                KeyValuePair.Create<string, JsonNode?>(kv.Key, kv.Value))),
            ["names"] = new JsonObject(current.Names.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
                KeyValuePair.Create<string, JsonNode?>(kv.Key, kv.Value))),
            ["order"] = new JsonArray(current.Order.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
        };
        var file = PathFor(provider, dataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>The card with the custom rates laid over it: what both parsers price with by default.</summary>
    public static PricingConfig WithCustomRates(PricingConfig card, IReadOnlyDictionary<string, ModelRate> custom)
    {
        if (custom.Count == 0) return card;
        var models = new Dictionary<string, ModelRate>(card.Models, StringComparer.Ordinal);
        foreach (var (model, rate) in custom) models[model] = KeepCardTier(card, model, rate);
        return new PricingConfig
        {
            Currency = card.Currency,
            LastVerified = card.LastVerified,
            Models = models,
            Aliases = card.Aliases,
            Source = card.Source,
        };
    }

    /// <summary>
    /// A custom rate replaces the per-token prices only. The long-context tier is
    /// how the vendor bills a long prompt, which the editor does not set, so it
    /// stays as the card has it - including a tier reached through an alias. Used
    /// for pricing AND for describing the rate, so the table never names fewer
    /// tiered models than the parse charges.
    /// </summary>
    private static ModelRate KeepCardTier(PricingConfig card, string model, ModelRate rate) =>
        AppConfig.GetRate(card, model)?.LongContext is { } tier ? rate with { LongContext = tier } : rate;

    public static PricingConfig LoadEffectivePricing(ProviderId provider) =>
        WithCustomRates(AppConfig.LoadPricing(provider), Load(provider).Pricing);

    /// <summary>
    /// Where each model's rate comes from. Takes the card and the custom rates
    /// separately: once laid over each other, a custom rate hides whether the
    /// card had an entry of its own, which decides whether Reset leaves a price.
    /// </summary>
    public static Dictionary<string, ModelRateInfo> DescribeRates(
        PricingConfig card,
        IReadOnlyDictionary<string, ModelRate> custom,
        IEnumerable<string> models)
    {
        var result = new Dictionary<string, ModelRateInfo>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var cardRate = AppConfig.GetRate(card, model);
            var onCard = cardRate is not null;
            card.Aliases.TryGetValue(model, out var aliasOf);
            if (custom.TryGetValue(model, out var own)) result[model] = new ModelRateInfo(KeepCardTier(card, model, own), RateSource.Custom, null, onCard);
            else if (card.Models.TryGetValue(model, out var direct)) result[model] = new ModelRateInfo(direct, RateSource.Card, null, onCard);
            else if (aliasOf is not null && cardRate is not null) result[model] = new ModelRateInfo(cardRate, RateSource.Alias, aliasOf, onCard);
            else result[model] = new ModelRateInfo(null, RateSource.None, null, onCard);
        }
        return result;
    }
}
