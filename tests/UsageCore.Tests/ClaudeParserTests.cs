using UsageCore.Parsing;
using static UsageCore.Tests.Fixtures;

namespace UsageCore.Tests;

public class ClaudeParserTests
{
    private const string Cwd = @"C:\Users\you\Projects\My App";
    private static readonly string Dir = ClaudeParser.EncodeProjectDir(Cwd); // C--Users-you-Projects-My-App

    private static Model.UsageReport Parse(string root) => ClaudeParser.Parse(Options(root));

    [Fact]
    public void EncodesProjectDirectoriesLikeClaudeCode()
    {
        Assert.Equal("C--Users-you-Projects-My-App", Dir);
    }

    [Fact]
    public void Trap1_CountsAMessageOnce_HoweverManyLinesCarryIt()
    {
        using var tmp = new TempDir();
        // The same message twice in one file (streaming partials) and once in
        // another project (a resumed session replays history).
        var line = Assistant("2026-08-01T10:00:00Z", "msg_a", input: 100, output: 20, cwd: Cwd);
        WriteLines(tmp.Combine(Dir, "s1.jsonl"), line, line);
        WriteLines(tmp.Combine(Dir + "-copy", "s2.jsonl"), line);

        var report = Parse(tmp.Path);
        Assert.Equal(1, report.Diagnostics.UniqueMessages);
        Assert.Equal(2, report.Diagnostics.DuplicateLinesSkipped);
        Assert.Equal(1, report.Global.Combined.Messages);
        Assert.Equal(120, report.Global.Combined.TotalTokens);
        // The copy contributed nothing, so it is hidden rather than a row of zeroes.
        Assert.Equal([Dir + "-copy"], report.Diagnostics.EmptyProjectsHidden);
        Assert.Single(report.Projects);
    }

    [Fact]
    public void Trap2_KeepsTheLargestOutputTokensSeenForAMessage()
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine(Dir, "s1.jsonl"),
            Assistant("2026-08-01T10:00:00Z", "msg_a", input: 1000, output: 2),
            Assistant("2026-08-01T10:00:01Z", "msg_a", input: 1000, output: 500));

        var report = Parse(tmp.Path);
        Assert.Equal(500, report.Global.Combined.Output);
        Assert.Equal(1, report.Diagnostics.OutputTokensRecovered);
    }

    [Fact]
    public void Trap3_FindsSubagentTranscriptsNestedUnderASession()
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine(Dir, "parent.jsonl"), Assistant("2026-08-01T10:00:00Z", "msg_parent", output: 10, cwd: Cwd));
        WriteLines(tmp.Combine(Dir, "parent", "subagents", "agent-1.jsonl"), Assistant("2026-08-01T10:01:00Z", "msg_child", output: 7, cwd: Cwd));

        var report = Parse(tmp.Path);
        Assert.Equal(2, report.Diagnostics.FilesScanned);
        Assert.Equal(2, report.Activity.Sessions);
        Assert.Equal(1, report.Activity.SubagentSessions);
        Assert.Equal(17, report.Global.Combined.Output);
        Assert.Equal("parent", report.Projects[0].Sessions.Single(s => s.IsSubagent).ParentSessionId);
    }

    [Fact]
    public void Trap4_PricesCacheWritesByTtl_FallingBackToThe5mRate()
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine(Dir, "split.jsonl"), Assistant("2026-08-01T10:00:00Z", "msg_a", cacheWrite5m: 1_000_000, cacheWrite1h: 1_000_000));
        WriteLines(tmp.Combine(Dir + "-flat", "flat.jsonl"), Assistant("2026-08-01T10:00:00Z", "msg_b", flatCacheWrite: 1_000_000));

        var cell = Parse(tmp.Path).Global.Combined;
        Assert.Equal(2_000_000, cell.CacheWrite5m);
        Assert.Equal(1_000_000, cell.CacheWrite1h);
        // 12.50 + 20.00 for the split file, 12.50 for the flat one.
        Assert.Equal(45, Math.Round(cell.CostUsd, 2));
    }

    [Fact]
    public void Trap4_ClaudeCellsDoNotClaimAMeasuredReasoningZero()
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine(Dir, "s1.jsonl"), Assistant("2026-08-01T10:00:00Z", "msg_a", output: 10));
        Assert.Null(Parse(tmp.Path).Global.Combined.Reasoning);
    }

    [Fact]
    public void SurvivesJsonThatIsNotAnObject_AndKeepsReadingTheFile()
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine(Dir, "s1.jsonl"),
            Assistant("2026-08-01T10:00:00Z", "msg_a", output: 10),
            "null", "42", "[1,2,3]", "{\"truncated\":",
            Assistant("2026-08-01T10:00:05Z", "msg_b", output: 5));

        var report = Parse(tmp.Path);
        Assert.Equal(4, report.Diagnostics.LinesUnparseable);
        Assert.Equal(0, report.Diagnostics.FilesFailed);
        Assert.Equal(2, report.Global.Combined.Messages);
        Assert.Equal(15, report.Global.Combined.Output);
        Assert.Empty(report.Diagnostics.Warnings);
    }

    [Fact]
    public void DoesNotThrowOnAnAbsurdTimestamp_AndBucketsItAsUndated()
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine(Dir, "s1.jsonl"),
            Assistant("2026-08-01T10:00:00Z", "msg_a", output: 10),
            Assistant("+275760-09-13T00:00:00Z", "msg_future", output: 3),
            Assistant("1969-01-01T00:00:00Z", "msg_ancient", output: 1));

        var report = Parse(tmp.Path);
        Assert.Equal(2, report.Diagnostics.ImplausibleTimestamps);
        Assert.Equal(14, report.Global.Combined.Output);
        Assert.Equal(4, report.Daily.Single(d => d.Date == Dates.UnknownDate).Combined.Output);
        Assert.Equal(1, report.Activity.ActiveDays);
    }

    [Fact]
    public void RefusesNegativeAndFractionalTokenCounts()
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine(Dir, "s1.jsonl"), Assistant("2026-08-01T10:00:00Z", "msg_a", input: -5_000_000_000, output: 10.7));

        var cell = Parse(tmp.Path).Global.Combined;
        Assert.Equal(0, cell.Input);
        Assert.Equal(10, cell.Output);
        Assert.True(cell.CostUsd > 0);
    }

    [Fact]
    public void SumsRuntimeBetweenLines_ButDropsGapsPastTheIdleCutoff()
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine(Dir, "s1.jsonl"),
            Assistant("2026-08-01T10:00:00Z", "msg_a", output: 1),
            Plain("2026-08-01T10:00:30Z"), // +30s, counted
            Plain("2026-08-01T12:00:00Z"), // ~2h, dropped
            Plain("2026-08-01T12:00:10Z")); // +10s, counted

        var report = Parse(tmp.Path);
        Assert.Equal(40, report.Global.Combined.RuntimeSeconds);
        var session = report.Projects[0].Sessions[0];
        Assert.Equal(40, session.RuntimeSeconds);
        Assert.Equal(7210, session.SpanSeconds);
    }

    [Fact]
    public void NamesAProjectAfterItsOwnDirectory_NotAReplayedPath()
    {
        using var tmp = new TempDir();
        const string other = @"C:\Users\you\Projects\Project A";
        // The replayed lines come first AND are the majority.
        WriteLines(tmp.Combine(Dir, "s1.jsonl"),
            Assistant("2026-08-01T10:00:00Z", "msg_a", output: 1, cwd: other),
            Assistant("2026-08-01T10:00:01Z", "msg_b", output: 1, cwd: other),
            Assistant("2026-08-01T10:00:02Z", "msg_c", output: 1, cwd: Cwd));

        var project = Parse(tmp.Path).Projects[0];
        Assert.Equal("My App", project.Name);
        Assert.Equal(Cwd, project.Cwd);
    }

    [Fact]
    public void ReportsAModelWithNoRateCardEntryAsUnpriced()
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine(Dir, "s1.jsonl"), Assistant("2026-08-01T10:00:00Z", "msg_a", model: "brand-new-model", output: 1_000_000));

        var report = Parse(tmp.Path);
        Assert.Equal(["brand-new-model"], report.Diagnostics.UnpricedModels);
        Assert.Equal(0, report.Global.Combined.CostUsd);
        Assert.True(report.Global.PerModel["brand-new-model"].Unpriced);
    }

    [Fact]
    public void RecordsThePeakChatOverAChatAndItsSubagents_ButNotTheirRuntimeTwice()
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine(Dir, "parent.jsonl"),
            Assistant("2026-08-01T10:00:00Z", "msg_p", output: 100, cwd: Cwd),
            Plain("2026-08-01T10:01:00Z", Cwd)); // 60s of parent runtime
        WriteLines(tmp.Combine(Dir, "parent", "subagents", "agent-1.jsonl"),
            Assistant("2026-08-01T10:00:10Z", "msg_c", output: 50, cwd: Cwd),
            Plain("2026-08-01T10:00:50Z", Cwd)); // 40s, inside the parent's clock

        var activity = Parse(tmp.Path).Activity;
        Assert.Equal("parent", activity.PeakSession?.SessionId);
        Assert.Equal(150, activity.PeakSession?.TotalTokens);
        Assert.Equal(1, activity.PeakSession?.SubagentThreads);
        Assert.False(activity.PeakSession?.IsSubagent);
        Assert.Equal(60, activity.LongestSession?.RuntimeSeconds);
    }

    [Fact]
    public void MergeRulesFoldARenamedDirectoryIntoItsTarget_WithoutChangingTheTotal()
    {
        using var tmp = new TempDir();
        const string oldCwd = @"C:\Users\you\Projects\Old Name";
        var oldDir = ClaudeParser.EncodeProjectDir(oldCwd);
        WriteLines(tmp.Combine(Dir, "s1.jsonl"), Assistant("2026-08-01T10:00:00Z", "msg_a", output: 10, cwd: Cwd));
        WriteLines(tmp.Combine(oldDir, "s2.jsonl"), Assistant("2026-08-01T11:00:00Z", "msg_b", output: 5, cwd: oldCwd));

        var unmerged = Parse(tmp.Path);
        var merged = ClaudeParser.Parse(Options(tmp.Path, projects: new Config.ProjectConfig(
            new Dictionary<string, string> { [oldDir] = Dir },
            new Dictionary<string, string> { [Dir] = "Renamed" })));

        Assert.Equal(unmerged.Global.Combined.CostUsd, merged.Global.Combined.CostUsd);
        var project = Assert.Single(merged.Projects);
        Assert.Equal("Renamed", project.Name);
        Assert.Equal(Cwd, project.Cwd);
        Assert.Equal([oldDir], project.MergedFrom);
    }

    [Fact]
    public void SkipsAnUnreadableDirectoryWithoutFailingTheRun()
    {
        using var tmp = new TempDir();
        var report = Parse(tmp.Combine("does-not-exist"));
        Assert.Empty(report.Projects);
        Assert.Equal(0, report.Global.Combined.CostUsd);
        var warning = Assert.Single(report.Diagnostics.Warnings);
        Assert.Contains("Cannot read projects directory", warning);
    }
}
