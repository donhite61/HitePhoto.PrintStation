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
using HitePhoto.PrintStation.Data.Sync;

namespace HitePhoto.PrintStation.UI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Parse --profile from command line
        string? profile = null;
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--profile" && i + 1 < args.Length)
                profile = args[++i];
        }

        // Load settings (profile-aware)
        var settingsManager = new SettingsManager(profile);
        var settings = settingsManager.Load();

        // Init logging (must happen before anything else logs)
        AppLog.Init(settings.LogDirectory);
        AppLog.InitNas(settings.NasLogFolder);
        AppLog.Enabled = settings.EnableLogging;
        AppLog.Info($"PrintStation starting{(profile != null ? $" [profile: {profile}]" : "")}");

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

        // Build DI container
        var services = new ServiceCollection();

        // Settings (singleton — loaded once at startup)
        services.AddSingleton(settings);
        services.AddSingleton(settingsManager);

        // Data layer — use explicit SQLite path if configured
        if (!string.IsNullOrEmpty(settings.SqlitePath))
            services.AddSingleton(new OrderDb(settings.SqlitePath));
        else
            services.AddSingleton<OrderDb>();

        // MariaDB sync layer
        services.AddSingleton(sp => new PrintStationDb(settings.ConnectionString));
        services.AddSingleton<OutboxRepository>();
        services.AddSingleton<ISyncService, SyncService>();

        // Repositories — wrap with sync decorators when sync is enabled
        services.AddSingleton<OrderRepository>();
        services.AddSingleton<HistoryRepository>();
        if (settings.SyncEnabled)
        {
            services.AddSingleton<IOrderRepository>(sp => new SyncingOrderRepository(
                sp.GetRequiredService<OrderRepository>(),
                sp.GetRequiredService<ISyncService>()));
            services.AddSingleton<IHistoryRepository>(sp => new SyncingHistoryRepository(
                sp.GetRequiredService<HistoryRepository>(),
                sp.GetRequiredService<ISyncService>()));
        }
        else
        {
            services.AddSingleton<IOrderRepository>(sp => sp.GetRequiredService<OrderRepository>());
            services.AddSingleton<IHistoryRepository>(sp => sp.GetRequiredService<HistoryRepository>());
        }

        services.AddSingleton<IAlertRepository, AlertRepository>();
        services.AddSingleton<IOptionDefaultsRepository, OptionDefaultsRepository>();

        // Decision makers
        services.AddSingleton<IHoldDecision, HoldDecision>();
        services.AddSingleton<IFilesNeededDecision, FilesNeededDecision>();
        services.AddSingleton<IChannelDecision, ChannelDecision>();

        // Services
        services.AddSingleton<IOrderVerifier, OrderVerifier>();
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

        // Ingest — shared
        services.AddSingleton<IngestOrderWriter>();

        // Ingest — Dakis
        services.AddSingleton<DakisOrderParser>();
        services.AddSingleton<DakisIngestService>();

        // Color correction
        services.AddSingleton<CorrectionStore>();

        // Processing (ported from PrintRouter — stateless, reuse instances)
        services.AddSingleton(new Core.Processing.NoritsuMrkWriter(settings.NoritsuOutputRoot));
        services.AddSingleton<IPrinterWriter>(sp =>
            new Core.Processing.NoritsuMrkWriterAdapter(
                sp.GetRequiredService<Core.Processing.NoritsuMrkWriter>()));
        services.AddSingleton<IPrintService>(sp =>
            new PrintService(
                sp.GetRequiredService<IOrderRepository>(),
                sp.GetRequiredService<IHistoryRepository>(),
                sp.GetRequiredService<IChannelDecision>(),
                sp.GetRequiredService<IPrinterWriter>(),
                settings.NoritsuOutputRoot));

        // TODO: wire real implementations when ready
        services.AddSingleton<IEmailSender>(sp =>
            new Core.Processing.EmailService(sp.GetRequiredService<AppSettings>()));
        services.AddSingleton<IPixfizzNotifier, StubPixfizzNotifier>();
        // services.AddSingleton<ITransferService, TransferService>();

        // ViewModel
        services.AddSingleton<ViewModels.MainViewModel>();

        // Window
        services.AddTransient<MainWindow>();

        Services = services.BuildServiceProvider();

        // Wire alert sinks and purge old alerts
        var alertRepo = Services.GetRequiredService<IAlertRepository>();
        AlertCollector.AddSink(new SqliteAlertSink(alertRepo));
        try { alertRepo.PurgeOlderThan(30); }
        catch (Exception ex) { AppLog.Error($"Failed to purge old alerts: {ex.Message}"); }

        var mariaDb = Services.GetRequiredService<PrintStationDb>();
        AlertCollector.AddSink(new MariaDbAlertSink(mariaDb, settings.StoreId));
        _ = mariaDb.EnsureAlertsTableAsync();

        // Show main window
        try
        {
            var mainWindow = Services.GetRequiredService<MainWindow>();
            if (profile != null)
                mainWindow.Title += $" [{profile}]";
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
