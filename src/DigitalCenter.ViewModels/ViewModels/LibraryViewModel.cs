using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.ViewModels;

/// <summary>The categories of the library screen.</summary>
public enum LibraryTab
{
    /// <summary>
    /// The Home dashboard: Movies and TV Series are loaded and shown together,
    /// so landing on Home displays the collection without touching a category
    /// button first.
    /// </summary>
    All,

    Movies,
    Series,
    Genres
}

/// <summary>
/// Home screen: grid-only library with portrait poster cards. The top
/// navigation selects Movies / Series / Genres; the subbar
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
    private readonly ActivityFeed _activity;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _logger;

    public LibraryViewModel(
        LibraryQueryService queryService,
        UsbCopyService copyService,
        SettingsService settingsService,
        ActivityFeed activity,
        IDialogService dialogs,
        IAppLogger logger)
    {
        _queryService = queryService;
        _copyService = copyService;
        _settingsService = settingsService;
        _activity = activity;
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

    /// <summary>Movies shown on the Home dashboard (newest first, capped).</summary>
    public ObservableCollection<LibraryItemViewModel> HomeMovies { get; } = [];

    /// <summary>TV Series shown on the Home dashboard (newest first, capped).</summary>
    public ObservableCollection<LibraryItemViewModel> HomeSeries { get; } = [];

    public ObservableCollection<string> YearOptions { get; }

    public ObservableCollection<string> LanguageOptions { get; }

    /// <summary>Genre chips shown on the Genres tab (name + count label).</summary>
    public ObservableCollection<GenreChipViewModel> GenreChips { get; } = [];

    [ObservableProperty]
    public partial LibraryTab SelectedTab { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; }

    // Initialized here (not in the constructor) so the combos start on their
    // "all" entry instead of showing an empty selection, and so no change
    // callback fires while the view model is still being built.
    [ObservableProperty]
    public partial string SelectedYear { get; set; } = "All years";

    [ObservableProperty]
    public partial string SelectedLanguage { get; set; } = "All languages";

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
    public partial int HomeMovieTotal { get; set; }

    [ObservableProperty]
    public partial int HomeSeriesTotal { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string LoadError { get; set; } = string.Empty;

    public bool HasNoItems => !IsLoading && Items.Count == 0 && !HasHomeItems && LoadError.Length == 0;

    /// <summary>True on the Home dashboard (Movies + TV Series shown together).</summary>
    public bool IsHomeTab => SelectedTab == LibraryTab.All;

    public bool HasHomeMovies => HomeMovies.Count > 0;

    public bool HasHomeSeries => HomeSeries.Count > 0;

    public bool HasHomeItems => HomeMovies.Count > 0 || HomeSeries.Count > 0;

    /// <summary>How many movies the dashboard shows out of the matching total.</summary>
    public string HomeMovieSummary => HomeMovieTotal <= HomeMovies.Count
        ? $"{HomeMovieTotal} title(s)"
        : $"{HomeMovies.Count} of {HomeMovieTotal} title(s)";

    public string HomeSeriesSummary => HomeSeriesTotal <= HomeSeries.Count
        ? $"{HomeSeriesTotal} title(s)"
        : $"{HomeSeries.Count} of {HomeSeriesTotal} title(s)";

    /// <summary>The poster grid is shown on every tab except the Home dashboard.</summary>
    public bool IsCategoryGridVisible => !IsHomeTab;

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

        OnPropertyChanged(nameof(IsHomeTab));
        OnPropertyChanged(nameof(IsCategoryGridVisible));
        OnPropertyChanged(nameof(IsGenresTab));
        OnPropertyChanged(nameof(HasGenres));
        _activity.Publish($"Browsing {TabLabel(value)}.", ActivityKind.Navigation);
        _ = RefreshSafeAsync();
    }

    /// <summary>The wording used in the activity feed for a category.</summary>
    private static string TabLabel(LibraryTab tab) => tab switch
    {
        LibraryTab.Movies => "Movies",
        LibraryTab.Series => "TV Series",
        LibraryTab.Genres => "genres",
        _ => "the Home dashboard"
    };

    partial void OnSelectedYearChanged(string value) => _ = RefreshSafeAsync();

    partial void OnSelectedLanguageChanged(string value) => _ = RefreshSafeAsync();

    partial void OnSortIndexChanged(int value) => _ = RefreshSafeAsync();

    partial void OnSelectedGenreChanged(string? value) => _ = RefreshSafeAsync();

    /// <summary>
    /// Keeps exactly one card selected and mirrors the state onto the rows —
    /// including the two Home dashboard sections, whose cards are separate
    /// instances of the same titles.
    /// </summary>
    partial void OnSelectedItemChanged(LibraryItemViewModel? value)
    {
        foreach (var item in Items)
        {
            item.IsSelected = ReferenceEquals(item, value);
        }

        foreach (var item in HomeMovies)
        {
            item.IsSelected = value is not null && item.Id == value.Id;
        }

        foreach (var item in HomeSeries)
        {
            item.IsSelected = value is not null && item.Id == value.Id;
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
            var term = SearchText?.Trim();
            _activity.Publish(
                term is { Length: > 0 } ? $"Searching the library for “{term}”." : "Search cleared.",
                ActivityKind.Navigation);
            await RefreshSafeAsync();
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke replaced this pending refresh.
        }
    }

    private bool _refreshRunning;
    private bool _refreshQueued;

    /// <summary>
    /// The single entry point for a library reload. Overlapping reloads are
    /// coalesced instead of running at the same time: two passes clearing and
    /// refilling <see cref="Items"/> concurrently is what produced doubled rows,
    /// a lost selection and duplicated year/language filter entries.
    /// </summary>
    public async Task RefreshSafeAsync()
    {
        if (_refreshRunning)
        {
            _refreshQueued = true;
            return;
        }

        _refreshRunning = true;
        try
        {
            do
            {
                _refreshQueued = false;
                LoadError = string.Empty;
                await RefreshCoreAsync();
            }
            while (_refreshQueued);
        }
        catch (Exception ex)
        {
            _logger.Error("Loading the library failed.", ex);
            LoadError = ex.Message;
            _activity.Publish($"Loading the library failed: {ex.Message}", ActivityKind.Error);
        }
        finally
        {
            _refreshRunning = false;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => RefreshSafeAsync();

    private async Task LoadGenreChipsAsync()
    {
        var genres = await _queryService.GetGenreCountsAsync(KindOrNull());
        SetGenreChips(genres);
    }

    /// <summary>How many titles each Home dashboard section shows.</summary>
    private const int HomeSectionSize = 12;

    private async Task RefreshCoreAsync()
    {
        if (IsHomeTab)
        {
            // The dashboard loads BOTH categories in one go: landing on Home
            // shows Movies and TV Series immediately, no category click needed.
            Items.Clear();
            await LoadHomeSectionsAsync();
            return;
        }

        HomeMovies.Clear();
        HomeSeries.Clear();
        RaiseHomeState();

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

    /// <summary>The category the year/language options were loaded for.</summary>
    private MediaKind? _optionsKind;

    private bool _optionsLoaded;

    /// <summary>
    /// Fills the year and language filters for the CURRENT category. They used
    /// to be loaded once and then never again, so switching between Movies and
    /// Series kept offering the other category's years and languages (stale
    /// filter options that could only produce an empty grid).
    /// </summary>
    /// <summary>
    /// Fills the two Home dashboard sections with the current search/filter/sort
    /// applied to each category, newest first, capped at
    /// <see cref="HomeSectionSize"/> so the dashboard stays scannable (each
    /// section header offers "Show all").
    /// </summary>
    private async Task LoadHomeSectionsAsync()
    {
        IsLoading = true;
        try
        {
            var filter = BuildFilter() with { SortBy = "Created", SortDescending = true, MaxResults = HomeSectionSize };
            var moviesTask = _queryService.QueryAsync(filter with { Type = MediaKind.Movie });
            var seriesTask = _queryService.QueryAsync(filter with { Type = MediaKind.Series });
            await Task.WhenAll(moviesTask, seriesTask);

            var movies = moviesTask.Result;
            var series = seriesTask.Result;

            // Keep the current selection across the rebuild (by title id).
            var selectedId = SelectedItem?.Id;

            Fill(HomeMovies, movies.Items);
            Fill(HomeSeries, series.Items);

            HomeMovieTotal = movies.TotalMatches;
            HomeSeriesTotal = series.TotalMatches;
            OnPropertyChanged(nameof(HomeMovieSummary));
            OnPropertyChanged(nameof(HomeSeriesSummary));

            TotalMatchesText = $"{movies.TotalMatches} movie(s) · {series.TotalMatches} series";

            SelectedItem = selectedId is { } id
                ? HomeMovies.FirstOrDefault(item => item.Id == id) ?? HomeSeries.FirstOrDefault(item => item.Id == id)
                : null;

            RaiseHomeState();
            await EnsureFilterOptionsAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static void Fill(ObservableCollection<LibraryItemViewModel> target, IEnumerable<LibraryPageItem> rows)
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(new LibraryItemViewModel(row));
        }
    }

    private void RaiseHomeState()
    {
        OnPropertyChanged(nameof(HasHomeMovies));
        OnPropertyChanged(nameof(HasHomeSeries));
        OnPropertyChanged(nameof(HasHomeItems));
        OnPropertyChanged(nameof(HasNoItems));
    }

    /// <summary>Shows every movie / series (leaves the dashboard for that category).</summary>
    public void ShowAllMovies() => SelectedTab = LibraryTab.Movies;

    public void ShowAllSeries() => SelectedTab = LibraryTab.Series;

    private async Task EnsureFilterOptionsAsync()
    {
        var kind = KindOrNull();
        if (_optionsLoaded && _optionsKind == kind)
        {
            return;
        }

        var years = await _queryService.GetDistinctYearsAsync(kind);
        var languages = await _queryService.GetDistinctLanguagesAsync(kind);

        var selectedYear = SelectedYear;
        var selectedLanguage = SelectedLanguage;

        YearOptions.Clear();
        YearOptions.Add("All years");
        foreach (var year in years)
        {
            YearOptions.Add(year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        LanguageOptions.Clear();
        LanguageOptions.Add("All languages");
        foreach (var language in languages)
        {
            LanguageOptions.Add(language);
        }

        _optionsKind = kind;
        _optionsLoaded = true;

        // A selection that the new category does not have falls back to "all"
        // instead of leaving the combo empty (which would silently filter
        // everything away).
        if (selectedYear is null || !YearOptions.Contains(selectedYear))
        {
            SelectedYear = "All years";
        }

        if (selectedLanguage is null || !LanguageOptions.Contains(selectedLanguage))
        {
            SelectedLanguage = "All languages";
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

            _activity.Publish(
                result.Queued > 0
                    ? $"{item.TypeDisplay} “{item.Title}” added to the cart successfully ({result.Queued} file(s))."
                    : StatusText,
                result.Queued > 0 ? ActivityKind.Cart : ActivityKind.Warning);

            CartChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error("Adding a title to the USB cart failed.", ex);
            _activity.Publish($"Adding “{item.Title}” to the cart failed: {ex.Message}", ActivityKind.Error);
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
