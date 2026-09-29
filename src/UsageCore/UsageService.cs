using UsageCore.Config;
using UsageCore.History;
using UsageCore.Model;
using UsageCore.Parsing;

namespace UsageCore;

/// <summary>The one entry point the app and the CLI use: parse, then archive.</summary>
public static class UsageService
{
    /// <summary>
    /// Parses an agent's transcripts and rebuilds the result from the archive.
    /// Recomputed from disk every time: no cache, because a stale cache reports
    /// numbers that were true a minute ago, which is worse than a slow parse.
    /// </summary>
    /// <remarks>
    /// Prices with the rate card plus the user's own rates (ModelSettings), and
    /// prices the archive's stored days at the same rates - so a rate set in the
    /// app reaches every day, not only the ones still on disk. Then says where
    /// each model's rate came from, for the Model prices table.
    /// </remarks>
    public static UsageReport Load(ProviderId provider, bool useArchive = true)
    {
        var card = AppConfig.LoadPricing(provider);
        var custom = ModelSettings.Load(provider).Pricing;
        var pricing = ModelSettings.WithCustomRates(card, custom);
        var options = new ParseOptions { Pricing = pricing };
        var parsed = provider == ProviderId.Claude ? ClaudeParser.Parse(options) : CodexParser.Parse(options);
        var report = useArchive ? HistoryArchive.WithHistory(parsed, pricing: pricing) : parsed;
        report.ModelRates = ModelSettings.DescribeRates(card, custom, report.Global.PerModel.Keys);
        return report;
    }
}
