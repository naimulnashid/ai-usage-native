using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace UsageApp;

/// <summary>
/// A custom entry point for one reason: a single instance. The app lives in
/// the tray and can be started at login, so a second launch (the Start menu,
/// a double-click) must bring the running copy forward, not start a second
/// one watching the same files.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey());
        if (!instance.IsCurrent)
        {
            // Hand the launch to the running copy, which shows its window.
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            instance.RedirectActivationToAsync(activation).AsTask().Wait();
            return 0;
        }

        instance.Activated += (_, _) => App.OnRedirected();

        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>
    /// One instance per data folder, not per machine. A copy pointed elsewhere
    /// by <c>AIUSAGE_DATA_DIR</c> - the demo, a screenshot run - is a different
    /// app's worth of state, and must not be folded into the copy in the tray.
    /// </summary>
    private static string InstanceKey()
    {
        if (Environment.GetEnvironmentVariable("AIUSAGE_DATA_DIR") is not { Length: > 0 } dir) return "AIUsage.Main";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dir).ToUpperInvariant()));
        return "AIUsage." + Convert.ToHexString(hash, 0, 8);
    }
}
