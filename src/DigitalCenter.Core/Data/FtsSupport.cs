using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Data;

/// <summary>
/// Installs and maintains the SQLite FTS5 index over media titles and their
/// associated people. The FTS table is managed with raw SQL (EF Core has no
/// first-class FTS support) and kept in sync with triggers, so every code path
/// that writes MediaTitles / TitlePeople through this database automatically
/// updates the index.
/// </summary>
public static class FtsSupport
{
    public const string FtsTableName = "MediaTitleFts";

    /// <summary>True when the SQLite build used by this process supports FTS5.</summary>
    public static bool Fts5Available(DbContext db)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_compile_options WHERE compile_options = 'ENABLE_FTS5';";
        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            command.Connection.Open();
        }

        var result = command.ExecuteScalar();
        return Convert.ToInt64(result) > 0;
    }

    /// <summary>
    /// Idempotently creates the FTS5 virtual table and sync triggers. Safe to
    /// call on every startup. Returns false when FTS5 is unavailable (the app
    /// then falls back to LIKE-based search).
    /// </summary>
    public static async Task<bool> EnsureInstalledAsync(DbContext db, CancellationToken ct = default)
    {
        if (!Fts5Available(db))
        {
            return false;
        }

        var ddl = $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS {FtsTableName} USING fts5(
                Title,
                People,
                MediaTitleId UNINDEXED,
                tokenize = 'unicode61 remove_diacritics 2'
            );

            CREATE TRIGGER IF NOT EXISTS trg_MediaTitles_fts_insert
            AFTER INSERT ON MediaTitles
            BEGIN
                INSERT INTO {FtsTableName}(Title, People, MediaTitleId)
                VALUES (
                    new.Title,
                    COALESCE((SELECT group_concat(p.Name, ' ') FROM TitlePeople tp JOIN People p ON p.Id = tp.PersonId WHERE tp.MediaTitleId = new.Id), ''),
                    new.Id
                );
            END;

            CREATE TRIGGER IF NOT EXISTS trg_MediaTitles_fts_update
            AFTER UPDATE OF Title ON MediaTitles
            BEGIN
                UPDATE {FtsTableName} SET Title = new.Title WHERE MediaTitleId = new.Id;
            END;

            CREATE TRIGGER IF NOT EXISTS trg_MediaTitles_fts_delete
            AFTER DELETE ON MediaTitles
            BEGIN
                DELETE FROM {FtsTableName} WHERE MediaTitleId = old.Id;
            END;

            CREATE TRIGGER IF NOT EXISTS trg_TitlePeople_fts_insert
            AFTER INSERT ON TitlePeople
            BEGIN
                UPDATE {FtsTableName}
                SET People = COALESCE((SELECT group_concat(p.Name, ' ') FROM TitlePeople tp JOIN People p ON p.Id = tp.PersonId WHERE tp.MediaTitleId = new.MediaTitleId), '')
                WHERE MediaTitleId = new.MediaTitleId;
            END;

            CREATE TRIGGER IF NOT EXISTS trg_TitlePeople_fts_delete
            AFTER DELETE ON TitlePeople
            BEGIN
                UPDATE {FtsTableName}
                SET People = COALESCE((SELECT group_concat(p.Name, ' ') FROM TitlePeople tp JOIN People p ON p.Id = tp.PersonId WHERE tp.MediaTitleId = old.MediaTitleId), '')
                WHERE MediaTitleId = old.MediaTitleId;
            END;
            """;

        await db.Database.ExecuteSqlRawAsync(ddl, ct);
        return true;
    }

    /// <summary>
    /// Builds a safe FTS5 MATCH expression from user input. Each whitespace
    /// separated term becomes a quoted prefix term, so "star trek" becomes
    /// "star"* "trek"* and raw operators typed by the user lose their power.
    /// </summary>
    public static string BuildMatchExpression(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(' ', terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\"*"));
    }
}
