using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Tmdb;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Services;

/// <summary>
/// ImportService, part 2 of 5: matching. Turns a parsed file name into a TMDb
/// identity and records the outcome on the import log. Nothing is moved here.
/// </summary>
public sealed partial class ImportService
{
    /// <summary>
    /// Runs the automatic match for every Pending import log. Called by the
    /// Watch Folder pass, the "Rescan Now" action and application startup, so
    /// files detected while TMDb was offline are retried automatically.
    /// Returns the number of logs that changed state.
    /// </summary>
    public async Task<int> MatchPendingAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pending = await db.MetadataImportLogs
            .Where(l => l.Status == ImportStatuses.Pending)
            .OrderBy(l => l.Id)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return 0;
        }

        ActivityReported?.Invoke($"Downloading metadata for {pending.Count} pending file(s) …");

        var changed = 0;
        foreach (var log in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (await MatchOneAsync(log, ct))
            {
                changed++;
            }
        }

        ActivityReported?.Invoke(
            $"Metadata matching finished: {changed} of {pending.Count} file(s) matched.");

        return changed;
    }

    /// <summary>Matches a single log row and persists the result. Returns true when the row changed.</summary>
    public async Task<bool> MatchOneAsync(MetadataImportLog log, CancellationToken ct = default)
    {
        if (log.Status != ImportStatuses.Pending)
        {
            return false;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var tracked = await db.MetadataImportLogs.FirstOrDefaultAsync(l => l.Id == log.Id, ct);
        if (tracked is null || tracked.Status != ImportStatuses.Pending)
        {
            return false;
        }

        var result = await MatchTrackedAsync(tracked, ct);
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Core matching used by both entry points; the caller owns SaveChanges.</summary>
    private async Task<bool> MatchTrackedAsync(MetadataImportLog log, CancellationToken ct)
    {
        var parsed = new FilenameParser().Parse(
            log.SourceFile,
            string.IsNullOrEmpty(log.SourcePath) ? null : Path.GetDirectoryName(log.SourcePath));

        // Rows detected by an older parser may still carry movie values for a
        // TV episode; the season/episode markers found now win, so a repeated
        // scan repairs the classification instead of duplicating it.
        if (parsed.IsSeries && parsed.Season.HasValue && parsed.Episode.HasValue)
        {
            log.IsSeries = true;
            log.ParsedSeason = parsed.Season;
            log.ParsedEpisode = parsed.Episode;
            if (!string.IsNullOrWhiteSpace(parsed.Title))
            {
                log.ParsedTitle = parsed.Title;
            }

            log.ParsedYear ??= parsed.Year;
        }

        // Non-media files were already marked Unsupported at detection time;
        // a pending row without any usable signal can never match.
        if (!parsed.HasExternalId && string.IsNullOrWhiteSpace(parsed.Title))
        {
            log.Status = ImportStatuses.NoMatch;
            log.Confidence = 0;
            log.ErrorDetails = "The file name contains neither an identifier nor a usable title; the file stays in place.";
            _logger?.Info($"Import match failed (no signal): '{log.SourceFile}'.");
            return true;
        }

        try
        {
            var matcher = new MetadataMatcher(_tmdb, _settings);
            var outcome = await matcher.MatchAsync(parsed, ct);
            log.Confidence = outcome.Confidence;
            log.TmdbId = outcome.TmdbId;
            log.ErrorDetails = null;

            if (outcome.IsMatch)
            {
                log.IsSeries = outcome.IsSeries;
                // TMDb metadata wins over the parsed name when it arrives. A
                // search match already carries the catalogue title/year AND the
                // original language, so no second request is needed; only
                // identifier matches (TMDb/IMDb tags) resolve their title here.
                var language = outcome.OriginalLanguage;
                if (!string.IsNullOrWhiteSpace(outcome.Title))
                {
                    log.ParsedTitle = outcome.Title;
                    if (outcome.Year.HasValue)
                    {
                        log.ParsedYear = outcome.Year.Value;
                    }
                }
                else
                {
                    var metadata = outcome.IsSeries
                        ? await _tmdb.GetSeriesSummaryAsync(outcome.TmdbId!.Value, ct)
                        : await _tmdb.GetMovieAsync(outcome.TmdbId!.Value, ct);
                    if (metadata is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(metadata.Title))
                        {
                            log.ParsedTitle = metadata.Title;
                        }

                        if (metadata.Year.HasValue)
                        {
                            log.ParsedYear = metadata.Year.Value;
                        }

                        language ??= metadata.OriginalLanguage;
                    }
                }

                log.Status = ImportStatuses.Matched;
                log.MetadataSource = "TMDb";
                log.Note = outcome.Detail;

                // When the language is known, plan the destination right away so
                // the review list, the preview and the automatic move all agree on
                // ONE folder: the library root for the media kind plus the folder
                // of the detected language (created when it is missing). An
                // explicit choice made earlier always wins, and without a known
                // language nothing is pre-planned — the root is resolved when the
                // move is confirmed.
                if (!string.IsNullOrWhiteSpace(language) && string.IsNullOrWhiteSpace(log.ChosenFolder))
                {
                    log.ChosenFolder = GetDefaultDestinationFolder(log.IsSeries, _settings.Load(), language);
                }

                _logger?.Info($"Import matched: '{log.SourceFile}' → TMDb {outcome.TmdbId} ({outcome.Confidence:P0})" +
                              (language is null ? "." : $", language '{language}' → '{log.ChosenFolder}'."));
            }
            else
            {
                log.Status = ImportStatuses.NoMatch;
                log.MetadataSource = null;
                log.ErrorDetails = outcome.Detail;
                _logger?.Info($"Import has no match: '{log.SourceFile}' ({outcome.Detail}).");
            }

            return true;
        }
        catch (Exception ex) when (ex is TmdbNotConfiguredException or TmdbRequestException or InvalidOperationException)
        {
            // Stay Pending: the next pass (rescan / startup / next poll) retries.
            log.ErrorDetails = ex is TmdbNotConfiguredException
                ? "Metadata matching is unavailable: no TMDb token is configured."
                : $"Metadata matching is unavailable: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Series metadata for an import: one request for the series itself plus at
    /// most one request for the season the detected episode belongs to (instead
    /// of every season the series has). Unavailable season details never fail
    /// the import — the episode is then created from the parsed numbers.
    /// </summary>
    private async Task<TmdbTitleMetadata?> GetSeriesMetadataAsync(long tmdbId, int? seasonNumber, CancellationToken ct)
    {
        var summary = await _tmdb.GetSeriesSummaryAsync(tmdbId, ct);
        if (summary is null || !seasonNumber.HasValue)
        {
            return summary;
        }

        try
        {
            var season = await _tmdb.GetSeriesSeasonAsync(tmdbId, seasonNumber.Value, ct);
            return season is null || season.Episodes.Count == 0
                ? summary
                : summary with { Seasons = [season] };
        }
        catch (Exception ex) when (ex is TmdbRequestException or IOException)
        {
            _logger?.Warn($"Season details for series {tmdbId} season {seasonNumber.Value} are unavailable: {ex.Message}");
            return summary;
        }
    }
}
