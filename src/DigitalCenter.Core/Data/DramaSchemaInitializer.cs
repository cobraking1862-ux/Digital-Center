using System.Data;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Data;

/// <summary>
/// Idempotently creates and upgrades the Dramas, DramaEpisodes, DramaCast,
/// DramaCartItems, DramaWatchHistory, and WatchFolderIssues tables inside
/// DigitalCenter's existing SQLite database without disturbing EF Core's
/// migration history or model snapshot.
/// </summary>
public static class DramaSchemaInitializer
{
    public static async Task EnsureInstalledAsync(DbContext db, CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS Dramas (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Title TEXT NOT NULL COLLATE NOCASE,
                OriginalTitle TEXT NULL,
                FolderPath TEXT NOT NULL COLLATE NOCASE,
                Year INTEGER NULL,
                Overview TEXT NULL,
                Language TEXT NULL,
                Channel TEXT NULL,
                Status TEXT NOT NULL DEFAULT 'Ongoing',
                StatusLocked INTEGER NOT NULL DEFAULT 0,
                TitleLocked INTEGER NOT NULL DEFAULT 0,
                ExpectedEpisodeCount INTEGER NULL,
                TmdbId INTEGER NULL,
                ImdbId TEXT NULL,
                VoteAverage REAL NULL,
                PosterPath TEXT NULL,
                BackdropPath TEXT NULL,
                Genres TEXT NULL,
                LastScannedAt TEXT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_Dramas_FolderPath ON Dramas (FolderPath);
            CREATE INDEX IF NOT EXISTS IX_Dramas_Title ON Dramas (Title);
            CREATE INDEX IF NOT EXISTS IX_Dramas_Status ON Dramas (Status);

            CREATE TABLE IF NOT EXISTS DramaEpisodes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DramaId INTEGER NOT NULL,
                SeasonNumber INTEGER NOT NULL DEFAULT 1,
                EpisodeNumber INTEGER NOT NULL DEFAULT 0,
                Title TEXT NOT NULL,
                FileName TEXT NOT NULL,
                FilePath TEXT NOT NULL COLLATE NOCASE,
                FileSize INTEGER NOT NULL DEFAULT 0,
                YouTubeVideoId TEXT NULL COLLATE NOCASE,
                CopyCount INTEGER NOT NULL DEFAULT 0,
                LastCopiedAt TEXT NULL,
                AddedAt TEXT NOT NULL,
                FOREIGN KEY (DramaId) REFERENCES Dramas(Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_DramaEpisodes_FilePath ON DramaEpisodes (FilePath);
            CREATE INDEX IF NOT EXISTS IX_DramaEpisodes_DramaId ON DramaEpisodes (DramaId, SeasonNumber, EpisodeNumber);

            CREATE TABLE IF NOT EXISTS DramaCast (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DramaId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                Character TEXT NULL,
                ProfilePath TEXT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (DramaId) REFERENCES Dramas(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_DramaCast_DramaId ON DramaCast (DramaId, SortOrder);

            CREATE TABLE IF NOT EXISTS DramaCartItems (
                EpisodeId INTEGER PRIMARY KEY,
                AddedAt TEXT NOT NULL,
                FOREIGN KEY (EpisodeId) REFERENCES DramaEpisodes(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS DramaWatchHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DramaId INTEGER NULL,
                DramaTitle TEXT NOT NULL,
                SeasonNumber INTEGER NOT NULL DEFAULT 1,
                EpisodeNumber INTEGER NOT NULL DEFAULT 0,
                SourceFileName TEXT NOT NULL,
                SourcePath TEXT NOT NULL,
                TargetPath TEXT NOT NULL,
                FileSize INTEGER NOT NULL DEFAULT 0,
                Status TEXT NOT NULL DEFAULT 'Imported',
                Timestamp TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_DramaWatchHistory_Timestamp ON DramaWatchHistory (Timestamp DESC);

            CREATE TABLE IF NOT EXISTS WatchFolderIssues (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MediaType TEXT NOT NULL,
                Category TEXT NOT NULL,
                FileName TEXT NOT NULL,
                SourcePath TEXT NOT NULL COLLATE NOCASE,
                Reason TEXT NOT NULL,
                Timestamp TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_WatchFolderIssues_MediaSource ON WatchFolderIssues (MediaType, SourcePath);
            CREATE INDEX IF NOT EXISTS IX_WatchFolderIssues_Timestamp ON WatchFolderIssues (Timestamp DESC);
            """;

        await db.Database.ExecuteSqlRawAsync(ddl, ct);

        await EnsureColumnsAsync(db, "Dramas",
        [
            ("OriginalTitle", "TEXT NULL"),
            ("Year", "INTEGER NULL"),
            ("Overview", "TEXT NULL"),
            ("Language", "TEXT NULL"),
            ("Channel", "TEXT NULL"),
            ("Status", "TEXT NOT NULL DEFAULT 'Ongoing'"),
            ("StatusLocked", "INTEGER NOT NULL DEFAULT 0"),
            ("TitleLocked", "INTEGER NOT NULL DEFAULT 0"),
            ("ExpectedEpisodeCount", "INTEGER NULL"),
            ("TmdbId", "INTEGER NULL"),
            ("ImdbId", "TEXT NULL"),
            ("VoteAverage", "REAL NULL"),
            ("PosterPath", "TEXT NULL"),
            ("BackdropPath", "TEXT NULL"),
            ("Genres", "TEXT NULL"),
            ("LastScannedAt", "TEXT NULL"),
            ("CreatedAt", "TEXT NOT NULL DEFAULT ''")
        ], ct);

        await EnsureColumnsAsync(db, "DramaEpisodes",
        [
            ("SeasonNumber", "INTEGER NOT NULL DEFAULT 1"),
            ("EpisodeNumber", "INTEGER NOT NULL DEFAULT 0"),
            ("YouTubeVideoId", "TEXT NULL"),
            ("CopyCount", "INTEGER NOT NULL DEFAULT 0"),
            ("LastCopiedAt", "TEXT NULL"),
            ("AddedAt", "TEXT NOT NULL DEFAULT ''")
        ], ct);
    }

    private static async Task EnsureColumnsAsync(
        DbContext db,
        string tableName,
        IReadOnlyList<(string ColumnName, string ColumnDef)> requiredColumns,
        CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose)
        {
            await conn.OpenAsync(ct);
        }

        try
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({tableName});";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    existing.Add(reader.GetString(1));
                }
            }

            foreach (var (colName, colDef) in requiredColumns)
            {
                if (existing.Contains(colName))
                {
                    continue;
                }

                await using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {colName} {colDef};";
                await alter.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            if (shouldClose && conn.State == ConnectionState.Open)
            {
                await conn.CloseAsync();
            }
        }
    }
}
