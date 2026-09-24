using System.Text.Json;
using UsageCore;
using UsageCore.Model;

namespace UsageApp.State;

/// <summary>
/// Projects hidden from the Projects page, one file per agent.
/// </summary>
/// <remarks>
/// <b>A view preference, never a filter on the data.</b> A hidden project still
/// counts in every total, chart and share; the donut sums it into its remainder
/// slice rather than naming or dropping it. Ids only.
/// </remarks>
public sealed class HiddenProjectsStore
{
    private readonly Dictionary<ProviderId, HashSet<string>> _hidden = [];

    public IReadOnlySet<string> For(ProviderId provider)
    {
        if (!_hidden.TryGetValue(provider, out var set))
        {
            set = Load(provider);
            _hidden[provider] = set;
        }
        return set;
    }

    private static HashSet<string> Load(ProviderId provider)
    {
        var file = AppPaths.HiddenProjectsFile(provider);
        try
        {
            if (!File.Exists(file)) return new(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("hidden", out var list) || list.ValueKind != JsonValueKind.Array) return new(StringComparer.Ordinal);
            return list.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 and <= 512 })
                .Select(e => e.GetString()!)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>Hides or shows a project, and saves. Throws with a readable message on failure.</summary>
    public void Set(ProviderId provider, string projectId, bool hidden)
    {
        var set = new HashSet<string>(For(provider), StringComparer.Ordinal);
        if (hidden) set.Add(projectId);
        else set.Remove(projectId);

        var file = AppPaths.HiddenProjectsFile(provider);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { hidden = set.Order(StringComparer.Ordinal).ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, file, overwrite: true);
        _hidden[provider] = set;
    }
}
