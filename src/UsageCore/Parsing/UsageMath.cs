using UsageCore.Model;

namespace UsageCore.Parsing;

/// <summary>
/// The arithmetic both parsers do after a line is understood: tokens into a
/// cell, cells into a per-model bucket, buckets out as a sorted daily list.
/// Nothing about READING a transcript is shared between the two agents; this
/// part is, and living twice is how a fix to one copy misses the other.
/// </summary>
/// <remarks>
/// The one real difference is reasoning tokens: Codex reports them as a subset
/// of output, Claude Code not at all. So a builder is told whether its agent
/// tracks them, and a Claude Code cell keeps <c>Reasoning</c> null rather than
/// claiming a measured zero.
/// </remarks>
internal sealed class BucketBuilder(bool tracksReasoning)
{
    private readonly Dictionary<string, UsageCell> _perModel = new(StringComparer.Ordinal);

    public UsageCell Combined { get; } = UsageCell.Empty(tracksReasoning);

    public UsageCell CellFor(string model)
    {
        if (!_perModel.TryGetValue(model, out var cell))
        {
            cell = UsageCell.Empty(tracksReasoning);
            _perModel[model] = cell;
        }
        return cell;
    }

    public IReadOnlyDictionary<string, UsageCell> PerModel => _perModel;

    /// <summary>Per-model cells ordered by total tokens, largest first.</summary>
    public Dictionary<string, UsageCell> SortedPerModel() =>
        _perModel.OrderByDescending(kv => kv.Value.TotalTokens)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    public UsageBucket ToBucket() => new() { PerModel = SortedPerModel(), Combined = Combined };
}

internal sealed class DailyBuilder(bool tracksReasoning)
{
    private readonly Dictionary<string, BucketBuilder> _days = new(StringComparer.Ordinal);

    public BucketBuilder Day(string date)
    {
        if (!_days.TryGetValue(date, out var bucket))
        {
            bucket = new BucketBuilder(tracksReasoning);
            _days[date] = bucket;
        }
        return bucket;
    }

    public List<DailyEntry> ToList() =>
        _days.OrderBy(kv => kv.Key, Comparer<string>.Create(Dates.CompareKeys))
            .Select(kv => new DailyEntry
            {
                Date = kv.Key,
                PerModel = kv.Value.SortedPerModel(),
                Combined = kv.Value.Combined,
            })
            .ToList();
}

/// <summary>A project's running totals while a parse walks the files.</summary>
internal sealed class ProjectBuilder(bool tracksReasoning)
{
    public BucketBuilder Totals { get; } = new(tracksReasoning);
    public DailyBuilder Daily { get; } = new(tracksReasoning);
}

/// <summary>Everything one parse accumulates, whichever agent it is reading.</summary>
internal sealed class Accumulator(bool tracksReasoning)
{
    public bool TracksReasoning => tracksReasoning;
    public BucketBuilder Global { get; } = new(tracksReasoning);
    public DailyBuilder GlobalDaily { get; } = new(tracksReasoning);
    public Dictionary<string, ProjectBuilder> Projects { get; } = new(StringComparer.Ordinal);
    public int[] HourHistogram { get; } = new int[24];

    public ProjectBuilder Project(string id)
    {
        if (!Projects.TryGetValue(id, out var project))
        {
            project = new ProjectBuilder(tracksReasoning);
            Projects[id] = project;
        }
        return project;
    }

    /// <summary>The four buckets one piece of usage lands in.</summary>
    private IEnumerable<BucketBuilder> Buckets(string projectId, string date)
    {
        var project = Project(projectId);
        yield return Global;
        yield return project.Totals;
        yield return GlobalDaily.Day(date);
        yield return project.Daily.Day(date);
    }

    /// <summary>Makes sure a project and its day exist, even with no usage.</summary>
    public void Touch(string projectId, string date)
    {
        foreach (var _ in Buckets(projectId, date)) { }
    }

    public void AddRuntime(string projectId, string date, string model, double seconds)
    {
        foreach (var bucket in Buckets(projectId, date))
        {
            bucket.CellFor(model).RuntimeSeconds += seconds;
            bucket.Combined.RuntimeSeconds += seconds;
        }
    }

    public void AddMessage(string projectId, string date, string model, in TokenCounts tokens, double cost, bool unpriced)
    {
        foreach (var bucket in Buckets(projectId, date))
        {
            var cell = bucket.CellFor(model);
            cell.AddMessage(tokens, cost);
            if (unpriced) cell.Unpriced = true;
            bucket.Combined.AddMessage(tokens, cost);
        }
    }
}
