using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Services;

public sealed record WatchScanResult(
    int FilesSeen,
    int NewPending,
    int AlreadyKnown,
    int Unstable,
    int UnsupportedLogged,
    List<string> UnstableFiles);

/// <summary>
/// Monitors the single default Watch Folder for new files. Detection is
/// polling-based (robust against network folders and partially written
/// downloads) and creates one <see cref="MetadataImportLog"/> row per file:
/// media files go through the automatic metadata match right away, everything
/// else is logged as Unsupported. Files that are still growing (size changes
/// between checks) are retried on a later pass, never imported prematurely.
/// The service never moves or deletes anything; the operator confirms every
/// move in the Management workspace.
/// </summary>
public sealed class WatchFolderService : IDisposable
{
    /// <summary>How long a file must keep its size to count as stable. Configurable so tests stay fast.</summary>
    public TimeSpan StabilityDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Raised after a scan changed the pending entries (UI refresh hook).</summary>
    public event Action? PendingImportsChanged;

    private readonly IDbContextFactory<DigitalCenterDbContext> _factory;
    private readonly FilenameParser _parser;
    private readonly ImportService _imports;
    private readonly SettingsService _settings;
    private readonly IAppLogger? _logger;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private readonly Lock _lifecycleLock = new();

    public WatchFolderService(
        IDbContextFactory<DigitalCenterDbContext> factory,
        FilenameParser parser,
        ImportService imports,
        SettingsService settings,
        IAppLogger? logger = null)
    {
        _factory = factory;
        _parser = parser;
        _imports = imports;
        _settings = settings;
        _logger = logger;
    }

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public void Start(string watchFolderPath, TimeSpan? pollInterval = null)
    {
        lock (_lifecycleLock)
        {
            if (IsRunning)
            {
                return;
            }

            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            _loopTask = Task.Run(() => PollLoopAsync(watchFolderPath, pollInterval ?? TimeSpan.FromSeconds(15), token), CancellationToken.None);
            _logger?.Info($"Watch Folder monitoring started for '{watchFolderPath}'.");
        }
    }

    public void Stop()
    {
        Task? loop;
        lock (_lifecycleLock)
        {
            _loopCts?.Cancel();
            loop = _loopTask;
            _loopCts?.Dispose();
            _loopCts = null;
            _loopTask = null;
        }

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // Shutdown must never hang on the poll loop.
        }

        _logger?.Info("Watch Folder monitoring stopped.");
    }

    private async Task PollLoopAsync(string watchFolder, TimeSpan interval, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RescanOnceAsync(watchFolder, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed pass (folder temporarily missing, drive offline)
                // must never end the monitoring loop.
                _logger?.Error("Watch Folder pass failed; will retry next interval.", ex);
            }

            try
            {
                await Task.Delay(interval, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One full pass: detect new files, then run the automatic metadata match
    /// over every still-pending import. Also used by the "Rescan Now" action.
    /// Idempotent: a file already has exactly one import log (unique
    /// SourcePath index) no matter how often it is seen.
    /// </summary>
    public async Task<WatchScanResult> RescanOnceAsync(string watchFolder, CancellationToken ct = default)
    {
        var result = await ScanOnceAsync(watchFolder, ct);
        await _imports.MatchPendingAsync(ct);
        return result;
    }

    public async Task<WatchScanResult> ScanOnceAsync(string watchFolder, CancellationToken ct = default)
    {
        var result = new WatchScanResult(0, 0, 0, 0, 0, []);
        if (string.IsNullOrWhiteSpace(watchFolder) || !Directory.Exists(watchFolder))
        {
            return result;
        }

        var settings = _settings.Load();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = settings.WatchFolderIncludeSubfolders,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        var db = await _factory.CreateDbContextAsync(ct);
        try
        {
            foreach (var path in Directory.EnumerateFiles(watchFolder, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                result = result with { FilesSeen = result.FilesSeen + 1 };

                var fullPath = Path.GetFullPath(path);
                var isMedia = LibraryScanner.MediaExtensions.Contains(Path.GetExtension(path));

                var fileName = Path.GetFileName(fullPath);
                var parsed = _parser.Parse(fileName, Path.GetDirectoryName(fullPath));

                if (await db.MetadataImportLogs.AnyAsync(l => l.SourcePath == fullPath, ct))
                {
                    result = result with { AlreadyKnown = result.AlreadyKnown + 1 };
                    continue;
                }

                if (!isMedia)
                {
                    // Non-media files are logged once as Unsupported so the
                    // activity list explains what happened to them. The file
                    // itself is never touched.
                    db.MetadataImportLogs.Add(new MetadataImportLog
                    {
                        SourceFile = fileName,
                        SourcePath = fullPath,
                        Status = ImportStatuses.Unsupported,
                        FileSize = GetFileSize(fullPath),
                        IsSeries = parsed.IsSeries,
                        ParsedTitle = parsed.Title,
                        ParsedYear = parsed.Year,
                        ParsedSeason = parsed.Season,
                        ParsedEpisode = parsed.Episode,
                        MetadataSource = "WatchFolder",
                        ErrorDetails = $"'{(string.IsNullOrEmpty(Path.GetExtension(path)) ? "(no extension)" : Path.GetExtension(path))}' is not a supported video file; the file stays in place.",
                        Timestamp = DateTime.UtcNow
                    });

                    try
                    {
                        await db.SaveChangesAsync(ct);
                        result = result with { UnsupportedLogged = result.UnsupportedLogged + 1 };
                    }
                    catch (DbUpdateException ex)
                    {
                        _logger?.Warn($"Watch Folder entry for '{fileName}' could not be added (likely already known): {ex.InnerException?.Message ?? ex.Message}");
                        db.ChangeTracker.Clear();
                    }

                    continue;
                }

                if (!await IsFileStableAsync(fullPath, ct))
                {
                    result = result with
                    {
                        Unstable = result.Unstable + 1,
                        UnstableFiles = [.. result.UnstableFiles, fullPath]
                    };
                    _logger?.Info($"Watch Folder file not yet stable, will retry later: '{fileName}'.");
                    continue;
                }

                db.MetadataImportLogs.Add(new MetadataImportLog
                {
                    SourceFile = fileName,
                    SourcePath = fullPath,
                    Status = ImportStatuses.Pending,
                    Stage = ImportStages.Detected,
                    FileSize = new FileInfo(fullPath).Length,
                    IsSeries = parsed.IsSeries,
                    ParsedTitle = parsed.Title,
                    ParsedYear = parsed.Year,
                    ParsedSeason = parsed.Season,
                    ParsedEpisode = parsed.Episode,
                    MetadataSource = "WatchFolder",
                    Timestamp = DateTime.UtcNow
                });

                try
                {
                    await db.SaveChangesAsync(ct);
                    result = result with { NewPending = result.NewPending + 1 };
                    _logger?.Info($"Watch Folder detected new file: '{fileName}' (series={parsed.IsSeries}).");
                }
                catch (DbUpdateException ex)
                {
                    // Concurrent detection (two poll passes racing) can hit the
                    // unique SourcePath index; that is not an error. Drop the
                    // failed entry from the tracker and continue the pass.
                    _logger?.Warn($"Watch Folder entry for '{fileName}' could not be added (likely already known): {ex.InnerException?.Message ?? ex.Message}");
                    db.ChangeTracker.Clear();
                }
            }
        }
        finally
        {
            await db.DisposeAsync();
        }

        if (result.NewPending > 0 || result.UnsupportedLogged > 0)
        {
            PendingImportsChanged?.Invoke();
        }

        return result;
    }

    /// <summary>Size must stay identical across two checks and the file must be readable.</summary>
    private async Task<bool> IsFileStableAsync(string path, CancellationToken ct)
    {
        try
        {
            var first = new FileInfo(path);
            if (!first.Exists || first.Length == 0)
            {
                return false;
            }

            var firstSize = first.Length;
            var firstWrite = first.LastWriteTimeUtc;

            await Task.Delay(StabilityDelay, ct);

            var second = new FileInfo(path);
            if (!second.Exists || second.Length != firstSize || second.LastWriteTimeUtc != firstWrite)
            {
                return false;
            }

            // Accessibility probe: open, read a byte, close.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var buffer = new byte[1];
            return await stream.ReadAsync(buffer, ct) >= 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long GetFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public void Dispose() => Stop();
}
