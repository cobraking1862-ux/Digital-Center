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

/// <summary>
/// Management workspace: the ONE place where the single default Watch Folder is
/// configured and monitored — its path, the monitoring switches, start/stop,
/// rescan, import statistics, recent activity with the automatic match results,
/// a file preview and the destination-selection / confirmation workflow.
/// Settings no longer holds a Watch Folder path or any monitoring state, so
/// there is exactly one authoritative saved value
/// (<see cref="AppSettings.WatchFolderPath"/>) and one monitoring state.
/// There is deliberately no metadata editing here — that belongs to the details
/// screen. Changing the path never moves media and never erases records.
/// </summary>
public partial class ManagementViewModel : ObservableObject
{
    private readonly WatchFolderService _watchFolder;
    private readonly ImportService _imports;
    private readonly SettingsService _settingsService;
    private readonly ActivityFeed _activity;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _logger;

    /// <summary>Raised after an import completed so the shell can refresh the library.</summary>
    public event Action? ImportCompleted;

    public ManagementViewModel(
        WatchFolderService watchFolder,
        ImportService imports,
        SettingsService settingsService,
        ActivityFeed activity,
        IDialogService dialogs,
        IAppLogger logger)
    {
        _watchFolder = watchFolder;
        _imports = imports;
        _settingsService = settingsService;
        _activity = activity;
        _dialogs = dialogs;
        _logger = logger;

        WatchPathDisplay = string.Empty;
        MonitorStatusText = string.Empty;
        StatusText = string.Empty;
        DestinationPreviewText = string.Empty;
        ImportSummary = string.Empty;
        WatchFolderPath = string.Empty;
        StabilityDelaySeconds = 8;
        LoadWatchFolderSettings();
    }

    public ObservableCollection<ActivityItemViewModel> Activity { get; } = [];

    [ObservableProperty] public partial string WatchPathDisplay { get; set; }
    [ObservableProperty] public partial string MonitorStatusText { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }

    // ------------------------------------------------ Watch Folder (the only
    // place these values are edited; they are persisted through SettingsService)
    [ObservableProperty] public partial string WatchFolderPath { get; set; }
    [ObservableProperty] public partial bool WatchFolderEnabled { get; set; }
    [ObservableProperty] public partial bool ScanOnStartup { get; set; }
    [ObservableProperty] public partial bool IncludeSubfolders { get; set; }
    [ObservableProperty] public partial int StabilityDelaySeconds { get; set; }
    [ObservableProperty] public partial bool HasWatchFolderChanges { get; set; }

    /// <summary>The saved Watch Folder path, so unsaved edits can be detected.</summary>
    private string _savedWatchFolderPath = string.Empty;

    /// <summary>The saved file-stability delay, so unsaved edits can be detected.</summary>
    private int _savedStabilityDelaySeconds = 8;

    partial void OnWatchFolderPathChanged(string value) => UpdateWatchFolderDirtyState();
    partial void OnWatchFolderEnabledChanged(bool value) => UpdateWatchFolderDirtyState();
    partial void OnScanOnStartupChanged(bool value) => UpdateWatchFolderDirtyState();
    partial void OnIncludeSubfoldersChanged(bool value) => UpdateWatchFolderDirtyState();
    partial void OnStabilityDelaySecondsChanged(int value) => UpdateWatchFolderDirtyState();

    /// <summary>
    /// "Save Watch Folder" is enabled as soon as anything in this card differs
    /// from the saved configuration — the path or the stability delay.
    /// </summary>
    private void UpdateWatchFolderDirtyState() =>
        HasWatchFolderChanges =
            !string.Equals(WatchFolderPath.Trim(), _savedWatchFolderPath, StringComparison.OrdinalIgnoreCase) ||
            StabilityDelaySeconds != _savedStabilityDelaySeconds;
    [ObservableProperty] public partial string DestinationPreviewText { get; set; }
    [ObservableProperty] public partial string ImportSummary { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial ActivityItemViewModel? SelectedItem { get; set; }

    // Statistics
    [ObservableProperty] public partial int TotalCount { get; set; }
    [ObservableProperty] public partial int ImportedCount { get; set; }
    [ObservableProperty] public partial int SkippedCount { get; set; }
    [ObservableProperty] public partial int FailedCount { get; set; }
    [ObservableProperty] public partial int NoMatchCount { get; set; }
    [ObservableProperty] public partial int UnsupportedCount { get; set; }
    [ObservableProperty] public partial int AwaitingCount { get; set; }

    // Selected-file preview
    [ObservableProperty] public partial bool HasPreview { get; set; }
    [ObservableProperty] public partial string PreviewFileName { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewSourcePath { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewSizeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewTypeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewMatchText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewDetailText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewTargetText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool PreviewHasTarget { get; set; }
    [ObservableProperty] public partial bool PreviewIsMedia { get; set; }
    [ObservableProperty] public partial bool CanConfirmSelected { get; set; }
    [ObservableProperty] public partial bool CanSkipSelected { get; set; }

    public bool IsMonitoring => _watchFolder.IsRunning;

    partial void OnSelectedItemChanged(ActivityItemViewModel? value) => _ = RefreshPreviewAsync();

    /// <summary>Navigation hook (also used after watch-folder events).</summary>
    public async Task OnNavigatedToAsync()
    {
        LoadWatchFolderSettings();
        await RefreshAsync();
    }

    /// <summary>Reads the single authoritative Watch Folder configuration into the editor fields.</summary>
    private void LoadWatchFolderSettings()
    {
        var settings = _settingsService.Load();
        _savedWatchFolderPath = (settings.WatchFolderPath ?? string.Empty).Trim();
        WatchFolderPath = _savedWatchFolderPath;
        WatchFolderEnabled = settings.WatchFolderEnabled;
        ScanOnStartup = settings.ScanOnStartup;
        IncludeSubfolders = settings.WatchFolderIncludeSubfolders;
        StabilityDelaySeconds = Math.Clamp(settings.StabilityDelaySeconds, 2, 600);
        _savedStabilityDelaySeconds = StabilityDelaySeconds;
        HasWatchFolderChanges = false;
    }

    /// <summary>True while a dashboard refresh is running on the UI thread.</summary>
    private bool _refreshRunning;

    /// <summary>Set when another refresh was requested while one was running.</summary>
    private bool _refreshAgain;

    /// <summary>
    /// Refreshes the dashboard, coalescing bursts. A watch-folder event, a
    /// navigation and a finished rescan can all land within a few milliseconds;
    /// instead of rebuilding the same rows three times (which is what made the
    /// screen look frozen) the running pass is simply repeated once at the end.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_refreshRunning)
        {
            _refreshAgain = true;
            return;
        }

        _refreshRunning = true;
        try
        {
            do
            {
                _refreshAgain = false;
                await RefreshCoreAsync();
            }
            while (_refreshAgain);
        }
        finally
        {
            _refreshRunning = false;
        }
    }

    private async Task RefreshCoreAsync()
    {
        var settings = _settingsService.Load();
        var configured = !string.IsNullOrWhiteSpace(settings.WatchFolderPath);
        WatchPathDisplay = configured
            ? settings.WatchFolderPath!
            : "No Watch Folder yet — choose the folder below and save it.";
        OnPropertyChanged(nameof(IsMonitoring));
        MonitorStatusText = IsMonitoring
            ? $"Monitoring active{(_watchFolder.CurrentPath is { } p ? $" · {p}" : string.Empty)}"
            : !configured
                ? "No Watch Folder configured — choose and save one below"
                : "Monitoring stopped — press Start Monitoring to watch the folder now";

        try
        {
            var stats = await _imports.GetStatsAsync();
            TotalCount = stats.Total;
            ImportedCount = stats.Imported;
            SkippedCount = stats.Skipped;
            FailedCount = stats.Failed;
            NoMatchCount = stats.NoMatch;
            UnsupportedCount = stats.Unsupported;
            AwaitingCount = stats.AwaitingDestination;
            ImportSummary = stats.Total == 0
                ? string.Empty
                : $"{stats.Imported} imported · {stats.NoMatch} no match · {stats.Failed} failed";

            var logs = await _imports.GetActivityAsync();

            // Update or insert rows in place so the list does not flicker.
            var byId = Activity.ToDictionary(a => a.Id);
            foreach (var log in logs)
            {
                if (byId.TryGetValue(log.Id, out var existing))
                {
                    var index = Activity.IndexOf(existing);
                    var updated = new ActivityItemViewModel(log) { Log = log };
                    Activity[index] = updated;
                    byId[log.Id] = updated;
                    if (ReferenceEquals(SelectedItem, existing))
                    {
                        SelectedItem = updated;
                    }
                }
                else
                {
                    Activity.Insert(0, new ActivityItemViewModel(log) { Log = log });
                }
            }

            while (Activity.Count > logs.Count)
            {
                Activity.RemoveAt(Activity.Count - 1);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Refreshing the Management dashboard failed.", ex);
            StatusText = $"Refresh failed: {ex.Message}";
            _activity.Publish(StatusText, ActivityKind.Error);
        }
    }

    private async Task RefreshPreviewAsync()
    {
        DestinationPreviewText = string.Empty;
        if (SelectedItem is null)
        {
            HasPreview = false;
            return;
        }

        try
        {
            var log = await _imports.GetLogAsync(SelectedItem.Id);
            if (log is null)
            {
                HasPreview = false;
                return;
            }

            var isMedia = LibraryScanner.MediaExtensions.Contains(Path.GetExtension(log.SourceFile));
            PreviewIsMedia = isMedia;
            PreviewFileName = log.SourceFile;
            PreviewSourcePath = log.SourcePath ?? "—";
            PreviewSizeText = log.FileSize is { } size ? UiFormat.FormatBytes(size) : "unknown";
            PreviewTypeText = isMedia ? (log.IsSeries ? "Series" : "Movie") : "Not a video file";
            PreviewMatchText = log.TmdbId is not null
                ? $"{log.ParsedTitle}{(log.ParsedYear is { } y ? $" ({y})" : "")} — TMDb {log.TmdbId}, confidence {log.Confidence:P0}"
                : ImportStatusLabels.For(log.Status);
            PreviewDetailText = log.ErrorDetails ?? log.Note ?? string.Empty;

            var settings = _settingsService.Load();
            var defaultFolder = _imports.GetDefaultDestinationFolder(log.IsSeries, settings);
            if (log.Status == ImportStatuses.Matched)
            {
                var preview = await _imports.PreviewImportAsync(log, destinationFolder: null);
                DestinationPreviewText = preview.DestinationFolder;
                PreviewTargetText = preview.PlannedTargetPath;
                PreviewHasTarget = true;
            }
            else
            {
                PreviewTargetText = log.TargetPath ?? string.Empty;
                PreviewHasTarget = !string.IsNullOrEmpty(PreviewTargetText);
                DestinationPreviewText = log.ChosenFolder ?? defaultFolder ?? string.Empty;
            }

            CanConfirmSelected = log.Status is ImportStatuses.Matched or ImportStatuses.Approved;
            CanSkipSelected = log.Status is ImportStatuses.Matched or ImportStatuses.Pending or ImportStatuses.NoMatch;
            HasPreview = true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Preview refresh failed: {ex.Message}");
            HasPreview = true; // Show what we have; the detail explains the rest.
            PreviewDetailText = $"Preview incomplete: {ex.Message}";
        }
    }
}
