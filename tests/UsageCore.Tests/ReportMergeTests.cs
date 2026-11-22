using UsageCore.Combining;
using UsageCore.Config;
using UsageCore.Model;
using UsageCore.Parsing;

namespace UsageCore.Tests;

/// <summary>The "All agents" report: the agents' reports merged, projects matched by folder.</summary>
public class ReportMergeTests
{
    private static readonly Settings Settings = AppConfig.DefaultSettings() with { LocalUtcOffsetHours = 0 };
    private static readonly long Now = DateTimeOffset.Parse("2026-11-20T12:00:00Z").ToUnixTimeMilliseconds();

    private static UsageCell Cell(double cost, long tokens, long messages = 1, long? reasoning = null) => new()
    {
        Input = tokens,
        TotalTokens = tokens,
        CostUsd = cost,
        Messages = messages,
        RuntimeSeconds = 60,
        Reasoning = reasoning,
    };

    private static ProjectSummary Project(string id, string? cwd, string model, params (string Date, double Cost, long Tokens)[] days)
    {
        var daily = days.Select(d => new DailyEntry
        {
            Date = d.Date,
            PerModel = new() { [model] = Cell(d.Cost, d.Tokens) },
            Combined = Cell(d.Cost, d.Tokens),
        }).ToList();
        var total = new UsageCell();
        foreach (var d in daily) total.AddCell(d.Combined);
        return new ProjectSummary
        {
            Id = id,
            Cwd = cwd,
            Name = cwd?.Split('\\').Last() ?? id,
            PerModel = new() { [model] = total.Clone() },
            Combined = total,
            Daily = daily,
            Sessions = [new SessionSummary { SessionId = $"{id}-s", ProjectId = id, File = "f", CostUsd = total.CostUsd, TotalTokens = total.TotalTokens }],
            Activity = new ActivityStats
            {
                Sessions = 1,
                HourHistogram = Enumerable.Range(0, 24).Select(h => h == 9 ? 1 : 0).ToArray(),
                PeakSession = new SessionRecord { SessionId = $"{id}-s", ProjectId = id, ProjectName = id, TotalTokens = total.TotalTokens },
            },
        };
    }

    private static UsageReport Report(ProviderId provider, params ProjectSummary[] projects)
    {
        var perModel = new Dictionary<string, UsageCell>(StringComparer.Ordinal);
        var combined = new UsageCell();
        var byDate = new SortedDictionary<string, DailyEntry>(StringComparer.Ordinal);
        foreach (var p in projects)
        {
            combined.AddCell(p.Combined);
            foreach (var (m, c) in p.PerModel)
            {
                if (perModel.TryGetValue(m, out var have)) have.AddCell(c);
                else perModel[m] = c.Clone();
            }
            foreach (var d in p.Daily)
            {
                if (!byDate.TryGetValue(d.Date, out var day)) byDate[d.Date] = day = new DailyEntry { Date = d.Date };
                day.Combined.AddCell(d.Combined);
            }
        }
        return new UsageReport
        {
            Provider = provider,
            TranscriptsDir = provider.ToString(),
            Settings = Settings,
            Diagnostics = new ParseDiagnostics { FilesScanned = projects.Length },
            Activity = new ActivityStats
            {
                Sessions = projects.Length,
                HourHistogram = Enumerable.Range(0, 24).Select(h => h == 9 ? projects.Length : 0).ToArray(),
                PeakSession = projects.Select(p => p.Activity!.PeakSession!).MaxBy(r => r.TotalTokens),
            },
            Global = new UsageBucket { PerModel = perModel, Combined = combined },
            Daily = [.. byDate.Values],
            Projects = [.. projects],
        };
    }

    [Fact]
    public void Key_FoldsEveryAgentsIdForTheSameFolder_ToOneForm()
    {
        var cwd = @"D:\AI\Claude Code\my_app.native";
        var claude = ClaudeParser.EncodeProjectDir(cwd);
        var codex = CodexParser.ProjectIdFromCwd(cwd);
        Assert.NotEqual(claude, codex);
        Assert.Equal(ReportMerge.Key(claude), ReportMerge.Key(codex));
        Assert.Equal(ReportMerge.Key(cwd), ReportMerge.Key(codex));
        Assert.Equal("d-ai-claude-code-my-app-native", ReportMerge.Key(cwd));
    }

    [Fact]
    public void TheSameFolderUnderTwoAgents_IsOneProject_AndNoTotalMoves()
    {
        var cwd = @"D:\Work\music.vault";
        var claude = Report(ProviderId.Claude,
            Project(ClaudeParser.EncodeProjectDir(cwd), cwd, "claude-opus-5", ("2026-11-18", 3, 300)),
            Project(ClaudeParser.EncodeProjectDir(@"D:\Work\other"), @"D:\Work\other", "claude-opus-5", ("2026-11-19", 1, 100)));
        var codex = Report(ProviderId.Codex,
            Project(CodexParser.ProjectIdFromCwd(cwd), cwd, "gpt-5.6-sol", ("2026-11-18", 2, 200), ("2026-11-20", 5, 500)));

        var merged = ReportMerge.Merge([codex, claude], nowMs: Now);

        Assert.Equal(ProviderId.Combined, merged.Provider);
        Assert.Equal(11, merged.Global.Combined.CostUsd, 9);
        Assert.Equal(1100, merged.Global.Combined.TotalTokens);
        Assert.Equal(2, merged.Projects.Count);

        var vault = merged.Projects[0];
        Assert.Equal(10, vault.Combined.CostUsd, 9);
        Assert.Equal(ReportMerge.Key(cwd), vault.Id);
        Assert.Equal("music.vault", vault.Name);
        Assert.Equal([ProviderId.Claude, ProviderId.Codex], vault.Sources!.Select(s => s.Provider));
        Assert.Equal([3.0, 7.0], vault.Agents!.Select(a => a.Combined.CostUsd));
        Assert.Equal(["2026-11-18", "2026-11-20"], vault.Daily.Select(d => d.Date));
        Assert.Equal(5, vault.Daily[0].Combined.CostUsd, 9);
        Assert.Equal(2, vault.Daily[0].PerModel.Count);
        Assert.All(vault.Sessions, s => Assert.Equal(vault.Id, s.ProjectId));

        // Counts add, days are recomputed across agents, records keep the biggest.
        Assert.Equal(2, vault.Activity!.Sessions);
        Assert.Equal(2, vault.Activity.ActiveDays);
        Assert.Equal(9, vault.Activity.PeakHour);
        Assert.Equal(vault.Id, vault.Activity.PeakSession!.ProjectId);
        Assert.Equal(700, vault.Activity.PeakSession.TotalTokens);

        Assert.Equal(3, merged.Activity.ActiveDays);
        Assert.Equal(3, merged.Activity.CurrentStreakDays);
        Assert.Equal(3, merged.Activity.Sessions);
        Assert.Equal("gpt-5.6-sol", merged.Activity.FavoriteModel);
        Assert.Equal([ProviderId.Claude, ProviderId.Codex], merged.Agents!.Select(a => a.Provider));
    }

    [Fact]
    public void AMergeMadeOnOneAgentsPage_CarriesOverToTheOthers()
    {
        // Claude Code has "app-old" merged into "app"; Codex still has both apart.
        var app = @"D:\Work\app";
        var old = @"D:\Work\app-old";
        var claudeApp = Project(ClaudeParser.EncodeProjectDir(app), app, "claude-opus-5", ("2026-11-18", 3, 300));
        claudeApp.MergedFrom.Add(ClaudeParser.EncodeProjectDir(old));
        var claude = Report(ProviderId.Claude, claudeApp);
        var codex = Report(ProviderId.Codex,
            Project(CodexParser.ProjectIdFromCwd(app), app, "gpt-5.6-sol", ("2026-11-18", 1, 100)),
            Project(CodexParser.ProjectIdFromCwd(old), old, "gpt-5.6-sol", ("2026-11-19", 1, 100)));

        var merged = ReportMerge.Merge([claude, codex], nowMs: Now);

        var project = Assert.Single(merged.Projects);
        Assert.Equal(5, project.Combined.CostUsd, 9);
        Assert.Equal(3, project.Sources!.Count);
    }

    [Fact]
    public void ThisViewsOwnMergesAndNames_JoinWhatTheFoldersMiss()
    {
        var claude = Report(ProviderId.Claude, Project("D--Work-renamed", @"D:\Work\renamed", "claude-opus-5", ("2026-11-18", 3, 300)));
        var codex = Report(ProviderId.Codex, Project("D--Work-original", @"D:\Work\original", "gpt-5.6-sol", ("2026-11-18", 1, 100)));
        var config = new ProjectConfig(
            new Dictionary<string, string> { ["d-work-original"] = "d-work-renamed" },
            new Dictionary<string, string> { ["d-work-renamed"] = "Renamed App" });

        var merged = ReportMerge.Merge([claude, codex], config, Now);

        var project = Assert.Single(merged.Projects);
        Assert.Equal("d-work-renamed", project.Id);
        Assert.Equal("Renamed App", project.Name);
        Assert.Equal(["d-work-original"], project.MergedFrom);
        Assert.Equal(4, project.Combined.CostUsd, 9);
    }

    [Fact]
    public void AMergeOntoAProjectThatDoesNotExist_IsIgnored()
    {
        var claude = Report(ProviderId.Claude, Project("D--Work-a", @"D:\Work\a", "claude-opus-5", ("2026-11-18", 3, 300)));
        var config = new ProjectConfig(new Dictionary<string, string> { ["d-work-a"] = "gone" }, new Dictionary<string, string>());

        var merged = ReportMerge.Merge([claude], config, Now);

        Assert.Equal("d-work-a", Assert.Single(merged.Projects).Id);
    }

    [Fact]
    public void ReasoningStaysNullWhereNoAgentReportsIt()
    {
        var claude = Report(ProviderId.Claude, Project("D--a", @"D:\a", "claude-opus-5", ("2026-11-18", 3, 300)));
        var merged = ReportMerge.Merge([claude], nowMs: Now);
        Assert.Null(merged.Global.Combined.Reasoning);
    }

    [Fact]
    public void ADayIsArchivedOnly_OnlyWhenNoAgentStillHasItsTranscripts()
    {
        var claude = Report(ProviderId.Claude, Project("D--a", @"D:\a", "claude-opus-5", ("2026-11-01", 1, 10), ("2026-11-02", 1, 10), ("2026-11-10", 1, 10)));
        var codex = Report(ProviderId.Codex, Project("D--b", @"D:\b", "gpt-5.6-sol", ("2026-11-02", 1, 10)));
        claude = WithCoverage(claude, "2026-11-10");
        codex = WithCoverage(codex, "2026-11-02");

        var merged = ReportMerge.Merge([claude, codex], nowMs: Now);

        // 11-01: Claude Code's archive only. 11-02: live for Codex.
        Assert.Equal(1, merged.Coverage!.ArchivedOnlyDays);
        Assert.Equal("2026-11-01", merged.Coverage.EarliestDate);
        Assert.Equal("2026-11-02", merged.Coverage.LiveEarliestDate);
    }

    private static UsageReport WithCoverage(UsageReport r, string liveEarliest) => new()
    {
        Provider = r.Provider,
        TranscriptsDir = r.TranscriptsDir,
        Settings = r.Settings,
        Diagnostics = r.Diagnostics,
        Activity = r.Activity,
        Global = r.Global,
        Daily = r.Daily,
        Projects = r.Projects,
        Coverage = new HistoryCoverage { EarliestDate = r.Daily[0].Date, LatestDate = r.Daily[^1].Date, LiveEarliestDate = liveEarliest },
    };

    [Fact]
    public void AnAgentsWarningsSayWhichAgentTheyCameFrom()
    {
        var codex = Report(ProviderId.Codex, Project("D--a", @"D:\a", "gpt-5.6-sol", ("2026-11-18", 1, 10)));
        codex.Diagnostics.Warnings.Add("something odd");
        var merged = ReportMerge.Merge([codex], nowMs: Now);
        Assert.Contains("Codex: something odd", merged.Diagnostics.Warnings);
    }
}
