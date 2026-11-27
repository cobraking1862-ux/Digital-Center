using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.ViewModels;

public sealed class DramaCastRowViewModel
{
    public DramaCastRowViewModel(DramaCastMember model, PosterCache posterCache)
    {
        Name = model.Name;
        Character = model.Character ?? string.Empty;
        ProfileAbsolutePath = DramaCardViewModel.ResolveArtworkPath(model.ProfilePath, posterCache);
        HasProfile = !string.IsNullOrWhiteSpace(ProfileAbsolutePath) && File.Exists(ProfileAbsolutePath);
    }

    public string Name { get; }
    public string Character { get; }
    public string? ProfileAbsolutePath { get; }
    public bool HasProfile { get; }
}

/// <summary>
/// One episode row inside a Season group. Ticking the checkbox directly adds or
/// removes the episode in the USB Cart's Dramas bucket.
/// </summary>
public sealed partial class DramaEpisodeItemViewModel : ObservableObject
{
    private readonly Func<int, bool, Task> _onCartToggled;
    private bool _suppressNotify;

    public DramaEpisodeItemViewModel(DramaEpisode model, Func<int, bool, Task> onCartToggled)
    {
        Model = model;
        _onCartToggled = onCartToggled;
        EpisodeBadge = model.EpisodeNumber > 0 ? $"Ep {model.EpisodeNumber:00}" : "Video";
        SizeText = UiFormat.FormatBytes(model.FileSize);
        CopyCountText = model.CopyCount > 0 ? $"Copied ×{model.CopyCount}" : string.Empty;
        HasCopyCount = model.CopyCount > 0;

        _suppressNotify = true;
        IsInCart = model.IsInUsbCart;
        _suppressNotify = false;
    }

    public DramaEpisode Model { get; }
    public int Id => Model.Id;
    public int SeasonNumber => Model.SeasonNumber;
    public int EpisodeNumber => Model.EpisodeNumber;
    public string EpisodeBadge { get; }
    public string Title => Model.Title;
    public string FileName => Model.FileName;
    public string FilePath => Model.FilePath;
    public string SizeText { get; }
    public bool ExistsOnDisk => Model.ExistsOnDisk;
    public string CopyCountText { get; }
    public bool HasCopyCount { get; }

    [ObservableProperty] public partial bool IsInCart { get; set; }

    partial void OnIsInCartChanged(bool value)
    {
        if (!_suppressNotify)
        {
            _ = _onCartToggled(Id, value);
        }
    }

    public void SetCheckedSilently(bool value)
    {
        _suppressNotify = true;
        IsInCart = value;
        _suppressNotify = false;
    }
}

/// <summary>One separate Season section inside DramaDetailsPage.</summary>
public sealed class DramaSeasonGroupViewModel
{
    public DramaSeasonGroupViewModel(int seasonNumber, IEnumerable<DramaEpisodeItemViewModel> episodes)
    {
        SeasonNumber = seasonNumber;
        Episodes = new ObservableCollection<DramaEpisodeItemViewModel>(episodes);
        HeaderText = $"Season {seasonNumber}";
        SummaryText = $"{Episodes.Count} episode(s) · {UiFormat.FormatBytes(Episodes.Sum(e => e.Model.FileSize))}";
    }

    public int SeasonNumber { get; }
    public string HeaderText { get; }
    public string SummaryText { get; }
    public ObservableCollection<DramaEpisodeItemViewModel> Episodes { get; }
}

/// <summary>
/// Drama Details workspace (mirrors DetailsViewModel): backdrop hero, poster,
/// vertical details, horizontal cast row, inline Edit Metadata (Title, Status,
/// TMDb ID, Overview, Poster), and separate Seasons where ticking an episode
/// checkbox adds it directly to the USB Cart's Dramas bucket.
/// </summary>
public partial class DramaDetailsViewModel : ObservableObject
{
    private readonly int _dramaId;
    private readonly DramaLibraryService _library;
    private readonly PosterCache _posterCache;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _logger;

    public event EventHandler? CloseRequested;
    public event Action? SavedChanges;

    public DramaDetailsViewModel(
        int dramaId,
        DramaLibraryService library,
        PosterCache posterCache,
        IDialogService dialogs,
        IAppLogger logger)
    {
        _dramaId = dramaId;
        _library = library;
        _posterCache = posterCache;
        _dialogs = dialogs;
        _logger = logger;

        EditableStatuses = DramaStatuses.All;
        Title = string.Empty;
        Overview = string.Empty;
        Status = DramaStatuses.Ongoing;
        Channel = string.Empty;
        Genres = string.Empty;
        Language = string.Empty;
        ImdbId = string.Empty;
        RatingText = string.Empty;
        StatsText = string.Empty;
        FolderPath = string.Empty;
        StatusText = string.Empty;
        EditTitle = string.Empty;
        EditStatus = DramaStatuses.Ongoing;
        EditTmdbId = string.Empty;
        EditOverview = string.Empty;
        EditPosterPath = string.Empty;
    }

    public IReadOnlyList<string> EditableStatuses { get; }
    public ObservableCollection<DramaCastRowViewModel> Cast { get; } = [];
    public ObservableCollection<DramaSeasonGroupViewModel> Seasons { get; } = [];

    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial int? Year { get; set; }
    [ObservableProperty] public partial string Overview { get; set; }
    [ObservableProperty] public partial string Status { get; set; }
    [ObservableProperty] public partial string Channel { get; set; }
    [ObservableProperty] public partial string Genres { get; set; }
    [ObservableProperty] public partial string Language { get; set; }
    [ObservableProperty] public partial string ImdbId { get; set; }
    [ObservableProperty] public partial long? TmdbId { get; set; }
    [ObservableProperty] public partial string RatingText { get; set; }
    [ObservableProperty] public partial string StatsText { get; set; }
    [ObservableProperty] public partial string FolderPath { get; set; }
    [ObservableProperty] public partial string? PosterAbsolutePath { get; set; }
    [ObservableProperty] public partial string? BackdropAbsolutePath { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }

    // Inline Edit Metadata
    [ObservableProperty] public partial bool IsEditing { get; set; }
    [ObservableProperty] public partial string EditTitle { get; set; }
    [ObservableProperty] public partial string EditStatus { get; set; }
    [ObservableProperty] public partial string EditTmdbId { get; set; }
    [ObservableProperty] public partial string EditOverview { get; set; }
    [ObservableProperty] public partial string EditPosterPath { get; set; }

    public bool HasPoster => !string.IsNullOrWhiteSpace(PosterAbsolutePath) && File.Exists(PosterAbsolutePath);
    public bool HasBackdrop => !string.IsNullOrWhiteSpace(BackdropAbsolutePath) && File.Exists(BackdropAbsolutePath);
    public bool HasCast => Cast.Count > 0;
    public bool HasOverview => !string.IsNullOrWhiteSpace(Overview);
    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    partial void OnPosterAbsolutePathChanged(string? value) => OnPropertyChanged(nameof(HasPoster));
    partial void OnBackdropAbsolutePathChanged(string? value) => OnPropertyChanged(nameof(HasBackdrop));
    partial void OnOverviewChanged(string value) => OnPropertyChanged(nameof(HasOverview));
    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatusText));

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var drama = await Task.Run(() => _library.GetDramaByIdAsync(_dramaId));
            if (drama is null)
            {
                StatusText = "Drama not found.";
                return;
            }

            Title = drama.Title;
            Year = drama.Year;
            Overview = drama.Overview ?? string.Empty;
            Status = drama.Status;
            Channel = drama.Channel ?? string.Empty;
            Genres = drama.Genres ?? string.Empty;
            Language = drama.Language ?? string.Empty;
            ImdbId = drama.ImdbId ?? string.Empty;
            TmdbId = drama.TmdbId;
            RatingText = drama.VoteAverage.HasValue && drama.VoteAverage.Value > 0 ? $"{drama.VoteAverage.Value:0.0}/10" : string.Empty;
            FolderPath = drama.FolderPath;
            PosterAbsolutePath = DramaCardViewModel.ResolveArtworkPath(drama.PosterPath, _posterCache);
            BackdropAbsolutePath = DramaCardViewModel.ResolveArtworkPath(drama.BackdropPath, _posterCache);

            var epSummary = drama.ExpectedEpisodeCount is > 0
                ? $"{drama.EpisodeCount} / {drama.ExpectedEpisodeCount} episodes on disk"
                : $"{drama.EpisodeCount} episode(s)";
            StatsText = $"{drama.SeasonCount} season(s) · {epSummary} · {UiFormat.FormatBytes(drama.TotalSizeBytes)}";

            var castRows = await Task.Run(() => _library.GetCastAsync(_dramaId));
            Cast.Clear();
            foreach (var c in castRows)
            {
                Cast.Add(new DramaCastRowViewModel(c, _posterCache));
            }
            OnPropertyChanged(nameof(HasCast));

            var episodes = await Task.Run(() => _library.GetEpisodesAsync(_dramaId));
            Seasons.Clear();
            foreach (var group in episodes.GroupBy(e => e.SeasonNumber).OrderBy(g => g.Key))
            {
                var items = group.Select(ep => new DramaEpisodeItemViewModel(ep, OnEpisodeCartToggledAsync));
                Seasons.Add(new DramaSeasonGroupViewModel(group.Key, items));
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Loading drama details failed.", ex);
            StatusText = $"Failed to load drama details: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OnEpisodeCartToggledAsync(int episodeId, bool inCart)
    {
        try
        {
            await _library.SetEpisodeInCartAsync(episodeId, inCart);
            StatusText = inCart
                ? "Added episode to USB Cart → Dramas bucket."
                : "Removed episode from USB Cart → Dramas bucket.";
        }
        catch (Exception ex)
        {
            _logger.Error("Toggling drama episode in USB Cart failed.", ex);
            StatusText = $"Could not update USB Cart: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ToggleAllInSeasonAsync(DramaSeasonGroupViewModel? season)
    {
        if (season is null) return;
        var available = season.Episodes.Where(e => e.ExistsOnDisk).ToList();
        if (available.Count == 0) return;

        var targetState = !available.All(e => e.IsInCart);
        foreach (var ep in available)
        {
            ep.SetCheckedSilently(targetState);
            await _library.SetEpisodeInCartAsync(ep.Id, targetState);
        }

        StatusText = targetState
            ? $"Added all {available.Count} episode(s) of {season.HeaderText} to USB Cart → Dramas."
            : $"Removed {season.HeaderText} episodes from USB Cart → Dramas.";
    }

    [RelayCommand]
    private void GoBack() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenFolder()
    {
        if (!string.IsNullOrWhiteSpace(FolderPath) && Directory.Exists(FolderPath))
        {
            _dialogs.OpenInExplorer(FolderPath);
        }
    }

    [RelayCommand]
    private void OpenEpisodeInExplorer(DramaEpisodeItemViewModel? ep)
    {
        if (ep is null) return;
        if (File.Exists(ep.FilePath))
        {
            _dialogs.OpenInExplorer(ep.FilePath);
        }
        else
        {
            _dialogs.Error("Missing File", $"File not found on disk:\n{ep.FilePath}");
        }
    }

    [RelayCommand]
    private async Task RefreshTmdbAsync()
    {
        IsBusy = true;
        StatusText = TmdbId is > 0
            ? $"Fetching TMDb TV #{TmdbId} details for '{Title}'…"
            : $"Fetching TMDb details for '{Title}'…";
        try
        {
            var matched = await Task.Run(() => _library.EnrichDramaFromTmdbAsync(_dramaId, Title));
            if (matched)
            {
                await InitializeAsync();
                StatusText = "Updated details, artwork, and cast from TMDb (manual title & status preserved).";
                SavedChanges?.Invoke();
            }
            else
            {
                StatusText = "No TMDb TV match found (check TMDb token in Settings or enter the TMDb ID in Edit Metadata).";
            }
        }
        catch (Exception ex)
        {
            _logger.Error("TMDb refresh for drama failed.", ex);
            StatusText = $"TMDb refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BeginEdit()
    {
        EditTitle = Title;
        EditStatus = Status;
        EditTmdbId = TmdbId.HasValue ? TmdbId.Value.ToString() : string.Empty;
        EditOverview = Overview;
        EditPosterPath = string.Empty;
        IsEditing = true;
    }

    [RelayCommand]
    private void PickPoster()
    {
        var picked = _dialogs.PickFile("Select Drama Poster Image", "Images (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|All files (*.*)|*.*");
        if (!string.IsNullOrWhiteSpace(picked))
        {
            EditPosterPath = picked;
        }
    }

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        var cleanedTitle = (EditTitle ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanedTitle))
        {
            _dialogs.Error("Edit Drama", "Drama title cannot be empty.");
            return;
        }

        long? parsedTmdbId = null;
        var rawTmdb = (EditTmdbId ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(rawTmdb))
        {
            if (!long.TryParse(rawTmdb, out var idVal) || idVal <= 0)
            {
                _dialogs.Error("Edit Drama", "TMDb ID must be a positive number (e.g. 217830), or leave blank.");
                return;
            }
            parsedTmdbId = idVal;
        }

        var tmdbIdChanged = parsedTmdbId != TmdbId;
        await _library.UpdateDramaMetadataAsync(_dramaId, cleanedTitle, EditStatus, parsedTmdbId, EditOverview, EditPosterPath);
        IsEditing = false;

        if (tmdbIdChanged && parsedTmdbId is > 0)
        {
            IsBusy = true;
            StatusText = $"Saved changes — fetching TMDb TV #{parsedTmdbId}…";
            try
            {
                await Task.Run(() => _library.EnrichDramaFromTmdbAsync(_dramaId, cleanedTitle));
            }
            finally
            {
                IsBusy = false;
            }
        }

        await InitializeAsync();
        StatusText = tmdbIdChanged && parsedTmdbId is > 0
            ? $"Saved drama details and synced TMDb TV #{parsedTmdbId} (manual title & status locked)."
            : "Saved drama details (manual title & status locked against TMDb overwrites).";
        SavedChanges?.Invoke();
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private async Task RemoveFromCatalogAsync()
    {
        if (!_dialogs.Confirm("Remove Drama", $"Remove '{Title}' from the catalog? Files on disk will NOT be deleted."))
        {
            return;
        }

        await _library.DeleteDramaFromCatalogAsync(_dramaId);
        SavedChanges?.Invoke();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
