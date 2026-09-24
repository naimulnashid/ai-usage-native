using System.Drawing;
using H.NotifyIcon.Core;
using UsageCore;
using UsageCore.Model;
using UsageCore.View;

namespace UsageApp.State;

/// <summary>
/// The notification-area icon: today's spend for each agent in its tooltip,
/// click to open, right-click for the menu. Closing the window leaves the app
/// here, so the numbers keep updating in the background.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private readonly TrayIconWithContextMenu _tray;
    private readonly Icon _icon;
    private readonly PopupMenuItem _startAtLogin;
    private readonly PopupMenuItem _autoRefresh;

    public TrayHost(AppState state, UiSettings settings, Action open, Action exit)
    {
        _icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"), 16, 16);
        _tray = new TrayIconWithContextMenu("AIUsage.Tray")
        {
            Icon = _icon.Handle,
            ToolTip = "AI Usage",
        };

        _startAtLogin = new PopupMenuItem("Start at login", (_, _) => state.OnUi(() =>
        {
            try
            {
                StartupRegistration.Set(!StartupRegistration.IsEnabled);
            }
            catch (Exception)
            {
                // Registry denied: the item's check mark stays truthful below.
            }
            _startAtLogin!.Checked = StartupRegistration.IsEnabled;
        }))
        { Checked = StartupRegistration.IsEnabled };

        _autoRefresh = new PopupMenuItem("Refresh when transcripts change", (_, _) => state.OnUi(() =>
        {
            settings.AutoRefresh = !settings.AutoRefresh;
            settings.Save();
            _autoRefresh!.Checked = settings.AutoRefresh;
        }))
        { Checked = settings.AutoRefresh };

        _tray.ContextMenu = new PopupMenu
        {
            Items =
            {
                new PopupMenuItem("Open AI Usage", (_, _) => state.OnUi(open)),
                new PopupMenuItem("Refresh now", (_, _) => state.OnUi(() =>
                {
                    _ = state.RefreshAsync(ProviderId.Claude);
                    _ = state.RefreshAsync(ProviderId.Codex);
                })),
                new PopupMenuSeparator(),
                _autoRefresh,
                _startAtLogin,
                new PopupMenuSeparator(),
                new PopupMenuItem("Exit", (_, _) => state.OnUi(exit)),
            },
        };

        _tray.MessageWindow.MouseEventReceived += (_, e) =>
        {
            if (e.MouseEvent is MouseEvent.IconLeftMouseUp or MouseEvent.IconDoubleClick) state.OnUi(open);
        };
        _tray.Create();

        state.Changed += _ => UpdateToolTip(state);
    }

    /// <summary>Keeps the menu's check marks true if they were changed in the window.</summary>
    public void SyncChecks(UiSettings settings)
    {
        _autoRefresh.Checked = settings.AutoRefresh;
        _startAtLogin.Checked = StartupRegistration.IsEnabled;
    }

    /// <summary>
    /// "Claude Code today: $12.34". Each agent carries its own cost label's
    /// meaning: Codex's figure is API-equivalent, and the tooltip says so.
    /// </summary>
    private void UpdateToolTip(AppState state)
    {
        var lines = new List<string> { "AI Usage" };
        foreach (var meta in Providers.All)
        {
            if (state.For(meta.Id).Report is not { } report || ReportState.IsEmpty(report)) continue;
            var today = DayRanges.Today(report);
            var cost = report.Daily.FirstOrDefault(d => d.Date == today)?.Combined.CostUsd ?? 0;
            var basis = meta.Id == ProviderId.Codex ? " (API-equiv.)" : "";
            lines.Add($"{meta.Label} today: {Format.Usd(cost)}{basis}");
        }
        // The shell truncates a tray tooltip at 127 characters.
        var text = string.Join("\n", lines);
        _tray.UpdateToolTip(text.Length > 127 ? text[..127] : text);
    }

    public void Notify(string title, string message) =>
        _tray.ShowNotification(title, message, NotificationIcon.Info, null, false, false, false, false, null);

    public void Dispose()
    {
        _tray.Dispose();
        _icon.Dispose();
    }
}
