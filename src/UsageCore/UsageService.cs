using UsageCore.History;
using UsageCore.Model;
using UsageCore.Parsing;

namespace UsageCore;

/// <summary>The one entry point the app and the CLI use: parse, then archive.</summary>
public static class UsageService
{
    /// <summary>
    /// Parses an agent's transcripts and rebuilds the result from the archive.
    /// Recomputed from disk every time: no cache, because a stale cache reports
    /// numbers that were true a minute ago, which is worse than a slow parse.
    /// </summary>
    public static UsageReport Load(ProviderId provider, bool useArchive = true)
    {
        var report = provider == ProviderId.Claude ? ClaudeParser.Parse() : CodexParser.Parse();
        return useArchive ? HistoryArchive.WithHistory(report) : report;
    }
}
