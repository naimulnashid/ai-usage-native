using System.Text.Json;
using System.Text.Json.Nodes;
using UsageCore.Model;

namespace UsageCore.Config;

/// <summary>
/// The merges and display names set from the app's Projects page, one file per
/// agent: <c>project-settings.json</c> and <c>codex-project-settings.json</c>
/// in the data folder.
/// </summary>
/// <remarks>
/// <para><b>Why not <c>config\projects.json</c>.</b> That file is written by
/// hand and documents itself with <c>_comment</c> keys; an app rewriting it
/// would reformat it on the first click. Both are read, and for any one key the
/// app's entry wins (<see cref="Combine"/>), so a rule in the file can be
/// overridden here but only removed there.</para>
/// <para><b>Nothing on disk is renamed.</b> A display name is a label: the
/// project keeps its id, its archive entries and its working directory. Logos
/// are matched by the name shown, so a renamed project takes the logo filed
/// under its new name.</para>
/// <para><b>Both change the report</b>, so a save is followed by a fresh parse.
/// A merge never changes a total, only how it is grouped.</para>
/// </remarks>
public static class ProjectSettings
{
    public const int MaxEntries = 2000;
    public const int MaxIdLength = 1024;
    public const int MaxNameLength = 120;

    public static string PathFor(ProviderId provider, string? dataDir = null) =>
        Path.Combine(dataDir ?? AppPaths.DataDir, provider == ProviderId.Claude ? "project-settings.json" : "codex-project-settings.json");

    public static bool IsValidId(string? id) => id is { Length: > 0 and <= MaxIdLength } && !id.StartsWith('_');

    /// <summary>A name with no surrounding space, no control characters, not too long.</summary>
    public static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= MaxNameLength } && name.Trim() == name && !name.Any(char.IsControl);

    /// <summary>One agent's settings. Fails soft to none: the worst case is a project shown under its folder's name.</summary>
    public static ProjectConfig Load(ProviderId provider, string? dataDir = null)
    {
        var merge = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var file = PathFor(provider, dataDir);
            if (!File.Exists(file)) return ProjectConfig.None;
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root) return ProjectConfig.None;
            if (root["merge"] is JsonObject merges)
            {
                foreach (var (source, node) in merges.Take(MaxEntries))
                {
                    if (IsValidId(source) && node is JsonValue v && v.TryGetValue<string>(out var target) && IsValidId(target) && target != source)
                    {
                        merge[source] = target;
                    }
                }
            }
            if (root["displayNames"] is JsonObject displayNames)
            {
                foreach (var (id, node) in displayNames.Take(MaxEntries))
                {
                    if (IsValidId(id) && node is JsonValue v && v.TryGetValue<string>(out var name) && IsValidName(name)) names[id] = name;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ProjectConfig.None;
        }
        return new ProjectConfig(merge, names);
    }

    /// <summary>The hand-written file with the app's settings laid over it, key by key.</summary>
    public static ProjectConfig Combine(ProjectConfig file, ProjectConfig app)
    {
        if (app.Merge.Count == 0 && app.DisplayNames.Count == 0) return file;
        var merge = new Dictionary<string, string>(file.Merge, StringComparer.Ordinal);
        foreach (var (source, target) in app.Merge) merge[source] = target;
        var names = new Dictionary<string, string>(file.DisplayNames, StringComparer.Ordinal);
        foreach (var (id, name) in app.DisplayNames) names[id] = name;
        return new ProjectConfig(merge, names);
    }

    /// <summary>What both parsers and the archive use: <c>projects.json</c> plus the app's settings.</summary>
    public static ProjectConfig LoadEffective(ProviderId provider, string? configDir = null, string? dataDir = null) =>
        Combine(AppConfig.LoadProjectConfig(provider, configDir), Load(provider, dataDir));

    /// <summary>
    /// Sets (or, with null, clears) the name a project is shown under, and
    /// returns the app's settings. Throws with a readable message on bad input
    /// or a failed write.
    /// </summary>
    public static ProjectConfig SaveName(ProviderId provider, string id, string? name, string? dataDir = null)
    {
        if (!IsValidId(id)) throw new ArgumentException("Not a project id.");
        name = name?.Trim();
        if (name is { Length: 0 }) name = null;
        if (name is not null && !IsValidName(name)) throw new ArgumentException($"A name is 1 to {MaxNameLength} characters, on one line.");

        var current = Load(provider, dataDir);
        var names = new Dictionary<string, string>(current.DisplayNames, StringComparer.Ordinal);
        if (name is null) names.Remove(id);
        else names[id] = name;
        return Write(provider, new ProjectConfig(current.Merge, names), dataDir);
    }

    /// <summary>
    /// Folds <paramref name="source"/> into <paramref name="target"/>, or with a
    /// null target separates it again, and returns the app's settings.
    /// </summary>
    /// <remarks>
    /// Refuses a merge that would come back round to its source, through the
    /// app's rules or <c>projects.json</c>'s: the parser would ignore the cycle
    /// with a warning, which reads as the click having done nothing.
    /// </remarks>
    public static ProjectConfig SaveMerge(ProviderId provider, string source, string? target, string? dataDir = null, string? configDir = null)
    {
        if (!IsValidId(source)) throw new ArgumentException("Not a project id.");
        if (target is not null && !IsValidId(target)) throw new ArgumentException("Not a project id.");
        if (target == source) throw new ArgumentException("A project cannot be merged into itself.");

        var current = Load(provider, dataDir);
        var merge = new Dictionary<string, string>(current.Merge, StringComparer.Ordinal);
        if (target is null)
        {
            merge.Remove(source);
        }
        else
        {
            merge[source] = target;
            var effective = Combine(AppConfig.LoadProjectConfig(provider, configDir), new ProjectConfig(merge, current.DisplayNames));
            var warnings = new List<string>();
            if (AppConfig.ResolveProjectId(source, effective.Merge, warnings) == source || warnings.Count > 0)
            {
                throw new ArgumentException("That would merge the project back into itself: the other project is already merged into this one.");
            }
        }
        return Write(provider, new ProjectConfig(merge, current.DisplayNames), dataDir);
    }

    /// <summary>Written through a temporary file, so a crash leaves the old settings.</summary>
    private static ProjectConfig Write(ProviderId provider, ProjectConfig settings, string? dataDir)
    {
        if (settings.Merge.Count > MaxEntries || settings.DisplayNames.Count > MaxEntries)
        {
            throw new InvalidOperationException($"No more than {MaxEntries} projects can be merged or renamed.");
        }
        var root = new JsonObject
        {
            ["version"] = 1,
            ["merge"] = new JsonObject(settings.Merge.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => KeyValuePair.Create<string, JsonNode?>(kv.Key, kv.Value))),
            ["displayNames"] = new JsonObject(settings.DisplayNames.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => KeyValuePair.Create<string, JsonNode?>(kv.Key, kv.Value))),
        };
        var file = PathFor(provider, dataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(temp, file, overwrite: true);
        return settings;
    }
}
