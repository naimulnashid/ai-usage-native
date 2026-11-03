using Microsoft.Data.Sqlite;
using UsageCore.Parsing;
using static UsageCore.Parsing.AntigravityFormat;
using static UsageCore.Tests.Fixtures;

namespace UsageCore.Tests;

public class AntigravityParserTests
{
    private const string Workspace = @"C:\Users\you\projects\my-app";
    private const string Chat = "00000000-0000-0000-0000-0000000000a1";
    private const string Helper = "00000000-0000-0000-0000-0000000000b2";
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

    private static Model.UsageReport Parse(string home, Model.PricingConfig? pricing = null) =>
        AntigravityParser.Parse(Options(home, pricing));

    private static string Db(TempDir tmp, string cascadeId, string folder = "antigravity") =>
        tmp.Combine(folder, "conversations", cascadeId + ".db");

    private static DemoRequest Req(int minutes, long input, long output, long cache = 0, long thinking = 0, string model = "test-model") =>
        new(T0.AddMinutes(minutes), model, null, input, output, cache, thinking);

    [Fact]
    public void Trap1_ACopyKeptByBothBuildsIsCountedOnce_TheFullerCopyWinning()
    {
        using var tmp = new TempDir();
        WriteConversation(Db(tmp, Chat, "antigravity-ide"), Workspace, T0, [Req(0, 100, 10)]);
        WriteConversation(Db(tmp, Chat), Workspace, T0, [Req(0, 100, 10), Req(1, 200, 20)]);

        var report = Parse(tmp.Path);
        Assert.Equal(2, report.Global.Combined.Messages);
        Assert.Equal(300, report.Global.Combined.Input);
        Assert.Equal(1, report.Diagnostics.DuplicateLinesSkipped);
        Assert.Single(report.Projects.SelectMany(p => p.Sessions));
    }

    [Fact]
    public void Trap2_InputIsTheUncachedRemainder_AndPricedAsIs()
    {
        using var tmp = new TempDir();
        WriteConversation(Db(tmp, Chat), Workspace, T0, [Req(0, 1_000_000, 0, cache: 2_000_000)]);

        var cell = Parse(tmp.Path).Global.Combined;
        Assert.Equal(1_000_000, cell.Input);
        Assert.Equal(2_000_000, cell.CacheRead);
        // test-model: $10 input, $1 cache read per million.
        Assert.Equal(12, cell.CostUsd, 6);
    }

    [Fact]
    public void Trap3_ThinkingIsInsideOutput_AndNeverAdded()
    {
        using var tmp = new TempDir();
        WriteConversation(Db(tmp, Chat), Workspace, T0, [Req(0, 100, 500, thinking: 300)]);

        var cell = Parse(tmp.Path).Global.Combined;
        Assert.Equal(500, cell.Output);
        Assert.Equal(300, cell.Reasoning);
        Assert.Equal(600, cell.TotalTokens);
    }

    [Fact]
    public void Trap4_ASubagentConversationIsCounted_AndPointsAtItsParent()
    {
        using var tmp = new TempDir();
        WriteConversation(Db(tmp, Chat), Workspace, T0, [Req(0, 100, 10)]);
        WriteConversation(Db(tmp, Helper), Workspace, T0.AddMinutes(1), [Req(1, 50, 5)], parentCascadeId: Chat, subagentType: "research");

        var report = Parse(tmp.Path);
        Assert.Equal(150, report.Global.Combined.Input);
        var helper = report.Projects.SelectMany(p => p.Sessions).Single(s => s.SessionId == Helper);
        Assert.True(helper.IsSubagent);
        Assert.Equal(Chat, helper.ParentSessionId);
        Assert.Equal("research", helper.SubagentKind);
        Assert.Equal(1, report.Activity.SubagentSessions);
        // The record folds the subagent into its parent.
        Assert.Equal(165, report.Activity.PeakSession!.TotalTokens);
    }

    [Fact]
    public void Trap5_AMovingAliasIsNamedByItsDisplayName()
    {
        Assert.Equal("gemini-3.1-pro", AntigravityParser.ModelKey("gemini-pro-default", "Gemini 3.1 Pro (High)"));
        Assert.Equal("gemini-3.7-flash", AntigravityParser.ModelKey("gemini-3.7-flash", "Gemini 3.7 Flash (High)"));
        Assert.Equal("gemini-4-argon", AntigravityParser.ModelKey(null, "Gemini 4 Argon (Medium)"));
        Assert.Equal("gemini-pro-default", AntigravityParser.ModelKey("gemini-pro-default", null));
        Assert.Equal("(unknown)", AntigravityParser.ModelKey(null, null));
    }

    [Fact]
    public void Trap6_ABrowserSubagentsUsageOnItsToolStepIsCounted_UnderTheModelItsNumberNames()
    {
        using var tmp = new TempDir();
        WriteConversation(Db(tmp, Chat), Workspace, T0,
        [
            Req(0, 100, 10) with { ModelNumber = 7, Browser = new DemoSubagent(9, 1000, 100, 2000) },
            Req(2, 100, 10, model: "cheap-model") with { ModelNumber = 9 },
            Req(3, 100, 10) with { ModelNumber = 7, Browser = new DemoSubagent(42, 10, 1, 0) },
        ]);

        var report = Parse(tmp.Path);
        Assert.Equal(5, report.Global.Combined.Messages);
        Assert.Equal(1100, report.Global.PerModel["cheap-model"].Input);
        Assert.True(report.Global.PerModel["(model 42)"].Unpriced);
        Assert.Contains("(model 42)", report.Diagnostics.UnpricedModels);
        // The subagent's usage is not the request's own, so it is not a mismatch.
        Assert.Equal(0, report.Diagnostics.ReconcileFailures);
    }

    [Fact]
    public void ARequestThatNeverRan_CountsNothing()
    {
        using var tmp = new TempDir();
        WriteConversation(Db(tmp, Chat), Workspace, T0, [Req(0, 100, 10), Req(1, 0, 0)]);

        var report = Parse(tmp.Path);
        Assert.Equal(1, report.Global.Combined.Messages);
        Assert.Single(report.Global.PerModel);
    }

    [Fact]
    public void UsageThatDisagreesWithItsStep_FailsReconciliation()
    {
        using var tmp = new TempDir();
        var db = Db(tmp, Chat);
        WriteConversation(db, Workspace, T0, [Req(0, 100, 10), Req(1, 200, 20)]);
        // Steps: 0 the prompt, 1 and 2 the two requests. Swap request 1's step usage.
        Exec(db, "UPDATE steps SET metadata = (SELECT metadata FROM steps WHERE idx = 2) WHERE idx = 1");

        var report = Parse(tmp.Path);
        Assert.Equal(1, report.Diagnostics.ReconcileFailures);
        Assert.Equal(0, report.Diagnostics.ReconciledFiles);
    }

    [Fact]
    public void TheProjectIsTheWorkspace_WithTheDriveInUpperCase()
    {
        Assert.Equal(@"D:\AI\My App", AntigravityParser.LocalPath("file:///d:/AI/My%20App"));
        Assert.Null(AntigravityParser.LocalPath("https://example.com/x"));

        using var tmp = new TempDir();
        WriteConversation(Db(tmp, Chat), Workspace, T0, [Req(0, 100, 10)]);
        WriteConversation(Db(tmp, Helper), null, T0, [Req(0, 100, 10)]);

        var report = Parse(tmp.Path);
        var project = report.Projects.Single(p => p.Cwd == Workspace);
        Assert.Equal("my-app", project.Name);
        Assert.Contains(report.Projects, p => p.Id == "(no workspace)");
    }

    [Fact]
    public void TimesComeFromTheSteps_AndRuntimeSumsTheirGaps()
    {
        using var tmp = new TempDir();
        WriteConversation(Db(tmp, Chat), Workspace, T0, [Req(0, 100, 10), Req(2, 100, 10), Req(120, 100, 10)]);

        var report = Parse(tmp.Path);
        Assert.Equal("2026-08-01", Assert.Single(report.Daily).Date);
        // 0 -> 2 minutes counts; the 118-minute gap is idle.
        Assert.Equal(120, report.Global.Combined.RuntimeSeconds, 6);
    }

    [Fact]
    public void PricesALongPromptAtTheTier_OnInputPlusCache()
    {
        using var tmp = new TempDir();
        WriteConversation(Db(tmp, Chat), Workspace, T0, [Req(0, 400_000, 0, cache: 700_000), Req(1, 400_000, 0, cache: 500_000)]);

        var report = Parse(tmp.Path, CodexParserTests.Tiered());
        Assert.Equal(1, report.Diagnostics.LongContextRequests);
        // Request 1 at the tier (2x input and cache): 4 * 2 + 0.7 * 2; request 2 at base: 4 + 0.5.
        Assert.Equal(13.9, report.Global.Combined.CostUsd, 6);
    }

    [Fact]
    public void AFileThatIsNotAConversation_IsSkippedWithAWarning()
    {
        using var tmp = new TempDir();
        var db = Db(tmp, Chat);
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        Exec(db, "CREATE TABLE other (x integer)");
        File.WriteAllText(Db(tmp, Helper), "not a database");

        var report = Parse(tmp.Path);
        Assert.Equal(2, report.Diagnostics.FilesFailed);
        Assert.Equal(0, report.Global.Combined.Messages);
    }

    [Fact]
    public void ACorruptRequestRowIsSkipped_ItsUsageRecoveredFromItsStep()
    {
        using var tmp = new TempDir();
        var db = Db(tmp, Chat);
        WriteConversation(db, Workspace, T0, [Req(0, 100, 10) with { ModelNumber = 7 }, Req(1, 200, 20) with { ModelNumber = 7 }]);
        Exec(db, "UPDATE gen_metadata SET data = X'0A05FFFF' WHERE idx = 0");

        var report = Parse(tmp.Path);
        Assert.Equal(1, report.Diagnostics.LinesUnparseable);
        // The model step still carries the usage, and its number names the model.
        Assert.Equal(2, report.Global.PerModel["test-model"].Messages);
        Assert.Equal(300, report.Global.Combined.Input);
    }

    private static void Exec(string db, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
