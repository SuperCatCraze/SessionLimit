using System.Windows;
using System.Windows.Threading;

namespace SessionLimit;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A crash in a background collector must never take the overlay down.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled domain exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        // Relaunched by the updater: the build we replaced is still winding down, and two
        // instances tailing the transcripts would both append to the same ledger.
        if (e.Args.Contains(Updater.UpdatedFlag))
        {
            Log.Info($"restarted after update, now {Updater.Display}");
            Updater.WaitForPreviousExit(TimeSpan.FromSeconds(10));
        }

        var win = new OverlayWindow();
        MainWindow = win;
        win.Show();
    }
}
