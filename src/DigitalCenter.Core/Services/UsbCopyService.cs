using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Services;

/// <summary>Conflict handling when the target file already exists on the USB drive.</summary>
public enum CopyConflictAction
{
    Skip,
    Overwrite,
    Rename
}

/// <summary>Post-copy verification depth.</summary>
public enum CopyVerifyMode
{
    Size,
    SizeAndHash
}

/// <summary>Options for one copy run over the whole queue.</summary>
public sealed record CopyRunOptions(
    string DestinationRoot,
    CopyConflictAction ConflictAction,
    CopyVerifyMode VerifyMode);

/// <summary>Progress snapshot for the UI.</summary>
public sealed record CopyProgressReport(
    int CompletedFiles,
    int TotalFiles,
    long BytesCopied,
    long TotalBytes,
    string? CurrentFileName,
    double CurrentFilePercent,
    string Message);

/// <summary>Outcome of one copy run.</summary>
public sealed record CopyRunSummary(
    int TotalJobs,
    int Completed,
    int Skipped,
    int Failed,
    int ReturnedToQueue,
    long BytesCopied,
    TimeSpan Duration,
    bool WasCancelled)
{
    public override string ToString()
    {
        var verb = WasCancelled ? "Cancelled" : "Finished";
        return $"{verb}: {Completed} copied, {Skipped} skipped, {Failed} failed"
               + (ReturnedToQueue > 0 ? $", {ReturnedToQueue} still queued" : string.Empty)
               + $" ({BytesCopied / (1024.0 * 1024.0):0.#} MB in {Duration.TotalSeconds:0.0} s).";
    }
}

public sealed record EnqueueResult(int Queued, int AlreadyQueued, int NotFound)
{
    public string Describe() =>
        $"{Queued} file(s) queued"
        + (AlreadyQueued > 0 ? $", {AlreadyQueued} already in the queue" : string.Empty)
        + (NotFound > 0 ? $", {NotFound} file record(s) not found" : string.Empty) + ".";
}

public sealed record CopyJobView(
    int Id,
    int? MediaFileId,
    string FileName,
    string TitleDisplay,
    string SourcePath,
    string RelativeTargetPath,
    long FileSize,
    string Status,
    string? ErrorMessage,
    string? ConflictNote,
    string? DestinationRoot,
    long? BytesCopied,
    bool Verified,
    DateTime QueuedAt,
    DateTime? CompletedAt);

/// <summary>
/// Sequential background copying of queued files to a USB drive, SD card,
/// or connected Android/MTP phone. Files are copied directly into the single
/// target folder defined in settings without creating language sub-folders.
/// </summary>
public sealed class UsbCopyService
{
    public event Action? QueueChanged;

    private readonly IDbContextFactory<DigitalCenterDbContext> _factory;
    private readonly IDriveProbe _drives;
    private readonly IPortableDeviceTransfer? _portable;
    private readonly IAppLogger? _logger;

    private readonly SemaphoreSlim _runLock = new(1, 1);
    private volatile bool _isRunning;
    private CancellationTokenSource? _runCts;

    public UsbCopyService(
        IDbContextFactory<DigitalCenterDbContext> factory,
        IDriveProbe drives,
        IAppLogger? logger = null,
        IPortableDeviceTransfer? portable = null)
    {
        _factory = factory;
        _drives = drives;
        _logger = logger;
        _portable = portable;
    }

    public bool IsRunning => _isRunning;

    public Func<CancellationToken, Task>? CopyStartHook { get; set; }

    // ------------------------------------------------------------------ queue

    public async Task<EnqueueResult> EnqueueTitlesAsync(
        IReadOnlyCollection<int> titleIds,
        string? moviesRoot,
        string? seriesRoot,
        bool createPerTitleFolders,
        bool cleanFilenames,
        CancellationToken ct = default)
    {
        if (titleIds.Count == 0)
        {
            return new EnqueueResult(0, 0, 0);
        }

        await using var db = await _factory.CreateDbContextAsync(ct);

        var titles = await db.MediaTitles
            .Include(t => t.Files)
            .Include(t => t.SeriesDetails!).ThenInclude(sd => sd.Seasons).ThenInclude(s => s.Episodes).ThenInclude(e => e.Files)
            .Where(t => titleIds.Contains(t.Id))
            .ToListAsync(ct);

        var activePaths = await db.CopyJobs
            .Where(j => j.Status == CopyJobStatuses.Queued || j.Status == CopyJobStatuses.Copying)
            .Select(j => new { j.MediaFileId, j.SourcePath })
            .ToListAsync(ct);
        var activeFileIds = activePaths.Where(j => j.MediaFileId.HasValue).Select(j => j.MediaFileId!.Value).ToHashSet();
        var activeSources = activePaths.Select(j => j.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var queued = 0;
        var alreadyQueued = 0;

        foreach (var title in titles)
        {
            ct.ThrowIfCancellationRequested();

            var files = title.Type == MediaKind.Movie
                ? title.Files.ToList()
                : title.SeriesDetails?.Seasons.SelectMany(s => s.Episodes).SelectMany(e => e.Files).ToList() ?? [];

            if (files.Count == 0)
            {
                continue;
            }

            var titleDisplay = title.ReleaseYear.HasValue
                ? $"{title.Title} ({title.ReleaseYear.Value})"
                : title.Title;

            foreach (var file in files)
            {
                if (activeFileIds.Contains(file.Id) || activeSources.Contains(file.FilePath))
                {
                    alreadyQueued++;
                    continue;
                }

                var fileName = title.Type == MediaKind.Movie && cleanFilenames
                    ? PathPlanner.BuildImportFileName("{Title} ({Year})", "{Title}", false,
                        title.Title, title.ReleaseYear, null, null, Path.GetFileName(file.FilePath))
                    : Path.GetFileName(file.FilePath);

                var relative = PathPlanner.SanitizeFileName(fileName);

                db.CopyJobs.Add(new CopyJob
                {
                    MediaFileId = file.Id,
                    SourcePath = file.FilePath,
                    RelativeTargetPath = relative,
                    FileName = Path.GetFileName(file.FilePath),
                    TitleDisplay = titleDisplay,
                    FileSize = file.FileSize,
                    Status = CopyJobStatuses.Queued,
                    QueuedAt = DateTime.UtcNow
                });
                queued++;
                activeSources.Add(file.FilePath);
                activeFileIds.Add(file.Id);
            }
        }

        await db.SaveChangesAsync(ct);
        QueueChanged?.Invoke();
        return new EnqueueResult(queued, alreadyQueued, 0);
    }

    public async Task<EnqueueResult> EnqueueAsync(IEnumerable<int> mediaFileIds, string? libraryRoot, CancellationToken ct = default)
    {
        var ids = mediaFileIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new EnqueueResult(0, 0, 0);
        }

        await using var db = await _factory.CreateDbContextAsync(ct);

        var files = await db.MediaFiles
            .Include(f => f.MovieTitle)
            .Include(f => f.Episode!).ThenInclude(e => e.Season).ThenInclude(s => s.SeriesDetails).ThenInclude(sd => sd.MediaTitle)
            .Where(f => ids.Contains(f.Id))
            .ToListAsync(ct);

        var activePaths = await db.CopyJobs
            .Where(j => j.Status == CopyJobStatuses.Queued || j.Status == CopyJobStatuses.Copying)
            .Select(j => new { j.MediaFileId, j.SourcePath })
            .ToListAsync(ct);
        var activeFileIds = activePaths.Where(j => j.MediaFileId.HasValue).Select(j => j.MediaFileId!.Value).ToHashSet();
        var activeSources = activePaths.Select(j => j.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var queued = 0;
        var alreadyQueued = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (activeFileIds.Contains(file.Id) || activeSources.Contains(file.FilePath))
            {
                alreadyQueued++;
                continue;
            }

            var (titleDisplay, _) = DescribeOwner(file);
            var relative = PathPlanner.SanitizeFileName(Path.GetFileName(file.FilePath));

            db.CopyJobs.Add(new CopyJob
            {
                MediaFileId = file.Id,
                SourcePath = file.FilePath,
                RelativeTargetPath = relative,
                FileName = Path.GetFileName(file.FilePath),
                TitleDisplay = titleDisplay,
                FileSize = file.FileSize,
                Status = CopyJobStatuses.Queued,
                QueuedAt = DateTime.UtcNow
            });
            queued++;
            activeSources.Add(file.FilePath);
            activeFileIds.Add(file.Id);
        }

        foreach (var missingId in ids.Where(id => files.All(f => f.Id != id)))
        {
            _logger?.Warn($"Copy queue: media file {missingId} not found in the catalog.");
        }

        await db.SaveChangesAsync(ct);
        QueueChanged?.Invoke();
        return new EnqueueResult(queued, alreadyQueued, ids.Count - files.Count);
    }

    private static (string TitleDisplay, MediaKind Kind) DescribeOwner(MediaFile file)
    {
        if (file.MovieTitle is not null)
        {
            var title = file.MovieTitle.Title;
            if (file.MovieTitle.ReleaseYear.HasValue)
            {
                title += $" ({file.MovieTitle.ReleaseYear.Value})";
            }

            return (title, MediaKind.Movie);
        }

        var seriesTitle = file.Episode?.Season?.SeriesDetails?.MediaTitle?.Title ?? "Series";
        var seasonNumber = file.Episode?.Season?.SeasonNumber ?? 0;
        var episodeNumber = file.Episode?.EpisodeNumber ?? 0;
        return ($"{seriesTitle} - S{seasonNumber:00}E{episodeNumber:00}", MediaKind.Series);
    }

    public async Task<IReadOnlyList<CopyJobView>> GetJobsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var jobs = await db.CopyJobs
            .AsNoTracking()
            .OrderBy(j => j.Status == CopyJobStatuses.Queued ? 0 : 1)
            .ThenBy(j => j.Status == CopyJobStatuses.Queued ? j.QueuedAt : j.CompletedAt ?? j.StartedAt ?? j.QueuedAt)
            .ToListAsync(ct);

        return jobs.Select(j => new CopyJobView(
            j.Id, j.MediaFileId, j.FileName, j.TitleDisplay, j.SourcePath, j.RelativeTargetPath,
            j.FileSize, j.Status, j.ErrorMessage, j.ConflictNote, j.DestinationRoot,
            j.BytesCopied, j.Verified, j.QueuedAt, j.CompletedAt)).ToList();
    }

    public async Task<int> GetQueuedCountAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.CopyJobs.CountAsync(j => j.Status == CopyJobStatuses.Queued || j.Status == CopyJobStatuses.Copying, ct);
    }

    public async Task RemoveAsync(int jobId, CancellationToken ct = default)
    {
        if (_isRunning)
        {
            throw new InvalidOperationException("Jobs cannot be removed while a copy run is active.");
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var job = await db.CopyJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            return;
        }

        if (job.Status == CopyJobStatuses.Copying)
        {
            throw new InvalidOperationException("A copying job cannot be removed; cancel the copy first.");
        }

        db.CopyJobs.Remove(job);
        await db.SaveChangesAsync(ct);
        QueueChanged?.Invoke();
    }

    public async Task<int> ClearCompletedAsync(CancellationToken ct = default)
    {
        if (_isRunning)
        {
            throw new InvalidOperationException("The queue cannot be cleaned while a copy run is active.");
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var done = await db.CopyJobs
            .Where(j => j.Status == CopyJobStatuses.Completed || j.Status == CopyJobStatuses.Skipped || j.Status == CopyJobStatuses.Cancelled)
            .ToListAsync(ct);
        db.CopyJobs.RemoveRange(done);
        await db.SaveChangesAsync(ct);
        QueueChanged?.Invoke();
        return done.Count;
    }

    public async Task<int> RetryFailedAsync(CancellationToken ct = default)
    {
        if (_isRunning)
        {
            throw new InvalidOperationException("Failed jobs cannot be retried while a copy run is active.");
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var failed = await db.CopyJobs.Where(j => j.Status == CopyJobStatuses.Failed).ToListAsync(ct);
        foreach (var job in failed)
        {
            job.Status = CopyJobStatuses.Queued;
            job.ErrorMessage = null;
            job.ConflictNote = null;
            job.BytesCopied = null;
            job.StartedAt = null;
            job.CompletedAt = null;
        }

        await db.SaveChangesAsync(ct);
        QueueChanged?.Invoke();
        return failed.Count;
    }

    // ------------------------------------------------------------------ estimation

    public async Task<(long RequiredBytes, long FreeBytes, bool Fits)> EstimateAsync(string destinationRoot, CopyConflictAction action, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var jobs = await db.CopyJobs
            .Where(j => j.Status == CopyJobStatuses.Queued)
            .Select(j => new { j.RelativeTargetPath, j.FileSize })
            .ToListAsync(ct);

        var isPortable = _portable is not null && _portable.IsPortablePath(destinationRoot);
        long required = 0;
        foreach (var job in jobs)
        {
            if (action == CopyConflictAction.Skip)
            {
                var exists = isPortable
                    ? _portable!.FileExists(destinationRoot, Path.GetFileName(job.RelativeTargetPath))
                    : File.Exists(Path.Combine(destinationRoot, job.RelativeTargetPath));
                if (exists)
                {
                    continue;
                }
            }

            required += job.FileSize;
        }

        var free = _drives.GetFreeSpace(destinationRoot);
        return (required, free, required <= free);
    }

    // ------------------------------------------------------------------ copy run

    public void Cancel() => _runCts?.Cancel();

    public async Task<CopyRunSummary> RunAsync(CopyRunOptions options, IProgress<CopyProgressReport>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.DestinationRoot))
        {
            throw new InvalidOperationException("Select a destination drive first.");
        }

        if (!_drives.IsReady(options.DestinationRoot))
        {
            throw new InvalidOperationException($"The destination drive is not ready: '{options.DestinationRoot}'.");
        }

        await _runLock.WaitAsync(ct);
        try
        {
            _isRunning = true;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _runCts.Token;

            var started = DateTime.UtcNow;
            var jobs = await LoadQueuedJobsAsync(token);
            if (jobs.Count == 0)
            {
                return new CopyRunSummary(0, 0, 0, 0, 0, 0, TimeSpan.Zero, false);
            }

            var totalBytes = jobs.Sum(j => j.FileSize);
            var completed = 0;
            var skipped = 0;
            var failed = 0;
            var returned = 0;
            long bytesCopied = 0;
            var wasCancelled = false;

            Report(progress, 0, jobs.Count, 0, totalBytes, null, 0, $"Copying {jobs.Count} file(s)…");

            for (var index = 0; index < jobs.Count; index++)
            {
                if (token.IsCancellationRequested)
                {
                    wasCancelled = true;
                    break;
                }

                var job = jobs[index];

                try
                {
                    var outcome = await CopyOneAsync(job, options, token, progress, index, jobs.Count, bytesCopied, totalBytes);
                    switch (outcome.Status)
                    {
                        case CopyJobStatuses.Completed:
                            completed++;
                            bytesCopied += outcome.BytesCopied;
                            break;
                        case CopyJobStatuses.Skipped:
                            skipped++;
                            break;
                        default:
                            failed++;
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    returned++;
                    wasCancelled = true;
                }

                if (wasCancelled)
                {
                    break;
                }
            }

            var remaining = jobs.Count - completed - skipped - failed - returned;

            var duration = DateTime.UtcNow - started;
            _logger?.Info($"USB copy run: {completed} completed, {skipped} skipped, {failed} failed, {returned} re-queued ({duration.TotalSeconds:0.0} s).");
            QueueChanged?.Invoke();

            var summary = new CopyRunSummary(jobs.Count, completed, skipped, failed, Math.Max(0, returned + remaining), bytesCopied, duration, wasCancelled);
            Report(progress, completed + skipped, jobs.Count, bytesCopied, totalBytes, null, 0, summary.ToString());
            return summary;
        }
        finally
        {
            _isRunning = false;
            _runCts?.Dispose();
            _runCts = null;
            _runLock.Release();
        }
    }

    private async Task<List<CopyJob>> LoadQueuedJobsAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.CopyJobs
            .Where(j => j.Status == CopyJobStatuses.Queued)
            .OrderBy(j => j.QueuedAt)
            .ThenBy(j => j.Id)
            .ToListAsync(ct);
    }

    private sealed record JobOutcome(string Status, long BytesCopied);

    private async Task<JobOutcome> CopyOneAsync(
        CopyJob job,
        CopyRunOptions options,
        CancellationToken ct,
        IProgress<CopyProgressReport>? report,
        int filesDoneBefore,
        int totalFiles,
        long bytesCopiedBefore,
        long totalBytes)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        var tracked = await context.CopyJobs.FirstAsync(j => j.Id == job.Id, ct);

        tracked.Status = CopyJobStatuses.Copying;
        tracked.StartedAt = DateTime.UtcNow;
        tracked.DestinationRoot = options.DestinationRoot;
        tracked.ErrorMessage = null;
        tracked.ConflictNote = null;
        await context.SaveChangesAsync(ct);

        try
        {
            var flatFileName = Path.GetFileName(tracked.RelativeTargetPath);
            if (string.IsNullOrWhiteSpace(flatFileName))
            {
                flatFileName = tracked.FileName;
            }

            if (!File.Exists(tracked.SourcePath))
            {
                tracked.Status = CopyJobStatuses.Failed;
                tracked.ErrorMessage = "The source file is missing (it may have been moved or deleted).";
                tracked.CompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(CancellationToken.None);
                QueueChanged?.Invoke();
                return new JobOutcome(CopyJobStatuses.Failed, 0);
            }

            var sourceInfo = new FileInfo(tracked.SourcePath);
            if (sourceInfo.Length != tracked.FileSize && tracked.FileSize > 0)
            {
                tracked.FileSize = sourceInfo.Length;
            }

            if (!_drives.IsReady(options.DestinationRoot))
            {
                tracked.Status = CopyJobStatuses.Failed;
                tracked.ErrorMessage = "The destination device is not ready.";
                tracked.CompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(CancellationToken.None);
                QueueChanged?.Invoke();
                return new JobOutcome(CopyJobStatuses.Failed, 0);
            }

            var freeSpace = _drives.GetFreeSpace(options.DestinationRoot);
            if (freeSpace > 0 && freeSpace < tracked.FileSize)
            {
                tracked.Status = CopyJobStatuses.Failed;
                tracked.ErrorMessage =
                    $"Insufficient space on the destination: {tracked.FileSize / (1024.0 * 1024.0):0.#} MB needed, {freeSpace / (1024.0 * 1024.0):0.#} MB free.";
                tracked.CompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(CancellationToken.None);
                QueueChanged?.Invoke();
                return new JobOutcome(CopyJobStatuses.Failed, 0);
            }

            if (_portable is not null && _portable.IsPortablePath(options.DestinationRoot))
            {
                return await CopyOneToPortableAsync(
                    context, tracked, flatFileName, options, ct, report, filesDoneBefore, totalFiles, bytesCopiedBefore, totalBytes);
            }

            var targetPath = Path.Combine(options.DestinationRoot, flatFileName);
            var directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(directory))
            {
                tracked.Status = CopyJobStatuses.Failed;
                tracked.ErrorMessage = "The computed target path has no directory part.";
                tracked.CompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(CancellationToken.None);
                QueueChanged?.Invoke();
                return new JobOutcome(CopyJobStatuses.Failed, 0);
            }

            Directory.CreateDirectory(directory);

            if (File.Exists(targetPath))
            {
                switch (options.ConflictAction)
                {
                    case CopyConflictAction.Skip:
                        tracked.Status = CopyJobStatuses.Skipped;
                        tracked.ConflictNote = "Target already existed; skipped (existing file kept).";
                        tracked.CompletedAt = DateTime.UtcNow;
                        await context.SaveChangesAsync(CancellationToken.None);
                        QueueChanged?.Invoke();
                        return new JobOutcome(CopyJobStatuses.Skipped, 0);

                    case CopyConflictAction.Rename:
                        var unique = PathPlanner.ResolveFileNameConflict(directory, Path.GetFileName(targetPath));
                        targetPath = Path.Combine(directory, unique);
                        tracked.ConflictNote = $"Target existed; copied as '{Path.GetFileName(targetPath)}'.";
                        break;

                    case CopyConflictAction.Overwrite:
                        tracked.ConflictNote = "Target existed and was overwritten.";
                        break;
                }
            }

            var partialPath = targetPath + ".dcenter-tmp";
            var copied = 0L;
            byte[]? sourceHash = null;
            try
            {
                const int bufferSize = 1024 * 1024;
                await using (var source = new FileStream(tracked.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var destination = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
                {
                    if (CopyStartHook is not null)
                    {
                        await CopyStartHook(ct);
                    }

                    using var sha = options.VerifyMode == CopyVerifyMode.SizeAndHash
                        ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                        : null;

                    var buffer = new byte[bufferSize];
                    var reportClock = Stopwatch.StartNew();
                    var lastReportElapsed = -ProgressInterval;
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                        copied += read;
                        sha?.AppendData(buffer.AsSpan(0, read));

                        if (report is not null && tracked.FileSize > 0)
                        {
                            var elapsed = reportClock.Elapsed;
                            if (elapsed - lastReportElapsed >= ProgressInterval)
                            {
                                lastReportElapsed = elapsed;
                                var percent = Math.Min(100.0, copied * 100.0 / tracked.FileSize);
                                report.Report(new CopyProgressReport(
                                    filesDoneBefore, totalFiles, bytesCopiedBefore + copied, totalBytes,
                                    tracked.FileName, percent, $"Copying '{tracked.FileName}'…"));
                            }
                        }
                    }

                    sourceHash = sha?.GetHashAndReset();
                    await destination.FlushAsync(ct);
                }

                if (report is not null && tracked.FileSize > 0)
                {
                    report.Report(new CopyProgressReport(
                        filesDoneBefore, totalFiles, bytesCopiedBefore + copied, totalBytes,
                        tracked.FileName, 100.0, $"Copying '{tracked.FileName}'…"));
                }

                var verified = false;
                if (new FileInfo(partialPath).Length == tracked.FileSize)
                {
                    if (options.VerifyMode == CopyVerifyMode.SizeAndHash)
                    {
                        var copyHash = await ComputeSha256Async(partialPath, ct);
                        verified = sourceHash is not null &&
                                   copyHash.Length == sourceHash.Length &&
                                   CryptographicOperations.FixedTimeEquals(copyHash, sourceHash);
                        if (!verified)
                        {
                            throw new IOException("SHA-256 verification failed; the copy differs from the source.");
                        }
                    }
                    else
                    {
                        verified = true;
                    }
                }
                else
                {
                    throw new IOException($"Size verification failed ({new FileInfo(partialPath).Length} bytes copied, {tracked.FileSize} expected).");
                }

                if (options.ConflictAction == CopyConflictAction.Overwrite && File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                File.Move(partialPath, targetPath, overwrite: false);

                tracked.Status = CopyJobStatuses.Completed;
                tracked.BytesCopied = copied;
                tracked.Verified = verified;
                tracked.CompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(CancellationToken.None);
                QueueChanged?.Invoke();
                return new JobOutcome(CopyJobStatuses.Completed, copied);
            }
            catch (OperationCanceledException)
            {
                TryDelete(partialPath);
                tracked.Status = CopyJobStatuses.Queued;
                tracked.BytesCopied = null;
                tracked.ErrorMessage = "Cancelled by the operator; the job returned to the queue.";
                tracked.StartedAt = null;
                await context.SaveChangesAsync(CancellationToken.None);
                QueueChanged?.Invoke();
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(partialPath);
                tracked.Status = CopyJobStatuses.Failed;
                tracked.ErrorMessage = $"The copy failed: {ex.Message}";
                tracked.CompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(CancellationToken.None);
                QueueChanged?.Invoke();
                return new JobOutcome(CopyJobStatuses.Failed, 0);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            tracked.Status = CopyJobStatuses.Failed;
            tracked.ErrorMessage = $"Unexpected error: {ex.Message}";
            tracked.CompletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(CancellationToken.None);
            QueueChanged?.Invoke();
            return new JobOutcome(CopyJobStatuses.Failed, 0);
        }
    }

    private async Task<JobOutcome> CopyOneToPortableAsync(
        DigitalCenterDbContext context,
        CopyJob tracked,
        string flatFileName,
        CopyRunOptions options,
        CancellationToken ct,
        IProgress<CopyProgressReport>? report,
        int filesDoneBefore,
        int totalFiles,
        long bytesCopiedBefore,
        long totalBytes)
    {
        var portable = _portable!;
        portable.EnsureDirectory(options.DestinationRoot);

        var targetFileName = flatFileName;
        if (portable.FileExists(options.DestinationRoot, targetFileName))
        {
            switch (options.ConflictAction)
            {
                case CopyConflictAction.Skip:
                    tracked.Status = CopyJobStatuses.Skipped;
                    tracked.ConflictNote = "Target already existed on device; skipped.";
                    tracked.CompletedAt = DateTime.UtcNow;
                    await context.SaveChangesAsync(CancellationToken.None);
                    QueueChanged?.Invoke();
                    return new JobOutcome(CopyJobStatuses.Skipped, 0);

                case CopyConflictAction.Rename:
                    var stem = Path.GetFileNameWithoutExtension(flatFileName);
                    var ext = Path.GetExtension(flatFileName);
                    var n = 2;
                    while (portable.FileExists(options.DestinationRoot, $"{stem} ({n}){ext}"))
                    {
                        n++;
                    }
                    targetFileName = $"{stem} ({n}){ext}";
                    tracked.ConflictNote = $"Target existed; copied as '{targetFileName}'.";
                    break;

                case CopyConflictAction.Overwrite:
                    portable.DeleteFile(options.DestinationRoot, targetFileName);
                    tracked.ConflictNote = "Target existed and was overwritten.";
                    break;
            }
        }

        var reportClock = Stopwatch.StartNew();
        var lastReportElapsed = -ProgressInterval;
        long copied = 0;

        try
        {
            if (CopyStartHook is not null)
            {
                await CopyStartHook(ct);
            }

            await portable.UploadFileAsync(
                tracked.SourcePath,
                options.DestinationRoot,
                targetFileName,
                bytes =>
                {
                    copied = bytes;
                    if (report is not null && tracked.FileSize > 0)
                    {
                        var elapsed = reportClock.Elapsed;
                        if (elapsed - lastReportElapsed >= ProgressInterval)
                        {
                            lastReportElapsed = elapsed;
                            var percent = Math.Min(100.0, bytes * 100.0 / tracked.FileSize);
                            report.Report(new CopyProgressReport(
                                filesDoneBefore, totalFiles, bytesCopiedBefore + bytes, totalBytes,
                                tracked.FileName, percent, $"Copying '{tracked.FileName}' to device…"));
                        }
                    }
                },
                ct);

            copied = tracked.FileSize > 0 ? tracked.FileSize : copied;
            if (report is not null && tracked.FileSize > 0)
            {
                report.Report(new CopyProgressReport(
                    filesDoneBefore, totalFiles, bytesCopiedBefore + copied, totalBytes,
                    tracked.FileName, 100.0, $"Copied '{tracked.FileName}' to device."));
            }

            tracked.Status = CopyJobStatuses.Completed;
            tracked.BytesCopied = copied;
            tracked.Verified = true;
            tracked.CompletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(CancellationToken.None);
            QueueChanged?.Invoke();
            return new JobOutcome(CopyJobStatuses.Completed, copied);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (portable.FileExists(options.DestinationRoot, targetFileName))
                {
                    portable.DeleteFile(options.DestinationRoot, targetFileName);
                }
            }
            catch (Exception)
            {
            }

            tracked.Status = CopyJobStatuses.Queued;
            tracked.BytesCopied = null;
            tracked.ErrorMessage = "Cancelled by the operator; the job returned to the queue.";
            tracked.StartedAt = null;
            await context.SaveChangesAsync(CancellationToken.None);
            QueueChanged?.Invoke();
            throw;
        }
        catch (Exception ex)
        {
            tracked.Status = CopyJobStatuses.Failed;
            tracked.ErrorMessage = $"Device copy failed: {ex.Message}";
            tracked.CompletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(CancellationToken.None);
            QueueChanged?.Invoke();
            return new JobOutcome(CopyJobStatuses.Failed, 0);
        }
    }

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private static async Task<byte[]> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return await sha.ComputeHashAsync(stream, ct);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Report(
        IProgress<CopyProgressReport>? progress,
        int completedFiles,
        int totalFiles,
        long bytesCopied,
        long totalBytes,
        string? currentFile,
        double currentFilePercent,
        string message) => progress?.Report(new CopyProgressReport(
            completedFiles, totalFiles, bytesCopied, totalBytes, currentFile, currentFilePercent, message));
}
