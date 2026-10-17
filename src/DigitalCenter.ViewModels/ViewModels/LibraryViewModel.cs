using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.ViewModels;

/// <summary>The four top-navigation destinations of the main library.</summary>
public enum LibraryTab
{
    Movies,
    Series,
    Collections,
    Genres
}

/// <summary>
/// Home screen: grid-only library with portrait poster cards. The top
/// navigation selects Movies / Series / Collections / Genres; the subbar
/// offers year, language and sorting; the search box on the right is the only
/// other header control.
///
/// Card interaction is split in two: a single click selects the card
/// (<see cref="SelectItemCommand"/>), a double click — or Enter/Space on a
/// focused card — raises <see cref="DetailsRequested"/> and opens the details
/// screen. Works identically for movies and series.
/// </summary>
public partial class LibraryViewModel : ObservableObject
{
    private readonly LibraryQueryService _queryService;
    private readonly UsbCopyService _copyService;
    private readonly SettingsService _settingsService;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _logger;

    public LibraryViewModel(
        LibraryQueryService queryService,
        UsbCopyService copyService,
        SettingsService settingsService,
        IDialogService dialogs,
        IAppLogger logger)
    {
        _queryService = queryService;
        _copyService = copyService;
        _settingsService = settingsService;
        _dialogs = dialogs;
        _logger = logger;

        SearchText = string.Empty;
        TotalMatchesText = string.Empty;
        StatusText = string.Empty;
        SortOptionsSource =
        [
            "Title A → Z",
            "Title Z → A",
            "Year (newest)",
            "Year (oldest)",
            "Recently added"
        ];

        YearOptions = ["All years"];
        LanguageOptions = ["All languages"];
    }

    public event Action<int>? DetailsRequested;

    /// <summary>Raised after titles were added to the USB cart (shell refreshes the cart).</summary>
    public event Action? CartChanged;

    public IReadOnlyList<string> SortOptionsSource { get; }

    public ObservableCollection<LibraryItemViewModel> Items { get; } = [];

    public ObservableCollection<string> YearOptions { get; }

    public ObservableCollection<string> LanguageOptions { get; }

    /// <summary>Genre chips shown on the Genres tab (name + count label).</summary>
    public ObservableCollection<GenreChipViewModel> GenreChips { get; } = [];

    [ObservableProperty]
    public partial LibraryTab SelectedTab { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    public partial string SelectedYear { get; set; }

    [ObservableProperty]
    public partial string SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial int SortIndex { get; set; }

    [ObservableProperty]
    public partial string? SelectedGenre { get; set; }

    [ObservableProperty]
    public partial string TotalMatchesText { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial LibraryItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string LoadError { get; set; } = string.Empty;

    public bool HasNoItems => !IsLoading && Items.Count == 0 && LoadError.Length == 0;

    public bool IsCollectionsTab => SelectedTab == LibraryTab.Collections;

    public bool IsGenresTab => SelectedTab == LibraryTab.Genres;

    public bool HasGenres => GenreChips.Count > 0;

    private CancellationTokenSource? _debounce;

    partial void OnSearchTextChanged(string value)
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        _ = DebouncedRefreshAsync(_debounce.Token);
    }

    partial void OnSelectedTabChanged(LibraryTab value)
    {
        if (value == LibraryTab.Genres && GenreChips.Count == 0)
        {
            _ = LoadGenreChipsAsync();
        }

        OnPropertyChanged(nameof(IsCollectionsTab));
        OnPropertyChanged(nameof(IsGenresTab));
        OnPropertyChanged(nameof(HasGenres));
        _ = RefreshSafeAsync();
    }

    partial void OnSelectedYearChanged(string value) => _ = RefreshSafeAsync();

    partial void OnSelectedLanguageChanged(string value) => _ = RefreshSafeAsync();

    partial void OnSortIndexChanged(int value) => _ = RefreshSafeAsync();

    partial void OnSelectedGenreChanged(string? value) => _ = RefreshSafeAsync();

    /// <summary>Keeps exactly one card selected and mirrors the state onto the rows.</summary>
    partial void OnSelectedItemChanged(LibraryItemViewModel? value)
    {
        foreach (var item in Items)
        {
            item.IsSelected = ReferenceEquals(item, value);
        }
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoItems));
    }

    partial void OnLoadErrorChanged(string value) => OnPropertyChanged(nameof(HasNoItems));

    private async Task DebouncedRefreshAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(350, ct);
            await RefreshCoreAsync();
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke replaced this pending refresh.
        }
    }

    public async Task RefreshSafeAsync()
    {
        try
        {
            LoadError = string.Empty;
            await RefreshCoreAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Loading the library failed.", ex);
            LoadError = ex.Message;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => RefreshSafeAsync();

    private async Task LoadGenreChipsAsync()
    {
        var genres = await _queryService.GetGenreCountsAsync(KindOrNull());
        SetGenreChips(genres);
    }

    private async Task RefreshCoreAsync()
    {
        if (IsCollectionsTab)
        {
            Items.Clear();
            TotalMatchesText = string.Empty;
            return; // The view shows the collections notice; no grid content.
        }

        var filter = BuildFilter();
        IsLoading = true;
        try
        {
            var page = await _queryService.QueryAsync(filter);

            // Rebuilding the grid must not throw away the selection: keep it by
            // title id so a background refresh cannot deselect the open card.
            var selectedId = SelectedItem?.Id;

            Items.Clear();
            foreach (var row in page.Items)
            {
                Items.Add(new LibraryItemViewModel(row));
            }

            SelectedItem = selectedId is { } id ? Items.FirstOrDefault(item => item.Id == id) : null;

            TotalMatchesText = page.TotalMatches == page.Items.Count
                ? $"{page.TotalMatches} title(s)"
                : $"{page.TotalMatches} matching title(s), showing first {page.Items.Count}";

            await EnsureFilterOptionsAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task EnsureFilterOptionsAsync()
    {
        if (YearOptions.Count == 1 && LanguageOptions.Count == 1)
        {
            var years = await _queryService.GetDistinctYearsAsync(KindOrNull());
            var languages = await _queryService.GetDistinctLanguagesAsync(KindOrNull());

            foreach (var year in years)
            {
                YearOptions.Add(year.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            foreach (var language in languages)
            {
                LanguageOptions.Add(language);
            }
        }
    }

    private MediaKind? KindOrNull() => SelectedTab switch
    {
        LibraryTab.Movies => MediaKind.Movie,
        LibraryTab.Series => MediaKind.Series,
        _ => null
    };

    private LibraryFilter BuildFilter()
    {
        var (sortBy, descending) = SortIndex switch
        {
            1 => ("Title", true),
            2 => ("Year", true),
            3 => ("Year", false),
            4 => ("Created", true),
            _ => ("Title", false)
        };

        int? yearFrom = null, yearTo = null;
        if (SelectedYear is not null && SelectedYear != "All years" &&
            int.TryParse(SelectedYear, out var parsedYear))
        {
            yearFrom = parsedYear;
            yearTo = parsedYear;
        }

        var language = SelectedLanguage is null || SelectedLanguage == "All languages" ? null : SelectedLanguage;

        return new LibraryFilter
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            Type = KindOrNull(),
            YearFrom = yearFrom,
            YearTo = yearTo,
            Language = language,
            Genre = SelectedGenre,
            SortBy = sortBy,
            SortDescending = descending
        };
    }

    /// <summary>Selects a card (single click). Does not navigate.</summary>
    [RelayCommand]
    private void SelectItem(LibraryItemViewModel? item) => SelectedItem = item;

    /// <summary>Opens the details screen for a card (double click, Enter or Space).</summary>
    [RelayCommand]
    private void OpenDetails(LibraryItemViewModel? item)
    {
        if (item is null || item.Id <= 0)
        {
            return;
        }

        SelectedItem = item;
        DetailsRequested?.Invoke(item.Id);
    }

    [RelayCommand]
    private async Task AddToCartAsync(LibraryItemViewModel? item)
    {
        if (item is null || item.Id <= 0)
        {
            return;
        }

        var settings = _settingsService.Load();
        try
        {
            var result = await _copyService.EnqueueTitlesAsync(
                [item.Id],
                settings.MoviesRoot,
                settings.SeriesRoot,
                settings.CreatePerTitleFolders,
                settings.CleanFilenames);

            StatusText = result.Queued > 0
                ? $"'{item.Title}' added to the USB cart."
                : result.AlreadyQueued > 0
                    ? $"'{item.Title}' is already in the USB cart."
                    : $"'{item.Title}' has no registered files to copy.";

            CartChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error("Adding a title to the USB cart failed.", ex);
            _dialogs.Error("USB Cart", $"The title could not be added to the cart: {ex.Message}");
        }
    }

    /// <summary>Called by the view after the genre chips were loaded from the database.</summary>
    public void SetGenreChips(IEnumerable<(string Name, int Count)> genres)
    {
        GenreChips.Clear();
        foreach (var (name, count) in genres)
        {
            var chip = new GenreChipViewModel(name, count);
            chip.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GenreChipViewModel.IsSelected))
                {
                    SelectedGenre = chip.IsSelected ? chip.Name : null;
                }
            };
            GenreChips.Add(chip);
        }

        OnPropertyChanged(nameof(HasGenres));
    }
}

/// <summary>A selectable genre chip on the Genres tab.</summary>
public sealed partial class GenreChipViewModel(string name, int count) : ObservableObject
{
    public string Name { get; } = name;

    public int Count { get; } = count;

    public string Display => Count > 0 ? $"{Name}  ({Count})" : Name;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
