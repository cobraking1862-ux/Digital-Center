using System.Diagnostics;
using System.IO;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Services;

/// <summary>Live storage, catalog, and health metrics for digitalcenter.db and app data folders.</summary>
public sealed record DatabaseHealthSnapshot(
    long DatabaseBytes,
    long WalAndShmBytes,
    long ReclaimableBytes,
    string JournalMode,
    int MovieCount,
    int MovieFileCount,
    int DramaCount,
    int DramaEpisodeCount,
    int OpenTicketCount,
    int WatchFolderIssueCount,
    int FailedImportLogCount,
    int LogFileCount,
    long LogFilesBytes,
    int PosterCacheFileCount,
    long PosterCacheBytes,
    DateTime MeasuredAtUtc);

/// <summary>Result of SQLite PRAGMA integrity_check and foreign_key_check.</summary>
public sealed record DatabaseIntegrityReport(
    bool IsHealthy,
    IReadOnlyList<string> IntegrityMessages,
    IReadOnlyList<string> ForeignKeyViolations,
    TimeSpan Duration);

/// <summary>Result of WAL checkpoint, VACUUM, and ANALYZE optimization.</summary>
public sealed record DatabaseOptimizeResult(
    long SizeBeforeBytes,
    long SizeAfterBytes,
    long BytesReclaimed,
    int WalPagesCheckpointed,
    TimeSpan Duration);

/// <summary>Result of pruning movie files that no longer exist on disk.</summary>
public sealed record MissingMoviePruneResult(
    int MissingFilesRemoved,
    int EmptyTitlesRemoved,
    TimeSpan Duration);

/// <summary>Result of deleting unreferenced images from the Posters cache directory.</summary>
public sealed record OrphanPosterCleanupResult(
    int FilesScanned,
    int FilesDeleted,
    long BytesFreed,
    TimeSpan Duration);

/// <summary>Result of clearing failed import logs, watch folder issues, and old log files.</summary>
public sealed record ErrorLogPurgeResult(
    int FailedMovieLogsDeleted,
    int WatchFolderIssuesDeleted,
    int OldLogFilesDeleted,
    long LogBytesFreed,
    TimeSpan Duration);

/// <summary>
/// Provides safe, built-in SQLite database maintenance, integrity diagnostics,
/// optimization (WAL checkpoint + VACUUM + ANALYZE), missing-movie pruning,
/// orphaned poster cache cleanup, and error/log purging so operators never
/// need to run manual SQL queries against digitalcenter.db.
/// </summary>
public sealed class DatabaseMaintenanceService
{
    private readonly IDbContextFactory<DigitalCenterDbContext> _factory;
    private readonly AppPaths _paths;
    private readonly IAppLogger? _logger;

    public DatabaseMaintenanceService(
        IDbContextFactory<DigitalCenterDbContext> factory,
        AppPaths paths,
        IAppLogger? logger = null)
    {
        _factory = factory;
        _paths = paths;
        _logger = logger;
    }

    public async Task<DatabaseHealthSnapshot> GetHealthSnapshotAsync(CancellationToken ct = default)
    {
        var dbBytes = SafeFileLength(_paths.DatabaseFile);
        var walShmBytes = SafeFileLength(_paths.DatabaseFile + "-wal") +
                          SafeFileLength(_paths.DatabaseFile + "-shm");

        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        var journalMode = await ExecuteScalarStringAsync(conn, "PRAGMA journal_mode;", ct) ?? "unknown";
        var pageSize = await ExecuteScalarLongAsync(conn, "PRAGMA page_size;", ct);
        var freelistCount = await ExecuteScalarLongAsync(conn, "PRAGMA freelist_count;", ct);
        var reclaimableBytes = Math.Max(0, pageSize * freelistCount);

        var movieCount = await CountRowsAsync(conn, "MediaTitles", null, ct);
        var movieFileCount = await CountRowsAsync(conn, "MediaFiles", null, ct);
        var dramaCount = await CountRowsAsync(conn, "Dramas", null, ct);
        var dramaEpisodeCount = await CountRowsAsync(conn, "DramaEpisodes", null, ct);
        var openTickets = await CountRowsAsync(conn, "LookupTickets", "Status = 'Open'", ct);
        var watchIssues = await CountRowsAsync(conn, "WatchFolderIssues", null, ct);
        var failedLogs = await CountRowsAsync(conn, "MetadataImportLogs", "Status IN ('Failed', 'NoMatch', 'Unsupported')", ct);

        var (logCount, logBytes) = MeasureDirectory(_paths.LogsDirectory);
        var (posterCount, posterBytes) = MeasureDirectory(_paths.PostersDirectory);

        return new DatabaseHealthSnapshot(
            DatabaseBytes: dbBytes,
            WalAndShmBytes: walShmBytes,
            ReclaimableBytes: reclaimableBytes,
            JournalMode: journalMode,
            MovieCount: movieCount,
            MovieFileCount: movieFileCount,
            DramaCount: dramaCount,
            DramaEpisodeCount: dramaEpisodeCount,
            OpenTicketCount: openTickets,
            WatchFolderIssueCount: watchIssues,
            FailedImportLogCount: failedLogs,
            LogFileCount: logCount,
            LogFilesBytes: logBytes,
            PosterCacheFileCount: posterCount,
            PosterCacheBytes: posterBytes,
            MeasuredAtUtc: DateTime.UtcNow);
    }

    public async Task<DatabaseIntegrityReport> RunIntegrityCheckAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var integrityMessages = new List<string>();
        var fkViolations = new List<string>();

        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA integrity_check;";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                integrityMessages.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var table = reader.IsDBNull(0) ? "?" : reader.GetString(0);
                var rowId = reader.IsDBNull(1) ? "?" : reader.GetValue(1).ToString();
                var parent = reader.IsDBNull(2) ? "?" : reader.GetString(2);
                fkViolations.Add($"Table '{table}' row {rowId} references missing parent in '{parent}'");
            }
        }

        sw.Stop();
        var isHealthy = integrityMessages.Count == 1 &&
                        string.Equals(integrityMessages[0], "ok", StringComparison.OrdinalIgnoreCase) &&
                        fkViolations.Count == 0;

        _logger?.Info($"Database integrity check finished in {sw.ElapsedMilliseconds} ms (Healthy={isHealthy}, FKViolations={fkViolations.Count}).");
        return new DatabaseIntegrityReport(isHealthy, integrityMessages, fkViolations, sw.Elapsed);
    }

    public async Task<DatabaseOptimizeResult> OptimizeDatabaseAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var sizeBefore = SafeFileLength(_paths.DatabaseFile) +
                         SafeFileLength(_paths.DatabaseFile + "-wal") +
                         SafeFileLength(_paths.DatabaseFile + "-shm");

        var checkpointedPages = 0;

        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        // 1. Flush WAL journal into main database file
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct) && reader.FieldCount >= 3 && !reader.IsDBNull(2))
            {
                checkpointedPages = reader.GetInt32(2);
            }
        }

        // 2. Compact database and reclaim freelist pages
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "VACUUM;";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 3. Refresh SQLite query planner statistics
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "ANALYZE;";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        sw.Stop();
        var sizeAfter = SafeFileLength(_paths.DatabaseFile) +
                        SafeFileLength(_paths.DatabaseFile + "-wal") +
                        SafeFileLength(_paths.DatabaseFile + "-shm");
        var reclaimed = Math.Max(0, sizeBefore - sizeAfter);

        _logger?.Info($"Database optimized (WAL checkpoint + VACUUM + ANALYZE) in {sw.ElapsedMilliseconds} ms; reclaimed {reclaimed} byte(s).");
        return new DatabaseOptimizeResult(sizeBefore, sizeAfter, reclaimed, checkpointedPages, sw.Elapsed);
    }

    public async Task<MissingMoviePruneResult> RemoveMissingMovieFilesAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await using var db = await _factory.CreateDbContextAsync(ct);

        var allFiles = await db.MediaFiles.ToListAsync(ct);
        var missing = allFiles.Where(f => !File.Exists(f.FilePath)).ToList();
        if (missing.Count == 0)
        {
            sw.Stop();
            return new MissingMoviePruneResult(0, 0, sw.Elapsed);
        }

        var affectedMovieTitleIds = missing
            .Where(f => f.MovieTitleId.HasValue)
            .Select(f => f.MovieTitleId!.Value)
            .Distinct()
            .ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.MediaFiles.RemoveRange(missing);
        await db.SaveChangesAsync(ct);

        var emptyTitlesRemoved = 0;
        if (affectedMovieTitleIds.Count > 0)
        {
            var remainingIds = await db.MediaFiles
                .Where(f => f.MovieTitleId.HasValue && affectedMovieTitleIds.Contains(f.MovieTitleId.Value))
                .Select(f => f.MovieTitleId!.Value)
                .Distinct()
                .ToListAsync(ct);

            var orphanTitleIds = affectedMovieTitleIds.Except(remainingIds).ToList();
            if (orphanTitleIds.Count > 0)
            {
                var emptyTitles = await db.MediaTitles
                    .Where(t => t.Type == MediaKind.Movie && orphanTitleIds.Contains(t.Id))
                    .ToListAsync(ct);

                if (emptyTitles.Count > 0)
                {
                    db.MediaTitles.RemoveRange(emptyTitles);
                    await db.SaveChangesAsync(ct);
                    emptyTitlesRemoved = emptyTitles.Count;
                }
            }
        }

        await tx.CommitAsync(ct);
        sw.Stop();

        _logger?.Info($"Pruned {missing.Count} missing movie file(s) and {emptyTitlesRemoved} empty movie title(s).");
        return new MissingMoviePruneResult(missing.Count, emptyTitlesRemoved, sw.Elapsed);
    }

    public async Task<OrphanPosterCleanupResult> CleanOrphanedPosterCacheAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(_paths.PostersDirectory) || !Directory.Exists(_paths.PostersDirectory))
        {
            sw.Stop();
            return new OrphanPosterCleanupResult(0, 0, 0, sw.Elapsed);
        }

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        await CollectReferencedPathsAsync(conn, "MediaTitles", "PosterPath", referenced, ct);
        await CollectReferencedPathsAsync(conn, "MediaTitles", "BackdropPath", referenced, ct);
        await CollectReferencedPathsAsync(conn, "People", "ProfilePath", referenced, ct);
        await CollectReferencedPathsAsync(conn, "Dramas", "PosterPath", referenced, ct);
        await CollectReferencedPathsAsync(conn, "Dramas", "BackdropPath", referenced, ct);
        await CollectReferencedPathsAsync(conn, "DramaCast", "ProfilePath", referenced, ct);

        var scanned = 0;
        var deleted = 0;
        long bytesFreed = 0;

        foreach (var file in Directory.EnumerateFiles(_paths.PostersDirectory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            scanned++;

            var fullPath = Path.GetFullPath(file);
            var relative = Path.GetRelativePath(_paths.PostersDirectory, fullPath);
            var fileName = Path.GetFileName(fullPath);

            if (referenced.Contains(fullPath) ||
                referenced.Contains(relative) ||
                referenced.Contains(relative.Replace('\\', '/')) ||
                referenced.Contains(fileName))
            {
                continue;
            }

            try
            {
                var length = SafeFileLength(fullPath);
                File.Delete(fullPath);
                deleted++;
                bytesFreed += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip locked files safely
            }
        }

        sw.Stop();
        _logger?.Info($"Orphaned poster cache cleanup: scanned {scanned}, deleted {deleted} file(s), freed {bytesFreed} byte(s).");
        return new OrphanPosterCleanupResult(scanned, deleted, bytesFreed, sw.Elapsed);
    }

    public async Task<ErrorLogPurgeResult> PurgeErrorLogsAndReportsAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var failedLogsDeleted = 0;
        var issuesDeleted = 0;
        var logFilesDeleted = 0;
        long logBytesFreed = 0;

        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        if (await TableExistsAsync(conn, "MetadataImportLogs", ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM MetadataImportLogs WHERE Status IN ('Failed', 'NoMatch', 'Unsupported');";
            failedLogsDeleted = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (await TableExistsAsync(conn, "WatchFolderIssues", ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM WatchFolderIssues;";
            issuesDeleted = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!string.IsNullOrWhiteSpace(_paths.LogsDirectory) && Directory.Exists(_paths.LogsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(_paths.LogsDirectory, "*.*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".log", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var len = SafeFileLength(file);
                    File.Delete(file);
                    logFilesDeleted++;
                    logBytesFreed += len;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Active log file currently held open by FileLogger — leave untouched
                }
            }
        }

        sw.Stop();
        _logger?.Info($"Error & log purge complete: {failedLogsDeleted} import error(s), {issuesDeleted} watch issue(s), {logFilesDeleted} log file(s) removed.");
        return new ErrorLogPurgeResult(failedLogsDeleted, issuesDeleted, logFilesDeleted, logBytesFreed, sw.Elapsed);
    }

    // --------------------------------------------------------- Helpers

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string tableName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", tableName);
        var res = await cmd.ExecuteScalarAsync(ct);
        return res is not null && res != DBNull.Value;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string tableName, string columnName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(1) && string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<int> CountRowsAsync(SqliteConnection conn, string tableName, string? whereClause, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, tableName, ct))
        {
            return 0;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(whereClause)
            ? $"SELECT COUNT(*) FROM \"{tableName}\";"
            : $"SELECT COUNT(*) FROM \"{tableName}\" WHERE {whereClause};";
        var res = await cmd.ExecuteScalarAsync(ct);
        return res is null || res == DBNull.Value ? 0 : Convert.ToInt32(res);
    }

    private static async Task CollectReferencedPathsAsync(
        SqliteConnection conn,
        string tableName,
        string columnName,
        HashSet<string> target,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, tableName, ct) ||
            !await ColumnExistsAsync(conn, tableName, columnName, ct))
        {
            return;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"{columnName}\" FROM \"{tableName}\" WHERE \"{columnName}\" IS NOT NULL AND \"{columnName}\" != '';";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0)) continue;
            var raw = reader.GetString(0).Trim();
            if (raw.Length == 0) continue;

            target.Add(raw);
            target.Add(raw.Replace('\\', '/'));
            var name = Path.GetFileName(raw);
            if (!string.IsNullOrWhiteSpace(name))
            {
                target.Add(name);
            }
        }
    }

    private static async Task<string?> ExecuteScalarStringAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var res = await cmd.ExecuteScalarAsync(ct);
        return res is null || res == DBNull.Value ? null : res.ToString();
    }

    private static async Task<long> ExecuteScalarLongAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var res = await cmd.ExecuteScalarAsync(ct);
        return res is null || res == DBNull.Value ? 0 : Convert.ToInt64(res);
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static (int Count, long Bytes) MeasureDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return (0, 0);
        }

        var count = 0;
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                count++;
                bytes += SafeFileLength(file);
            }
        }
        catch
        {
            // Best-effort folder metrics
        }

        return (count, bytes);
    }
}
