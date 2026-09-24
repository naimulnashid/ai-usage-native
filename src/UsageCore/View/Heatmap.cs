using UsageCore.Model;
using UsageCore.Parsing;

namespace UsageCore.View;

public sealed record HeatmapCell(string Date, DailyEntry? Entry, bool Outside, int Column, int Row);

public sealed record HeatmapStrip(
    IReadOnlyList<HeatmapCell> Cells,
    IReadOnlyList<(string Label, int Column)> Months,
    string FirstDate,
    string LastDate);

public sealed record HeatmapLayout(IReadOnlyList<HeatmapStrip> Strips, double Max, double Total, int ActiveDays);

/// <summary>
/// Where every day of the activity heat map goes, for both layouts: the
/// overview's single strip of the last 26 weeks, and the full-history page's
/// strips repeated downwards, oldest first, so more history makes the page
/// taller rather than the cells smaller.
/// </summary>
public static class Heatmap
{
    /// <summary>Weeks in one strip: six months.</summary>
    public const int Weeks = 26;

    private static readonly string[] MonthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    public static DayOfWeek FirstDay(WeekStart start) => start switch
    {
        WeekStart.Sunday => DayOfWeek.Sunday,
        WeekStart.Saturday => DayOfWeek.Saturday,
        _ => DayOfWeek.Monday,
    };

    /// <summary>Weekday labels in row order.</summary>
    public static string[] RowLabels(WeekStart start)
    {
        string[] names = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
        var first = (int)FirstDay(start);
        return Enumerable.Range(0, 7).Select(i => names[(first + i) % 7]).ToArray();
    }

    private static DateOnly WeekOf(DateOnly date, DayOfWeek first) =>
        date.AddDays(-(((int)date.DayOfWeek - (int)first + 7) % 7));

    public static DateOnly Today(UsageReport report) => Dates.ToDate(DayRanges.Today(report));

    public static DateOnly WindowStart(DateOnly today, DayOfWeek first) => WeekOf(today, first).AddDays(-(Weeks - 1) * 7);

    public static string? EarliestActiveDate(IEnumerable<DailyEntry> daily) =>
        daily.Where(d => Dates.IsDayKey(d.Date) && DayRanges.IsActive(d)).Select(d => d.Date).Order(StringComparer.Ordinal).FirstOrDefault();

    /// <summary>True when some recorded day is older than the overview's strip - when Expand is offered.</summary>
    public static bool HasHistoryBeforeWindow(IEnumerable<DailyEntry> daily, DateOnly today, DayOfWeek first) =>
        EarliestActiveDate(daily) is { } earliest && string.CompareOrdinal(earliest, Dates.ToKey(WindowStart(today, first))) < 0;

    /// <summary>The first of the month holding the earliest recorded day.</summary>
    public static string? FullHistoryStart(IEnumerable<DailyEntry> daily) =>
        EarliestActiveDate(daily) is { } earliest ? earliest[..7] + "-01" : null;

    public static HeatmapLayout Recent(IReadOnlyList<DailyEntry> daily, DateOnly today, DayOfWeek first) =>
        Layout(daily, WindowStart(today, first), 1, null, today);

    public static HeatmapLayout Full(IReadOnlyList<DailyEntry> daily, string from, DateOnly today, DayOfWeek first)
    {
        var start = Dates.ToDate(from);
        var firstWeek = WeekOf(start, first);
        var weeks = (WeekOf(today, first).DayNumber - firstWeek.DayNumber) / 7 + 1;
        var strips = Math.Max(1, (int)Math.Ceiling(weeks / (double)Weeks));
        return Layout(daily, firstWeek, strips, start, today);
    }

    /// <summary>"Jul 2026 – Dec 2026", or one month alone.</summary>
    public static string StripLabel(HeatmapStrip strip)
    {
        static string Month(string key) => $"{MonthNames[int.Parse(key[5..7]) - 1]} {key[..4]}";
        var first = Month(strip.FirstDate);
        var last = Month(strip.LastDate);
        return first == last ? first : $"{first} – {last}";
    }

    private static HeatmapLayout Layout(IReadOnlyList<DailyEntry> daily, DateOnly firstWeek, int stripCount, DateOnly? from, DateOnly today)
    {
        var byDate = new Dictionary<string, DailyEntry>(StringComparer.Ordinal);
        foreach (var d in daily) byDate[d.Date] = d;
        var strips = new List<HeatmapStrip>();
        double max = 0, total = 0;
        var activeDays = 0;

        for (var s = 0; s < stripCount; s++)
        {
            var cells = new List<HeatmapCell>(Weeks * 7);
            var months = new List<(string, int)>();
            var lastMonth = -1;
            string firstDate = "", lastDate = "";

            for (var column = 0; column < Weeks; column++)
            {
                var weekStart = firstWeek.AddDays((s * Weeks + column) * 7);
                var labelled = false;
                for (var row = 0; row < 7; row++)
                {
                    var day = weekStart.AddDays(row);
                    var key = Dates.ToKey(day);
                    var outside = day > today || (from is { } f && day < f);
                    byDate.TryGetValue(key, out var entry);

                    if (!outside)
                    {
                        // A column is named after the month of its first SHOWN day.
                        if (!labelled)
                        {
                            labelled = true;
                            if (day.Month - 1 != lastMonth)
                            {
                                months.Add((MonthNames[day.Month - 1], column));
                                lastMonth = day.Month - 1;
                            }
                        }
                        if (firstDate.Length == 0) firstDate = key;
                        lastDate = key;
                        if (entry is not null)
                        {
                            max = Math.Max(max, entry.Combined.CostUsd);
                            total += entry.Combined.CostUsd;
                            if (entry.Combined.CostUsd > 0) activeDays++;
                        }
                    }
                    cells.Add(new HeatmapCell(key, entry, outside, column, row));
                }
            }
            strips.Add(new HeatmapStrip(cells, months, firstDate, lastDate));
        }
        return new HeatmapLayout(strips, max, total, activeDays);
    }
}
