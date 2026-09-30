using UsageApp.Imaging;
using UsageCore.Config;
using UsageCore.Model;
using UsageCore.View;

namespace UsageApp.State;

/// <summary>
/// The colour each project is drawn in on the charts split by project: the one
/// chosen from its menu, else its logo's dominant colour, else a fallback.
/// See <see cref="ProjectColors"/> for the rules; this is the app's half - the
/// chosen colours' file, and reading the logos.
/// </summary>
/// <remarks>
/// A logo is read once per file version (path and last-write time), at 64px:
/// plenty for one dominant colour. A new or replaced logo is picked up on the
/// next build, since the logo folder's watcher rebuilds the page.
/// </remarks>
public sealed class ProjectColorStore(LogoStore logos)
{
    private const int Sample = 64;

    private readonly Dictionary<ProviderId, Dictionary<string, string>> _custom = [];
    private readonly Dictionary<string, (DateTime Written, string? Hex)> _fromLogo = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Custom(ProviderId provider)
    {
        if (!_custom.TryGetValue(provider, out var map))
        {
            map = ProjectColorSettings.Load(provider);
            _custom[provider] = map;
        }
        return map;
    }

    /// <summary>Sets (<c>#RRGGBB</c>) or clears (null) one project's colour. Throws with a readable message.</summary>
    public void Save(ProviderId provider, string projectId, string? hex) =>
        _custom[provider] = ProjectColorSettings.Save(provider, projectId, hex);

    /// <summary>
    /// Every project's colour for a report. Assigned over the WHOLE project list
    /// in the report's order, so the donut and the daily chart agree.
    /// </summary>
    public Dictionary<string, (string Hex, ProjectColorSource Source)> For(ProviderId provider, IReadOnlyList<ProjectSummary> projects)
    {
        var names = projects.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        return ProjectColors.Assign(projects.Select(p => p.Id), Custom(provider),
            id => logos.PathFor(provider, names[id]) is { } path ? FromLogo(path) : null);
    }

    private string? FromLogo(string path)
    {
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (_fromLogo.TryGetValue(path, out var cached) && cached.Written == written) return cached.Hex;
        var pixels = ImageLoader.Pixels(path, Sample);
        var hex = pixels is null ? null : ProjectColors.DominantColor(pixels, Sample, Sample);
        _fromLogo[path] = (written, hex);
        return hex;
    }
}
