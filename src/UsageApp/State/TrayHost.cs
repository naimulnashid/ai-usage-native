using System.Drawing;
using System.Runtime.InteropServices;
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
        try
        {
            AllowDarkModeForWindow(_tray.MessageWindow.Handle, true);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }

        state.Changed += _ => UpdateToolTip(state);
    }

    // Undocumented uxtheme ordinals, stable since Windows 10 1903, which
    // Explorer and Notepad++ use for the same job: no public API themes a
    // Win32 menu.
    [DllImport("uxtheme.dll", EntryPoint = "#133")]
    private static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    /// <summary>
    /// The menu is a Win32 one (TrackPopupMenuEx), so it follows neither
    /// WinUI's RequestedTheme nor Windows' app mode on its own. This sets it
    /// for the whole process to the app's resolved theme, "system" included.
    /// </summary>
    public static void ApplyMenuTheme(bool light)
    {
        try
        {
            SetPreferredAppMode(light ? 3 : 2); // ForceLight : ForceDark
            FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // An older Windows: the menu keeps the system's look.
        }
    }

    /// <summary>Keeps the menu's check marks true if they were changed in the window.</summary>
    public void SyncChecks(UiSettings settings)
    {
        _autoRefresh.Checked = settings.AutoRefresh;
        _startAtLogin.Checked = StartupRegistration.IsEnabled;
    }

    /// <summary>
    /// "Claude Code today: $12.34", one line per agent, both worded alike by
    /// the owner's choice (2026-10-28); Codex's used to add "(API-equiv.)".
    /// </summary>
    private void UpdateToolTip(AppState state)
    {
        var lines = new List<string> { "AI Usage" };
        foreach (var meta in Providers.All)
        {
            if (state.For(meta.Id).Report is not { } report || ReportState.IsEmpty(report)) continue;
            var today = DayRanges.Today(report);
            var cost = report.Daily.FirstOrDefault(d => d.Date == today)?.Combined.CostUsd ?? 0;
            lines.Add($"{meta.Label} today: {Format.Usd(cost)}");
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
