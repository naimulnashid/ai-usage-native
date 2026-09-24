using UsageCore.Model;

namespace UsageCore.View;

/// <summary>
/// Which of the four states a page is in: loading, failed, empty or has data.
/// The middle two used to be told apart by the reader, which is how a fresh
/// install came to look broken.
/// </summary>
public static class ReportState
{
    /// <summary>The parse worked and found nothing: a first run, or transcripts elsewhere.</summary>
    public static bool IsEmpty(UsageReport report) =>
        report.Projects.Count == 0
        && report.Activity.Messages == 0
        && report.Global.Combined.TotalTokens == 0
        && report.Global.Combined.RuntimeSeconds == 0;

    /// <summary>"There was nothing here" is the normal state of a fresh install, not a warning.</summary>
    public static bool IsNotFoundWarning(string warning) =>
        warning.Contains("Cannot read projects directory", StringComparison.Ordinal)
        || warning.Contains("No Codex rollout files found", StringComparison.Ordinal);

    public static List<string> NoteworthyWarnings(IEnumerable<string> warnings) =>
        warnings.Where(w => !IsNotFoundWarning(w)).ToList();
}
