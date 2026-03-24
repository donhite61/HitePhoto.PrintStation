using System;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Core.Decisions;
using HitePhoto.PrintStation.Core.Ingest;
using HitePhoto.PrintStation.Core.Services;
using HitePhoto.PrintStation.Data;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.UI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppLog.Enabled = true;
        AppLog.Info("PrintStation starting");

        // Exception handlers
        DispatcherUnhandledException += (_, args) =>
        {
            AlertCollector.Error(AlertCategory.General,
                "Unhandled UI thread exception", ex: args.Exception);
            args.Handled = false;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AlertCollector.Error(AlertCategory.General,
                    "Unhandled background thread exception", ex: ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AlertCollector.Error(AlertCategory.General,
                "Unobserved task exception", ex: args.Exception);
            args.SetObserved();
        };

        // Load settings
        var settingsManager = new SettingsManager();
        var settings = settingsManager.Load();

        // Build DI container
        var services = new ServiceCollection();

        // Settings (singleton — loaded once at startup)
        services.AddSingleton(settings);
        services.AddSingleton(settingsManager);

        // Data layer
        services.AddSingleton<OrderDb>();
        services.AddSingleton<IOrderRepository, OrderRepository>();
        services.AddSingleton<IHistoryRepository, HistoryRepository>();

        // Decision makers
        services.AddSingleton<IHoldDecision, HoldDecision>();
        services.AddSingleton<IFilesNeededDecision, FilesNeededDecision>();
        services.AddSingleton<IChannelDecision, ChannelDecision>();

        // Services
        services.AddSingleton<IHoldService, HoldService>();
        services.AddSingleton<INotificationService>(sp => new NotificationService(
            sp.GetRequiredService<IOrderRepository>(),
            sp.GetRequiredService<IHistoryRepository>(),
            sp.GetRequiredService<IEmailSender>(),
            sp.GetRequiredService<IPixfizzNotifier>(),
            sp.GetRequiredService<AppSettings>()));

        // Ingest — Pixfizz
        services.AddSingleton<OhdApiSource>();
        services.AddSingleton<PixfizzFtpDownloader>();
        services.AddSingleton<PixfizzArtworkDownloader>();
        services.AddSingleton<PixfizzOrderParser>();
        services.AddSingleton<OhdReceivedPusher>();
        services.AddSingleton<PixfizzIngestService>();

        // Ingest — Dakis
        services.AddSingleton<DakisOrderParser>();
        services.AddSingleton<DakisIngestService>();

        // Processing (ported from PrintRouter — stateless, reuse instances)
        services.AddSingleton<Core.Processing.NoritsuMrkWriter>();
        // ImageProcessor is static — no DI registration needed

        // TODO: wire these when implementations are ready
        // services.AddSingleton<IEmailSender, EmailSenderAdapter>();
        // services.AddSingleton<IPixfizzNotifier, PixfizzNotifierAdapter>();
        // services.AddSingleton<IPrinterWriter, NoritsuMrkWriterAdapter>();
        // services.AddSingleton<IPrintService, PrintService>();
        // services.AddSingleton<ITransferService, TransferService>();

        // ViewModel
        services.AddSingleton<ViewModels.MainViewModel>();

        // Window
        services.AddTransient<MainWindow>();

        Services = services.BuildServiceProvider();

        // Show main window
        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }
}
