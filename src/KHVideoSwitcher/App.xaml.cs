using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;
using KHVideoSwitcher.Diagnostics;

namespace KHVideoSwitcher;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppLog.Cleanup();
        AppLog.Info($"App starting, version {UpdateChecker.CurrentVersion}");

        // Catch-all so a crash still leaves a log entry a tester can hand over
        // via Report a Bug, instead of the app just vanishing with no trace.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Unhandled UI exception", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLog.Error("Unhandled exception", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }
}

