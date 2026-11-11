using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using UsageApp.State;
using UsageCore;
using UsageCore.Model;

namespace UsageApp;

/// <summary>
/// The dashboard process: the window, its state and its watcher. The tray
/// icon is another process (<see cref="TrayProcess"/>), told what it needs
/// over <see cref="TrayLink"/>.
/// </summary>
public partial class App : Application
{
    private static App? _instance;
    private static DispatcherQueue? _ui;

    private MainWindow? _window;
    private AppState? _state;
    private LogoStore? _logos;
    private TranscriptWatcher? _watcher;
    private UiSettings _settings = new();
    private readonly Dictionary<ProviderId, string> _sentLines = [];
    private bool _linked;

    public App()
    {
        InitializeComponent();
        _instance = this;
    }

    /// <summary>
    /// A second <c>--dashboard</c> launch was redirected here: one of the
    /// tray's requests (<see cref="Program.Requests"/>), or else someone
    /// asking for the window.
    /// </summary>
    internal static void OnRedirected(string? arguments) => _ui?.TryEnqueue(() =>
    {
        if (_instance is not { _window: { } window, _state: { } state } app) return;
        var args = (arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Asked(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

        if (Asked("--exit")) app.Quit();
        else if (Asked("--refresh"))
        {
            foreach (var meta in Providers.All) _ = state.RefreshAsync(meta.Id);
        }
        // The one setting the tray's menu changes; the rest are this window's own.
        else if (Asked("--reload-settings")) app._settings.AutoRefresh = UiSettings.Load().AutoRefresh;
        else
        {
            // A tray started after this window does not know it yet.
            app.Link();
            window.ShowAndActivate();
        }
    });

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _settings = UiSettings.Load();
        _state = new AppState(_ui);
        _logos = new LogoStore(_ui);
        _window = new MainWindow(_state, _logos, _settings, QuitAll);
        _window.SettingsChanged += () => TrayLink.Send("settings");
        // The window is the process: closing it gives WinUI's memory back.
        // Whether the tray icon stays is the tray's business ("Keep running
        // in the tray when closed").
        _window.Closed += (_, _) => Quit();
        _watcher = new TranscriptWatcher(_state.RefreshAsync, _ui, () => _settings.AutoRefresh);

        _state.Changed += SendLine;
        Link();
        _window.OpenMaximized();

        // Every agent loads up front, one after the other: the tray shows them
        // all, whichever one is on screen.
        _ = LoadAll();
    }

    private async Task LoadAll()
    {
        foreach (var meta in Providers.All) await _state!.RefreshAsync(meta.Id);
    }

    /// <summary>Tells the tray this window exists, and what it has read so far. No tray: a window on its own.</summary>
    private void Link()
    {
        _linked = TrayLink.Send("hello", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _sentLines.Clear();
        foreach (var meta in Providers.All) SendLine(meta.Id);
    }

    /// <summary>An agent's tooltip line, when it is new: this process reads the transcripts while it is open.</summary>
    private void SendLine(ProviderId id)
    {
        if (!_linked || _state!.For(id).Report is not { } report) return;
        var meta = Providers.Get(id);
        var line = TrayHost.Line(meta, report) ?? "";
        if (_sentLines.TryGetValue(id, out var sent) && sent == line) return;
        _sentLines[id] = line;
        _linked = TrayLink.Send("tip", meta.Slug, line);
    }

    /// <summary>Exit, chosen in the window: the tray goes too.</summary>
    private void QuitAll()
    {
        TrayLink.Send("exit");
        Quit();
    }

    private bool _quitting;

    private void Quit()
    {
        // Exit() closes the window, whose Closed comes back here.
        if (_quitting) return;
        _quitting = true;
        _watcher?.Dispose();
        _logos?.Dispose();
        Exit();
        Environment.Exit(0);
    }
}
