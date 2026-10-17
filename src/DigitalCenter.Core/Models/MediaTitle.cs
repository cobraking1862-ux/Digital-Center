namespace DigitalCenter.Core.Models;

/// <summary>
/// A catalogued movie or series. A title may own multiple files (different
/// versions) and is linked to people, genres, countries and categories via
/// join tables. Series-specific data lives in <see cref="SeriesDetails"/>.
/// Poster/BackdropPath hold file names inside the local artwork cache; the
/// Tmdb*Path fields keep the original TMDb image paths so artwork can be
/// re-downloaded after the cache is cleared.
/// <see cref="MetadataOverrides"/> lists the fields the operator edited by
/// hand (JSON array of field names); TMDb refreshes never overwrite them.
/// </summary>
public class MediaTitle
{
    public int Id { get; set; }

    public MediaKind Type { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>Title in the original language (TMDb "original_title"/"original_name").</summary>
    public string? OriginalTitle { get; set; }

    public int? ReleaseYear { get; set; }

    /// <summary>Full release date (movies: theatrical release; series: first air date).</summary>
    public DateTime? ReleaseDate { get; set; }

    public string? Synopsis { get; set; }

    /// <summary>ISO-639-1 code of the original language (e.g. "en").</summary>
    public string? OriginalLanguage { get; set; }

    /// <summary>Relative path (under the application poster cache) of the local poster file, if any.</summary>
    public string? PosterPath { get; set; }

    /// <summary>Relative path (under the artwork cache) of the local backdrop file, if any.</summary>
    public string? BackdropPath { get; set; }

    /// <summary>Original TMDb poster path (e.g. "/abc.jpg"), kept for re-downloads.</summary>
    public string? TmdbPosterPath { get; set; }

    /// <summary>Original TMDb backdrop path (e.g. "/abc.jpg"), kept for re-downloads.</summary>
    public string? TmdbBackdropPath { get; set; }

    /// <summary>YouTube watch URL of the official trailer, when known.</summary>
    public string? TrailerUrl { get; set; }

    /// <summary>Display name of the trailer ("Official Trailer", ...).</summary>
    public string? TrailerName { get; set; }

    /// <summary>TMDb rating (0-10), when known.</summary>
    public double? VoteAverage { get; set; }

    public long? TmdbId { get; set; }

    /// <summary>IMDb identifier ("tt1234567"), when known.</summary>
    public string? ImdbId { get; set; }

    /// <summary>JSON array of manually edited field names; see MetadataMerge.</summary>
    public string? MetadataOverrides { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public SeriesDetails? SeriesDetails { get; set; }

    public ICollection<MediaFile> Files { get; set; } = new List<MediaFile>();

    public ICollection<TitlePerson> People { get; set; } = new List<TitlePerson>();

    public ICollection<TitleGenre> Genres { get; set; } = new List<TitleGenre>();

    public ICollection<TitleCountry> Countries { get; set; } = new List<TitleCountry>();

    public ICollection<TitleCategory> Categories { get; set; } = new List<TitleCategory>();
}

/// <summary>
/// Field names that can be marked as manually edited in
/// <see cref="MediaTitle.MetadataOverrides"/>. TMDb refreshes skip these
/// fields unless the operator explicitly forces a full overwrite.
/// </summary>
public static class MetadataFieldNames
{
    public const string Title = "Title";
    public const string OriginalTitle = "OriginalTitle";
    public const string ReleaseDate = "ReleaseDate";
    public const string Synopsis = "Synopsis";
    public const string OriginalLanguage = "OriginalLanguage";
    public const string Genres = "Genres";
    public const string Countries = "Countries";
    public const string People = "People";
    public const string Poster = "Poster";
    public const string Backdrop = "Backdrop";
    public const string Trailer = "Trailer";
    public const string TmdbId = "TmdbId";
    public const string ImdbId = "ImdbId";
    public const string Series = "Series";
}
