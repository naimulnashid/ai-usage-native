using System.Reflection;
using System.Text.Json;
using UsageCore.Model;
using UsageCore.Parsing;

namespace UsageCore.Config;

/// <summary>Merge rules and display names for one agent's projects.</summary>
public sealed record ProjectConfig(
    IReadOnlyDictionary<string, string> Merge,
    IReadOnlyDictionary<string, string> DisplayNames)
{
    public static ProjectConfig None { get; } =
        new(new Dictionary<string, string>(), new Dictionary<string, string>());
}

/// <summary>
/// Loads the rate cards, settings and project overrides. Every loader fails
/// soft: an unreadable file means the default, never an exception.
/// </summary>
public static class AppConfig
{
    public static string PricingFileName(ProviderId provider) =>
        provider == ProviderId.Claude ? "pricing.json" : "codex-pricing.json";

    public static string ProjectsFileName(ProviderId provider) =>
        provider == ProviderId.Claude ? "projects.json" : "codex-projects.json";

    /// <summary>
    /// The agent's rate card: an override in the config folder when there is a
    /// readable one, else the card built into the app.
    /// </summary>
    /// <remarks>
    /// A missing card surfaces every model as UNPRICED - never as free. One card
    /// per agent: the two vendors' prices move independently.
    /// </remarks>
    public static PricingConfig LoadPricing(ProviderId provider, string? configDir = null)
    {
        var name = PricingFileName(provider);
        var overridePath = Path.Combine(configDir ?? AppPaths.ConfigDir, name);
        if (File.Exists(overridePath))
        {
            try
            {
                var parsed = ParsePricing(File.ReadAllText(overridePath), overridePath);
                if (parsed is not null) return parsed;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"UsageCore.{name}");
        if (stream is null) return new PricingConfig();
        using var reader = new StreamReader(stream);
        return ParsePricing(reader.ReadToEnd(), "built-in") ?? new PricingConfig();
    }

    /// <summary>Reads a rate card. Null when it is not one at all.</summary>
    public static PricingConfig? ParsePricing(string json, string source)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (!Guards.TryObject(root, "models", out var models)) return null;

            var rates = new Dictionary<string, ModelRate>(StringComparer.Ordinal);
            foreach (var model in models.EnumerateObject())
            {
                if (model.Name.StartsWith('_') || model.Value.ValueKind != JsonValueKind.Object) continue;
                rates[model.Name] = new ModelRate(
                    Rate(model.Value, "input"),
                    Rate(model.Value, "cacheWrite5m"),
                    Rate(model.Value, "cacheWrite1h"),
                    Rate(model.Value, "cacheRead"),
                    Rate(model.Value, "output"));
            }

            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Guards.TryObject(root, "aliases", out var aliasObj))
            {
                foreach (var alias in aliasObj.EnumerateObject())
                {
                    if (!alias.Name.StartsWith('_') && alias.Value.ValueKind == JsonValueKind.String && alias.Value.GetString() is { Length: > 0 } target)
                    {
                        aliases[alias.Name] = target;
                    }
                }
            }

            return new PricingConfig
            {
                Currency = Guards.StringField(root, "currency") ?? "USD",
                LastVerified = Guards.StringField(root, "lastVerified"),
                Models = rates,
                Aliases = aliases,
                Source = source,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double Rate(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0
            ? d
            : 0;

    /// <summary>
    /// Defaults for a fresh install. The day offset follows the machine's
    /// CURRENT UTC offset, read once per parse - so in a daylight-saving zone a
    /// few hours around each switch can land a day off unless it is pinned.
    /// </summary>
    public static Settings DefaultSettings() => new(
        LocalUtcOffsetHours: TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalHours,
        WeekStartsOn: WeekStart.Monday,
        MaxIdleGapMinutes: 30,
        SuspiciousOutputTokens: 5,
        SuspiciousContextTokens: 1000);

    /// <summary><c>settings.json</c>. Every key optional; a missing or invalid one takes its default.</summary>
    public static Settings LoadSettings(string? configDir = null)
    {
        var defaults = DefaultSettings();
        var file = Path.Combine(configDir ?? AppPaths.ConfigDir, "settings.json");
        if (!File.Exists(file)) return defaults;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return defaults;

            double Num(string name, double fallback) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : fallback;

            var weekStart = Guards.StringField(root, "weekStartsOn")?.ToLowerInvariant() switch
            {
                "sunday" => WeekStart.Sunday,
                "saturday" => WeekStart.Saturday,
                "monday" => WeekStart.Monday,
                _ => defaults.WeekStartsOn,
            };

            return new Settings(
                Num("localUtcOffsetHours", defaults.LocalUtcOffsetHours),
                weekStart,
                Num("maxIdleGapMinutes", defaults.MaxIdleGapMinutes),
                Num("suspiciousOutputTokens", defaults.SuspiciousOutputTokens),
                Num("suspiciousContextTokens", defaults.SuspiciousContextTokens));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return defaults;
        }
    }

    /// <summary>
    /// <c>projects.json</c> / <c>codex-projects.json</c>. Agents key projects by
    /// working directory, so renaming or moving a folder splits a project's
    /// history in two; <c>merge</c> stitches it back. Keys starting with <c>_</c>
    /// are documentation.
    /// </summary>
    public static ProjectConfig LoadProjectConfig(ProviderId provider, string? configDir = null)
    {
        var file = Path.Combine(configDir ?? AppPaths.ConfigDir, ProjectsFileName(provider));
        if (!File.Exists(file)) return ProjectConfig.None;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return new ProjectConfig(Clean(doc.RootElement, "merge"), Clean(doc.RootElement, "displayNames"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ProjectConfig.None;
        }

        static Dictionary<string, string> Clean(JsonElement root, string name)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Guards.TryObject(root, name, out var obj)) return result;
            foreach (var property in obj.EnumerateObject())
            {
                if (property.Name.StartsWith('_')) continue;
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } value)
                {
                    result[property.Name] = value;
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Follows a merge chain (A to B to C lands on C), with a cycle guard so a
    /// config mistake degrades to "no merge" rather than hanging the parse. A
    /// cycle is reported once, members sorted, so <c>a -> b -> a</c> reads the
    /// same whichever end it was reached from.
    /// </summary>
    public static string ResolveProjectId(string id, IReadOnlyDictionary<string, string> merge, List<string> warnings)
    {
        var seen = new List<string> { id };
        var current = id;
        while (merge.TryGetValue(current, out var next))
        {
            if (seen.Contains(next))
            {
                var cycle = string.Join(" -> ", seen.Append(next).Distinct().Order(StringComparer.Ordinal).Select(m => $"\"{m}\""));
                var warning = $"Ignored a cycle in your project merge rules: {cycle}.";
                if (!warnings.Contains(warning)) warnings.Add(warning);
                return id;
            }
            seen.Add(next);
            current = next;
        }
        return current;
    }

    /// <summary>The rate for a model, following one hop of aliases.</summary>
    public static ModelRate? GetRate(PricingConfig pricing, string model)
    {
        if (pricing.Models.TryGetValue(model, out var direct)) return direct;
        return pricing.Aliases.TryGetValue(model, out var target) && pricing.Models.TryGetValue(target, out var aliased)
            ? aliased
            : null;
    }

    /// <summary>
    /// USD for one set of buckets; 0 when unpriced (callers flag that rather
    /// than treating it as free). Reasoning is absent on purpose: it is already
    /// inside output and would be billed twice.
    /// </summary>
    public static double CostOf(in TokenCounts tokens, ModelRate? rate) =>
        rate is null
            ? 0
            : (tokens.Input * rate.Input
               + tokens.CacheWrite5m * rate.CacheWrite5m
               + tokens.CacheWrite1h * rate.CacheWrite1h
               + tokens.CacheRead * rate.CacheRead
               + tokens.Output * rate.Output) / 1_000_000d;
}
