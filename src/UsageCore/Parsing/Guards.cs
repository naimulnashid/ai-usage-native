using System.Globalization;
using System.Text.Json;

namespace UsageCore.Parsing;

/// <summary>
/// What a malformed transcript line can and cannot do. Both agents' schemas are
/// undocumented and change between releases, so every read here degrades rather
/// than throws: one bad line must never cost the whole report.
/// </summary>
public static class Guards
{
    /// <summary>The earliest timestamp worth believing; both agents postdate it by years.</summary>
    public static readonly long EarliestPlausibleMs =
        new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>Clock skew is real; a year of it is not.</summary>
    public const long MaxFutureSkewMs = 365L * 86_400_000;

    /// <summary>
    /// A token count as a non-negative integer. A string, null, fraction or
    /// negative becomes 0 rather than being trusted - one negative value would
    /// otherwise flow into the totals as negative cost and then into the
    /// archive, where it would outlive the line that caused it.
    /// </summary>
    public static long ToTokenCount(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number) return 0;
        if (!value.TryGetDouble(out var d) || !double.IsFinite(d) || d <= 0) return 0;
        return d >= long.MaxValue ? long.MaxValue : (long)Math.Floor(d);
    }

    /// <summary><see cref="ToTokenCount(JsonElement)"/> for an optional property.</summary>
    public static long TokenField(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? ToTokenCount(v) : 0;

    /// <summary>
    /// A line's timestamp in Unix milliseconds, or null when it is missing,
    /// unparseable or absurd. "+275760-09-13T00:00:00Z" is the case worth
    /// naming: the JavaScript original accepted it and then overflowed on the
    /// timezone shift. Here it simply fails to parse - and is still counted as
    /// implausible by the caller, since the line did carry a timestamp.
    /// </summary>
    public static long? ParseTimestampMs(string? raw, long nowMs)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (!DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return null;
        }
        var ms = parsed.ToUnixTimeMilliseconds();
        if (ms < EarliestPlausibleMs) return null;
        if (ms > nowMs + MaxFutureSkewMs) return null;
        return ms;
    }

    /// <summary>A string property's value, or null when absent, not a string, or empty.</summary>
    public static string? StringField(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
        && v.GetString() is { Length: > 0 } s
            ? s
            : null;

    /// <summary>An object-valued property, or default when it is not an object.</summary>
    public static bool TryObject(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }
        value = default;
        return false;
    }
}

/// <summary>Day keys and the local-offset arithmetic both parsers bucket with.</summary>
public static class Dates
{
    /// <summary>The bucket for usage whose line carried no usable timestamp.</summary>
    public const string UnknownDate = "(unknown date)";

    private static readonly long MinMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly long MaxMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    private static long? Shift(long timestampMs, double offsetHours)
    {
        var shifted = timestampMs + offsetHours * 3_600_000d;
        if (!double.IsFinite(shifted) || shifted < MinMs || shifted > MaxMs) return null;
        return (long)shifted;
    }

    /// <summary>The local calendar day for a UTC instant, or <see cref="UnknownDate"/>.</summary>
    public static string LocalDate(long timestampMs, double offsetHours) =>
        Shift(timestampMs, offsetHours) is { } s
            ? DateTimeOffset.FromUnixTimeMilliseconds(s).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : UnknownDate;

    /// <summary>The local hour 0-23 for a UTC instant, or null out of range.</summary>
    public static int? LocalHour(long timestampMs, double offsetHours) =>
        Shift(timestampMs, offsetHours) is { } s
            ? DateTimeOffset.FromUnixTimeMilliseconds(s).UtcDateTime.Hour
            : null;

    public static bool IsDayKey(string value) =>
        value.Length == 10 && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static DateOnly ToDate(string key) =>
        DateOnly.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string ToKey(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Days apart between two day keys.</summary>
    public static int DaysBetween(string a, string b) => ToDate(b).DayNumber - ToDate(a).DayNumber;

    /// <summary>
    /// Orders day keys oldest first, with the undated bucket last: it is usage,
    /// but not a day, and nothing that walks days should meet it in the middle.
    /// </summary>
    public static int CompareKeys(string a, string b)
    {
        var ua = a == UnknownDate;
        var ub = b == UnknownDate;
        if (ua || ub) return ua == ub ? 0 : ua ? 1 : -1;
        return string.CompareOrdinal(a, b);
    }

    /// <summary>
    /// Longest run of consecutive active days, and the run ending today - or
    /// yesterday, so a streak is not reported broken before the day is over.
    /// </summary>
    public static (int Current, int Longest) Streaks(IEnumerable<string> activeDates, string todayKey)
    {
        var sorted = activeDates.Where(IsDayKey).Distinct().Order(StringComparer.Ordinal).ToList();
        if (sorted.Count == 0) return (0, 0);

        int longest = 1, run = 1;
        for (var i = 1; i < sorted.Count; i++)
        {
            run = DaysBetween(sorted[i - 1], sorted[i]) == 1 ? run + 1 : 1;
            longest = Math.Max(longest, run);
        }

        var last = sorted[^1];
        if (DaysBetween(last, todayKey) > 1) return (0, longest);

        var present = sorted.ToHashSet(StringComparer.Ordinal);
        var current = 0;
        var cursor = ToDate(last);
        while (present.Contains(ToKey(cursor)))
        {
            current++;
            cursor = cursor.AddDays(-1);
        }
        return (current, longest);
    }
}
