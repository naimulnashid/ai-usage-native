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

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "version": 2, "days": { "2026-07-01": { "combined": { "messages": 9 } } } }""")]
    [InlineData("[]")]
    public void AnArchiveThatCannotBeReadIsNeverWrittenOver(string content)
    {
        using var tmp = new TempDir();
        var file = HistoryArchive.PathFor(ProviderId.Claude, tmp.Path);
        File.WriteAllText(file, content);

        var report = HistoryArchive.WithHistory(ReportFor(("2026-08-01", 1_000_000)), tmp.Path);

        Assert.Equal(content, File.ReadAllText(file));
        Assert.Contains(report.Diagnostics.Warnings, w => w.Contains("left as it is"));
        Assert.Equal("2026-08-01", Assert.Single(report.Daily).Date);
        Assert.False(File.Exists(file + ".lock"));
    }

    [Fact]
    public void UnreadableDaysAreCopiedAsideBeforeTheArchiveIsSavedWithoutThem()
    {
        using var tmp = new TempDir();
        var file = HistoryArchive.PathFor(ProviderId.Claude, tmp.Path);
        var original = """
            { "version": 1, "days": {
                "2026-07-01": { "combined": { "messages": 1, "totalTokens": 5 }, "perModel": {} },
                "2026-07-02": "damaged"
            } }
            """;
        File.WriteAllText(file, original);

        var report = HistoryArchive.WithHistory(ReportFor(("2026-08-01", 1_000_000)), tmp.Path);

        var backup = Assert.Single(Directory.GetFiles(tmp.Path, "claude-history.json.bak-*"));
        Assert.Equal(original, File.ReadAllText(backup));
        Assert.Equal(["2026-07-01", "2026-08-01"], HistoryArchive.Load(file, []).Days.Keys);
        Assert.Equal(2, report.Daily.Count);
    }

    [Fact]
    public async Task AWriterWaitsForTheLock_AndKeepsWhatTheOtherWriterSaved()
    {
        using var tmp = new TempDir();
        var file = HistoryArchive.PathFor(ProviderId.Claude, tmp.Path);
        var mine = ReportFor(("2026-08-02", 1_000_000));
        var theirs = HistoryArchive.Merge(new HistoryFile(), ReportFor(("2026-08-01", 1_000_000)), Now);

        Task<UsageReport> writer;
        using (new FileStream(file + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
        {
            writer = Task.Run(() => HistoryArchive.WithHistory(mine, tmp.Path), TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.False(writer.IsCompleted);
            // The other process commits while this one waits.
            HistoryArchive.Save(theirs, file, []);
        }

        var report = await writer.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(["2026-08-01", "2026-08-02"], HistoryArchive.Load(file, []).Days.Keys);
        Assert.Equal(2, report.Daily.Count);
    }

    /// <summary>One message at each instant, parsed with the given day boundary.</summary>
    private static UsageReport ParseAt(double offsetHours, params string[] timestamps)
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine(Dir, "s1.jsonl"),
            timestamps.Select((ts, i) => (object)Assistant(ts, $"msg_{i}", output: 1_000, cwd: Cwd)).ToArray());
        var options = Options(tmp.Path);
        return ClaudeParser.Parse(new ParseOptions
        {
            Root = options.Root,
            Pricing = options.Pricing,
            Settings = Settings() with { LocalUtcOffsetHours = offsetHours },
            ProjectConfig = options.ProjectConfig,
            Now = options.Now,
        });
    }

    [Theory]
    // The 23:30 message moves from 08-02 to 08-03; 08-02 shrinks but must not keep it.
    [InlineData("2026-08-01T12:00:00Z", "2026-08-02T12:00:00Z", "2026-08-02T23:30:00Z", "2026-08-03T12:00:00Z")]
    // 08-02 empties entirely and must go.
    [InlineData("2026-08-01T12:00:00Z", "2026-08-02T23:30:00Z", "2026-08-03T12:00:00Z")]
    public void ADayBoundaryChangeDoesNotCountMessagesTwice(params string[] timestamps)
    {
        var before = ParseAt(0, timestamps);
        var history = HistoryArchive.Merge(new HistoryFile(), before, Now);
        Assert.Equal(0, history.LocalUtcOffsetHours);

        var after = ParseAt(1, timestamps);
        HistoryArchive.Merge(history, after, Now);
        var report = HistoryArchive.Apply(after, history, Now.ToUnixTimeMilliseconds());

        Assert.Equal(timestamps.Length, report.Global.Combined.Messages);
        Assert.Equal(before.Global.Combined.TotalTokens, report.Global.Combined.TotalTokens);
        Assert.Equal(1, history.LocalUtcOffsetHours);
        Assert.Contains(after.Diagnostics.Warnings, w => w.Contains("UTC+0 to UTC+1"));
    }

    [Fact]
    public void ADayBoundaryChangeKeepsDaysOlderThanTheTranscripts()
    {
        var history = HistoryArchive.Merge(new HistoryFile(), ParseAt(0, "2026-07-01T12:00:00Z", "2026-08-01T12:00:00Z"), Now);
        HistoryArchive.Merge(history, ParseAt(1, "2026-08-01T12:00:00Z"), Now);
        Assert.Equal(["2026-07-01", "2026-08-01"], history.Days.Keys);
    }
}
