using System.Text;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Services;

public sealed record MissingFileInfo(int MediaFileId, string FilePath, long FileSize, DateTime AddedAt, string TitleDisplay);

public sealed record DuplicateGroupInfo(string Reason, long FileSize, IReadOnlyList<string> FilePaths, string TitleDisplay);

public sealed record CsvImportSummary(int RowsRead, int TitlesCreated, int TitlesMerged, int FilesRegistered, int FilesMissing, int RowsSkipped, IReadOnlyList<string> Errors);

/// <summary>
/// Library maintenance: missing-file detection, duplicate candidates, FTS
/// index rebuild and CSV import/export. Read-only helpers never modify the
/// filesystem; CSV import is additive (existing titles are merged, files are
/// only registered, never moved).
/// </summary>
public sealed class MaintenanceService
{
    private readonly IDbContextFactory<DigitalCenterDbContext> _factory;
    private readonly IAppLogger? _logger;

    public MaintenanceService(IDbContextFactory<DigitalCenterDbContext> factory, IAppLogger? logger = null)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>Catalog files that no longer exist on disk. Purely informational; nothing is deleted automatically.</summary>
    public async Task<IReadOnlyList<MissingFileInfo>> GetMissingFilesAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var files = await db.MediaFiles
            .AsNoTracking()
            .Include(f => f.MovieTitle)
            .Include(f => f.Episode!).ThenInclude(e => e.Season).ThenInclude(s => s.SeriesDetails).ThenInclude(sd => sd.MediaTitle)
            .OrderBy(f => f.FilePath)
            .ToListAsync(ct);

        var missing = new List<MissingFileInfo>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(file.FilePath))
            {
                continue;
            }

            var titleDisplay = file.MovieTitle is not null
                ? file.MovieTitle.Title + (file.MovieTitle.ReleaseYear.HasValue ? $" ({file.MovieTitle.ReleaseYear.Value})" : string.Empty)
                : (file.Episode?.Season?.SeriesDetails?.MediaTitle is { } series
                    ? $"{series.Title} - S{file.Episode.Season.SeasonNumber:00}E{file.Episode.EpisodeNumber:00}"
                    : "Unknown");

            missing.Add(new MissingFileInfo(file.Id, file.FilePath, file.FileSize, file.AddedAt, titleDisplay));
        }

        return missing;
    }

    /// <summary>
    /// Duplicate candidates (reported only, never auto-deleted):
    /// 1) identical file name + size at multiple locations,
    /// 2) titles with more than one registered file (possible alternate versions).
    /// </summary>
    public async Task<IReadOnlyList<DuplicateGroupInfo>> GetDuplicateCandidatesAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var files = await db.MediaFiles
            .AsNoTracking()
            .Include(f => f.MovieTitle)
            .Include(f => f.Episode!).ThenInclude(e => e.Season).ThenInclude(s => s.SeriesDetails).ThenInclude(sd => sd.MediaTitle)
            .ToListAsync(ct);

        var groups = new List<DuplicateGroupInfo>();

        foreach (var group in files
                     .GroupBy(f => new { Name = Path.GetFileName(f.FilePath).ToLowerInvariant(), f.FileSize })
                     .Where(g => g.Count() > 1 && g.Key.FileSize > 0))
        {
            groups.Add(new DuplicateGroupInfo(
                $"Identical file name and size in {group.Count()} locations",
                group.Key.FileSize,
                group.Select(f => f.FilePath).OrderBy(p => p).ToList(),
                group.Key.Name));
        }

        var movieMulti = files
            .Where(f => f.MovieTitleId.HasValue)
            .GroupBy(f => f.MovieTitleId!.Value)
            .Where(g => g.Count() > 1);
        foreach (var group in movieMulti)
        {
            var title = group.First().MovieTitle!;
            groups.Add(new DuplicateGroupInfo(
                $"{group.Count()} files for one movie (possible alternate versions)",
                group.Sum(f => f.FileSize),
                group.Select(f => f.FilePath).OrderBy(p => p).ToList(),
                title.Title + (title.ReleaseYear.HasValue ? $" ({title.ReleaseYear.Value})" : string.Empty)));
        }

        var episodeMulti = files
            .Where(f => f.EpisodeId.HasValue)
            .GroupBy(f => f.EpisodeId!.Value)
            .Where(g => g.Count() > 1);
        foreach (var group in episodeMulti)
        {
            var episode = group.First().Episode!;
            var seriesTitle = episode.Season?.SeriesDetails?.MediaTitle?.Title ?? "Series";
            groups.Add(new DuplicateGroupInfo(
                $"{group.Count()} files for one episode (possible alternate versions)",
                group.Sum(f => f.FileSize),
                group.Select(f => f.FilePath).OrderBy(p => p).ToList(),
                $"{seriesTitle} - S{episode.Season?.SeasonNumber ?? 0:00}E{episode.EpisodeNumber:00}"));
        }

        return groups;
    }

    /// <summary>Rebuilds the FTS5 index from the catalog. Throws when FTS5 is unavailable (LIKE fallback active).</summary>
    public async Task<int> RebuildSearchIndexAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        if (!FtsSupport.Fts5Available(db))
        {
            throw new NotSupportedException("FTS5 is not available in this SQLite build; the LIKE fallback needs no index.");
        }

        await db.Database.ExecuteSqlRawAsync($"DELETE FROM {FtsSupport.FtsTableName};", ct);

        var titles = await db.MediaTitles
            .AsNoTracking()
            .Include(t => t.People).ThenInclude(tp => tp.Person)
            .ToListAsync(ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var title in titles)
        {
            ct.ThrowIfCancellationRequested();
            var people = string.Join(' ', title.People.Select(p => p.Person.Name).Distinct());
            await db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO {FtsSupport.FtsTableName}(Title, People, MediaTitleId) VALUES ({{0}}, {{1}}, {{2}});",
                new object[] { title.Title, people, title.Id }, ct);
        }

        await transaction.CommitAsync(ct);
        _logger?.Info($"Search index rebuilt for {titles.Count} title(s).");
        return titles.Count;
    }

    // ------------------------------------------------------------------ CSV

    private static readonly string[] CsvColumns =
    [
        "Type", "Title", "ReleaseYear", "Synopsis", "AiringStatus", "TmdbId",
        "Categories", "Genres", "Countries", "Actors", "Directors",
        "FilePath", "FileSize"
    ];

    /// <summary>Exports the whole catalog as RFC-4180 CSV (multi-value columns use "; " separators).</summary>
    public async Task<int> ExportCsvAsync(string targetFile, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var titles = await db.MediaTitles
            .AsNoTracking()
            .Include(t => t.Files)
            .Include(t => t.SeriesDetails)
            .Include(t => t.Categories).ThenInclude(tc => tc.Category)
            .Include(t => t.Genres).ThenInclude(tg => tg.Genre)
            .Include(t => t.Countries).ThenInclude(tc => tc.Country)
            .Include(t => t.People).ThenInclude(tp => tp.Person)
            .OrderBy(t => t.Type).ThenBy(t => t.Title)
            .ToListAsync(ct);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetFile))!);

        await using var writer = new StreamWriter(targetFile, append: false, Encoding.UTF8);
        await writer.WriteLineAsync(string.Join(",", CsvColumns.Select(EscapeCsv)));

        var exported = 0;
        foreach (var title in titles)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var file in title.Files.OrderBy(f => f.FilePath).DefaultIfEmpty())
            {
                var row = new[]
                {
                    title.Type.ToString(),
                    title.Title,
                    title.ReleaseYear?.ToString() ?? string.Empty,
                    title.Synopsis ?? string.Empty,
                    title.SeriesDetails?.AiringStatus ?? string.Empty,
                    title.TmdbId?.ToString() ?? string.Empty,
                    string.Join("; ", title.Categories.Select(tc => tc.Category.Name)),
                    string.Join("; ", title.Genres.Select(tg => tg.Genre.Name)),
                    string.Join("; ", title.Countries.Select(tc => tc.Country.Name)),
                    string.Join("; ", title.People.Where(p => p.Role == "Actor").Select(p => p.Person.Name).Distinct()),
                    string.Join("; ", title.People.Where(p => p.Role == "Director" || p.Role == "Creator").Select(p => p.Person.Name).Distinct()),
                    file?.FilePath ?? string.Empty,
                    file is null ? string.Empty : file.FileSize.ToString()
                };

                await writer.WriteLineAsync(string.Join(",", row.Select(EscapeCsv)));
                exported++;
            }
        }

        _logger?.Info($"Catalog exported to '{targetFile}' ({exported} rows).");
        return exported;
    }

    /// <summary>
    /// Additive CSV import: rows with the same (Type, Title, Year) are merged
    /// into one title; existing metadata lists are extended, never cleared.
    /// FilePath entries are registered when the file exists on disk and is
    /// not already registered; missing paths are counted, never invented.
    /// </summary>
    public async Task<CsvImportSummary> ImportCsvAsync(string sourceFile, CancellationToken ct = default)
    {
        if (!File.Exists(sourceFile))
        {
            throw new FileNotFoundException("The CSV file does not exist.", sourceFile);
        }

        var summary = new CsvImportSummary(0, 0, 0, 0, 0, 0, new List<string>());
        var errors = (List<string>)summary.Errors;

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(sourceFile, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The CSV file could not be read: {ex.Message}", ex);
        }

        if (lines.Length == 0)
        {
            return summary;
        }

        var header = ParseCsvLine(lines[0]);
        if (header.Count < 2 || !header.Contains("Title", StringComparer.OrdinalIgnoreCase) || !header.Contains("Type", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The CSV header must contain at least 'Type' and 'Title' (expected columns: " + string.Join(", ", CsvColumns) + ").");
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        for (var lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var rawLine = lines[lineIndex];
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            try
            {
                var cells = ParseCsvLine(rawLine);
                var row = MapRow(header, cells);
                summary = summary with { RowsRead = summary.RowsRead + 1 };

                if (!Enum.TryParse<MediaKind>(GetValue(row, "Type"), ignoreCase: true, out var kind) ||
                    string.IsNullOrWhiteSpace(GetValue(row, "Title")))
                {
                    summary = summary with { RowsSkipped = summary.RowsSkipped + 1 };
                    errors.Add($"Row {lineIndex + 1}: invalid Type or empty Title; row skipped.");
                    continue;
                }

                var titleName = GetValue(row, "Title").Trim();
                int? year = int.TryParse(GetValue(row, "ReleaseYear"), out var parsedYear) ? parsedYear : null;

                var title = await db.MediaTitles.FirstOrDefaultAsync(
                    t => t.Type == kind && t.Title == titleName &&
                         (t.ReleaseYear == year || (t.ReleaseYear == null && year == null)), ct);

                if (title is null)
                {
                    title = new MediaTitle { Type = kind, Title = titleName, ReleaseYear = year };
                    db.MediaTitles.Add(title);
                    await db.SaveChangesAsync(ct);
                    summary = summary with { TitlesCreated = summary.TitlesCreated + 1 };
                }
                else
                {
                    summary = summary with { TitlesMerged = summary.TitlesMerged + 1 };
                }

                var synopsis = GetValue(row, "Synopsis");
                if (title.Synopsis is null && synopsis.Length > 0)
                {
                    title.Synopsis = synopsis;
                }

                if (title.TmdbId is null && long.TryParse(GetValue(row, "TmdbId"), out var tmdbId))
                {
                    title.TmdbId = tmdbId;
                }

                if (kind == MediaKind.Series)
                {
                    // Series always get the extension row, even without extra data.
                    var details = await db.SeriesDetails.FirstOrDefaultAsync(sd => sd.MediaTitleId == title.Id, ct);
                    if (details is null)
                    {
                        details = new SeriesDetails { MediaTitleId = title.Id };
                        db.SeriesDetails.Add(details);
                        await db.SaveChangesAsync(ct);
                    }

                    var airing = GetValue(row, "AiringStatus");
                    if (airing.Length > 0)
                    {
                        details.AiringStatus ??= airing;
                    }
                }

                await MergeCategoriesAsync(db, title, kind, GetValue(row, "Categories"), ct);
                await MergeGenresAsync(db, title, GetValue(row, "Genres"), ct);
                await MergeCountriesAsync(db, title, GetValue(row, "Countries"), ct);

                foreach (var (names, role) in new[] { (GetValue(row, "Actors"), "Actor"), (GetValue(row, "Directors"), "Director") })
                {
                    foreach (var personName in SplitMulti(names))
                    {
                        var person = await db.People.FirstOrDefaultAsync(p => p.Name == personName, ct);
                        if (person is null)
                        {
                            person = new Person { Name = personName };
                            db.People.Add(person);
                            await db.SaveChangesAsync(ct);
                        }

                        var exists = await db.TitlePeople.AnyAsync(tp => tp.MediaTitleId == title.Id && tp.PersonId == person.Id && tp.Role == role, ct);
                        if (!exists)
                        {
                            db.TitlePeople.Add(new TitlePerson { MediaTitleId = title.Id, PersonId = person.Id, Role = role });
                        }
                    }
                }

                await db.SaveChangesAsync(ct);

                var filePath = GetValue(row, "FilePath");
                if (filePath.Length > 0)
                {
                    if (!File.Exists(filePath))
                    {
                        summary = summary with { FilesMissing = summary.FilesMissing + 1 };
                    }
                    else if (!await db.MediaFiles.AnyAsync(f => f.FilePath == filePath, ct))
                    {
                        MediaFile file;
                        if (kind == MediaKind.Series)
                        {
                            // Episodes without season/episode columns cannot be
                            // linked; count them as missing rather than guessing.
                            summary = summary with { FilesMissing = summary.FilesMissing + 1 };
                            errors.Add($"Row {lineIndex + 1}: series file '{filePath}' cannot be linked without Season/Episode columns; not registered.");
                            continue;
                        }
                        else
                        {
                            file = new MediaFile { MovieTitleId = title.Id, FilePath = filePath, FileSize = new FileInfo(filePath).Length };
                        }

                        db.MediaFiles.Add(file);
                        await db.SaveChangesAsync(ct);
                        summary = summary with { FilesRegistered = summary.FilesRegistered + 1 };
                    }
                }
            }
            catch (Exception ex)
            {
                summary = summary with { RowsSkipped = summary.RowsSkipped + 1 };
                errors.Add($"Row {lineIndex + 1}: {ex.Message}");
                _logger?.Warn($"CSV import row {lineIndex + 1} failed: {ex.Message}");
            }
        }

        await transaction.CommitAsync(ct);
        _logger?.Info($"CSV import finished: {summary.RowsRead} rows, {summary.TitlesCreated} created, {summary.TitlesMerged} merged, {summary.FilesRegistered} files registered.");
        return summary;
    }

    /// <summary>Additively links the "; "-separated category names to the title.</summary>
    private static async Task MergeCategoriesAsync(DigitalCenterDbContext db, MediaTitle title, MediaKind kind, string multiValue, CancellationToken ct)
    {
        foreach (var name in SplitMulti(multiValue))
        {
            var category = await db.Categories.FirstOrDefaultAsync(c => c.Type == kind && c.Name == name, ct);
            if (category is null)
            {
                category = new Category { Type = kind, Name = name };
                db.Categories.Add(category);
                await db.SaveChangesAsync(ct);
            }

            if (!await db.TitleCategories.AnyAsync(tc => tc.MediaTitleId == title.Id && tc.CategoryId == category.Id, ct))
            {
                db.TitleCategories.Add(new TitleCategory { MediaTitleId = title.Id, CategoryId = category.Id });
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private static async Task MergeGenresAsync(DigitalCenterDbContext db, MediaTitle title, string multiValue, CancellationToken ct)
    {
        foreach (var name in SplitMulti(multiValue))
        {
            var genre = await db.Genres.FirstOrDefaultAsync(g => g.Name == name, ct);
            if (genre is null)
            {
                genre = new Genre { Name = name };
                db.Genres.Add(genre);
                await db.SaveChangesAsync(ct);
            }

            if (!await db.TitleGenres.AnyAsync(tg => tg.MediaTitleId == title.Id && tg.GenreId == genre.Id, ct))
            {
                db.TitleGenres.Add(new TitleGenre { MediaTitleId = title.Id, GenreId = genre.Id });
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private static async Task MergeCountriesAsync(DigitalCenterDbContext db, MediaTitle title, string multiValue, CancellationToken ct)
    {
        foreach (var name in SplitMulti(multiValue))
        {
            var country = await db.Countries.FirstOrDefaultAsync(c => c.Name == name, ct);
            if (country is null)
            {
                country = new Country { Name = name };
                db.Countries.Add(country);
                await db.SaveChangesAsync(ct);
            }

            if (!await db.TitleCountries.AnyAsync(tc => tc.MediaTitleId == title.Id && tc.CountryId == country.Id, ct))
            {
                db.TitleCountries.Add(new TitleCountry { MediaTitleId = title.Id, CountryId = country.Id });
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private static string[] SplitMulti(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static string EscapeCsv(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
        {
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        return text;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }

    private static Dictionary<string, string> MapRow(List<string> header, List<string> cells)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count && i < cells.Count; i++)
        {
            map[header[i].Trim()] = cells[i];
        }

        return map;
    }

    private static string GetValue(Dictionary<string, string> row, string column) =>
        row.TryGetValue(column, out var value) ? value : string.Empty;
}
