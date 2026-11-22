using System.Text.RegularExpressions;
using UsageCore.Config;
using UsageCore.Model;
using UsageCore.Parsing;

namespace UsageCore.Combining;

/// <summary>
/// The "All agents" report: the three agents' finished reports merged into one
/// of the same shape, so every page draws it unchanged.
/// </summary>
/// <remarks>
/// <para><b>Built from reports, not transcripts.</b> Each agent's report has
/// already been de-duplicated, priced and restored from its archive by its own
/// rules; merging the results keeps all of that and costs no extra parse. So
/// this view can never disagree with an agent's own page.</para>
/// <para><b>Projects are matched by folder.</b> Each agent mints its project ids
/// from the working directory, but not the same way: Claude Code turns every
/// character that is not a letter or digit into <c>-</c>, Codex and Antigravity
/// keep <c>.</c>, <c>_</c> and <c>-</c>. <see cref="Key"/> folds all of them to
/// one form, and two projects that share a key - from their id, their folder or
/// a directory an agent merged into them - are one project here. A merge made
/// on any agent's page therefore carries over. What the keys miss (a folder
/// renamed between agents) is joined by this view's own merges.</para>
/// <para><b>The costs are not alike</b>, and adding them is a choice the label
/// owns (<see cref="Providers.Combined"/>): every total carries its split by
/// agent (<see cref="AgentShare"/>).</para>
/// </remarks>
public static partial class ReportMerge
{
    /// <summary>
    /// A project id or folder in the one form every agent's id reduces to:
    /// lower case, each run of anything but letters and digits a single dash.
    /// </summary>
    public static string Key(string text) => Separators().Replace(text.ToLowerInvariant(), "-").Trim('-');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex Separators();

    private static readonly ProviderId[] AgentOrder = [ProviderId.Claude, ProviderId.Codex, ProviderId.Antigravity];

    /// <summary>
    /// Merges whichever agents' reports are given. <paramref name="config"/> is
    /// this view's own merges and names (<c>combined-project-settings.json</c>),
    /// keyed by the combined ids this returns.
    /// </summary>
    public static UsageReport Merge(IEnumerable<UsageReport> reports, ProjectConfig? config = null, long? nowMs = null)
    {
        config ??= ProjectConfig.None;
        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ordered = reports
            .Where(r => r.Provider != ProviderId.Combined)
            .OrderBy(r => Array.IndexOf(AgentOrder, r.Provider))
            .ToList();
        var settings = ordered.FirstOrDefault()?.Settings ?? AppConfig.LoadSettings();
        var warnings = new List<string>();

        // ---- Group the agents' projects by shared keys ------------------------
        var nodes = ordered
            .SelectMany(r => r.Projects.OrderBy(p => Key(p.Id), StringComparer.Ordinal).Select(p => (r.Provider, Project: p)))
            .ToList();
        var parent = Enumerable.Range(0, nodes.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }
        var owner = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
        {
            foreach (var key in KeysOf(nodes[i].Project))
            {
                if (owner.TryGetValue(key, out var other))
                {
                    // The earlier node stays the root, so a group's first member
                    // (agent order, then key) names it.
                    var (a, b) = (Find(other), Find(i));
                    if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                }
                else
                {
                    owner[key] = i;
                }
            }
        }
        var groups = Enumerable.Range(0, nodes.Count)
            .GroupBy(Find)
            .Select(g => g.Select(i => nodes[i]).ToList())
            .ToList();
        var groupIds = groups.Select(g => Key(g[0].Project.Id)).ToList();

        // ---- This view's own merges, onto ids that exist ----------------------
        var known = new HashSet<string>(groupIds, StringComparer.Ordinal);
        var finalOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in groupIds)
        {
            var target = AppConfig.ResolveProjectId(id, config.Merge, warnings);
            finalOf[id] = known.Contains(target) ? target : id;
        }

        var projectOf = new Dictionary<(ProviderId, string), (string Id, string Name)>();
        var projects = new List<ProjectSummary>();
        foreach (var bundle in groups.Select((g, i) => (Group: g, Id: groupIds[i])).GroupBy(x => finalOf[x.Id]))
        {
            var id = bundle.Key;
            var own = bundle.FirstOrDefault(x => x.Id == id).Group ?? bundle.First().Group;
            var members = bundle.SelectMany(x => x.Group).ToList();
            var name = config.DisplayNames.GetValueOrDefault(id) ?? own[0].Project.Name;
            foreach (var (provider, project) in members) projectOf[(provider, project.Id)] = (id, name);

            var perModel = SumModels(members.Select(m => m.Project.PerModel));
            var combined = Sum(members.Select(m => m.Project.Combined));
            var daily = MergeDays(members.Select(m => m.Project.Daily));
            projects.Add(new ProjectSummary
            {
                Id = id,
                Cwd = own[0].Project.Cwd ?? members.Select(m => m.Project.Cwd).FirstOrDefault(c => c is not null),
                Name = name,
                MergedFrom = bundle.Select(x => x.Id).Where(g => g != id).Order(StringComparer.Ordinal).ToList(),
                PerModel = perModel,
                Combined = combined,
                Daily = daily,
                Sessions = members
                    .SelectMany(m => m.Project.Sessions)
                    .Select(s => Reassign(s, id))
                    .OrderByDescending(s => s.CostUsd)
                    .ToList(),
                Agents = Shares(members.Select(m => (m.Provider, m.Project.Combined, (IEnumerable<DailyEntry>)m.Project.Daily))),
                Sources = members.Select(m => new ProjectSource(m.Provider, m.Project.Id, m.Project.Name, m.Project.Cwd)).ToList(),
                Activity = null,
            });
        }

        SessionRecord? Remap(ProviderId provider, SessionRecord? record) =>
            record is null ? null
            : projectOf.TryGetValue((provider, record.ProjectId), out var to) ? Reassign(record, to.Id, to.Name)
            : record;

        foreach (var project in projects)
        {
            var sources = project.Sources!;
            var parts = sources
                .Select(s => (s.Provider, ordered.First(r => r.Provider == s.Provider).Projects.First(p => p.Id == s.ProjectId).Activity))
                .ToList();
            project.Activity = CombineActivity(parts, project.Daily, project.Combined, project.PerModel, settings, now, Remap);
        }
        projects = projects.OrderByDescending(p => p.Combined.CostUsd).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();

        // ---- The whole -------------------------------------------------------
        var globalPerModel = SumModels(ordered.Select(r => r.Global.PerModel));
        var globalCombined = Sum(ordered.Select(r => r.Global.Combined));
        var globalDaily = MergeDays(ordered.Select(r => r.Daily));

        var diagnostics = new ParseDiagnostics();
        foreach (var r in ordered)
        {
            var d = r.Diagnostics;
            var label = Providers.Get(r.Provider).Label;
            diagnostics.FilesScanned += d.FilesScanned;
            diagnostics.FilesFailed += d.FilesFailed;
            diagnostics.LinesRead += d.LinesRead;
            diagnostics.LinesUnparseable += d.LinesUnparseable;
            diagnostics.AssistantLines += d.AssistantLines;
            diagnostics.UniqueMessages += d.UniqueMessages;
            diagnostics.DuplicateLinesSkipped += d.DuplicateLinesSkipped;
            diagnostics.OutputTokensRecovered += d.OutputTokensRecovered;
            diagnostics.ImplausibleTimestamps += d.ImplausibleTimestamps;
            foreach (var model in d.UnpricedModels) if (!diagnostics.UnpricedModels.Contains(model)) diagnostics.UnpricedModels.Add(model);
            diagnostics.EmptyProjectsHidden.AddRange(d.EmptyProjectsHidden);
            diagnostics.Warnings.AddRange(d.Warnings.Select(w => $"{label}: {w}"));
        }
        diagnostics.Warnings.AddRange(warnings);

        var rates = new Dictionary<string, ModelRateInfo>(StringComparer.Ordinal);
        foreach (var r in ordered)
        {
            if (r.ModelRates is null) continue;
            foreach (var (model, info) in r.ModelRates) rates.TryAdd(model, info);
        }

        return new UsageReport
        {
            Provider = ProviderId.Combined,
            GeneratedAt = DateTimeOffset.FromUnixTimeMilliseconds(now),
            TranscriptsDir = string.Join("; ", ordered.Select(r => r.TranscriptsDir)),
            Settings = settings,
            PricingLastVerified = null,
            PricingSource = "each agent's own rate card",
            Diagnostics = diagnostics,
            Activity = CombineActivity(ordered.Select(r => (r.Provider, (ActivityStats?)r.Activity)).ToList(), globalDaily, globalCombined, globalPerModel, settings, now, Remap),
            Global = new UsageBucket { PerModel = globalPerModel, Combined = globalCombined },
            Daily = globalDaily,
            Projects = projects,
            Coverage = Coverage(ordered, globalDaily),
            ModelRates = rates,
            Agents = Shares(ordered.Select(r => (r.Provider, r.Global.Combined, (IEnumerable<DailyEntry>)r.Daily))),
        };
    }

    /// <summary>Every key a project answers to: its id, its folder, and the ids merged into it.</summary>
    private static IEnumerable<string> KeysOf(ProjectSummary project)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { Key(project.Id) };
        if (project.Cwd is { Length: > 0 } cwd) keys.Add(Key(cwd));
        foreach (var merged in project.MergedFrom) keys.Add(Key(merged));
        keys.Remove("");
        return keys;
    }

    private static UsageCell Sum(IEnumerable<UsageCell> cells)
    {
        var total = new UsageCell();
        foreach (var cell in cells) total.AddCell(cell);
        return total;
    }

    /// <summary>Per-model sums, largest by tokens first, as the parsers order them.</summary>
    private static Dictionary<string, UsageCell> SumModels(IEnumerable<IReadOnlyDictionary<string, UsageCell>> maps)
    {
        var sums = new Dictionary<string, UsageCell>(StringComparer.Ordinal);
        foreach (var map in maps)
        {
            foreach (var (model, cell) in map)
            {
                if (sums.TryGetValue(model, out var have)) have.AddCell(cell);
                else sums[model] = cell.Clone();
            }
        }
        return sums.OrderByDescending(kv => kv.Value.TotalTokens).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>Days merged by key, in date order, the undated bucket last.</summary>
    private static List<DailyEntry> MergeDays(IEnumerable<IEnumerable<DailyEntry>> lists)
    {
        var byDate = new Dictionary<string, List<DailyEntry>>(StringComparer.Ordinal);
        foreach (var day in lists.SelectMany(l => l))
        {
            if (!byDate.TryGetValue(day.Date, out var list)) byDate[day.Date] = list = [];
            list.Add(day);
        }
        return byDate
            .OrderBy(kv => kv.Key == Dates.UnknownDate ? 1 : 0)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new DailyEntry
            {
                Date = kv.Key,
                PerModel = SumModels(kv.Value.Select(d => d.PerModel)),
                Combined = Sum(kv.Value.Select(d => d.Combined)),
            })
            .ToList();
    }

    private static List<AgentShare> Shares(IEnumerable<(ProviderId Provider, UsageCell Cell, IEnumerable<DailyEntry> Daily)> parts) =>
        parts
            .GroupBy(p => p.Provider)
            .OrderBy(g => Array.IndexOf(AgentOrder, g.Key))
            .Select(g =>
            {
                var daily = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var day in g.SelectMany(p => p.Daily)) daily[day.Date] = daily.GetValueOrDefault(day.Date) + day.Combined.CostUsd;
                return new AgentShare { Provider = g.Key, Combined = Sum(g.Select(p => p.Cell)), DailyCost = daily };
            })
            .ToList();

    /// <summary>
    /// The twelve stats for a merged scope. Counts add; the day-derived ones are
    /// recomputed from the merged days, since a streak can run across agents;
    /// the hour histogram adds; the records are the largest of each agent's.
    /// </summary>
    private static ActivityStats CombineActivity(
        IReadOnlyList<(ProviderId Provider, ActivityStats? Stats)> parts,
        IReadOnlyList<DailyEntry> daily,
        UsageCell combined,
        IReadOnlyDictionary<string, UsageCell> perModel,
        Settings settings,
        long nowMs,
        Func<ProviderId, SessionRecord?, SessionRecord?> remap)
    {
        var activeDates = daily.Select(d => d.Date).Where(d => d != Dates.UnknownDate).ToList();
        var (current, longest) = Dates.Streaks(activeDates, Dates.LocalDate(nowMs, settings.LocalUtcOffsetHours));
        var hours = new int[24];
        foreach (var (_, stats) in parts)
        {
            if (stats is null) continue;
            for (var h = 0; h < 24 && h < stats.HourHistogram.Length; h++) hours[h] += stats.HourHistogram[h];
        }
        var peaks = parts.Select(p => remap(p.Provider, p.Stats?.PeakSession)).OfType<SessionRecord>().ToList();
        var longests = parts.Select(p => remap(p.Provider, p.Stats?.LongestSession)).OfType<SessionRecord>().ToList();
        return new ActivityStats
        {
            Sessions = parts.Sum(p => p.Stats?.Sessions ?? 0),
            SubagentSessions = parts.Sum(p => p.Stats?.SubagentSessions ?? 0),
            Messages = combined.Messages,
            TotalTokens = combined.TotalTokens,
            ActiveDays = activeDates.Count,
            CurrentStreakDays = current,
            LongestStreakDays = longest,
            PeakHour = hours.Any(n => n > 0) ? Array.IndexOf(hours, hours.Max()) : null,
            HourHistogram = hours,
            FavoriteModel = perModel
                .Where(kv => kv.Key != ClaudeParser.SyntheticModel)
                .OrderByDescending(kv => kv.Value.TotalTokens)
                .Select(kv => kv.Key)
                .FirstOrDefault(),
            // The first agent wins a tie, as a parser's first chat does.
            PeakSession = peaks.Count == 0 ? null : peaks.Aggregate((a, b) => b.TotalTokens > a.TotalTokens ? b : a),
            LongestSession = longests.Count == 0 ? null : longests.Aggregate((a, b) => b.RuntimeSeconds > a.RuntimeSeconds ? b : a),
        };
    }

    /// <summary>
    /// A day is archived-only here when every agent that has it holds it only in
    /// its archive: one agent's live transcripts make the day live.
    /// </summary>
    private static HistoryCoverage? Coverage(IReadOnlyList<UsageReport> reports, IReadOnlyList<DailyEntry> daily)
    {
        var covered = reports.Where(r => r.Coverage is not null).ToList();
        if (covered.Count == 0) return null;
        var dated = daily.Select(d => d.Date).Where(Dates.IsDayKey).ToList();
        var archivedOnly = dated.Count(date => reports.All(r =>
            !r.Daily.Any(d => d.Date == date)
            || (r.Coverage?.LiveEarliestDate is { } live ? string.CompareOrdinal(date, live) < 0 : false)));
        return new HistoryCoverage
        {
            EarliestDate = dated.FirstOrDefault(),
            LatestDate = dated.LastOrDefault(),
            LiveEarliestDate = covered.Select(r => r.Coverage!.LiveEarliestDate).Where(d => d is not null).Min(StringComparer.Ordinal),
            ArchivedOnlyDays = archivedOnly,
        };
    }

    private static SessionSummary Reassign(SessionSummary s, string projectId) => new()
    {
        SessionId = s.SessionId,
        ProjectId = projectId,
        File = s.File,
        FirstTimestampMs = s.FirstTimestampMs,
        LastTimestampMs = s.LastTimestampMs,
        RuntimeSeconds = s.RuntimeSeconds,
        SpanSeconds = s.SpanSeconds,
        Models = s.Models,
        Messages = s.Messages,
        TotalTokens = s.TotalTokens,
        CostUsd = s.CostUsd,
        IsSubagent = s.IsSubagent,
        ParentSessionId = s.ParentSessionId,
        SubagentKind = s.SubagentKind,
        Title = s.Title,
        PossiblyInaccurateOutput = s.PossiblyInaccurateOutput,
        SuspiciousMessageCount = s.SuspiciousMessageCount,
    };

    private static SessionRecord Reassign(SessionRecord r, string projectId, string projectName) => new()
    {
        SessionId = r.SessionId,
        ProjectId = projectId,
        ProjectName = projectName,
        Date = r.Date,
        TotalTokens = r.TotalTokens,
        RuntimeSeconds = r.RuntimeSeconds,
        CostUsd = r.CostUsd,
        IsSubagent = r.IsSubagent,
        Title = r.Title,
        SubagentThreads = r.SubagentThreads,
    };
}
