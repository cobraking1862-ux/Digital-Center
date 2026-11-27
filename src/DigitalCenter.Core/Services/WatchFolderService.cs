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
    List<string> UnstableFiles,
    int Retried = 0);

/// <summary>
/// Monitors the Watch Folder for new files. Detection is polling-based (robust
/// against network folders and partially written downloads) and creates one
/// <see cref="MetadataImportLog"/> row per file. Once detected as stable,
/// media files automatically go through metadata lookup and language-based
/// subfolder organization into the library root via <see cref="ImportService"/>.
/// </summary>
public sealed class WatchFolderService : IDisposable
{
    /// <summary>How long a file must keep its size to count as stable. Configurable so tests stay fast.</summary>
    public TimeSpan StabilityDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Raised after file detection, metadata matching, and auto-organization have completed.</summary>
    public event Action? PendingImportsChanged;

    /// <summary>Human-readable work status for the shell-wide activity feed.</summary>
    public event Action<string>? StatusChanged;

    private readonly IDbContextFactory<DigitalCenterDbContext> _factory;
    private readonly FilenameParser _parser;
    private readonly ImportService _imports;
    private readonly SettingsService _settings;
    private readonly IAppLogger? _logger;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private readonly Lock _lifecycleLock = new();

    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private HashSet<string>? _knownSourcePaths;
    private DateTime _knownSourcePathsRefreshedUtc;
    private static readonly TimeSpan KnownPathCacheLifetime = TimeSpan.FromMinutes(2);

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

    /// <summary>
    /// Clears the in-memory known-paths cache so the next scan reads fresh state
    /// from the database (e.g. after clearing Watch Folder history).
    /// </summary>
    public void InvalidateKnownPathsCache()
    {
        _knownSourcePaths = null;
        _knownSourcePathsRefreshedUtc = DateTime.MinValue;
    }

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
    /// One full pass: detect new files, then automatically fetch metadata from
    /// TMDb and move matched files into their collection or language sub-folders.
    /// When <paramref name="retryUnmatched"/> is true (manual Rescan Now), the
    /// known-paths cache is invalidated, any interrupted "Moving…" (Approved)
    /// imports are recovered, a short stability check is used, and any existing
    /// NoMatch/Failed/Matched/Imported logs whose files are physically present
    /// in the Watch Folder are reset to Pending so TMDb lookup runs immediately.
    /// </summary>
    public async Task<WatchScanResult> RescanOnceAsync(
        string watchFolder,
        CancellationToken ct = default,
        bool retryUnmatched = false)
    {
        await _scanGate.WaitAsync(ct);
        try
        {
            var retried = 0;
            var recoveredCount = 0;
            if (retryUnmatched)
            {
                InvalidateKnownPathsCache();
                var recovered = await _imports.RecoverInterruptedAsync(ct);
                recoveredCount = recovered.Count;
                retried = await ResetRetryableLogsAsync(watchFolder, ct);
            }

            var stabilityOverride = retryUnmatched
                ? TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(0, StabilityDelay.TotalMilliseconds)))
                : (TimeSpan?)null;

            var result = await ScanCoreOnceAsync(watchFolder, ct, stabilityOverride, allowEmptyFiles: retryUnmatched);
            if (retried > 0)
            {
                result = result with { Retried = retried };
            }

            var matched = await _imports.MatchPendingAsync(ct);

            if (result.NewPending > 0 || result.UnsupportedLogged > 0 || retried > 0 || recoveredCount > 0 || matched > 0)
            {
                NotifyPendingImportsChanged();
            }

            return result;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<int> ResetRetryableLogsAsync(string watchFolder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(watchFolder) || !Directory.Exists(watchFolder))
        {
            return 0;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(watchFolder)) + Path.DirectorySeparatorChar;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var retryable = await db.MetadataImportLogs
            .Where(l => l.Status == ImportStatuses.NoMatch ||
                        l.Status == ImportStatuses.Failed ||
                        l.Status == ImportStatuses.Matched ||
                        l.Status == ImportStatuses.Imported)
            .ToListAsync(ct);

        var count = 0;
        foreach (var log in retryable)
        {
            if (string.IsNullOrWhiteSpace(log.SourcePath))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(log.SourcePath);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                continue;
            }

            log.Status = ImportStatuses.Pending;
            log.Stage = ImportStages.Detected;
            log.ErrorDetails = null;
            log.Note = null;
            log.TargetPath = null;
            log.ChosenFolder = null;
            log.FileSize = GetFileSize(fullPath);
            log.Timestamp = DateTime.UtcNow;
            count++;
        }

        if (count > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger?.Info($"Watch Folder manual rescan queued {count} existing file(s) in Watch Folder for TMDb metadata lookup.");
        }

        return count;
    }

    private void ReportStatus(string message)
    {
        var handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var callback in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                callback(message);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Watch Folder status notification failed: {ex.Message}");
            }
        }
    }

    public void NotifyPendingImportsChanged()
    {
        var handlers = PendingImportsChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var callback in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Watch Folder refresh notification failed: {ex.Message}");
            }
        }
    }

    private sealed record FileCandidate(
        string FullPath,
        string FileName,
        ParsedFileName Parsed,
        long InitialSize,
        DateTime InitialWriteUtc);

    /// <summary>Detection-only pass retained for tests and diagnostics.</summary>
    public async Task<WatchScanResult> ScanOnceAsync(string watchFolder, CancellationToken ct = default)
    {
        await _scanGate.WaitAsync(ct);
        try
        {
            return await ScanCoreOnceAsync(watchFolder, ct);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<WatchScanResult> ScanCoreOnceAsync(
        string watchFolder,
        CancellationToken ct,
        TimeSpan? stabilityDelayOverride = null,
        bool allowEmptyFiles = false)
    {
        var result = new WatchScanResult(0, 0, 0, 0, 0, []);
        if (string.IsNullOrWhiteSpace(watchFolder) || !Directory.Exists(watchFolder))
        {
            return result;
        }

        var root = Path.GetFullPath(watchFolder);
        var settings = _settings.Load();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = settings.WatchFolderIncludeSubfolders,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        var paths = Directory.EnumerateFiles(root, "*", options)
            .Select(Path.GetFullPath)
            .Where(path => !Path.GetFileName(path).StartsWith(".digitalcenter-", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        result = result with { FilesSeen = paths.Count };

        await using var db = await _factory.CreateDbContextAsync(ct);
        var knownPaths = await GetKnownSourcePathsAsync(db, ct);
        var entriesToSave = new List<MetadataImportLog>();
        var savedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<FileCandidate>();

        foreach (var fullPath in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (knownPaths.Contains(fullPath))
            {
                result = result with { AlreadyKnown = result.AlreadyKnown + 1 };
                continue;
            }

            var fileName = Path.GetFileName(fullPath);
            var extension = Path.GetExtension(fullPath);
            var isMedia = LibraryScanner.MediaExtensions.Contains(extension);
            var parsed = _parser.Parse(fileName, Path.GetDirectoryName(fullPath));

            if (!isMedia)
            {
                entriesToSave.Add(new MetadataImportLog
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
                    ErrorDetails = $"'{(string.IsNullOrEmpty(extension) ? "(no extension)" : extension)}' is not a supported video file; the file stays in place.",
                    Timestamp = DateTime.UtcNow
                });
                continue;
            }

            try
            {
                var info = new FileInfo(fullPath);
                info.Refresh();
                if (!info.Exists || (!allowEmptyFiles && info.Length <= 0))
                {
                    result = AddUnstable(result, fullPath);
                    continue;
                }

                candidates.Add(new FileCandidate(fullPath, fileName, parsed, info.Length, info.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result = AddUnstable(result, fullPath);
                _logger?.Info($"Watch Folder file is not readable yet; will retry: '{fileName}'.");
            }
        }

        var rawDelay = stabilityDelayOverride ?? StabilityDelay;
        var delay = rawDelay < TimeSpan.Zero ? TimeSpan.Zero : rawDelay;
        if (candidates.Count > 0 && delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, ct);
        }

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!await IsSnapshotStableAndReadableAsync(candidate, allowEmptyFiles, ct))
            {
                result = AddUnstable(result, candidate.FullPath);
                _logger?.Info($"Watch Folder file not yet stable, will retry later: '{candidate.FileName}'.");
                continue;
            }

            ReportStatus($"Detected new file in Watch Folder: {candidate.FileName}");
            entriesToSave.Add(new MetadataImportLog
            {
                SourceFile = candidate.FileName,
                SourcePath = candidate.FullPath,
                Status = ImportStatuses.Pending,
                Stage = ImportStages.Detected,
                FileSize = candidate.InitialSize,
                IsSeries = candidate.Parsed.IsSeries,
                ParsedTitle = candidate.Parsed.Title,
                ParsedYear = candidate.Parsed.Year,
                ParsedSeason = candidate.Parsed.Season,
                ParsedEpisode = candidate.Parsed.Episode,
                CategoryName = LanguageResolver.FromFileName(candidate.FileName) is { } fileLang ? LanguageResolver.MapToCategoryFolder(fileLang) : null,
                MetadataSource = "WatchFolder",
                Timestamp = DateTime.UtcNow
            });
        }

        if (entriesToSave.Count == 0)
        {
            return result;
        }

        db.MetadataImportLogs.AddRange(entriesToSave);
        try
        {
            await db.SaveChangesAsync(ct);
            foreach (var entry in entriesToSave)
            {
                if (string.IsNullOrWhiteSpace(entry.SourcePath))
                {
                    continue;
                }

                knownPaths.Add(entry.SourcePath);
                savedPaths.Add(entry.SourcePath);
            }

            result = result with
            {
                NewPending = entriesToSave.Count(entry => entry.Status == ImportStatuses.Pending),
                UnsupportedLogged = entriesToSave.Count(entry => entry.Status == ImportStatuses.Unsupported)
            };
        }
        catch (DbUpdateException ex)
        {
            _logger?.Warn($"Watch Folder batch insert conflicted; retrying entries individually: {ex.InnerException?.Message ?? ex.Message}");
            db.ChangeTracker.Clear();
            foreach (var entry in entriesToSave)
            {
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(entry.SourcePath) &&
                    await db.MetadataImportLogs.AsNoTracking().AnyAsync(
                        log => log.SourcePath != null &&
                               EF.Functions.Collate(log.SourcePath, "NOCASE") == entry.SourcePath, ct))
                {
                    knownPaths.Add(entry.SourcePath);
                    continue;
                }

                entry.Id = 0;
                db.MetadataImportLogs.Add(entry);
                try
                {
                    await db.SaveChangesAsync(ct);
                    if (!string.IsNullOrWhiteSpace(entry.SourcePath))
                    {
                        knownPaths.Add(entry.SourcePath);
                        savedPaths.Add(entry.SourcePath);
                    }

                    if (entry.Status == ImportStatuses.Pending)
                    {
                        result = result with { NewPending = result.NewPending + 1 };
                    }
                    else if (entry.Status == ImportStatuses.Unsupported)
                    {
                        result = result with { UnsupportedLogged = result.UnsupportedLogged + 1 };
                    }
                }
                catch (DbUpdateException rowEx)
                {
                    _logger?.Warn($"Watch Folder entry for '{entry.SourceFile}' could not be added: {rowEx.InnerException?.Message ?? rowEx.Message}");
                    db.ChangeTracker.Clear();
                }
            }
        }

        foreach (var entry in entriesToSave.Where(entry =>
                     entry.SourcePath is not null && savedPaths.Contains(entry.SourcePath) &&
                     entry.Status == ImportStatuses.Pending))
        {
            _logger?.Info($"Watch Folder detected new file: '{entry.SourceFile}' (series={entry.IsSeries}).");
        }
        foreach (var entry in entriesToSave.Where(entry =>
                     entry.SourcePath is not null && savedPaths.Contains(entry.SourcePath) &&
                     entry.Status == ImportStatuses.Unsupported))
        {
            _logger?.Info($"Watch Folder skipped unsupported file: '{entry.SourceFile}'.");
        }
        return result;
    }

    private async Task<HashSet<string>> GetKnownSourcePathsAsync(DigitalCenterDbContext db, CancellationToken ct)
    {
        var cached = _knownSourcePaths;
        if (cached is not null && DateTime.UtcNow - _knownSourcePathsRefreshedUtc < KnownPathCacheLifetime)
        {
            return cached;
        }

        var existingPaths = await db.MetadataImportLogs
            .AsNoTracking()
            .Where(log => log.SourcePath != null)
            .Select(log => log.SourcePath!)
            .ToListAsync(ct);
        var loaded = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);
        _knownSourcePaths = loaded;
        _knownSourcePathsRefreshedUtc = DateTime.UtcNow;
        return loaded;
    }

    private static WatchScanResult AddUnstable(WatchScanResult result, string path) => result with
    {
        Unstable = result.Unstable + 1,
        UnstableFiles = [.. result.UnstableFiles, path]
    };

    private static async Task<bool> IsSnapshotStableAndReadableAsync(
        FileCandidate candidate,
        bool allowEmptyFiles,
        CancellationToken ct)
    {
        try
        {
            var latest = new FileInfo(candidate.FullPath);
            latest.Refresh();
            if (!latest.Exists || latest.Length != candidate.InitialSize || latest.LastWriteTimeUtc != candidate.InitialWriteUtc)
            {
                return false;
            }

            if (latest.Length == 0)
            {
                return allowEmptyFiles;
            }

            await using var stream = new FileStream(
                candidate.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);
            var buffer = new byte[1];
            await stream.ReadExactlyAsync(buffer.AsMemory(0, 1), ct);
            return true;
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

    public void Dispose()
    {
        Stop();
        _scanGate.Dispose();
    }
}
