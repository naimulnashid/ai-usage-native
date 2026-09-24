using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UsageCli;

/// <summary>
/// Writes a synthetic transcript tree for both agents.
/// </summary>
/// <remarks>
/// <para>Every screenshot of this app is otherwise a screenshot of somebody's
/// real projects and spend. This produces a tree that looks like real usage and
/// contains nothing real.</para>
/// <para>It is not a mock: the output goes through the real parsers, so it has
/// to reproduce the traps they exist for. Claude Code: streaming partials with a
/// growing output_tokens, replayed history shared between files, nested
/// subagents, cache writes split by TTL. Codex: cumulative totals, a repeated
/// reading, cached tokens inside input, reasoning inside output, auto-review
/// threads under their own model. A demo parse should report duplicates skipped
/// and output recovered, and <c>files reconciled: N ok / 0 mismatched</c>.</para>
/// <para>Seeded, so the same command produces the same data (relative to today).</para>
/// </remarks>
internal static class DemoData
{
    private static uint _seed = 20260921;

    /// <summary>mulberry32.</summary>
    private static double Rand()
    {
        _seed += 0x6D2B79F5;
        var t = _seed;
        t = unchecked((t ^ (t >> 15)) * (1 | t));
        t = unchecked(t + (t ^ (t >> 7)) * (61 | t)) ^ t;
        return (t ^ (t >> 14)) / 4294967296d;
    }

    private static int Int(int lo, int hi) => lo + (int)Math.Floor(Rand() * (hi - lo + 1));
    private static T Pick<T>(IReadOnlyList<T> xs) => xs[Int(0, xs.Count - 1)];
    private static string Hex(int n)
    {
        var sb = new StringBuilder(n);
        for (var i = 0; i < n; i++) sb.Append("0123456789abcdef"[Int(0, 15)]);
        return sb.ToString();
    }
    private static string Uuid() => $"{Hex(8)}-{Hex(4)}-{Hex(4)}-{Hex(4)}-{Hex(12)}";

    private const string Home = @"C:\Users\you\Projects";
    private const int Days = 54;

    // Obviously fictional, on purpose.
    private static readonly (string Name, int Weight)[] ClaudeProjects =
        [("Recipe Box", 34), ("Weather Widget", 22), ("Invoice Tool", 18), ("Bird Log", 12), ("Chess Clock", 8), ("Tide Table", 6)];

    private static readonly (string Name, int Weight)[] CodexProjects = [("Recipe Box", 62), ("Label Printer", 38)];

    private static readonly (string Id, int Share)[] ClaudeModels =
        [("claude-opus-5", 46), ("claude-opus-4-8", 18), ("claude-fable-5", 14), ("claude-sonnet-5", 20), ("<synthetic>", 2)];

    private static string Weighted((string Id, int Share)[] xs)
    {
        var total = xs.Sum(x => x.Share);
        var r = Rand() * total;
        foreach (var x in xs)
        {
            r -= x.Share;
            if (r <= 0) return x.Id;
        }
        return xs[^1].Id;
    }

    private static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>Working hours, so the heat map and peak hour have a shape.</summary>
    private static long DayStart(int daysAgo)
    {
        var midnight = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();
        return midnight - daysAgo * 86_400_000L + Int(8, 15) * 3_600_000L;
    }

    private static string EncodeDir(string cwd) =>
        new(cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());

    private static void WriteJsonl(string file, IEnumerable<JsonNode> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Join("\n", lines.Select(l => l.ToJsonString())) + "\n");
    }

    public static int Run(string target)
    {
        target = Path.GetFullPath(target);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);

        var claude = WriteClaude(target);
        var codex = WriteCodex(target);

        Console.WriteLine($"Demo transcripts written to {target}");
        Console.WriteLine($"  Claude Code  {claude.Files} files ({claude.Sessions} sessions, plus subagents)");
        Console.WriteLine($"  Codex        {codex.Files} files ({codex.Threads} threads, plus auto-reviews)");
        Console.WriteLine();
        Console.WriteLine("Point the app at them (PowerShell):");
        Console.WriteLine();
        Console.WriteLine($"  $env:CLAUDE_CONFIG_DIR = '{Path.Combine(target, "claude")}'");
        Console.WriteLine($"  $env:CODEX_HOME = '{Path.Combine(target, "codex")}'");
        Console.WriteLine($"  $env:AIUSAGE_DATA_DIR = '{Path.Combine(target, "appdata")}'");
        Console.WriteLine();
        Console.WriteLine("AIUSAGE_DATA_DIR is not optional: without it these synthetic days are");
        Console.WriteLine("folded into your real archive, and the archive keeps whichever copy of");
        Console.WriteLine("a day has more messages. That is not reversible.");
        return 0;
    }

    /// <summary>One assistant turn as Claude Code writes it: partials, then the truth.</summary>
    private static List<JsonObject> AssistantTurn(long at, string cwd, string model)
    {
        var id = $"msg_{Hex(20)}";
        var requestId = $"req_{Hex(16)}";
        var input = Int(3, 40);
        var cacheRead = Int(12_000, 190_000);
        var write5m = Int(900, 14_000);
        var write1h = Rand() < 0.25 ? Int(400, 6_000) : 0;
        var finalOutput = Int(120, 3_400);
        var steps = Int(2, 4);

        var lines = new List<JsonObject>();
        for (var i = 0; i <= steps; i++)
        {
            var last = i == steps;
            lines.Add(new JsonObject
            {
                ["type"] = "assistant",
                ["timestamp"] = Iso(at + i * Int(700, 4_000)),
                ["cwd"] = cwd,
                ["requestId"] = requestId,
                ["message"] = new JsonObject
                {
                    ["id"] = id,
                    ["model"] = model,
                    ["usage"] = new JsonObject
                    {
                        ["input_tokens"] = input,
                        // The placeholder early partials carry (Trap 2).
                        ["output_tokens"] = last ? finalOutput : Math.Max(1, (int)Math.Round(finalOutput * (i / (double)steps))),
                        ["cache_read_input_tokens"] = cacheRead,
                        ["cache_creation"] = new JsonObject
                        {
                            ["ephemeral_5m_input_tokens"] = write5m,
                            ["ephemeral_1h_input_tokens"] = write1h,
                        },
                    },
                },
            });
        }
        return lines;
    }

    private static (int Files, int Sessions) WriteClaude(string root)
    {
        var dir = Path.Combine(root, "claude", "projects");
        Directory.CreateDirectory(dir);
        int files = 0, sessions = 0;
        // One earlier session per project, so a later one can replay it.
        var previous = new Dictionary<string, List<JsonObject>>();

        for (var daysAgo = Days; daysAgo >= 0; daysAgo--)
        {
            var dow = DateTimeOffset.FromUnixTimeMilliseconds(DayStart(daysAgo)).UtcDateTime.DayOfWeek;
            var busy = dow is DayOfWeek.Sunday or DayOfWeek.Saturday ? Rand() < 0.35 : Rand() < 0.88;
            if (!busy) continue;

            foreach (var (name, weight) in ClaudeProjects)
            {
                if (Rand() * 100 > weight) continue;
                var cwd = $@"{Home}\{name}";
                var projectDir = Path.Combine(dir, EncodeDir(cwd));
                var sessionId = Uuid();
                var at = DayStart(daysAgo);
                var lines = new List<JsonObject>();

                // Trap 1, second mechanism: a resumed session replays history.
                if (previous.TryGetValue(name, out var replay) && Rand() < 0.4)
                {
                    lines.AddRange(replay.Take(Int(4, 12)).Select(l => (JsonObject)l.DeepClone()));
                }

                var turns = Int(4, 22);
                for (var t = 0; t < turns; t++)
                {
                    lines.Add(new JsonObject { ["type"] = "user", ["timestamp"] = Iso(at), ["cwd"] = cwd });
                    at += Int(4_000, 40_000);
                    lines.AddRange(AssistantTurn(at, cwd, Weighted(ClaudeModels)));
                    at += Int(20_000, 260_000);
                    // A long gap the idle cutoff should drop.
                    if (Rand() < 0.08) at += Int(40, 220) * 60_000L;
                }

                WriteJsonl(Path.Combine(projectDir, $"{sessionId}.jsonl"), lines);
                previous[name] = lines.Where(l => (string?)l["type"] == "assistant").Take(12).ToList();
                files++;
                sessions++;

                // Trap 3: subagents nest a level down.
                if (Rand() < 0.22)
                {
                    var subAt = DayStart(daysAgo) + Int(60_000, 400_000);
                    var subLines = new List<JsonObject>();
                    var subTurns = Int(2, 7);
                    for (var t = 0; t < subTurns; t++)
                    {
                        subLines.AddRange(AssistantTurn(subAt, cwd, Weighted(ClaudeModels)));
                        subAt += Int(15_000, 120_000);
                    }
                    WriteJsonl(Path.Combine(projectDir, sessionId, "subagents", $"agent-{Hex(8)}.jsonl"), subLines);
                    files++;
                }
            }
        }
        return (files, sessions);
    }

    private static (int Files, int Threads) WriteCodex(string root)
    {
        var home = Path.Combine(root, "codex");
        var sessionsRoot = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessionsRoot);
        var index = new List<string>();
        int files = 0, threads = 0;
        string[] threadNames = ["Fix the printer queue", "Port the importer", "Tidy the label layout", "Chase a flaky test", "Rewrite the CSV reader"];

        for (var daysAgo = Days; daysAgo >= 0; daysAgo--)
        {
            if (Rand() < 0.45) continue;

            foreach (var (name, weight) in CodexProjects)
            {
                if (Rand() * 100 > weight) continue;
                var cwd = $@"{Home}\{name}";
                var start = DayStart(daysAgo);
                var day = DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime;
                var dir = Path.Combine(sessionsRoot, day.Year.ToString("0000"), day.Month.ToString("00"), day.Day.ToString("00"));

                var threadId = Uuid();
                threads++;
                index.Add(JsonSerializer.Serialize(new { id = threadId, thread_name = Pick(threadNames) }));

                void Write(string id, string model, string? parent, int turns)
                {
                    var lines = new List<JsonNode>();
                    var at = start + (parent is not null ? Int(30_000, 300_000) : 0);
                    var meta = new JsonObject { ["id"] = id, ["cwd"] = cwd };
                    if (parent is not null)
                    {
                        meta["parent_thread_id"] = parent;
                        meta["thread_source"] = "subagent";
                    }
                    lines.Add(new JsonObject { ["type"] = "session_meta", ["timestamp"] = Iso(at), ["payload"] = meta });
                    // Trap 5: the model is state, written only when it changes.
                    lines.Add(new JsonObject { ["type"] = "turn_context", ["timestamp"] = Iso(at), ["payload"] = new JsonObject { ["model"] = model, ["cwd"] = cwd } });

                    // Trap 1: totals are CUMULATIVE.
                    long tIn = 0, tCached = 0, tOut = 0, tReason = 0, tTotal = 0;
                    for (var t = 0; t < turns; t++)
                    {
                        at += Int(8_000, 90_000);
                        var cached = Int(20_000, 240_000);
                        var fresh = Int(200, 2_600); // Trap 2: input INCLUDES cached
                        var output = Int(150, 2_900);
                        var reasoning = (int)Math.Round(output * (0.3 + Rand() * 0.45)); // Trap 3: inside output
                        tIn += cached + fresh;
                        tCached += cached;
                        tOut += output;
                        tReason += reasoning;
                        tTotal += cached + fresh + output;

                        JsonObject Reading(long ts) => new()
                        {
                            ["type"] = "event_msg",
                            ["timestamp"] = Iso(ts),
                            ["payload"] = new JsonObject
                            {
                                ["type"] = "token_count",
                                ["info"] = new JsonObject
                                {
                                    ["total_token_usage"] = new JsonObject
                                    {
                                        ["input_tokens"] = tIn, ["cached_input_tokens"] = tCached, ["cache_write_input_tokens"] = 0,
                                        ["output_tokens"] = tOut, ["reasoning_output_tokens"] = tReason, ["total_tokens"] = tTotal,
                                    },
                                    ["last_token_usage"] = new JsonObject
                                    {
                                        ["input_tokens"] = cached + fresh, ["cached_input_tokens"] = cached, ["cache_write_input_tokens"] = 0,
                                        ["output_tokens"] = output, ["reasoning_output_tokens"] = reasoning, ["total_tokens"] = cached + fresh + output,
                                    },
                                },
                            },
                        };

                        lines.Add(Reading(at));
                        // A repeated reading, which the delta method makes worth zero.
                        if (Rand() < 0.09) lines.Add(Reading(at + 400));
                    }

                    var stamp = day.ToString("yyyy-MM-ddTHH-mm-ss");
                    WriteJsonl(Path.Combine(dir, $"rollout-{stamp}-{id}.jsonl"), lines);
                    files++;
                }

                Write(threadId, "gpt-5.6-sol", null, Int(6, 40));
                // Trap 4: guardian auto-reviews, their own files and model band.
                var reviews = Int(0, 3);
                for (var r = 0; r < reviews; r++) Write(Uuid(), "codex-auto-review", threadId, Int(2, 9));
            }
        }

        File.WriteAllText(Path.Combine(home, "session_index.jsonl"), string.Join("\n", index) + "\n");
        return (files, threads);
    }
}
