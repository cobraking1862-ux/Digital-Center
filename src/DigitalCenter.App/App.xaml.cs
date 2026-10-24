using System.IO;
using System.Windows;
using DigitalCenter.App.Services;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Services;
using DigitalCenter.Core.Tmdb;
using DigitalCenter.ViewModels;
using DigitalCenter.ViewModels.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalCenter.App;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;

    private WatchFolderService? _watchFolder;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{args.Exception.Message}",
                "DigitalCenter",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            Services = BuildServices();

            var paths = Services.GetRequiredService<AppPaths>();
            paths.EnsureDataDirectories();

            var logger = Services.GetRequiredService<IAppLogger>();

            await Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();

            var mainViewModel = Services.GetRequiredService<MainViewModel>();

            // Details screens are created per title, so the shell receives a
            // factory from the container. Without this assignment the shell
            // silently ignores card activation (that is why double-clicking a
            // poster used to do nothing at all).
            mainViewModel.DetailsFactory = Services.GetRequiredService<Func<int, DetailsViewModel>>();

            var mainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };

            MainWindow = mainWindow;

            Services.GetRequiredService<ActivityFeed>().Publish("DigitalCenter started.", ActivityKind.System);

            // Rehydrate the first screens BEFORE showing the view and BEFORE any
            // maintenance work. Showing first raised LibraryPage.Loaded, which
            // started another refresh while this one was still running; the
            // coalesced call then returned immediately and startup continued as
            // though Home had loaded. InitializeAsync now owns the one startup
            // query and its task represents the completed homepage load.
            await mainViewModel.InitializeAsync();

            mainWindow.Show();

            // Complete interrupted imports in the background, then start
            // Watch Folder monitoring. Both are non-blocking for the UI.
            _ = RunStartupMaintenanceAsync(logger);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "DigitalCenter could not start because the local database could not be prepared.\n\n" +
                $"Details: {ex.Message}",
                "DigitalCenter",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Background startup work. Every step has its own guard: a failed recovery
    /// or an unreachable TMDb must not stop the steps that follow (and must
    /// never stop the screens from loading — that already happened).
    /// </summary>
    private async Task RunStartupMaintenanceAsync(IAppLogger logger)
    {
        var mainViewModel = Services.GetRequiredService<MainViewModel>();
        var importService = Services.GetRequiredService<ImportService>();

        // 1. Finish imports that were interrupted by a crash or a shutdown.
        try
        {
            var report = await importService.RecoverInterruptedAsync();
            foreach (var line in report)
            {
                logger.Info($"Recovery: {line}");
            }

            if (report.Count > 0)
            {
                Services.GetRequiredService<ActivityFeed>().Publish(
                    $"Recovered {report.Count} interrupted import(s); refreshing the library.",
                    ActivityKind.Background);

                // Recovered rows can change the catalog: refresh what is on screen.
                await mainViewModel.Library.RefreshSafeAsync();
            }
        }
        catch (Exception ex)
        {
            logger.Error("Recovering interrupted imports failed.", ex);
        }

        // 2. Retry automatic matching for files detected while TMDb was offline,
        //    then file the matched ones into the library (under their language).
        try
        {
            await importService.MatchPendingAsync();
            var organized = await importService.AutoOrganizeAsync();
            if (organized.Moved > 0)
            {
                // Files moved while the UI was starting: show the new library.
                await mainViewModel.Library.RefreshSafeAsync();
            }
        }
        catch (Exception ex)
        {
            logger.Error("Retrying automatic metadata matching failed; the files stay pending.", ex);
        }

        // 3. Start the single Watch Folder monitor — the only startup path for it.
        try
        {
            var settings = Services.GetRequiredService<SettingsService>().Load();
            _watchFolder = Services.GetRequiredService<WatchFolderService>();
            _watchFolder.StabilityDelay = TimeSpan.FromSeconds(Math.Clamp(settings.StabilityDelaySeconds, 2, 600));
            var feed = Services.GetRequiredService<ActivityFeed>();
            if (settings.WatchFolderEnabled && !string.IsNullOrWhiteSpace(settings.WatchFolderPath) && !_watchFolder.IsRunning)
            {
                _watchFolder.Start(settings.WatchFolderPath);
                feed.Publish($"Watching '{settings.WatchFolderPath}' for new files.", ActivityKind.Background);
            }
            else
            {
                logger.Info("Watch Folder monitoring not started (disabled or not configured in Management).");
                feed.Publish("Watch Folder monitoring is off (start it in Management).", ActivityKind.System);
            }
        }
        catch (Exception ex)
        {
            logger.Error("Starting Watch Folder monitoring failed.", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watchFolder?.Stop();
        base.OnExit(e);
    }

    /// <summary>Closes the in-application details screen (Back button).</summary>
    public void CloseDetails()
    {
        (Services.GetService<MainViewModel>())?.CloseDetails();
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        var paths = AppPaths.CreateDefault();
        services.AddSingleton(paths);
        services.AddSingleton<IAppLogger>(new FileLogger(paths.LogsDirectory));
        services.AddSingleton<SettingsService>();

        // TMDb token: appsettings.json next to the exe → environment
        // variables (Tmdb__ReadAccessToken) → user secrets (development).
        // The token never reaches the UI or the log files.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddUserSecrets(typeof(App).Assembly, optional: true)
            .Build();

        var tmdbOptions = new TmdbOptions
        {
            ReadAccessToken = configuration[TmdbOptions.TokenConfigKey]
        };
        services.AddSingleton(tmdbOptions);
        services.AddSingleton<ITmdbMetadataProvider>(sp =>
            new TmdbMetadataProvider(
                sp.GetRequiredService<TmdbOptions>(),
                sp.GetRequiredService<IAppLogger>()));

        services.AddDbContextFactory<DigitalCenterDbContext>(options =>
            options.UseSqlite($"Data Source={paths.DatabaseFile}"));

        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<FilenameParser>();
        services.AddSingleton<LibraryScanner>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<PosterCache>();
        services.AddSingleton<IDriveProbe, DriveProbe>();
        services.AddSingleton<ImportService>();
        services.AddSingleton<WatchFolderService>();
        services.AddSingleton<UsbCopyService>();
        services.AddSingleton<MaintenanceService>();

        // Resolved lazily on first use, after DatabaseInitializer has decided
        // whether FTS5 is available in this SQLite build.
        services.AddSingleton(sp => new LibraryQueryService(
            sp.GetRequiredService<IDbContextFactory<DigitalCenterDbContext>>(),
            sp.GetRequiredService<DatabaseInitializer>().FullTextSearchEnabled));

        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();

        // The single consolidated activity feed every screen publishes to; the
        // shell shows it in the sidebar ("Current Activity & Status").
        services.AddSingleton<ActivityFeed>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IAppControl, AppControl>();

        // One details view model per opened title: transient, built through a
        // factory that supplies the title id (the rest comes from the container).
        services.AddTransient<Func<int, DetailsViewModel>>(sp =>
            titleId => ActivatorUtilities.CreateInstance<DetailsViewModel>(sp, titleId));

        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<UsbCartViewModel>();
        services.AddSingleton<ManagementViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MaintenanceViewModel>();
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
