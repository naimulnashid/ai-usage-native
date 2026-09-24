using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using UsageApp.State;
using UsageCore.Model;

namespace UsageApp;

public partial class App : Application
{
    private static App? _instance;
    private static DispatcherQueue? _ui;

    private MainWindow? _window;
    private AppState? _state;
    private LogoStore? _logos;
    private TranscriptWatcher? _watcher;
    private TrayHost? _tray;
    private UiSettings _settings = new();

    public App()
    {
        InitializeComponent();
        _instance = this;
    }

    /// <summary>A second launch was redirected here: bring the window back.</summary>
    internal static void OnRedirected() => _ui?.TryEnqueue(() => _instance?._window?.ShowAndActivate());

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _settings = UiSettings.Load();
        _state = new AppState(_ui);
        _logos = new LogoStore(_ui);
        _window = new MainWindow(_state, _logos, _settings, Quit);

        _tray = new TrayHost(_state, _settings, () => _window.ShowAndActivate(), Quit);
        _window.SettingsChanged += () => _tray.SyncChecks(_settings);
        _window.ClosedToTray += () =>
        {
            if (_settings.TrayNoteShown) return;
            _settings.TrayNoteShown = true;
            _settings.Save();
            _tray.Notify("AI Usage is still running", "It keeps its numbers up to date from the notification area. Right-click the icon to exit.");
        };
        _watcher = new TranscriptWatcher(_state, _ui, () => _settings.AutoRefresh);

        // Launched by the Run key at login: straight to the tray.
        var startInTray = Environment.GetCommandLineArgs().Contains("--tray", StringComparer.OrdinalIgnoreCase);
        if (startInTray) _window.EnterTray();
        else _window.Activate();

        // Both agents load up front, one after the other: the tray shows both,
        // whichever one is on screen.
        _ = LoadBoth();
    }

    private async Task LoadBoth()
    {
        await _state!.RefreshAsync(ProviderId.Claude);
        await _state.RefreshAsync(ProviderId.Codex);
    }

    private void Quit()
    {
        _watcher?.Dispose();
        _logos?.Dispose();
        _tray?.Dispose();
        Exit();
        Environment.Exit(0);
    }
}
