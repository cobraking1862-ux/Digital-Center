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
    private readonly ActivityFeed _activity;
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
        ActivityFeed activity,
        IDialogService dialogs,
        IAppLogger logger)
    {
        _activity = activity;
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



    public async Task InitializeAsync()
    {
        await LoadEntityAsync();

        if (_entity is not null)
        {
            var year = YearDisplay is { Length: > 0 } y && y != "—" ? $" ({y})" : string.Empty;
            _activity.Publish(
                $"Opened {(IsSeries ? "Series" : "Movie")} “{Title}”{year}.",
                ActivityKind.Navigation);
        }
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
}
