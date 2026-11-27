using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.ViewModels;

/// <summary>One poster card in the Drama Catalog grid (mirrors LibraryItemViewModel).</summary>
public sealed class DramaCardViewModel
{
    public DramaCardViewModel(DramaSeries model, PosterCache posterCache)
    {
        Model = model;
        PosterAbsolutePath = ResolveArtworkPath(model.PosterPath, posterCache);
        HasPoster = !string.IsNullOrWhiteSpace(PosterAbsolutePath) && File.Exists(PosterAbsolutePath);
        AllOnDisk = model.EpisodeCount > 0 && model.AvailableEpisodeCount == model.EpisodeCount;
        EpisodeCountText = model.ExpectedEpisodeCount is > 0
            ? $"{model.EpisodeCount} / {model.ExpectedEpisodeCount} eps"
            : (model.EpisodeCount == 1 ? "1 ep" : $"{model.EpisodeCount} eps");
        SeasonBadgeText = model.SeasonCount > 1 ? $"{model.SeasonCount} seasons" : model.Status;
        SubtitleText = model.Year.HasValue
            ? $"{model.Year} · {EpisodeCountText} · {UiFormat.FormatBytes(model.TotalSizeBytes)}"
            : $"{EpisodeCountText} · {UiFormat.FormatBytes(model.TotalSizeBytes)}";
    }

    public DramaSeries Model { get; }
    public int Id => Model.Id;
    public string Title => Model.Title;
    public string Status => Model.Status;
    public string? PosterAbsolutePath { get; }
    public bool HasPoster { get; }
    public bool AllOnDisk { get; }
    public string EpisodeCountText { get; }
    public string SeasonBadgeText { get; }
    public string SubtitleText { get; }

    internal static string? ResolveArtworkPath(string? storedPath, PosterCache posterCache)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return null;
        }

        if (Path.IsPathRooted(storedPath))
        {
            return storedPath;
        }

        return posterCache.GetAbsolutePath(storedPath);
    }
}

/// <summary>
/// Drama Catalog workspace: clean Movies-style search bar, inline status pill row
/// (All, Ongoing, Completed, On Hold), and poster grid sorted cleanly A → Z.
/// Scanning and Watch Folder checks live in Maintenance and Watch Folder.
/// </summary>
public partial class DramaViewModel : ObservableObject
{
    private readonly DramaLibraryService _library;
    private readonly PosterCache _posterCache;
    private readonly IAppLogger _logger;

    private bool _suppressReload;

    public event Action<int>? DetailsRequested;

    public DramaViewModel(
        DramaLibraryService library,
        PosterCache posterCache,
        IAppLogger logger)
    {
        _library = library;
        _posterCache = posterCache;
        _logger = logger;

        _suppressReload = true;
        try
        {
            SearchText = string.Empty;
            SelectedStatusFilter = "All";
            TotalMatchesText = string.Empty;
            StatusText = string.Empty;
        }
        finally
        {
            _suppressReload = false;
        }
    }

    public ObservableCollection<DramaCardViewModel> Dramas { get; } = [];

    [ObservableProperty] public partial string SearchText { get; set; }
    [ObservableProperty] public partial string SelectedStatusFilter { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string TotalMatchesText { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }

    public bool HasNoDramas => !IsBusy && Dramas.Count == 0;
    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    public bool IsAllStatus
    {
        get => string.Equals(SelectedStatusFilter, "All", StringComparison.OrdinalIgnoreCase);
        set { if (value) SelectedStatusFilter = "All"; }
    }

    public bool IsOngoingStatus
    {
        get => string.Equals(SelectedStatusFilter, DramaStatuses.Ongoing, StringComparison.OrdinalIgnoreCase);
        set { if (value) SelectedStatusFilter = DramaStatuses.Ongoing; }
    }

    public bool IsCompletedStatus
    {
        get => string.Equals(SelectedStatusFilter, DramaStatuses.Completed, StringComparison.OrdinalIgnoreCase);
        set { if (value) SelectedStatusFilter = DramaStatuses.Completed; }
    }

    public bool IsOnHoldStatus
    {
        get => string.Equals(SelectedStatusFilter, DramaStatuses.OnHold, StringComparison.OrdinalIgnoreCase);
        set { if (value) SelectedStatusFilter = DramaStatuses.OnHold; }
    }

    partial void OnSearchTextChanged(string value)
    {
        if (!_suppressReload) _ = RefreshSafeAsync();
    }

    partial void OnSelectedStatusFilterChanged(string value)
    {
        OnPropertyChanged(nameof(IsAllStatus));
        OnPropertyChanged(nameof(IsOngoingStatus));
        OnPropertyChanged(nameof(IsCompletedStatus));
        OnPropertyChanged(nameof(IsOnHoldStatus));
        if (!_suppressReload) _ = RefreshSafeAsync();
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(HasNoDramas));
    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatusText));

    public Task OnNavigatedToAsync() => RefreshSafeAsync();

    [RelayCommand]
    public async Task RefreshSafeAsync()
    {
        try
        {
            var search = SearchText;
            var status = SelectedStatusFilter;

            var list = await Task.Run(() => _library.GetDramasAsync(search, status, sortIndex: 0));
            Dramas.Clear();
            foreach (var d in list)
            {
                Dramas.Add(new DramaCardViewModel(d, _posterCache));
            }

            TotalMatchesText = Dramas.Count == 1 ? "1 drama" : $"{Dramas.Count} dramas";
            OnPropertyChanged(nameof(HasNoDramas));
        }
        catch (Exception ex)
        {
            _logger.Error("Loading Drama catalog failed.", ex);
            StatusText = $"Could not load dramas: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenDetails(DramaCardViewModel? card)
    {
        if (card is not null && card.Id > 0)
        {
            DetailsRequested?.Invoke(card.Id);
        }
    }
}
