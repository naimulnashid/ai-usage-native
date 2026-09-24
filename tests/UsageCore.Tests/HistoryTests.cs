using System.Text.Json.Nodes;
using UsageCore.History;
using UsageCore.Model;
using UsageCore.Parsing;
using static UsageCore.Tests.Fixtures;

namespace UsageCore.Tests;

public class HistoryTests
{
    private const string Cwd = @"C:\Users\you\Projects\My App";
    private static readonly string Dir = ClaudeParser.EncodeProjectDir(Cwd);

    /// <summary>A report covering the given days, one message each.</summary>
    private static UsageReport ReportFor(params (string Date, double Output)[] days)
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine(Dir, "s1.jsonl"),
            days.Select((d, i) => (object)Assistant($"{d.Date}T10:0{i}:00Z", $"msg_{i}_{d.Date}", output: d.Output, cwd: Cwd)).ToArray());
        return ClaudeParser.Parse(Options(tmp.Path));
    }

    [Fact]
    public void RestoresDaysWhoseTranscriptsAreGone_AndSaysHowMany()
    {
        var full = ReportFor(("2026-08-01", 1_000_000), ("2026-08-02", 2_000_000));
        var history = HistoryArchive.Merge(new HistoryFile(), full, Now);
        Assert.Equal(2, history.Days.Count);

        // The first day's transcript is deleted; only the second is on disk.
        var thinned = ReportFor(("2026-08-02", 2_000_000));
        var restored = HistoryArchive.Apply(thinned, HistoryArchive.Merge(history, thinned, Now), Now.ToUnixTimeMilliseconds());

        Assert.Equal(2, restored.Daily.Count);
        Assert.Equal(full.Global.Combined.CostUsd, restored.Global.Combined.CostUsd, 9);
        Assert.Equal(full.Global.Combined.TotalTokens, restored.Global.Combined.TotalTokens);
        Assert.Equal(1, restored.Coverage?.ArchivedOnlyDays);
        Assert.True(restored.Coverage?.Restored);
        Assert.Equal("2026-08-01", restored.Coverage?.EarliestDate);
        Assert.Single(restored.Projects);
    }

    [Fact]
    public void NeverLetsAThinnerParseOverwriteAFullerDay()
    {
        var full = ReportFor(("2026-08-01", 2_000_000));
        var history = HistoryArchive.Merge(new HistoryFile(), full, Now);
        var before = history.Days["2026-08-01"].Combined.CostUsd;

        var partial = ReportFor(("2026-08-01", 1));
        partial.Daily[0].Combined.Messages = 0;
        HistoryArchive.Merge(history, partial, Now);

        Assert.Equal(before, history.Days["2026-08-01"].Combined.CostUsd);
    }

    [Fact]
    public void KeepsTodayGrowing_AnEqualOrLargerParseDoesOverwrite()
    {
        var history = HistoryArchive.Merge(new HistoryFile(), ReportFor(("2026-08-01", 1_000_000)), Now);
        HistoryArchive.Merge(history, ReportFor(("2026-08-01", 1_000_000), ("2026-08-01", 3_000_000)), Now);
        Assert.Equal(4_000_000, history.Days["2026-08-01"].Combined.Output);
    }

    [Fact]
    public void DropsUnreadableDaysInsteadOfThrowing_AndCoercesMissingNumbers()
    {
        var warnings = new List<string>();
        var days = HistoryArchive.SanitizeDays(JsonNode.Parse("""
            {
              "2026-08-01": {
                "combined": { "messages": 2, "totalTokens": 10, "costUsd": 1.5 },
                "perModel": { "test-model": { "messages": 2, "totalTokens": 10, "costUsd": 1.5 } },
                "projects": { "my-app": { "combined": { "totalTokens": 10 }, "perModel": {} } }
              },
              "2026-08-02": { "perModel": {} },
              "2026-08-03": "nonsense",
              "2026-08-04": null
            }
            """) as JsonObject, "history.json", warnings);

        Assert.Equal(["2026-08-01"], days.Keys);
        Assert.Equal(2, days["2026-08-01"].Combined.Messages);
        Assert.Equal(0, days["2026-08-01"].Combined.RuntimeSeconds);
        Assert.False(days["2026-08-01"].Combined.Unpriced);
        Assert.Equal(10, days["2026-08-01"].Projects["my-app"].Combined.TotalTokens);
        Assert.Contains("Skipped 3 unreadable days", Assert.Single(warnings));
    }

    [Fact]
    public void RoundTripsThroughDisk()
    {
        using var tmp = new TempDir();
        var file = tmp.Combine("claude-history.json");
        var warnings = new List<string>();
        var history = HistoryArchive.Merge(new HistoryFile(), ReportFor(("2026-08-01", 1_000_000)), Now);
        HistoryArchive.Save(history, file, warnings);

        var loaded = HistoryArchive.Load(file, warnings);
        Assert.Empty(warnings);
        Assert.Equal(history.Days["2026-08-01"].Combined.CostUsd, loaded.Days["2026-08-01"].Combined.CostUsd);
        Assert.Equal("My App", loaded.ProjectMeta[Dir].Name);
    }

    [Fact]
    public void AnUnreadableArchiveIsAnEmptyOnePlusAWarning()
    {
        using var tmp = new TempDir();
        var file = tmp.Combine("claude-history.json");
        File.WriteAllText(file, "{ not json");
        var warnings = new List<string>();
        Assert.Empty(HistoryArchive.Load(file, warnings).Days);
        Assert.Single(warnings);
    }
}
