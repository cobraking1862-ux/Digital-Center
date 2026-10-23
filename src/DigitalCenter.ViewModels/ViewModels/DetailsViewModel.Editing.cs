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
/// DetailsViewModel, part 2 of 3: the editor. Holds the editable copy of a
/// title, adds and removes cast/episode rows and writes the changes back.
/// </summary>
public partial class DetailsViewModel
{
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
            _activity.Publish($"Metadata saved for “{Title}”.", ActivityKind.Success);
            SavedChanges?.Invoke();
            await LoadEntityAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Saving metadata failed.", ex);
            _activity.Publish($"Saving metadata for “{Title}” failed: {ex.Message}", ActivityKind.Error);
            _dialogs.Error("Edit metadata", $"The change could not be saved: {ex.Message}");
        }
    }

    private static string? NullIfEmpty(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
