using System.Text.Json;
using System.Text.RegularExpressions;
using UsageCore.Config;
using UsageCore.Model;

namespace UsageCore.Parsing;

public sealed class ParseOptions
{
    /// <summary>Claude Code: the projects directory. Codex: CODEX_HOME.</summary>
    public string? Root { get; init; }
    public PricingConfig? Pricing { get; init; }
    public Settings? Settings { get; init; }

    /// <summary>Injectable so a test never has to write the user's own config.</summary>
    public ProjectConfig? ProjectConfig { get; init; }

    /// <summary>The clock, for "today" and the future-timestamp guard.</summary>
    public Func<DateTimeOffset>? Now { get; init; }
}

/// <summary>
/// Claude Code's transcripts: one JSONL file per session under
/// <c>~/.claude/projects/&lt;encoded-cwd&gt;/</c>, usage on <c>type: "assistant"</c>
/// lines under <c>message.usage</c>.
/// </summary>
/// <remarks>
/// <para><b>Trap 1 - the same message is written many times.</b> Streaming
/// partials (one line per update, same message id and request id) and session
/// replay (resuming copies earlier history into the new file, sometimes in a
/// different project). Counting every line inflates cost by ~89%. De-duplicate
/// GLOBALLY on (message.id, requestId).</para>
/// <para><b>Trap 2 - output_tokens placeholders are recoverable.</b> Early
/// partials carry 1-4; the final line carries the truth. Keep the MAXIMUM per
/// key.</para>
/// <para><b>Trap 3 - subagent transcripts are nested</b> under
/// <c>&lt;session&gt;/subagents/agent-*.jsonl</c>. Recurse.</para>
/// <para><b>Trap 4 - cache writes are split by TTL</b> and priced differently.
/// Prefer <c>cache_creation.ephemeral_5m/1h</c>; the flat legacy field is 5m.</para>
/// </remarks>
public static partial class ClaudeParser
{
    private sealed record DiscoveredFile(
        string Abs,
        string Rel,
        string SourceProjectId,
        string SessionId,
        bool IsSubagent,
        string? ParentSessionId)
    {
        /// <summary>After merge rules: a renamed directory carries its target's id.</summary>
        public string ProjectId { get; set; } = SourceProjectId;
    }

    private readonly record struct LineRecord(long? TimestampMs, string? LineModel, string? Key, TokenCounts? Tokens);

    private sealed class FileRecords(DiscoveredFile file)
    {
        public DiscoveredFile File { get; } = file;
        public List<LineRecord> Records { get; } = [];
        public long? FirstTimestampMs { get; set; }
        public string? FirstCwd { get; set; }
        public Dictionary<string, int> CwdCounts { get; } = new(StringComparer.Ordinal);
        public FileDiagnostics Diag { get; } = new();
    }

    /// <summary>
    /// Re-encodes a working directory the way Claude Code names project
    /// directories: every character outside [A-Za-z0-9] becomes a dash. Lossy
    /// (<c>My App</c> and <c>My_App</c> collide) so it cannot be reversed - but it
    /// can be applied forwards to ask which path a directory is named after.
    /// </summary>
    public static string EncodeProjectDir(string cwd) => NonAlphanumeric().Replace(cwd, "-");

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();

    public static UsageReport Parse(ParseOptions? options = null)
    {
        options ??= new ParseOptions();
        var projectsDir = options.Root ?? AppPaths.ClaudeProjectsDir;
        var pricing = options.Pricing ?? AppConfig.LoadPricing(ProviderId.Claude);
        var settings = options.Settings ?? AppConfig.LoadSettings();
        var projectConfig = options.ProjectConfig ?? AppConfig.LoadProjectConfig(ProviderId.Claude);
        var nowMs = (options.Now?.Invoke() ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

        var diagnostics = new ParseDiagnostics();
        var files = Discover(projectsDir, diagnostics.Warnings);
        diagnostics.FilesScanned = files.Count;

        // Merge rules first, so a renamed directory lands in its target's buckets.
        foreach (var file in files)
        {
            file.ProjectId = AppConfig.ResolveProjectId(file.SourceProjectId, projectConfig.Merge, diagnostics.Warnings);
        }

        // Read every file once, in parallel; merge per-file counters in file order.
        var fileRecords = new FileRecords[files.Count];
        Parallel.For(0, files.Count, i => fileRecords[i] = ReadFile(files[i], nowMs));
        foreach (var fr in fileRecords) fr.Diag.MergeInto(diagnostics);

        // Trap 1 + 2: one canonical token set per key, the largest output wins.
        var canonical = new Dictionary<string, TokenCounts>(StringComparer.Ordinal);
        var firstSeenOutput = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var fr in fileRecords)
        {
            foreach (var record in fr.Records)
            {
                if (record.Key is null || record.Tokens is not { } tokens) continue;
                if (!canonical.TryGetValue(record.Key, out var existing))
                {
                    canonical[record.Key] = tokens;
                    firstSeenOutput[record.Key] = tokens.Output;
                }
                else if (tokens.Output > existing.Output)
                {
                    canonical[record.Key] = tokens;
                }
            }
        }
        foreach (var (key, tokens) in canonical)
        {
            if (firstSeenOutput[key] < tokens.Output) diagnostics.OutputTokensRecovered++;
        }

        // Chronological, so a replayed message is credited to its ORIGINAL session.
        var ordered = fileRecords
            .Select((fr, index) => (fr, index))
            .OrderBy(x => x.fr.FirstTimestampMs ?? long.MaxValue)
            .ThenBy(x => x.index)
            .Select(x => x.fr)
            .ToList();

        var acc = new Accumulator(tracksReasoning: false);
        var projectCwd = new Dictionary<string, string>(StringComparer.Ordinal);
        var projectCwdFallback = new Dictionary<string, string>(StringComparer.Ordinal);
        var dirCwdCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var mergedFrom = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var sessions = new List<SessionSummary>();
        var unpriced = new SortedSet<string>(StringComparer.Ordinal);
        var counted = new HashSet<string>(StringComparer.Ordinal);
        var maxIdleGapSeconds = settings.MaxIdleGapMinutes * 60;

        foreach (var fr in ordered)
        {
            var file = fr.File;
            // A merged project shows the surviving directory's path; a merged-in
            // path is only a fallback.
            if (fr.FirstCwd is { } firstCwd)
            {
                if (file.SourceProjectId == file.ProjectId) projectCwd.TryAdd(file.ProjectId, firstCwd);
                else projectCwdFallback.TryAdd(file.ProjectId, firstCwd);
            }
            // Counted against the directory the file actually lives in: a
            // replayed conversation names another project's path.
            if (!dirCwdCounts.TryGetValue(file.SourceProjectId, out var dirCounts))
            {
                dirCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                dirCwdCounts[file.SourceProjectId] = dirCounts;
            }
            foreach (var (seen, count) in fr.CwdCounts)
            {
                dirCounts[seen] = dirCounts.GetValueOrDefault(seen) + count;
            }
            if (file.SourceProjectId != file.ProjectId)
            {
                if (!mergedFrom.TryGetValue(file.ProjectId, out var set))
                {
                    set = new SortedSet<string>(StringComparer.Ordinal);
                    mergedFrom[file.ProjectId] = set;
                }
                set.Add(file.SourceProjectId);
            }
            acc.Project(file.ProjectId);

            string? currentModel = null;
            long? lastKeptMs = null;
            double sessionRuntime = 0;
            long sessionMessages = 0, sessionTokens = 0;
            double sessionCost = 0;
            var sessionSuspicious = 0;
            long? firstTs = null, lastTs = null, minMs = null, maxMs = null;
            var sessionModels = new List<string>();

            foreach (var record in fr.Records)
            {
                // Already counted: a replay or a streaming partial. It adds
                // neither tokens nor runtime.
                if (record.Key is not null)
                {
                    if (!counted.Add(record.Key))
                    {
                        diagnostics.DuplicateLinesSkipped++;
                        continue;
                    }
                }

                if (record.LineModel is not null) currentModel = record.LineModel;

                if (record.TimestampMs is { } ts)
                {
                    firstTs ??= ts;
                    lastTs = ts;
                    minMs = minMs is null ? ts : Math.Min(minMs.Value, ts);
                    maxMs = maxMs is null ? ts : Math.Max(maxMs.Value, ts);

                    // Runtime: gaps between consecutive kept lines, idle excluded.
                    if (lastKeptMs is { } previous && currentModel is not null)
                    {
                        var gap = (ts - previous) / 1000d;
                        if (gap > 0 && gap <= maxIdleGapSeconds)
                        {
                            sessionRuntime += gap;
                            acc.AddRuntime(file.ProjectId, Dates.LocalDate(ts, settings.LocalUtcOffsetHours), currentModel, gap);
                        }
                    }
                    lastKeptMs = ts;
                }

                if (record.Key is null || record.Tokens is null) continue;
                var tokens = canonical.GetValueOrDefault(record.Key, record.Tokens.Value);
                var model = record.LineModel ?? currentModel ?? "(unknown)";
                if (!sessionModels.Contains(model)) sessionModels.Add(model);

                var rate = AppConfig.GetRate(pricing, model);
                if (rate is null) unpriced.Add(model);
                var cost = AppConfig.CostOf(tokens, rate);

                var date = record.TimestampMs is { } t ? Dates.LocalDate(t, settings.LocalUtcOffsetHours) : Dates.UnknownDate;
                if (record.TimestampMs is { } t2 && Dates.LocalHour(t2, settings.LocalUtcOffsetHours) is { } hour)
                {
                    acc.HourHistogram[hour]++;
                }

                acc.AddMessage(file.ProjectId, date, model, tokens, cost, rate is null);

                sessionMessages++;
                sessionTokens += tokens.Total;
                sessionCost += cost;

                // Known Claude Code limitation: output_tokens occasionally stays a
                // placeholder. Anything still tiny despite a large context is
                // flagged, not trusted. Measured to be immaterial, so the UI does
                // not surface it; the counter is how a change would be noticed.
                if (model != "<synthetic>"
                    && tokens.Output < settings.SuspiciousOutputTokens
                    && tokens.Input + tokens.CacheRead >= settings.SuspiciousContextTokens)
                {
                    sessionSuspicious++;
                }
            }

            sessions.Add(new SessionSummary
            {
                SessionId = file.SessionId,
                ProjectId = file.ProjectId,
                File = file.Rel,
                FirstTimestampMs = firstTs,
                LastTimestampMs = lastTs,
                RuntimeSeconds = sessionRuntime,
                SpanSeconds = minMs is { } lo && maxMs is { } hi ? (hi - lo) / 1000d : 0,
                Models = sessionModels,
                Messages = sessionMessages,
                TotalTokens = sessionTokens,
                CostUsd = sessionCost,
                IsSubagent = file.IsSubagent,
                ParentSessionId = file.ParentSessionId,
                PossiblyInaccurateOutput = sessionSuspicious > 0,
                SuspiciousMessageCount = sessionSuspicious,
            });
        }

        diagnostics.UniqueMessages = counted.Count;
        diagnostics.UnpricedModels = [.. unpriced];

        var projects = acc.Projects.Select(kv =>
        {
            var id = kv.Key;
            var mergedIn = mergedFrom.TryGetValue(id, out var m) ? m.ToList() : [];
            var cwd = PickProjectCwd(id, dirCwdCounts.GetValueOrDefault(id))
                      ?? mergedIn.Select(source => PickProjectCwd(source, dirCwdCounts.GetValueOrDefault(source))).FirstOrDefault(p => p is not null)
                      ?? projectCwd.GetValueOrDefault(id)
                      ?? projectCwdFallback.GetValueOrDefault(id);
            return new ProjectSummary
            {
                Id = id,
                Cwd = cwd,
                Name = projectConfig.DisplayNames.TryGetValue(id, out var display) ? display : ReportShaping.DerivedName(id, cwd),
                MergedFrom = mergedIn,
                PerModel = kv.Value.Totals.SortedPerModel(),
                Combined = kv.Value.Totals.Combined,
                Daily = kv.Value.Daily.ToList(),
                Sessions = sessions.Where(s => s.ProjectId == id).OrderByDescending(s => s.CostUsd).ToList(),
            };
        }).ToList();

        return ReportShaping.Finish(ProviderId.Claude, projectsDir, settings, pricing, diagnostics, acc, projects, sessions, nowMs, excludeSyntheticFromFavorite: true);
    }

    /// <summary>
    /// The path a project directory is named after. Resuming a session from
    /// another directory replays the old conversation - with the OLD cwd - into
    /// the new project's file, so the first cwd seen is not it. Only paths that
    /// re-encode to the directory's own name qualify; ties (the lossy case) go
    /// to the spelling on the most lines.
    /// </summary>
    private static string? PickProjectCwd(string id, Dictionary<string, int>? counts)
    {
        if (counts is null) return null;
        string? best = null;
        var bestCount = -1;
        foreach (var (cwd, count) in counts)
        {
            if (EncodeProjectDir(cwd) != id) continue;
            if (count > bestCount)
            {
                best = cwd;
                bestCount = count;
            }
        }
        return best;
    }

    /// <summary>
    /// Every .jsonl under each project directory, recursively - subagent
    /// transcripts are nested a level down and a flat scan silently drops them.
    /// </summary>
    private static List<DiscoveredFile> Discover(string projectsDir, List<string> warnings)
    {
        var result = new List<DiscoveredFile>();
        string[] projectDirs;
        try
        {
            projectDirs = Directory.GetDirectories(projectsDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            warnings.Add($"Cannot read projects directory {projectsDir}: {ex.Message}");
            return result;
        }

        foreach (var projectDir in projectDirs.Order(StringComparer.Ordinal))
        {
            var projectId = Path.GetFileName(projectDir);
            Walk(projectDir);

            void Walk(string dir)
            {
                string[] entries;
                string[] subdirs;
                try
                {
                    entries = Directory.GetFiles(dir);
                    subdirs = Directory.GetDirectories(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Skipped unreadable directory {dir}: {ex.Message}");
                    return;
                }
                foreach (var file in entries.Order(StringComparer.Ordinal))
                {
                    if (!file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                    var rel = Path.GetRelativePath(projectsDir, file).Replace('\\', '/');
                    var parts = rel.Split('/');
                    var at = Array.LastIndexOf(parts, "subagents");
                    result.Add(new DiscoveredFile(
                        file,
                        rel,
                        projectId,
                        Path.GetFileNameWithoutExtension(file),
                        rel.Contains("/subagents/", StringComparison.Ordinal),
                        at > 0 ? parts[at - 1] : null));
                }
                foreach (var sub in subdirs.Order(StringComparer.Ordinal)) Walk(sub);
            }
        }
        return result;
    }

    private static FileRecords ReadFile(DiscoveredFile file, long nowMs)
    {
        var fr = new FileRecords(file);
        try
        {
            using var reader = new StreamReader(file.Abs, new FileStreamOptions { Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete });
            string? raw;
            while ((raw = reader.ReadLine()) is not null)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                fr.Diag.LinesRead++;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    // Truncated or malformed: skipped, never fatal.
                    fr.Diag.LinesUnparseable++;
                    continue;
                }

                using (doc)
                {
                    var entry = doc.RootElement;
                    // Valid JSON that is not an object (null, a number, an array)
                    // is just as unusable.
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        fr.Diag.LinesUnparseable++;
                        continue;
                    }

                    var tsRaw = Guards.StringField(entry, "timestamp");
                    var ts = Guards.ParseTimestampMs(tsRaw, nowMs);
                    if (ts is null && tsRaw is not null) fr.Diag.ImplausibleTimestamps++;
                    if (ts is { } t && (fr.FirstTimestampMs is null || t < fr.FirstTimestampMs)) fr.FirstTimestampMs = t;

                    if (Guards.StringField(entry, "cwd") is { } cwd)
                    {
                        fr.FirstCwd ??= cwd;
                        fr.CwdCounts[cwd] = fr.CwdCounts.GetValueOrDefault(cwd) + 1;
                    }

                    var hasMessage = Guards.TryObject(entry, "message", out var message);
                    var lineModel = hasMessage ? Guards.StringField(message, "model") : null;

                    string? key = null;
                    TokenCounts? tokens = null;
                    if (hasMessage && Guards.StringField(entry, "type") == "assistant")
                    {
                        fr.Diag.AssistantLines++;
                        if (Guards.TryObject(message, "usage", out var usage))
                        {
                            tokens = ReadTokens(usage);
                            var messageId = Guards.StringField(message, "id");
                            var requestId = Guards.StringField(entry, "requestId");
                            if (messageId is not null) key = $"{messageId}::{requestId}";
                        }
                    }

                    fr.Records.Add(new LineRecord(ts, lineModel, key, tokens));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fr.Diag.FilesFailed++;
            fr.Diag.Warnings.Add($"Error reading {file.Rel}: {ex.Message}");
        }
        return fr;
    }

    /// <summary>
    /// The five buckets. Cache writes prefer the TTL-split object, falling back
    /// to the flat legacy field at the 5m rate (5m is the default TTL).
    /// </summary>
    private static TokenCounts ReadTokens(JsonElement usage)
    {
        long write5m, write1h = 0;
        if (Guards.TryObject(usage, "cache_creation", out var creation))
        {
            write5m = Guards.TokenField(creation, "ephemeral_5m_input_tokens");
            write1h = Guards.TokenField(creation, "ephemeral_1h_input_tokens");
        }
        else
        {
            write5m = Guards.TokenField(usage, "cache_creation_input_tokens");
        }

        return new TokenCounts(
            Input: Guards.TokenField(usage, "input_tokens"),
            Output: Guards.TokenField(usage, "output_tokens"),
            CacheRead: Guards.TokenField(usage, "cache_read_input_tokens"),
            CacheWrite5m: write5m,
            CacheWrite1h: write1h);
    }
}

/// <summary>Counters one file contributes, merged into the report in file order.</summary>
internal sealed class FileDiagnostics
{
    public long LinesRead;
    public long LinesUnparseable;
    public long AssistantLines;
    public long ImplausibleTimestamps;
    public int FilesFailed;
    public List<string> Warnings { get; } = [];

    public void MergeInto(ParseDiagnostics d)
    {
        d.LinesRead += LinesRead;
        d.LinesUnparseable += LinesUnparseable;
        d.AssistantLines += AssistantLines;
        d.ImplausibleTimestamps += ImplausibleTimestamps;
        d.FilesFailed += FilesFailed;
        d.Warnings.AddRange(Warnings);
    }
}
