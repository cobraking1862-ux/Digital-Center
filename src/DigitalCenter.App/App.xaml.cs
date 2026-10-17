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

    private async Task RunStartupMaintenanceAsync(IAppLogger logger)
    {
        try
        {
            var importService = Services.GetRequiredService<ImportService>();
            var report = await importService.RecoverInterruptedAsync();
            foreach (var line in report)
            {
                logger.Info($"Recovery: {line}");
            }

            // Retry automatic matching for files detected while TMDb was offline.
            await importService.MatchPendingAsync();

            var settings = Services.GetRequiredService<SettingsService>().Load();
            _watchFolder = Services.GetRequiredService<WatchFolderService>();
            _watchFolder.StabilityDelay = TimeSpan.FromSeconds(Math.Clamp(settings.StabilityDelaySeconds, 2, 600));
            if (settings.WatchFolderEnabled && !string.IsNullOrWhiteSpace(settings.WatchFolderPath) && !_watchFolder.IsRunning)
            {
                _watchFolder.Start(settings.WatchFolderPath);
            }
            else
            {
                logger.Info("Watch Folder monitoring not started (disabled or not configured in Management).");
            }

            // Initial screen loads (library grid + the cart badge in the
            // sidebar). Monitoring is started above and only above, so the
            // Watch Folder has exactly one startup path.
            await Services.GetRequiredService<MainViewModel>().InitializeAsync();
        }
        catch (Exception ex)
        {
            logger.Error("Startup maintenance (import recovery / watch folder) failed.", ex);
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
