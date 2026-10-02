using UsageCore.Config;
using UsageCore.History;
using UsageCore.Model;
using UsageCore.Parsing;
using static UsageCore.Tests.Fixtures;

namespace UsageCore.Tests;

/// <summary>Merges and display names set from the Projects page.</summary>
public class ProjectSettingsTests
{
    private const string Cwd = @"C:\Users\you\Projects\My App";
    private const string OtherCwd = @"C:\Users\you\Projects\my-app-native";
    private static readonly string Dir = ClaudeParser.EncodeProjectDir(Cwd);
    private static readonly string OtherDir = ClaudeParser.EncodeProjectDir(OtherCwd);

    [Fact]
    public void SavesANameAndAMerge_ReadsThemBack_AndClearsThem()
    {
        using var tmp = new TempDir();
        ProjectSettings.SaveName(ProviderId.Claude, Dir, "  My App  ", tmp.Path);
        ProjectSettings.SaveMerge(ProviderId.Claude, OtherDir, Dir, tmp.Path, configDir: tmp.Path);

        var loaded = ProjectSettings.Load(ProviderId.Claude, tmp.Path);
        Assert.Equal("My App", loaded.DisplayNames[Dir]);
        Assert.Equal(Dir, loaded.Merge[OtherDir]);
        // The other agent has a file of its own.
        Assert.Empty(ProjectSettings.Load(ProviderId.Codex, tmp.Path).Merge);

        ProjectSettings.SaveName(ProviderId.Claude, Dir, null, tmp.Path);
        ProjectSettings.SaveMerge(ProviderId.Claude, OtherDir, null, tmp.Path, configDir: tmp.Path);
        loaded = ProjectSettings.Load(ProviderId.Claude, tmp.Path);
        Assert.Empty(loaded.DisplayNames);
        Assert.Empty(loaded.Merge);
    }

    [Fact]
    public void ABlankNameClearsIt_AndABadOneIsRefused()
    {
        using var tmp = new TempDir();
        ProjectSettings.SaveName(ProviderId.Claude, Dir, "Mine", tmp.Path);
        ProjectSettings.SaveName(ProviderId.Claude, Dir, "   ", tmp.Path);
        Assert.Empty(ProjectSettings.Load(ProviderId.Claude, tmp.Path).DisplayNames);

        Assert.Throws<ArgumentException>(() => ProjectSettings.SaveName(ProviderId.Claude, Dir, "two\nlines", tmp.Path));
        Assert.Throws<ArgumentException>(() => ProjectSettings.SaveName(ProviderId.Claude, Dir, new string('x', ProjectSettings.MaxNameLength + 1), tmp.Path));
        Assert.Throws<ArgumentException>(() => ProjectSettings.SaveName(ProviderId.Claude, "_comment", "x", tmp.Path));
    }

    [Fact]
    public void RefusesAMergeThatComesBackRoundToItsSource_IncludingThroughProjectsJson()
    {
        using var tmp = new TempDir();
        Assert.Throws<ArgumentException>(() => ProjectSettings.SaveMerge(ProviderId.Claude, Dir, Dir, tmp.Path, configDir: tmp.Path));

        ProjectSettings.SaveMerge(ProviderId.Claude, OtherDir, Dir, tmp.Path, configDir: tmp.Path);
        Assert.Throws<ArgumentException>(() => ProjectSettings.SaveMerge(ProviderId.Claude, Dir, OtherDir, tmp.Path, configDir: tmp.Path));

        // A rule in the hand-written file counts too.
        File.WriteAllText(Path.Combine(tmp.Path, "projects.json"), $$"""{ "merge": { "c": "{{Dir}}" } }""");
        Assert.Throws<ArgumentException>(() => ProjectSettings.SaveMerge(ProviderId.Claude, Dir, "c", tmp.Path, configDir: tmp.Path));
        Assert.Equal(Dir, ProjectSettings.Load(ProviderId.Claude, tmp.Path).Merge[OtherDir]);
    }

    [Fact]
    public void TheAppsEntryWinsOverProjectsJson_KeyByKey()
    {
        var file = new ProjectConfig(
            new Dictionary<string, string> { ["a"] = "b", ["x"] = "y" },
            new Dictionary<string, string> { ["b"] = "From file", ["y"] = "Kept" });
        var app = new ProjectConfig(
            new Dictionary<string, string> { ["a"] = "c" },
            new Dictionary<string, string> { ["b"] = "From app" });
        var combined = ProjectSettings.Combine(file, app);
        Assert.Equal("c", combined.Merge["a"]);
        Assert.Equal("y", combined.Merge["x"]);
        Assert.Equal("From app", combined.DisplayNames["b"]);
        Assert.Equal("Kept", combined.DisplayNames["y"]);
    }

    [Fact]
    public void FailsSoftOnACorruptFile()
    {
        using var tmp = new TempDir();
        File.WriteAllText(ProjectSettings.PathFor(ProviderId.Claude, tmp.Path), "{ not json");
        var loaded = ProjectSettings.Load(ProviderId.Claude, tmp.Path);
        Assert.Empty(loaded.Merge);
        Assert.Empty(loaded.DisplayNames);
    }

    /// <summary>
    /// A merge has to reach the days whose transcripts are gone, or the source
    /// would stay on the list holding just its oldest history.
    /// </summary>
    [Fact]
    public void AMergeAndANameReachArchivedDaysWhoseTranscriptsAreGone()
    {
        using var tmp = new TempDir();
        WriteLines(tmp.Combine(Dir, "s1.jsonl"), Assistant("2026-08-01T10:00:00Z", "msg_a", output: 1_000_000, cwd: Cwd));
        WriteLines(tmp.Combine(OtherDir, "s2.jsonl"), Assistant("2026-08-01T11:00:00Z", "msg_b", output: 2_000_000, cwd: OtherCwd));
        var before = ClaudeParser.Parse(Options(tmp.Path));
        var history = HistoryArchive.Merge(new HistoryFile(), before, Now);
        Assert.Equal(2, before.Projects.Count);

        // Both transcripts are deleted, then the two are merged and renamed.
        Directory.Delete(tmp.Combine(Dir), recursive: true);
        Directory.Delete(tmp.Combine(OtherDir), recursive: true);
        var projects = new ProjectConfig(
            new Dictionary<string, string> { [OtherDir] = Dir },
            new Dictionary<string, string> { [Dir] = "Renamed" });
        var empty = ClaudeParser.Parse(Options(tmp.Path, projects: projects));
        var report = HistoryArchive.Apply(empty, HistoryArchive.Merge(history, empty, Now), Now.ToUnixTimeMilliseconds(), projectConfig: projects);

        var project = Assert.Single(report.Projects);
        Assert.Equal(Dir, project.Id);
        Assert.Equal("Renamed", project.Name);
        Assert.Equal([OtherDir], project.MergedFrom);
        Assert.Equal(before.Global.Combined.CostUsd, project.Combined.CostUsd, 9);
        Assert.Equal(before.Global.Combined.CostUsd, report.Global.Combined.CostUsd, 9);
        Assert.Equal(2, project.Combined.Messages);
    }
}
