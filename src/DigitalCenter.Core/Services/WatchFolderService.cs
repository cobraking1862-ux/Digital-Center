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

    /// <summary>
    /// One scan at a time. The poll loop, "Rescan Now" and the startup scan all
    /// funnel through this gate, so the same folder is never walked twice at
    /// once (which produced duplicate detection passes, duplicate TMDb lookups
    /// and conflicting folder operations).
    /// </summary>
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    private DateTime _lastAutomaticMatchUtc = DateTime.MinValue;

    /// <summary>
    /// Minimum time between two AUTOMATIC metadata-match passes. Detection runs
    /// on every poll, but matching calls TMDb: while files sit unmatched
    /// (typically because TMDb is unreachable) the automatic pass backs off
    /// instead of re-requesting the same lookups every few seconds. Newly
    /// detected files are always matched immediately, and an explicit
    /// "Rescan Now" always matches.
    /// </summary>
    public TimeSpan AutomaticMatchInterval { get; set; } = TimeSpan.FromMinutes(5);

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
    /// The folder currently being monitored, or null while monitoring is
    /// stopped. There is exactly one Watch Folder; its authoritative value is
    /// <see cref="AppSettings.WatchFolderPath"/> (managed in the Management
    /// workspace), and starting with another path simply moves the single
    /// monitor over — it never runs two monitors side by side.
    /// </summary>
    public string? CurrentPath { get; private set; }

    public void Start(string watchFolderPath, TimeSpan? pollInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(watchFolderPath);

        CancellationTokenSource? previousCts;
        Task? previousLoop;
        lock (_lifecycleLock)
        {
            if (IsRunning && string.Equals(CurrentPath, watchFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                return; // Already monitoring exactly this folder.
            }

            // A different folder was requested: retire the old loop first so
            // only one monitor ever runs.
            previousCts = _loopCts;
            previousLoop = _loopTask;
            previousCts?.Cancel();

            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            CurrentPath = watchFolderPath;
            _loopTask = Task.Run(() => PollLoopAsync(watchFolderPath, pollInterval ?? TimeSpan.FromSeconds(15), token), CancellationToken.None);
            _logger?.Info($"Watch Folder monitoring started for '{watchFolderPath}'.");
        }

        Retire(previousLoop, previousCts);
    }

    public void Stop()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_lifecycleLock)
        {
            cts = _loopCts;
            loop = _loopTask;
            cts?.Cancel();
            _loopCts = null;
            _loopTask = null;
            CurrentPath = null;
        }

        Retire(loop, cts);
        _logger?.Info("Watch Folder monitoring stopped.");
    }

    /// <summary>Waits (bounded) for a retired poll loop and releases its token source.</summary>
    private static void Retire(Task? loop, CancellationTokenSource? cts)
    {
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // Shutdown must never hang on the poll loop.
        }

        cts?.Dispose();
    }

    /// <summary>
    /// Applies a new Watch Folder path. When monitoring was running it is
    /// restarted on the new folder (only one monitor ever exists); when it was
    /// stopped it stays stopped. Existing files, import history and catalog
    /// records are never touched by a path change.
    /// </summary>
    public void ChangePath(string watchFolderPath, TimeSpan? pollInterval = null)
    {
        if (IsRunning)
        {
            Start(watchFolderPath, pollInterval);
            return;
        }

        lock (_lifecycleLock)
        {
            CurrentPath = null;
        }
    }

    private async Task PollLoopAsync(string watchFolder, TimeSpan interval, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await AutomaticPassAsync(watchFolder, token).ConfigureAwait(false);
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

    /// <summary>Rescans the configured default Watch Folder (see <see cref="AppSettings.WatchFolderPath"/>).</summary>
    public Task<WatchScanResult> RescanConfiguredAsync(CancellationToken ct = default)
    {
        var path = _settings.Load().WatchFolderPath;
        return string.IsNullOrWhiteSpace(path)
            ? Task.FromResult(new WatchScanResult(0, 0, 0, 0, 0, []))
            : RescanOnceAsync(path, ct);
    }

    /// <summary>
    /// One full pass: detect new files, then run the automatic metadata match
    /// over every still-pending import. Also used by the "Rescan Now" action.
    /// Idempotent: a file already has exactly one import log (unique
    /// SourcePath index) no matter how often it is seen.
    /// </summary>
    public async Task<WatchScanResult> RescanOnceAsync(string watchFolder, CancellationToken ct = default)
    {
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await ScanOnceAsync(watchFolder, ct).ConfigureAwait(false);
            _lastAutomaticMatchUtc = DateTime.UtcNow;
            await _imports.MatchPendingAsync(ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>
    /// The automatic (poll-loop) pass: detection every time, metadata matching
    /// only when something new appeared or the back-off interval has elapsed.
    /// </summary>
    private async Task<WatchScanResult> AutomaticPassAsync(string watchFolder, CancellationToken ct)
    {
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await ScanOnceAsync(watchFolder, ct).ConfigureAwait(false);

            if (result.NewPending > 0 || DateTime.UtcNow - _lastAutomaticMatchUtc >= AutomaticMatchInterval)
            {
                _lastAutomaticMatchUtc = DateTime.UtcNow;
                await _imports.MatchPendingAsync(ct).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _scanGate.Release();
        }
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
            // Pass 1 walks the folder once and snapshots every new media file.
            var candidates = new List<Candidate>();

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

                var info = new FileInfo(fullPath);
                candidates.Add(new Candidate(fullPath, fileName, parsed, info.Length, info.LastWriteTimeUtc));
            }

            if (candidates.Count > 0)
            {
                // ONE stability window for the whole pass. Waiting per file made
                // a folder with several new files take (files x delay) before
                // anything reached the UI, which looked like a frozen scan.
                await Task.Delay(StabilityDelay, ct).ConfigureAwait(false);

                foreach (var candidate in candidates)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!await IsUnchangedAndReadableAsync(candidate, ct).ConfigureAwait(false))
                    {
                        result = result with
                        {
                            Unstable = result.Unstable + 1,
                            UnstableFiles = [.. result.UnstableFiles, candidate.Path]
                        };
                        _logger?.Info($"Watch Folder file not yet stable, will retry later: '{candidate.FileName}'.");
                        continue;
                    }

                    db.MetadataImportLogs.Add(new MetadataImportLog
                    {
                        SourceFile = candidate.FileName,
                        SourcePath = candidate.Path,
                        Status = ImportStatuses.Pending,
                        Stage = ImportStages.Detected,
                        FileSize = candidate.Size,
                        IsSeries = candidate.Parsed.IsSeries,
                        ParsedTitle = candidate.Parsed.Title,
                        ParsedYear = candidate.Parsed.Year,
                        ParsedSeason = candidate.Parsed.Season,
                        ParsedEpisode = candidate.Parsed.Episode,
                        MetadataSource = "WatchFolder",
                        Timestamp = DateTime.UtcNow
                    });

                    try
                    {
                        await db.SaveChangesAsync(ct).ConfigureAwait(false);
                        result = result with { NewPending = result.NewPending + 1 };
                        _logger?.Info($"Watch Folder detected new file: '{candidate.FileName}' (series={candidate.Parsed.IsSeries}).");
                    }
                    catch (DbUpdateException ex)
                    {
                        // The unique SourcePath index already holds this file
                        // (an earlier pass won the race); that is not an error.
                        _logger?.Warn($"Watch Folder entry for '{candidate.FileName}' could not be added (likely already known): {ex.InnerException?.Message ?? ex.Message}");
                        db.ChangeTracker.Clear();
                    }
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

    /// <summary>A new media file waiting for the shared stability window.</summary>
    private sealed record Candidate(string Path, string FileName, ParsedFileName Parsed, long Size, DateTime WrittenUtc);

    /// <summary>
    /// Stability check against the snapshot taken before the shared delay: the
    /// size and the write time must be unchanged, the file must not be empty and
    /// it must be readable (open, read a byte, close). A file that is still
    /// being written is reported unstable and picked up by a later pass.
    /// </summary>
    private async Task<bool> IsUnchangedAndReadableAsync(Candidate candidate, CancellationToken ct)
    {
        if (candidate.Size <= 0)
        {
            return false;
        }

        try
        {
            var current = new FileInfo(candidate.Path);
            if (!current.Exists || current.Length != candidate.Size || current.LastWriteTimeUtc != candidate.WrittenUtc)
            {
                return false;
            }

            await using var stream = new FileStream(candidate.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var buffer = new byte[1];
            return await stream.ReadAsync(buffer, ct).ConfigureAwait(false) >= 0;
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
