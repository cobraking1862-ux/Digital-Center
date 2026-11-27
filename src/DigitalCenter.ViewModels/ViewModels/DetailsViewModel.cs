using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.Core.Tmdb;
using DigitalCenter.ViewModels.Services;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.ViewModels;

/// <summary>One cast row on the details screen.</summary>
public sealed record PersonRow(int PersonId, string Name, string Role, string? Character, string? ProfilePath, bool HasProfile)
{
    public string RoleDisplay => Character is { Length: > 0 } && Role == "Actor" ? Character : Role;
}

public sealed record MediaFileRow(int FileId, string FilePath, string SizeText, bool Exists)
{
    public string FileName => Path.GetFileName(FilePath);
}

/// <summary>One vertical key-value detail entry in the highlighted Movie Details card.</summary>
public sealed record DetailFieldRow(string Label, string Value);

/// <summary>
/// Movie details screen: visible backdrop hero banner, highlighted vertical
/// Movie Details section, single-row horizontal scrollable Cast strip below
/// details, local files, and a metadata editor with custom poster upload.
/// </summary>
public partial class DetailsViewModel : ObservableObject
{
    private readonly IDbContextFactory<DigitalCenterDbContext> _factory;
    private readonly ITmdbMetadataProvider _tmdb;
    private readonly PosterCache _posters;
    private readonly SettingsService _settingsService;
    private readonly UsbCopyService _copyService;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly ActivityFeedService _activityFeed;
    private readonly int _titleId;

    private MediaTitle? _entity;

    public event Action? SavedChanges;
    public event EventHandler? CloseRequested;

    public bool Saved { get; private set; }

    public DetailsViewModel(
        int titleId,
        IDbContextFactory<DigitalCenterDbContext> factory,
        ITmdbMetadataProvider tmdb,
        PosterCache posters,
        SettingsService settingsService,
        UsbCopyService copyService,
        IDialogService dialogs,
        IAppLogger logger,
        ActivityFeedService activityFeed)
    {
        _titleId = titleId;
        _factory = factory;
        _tmdb = tmdb;
        _posters = posters;
        _settingsService = settingsService;
        _copyService = copyService;
        _dialogs = dialogs;
        _logger = logger;
        _activityFeed = activityFeed;

        Title = string.Empty;
        YearDisplay = string.Empty;
        TypeDisplay = "Movie";
        CollectionDisplay = string.Empty;
        ReleaseDateText = string.Empty;
        RatingDisplay = string.Empty;
        Synopsis = string.Empty;
        MetaText = string.Empty;
        GenresText = string.Empty;
        StatusText = string.Empty;
    }

    public ObservableCollection<DetailFieldRow> DetailFields { get; } = [];
    public ObservableCollection<PersonRow> Cast { get; } = [];
    public ObservableCollection<string> Genres { get; } = [];
    public ObservableCollection<string> Tags { get; } = [];
    public ObservableCollection<MediaFileRow> Files { get; } = [];
    public ObservableCollection<string> EditTags { get; } = [];

    // ----------------------------------------------------------- display
    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial string YearDisplay { get; set; }
    [ObservableProperty] public partial string TypeDisplay { get; set; }
    [ObservableProperty] public partial string CollectionDisplay { get; set; }
    [ObservableProperty] public partial string ReleaseDateText { get; set; }
    [ObservableProperty] public partial string RatingDisplay { get; set; }
    [ObservableProperty] public partial string GenresText { get; set; }
    [ObservableProperty] public partial string Synopsis { get; set; }
    [ObservableProperty] public partial string MetaText { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty] public partial string? PosterPath { get; set; }
    [ObservableProperty] public partial string? BackdropPath { get; set; }
    [ObservableProperty] public partial string? TrailerUrl { get; set; }
    [ObservableProperty] public partial string? TrailerName { get; set; }
    [ObservableProperty] public partial bool IsEditing { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public bool HasTrailer => !string.IsNullOrWhiteSpace(TrailerUrl);
    public bool HasBackdrop => !string.IsNullOrWhiteSpace(BackdropPath);
    public bool HasCollection => !string.IsNullOrWhiteSpace(CollectionDisplay);
    public bool HasRating => !string.IsNullOrWhiteSpace(RatingDisplay);
    public bool HasCast => Cast.Count > 0;
    public bool HasGenres => Genres.Count > 0;
    public bool HasTags => Tags.Count > 0;
    public bool HasFiles => Files.Count > 0;

    partial void OnTrailerUrlChanged(string? value) => OnPropertyChanged(nameof(HasTrailer));
    partial void OnBackdropPathChanged(string? value) => OnPropertyChanged(nameof(HasBackdrop));
    partial void OnCollectionDisplayChanged(string value) => OnPropertyChanged(nameof(HasCollection));
    partial void OnRatingDisplayChanged(string value) => OnPropertyChanged(nameof(HasRating));

    // ----------------------------------------------------------- editor
    [ObservableProperty] public partial string EditTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditOriginalTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditYear { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditSynopsis { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditLanguage { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditCollectionName { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditImdbId { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditTmdbId { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditTrailerName { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditTrailerUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string NewTagName { get; set; } = string.Empty;
    [ObservableProperty] public partial string? EditPosterFilePath { get; set; }

    public string EditPosterDisplay => string.IsNullOrWhiteSpace(EditPosterFilePath)
        ? (string.IsNullOrWhiteSpace(PosterPath) ? "No poster selected" : "Current cached poster")
        : Path.GetFileName(EditPosterFilePath);

    public bool HasEditPosterSelection => !string.IsNullOrWhiteSpace(EditPosterFilePath);

    partial void OnEditPosterFilePathChanged(string? value)
    {
        OnPropertyChanged(nameof(EditPosterDisplay));
        OnPropertyChanged(nameof(HasEditPosterSelection));
    }

    public async Task InitializeAsync() => await LoadEntityAsync();

    private async Task LoadEntityAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        _entity = await db.MediaTitles
            .Include(t => t.Files)
            .Include(t => t.SeriesDetails!).ThenInclude(sd => sd.Seasons).ThenInclude(s => s.Episodes).ThenInclude(e => e.Files)
            .Include(t => t.Genres).ThenInclude(tg => tg.Genre)
            .Include(t => t.Categories).ThenInclude(tc => tc.Category)
            .Include(t => t.Countries).ThenInclude(tc => tc.Country)
            .Include(t => t.People).ThenInclude(tp => tp.Person)
            .FirstOrDefaultAsync(t => t.Id == _titleId);

        if (_entity is null)
        {
            _dialogs.Error("Details", "This movie no longer exists in the catalog.");
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        ApplyEntityToDisplay();
    }

    private void ApplyEntityToDisplay()
    {
        var entity = _entity!;
        Title = entity.Title;
        YearDisplay = entity.ReleaseYear?.ToString() ?? string.Empty;
        ReleaseDateText = entity.ReleaseDate?.ToString("d MMMM yyyy") ?? (entity.ReleaseYear?.ToString() ?? string.Empty);
        RatingDisplay = entity.VoteAverage is { } v ? $"{v:0.0} / 10" : string.Empty;
        Synopsis = entity.Synopsis ?? "No synopsis has been stored for this movie yet.";

        var allCategoryNames = entity.Categories
            .Select(c => c.Category?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        var collectionCat = allCategoryNames.FirstOrDefault(n => n.StartsWith("Collection:", StringComparison.OrdinalIgnoreCase));
        CollectionDisplay = collectionCat is not null
            ? collectionCat["Collection:".Length..].Trim()
            : string.Empty;
        TypeDisplay = HasCollection ? $"Collection · {CollectionDisplay}" : "Movie";

        Genres.Clear();
        foreach (var genre in entity.Genres
                     .Select(g => g.Genre?.Name)
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .Select(n => n!)
                     .OrderBy(n => n))
        {
            Genres.Add(genre);
        }
        GenresText = Genres.Count > 0 ? string.Join(", ", Genres) : string.Empty;
        OnPropertyChanged(nameof(HasGenres));

        Tags.Clear();
        foreach (var tag in allCategoryNames.Where(n => !n.StartsWith("Collection:", StringComparison.OrdinalIgnoreCase)))
        {
            Tags.Add(tag);
        }
        OnPropertyChanged(nameof(HasTags));

        // Build vertical structured Movie Details rows
        DetailFields.Clear();
        var meta = new List<string>();

        if (HasCollection)
        {
            DetailFields.Add(new DetailFieldRow("Movie Collection", CollectionDisplay));
            meta.Add("Movie Collection: " + CollectionDisplay);
        }

        if (!string.IsNullOrWhiteSpace(ReleaseDateText))
        {
            DetailFields.Add(new DetailFieldRow("Release Date", ReleaseDateText));
            meta.Add("Release: " + ReleaseDateText);
        }

        if (entity.VoteAverage is { } vote)
        {
            DetailFields.Add(new DetailFieldRow("TMDb Rating", $"{vote:0.0} / 10"));
            meta.Add($"TMDb rating: {vote:0.0}/10");
        }

        if (!string.IsNullOrWhiteSpace(GenresText))
        {
            DetailFields.Add(new DetailFieldRow("Genres", GenresText));
        }

        if (!string.IsNullOrWhiteSpace(entity.OriginalLanguage))
        {
            DetailFields.Add(new DetailFieldRow("Original Language", entity.OriginalLanguage));
            meta.Add("Original language: " + entity.OriginalLanguage);
        }

        var countries = entity.Countries
            .Select(c => c.Country?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();
        if (countries.Count > 0)
        {
            var countriesText = string.Join(", ", countries);
            DetailFields.Add(new DetailFieldRow("Countries", countriesText));
            meta.Add("Countries: " + countriesText);
        }

        if (!string.IsNullOrWhiteSpace(entity.OriginalTitle) &&
            !string.Equals(entity.OriginalTitle, entity.Title, StringComparison.Ordinal))
        {
            DetailFields.Add(new DetailFieldRow("Original Title", entity.OriginalTitle));
            meta.Add("Original title: " + entity.OriginalTitle);
        }

        if (!string.IsNullOrWhiteSpace(entity.ImdbId))
        {
            DetailFields.Add(new DetailFieldRow("IMDb ID", entity.ImdbId));
            meta.Add("IMDb: " + entity.ImdbId);
        }

        if (entity.TmdbId is not null)
        {
            DetailFields.Add(new DetailFieldRow("TMDb ID", entity.TmdbId.Value.ToString()));
            meta.Add("TMDb ID: " + entity.TmdbId);
        }

        if (DetailFields.Count == 0)
        {
            DetailFields.Add(new DetailFieldRow("Catalog ID", entity.Id.ToString()));
            meta.Add($"Catalog ID: {entity.Id}");
        }

        MetaText = string.Join("   ·   ", meta);
        TrailerUrl = entity.TrailerUrl;
        TrailerName = string.IsNullOrWhiteSpace(entity.TrailerName) ? "Watch trailer" : entity.TrailerName;
        PosterPath = _posters.Exists(entity.PosterPath) ? entity.PosterPath : null;
        BackdropPath = _posters.Exists(entity.BackdropPath)
            ? entity.BackdropPath
            : (_posters.Exists(entity.PosterPath) ? entity.PosterPath : null);
        OnPropertyChanged(nameof(EditPosterDisplay));

        Cast.Clear();
        foreach (var person in entity.People
                     .Where(p => p.Person is not null && p.Role == "Actor")
                     .OrderBy(p => p.Person.Name))
        {
            var hasProfile = _posters.Exists(person.Person.ProfilePath);
            Cast.Add(new PersonRow(
                person.PersonId,
                person.Person.Name,
                person.Role,
                person.Character,
                hasProfile ? person.Person.ProfilePath : null,
                hasProfile));
        }
        OnPropertyChanged(nameof(HasCast));

        Files.Clear();
        var allFiles = entity.Files
            .Concat(entity.SeriesDetails?.Seasons.SelectMany(s => s.Episodes).SelectMany(e => e.Files) ?? [])
            .DistinctBy(f => f.Id)
            .OrderBy(f => f.FilePath)
            .ToList();

        foreach (var file in allFiles)
        {
            Files.Add(new MediaFileRow(file.Id, file.FilePath, UiFormat.FormatBytes(file.FileSize), File.Exists(file.FilePath)));
        }

        OnPropertyChanged(nameof(HasFiles));
    }

    // ------------------------------------------------------------ actions

    [RelayCommand]
    private void OpenInExplorer()
    {
        var file = Files.FirstOrDefault(f => File.Exists(f.FilePath))?.FilePath
                   ?? Files.FirstOrDefault()?.FilePath;
        if (!string.IsNullOrWhiteSpace(file))
        {
            _dialogs.OpenInExplorer(file);
            return;
        }

        var settings = _settingsService.Load();
        var fallbackDir = HasCollection
            ? settings.GetEffectiveCollectionsRoot()
            : settings.GetEffectiveMoviesRoot();
        if (!string.IsNullOrWhiteSpace(fallbackDir) && Directory.Exists(fallbackDir))
        {
            _dialogs.OpenInExplorer(fallbackDir);
            return;
        }

        _dialogs.Info("Open in Explorer", "This movie has no registered file on disk yet.");
    }

    [RelayCommand]
    private void OpenTrailer()
    {
        if (!HasTrailer || TrailerUrl is null)
        {
            return;
        }

        _dialogs.OpenUrl(TrailerUrl);
    }

    [RelayCommand]
    private async Task AddToCartAsync()
    {
        var settings = _settingsService.Load();
        var result = await _copyService.EnqueueTitlesAsync(
            [_titleId],
            settings.MoviesRoot,
            settings.SeriesRoot,
            settings.CreatePerTitleFolders,
            settings.CleanFilenames);

        StatusText = result.Queued > 0
            ? "Added to the USB cart."
            : result.AlreadyQueued > 0
                ? "Already in the USB cart."
                : "This movie has no registered files to copy.";

        if (result.Queued > 0)
        {
            _activityFeed.Publish($"Added to USB cart: {Title}");
        }
    }

    [RelayCommand]
    private void StartEdit()
    {
        var entity = _entity;
        if (entity is null)
        {
            return;
        }

        EditTitle = entity.Title;
        EditOriginalTitle = entity.OriginalTitle ?? string.Empty;
        EditYear = entity.ReleaseYear?.ToString() ?? string.Empty;
        EditSynopsis = entity.Synopsis ?? string.Empty;
        EditLanguage = entity.OriginalLanguage ?? string.Empty;
        EditCollectionName = CollectionDisplay;
        EditImdbId = entity.ImdbId ?? string.Empty;
        EditTmdbId = entity.TmdbId?.ToString() ?? string.Empty;
        EditTrailerName = entity.TrailerName ?? string.Empty;
        EditTrailerUrl = entity.TrailerUrl ?? string.Empty;
        EditPosterFilePath = null;

        EditTags.Clear();
        foreach (var tag in entity.Categories
                     .Select(c => c.Category?.Name)
                     .Where(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith("Collection:", StringComparison.OrdinalIgnoreCase))
                     .Select(n => n!)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n))
        {
            EditTags.Add(tag);
        }

        NewTagName = string.Empty;
        IsEditing = true;
    }

    [RelayCommand]
    private void BrowsePoster()
    {
        var picked = _dialogs.PickFile(
            "Choose a movie poster image",
            "Image files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All files (*.*)|*.*");
        if (!string.IsNullOrWhiteSpace(picked) && File.Exists(picked))
        {
            EditPosterFilePath = picked;
        }
    }

    [RelayCommand]
    private void ClearSelectedPoster() => EditPosterFilePath = null;

    [RelayCommand]
    private void CancelEdit()
    {
        EditPosterFilePath = null;
        IsEditing = false;
    }

    [RelayCommand]
    private void AddTag()
    {
        var tag = NewTagName.Trim();
        if (tag.Length == 0)
        {
            return;
        }

        if (!EditTags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
        {
            EditTags.Add(tag);
        }

        NewTagName = string.Empty;
    }

    [RelayCommand]
    private void RemoveTag(string? tag)
    {
        if (tag is not null)
        {
            EditTags.Remove(tag);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var entity = _entity;
        if (entity is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(EditTitle))
        {
            _dialogs.Error("Edit metadata", "The title cannot be empty.");
            return;
        }

        int? year = null;
        var yearText = EditYear?.Trim();
        if (!string.IsNullOrEmpty(yearText))
        {
            if (!int.TryParse(yearText, out var parsedYear) || parsedYear is < 1880 or > 2100)
            {
                _dialogs.Error("Edit metadata", "The release year must be a year between 1880 and 2100, or empty.");
                return;
            }

            year = parsedYear;
        }

        long? tmdbId = null;
        var tmdbText = EditTmdbId?.Trim();
        if (!string.IsNullOrEmpty(tmdbText))
        {
            if (!long.TryParse(tmdbText, out var parsedTmdb) || parsedTmdb <= 0)
            {
                _dialogs.Error("Edit metadata", "The TMDb id must be a positive number, or empty.");
                return;
            }

            tmdbId = parsedTmdb;
        }

        var imdb = EditImdbId?.Trim();
        if (!string.IsNullOrEmpty(imdb) && !System.Text.RegularExpressions.Regex.IsMatch(imdb, @"^tt\d{7,8}$"))
        {
            _dialogs.Error("Edit metadata", "The IMDb id must look like tt1234567, or be empty.");
            return;
        }

        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            var tracked = await db.MediaTitles
                .Include(t => t.Categories).ThenInclude(tc => tc.Category)
                .FirstAsync(t => t.Id == _titleId);

            var overrides = new List<string>();
            if (!string.Equals(tracked.Title, EditTitle.Trim(), StringComparison.Ordinal))
            {
                tracked.Title = EditTitle.Trim();
                overrides.Add(MetadataFieldNames.Title);
            }

            if (!string.Equals(tracked.OriginalTitle, NullIfEmpty(EditOriginalTitle), StringComparison.Ordinal))
            {
                tracked.OriginalTitle = NullIfEmpty(EditOriginalTitle);
                overrides.Add(MetadataFieldNames.OriginalTitle);
            }

            if (tracked.ReleaseYear != year)
            {
                tracked.ReleaseYear = year;
                overrides.Add(MetadataFieldNames.ReleaseDate);
            }

            var synopsis = NullIfEmpty(EditSynopsis);
            if (!string.Equals(tracked.Synopsis, synopsis, StringComparison.Ordinal))
            {
                tracked.Synopsis = synopsis;
                overrides.Add(MetadataFieldNames.Synopsis);
            }

            var language = NullIfEmpty(EditLanguage);
            if (!string.Equals(tracked.OriginalLanguage, language, StringComparison.Ordinal))
            {
                tracked.OriginalLanguage = language;
                overrides.Add(MetadataFieldNames.OriginalLanguage);
            }

            if (tracked.TmdbId != tmdbId)
            {
                if (tmdbId is not null)
                {
                    var clash = await db.MediaTitles.AnyAsync(t => t.TmdbId == tmdbId && t.Id != _titleId);
                    if (clash)
                    {
                        _dialogs.Error("Edit metadata", "Another title already uses that TMDb id.");
                        return;
                    }
                }

                tracked.TmdbId = tmdbId;
                overrides.Add(MetadataFieldNames.TmdbId);
            }

            var imdbValue = NullIfEmpty(imdb);
            if (!string.Equals(tracked.ImdbId, imdbValue, StringComparison.Ordinal))
            {
                tracked.ImdbId = imdbValue;
                overrides.Add(MetadataFieldNames.ImdbId);
            }

            var trailerUrl = NullIfEmpty(EditTrailerUrl);
            var trailerName = NullIfEmpty(EditTrailerName);
            if (!string.Equals(tracked.TrailerUrl, trailerUrl, StringComparison.Ordinal) ||
                !string.Equals(tracked.TrailerName, trailerName, StringComparison.Ordinal))
            {
                tracked.TrailerUrl = trailerUrl;
                tracked.TrailerName = trailerName;
                overrides.Add(MetadataFieldNames.Trailer);
            }

            // Custom poster upload
            if (!string.IsNullOrWhiteSpace(EditPosterFilePath) && File.Exists(EditPosterFilePath))
            {
                await using var stream = File.OpenRead(EditPosterFilePath);
                tracked.PosterPath = await _posters.SaveCustomPosterAsync(_titleId, stream, EditPosterFilePath);
            }

            // ----- Tags & Collection (Categories) -----
            var desiredTags = EditTags
                .Select(t => t.Trim())
                .Where(t => t.Length > 0 && !t.StartsWith("Collection:", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var cleanCollection = CollectionResolver.NormalizeCollectionName(EditCollectionName);
            if (!string.IsNullOrWhiteSpace(cleanCollection))
            {
                desiredTags.Add($"Collection: {cleanCollection}");
            }

            var existingTagLinks = tracked.Categories.ToList();
            foreach (var link in existingTagLinks.Where(l => !desiredTags.Contains(l.Category.Name, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                tracked.Categories.Remove(link);
            }

            foreach (var tagName in desiredTags)
            {
                if (tracked.Categories.Any(tc => string.Equals(tc.Category.Name, tagName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var category = await db.Categories.FirstOrDefaultAsync(c => c.Type == tracked.Type && c.Name == tagName);
                if (category is null)
                {
                    category = new Category { Type = tracked.Type, Name = tagName };
                    db.Categories.Add(category);
                    await db.SaveChangesAsync();
                }

                tracked.Categories.Add(new TitleCategory
                {
                    MediaTitleId = tracked.Id,
                    CategoryId = category.Id,
                    Category = category
                });
            }

            if (overrides.Count > 0)
            {
                MetadataMerge.WriteOverrides(tracked, overrides);
            }

            await db.SaveChangesAsync();
            EditPosterFilePath = null;
            Saved = true;
            IsEditing = false;
            StatusText = "Metadata saved.";
            _activityFeed.Publish($"Metadata saved: {tracked.Title}");
            SavedChanges?.Invoke();
            await LoadEntityAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Saving metadata failed.", ex);
            _dialogs.Error("Edit metadata", $"The change could not be saved: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RefreshFromTmdbAsync()
    {
        var entity = _entity;
        if (entity is null)
        {
            return;
        }

        if (entity.TmdbId is null)
        {
            _dialogs.Info("Refresh from TMDb", "This movie has no TMDb id. Add one in the metadata editor first.");
            return;
        }

        IsBusy = true;
        try
        {
            var metadata = await _tmdb.GetMovieAsync(entity.TmdbId.Value);
            if (metadata is null)
            {
                StatusText = "TMDb returned no data for this id.";
                return;
            }

            await using var db = await _factory.CreateDbContextAsync();
            var tracked = await db.MediaTitles
                .Include(t => t.Genres).ThenInclude(tg => tg.Genre)
                .Include(t => t.Categories).ThenInclude(tc => tc.Category)
                .Include(t => t.Countries).ThenInclude(tc => tc.Country)
                .Include(t => t.People).ThenInclude(tp => tp.Person)
                .FirstAsync(t => t.Id == _titleId);

            MetadataMerge.Apply(db, tracked, metadata);

            var settings = _settingsService.Load();
            if (settings.DownloadPosters && !_posters.Exists(tracked.PosterPath) && !string.IsNullOrWhiteSpace(metadata.PosterPath))
            {
                await using var stream = await _tmdb.DownloadPosterAsync(metadata.PosterPath);
                tracked.PosterPath = await _posters.SaveAsync(_titleId, stream);
                tracked.TmdbPosterPath = metadata.PosterPath;
            }

            if (settings.DownloadBackdrops && !_posters.Exists(tracked.BackdropPath) && !string.IsNullOrWhiteSpace(metadata.BackdropPath))
            {
                await using var stream = await _tmdb.DownloadBackdropAsync(metadata.BackdropPath);
                tracked.BackdropPath = await _posters.SaveBackdropAsync(_titleId, stream);
                tracked.TmdbBackdropPath = metadata.BackdropPath;
            }

            await db.SaveChangesAsync();

            var skipped = MetadataMerge
                .ReadOverrides(tracked.MetadataOverrides)
                .Where(f => f is not MetadataFieldNames.TmdbId and not MetadataFieldNames.ImdbId)
                .ToList();
            StatusText = skipped.Count > 0
                ? $"Metadata refreshed; manual edits kept ({string.Join(", ", skipped)})."
                : "Metadata refreshed from TMDb.";
            Saved = true;
            SavedChanges?.Invoke();
            await LoadEntityAsync();
        }
        catch (Exception ex) when (ex is TmdbRequestException or TmdbNotConfiguredException)
        {
            StatusText = $"The TMDb refresh failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.Error("TMDb refresh failed.", ex);
            StatusText = $"The TMDb refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string? NullIfEmpty(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
