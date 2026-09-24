using System.Text.Json.Nodes;
using UsageCore.Config;
using UsageCore.Parsing;
using static UsageCore.Tests.Fixtures;

namespace UsageCore.Tests;

public class CodexParserTests
{
    private const string Cwd = "/home/you/projects/my-app";
    private const string OtherCwd = "/home/you/projects/other-app";
    private const string Thread = "00000000-0000-0000-0000-00000000000a";
    private const string Guardian = "00000000-0000-0000-0000-00000000000b";

    private static readonly Model.PricingConfig AliasPricing =
        Pricing(new Dictionary<string, string> { ["codex-auto-review"] = "cheap-model" });

    private static Model.UsageReport Parse(string home) => CodexParser.Parse(Options(home, AliasPricing));

    private static void Rollout(TempDir tmp, string threadId, params object[] lines) =>
        WriteLines(tmp.Combine("sessions", "2026", "08", "01", Fixtures.Rollout(threadId)), lines);

    [Fact]
    public void Trap1_TakesPerTurnUsageAsTheDelta_AndIgnoresARepeatedReading()
    {
        using var tmp = new TempDir();
        var first = new Totals("2026-08-01T10:00:00Z", 400, 300, 50);
        var second = new Totals("2026-08-01T10:01:00Z", 1000, 800, 100);
        Rollout(tmp, Thread,
            SessionMeta("2026-08-01T10:00:00Z", Cwd),
            TurnContext("2026-08-01T10:00:00Z", "test-model"),
            TokenCount(first),
            TokenCount(second, first),
            // A repeat with a non-zero last_token_usage: summing would double-count.
            TokenCount(second with { Ts = "2026-08-01T10:01:30Z" }, first));

        var report = Parse(tmp.Path);
        Assert.Equal(1, report.Diagnostics.DuplicateLinesSkipped);
        Assert.Equal(2, report.Global.Combined.Messages);
        Assert.Equal(200, report.Global.Combined.Input);
        Assert.Equal(800, report.Global.Combined.CacheRead);
        Assert.Equal(100, report.Global.Combined.Output);
    }

    [Fact]
    public void Trap2_BillsOnlyTheUncachedPartOfThePrompt()
    {
        using var tmp = new TempDir();
        Rollout(tmp, Thread,
            SessionMeta("2026-08-01T10:00:00Z", Cwd),
            TurnContext("2026-08-01T10:00:00Z", "test-model"),
            TokenCount(new Totals("2026-08-01T10:00:10Z", 1_000_000, 800_000, 100_000)));

        var cell = Parse(tmp.Path).Global.Combined;
        Assert.Equal(200_000, cell.Input);
        Assert.Equal(800_000, cell.CacheRead);
        Assert.Equal(1_100_000, cell.TotalTokens);
        // 200k @ $10/M + 800k @ $1/M + 100k @ $50/M
        Assert.Equal(7.8, Math.Round(cell.CostUsd, 2));
    }

    [Fact]
    public void Trap3_CarriesReasoningWithoutAddingItToTotalsOrCost()
    {
        using var tmp = new TempDir();
        Rollout(tmp, Thread,
            SessionMeta("2026-08-01T10:00:00Z", Cwd),
            TurnContext("2026-08-01T10:00:00Z", "test-model"),
            TokenCount(new Totals("2026-08-01T10:00:10Z", 1_000_000, 800_000, 100_000, Reasoning: 50_000)));

        var cell = Parse(tmp.Path).Global.Combined;
        Assert.Equal(50_000, cell.Reasoning);
        Assert.Equal(1_100_000, cell.TotalTokens);
        Assert.Equal(7.8, Math.Round(cell.CostUsd, 2));
    }

    [Fact]
    public void Trap4_CountsAutoReviewThreadsAsTheirOwnBand_PricedByAlias()
    {
        using var tmp = new TempDir();
        Rollout(tmp, Thread,
            SessionMeta("2026-08-01T10:00:00Z", Cwd),
            TurnContext("2026-08-01T10:00:00Z", "test-model"),
            TokenCount(new Totals("2026-08-01T10:00:10Z", 100, 0, 10)));
        Rollout(tmp, Guardian,
            SessionMeta("2026-08-01T10:00:20Z", Cwd, new JsonObject
            {
                ["thread_source"] = "subagent",
                ["parent_thread_id"] = Thread,
                ["source"] = new JsonObject { ["subagent"] = new JsonObject { ["other"] = "guardian" } },
            }),
            TurnContext("2026-08-01T10:00:20Z", "codex-auto-review"),
            TokenCount(new Totals("2026-08-01T10:00:30Z", 1_000_000, 0, 0)));

        var report = Parse(tmp.Path);
        Assert.Equal(2, report.Activity.Sessions);
        Assert.Equal(1, report.Activity.SubagentSessions);
        Assert.Equal(1, Math.Round(report.Global.PerModel["codex-auto-review"].CostUsd, 2));
        Assert.Empty(report.Diagnostics.UnpricedModels);

        var guardian = report.Projects[0].Sessions.Single(s => s.IsSubagent);
        Assert.Equal(Thread, guardian.ParentSessionId);
        Assert.Equal("guardian", guardian.SubagentKind);
    }

    [Fact]
    public void Trap5_TracksTheModelAsStateAcrossTheFile()
    {
        using var tmp = new TempDir();
        Rollout(tmp, Thread,
            SessionMeta("2026-08-01T10:00:00Z", Cwd),
            TurnContext("2026-08-01T10:00:00Z", "test-model"),
            TokenCount(new Totals("2026-08-01T10:00:10Z", 100, 0, 10)),
            TokenCount(new Totals("2026-08-01T10:00:20Z", 200, 0, 20), new Totals("", 100, 0, 10)),
            TurnContext("2026-08-01T10:00:30Z", "cheap-model"),
            TokenCount(new Totals("2026-08-01T10:00:40Z", 300, 0, 30), new Totals("", 200, 0, 20)));

        var report = Parse(tmp.Path);
        Assert.Equal(2, report.Global.PerModel["test-model"].Messages);
        Assert.Equal(1, report.Global.PerModel["cheap-model"].Messages);
        Assert.False(report.Global.PerModel.ContainsKey("(unknown)"));
    }

    [Fact]
    public void TreatsAMidFileCounterResetAsANewBaseline_NotNegativeUsage()
    {
        using var tmp = new TempDir();
        var before = new Totals("2026-08-01T10:00:10Z", 1000, 0, 100);
        Rollout(tmp, Thread,
            SessionMeta("2026-08-01T10:00:00Z", Cwd),
            TurnContext("2026-08-01T10:00:00Z", "test-model"),
            TokenCount(before),
            TokenCount(new Totals("2026-08-01T10:05:00Z", 200, 0, 20), before));

        var report = Parse(tmp.Path);
        Assert.Equal(1, report.Diagnostics.CounterResets);
        Assert.Equal(1200, report.Global.Combined.Input);
        Assert.Equal(120, report.Global.Combined.Output);
    }

    [Fact]
    public void FlagsAFileWhoseOwnPerTurnFigureDisagreesWithTheDelta()
    {
        using var tmp = new TempDir();
        Rollout(tmp, Thread,
            SessionMeta("2026-08-01T10:00:00Z", Cwd),
            TurnContext("2026-08-01T10:00:00Z", "test-model"),
            TokenCount(new Totals("2026-08-01T10:00:10Z", 100, 0, 10, LastTotal: 999)));

        var report = Parse(tmp.Path);
        Assert.Equal(1, report.Diagnostics.ReconcileFailures);
        Assert.Equal(0, report.Diagnostics.ReconciledFiles);
    }

    [Fact]
    public void SurvivesJsonThatIsNotAnObject_AndKeepsReadingTheFile()
    {
        using var tmp = new TempDir();
        Rollout(tmp, Thread,
            SessionMeta("2026-08-01T10:00:00Z", Cwd),
            "null",
            TurnContext("2026-08-01T10:00:00Z", "test-model"),
            "[]",
            TokenCount(new Totals("2026-08-01T10:00:10Z", 100, 0, 10)));

        var report = Parse(tmp.Path);
        Assert.Equal(2, report.Diagnostics.LinesUnparseable);
        Assert.Equal(0, report.Diagnostics.FilesFailed);
        Assert.Equal(1, report.Global.Combined.Messages);
        Assert.Equal(10, report.Global.PerModel["test-model"].Output);
    }

    [Fact]
    public void DoesNotThrowOnAnAbsurdTimestamp()
    {
        using var tmp = new TempDir();
        Rollout(tmp, Thread,
            SessionMeta("2026-08-01T10:00:00Z", Cwd),
            TurnContext("2026-08-01T10:00:00Z", "test-model"),
            TokenCount(new Totals("+275760-09-13T00:00:00Z", 100, 0, 10)));

        var report = Parse(tmp.Path);
        Assert.Equal(1, report.Diagnostics.ImplausibleTimestamps);
        Assert.Equal(10, report.Global.Combined.Output);
        Assert.Equal(Dates.UnknownDate, report.Daily.Single(d => d.Combined.Messages > 0).Date);
    }

    [Fact]
    public void KeysProjectsByTheWorkingDirectorySlug()
    {
        using var tmp = new TempDir();
        Rollout(tmp, Thread,
            SessionMeta("2026-08-01T10:00:00Z", Cwd),
            TurnContext("2026-08-01T10:00:00Z", "test-model"),
            TokenCount(new Totals("2026-08-01T10:00:10Z", 100, 0, 10)));

        var report = Parse(tmp.Path);
        Assert.Equal(CodexParser.ProjectIdFromCwd(Cwd), report.Projects[0].Id);
        Assert.Equal("my-app", report.Projects[0].Name);
        Assert.Equal(Model.ProviderId.Codex, report.Provider);
        Assert.Equal("C--Users-me-Thing", CodexParser.ProjectIdFromCwd(@"C:\Users\me\Thing"));
    }

    [Fact]
    public void WarnsRatherThanFailing_WhenThereAreNoRolloutFiles()
    {
        using var tmp = new TempDir();
        var report = Parse(tmp.Path);
        Assert.Empty(report.Projects);
        Assert.Equal(0, report.Diagnostics.FilesScanned);
        Assert.Contains(report.Diagnostics.Warnings, w => w.Contains("No Codex rollout files found"));
    }

    [Fact]
    public void SaysAMergeRuleCycleOnce_NotOncePerEvent()
    {
        using var tmp = new TempDir();
        var a = CodexParser.ProjectIdFromCwd(Cwd);
        var b = CodexParser.ProjectIdFromCwd(OtherCwd);
        var events = Enumerable.Range(0, 40)
            .Select(i => (object)TokenCount(new Totals($"2026-08-01T10:{i:00}:00Z", 100 * (i + 1), 0, 10 * (i + 1))))
            .ToArray();
        Rollout(tmp, Thread, [SessionMeta("2026-08-01T10:00:00Z", Cwd), TurnContext("2026-08-01T10:00:00Z", "test-model"), .. events]);
        Rollout(tmp, Guardian, [SessionMeta("2026-08-01T11:00:00Z", OtherCwd), TurnContext("2026-08-01T11:00:00Z", "test-model"), .. events]);

        var report = CodexParser.Parse(Options(tmp.Path, AliasPricing, new ProjectConfig(
            new Dictionary<string, string> { [a] = b, [b] = a },
            new Dictionary<string, string>())));

        Assert.Single(report.Diagnostics.Warnings, w => w.Contains("cycle", StringComparison.OrdinalIgnoreCase));
        Assert.True(report.Global.Combined.TotalTokens > 0);
    }

    [Fact]
    public void ALongestChatIsNeverAnAutoReview()
    {
        using var tmp = new TempDir();
        Rollout(tmp, Thread,
            SessionMeta("2026-08-01T10:00:00Z", Cwd),
            TurnContext("2026-08-01T10:00:00Z", "test-model"),
            TokenCount(new Totals("2026-08-01T10:01:00Z", 100, 0, 10)));
        // The guardian runs far longer than its parent, and inside its clock.
        Rollout(tmp, Guardian,
            SessionMeta("2026-08-01T10:00:00Z", Cwd, new JsonObject { ["thread_source"] = "subagent", ["parent_thread_id"] = Thread }),
            TurnContext("2026-08-01T10:00:00Z", "codex-auto-review"),
            TokenCount(new Totals("2026-08-01T10:20:00Z", 100, 0, 10)),
            TokenCount(new Totals("2026-08-01T10:40:00Z", 200, 0, 20), new Totals("", 100, 0, 10)));

        var longest = Parse(tmp.Path).Activity.LongestSession;
        Assert.Equal(Thread, longest?.SessionId);
        Assert.Equal(60, longest?.RuntimeSeconds);
        Assert.Equal(1, longest?.SubagentThreads);
    }
}
