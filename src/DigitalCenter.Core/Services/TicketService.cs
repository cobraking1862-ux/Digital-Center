using System.Globalization;
using System.Text.Json;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using Microsoft.Data.Sqlite;

namespace DigitalCenter.Core.Services;

/// <summary>
/// Persists and manages Lookup Tickets inside digitalcenter.db so operators can
/// file tickets from Lookup availability checks and track/complete them later
/// in the Tickets sidebar panel (or auto-complete them when movies land in the library).
/// </summary>
public sealed class TicketService
{
    public event Action? TicketsChanged;

    private readonly AppPaths _paths;
    private readonly IAppLogger? _logger;

    public TicketService(AppPaths paths, IAppLogger? logger = null)
    {
        _paths = paths;
        _logger = logger;
    }

    private string ConnectionString => $"Data Source={_paths.DatabaseFile};Default Timeout=30;Pooling=True";

    public async Task<LookupTicket> CreateTicketAsync(
        string title,
        int? year,
        string mediaType,
        string imdbId,
        string? posterUrl,
        string availabilitySummary,
        IReadOnlyList<TicketSiteResult> siteResults,
        string? notes = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(siteResults ?? []);

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO LookupTickets (Title, Year, MediaType, ImdbId, PosterUrl, Status, AvailabilitySummary, SiteResultsJson, Notes, CreatedAt)
            VALUES (@title, @year, @type, @imdb, @poster, @status, @summary, @json, @notes, @created);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@title", title.Trim());
        cmd.Parameters.AddWithValue("@year", year.HasValue ? year.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@type", string.IsNullOrWhiteSpace(mediaType) ? "Movie" : mediaType.Trim());
        cmd.Parameters.AddWithValue("@imdb", imdbId.Trim());
        cmd.Parameters.AddWithValue("@poster", string.IsNullOrWhiteSpace(posterUrl) ? DBNull.Value : posterUrl.Trim());
        cmd.Parameters.AddWithValue("@status", TicketStatuses.Open);
        cmd.Parameters.AddWithValue("@summary", availabilitySummary ?? string.Empty);
        cmd.Parameters.AddWithValue("@json", json);
        cmd.Parameters.AddWithValue("@notes", string.IsNullOrWhiteSpace(notes) ? DBNull.Value : notes.Trim());
        cmd.Parameters.AddWithValue("@created", now.ToString("O", CultureInfo.InvariantCulture));

        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        _logger?.Info($"Ticket #{id} filed for '{title}' ({imdbId}).");
        TicketsChanged?.Invoke();

        return new LookupTicket
        {
            Id = id,
            Title = title.Trim(),
            Year = year,
            MediaType = string.IsNullOrWhiteSpace(mediaType) ? "Movie" : mediaType.Trim(),
            ImdbId = imdbId.Trim(),
            PosterUrl = posterUrl,
            Status = TicketStatuses.Open,
            AvailabilitySummary = availabilitySummary ?? string.Empty,
            SiteResults = siteResults?.ToList() ?? [],
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CreatedAt = now
        };
    }

    public async Task<IReadOnlyList<LookupTicket>> GetTicketsAsync(
        string? statusFilter = null,
        string? search = null,
        CancellationToken ct = default)
    {
        var results = new List<LookupTicket>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(statusFilter) && !statusFilter.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            conditions.Add("Status = @status");
            cmd.Parameters.AddWithValue("@status", statusFilter.Trim());
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            conditions.Add("(Title LIKE @q OR ImdbId LIKE @q OR AvailabilitySummary LIKE @q OR Notes LIKE @q)");
            cmd.Parameters.AddWithValue("@q", $"%{search.Trim()}%");
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        cmd.CommandText = $"""
            SELECT Id, Title, Year, MediaType, ImdbId, PosterUrl, Status, AvailabilitySummary,
                   SiteResultsJson, Notes, CreatedAt, CompletedAt
            FROM LookupTickets
            {where}
            ORDER BY CASE Status WHEN 'Open' THEN 0 ELSE 1 END, CreatedAt DESC, Id DESC
            LIMIT 300;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadTicket(reader));
        }

        return results;
    }

    public async Task<int> GetOpenCountAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LookupTickets WHERE Status = 'Open';";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task SetStatusAsync(int ticketId, string status, CancellationToken ct = default)
    {
        var completedAt = string.Equals(status, TicketStatuses.Completed, StringComparison.OrdinalIgnoreCase)
            ? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            : null;

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE LookupTickets
            SET Status = @status,
                CompletedAt = @completed
            WHERE Id = @id;
            """;
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@completed", (object?)completedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", ticketId);
        await cmd.ExecuteNonQueryAsync(ct);

        TicketsChanged?.Invoke();
    }

    /// <summary>
    /// Automatically marks any Open ticket matching an imported movie's IMDb ID
    /// (or exact title + optional year) as Completed.
    /// </summary>
    public async Task<int> AutoCompleteMatchingTicketsAsync(
        string? imdbId,
        string? title,
        int? year,
        CancellationToken ct = default)
    {
        var cleanImdb = (imdbId ?? string.Empty).Trim();
        var cleanTitle = (title ?? string.Empty).Trim();
        if (cleanImdb.Length == 0 && cleanTitle.Length == 0)
        {
            return 0;
        }

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        var conditions = new List<string>();
        await using var cmd = conn.CreateCommand();

        if (cleanImdb.Length > 0)
        {
            conditions.Add("ImdbId = @imdb");
            cmd.Parameters.AddWithValue("@imdb", cleanImdb);
        }

        if (cleanTitle.Length > 0)
        {
            if (year.HasValue)
            {
                conditions.Add("(Title = @title AND (Year IS NULL OR Year = @year))");
                cmd.Parameters.AddWithValue("@title", cleanTitle);
                cmd.Parameters.AddWithValue("@year", year.Value);
            }
            else
            {
                conditions.Add("Title = @title");
                cmd.Parameters.AddWithValue("@title", cleanTitle);
            }
        }

        var completedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        cmd.CommandText = $"""
            UPDATE LookupTickets
            SET Status = @completedStatus,
                CompletedAt = @completedAt
            WHERE Status = @openStatus
              AND ({string.Join(" OR ", conditions)});
            """;
        cmd.Parameters.AddWithValue("@completedStatus", TicketStatuses.Completed);
        cmd.Parameters.AddWithValue("@completedAt", completedAt);
        cmd.Parameters.AddWithValue("@openStatus", TicketStatuses.Open);

        var updated = await cmd.ExecuteNonQueryAsync(ct);
        if (updated > 0)
        {
            _logger?.Info($"Auto-completed {updated} open ticket(s) matching '{cleanTitle}' ({cleanImdb}).");
            TicketsChanged?.Invoke();
        }

        return updated;
    }

    /// <summary>
    /// Reconciles all Open tickets against the local Movies catalog (MediaTitles)
    /// and marks any ticket whose movie is now imported in the library as Completed.
    /// </summary>
    public async Task<int> AutoCompleteImportedMoviesAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        var completedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE LookupTickets
            SET Status = @completedStatus,
                CompletedAt = @completedAt
            WHERE Status = @openStatus
              AND EXISTS (
                  SELECT 1
                  FROM MediaTitles m
                  WHERE (LookupTickets.ImdbId != '' AND m.ImdbId IS NOT NULL AND lower(m.ImdbId) = lower(LookupTickets.ImdbId))
                     OR (lower(m.Title) = lower(LookupTickets.Title)
                         AND (LookupTickets.Year IS NULL OR m.ReleaseYear IS NULL OR m.ReleaseYear = LookupTickets.Year))
              );
            """;
        cmd.Parameters.AddWithValue("@completedStatus", TicketStatuses.Completed);
        cmd.Parameters.AddWithValue("@completedAt", completedAt);
        cmd.Parameters.AddWithValue("@openStatus", TicketStatuses.Open);

        var updated = await cmd.ExecuteNonQueryAsync(ct);
        if (updated > 0)
        {
            _logger?.Info($"Auto-completed {updated} open ticket(s) already present in local Movies library.");
            TicketsChanged?.Invoke();
        }

        return updated;
    }

    public async Task DeleteTicketAsync(int ticketId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM LookupTickets WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@id", ticketId);
        await cmd.ExecuteNonQueryAsync(ct);

        TicketsChanged?.Invoke();
    }

    public async Task<int> ClearCompletedAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM LookupTickets WHERE Status = 'Completed';";
        var removed = await cmd.ExecuteNonQueryAsync(ct);
        if (removed > 0)
        {
            TicketsChanged?.Invoke();
        }

        return removed;
    }

    private static LookupTicket ReadTicket(SqliteDataReader r)
    {
        List<TicketSiteResult> sites = [];
        try
        {
            var json = r.GetString(8);
            if (!string.IsNullOrWhiteSpace(json))
            {
                sites = JsonSerializer.Deserialize<List<TicketSiteResult>>(json) ?? [];
            }
        }
        catch (Exception)
        {
        }

        return new LookupTicket
        {
            Id = r.GetInt32(0),
            Title = r.GetString(1),
            Year = r.IsDBNull(2) ? null : r.GetInt32(2),
            MediaType = r.GetString(3),
            ImdbId = r.GetString(4),
            PosterUrl = r.IsDBNull(5) ? null : r.GetString(5),
            Status = r.GetString(6),
            AvailabilitySummary = r.GetString(7),
            SiteResults = sites,
            Notes = r.IsDBNull(9) ? null : r.GetString(9),
            CreatedAt = DateTime.TryParse(r.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var c) ? c : DateTime.UtcNow,
            CompletedAt = !r.IsDBNull(11) && DateTime.TryParse(r.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var done) ? done : null
        };
    }
}
