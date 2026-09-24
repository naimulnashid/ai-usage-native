using UsageCore.Model;
using UsageCore.Parsing;

namespace UsageCore.View;

public enum DayRange
{
    Last30,
    Last60,
    All,
}

/// <summary>
/// The stacked charts' date windows: the last 30 days (the default), the last
/// 60, or everything on record.
/// </summary>
/// <remarks>
/// A window is calendar days ending on the report's own "today", not the last
/// N days that happen to hold data: "Last 30 days" is always 30 columns, and an
/// idle day is an empty column. A project untouched this month shows an empty
/// window rather than borrowing the label for an older stretch.
/// </remarks>
public static class DayRanges
{
    public static string Label(DayRange range) => range switch
    {
        DayRange.Last30 => "Last 30 days",
        DayRange.Last60 => "Last 60 days",
        _ => "All days",
    };

    /// <summary>The report's today, in the offset the parser bucketed days with - never the clock.</summary>
    public static string Today(UsageReport report) =>
        Dates.LocalDate(report.GeneratedAt.ToUnixTimeMilliseconds(), report.Settings.LocalUtcOffsetHours);

    /// <summary>
    /// The days a chart shows, oldest first. A window is filled, one entry per
    /// calendar day; the undated bucket is not a day and appears in All only.
    /// </summary>
    public static List<DailyEntry> DaysIn(IReadOnlyList<DailyEntry> daily, DayRange range, string today)
    {
        if (range == DayRange.All || !Dates.IsDayKey(today)) return daily.ToList();
        var byDate = daily.ToDictionary(d => d.Date, StringComparer.Ordinal);
        var end = Dates.ToDate(today);
        var count = range == DayRange.Last30 ? 30 : 60;
        var days = new List<DailyEntry>(count);
        for (var i = count - 1; i >= 0; i--)
        {
            var key = Dates.ToKey(end.AddDays(-i));
            days.Add(byDate.TryGetValue(key, out var entry) ? entry : new DailyEntry { Date = key });
        }
        return days;
    }

    public static bool IsActive(DailyEntry entry) =>
        entry.Combined.TotalTokens > 0 || entry.Combined.CostUsd > 0 || entry.Combined.Messages > 0;

    public static string? LastActiveDate(IReadOnlyList<DailyEntry> daily)
    {
        for (var i = daily.Count - 1; i >= 0; i--)
        {
            if (Dates.IsDayKey(daily[i].Date) && IsActive(daily[i])) return daily[i].Date;
        }
        return null;
    }

    /// <summary>
    /// Per-model and combined totals over the days shown. A legend beside a
    /// window must describe the window: all-time shares next to thirty columns
    /// would be two denominators on one panel.
    /// </summary>
    public static UsageBucket Sum(IEnumerable<DailyEntry> days)
    {
        var perModel = new Dictionary<string, UsageCell>(StringComparer.Ordinal);
        var combined = new UsageCell();
        foreach (var day in days)
        {
            combined.AddCell(day.Combined);
            foreach (var (model, cell) in day.PerModel)
            {
                if (!perModel.TryGetValue(model, out var target))
                {
                    target = new UsageCell();
                    perModel[model] = target;
                }
                target.AddCell(cell);
            }
        }
        return new UsageBucket { PerModel = perModel, Combined = combined };
    }
}
