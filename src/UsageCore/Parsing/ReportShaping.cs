using UsageCore.Model;

namespace UsageCore.Parsing;

/// <summary>What both parsers do once every file has been walked.</summary>
internal static class ReportShaping
{
    /// <summary>
    /// A project's display name: the last segment of its working directory, or
    /// the id with the user prefix stripped when no directory was recovered.
    /// </summary>
    public static string DerivedName(string id, string? cwd)
    {
        if (cwd is not null)
        {
            var last = cwd.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (last is not null) return last;
        }
        var stripped = System.Text.RegularExpressions.Regex.Replace(id, "^C--Users-[^-]+-", "");
        return stripped.Replace('-', ' ').Trim() is { Length: > 0 } name ? name : id;
    }

    /// <summary>
    /// A project that recorded nothing at all - every message in it was a replay
    /// already credited elsewhere. The test is strict: any token, message or
    /// second of runtime keeps a project visible.
    /// </summary>
    public static bool IsEmpty(ProjectSummary project) =>
        project.Combined.Messages == 0 && project.Combined.TotalTokens == 0 && project.Combined.RuntimeSeconds == 0;

    /// <summary>
    /// The "Peak tokens" and "Longest chat" records, over CHATS not transcripts.
    /// </summary>
    /// <remarks>
    /// A subagent transcript is separately billed work and counts everywhere
    /// else, but it is not a conversation, and a card that says "Longest chat"
    /// must not be able to name one. So each transcript folds into its parent:
    /// tokens and cost are summed (that is what the chat cost), while runtime is
    /// the parent's own - a subagent runs inside the parent's wall clock, and
    /// summing would count the same minutes twice. An orphan whose parent is
    /// gone stands as its own chat rather than being dropped.
    /// </remarks>
    public static (SessionRecord? Peak, SessionRecord? Longest) SessionRecords(
        IReadOnlyList<SessionSummary> sessions,
        IReadOnlyDictionary<string, string> projectNames,
        double offsetHours)
    {
        var byId = new Dictionary<string, SessionSummary>(StringComparer.Ordinal);
        foreach (var session in sessions) byId.TryAdd(session.SessionId, session);

        // Insertion-ordered, so a tie goes to the chat walked first: the earliest.
        var chats = new List<Chat>();
        var chatIndex = new Dictionary<SessionSummary, Chat>(ReferenceEqualityComparer.Instance);
        foreach (var session in sessions)
        {
            var root = session.ParentSessionId is { } parentId && byId.TryGetValue(parentId, out var parent)
                ? parent
                : session;
            if (!chatIndex.TryGetValue(root, out var chat))
            {
                chat = new Chat(root);
                chatIndex[root] = chat;
                chats.Add(chat);
            }
            chat.TotalTokens += session.TotalTokens;
            chat.CostUsd += session.CostUsd;
            if (!ReferenceEquals(session, root)) chat.SubagentThreads++;
        }

        SessionRecord ToRecord(Chat chat) => new()
        {
            SessionId = chat.Root.SessionId,
            ProjectId = chat.Root.ProjectId,
            ProjectName = projectNames.TryGetValue(chat.Root.ProjectId, out var name) ? name : chat.Root.ProjectId,
            Date = chat.Root.FirstTimestampMs is { } ms ? Dates.LocalDate(ms, offsetHours) : null,
            TotalTokens = chat.TotalTokens,
            RuntimeSeconds = chat.Root.RuntimeSeconds,
            CostUsd = chat.CostUsd,
            IsSubagent = chat.Root.IsSubagent,
            Title = chat.Root.Title,
            SubagentThreads = chat.SubagentThreads,
        };

        SessionRecord? Best(Func<Chat, double> score)
        {
            Chat? winner = null;
            double winning = 0;
            foreach (var chat in chats)
            {
                var value = score(chat);
                if (value > winning)
                {
                    winner = chat;
                    winning = value;
                }
            }
            return winner is null ? null : ToRecord(winner);
        }

        return (Best(c => c.TotalTokens), Best(c => c.Root.RuntimeSeconds));
    }

    private sealed class Chat(SessionSummary root)
    {
        public SessionSummary Root { get; } = root;
        public long TotalTokens { get; set; }
        public double CostUsd { get; set; }
        public int SubagentThreads { get; set; }
    }

    /// <summary>
    /// The twelve activity stats, for the whole agent or for one project - one
    /// function, so the overview and a project page cannot drift.
    /// </summary>
    public static ActivityStats BuildActivity(
        IReadOnlyList<SessionSummary> sessions,
        UsageCell combined,
        IReadOnlyDictionary<string, UsageCell> perModel,
        IReadOnlyList<DailyEntry> daily,
        int[] hourHistogram,
        IReadOnlyDictionary<string, string> projectNames,
        Settings settings,
        long nowMs)
    {
        var activeDates = daily.Select(d => d.Date).Where(d => d != Dates.UnknownDate).ToList();
        var (current, longest) = Dates.Streaks(activeDates, Dates.LocalDate(nowMs, settings.LocalUtcOffsetHours));
        int? peakHour = hourHistogram.Any(n => n > 0) ? Array.IndexOf(hourHistogram, hourHistogram.Max()) : null;
        var favorite = perModel
            .Where(kv => kv.Key != ClaudeParser.SyntheticModel)
            .OrderByDescending(kv => kv.Value.TotalTokens)
            .Select(kv => kv.Key)
            .FirstOrDefault();
        var (peak, longestSession) = SessionRecords(sessions, projectNames, settings.LocalUtcOffsetHours);
        return new ActivityStats
        {
            Sessions = sessions.Count,
            SubagentSessions = sessions.Count(s => s.IsSubagent),
            Messages = combined.Messages,
            TotalTokens = combined.TotalTokens,
            ActiveDays = activeDates.Count,
            CurrentStreakDays = current,
            LongestStreakDays = longest,
            PeakHour = peakHour,
            HourHistogram = [.. hourHistogram],
            FavoriteModel = favorite,
            PeakSession = peak,
            LongestSession = longestSession,
        };
    }

    /// <summary>
    /// Brings the day-derived stats back in line after the archive has changed
    /// the days under them. Session-derived ones stay: sessions are not archived.
    /// </summary>
    public static ActivityStats RefreshActivity(ActivityStats? baseline, IReadOnlyList<DailyEntry> daily, UsageCell combined, double offsetHours, long nowMs)
    {
        var activeDates = daily.Select(d => d.Date).Where(d => d != Dates.UnknownDate).ToList();
        var (current, longest) = Dates.Streaks(activeDates, Dates.LocalDate(nowMs, offsetHours));
        return new ActivityStats
        {
            Sessions = baseline?.Sessions ?? 0,
            SubagentSessions = baseline?.SubagentSessions ?? 0,
            Messages = combined.Messages,
            TotalTokens = combined.TotalTokens,
            ActiveDays = activeDates.Count,
            CurrentStreakDays = current,
            LongestStreakDays = longest,
            PeakHour = baseline?.PeakHour,
            HourHistogram = baseline?.HourHistogram ?? new int[24],
            FavoriteModel = baseline?.FavoriteModel,
            PeakSession = baseline?.PeakSession,
            LongestSession = baseline?.LongestSession,
        };
    }

    /// <summary>
    /// The activity stats, the visible project list and the finished report.
    /// </summary>
    public static UsageReport Finish(
        ProviderId provider,
        string transcriptsDir,
        Settings settings,
        Model.PricingConfig pricing,
        ParseDiagnostics diagnostics,
        Accumulator acc,
        List<ProjectSummary> projects,
        List<SessionSummary> sessions,
        long nowMs)
    {
        var visible = new List<ProjectSummary>();
        foreach (var project in projects.OrderByDescending(p => p.Combined.CostUsd))
        {
            if (IsEmpty(project)) diagnostics.EmptyProjectsHidden.Add(project.Id);
            else visible.Add(project);
        }

        var daily = acc.GlobalDaily.ToList();
        var names = visible.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        var activity = BuildActivity(sessions, acc.Global.Combined, acc.Global.PerModel, daily, acc.HourHistogram, names, settings, nowMs);
        foreach (var project in visible)
        {
            project.Activity = BuildActivity(
                project.Sessions,
                project.Combined,
                project.PerModel,
                project.Daily,
                acc.ProjectHours.TryGetValue(project.Id, out var hours) ? hours : new int[24],
                names,
                settings,
                nowMs);
        }

        return new UsageReport
        {
            Provider = provider,
            GeneratedAt = DateTimeOffset.FromUnixTimeMilliseconds(nowMs),
            TranscriptsDir = transcriptsDir,
            Settings = settings,
            PricingLastVerified = pricing.LastVerified,
            PricingSource = pricing.Source,
            Diagnostics = diagnostics,
            Activity = activity,
            Global = acc.Global.ToBucket(),
            Daily = daily,
            Projects = visible,
        };
    }
}
