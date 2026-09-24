using System.Diagnostics;
using Microsoft.UI.Dispatching;
using UsageCore;
using UsageCore.Model;

namespace UsageApp.State;

/// <summary>What the app knows about one agent right now.</summary>
public sealed class AgentData
{
    public UsageReport? Report { get; set; }
    public string? Error { get; set; }
    public bool Loading { get; set; }
    public DateTimeOffset? LastRefreshed { get; set; }
    public TimeSpan ParseTime { get; set; }

    /// <summary>Bumped on every successful load, so views know to replay their count-ups.</summary>
    public int Version { get; set; }
}

/// <summary>
/// The app's state: the current agent, each agent's report, and refresh.
/// </summary>
/// <remarks>
/// Reports are recomputed from disk on every refresh - no cache, because a
/// stale cache reports numbers that were true a minute ago, which is worse than
/// a slow parse. While a refresh runs the previous report stays on screen.
/// </remarks>
public sealed class AppState
{
    private readonly DispatcherQueue _ui;
    private readonly Dictionary<ProviderId, AgentData> _agents = new()
    {
        [ProviderId.Claude] = new AgentData(),
        [ProviderId.Codex] = new AgentData(),
    };
    private readonly Dictionary<ProviderId, Task> _inFlight = [];

    public AppState(DispatcherQueue ui) => _ui = ui;

    public ProviderId Provider { get; private set; } = ProviderId.Claude;

    public ProviderMeta Meta => Providers.Get(Provider);

    public AgentData Current => _agents[Provider];

    public AgentData For(ProviderId id) => _agents[id];

    public HiddenProjectsStore Hidden { get; } = new();

    /// <summary>Raised on the UI thread whenever an agent's data or the current agent changes.</summary>
    public event Action<ProviderId>? Changed;

    public void SelectProvider(ProviderId id)
    {
        if (Provider == id) return;
        Provider = id;
        Changed?.Invoke(id);
        if (_agents[id].Report is null && !_agents[id].Loading) _ = RefreshAsync(id);
    }

    /// <summary>
    /// Re-reads an agent's transcripts. A refresh already running for that
    /// agent is joined rather than started twice.
    /// </summary>
    public Task RefreshAsync(ProviderId id)
    {
        if (_inFlight.TryGetValue(id, out var running)) return running;
        var task = RunRefresh(id);
        _inFlight[id] = task;
        return task;
    }

    private async Task RunRefresh(ProviderId id)
    {
        var data = _agents[id];
        data.Loading = true;
        Changed?.Invoke(id);

        var clock = Stopwatch.StartNew();
        try
        {
            var report = await Task.Run(() =>
            {
                var result = UsageService.Load(id);
                // The parse holds one record per transcript line until it is
                // done. Hand that back now rather than whenever the GC gets to
                // it, since this process may sit in the tray all day.
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                return result;
            });
            data.Report = report;
            data.Error = null;
            data.Version++;
            data.LastRefreshed = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            data.Error = ex.Message;
        }
        finally
        {
            data.ParseTime = clock.Elapsed;
            data.Loading = false;
            _inFlight.Remove(id);
            Changed?.Invoke(id);
        }
    }

    /// <summary>Marshals work onto the UI thread from a watcher or the tray.</summary>
    public void OnUi(Action action) => _ui.TryEnqueue(() => action());
}
