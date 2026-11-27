using System.Diagnostics;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;

namespace DigitalCenter.Core.Services;

public sealed record DramaCopyRunOptions(
    string DestinationRoot,
    string? DramaSubfolder,
    CopyConflictAction ConflictAction,
    bool CleanFilenames);

public sealed record DramaCopyRunSummary(
    int Completed,
    int Skipped,
    int Failed,
    bool WasCancelled,
    long BytesCopied,
    IReadOnlyList<string> Errors);

/// <summary>
/// Copies queued episodes from the USB Cart's Dramas bucket to a USB flash drive,
/// SD card, or connected Android/MTP phone, placing each drama's episodes inside
/// its own subfolder (&lt;Destination&gt;\Dramas\&lt;Drama Name&gt;\) so episode
/// filenames across different dramas never collide.
/// </summary>
public sealed class DramaCopyService
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly IDriveProbe _drives;
    private readonly DramaLibraryService _dramaLibrary;
    private readonly IPortableDeviceTransfer? _portable;
    private readonly IAppLogger? _logger;

    public DramaCopyService(
        IDriveProbe drives,
        DramaLibraryService dramaLibrary,
        IPortableDeviceTransfer? portable = null,
        IAppLogger? logger = null)
    {
        _drives = drives;
        _dramaLibrary = dramaLibrary;
        _portable = portable;
        _logger = logger;
    }

    public static string BuildTargetFileName(DramaEpisode ep, bool cleanFilenames)
    {
        if (!cleanFilenames)
        {
            return Path.GetFileName(ep.FilePath);
        }

        var ext = Path.GetExtension(ep.FilePath);
        var safeDrama = PathPlanner.SanitizeName(ep.DramaTitle, 70);
        if (ep.EpisodeNumber > 0)
        {
            return ep.SeasonNumber > 1
                ? $"{safeDrama} - S{ep.SeasonNumber:00}E{ep.EpisodeNumber:00}{ext}"
                : $"{safeDrama} - Episode {ep.EpisodeNumber:00}{ext}";
        }

        return $"{safeDrama} - {PathPlanner.SanitizeName(ep.Title, 60)}{ext}";
    }

    public async Task<DramaCopyRunSummary> CopyEpisodesAsync(
        IReadOnlyList<DramaEpisode> episodes,
        DramaCopyRunOptions options,
        IProgress<CopyProgressReport>? progress = null,
        CancellationToken ct = default)
    {
        if (episodes.Count == 0)
        {
            return new DramaCopyRunSummary(0, 0, 0, false, 0, []);
        }

        var isPortable = _portable is not null && _portable.IsPortablePath(options.DestinationRoot);
        var baseDir = isPortable
            ? _portable!.CombineSubfolder(options.DestinationRoot, options.DramaSubfolder)
            : (string.IsNullOrWhiteSpace(options.DramaSubfolder)
                ? options.DestinationRoot
                : Path.Combine(options.DestinationRoot, options.DramaSubfolder.Trim()));

        var totalBytes = episodes.Sum(e => Math.Max(0L, e.FileSize));
        var completed = 0;
        var skipped = 0;
        var failed = 0;
        var cancelled = false;
        long bytesCopiedTotal = 0;
        var errors = new List<string>();

        for (var i = 0; i < episodes.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var ep = episodes[i];
            if (!File.Exists(ep.FilePath))
            {
                failed++;
                errors.Add($"{ep.DramaTitle} ({ep.Title}): missing on disk.");
                continue;
            }

            var safeDramaFolder = PathPlanner.SanitizeName(ep.DramaTitle, 70);
            var targetDir = isPortable
                ? _portable!.CombineSubfolder(baseDir, safeDramaFolder)
                : Path.Combine(baseDir, safeDramaFolder);
            var targetFileName = BuildTargetFileName(ep, options.CleanFilenames);

            try
            {
                var (status, copiedBytes) = isPortable
                    ? await CopyOneToPortableAsync(ep, targetDir, targetFileName, options.ConflictAction, progress, i, episodes.Count, bytesCopiedTotal, totalBytes, ct)
                    : await CopyOneToDiskAsync(ep, targetDir, targetFileName, options.ConflictAction, progress, i, episodes.Count, bytesCopiedTotal, totalBytes, ct);

                if (status == "Copied")
                {
                    completed++;
                    bytesCopiedTotal += copiedBytes;
                    await _dramaLibrary.RecordEpisodeCopiedAndDequeueAsync(ep.Id, CancellationToken.None);
                }
                else if (status == "Skipped")
                {
                    skipped++;
                    await _dramaLibrary.SetEpisodeInCartAsync(ep.Id, false, CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{ep.DramaTitle} — {ep.Title}: {ex.Message}");
                _logger?.Error($"Drama episode copy failed for '{ep.FilePath}'.", ex);
            }
        }

        return new DramaCopyRunSummary(completed, skipped, failed, cancelled, bytesCopiedTotal, errors);
    }

    private async Task<(string Status, long BytesCopied)> CopyOneToDiskAsync(
        DramaEpisode ep,
        string targetDir,
        string targetFileName,
        CopyConflictAction conflictAction,
        IProgress<CopyProgressReport>? progress,
        int index,
        int totalFiles,
        long bytesBefore,
        long totalBytes,
        CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);
        var targetPath = Path.Combine(targetDir, targetFileName);

        if (File.Exists(targetPath))
        {
            switch (conflictAction)
            {
                case CopyConflictAction.Skip:
                    return ("Skipped", 0);
                case CopyConflictAction.Rename:
                    targetFileName = PathPlanner.ResolveFileNameConflict(targetDir, targetFileName);
                    targetPath = Path.Combine(targetDir, targetFileName);
                    break;
                case CopyConflictAction.Overwrite:
                    break;
            }
        }

        var tempPath = targetPath + ".dcpart";
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        var fileSize = ep.FileSize > 0 ? ep.FileSize : new FileInfo(ep.FilePath).Length;
        var clock = Stopwatch.StartNew();
        var lastReport = -ProgressInterval;

        try
        {
            await using (var src = new FileStream(ep.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan))
            await using (var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length))
            {
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    copied += read;

                    if (progress is not null && fileSize > 0 && clock.Elapsed - lastReport >= ProgressInterval)
                    {
                        lastReport = clock.Elapsed;
                        var pct = Math.Min(100.0, copied * 100.0 / fileSize);
                        progress.Report(new CopyProgressReport(
                            index, totalFiles, bytesBefore + copied, totalBytes,
                            $"{ep.DramaTitle} · {targetFileName}", pct,
                            $"Copying {ep.DramaTitle} — {ep.Title} ({pct:0}%)"));
                    }
                }
            }

            File.Move(tempPath, targetPath, overwrite: true);
            return ("Copied", copied);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception) { }
            throw;
        }
    }

    private async Task<(string Status, long BytesCopied)> CopyOneToPortableAsync(
        DramaEpisode ep,
        string targetDir,
        string targetFileName,
        CopyConflictAction conflictAction,
        IProgress<CopyProgressReport>? progress,
        int index,
        int totalFiles,
        long bytesBefore,
        long totalBytes,
        CancellationToken ct)
    {
        var portable = _portable!;
        portable.EnsureDirectory(targetDir);

        if (portable.FileExists(targetDir, targetFileName))
        {
            switch (conflictAction)
            {
                case CopyConflictAction.Skip:
                    return ("Skipped", 0);
                case CopyConflictAction.Rename:
                    var stem = Path.GetFileNameWithoutExtension(targetFileName);
                    var ext = Path.GetExtension(targetFileName);
                    var n = 2;
                    while (portable.FileExists(targetDir, $"{stem} ({n}){ext}")) n++;
                    targetFileName = $"{stem} ({n}){ext}";
                    break;
                case CopyConflictAction.Overwrite:
                    portable.DeleteFile(targetDir, targetFileName);
                    break;
            }
        }

        long copied = 0;
        var fileSize = ep.FileSize > 0 ? ep.FileSize : new FileInfo(ep.FilePath).Length;
        var clock = Stopwatch.StartNew();
        var lastReport = -ProgressInterval;

        try
        {
            await portable.UploadFileAsync(
                ep.FilePath,
                targetDir,
                targetFileName,
                bytes =>
                {
                    copied = bytes;
                    if (progress is not null && fileSize > 0 && clock.Elapsed - lastReport >= ProgressInterval)
                    {
                        lastReport = clock.Elapsed;
                        var pct = Math.Min(100.0, bytes * 100.0 / fileSize);
                        progress.Report(new CopyProgressReport(
                            index, totalFiles, bytesBefore + bytes, totalBytes,
                            $"{ep.DramaTitle} · {targetFileName}", pct,
                            $"Copying to device: {ep.DramaTitle} — {ep.Title} ({pct:0}%)"));
                    }
                },
                ct);

            return ("Copied", fileSize > 0 ? fileSize : copied);
        }
        catch (OperationCanceledException)
        {
            try { if (portable.FileExists(targetDir, targetFileName)) portable.DeleteFile(targetDir, targetFileName); } catch (Exception) { }
            throw;
        }
    }
}
