using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using UsageApp.Imaging;
using UsageApp.State;
using UsageApp.Theme;
using UsageApp.Views;
using UsageCore;
using UsageCore.Model;
using Windows.Graphics;

namespace UsageApp;

/// <summary>
/// The shell: title bar, the agent rail, the top bar, and the page area.
/// </summary>
/// <remarks>
/// <b>The rail answers "which agent", the top bar answers "which page".</b>
/// The split is deliberate: page links in the same column as the agents made
/// switching agent look like changing page. The rail's expand control sits at
/// its top, next to what it opens.
/// </remarks>
public sealed partial class MainWindow : Window
{
    private const double RailExpanded = 252, RailCollapsed = 70;

    private readonly AppState _state;
    private readonly LogoStore _logos;
    private readonly UiSettings _settings;
    private readonly Action _exit;
    private readonly PageContext _ctx;

    private readonly Border _rail = new();
    private readonly TextBlock _railBrand = Ui.Text("AI Usage", 16, 600, spacing: -0.01);
    private readonly TextBlock _railGroup = Ui.Caps("Agent", 11, 0.09);
    private readonly Dictionary<ProviderId, (Button Button, TextBlock Label)> _railItems = [];
    private readonly TextBlock _heading = Ui.Text("", 17, 640, spacing: -0.015);
    private readonly Dictionary<PageKind, Button> _tabs = [];
    private readonly TextBlock _refreshMeta = Ui.Text("", 13.5, 400, Palette.TextFaintBrush, numeric: true);
    private readonly Button _refresh;
    private readonly ScrollViewer _scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ContentControl _pageHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };

    private Route _route = new(PageKind.Overview);
    private IPage? _page;
    private (ProviderId, int)? _shown;

    public MainWindow(AppState state, LogoStore logos, UiSettings settings, Action exit)
    {
        InitializeComponent();
        _state = state;
        _logos = logos;
        _settings = settings;
        _exit = exit;
        _ctx = new PageContext { State = state, Logos = logos, Navigate = Navigate, Window = this };
        _refresh = Ui.Button("", primary: true);
        _refresh.Click += (_, _) => _ = _state.RefreshAsync(_state.Provider);

        Palette.ApplyProvider(state.Meta);
        ConfigureWindow();
        BuildShell();

        state.Changed += OnStateChanged;
        logos.Changed += () => Rebuild(keepScroll: true);
        UpdateChrome();
        Rebuild(keepScroll: false);
    }

    /* ------------------------------------------------------------- Window */

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = Palette.TextMuted;
        bar.ButtonInactiveForegroundColor = Palette.TextFaint;
        bar.ButtonHoverBackgroundColor = Palette.SurfaceHover;
        bar.ButtonHoverForegroundColor = Palette.Text;
        bar.ButtonPressedBackgroundColor = Palette.BorderBright;
        bar.ButtonPressedForegroundColor = Palette.Text;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.Title = "AI Usage";

        // 1440 x 960 at the display's scale, never larger than its work area.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1440 * scale), area.Width - 40);
        var height = Math.Min((int)(960 * scale), area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(760 * scale);
            presenter.PreferredMinimumHeight = (int)(520 * scale);
        }

        AppWindow.Closing += (_, args) =>
        {
            if (!_settings.CloseToTray) return;
            // Keep running in the tray: the numbers keep updating, and the
            // window comes back from the tray icon.
            args.Cancel = true;
            AppWindow.Hide();
            EnterTray();
            ClosedToTray?.Invoke();
        };
    }

    private bool _inTray;

    /// <summary>
    /// Nothing is on screen, so nothing needs to be built: drop the page and let
    /// the tray process sit small. It is rebuilt when the window comes back.
    /// </summary>
    public void EnterTray()
    {
        _inTray = true;
        _pageHost.Content = null;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <summary>Raised when the close button sent the window to the tray.</summary>
    public event Action? ClosedToTray;

    public void ShowAndActivate()
    {
        if (_inTray)
        {
            _inTray = false;
            UpdateChrome();
            Rebuild(keepScroll: false);
        }
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
    }

    /* -------------------------------------------------------------- Shell */

    private void BuildShell()
    {
        Root.Background = Palette.BgBrush;
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Title bar: draggable, the app's name, and room for the caption buttons.
        var titleBar = new Grid { Background = Palette.BgBrush, Padding = new Thickness(16, 0, 150, 0) };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var icon = new Image { Width = 16, Height = 16 };
        title.Children.Add(icon);
        title.Children.Add(Ui.Text("AI Usage", 12.5, 500, Palette.TextFaintBrush));
        titleBar.Children.Add(title);
        Root.Children.Add(titleBar);
        SetTitleBar(titleBar);
        titleBar.Loaded += async (_, _) => icon.Source = await ImageLoader.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.svg"), 16, titleBar.XamlRoot?.RasterizationScale ?? 1);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        Root.Children.Add(body);

        body.Children.Add(BuildRail());

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(main, 1);
        body.Children.Add(main);
        main.Children.Add(BuildTopBar());

        var column = new StackPanel { MaxWidth = 1320 + 64, Padding = new Thickness(32, 0, 32, 40) };
        column.Children.Add(_pageHost);
        column.Children.Add(Footer());
        _scroller.Content = column;
        Grid.SetRow(_scroller, 1);
        main.Children.Add(_scroller);

        // F5 re-reads the current agent, as a browser would reload.
        var f5 = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F5 };
        f5.Invoked += (sender, e) =>
        {
            e.Handled = true;
            _ = _state.RefreshAsync(_state.Provider);
        };
        Root.KeyboardAccelerators.Add(f5);
    }

    private static Border Footer()
    {
        var text = Ui.Text("Not affiliated with Anthropic or OpenAI. Claude Code and Codex are trademarks of their owners.", 12.5, 400, Palette.TextFaintBrush, wrap: true);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.TextAlignment = TextAlignment.Center;
        return new Border
        {
            Margin = new Thickness(0, 54, 0, 0),
            Padding = new Thickness(0, 22, 0, 0),
            BorderBrush = Palette.BorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = text,
        };
    }

    /* --------------------------------------------------------------- Rail */

    private Border BuildRail()
    {
        var stack = new StackPanel { Spacing = 4 };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(4, 0, 4, 18) };
        var burger = new Button
        {
            Width = 38,
            Height = 38,
            Padding = new Thickness(0),
            Background = Palette.TransparentBrush,
            BorderBrush = Palette.TransparentBrush,
            CornerRadius = new CornerRadius(Ui.RadiusSmall),
            Content = new FontIcon { Glyph = "", FontSize = 17, Foreground = Palette.TextMutedBrush },
        };
        burger.Resources["ButtonBackgroundPointerOver"] = Palette.SurfaceHoverBrush;
        burger.Resources["ButtonBorderBrushPointerOver"] = Palette.BorderBrightBrush;
        burger.Click += (_, _) =>
        {
            _settings.RailCollapsed = !_settings.RailCollapsed;
            _settings.Save();
            ApplyRail(animate: true);
            UpdateBurgerLabel(burger);
        };
        UpdateBurgerLabel(burger);
        top.Children.Add(burger);
        _railBrand.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(_railBrand);
        stack.Children.Add(top);

        _railGroup.Margin = new Thickness(10, 0, 10, 8);
        stack.Children.Add(_railGroup);

        foreach (var meta in Providers.All)
        {
            var mark = new Image { Width = meta.Id == ProviderId.Codex ? 44 : 24, Height = meta.Id == ProviderId.Codex ? 44 : 24, Margin = new Thickness(meta.Id == ProviderId.Codex ? -10 : 0) };
            // The vendors pad their files differently: Anthropic's artwork fills
            // its viewBox, OpenAI's fills half of it. A 44px image pulled back
            // 10px each side keeps a 24px footprint and makes the marks look
            // the same size. The files themselves are untouched.
            var markFile = Path.Combine(AppContext.BaseDirectory, "Assets", "AgentMarks", meta.Id == ProviderId.Claude ? "claude-code.svg" : "codex.svg");
            mark.Loaded += async (_, _) => mark.Source = await ImageLoader.LoadAsync(markFile, mark.Width, mark.XamlRoot?.RasterizationScale ?? 1);
            var markBox = new Grid { Width = 24, Height = 24 };
            markBox.Children.Add(mark);

            var label = Ui.Text(meta.Label, 15, 500, Palette.TextMutedBrush);
            label.VerticalAlignment = VerticalAlignment.Center;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            content.Children.Add(markBox);
            content.Children.Add(label);
            var item = new Button
            {
                Content = content,
                Height = 42,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(Ui.RadiusSmall),
                BorderThickness = new Thickness(1),
            };
            item.Resources["ButtonBackgroundPointerOver"] = Palette.SurfaceHoverBrush;
            item.Resources["ButtonBackgroundPressed"] = Palette.SurfaceHoverBrush;
            ToolTipService.SetToolTip(item, meta.Label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, meta.Label);
            var id = meta.Id;
            item.Click += (_, _) => SelectProvider(id);
            _railItems[id] = (item, label);
            stack.Children.Add(item);
        }

        _rail.Child = stack;
        _rail.Background = Palette.SurfaceBrush;
        _rail.BorderBrush = Palette.BorderBrush;
        _rail.BorderThickness = new Thickness(0, 1, 1, 0);
        _rail.Padding = new Thickness(12, 18, 12, 14);
        ApplyRail(animate: false);
        return _rail;
    }

    private void UpdateBurgerLabel(Button burger)
    {
        var label = _settings.RailCollapsed ? "Expand sidebar" : "Collapse sidebar";
        ToolTipService.SetToolTip(burger, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(burger, label);
    }

    private void ApplyRail(bool animate)
    {
        var collapsed = _settings.RailCollapsed;
        var target = collapsed ? RailCollapsed : RailExpanded;
        var visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _railBrand.Visibility = visibility;
        _railGroup.Visibility = visibility;
        foreach (var (button, label) in _railItems.Values)
        {
            label.Visibility = visibility;
            button.HorizontalContentAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            button.Padding = collapsed ? new Thickness(0) : new Thickness(10, 0, 10, 0);
        }
        if (!animate || !Motion.Enabled || double.IsNaN(_rail.Width))
        {
            _rail.Width = target;
            return;
        }
        var story = new Storyboard();
        var anim = new DoubleAnimation
        {
            From = _rail.ActualWidth,
            To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(anim, _rail);
        Storyboard.SetTargetProperty(anim, "Width");
        story.Children.Add(anim);
        story.Completed += (_, _) => _rail.Width = target;
        story.Begin();
    }

    /* ------------------------------------------------------------ Top bar */

    private Border BuildTopBar()
    {
        var grid = new Grid { ColumnSpacing = 28, MaxWidth = 1320, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _heading.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(_heading);

        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (kind, label) in new[] { (PageKind.Overview, "Overview"), (PageKind.Projects, "Projects") })
        {
            var tab = new Button
            {
                Content = Ui.Text(label, 15.5, 500, Palette.TextMutedBrush),
                Padding = new Thickness(15, 8, 15, 8),
                CornerRadius = new CornerRadius(Ui.RadiusSmall),
                BorderThickness = new Thickness(1),
            };
            tab.Resources["ButtonBackgroundPointerOver"] = Palette.SurfaceHoverBrush;
            tab.Resources["ButtonBackgroundPressed"] = Palette.SurfaceHoverBrush;
            var target = kind;
            tab.Click += (_, _) => Navigate(new Route(target));
            _tabs[kind] = tab;
            nav.Children.Add(tab);
        }
        Grid.SetColumn(nav, 1);
        grid.Children.Add(nav);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Right };
        _refreshMeta.VerticalAlignment = VerticalAlignment.Center;
        right.Children.Add(_refreshMeta);
        right.Children.Add(SettingsButton());
        right.Children.Add(_refresh);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var bar = new Border
        {
            Padding = new Thickness(32, 20, 32, 20),
            BorderBrush = Palette.BorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Background = Palette.BgBrush,
            Child = grid,
        };
        return bar;
    }

    /// <summary>The app's own options, which the original had no need for.</summary>
    private Button SettingsButton()
    {
        var button = Ui.Button(new FontIcon { Glyph = "", FontSize = 15 }, padding: new Thickness(11, 10, 11, 10));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Settings");
        ToolTipService.SetToolTip(button, "Settings");
        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };

        var login = new ToggleMenuFlyoutItem { Text = "Start at login" };
        login.Click += (_, _) =>
        {
            try
            {
                StartupRegistration.Set(login.IsChecked);
            }
            catch (Exception ex)
            {
                _ = Shell.ShowMessage(this, "Could not change start at login", ex.Message);
            }
            login.IsChecked = StartupRegistration.IsEnabled;
            SettingsChanged?.Invoke();
        };
        var auto = new ToggleMenuFlyoutItem { Text = "Refresh when transcripts change" };
        auto.Click += (_, _) =>
        {
            _settings.AutoRefresh = auto.IsChecked;
            _settings.Save();
            SettingsChanged?.Invoke();
        };
        var tray = new ToggleMenuFlyoutItem { Text = "Keep running in the tray when closed" };
        tray.Click += (_, _) =>
        {
            _settings.CloseToTray = tray.IsChecked;
            _settings.Save();
        };
        flyout.Opening += (_, _) =>
        {
            login.IsChecked = StartupRegistration.IsEnabled;
            auto.IsChecked = _settings.AutoRefresh;
            tray.IsChecked = _settings.CloseToTray;
        };
        flyout.Items.Add(login);
        flyout.Items.Add(auto);
        flyout.Items.Add(tray);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var import = new MenuFlyoutItem { Text = "Import project logos…", Icon = new FontIcon { Glyph = "" } };
        import.Click += async (_, _) => await ImportLogos();
        flyout.Items.Add(import);
        var logos = new MenuFlyoutItem { Text = "Open project logos folder", Icon = new FontIcon { Glyph = "" } };
        logos.Click += (_, _) => Shell.OpenFolder(AppPaths.LogoDir(_state.Provider));
        flyout.Items.Add(logos);
        var data = new MenuFlyoutItem { Text = "Open settings folder", Icon = new FontIcon { Glyph = "" } };
        data.Click += (_, _) => Shell.OpenFolder(AppPaths.ConfigDir);
        flyout.Items.Add(data);
        flyout.Items.Add(new MenuFlyoutSeparator());
        var exit = new MenuFlyoutItem { Text = "Exit", Icon = new FontIcon { Glyph = "" } };
        exit.Click += (_, _) => _exit();
        flyout.Items.Add(exit);

        button.Flyout = flyout;
        return button;
    }

    /// <summary>Raised when a setting the tray also shows was changed here.</summary>
    public event Action? SettingsChanged;

    /// <summary>Copies a folder of logos in for the current agent - once, not a link to it.</summary>
    private async Task ImportLogos()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        try
        {
            var count = _logos.Import(_state.Provider, folder.Path);
            await Shell.ShowMessage(this, "Logos imported", $"Copied {count} image{(count == 1 ? "" : "s")} into the {_state.Meta.Label} logos folder. A file named after a project becomes its logo.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Shell.ShowMessage(this, "Could not import logos", ex.Message);
        }
    }

    /* --------------------------------------------------------- Navigation */

    private void SelectProvider(ProviderId id)
    {
        if (id == _state.Provider) return;
        Palette.ApplyProvider(Providers.Get(id));
        _route = new Route(PageKind.Overview);
        _page = null;
        _state.SelectProvider(id);
        UpdateChrome();
        Rebuild(keepScroll: false);
    }

    private void Navigate(Route route)
    {
        var same = route == _route;
        _route = route;
        if (!same) _page = null;
        Rebuild(keepScroll: same);
        UpdateChrome();
    }

    private void Rebuild(bool keepScroll)
    {
        var offset = _scroller.VerticalOffset;
        _page ??= _route.Kind switch
        {
            PageKind.Projects => new ProjectsPage(_ctx),
            PageKind.Project => new ProjectDetailPage(_ctx, _route.ProjectId ?? ""),
            PageKind.Activity => new ActivityPage(_ctx),
            _ => new OverviewPage(_ctx),
        };
        try
        {
            _pageHost.Content = _page.Build();
        }
        catch (Exception ex)
        {
            // One bad value must not leave a blank window: keep the chrome,
            // say what failed, and let Refresh try again.
            _pageHost.Content = RenderError(ex);
        }
        _shown = (_state.Provider, _state.Current.Version);
        if (keepScroll)
        {
            _scroller.UpdateLayout();
            _scroller.ChangeView(null, offset, null, disableAnimation: true);
        }
        else
        {
            _scroller.ChangeView(null, 0, null, disableAnimation: true);
        }
    }

    private static UIElement RenderError(Exception ex)
    {
        var page = new StackPanel { Padding = new Thickness(0, 34, 0, 0) };
        var detail = string.Join("\n", ex.ToString().Split('\n').Take(12));
        page.Children.Add(Parts.Notice(Ui.Paragraph($"This page could not be drawn. Refresh to try again.\n\n{detail}", 13.5, Palette.TextMutedBrush)));
        return page;
    }

    private void OnStateChanged(ProviderId id)
    {
        UpdateChrome();
        // Hidden in the tray: the tray tooltip updates itself; the page is
        // built when the window comes back.
        if (_inTray || id != _state.Provider) return;
        // Rebuild when there is something new to show; a refresh in progress
        // keeps the previous report on screen rather than flashing a skeleton.
        var data = _state.Current;
        var fresh = _shown != (id, data.Version);
        var failedFirstLoad = data.Report is null && !data.Loading;
        if (fresh || failedFirstLoad) Rebuild(keepScroll: true);
        if (data.Report is not null) ApplyDebugView();
    }

    private bool _debugApplied;

    /// <summary>
    /// Development only: <c>AIUSAGE_DEBUG_VIEW=agent,page[,projectId],scroll</c>
    /// opens a given page at a given scroll offset once the data is in, so
    /// tools/Capture-Window.ps1 can photograph any section of any page.
    /// </summary>
    private void ApplyDebugView()
    {
        if (_debugApplied || Environment.GetEnvironmentVariable("AIUSAGE_DEBUG_VIEW") is not { Length: > 0 } spec) return;
        _debugApplied = true;
        var parts = spec.Split(',');
        if (parts.Length < 2) return;
        var provider = parts[0].Equals("codex", StringComparison.OrdinalIgnoreCase) ? ProviderId.Codex : ProviderId.Claude;
        if (provider != _state.Provider)
        {
            SelectProvider(provider);
            _debugApplied = false;
            return;
        }
        var kind = Enum.TryParse<PageKind>(parts[1], true, out var k) ? k : PageKind.Overview;
        var project = kind == PageKind.Project && parts.Length > 2 ? parts[2] : null;
        var scroll = double.TryParse(parts[^1], out var s) ? s : 0;
        Navigate(new Route(kind, project));
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _scroller.UpdateLayout();
            _scroller.ChangeView(null, scroll, null, disableAnimation: true);
        });
    }

    private void UpdateChrome()
    {
        var meta = _state.Meta;
        var data = _state.Current;
        _heading.Text = meta.Label;
        Title = $"{meta.Label} · AI Usage";

        _refreshMeta.Text = data.Loading
            ? "Reading transcripts…"
            : data.LastRefreshed is { } at
                ? $"Last refreshed {UsageCore.View.Format.ClockTime(at)} · {data.ParseTime.TotalSeconds:0.0}s"
                : "—";

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        if (data.Loading)
        {
            content.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14, Foreground = Palette.AccentBright });
            content.Children.Add(Ui.Text("Refreshing", 15, 570, Palette.AccentBright));
        }
        else
        {
            content.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Foreground = Palette.AccentBright });
            content.Children.Add(Ui.Text("Refresh", 15, 570, Palette.AccentBright));
        }
        _refresh.Content = content;
        _refresh.IsEnabled = !data.Loading;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_refresh, $"Re-read all {meta.Label} transcripts from disk and recompute");

        foreach (var (kind, tab) in _tabs)
        {
            var active = kind == _route.Kind || (kind == PageKind.Projects && _route.Kind == PageKind.Project) || (kind == PageKind.Overview && _route.Kind == PageKind.Activity);
            tab.Background = active ? Palette.AccentDim : Palette.TransparentBrush;
            tab.BorderBrush = active ? Palette.AccentBorder : Palette.TransparentBrush;
            if (tab.Content is TextBlock text) text.Foreground = active ? Palette.Accent : Palette.TextMutedBrush;
        }
        foreach (var (id, (button, label)) in _railItems)
        {
            var active = id == _state.Provider;
            button.Background = active ? Palette.AccentDim : Palette.TransparentBrush;
            button.BorderBrush = active ? Palette.AccentBorder : Palette.TransparentBrush;
            label.Foreground = active ? Palette.Accent : Palette.TextMutedBrush;
        }
    }
}
