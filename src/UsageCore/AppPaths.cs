using UsageCore.Model;

namespace UsageCore;

/// <summary>
/// Where the app keeps its own state, and where it reads each agent's transcripts.
/// </summary>
/// <remarks>
/// Everything the app writes lives under one folder, <c>%LOCALAPPDATA%\AI Usage
/// Native</c>. <c>AIUSAGE_DATA_DIR</c> moves it, and that exists for one reason:
/// pointing the app at a demo transcript tree without it would fold synthetic
/// days into the real archive - and the archive keeps whichever copy of a day
/// has more messages, so a fabricated day could permanently replace a real one.
/// The archive is the one thing here that does not rebuild itself from disk.
/// </remarks>
public static class AppPaths
{
    public const string DataDirVariable = "AIUSAGE_DATA_DIR";

    public static string DataDir =>
        Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI Usage Native");

    /// <summary>settings.json, the merge files and any rate-card overrides.</summary>
    public static string ConfigDir => Path.Combine(DataDir, "config");

    public static string HistoryDir => Path.Combine(DataDir, "history");

    public static string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>
    /// One folder per agent, never shared: the same project name can exist under
    /// both, as two projects with two histories, and a shared folder would hand
    /// one agent's mark to the other's project.
    /// </summary>
    public static string LogoDir(ProviderId provider) =>
        Path.Combine(DataDir, "project-logos", provider == ProviderId.Claude ? "claude" : "codex");

    public static string HiddenProjectsFile(ProviderId provider) =>
        Path.Combine(DataDir, provider == ProviderId.Claude ? "hidden-projects.json" : "codex-hidden-projects.json");

    private static string Home =>
        Environment.GetEnvironmentVariable("USERPROFILE") is { Length: > 0 } profile
            ? profile
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary><c>CLAUDE_CONFIG_DIR</c>, else <c>~/.claude</c>, plus <c>projects</c>.</summary>
    public static string ClaudeProjectsDir =>
        Path.Combine(
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".claude"),
            "projects");

    /// <summary><c>CODEX_HOME</c>, else <c>~/.codex</c>.</summary>
    public static string CodexHome =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".codex");
}
