using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.ViewModels;

/// <summary>One row of the Movie Watch Folder activity log.</summary>
public sealed class ActivityItemViewModel
{
    public ActivityItemViewModel(MetadataImportLog log)
    {
        Id = log.Id;
        FileName = log.SourceFile;
        SourcePath = log.SourcePath ?? string.Empty;
        SizeText = log.FileSize is { } size ? UiFormat.FormatBytes(size) : "—";
        Status = log.Status;
        StatusText = ImportStatusLabels.For(log.Status);
        StatusKey = log.Status switch
        {
            ImportStatuses.Imported => "Ok",
            ImportStatuses.ManuallyMoved => "Ok",
            ImportStatuses.Failed => "Bad",
            ImportStatuses.Matched => "Active",
            ImportStatuses.Pending => "Pending",
            ImportStatuses.Approved => "Pending",
            _ => "Neutral"
        };
        DetectedType = !string.IsNullOrWhiteSpace(log.CategoryName) && !LanguageResolver.IsKnownLanguageName(log.CategoryName) && log.CategoryName != "Unknown" ? "Collection" : "Movie";
        LanguageDisplay = string.IsNullOrWhiteSpace(log.CategoryName) ? "—" : log.CategoryName!;
        MatchedTitle = string.IsNullOrWhiteSpace(log.ParsedTitle) ? "—" : log.ParsedTitle!;
        MatchSummary = log.TmdbId is not null
            ? $"{log.ParsedTitle}{(log.ParsedYear is { } y ? $" ({y})" : "")} · {LanguageDisplay}" +
              (log.TargetPath is { Length: > 0 } tp ? $" → {tp}" : "")
            : ImportStatusLabels.For(log.Status);
        Detail = log.ErrorDetails ?? log.Note ?? string.Empty;
        HasDetail = Detail.Length > 0;
        MediaTitleId = log.MediaTitleId;
        TimestampText = log.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public int Id { get; }
    public string FileName { get; }
    public string SourcePath { get; }
    public string SizeText { get; }
    public string Status { get; }
    public string StatusText { get; }
    public string StatusKey { get; }
    public string DetectedType { get; }
    public string LanguageDisplay { get; }
    public string MatchedTitle { get; }
    public string MatchSummary { get; }
    public string Detail { get; }
    public bool HasDetail { get; }
    public int? MediaTitleId { get; }
    public string TimestampText { get; }

    public MetadataImportLog Log { get; internal set; } = null!;
}

/// <summary>One row of the Drama Watch Folder activity log (persisted in SQLite).</summary>
public sealed class DramaActivityItemViewModel
{
    public DramaActivityItemViewModel(DramaWatchHistoryEntry entry)
    {
        Id = entry.Id;
        DramaTitle = entry.DramaTitle;
        EpisodeBadge = entry.EpisodeNumber > 0
            ? $"S{entry.SeasonNumber:00}E{entry.EpisodeNumber:00}"
            : $"S{entry.SeasonNumber:00}";
        SourceFileName = entry.SourceFileName;
        TargetPath = entry.TargetPath;
        SizeText = entry.FileSize > 0 ? UiFormat.FormatBytes(entry.FileSize) : "—";
        StatusText = string.IsNullOrWhiteSpace(entry.Status) ? "Imported" : entry.Status;
        TimestampText = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        DestinationSummary = $"Moved → {entry.TargetPath}";
    }

    public int Id { get; }
    public string DramaTitle { get; }
    public string EpisodeBadge { get; }
    public string SourceFileName { get; }
    public string TargetPath { get; }
    public string SizeText { get; }
    public string StatusText { get; }
    public string TimestampText { get; }
    public string DestinationSummary { get; }
}

/// <summary>
/// Redesigned Watch Folder workspace: separate Status section (Movies &amp; Dramas, no Awaiting Info,
/// plus clickable Maintenance issues badge when &gt; 0) and two side-by-side import log panels
/// (Movie Imports on the left, Drama Imports on the right) with per-panel action buttons.
/// Folder locations are configured in Settings.
/// </summary>
public partial class ManagementViewModel : ObservableObject
{
    private readonly WatchFolderService _watchFolder;
    private readonly DramaWatchFolderService _dramaWatchFolder;
    private readonly ImportService _imports;
    private readonly DramaLibraryService _dramaLibrary;
    private readonly WatchFolderIssueService _issues;
    private readonly SettingsService _settingsService;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly ActivityFeedService _activityFeed;

    private DateTime? _lastMovieScanUtc;

    public event Action? ImportCompleted;
    public event Action? DramaImportCompleted;
    public event Action? OpenMaintenanceIssuesRequested;

    public ManagementViewModel(
        WatchFolderService watchFolder,
        DramaWatchFolderService dramaWatchFolder,
        ImportService imports,
        DramaLibraryService dramaLibrary,
        WatchFolderIssueService issues,
        SettingsService settingsService,
        IDialogService dialogs,
        IAppLogger logger,
        ActivityFeedService activityFeed)
    {
        _watchFolder = watchFolder;
        _dramaWatchFolder = dramaWatchFolder;
        _imports = imports;
        _dramaLibrary = dramaLibrary;
        _issues = issues;
        _settingsService = settingsService;
        _dialogs = dialogs;
        _logger = logger;
        _activityFeed = activityFeed;

        StatusText = string.Empty;
        MovieWatchStateText = "Idle";
        MovieWatchPathSummary = "Configure in Settings";
        MovieLastScanText = "Not scanned yet";
        DramaWatchStateText = "Idle";
        DramaWatchPathSummary = "Configure in Settings";
        DramaLastScanText = "Not scanned yet";
    }

    public ObservableCollection<ActivityItemViewModel> Activity { get; } = [];
    public ObservableCollection<DramaActivityItemViewModel> DramaActivity { get; } = [];

    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    // Separate Status section — Movies
    [ObservableProperty] public partial string MovieWatchStateText { get; set; }
    [ObservableProperty] public partial bool IsMovieWatcherRunning { get; set; }
    [ObservableProperty] public partial string MovieWatchPathSummary { get; set; }
    [ObservableProperty] public partial int MovieImportedCount { get; set; }
    [ObservableProperty] public partial string MovieLastScanText { get; set; }

    // Separate Status section — Dramas
    [ObservableProperty] public partial string DramaWatchStateText { get; set; }
    [ObservableProperty] public partial bool IsDramaWatcherRunning { get; set; }
    [ObservableProperty] public partial string DramaWatchPathSummary { get; set; }
    [ObservableProperty] public partial int DramaImportedCount { get; set; }
    [ObservableProperty] public partial string DramaLastScanText { get; set; }

    // Unified Maintenance Issues indicator (shown only when > 0)
    [ObservableProperty] public partial int UnresolvedIssuesCount { get; set; }

    public bool HasUnresolvedIssues => UnresolvedIssuesCount > 0;
    public string UnresolvedIssuesBadgeText => UnresolvedIssuesCount == 1
        ? "1 issue → View in Maintenance"
        : $"{UnresolvedIssuesCount} issues → View in Maintenance";

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);
    public bool HasNoMovieActivity => Activity.Count == 0;
    public bool HasNoDramaActivity => DramaActivity.Count == 0;

    partial void OnUnresolvedIssuesCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasUnresolvedIssues));
        OnPropertyChanged(nameof(UnresolvedIssuesBadgeText));
    }

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatusText));

    public Task OnNavigatedToAsync() => RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        UpdateStatusMetadata();

        try
        {
            var stats = await _imports.GetStatsAsync();
            MovieImportedCount = stats.Imported;

            var logs = await _imports.GetActivityAsync();
            Activity.Clear();
            foreach (var log in logs)
            {
                // Keep Watch Folder Movie Imports panel focused on active/imported activity;
                // failed/no-match issues are tracked centrally in Maintenance → Watch Folder Issues.
                if (log.Status is ImportStatuses.Failed or ImportStatuses.NoMatch)
                {
                    continue;
                }
                Activity.Add(new ActivityItemViewModel(log) { Log = log });
            }
            OnPropertyChanged(nameof(HasNoMovieActivity));

            var dramaHistory = await Task.Run(() => _dramaLibrary.GetWatchHistoryAsync());
            DramaActivity.Clear();
            foreach (var entry in dramaHistory)
            {
                DramaActivity.Add(new DramaActivityItemViewModel(entry));
            }
            DramaImportedCount = await Task.Run(() => _dramaLibrary.GetWatchHistoryCountAsync());
            OnPropertyChanged(nameof(HasNoDramaActivity));

            await RefreshIssueCountAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Refreshing Watch Folder activity failed.", ex);
            StatusText = $"Refresh failed: {ex.Message}";
        }
    }

    public async Task RefreshIssueCountAsync()
    {
        try
        {
            UnresolvedIssuesCount = await Task.Run(() => _issues.GetUnresolvedCountAsync());
        }
        catch (Exception ex)
        {
            _logger.Warn($"Refreshing Watch Folder issues count failed: {ex.Message}");
        }
    }

    private void UpdateStatusMetadata()
    {
        var settings = _settingsService.Load();

        var hasMoviePath = !string.IsNullOrWhiteSpace(settings.WatchFolderPath);
        IsMovieWatcherRunning = _watchFolder.IsRunning;
        MovieWatchStateText = !hasMoviePath
            ? "Not Configured"
            : _watchFolder.IsRunning ? "Watching" : "Idle";
        MovieWatchPathSummary = hasMoviePath
            ? settings.WatchFolderPath!
            : "Set Movies Watch Folder in Settings";
        MovieLastScanText = _lastMovieScanUtc.HasValue
            ? $"Last scan: {_lastMovieScanUtc.Value.ToLocalTime():HH:mm:ss}"
            : "Not scanned yet";

        var hasDramaPath = !string.IsNullOrWhiteSpace(settings.DramaWatchFolderPath);
        IsDramaWatcherRunning = _dramaWatchFolder.IsRunning;
        DramaWatchStateText = !hasDramaPath
            ? "Not Configured"
            : _dramaWatchFolder.IsRunning ? "Watching" : "Idle";
        DramaWatchPathSummary = hasDramaPath
            ? settings.DramaWatchFolderPath!
            : "Set Drama Watch Folder in Settings";
        DramaLastScanText = _dramaWatchFolder.LastScanUtc.HasValue
            ? $"Last scan: {_dramaWatchFolder.LastScanUtc.Value.ToLocalTime():HH:mm:ss}"
            : "Not scanned yet";
    }

    // --------------------------------------------------------- Movie Watch Folder Actions

    [RelayCommand]
    private async Task ScanMoviesAsync()
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.WatchFolderPath))
        {
            _dialogs.Info("Movies Watch Folder", "Configure your Movies Watch Folder in Settings first.");
            return;
        }

        var issues = SettingsService.ValidateWatchFolderPath(settings);
        if (issues.Count > 0)
        {
            _dialogs.Error("Invalid Movies Watch Folder", string.Join(Environment.NewLine, issues));
            return;
        }

        IsBusy = true;
        StatusText = "Scanning Movies Watch Folder…";
        try
        {
            var watchPath = settings.WatchFolderPath!;
            _watchFolder.StabilityDelay = TimeSpan.FromSeconds(Math.Clamp(settings.StabilityDelaySeconds, 2, 600));

            var scan = await Task.Run(() => _watchFolder.RescanOnceAsync(watchPath, retryUnmatched: true));
            _lastMovieScanUtc = DateTime.UtcNow;

            if (settings.WatchFolderEnabled && !_watchFolder.IsRunning)
            {
                _watchFolder.Start(watchPath);
            }

            if (scan.FilesSeen == 0)
            {
                StatusText = $"Movies scan complete: no files found in '{watchPath}'.";
            }
            else if (scan.Unstable > 0 && scan.NewPending == 0 && scan.Retried == 0)
            {
                StatusText = $"Movies scan saw {scan.FilesSeen} file(s), but {scan.Unstable} file(s) are still copying or locked — try again in a moment.";
            }
            else
            {
                StatusText = $"Movies scan complete ({scan.FilesSeen} file(s) seen): {scan.NewPending} new, {scan.Retried} retried, {scan.AlreadyKnown} known.";
            }

            ImportCompleted?.Invoke();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Movies Watch Folder scan failed.", ex);
            StatusText = $"Movies scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenMoviesWatchFolder()
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.WatchFolderPath))
        {
            _dialogs.Info("Movies Watch Folder", "No Movies Watch Folder is configured yet. Set it in Settings.");
            return;
        }

        Directory.CreateDirectory(settings.WatchFolderPath);
        _dialogs.OpenInExplorer(settings.WatchFolderPath);
    }

    [RelayCommand]
    private async Task ClearMovieHistoryAsync()
    {
        if (MovieImportedCount == 0 && Activity.Count == 0)
        {
            StatusText = "Movie import history is already empty.";
            return;
        }

        var confirmed = _dialogs.Confirm(
            "Clear Movie Import History",
            "Clear all Movie Watch Folder import history?\n\nThis only clears the activity log. No movies, files, or library entries will be deleted.");
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var cleared = await _imports.ClearHistoryAsync();
            StatusText = cleared > 0
                ? $"Cleared {cleared} Movie import history item(s)."
                : "No Movie import history items were cleared.";
            if (cleared > 0)
            {
                _activityFeed.Publish($"Movie import history cleared ({cleared})");
            }
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Clearing Movie Watch Folder history failed.", ex);
            StatusText = $"Clear Movie history failed: {ex.Message}";
            _dialogs.Error("Clear Movie History", StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --------------------------------------------------------- Drama Watch Folder Actions

    [RelayCommand]
    private async Task ScanDramasAsync()
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.DramaWatchFolderPath) || !Directory.Exists(settings.DramaWatchFolderPath))
        {
            _dialogs.Info("Drama Watch Folder", "Configure your Drama Watch Folder and Drama Root in Settings first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.DramaRoot))
        {
            _dialogs.Info("Drama Root", "Configure your Drama library root folder in Settings first.");
            return;
        }

        IsBusy = true;
        StatusText = "Scanning Drama Watch Folder…";
        try
        {
            var dramaWatchPath = settings.DramaWatchFolderPath!;
            var moved = await Task.Run(() => _dramaWatchFolder.RescanOnceAsync(dramaWatchPath));

            if (settings.DramaWatchFolderEnabled && !_dramaWatchFolder.IsRunning)
            {
                _dramaWatchFolder.Start(dramaWatchPath);
            }

            StatusText = moved > 0
                ? $"Drama scan complete: imported {moved} episode(s)."
                : "Drama scan complete: no new episodes found.";

            if (moved > 0)
            {
                DramaImportCompleted?.Invoke();
            }
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Drama Watch Folder scan failed.", ex);
            StatusText = $"Drama scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenDramaWatchFolder()
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.DramaWatchFolderPath))
        {
            _dialogs.Info("Drama Watch Folder", "No Drama Watch Folder is configured yet. Set it in Settings.");
            return;
        }

        Directory.CreateDirectory(settings.DramaWatchFolderPath);
        _dialogs.OpenInExplorer(settings.DramaWatchFolderPath);
    }

    [RelayCommand]
    private async Task ClearDramaHistoryAsync()
    {
        if (DramaImportedCount == 0 && DramaActivity.Count == 0)
        {
            StatusText = "Drama import history is already empty.";
            return;
        }

        var confirmed = _dialogs.Confirm(
            "Clear Drama Import History",
            "Clear all Drama Watch Folder import history?\n\nThis only clears the activity log. No dramas, episodes, or files on disk will be deleted.");
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var cleared = await Task.Run(() => _dramaLibrary.ClearWatchHistoryAsync());
            StatusText = cleared > 0
                ? $"Cleared {cleared} Drama import history item(s)."
                : "No Drama import history items were cleared.";
            if (cleared > 0)
            {
                _activityFeed.Publish($"Drama import history cleared ({cleared})");
            }
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Clearing Drama Watch Folder history failed.", ex);
            StatusText = $"Clear Drama history failed: {ex.Message}";
            _dialogs.Error("Clear Drama History", StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenMaintenanceIssues() => OpenMaintenanceIssuesRequested?.Invoke();
}
