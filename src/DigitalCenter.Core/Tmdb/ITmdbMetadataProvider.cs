namespace DigitalCenter.Core.Tmdb;

/// <summary>One search result (movie or series) as shown in candidate lists.</summary>
public sealed record TmdbSearchCandidate(
    long Id,
    string Title,
    int? Year,
    string? OriginalTitle,
    string? Overview,
    string? PosterPath,
    bool IsSeries,
    string ReleaseInfo,
    /// <summary>
    /// Original language as reported by the search ("en", "ur", "ko", ...). The
    /// search response already carries it, so organizing an import by language
    /// never costs a second request.
    /// </summary>
    string? OriginalLanguage = null);

/// <summary>
/// An episode entry of a season ("Poker Face", number 3). The optional TMDb
/// episode id is the external identifier of that episode.
/// </summary>
public sealed record TmdbEpisodeInfo(int SeasonNumber, int EpisodeNumber, string? Title, long? TmdbId = null);

/// <summary>One person credit (cast or crew) with an optional profile image.</summary>
public sealed record TmdbPersonCredit(
    string Name,
    string Role,          // "Actor", "Director", "Creator", "Screenplay", ...
    string? Character,    // Character name for actors.
    long? TmdbPersonId,
    string? ProfilePath); // Original TMDb profile path ("/abc.jpg").

/// <summary>
/// Normalized metadata snapshot used to upsert a catalog title. All fields are
/// optional-rich: TMDb may return partial data and every consumer must cope
/// with that.
/// </summary>
public sealed record TmdbTitleMetadata
{
    public required string Title { get; init; }
    public string? OriginalTitle { get; init; }
    public int? Year { get; init; }
    public DateTime? ReleaseDate { get; init; }
    public string? Synopsis { get; init; }
    public string? OriginalLanguage { get; init; }
    public string? PosterPath { get; init; }
    public string? BackdropPath { get; init; }
    public string? ImdbId { get; init; }
    public double? VoteAverage { get; init; }
    public string? TrailerName { get; init; }
    public string? TrailerUrl { get; init; }
    public required bool IsSeries { get; init; }
    public string? AiringStatus { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> Countries { get; init; } = [];
    /// <summary>Cast (first entries first) and crew of interest.</summary>
    public IReadOnlyList<TmdbPersonCredit> People { get; init; } = [];
    /// <summary>Season/episode breakdown when the metadata source provides one (may be empty).</summary>
    public IReadOnlyList<TmdbSeasonMetadata> Seasons { get; init; } = [];

    /// <summary>
    /// Number of seasons the catalog entry has (TMDb "seasons" array length),
    /// independent of how many of them were actually fetched into
    /// <see cref="Seasons"/>. Null when the source did not report it.
    /// </summary>
    public int? SeasonCount { get; init; }
}

public sealed record TmdbSeasonMetadata(int SeasonNumber, IReadOnlyList<TmdbEpisodeInfo> Episodes);

/// <summary>
/// Abstraction over the TMDb REST API so import/matching workflows can be
/// tested without network access. Implementations must never log the access token.
/// </summary>
public interface ITmdbMetadataProvider
{
    /// <summary>False when no access token is configured; every other method then throws <see cref="TmdbNotConfiguredException"/>.</summary>
    bool IsConfigured { get; }

    Task<IReadOnlyList<TmdbSearchCandidate>> SearchMoviesAsync(string query, int? year, CancellationToken ct = default);

    Task<IReadOnlyList<TmdbSearchCandidate>> SearchSeriesAsync(string query, int? year, CancellationToken ct = default);

    Task<TmdbTitleMetadata?> GetMovieAsync(long tmdbId, CancellationToken ct = default);

    Task<TmdbTitleMetadata?> GetSeriesAsync(long tmdbId, CancellationToken ct = default);

    /// <summary>
    /// Series metadata in a single request, without per-season episode lists
    /// (<see cref="TmdbTitleMetadata.Seasons"/> stays empty). This is the cheap
    /// call for matching and importing one episode: the season that is
    /// actually needed is fetched separately with
    /// <see cref="GetSeriesSeasonAsync"/> instead of pulling every season.
    /// </summary>
    Task<TmdbTitleMetadata?> GetSeriesSummaryAsync(long tmdbId, CancellationToken ct = default);

    /// <summary>
    /// Episodes of one season of a series. Returns null when TMDb does not
    /// have that season (or it cannot be reached) — a missing season must
    /// never fail an import, so callers treat null as "no episode details".
    /// </summary>
    Task<TmdbSeasonMetadata?> GetSeriesSeasonAsync(long tmdbId, int seasonNumber, CancellationToken ct = default);

    /// <summary>
    /// Resolves an IMDb id ("tt0111161") to the TMDb catalog entry. Returns
    /// null when TMDb does not know the id. This uses the official TMDb
    /// /find endpoint — no IMDb page is scraped anywhere in DigitalCenter.
    /// </summary>
    Task<(long Id, bool IsSeries)?> FindByImdbIdAsync(string imdbId, CancellationToken ct = default);

    /// <summary>Downloads the poster image for a TMDb poster path (e.g. "/abc.jpg") and returns an open stream.</summary>
    Task<Stream> DownloadPosterAsync(string posterPath, CancellationToken ct = default);

    /// <summary>Downloads the backdrop image for a TMDb backdrop path and returns an open stream.</summary>
    Task<Stream> DownloadBackdropAsync(string backdropPath, CancellationToken ct = default);

    /// <summary>Downloads a person profile image for a TMDb profile path and returns an open stream.</summary>
    Task<Stream> DownloadProfileAsync(string profilePath, CancellationToken ct = default);

    /// <summary>Lightweight request used by the "Test connection" action. Returns false when TMDb cannot be reached.</summary>
    Task<bool> TestConnectionAsync(CancellationToken ct = default);
}

/// <summary>Thrown when TMDb is used without a configured read access token.</summary>
public sealed class TmdbNotConfiguredException : InvalidOperationException
{
    public TmdbNotConfiguredException()
        : base("TMDb is not configured. Set the Tmdb:ReadAccessToken configuration value (environment variable, appsettings.json or user secrets). Metadata search is unavailable, but the rest of the application keeps working.")
    {
    }
}
