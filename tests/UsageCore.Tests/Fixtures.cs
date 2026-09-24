using System.Text.Json;
using System.Text.Json.Nodes;
using UsageCore.Config;
using UsageCore.Model;

namespace UsageCore.Tests;

/// <summary>
/// Fixture builders. Every fixture is written at run time into a temp directory
/// and deleted afterwards: nothing derived from a real transcript is committed.
/// </summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aiusage-test-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        for (var i = 0; i < 10; i++)
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

public static class Fixtures
{
    public static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    public static void WriteLines(string file, params object[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Join("\n", lines.Select(l => l is string s ? s : JsonSerializer.Serialize(l))) + "\n");
    }

    /* Claude Code */

    public static JsonObject Assistant(
        string ts,
        string id,
        string model = "test-model",
        string? cwd = null,
        double input = 0,
        double output = 0,
        double cacheRead = 0,
        double cacheWrite5m = 0,
        double cacheWrite1h = 0,
        double? flatCacheWrite = null,
        string? requestId = null)
    {
        var usage = new JsonObject
        {
            ["input_tokens"] = input,
            ["output_tokens"] = output,
            ["cache_read_input_tokens"] = cacheRead,
        };
        if (flatCacheWrite is { } flat) usage["cache_creation_input_tokens"] = flat;
        else usage["cache_creation"] = new JsonObject { ["ephemeral_5m_input_tokens"] = cacheWrite5m, ["ephemeral_1h_input_tokens"] = cacheWrite1h };

        var line = new JsonObject
        {
            ["type"] = "assistant",
            ["timestamp"] = ts,
            ["requestId"] = requestId ?? $"req_{id}",
            ["message"] = new JsonObject { ["id"] = id, ["model"] = model, ["usage"] = usage },
        };
        if (cwd is not null) line["cwd"] = cwd;
        return line;
    }

    public static JsonObject Plain(string ts, string? cwd = null)
    {
        var line = new JsonObject { ["type"] = "user", ["timestamp"] = ts };
        if (cwd is not null) line["cwd"] = cwd;
        return line;
    }

    /* Codex */

    public static JsonObject SessionMeta(string ts, string cwd, JsonObject? extra = null)
    {
        var payload = new JsonObject { ["cwd"] = cwd };
        if (extra is not null)
        {
            foreach (var (k, v) in extra) payload[k] = v?.DeepClone();
        }
        return new JsonObject { ["type"] = "session_meta", ["timestamp"] = ts, ["payload"] = payload };
    }

    public static JsonObject TurnContext(string ts, string model, string? cwd = null)
    {
        var payload = new JsonObject { ["model"] = model };
        if (cwd is not null) payload["cwd"] = cwd;
        return new JsonObject { ["type"] = "turn_context", ["timestamp"] = ts, ["payload"] = payload };
    }

    public sealed record Totals(string Ts, long Input, long Cached, long Output, long Reasoning = 0, long? LastTotal = null);

    /// <summary>A token_count whose running totals are <paramref name="t"/>.</summary>
    public static JsonObject TokenCount(Totals t, Totals? previous = null)
    {
        var derived = t.Input - (previous?.Input ?? 0) + (t.Output - (previous?.Output ?? 0));
        return new JsonObject
        {
            ["type"] = "event_msg",
            ["timestamp"] = t.Ts,
            ["payload"] = new JsonObject
            {
                ["type"] = "token_count",
                ["info"] = new JsonObject
                {
                    ["total_token_usage"] = new JsonObject
                    {
                        ["input_tokens"] = t.Input,
                        ["cached_input_tokens"] = t.Cached,
                        ["output_tokens"] = t.Output,
                        ["reasoning_output_tokens"] = t.Reasoning,
                        ["total_tokens"] = t.Input + t.Output,
                    },
                    ["last_token_usage"] = new JsonObject { ["total_tokens"] = t.LastTotal ?? derived },
                },
            },
        };
    }

    public static string Rollout(string threadId) => $"rollout-2026-08-01T12-00-00-{threadId}.jsonl";

    /* Config */

    /// <summary>Round numbers, so expected costs are obvious by hand.</summary>
    public static PricingConfig Pricing(Dictionary<string, string>? aliases = null) => new()
    {
        Models = new Dictionary<string, ModelRate>(StringComparer.Ordinal)
        {
            ["test-model"] = new(10, 12.5, 20, 1, 50),
            ["cheap-model"] = new(1, 1.25, 2, 0.1, 5),
        },
        Aliases = aliases ?? new Dictionary<string, string>(StringComparer.Ordinal),
    };

    public static Settings Settings(double maxIdleGapMinutes = 30) =>
        new(0, WeekStart.Monday, maxIdleGapMinutes, 5, 1000);

    public static Parsing.ParseOptions Options(string root, PricingConfig? pricing = null, ProjectConfig? projects = null) => new()
    {
        Root = root,
        Pricing = pricing ?? Pricing(),
        Settings = Settings(),
        ProjectConfig = projects ?? ProjectConfig.None,
        Now = () => Now,
    };
}
