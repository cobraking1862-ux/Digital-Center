using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;
using Microsoft.Data.Sqlite;

namespace DigitalCenter.Core.Services;

public sealed record BackupFileInfo(string FilePath, long Size, DateTime CreatedAt);

/// <summary>
/// Database backup and restore using the SQLite Online Backup API. Restores
/// always keep a safety copy of the current database and are validated
/// against the DigitalCenter schema before replacing anything.
/// </summary>
public sealed class BackupService
{
    private readonly AppPaths _paths;
    private readonly IAppLogger? _logger;

    public BackupService(AppPaths paths, IAppLogger? logger = null)
    {
        _paths = paths;
        _logger = logger;
    }

    public IReadOnlyList<BackupFileInfo> ListBackups()
    {
        if (!Directory.Exists(_paths.BackupsDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_paths.BackupsDirectory, "*.db")
            .Select(f => new FileInfo(f))
            .Where(fi => fi.Name.StartsWith("digitalcenter-", StringComparison.Ordinal))
            .Select(fi => new BackupFileInfo(fi.FullName, fi.Length, fi.CreationTimeUtc))
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    /// <summary>Creates a consistent backup of the current database and returns its path.</summary>
    public async Task<string> BackupAsync(string? targetFile = null, CancellationToken ct = default)
    {
        if (!File.Exists(_paths.DatabaseFile))
        {
            throw new FileNotFoundException("There is no database file to back up yet.", _paths.DatabaseFile);
        }

        var target = targetFile ?? Path.Combine(
            _paths.BackupsDirectory,
            $"digitalcenter-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await Task.Run(() =>
        {
            using var source = new SqliteConnection($"Data Source={_paths.DatabaseFile}");
            source.Open();

            using var destination = new SqliteConnection($"Data Source={target}");
            destination.Open();

            source.BackupDatabase(destination);
        }, ct);

        _logger?.Info($"Database backed up to '{target}'.");
        return target;
    }

    /// <summary>
    /// Restores a backup over the live database after validating it. A safety
    /// copy of the current database is written first, so a failed restore can
    /// always be rolled back manually.
    /// </summary>
    public async Task RestoreAsync(string backupFile, CancellationToken ct = default)
    {
        await ValidateBackupAsync(backupFile, ct);

        if (!File.Exists(_paths.DatabaseFile))
        {
            throw new FileNotFoundException("Current database not found; restore still possible, but no safety copy could be made.", _paths.DatabaseFile);
        }

        await Task.Run(() =>
        {
            // Release pooled connections so no open handle keeps the old page
            // image alive while the file is replaced (single-user app: no other
            // work runs during a restore).
            SqliteConnection.ClearAllPools();

            var safetyCopy = _paths.DatabaseFile + $".pre-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            File.Copy(_paths.DatabaseFile, safetyCopy, overwrite: true);
            _logger?.Info($"Safety copy of the current database written to '{safetyCopy}'.");

            // Remove stale WAL/SHM sidecars so the restored page image is used as-is.
            foreach (var sidecar in new[] { _paths.DatabaseFile + "-wal", _paths.DatabaseFile + "-shm" })
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }

            File.Copy(backupFile, _paths.DatabaseFile, overwrite: true);
            foreach (var sidecar in new[] { backupFile + "-wal", backupFile + "-shm" })
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }
        }, ct);

        _logger?.Info($"Database restored from '{backupFile}'. Operator should restart the application.");
    }

    /// <summary>Throws InvalidOperationException when the file is not a usable DigitalCenter database.</summary>
    public async Task ValidateBackupAsync(string backupFile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(backupFile) || !File.Exists(backupFile))
        {
            throw new InvalidOperationException($"Backup file does not exist: '{backupFile}'.");
        }

        var requiredTables = new[] { "MediaTitles", "__EFMigrationsHistory" };
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = backupFile,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        await Task.Run(() =>
        {
            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    found.Add(reader.GetString(0));
                }
            }
            catch (SqliteException ex)
            {
                throw new InvalidOperationException(
                    "The selected file is not a valid SQLite database. Nothing was changed.", ex);
            }
        }, ct);

        var missing = requiredTables.Where(t => !found.Contains(t)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "The selected file is not a valid DigitalCenter database backup (missing tables: " +
                string.Join(", ", missing) + "). Nothing was changed.");
        }

        _logger?.Info($"Backup '{backupFile}' validated successfully.");
    }
}
