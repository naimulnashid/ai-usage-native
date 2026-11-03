using UsageCore.Model;

namespace UsageCore;

/// <summary>
/// Everything that differs between the agents and is not a number. The UI
/// reads this rather than branching on an id: no view may hard-code an agent's
/// name or wording.
/// </summary>
public sealed record ProviderMeta
{
    public required ProviderId Id { get; init; }
    public required string Label { get; init; }

    /// <summary>
    /// The agent's name in file and folder names (<c>project-logos\codex\</c>,
    /// <c>codex-history.json</c>) and on the CLI. Never change one: it names files
    /// already on users' disks.
    /// </summary>
    public required string Slug { get; init; }

    /// <summary>
    /// Prefixed to the agent's own settings files. Empty for Claude Code, whose
    /// files predate the second agent and kept their bare names.
    /// </summary>
    public string FilePrefix => Id == ProviderId.Claude ? "" : Slug + "-";

    /// <summary>Where the transcripts live, for the empty state.</summary>
    public required string TranscriptHint { get; init; }
    public required string TranscriptEnvVar { get; init; }

    /// <summary>
    /// The headline label. Both agents read "Total estimated spend" by the
    /// owner's choice (2026-10-28); Codex's used to say "API-equivalent spend".
    /// Codex's figure is still what its tokens would cost through the OpenAI
    /// API, which on a flat ChatGPT plan is not what was paid, and the two
    /// agents' figures are still never added.
    /// </summary>
    public required string CostLabel { get; init; }

    /// <summary>
    /// Codex's cache-write field has always been 0, so the column would be a stripe
    /// of zeroes. Hidden for that reason, not for want of a rate: OpenAI does bill
    /// cache writes on its newer models, and the rate card carries them.
    /// </summary>
    public required bool HasCacheWrites { get; init; }
    public required bool HasReasoningTokens { get; init; }
    public required string CacheReadLabel { get; init; }

    /// <summary>The Cached tokens card's tip, for an agent that logs no cache writes.</summary>
    public required string CacheTip { get; init; }
    public required string MessageNoun { get; init; }
    public required string MessageTip { get; init; }
    public required string SessionTip { get; init; }
    public required string SubagentNoun { get; init; }

    /// <summary>How Daily combined spend marks a day well above trend: red for Claude Code, the agent's own accent for the others.</summary>
    public required string SpikeNote { get; init; }

    /// <summary>True when spikes are drawn in the warning red; false draws them in the bright accent.</summary>
    public bool SpikeInWarning => Id == ProviderId.Claude;

    /// <summary>Accent colour and its heat-map ramp (step 0 is "no activity").</summary>
    public required string Accent { get; init; }
    public required string AccentBright { get; init; }
    public required string[] HeatRamp { get; init; }

    /// <summary>
    /// The light theme's accent, darkened until it carries text at AA on white
    /// (the dark accents are ~3:1 there), its hover shade - darker again, where
    /// the dark theme's is brighter - and a heat ramp that DARKENS as spend rises.
    /// </summary>
    public required string AccentLight { get; init; }
    public required string AccentBrightLight { get; init; }
    public required string[] HeatRampLight { get; init; }
}

public static class Providers
{
    public static readonly ProviderMeta Claude = new()
    {
        Id = ProviderId.Claude,
        Label = "Claude Code",
        Slug = "claude",
        TranscriptHint = @"%USERPROFILE%\.claude\projects\",
        TranscriptEnvVar = "CLAUDE_CONFIG_DIR",
        CostLabel = "Total estimated spend",
        HasCacheWrites = true,
        HasReasoningTokens = false,
        CacheReadLabel = "Cache read",
        CacheTip = "Cache reads plus cache writes. Reads are billed at roughly a tenth of the input rate and writes at a premium, but the sheer volume of reads makes this the largest driver of total cost.",
        MessageNoun = "messages",
        MessageTip = "One per billed API response. Claude Code writes the same message to disk several times (streaming updates, and replays when a session is resumed); those copies are collapsed here.",
        SessionTip = "Every transcript file found, including subagent transcripts nested under a session. The Claude Code app counts only top-level sessions.",
        SubagentNoun = "subagent",
        SpikeNote = "Days marked in red sit well above trend.",
        Accent = "#D97757",
        AccentBright = "#F08A66",
        HeatRamp = ["#161318", "#9D4224", "#BE4F2B", "#D3603B", "#D97757", "#E39981"],
        AccentLight = "#BA4D2A",
        AccentBrightLight = "#A14224",
        HeatRampLight = ["#D9DDE4", "#D97554", "#CF552E", "#AF4827", "#933C21", "#75301A"],
    };

    public static readonly ProviderMeta Codex = new()
    {
        Id = ProviderId.Codex,
        Label = "Codex",
        Slug = "codex",
        TranscriptHint = @"%USERPROFILE%\.codex\sessions\",
        TranscriptEnvVar = "CODEX_HOME",
        CostLabel = "Total estimated spend",
        HasCacheWrites = false,
        HasReasoningTokens = true,
        CacheReadLabel = "Cached input",
        CacheTip = "Prompt tokens served from cache, billed at a tenth of the input rate or less. Codex resends the whole conversation every turn, so this is nearly all of the prompt — and, despite the discount, where most of the cost sits.",
        MessageNoun = "requests",
        MessageTip = "One per billed API request. Codex reports a running total after each turn and sometimes repeats the last reading; repeats contribute nothing here.",
        SessionTip = "Every rollout file found, including the auto-review threads Codex spawns to check its own actions. Those are billed separately and are counted here.",
        SubagentNoun = "auto-review",
        SpikeNote = "Days marked with a dot sit well above trend.",
        Accent = "#10A37F",
        AccentBright = "#1FC79C",
        HeatRamp = ["#131817", "#0A6B53", "#0D8165", "#0E9372", "#10A37F", "#13C197"],
        AccentLight = "#0C7F63",
        AccentBrightLight = "#0A6B53",
        HeatRampLight = ["#D7DEE0", "#10A37F", "#0E8D6D", "#0C765C", "#0A624D", "#084F3E"],
    };

    /// <summary>
    /// Google's agent IDE. Its figure is what the tokens would cost through each
    /// model's own API (Google's for Gemini, Anthropic's for Claude): Antigravity
    /// itself is billed by plan, not per token. Cache writes are hidden because it
    /// logs none, for Claude models included.
    /// </summary>
    public static readonly ProviderMeta Antigravity = new()
    {
        Id = ProviderId.Antigravity,
        Label = "Antigravity",
        Slug = "antigravity",
        TranscriptHint = @"%USERPROFILE%\.gemini\antigravity\conversations\",
        TranscriptEnvVar = AppPaths.AntigravityHomeVariable,
        CostLabel = "Total estimated spend",
        HasCacheWrites = false,
        HasReasoningTokens = true,
        CacheReadLabel = "Cached input",
        CacheTip = "Prompt tokens served from cache, billed at a tenth of the input rate. Antigravity resends the conversation every turn, so this is most of the prompt.",
        MessageNoun = "requests",
        MessageTip = "One per model request Antigravity recorded. A conversation kept by both the current app and the older IDE build is counted once.",
        SessionTip = "Every conversation found, including the research subagents Antigravity starts, which are stored as conversations of their own and billed separately.",
        SubagentNoun = "subagent",
        SpikeNote = "Days marked with a dot sit well above trend.",
        Accent = "#4285F4",
        AccentBright = "#6A9FF6",
        HeatRamp = ["#13151A", "#163F83", "#1C50A6", "#2261C9", "#4285F4", "#6A9FF6"],
        AccentLight = "#1A57BC",
        AccentBrightLight = "#154698",
        HeatRampLight = ["#D7DCE4", "#558CE7", "#286EE1", "#1A57BC", "#144494", "#103470"],
    };

    public static readonly IReadOnlyList<ProviderMeta> All = [Claude, Codex, Antigravity];

    public static ProviderMeta Get(ProviderId id) => id switch
    {
        ProviderId.Claude => Claude,
        ProviderId.Codex => Codex,
        _ => Antigravity,
    };

    /// <summary>The agent a CLI or debug argument names by its slug, or null.</summary>
    public static ProviderMeta? FromSlug(string? slug) =>
        All.FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase));
}
