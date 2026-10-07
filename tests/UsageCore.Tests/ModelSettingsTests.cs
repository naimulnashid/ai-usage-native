using System.Text.Json.Nodes;
using UsageCore.Config;
using UsageCore.History;
using UsageCore.Model;
using UsageCore.Parsing;
using UsageCore.View;
using static UsageCore.Tests.Fixtures;

namespace UsageCore.Tests;

/// <summary>Custom rates and colours, <c>&lt;synthetic&gt;</c>, and per-project activity.</summary>
public class ModelSettingsTests
{
    private const string Cwd = @"C:\Users\you\Projects\My App";
    private static readonly string Dir = ClaudeParser.EncodeProjectDir(Cwd);
    private static readonly ModelRate Rate = new(3, 3.75, 6, 0.3, 15);

    [Fact]
    public void SavesARateAndAColour_ReadsThemBack_AndClearsThem()
    {
        using var tmp = new TempDir();
        var shade = ModelColors.Palettes[ProviderId.Claude][2];
        ModelSettings.Save(ProviderId.Claude, "claude-new", rate: Rate, dataDir: tmp.Path);
        ModelSettings.Save(ProviderId.Claude, "claude-new", color: shade, dataDir: tmp.Path);

        var loaded = ModelSettings.Load(ProviderId.Claude, tmp.Path);
        Assert.Equal(Rate, loaded.Pricing["claude-new"]);
        Assert.Equal(shade, loaded.Colors["claude-new"]);
        // The other agent has a file of its own.
        Assert.Empty(ModelSettings.Load(ProviderId.Codex, tmp.Path).Pricing);

        ModelSettings.Save(ProviderId.Claude, "claude-new", clearRate: true, dataDir: tmp.Path);
        loaded = ModelSettings.Load(ProviderId.Claude, tmp.Path);
        Assert.Empty(loaded.Pricing);
        Assert.Equal(shade, loaded.Colors["claude-new"]);
    }

    [Fact]
    public void RefusesAColourOutsideTheAgentsShades_AndABadRate()
    {
        using var tmp = new TempDir();
        Assert.Throws<ArgumentException>(() => ModelSettings.Save(ProviderId.Claude, "m", color: "#FF0000", dataDir: tmp.Path));
        Assert.Throws<ArgumentException>(() => ModelSettings.Save(ProviderId.Claude, "m", color: ModelColors.Palettes[ProviderId.Codex][0], dataDir: tmp.Path));
        Assert.Throws<ArgumentException>(() => ModelSettings.Save(ProviderId.Claude, "m", rate: Rate with { Output = -1 }, dataDir: tmp.Path));
        Assert.Throws<ArgumentException>(() => ModelSettings.Save(ProviderId.Claude, "_comment", rate: Rate, dataDir: tmp.Path));
    }

    [Fact]
    public void FailsSoftOnACorruptFile()
    {
        using var tmp = new TempDir();
        File.WriteAllText(ModelSettings.PathFor(ProviderId.Claude, tmp.Path), "{ not json");
        var loaded = ModelSettings.Load(ProviderId.Claude, tmp.Path);
        Assert.Empty(loaded.Pricing);
        Assert.Empty(loaded.Colors);
    }

    [Fact]
    public void CustomRatesPriceAnUnknownModel_AndSayWhereEachRateCameFrom()
    {
        var card = Pricing(new Dictionary<string, string> { ["review-bot"] = "cheap-model" });
        var custom = new Dictionary<string, ModelRate> { ["brand-new"] = Rate, ["test-model"] = Rate };
        var pricing = ModelSettings.WithCustomRates(card, custom);
        Assert.Equal(Rate, AppConfig.GetRate(pricing, "brand-new"));
        Assert.Equal(Rate, AppConfig.GetRate(pricing, "test-model"));
        Assert.False(card.Models.ContainsKey("brand-new"));

        var rates = ModelSettings.DescribeRates(card, custom, ["brand-new", "test-model", "cheap-model", "review-bot", "nobody"]);
        Assert.Equal(new ModelRateInfo(Rate, RateSource.Custom, null, false), rates["brand-new"]);
        Assert.True(rates["test-model"].OnCard);
        Assert.Equal(RateSource.Card, rates["cheap-model"].Source);
        Assert.Equal(RateSource.Alias, rates["review-bot"].Source);
        Assert.Equal("cheap-model", rates["review-bot"].AliasOf);
        Assert.Equal(RateSource.None, rates["nobody"].Source);
    }

    [Fact]
    public void EveryDefaultShadeIsAPaletteChoice()
    {
        foreach (var (model, shade) in ModelColors.Shades)
        {
            Assert.True(ModelColors.Palettes.Values.Any(p => p.Contains(shade.Hex)), $"{model}'s {shade.Hex} is not a palette choice");
        }
        var chosen = new Dictionary<string, string> { ["claude-opus-5"] = "#AE4827" };
        Assert.Equal("#AE4827", ModelColors.Hex("claude-opus-5", chosen));
        Assert.NotEqual("#AE4827", ModelColors.Hex("claude-opus-5"));
    }

    [Fact]
    public void DoesNotCountSyntheticAsAModel_AMessage_OrAHolderOfRuntime()
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine(Dir, "s1.jsonl"),
            Assistant("2026-08-01T10:00:00Z", "msg_a", output: 10, cwd: Cwd),
            Assistant("2026-08-01T10:01:00Z", "msg_s", model: "<synthetic>", cwd: Cwd),
            Assistant("2026-08-01T10:03:00Z", "msg_b", output: 10, cwd: Cwd));

        var report = ClaudeParser.Parse(Options(tmp.Path));
        Assert.Equal(["test-model"], report.Global.PerModel.Keys);
        Assert.Equal(2, report.Global.Combined.Messages);
        // Both gaps, either side of the synthetic line, stay with the model working.
        Assert.Equal(180, report.Global.PerModel["test-model"].RuntimeSeconds, 6);
        Assert.Equal(["test-model"], report.Projects[0].Sessions[0].Models);
    }

    [Fact]
    public void GivesEveryProjectTheActivityStatsTheOverviewHas()
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine(Dir, "s1.jsonl"),
            Assistant("2026-08-01T10:00:00Z", "msg_a", output: 10, cwd: Cwd),
            Assistant("2026-08-02T10:00:00Z", "msg_b", output: 20, cwd: Cwd));
        WriteLines(tmp.Combine(Dir + "-other", "s2.jsonl"),
            Assistant("2026-08-05T15:00:00Z", "msg_c", model: "cheap-model", output: 5));

        var report = ClaudeParser.Parse(Options(tmp.Path));
        var app = report.Projects.Single(p => p.Id == Dir).Activity!;
        Assert.Equal(1, app.Sessions);
        Assert.Equal(2, app.Messages);
        Assert.Equal(2, app.ActiveDays);
        Assert.Equal(2, app.LongestStreakDays);
        Assert.Equal(10, app.PeakHour);
        Assert.Equal("test-model", app.FavoriteModel);
        Assert.Equal("s1", app.PeakSession?.SessionId);
        Assert.Equal(2, report.Activity.Sessions);
        Assert.Equal(3, report.Activity.ActiveDays);
    }

    [Fact]
    public void DropsSyntheticFromAnArchiveWrittenBeforeTheParserDid()
    {
        static JsonObject Cell(long messages, double runtime) => new()
        {
            ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite5m"] = 0, ["cacheWrite1h"] = 0,
            ["messages"] = messages, ["runtimeSeconds"] = runtime, ["totalTokens"] = 0, ["costUsd"] = 0, ["unpriced"] = false,
        };
        var days = new JsonObject
        {
            ["2026-08-01"] = new JsonObject
            {
                ["perModel"] = new JsonObject { ["test-model"] = Cell(5, 100), ["<synthetic>"] = Cell(2, 30) },
                ["combined"] = Cell(7, 130),
                ["projects"] = new JsonObject(),
            },
        };
        // Through text, as a real archive is read: an in-memory JsonValue built
        // from a long does not convert to double the way parsed JSON does.
        var parsed = (JsonObject)JsonNode.Parse(days.ToJsonString())!;
        var day = HistoryArchive.SanitizeDays(parsed, "history.json", [])["2026-08-01"];
        Assert.Equal(["test-model"], day.PerModel.Keys);
        Assert.Equal(5, day.Combined.Messages);
        // The runtime was spent, so the combined figure keeps it.
        Assert.Equal(130, day.Combined.RuntimeSeconds);
    }

    [Fact]
    public void CostOf_AddsOnlyTheLongContextSurcharge_AndOnlyWithATier()
    {
        var rate = Pricing().Models["test-model"];
        var tokens = new TokenCounts(1_000_000, 1_000_000, 1_000_000, 1_000_000, 0, LongContext: new PricedTokens(1_000_000, 1_000_000, 1_000_000, 1_000_000, 0));
        // 10 + 50 + 1 + 12.50 at the standard rates.
        Assert.Equal(73.5, AppConfig.CostOf(tokens, rate), 9);
        // Input, cache read and cache write doubled; output x1.5: 47 + 75.
        Assert.Equal(122, AppConfig.CostOf(tokens, rate with { LongContext = new LongContextRule(0, 2, 1.5) }), 9);
    }

    [Fact]
    public void ACustomRateKeepsTheCardsLongContextTier()
    {
        var tier = new LongContextRule(272_000, 2, 1.5);
        var card = Pricing(new Dictionary<string, string> { ["review-bot"] = "cheap-model" });
        card.Models["cheap-model"] = card.Models["cheap-model"] with { LongContext = tier };
        var pricing = ModelSettings.WithCustomRates(card, new Dictionary<string, ModelRate> { ["cheap-model"] = Rate, ["review-bot"] = Rate, ["brand-new"] = Rate });
        Assert.Equal(Rate with { LongContext = tier }, AppConfig.GetRate(pricing, "cheap-model"));
        Assert.Equal(Rate with { LongContext = tier }, AppConfig.GetRate(pricing, "review-bot"));
        Assert.Equal(Rate, AppConfig.GetRate(pricing, "brand-new"));
        // The table describes the rate the parse charges, tier included.
        var described = ModelSettings.DescribeRates(card, new Dictionary<string, ModelRate> { ["cheap-model"] = Rate }, ["cheap-model"]);
        Assert.Equal(Rate with { LongContext = tier }, described["cheap-model"].Rate);
    }

    [Fact]
    public void ReadsALongContextTierFromARateCard_AndIgnoresOneThatWouldLowerAPrice()
    {
        var card = AppConfig.ParsePricing("""
            { "models": {
                "a": { "input": 1, "output": 2, "longContext": { "aboveInputTokens": 272000, "inputMultiplier": 2, "outputMultiplier": 1.5 } },
                "b": { "input": 1, "output": 2, "longContext": { "aboveInputTokens": 272000, "inputMultiplier": 0.5, "outputMultiplier": 1.5 } },
                "c": { "input": 1, "output": 2 } } }
            """, "test")!;
        Assert.Equal(new LongContextRule(272_000, 2, 1.5), card.Models["a"].LongContext);
        Assert.Null(card.Models["b"].LongContext);
        Assert.Null(card.Models["c"].LongContext);
    }

    [Fact]
    public void TheArchiveKeepsTheLongContextSubset_SoARepricedDayKeepsItsSurcharge()
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine("sessions", "2026", "08", "01", Fixtures.Rollout("00000000-0000-0000-0000-00000000000a")),
            SessionMeta("2026-08-01T10:00:00Z", "/home/you/projects/my-app"),
            TurnContext("2026-08-01T10:00:00Z", "test-model"),
            TokenCount(new Totals("2026-08-01T10:00:10Z", 2_000_000, 0, 1_000_000)));
        var tiered = CodexParserTests.Tiered();
        var report = CodexParser.Parse(Options(tmp.Path, tiered));
        // 2M in @ $10 x 2 + 1M out @ $50 x 1.5.
        Assert.Equal(40 + 75, report.Global.Combined.CostUsd, 9);

        // Through the file on disk, the way the app reads it back.
        var file = tmp.Combine("codex-history.json");
        var warnings = new List<string>();
        HistoryArchive.Save(HistoryArchive.Merge(new HistoryFile(), report, Now), file, warnings);
        var history = HistoryArchive.Load(file, warnings);
        Assert.Empty(warnings);
        Assert.Equal(new PricedTokens(2_000_000, 1_000_000, 0, 0, 0), history.Days["2026-08-01"].PerModel["test-model"].LongContext);

        // The transcript is gone; the day is priced again from the archive.
        using var empty = new TempDir();
        var later = CodexParser.Parse(Options(empty.Path, tiered));
        var restored = HistoryArchive.Apply(later, history, Now.ToUnixTimeMilliseconds(), tiered);
        Assert.Equal(40 + 75, restored.Global.Combined.CostUsd, 9);
        Assert.Equal(40 + 75, restored.Daily.Single(d => d.Date == "2026-08-01").PerModel["test-model"].CostUsd, 9);
    }

    [Fact]
    public void BackfillsTheSubsetOntoAStoredDayTheMergeKeeps_OnlyWhereTokensMatch()
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine("sessions", "2026", "08", "01", Fixtures.Rollout("00000000-0000-0000-0000-00000000000a")),
            SessionMeta("2026-08-01T10:00:00Z", "/home/you/projects/my-app"),
            TurnContext("2026-08-01T10:00:00Z", "test-model"),
            TokenCount(new Totals("2026-08-01T10:00:10Z", 2_000_000, 0, 1_000_000)));
        var tiered = CodexParserTests.Tiered();
        var report = CodexParser.Parse(Options(tmp.Path, tiered));
        var fresh = report.Daily.Single();
        var projectId = report.Projects.Single().Id;

        // Stored before the subset existed, and fuller than what is left on
        // disk: a second model whose transcript has since gone.
        UsageCell Old(UsageCell from, long extraMessages)
        {
            var cell = from.Clone();
            cell.LongContext = null;
            cell.Messages += extraMessages;
            return cell;
        }
        var gone = new UsageCell { Input = 5, Messages = 5 };
        var history = new HistoryFile();
        history.Days["2026-08-01"] = new ArchivedDay
        {
            PerModel = new(StringComparer.Ordinal) { ["test-model"] = Old(fresh.PerModel["test-model"], 0), ["gone-model"] = gone },
            Combined = Old(fresh.Combined, 5),
            Projects = new(StringComparer.Ordinal)
            {
                [projectId] = new UsageBucket
                {
                    PerModel = new(StringComparer.Ordinal) { ["test-model"] = Old(fresh.PerModel["test-model"], 0) },
                    Combined = Old(fresh.Combined, 0),
                },
            },
        };

        var day = HistoryArchive.Merge(history, report, Now).Days["2026-08-01"];
        var subset = new PricedTokens(2_000_000, 1_000_000, 0, 0, 0);
        Assert.Equal(6, day.Combined.Messages);
        Assert.Equal(subset, day.PerModel["test-model"].LongContext);
        Assert.Null(day.PerModel["gone-model"].LongContext);
        Assert.Equal(subset, day.Combined.LongContext);
        Assert.Equal(subset, day.Projects[projectId].PerModel["test-model"].LongContext);
    }

    [Fact]
    public void PricesArchivedDaysAtTodaysRates_SoANewRateReachesThemToo()
    {
        UsageReport ParseOne(string date, string model)
        {
            using var tmp = new TempDir();
            WriteLines(tmp.Combine(Dir, "s1.jsonl"), Assistant($"{date}T10:00:00Z", $"msg_{date}", model: model, output: 1_000_000, cwd: Cwd));
            return ClaudeParser.Parse(Options(tmp.Path));
        }

        // Recorded while the model had no rate: counted, but at $0.
        var unpriced = ParseOne("2026-08-01", "brand-new");
        Assert.Equal(0, unpriced.Global.Combined.CostUsd);
        var history = HistoryArchive.Merge(new HistoryFile(), unpriced, Now);

        // The transcript is gone; the model now has a rate.
        var later = ParseOne("2026-08-02", "test-model");
        HistoryArchive.Merge(history, later, Now);
        var pricing = ModelSettings.WithCustomRates(Pricing(), new Dictionary<string, ModelRate> { ["brand-new"] = new(10, 12.5, 20, 1, 7) });
        var restored = HistoryArchive.Apply(later, history, Now.ToUnixTimeMilliseconds(), pricing);

        var archived = restored.Daily.Single(d => d.Date == "2026-08-01");
        Assert.Equal(7, archived.PerModel["brand-new"].CostUsd, 9);
        Assert.False(archived.PerModel["brand-new"].Unpriced);
        Assert.Equal(7 + 50, restored.Global.Combined.CostUsd, 9);
        Assert.Equal(7 + 50, restored.Projects[0].Combined.CostUsd, 9);
        // The archive itself still says what it said.
        Assert.Equal(0, history.Days["2026-08-01"].Combined.CostUsd);
        // And the project's day-derived activity follows the archive.
        Assert.Equal(2, restored.Projects[0].Activity?.ActiveDays);
    }
}
