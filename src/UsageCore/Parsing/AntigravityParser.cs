using Microsoft.Data.Sqlite;
using UsageCore.Config;
using UsageCore.Model;
using F = UsageCore.Parsing.AntigravityFormat;

namespace UsageCore.Parsing;

/// <summary>
/// Antigravity's conversations: one SQLite database per conversation under
/// <c>~/.gemini/antigravity/conversations/&lt;cascade-id&gt;.db</c>, and under
/// <c>antigravity-ide/</c> for the older IDE build. The rows are protobuf with no
/// published schema; the field numbers live in <see cref="AntigravityFormat"/>.
/// </summary>
/// <remarks>
/// <para><b>Trap 1 - the same conversation can be on disk twice.</b> The current
/// app copied the IDE build's conversations into its own folder and left the
/// originals, same file name, same rows. Counting both doubles them. A cascade id
/// is read once: from the copy with the most requests, the current app's on a tie.</para>
/// <para><b>Trap 2 - input excludes the cached prompt.</b> Unlike OpenAI's, the
/// input count is the uncached remainder (a turn's input can be a tenth of its
/// cache read), so it is taken as it is. The prompt for the long-context tier is
/// the two added.</para>
/// <para><b>Trap 3 - thinking is INSIDE output.</b> Output is always thinking
/// plus response. Carried as <c>Reasoning</c>, never added.</para>
/// <para><b>Trap 4 - subagents are conversations of their own</b>, in their own
/// file, labelled with their parent's cascade id. Real, separate spend: counted,
/// and folded into the parent for the records.</para>
/// <para><b>Trap 5 - a model id can be a moving alias.</b> <c>gemini-pro-default</c>
/// is whichever Pro is current; its display name ("Gemini 3.1 Pro (High)") says
/// which, and decides the model (<see cref="ModelKey"/>).</para>
/// <para><b>Trap 6 - some spend has no request row.</b> A browser subagent's
/// whole run is written as the usage of its tool step, and nowhere else (one
/// real run was over a third of its conversation's tokens); small internal calls are written the same way
/// on steps of their own. Each is counted as a request, under the model its
/// usage names by number: the id another request with that number had, else
/// <c>(model N)</c>, which is unpriced rather than guessed.</para>
/// A request's time is its own step's, from <c>steps</c>; the same step also
/// carries the request's usage, which is the independent check
/// (<c>ReconcileFailures</c>). No cache writes are logged, Claude models included.
/// </remarks>
public static class AntigravityParser
{
    private sealed record DiscoveredFile(string Abs, string Rel, string CascadeId, int RootOrder);

    /// <param name="Model">Null for usage that names its model only by number (Trap 6).</param>
    private readonly record struct Request(long? TimestampMs, string? Model, long ModelNumber, TokenCounts Tokens);

    private readonly record struct Tick(long TimestampMs, string? Model);

    private sealed class Conversation(DiscoveredFile file)
    {
        public DiscoveredFile File { get; } = file;
        public List<Request> Requests { get; } = [];
        public List<Tick> Ticks { get; } = [];

        /// <summary>Antigravity's model numbers, as this file's requests named them.</summary>
        public Dictionary<long, string> ModelNumbers { get; } = [];
        public long? FirstTimestampMs { get; set; }
        public long? LastTimestampMs { get; set; }
        public string? Cwd { get; set; }
        public string? ParentId { get; set; }
        public string? SubagentKind { get; set; }
        public long Rows { get; set; }
        public bool Reconciled { get; set; } = true;
        public bool Read { get; set; }
        public FileDiagnostics Diag { get; } = new();
    }

    /// <summary>Both folders conversations are kept in, current app first.</summary>
    public static string[] ConversationRoots(string home) =>
        [Path.Combine(home, "antigravity", "conversations"), Path.Combine(home, "antigravity-ide", "conversations")];

    public static UsageReport Parse(ParseOptions? options = null)
    {
        options ??= new ParseOptions();
        var home = options.Root ?? AppPaths.AntigravityHome;
        var pricing = options.Pricing ?? ModelSettings.LoadEffectivePricing(ProviderId.Antigravity);
        var settings = options.Settings ?? AppConfig.LoadSettings();
        var projectConfig = options.ProjectConfig ?? ProjectSettings.LoadEffective(ProviderId.Antigravity);
        var nowMs = (options.Now?.Invoke() ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

        var diagnostics = new ParseDiagnostics { ReconciledFiles = 0, ReconcileFailures = 0, LongContextRequests = 0 };
        var root = ConversationRoots(home)[0];
        var files = Discover(home, diagnostics.Warnings);
        diagnostics.FilesScanned = files.Count;
        if (files.Count == 0)
        {
            diagnostics.Warnings.Add($"No Antigravity conversations found under {home}. Set {AppPaths.AntigravityHomeVariable} if yours live elsewhere.");
        }

        var read = new Conversation[files.Count];
        var pool = new StringPool();
        Parallel.For(0, files.Count, i => read[i] = ReadFile(files[i], nowMs, pool));
        foreach (var c in read) c.Diag.MergeInto(diagnostics);

        // Trap 1: one copy per cascade id.
        var kept = new List<Conversation>();
        foreach (var group in read.Where(c => c.Read).GroupBy(c => c.File.CascadeId, StringComparer.OrdinalIgnoreCase))
        {
            var best = group.OrderByDescending(c => c.Rows).ThenBy(c => c.File.RootOrder).First();
            kept.Add(best);
            foreach (var other in group)
            {
                if (!ReferenceEquals(other, best)) diagnostics.DuplicateLinesSkipped += other.Rows;
            }
        }
        kept.Sort((a, b) => (a.FirstTimestampMs ?? long.MaxValue).CompareTo(b.FirstTimestampMs ?? long.MaxValue));

        // Trap 6: a number is named by any file's requests, not only its own.
        var modelNumbers = new Dictionary<long, string>();
        foreach (var c in kept)
        {
            foreach (var (number, model) in c.ModelNumbers) modelNumbers.TryAdd(number, model);
        }
        string ModelOf(in Request r) =>
            r.Model ?? (modelNumbers.TryGetValue(r.ModelNumber, out var named) ? named : pool.Get($"(model {r.ModelNumber})"));

        var acc = new Accumulator(tracksReasoning: true);
        var projectCwd = new Dictionary<string, string>(StringComparer.Ordinal);
        var mergedFrom = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var sessions = new List<SessionSummary>();
        var unpriced = new SortedSet<string>(StringComparer.Ordinal);
        var maxIdleGapSeconds = settings.MaxIdleGapMinutes * 60;
        const string Unplaced = "(no workspace)";

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        string ResolveProject(string? cwd)
        {
            if (cwd is null) return Unplaced;
            if (resolved.TryGetValue(cwd, out var cached)) return cached;
            var sourceId = CodexParser.ProjectIdFromCwd(cwd);
            var targetId = AppConfig.ResolveProjectId(sourceId, projectConfig.Merge, diagnostics.Warnings);
            if (sourceId == targetId) projectCwd.TryAdd(targetId, cwd);
            else
            {
                if (!mergedFrom.TryGetValue(targetId, out var set)) mergedFrom[targetId] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(sourceId);
            }
            return resolved[cwd] = targetId;
        }

        foreach (var c in kept)
        {
            if (c.Reconciled) diagnostics.ReconciledFiles++;
            else diagnostics.ReconcileFailures++;
            var projectId = ResolveProject(c.Cwd);

            double runtime = 0;
            long? previous = null;
            foreach (var tick in c.Ticks)
            {
                if (previous is { } p && tick.Model is not null)
                {
                    var gap = (tick.TimestampMs - p) / 1000d;
                    if (gap > 0 && gap <= maxIdleGapSeconds)
                    {
                        runtime += gap;
                        acc.AddRuntime(projectId, Dates.LocalDate(tick.TimestampMs, settings.LocalUtcOffsetHours), tick.Model, gap);
                    }
                }
                previous = tick.TimestampMs;
            }

            long messages = 0, tokensTotal = 0;
            double costTotal = 0;
            var models = new List<string>();
            foreach (var request in c.Requests)
            {
                var model = ModelOf(request);
                if (!models.Contains(model)) models.Add(model);
                var rate = AppConfig.GetRate(pricing, model);
                if (rate is null) unpriced.Add(model);
                var tokens = request.Tokens;
                // The tier is decided on the whole prompt, cached part included (Trap 2).
                if (rate?.LongContext is { } rule && tokens.Input + tokens.CacheRead > rule.AboveInputTokens)
                {
                    tokens = tokens with { LongContext = tokens.Priced };
                    diagnostics.LongContextRequests++;
                }
                var cost = AppConfig.CostOf(tokens, rate);
                var date = request.TimestampMs is { } t ? Dates.LocalDate(t, settings.LocalUtcOffsetHours) : Dates.UnknownDate;
                if (request.TimestampMs is { } t2 && Dates.LocalHour(t2, settings.LocalUtcOffsetHours) is { } hour) acc.AddHour(projectId, hour);
                acc.AddMessage(projectId, date, model, tokens, cost, rate is null);
                messages++;
                tokensTotal += tokens.Total;
                costTotal += cost;
            }
            acc.Touch(projectId, Dates.LocalDate(c.FirstTimestampMs ?? nowMs, settings.LocalUtcOffsetHours));

            sessions.Add(new SessionSummary
            {
                SessionId = c.File.CascadeId,
                ProjectId = projectId,
                File = c.File.Rel,
                FirstTimestampMs = c.FirstTimestampMs,
                LastTimestampMs = c.LastTimestampMs,
                RuntimeSeconds = runtime,
                SpanSeconds = c.FirstTimestampMs is { } lo && c.LastTimestampMs is { } hi ? (hi - lo) / 1000d : 0,
                Models = models,
                Messages = messages,
                TotalTokens = tokensTotal,
                CostUsd = costTotal,
                IsSubagent = c.ParentId is not null,
                ParentSessionId = c.ParentId,
                SubagentKind = c.ParentId is null ? null : c.SubagentKind ?? "subagent",
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

        return ReportShaping.Finish(ProviderId.Antigravity, root, settings, pricing, diagnostics, acc, projects, sessions, nowMs);
    }

    /// <summary>
    /// The model a request is counted under: the logged id, except a moving alias
    /// (<c>*-default</c>), which takes its display name without the effort level -
    /// "Gemini 3.1 Pro (High)" is <c>gemini-3.1-pro</c>.
    /// </summary>
    public static string ModelKey(string? id, string? displayName)
    {
        if (id is { Length: > 0 } && !id.EndsWith("-default", StringComparison.Ordinal)) return id;
        if (displayName is { Length: > 0 })
        {
            var name = displayName;
            var paren = name.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) name = name[..paren];
            name = string.Join('-', name.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (name.Length > 0) return name;
        }
        return id is { Length: > 0 } ? id : "(unknown)";
    }

    private static List<DiscoveredFile> Discover(string home, List<string> warnings)
    {
        var result = new List<DiscoveredFile>();
        var roots = ConversationRoots(home);
        for (var order = 0; order < roots.Length; order++)
        {
            if (!Directory.Exists(roots[order])) continue;
            string[] entries;
            try
            {
                entries = Directory.GetFiles(roots[order], "*.db");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Skipped unreadable directory {roots[order]}: {ex.Message}");
                continue;
            }
            foreach (var file in entries.Order(StringComparer.Ordinal))
            {
                // A search pattern is matched against 8.3 short names too, so it can
                // catch more than it says; the -wal and -shm files must not get in.
                if (!file.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new DiscoveredFile(file, Path.GetRelativePath(home, file).Replace('\\', '/'), Path.GetFileNameWithoutExtension(file), order));
            }
        }
        return result;
    }

    private static Conversation ReadFile(DiscoveredFile file, long nowMs, StringPool pool)
    {
        var c = new Conversation(file);
        // Read-only, and never pooled: a pooled handle would keep the file open
        // after the parse, and Antigravity deletes conversations.
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = file.Abs,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();
        try
        {
            using var connection = new SqliteConnection(cs);
            connection.Open();
            if (!HasTables(connection))
            {
                c.Diag.Warnings.Add($"Skipped {file.Rel}: not an Antigravity conversation (tables missing).");
                c.Diag.FilesFailed++;
                return c;
            }

            ReadMetadata(connection, c);
            var steps = new Dictionary<long, StepInfo>();
            ReadSteps(connection, c, steps, nowMs);
            var stepModel = new Dictionary<long, string>();
            var owned = new HashSet<long>();
            ReadRequests(connection, c, steps, stepModel, owned, pool);

            // Trap 6: usage on any step that is not a request's own.
            foreach (var (idx, step) in steps.OrderBy(kv => kv.Key))
            {
                if (step.Usage is not { } u || owned.Contains(idx) || u.Tokens.Total == 0) continue;
                c.Diag.AssistantLines++;
                c.Requests.Add(new Request(step.TimestampMs, null, u.ModelNumber, u.Tokens));
            }

            // Runtime ticks: every step, carrying the model of the request that
            // produced it, or the latest one before it.
            string? model = null;
            foreach (var (idx, step) in steps.OrderBy(kv => kv.Key))
            {
                if (stepModel.TryGetValue(idx, out var m)) model = m;
                if (step.TimestampMs is { } ms) c.Ticks.Add(new Tick(ms, model));
            }
            c.Ticks.Sort((a, b) => a.TimestampMs.CompareTo(b.TimestampMs));
            c.Read = true;
        }
        catch (SqliteException ex)
        {
            c.Diag.FilesFailed++;
            c.Diag.Warnings.Add($"Error reading {file.Rel}: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            c.Diag.FilesFailed++;
            c.Diag.Warnings.Add($"Error reading {file.Rel}: {ex.Message}");
        }
        return c;
    }

    private static bool HasTables(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('gen_metadata', 'steps')";
        return Convert.ToInt64(cmd.ExecuteScalar()) == 2;
    }

    private static void ReadMetadata(SqliteConnection connection, Conversation c)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT data FROM trajectory_metadata_blob LIMIT 1";
        if (cmd.ExecuteScalar() is not byte[] data) return;
        try
        {
            var r = new ProtoReader(data);
            while (r.Next())
            {
                if (r.Field == F.MetaWorkspace && r.WireType == 2 && c.Cwd is null)
                {
                    var w = new ProtoReader(r.Bytes());
                    while (w.Next())
                    {
                        if (w.Field == F.WorkspaceUri && w.WireType == 2) c.Cwd = LocalPath(w.String());
                        else w.Skip();
                    }
                }
                else r.Skip();
            }
        }
        catch (ProtoException)
        {
            // No workspace is a usable answer; the usage does not depend on it.
            c.Diag.LinesUnparseable++;
        }
    }

    /// <summary><c>file:///d:/AI/My%20App</c> as <c>D:\AI\My App</c>; null for anything else.</summary>
    internal static string? LocalPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return null;
        var path = parsed.LocalPath.TrimEnd('\\', '/');
        // Antigravity writes the drive in lower case; Windows and the other agents do not.
        return path.Length >= 2 && path[1] == ':' ? char.ToUpperInvariant(path[0]) + path[1..] : path;
    }

    private readonly record struct Usage(long ModelNumber, TokenCounts Tokens);

    private sealed class StepInfo
    {
        public long Type;
        public long? TimestampMs;
        public Usage? Usage;
    }

    private static void ReadSteps(SqliteConnection connection, Conversation c, Dictionary<long, StepInfo> steps, long nowMs)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT idx, step_type, metadata FROM steps ORDER BY idx";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(2)) continue;
            var step = new StepInfo { Type = reader.GetInt64(1) };
            var data = (byte[])reader[2];
            try
            {
                var r = new ProtoReader(data);
                while (r.Next())
                {
                    if (r.Field == F.StepCreated && r.WireType == 2)
                    {
                        if (ProtoReader.TimestampMs(r.Bytes()) is not { } ms) continue;
                        if (ms < Guards.EarliestPlausibleMs || ms > nowMs + Guards.MaxFutureSkewMs)
                        {
                            c.Diag.ImplausibleTimestamps++;
                            continue;
                        }
                        step.TimestampMs = ms;
                        if (c.FirstTimestampMs is null || ms < c.FirstTimestampMs) c.FirstTimestampMs = ms;
                        if (c.LastTimestampMs is null || ms > c.LastTimestampMs) c.LastTimestampMs = ms;
                    }
                    else if (r.Field == F.StepUsage && r.WireType == 2) step.Usage = ReadUsage(r.Bytes());
                    else r.Skip();
                }
            }
            catch (ProtoException)
            {
                c.Diag.LinesUnparseable++;
                continue;
            }
            steps[reader.GetInt64(0)] = step;
        }
    }

    private static void ReadRequests(
        SqliteConnection connection,
        Conversation c,
        Dictionary<long, StepInfo> stepInfo,
        Dictionary<long, string> stepModel,
        HashSet<long> owned,
        StringPool pool)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT data FROM gen_metadata ORDER BY idx";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            c.Rows++;
            c.Diag.LinesRead++;
            var data = (byte[])reader[0];
            string? id = null, name = null, parent = null, kind = null;
            Usage? usage = null;
            var steps = new List<long>();
            try
            {
                var r = new ProtoReader(data);
                while (r.Next())
                {
                    if (r.Field == F.GenSteps)
                    {
                        if (r.WireType == 0) steps.Add((long)r.Varint());
                        else
                        {
                            var packed = new ProtoReader(r.Bytes());
                            // A packed list is bare varints, with no keys between them.
                            while (packed.HasMore) steps.Add((long)packed.Varint());
                        }
                    }
                    else if (r.Field == F.GenRequest && r.WireType == 2)
                    {
                        var g = new ProtoReader(r.Bytes());
                        while (g.Next())
                        {
                            if (g.Field == F.RequestUsage && g.WireType == 2) usage = ReadUsage(g.Bytes());
                            else if (g.Field == F.RequestModel && g.WireType == 2) id = g.String();
                            else if (g.Field == F.RequestModelName && g.WireType == 2) name = g.String();
                            else if (g.Field == F.RequestLabel && g.WireType == 2)
                            {
                                var (key, value) = ReadLabel(g.Bytes());
                                if (key == F.ParentLabel) parent = value;
                                else if (key == F.SubagentLabel) kind = value;
                            }
                            else g.Skip();
                        }
                    }
                    else r.Skip();
                }
            }
            catch (ProtoException)
            {
                c.Diag.LinesUnparseable++;
                continue;
            }

            if (parent is { Length: > 0 } && !string.Equals(parent, c.File.CascadeId, StringComparison.OrdinalIgnoreCase))
            {
                c.ParentId ??= parent;
                c.SubagentKind ??= kind;
            }

            // A request that was cut off before it ran logs an empty usage: nothing billed.
            if (usage is not { } u || u.Tokens.Total == 0) continue;
            c.Diag.AssistantLines++;
            var model = pool.Get(ModelKey(id, name));
            if (u.ModelNumber > 0) c.ModelNumbers.TryAdd(u.ModelNumber, model);
            foreach (var s in steps) stepModel.TryAdd(s, model);

            // The request's own step is its model step. It carries the same usage
            // again, which is the check that the field numbers still mean what they did.
            long? at = null;
            foreach (var s in steps)
            {
                if (!stepInfo.TryGetValue(s, out var step)) continue;
                at ??= step.TimestampMs;
                if (step.Type != F.ModelStepType || step.Usage is not { } own || !owned.Add(s)) continue;
                if (own.Tokens.Priced != u.Tokens.Priced) c.Reconciled = false;
            }
            c.Requests.Add(new Request(at, model, u.ModelNumber, u.Tokens));
        }
    }

    private static Usage ReadUsage(ReadOnlySpan<byte> data)
    {
        long number = 0, input = 0, output = 0, cacheRead = 0, thinking = 0;
        var r = new ProtoReader(data);
        while (r.Next())
        {
            if (r.Field == F.UsageModel) number = r.Count();
            else if (r.Field == F.UsageInput) input = r.Count();
            else if (r.Field == F.UsageOutput) output = r.Count();
            else if (r.Field == F.UsageCacheRead) cacheRead = r.Count();
            else if (r.Field == F.UsageThinking) thinking = r.Count();
            else r.Skip();
        }
        return new Usage(number, new TokenCounts(input, output, cacheRead, 0, 0, Reasoning: thinking));
    }

    private static (string? Key, string? Value) ReadLabel(ReadOnlySpan<byte> data)
    {
        string? key = null, value = null;
        var r = new ProtoReader(data);
        while (r.Next())
        {
            if (r.Field == F.LabelKey && r.WireType == 2) key = r.String();
            else if (r.Field == F.LabelValue && r.WireType == 2) value = r.String();
            else r.Skip();
        }
        return (key, value);
    }
}
