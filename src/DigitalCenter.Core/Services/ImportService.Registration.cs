using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Tmdb;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Services;

/// <summary>
/// ImportService, part 5 of 5: registration. Links the moved file to its title,
/// season and episode rows and caches the artwork that came with the metadata.
/// </summary>
public sealed partial class ImportService
{
    private async Task RegisterFileAsync(DigitalCenterDbContext db, MetadataImportLog log, string targetPath, bool isSeries, int titleId, CancellationToken ct)
    {
        log.Stage = ImportStages.RegisterStarted;
        await db.SaveChangesAsync(ct);

        // Idempotent: a crashed run may have registered the file already.
        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.FilePath == targetPath, ct);
        long size;
        try
        {
            size = new FileInfo(targetPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            size = log.FileSize ?? 0;
        }

        if (file is null)
        {
            file = new MediaFile { FilePath = targetPath, FileSize = size };
            if (isSeries)
            {
                var episode = await FindOrCreateEpisodeAsync(db, titleId, log.ParsedSeason!.Value, log.ParsedEpisode!.Value, ct);
                file.EpisodeId = episode.Id;
            }
            else
            {
                file.MovieTitleId = titleId;
            }

            db.MediaFiles.Add(file);
        }
        else if (file.MovieTitleId is null && file.EpisodeId is null)
        {
            // Should not happen (check constraint), but stay defensive.
            file.MovieTitleId = titleId;
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<Episode> FindOrCreateEpisodeAsync(DigitalCenterDbContext db, int titleId, int seasonNumber, int episodeNumber, CancellationToken ct)
    {
        var details = await db.SeriesDetails.FirstOrDefaultAsync(sd => sd.MediaTitleId == titleId, ct);
        if (details is null)
        {
            details = new SeriesDetails { MediaTitleId = titleId };
            db.SeriesDetails.Add(details);
            await db.SaveChangesAsync(ct);
        }

        var season = await db.Seasons.FirstOrDefaultAsync(s => s.SeriesDetailsId == titleId && s.SeasonNumber == seasonNumber, ct);
        if (season is null)
        {
            season = new Season { SeriesDetailsId = titleId, SeasonNumber = seasonNumber };
            db.Seasons.Add(season);
            await db.SaveChangesAsync(ct);
        }

        var episode = await db.Episodes.FirstOrDefaultAsync(e => e.SeasonId == season.Id && e.EpisodeNumber == episodeNumber, ct);
        if (episode is null)
        {
            episode = new Episode
            {
                SeasonId = season.Id,
                EpisodeNumber = episodeNumber,
                Title = $"Episode {episodeNumber}"
            };
            db.Episodes.Add(episode);
            await db.SaveChangesAsync(ct);
        }

        return episode;
    }

    private async Task TryCacheArtworkAsync(DigitalCenterDbContext db, MetadataImportLog log, int titleId, string? posterPath, TmdbTitleMetadata? metadata, CancellationToken ct)
    {
        try
        {
            var settings = _settings.Load();
            if (!_tmdb.IsConfigured || log.TmdbId is null)
            {
                return;
            }

            var title = await db.MediaTitles.FirstAsync(t => t.Id == titleId, ct);

            if (settings.DownloadPosters &&
                !string.IsNullOrWhiteSpace(posterPath) &&
                !_posters.Exists(title.PosterPath))
            {
                await using var stream = await _tmdb.DownloadPosterAsync(posterPath, ct);
                title.PosterPath = await _posters.SaveAsync(titleId, stream, ct);
                title.TmdbPosterPath = posterPath;
                await db.SaveChangesAsync(ct);
            }

            if (settings.DownloadBackdrops &&
                !string.IsNullOrWhiteSpace(title.TmdbBackdropPath) &&
                !_posters.Exists(title.BackdropPath))
            {
                await using var stream = await _tmdb.DownloadBackdropAsync(title.TmdbBackdropPath, ct);
                title.BackdropPath = await _posters.SaveBackdropAsync(titleId, stream, ct);
                await db.SaveChangesAsync(ct);
            }

            if (settings.DownloadCastProfiles && metadata is not null)
            {
                await CacheProfilesAsync(db, titleId, metadata, ct);
            }
        }
        catch (Exception ex) when (ex is TmdbRequestException or TmdbNotConfiguredException or IOException or InvalidOperationException)
        {
            _logger?.Warn($"Artwork caching failed for title {titleId}: {ex.Message}");
        }
    }

    private async Task CacheProfilesAsync(DigitalCenterDbContext db, int titleId, TmdbTitleMetadata metadata, CancellationToken ct)
    {
        var people = await db.TitlePeople
            .Where(tp => tp.MediaTitleId == titleId)
            .Include(tp => tp.Person)
            .ToListAsync(ct);

        foreach (var credit in metadata.People)
        {
            ct.ThrowIfCancellationRequested();
            if (credit.TmdbPersonId is null || string.IsNullOrWhiteSpace(credit.ProfilePath))
            {
                continue; // No TMDb image reference for this person.
            }

            var person = people.FirstOrDefault(p => p.Person.TmdbPersonId == credit.TmdbPersonId.Value)?.Person;
            if (person is null)
            {
                continue;
            }

            var cacheName = $"p{credit.TmdbPersonId.Value}.jpg";
            if (_posters.Exists(cacheName))
            {
                person.ProfilePath = cacheName;
                continue;
            }

            try
            {
                await using var stream = await _tmdb.DownloadProfileAsync(credit.ProfilePath, ct);
                await _posters.SaveProfileAsync(credit.TmdbPersonId.Value, stream, ct);
                person.ProfilePath = cacheName;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is TmdbRequestException or IOException)
            {
                // One missing profile never blocks the rest.
            }
        }
    }
}
