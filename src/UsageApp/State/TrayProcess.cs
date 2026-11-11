using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using UsageCore;
using UsageCore.Model;

namespace UsageApp.State;

/// <summary>
/// The tray process: the icon, its menu and tooltip, and the window's
/// launcher. It is what runs all day, so it never loads WinUI.
/// </summary>
/// <remarks>
/// <para><b>Why two processes:</b> WinUI cannot be unloaded once loaded, and
/// held about 155 MB with the window closed to the tray (measured
/// 2026-11-11). The window is now its own process (<c>--dashboard</c>), gone
/// when it closes. Don't add a WinUI type (anything under
/// <c>Microsoft.UI.Xaml</c>) to what this class touches.</para>
/// <para><b>Transcripts are read by one process at a time.</b> While a
/// dashboard is open it does the reading and sends each agent's tooltip line
/// here (<see cref="TrayLink"/>); with none open, this process reads them, as
/// the app in the tray always did, keeps the one line per agent, and lets the
/// report go.</para>
/// </remarks>
public sealed class TrayProcess : IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly AppState _state;
    private readonly TrayHost _tray;
    private readonly TranscriptWatcher _watcher;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Windows.UI.ViewManagement.UISettings _systemColors = new();
    private readonly Dictionary<ProviderId, string> _lines = [];
    private UiSettings _settings = UiSettings.Load();
    private Process? _dashboard;
    private bool _everRead;

    /// <param name="openDashboard">False when started at login (<c>--tray</c>): the icon only.</param>
    public TrayProcess(DispatcherQueue ui, bool openDashboard)
    {
        _ui = ui;
        _state = new AppState(ui);
        _state.Changed += OnRead;

        // Before the icon exists: its menu window takes the theme as it is made.
        ApplyMenuTheme();
        // "System" follows Windows while the app runs, not only at launch.
        _systemColors.ColorValuesChanged += (_, _) => ui.TryEnqueue(ApplyMenuTheme);

        _tray = new TrayHost(action => ui.TryEnqueue(() => action()), _settings.AutoRefresh, OpenDashboard, RefreshNow, ToggleAutoRefresh, () => Exit(closeDashboard: true));
        _watcher = new TranscriptWatcher(_state.RefreshAsync, ui, () => _settings.AutoRefresh && _dashboard is null);
        TrayLink.Serve(parts => ui.TryEnqueue(() => Handle(parts)), _stopping.Token);

        // The dashboard reads the transcripts when it opens, and sends the
        // lines; reading them here as well would do the work twice.
        if (openDashboard) OpenDashboard();
        else ReadAll();
    }

    private void ApplyMenuTheme() => MenuTheme.Apply(_settings.ResolveLight(_systemColors));

    /* ---------------------------------------------------------- Dashboard */

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// Opens the window: a new dashboard process, which hands itself to one
    /// already open. Windows gives the foreground to the process the user
    /// last acted on, which is this one (a click on the icon), so the right
    /// is passed on, or the window would open behind whatever is in front.
    /// </summary>
    public void OpenDashboard()
    {
        if (_dashboard is { HasExited: false } open) AllowSetForegroundWindow(open.Id);
        if (Start("--dashboard") is { } started)
        {
            AllowSetForegroundWindow(started.Id);
            started.Dispose();
        }
    }

    /// <summary>
    /// Passes a request to the open dashboard: the flag makes the new process
    /// a messenger, which never becomes a window (see <see cref="Program"/>).
    /// </summary>
    private void Tell(string flag) => Start("--dashboard " + flag)?.Dispose();

    private static Process? Start(string arguments)
    {
        try
        {
            return Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private void Handle(string[] parts)
    {
        switch (parts[0])
        {
            case "hello" when parts.Length > 1 && int.TryParse(parts[1], out var pid):
                Adopt(pid);
                break;
            case "tip" when parts.Length > 2 && Providers.FromSlug(parts[1]) is { } meta:
                if (parts[2].Length > 0) _lines[meta.Id] = parts[2];
                else _lines.Remove(meta.Id);
                _everRead = true;
                _tray.ShowLines(_lines);
                break;
            case "settings":
                ReloadSettings();
                break;
            case "exit":
                Exit(closeDashboard: false);
                break;
        }
    }

    /// <summary>A dashboard said hello: watch it end, however it was started.</summary>
    private void Adopt(int pid)
    {
        if (_dashboard is { } current && !current.HasExited && current.Id == pid) return;
        try
        {
            var process = Process.GetProcessById(pid);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _ui.TryEnqueue(() => DashboardClosed(process));
            _dashboard = process;
            if (process.HasExited) DashboardClosed(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone before it could be watched: nothing is open.
        }
    }

    private void DashboardClosed(Process process)
    {
        if (!ReferenceEquals(_dashboard, process)) return;
        _dashboard = null;
        process.Dispose();
        ReloadSettings();
        if (!_settings.CloseToTray)
        {
            Exit(closeDashboard: false);
            return;
        }
        if (!_settings.TrayNoteShown)
        {
            _settings.TrayNoteShown = true;
            _settings.Save();
            _tray.Notify("AI Usage is still running", "It keeps its numbers up to date from the notification area. Right-click the icon to exit.");
        }
        // A window that closed before its first read left no lines behind.
        if (!_everRead) ReadAll();
    }

    /* ----------------------------------------------------------- Settings */

    /// <summary>The file is the window's as much as this process's: read it again before trusting or changing it.</summary>
    private void ReloadSettings()
    {
        _settings = UiSettings.Load();
        _tray.SyncChecks(_settings.AutoRefresh);
        ApplyMenuTheme();
    }

    private bool ToggleAutoRefresh()
    {
        _settings = UiSettings.Load();
        _settings.AutoRefresh = !_settings.AutoRefresh;
        _settings.Save();
        if (_dashboard is not null) Tell("--reload-settings");
        return _settings.AutoRefresh;
    }

    /* ------------------------------------------------------------ Reading */

    private void RefreshNow()
    {
        if (_dashboard is not null) Tell("--refresh");
        else ReadAll();
    }

    /// <summary>One agent after the other, so only one parse's records are held at a time.</summary>
    private async void ReadAll()
    {
        foreach (var meta in Providers.All) await _state.RefreshAsync(meta.Id);
    }

    /// <summary>A read finished: keep the agent's line and let the report go.</summary>
    private void OnRead(ProviderId id)
    {
        var data = _state.For(id);
        if (data.Report is not { } report) return;
        if (TrayHost.Line(Providers.Get(id), report) is { } line) _lines[id] = line;
        else _lines.Remove(id);
        _everRead = true;
        _tray.ShowLines(_lines);
        data.Report = null;
        // The report was the last large thing alive here. Off the UI thread:
        // a blocking collection would hold up the icon's messages.
        _ = Task.Run(() => GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true));
    }

    /* --------------------------------------------------------------- Exit */

    private void Exit(bool closeDashboard)
    {
        if (closeDashboard && _dashboard is not null) Tell("--exit");
        Dispose();
        Environment.Exit(0);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _watcher.Dispose();
        _tray.Dispose();
    }
}
