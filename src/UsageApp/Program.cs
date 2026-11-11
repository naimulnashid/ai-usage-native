using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using UsageApp.State;
using Windows.ApplicationModel.Activation;

namespace UsageApp;

/// <summary>
/// The entry point of one exe that runs as two processes.
/// </summary>
/// <remarks>
/// <para><b>The tray</b> (the default, and <c>--tray</c> at login): the icon,
/// its tooltip and menu, on a plain message loop. It runs all day, so it never
/// loads WinUI (see <see cref="TrayProcess"/>).</para>
/// <para><b>The dashboard</b> (<c>--dashboard</c>): the WinUI window, started
/// by the tray when it is asked for and gone when it closes, taking WinUI's
/// memory with it. Started on its own it is a window with no tray, which is
/// what the capture tools do.</para>
/// <para>Each is a single instance. A second launch of the tray (the Start
/// menu, a double-click) hands over to the running one, which opens the
/// dashboard; a second dashboard hands over to the open one, which comes
/// forward - or, given one of <see cref="Requests"/>, does that instead.</para>
/// </remarks>
public static class Program
{
    /// <summary>
    /// What the tray asks of an open dashboard, as a flag on a second
    /// <c>--dashboard</c> launch. A launch carrying one is a messenger: with
    /// no dashboard to hand it to, it ends without becoming a window.
    /// </summary>
    internal static readonly string[] Requests = ["--refresh", "--reload-settings", "--exit"];

    /// <summary>
    /// "Main", or a hash of the data folder. One instance per data folder, not
    /// per machine: a copy pointed elsewhere by <c>AIUSAGE_DATA_DIR</c> - the
    /// demo, a screenshot run - is a different app's worth of state, and must
    /// not be folded into the copy in the tray.
    /// </summary>
    public static string InstanceSuffix { get; } = ComputeSuffix();

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        return args.Contains("--dashboard", StringComparer.OrdinalIgnoreCase) ? DashboardMain(args) : TrayMain(args);
    }

    private static string ComputeSuffix()
    {
        if (Environment.GetEnvironmentVariable("AIUSAGE_DATA_DIR") is not { Length: > 0 } dir) return "Main";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dir).ToUpperInvariant()));
        return Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>
    /// Hands this launch to the instance that holds the key. This process was
    /// started by the user, so it may bring a window forward; the one it
    /// hands over to may not, unless told it can (-1: any process).
    /// </summary>
    private static int Redirect(AppInstance running)
    {
        AllowSetForegroundWindow(-1);
        running.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask().Wait();
        return 0;
    }

    /* --------------------------------------------------------------- Tray */

    private static int TrayMain(string[] args)
    {
        var instance = AppInstance.FindOrRegisterForKey("AIUsage.Tray." + InstanceSuffix);
        if (!instance.IsCurrent) return Redirect(instance);

        var ui = DispatcherQueueController.CreateOnCurrentThread().DispatcherQueue;
        SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(ui));

        // Launched by the Run key at login: the icon only, until the window is asked for.
        var tray = new TrayProcess(ui, openDashboard: !args.Contains("--tray", StringComparer.OrdinalIgnoreCase));
        // A second launch of the app is someone asking for the window.
        instance.Activated += (_, _) => ui.TryEnqueue(tray.OpenDashboard);

        ui.RunEventLoop();
        return 0;
    }

    /* ---------------------------------------------------------- Dashboard */

    private static int DashboardMain(string[] args)
    {
        var instance = AppInstance.FindOrRegisterForKey("AIUsage.Dashboard." + InstanceSuffix);
        if (!instance.IsCurrent) return Redirect(instance);
        // A request with no dashboard to carry it out.
        if (args.Any(a => Requests.Contains(a, StringComparer.OrdinalIgnoreCase))) return 0;

        instance.Activated += (_, e) => App.OnRedirected(e.Data is ILaunchActivatedEventArgs launch ? launch.Arguments : null);

        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
