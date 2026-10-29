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
/// <para><b>A root can appear later</b> - the app opened before an agent was
/// first used, or Codex's <c>archived_sessions</c> made on its first archive -
/// or be deleted and made again. The roots are checked every
/// <see cref="Recheck"/>: one that appeared is watched (and read, since it may
/// already hold transcripts); one that went is dropped, so a new folder of
/// the same name is watched afresh.</para>
/// </remarks>
public sealed class TranscriptWatcher : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(30);

    private readonly AppState _state;
    private readonly DispatcherQueue _ui;
    private readonly Func<bool> _enabled;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ProviderId, DispatcherQueueTimer> _timers = [];
    private readonly Dictionary<ProviderId, DateTimeOffset> _lastRun = [];
    private readonly DispatcherQueueTimer _recheck;

    public TranscriptWatcher(AppState state, DispatcherQueue ui, Func<bool> enabled)
    {
        _state = state;
        _ui = ui;
        _enabled = enabled;
        Reconcile(initial: true);
        _recheck = ui.CreateTimer();
        _recheck.Interval = Recheck;
        _recheck.IsRepeating = true;
        _recheck.Tick += (_, _) => Reconcile(initial: false);
        _recheck.Start();
    }

    private static IEnumerable<(ProviderId Provider, string Dir)> Roots() =>
        CodexParser.SessionRoots(AppPaths.CodexHome)
            .Select(dir => (ProviderId.Codex, dir))
            .Prepend((ProviderId.Claude, AppPaths.ClaudeProjectsDir));

    private void Reconcile(bool initial)
    {
        foreach (var (provider, dir) in Roots())
        {
            var exists = Directory.Exists(dir);
            if (_watchers.TryGetValue(dir, out var watcher))
            {
                if (exists) continue;
                watcher.Dispose();
                _watchers.Remove(dir);
                Schedule(provider);
            }
            else if (exists && Watch(provider, dir) && !initial)
            {
                Schedule(provider);
            }
        }
    }

    private bool Watch(ProviderId provider, string dir)
    {
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
            // An overflowed buffer means changes were missed - which is still a
            // change. The other error is the folder itself going away, so the
            // watcher is made again rather than trusted.
            watcher.Error += (_, _) => _ui.TryEnqueue(() =>
            {
                if (_watchers.TryGetValue(dir, out var current) && current == watcher)
                {
                    _watchers.Remove(dir);
                    watcher.Dispose();
                    Reconcile(initial: false);
                }
                Schedule(provider);
            });
            watcher.EnableRaisingEvents = true;
            _watchers[dir] = watcher;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Not watchable: Refresh still works, and the next check tries again.
            return false;
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
        _recheck.Stop();
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        foreach (var timer in _timers.Values) timer.Stop();
    }
}
