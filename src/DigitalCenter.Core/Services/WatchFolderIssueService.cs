using System.Globalization;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Services;

/// <summary>
/// Unified SQLite-backed Watch Folder issues tracker for Maintenance.
/// Combines explicit Drama/Movie issues stored in WatchFolderIssues with
/// unresolved Movie Watch Folder failures, low-confidence (NoMatch) items,
/// and interrupted moves from MetadataImportLogs.
/// </summary>
public sealed class WatchFolderIssueService
{
    public event Action? IssuesChanged;

    private readonly AppPaths _paths;
    private readonly IDbContextFactory<DigitalCenterDbContext> _dbFactory;
    private readonly IAppLogger? _logger;

    public WatchFolderIssueService(
        AppPaths paths,
        IDbContextFactory<DigitalCenterDbContext> dbFactory,
        IAppLogger? logger = null)
    {
        _paths = paths;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    private string ConnectionString => $"Data Source={_paths.DatabaseFile};Default Timeout=30;Pooling=True";

    public async Task RecordIssueAsync(
        string mediaType,
        string category,
        string sourcePath,
        string reason,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        var fullPath = NormalizePath(sourcePath);
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = fullPath;
        }

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO WatchFolderIssues (MediaType, Category, FileName, SourcePath, Reason, Timestamp)
            VALUES (@type, @cat, @fname, @path, @reason, @now)
            ON CONFLICT(MediaType, SourcePath) DO UPDATE SET
                Category = excluded.Category,
                FileName = excluded.FileName,
                Reason = excluded.Reason,
                Timestamp = excluded.Timestamp;
            """;
        cmd.Parameters.AddWithValue("@type", mediaType);
        cmd.Parameters.AddWithValue("@cat", category);
        cmd.Parameters.AddWithValue("@fname", fileName);
        cmd.Parameters.AddWithValue("@path", fullPath);
        cmd.Parameters.AddWithValue("@reason", string.IsNullOrWhiteSpace(reason) ? "Unknown error." : reason.Trim());
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);

        IssuesChanged?.Invoke();
    }

    /// <summary>Removes an issue automatically when a later scan successfully imports that file.</summary>
    public async Task ResolveIssueAsync(string mediaType, string sourcePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        var fullPath = NormalizePath(sourcePath);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM WatchFolderIssues WHERE MediaType = @type AND SourcePath = @path;";
        cmd.Parameters.AddWithValue("@type", mediaType);
        cmd.Parameters.AddWithValue("@path", fullPath);
        var removed = await cmd.ExecuteNonQueryAsync(ct);
        if (removed > 0)
        {
            IssuesChanged?.Invoke();
        }
    }

    public async Task<IReadOnlyList<WatchFolderIssue>> GetIssuesAsync(CancellationToken ct = default)
    {
        var results = new List<WatchFolderIssue>();
        var seenMoviePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Explicit WatchFolderIssues rows (Drama issues + any direct Movie issues)
        await using (var conn = new SqliteConnection(ConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, MediaType, Category, FileName, SourcePath, Reason, Timestamp
                FROM WatchFolderIssues
                ORDER BY Timestamp DESC, Id DESC;
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var item = new WatchFolderIssue
                {
                    Id = r.GetInt32(0),
                    MediaType = r.GetString(1),
                    Category = r.GetString(2),
                    FileName = r.GetString(3),
                    SourcePath = r.GetString(4),
                    Reason = r.GetString(5),
                    Timestamp = DateTime.TryParse(r.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
                        ? ts
                        : DateTime.UtcNow
                };
                results.Add(item);
                if (string.Equals(item.MediaType, WatchFolderIssueTypes.Movie, StringComparison.OrdinalIgnoreCase))
                {
                    seenMoviePaths.Add(item.SourcePath);
                }
            }
        }

        // 2. Unresolved Movie Watch Folder issues from MetadataImportLogs
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var movieLogs = await db.MetadataImportLogs
                .AsNoTracking()
                .Where(l => l.Status == ImportStatuses.Failed ||
                            l.Status == ImportStatuses.NoMatch ||
                            l.Status == ImportStatuses.Approved)
                .OrderByDescending(l => l.Timestamp)
                .ToListAsync(ct);

            foreach (var log in movieLogs)
            {
                var sourcePath = string.IsNullOrWhiteSpace(log.SourcePath) ? log.SourceFile : log.SourcePath;
                if (seenMoviePaths.Contains(sourcePath))
                {
                    continue;
                }

                var category = log.Status switch
                {
                    ImportStatuses.NoMatch => WatchFolderIssueCategories.LowConfidence,
                    ImportStatuses.Approved => WatchFolderIssueCategories.Interrupted,
                    _ => WatchFolderIssueCategories.Failed
                };

                var reason = !string.IsNullOrWhiteSpace(log.ErrorDetails)
                    ? log.ErrorDetails!
                    : !string.IsNullOrWhiteSpace(log.Note)
                        ? log.Note!
                        : log.Status switch
                        {
                            ImportStatuses.NoMatch => "No confident TMDb match found — rename the file or lower confidence in Settings.",
                            ImportStatuses.Approved => "Move was interrupted before completion — click Retry to recover.",
                            _ => "Movie import failed."
                        };

                results.Add(new WatchFolderIssue
                {
                    MovieImportLogId = log.Id,
                    MediaType = WatchFolderIssueTypes.Movie,
                    Category = category,
                    FileName = log.SourceFile,
                    SourcePath = sourcePath,
                    Reason = reason,
                    Timestamp = log.Timestamp
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Reading Movie Watch Folder issues failed: {ex.Message}");
        }

        return results.OrderByDescending(i => i.Timestamp).ToList();
    }

    public async Task<int> GetUnresolvedCountAsync(CancellationToken ct = default)
    {
        var items = await GetIssuesAsync(ct);
        return items.Count;
    }

    public async Task DismissIssueAsync(WatchFolderIssue issue, CancellationToken ct = default)
    {
        var changed = false;
        if (issue.Id > 0)
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM WatchFolderIssues WHERE Id = @id;";
            cmd.Parameters.AddWithValue("@id", issue.Id);
            changed |= await cmd.ExecuteNonQueryAsync(ct) > 0;
        }

        if (issue.MovieImportLogId is { } logId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var removed = await db.MetadataImportLogs
                .Where(l => l.Id == logId && l.Status != ImportStatuses.Imported)
                .ExecuteDeleteAsync(ct);
            changed |= removed > 0;
        }

        if (changed)
        {
            IssuesChanged?.Invoke();
        }
    }

    public async Task<int> ClearAllIssuesAsync(CancellationToken ct = default)
    {
        var removed = 0;

        await using (var conn = new SqliteConnection(ConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM WatchFolderIssues;";
            removed += await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            removed += await db.MetadataImportLogs
                .Where(l => l.Status == ImportStatuses.Failed || l.Status == ImportStatuses.NoMatch)
                .ExecuteDeleteAsync(ct);
        }

        if (removed > 0)
        {
            _logger?.Info($"Cleared {removed} Watch Folder issue(s) from Maintenance.");
            IssuesChanged?.Invoke();
        }

        return removed;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception)
        {
            return path.Trim();
        }
    }
}
