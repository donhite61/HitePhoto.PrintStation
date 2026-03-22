using System;
using System.Windows;
using System.Windows.Threading;
using HitePhoto.PrintStation.Core;

namespace HitePhoto.PrintStation.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Enable logging immediately to capture crash data
        AppLog.Enabled = true;
        AppLog.Info("PrintStation starting");

        // Wire unhandled exception handlers
        DispatcherUnhandledException += (_, args) =>
        {
            AlertCollector.Error(AlertCategory.General,
                "Unhandled UI thread exception",
                detail: "An unhandled exception occurred on the WPF dispatcher thread. " +
                        "The application may be in an unstable state.",
                ex: args.Exception);
            args.Handled = false; // let it crash visibly
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AlertCollector.Error(AlertCategory.General,
                    "Unhandled background thread exception",
                    detail: $"IsTerminating: {args.IsTerminating}. " +
                            "An unhandled exception occurred on a background thread.",
                    ex: ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AlertCollector.Error(AlertCategory.General,
                "Unobserved task exception",
                detail: "A fire-and-forget task threw an exception that was never awaited.",
                ex: args.Exception);
            args.SetObserved(); // prevent crash from unobserved tasks
        };

        base.OnStartup(e);
    }
}
