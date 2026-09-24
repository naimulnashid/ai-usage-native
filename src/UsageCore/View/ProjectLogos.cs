using System.Text.RegularExpressions;
using UsageCore.Model;

namespace UsageCore.View;

/// <summary>
/// Project marks, claimed by filename: <c>My App.svg</c> in an agent's logo
/// folder is the mark for that agent's project displayed as "My App". No config
/// file - a hand-kept map goes stale the first time a folder is renamed.
/// </summary>
public static partial class ProjectLogos
{
    public static readonly string[] Extensions = [".svg", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico"];

    /// <summary>
    /// The key both sides match on: lower-cased, every run of non-alphanumerics
    /// collapsed to one space. So <c>My_App</c> finds <c>My App.png</c>.
    /// </summary>
    public static string Key(string name) => NonAlnumRuns().Replace(name.ToLowerInvariant(), " ").Trim();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnumRuns();

    /// <summary>At most two initials, for the monogram tile.</summary>
    public static string Initials(string name)
    {
        var words = Regex.Split(name, "[^A-Za-z0-9]+").Where(w => w.Length > 0).ToArray();
        if (words.Length == 0) return "?";
        if (words.Length == 1) return words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant();
        return (words[0][..1] + words[^1][..1]).ToUpperInvariant();
    }

    /// <summary>
    /// The logo file for each key in an agent's folder. When two files share a
    /// key, SVG wins, then the order of <see cref="Extensions"/>.
    /// </summary>
    public static Dictionary<string, string> Scan(ProviderId provider, string? dir = null)
    {
        dir ??= AppPaths.LogoDir(provider);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(dir)) return result;
        IEnumerable<string> files;
        try
        {
            files = Directory.GetFiles(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var file in files
                     .Where(f => Array.IndexOf(Extensions, Path.GetExtension(f).ToLowerInvariant()) >= 0)
                     .OrderBy(f => Array.IndexOf(Extensions, Path.GetExtension(f).ToLowerInvariant()))
                     .ThenBy(f => f, StringComparer.Ordinal))
        {
            result.TryAdd(Key(Path.GetFileNameWithoutExtension(file)), file);
        }
        return result;
    }
}
