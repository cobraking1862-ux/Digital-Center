using System.Text.Json;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Tmdb;

namespace DigitalCenter.Core.Services;

/// <summary>
/// Applies TMDb metadata to a <see cref="MediaTitle"/>. Fields the operator
/// edited by hand are listed in <see cref="MediaTitle.MetadataOverrides"/>
/// (a JSON array of <see cref="MetadataFieldNames"/> values) and are never
/// touched by an automatic refresh, so manual corrections always survive.
/// </summary>
public static class MetadataMerge
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    public static IReadOnlyList<string> ReadOverrides(string? overridesJson)
    {
        if (string.IsNullOrWhiteSpace(overridesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(overridesJson, SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void WriteOverrides(MediaTitle title, IEnumerable<string> fields)
    {
        var list = ReadOverrides(title.MetadataOverrides).ToHashSet(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            list.Add(field);
        }

        title.MetadataOverrides = JsonSerializer.Serialize(list.OrderBy(f => f, StringComparer.Ordinal).ToList(), SerializerOptions);
    }

    public static bool IsOverridden(MediaTitle title, string field) =>
        ReadOverrides(title.MetadataOverrides).Contains(field, StringComparer.Ordinal);

    /// <summary>Applies TMDb metadata to the title, skipping manually edited fields.</summary>
    public static void Apply(DigitalCenterDbContext db, MediaTitle title, TmdbTitleMetadata metadata)
    {
        if (!IsOverridden(title, MetadataFieldNames.Title) && !string.IsNullOrWhiteSpace(metadata.Title))
        {
            title.Title = metadata.Title;
        }

        if (!IsOverridden(title, MetadataFieldNames.OriginalTitle) && !string.IsNullOrWhiteSpace(metadata.OriginalTitle))
        {
            title.OriginalTitle = metadata.OriginalTitle;
        }

        if (!IsOverridden(title, MetadataFieldNames.ReleaseDate))
        {
            if (metadata.ReleaseDate.HasValue)
            {
                title.ReleaseDate = metadata.ReleaseDate;
                title.ReleaseYear = metadata.Year ?? title.ReleaseYear;
            }
            else if (metadata.Year.HasValue)
            {
                title.ReleaseYear = metadata.Year.Value;
            }
        }

        if (!IsOverridden(title, MetadataFieldNames.Synopsis) && !string.IsNullOrWhiteSpace(metadata.Synopsis))
        {
            title.Synopsis = metadata.Synopsis;
        }

        if (!IsOverridden(title, MetadataFieldNames.OriginalLanguage) && !string.IsNullOrWhiteSpace(metadata.OriginalLanguage))
        {
            title.OriginalLanguage = metadata.OriginalLanguage;
        }

        if (metadata.ImdbId is not null && !IsOverridden(title, MetadataFieldNames.ImdbId))
        {
            title.ImdbId = string.IsNullOrWhiteSpace(metadata.ImdbId) ? title.ImdbId : metadata.ImdbId;
        }

        if (metadata.VoteAverage is not null)
        {
            title.VoteAverage = metadata.VoteAverage;
        }

        // Artwork: only fill what is missing; manual artwork choices survive.
        if (!IsOverridden(title, MetadataFieldNames.Poster))
        {
            title.TmdbPosterPath ??= metadata.PosterPath;
        }

        if (!IsOverridden(title, MetadataFieldNames.Backdrop))
        {
            title.TmdbBackdropPath ??= metadata.BackdropPath;
        }

        if (!IsOverridden(title, MetadataFieldNames.Trailer))
        {
            if (!string.IsNullOrWhiteSpace(metadata.TrailerUrl))
            {
                title.TrailerUrl = metadata.TrailerUrl;
                title.TrailerName = metadata.TrailerName;
            }
        }

        ApplyGenres(db, title, metadata);
        ApplyCountries(db, title, metadata);
        ApplyPeople(db, title, metadata);
        ApplySeries(db, title, metadata);
    }

    private static void ApplyGenres(DigitalCenterDbContext db, MediaTitle title, TmdbTitleMetadata metadata)
    {
        if (IsOverridden(title, MetadataFieldNames.Genres) || metadata.Genres.Count == 0)
        {
            return;
        }

        title.Genres.Clear();
        foreach (var genreName in metadata.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var genre = db.Genres.Local.FirstOrDefault(g => g.Name == genreName)
                        ?? db.Genres.FirstOrDefault(g => g.Name == genreName);
            if (genre is null)
            {
                genre = new Genre { Name = genreName };
                db.Genres.Add(genre);
            }

            title.Genres.Add(new TitleGenre { MediaTitleId = title.Id, Genre = genre });
        }
    }

    private static void ApplyCountries(DigitalCenterDbContext db, MediaTitle title, TmdbTitleMetadata metadata)
    {
        if (IsOverridden(title, MetadataFieldNames.Countries) || metadata.Countries.Count == 0)
        {
            return;
        }

        title.Countries.Clear();
        foreach (var countryName in metadata.Countries.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var country = db.Countries.Local.FirstOrDefault(c => c.Name == countryName)
                          ?? db.Countries.FirstOrDefault(c => c.Name == countryName);
            if (country is null)
            {
                country = new Country { Name = countryName };
                db.Countries.Add(country);
            }

            title.Countries.Add(new TitleCountry { MediaTitleId = title.Id, Country = country });
        }
    }

    private static void ApplyPeople(DigitalCenterDbContext db, MediaTitle title, TmdbTitleMetadata metadata)
    {
        if (IsOverridden(title, MetadataFieldNames.People) || metadata.People.Count == 0)
        {
            return;
        }

        title.People.Clear();
        foreach (var credit in metadata.People.Where(p => !string.IsNullOrWhiteSpace(p.Name)).Distinct())
        {
            var person = FindPerson(db, credit);
            if (person is null)
            {
                person = new Person
                {
                    Name = credit.Name,
                    TmdbPersonId = credit.TmdbPersonId,
                    // Store the intended cache file name now; the artwork step
                    // downloads the image into it.
                    ProfilePath = credit.TmdbPersonId is not null ? $"p{credit.TmdbPersonId}.jpg" : null
                };
                db.People.Add(person);
            }
            else
            {
                person.TmdbPersonId ??= credit.TmdbPersonId;
                if (person.ProfilePath is null && credit.TmdbPersonId is not null)
                {
                    person.ProfilePath = $"p{credit.TmdbPersonId}.jpg";
                }
            }

            title.People.Add(new TitlePerson
            {
                MediaTitleId = title.Id,
                Person = person,
                Role = credit.Role,
                Character = credit.Character
            });
        }
    }

    private static Person? FindPerson(DigitalCenterDbContext db, TmdbPersonCredit credit)
    {
        if (credit.TmdbPersonId.HasValue)
        {
            var byTmdb = db.People.Local.FirstOrDefault(p => p.TmdbPersonId == credit.TmdbPersonId.Value)
                         ?? db.People.FirstOrDefault(p => p.TmdbPersonId == credit.TmdbPersonId.Value);
            if (byTmdb is not null)
            {
                return byTmdb;
            }
        }

        return db.People.Local.FirstOrDefault(p => p.Name == credit.Name)
               ?? db.People.FirstOrDefault(p => p.Name == credit.Name);
    }

    private static void ApplySeries(DigitalCenterDbContext db, MediaTitle title, TmdbTitleMetadata metadata)
    {
        if (!metadata.IsSeries)
        {
            return;
        }

        var details = db.SeriesDetails.Local.FirstOrDefault(sd => sd.MediaTitleId == title.Id)
                      ?? title.SeriesDetails;
        if (details is null)
        {
            details = new SeriesDetails { MediaTitleId = title.Id, MediaTitle = title };
            db.SeriesDetails.Add(details);
        }

        if (!string.IsNullOrWhiteSpace(metadata.AiringStatus))
        {
            details.AiringStatus = metadata.AiringStatus;
        }

        if (metadata.Seasons.Count > 0)
        {
            details.TotalSeasons = metadata.Seasons.Count;
        }

        foreach (var seasonMeta in metadata.Seasons)
        {
            var season = details.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonMeta.SeasonNumber);
            if (season is null)
            {
                season = new Season { SeriesDetailsId = details.MediaTitleId, SeasonNumber = seasonMeta.SeasonNumber };
                details.Seasons.Add(season);
            }

            foreach (var episodeMeta in seasonMeta.Episodes)
            {
                var episode = season.Episodes.FirstOrDefault(e => e.EpisodeNumber == episodeMeta.EpisodeNumber);
                if (episode is null)
                {
                    season.Episodes.Add(new Episode
                    {
                        SeasonId = season.Id == 0 ? 0 : season.Id,
                        Season = season,
                        EpisodeNumber = episodeMeta.EpisodeNumber,
                        Title = string.IsNullOrWhiteSpace(episodeMeta.Title)
                            ? $"Episode {episodeMeta.EpisodeNumber}"
                            : episodeMeta.Title
                    });
                }
                else if (!string.IsNullOrWhiteSpace(episodeMeta.Title) &&
                         (string.IsNullOrWhiteSpace(episode.Title) || episode.Title.StartsWith("Episode ", StringComparison.Ordinal)))
                {
                    episode.Title = episodeMeta.Title;
                }
            }
        }
    }
}
