using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels.Services;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.ViewModels;

/// <summary>One poster card in the USB cart Movies bucket (one row per title).</summary>
public sealed partial class CartItemViewModel : ObservableObject
{
    public CartItemViewModel(int titleId, string titleDisplay, long totalSize, int fileCount, string? posterPath)
    {
        TitleId = titleId;
        TitleDisplay = titleDisplay;
        TotalSize = totalSize;
        FileCount = fileCount;
        PosterPath = posterPath;
    }

    public int TitleId { get; }
    public string TitleDisplay { get; }
    public long TotalSize { get; }
    public int FileCount { get; }
    public string? PosterPath { get; }
    public string SizeText => UiFormat.FormatBytes(TotalSize);
    public string FilesText => FileCount == 1 ? "1 file" : $"{FileCount} files";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>One staged drama episode in the USB cart Dramas bucket.</summary>
public sealed class DramaCartEpisodeViewModel
{
    public DramaCartEpisodeViewModel(DramaEpisode episode)
    {
        Episode = episode;
        BadgeText = episode.EpisodeNumber > 0
            ? $"S{episode.SeasonNumber:00} · Ep {episode.EpisodeNumber:00}"
            : $"S{episode.SeasonNumber:00}";
        SizeText = UiFormat.FormatBytes(episode.FileSize);
    }

    public DramaEpisode Episode { get; }
    public int EpisodeId => Episode.Id;
    public string DramaTitle => Episode.DramaTitle;
    public string BadgeText { get; }
    public string Title => Episode.Title;
    public string FileName => Episode.FileName;
    public long FileSize => Episode.FileSize;
    public string SizeText { get; }
    public bool ExistsOnDisk => Episode.ExistsOnDisk;
}

/// <summary>
/// USB Cart with two dedicated buckets: Movies (whole movie titles) and Dramas
/// (individual checked drama episodes). Detects removable USB/SD drives and
/// connected Android/MTP phones, validates free space, and copies both buckets.
/// </summary>
public partial class UsbCartViewModel : ObservableObject
{
    private readonly UsbCopyService _copyService;
    private readonly DramaLibraryService _dramaLibrary;
    private readonly DramaCopyService _dramaCopyService;
    private readonly IDbContextFactory<DigitalCenter.Core.Data.DigitalCenterDbContext> _dbFactory;
    private readonly IDriveProbe _drives;
    private readonly SettingsService _settingsService;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ActivityFeedService _activityFeed;

    private CancellationTokenSource? _dramaCopyCts;

    public UsbCartViewModel(
        UsbCopyService copyService,
        DramaLibraryService dramaLibrary,
        DramaCopyService dramaCopyService,
        IDbContextFactory<DigitalCenter.Core.Data.DigitalCenterDbContext> dbFactory,
        IDriveProbe drives,
        SettingsService settingsService,
        IDialogService dialogs,
        IAppLogger logger,
        IUiDispatcher uiDispatcher,
        ActivityFeedService activityFeed)
    {
        _copyService = copyService;
        _dramaLibrary = dramaLibrary;
        _dramaCopyService = dramaCopyService;
        _dbFactory = dbFactory;
        _drives = drives;
        _settingsService = settingsService;
        _dialogs = dialogs;
        _logger = logger;
        _uiDispatcher = uiDispatcher;
        _activityFeed = activityFeed;

        SpaceEstimateText = string.Empty;
        ResultSummaryText = string.Empty;
        ProgressText = string.Empty;
        StatusText = string.Empty;
        EstimatedTimeText = string.Empty;
        ConflictOptions = ["Skip", "Overwrite", "Rename"];

        _copyService.QueueChanged += () => _uiDispatcher.Post(() => _ = RefreshCartAsync());
        _dramaLibrary.DramaCartChanged += () => _uiDispatcher.Post(() => _ = RefreshDramaBucketAsync());
    }

    /// <summary>Bucket 1: Queued movies.</summary>
    public ObservableCollection<CartItemViewModel> CartItems { get; } = [];

    /// <summary>Bucket 2: Queued drama episodes (added via checkboxes in Drama Details).</summary>
    public ObservableCollection<DramaCartEpisodeViewModel> DramaCartItems { get; } = [];

    public ObservableCollection<UsbDriveOptionViewModel> Drives { get; } = [];

    public IReadOnlyList<string> ConflictOptions { get; }

    [ObservableProperty] public partial UsbDriveOptionViewModel? SelectedDrive { get; set; }
    [ObservableProperty] public partial int ConflictIndex { get; set; }
    [ObservableProperty] public partial bool VerifyHashes { get; set; }
    [ObservableProperty] public partial bool CreatePerTitleFolders { get; set; }
    [ObservableProperty] public partial bool CleanFilenames { get; set; }
    [ObservableProperty] public partial bool IncludeFixedDrives { get; set; }
    [ObservableProperty] public partial CartItemViewModel? SelectedItem { get; set; }

    partial void OnSelectedItemChanged(CartItemViewModel? value)
    {
        foreach (var item in CartItems)
        {
            item.IsSelected = ReferenceEquals(item, value);
        }
    }

    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty] public partial string SpaceEstimateText { get; set; }
    [ObservableProperty] public partial string EstimatedTimeText { get; set; }
    [ObservableProperty] public partial string ResultSummaryText { get; set; }
    [ObservableProperty] public partial string ProgressText { get; set; }
    [ObservableProperty] public partial double OverallPercent { get; set; }

    public bool HasMovieItems => CartItems.Count > 0;
    public bool HasDramaItems => DramaCartItems.Count > 0;
    public bool HasItems => CartItems.Count > 0 || DramaCartItems.Count > 0;
    public int MovieCartCount => CartItems.Count;
    public int DramaCartCount => DramaCartItems.Count;
    public int CartCount => CartItems.Count + DramaCartItems.Count;
    public bool HasResults => ResultSummaryText.Length > 0;

    public string MoviesBucketHeader => $"Movies ({CartItems.Count})";
    public string DramasBucketHeader => $"Dramas ({DramaCartItems.Count})";

    partial void OnIncludeFixedDrivesChanged(bool value) => RefreshDrives();
    partial void OnConflictIndexChanged(int value) => UpdateSpaceEstimate();
    partial void OnSelectedDriveChanged(UsbDriveOptionViewModel? value)
    {
        StartCopyCommand.NotifyCanExecuteChanged();
        UpdateSpaceEstimate();
    }

    public void RaiseHasItemsChanged()
    {
        OnPropertyChanged(nameof(HasMovieItems));
        OnPropertyChanged(nameof(HasDramaItems));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(MovieCartCount));
        OnPropertyChanged(nameof(DramaCartCount));
        OnPropertyChanged(nameof(CartCount));
        OnPropertyChanged(nameof(MoviesBucketHeader));
        OnPropertyChanged(nameof(DramasBucketHeader));
        StartCopyCommand.NotifyCanExecuteChanged();
        ClearCartCommand.NotifyCanExecuteChanged();
        ClearDramaBucketCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Navigation hook: refresh both cart buckets + drives + settings-backed options.</summary>
    public async Task OnNavigatedToAsync()
    {
        LoadOptionsFromSettings();
        RefreshDrives();
        await RefreshCartAsync();
    }

    private void LoadOptionsFromSettings()
    {
        var settings = _settingsService.Load();
        VerifyHashes = settings.VerifyHashes;
        CreatePerTitleFolders = settings.CreatePerTitleFolders;
        CleanFilenames = settings.CleanFilenames;
        ConflictIndex = settings.UsbConflictAction switch
        {
            "Overwrite" => 1,
            "Rename" => 2,
            _ => 0
        };
    }

    private void PersistOptionsToSettings()
    {
        var settings = _settingsService.Load();
        settings.VerifyHashes = VerifyHashes;
        settings.CreatePerTitleFolders = CreatePerTitleFolders;
        settings.CleanFilenames = CleanFilenames;
        settings.UsbConflictAction = ConflictIndex switch
        {
            1 => "Overwrite",
            2 => "Rename",
            _ => "Skip"
        };
        _settingsService.Save(settings);
    }

    public async Task RefreshCartAsync()
    {
        await RefreshMoviesBucketAsync();
        await RefreshDramaBucketAsync();
    }

    private async Task RefreshMoviesBucketAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var jobs = await _copyService.GetJobsAsync();
        var queuedIds = jobs
            .Where(j => j.Status is CopyJobStatuses.Queued or CopyJobStatuses.Copying)
            .Select(j => j.MediaFileId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();

        var fileRows = await db.MediaFiles
            .AsNoTracking()
            .Where(f => queuedIds.Contains(f.Id))
            .Select(f => new
            {
                f.Id,
                f.FileSize,
                f.FilePath,
                MovieTitleId = f.MovieTitleId,
                SeriesTitleId = (int?)f.Episode!.Season.SeriesDetailsId,
                Poster = f.MovieTitle != null
                    ? f.MovieTitle.PosterPath
                    : f.Episode!.Season.SeriesDetails!.MediaTitle.PosterPath
            })
            .ToListAsync();

        var titleIds = fileRows
            .Select(f => f.MovieTitleId ?? f.SeriesTitleId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var titles = await db.MediaTitles
            .AsNoTracking()
            .Where(t => titleIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title, t.ReleaseYear })
            .ToDictionaryAsync(t => t.Id, t => t);

        var groups = fileRows
            .GroupBy(f => f.MovieTitleId ?? f.SeriesTitleId ?? 0)
            .ToList();

        CartItems.Clear();
        foreach (var group in groups)
        {
            if (!titles.TryGetValue(group.Key, out var title))
            {
                continue;
            }

            var display = title.ReleaseYear is { } year ? $"{title.Title} ({year})" : title.Title;
            CartItems.Add(new CartItemViewModel(
                title.Id,
                display,
                group.Sum(f => f.FileSize),
                group.Count(),
                group.Select(f => f.Poster).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))));
        }

        RaiseHasItemsChanged();
        UpdateSpaceEstimate();
    }

    public async Task RefreshDramaBucketAsync()
    {
        try
        {
            var episodes = await Task.Run(() => _dramaLibrary.GetDramaCartEpisodesAsync());
            DramaCartItems.Clear();
            foreach (var ep in episodes)
            {
                DramaCartItems.Add(new DramaCartEpisodeViewModel(ep));
            }

            RaiseHasItemsChanged();
            UpdateSpaceEstimate();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Refreshing USB Cart Dramas bucket failed: {ex.Message}");
        }
    }

    public void RefreshDrives()
    {
        var selected = SelectedDrive?.Root;
        Drives.Clear();
        foreach (var drive in _drives.GetCandidateDrives(IncludeFixedDrives))
        {
            Drives.Add(new UsbDriveOptionViewModel(drive));
        }

        SelectedDrive = Drives.FirstOrDefault(d => d.Root == selected) ?? Drives.FirstOrDefault();
        OnPropertyChanged(nameof(Drives));
        StartCopyCommand.NotifyCanExecuteChanged();
        UpdateSpaceEstimate();
    }

    [RelayCommand]
    private void ReloadDrives() => RefreshDrives();

    private void UpdateSpaceEstimate()
    {
        if (!HasItems)
        {
            SpaceEstimateText = string.Empty;
            EstimatedTimeText = string.Empty;
            return;
        }

        var required = CartItems.Sum(i => i.TotalSize) + DramaCartItems.Sum(d => d.FileSize);
        if (SelectedDrive is null)
        {
            SpaceEstimateText = $"Cart size: {UiFormat.FormatBytes(required)}. No removable drive or Android device detected.";
            EstimatedTimeText = string.Empty;
            return;
        }

        var free = _drives.GetFreeSpace(SelectedDrive.Root);
        SpaceEstimateText = free > 0
            ? $"{UiFormat.FormatBytes(required)} needed · {UiFormat.FormatBytes(free)} free on {SelectedDrive.TargetLabel}"
              + (required <= free ? string.Empty : " — not enough space!")
            : $"{UiFormat.FormatBytes(required)} needed on {SelectedDrive.TargetLabel}";

        long assumedBytesPerSecond = SelectedDrive.IsPortable ? 25 * 1024 * 1024 : 60 * 1024 * 1024;
        var seconds = required / assumedBytesPerSecond;
        EstimatedTimeText = seconds < 90
            ? $"≈ {seconds} s (estimate)"
            : $"≈ {seconds / 60.0:0} min (estimate)";
    }

    private bool CanStartCopy() => HasItems && SelectedDrive is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStartCopy))]
    private async Task StartCopyAsync()
    {
        if (SelectedDrive is null)
        {
            return;
        }

        PersistOptionsToSettings();

        var settings = _settingsService.Load();
        var required = CartItems.Sum(i => i.TotalSize) + DramaCartItems.Sum(d => d.FileSize);
        var free = _drives.GetFreeSpace(SelectedDrive.Root);
        if (free > 0 && required > free)
        {
            _dialogs.Error("USB Cart",
                $"Not enough space on '{SelectedDrive.TargetLabel}': {UiFormat.FormatBytes(required)} needed, {UiFormat.FormatBytes(free)} free.");
            return;
        }

        var conflictAction = ConflictIndex switch
        {
            1 => CopyConflictAction.Overwrite,
            2 => CopyConflictAction.Rename,
            _ => CopyConflictAction.Skip
        };

        IsRunning = true;
        ResultSummaryText = string.Empty;
        _dramaCopyCts = new CancellationTokenSource();
        _activityFeed.Publish($"Copying to {SelectedDrive.TargetLabel}: {CartItems.Count} movie(s), {DramaCartItems.Count} drama episode(s)");

        var summaryParts = new List<string>();
        var cancelled = false;

        var progress = new Progress<CopyProgressReport>(report =>
        {
            OverallPercent = report.TotalBytes > 0 ? report.BytesCopied * 100.0 / report.TotalBytes : 0;
            ProgressText = report.CurrentFileName is null
                ? report.Message
                : $"{report.CompletedFiles + 1}/{report.TotalFiles} · {report.CurrentFileName} ({report.CurrentFilePercent:0}%)";
        });

        try
        {
            // 1. Copy Movies bucket if non-empty
            if (CartItems.Count > 0)
            {
                var subfolder = string.IsNullOrWhiteSpace(settings.UsbCopySubfolder) ? string.Empty : settings.UsbCopySubfolder.Trim();
                var movieDest = SelectedDrive.IsPortable
                    ? (string.IsNullOrEmpty(subfolder) ? SelectedDrive.Root : $"{SelectedDrive.Root.TrimEnd('\\')}\\{subfolder.Trim('\\')}")
                    : Path.Combine(SelectedDrive.Root, subfolder);

                if (!SelectedDrive.IsPortable)
                {
                    Directory.CreateDirectory(movieDest);
                }

                var movieOptions = new CopyRunOptions(
                    movieDest,
                    conflictAction,
                    VerifyHashes ? CopyVerifyMode.SizeAndHash : CopyVerifyMode.Size);

                var movieSummary = await _copyService.RunAsync(movieOptions, progress);
                summaryParts.Add($"Movies: {movieSummary}");
                cancelled = movieSummary.WasCancelled;
            }

            // 2. Copy Dramas bucket if non-empty and not cancelled
            if (!cancelled && DramaCartItems.Count > 0)
            {
                var episodesToCopy = DramaCartItems.Select(d => d.Episode).ToList();
                var dramaSubfolder = string.IsNullOrWhiteSpace(settings.DramaCopySubfolder) ? "Dramas" : settings.DramaCopySubfolder.Trim();
                var dramaOptions = new DramaCopyRunOptions(
                    SelectedDrive.Root,
                    dramaSubfolder,
                    conflictAction,
                    CleanFilenames);

                var dramaSummary = await Task.Run(() => _dramaCopyService.CopyEpisodesAsync(
                    episodesToCopy, dramaOptions, progress, _dramaCopyCts.Token));

                summaryParts.Add($"Dramas: {dramaSummary.Completed} copied, {dramaSummary.Skipped} skipped, {dramaSummary.Failed} failed");
                cancelled = dramaSummary.WasCancelled;
            }

            ResultSummaryText = string.Join(Environment.NewLine, summaryParts);
            StatusText = cancelled ? "Copy cancelled." : "Copy finished.";
            _activityFeed.Publish(cancelled ? "USB copy cancelled" : $"USB copy finished to {SelectedDrive.TargetLabel}");
        }
        catch (Exception ex)
        {
            _logger.Error("USB copy run failed.", ex);
            _dialogs.Error("USB Cart", $"The copy run failed: {ex.Message}");
            StatusText = $"Copy failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            OverallPercent = 0;
            ProgressText = string.Empty;
            _dramaCopyCts?.Dispose();
            _dramaCopyCts = null;
            StartCopyCommand.NotifyCanExecuteChanged();
            await RefreshCartAsync();
        }
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelCopy()
    {
        _copyService.Cancel();
        _dramaCopyCts?.Cancel();
        StatusText = "Cancelling after the current chunk…";
    }

    private bool CanModifyCart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanModifyCart))]
    private async Task RemoveItemAsync(CartItemViewModel? item)
    {
        if (item is null) return;
        var removed = await RemoveTitleJobsAsync(item.TitleId);
        StatusText = removed > 0 ? $"'{item.TitleDisplay}' removed from Movies bucket." : StatusText;
        await RefreshMoviesBucketAsync();
    }

    [RelayCommand(CanExecute = nameof(CanModifyCart))]
    private async Task RemoveDramaEpisodeAsync(DramaCartEpisodeViewModel? item)
    {
        if (item is null) return;
        await _dramaLibrary.SetEpisodeInCartAsync(item.EpisodeId, false);
        StatusText = $"Removed '{item.DramaTitle} · {item.BadgeText}' from Dramas bucket.";
    }

    [RelayCommand(CanExecute = nameof(CanModifyCart))]
    private async Task ClearDramaBucketAsync()
    {
        if (DramaCartItems.Count == 0) return;
        if (!_dialogs.Confirm("USB Cart — Dramas", $"Remove all {DramaCartItems.Count} drama episode(s) from the Dramas bucket?"))
        {
            return;
        }

        await _dramaLibrary.ClearDramaCartAsync();
        StatusText = "Dramas bucket cleared.";
    }

    [RelayCommand(CanExecute = nameof(CanModifyCart))]
    private async Task ClearCartAsync()
    {
        if (!HasItems) return;

        if (!_dialogs.Confirm("USB Cart", $"Clear all {CartItems.Count} movie(s) and {DramaCartItems.Count} drama episode(s) from the USB cart?"))
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var queued = await db.CopyJobs
            .Where(j => j.Status == CopyJobStatuses.Queued)
            .ToListAsync();
        db.CopyJobs.RemoveRange(queued);
        await db.SaveChangesAsync();

        await _dramaLibrary.ClearDramaCartAsync();
        StatusText = "Cart cleared.";
        _activityFeed.Publish("USB cart cleared");
        await RefreshCartAsync();
    }

    private async Task<int> RemoveTitleJobsAsync(int titleId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.MediaFiles
            .Where(f => (f.MovieTitleId != null && f.MovieTitleId == titleId) ||
                        (f.EpisodeId != null && f.Episode!.Season.SeriesDetailsId == titleId))
            .Select(f => new { f.Id, f.FilePath })
            .ToListAsync();

        var ids = rows.Select(r => r.Id).ToList();
        var paths = rows.Select(r => r.FilePath).ToList();

        var jobs = await db.CopyJobs
            .Where(j => j.Status == CopyJobStatuses.Queued &&
                        (j.MediaFileId != null && ids.Contains(j.MediaFileId.Value)))
            .ToListAsync();

        var byPath = await db.CopyJobs
            .Where(j => j.Status == CopyJobStatuses.Queued && paths.Contains(j.SourcePath))
            .ToListAsync();

        var all = jobs.Concat(byPath).Distinct().ToList();
        db.CopyJobs.RemoveRange(all);
        await db.SaveChangesAsync();
        return all.Count;
    }
}

/// <summary>One selectable destination drive or portable device in the USB cart.</summary>
public sealed class UsbDriveOptionViewModel
{
    public UsbDriveOptionViewModel(UsbDriveOption option)
    {
        Option = option;
    }

    public UsbDriveOption Option { get; }
    public string Root => Option.Root;
    public string Name => Option.Name;
    public string DriveTypeDisplay => Option.DriveTypeDisplay;
    public bool IsPortable => Option.Root.StartsWith("mtp://", StringComparison.OrdinalIgnoreCase);
    public string TargetLabel => IsPortable ? Option.Name : Option.Root;

    public double UsedFraction => Option.TotalSpace <= 0
        ? 0
        : Math.Clamp((Option.TotalSpace - Option.FreeSpace) / (double)Option.TotalSpace, 0, 1);

    public double UsedPercent => UsedFraction * 100.0;

    public string CapacityText => Option.TotalSpace > 0
        ? $"{UiFormat.FormatBytes(Option.FreeSpace)} free of {UiFormat.FormatBytes(Option.TotalSpace)}"
        : "Connected (unlock phone in File Transfer mode to view free space)";

    public string Display => IsPortable
        ? $"{Option.Name} ({Option.DriveTypeDisplay}), {CapacityText}"
        : $"{Option.Root} — {Option.Name} ({Option.DriveTypeDisplay}), {CapacityText}";

    public override string ToString() => Display;
}
