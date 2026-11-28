using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels.Services;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.ViewModels;

/// <summary>Selectable download workflow option for a Lookup site row.</summary>
public sealed record WorkflowOptionItem(string ProviderId, string DisplayName);

/// <summary>Display wrapper around a backup file for the maintenance list.</summary>
public sealed class BackupItemViewModel
{
    public BackupItemViewModel(BackupFileInfo source)
    {
        Source = source;
        CreatedAtText = source.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        SizeText = UiFormat.FormatBytes(source.Size);
        FileNameText = Path.GetFileName(source.FilePath);
        FilePathText = source.FilePath;
    }

    public BackupFileInfo Source { get; }
    public string CreatedAtText { get; }
    public string SizeText { get; }
    public string FileNameText { get; }
    public string FilePathText { get; }
}

/// <summary>Display row for one missing movie file report entry.</summary>
public sealed class MissingFileRow
{
    public MissingFileRow(MissingFileInfo info)
    {
        TitleDisplay = info.TitleDisplay;
        FilePath = info.FilePath;
        SizeText = UiFormat.FormatBytes(info.FileSize);
        AddedText = info.AddedAt.ToLocalTime().ToString("yyyy-MM-dd");
    }

    public string TitleDisplay { get; }
    public string FilePath { get; }
    public string SizeText { get; }
    public string AddedText { get; }
}

/// <summary>Display row for one missing drama episode file report entry.</summary>
public sealed class MissingDramaEpisodeRow
{
    public MissingDramaEpisodeRow(DramaEpisode ep)
    {
        TitleDisplay = ep.EpisodeNumber > 0
            ? $"{ep.DramaTitle} — S{ep.SeasonNumber:00}E{ep.EpisodeNumber:00}"
            : $"{ep.DramaTitle} — {ep.Title}";
        FilePath = ep.FilePath;
        SizeText = UiFormat.FormatBytes(ep.FileSize);
    }

    public string TitleDisplay { get; }
    public string FilePath { get; }
    public string SizeText { get; }
}

/// <summary>Editable row for one configured Lookup site managed in Maintenance.</summary>
public sealed partial class LookupSiteRowViewModel : ObservableObject
{
    private readonly Action _onToggled;
    private readonly Action<string, string?> _onWorkflowChanged;

    public LookupSiteRowViewModel(
        LookupSiteEntry entry,
        string? boundProviderId,
        IReadOnlyList<WorkflowOptionItem> availableWorkflows,
        Action onToggled,
        Action<string, string?> onWorkflowChanged)
    {
        Entry = entry;
        AvailableWorkflows = availableWorkflows
            .Where(option => option.ProviderId != DownloadWorkflowIds.BigOneBrowser || IsBollyFlixEntry(entry))
            .ToList();
        _onToggled = onToggled;
        _onWorkflowChanged = onWorkflowChanged;
        IsEnabled = entry.IsEnabled;
        SelectedProviderId = string.IsNullOrWhiteSpace(boundProviderId)
            ? DownloadWorkflowIds.None
            : boundProviderId.Trim();
    }

    public LookupSiteEntry Entry { get; }
    public IReadOnlyList<WorkflowOptionItem> AvailableWorkflows { get; }
    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string UrlTemplate => Entry.UrlTemplate;
    public string NoResultsMarker => Entry.NoResultsMarker ?? string.Empty;
    public bool HasNoResultsMarker => !string.IsNullOrWhiteSpace(Entry.NoResultsMarker);
    public bool IsBuiltIn => Entry.IsBuiltIn;

    private static bool IsBollyFlixEntry(LookupSiteEntry entry) =>
        string.Equals(entry.Id, "builtin-bollyflix", StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(entry.UrlTemplate) &&
         entry.UrlTemplate.Contains("new1.bollyflix.tours", StringComparison.OrdinalIgnoreCase));

    [ObservableProperty] public partial bool IsEnabled { get; set; }
    [ObservableProperty] public partial string SelectedProviderId { get; set; }

    partial void OnIsEnabledChanged(bool value)
    {
        if (Entry.IsEnabled != value)
        {
            Entry.IsEnabled = value;
            _onToggled();
        }
    }

    partial void OnSelectedProviderIdChanged(string value)
    {
        _onWorkflowChanged(Entry.Id, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }
}

/// <summary>Unified Watch Folder issue row (Movie or Drama) displayed in Maintenance.</summary>
public sealed class WatchFolderIssueRowViewModel
{
    public WatchFolderIssueRowViewModel(WatchFolderIssue model)
    {
        Model = model;
        TimestampText = model.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        IsMovie = string.Equals(model.MediaType, WatchFolderIssueTypes.Movie, StringComparison.OrdinalIgnoreCase);
        IsDrama = !IsMovie;
        IsFailed = string.Equals(model.Category, WatchFolderIssueCategories.Failed, StringComparison.OrdinalIgnoreCase);
        IsInterrupted = string.Equals(model.Category, WatchFolderIssueCategories.Interrupted, StringComparison.OrdinalIgnoreCase);
        IsLowConfidence = string.Equals(model.Category, WatchFolderIssueCategories.LowConfidence, StringComparison.OrdinalIgnoreCase);
    }

    public WatchFolderIssue Model { get; }
    public string MediaType => Model.MediaType;
    public string Category => Model.Category;
    public string FileName => Model.FileName;
    public string SourcePath => Model.SourcePath;
    public string Reason => Model.Reason;
    public string TimestampText { get; }
    public bool IsMovie { get; }
    public bool IsDrama { get; }
    public bool IsFailed { get; }
    public bool IsInterrupted { get; }
    public bool IsLowConfidence { get; }
}

/// <summary>
/// Professional Maintenance &amp; Diagnostics workspace:
/// Tab 0: Library &amp; Diagnostics (Movies Management + Drama Management with unambiguous action rows)
/// Tab 1: Watch Folder Issues (Unified failed/interrupted/low-confidence queue — targeted by Watch Folder badge)
/// Tab 2: Database &amp; System (Live SQLite health KPIs, Integrity Check, Optimize/VACUUM/ANALYZE, Poster Cache &amp; Error Log Purge)
/// Tab 3: Lookup Sites &amp; Downloads (Download Manager / IDM defaults + Lookup sites with per-site workflow binding)
/// Tab 4: Data &amp; Backups (SQLite Backup/Restore + CSV Import/Export)
/// </summary>
public partial class MaintenanceViewModel : ObservableObject
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settingsService;
    private readonly DownloadSettingsService _downloadSettings;
    private readonly LibraryScanner _scanner;
    private readonly DramaLibraryService _dramaLibrary;
    private readonly TicketService _ticketService;
    private readonly BackupService _backupService;
    private readonly MaintenanceService _maintenance;
    private readonly DatabaseMaintenanceService _dbMaintenance;
    private readonly WatchFolderIssueService _issuesService;
    private readonly WatchFolderService _watchFolder;
    private readonly DramaWatchFolderService _dramaWatchFolder;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly IAppControl _appControl;

    /// <summary>Raised when a scan, re-analysis, prune, index rebuild, or CSV import modifies the catalog.</summary>
    public event Action? CatalogChanged;

    /// <summary>Raised when the configured Lookup sites or workflow bindings are modified.</summary>
    public event Action? LookupSitesChanged;

    /// <summary>Raised when Watch Folder issues are retried, dismissed, or cleared.</summary>
    public event Action? WatchFolderIssuesChanged;

    public MaintenanceViewModel(
        AppPaths paths,
        SettingsService settingsService,
        LibraryScanner scanner,
        DramaLibraryService dramaLibrary,
        TicketService ticketService,
        BackupService backupService,
        MaintenanceService maintenance,
        WatchFolderIssueService issuesService,
        WatchFolderService watchFolder,
        DramaWatchFolderService dramaWatchFolder,
        IDbContextFactory<DigitalCenterDbContext> dbFactory,
        IDialogService dialogs,
        IAppLogger logger,
        IAppControl appControl)
    {
        _paths = paths;
        _settingsService = settingsService;
        _downloadSettings = new DownloadSettingsService(paths);
        _scanner = scanner;
        _dramaLibrary = dramaLibrary;
        _ticketService = ticketService;
        _backupService = backupService;
        _maintenance = maintenance;
        _dbMaintenance = new DatabaseMaintenanceService(dbFactory, paths, logger);
        _issuesService = issuesService;
        _watchFolder = watchFolder;
        _dramaWatchFolder = dramaWatchFolder;
        _dialogs = dialogs;
        _logger = logger;
        _appControl = appControl;

        QualityOptions = DownloadQualityTiers.All;
        AvailableWorkflows =
        [
            new WorkflowOptionItem(DownloadWorkflowIds.None, "None (Lookup-only)"),
            new WorkflowOptionItem(DownloadWorkflowIds.DirectLink, "Direct Link Workflow"),
            new WorkflowOptionItem(DownloadWorkflowIds.MultiStepLink, "Multi-Step Host Workflow"),
            new WorkflowOptionItem(DownloadWorkflowIds.BigOneBrowser, "BigOne — Visible Browser (BollyFlix)")
        ];

        ProgressText = string.Empty;
        ScanSummary = string.Empty;
        ReportSummary = string.Empty;
        DramaScanSummary = string.Empty;
        DramaReportSummary = string.Empty;
        DatabaseDiagnosticsSummary = string.Empty;
        CsvStatusText = string.Empty;
        NewLookupSiteName = string.Empty;
        NewLookupSiteUrlTemplate = string.Empty;
        NewLookupNoResultsMarker = string.Empty;
        LookupSitesStatusText = string.Empty;
        WatchFolderIssuesStatusText = string.Empty;
        IdmExecutablePath = string.Empty;
        PreferredDownloadQuality = DownloadQualityTiers.Q1080p;
        DownloadSettingsStatusText = string.Empty;

        DbSizeText = "—";
        DbJournalDetailText = "SQLite";
        CatalogRecordsText = "—";
        CatalogRecordsDetailText = "Movies & Dramas";
        ErrorsAndLogsText = "—";
        ErrorsAndLogsDetailText = "Issues & log files";
        ReclaimableSpaceText = "—";
        ReclaimableSpaceDetailText = "Free pages & cache";

        ReloadDownloadSettings();
        ReloadBackups();
        ReloadLookupSites();
    }

    public IReadOnlyList<string> QualityOptions { get; }
    public IReadOnlyList<WorkflowOptionItem> AvailableWorkflows { get; }

    public ObservableCollection<BackupItemViewModel> Backups { get; } = [];
    public ObservableCollection<MissingFileRow> MissingFiles { get; } = [];
    public ObservableCollection<MissingDramaEpisodeRow> MissingDramaEpisodes { get; } = [];
    public ObservableCollection<LookupSiteRowViewModel> LookupSites { get; } = [];
    public ObservableCollection<WatchFolderIssueRowViewModel> WatchFolderIssues { get; } = [];

    [ObservableProperty] public partial int SelectedTabIndex { get; set; }
    [ObservableProperty] public partial string ProgressText { get; set; }
    [ObservableProperty] public partial string ScanSummary { get; set; }
    [ObservableProperty] public partial string ReportSummary { get; set; }
    [ObservableProperty] public partial string DramaScanSummary { get; set; }
    [ObservableProperty] public partial string DramaReportSummary { get; set; }
    [ObservableProperty] public partial string DatabaseDiagnosticsSummary { get; set; }
    [ObservableProperty] public partial string CsvStatusText { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial BackupItemViewModel? SelectedBackup { get; set; }

    // Download Manager & Default Quality (v14.0.0)
    [ObservableProperty] public partial string IdmExecutablePath { get; set; }
    [ObservableProperty] public partial string PreferredDownloadQuality { get; set; }
    [ObservableProperty] public partial bool RouteIdmToMoviesWatchFolder { get; set; }
    [ObservableProperty] public partial bool AutoCompleteTicketsOnImport { get; set; }
    [ObservableProperty] public partial string DownloadSettingsStatusText { get; set; }

    // Database Health KPI cards
    [ObservableProperty] public partial string DbSizeText { get; set; }
    [ObservableProperty] public partial string DbJournalDetailText { get; set; }
    [ObservableProperty] public partial string CatalogRecordsText { get; set; }
    [ObservableProperty] public partial string CatalogRecordsDetailText { get; set; }
    [ObservableProperty] public partial string ErrorsAndLogsText { get; set; }
    [ObservableProperty] public partial string ErrorsAndLogsDetailText { get; set; }
    [ObservableProperty] public partial string ReclaimableSpaceText { get; set; }
    [ObservableProperty] public partial string ReclaimableSpaceDetailText { get; set; }

    // Lookup Sites configuration
    [ObservableProperty] public partial string NewLookupSiteName { get; set; }
    [ObservableProperty] public partial string NewLookupSiteUrlTemplate { get; set; }
    [ObservableProperty] public partial string NewLookupNoResultsMarker { get; set; }
    [ObservableProperty] public partial string LookupSitesStatusText { get; set; }

    // Watch Folder Issues status
    [ObservableProperty] public partial string WatchFolderIssuesStatusText { get; set; }

    public bool HasProgressText => !string.IsNullOrWhiteSpace(ProgressText);
    public bool HasScanSummary => !string.IsNullOrEmpty(ScanSummary);
    public bool HasReportSummary => !string.IsNullOrEmpty(ReportSummary);
    public bool HasAnyMovieOutput => HasScanSummary || HasReportSummary;
    public bool HasMissingFiles => MissingFiles.Count > 0;
    public bool HasDramaScanSummary => !string.IsNullOrEmpty(DramaScanSummary);
    public bool HasDramaReportSummary => !string.IsNullOrEmpty(DramaReportSummary);
    public bool HasAnyDramaOutput => HasDramaScanSummary || HasDramaReportSummary;
    public bool HasMissingDramaEpisodes => MissingDramaEpisodes.Count > 0;
    public bool HasDatabaseDiagnosticsSummary => !string.IsNullOrWhiteSpace(DatabaseDiagnosticsSummary);
    public bool HasCsvStatusText => !string.IsNullOrWhiteSpace(CsvStatusText);
    public bool HasLookupSitesStatus => !string.IsNullOrWhiteSpace(LookupSitesStatusText);
    public bool HasDownloadSettingsStatus => !string.IsNullOrWhiteSpace(DownloadSettingsStatusText);
    public bool HasWatchFolderIssues => WatchFolderIssues.Count > 0;
    public bool HasNoWatchFolderIssues => WatchFolderIssues.Count == 0;
    public bool HasWatchFolderIssuesStatus => !string.IsNullOrWhiteSpace(WatchFolderIssuesStatusText);
    public string WatchFolderIssuesTabHeader => WatchFolderIssues.Count > 0
        ? $"Watch Folder Issues ({WatchFolderIssues.Count})"
        : "Watch Folder Issues";

    partial void OnProgressTextChanged(string value) => OnPropertyChanged(nameof(HasProgressText));
    partial void OnScanSummaryChanged(string value)
    {
        OnPropertyChanged(nameof(HasScanSummary));
        OnPropertyChanged(nameof(HasAnyMovieOutput));
    }

    partial void OnReportSummaryChanged(string value)
    {
        OnPropertyChanged(nameof(HasReportSummary));
        OnPropertyChanged(nameof(HasAnyMovieOutput));
    }

    partial void OnDramaScanSummaryChanged(string value)
    {
        OnPropertyChanged(nameof(HasDramaScanSummary));
        OnPropertyChanged(nameof(HasAnyDramaOutput));
    }

    partial void OnDramaReportSummaryChanged(string value)
    {
        OnPropertyChanged(nameof(HasDramaReportSummary));
        OnPropertyChanged(nameof(HasAnyDramaOutput));
    }

    partial void OnDatabaseDiagnosticsSummaryChanged(string value) => OnPropertyChanged(nameof(HasDatabaseDiagnosticsSummary));
    partial void OnCsvStatusTextChanged(string value) => OnPropertyChanged(nameof(HasCsvStatusText));
    partial void OnLookupSitesStatusTextChanged(string value) => OnPropertyChanged(nameof(HasLookupSitesStatus));
    partial void OnDownloadSettingsStatusTextChanged(string value) => OnPropertyChanged(nameof(HasDownloadSettingsStatus));
    partial void OnWatchFolderIssuesStatusTextChanged(string value) => OnPropertyChanged(nameof(HasWatchFolderIssuesStatus));

    partial void OnIsBusyChanged(bool value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        FindMissingFilesCommand.NotifyCanExecuteChanged();
        RemoveMissingMovieFilesCommand.NotifyCanExecuteChanged();
        RebuildSearchIndexCommand.NotifyCanExecuteChanged();
        ScanDramaLibraryCommand.NotifyCanExecuteChanged();
        FindMissingDramaEpisodesCommand.NotifyCanExecuteChanged();
        CleanMissingDramaEpisodesCommand.NotifyCanExecuteChanged();
        ReanalyzeDramasCommand.NotifyCanExecuteChanged();
        RetryAllIssuesCommand.NotifyCanExecuteChanged();
        RefreshDatabaseHealthCommand.NotifyCanExecuteChanged();
        RunIntegrityCheckCommand.NotifyCanExecuteChanged();
        OptimizeDatabaseCommand.NotifyCanExecuteChanged();
        CleanOrphanedPostersCommand.NotifyCanExecuteChanged();
        PurgeErrorLogsCommand.NotifyCanExecuteChanged();
        BackupCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        ImportCsvCommand.NotifyCanExecuteChanged();
    }

    public void OnNavigatedTo()
    {
        ReloadDownloadSettings();
        ReloadBackups();
        ReloadLookupSites();
        _ = RefreshWatchFolderIssuesAsync();
        _ = RefreshDatabaseHealthAsync();
    }

    private bool CanRunMaintenance() => !IsBusy;

    // --------------------------------------------------------- Movies Management

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task ScanAsync()
    {
        var settings = _settingsService.Load();
        var roots = settings.GetConfiguredLibraryRoots();
        if (roots.Count == 0)
        {
            _dialogs.Error("Scan Movies", "Configure at least one library root (Movies or Collections) in Settings first.");
            return;
        }

        var existingRoots = roots.Where(Directory.Exists).ToList();
        if (existingRoots.Count == 0)
        {
            _dialogs.Error("Scan Movies", "None of the configured Movies/Collections root folders currently exist on disk.");
            return;
        }

        IsBusy = true;
        ScanSummary = string.Empty;
        ProgressText = "Scanning Movies & Collections folders…";
        var progress = new Progress<ScanProgress>(p =>
            ProgressText = $"Scanning Movies… {p.FilesSeen} file(s) seen — {Path.GetFileName(p.CurrentFile)}");

        try
        {
            var totalSeen = 0;
            var totalMovies = 0;
            var totalEpisodes = 0;
            var totalRelocated = 0;
            var totalNewTitles = 0;
            var totalDuplicates = 0;
            var totalUnsupported = 0;
            var totalDuration = TimeSpan.Zero;
            var skipped = new List<ScanSkippedFile>();

            await Task.Run(async () =>
            {
                foreach (var root in existingRoots)
                {
                    var result = await _scanner.ScanAsync(root, progress);
                    totalSeen += result.FilesSeen;
                    totalMovies += result.MovieFilesRegistered;
                    totalEpisodes += result.EpisodeFilesRegistered;
                    totalRelocated += result.FilesRelocated;
                    totalNewTitles += result.NewTitlesCreated;
                    totalDuplicates += result.DuplicatePathsSkipped;
                    totalUnsupported += result.UnsupportedFiles;
                    totalDuration += result.Duration;
                    skipped.AddRange(result.Skipped);
                }
            });

            var autoCompletedTickets = 0;
            if (_downloadSettings.Load().AutoCompleteTicketsOnImport)
            {
                autoCompletedTickets = await Task.Run(() => _ticketService.AutoCompleteImportedMoviesAsync());
            }

            ProgressText = $"Movies scan finished in {totalDuration.TotalSeconds:0.0} s.";

            var summary = new StringBuilder();
            summary.AppendLine($"Files seen: {totalSeen}");
            summary.AppendLine($"Movie files registered: {totalMovies}");
            if (totalEpisodes > 0) summary.AppendLine($"Episode files registered: {totalEpisodes}");
            summary.AppendLine($"Moved files updated: {totalRelocated}");
            summary.AppendLine($"New titles created: {totalNewTitles}");
            summary.AppendLine($"Unchanged paths skipped: {totalDuplicates}");
            summary.AppendLine($"Unsupported files: {totalUnsupported}");
            if (autoCompletedTickets > 0) summary.AppendLine($"Matching open tickets auto-completed: {autoCompletedTickets}");
            if (skipped.Count > 0)
            {
                summary.AppendLine($"Skipped ({skipped.Count}):");
                foreach (var s in skipped.Take(10))
                {
                    summary.AppendLine($"  • {Path.GetFileName(s.Path)} — {s.Reason}");
                }
            }

            ScanSummary = summary.ToString().TrimEnd();
            CatalogChanged?.Invoke();
            await RefreshDatabaseHealthAsync();
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Movies scan cancelled.";
        }
        catch (Exception ex)
        {
            _logger.Error("Movies scan failed.", ex);
            ProgressText = string.Empty;
            _dialogs.Error("Scan Movies", $"The scan failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task FindMissingFilesAsync()
    {
        IsBusy = true;
        ProgressText = "Checking Movies catalog for missing files on disk…";
        try
        {
            var missing = await Task.Run(() => _maintenance.GetMissingFilesAsync());
            MissingFiles.Clear();
            foreach (var row in missing)
            {
                MissingFiles.Add(new MissingFileRow(row));
            }

            OnPropertyChanged(nameof(HasMissingFiles));
            ReportSummary = missing.Count == 0
                ? "All cataloged movie files exist on disk."
                : $"{missing.Count} movie file(s) are missing on disk. Run 'Scan Movies Library' if files were moved, or click 'Prune Missing' to remove deleted entries from the database.";
            ProgressText = "Missing movie files check complete.";
        }
        catch (Exception ex)
        {
            _logger.Error("Missing-file report failed.", ex);
            ProgressText = string.Empty;
            _dialogs.Error("Movies Diagnostics", $"The missing-file check failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task RemoveMissingMovieFilesAsync()
    {
        if (MissingFiles.Count == 0)
        {
            ReportSummary = "No missing movie entries to prune.";
            return;
        }

        if (!_dialogs.Confirm(
                "Prune Missing Movies",
                $"Remove {MissingFiles.Count} missing movie file entry(ies) from the database (and any movie titles left with zero files)?\n\nNo files on disk will be modified or deleted."))
        {
            return;
        }

        IsBusy = true;
        ProgressText = "Pruning missing movie entries from database…";
        try
        {
            var result = await Task.Run(() => _dbMaintenance.RemoveMissingMovieFilesAsync());
            MissingFiles.Clear();
            OnPropertyChanged(nameof(HasMissingFiles));
            ReportSummary = $"Pruned {result.MissingFilesRemoved} missing movie file entry(ies) and {result.EmptyTitlesRemoved} empty title(s) in {result.Duration.TotalMilliseconds:0} ms.";
            ProgressText = "Missing movie entries pruned.";
            CatalogChanged?.Invoke();
            await RefreshDatabaseHealthAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Pruning missing movie files failed.", ex);
            ProgressText = string.Empty;
            _dialogs.Error("Prune Missing Movies", $"Pruning failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task RebuildSearchIndexAsync()
    {
        IsBusy = true;
        ProgressText = "Rebuilding SQLite FTS5 full-text search index…";
        try
        {
            var count = await Task.Run(() => _maintenance.RebuildSearchIndexAsync());
            ReportSummary = $"Full-text search index rebuilt cleanly for {count} title(s).";
            ProgressText = "Search index rebuild complete.";
            CatalogChanged?.Invoke();
        }
        catch (NotSupportedException ex)
        {
            ProgressText = string.Empty;
            _dialogs.Info("Search Index", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Error("Search index rebuild failed.", ex);
            ProgressText = string.Empty;
            _dialogs.Error("Search Index", $"The index could not be rebuilt: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearMovieDiagnostics()
    {
        ScanSummary = string.Empty;
        ReportSummary = string.Empty;
        MissingFiles.Clear();
        OnPropertyChanged(nameof(HasMissingFiles));
    }

    // --------------------------------------------------------- Drama Management

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task ScanDramaLibraryAsync()
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.DramaRoot) || !Directory.Exists(settings.DramaRoot))
        {
            _dialogs.Error("Scan Drama Library", "Configure a valid Drama library root folder in Settings first.");
            return;
        }

        IsBusy = true;
        DramaScanSummary = string.Empty;
        ProgressText = "Scanning Drama library folder…";
        var progress = new Progress<string>(msg => ProgressText = msg);
        try
        {
            var res = await Task.Run(() => _dramaLibrary.ScanDramaRootAsync(settings.DramaRoot!, progress));
            ProgressText = $"Drama scan finished in {res.Duration.TotalSeconds:0.0} s.";

            var sb = new StringBuilder();
            sb.AppendLine($"Dramas found: {res.DramasFound}");
            sb.AppendLine($"New dramas created: {res.DramasCreated}");
            sb.AppendLine($"TMDb-matched: {res.DramasMatchedTmdb}");
            sb.AppendLine($"New episodes registered: {res.EpisodesRegistered}");
            sb.AppendLine($"Moved episodes relocated: {res.EpisodesRelocated}");
            sb.AppendLine($"Episodes updated: {res.EpisodesUpdated}");
            DramaScanSummary = sb.ToString().TrimEnd();
            CatalogChanged?.Invoke();
            await RefreshDatabaseHealthAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Drama library scan failed.", ex);
            ProgressText = string.Empty;
            _dialogs.Error("Scan Drama Library", $"Drama scan failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task ReanalyzeDramasAsync()
    {
        IsBusy = true;
        DramaReportSummary = string.Empty;
        ProgressText = "Re-analyzing dramas, relocating moved episodes, and re-parsing filenames…";
        var progress = new Progress<string>(msg => ProgressText = msg);
        try
        {
            var res = await Task.Run(() => _dramaLibrary.ReanalyzeDramasAsync(progress));
            ProgressText = $"Drama re-analysis finished in {res.Duration.TotalSeconds:0.0} s.";

            var sb = new StringBuilder();
            sb.AppendLine($"Dramas analyzed: {res.DramasAnalyzed}");
            sb.AppendLine($"Episodes re-parsed: {res.EpisodesReparsed}");
            sb.AppendLine($"Moved episodes relocated: {res.EpisodesRelocated}");
            sb.AppendLine($"Missing episodes remaining: {res.MissingEpisodesFound}");
            sb.AppendLine($"TMDb metadata refreshed: {res.TmdbRefreshed}");
            DramaReportSummary = sb.ToString().TrimEnd();
            CatalogChanged?.Invoke();
            await RefreshDatabaseHealthAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Drama re-analysis failed.", ex);
            ProgressText = string.Empty;
            _dialogs.Error("Re-analyze Dramas", $"Re-analysis failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task FindMissingDramaEpisodesAsync()
    {
        IsBusy = true;
        ProgressText = "Checking Drama catalog for missing episodes on disk…";
        try
        {
            var missing = await Task.Run(() => _dramaLibrary.GetMissingDramaEpisodesAsync());
            MissingDramaEpisodes.Clear();
            foreach (var ep in missing)
            {
                MissingDramaEpisodes.Add(new MissingDramaEpisodeRow(ep));
            }

            OnPropertyChanged(nameof(HasMissingDramaEpisodes));
            DramaReportSummary = missing.Count == 0
                ? "All cataloged drama episodes exist on disk."
                : $"{missing.Count} drama episode(s) are missing on disk. Run 'Scan Drama Library' or 'Re-analyze Dramas' to relocate moved files, or click 'Prune Missing' to remove deleted entries.";
            ProgressText = "Missing drama episode check complete.";
        }
        catch (Exception ex)
        {
            _logger.Error("Missing drama episodes check failed.", ex);
            ProgressText = string.Empty;
            _dialogs.Error("Drama Diagnostics", $"Check failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task CleanMissingDramaEpisodesAsync()
    {
        if (MissingDramaEpisodes.Count == 0)
        {
            DramaReportSummary = "No missing drama episodes to prune.";
            return;
        }

        if (!_dialogs.Confirm(
                "Prune Missing Drama Episodes",
                $"Remove {MissingDramaEpisodes.Count} missing episode entry(ies) from the Drama catalog?\n\nFiles on disk are never touched."))
        {
            return;
        }

        IsBusy = true;
        ProgressText = "Pruning missing drama episode entries…";
        try
        {
            var removed = await Task.Run(() => _dramaLibrary.RemoveMissingDramaEpisodesAsync());
            MissingDramaEpisodes.Clear();
            OnPropertyChanged(nameof(HasMissingDramaEpisodes));
            DramaReportSummary = $"Pruned {removed} missing drama episode entry(ies) from the catalog.";
            ProgressText = "Drama cleanup complete.";
            CatalogChanged?.Invoke();
            await RefreshDatabaseHealthAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Removing missing drama episodes failed.", ex);
            ProgressText = string.Empty;
            _dialogs.Error("Drama Diagnostics", $"Cleanup failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearDramaDiagnostics()
    {
        DramaScanSummary = string.Empty;
        DramaReportSummary = string.Empty;
        MissingDramaEpisodes.Clear();
        OnPropertyChanged(nameof(HasMissingDramaEpisodes));
    }

    // --------------------------------------------------------- Watch Folder Issues (Tab 1)

    [RelayCommand]
    public async Task RefreshWatchFolderIssuesAsync()
    {
        try
        {
            var items = await Task.Run(() => _issuesService.GetIssuesAsync());
            WatchFolderIssues.Clear();
            foreach (var item in items)
            {
                WatchFolderIssues.Add(new WatchFolderIssueRowViewModel(item));
            }

            OnPropertyChanged(nameof(HasWatchFolderIssues));
            OnPropertyChanged(nameof(HasNoWatchFolderIssues));
            OnPropertyChanged(nameof(WatchFolderIssuesTabHeader));
        }
        catch (Exception ex)
        {
            _logger.Warn($"Loading Watch Folder issues failed: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task RetryAllIssuesAsync()
    {
        if (WatchFolderIssues.Count == 0)
        {
            WatchFolderIssuesStatusText = "No Watch Folder issues to retry.";
            return;
        }

        var settings = _settingsService.Load();
        var hasMovieIssues = WatchFolderIssues.Any(i => i.IsMovie);
        var hasDramaIssues = WatchFolderIssues.Any(i => i.IsDrama);

        IsBusy = true;
        ProgressText = "Retrying Watch Folder scans for all unresolved issues…";
        try
        {
            var summaryParts = new List<string>();

            if (hasMovieIssues &&
                !string.IsNullOrWhiteSpace(settings.WatchFolderPath) &&
                Directory.Exists(settings.WatchFolderPath))
            {
                var scan = await Task.Run(() => _watchFolder.RescanOnceAsync(settings.WatchFolderPath!, retryUnmatched: true));
                summaryParts.Add($"Movies: {scan.FilesSeen} checked, {scan.Retried} retried");
            }

            if (hasDramaIssues &&
                !string.IsNullOrWhiteSpace(settings.DramaWatchFolderPath) &&
                Directory.Exists(settings.DramaWatchFolderPath))
            {
                var moved = await Task.Run(() => _dramaWatchFolder.RescanOnceAsync(settings.DramaWatchFolderPath!));
                summaryParts.Add($"Dramas: {moved} episode(s) imported");
            }

            WatchFolderIssuesStatusText = summaryParts.Count > 0
                ? $"Retry complete — {string.Join(" · ", summaryParts)}."
                : "Configure valid Watch Folder paths in Settings before retrying.";

            CatalogChanged?.Invoke();
            WatchFolderIssuesChanged?.Invoke();
            await RefreshWatchFolderIssuesAsync();
            await RefreshDatabaseHealthAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Retrying all Watch Folder issues failed.", ex);
            WatchFolderIssuesStatusText = $"Retry all failed: {ex.Message}";
        }
        finally
        {
            ProgressText = string.Empty;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenIssueFolder(WatchFolderIssueRowViewModel? row)
    {
        if (row is null) return;

        if (!string.IsNullOrWhiteSpace(row.SourcePath) && File.Exists(row.SourcePath))
        {
            _dialogs.OpenInExplorer(row.SourcePath);
            return;
        }

        var parent = !string.IsNullOrWhiteSpace(row.SourcePath) ? Path.GetDirectoryName(row.SourcePath) : null;
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
        {
            _dialogs.OpenInExplorer(parent);
            return;
        }

        var settings = _settingsService.Load();
        var fallback = row.IsDrama ? settings.DramaWatchFolderPath : settings.WatchFolderPath;
        if (!string.IsNullOrWhiteSpace(fallback) && Directory.Exists(fallback))
        {
            _dialogs.OpenInExplorer(fallback);
        }
        else
        {
            _dialogs.Info("Open Folder", "The source file or watch folder could not be located on disk.");
        }
    }

    [RelayCommand]
    private async Task RetryIssueAsync(WatchFolderIssueRowViewModel? row)
    {
        if (row is null || IsBusy) return;

        var settings = _settingsService.Load();
        IsBusy = true;
        ProgressText = $"Retrying {row.MediaType} Watch Folder scan…";
        try
        {
            if (row.IsDrama)
            {
                if (string.IsNullOrWhiteSpace(settings.DramaWatchFolderPath) || !Directory.Exists(settings.DramaWatchFolderPath))
                {
                    WatchFolderIssuesStatusText = "Configure your Drama Watch Folder in Settings first.";
                    return;
                }

                var moved = await Task.Run(() => _dramaWatchFolder.RescanOnceAsync(settings.DramaWatchFolderPath!));
                WatchFolderIssuesStatusText = moved > 0
                    ? $"Retry imported {moved} drama episode(s)."
                    : $"Retried '{row.FileName}' — check folder or filename if it still appears below.";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(settings.WatchFolderPath) || !Directory.Exists(settings.WatchFolderPath))
                {
                    WatchFolderIssuesStatusText = "Configure your Movies Watch Folder in Settings first.";
                    return;
                }

                var scan = await Task.Run(() => _watchFolder.RescanOnceAsync(settings.WatchFolderPath!, retryUnmatched: true));
                WatchFolderIssuesStatusText = $"Movie retry finished ({scan.FilesSeen} file(s) checked, {scan.Retried} retried).";
            }

            CatalogChanged?.Invoke();
            WatchFolderIssuesChanged?.Invoke();
            await RefreshWatchFolderIssuesAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Retrying Watch Folder issue failed.", ex);
            WatchFolderIssuesStatusText = $"Retry failed: {ex.Message}";
        }
        finally
        {
            ProgressText = string.Empty;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DismissIssueAsync(WatchFolderIssueRowViewModel? row)
    {
        if (row is null) return;
        try
        {
            await _issuesService.DismissIssueAsync(row.Model);
            WatchFolderIssuesStatusText = $"Dismissed '{row.FileName}'.";
            WatchFolderIssuesChanged?.Invoke();
            await RefreshWatchFolderIssuesAsync();
            await RefreshDatabaseHealthAsync();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Dismissing Watch Folder issue failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ClearWatchFolderIssuesAsync()
    {
        if (WatchFolderIssues.Count == 0)
        {
            WatchFolderIssuesStatusText = "No Watch Folder issues to clear.";
            return;
        }

        if (!_dialogs.Confirm("Clear Watch Folder Issues", "Clear all Watch Folder issue logs? Files in your watch folders will NOT be deleted."))
        {
            return;
        }

        try
        {
            var removed = await _issuesService.ClearAllIssuesAsync();
            WatchFolderIssuesStatusText = $"Cleared {removed} Watch Folder issue(s).";
            WatchFolderIssuesChanged?.Invoke();
            await RefreshWatchFolderIssuesAsync();
            await RefreshDatabaseHealthAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Clearing Watch Folder issues failed.", ex);
            WatchFolderIssuesStatusText = $"Clear failed: {ex.Message}";
        }
    }

    // --------------------------------------------------------- Database & System Health (Tab 2)

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    public async Task RefreshDatabaseHealthAsync()
    {
        try
        {
            var snap = await Task.Run(() => _dbMaintenance.GetHealthSnapshotAsync());

            DbSizeText = UiFormat.FormatBytes(snap.DatabaseBytes + snap.WalAndShmBytes);
            DbJournalDetailText = snap.WalAndShmBytes > 0
                ? $"DB {UiFormat.FormatBytes(snap.DatabaseBytes)} + WAL {UiFormat.FormatBytes(snap.WalAndShmBytes)} ({snap.JournalMode.ToUpperInvariant()})"
                : $"Mode: {snap.JournalMode.ToUpperInvariant()} · Updated {snap.MeasuredAtUtc.ToLocalTime():HH:mm:ss}";

            CatalogRecordsText = $"{snap.MovieCount + snap.DramaCount:N0}";
            CatalogRecordsDetailText = $"{snap.MovieCount} movies ({snap.MovieFileCount} files) · {snap.DramaCount} dramas ({snap.DramaEpisodeCount} eps)";

            var totalIssues = snap.WatchFolderIssueCount + snap.FailedImportLogCount;
            ErrorsAndLogsText = $"{totalIssues:N0}";
            ErrorsAndLogsDetailText = $"{snap.WatchFolderIssueCount} watch issues · {snap.FailedImportLogCount} import errors · {snap.LogFileCount} log file(s)";

            ReclaimableSpaceText = UiFormat.FormatBytes(snap.ReclaimableBytes);
            ReclaimableSpaceDetailText = $"Free SQLite pages · Poster cache: {UiFormat.FormatBytes(snap.PosterCacheBytes)} ({snap.PosterCacheFileCount} files)";
        }
        catch (Exception ex)
        {
            _logger.Warn($"Refreshing database health metrics failed: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task RunIntegrityCheckAsync()
    {
        IsBusy = true;
        ProgressText = "Running SQLite integrity_check and foreign_key_check…";
        try
        {
            var report = await Task.Run(() => _dbMaintenance.RunIntegrityCheckAsync());
            var sb = new StringBuilder();
            sb.AppendLine($"SQLite Integrity Check — {(report.IsHealthy ? "PASSED (Healthy)" : "ISSUES DETECTED")} ({report.Duration.TotalMilliseconds:0} ms)");
            foreach (var msg in report.IntegrityMessages.Take(20))
            {
                sb.AppendLine($"  • integrity_check: {msg}");
            }

            if (report.ForeignKeyViolations.Count == 0)
            {
                sb.AppendLine("  • foreign_key_check: 0 violation(s)");
            }
            else
            {
                sb.AppendLine($"  • foreign_key_check: {report.ForeignKeyViolations.Count} violation(s)");
                foreach (var fk in report.ForeignKeyViolations.Take(20))
                {
                    sb.AppendLine($"      - {fk}");
                }
            }

            DatabaseDiagnosticsSummary = sb.ToString().TrimEnd();
            ProgressText = report.IsHealthy ? "Database integrity check passed." : "Database integrity check reported issues.";
        }
        catch (Exception ex)
        {
            _logger.Error("Database integrity check failed.", ex);
            ProgressText = string.Empty;
            _dialogs.Error("Integrity Check", $"Integrity check failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task OptimizeDatabaseAsync()
    {
        IsBusy = true;
        ProgressText = "Flushing WAL journal, running VACUUM, and refreshing SQLite ANALYZE statistics…";
        try
        {
            var res = await Task.Run(() => _dbMaintenance.OptimizeDatabaseAsync());
            var sb = new StringBuilder();
            sb.AppendLine($"Database Optimization Complete ({res.Duration.TotalMilliseconds:0} ms)");
            sb.AppendLine($"  • WAL pages checkpointed: {res.WalPagesCheckpointed}");
            sb.AppendLine($"  • Size before: {UiFormat.FormatBytes(res.SizeBeforeBytes)}");
            sb.AppendLine($"  • Size after:  {UiFormat.FormatBytes(res.SizeAfterBytes)}");
            sb.AppendLine($"  • Reclaimed:   {UiFormat.FormatBytes(res.BytesReclaimed)}");
            sb.AppendLine("  • SQLite query planner statistics updated (ANALYZE).");

            DatabaseDiagnosticsSummary = sb.ToString().TrimEnd();
            ProgressText = "Database optimization complete.";
            await RefreshDatabaseHealthAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Database optimization failed.", ex);
            ProgressText = string.Empty;
            _dialogs.Error("Optimize Database", $"Database optimization failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task CleanOrphanedPostersAsync()
    {
        if (!_dialogs.Confirm(
                "Clean Orphaned Poster Cache",
                "Scan the cached Posters folder and delete image files that are no longer referenced by any Movie, Drama, or Cast entry?\n\nActive posters and media files are never touched."))
        {
            return;
        }

        IsBusy = true;
        ProgressText = "Scanning Poster cache for unreferenced image files…";
        try
        {
            var res = await Task.Run(() => _dbMaintenance.CleanOrphanedPosterCacheAsync());
            DatabaseDiagnosticsSummary =
                $"Poster Cache Cleanup Complete ({res.Duration.TotalMilliseconds:0} ms)\n" +
                $"  • Cache files scanned: {res.FilesScanned}\n" +
                $"  • Orphaned files deleted: {res.FilesDeleted}\n" +
                $"  • Disk space freed: {UiFormat.FormatBytes(res.BytesFreed)}";
            ProgressText = $"Poster cache cleanup deleted {res.FilesDeleted} orphaned file(s).";
            await RefreshDatabaseHealthAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Poster cache cleanup failed.", ex);
            ProgressText = string.Empty;
            _dialogs.Error("Clean Poster Cache", $"Poster cache cleanup failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task PurgeErrorLogsAsync()
    {
        if (!_dialogs.Confirm(
                "Clear Error Logs & Bug Reports",
                "Clear all failed/unmatched Movie import error records, Watch Folder issue logs, and archived diagnostic log files from disk?\n\nYour Movies, Dramas, Tickets, and media files will NOT be affected."))
        {
            return;
        }

        IsBusy = true;
        ProgressText = "Clearing error logs and diagnostic reports…";
        try
        {
            var res = await Task.Run(() => _dbMaintenance.PurgeErrorLogsAndReportsAsync());
            DatabaseDiagnosticsSummary =
                $"Error & Diagnostic Log Purge Complete ({res.Duration.TotalMilliseconds:0} ms)\n" +
                $"  • Failed/unmatched movie import logs cleared: {res.FailedMovieLogsDeleted}\n" +
                $"  • Watch Folder issue records cleared: {res.WatchFolderIssuesDeleted}\n" +
                $"  • Archived log files deleted: {res.OldLogFilesDeleted} ({UiFormat.FormatBytes(res.LogBytesFreed)} freed)";
            ProgressText = "Error logs and diagnostic reports cleared.";

            WatchFolderIssuesChanged?.Invoke();
            await RefreshWatchFolderIssuesAsync();
            await RefreshDatabaseHealthAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Purging error logs failed.", ex);
            ProgressText = string.Empty;
            _dialogs.Error("Clear Error Logs", $"Clearing error logs failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        Directory.CreateDirectory(_paths.LogsDirectory);
        _dialogs.OpenInExplorer(_paths.LogsDirectory);
    }

    [RelayCommand]
    private void ClearDatabaseDiagnostics() => DatabaseDiagnosticsSummary = string.Empty;

    // --------------------------------------------------------- Download Manager & Lookup Sites (Tab 3)

    private void ReloadDownloadSettings()
    {
        var dl = _downloadSettings.Load();
        IdmExecutablePath = dl.IdmExecutablePath ?? IdmLauncherService.DetectIdmExecutablePath() ?? string.Empty;
        PreferredDownloadQuality = DownloadQualityTiers.All.Contains(dl.PreferredQuality, StringComparer.OrdinalIgnoreCase)
            ? dl.PreferredQuality
            : DownloadQualityTiers.Q1080p;
        RouteIdmToMoviesWatchFolder = dl.RouteIdmToMoviesWatchFolder;
        AutoCompleteTicketsOnImport = dl.AutoCompleteTicketsOnImport;
    }

    [RelayCommand]
    private void AutoDetectIdm()
    {
        var found = IdmLauncherService.DetectIdmExecutablePath();
        if (!string.IsNullOrWhiteSpace(found))
        {
            IdmExecutablePath = found;
            DownloadSettingsStatusText = $"Detected IDM at '{found}'. Click Save Download Settings to persist.";
        }
        else
        {
            DownloadSettingsStatusText = "IDMan.exe was not found in standard Program Files folders. Use Browse… to select it manually.";
        }
    }

    [RelayCommand]
    private void BrowseIdm()
    {
        var picked = _dialogs.PickFile("Select IDMan.exe", "IDM Executable (IDMan.exe)|IDMan.exe|Executable (*.exe)|*.exe|All files (*.*)|*.*");
        if (!string.IsNullOrWhiteSpace(picked))
        {
            IdmExecutablePath = picked;
        }
    }

    [RelayCommand]
    private void SaveDownloadSettings()
    {
        try
        {
            var dl = _downloadSettings.Load();
            dl.IdmExecutablePath = string.IsNullOrWhiteSpace(IdmExecutablePath) ? null : IdmExecutablePath.Trim();
            dl.PreferredQuality = string.IsNullOrWhiteSpace(PreferredDownloadQuality)
                ? DownloadQualityTiers.Q1080p
                : PreferredDownloadQuality.Trim();
            dl.RouteIdmToMoviesWatchFolder = RouteIdmToMoviesWatchFolder;
            dl.AutoCompleteTicketsOnImport = AutoCompleteTicketsOnImport;
            _downloadSettings.Save(dl);

            DownloadSettingsStatusText = $"Saved Download Manager settings (Default quality: {dl.PreferredQuality}, downward fallback enabled).";
            LookupSitesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error("Saving Download Manager settings failed.", ex);
            DownloadSettingsStatusText = $"Save failed: {ex.Message}";
        }
    }

    private void ReloadLookupSites()
    {
        var settings = _settingsService.Load();
        var entries = settings.LookupSites is { Count: > 0 }
            ? settings.LookupSites
            : ImdbLookupService.GetDefaultSites().ToList();

        _downloadSettings.MigrateBollyFlixBrowserBinding(entries);
        var dl = _downloadSettings.Load();

        LookupSites.Clear();
        foreach (var entry in entries)
        {
            dl.SiteWorkflowBindings.TryGetValue(entry.Id, out var boundProviderId);
            LookupSites.Add(new LookupSiteRowViewModel(
                entry,
                boundProviderId,
                AvailableWorkflows,
                PersistLookupSites,
                OnSiteWorkflowChanged));
        }
    }

    private void OnSiteWorkflowChanged(string siteId, string? providerId)
    {
        try
        {
            _downloadSettings.SetProviderIdForSite(siteId, providerId);
            LookupSitesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Saving workflow binding for site '{siteId}' failed: {ex.Message}");
        }
    }

    private void PersistLookupSites()
    {
        try
        {
            var settings = _settingsService.Load();
            settings.LookupSites = LookupSites.Select(s => s.Entry).ToList();
            _settingsService.Save(settings);
            LookupSitesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Saving Lookup sites failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void AddLookupSite()
    {
        var name = (NewLookupSiteName ?? string.Empty).Trim();
        var template = (NewLookupSiteUrlTemplate ?? string.Empty).Trim();
        var marker = (NewLookupNoResultsMarker ?? string.Empty).Trim();

        if (name.Length == 0 || template.Length == 0)
        {
            _dialogs.Error("Add Lookup Site", "Enter both a Site Name and a Search URL Template (e.g. https://example.com/?s={imdb_id}).");
            return;
        }

        if (!template.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !template.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            template = "https://" + template;
        }

        var previewUrl = ImdbLookupService.BuildLookupUrl(template, "tt1272878", "Test Movie", 2024);
        if (!Uri.TryCreate(previewUrl, UriKind.Absolute, out _))
        {
            _dialogs.Error("Add Lookup Site", "The URL template does not produce a valid web address.");
            return;
        }

        var entry = new LookupSiteEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            UrlTemplate = template,
            NoResultsMarker = string.IsNullOrWhiteSpace(marker) ? null : marker,
            IsEnabled = true,
            IsBuiltIn = false
        };

        LookupSites.Add(new LookupSiteRowViewModel(entry, null, AvailableWorkflows, PersistLookupSites, OnSiteWorkflowChanged));
        PersistLookupSites();

        NewLookupSiteName = string.Empty;
        NewLookupSiteUrlTemplate = string.Empty;
        NewLookupNoResultsMarker = string.Empty;
        LookupSitesStatusText = $"Added '{name}' and enabled it for background Lookup checks.";
    }

    [RelayCommand]
    private void RemoveLookupSite(LookupSiteRowViewModel? site)
    {
        if (site is null) return;
        LookupSites.Remove(site);
        _downloadSettings.SetProviderIdForSite(site.Id, null);
        PersistLookupSites();
        LookupSitesStatusText = $"Removed '{site.Name}'.";
    }

    [RelayCommand]
    private void ResetDefaultLookupSites()
    {
        if (!_dialogs.Confirm("Reset Lookup Sites", "Restore the default Lookup sites list? Any manually added sites will be removed."))
        {
            return;
        }

        LookupSites.Clear();
        foreach (var entry in ImdbLookupService.GetDefaultSites())
        {
            LookupSites.Add(new LookupSiteRowViewModel(entry, null, AvailableWorkflows, PersistLookupSites, OnSiteWorkflowChanged));
        }

        PersistLookupSites();
        LookupSitesStatusText = "Restored default Lookup sites.";
    }

    // --------------------------------------------------------- Data & Backups (Tab 4)

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task BackupAsync()
    {
        IsBusy = true;
        ProgressText = "Creating validated SQLite database backup…";
        try
        {
            var file = await Task.Run(() => _backupService.BackupAsync());
            ReloadBackups();
            ProgressText = "Database backup completed.";
            _dialogs.Info("Backup Created", $"Validated backup saved to:\n{file}");
        }
        catch (Exception ex)
        {
            _logger.Error("Backup failed.", ex);
            ProgressText = string.Empty;
            _dialogs.Error("Backup", $"The backup failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task RestoreAsync()
    {
        var selected = SelectedBackup?.Source.FilePath
                       ?? _dialogs.PickFile("Select a DigitalCenter backup", "SQLite backup (*.db)|*.db|All files (*.*)|*.*");
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        if (!_dialogs.Confirm(
                "Restore Database Backup",
                $"Restore the database from:\n{selected}\n\nA safety copy of your current database is saved automatically before restoring. DigitalCenter will close so it can reopen cleanly with the restored catalog."))
        {
            return;
        }

        IsBusy = true;
        ProgressText = "Restoring database backup…";
        try
        {
            await Task.Run(() => _backupService.RestoreAsync(selected));
            _dialogs.Info(
                "Restore Complete",
                "The database was restored. DigitalCenter will now close; please start it again to reload the restored catalog.");
            _appControl.Shutdown();
        }
        catch (Exception ex)
        {
            _logger.Error("Restore failed.", ex);
            ProgressText = string.Empty;
            _dialogs.Error("Restore", $"The restore failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenBackupsFolder()
    {
        Directory.CreateDirectory(_paths.BackupsDirectory);
        _dialogs.OpenInExplorer(_paths.BackupsDirectory);
    }

    private void ReloadBackups()
    {
        Backups.Clear();
        foreach (var item in _backupService.ListBackups())
        {
            Backups.Add(new BackupItemViewModel(item));
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task ExportCsvAsync()
    {
        var folder = _dialogs.PickFolder(_paths.DataRoot);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var target = Path.Combine(folder, $"digitalcenter-catalog-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        IsBusy = true;
        ProgressText = "Exporting catalog to CSV…";
        try
        {
            var rows = await Task.Run(() => _maintenance.ExportCsvAsync(target));
            CsvStatusText = $"Exported {rows} catalog row(s) to {target}";
            ProgressText = "CSV export complete.";
            _dialogs.Info("CSV Export", $"The catalog ({rows} rows) was exported to:\n{target}");
        }
        catch (Exception ex)
        {
            _logger.Error("CSV export failed.", ex);
            ProgressText = string.Empty;
            _dialogs.Error("CSV Export", $"The export failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task ImportCsvAsync()
    {
        var source = _dialogs.PickFile("Select a catalog CSV file", "CSV file (*.csv)|*.csv|All files (*.*)|*.*");
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        if (!_dialogs.Confirm(
                "Import Catalog CSV",
                $"Additively import catalog rows from:\n{source}\n\nExisting titles are merged; files on disk are never moved or deleted."))
        {
            return;
        }

        IsBusy = true;
        ProgressText = "Importing catalog from CSV…";
        try
        {
            var summary = await Task.Run(() => _maintenance.ImportCsvAsync(source));
            var report = new StringBuilder();
            report.AppendLine($"Rows read: {summary.RowsRead} · Titles created: {summary.TitlesCreated} · Merged: {summary.TitlesMerged}");
            report.AppendLine($"Files registered: {summary.FilesRegistered} · Missing on disk: {summary.FilesMissing}");
            if (summary.Errors.Count > 0)
            {
                report.AppendLine($"Warnings ({summary.Errors.Count}): {string.Join("; ", summary.Errors.Take(3))}");
            }

            CsvStatusText = report.ToString().TrimEnd();
            ProgressText = "CSV import complete.";
            CatalogChanged?.Invoke();
            await RefreshDatabaseHealthAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("CSV import failed.", ex);
            ProgressText = string.Empty;
            _dialogs.Error("CSV Import", $"The import failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
