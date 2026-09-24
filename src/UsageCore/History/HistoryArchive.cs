using System.Text.Json;
using System.Text.Json.Nodes;
using UsageCore.Model;
using UsageCore.Parsing;

namespace UsageCore.History;

public sealed class ArchivedDay
{
    public Dictionary<string, UsageCell> PerModel { get; init; } = new(StringComparer.Ordinal);
    public UsageCell Combined { get; init; } = new();
    public Dictionary<string, UsageBucket> Projects { get; init; } = new(StringComparer.Ordinal);
}

public sealed record ProjectMeta(string Name, string? Cwd);

public sealed class HistoryFile
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public string UpdatedAt { get; set; } = "";

    /// <summary>Kept so a project whose transcripts are all gone can still be named.</summary>
    public Dictionary<string, ProjectMeta> ProjectMeta { get; init; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, ArchivedDay> Days { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A durable daily archive, so the app outlives the transcripts it reads.
/// </summary>
/// <remarks>
/// <para>Both agents delete their own transcripts (Claude Code after
/// <c>cleanupPeriodDays</c>, 30 by default; Codex prunes <c>sessions/</c>). A
/// deleted transcript silently removes a day from the charts, and nothing
/// distinguishes "no work that day" from "that file is gone". Every parse folds
/// its days in here, then the report is rebuilt from the archive.</para>
/// <para><b>One file per agent</b>: separate project id spaces and separate rate
/// cards. <b>Numbers only</b> - no message content. <b>The merge is one-way</b>:
/// a day is replaced only when the fresh parse has at least as many messages,
/// so today can grow while a day whose transcripts were partly deleted cannot
/// overwrite its fuller record. <b>Fails soft</b>: an unreadable archive is an
/// empty one plus a warning.</para>
/// <para>Not archived: the hour histogram and session lists, which cannot be
/// rebuilt from daily aggregates.</para>
/// </remarks>
public static class HistoryArchive
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(ProviderId provider, string? historyDir = null) =>
        System.IO.Path.Combine(historyDir ?? AppPaths.HistoryDir, provider == ProviderId.Claude ? "claude-history.json" : "codex-history.json");

    public static HistoryFile Load(string file, List<string> warnings)
    {
        if (!File.Exists(file)) return new HistoryFile();
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            if (root is null || (int?)Num(root["version"]) != HistoryFile.CurrentVersion || root["days"] is not JsonObject days)
            {
                warnings.Add($"History archive at {file} has an unexpected shape; ignoring it.");
                return new HistoryFile();
            }

            var history = new HistoryFile
            {
                UpdatedAt = root["updatedAt"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "",
            };
            if (root["projectMeta"] is JsonObject meta)
            {
                foreach (var (id, node) in meta)
                {
                    if (node is JsonObject m && m["name"] is JsonValue nv && nv.TryGetValue<string>(out var name))
                    {
                        history.ProjectMeta[id] = new ProjectMeta(name, m["cwd"] is JsonValue cv && cv.TryGetValue<string>(out var cwd) ? cwd : null);
                    }
                }
            }
            foreach (var (date, day) in SanitizeDays(days, file, warnings)) history.Days[date] = day;
            return history;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            warnings.Add($"Could not read the history archive at {file}: {ex.Message}");
            return new HistoryFile();
        }
    }

    private static double Num(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : 0;

    private static UsageCell? NormalizeCell(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        return new UsageCell
        {
            Input = (long)Num(o["input"]),
            Output = (long)Num(o["output"]),
            CacheRead = (long)Num(o["cacheRead"]),
            CacheWrite5m = (long)Num(o["cacheWrite5m"]),
            CacheWrite1h = (long)Num(o["cacheWrite1h"]),
            Reasoning = o["reasoning"] is null ? null : (long)Num(o["reasoning"]),
            Messages = (long)Num(o["messages"]),
            RuntimeSeconds = Num(o["runtimeSeconds"]),
            TotalTokens = (long)Num(o["totalTokens"]),
            CostUsd = Num(o["costUsd"]),
            Unpriced = o["unpriced"] is JsonValue u && u.TryGetValue<bool>(out var b) && b,
        };
    }

    private static UsageBucket? NormalizeBucket(JsonNode? node)
    {
        if (node is not JsonObject o || NormalizeCell(o["combined"]) is not { } combined) return null;
        var perModel = new Dictionary<string, UsageCell>(StringComparer.Ordinal);
        if (o["perModel"] is JsonObject models)
        {
            foreach (var (model, cell) in models)
            {
                if (NormalizeCell(cell) is { } normalized) perModel[model] = normalized;
            }
        }
        return new UsageBucket { PerModel = perModel, Combined = combined };
    }

    /// <summary>
    /// Validates the archive day by day. The file is on the user's disk and can
    /// be hand-edited or truncated: a day that cannot be read is dropped with a
    /// warning, missing numbers become 0, and the rest still loads.
    /// </summary>
    public static Dictionary<string, ArchivedDay> SanitizeDays(JsonObject? days, string file, List<string> warnings)
    {
        var result = new Dictionary<string, ArchivedDay>(StringComparer.Ordinal);
        var dropped = new List<string>();
        if (days is null) return result;

        foreach (var (date, node) in days)
        {
            if (!Dates.IsDayKey(date) || NormalizeBucket(node) is not { } bucket)
            {
                dropped.Add(date);
                continue;
            }
            var projects = new Dictionary<string, UsageBucket>(StringComparer.Ordinal);
            if (node is JsonObject o && o["projects"] is JsonObject ps)
            {
                foreach (var (id, project) in ps)
                {
                    if (NormalizeBucket(project) is { } normalized) projects[id] = normalized;
                }
            }
            result[date] = new ArchivedDay { PerModel = bucket.PerModel, Combined = bucket.Combined, Projects = projects };
        }

        if (dropped.Count > 0)
        {
            var shown = string.Join(", ", dropped.Take(5)) + (dropped.Count > 5 ? ", …" : "");
            warnings.Add($"Skipped {dropped.Count} unreadable day{(dropped.Count == 1 ? "" : "s")} in the history archive at {file} ({shown}).");
        }
        return result;
    }

    /// <summary>Atomic write: a crash mid-save cannot leave a truncated archive.</summary>
    public static void Save(HistoryFile history, string file, List<string> warnings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(history, WriteOptions));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not write the history archive at {file}: {ex.Message}");
        }
    }

    private static Dictionary<string, UsageCell> CloneCells(Dictionary<string, UsageCell> cells) =>
        cells.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.Ordinal);

    /// <summary>Folds a fresh report into the archive (see the class remarks on direction).</summary>
    public static HistoryFile Merge(HistoryFile history, UsageReport report, DateTimeOffset now)
    {
        var projectDays = report.Projects.ToDictionary(
            p => p.Id,
            p => p.Daily.ToDictionary(d => d.Date, StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var entry in report.Daily)
        {
            if (entry.Date == Dates.UnknownDate) continue;
            if (history.Days.TryGetValue(entry.Date, out var existing) && existing.Combined.Messages > entry.Combined.Messages) continue;

            var projects = new Dictionary<string, UsageBucket>(StringComparer.Ordinal);
            foreach (var project in report.Projects)
            {
                if (projectDays[project.Id].TryGetValue(entry.Date, out var day))
                {
                    projects[project.Id] = new UsageBucket { PerModel = CloneCells(day.PerModel), Combined = day.Combined.Clone() };
                }
            }
            history.Days[entry.Date] = new ArchivedDay
            {
                PerModel = CloneCells(entry.PerModel),
                Combined = entry.Combined.Clone(),
                Projects = projects,
            };
        }

        foreach (var project in report.Projects)
        {
            history.ProjectMeta[project.Id] = new ProjectMeta(project.Name, project.Cwd);
        }
        history.Version = HistoryFile.CurrentVersion;
        history.UpdatedAt = now.ToString("O");
        return history;
    }

    private static UsageBucket RollUp(IEnumerable<(Dictionary<string, UsageCell> PerModel, UsageCell Combined)> buckets, bool tracksReasoning)
    {
        var perModel = new Dictionary<string, UsageCell>(StringComparer.Ordinal);
        var combined = UsageCell.Empty(tracksReasoning);
        foreach (var (models, cell) in buckets)
        {
            combined.AddCell(cell);
            foreach (var (model, modelCell) in models)
            {
                if (!perModel.TryGetValue(model, out var target))
                {
                    target = UsageCell.Empty(tracksReasoning);
                    perModel[model] = target;
                }
                target.AddCell(modelCell);
            }
        }
        return new UsageBucket
        {
            PerModel = perModel.OrderByDescending(kv => kv.Value.TotalTokens).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            Combined = combined,
        };
    }

    /// <summary>
    /// Rebuilds the report from the archive, which by now holds the best known
    /// version of every day, including the ones just parsed.
    /// </summary>
    public static UsageReport Apply(UsageReport report, HistoryFile history, long nowMs)
    {
        if (history.Days.Count == 0) return report;
        var tracksReasoning = report.Provider == ProviderId.Codex;
        var archivedDates = history.Days.Keys.ToList(); // SortedDictionary: already in order
        var liveDates = report.Daily.Select(d => d.Date).Where(d => d != Dates.UnknownDate).ToHashSet(StringComparer.Ordinal);

        var daily = archivedDates
            .Select(date => new DailyEntry { Date = date, PerModel = history.Days[date].PerModel, Combined = history.Days[date].Combined })
            .ToList();
        // The undated bucket cannot be archived by day; carry it through.
        if (report.Daily.FirstOrDefault(d => d.Date == Dates.UnknownDate) is { } unknown) daily.Add(unknown);

        var global = RollUp(daily.Select(d => (d.PerModel, d.Combined)), tracksReasoning);

        var live = report.Projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var ids = new HashSet<string>(report.Projects.Select(p => p.Id), StringComparer.Ordinal);
        foreach (var date in archivedDates) ids.UnionWith(history.Days[date].Projects.Keys);

        var projects = ids.Select(id =>
            {
                live.TryGetValue(id, out var liveProject);
                var projectDaily = new List<DailyEntry>();
                foreach (var date in archivedDates)
                {
                    if (history.Days[date].Projects.TryGetValue(id, out var bucket))
                    {
                        projectDaily.Add(new DailyEntry { Date = date, PerModel = bucket.PerModel, Combined = bucket.Combined });
                    }
                }
                if (liveProject?.Daily.FirstOrDefault(d => d.Date == Dates.UnknownDate) is { } unknownDay) projectDaily.Add(unknownDay);

                var rolled = RollUp(projectDaily.Select(d => (d.PerModel, d.Combined)), tracksReasoning);
                history.ProjectMeta.TryGetValue(id, out var meta);
                return new ProjectSummary
                {
                    Id = id,
                    Cwd = liveProject?.Cwd ?? meta?.Cwd,
                    Name = liveProject?.Name ?? meta?.Name ?? id,
                    MergedFrom = liveProject?.MergedFrom ?? [],
                    PerModel = rolled.PerModel,
                    Combined = rolled.Combined,
                    Daily = projectDaily,
                    // Sessions cannot be rebuilt from aggregates.
                    Sessions = liveProject?.Sessions ?? [],
                };
            })
            .Where(p => p.Combined.Messages > 0 || p.Combined.TotalTokens > 0)
            .OrderByDescending(p => p.Combined.CostUsd)
            .ToList();

        var activeDates = daily.Select(d => d.Date).Where(d => d != Dates.UnknownDate).ToList();
        var (current, longest) = Dates.Streaks(activeDates, Dates.LocalDate(nowMs, report.Settings.LocalUtcOffsetHours));
        var archivedOnly = archivedDates.Count(d => !liveDates.Contains(d));
        var a = report.Activity;

        return new UsageReport
        {
            Provider = report.Provider,
            GeneratedAt = report.GeneratedAt,
            TranscriptsDir = report.TranscriptsDir,
            Settings = report.Settings,
            PricingLastVerified = report.PricingLastVerified,
            PricingSource = report.PricingSource,
            Diagnostics = report.Diagnostics,
            Activity = new ActivityStats
            {
                Sessions = a.Sessions,
                SubagentSessions = a.SubagentSessions,
                Messages = global.Combined.Messages,
                TotalTokens = global.Combined.TotalTokens,
                ActiveDays = activeDates.Count,
                CurrentStreakDays = current,
                LongestStreakDays = longest,
                PeakHour = a.PeakHour,
                HourHistogram = a.HourHistogram,
                FavoriteModel = a.FavoriteModel,
                PeakSession = a.PeakSession,
                LongestSession = a.LongestSession,
            },
            Global = global,
            Daily = daily,
            Projects = projects,
            Coverage = new HistoryCoverage
            {
                EarliestDate = archivedDates.FirstOrDefault(),
                LatestDate = archivedDates.LastOrDefault(),
                LiveEarliestDate = liveDates.Order(StringComparer.Ordinal).FirstOrDefault(),
                ArchivedOnlyDays = archivedOnly,
            },
        };
    }

    /// <summary>Parse-time hook: fold in, persist, rebuild from the archive.</summary>
    public static UsageReport WithHistory(UsageReport report, string? historyDir = null)
    {
        var warnings = report.Diagnostics.Warnings;
        var file = PathFor(report.Provider, historyDir);
        var history = Load(file, warnings);
        Merge(history, report, DateTimeOffset.UtcNow);
        Save(history, file, warnings);
        return Apply(report, history, report.GeneratedAt.ToUnixTimeMilliseconds());
    }
}
