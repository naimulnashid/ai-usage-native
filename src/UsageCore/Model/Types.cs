namespace UsageCore.Model;

/// <summary>The two coding agents this app reports on.</summary>
public enum ProviderId
{
    Claude,
    Codex,
}

/// <summary>First day of the week in the activity heat map.</summary>
public enum WeekStart
{
    Monday,
    Sunday,
    Saturday,
}

/// <summary>Rates in USD per million tokens.</summary>
public sealed record ModelRate(
    double Input,
    double CacheWrite5m,
    double CacheWrite1h,
    double CacheRead,
    double Output);

public sealed class PricingConfig
{
    public string Currency { get; init; } = "USD";
    public string? LastVerified { get; init; }
    public Dictionary<string, ModelRate> Models { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Model strings seen in transcripts that are PRICED as another entry without
    /// being renamed. Codex logs its automated review turns as
    /// <c>codex-auto-review</c>, which is GPT-5.3-Codex wearing a job title; the
    /// alias prices it correctly while the charts keep it as its own band.
    /// </summary>
    public Dictionary<string, string> Aliases { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Where this card came from: "built-in" or the override file's path.</summary>
    public string Source { get; init; } = "built-in";
}

public sealed record Settings(
    double LocalUtcOffsetHours,
    WeekStart WeekStartsOn,
    double MaxIdleGapMinutes,
    double SuspiciousOutputTokens,
    double SuspiciousContextTokens);

/// <summary>
/// The five priced token buckets, cache writes split by TTL.
/// </summary>
/// <remarks>
/// Codex maps onto the same five: <c>cached_input_tokens</c> becomes
/// <see cref="CacheRead"/>, and <see cref="Input"/> carries only the uncached
/// remainder, because OpenAI's <c>input_tokens</c> already includes the cached
/// part. <see cref="Reasoning"/> is NOT a sixth bucket: it is already inside
/// <see cref="Output"/>, reported by Codex only, and never summed or priced.
/// </remarks>
public readonly record struct TokenCounts(
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite5m,
    long CacheWrite1h,
    long? Reasoning = null)
{
    /// <summary>The five priced buckets. Reasoning is deliberately left out.</summary>
    public long Total => Input + Output + CacheRead + CacheWrite5m + CacheWrite1h;
}

/// <summary>A cell of aggregated usage: tokens, derived cost and runtime.</summary>
public sealed class UsageCell
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite5m { get; set; }
    public long CacheWrite1h { get; set; }

    /// <summary>
    /// Null means "this agent does not report reasoning" - which is not the same
    /// claim as a measured zero. Claude Code cells leave it null.
    /// </summary>
    public long? Reasoning { get; set; }

    /// <summary>De-duplicated assistant messages (Codex: billed requests).</summary>
    public long Messages { get; set; }
    public double RuntimeSeconds { get; set; }
    public long TotalTokens { get; set; }

    /// <summary>Estimated USD. Zero when unpriced - check <see cref="Unpriced"/>.</summary>
    public double CostUsd { get; set; }
    public bool Unpriced { get; set; }

    public static UsageCell Empty(bool tracksReasoning) =>
        new() { Reasoning = tracksReasoning ? 0 : null };

    /// <summary>Adds one message's tokens and cost.</summary>
    public void AddMessage(in TokenCounts tokens, double cost)
    {
        Input += tokens.Input;
        Output += tokens.Output;
        CacheRead += tokens.CacheRead;
        CacheWrite5m += tokens.CacheWrite5m;
        CacheWrite1h += tokens.CacheWrite1h;
        if (Reasoning is not null) Reasoning += tokens.Reasoning ?? 0;
        Messages += 1;
        TotalTokens += tokens.Total;
        CostUsd += cost;
    }

    /// <summary>Adds another aggregated cell into this one.</summary>
    public void AddCell(UsageCell other)
    {
        Input += other.Input;
        Output += other.Output;
        CacheRead += other.CacheRead;
        CacheWrite5m += other.CacheWrite5m;
        CacheWrite1h += other.CacheWrite1h;
        if (other.Reasoning is not null) Reasoning = (Reasoning ?? 0) + other.Reasoning;
        Messages += other.Messages;
        RuntimeSeconds += other.RuntimeSeconds;
        TotalTokens += other.TotalTokens;
        CostUsd += other.CostUsd;
        Unpriced |= other.Unpriced;
    }

    public UsageCell Clone() => (UsageCell)MemberwiseClone();
}

/// <summary>Per-model cells plus their combined total.</summary>
public sealed class UsageBucket
{
    /// <summary>Ordered by total tokens, largest first.</summary>
    public Dictionary<string, UsageCell> PerModel { get; init; } = new(StringComparer.Ordinal);
    public UsageCell Combined { get; init; } = new();
}

public sealed class DailyEntry
{
    /// <summary>YYYY-MM-DD in the configured offset, or <see cref="Dates.UnknownDate"/>.</summary>
    public required string Date { get; init; }
    public Dictionary<string, UsageCell> PerModel { get; init; } = new(StringComparer.Ordinal);
    public UsageCell Combined { get; init; } = new();
}

public sealed class SessionSummary
{
    public required string SessionId { get; init; }
    public required string ProjectId { get; init; }

    /// <summary>Path relative to the transcript root, for traceability.</summary>
    public required string File { get; init; }
    public long? FirstTimestampMs { get; init; }
    public long? LastTimestampMs { get; init; }

    /// <summary>Gap-summed active time, idle stretches excluded.</summary>
    public double RuntimeSeconds { get; init; }

    /// <summary>First-to-last span, kept for comparison only. Overstates ~6x.</summary>
    public double SpanSeconds { get; init; }
    public List<string> Models { get; init; } = [];
    public long Messages { get; init; }
    public long TotalTokens { get; init; }
    public double CostUsd { get; init; }
    public bool IsSubagent { get; init; }

    /// <summary>
    /// The chat this transcript belongs to, when it is not one itself. Codex
    /// records <c>parent_thread_id</c>; Claude Code encodes it in the path
    /// (<c>&lt;session-id&gt;/subagents/agent-*.jsonl</c>). Null for a real chat.
    /// </summary>
    public string? ParentSessionId { get; init; }

    /// <summary>What kind of subagent, when the transcript says. Codex: "guardian".</summary>
    public string? SubagentKind { get; init; }

    /// <summary>Human-readable thread name, where the agent keeps one. Codex only.</summary>
    public string? Title { get; init; }

    /// <summary>A message still looked like a placeholder output_tokens after recovery.</summary>
    public bool PossiblyInaccurateOutput { get; init; }
    public int SuspiciousMessageCount { get; init; }
}

public sealed class ProjectSummary
{
    /// <summary>
    /// Claude Code: the encoded directory name under .claude/projects.
    /// Codex: the same shape, minted from the working directory.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Real working directory, recovered from the transcripts.</summary>
    public string? Cwd { get; init; }
    public required string Name { get; init; }

    /// <summary>Directories folded into this one by a merge rule.</summary>
    public List<string> MergedFrom { get; init; } = [];
    public Dictionary<string, UsageCell> PerModel { get; init; } = new(StringComparer.Ordinal);
    public UsageCell Combined { get; init; } = new();
    public List<DailyEntry> Daily { get; init; } = [];
    public List<SessionSummary> Sessions { get; init; } = [];
}

public sealed class ParseDiagnostics
{
    public int FilesScanned { get; set; }
    public int FilesFailed { get; set; }
    public long LinesRead { get; set; }
    public long LinesUnparseable { get; set; }
    public long AssistantLines { get; set; }

    /// <summary>Unique (message.id, requestId) pairs - Codex: billed turns.</summary>
    public long UniqueMessages { get; set; }

    /// <summary>Claude Code: replays and streaming partials. Codex: repeated readings.</summary>
    public long DuplicateLinesSkipped { get; set; }

    /// <summary>Claude Code only: output_tokens recovered from a later streaming line.</summary>
    public long OutputTokensRecovered { get; set; }

    /// <summary>Codex only: files whose deltas matched Codex's own per-turn figures.</summary>
    public int? ReconciledFiles { get; set; }

    /// <summary>Codex only. Non-zero is how you find out the schema has moved.</summary>
    public int? ReconcileFailures { get; set; }

    /// <summary>Codex only: running counter went backwards (context reset).</summary>
    public int? CounterResets { get; set; }

    /// <summary>Timestamps before 2000 or over a year ahead. Their tokens still count.</summary>
    public long ImplausibleTimestamps { get; set; }
    public List<string> UnpricedModels { get; set; } = [];

    /// <summary>Projects that held nothing but replayed history.</summary>
    public List<string> EmptyProjectsHidden { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

/// <summary>The single biggest chat on one axis - the record behind a "peak" card.</summary>
public sealed class SessionRecord
{
    public required string SessionId { get; init; }
    public required string ProjectId { get; init; }
    public required string ProjectName { get; init; }

    /// <summary>Local day the chat started, or null when untimed.</summary>
    public string? Date { get; init; }
    public long TotalTokens { get; init; }

    /// <summary>The chat's OWN runtime: subagents run inside its wall clock.</summary>
    public double RuntimeSeconds { get; init; }
    public double CostUsd { get; init; }
    public bool IsSubagent { get; init; }
    public string? Title { get; init; }

    /// <summary>Subagent transcripts folded in. Their tokens and cost are included.</summary>
    public int SubagentThreads { get; init; }
}

public sealed class ActivityStats
{
    /// <summary>All transcript files, subagents included.</summary>
    public int Sessions { get; init; }
    public int SubagentSessions { get; init; }
    public long Messages { get; init; }
    public long TotalTokens { get; init; }
    public int ActiveDays { get; init; }

    /// <summary>Consecutive active days ending today or yesterday.</summary>
    public int CurrentStreakDays { get; init; }
    public int LongestStreakDays { get; init; }

    /// <summary>Local hour 0-23 with the most messages, or null with no data.</summary>
    public int? PeakHour { get; init; }
    public int[] HourHistogram { get; init; } = new int[24];

    /// <summary>Model with the most tokens, excluding &lt;synthetic&gt;.</summary>
    public string? FavoriteModel { get; init; }
    public SessionRecord? PeakSession { get; init; }
    public SessionRecord? LongestSession { get; init; }
}

/// <summary>
/// What span of time the report describes. Agents delete their own transcripts,
/// so the live files are a moving window; the archive keeps what they drop.
/// </summary>
public sealed class HistoryCoverage
{
    public string? EarliestDate { get; init; }
    public string? LatestDate { get; init; }
    public string? LiveEarliestDate { get; init; }

    /// <summary>Days held only by the archive - their transcripts are gone.</summary>
    public int ArchivedOnlyDays { get; init; }
    public bool Restored => ArchivedOnlyDays > 0;
}

public sealed class UsageReport
{
    public ProviderId Provider { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public required string TranscriptsDir { get; init; }
    public required Settings Settings { get; init; }
    public string? PricingLastVerified { get; init; }
    public string PricingSource { get; init; } = "built-in";
    public required ParseDiagnostics Diagnostics { get; init; }
    public required ActivityStats Activity { get; init; }
    public required UsageBucket Global { get; init; }
    public List<DailyEntry> Daily { get; init; } = [];
    public List<ProjectSummary> Projects { get; init; } = [];

    /// <summary>Null only when the archive was not applied.</summary>
    public HistoryCoverage? Coverage { get; init; }
}
