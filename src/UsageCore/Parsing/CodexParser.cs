using System.Text.Json;
using System.Text.RegularExpressions;
using UsageCore.Config;
using UsageCore.Model;

namespace UsageCore.Parsing;

/// <summary>
/// Codex rollouts: one JSONL file per thread under
/// <c>$CODEX_HOME/sessions/YYYY/MM/DD/rollout-&lt;local-time&gt;-&lt;thread-id&gt;.jsonl</c>.
/// Nothing about Claude Code's format transfers; the traps are all different.
/// </summary>
/// <remarks>
/// <para><b>Trap 1 - usage is CUMULATIVE, and a reading gets repeated.</b>
/// <c>total_token_usage</c> is authoritative; per-turn usage is its delta, so a
/// repeat contributes zero. Summing <c>last_token_usage</c> overcounts. It is
/// still read, as an independent check (<c>ReconcileFailures</c>).</para>
/// <para><b>Trap 2 - input_tokens already INCLUDES cached tokens.</b> Input
/// carries the uncached remainder only; cache hits are ~98% of the prompt, so
/// pricing both would nearly double the input bill.</para>
/// <para><b>Trap 3 - reasoning_output_tokens is INSIDE output_tokens.</b>
/// Carried for display, never summed or priced.</para>
/// <para><b>Trap 4 - auto-review threads are separate files and real spend.</b>
/// Counted, as their own model band (<c>codex-auto-review</c>, priced by alias).</para>
/// <para><b>Trap 5 - the model is a state, not a per-event field.</b> It comes
/// from the most recent <c>turn_context</c>, written only when it changes.</para>
/// The filename carries LOCAL time while every timestamp inside is UTC.
/// </remarks>
public static partial class CodexParser
{
    private sealed record DiscoveredFile(string Abs, string Rel, string SessionId);

    private readonly record struct RawTotals(long Input, long Cached, long CacheWrite, long Output, long Reasoning, long Total);

    private readonly record struct UsageEvent(long? TimestampMs, string Model, string? Cwd, TokenCounts Tokens);

    private readonly record struct Tick(long TimestampMs, string? Model, string? Cwd);

    private sealed class FileRecords(DiscoveredFile file)
    {
        public DiscoveredFile File { get; } = file;
        public List<UsageEvent> Events { get; } = [];
        public List<Tick> Ticks { get; } = [];
        public long? FirstTimestampMs { get; set; }
        public long? LastTimestampMs { get; set; }
        public string? Cwd { get; set; }
        public bool IsSubagent { get; set; }
        public string? ParentThreadId { get; set; }
        public string? SubagentKind { get; set; }
        public bool Reconciled { get; set; } = true;
        public int CounterResets { get; set; }
        public long Repeats { get; set; }
        public FileDiagnostics Diag { get; } = new();
    }

    /// <summary>
    /// A filename-safe id for a working directory, in the shape Claude Code
    /// uses: <c>C:\Users\me\Thing</c> gives <c>C--Users-me-Thing</c>. Each
    /// character is replaced individually - collapsing runs would give the same
    /// path a different id. Changing this renames every project and orphans the
    /// archive's entries.
    /// </summary>
    public static string ProjectIdFromCwd(string cwd) =>
        EdgeDashes().Replace(UnsafeChars().Replace(cwd, "-"), "");

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeChars();

    [GeneratedRegex("^-+|-+$")]
    private static partial Regex EdgeDashes();

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingUuid();

    /// <summary>Both roots Codex uses; <c>archived_sessions</c> is often absent.</summary>
    public static List<string> SessionDirs(string home) =>
        new[] { Path.Combine(home, "sessions"), Path.Combine(home, "archived_sessions") }.Where(Directory.Exists).ToList();

    public static UsageReport Parse(ParseOptions? options = null)
    {
        options ??= new ParseOptions();
        var home = options.Root ?? AppPaths.CodexHome;
        var pricing = options.Pricing ?? AppConfig.LoadPricing(ProviderId.Codex);
        var settings = options.Settings ?? AppConfig.LoadSettings();
        var projectConfig = options.ProjectConfig ?? AppConfig.LoadProjectConfig(ProviderId.Codex);
        var nowMs = (options.Now?.Invoke() ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

        var diagnostics = new ParseDiagnostics { ReconciledFiles = 0, ReconcileFailures = 0, CounterResets = 0 };
        var sessionsRoot = SessionDirs(home).FirstOrDefault() ?? Path.Combine(home, "sessions");
        var files = Discover(home, diagnostics.Warnings);
        diagnostics.FilesScanned = files.Count;
        if (files.Count == 0)
        {
            diagnostics.Warnings.Add($"No Codex rollout files found under {home}. Set CODEX_HOME if yours live elsewhere.");
        }

        var threadNames = LoadThreadNames(home, diagnostics.Warnings);

        var fileRecords = new FileRecords[files.Count];
        Parallel.For(0, files.Count, i => fileRecords[i] = ReadFile(files[i], nowMs));
        foreach (var fr in fileRecords)
        {
            fr.Diag.MergeInto(diagnostics);
            diagnostics.CounterResets += fr.CounterResets;
            diagnostics.DuplicateLinesSkipped += fr.Repeats;
        }

        var ordered = fileRecords
            .Select((fr, index) => (fr, index))
            .OrderBy(x => x.fr.FirstTimestampMs ?? long.MaxValue)
            .ThenBy(x => x.index)
            .Select(x => x.fr)
            .ToList();

        var acc = new Accumulator(tracksReasoning: true);
        var projectCwd = new Dictionary<string, string>(StringComparer.Ordinal);
        var mergedFrom = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var sessions = new List<SessionSummary>();
        var unpriced = new SortedSet<string>(StringComparer.Ordinal);
        var maxIdleGapSeconds = settings.MaxIdleGapMinutes * 60;
        const string Unplaced = "(unknown project)";

        // Memoised per cwd, and that is load-bearing: this runs per tick and per
        // event, and an unmemoised merge-rule cycle warned once per event.
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        string ResolveProject(string? rawCwd)
        {
            if (rawCwd is null) return Unplaced;
            if (resolved.TryGetValue(rawCwd, out var cached)) return cached;
            var sourceId = ProjectIdFromCwd(rawCwd);
            var targetId = AppConfig.ResolveProjectId(sourceId, projectConfig.Merge, diagnostics.Warnings);
            if (sourceId == targetId)
            {
                projectCwd.TryAdd(targetId, rawCwd);
            }
            else
            {
                if (!mergedFrom.TryGetValue(targetId, out var set))
                {
                    set = new SortedSet<string>(StringComparer.Ordinal);
                    mergedFrom[targetId] = set;
                }
                set.Add(sourceId);
            }
            resolved[rawCwd] = targetId;
            return targetId;
        }

        foreach (var record in ordered)
        {
            if (record.Reconciled) diagnostics.ReconciledFiles++;
            else diagnostics.ReconcileFailures++;

            // Runtime: gaps between consecutive lines, idle excluded.
            double sessionRuntime = 0;
            long? previousTick = null;
            foreach (var tick in record.Ticks)
            {
                if (previousTick is { } previous && tick.Model is not null)
                {
                    var gap = (tick.TimestampMs - previous) / 1000d;
                    if (gap > 0 && gap <= maxIdleGapSeconds)
                    {
                        sessionRuntime += gap;
                        acc.AddRuntime(
                            ResolveProject(tick.Cwd ?? record.Cwd),
                            Dates.LocalDate(tick.TimestampMs, settings.LocalUtcOffsetHours),
                            tick.Model,
                            gap);
                    }
                }
                previousTick = tick.TimestampMs;
            }

            long sessionMessages = 0, sessionTokens = 0;
            double sessionCost = 0;
            string? sessionProject = null;
            var sessionModels = new List<string>();

            foreach (var ev in record.Events)
            {
                if (!sessionModels.Contains(ev.Model)) sessionModels.Add(ev.Model);
                var rate = AppConfig.GetRate(pricing, ev.Model);
                if (rate is null) unpriced.Add(ev.Model);
                var cost = AppConfig.CostOf(ev.Tokens, rate);

                var date = ev.TimestampMs is { } t ? Dates.LocalDate(t, settings.LocalUtcOffsetHours) : Dates.UnknownDate;
                var projectId = ResolveProject(ev.Cwd ?? record.Cwd);
                sessionProject ??= projectId;

                if (ev.TimestampMs is { } t2 && Dates.LocalHour(t2, settings.LocalUtcOffsetHours) is { } hour)
                {
                    acc.HourHistogram[hour]++;
                }

                acc.AddMessage(projectId, date, ev.Model, ev.Tokens, cost, rate is null);
                sessionMessages++;
                sessionTokens += ev.Tokens.Total;
                sessionCost += cost;
            }

            var sessionProjectId = sessionProject ?? ResolveProject(record.Cwd);
            // A thread with no usage still needs its project to exist.
            acc.Touch(sessionProjectId, Dates.LocalDate(record.FirstTimestampMs ?? nowMs, settings.LocalUtcOffsetHours));

            sessions.Add(new SessionSummary
            {
                SessionId = record.File.SessionId,
                ProjectId = sessionProjectId,
                File = record.File.Rel,
                FirstTimestampMs = record.FirstTimestampMs,
                LastTimestampMs = record.LastTimestampMs,
                RuntimeSeconds = sessionRuntime,
                SpanSeconds = record.FirstTimestampMs is { } lo && record.LastTimestampMs is { } hi ? (hi - lo) / 1000d : 0,
                Models = sessionModels,
                Messages = sessionMessages,
                TotalTokens = sessionTokens,
                CostUsd = sessionCost,
                IsSubagent = record.IsSubagent,
                ParentSessionId = record.ParentThreadId,
                SubagentKind = record.SubagentKind,
                Title = threadNames.GetValueOrDefault(record.File.SessionId),
                // No streaming-placeholder bug here: the counters are cumulative
                // and self-checking. The flag keeps meaning what it means.
                PossiblyInaccurateOutput = false,
                SuspiciousMessageCount = 0,
            });
        }

        diagnostics.UniqueMessages = acc.Global.Combined.Messages;
        diagnostics.UnpricedModels = [.. unpriced];

        var projects = acc.Projects.Select(kv =>
        {
            var cwd = projectCwd.GetValueOrDefault(kv.Key);
            return new ProjectSummary
            {
                Id = kv.Key,
                Cwd = cwd,
                Name = projectConfig.DisplayNames.TryGetValue(kv.Key, out var display) ? display : ReportShaping.DerivedName(kv.Key, cwd),
                MergedFrom = mergedFrom.TryGetValue(kv.Key, out var m) ? m.ToList() : [],
                PerModel = kv.Value.Totals.SortedPerModel(),
                Combined = kv.Value.Totals.Combined,
                Daily = kv.Value.Daily.ToList(),
                Sessions = sessions.Where(s => s.ProjectId == kv.Key).OrderByDescending(s => s.CostUsd).ToList(),
            };
        }).ToList();

        return ReportShaping.Finish(ProviderId.Codex, sessionsRoot, settings, pricing, diagnostics, acc, projects, sessions, nowMs, excludeSyntheticFromFavorite: false);
    }

    private static List<DiscoveredFile> Discover(string home, List<string> warnings)
    {
        var result = new List<DiscoveredFile>();
        foreach (var root in SessionDirs(home)) Walk(root);
        return result;

        void Walk(string dir)
        {
            string[] entries, subdirs;
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
                var stem = Path.GetFileNameWithoutExtension(file);
                var match = TrailingUuid().Match(stem);
                result.Add(new DiscoveredFile(file, Path.GetRelativePath(home, file).Replace('\\', '/'), match.Success ? match.Value : stem));
            }
            foreach (var sub in subdirs.Order(StringComparer.Ordinal)) Walk(sub);
        }
    }

    /// <summary>
    /// Thread id to name, from Codex's own <c>session_index.jsonl</c>. Only
    /// <c>id</c> and <c>thread_name</c> are read. Optional and cosmetic.
    /// </summary>
    public static Dictionary<string, string> LoadThreadNames(string home, List<string> warnings)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var file = Path.Combine(home, "session_index.jsonl");
        if (!File.Exists(file)) return names;
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (Guards.StringField(doc.RootElement, "id") is { } id && Guards.StringField(doc.RootElement, "thread_name") is { } name)
                    {
                        names[id] = name;
                    }
                }
                catch (JsonException)
                {
                    // One bad line does not invalidate the rest of the index.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not read the Codex session index: {ex.Message}");
        }
        return names;
    }

    private static RawTotals ReadTotals(JsonElement usage) => new(
        Guards.TokenField(usage, "input_tokens"),
        Guards.TokenField(usage, "cached_input_tokens"),
        Guards.TokenField(usage, "cache_write_input_tokens"),
        Guards.TokenField(usage, "output_tokens"),
        Guards.TokenField(usage, "reasoning_output_tokens"),
        Guards.TokenField(usage, "total_tokens"));

    /// <summary>
    /// Per-turn usage as the delta of the running totals. A field going
    /// backwards means the counter restarted (a context reset): it is taken as
    /// a fresh baseline, never as negative usage.
    /// </summary>
    private static TokenCounts Delta(RawTotals previous, RawTotals current, out bool reset)
    {
        var anyReset = false;
        long Step(long before, long after)
        {
            var diff = after - before;
            if (diff >= 0) return diff;
            anyReset = true;
            return after;
        }

        var input = Step(previous.Input, current.Input);
        var cached = Step(previous.Cached, current.Cached);
        var cacheWrite = Step(previous.CacheWrite, current.CacheWrite);
        var output = Step(previous.Output, current.Output);
        var reasoning = Step(previous.Reasoning, current.Reasoning);
        reset = anyReset;

        return new TokenCounts(
            Input: Math.Max(0, input - cached), // Trap 2
            Output: output,
            CacheRead: cached,
            CacheWrite5m: cacheWrite,
            CacheWrite1h: 0,
            Reasoning: reasoning);
    }

    private static FileRecords ReadFile(DiscoveredFile file, long nowMs)
    {
        var fr = new FileRecords(file);
        string? currentModel = null, currentCwd = null;
        RawTotals previous = default;
        var havePrevious = false;

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
                    fr.Diag.LinesUnparseable++;
                    continue;
                }

                using (doc)
                {
                    var entry = doc.RootElement;
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        fr.Diag.LinesUnparseable++;
                        continue;
                    }

                    var tsRaw = Guards.StringField(entry, "timestamp");
                    var ts = Guards.ParseTimestampMs(tsRaw, nowMs);
                    if (ts is null && tsRaw is not null) fr.Diag.ImplausibleTimestamps++;
                    if (ts is { } t)
                    {
                        if (fr.FirstTimestampMs is null || t < fr.FirstTimestampMs) fr.FirstTimestampMs = t;
                        if (fr.LastTimestampMs is null || t > fr.LastTimestampMs) fr.LastTimestampMs = t;
                    }

                    var payload = Guards.TryObject(entry, "payload", out var p) ? p : default;
                    var type = Guards.StringField(entry, "type");

                    if (type == "session_meta")
                    {
                        if (Guards.StringField(payload, "cwd") is { } cwd)
                        {
                            currentCwd = cwd;
                            fr.Cwd ??= cwd;
                        }
                        // Present on every subagent rollout, absent on a real chat.
                        if (Guards.StringField(payload, "parent_thread_id") is { } parent) fr.ParentThreadId ??= parent;
                        if (Guards.StringField(payload, "thread_source") == "subagent")
                        {
                            fr.IsSubagent = true;
                            // `source: { subagent: { other: "guardian" } }` - the
                            // shape has moved before, so read it defensively.
                            string? kind = null;
                            if (Guards.TryObject(payload, "source", out var source) && Guards.TryObject(source, "subagent", out var sub))
                            {
                                kind = Guards.StringField(sub, "other") ?? Guards.StringField(sub, "kind") ?? Guards.StringField(sub, "type");
                            }
                            fr.SubagentKind ??= kind ?? "subagent";
                        }
                        // Some schema versions carried the model here.
                        if (Guards.StringField(payload, "model") is { } model) currentModel = model;
                    }
                    else if (type == "turn_context")
                    {
                        if (Guards.StringField(payload, "model") is { } model) currentModel = model;
                        if (Guards.StringField(payload, "cwd") is { } cwd)
                        {
                            currentCwd = cwd;
                            fr.Cwd ??= cwd;
                        }
                    }
                    else if (type == "event_msg" && Guards.StringField(payload, "type") == "token_count")
                    {
                        var hasInfo = Guards.TryObject(payload, "info", out var info);
                        JsonElement totalUsage = default;
                        var hasTotals = (hasInfo && Guards.TryObject(info, "total_token_usage", out totalUsage))
                                        || Guards.TryObject(payload, "total_token_usage", out totalUsage);
                        // Older shapes, and a null `info` on an aborted turn,
                        // carry no running total. Skipped, not guessed at.
                        if (!hasTotals)
                        {
                            fr.Diag.LinesUnparseable++;
                            continue;
                        }

                        fr.Diag.AssistantLines++;
                        var current = ReadTotals(totalUsage);

                        // Trap 1: identical running totals say nothing new,
                        // however large their last_token_usage looks.
                        if (havePrevious && current == previous)
                        {
                            fr.Repeats++;
                            continue;
                        }

                        var tokens = Delta(havePrevious ? previous : default, current, out var reset);
                        if (reset) fr.CounterResets++;
                        previous = current;
                        havePrevious = true;

                        // The independent check: Codex's own figure for the turn.
                        JsonElement lastUsage = default;
                        var hasLast = (hasInfo && Guards.TryObject(info, "last_token_usage", out lastUsage))
                                      || Guards.TryObject(payload, "last_token_usage", out lastUsage);
                        if (hasLast)
                        {
                            var reported = Guards.TokenField(lastUsage, "total_tokens");
                            var derived = tokens.Input + tokens.CacheRead + tokens.Output + tokens.CacheWrite5m;
                            if (reported != derived) fr.Reconciled = false;
                        }

                        fr.Events.Add(new UsageEvent(ts, currentModel ?? "(unknown)", currentCwd, tokens));
                    }

                    if (ts is { } tickMs) fr.Ticks.Add(new Tick(tickMs, currentModel, currentCwd));
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
}
