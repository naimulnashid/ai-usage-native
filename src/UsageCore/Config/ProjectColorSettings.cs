using System.Text.Json;
using System.Text.Json.Nodes;
using UsageCore.Model;
using UsageCore.View;

namespace UsageCore.Config;

/// <summary>
/// The colours chosen for projects, one file per agent:
/// <c>project-colors.json</c> and <c>codex-project-colors.json</c> in the data
/// folder. Ids and <c>#RRGGBB</c> only.
/// </summary>
/// <remarks>
/// A view preference, like the hidden projects: saving one redraws, it never
/// re-parses. Fails soft to none, since the worst case is the logo's colour.
/// </remarks>
public static class ProjectColorSettings
{
    public const int MaxProjects = 2000;
    public const int MaxIdLength = 1024;

    public static string PathFor(ProviderId provider, string? dataDir = null) =>
        Path.Combine(dataDir ?? AppPaths.DataDir, provider == ProviderId.Claude ? "project-colors.json" : "codex-project-colors.json");

    public static bool IsValidId(string? id) => id is { Length: > 0 and <= MaxIdLength };

    public static Dictionary<string, string> Load(ProviderId provider, string? dataDir = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var file = PathFor(provider, dataDir);
            if (!File.Exists(file)) return result;
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root || root["colors"] is not JsonObject colors) return result;
            foreach (var (id, node) in colors.Take(MaxProjects))
            {
                if (IsValidId(id) && node is JsonValue v && v.TryGetValue<string>(out var hex) && ProjectColors.IsHex(hex))
                {
                    result[id] = hex.ToUpperInvariant();
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        return result;
    }

    /// <summary>
    /// Sets (<c>#RRGGBB</c>) or clears (null) one project's colour and returns
    /// them all. Written through a temporary file. Throws on bad input or a
    /// failed write, because whoever clicked Save should be told.
    /// </summary>
    public static Dictionary<string, string> Save(ProviderId provider, string id, string? color, string? dataDir = null)
    {
        if (!IsValidId(id)) throw new ArgumentException("Not a project id.");
        if (color is not null && !ProjectColors.IsHex(color)) throw new ArgumentException("A colour is written #RRGGBB.");
        var current = Load(provider, dataDir);
        if (color is null) current.Remove(id);
        else current[id] = color.ToUpperInvariant();
        if (current.Count > MaxProjects) throw new InvalidOperationException($"No more than {MaxProjects} projects can have a colour.");

        var file = PathFor(provider, dataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        var body = new JsonObject
        {
            ["version"] = 1,
            ["colors"] = new JsonObject(current.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        };
        File.WriteAllText(temp, body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(temp, file, overwrite: true);
        return current;
    }
}
