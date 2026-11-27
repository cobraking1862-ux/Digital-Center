using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Data;

/// <summary>
/// Idempotently creates the LookupTickets table inside DigitalCenter's existing
/// SQLite database without disturbing EF Core's migration history.
/// </summary>
public static class TicketSchemaInitializer
{
    public static async Task EnsureInstalledAsync(DbContext db, CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS LookupTickets (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Title TEXT NOT NULL COLLATE NOCASE,
                Year INTEGER NULL,
                MediaType TEXT NOT NULL DEFAULT 'Movie',
                ImdbId TEXT NOT NULL COLLATE NOCASE,
                PosterUrl TEXT NULL,
                Status TEXT NOT NULL DEFAULT 'Open',
                AvailabilitySummary TEXT NOT NULL DEFAULT '',
                SiteResultsJson TEXT NOT NULL DEFAULT '[]',
                Notes TEXT NULL,
                CreatedAt TEXT NOT NULL,
                CompletedAt TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_LookupTickets_Status ON LookupTickets (Status);
            CREATE INDEX IF NOT EXISTS IX_LookupTickets_ImdbId ON LookupTickets (ImdbId);
            CREATE INDEX IF NOT EXISTS IX_LookupTickets_CreatedAt ON LookupTickets (CreatedAt);
            """;

        await db.Database.ExecuteSqlRawAsync(ddl, ct);
    }
}
