using System.Drawing;
using H.NotifyIcon.Core;
using UsageCore;
using UsageCore.Model;
using UsageCore.View;

namespace UsageApp.State;

/// <summary>
/// The notification-area icon: today's spend for each agent in its tooltip,
/// click to open, right-click for the menu. It belongs to the tray process
/// (<see cref="TrayProcess"/>), which outlives the window.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private readonly TrayIconWithContextMenu _tray;
    private readonly Icon _icon;
    private readonly PopupMenuItem _startAtLogin;
    private readonly PopupMenuItem _autoRefresh;

    /// <param name="onUi">Marshals a menu click, which arrives on the icon's thread, onto the UI thread.</param>
    /// <param name="toggleAutoRefresh">Flips the setting and returns its new value.</param>
    public TrayHost(Action<Action> onUi, bool autoRefresh, Action open, Action refresh, Func<bool> toggleAutoRefresh, Action exit)
    {
        _icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"), 16, 16);
        // The id is per exe path. Windows binds a tray icon's id to the exe
        // that first registered it and silently refuses it from any other:
        // once a Debug build has run, the installed app comes up with no tray
        // icon at all (HKCU\Control Panel\NotifyIconSettings holds the Debug
        // path), and closed to the tray it cannot be brought back.
        _tray = new TrayIconWithContextMenu($"AIUsage.Tray|{Environment.ProcessPath}")
        {
            Icon = _icon.Handle,
            ToolTip = "AI Usage",
        };

        _startAtLogin = new PopupMenuItem("Start at login", (_, _) => onUi(() =>
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

        _autoRefresh = new PopupMenuItem("Refresh when transcripts change", (_, _) => onUi(() => _autoRefresh!.Checked = toggleAutoRefresh()))
        { Checked = autoRefresh };

        _tray.ContextMenu = new PopupMenu
        {
            Items =
            {
                new PopupMenuItem("Open AI Usage", (_, _) => onUi(open)),
                new PopupMenuItem("Refresh now", (_, _) => onUi(refresh)),
                new PopupMenuSeparator(),
                _autoRefresh,
                _startAtLogin,
                new PopupMenuSeparator(),
                new PopupMenuItem("Exit", (_, _) => onUi(exit)),
            },
        };

        _tray.MessageWindow.MouseEventReceived += (_, e) =>
        {
            if (e.MouseEvent is MouseEvent.IconLeftMouseUp or MouseEvent.IconDoubleClick) onUi(open);
        };
        _tray.Create();
        MenuTheme.Allow(_tray.MessageWindow.Handle);
    }

    /// <summary>Keeps the menu's check marks true if they were changed in the window.</summary>
    public void SyncChecks(bool autoRefresh)
    {
        _autoRefresh.Checked = autoRefresh;
        _startAtLogin.Checked = StartupRegistration.IsEnabled;
    }

    /// <summary>
    /// "Claude Code today: $12.34", an agent's tooltip line, all worded alike
    /// by the owner's choice (2026-10-28); Codex's used to add "(API-equiv.)".
    /// Null when the agent has nothing to show.
    /// </summary>
    public static string? Line(ProviderMeta meta, UsageReport report)
    {
        if (ReportState.IsEmpty(report)) return null;
        var today = DayRanges.Today(report);
        var cost = report.Daily.FirstOrDefault(d => d.Date == today)?.Combined.CostUsd ?? 0;
        return $"{meta.Label} today: {Format.Usd(cost)}";
    }

    /// <summary>Shows the agents' lines, in the agents' own order.</summary>
    public void ShowLines(IReadOnlyDictionary<ProviderId, string> lines)
    {
        var all = new List<string> { "AI Usage" };
        foreach (var meta in Providers.All)
        {
            if (lines.TryGetValue(meta.Id, out var line)) all.Add(line);
        }
        // The shell truncates a tray tooltip at 127 characters.
        var text = string.Join("\n", all);
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
