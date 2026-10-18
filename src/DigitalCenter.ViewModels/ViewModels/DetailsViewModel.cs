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

/// <summary>One cast/crew row on the details screen.</summary>
public sealed record PersonRow(int PersonId, string Name, string Role, string? Character, string? ProfilePath, bool HasProfile)
{
    public string RoleDisplay => Character is { Length: > 0 } && Role == "Actor" ? Character : Role;
}

/// <summary>
/// One episode row inside a season block of the details screen.
/// <see cref="HasFile"/> drives the visual split between episodes that are
/// actually on disk and episodes that are only known from metadata.
/// </summary>
public sealed record EpisodeRow(int EpisodeId, int EpisodeNumber, string Title, string FileSummary, bool HasFile)
{
    public string EpisodeLabel => $"E{EpisodeNumber:00}";
}

public sealed record SeasonRow(int SeasonNumber, ObservableCollection<EpisodeRow> Episodes)
{
    public string SeasonHeaderText => $"Season {SeasonNumber:00}";

    public int EpisodeCount => Episodes.Count;

    public int AvailableCount => Episodes.Count(e => e.HasFile);

    /// <summary>Season header summary: how much of the season is really here.</summary>
    public string AvailabilityText => EpisodeCount == 0
        ? "No episodes"
        : AvailableCount == EpisodeCount
            ? $"Complete · {EpisodeCount} episode(s)"
            : $"{AvailableCount} of {EpisodeCount} episode(s) available";
}

public sealed record MediaFileRow(int FileId, string FilePath, string SizeText, bool Exists)
{
    public string FileName => Path.GetFileName(FilePath);
}

/// <summary>Editable cast row inside the metadata editor.</summary>
public sealed partial class EditableCastRow : ObservableObject
{
    public EditableCastRow(int personId, string name, string character)
    {
        PersonId = personId;
        Name = name;
        Character = character;
    }

    public int PersonId { get; } // 0 = newly added row.

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Character { get; set; }
}

/// <summary>Editable episode row inside the metadata editor (series only).</summary>
public sealed partial class EditableEpisodeRow : ObservableObject
{
    public EditableEpisodeRow(int episodeId, int seasonNumber, int episodeNumber, string title)
    {
        EpisodeId = episodeId;
        SeasonNumber = seasonNumber;
        EpisodeNumber = episodeNumber;
        Title = title;
    }

    public int EpisodeId { get; } // 0 = newly added row.

    [ObservableProperty]
    public partial int SeasonNumber { get; set; }

    [ObservableProperty]
    public partial int EpisodeNumber { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; }
}

/// <summary>
/// The dedicated details screen: cinematic backdrop, portrait poster, full
/// metadata, cast with profile images, trailer links and the complete metadata
/// editor. Opened by clicking a library card; also used from Movies and Series
/// alike (Management never edits metadata).
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
    private readonly int _titleId;

    private MediaTitle? _entity;

    /// <summary>Raised when the editor saved changes (shell refreshes the library).</summary>
    public event Action? SavedChanges;

    /// <summary>Raised when the operator closes the details screen.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>True when the operator saved changes during this visit.</summary>
    public bool Saved { get; private set; }

    public DetailsViewModel(
        int titleId,
        IDbContextFactory<DigitalCenterDbContext> factory,
        ITmdbMetadataProvider tmdb,
        PosterCache posters,
        SettingsService settingsService,
        UsbCopyService copyService,
        IDialogService dialogs,
        IAppLogger logger)
    {
        _titleId = titleId;
        _factory = factory;
        _tmdb = tmdb;
        _posters = posters;
        _settingsService = settingsService;
        _copyService = copyService;
        _dialogs = dialogs;
        _logger = logger;

        Title = string.Empty;
        YearDisplay = string.Empty;
        Synopsis = string.Empty;
        MetaText = string.Empty;
        GenresText = string.Empty;
        StatusText = string.Empty;
    }

    public ObservableCollection<PersonRow> Cast { get; } = [];
    public ObservableCollection<MediaFileRow> Files { get; } = [];
    public ObservableCollection<SeasonRow> Seasons { get; } = [];

    // ----------------------------------------------------------- display
    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial string YearDisplay { get; set; }
    [ObservableProperty] public partial string TypeDisplay { get; set; }
    [ObservableProperty] public partial string ReleaseDateText { get; set; }
    [ObservableProperty] public partial string GenresText { get; set; }
    [ObservableProperty] public partial string Synopsis { get; set; }
    [ObservableProperty] public partial string MetaText { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty] public partial string? PosterPath { get; set; }
    [ObservableProperty] public partial string? BackdropPath { get; set; }
    [ObservableProperty] public partial string? TrailerUrl { get; set; }
    [ObservableProperty] public partial string? TrailerName { get; set; }
    [ObservableProperty] public partial bool IsSeries { get; set; }
    [ObservableProperty] public partial bool IsEditing { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public bool HasTrailer => !string.IsNullOrWhiteSpace(TrailerUrl);
    public bool HasCast => Cast.Count > 0;
    public bool HasFiles => Files.Count > 0;
    public bool HasSeasons => Seasons.Count > 0;

    partial void OnTrailerUrlChanged(string? value) => OnPropertyChanged(nameof(HasTrailer));

    // ----------------------------------------------------------- editor
    [ObservableProperty] public partial string EditTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditOriginalTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditYear { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditSynopsis { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditLanguage { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditImdbId { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditTmdbId { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditTrailerName { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditTrailerUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial ObservableCollection<EditableCastRow> EditCast { get; set; } = [];
    [ObservableProperty] public partial ObservableCollection<EditableEpisodeRow> EditEpisodes { get; set; } = [];
    [ObservableProperty] public partial string NewCastName { get; set; } = string.Empty;
    [ObservableProperty] public partial string NewCastCharacter { get; set; } = string.Empty;
    [ObservableProperty] public partial int NewEpisodeSeason { get; set; } = 1;
    [ObservableProperty] public partial int NewEpisodeNumber { get; set; } = 1;
    [ObservableProperty] public partial string NewEpisodeTitle { get; set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await LoadEntityAsync();
    }

    private async Task LoadEntityAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        _entity = await db.MediaTitles
            .Include(t => t.Files)
            .Include(t => t.SeriesDetails!).ThenInclude(sd => sd.Seasons).ThenInclude(s => s.Episodes).ThenInclude(e => e.Files)
            .Include(t => t.Genres).ThenInclude(tg => tg.Genre)
            .Include(t => t.Countries).ThenInclude(tc => tc.Country)
            .Include(t => t.People).ThenInclude(tp => tp.Person)
            .FirstOrDefaultAsync(t => t.Id == _titleId);

        if (_entity is null)
        {
            _dialogs.Error("Details", "This title no longer exists in the catalog.");
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        ApplyEntityToDisplay();
    }

    private void ApplyEntityToDisplay()
    {
        var entity = _entity!;
        Title = entity.Title;
        IsSeries = entity.Type == MediaKind.Series;
        TypeDisplay = entity.Type.ToString();
        YearDisplay = entity.ReleaseYear?.ToString() ?? string.Empty;
        ReleaseDateText = entity.ReleaseDate?.ToString("d MMMM yyyy") ?? (entity.ReleaseYear?.ToString() ?? string.Empty);
        Synopsis = entity.Synopsis ?? "No synopsis has been stored for this title yet.";

        var genres = entity.Genres.Select(g => g.Genre.Name).ToList();
        GenresText = genres.Count > 0 ? string.Join("  ·  ", genres) : string.Empty;

        var meta = new List<string>();
        var countries = entity.Countries.Select(c => c.Country.Name).ToList();
        if (countries.Count > 0)
        {
            meta.Add("Countries: " + string.Join(", ", countries));
        }

        if (!string.IsNullOrWhiteSpace(entity.OriginalLanguage))
        {
            meta.Add("Original language: " + entity.OriginalLanguage);
        }

        if (!string.IsNullOrWhiteSpace(entity.OriginalTitle) &&
            !string.Equals(entity.OriginalTitle, entity.Title, StringComparison.Ordinal))
        {
            meta.Add("Original title: " + entity.OriginalTitle);
        }

        var directors = entity.People.Where(p => p.Role is "Director" or "Creator").Select(p => p.Person.Name).Distinct().ToList();
        if (directors.Count > 0)
        {
            meta.Add((IsSeries ? "Created by: " : "Director: ") + string.Join(", ", directors));
        }

        if (entity.VoteAverage is { } vote)
        {
            meta.Add($"TMDb rating: {vote:0.0}/10");
        }

        if (!string.IsNullOrWhiteSpace(entity.ImdbId))
        {
            meta.Add("IMDb: " + entity.ImdbId);
        }

        if (entity.TmdbId is not null)
        {
            meta.Add("TMDb id: " + entity.TmdbId);
        }

        if (IsSeries && entity.SeriesDetails is not null)
        {
            meta.Add("Airing status: " + (entity.SeriesDetails.AiringStatus ?? "unknown"));
        }

        MetaText = string.Join(Environment.NewLine, meta);
        TrailerUrl = entity.TrailerUrl;
        TrailerName = string.IsNullOrWhiteSpace(entity.TrailerName) ? "Watch trailer" : entity.TrailerName;
        PosterPath = _posters.Exists(entity.PosterPath) ? entity.PosterPath : null;
        BackdropPath = _posters.Exists(entity.BackdropPath) ? entity.BackdropPath : null;

        Cast.Clear();
        foreach (var person in entity.People
                     .OrderBy(p => p.Role == "Actor" ? 0 : 1)
                     .ThenBy(p => p.Person.Name))
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
        Seasons.Clear();
        if (IsSeries && entity.SeriesDetails is not null)
        {
            foreach (var season in entity.SeriesDetails.Seasons.OrderBy(s => s.SeasonNumber))
            {
                var rows = new ObservableCollection<EpisodeRow>();
                foreach (var episode in season.Episodes.OrderBy(e => e.EpisodeNumber))
                {
                    var size = episode.Files.Sum(f => f.FileSize);
                    var summary = episode.Files.Count switch
                    {
                        0 => "Not in library",
                        1 => $"In library · 1 file · {UiFormat.FormatBytes(size)}",
                        _ => $"In library · {episode.Files.Count} files · {UiFormat.FormatBytes(size)}"
                    };
                    rows.Add(new EpisodeRow(
                        episode.Id,
                        episode.EpisodeNumber,
                        episode.Title ?? $"Episode {episode.EpisodeNumber}",
                        summary,
                        episode.Files.Count > 0));

                    // A series title itself carries no media files — they hang
                    // off its episodes. List them here too so the details screen
                    // shows the local media of a series and "Open in Explorer"
                    // has a real file to point at.
                    foreach (var file in episode.Files.OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase))
                    {
                        Files.Add(new MediaFileRow(file.Id, file.FilePath, UiFormat.FormatBytes(file.FileSize), File.Exists(file.FilePath)));
                    }
                }

                Seasons.Add(new SeasonRow(season.SeasonNumber, rows));
            }

            OnPropertyChanged(nameof(HasSeasons));
        }
        else
        {
            foreach (var file in entity.Files.OrderBy(f => f.FilePath))
            {
                Files.Add(new MediaFileRow(file.Id, file.FilePath, UiFormat.FormatBytes(file.FileSize), File.Exists(file.FilePath)));
            }
        }

        OnPropertyChanged(nameof(HasFiles));
    }

    // ------------------------------------------------------------ actions

    /// <summary>Returns to the library (Back button, Escape).</summary>
    [RelayCommand]
    private void Back() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Opens the first local media file in Windows Explorer. <see cref="Files"/>
    /// contains the movie files and, for a series, the episode files, so this
    /// works for both kinds.
    /// </summary>
    [RelayCommand]
    private void OpenInExplorer()
    {
        var file = Files
            .Select(row => row.FilePath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

        if (string.IsNullOrWhiteSpace(file))
        {
            _dialogs.Info("Open in Explorer", "This title has no registered media file yet.");
            return;
        }

        _dialogs.OpenInExplorer(file);
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
                : "This title has no registered files to copy.";
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
        EditImdbId = entity.ImdbId ?? string.Empty;
        EditTmdbId = entity.TmdbId?.ToString() ?? string.Empty;
        EditTrailerName = entity.TrailerName ?? string.Empty;
        EditTrailerUrl = entity.TrailerUrl ?? string.Empty;

        EditCast = [.. entity.People.Select(p => new EditableCastRow(p.PersonId, p.Person.Name, p.Character ?? string.Empty))];
        EditEpisodes = [.. entity.SeriesDetails?.Seasons
            .SelectMany(s => s.Episodes)
            .Select(e => new EditableEpisodeRow(e.Id, e.Season.SeasonNumber, e.EpisodeNumber, e.Title ?? string.Empty))
            ?? []];

        NewCastName = string.Empty;
        NewCastCharacter = string.Empty;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void AddCastRow()
    {
        var name = NewCastName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        EditCast.Add(new EditableCastRow(0, name, NewCastCharacter.Trim()));
        NewCastName = string.Empty;
        NewCastCharacter = string.Empty;
    }

    [RelayCommand]
    private void RemoveCastRow(EditableCastRow? row)
    {
        if (row is not null)
        {
            EditCast.Remove(row);
        }
    }

    [RelayCommand]
    private void AddEpisodeRow()
    {
        if (NewEpisodeSeason < 0 || NewEpisodeNumber <= 0)
        {
            return;
        }

        EditEpisodes.Add(new EditableEpisodeRow(0, NewEpisodeSeason, NewEpisodeNumber, NewEpisodeTitle.Trim()));
        NewEpisodeTitle = string.Empty;
        NewEpisodeNumber++;
    }

    [RelayCommand]
    private void RemoveEpisodeRow(EditableEpisodeRow? row)
    {
        if (row is not null)
        {
            EditEpisodes.Remove(row);
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
                .Include(t => t.People).ThenInclude(tp => tp.Person)
                .Include(t => t.SeriesDetails!).ThenInclude(sd => sd.Seasons).ThenInclude(s => s.Episodes)
                .FirstAsync(t => t.Id == _titleId);

            // Track every manually edited field so TMDb refreshes keep them.
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

            // ----- cast/crew relationships -----
            var existingLinks = tracked.People.ToList();
            var keepPersonIds = EditCast.Where(r => r.PersonId != 0).Select(r => r.PersonId).ToHashSet();

            foreach (var link in existingLinks.Where(l => !keepPersonIds.Contains(l.PersonId)).ToList())
            {
                tracked.People.Remove(link);
            }

            foreach (var row in EditCast.Where(r => r.PersonId == 0 && !string.IsNullOrWhiteSpace(r.Name)))
            {
                var person = await db.People.FirstOrDefaultAsync(p => p.Name == row.Name.Trim());
                if (person is null)
                {
                    person = new Person { Name = row.Name.Trim() };
                    db.People.Add(person);
                    await db.SaveChangesAsync();
                }

                tracked.People.Add(new TitlePerson
                {
                    MediaTitleId = tracked.Id,
                    PersonId = person.Id,
                    Role = "Actor",
                    Character = NullIfEmpty(row.Character)
                });
            }

            if (EditCast.Count != existingLinks.Count ||
                EditCast.Any(r => r.PersonId != 0 && existingLinks.Any(l => l.PersonId == r.PersonId && (l.Character ?? string.Empty) != r.Character)))
            {
                overrides.Add(MetadataFieldNames.People);
            }

            // ----- series episodes (series only) -----
            if (IsSeries)
            {
                var existingEpisodes = tracked.SeriesDetails?.Seasons.SelectMany(s => s.Episodes).ToList() ?? [];
                var keepEpisodeIds = EditEpisodes.Where(r => r.EpisodeId != 0).Select(r => r.EpisodeId).ToHashSet();

                foreach (var episode in existingEpisodes.Where(e => !keepEpisodeIds.Contains(e.Id) && e.Files.Count == 0).ToList())
                {
                    db.Episodes.Remove(episode);
                }

                foreach (var episode in existingEpisodes.Where(e => !keepEpisodeIds.Contains(e.Id) && e.Files.Count > 0))
                {
                    // Episodes with registered files are kept (their files would
                    // dangle); they stay visible so nothing is lost silently.
                }

                foreach (var row in EditEpisodes.Where(r => r.EpisodeId == 0))
                {
                    var details = tracked.SeriesDetails ??= new SeriesDetails { MediaTitleId = tracked.Id };
                    var season = details.Seasons.FirstOrDefault(s => s.SeasonNumber == row.SeasonNumber);
                    if (season is null)
                    {
                        season = new Season { SeriesDetailsId = details.MediaTitleId, SeasonNumber = row.SeasonNumber };
                        details.Seasons.Add(season);
                        await db.SaveChangesAsync();
                    }

                    db.Episodes.Add(new Episode
                    {
                        SeasonId = season.Id,
                        EpisodeNumber = row.EpisodeNumber,
                        Title = NullIfEmpty(row.Title) ?? $"Episode {row.EpisodeNumber}"
                    });
                }
            }

            if (overrides.Count > 0)
            {
                MetadataMerge.WriteOverrides(tracked, overrides);
            }

            await db.SaveChangesAsync();
            Saved = true;
            IsEditing = false;
            StatusText = "Metadata saved.";
            SavedChanges?.Invoke();
            await LoadEntityAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Saving metadata failed.", ex);
            _dialogs.Error("Edit metadata", $"The change could not be saved: {ex.Message}");
        }
    }

    /// <summary>
    /// Refreshes the metadata from TMDb. Fields the operator edited by hand
    /// (MetadataOverrides) are preserved; the status text reports them.
    /// </summary>
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
            _dialogs.Info("Refresh from TMDb", "This title has no TMDb id. Add one in the metadata editor first.");
            return;
        }

        IsBusy = true;
        try
        {
            var metadata = IsSeries
                ? await _tmdb.GetSeriesAsync(entity.TmdbId.Value)
                : await _tmdb.GetMovieAsync(entity.TmdbId.Value);

            if (metadata is null)
            {
                StatusText = "TMDb returned no data for this id.";
                return;
            }

            await using var db = await _factory.CreateDbContextAsync();
            var tracked = await db.MediaTitles
                .Include(t => t.Genres).ThenInclude(tg => tg.Genre)
                .Include(t => t.Countries).ThenInclude(tc => tc.Country)
                .Include(t => t.People).ThenInclude(tp => tp.Person)
                .Include(t => t.SeriesDetails!).ThenInclude(sd => sd.Seasons).ThenInclude(s => s.Episodes)
                .FirstAsync(t => t.Id == _titleId);

            MetadataMerge.Apply(db, tracked, metadata);

            // Artwork for new images only (existing downloads are kept).
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
                ? $"Metadata refreshed; your manual edits were kept ({string.Join(", ", skipped)})."
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
