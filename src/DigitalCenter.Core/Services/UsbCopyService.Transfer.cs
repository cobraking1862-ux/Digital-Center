using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Services;

/// <summary>
/// UsbCopyService, part 2 of 2: the copy run itself. Drives the queue job by
/// job, verifies every finished file with SHA-256 and reports progress.
/// </summary>
public sealed partial class UsbCopyService
{
    // ------------------------------------------------------------------ copy run

    /// <summary>Cancels the active run at the next chunk boundary; current file is cleaned up and re-queued.</summary>
    public void Cancel()
    {
        _runCts?.Cancel();
    }

    /// <summary>Runs the whole queued queue sequentially. Only one run can be active.</summary>
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

            Report(progress, 0, jobs.Count, 0, totalBytes, null, 0, $"Copying {jobs.Count} file(s) to '{options.DestinationRoot}'…");

            for (var index = 0; index < jobs.Count; index++)
            {
                if (token.IsCancellationRequested)
                {
                    // Cancelled between files: the remaining jobs simply stay queued.
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
                    // CopyOneAsync already re-queued the job and cleaned up.
                    returned++;
                    wasCancelled = true;
                }

                if (wasCancelled)
                {
                    break;
                }
            }

            // Remaining jobs stay queued after a cancellation.
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

    /// <summary>Copies one job with preflight, conflict handling, verification and logging. Manages its own DbContext.</summary>
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
            var targetPath = Path.Combine(options.DestinationRoot, tracked.RelativeTargetPath);

            // ---- preflight: source ----
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
            // Refresh the snapshot from the disk even when the queued size was
            // unknown (0): otherwise the capacity check and the progress
            // percentage silently work from a wrong number.
            if (sourceInfo.Length != tracked.FileSize)
            {
                tracked.FileSize = sourceInfo.Length;
            }

            // ---- preflight: drive & space ----
            if (!_drives.IsReady(options.DestinationRoot))
            {
                tracked.Status = CopyJobStatuses.Failed;
                tracked.ErrorMessage = "The destination drive is not ready.";
                tracked.CompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(CancellationToken.None);
                QueueChanged?.Invoke();
                return new JobOutcome(CopyJobStatuses.Failed, 0);
            }

            var freeSpace = _drives.GetFreeSpace(options.DestinationRoot);
            if (freeSpace < tracked.FileSize)
            {
                tracked.Status = CopyJobStatuses.Failed;
                tracked.ErrorMessage =
                    $"Insufficient space on the destination: {tracked.FileSize / (1024.0 * 1024.0):0.#} MB needed, {freeSpace / (1024.0 * 1024.0):0.#} MB free.";
                tracked.CompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(CancellationToken.None);
                QueueChanged?.Invoke();
                return new JobOutcome(CopyJobStatuses.Failed, 0);
            }

            // ---- conflict handling ----
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

            // ---- copy to a temp name, then rename ----
            var partialPath = targetPath + ".dcenter-tmp";
            var copied = 0L;
            var writtenLength = 0L;
            var wantsHash = options.VerifyMode == CopyVerifyMode.SizeAndHash;
            // The source hash is computed while the source streams past, so
            // enabling SHA-256 verification no longer costs a second full read
            // of the source. The copy itself is still hashed separately.
            using SHA256? sourceSha = wantsHash ? SHA256.Create() : null;
            var progressClock = Stopwatch.StartNew();
            var lastReport = TimeSpan.MinValue;
            try
            {
                await using (var source = new FileStream(
                                 tracked.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var destination = new FileStream(
                                 partialPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize,
                                 FileOptions.Asynchronous))
                {
                    if (CopyStartHook is not null)
                    {
                        await CopyStartHook(ct).ConfigureAwait(false);
                    }

                    // Pre-allocate: one allocation instead of letting the file
                    // system grow the file a chunk at a time.
                    if (tracked.FileSize > 0)
                    {
                        destination.SetLength(tracked.FileSize);
                    }

                    var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                    try
                    {
                        int read;
                        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            copied += read;
                            sourceSha?.TransformBlock(buffer, 0, read, null, 0);

                            if (report is not null && tracked.FileSize > 0 && DueForReport(progressClock, ref lastReport))
                            {
                                ReportFileProgress(report, filesDoneBefore, totalFiles, bytesCopiedBefore, copied, totalBytes,
                                    tracked.FileName, Math.Min(100.0, copied * 100.0 / tracked.FileSize), $"Copying '{tracked.FileName}'…");
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    // Truncate to what was really written: if the source changed
                    // size during the copy, the pre-allocated tail must not
                    // survive and make the size check pass by accident.
                    if (destination.Length != copied)
                    {
                        destination.SetLength(copied);
                    }

                    await destination.FlushAsync(ct).ConfigureAwait(false);
                    writtenLength = destination.Length;
                }

                sourceSha?.TransformFinalBlock([], 0, 0);

                // Always publish the finished file, whatever the throttle says.
                if (report is not null)
                {
                    ReportFileProgress(report, filesDoneBefore, totalFiles, bytesCopiedBefore, copied, totalBytes,
                        tracked.FileName, 100.0, $"Copied '{tracked.FileName}'.");
                }

                // ---- verification ----
                var verified = false;
                if (writtenLength == tracked.FileSize)
                {
                    if (wantsHash)
                    {
                        verified = await VerifyHashAsync(sourceSha!.Hash!, partialPath, ct).ConfigureAwait(false);
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
                    throw new IOException($"Size verification failed ({writtenLength} bytes copied, {tracked.FileSize} expected).");
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
                // Persisted without the caller token: once the file is in
                // place, a racing cancel must not turn it back into a queue
                // entry (the retry run would then just skip it).
                await context.SaveChangesAsync(CancellationToken.None);
                QueueChanged?.Invoke();
                return new JobOutcome(CopyJobStatuses.Completed, copied);
            }
            catch (OperationCanceledException)
            {
                TryDelete(partialPath);
                tracked.Status = CopyJobStatuses.Queued; // Resume later.
                tracked.BytesCopied = null;
                tracked.ErrorMessage = "Cancelled by the operator; the job returned to the queue.";
                tracked.StartedAt = null;
                // The cancellation token is already cancelled here, so save
                // without it (the re-queue must be persisted).
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
            // Per-file isolation: unexpected failures mark only this job.
            tracked.Status = CopyJobStatuses.Failed;
            tracked.ErrorMessage = $"Unexpected error: {ex.Message}";
            tracked.CompletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(CancellationToken.None);
            QueueChanged?.Invoke();
            return new JobOutcome(CopyJobStatuses.Failed, 0);
        }
    }

    /// <summary>
    /// Compares the SHA-256 captured while the source streamed past with the
    /// hash of the copy. Verification is never skipped to make a copy look
    /// faster — the saving is that the source is read once, not twice.
    /// </summary>
    private static async Task<bool> VerifyHashAsync(byte[] sourceHash, string copyPath, CancellationToken ct)
    {
        try
        {
            var copyHash = await ComputeSha256Async(copyPath, ct).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(sourceHash, copyHash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<byte[]> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
    }

    /// <summary>At most one progress report per <see cref="ProgressReportInterval"/>.</summary>
    private bool DueForReport(Stopwatch clock, ref TimeSpan lastReport)
    {
        var elapsed = clock.Elapsed;
        if (lastReport != TimeSpan.MinValue && elapsed - lastReport < ProgressReportInterval)
        {
            return false;
        }

        lastReport = elapsed;
        return true;
    }

    private static void ReportFileProgress(
        IProgress<CopyProgressReport> report,
        int filesDoneBefore,
        int totalFiles,
        long bytesCopiedBefore,
        long copied,
        long totalBytes,
        string fileName,
        double percent,
        string message) =>
        report.Report(new CopyProgressReport(
            filesDoneBefore, totalFiles, bytesCopiedBefore + copied, totalBytes, fileName, percent, message));

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
            // The partial file will be overwritten by the next attempt.
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
