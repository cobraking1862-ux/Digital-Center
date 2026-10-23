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

/// <summary>
/// DetailsViewModel, part 3 of 3: the actions on the details action bar —
/// back, open in Explorer, watch the trailer, queue the files for USB and
/// refresh the metadata from TMDb.
/// </summary>
public partial class DetailsViewModel
{
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

        _activity.Publish(
            result.Queued > 0
                ? $"{(IsSeries ? "Series" : "Movie")} “{Title}” added to the cart successfully ({result.Queued} file(s))."
                : StatusText,
            result.Queued > 0 ? ActivityKind.Cart : ActivityKind.Warning);
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
        _activity.Publish($"Downloading metadata for “{Title}” from TMDb …", ActivityKind.Background);
        try
        {
            var metadata = IsSeries
                ? await _tmdb.GetSeriesAsync(entity.TmdbId.Value)
                : await _tmdb.GetMovieAsync(entity.TmdbId.Value);

            if (metadata is null)
            {
                StatusText = "TMDb returned no data for this id.";
                _activity.Publish(StatusText, ActivityKind.Warning);
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
            _activity.Publish(StatusText, ActivityKind.Success);
            Saved = true;
            SavedChanges?.Invoke();
            await LoadEntityAsync();
        }
        catch (Exception ex) when (ex is TmdbRequestException or TmdbNotConfiguredException)
        {
            StatusText = $"The TMDb refresh failed: {ex.Message}";
            _activity.Publish(StatusText, ActivityKind.Warning);
        }
        catch (Exception ex)
        {
            _logger.Error("TMDb refresh failed.", ex);
            _activity.Publish($"Refreshing “{Title}” from TMDb failed: {ex.Message}", ActivityKind.Error);
            StatusText = $"The TMDb refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
