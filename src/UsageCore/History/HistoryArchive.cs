using System.Text.Json;
using System.Text.Json.Nodes;
using UsageCore.Config;
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

/// <summary>
/// Source-generated, so saving the archive never depends on reflection: a
/// trimmed build, or a host with reflection-based serialization turned off,
/// must not be what loses a day of history.
/// </summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(HistoryFile))]
internal sealed partial class ArchiveJson : System.Text.Json.Serialization.JsonSerializerContext;

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
            LongContext = o["longContext"] is JsonObject lc
                ? new PricedTokens(
                    (long)Num(lc["input"]),
                    (long)Num(lc["output"]),
                    (long)Num(lc["cacheRead"]),
                    (long)Num(lc["cacheWrite5m"]),
                    (long)Num(lc["cacheWrite1h"]))
                : null,
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
                if (NormalizeCell(cell) is not { } normalized) continue;
                // Archives written before the parser stopped counting
                // <synthetic>: drop its cell and take its messages out of the
                // combined count, so a stored day compares like for like with a
                // fresh parse (the merge keeps whichever has more messages). Its
                // runtime stays in the combined figure - that time was spent.
                if (model == ClaudeParser.SyntheticModel)
                {
                    combined.Messages = Math.Max(0, combined.Messages - normalized.Messages);
                    continue;
                }
                perModel[model] = normalized;
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
            File.WriteAllText(temp, JsonSerializer.Serialize(history, ArchiveJson.Default.HistoryFile));
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
            if (history.Days.TryGetValue(entry.Date, out var existing) && existing.Combined.Messages > entry.Combined.Messages)
            {
                BackfillLongContext(existing.PerModel, existing.Combined, entry.PerModel);
                foreach (var (id, stored) in existing.Projects)
                {
                    if (projectDays.TryGetValue(id, out var days) && days.TryGetValue(entry.Date, out var fresh))
                    {
                        BackfillLongContext(stored.PerModel, stored.Combined, fresh.PerModel);
                    }
                }
                continue;
            }

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

    /// <summary>
    /// Gives a stored day the long-context subset (<see cref="UsageCell.LongContext"/>)
    /// it was written without, from a fresh parse of the same day.
    /// </summary>
    /// <remarks>
    /// Days archived before the subset existed have none, so a re-pricing charges
    /// their long requests at the standard rate. A day the parse rewrites gets it
    /// anyway; this is for the day the merge KEEPS, because its stored copy has
    /// more messages than the transcripts still on disk. Copied per model, and
    /// only when that model's five buckets match exactly - then both cells count
    /// the same requests, so the subset is known rather than guessed.
    /// </remarks>
    private static void BackfillLongContext(Dictionary<string, UsageCell> stored, UsageCell storedCombined, Dictionary<string, UsageCell> fresh)
    {
        var changed = false;
        foreach (var (model, cell) in stored)
        {
            if (cell.LongContext is not null || !fresh.TryGetValue(model, out var source) || source.LongContext is null) continue;
            if (cell.Tokens.Priced != source.Tokens.Priced) continue;
            cell.LongContext = source.LongContext;
            changed = true;
        }
        if (!changed) return;
        PricedTokens? sum = null;
        foreach (var cell in stored.Values)
        {
            if (cell.LongContext is { } add) sum = sum is { } have ? have.Plus(add) : add;
        }
        storedCombined.LongContext = sum;
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
    /// A stored bucket priced at today's rates. The archive keeps the token
    /// counts, so a stored day can be priced again - without this, giving a new
    /// model a rate would fix every live day and leave its archived days at $0.
    /// A model with no rate now keeps what was stored.
    /// </summary>
    private static (Dictionary<string, UsageCell> PerModel, UsageCell Combined) Reprice(
        Dictionary<string, UsageCell> perModel, UsageCell combined, PricingConfig? pricing)
    {
        if (pricing is null) return (perModel, combined);
        var cells = new Dictionary<string, UsageCell>(StringComparer.Ordinal);
        double delta = 0;
        foreach (var (model, cell) in perModel)
        {
            if (AppConfig.GetRate(pricing, model) is not { } rate)
            {
                cells[model] = cell;
                continue;
            }
            var priced = cell.Clone();
            priced.CostUsd = AppConfig.CostOf(cell.Tokens, rate);
            priced.Unpriced = false;
            delta += priced.CostUsd - cell.CostUsd;
            cells[model] = priced;
        }
        var total = combined.Clone();
        total.CostUsd += delta;
        return (cells, total);
    }

    /// <summary>
    /// Rebuilds the report from the archive, which by now holds the best known
    /// version of every day, including the ones just parsed. With <paramref name="projectConfig"/>,
    /// stored days are grouped by today's merge rules and projects are named by
    /// today's display names - see <see cref="RegroupProjects"/>.
    /// </summary>
    public static UsageReport Apply(UsageReport report, HistoryFile history, long nowMs, PricingConfig? pricing = null, ProjectConfig? projectConfig = null)
    {
        if (history.Days.Count == 0) return report;
        var tracksReasoning = report.Provider == ProviderId.Codex;
        var merge = projectConfig?.Merge ?? ProjectConfig.None.Merge;
        var displayNames = projectConfig?.DisplayNames ?? ProjectConfig.None.DisplayNames;
        var foldedIn = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var dayProjects = history.Days.ToDictionary(
            kv => kv.Key,
            kv => RegroupProjects(kv.Value.Projects, merge, foldedIn, report.Diagnostics.Warnings, tracksReasoning),
            StringComparer.Ordinal);
        var archivedDates = history.Days.Keys.ToList(); // SortedDictionary: already in order
        var liveDates = report.Daily.Select(d => d.Date).Where(d => d != Dates.UnknownDate).ToHashSet(StringComparer.Ordinal);

        var daily = archivedDates
            .Select(date =>
            {
                var (perModel, combined) = Reprice(history.Days[date].PerModel, history.Days[date].Combined, pricing);
                return new DailyEntry { Date = date, PerModel = perModel, Combined = combined };
            })
            .ToList();
        // The undated bucket cannot be archived by day; carry it through.
        if (report.Daily.FirstOrDefault(d => d.Date == Dates.UnknownDate) is { } unknown) daily.Add(unknown);

        var global = RollUp(daily.Select(d => (d.PerModel, d.Combined)), tracksReasoning);

        var live = report.Projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var ids = new HashSet<string>(report.Projects.Select(p => p.Id), StringComparer.Ordinal);
        foreach (var date in archivedDates) ids.UnionWith(dayProjects[date].Keys);

        var projects = ids.Select(id =>
            {
                live.TryGetValue(id, out var liveProject);
                var projectDaily = new List<DailyEntry>();
                foreach (var date in archivedDates)
                {
                    if (dayProjects[date].TryGetValue(id, out var bucket))
                    {
                        var (perModel, combined) = Reprice(bucket.PerModel, bucket.Combined, pricing);
                        projectDaily.Add(new DailyEntry { Date = date, PerModel = perModel, Combined = combined });
                    }
                }
                if (liveProject?.Daily.FirstOrDefault(d => d.Date == Dates.UnknownDate) is { } unknownDay) projectDaily.Add(unknownDay);

                var rolled = RollUp(projectDaily.Select(d => (d.PerModel, d.Combined)), tracksReasoning);
                var sources = foldedIn.TryGetValue(id, out var folded) ? folded : [];
                // A target known only through what was folded into it borrows
                // the first source's path and name.
                var meta = history.ProjectMeta.GetValueOrDefault(id)
                           ?? sources.Select(s => history.ProjectMeta.GetValueOrDefault(s)).FirstOrDefault(m => m is not null);
                return new ProjectSummary
                {
                    Id = id,
                    Cwd = liveProject?.Cwd ?? meta?.Cwd,
                    Name = displayNames.TryGetValue(id, out var display) ? display : liveProject?.Name ?? meta?.Name ?? id,
                    MergedFrom = (liveProject?.MergedFrom ?? []).Union(sources, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                    PerModel = rolled.PerModel,
                    Combined = rolled.Combined,
                    Daily = projectDaily,
                    // Sessions cannot be rebuilt from aggregates.
                    Sessions = liveProject?.Sessions ?? [],
                    Activity = ReportShaping.RefreshActivity(liveProject?.Activity, projectDaily, rolled.Combined, report.Settings.LocalUtcOffsetHours, nowMs),
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
            ModelRates = report.ModelRates,
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
    public static UsageReport WithHistory(UsageReport report, string? historyDir = null, PricingConfig? pricing = null, ProjectConfig? projectConfig = null)
    {
        var warnings = report.Diagnostics.Warnings;
        var file = PathFor(report.Provider, historyDir);
        var history = Load(file, warnings);
        Merge(history, report, DateTimeOffset.UtcNow);
        Save(history, file, warnings);
        return Apply(report, history, report.GeneratedAt.ToUnixTimeMilliseconds(), pricing, projectConfig);
    }

    /// <summary>
    /// One stored day's projects, grouped by today's merge rules.
    /// </summary>
    /// <remarks>
    /// <para>A day is stored under the ids it was parsed with, so without this a
    /// merge would reach only the days whose transcripts are still on disk (the
    /// next parse rewrites those): a day whose transcript is gone would keep the
    /// merged-away project on the list, holding just its oldest history.</para>
    /// <para>It cannot run the other way. A day stored while a merge was in
    /// place holds the two as one, so separating them again splits only the days
    /// still on disk; older ones stay with the project they were merged into.</para>
    /// <para>The day's own totals are untouched - a merge only regroups.</para>
    /// </remarks>
    private static Dictionary<string, UsageBucket> RegroupProjects(
        Dictionary<string, UsageBucket> stored,
        IReadOnlyDictionary<string, string> merge,
        Dictionary<string, SortedSet<string>> foldedIn,
        List<string> warnings,
        bool tracksReasoning)
    {
        if (merge.Count == 0) return stored;
        var groups = new Dictionary<string, List<UsageBucket>>(StringComparer.Ordinal);
        foreach (var (id, bucket) in stored)
        {
            var target = AppConfig.ResolveProjectId(id, merge, warnings);
            if (target != id)
            {
                if (!foldedIn.TryGetValue(target, out var set)) foldedIn[target] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(id);
            }
            if (!groups.TryGetValue(target, out var list)) groups[target] = list = [];
            list.Add(bucket);
        }
        return groups.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Count == 1 ? kv.Value[0] : RollUp(kv.Value.Select(b => (b.PerModel, b.Combined)), tracksReasoning),
            StringComparer.Ordinal);
    }
}
