using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using UsageCore;
using UsageCore.History;
using UsageCore.Model;
using UsageCore.Parsing;
using UsageCli;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var command = args.FirstOrDefault()?.ToLowerInvariant();
switch (command)
{
    case "parse":
        return Parse(args.Skip(1).ToArray());
    case "demo-data":
        return DemoData.Run(args.Skip(1).FirstOrDefault() ?? "demo-data");
    default:
        Console.WriteLine("""
            aiusage parse <claude|codex> [--no-archive] [--json <file>]
                Parses an agent's transcripts and prints the numbers worth
                watching after a parser change. By default the result is folded
                into the app's archive, exactly as the app does.

            aiusage demo-data [dir]
                Writes synthetic transcripts for both agents (default ./demo-data)
                and prints how to point the app at them.
            """);
        return command is null or "help" or "--help" ? 0 : 1;
}

static int Parse(string[] args)
{
    var agent = args.FirstOrDefault()?.ToLowerInvariant();
    if (agent is not ("claude" or "codex"))
    {
        Console.Error.WriteLine("parse: name an agent, claude or codex.");
        return 1;
    }
    var provider = agent == "claude" ? ProviderId.Claude : ProviderId.Codex;
    var useArchive = !args.Contains("--no-archive");
    var jsonIndex = Array.IndexOf(args, "--json");
    var jsonOut = jsonIndex >= 0 && jsonIndex + 1 < args.Length ? args[jsonIndex + 1] : null;

    var clock = Stopwatch.StartNew();
    var parsed = provider == ProviderId.Claude ? ClaudeParser.Parse() : CodexParser.Parse();
    var report = useArchive ? HistoryArchive.WithHistory(parsed) : parsed;
    var elapsed = clock.Elapsed;
    var d = report.Diagnostics;

    static string Usd(double n) => "$" + n.ToString("0.00");
    static string M(double n) => (n / 1_000_000).ToString("0.00") + "M";
    static string Dur(double s) => (long)(s / 3600) > 0 ? $"{(long)(s / 3600)}h {(long)(s % 3600 / 60)}m" : $"{(long)(s % 3600 / 60)}m";

    Console.WriteLine();
    Console.WriteLine($"Source        : {report.TranscriptsDir}");
    Console.WriteLine($"Archive       : {(useArchive ? HistoryArchive.PathFor(provider) : "not used (--no-archive)")}");
    Console.WriteLine($"Parsed in     : {elapsed.TotalSeconds:0.0}s   (peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1_048_576} MB)");
    Console.WriteLine();
    Console.WriteLine("--- PARSER DIAGNOSTICS -------------------------------------------");
    Console.WriteLine($"  files scanned            : {d.FilesScanned}   (failed: {d.FilesFailed})");
    Console.WriteLine($"  lines read               : {d.LinesRead:#,0}");
    Console.WriteLine($"  unparseable lines        : {d.LinesUnparseable}");
    Console.WriteLine($"  implausible timestamps   : {d.ImplausibleTimestamps}");
    if (provider == ProviderId.Claude)
    {
        Console.WriteLine($"  assistant lines          : {d.AssistantLines:#,0}");
        Console.WriteLine($"  unique messages          : {d.UniqueMessages:#,0}");
        Console.WriteLine($"  duplicate lines skipped  : {d.DuplicateLinesSkipped:#,0}");
        Console.WriteLine($"  output tokens recovered  : {d.OutputTokensRecovered}");
        var flagged = report.Projects.SelectMany(p => p.Sessions).Sum(s => s.SuspiciousMessageCount);
        Console.WriteLine($"  still-suspicious outputs : {flagged}");
    }
    else
    {
        Console.WriteLine($"  token_count events       : {d.AssistantLines:#,0}");
        Console.WriteLine($"  billed turns counted     : {d.UniqueMessages:#,0}");
        Console.WriteLine($"  repeated readings skipped: {d.DuplicateLinesSkipped:#,0}");
        Console.WriteLine($"  files reconciled         : {d.ReconciledFiles} ok / {d.ReconcileFailures} mismatched");
        Console.WriteLine($"  counter resets           : {d.CounterResets}");
    }
    Console.WriteLine($"  unpriced models          : {(d.UnpricedModels.Count > 0 ? string.Join(", ", d.UnpricedModels) : "none")}");
    if (d.EmptyProjectsHidden.Count > 0) Console.WriteLine($"  replay-only dirs hidden  : {d.EmptyProjectsHidden.Count}");
    if (d.Warnings.Count > 0)
    {
        Console.WriteLine($"  warnings                 : {d.Warnings.Count}");
        foreach (var w in d.Warnings.Take(5)) Console.WriteLine($"      - {w}");
    }

    Console.WriteLine();
    Console.WriteLine("--- MODELS -------------------------------------------------------");
    foreach (var (model, cell) in report.Global.PerModel)
    {
        var extra = provider == ProviderId.Codex ? $"  reasoning {M(cell.Reasoning ?? 0),8}" : $"  writes {M(cell.CacheWrite5m + cell.CacheWrite1h),8}";
        Console.WriteLine($"{Usd(cell.CostUsd),12}{M(cell.TotalTokens),10}  in {M(cell.Input),8}  out {M(cell.Output),8}  cached {M(cell.CacheRead),9}{extra}  {model}{(cell.Unpriced ? "  (UNPRICED)" : "")}");
    }

    Console.WriteLine();
    Console.WriteLine("--- PROJECTS BY COST ---------------------------------------------");
    foreach (var project in report.Projects)
    {
        var subagents = project.Sessions.Count(s => s.IsSubagent);
        Console.WriteLine($"{Usd(project.Combined.CostUsd),12}{M(project.Combined.TotalTokens),10}{Dur(project.Combined.RuntimeSeconds),10}  {project.Name}  ({project.Sessions.Count - subagents} sessions, {subagents} {Providers.Get(provider).SubagentNoun})");
    }

    Console.WriteLine();
    var total = report.Global.Combined;
    Console.WriteLine($"TOTAL {Usd(total.CostUsd)}   {M(total.TotalTokens)} tokens   {total.Messages:#,0} {Providers.Get(provider).MessageNoun}   {Dur(total.RuntimeSeconds)} runtime   {report.Activity.ActiveDays} active days");
    if (report.Coverage is { } c) Console.WriteLine($"Coverage {c.EarliestDate} .. {c.LatestDate}   archived-only days: {c.ArchivedOnlyDays}");

    if (jsonOut is not null)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonOut))!);
        File.WriteAllText(jsonOut, JsonSerializer.Serialize(report, options));
        Console.WriteLine($"Full JSON written to: {Path.GetFullPath(jsonOut)}");
    }
    Console.WriteLine();
    return 0;
}
