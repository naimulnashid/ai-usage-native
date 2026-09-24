using Microsoft.UI.Dispatching;
using UsageCore;
using UsageCore.Model;
using UsageCore.Parsing;

namespace UsageApp.State;

/// <summary>
/// Re-reads an agent's transcripts when they change on disk.
/// </summary>
/// <remarks>
/// <para>A live session writes to its transcript every few seconds, so
/// "refresh when it goes quiet" would never fire while you are working. This
/// throttles instead: the first change is picked up a few seconds later, and
/// while changes keep coming an agent is re-parsed at most once a minute. Each
/// parse is a full re-read - there is no cache to fall out of date - so the
/// minute is the cost ceiling.</para>
/// <para>Each agent is watched and refreshed on its own, so Codex writing does
/// not re-parse Claude Code.</para>
/// </remarks>
public sealed class TranscriptWatcher : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);

    private readonly AppState _state;
    private readonly DispatcherQueue _ui;
    private readonly Func<bool> _enabled;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<ProviderId, DispatcherQueueTimer> _timers = [];
    private readonly Dictionary<ProviderId, DateTimeOffset> _lastRun = [];

    public TranscriptWatcher(AppState state, DispatcherQueue ui, Func<bool> enabled)
    {
        _state = state;
        _ui = ui;
        _enabled = enabled;
        Watch(ProviderId.Claude, AppPaths.ClaudeProjectsDir);
        foreach (var dir in CodexParser.SessionDirs(AppPaths.CodexHome)) Watch(ProviderId.Codex, dir);
    }

    private void Watch(ProviderId provider, string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            var watcher = new FileSystemWatcher(dir, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            void Changed(object? sender, FileSystemEventArgs e) => _ui.TryEnqueue(() => Schedule(provider));
            watcher.Changed += Changed;
            watcher.Created += Changed;
            watcher.Deleted += Changed;
            watcher.Renamed += (s, e) => Changed(s, e);
            // An overflowed buffer means changes were missed - which is still a change.
            watcher.Error += (_, _) => _ui.TryEnqueue(() => Schedule(provider));
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Not watchable: Refresh still works.
        }
    }

    private void Schedule(ProviderId provider)
    {
        if (!_enabled()) return;
        if (!_timers.TryGetValue(provider, out var timer))
        {
            timer = _ui.CreateTimer();
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                _lastRun[provider] = DateTimeOffset.Now;
                _ = _state.RefreshAsync(provider);
            };
            _timers[provider] = timer;
        }
        if (timer.IsRunning) return;

        var since = _lastRun.TryGetValue(provider, out var last) ? DateTimeOffset.Now - last : TimeSpan.MaxValue;
        timer.Interval = since >= MinInterval ? Settle : MinInterval - since + Settle;
        timer.Start();
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        foreach (var timer in _timers.Values) timer.Stop();
    }
}
