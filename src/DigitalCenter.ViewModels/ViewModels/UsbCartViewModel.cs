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

/// <summary>One poster card in the USB cart (one row per title).</summary>
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

    /// <summary>Visual selection state for the card highlight (UI only).</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// USB Cart: the titles selected for copying, shown as poster cards with
/// their real file sizes. Detects removable drives, validates free space,
/// and runs the existing copy engine with progress, cancellation, explicit
/// conflict handling and verification. No drive letter, capacity or result is
/// ever hard-coded.
/// </summary>
public partial class UsbCartViewModel : ObservableObject
{
    private readonly UsbCopyService _copyService;
    private readonly IDbContextFactory<DigitalCenter.Core.Data.DigitalCenterDbContext> _dbFactory;
    private readonly IDriveProbe _drives;
    private readonly SettingsService _settingsService;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly IUiDispatcher _uiDispatcher;

    public UsbCartViewModel(
        UsbCopyService copyService,
        IDbContextFactory<DigitalCenter.Core.Data.DigitalCenterDbContext> dbFactory,
        IDriveProbe drives,
        SettingsService settingsService,
        IDialogService dialogs,
        IAppLogger logger,
        IUiDispatcher uiDispatcher)
    {
        _copyService = copyService;
        _dbFactory = dbFactory;
        _drives = drives;
        _settingsService = settingsService;
        _dialogs = dialogs;
        _logger = logger;
        _uiDispatcher = uiDispatcher;

        SpaceEstimateText = string.Empty;
        ResultSummaryText = string.Empty;
        ProgressText = string.Empty;
        StatusText = string.Empty;
        EstimatedTimeText = string.Empty;
        ConflictOptions = ["Skip", "Overwrite", "Rename"];

        _copyService.QueueChanged += () => _uiDispatcher.Post(() => _ = RefreshCartAsync());
    }

    public ObservableCollection<CartItemViewModel> CartItems { get; } = [];

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

    public bool HasItems => CartItems.Count > 0;
    public int CartCount => CartItems.Count;
    public bool HasResults => ResultSummaryText.Length > 0;

    partial void OnIncludeFixedDrivesChanged(bool value) => RefreshDrives();
    partial void OnConflictIndexChanged(int value) => UpdateSpaceEstimate();

    public void RaiseHasItemsChanged()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CartCount));
        StartCopyCommand.NotifyCanExecuteChanged();
        ClearCartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Navigation hook: refresh cart + drives + settings-backed options.</summary>
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
        await using var db = await _dbFactory.CreateDbContextAsync();

        var jobs = await _copyService.GetJobsAsync();
        var queuedIds = jobs
            .Where(j => j.Status is CopyJobStatuses.Queued or CopyJobStatuses.Copying)
            .Select(j => j.MediaFileId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();

        // Resolve each queued file back to its owning title for the poster cards.
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
        if (CartItems.Count == 0)
        {
            SpaceEstimateText = string.Empty;
            EstimatedTimeText = string.Empty;
            return;
        }

        var required = CartItems.Sum(i => i.TotalSize);
        if (SelectedDrive is null)
        {
            SpaceEstimateText = $"Cart size: {UiFormat.FormatBytes(required)}. No removable drive detected.";
            EstimatedTimeText = string.Empty;
            return;
        }

        var free = _drives.GetFreeSpace(SelectedDrive.Root);
        SpaceEstimateText = $"{UiFormat.FormatBytes(required)} needed · {UiFormat.FormatBytes(free)} free on {SelectedDrive.Root}"
                            + (required <= free ? string.Empty : " — not enough space!");

        // Rough transfer estimate (labelled as an estimate in the UI): most
        // USB 3 drives sustain 60-100 MB/s; we assume 60 MB/s unless a USB
        // benchmark setting exists in a future version.
        const long assumedBytesPerSecond = 60 * 1024 * 1024;
        var seconds = required / assumedBytesPerSecond;
        EstimatedTimeText = seconds < 90
            ? $"≈ {seconds} s (estimate)"
            : $"≈ {seconds / 60.0:0} min (estimate)";
    }

    private bool CanStartCopy() => CartItems.Count > 0 && SelectedDrive is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStartCopy))]
    private async Task StartCopyAsync()
    {
        if (SelectedDrive is null)
        {
            return;
        }

        PersistOptionsToSettings();

        var settings = _settingsService.Load();
        var destinationRoot = Path.Combine(SelectedDrive.Root, string.IsNullOrWhiteSpace(settings.UsbCopySubfolder) ? string.Empty : settings.UsbCopySubfolder);
        try
        {
            Directory.CreateDirectory(destinationRoot);
        }
        catch (Exception ex)
        {
            _dialogs.Error("USB Cart", $"The destination folder could not be created: {ex.Message}");
            return;
        }

        var required = CartItems.Sum(i => i.TotalSize);
        var free = _drives.GetFreeSpace(SelectedDrive.Root);
        if (required > free)
        {
            _dialogs.Error("USB Cart",
                $"Not enough space on '{SelectedDrive.Root}': {UiFormat.FormatBytes(required)} needed, {UiFormat.FormatBytes(free)} free.");
            return;
        }

        IsRunning = true;
        ResultSummaryText = string.Empty;
        var progress = new Progress<CopyProgressReport>(report =>
        {
            OverallPercent = report.TotalBytes > 0 ? report.BytesCopied * 100.0 / report.TotalBytes : 0;
            ProgressText = report.CurrentFileName is null
                ? report.Message
                : $"{report.CompletedFiles + 1}/{report.TotalFiles} · {report.CurrentFileName} ({report.CurrentFilePercent:0}%)";
        });

        try
        {
            var options = new CopyRunOptions(
                destinationRoot,
                ConflictIndex switch
                {
                    1 => CopyConflictAction.Overwrite,
                    2 => CopyConflictAction.Rename,
                    _ => CopyConflictAction.Skip
                },
                VerifyHashes ? CopyVerifyMode.SizeAndHash : CopyVerifyMode.Size);

            var summary = await _copyService.RunAsync(options, progress);
            ResultSummaryText = summary.ToString();
            StatusText = summary.WasCancelled ? "Copy cancelled." : "Copy finished.";
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
            StartCopyCommand.NotifyCanExecuteChanged();
            await RefreshCartAsync();
        }
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelCopy()
    {
        _copyService.Cancel();
        StatusText = "Cancelling after the current chunk…";
    }

    private bool CanModifyCart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanModifyCart))]
    private async Task RemoveItemAsync(CartItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var removed = await RemoveTitleJobsAsync(item.TitleId);
        StatusText = removed > 0 ? $"'{item.TitleDisplay}' removed from the cart." : StatusText;
        await RefreshCartAsync();
    }

    [RelayCommand(CanExecute = nameof(CanModifyCart))]
    private async Task ClearCartAsync()
    {
        if (CartItems.Count == 0)
        {
            return;
        }

        if (!_dialogs.Confirm("USB Cart", $"Remove all {CartItems.Count} title(s) from the cart?"))
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var queued = await db.CopyJobs
            .Where(j => j.Status == CopyJobStatuses.Queued)
            .ToListAsync();
        db.CopyJobs.RemoveRange(queued);
        await db.SaveChangesAsync();

        StatusText = "Cart cleared.";
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

/// <summary>One selectable destination drive in the USB cart.</summary>
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

    /// <summary>0..1 fraction of the drive already in use (for the capacity bar).</summary>
    public double UsedFraction => Option.TotalSpace <= 0
        ? 0
        : Math.Clamp((Option.TotalSpace - Option.FreeSpace) / (double)Option.TotalSpace, 0, 1);

    /// <summary>0..100 percentage of the drive already in use.</summary>
    public double UsedPercent => UsedFraction * 100.0;

    public string CapacityText =>
        $"{UiFormat.FormatBytes(Option.FreeSpace)} free of {UiFormat.FormatBytes(Option.TotalSpace)}";

    public string Display =>
        $"{Option.Root} — {Option.Name} ({Option.DriveTypeDisplay}), {UiFormat.FormatBytes(Option.FreeSpace)} free of {UiFormat.FormatBytes(Option.TotalSpace)}";

    public override string ToString() => Display;
}
