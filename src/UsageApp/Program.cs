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
        var instance = AppInstance.FindOrRegisterForKey("AIUsage.Main");
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
}
