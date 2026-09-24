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
        long nowMs,
        bool excludeSyntheticFromFavorite)
    {
        var visible = new List<ProjectSummary>();
        foreach (var project in projects.OrderByDescending(p => p.Combined.CostUsd))
        {
            if (IsEmpty(project)) diagnostics.EmptyProjectsHidden.Add(project.Id);
            else visible.Add(project);
        }

        var daily = acc.GlobalDaily.ToList();
        var activeDates = daily.Select(d => d.Date).Where(d => d != Dates.UnknownDate).ToList();
        var (current, longest) = Dates.Streaks(activeDates, Dates.LocalDate(nowMs, settings.LocalUtcOffsetHours));

        var histogram = acc.HourHistogram;
        int? peakHour = histogram.Any(n => n > 0) ? Array.IndexOf(histogram, histogram.Max()) : null;

        var favorite = acc.Global.PerModel
            .Where(kv => !excludeSyntheticFromFavorite || kv.Key != "<synthetic>")
            .OrderByDescending(kv => kv.Value.TotalTokens)
            .Select(kv => kv.Key)
            .FirstOrDefault();

        var (peak, longestSession) = SessionRecords(
            sessions,
            visible.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal),
            settings.LocalUtcOffsetHours);

        return new UsageReport
        {
            Provider = provider,
            GeneratedAt = DateTimeOffset.FromUnixTimeMilliseconds(nowMs),
            TranscriptsDir = transcriptsDir,
            Settings = settings,
            PricingLastVerified = pricing.LastVerified,
            PricingSource = pricing.Source,
            Diagnostics = diagnostics,
            Activity = new ActivityStats
            {
                Sessions = sessions.Count,
                SubagentSessions = sessions.Count(s => s.IsSubagent),
                Messages = acc.Global.Combined.Messages,
                TotalTokens = acc.Global.Combined.TotalTokens,
                ActiveDays = activeDates.Count,
                CurrentStreakDays = current,
                LongestStreakDays = longest,
                PeakHour = peakHour,
                HourHistogram = histogram,
                FavoriteModel = favorite,
                PeakSession = peak,
                LongestSession = longestSession,
            },
            Global = acc.Global.ToBucket(),
            Daily = daily,
            Projects = visible,
        };
    }
}
