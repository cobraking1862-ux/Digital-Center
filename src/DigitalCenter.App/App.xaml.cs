using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
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
    private const string SingleInstanceMutexName = @"Local\DigitalCenter.App.SingleInstance";
    private const string ActivateEventName = @"Local\DigitalCenter.App.ActivateEvent";
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public IServiceProvider Services { get; private set; } = null!;

    private WatchFolderService? _watchFolder;
    private DramaWatchFolderService? _dramaWatchFolder;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWaitHandle;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                using var existingEvent = EventWaitHandle.OpenExisting(ActivateEventName);
                existingEvent.Set();
            }
            catch (Exception) { }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown(0);
            return;
        }

        RegisterActivationListener();

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
            var mainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };

            MainWindow = mainWindow;
            mainWindow.Show();

            _ = StartupSequenceAsync(mainViewModel, logger);
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

    private void RegisterActivationListener()
    {
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activateWaitHandle = ThreadPool.RegisterWaitForSingleObject(
                _activateEvent,
                (_, _) => Dispatcher.Invoke(BringMainWindowToFront),
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception) { }
    }

    private void BringMainWindowToFront()
    {
        var window = MainWindow;
        if (window is null) return;

        if (!window.IsVisible) window.Show();

        var hwnd = new WindowInteropHelper(window).Handle;
        if (window.WindowState == WindowState.Minimized)
        {
            if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SwRestore);
            else window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();

        if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
    }

    private async Task StartupSequenceAsync(MainViewModel mainViewModel, IAppLogger logger)
    {
        try { await mainViewModel.Library.RefreshSafeAsync(); }
        catch (Exception ex) { logger.Warn($"Initial library catalog load warning: {ex.Message}"); }

        try { await mainViewModel.Drama.RefreshSafeAsync(); }
        catch (Exception ex) { logger.Warn($"Initial drama catalog load warning: {ex.Message}"); }

        try { await mainViewModel.Tickets.RefreshAsync(); }
        catch (Exception ex) { logger.Warn($"Initial tickets load warning: {ex.Message}"); }

        try { await mainViewModel.UsbCart.RefreshCartAsync(); }
        catch (Exception ex) { logger.Warn($"Initial USB cart load warning: {ex.Message}"); }

        await Task.Run(() => RunStartupMaintenanceAsync(logger));
    }

    private async Task RunStartupMaintenanceAsync(IAppLogger logger)
    {
        try
        {
            var importService = Services.GetRequiredService<ImportService>();
            _watchFolder = Services.GetRequiredService<WatchFolderService>();
            _dramaWatchFolder = Services.GetRequiredService<DramaWatchFolderService>();

            var report = await importService.RecoverInterruptedAsync();
            foreach (var line in report)
            {
                logger.Info($"Recovery: {line}");
            }

            if (report.Count > 0)
            {
                _watchFolder.NotifyPendingImportsChanged();
            }

            var settings = Services.GetRequiredService<SettingsService>().Load();
            var stability = TimeSpan.FromSeconds(Math.Clamp(settings.StabilityDelaySeconds, 2, 600));
            _watchFolder.StabilityDelay = stability;
            _dramaWatchFolder.StabilityDelay = stability;

            var watchPathIssues = SettingsService.ValidateWatchFolderPath(settings);
            var canMonitor = settings.WatchFolderEnabled &&
                             !string.IsNullOrWhiteSpace(settings.WatchFolderPath) &&
                             watchPathIssues.Count == 0;

            if (canMonitor)
            {
                if (!_watchFolder.IsRunning)
                {
                    _watchFolder.Start(settings.WatchFolderPath!);
                }
            }
            else
            {
                if (_watchFolder.IsRunning) _watchFolder.Stop();
                var matched = await importService.MatchPendingAsync();
                if (matched > 0) _watchFolder.NotifyPendingImportsChanged();
            }

            if (settings.DramaWatchFolderEnabled &&
                !string.IsNullOrWhiteSpace(settings.DramaWatchFolderPath) &&
                Directory.Exists(settings.DramaWatchFolderPath) &&
                !string.IsNullOrWhiteSpace(settings.DramaRoot))
            {
                _dramaWatchFolder.Start(settings.DramaWatchFolderPath);
            }
        }
        catch (Exception ex)
        {
            logger.Error("Startup maintenance (import recovery / watch folders) failed.", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watchFolder?.Stop();
        _dramaWatchFolder?.Stop();

        try
        {
            _activateWaitHandle?.Unregister(null);
            _activateEvent?.Dispose();
            if (_singleInstanceMutex is not null)
            {
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
            }
        }
        catch (Exception) { }

        try
        {
            if (Services is IDisposable disposableServices)
            {
                disposableServices.Dispose();
            }
        }
        catch (Exception)
        {
            // Browser and service shutdown must not prevent the application from exiting.
        }

        base.OnExit(e);
    }

    public void CloseDetails() => (Services.GetService<MainViewModel>())?.CloseDetails();

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        var paths = AppPaths.CreateDefault();
        services.AddSingleton(paths);
        services.AddSingleton<IAppLogger>(new FileLogger(paths.LogsDirectory));
        services.AddSingleton<SettingsService>();

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
            options.UseSqlite($"Data Source={paths.DatabaseFile};Default Timeout=30;Pooling=True"));

        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<FilenameParser>();
        services.AddSingleton<LibraryScanner>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<PosterCache>();
        services.AddSingleton<IPortableDeviceTransfer, WindowsMtpTransfer>();
        services.AddSingleton<IDriveProbe, DriveProbe>();
        services.AddSingleton<WatchFolderIssueService>();
        services.AddSingleton<ImportService>();
        services.AddSingleton<WatchFolderService>();
        services.AddSingleton<UsbCopyService>();
        services.AddSingleton<DramaLibraryService>();
        services.AddSingleton<DramaWatchFolderService>();
        services.AddSingleton<DramaCopyService>();
        services.AddSingleton<ImdbLookupService>();
        services.AddSingleton<TicketService>();
        services.AddSingleton<MaintenanceService>();

        services.AddSingleton(sp => new LibraryQueryService(
            sp.GetRequiredService<IDbContextFactory<DigitalCenterDbContext>>(),
            sp.GetRequiredService<DatabaseInitializer>().FullTextSearchEnabled));

        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<ActivityFeedService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IAppControl, AppControl>();

        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<DramaViewModel>();
        services.AddSingleton<LookupViewModel>();
        services.AddSingleton<TicketsViewModel>();
        services.AddSingleton<UsbCartViewModel>();
        services.AddSingleton<ManagementViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MaintenanceViewModel>();

        services.AddSingleton<MainViewModel>(sp =>
        {
            var mainViewModel = ActivatorUtilities.CreateInstance<MainViewModel>(sp);
            mainViewModel.DetailsFactory = titleId =>
                ActivatorUtilities.CreateInstance<DetailsViewModel>(sp, titleId);
            mainViewModel.DramaDetailsFactory = dramaId =>
                ActivatorUtilities.CreateInstance<DramaDetailsViewModel>(sp, dramaId);
            return mainViewModel;
        });

        return services.BuildServiceProvider();
    }
}
