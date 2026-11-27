using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.ViewModels;

/// <summary>The primary filter views of the main library.</summary>
public enum LibraryTab
{
    Dashboard,
    Movies,
    Collections,
    Genres
}

/// <summary>
/// Library screen: unified virtualized poster grid for All (Dashboard), Movies,
/// Collections, and Genres, with 5-year grouped year filter, language, sorting,
/// and debounced search.
/// </summary>
public partial class LibraryViewModel : ObservableObject
{
    private readonly LibraryQueryService _queryService;
    private readonly UsbCopyService _copyService;
    private readonly SettingsService _settingsService;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly ActivityFeedService _activityFeed;
    private readonly IUiDispatcher? _uiDispatcher;

    public LibraryViewModel(
        LibraryQueryService queryService,
        UsbCopyService copyService,
        SettingsService settingsService,
        IDialogService dialogs,
        IAppLogger logger,
        ActivityFeedService activityFeed,
        IUiDispatcher? uiDispatcher = null)
    {
        _queryService = queryService;
        _copyService = copyService;
        _settingsService = settingsService;
        _dialogs = dialogs;
        _logger = logger;
        _activityFeed = activityFeed;
        _uiDispatcher = uiDispatcher;

        _suppressFilterRefresh = true;
        try
        {
            SelectedTab = LibraryTab.Dashboard;
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
            SelectedYear = "All years";
            SelectedLanguage = "All languages";
        }
        finally
        {
            _suppressFilterRefresh = false;
        }
    }

    public event Action<int>? DetailsRequested;
    public event Action? CartChanged;

    public IReadOnlyList<string> SortOptionsSource { get; }

    public ObservableCollection<string> YearOptions { get; }
    public ObservableCollection<string> LanguageOptions { get; }
    public ObservableCollection<GenreChipViewModel> GenreChips { get; } = [];
    public ObservableCollection<GenreChipViewModel> CollectionChips { get; } = [];

    private ObservableCollection<LibraryItemViewModel> _items = [];
    public ObservableCollection<LibraryItemViewModel> Items
    {
        get => _items;
        private set
        {
            if (SetProperty(ref _items, value))
            {
                OnPropertyChanged(nameof(HasNoItems));
            }
        }
    }

    [ObservableProperty] public partial LibraryTab SelectedTab { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; }
    [ObservableProperty] public partial string SelectedYear { get; set; }
    [ObservableProperty] public partial string SelectedLanguage { get; set; }
    [ObservableProperty] public partial int SortIndex { get; set; }
    [ObservableProperty] public partial string? SelectedGenre { get; set; }
    [ObservableProperty] public partial string? SelectedCollection { get; set; }
    [ObservableProperty] public partial string TotalMatchesText { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string LoadError { get; set; } = string.Empty;

    public bool HasNoItems => !IsLoading && Items.Count == 0 && LoadError.Length == 0;
    public bool IsGenresTab => SelectedTab == LibraryTab.Genres;
    public bool IsCollectionsTab => SelectedTab == LibraryTab.Collections;
    public bool HasGenres => GenreChips.Count > 0;
    public bool HasCollections => CollectionChips.Count > 0;

    private CancellationTokenSource? _debounce;
    private int _refreshGeneration;

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressFilterRefresh) return;

        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        _ = DebouncedRefreshAsync(_debounce.Token);
    }

    partial void OnSelectedTabChanged(LibraryTab value)
    {
        if (_suppressFilterRefresh) return;

        if (value == LibraryTab.Genres)
        {
            _ = LoadGenreChipsAsync();
        }
        else if (value == LibraryTab.Collections)
        {
            _ = LoadCollectionChipsAsync();
        }

        OnPropertyChanged(nameof(IsGenresTab));
        OnPropertyChanged(nameof(IsCollectionsTab));
        OnPropertyChanged(nameof(HasGenres));
        OnPropertyChanged(nameof(HasCollections));

        if (value != LibraryTab.Genres && SelectedGenre is not null)
        {
            SelectedGenre = null;
            return;
        }

        if (value != LibraryTab.Collections && SelectedCollection is not null)
        {
            SelectedCollection = null;
            return;
        }

        _ = RefreshSafeAsync();
    }

    private bool _suppressFilterRefresh;

    partial void OnSelectedYearChanged(string value)
    {
        if (!_suppressFilterRefresh && !string.IsNullOrEmpty(value))
        {
            _ = RefreshSafeAsync();
        }
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        if (!_suppressFilterRefresh && !string.IsNullOrEmpty(value))
        {
            _ = RefreshSafeAsync();
        }
    }

    partial void OnSortIndexChanged(int value)
    {
        if (!_suppressFilterRefresh)
        {
            _ = RefreshSafeAsync();
        }
    }

    partial void OnSelectedGenreChanged(string? value)
    {
        if (!_suppressFilterRefresh)
        {
            _ = RefreshSafeAsync();
        }
    }

    partial void OnSelectedCollectionChanged(string? value)
    {
        if (!_suppressFilterRefresh)
        {
            _ = RefreshSafeAsync();
        }
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(HasNoItems));
    partial void OnLoadErrorChanged(string value) => OnPropertyChanged(nameof(HasNoItems));

    private async Task DebouncedRefreshAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(220, ct);
            ct.ThrowIfCancellationRequested();
            await RefreshSafeAsync();
        }
        catch (OperationCanceledException) { }
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

    private async Task LoadCollectionChipsAsync()
    {
        var collections = await _queryService.GetCollectionCountsAsync();
        SetCollectionChips(collections);
    }

    private CancellationTokenSource? _queryCts;

    private async Task RefreshCoreAsync()
    {
        _queryCts?.Cancel();
        var cts = new CancellationTokenSource();
        _queryCts = cts;
        var ct = cts.Token;

        var generation = Interlocked.Increment(ref _refreshGeneration);
        IsLoading = true;
        try
        {
            var filter = BuildFilter(KindOrNull());
            var page = await _queryService.QueryAsync(filter, ct);
            if (generation != Volatile.Read(ref _refreshGeneration) || ct.IsCancellationRequested)
            {
                return;
            }

            var viewModels = await Task.Run(
                () => page.Items.Select(row => new LibraryItemViewModel(row)).ToList(),
                ct);
            if (generation != Volatile.Read(ref _refreshGeneration) || ct.IsCancellationRequested)
            {
                return;
            }

            void ApplyResultsToUi()
            {
                Items = new ObservableCollection<LibraryItemViewModel>(viewModels);

                TotalMatchesText = page.TotalMatches == page.Items.Count
                    ? $"{page.TotalMatches} title(s)"
                    : $"{page.TotalMatches} matching title(s), showing first {page.Items.Count}";
            }

            if (_uiDispatcher is not null)
            {
                _uiDispatcher.Post(ApplyResultsToUi);
            }
            else
            {
                ApplyResultsToUi();
            }

            await EnsureFilterOptionsAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (generation == Volatile.Read(ref _refreshGeneration))
            {
                IsLoading = false;
            }

            OnPropertyChanged(nameof(HasNoItems));
        }
    }

    private async Task EnsureFilterOptionsAsync()
    {
        var kind = KindOrNull();
        var yearsTask = _queryService.GetDistinctYearsAsync(kind);
        var languagesTask = _queryService.GetDistinctLanguagesAsync(kind);
        await Task.WhenAll(yearsTask, languagesTask);
        var years = await yearsTask;
        var languages = await languagesTask;

        // Group individual years into clean 5-year ranges (e.g. 2021-2025, 2016-2020, 2006-2010, 2001-2005)
        var yearBuckets = years
            .Where(y => y > 1800)
            .Select(y =>
            {
                var start = ((y - 1) / 5) * 5 + 1;
                return (Start: start, End: start + 4);
            })
            .Distinct()
            .OrderByDescending(b => b.Start)
            .Select(b => $"{b.Start}-{b.End}")
            .ToList();

        _suppressFilterRefresh = true;
        try
        {
            var expectedYears = new List<string>(yearBuckets.Count + 1) { "All years" };
            expectedYears.AddRange(yearBuckets);
            if (!YearOptions.SequenceEqual(expectedYears, StringComparer.Ordinal))
            {
                var selected = SelectedYear;
                YearOptions.Clear();
                foreach (var bucket in expectedYears)
                {
                    YearOptions.Add(bucket);
                }
                SelectedYear = selected is not null && YearOptions.Contains(selected) ? selected : "All years";
            }

            if (LanguageOptions.Count != languages.Count + 1)
            {
                var selected = SelectedLanguage;
                LanguageOptions.Clear();
                LanguageOptions.Add("All languages");
                foreach (var language in languages)
                {
                    LanguageOptions.Add(language);
                }
                SelectedLanguage = selected is not null && LanguageOptions.Contains(selected) ? selected : "All languages";
            }
        }
        finally
        {
            _suppressFilterRefresh = false;
        }
    }

    private MediaKind? KindOrNull() => SelectedTab switch
    {
        LibraryTab.Movies => MediaKind.Movie,
        LibraryTab.Collections => MediaKind.Movie,
        _ => null
    };

    private LibraryFilter BuildFilter(MediaKind? type)
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
        if (SelectedYear is not null && SelectedYear != "All years")
        {
            var parts = SelectedYear.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out var fromY) &&
                int.TryParse(parts[1], out var toY))
            {
                yearFrom = fromY;
                yearTo = toY;
            }
            else if (int.TryParse(SelectedYear, out var parsedYear))
            {
                yearFrom = parsedYear;
                yearTo = parsedYear;
            }
        }

        var language = SelectedLanguage is null || SelectedLanguage == "All languages" ? null : SelectedLanguage;

        return new LibraryFilter
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            Type = type,
            YearFrom = yearFrom,
            YearTo = yearTo,
            Language = language,
            Genre = SelectedGenre,
            OnlyCollections = SelectedTab == LibraryTab.Collections,
            CollectionName = SelectedTab == LibraryTab.Collections ? SelectedCollection : null,
            SortBy = sortBy,
            SortDescending = descending
        };
    }

    [RelayCommand]
    private void OpenDetails(LibraryItemViewModel? item) => DetailsRequested?.Invoke(item?.Id ?? 0);

    [RelayCommand]
    private async Task AddToCartAsync(LibraryItemViewModel? item)
    {
        if (item is null || item.Id <= 0) return;

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

            if (result.Queued > 0)
            {
                _activityFeed.Publish($"Added to USB cart: {item.Title}");
            }

            CartChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error("Adding a title to the USB cart failed.", ex);
            _dialogs.Error("USB Cart", $"The title could not be added to the cart: {ex.Message}");
        }
    }

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

        if (SelectedGenre is { } selected && GenreChips.All(c => c.Name != selected))
        {
            SelectedGenre = null;
        }

        OnPropertyChanged(nameof(HasGenres));
    }

    public void SetCollectionChips(IEnumerable<(string Name, int Count)> collections)
    {
        CollectionChips.Clear();
        foreach (var (name, count) in collections)
        {
            var chip = new GenreChipViewModel(name, count)
            {
                IsSelected = string.Equals(SelectedCollection, name, StringComparison.OrdinalIgnoreCase)
            };
            chip.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GenreChipViewModel.IsSelected))
                {
                    if (chip.IsSelected)
                    {
                        foreach (var other in CollectionChips.Where(c => !ReferenceEquals(c, chip) && c.IsSelected))
                        {
                            other.IsSelected = false;
                        }
                        SelectedCollection = chip.Name;
                    }
                    else if (string.Equals(SelectedCollection, chip.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        SelectedCollection = null;
                    }
                }
            };
            CollectionChips.Add(chip);
        }

        if (SelectedCollection is { } selected && CollectionChips.All(c => !string.Equals(c.Name, selected, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedCollection = null;
        }

        OnPropertyChanged(nameof(HasCollections));
    }
}

/// <summary>A selectable genre chip on the Genres tab.</summary>
public sealed partial class GenreChipViewModel(string name, int count) : ObservableObject
{
    public string Name { get; } = name;
    public int Count { get; } = count;
    public string Display => Count > 0 ? $"{Name} ({Count})" : Name;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
