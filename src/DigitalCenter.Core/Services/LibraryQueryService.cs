using DigitalCenter.Core.Data;
using DigitalCenter.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Services;

/// <summary>Structured filter + free-text search parameters for the library screen.</summary>
public sealed record LibraryFilter
{
    public string? SearchText { get; init; }
    public MediaKind? Type { get; init; }
    public int? YearFrom { get; init; }
    public int? YearTo { get; init; }
    public string? Genre { get; init; }
    public string? Country { get; init; }
    public string? Category { get; init; }
    /// <summary>ISO-639-1 code of the original language (e.g. "en"); null = all languages.</summary>
    public string? Language { get; init; }
    public string SortBy { get; init; } = "Title"; // Title | Year | Created
    public bool SortDescending { get; init; }
    public int MaxResults { get; init; } = 500;
}

/// <summary>One row of the library list/grid.</summary>
public sealed record LibraryPageItem(
    int Id,
    string Title,
    int? ReleaseYear,
    MediaKind Type,
    string? PosterPath,
    int FileCount,
    int FilesOnDisk,
    long TotalSize,
    List<string> Categories,
    List<string> Genres);

public sealed record LibraryPage(int TotalMatches, IReadOnlyList<LibraryPageItem> Items);

/// <summary>
/// Executes library searches and filters in the database (indexes for
/// structured filters, FTS5 for text search with a LIKE fallback) instead of
/// loading the catalog into memory.
/// </summary>
public sealed class LibraryQueryService
{
    private readonly IDbContextFactory<DigitalCenterDbContext> _factory;
    private readonly bool _ftsEnabled;

    public LibraryQueryService(IDbContextFactory<DigitalCenterDbContext> factory, bool ftsEnabled)
    {
        _factory = factory;
        _ftsEnabled = ftsEnabled;
    }

    public async Task<LibraryPage> QueryAsync(LibraryFilter filter, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        IQueryable<MediaTitle> query = db.MediaTitles
            .AsNoTracking()
            .Include(t => t.Files)
            .Include(t => t.Categories).ThenInclude(tc => tc.Category)
            .Include(t => t.Genres).ThenInclude(tg => tg.Genre);

        var textIds = await FindTextMatchIdsAsync(db, filter.SearchText, ct);
        if (textIds is not null)
        {
            query = query.Where(t => textIds.Contains(t.Id));
        }

        if (filter.Type.HasValue)
        {
            query = query.Where(t => t.Type == filter.Type.Value);
        }

        if (filter.YearFrom.HasValue)
        {
            query = query.Where(t => t.ReleaseYear >= filter.YearFrom.Value);
        }

        if (filter.YearTo.HasValue)
        {
            query = query.Where(t => t.ReleaseYear <= filter.YearTo.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Language))
        {
            query = query.Where(t => t.OriginalLanguage == filter.Language);
        }

        if (!string.IsNullOrWhiteSpace(filter.Genre))
        {
            query = query.Where(t => t.Genres.Any(g => g.Genre.Name == filter.Genre));
        }

        if (!string.IsNullOrWhiteSpace(filter.Country))
        {
            query = query.Where(t => t.Countries.Any(c => c.Country.Name == filter.Country));
        }

        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            query = query.Where(t => t.Categories.Any(c => c.Category.Name == filter.Category));
        }

        var totalMatches = await query.CountAsync(ct);

        query = (filter.SortBy, filter.SortDescending) switch
        {
            ("Year", false) => query.OrderBy(t => t.ReleaseYear).ThenBy(t => t.Title),
            ("Year", true) => query.OrderByDescending(t => t.ReleaseYear).ThenBy(t => t.Title),
            ("Created", false) => query.OrderBy(t => t.CreatedAt),
            ("Created", true) => query.OrderByDescending(t => t.CreatedAt),
            (_, true) => query.OrderByDescending(t => t.Title),
            _ => query.OrderBy(t => t.Title)
        };

        var rows = await query
            .Take(filter.MaxResults)
            .Select(t => new
            {
                t.Id,
                t.Title,
                t.ReleaseYear,
                t.Type,
                t.PosterPath,
                Files = t.Files.Select(f => new { f.FilePath, f.FileSize }).ToList(),
                Categories = t.Categories.Select(tc => tc.Category.Name).ToList(),
                Genres = t.Genres.Select(tg => tg.Genre.Name).ToList()
            })
            .ToListAsync(ct);

        var items = rows.Select(r =>
        {
            var onDisk = r.Files.Count(f => File.Exists(f.FilePath));
            return new LibraryPageItem(
                r.Id,
                r.Title,
                r.ReleaseYear,
                r.Type,
                r.PosterPath,
                r.Files.Count,
                onDisk,
                r.Files.Sum(f => f.FileSize),
                r.Categories,
                r.Genres);
        }).ToList();

        return new LibraryPage(totalMatches, items);
    }

    /// <summary>
    /// Returns the ids of all files belonging to the given titles (movies:
    /// their files; series: the files of every episode). Used by the
    /// "Add to USB Queue" action on the Library screen.
    /// </summary>
    public async Task<List<int>> GetFileIdsForTitlesAsync(IReadOnlyCollection<int> titleIds, CancellationToken ct = default)
    {
        if (titleIds.Count == 0)
        {
            return [];
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.MediaFiles
            .Where(f => (f.MovieTitleId != null && titleIds.Contains(f.MovieTitleId.Value)) ||
                        (f.EpisodeId != null && titleIds.Contains(f.Episode!.Season.SeriesDetailsId)))
            .Select(f => f.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the ids matching the free-text query, or null when no text
    /// filter is active. Uses FTS5 when available; otherwise LIKE across
    /// titles and people names.
    /// </summary>
    private async Task<List<int>?> FindTextMatchIdsAsync(DigitalCenterDbContext db, string? searchText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return null;
        }

        var text = searchText.Trim();

        if (_ftsEnabled)
        {
            var match = FtsSupport.BuildMatchExpression(text);
            if (match.Length == 0)
            {
                return [];
            }

            var ids = new List<int>();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT MediaTitleId FROM {FtsSupport.FtsTableName} WHERE {FtsSupport.FtsTableName} MATCH @match LIMIT 5000;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@match";
            parameter.Value = match;
            command.Parameters.Add(parameter);

            if (command.Connection!.State != System.Data.ConnectionState.Open)
            {
                await command.Connection.OpenAsync(ct);
            }

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ids.Add(reader.GetInt32(0));
            }

            return ids;
        }

        var escaped = text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
        var pattern = $"%{escaped}%";

        return await db.MediaTitles
            .AsNoTracking()
            .Where(t => EF.Functions.Like(t.Title, pattern, @"\")
                        || t.People.Any(tp => EF.Functions.Like(tp.Person.Name, pattern, @"\")))
            .Select(t => t.Id)
            .Take(5000)
            .ToListAsync(ct);
    }

    /// <summary>Distinct release years for the subbar year selector (newest first).</summary>
    public async Task<List<int>> GetDistinctYearsAsync(MediaKind? kind, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.MediaTitles.AsNoTracking().Where(t => t.ReleaseYear != null);
        if (kind.HasValue)
        {
            query = query.Where(t => t.Type == kind.Value);
        }

        return await query
            .Select(t => t.ReleaseYear!.Value)
            .Distinct()
            .OrderByDescending(y => y)
            .ToListAsync(ct);
    }

    /// <summary>Distinct original languages for the subbar language selector (A-Z).</summary>
    public async Task<List<string>> GetDistinctLanguagesAsync(MediaKind? kind, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.MediaTitles.AsNoTracking().Where(t => t.OriginalLanguage != null);
        if (kind.HasValue)
        {
            query = query.Where(t => t.Type == kind.Value);
        }

        return await query
            .Select(t => t.OriginalLanguage!)
            .Distinct()
            .OrderBy(l => l)
            .ToListAsync(ct);
    }

    /// <summary>Genre names with their title counts, for the Genres tab chips.</summary>
    public async Task<List<(string Name, int Count)>> GetGenreCountsAsync(MediaKind? kind, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.TitleGenres.AsNoTracking();
        if (kind.HasValue)
        {
            query = query.Where(tg => tg.MediaTitle!.Type == kind.Value);
        }

        var rows = await query
            .GroupBy(tg => tg.Genre!.Name)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderBy(g => g.Name)
            .ToListAsync(ct);

        return rows.Select(r => (r.Name, r.Count)).ToList();
    }
}
