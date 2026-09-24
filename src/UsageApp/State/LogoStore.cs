using Microsoft.UI.Dispatching;
using UsageCore;
using UsageCore.Model;
using UsageCore.View;

namespace UsageApp.State;

/// <summary>
/// Project logos: one folder per agent, claimed by filename, watched.
/// </summary>
/// <remarks>
/// <para><c>My App.svg</c> in the agent's folder is the mark for that agent's
/// project named "My App" (matched loosely - see ProjectLogos.Key). No config
/// file: a hand-kept map goes stale the first time a folder is renamed.</para>
/// <para>The folder is watched, so a file dropped in shows up within a second,
/// no restart. The original needed one, because its web server listed the
/// folder only at startup.</para>
/// </remarks>
public sealed class LogoStore : IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly Dictionary<ProviderId, Dictionary<string, string>> _files = [];
    private readonly List<FileSystemWatcher> _watchers = [];
    private DispatcherQueueTimer? _debounce;

    /// <summary>Raised on the UI thread when a logo folder changes.</summary>
    public event Action? Changed;

    public LogoStore(DispatcherQueue ui)
    {
        _ui = ui;
        foreach (var provider in new[] { ProviderId.Claude, ProviderId.Codex })
        {
            var dir = AppPaths.LogoDir(provider);
            try
            {
                Directory.CreateDirectory(dir);
                var watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = false, EnableRaisingEvents = true };
                watcher.Created += (_, _) => Invalidate();
                watcher.Deleted += (_, _) => Invalidate();
                watcher.Renamed += (_, _) => Invalidate();
                watcher.Changed += (_, _) => Invalidate();
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // No folder, no logos: every project draws its monogram.
            }
        }
    }

    /// <summary>The logo file for a project, or null for the monogram.</summary>
    public string? PathFor(ProviderId provider, string projectName)
    {
        if (!_files.TryGetValue(provider, out var map))
        {
            map = ProjectLogos.Scan(provider);
            _files[provider] = map;
        }
        return map.GetValueOrDefault(ProjectLogos.Key(projectName));
    }

    /// <summary>
    /// Copies an image in as the logo for a project, named after it, replacing
    /// any logo it had. The folder watcher then updates every view.
    /// </summary>
    public void SetLogo(ProviderId provider, string projectName, string sourceFile)
    {
        var dir = AppPaths.LogoDir(provider);
        Directory.CreateDirectory(dir);
        RemoveLogo(provider, projectName);
        var name = SafeFileName(projectName) + Path.GetExtension(sourceFile).ToLowerInvariant();
        File.Copy(sourceFile, Path.Combine(dir, name), overwrite: true);
        Refresh(provider);
    }

    public void RemoveLogo(ProviderId provider, string projectName)
    {
        if (PathFor(provider, projectName) is { } existing && File.Exists(existing)) File.Delete(existing);
        Refresh(provider);
    }

    /// <summary>Copies every supported image from a folder. Returns how many were copied.</summary>
    public int Import(ProviderId provider, string folder)
    {
        var dir = AppPaths.LogoDir(provider);
        Directory.CreateDirectory(dir);
        var copied = 0;
        foreach (var file in Directory.GetFiles(folder))
        {
            if (Array.IndexOf(ProjectLogos.Extensions, Path.GetExtension(file).ToLowerInvariant()) < 0) continue;
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
            copied++;
        }
        Refresh(provider);
        return copied;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length > 0 ? clean : "project";
    }

    private void Refresh(ProviderId provider) => _files.Remove(provider);

    /// <summary>Many events arrive for one copy; coalesce them into one refresh.</summary>
    private void Invalidate()
    {
        _ui.TryEnqueue(() =>
        {
            _debounce ??= CreateTimer();
            _debounce.Stop();
            _debounce.Start();
        });
    }

    private DispatcherQueueTimer CreateTimer()
    {
        var timer = _ui.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            _files.Clear();
            Changed?.Invoke();
        };
        return timer;
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
    }
}
