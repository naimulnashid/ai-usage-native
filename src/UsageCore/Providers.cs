using UsageCore.Model;

namespace UsageCore;

/// <summary>
/// Everything that differs between the two agents and is not a number. The UI
/// reads this rather than branching on an id: no view may hard-code an agent's
/// name or wording.
/// </summary>
public sealed record ProviderMeta
{
    public required ProviderId Id { get; init; }
    public required string Label { get; init; }

    /// <summary>Where the transcripts live, for the empty state.</summary>
    public required string TranscriptHint { get; init; }
    public required string TranscriptEnvVar { get; init; }

    /// <summary>
    /// The headline label, and the ENTIRE in-app treatment of the cost basis.
    /// Claude Code's figure tracks a per-token bill; Codex is commonly used on a
    /// flat subscription, so its figure is only "what these tokens would cost
    /// through the API". The two are never comparable spend and never added.
    /// </summary>
    public required string CostLabel { get; init; }

    /// <summary>Codex's cache-write field has always been 0, so the column would be a stripe of zeroes.</summary>
    public required bool HasCacheWrites { get; init; }
    public required bool HasReasoningTokens { get; init; }
    public required string CacheReadLabel { get; init; }
    public required string MessageNoun { get; init; }
    public required string MessageTip { get; init; }
    public required string SessionTip { get; init; }
    public required string SubagentNoun { get; init; }

    /// <summary>Accent colour and its heat-map ramp (step 0 is "no activity").</summary>
    public required string Accent { get; init; }
    public required string AccentBright { get; init; }
    public required string[] HeatRamp { get; init; }
}

public static class Providers
{
    public static readonly ProviderMeta Claude = new()
    {
        Id = ProviderId.Claude,
        Label = "Claude Code",
        TranscriptHint = @"%USERPROFILE%\.claude\projects\",
        TranscriptEnvVar = "CLAUDE_CONFIG_DIR",
        CostLabel = "Total estimated spend",
        HasCacheWrites = true,
        HasReasoningTokens = false,
        CacheReadLabel = "Cache read",
        MessageNoun = "messages",
        MessageTip = "One per billed API response. Claude Code writes the same message to disk several times (streaming updates, and replays when a session is resumed); those copies are collapsed here.",
        SessionTip = "Every transcript file found, including subagent transcripts nested under a session. The Claude Code app counts only top-level sessions.",
        SubagentNoun = "subagent",
        Accent = "#D97757",
        AccentBright = "#F08A66",
        HeatRamp = ["#161318", "#9D4224", "#BE4F2B", "#D3603B", "#D97757", "#E39981"],
    };

    public static readonly ProviderMeta Codex = new()
    {
        Id = ProviderId.Codex,
        Label = "Codex",
        TranscriptHint = @"%USERPROFILE%\.codex\sessions\",
        TranscriptEnvVar = "CODEX_HOME",
        CostLabel = "API-equivalent spend",
        HasCacheWrites = false,
        HasReasoningTokens = true,
        CacheReadLabel = "Cached input",
        MessageNoun = "requests",
        MessageTip = "One per billed API request. Codex reports a running total after each turn and sometimes repeats the last reading; repeats contribute nothing here.",
        SessionTip = "Every rollout file found, including the auto-review threads Codex spawns to check its own actions. Those are billed separately and are counted here.",
        SubagentNoun = "auto-review",
        Accent = "#10A37F",
        AccentBright = "#1FC79C",
        HeatRamp = ["#131817", "#0A6B53", "#0D8165", "#0E9372", "#10A37F", "#13C197"],
    };

    public static readonly IReadOnlyList<ProviderMeta> All = [Claude, Codex];

    public static ProviderMeta Get(ProviderId id) => id == ProviderId.Claude ? Claude : Codex;
}
