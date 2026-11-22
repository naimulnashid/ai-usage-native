using System.Diagnostics;
using Microsoft.UI.Dispatching;
using UsageCore;
using UsageCore.Combining;
using UsageCore.Config;
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
        [ProviderId.Antigravity] = new AgentData(),
        [ProviderId.Combined] = new AgentData(),
    };

    /// <summary>The agents' versions the combined report was built from.</summary>
    private (int, int, int)? _combinedFrom;
    private readonly Dictionary<ProviderId, Task> _inFlight = [];
    private readonly HashSet<ProviderId> _again = [];

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
        if (id == ProviderId.Combined) Recombine(force: false);
        Changed?.Invoke(id);
        if (id == ProviderId.Combined)
        {
            foreach (var meta in Providers.All)
            {
                if (_agents[meta.Id].Report is null && !_agents[meta.Id].Loading) _ = RefreshAsync(meta.Id);
            }
        }
        else if (_agents[id].Report is null && !_agents[id].Loading)
        {
            _ = RefreshAsync(id);
        }
    }

    /// <summary>
    /// After a project setting is saved: an agent re-parses, the combined view
    /// only merges again, since its merges and names act on finished reports.
    /// </summary>
    public Task ApplyProjectSettings(ProviderId id)
    {
        if (id != ProviderId.Combined) return RefreshAsync(id);
        Recombine(force: true);
        Changed?.Invoke(ProviderId.Combined);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The combined report, rebuilt when any agent's report has changed. Only
    /// while it is the view shown: the tray process never selects it, and a
    /// merged copy of every report held all day would cost memory for nothing.
    /// </summary>
    private void Recombine(bool force)
    {
        var data = _agents[ProviderId.Combined];
        var parts = Providers.All.Select(m => _agents[m.Id]).ToList();
        data.Loading = parts.Any(p => p.Loading);
        var errors = Providers.All.Where(m => _agents[m.Id].Error is not null).Select(m => $"{m.Label}: {_agents[m.Id].Error}").ToList();
        data.Error = errors.Count > 0 ? string.Join(Environment.NewLine, errors) : null;
        if (Provider != ProviderId.Combined) return;

        var from = (parts[0].Version, parts[1].Version, parts[2].Version);
        if (!force && from == _combinedFrom) return;
        // Wait for every agent's first read, or the totals would grow on screen
        // agent by agent and the count-ups replay three times.
        if (parts.Any(p => p.Report is null && p.Error is null)) return;
        var reports = parts.Select(p => p.Report).OfType<UsageReport>().ToList();
        if (reports.Count == 0) return;

        var clock = Stopwatch.StartNew();
        data.Report = ReportMerge.Merge(reports, ProjectSettings.LoadEffective(ProviderId.Combined));
        data.Version++;
        data.LastRefreshed = parts.Max(p => p.LastRefreshed);
        data.ParseTime = parts.Max(p => p.ParseTime) + clock.Elapsed;
        _combinedFrom = from;
    }

    private void Raise(ProviderId id)
    {
        Changed?.Invoke(id);
        if (id == ProviderId.Combined) return;
        Recombine(force: false);
        Changed?.Invoke(ProviderId.Combined);
    }

    /// <summary>
    /// Re-reads an agent's transcripts. A refresh already running for that
    /// agent is joined rather than started twice - but it may have read its
    /// prices, merges and transcripts before whatever asked again (a price
    /// just saved, a file just written), so one more pass follows it. Every
    /// request made during the same pass shares that one follow-up.
    /// </summary>
    public Task RefreshAsync(ProviderId id)
    {
        if (id == ProviderId.Combined) return Task.WhenAll(Providers.All.Select(m => RefreshAsync(m.Id)));
        if (_inFlight.TryGetValue(id, out var running))
        {
            _again.Add(id);
            return running;
        }
        var task = RunRefresh(id);
        _inFlight[id] = task;
        return task;
    }

    private async Task RunRefresh(ProviderId id)
    {
        var data = _agents[id];
        data.Loading = true;
        Raise(id);
        try
        {
            do
            {
                _again.Remove(id);
                await ParseOnce(id, data);
            }
            while (_again.Contains(id));
        }
        finally
        {
            data.Loading = false;
            _inFlight.Remove(id);
            Raise(id);
        }
    }

    private static async Task ParseOnce(ProviderId id, AgentData data)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var report = await Task.Run(() =>
            {                var result = UsageService.Load(id);
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
        }
    }

    /// <summary>Marshals work onto the UI thread from a watcher or the tray.</summary>
    public void OnUi(Action action) => _ui.TryEnqueue(() => action());
}
