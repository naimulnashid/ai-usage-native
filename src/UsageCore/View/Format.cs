using System.Globalization;
using UsageCore.Parsing;

namespace UsageCore.View;

/// <summary>Display formatting. Everything is en-US, as the rest of the UI is.</summary>
public static class Format
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    public static string Usd(double value, bool compact = false)
    {
        if (compact && Math.Abs(value) >= 1000) return $"${(value / 1000).ToString("0.0", En)}k";
        return "$" + value.ToString("#,0.00", En);
    }

    public static string Tokens(double value)
    {
        if (value >= 1_000_000_000) return (value / 1_000_000_000).ToString("0.00", En) + "B";
        if (value >= 1_000_000) return (value / 1_000_000).ToString("0.00", En) + "M";
        if (value >= 1_000) return (value / 1_000).ToString("0.0", En) + "K";
        return value.ToString("#,0", En);
    }

    public static string Duration(double seconds)
    {
        if (seconds <= 0) return "0m";
        var h = (long)(seconds / 3600);
        var m = (long)(seconds % 3600 / 60);
        if (h > 0) return m > 0 ? $"{h}h {m}m" : $"{h}h";
        var s = (long)(seconds % 60);
        return m > 0 ? $"{m}m" : $"{s}s";
    }

    public static string Count(double value) => value.ToString("#,0", En);

    public static string Percent(double share) =>
        share <= 0 ? "0%" : share < 0.001 ? "<0.1%" : (share * 100).ToString(share < 0.1 ? "0.0" : "0", En) + "%";

    /// <summary>"Aug 19" - compact axis and table label.</summary>
    public static string DateShort(string key) =>
        Dates.IsDayKey(key) ? $"{Months[Dates.ToDate(key).Month - 1]} {Dates.ToDate(key).Day}" : key;

    /// <summary>"Tue, Aug 19 2026" - full label for tooltips.</summary>
    public static string DateLong(string key) =>
        Dates.IsDayKey(key) ? Dates.ToDate(key).ToString("ddd, MMM d yyyy", En) : key;

    /// <summary>"Aug 19, 2026" - a date that is a fact about a file, not a data point.</summary>
    public static string DateStamp(string key) =>
        Dates.IsDayKey(key) ? Dates.ToDate(key).ToString("MMM d, yyyy", En) : key;

    /// <summary>15 to "3 PM". Hours are already in the configured offset.</summary>
    public static string Hour(int? hour)
    {
        if (hour is not { } h) return "—";
        var display = h % 12 == 0 ? 12 : h % 12;
        return $"{display} {(h < 12 ? "AM" : "PM")}";
    }

    public static string ClockTime(DateTimeOffset time) => time.ToLocalTime().ToString("h:mm:ss tt", En);

    /// <summary>
    /// A model for display only. Claude models lose the redundant prefix; Codex
    /// strings are not readable that way, so they come from a table.
    /// </summary>
    public static string Model(string model)
    {
        if (model == "<synthetic>") return "synthetic";
        return ModelColors.DisplayName(model) ?? (model.StartsWith("claude-", StringComparison.Ordinal) ? model[7..] : model);
    }
}
