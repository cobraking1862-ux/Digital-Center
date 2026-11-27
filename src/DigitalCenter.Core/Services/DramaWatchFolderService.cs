using System.Collections.Concurrent;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;

namespace DigitalCenter.Core.Services;

/// <summary>
/// Watches the configured Drama Watch Folder for incoming drama subfolders and
/// episode videos. Once a file's size stabilizes, moves it into
/// &lt;DramaRoot&gt;\&lt;Drama Name&gt;\ and enriches the drama from TMDb.
/// </summary>
public sealed class DramaWatchFolderService : IDisposable
{
    public event Action<string>? StatusChanged;
    public event Action? DramaIngested;

    private readonly DramaLibraryService _dramaLibrary;
    private readonly WatchFolderIssueService? _issues;
    private readonly IAppLogger? _logger;
    private readonly ConcurrentDictionary<string, (long Size, DateTime FirstStableUtc)> _observed = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private string? _watchedPath;

    public DramaWatchFolderService(
        DramaLibraryService dramaLibrary,
        IAppLogger? logger = null,
        WatchFolderIssueService? issues = null)
    {
        _dramaLibrary = dramaLibrary;
        _logger = logger;
        _issues = issues;
    }

    public TimeSpan StabilityDelay { get; set; } = TimeSpan.FromSeconds(8);
    public bool IsRunning => _loopTask is not null && !_loopTask.IsCompleted;
    public DateTime? LastScanUtc { get; private set; }

    public void Start(string watchPath)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(watchPath) || !Directory.Exists(watchPath))
        {
            return;
        }

        _watchedPath = Path.GetFullPath(watchPath.Trim());
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loopTask = Task.Run(() => PollLoopAsync(_watchedPath, ct), ct);
        _logger?.Info($"Drama Watch Folder started on '{_watchedPath}'.");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch (Exception) { }
        _cts?.Dispose();
        _cts = null;
        _loopTask = null;
        _observed.Clear();
    }

    public async Task<int> RescanOnceAsync(string watchPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(watchPath) || !Directory.Exists(watchPath))
        {
            return 0;
        }

        var fullRoot = Path.GetFullPath(watchPath.Trim());
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!DramaFilenameParser.IsVideoFile(file))
            {
                continue;
            }

            if (!IsFileAccessible(file))
            {
                if (_issues is not null)
                {
                    await _issues.RecordIssueAsync(
                        WatchFolderIssueTypes.Drama,
                        WatchFolderIssueCategories.Interrupted,
                        file,
                        "File is still downloading or locked by another application.",
                        ct);
                }
                continue;
            }

            try
            {
                if (await _dramaLibrary.IngestWatchFileAsync(file, fullRoot, ct))
                {
                    moved++;
                    StatusChanged?.Invoke($"Drama Watch Folder imported: {Path.GetFileName(file)}");
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Drama Watch Folder could not import '{file}': {ex.Message}");
                if (_issues is not null)
                {
                    await _issues.RecordIssueAsync(
                        WatchFolderIssueTypes.Drama,
                        WatchFolderIssueCategories.Failed,
                        file,
                        ex.Message,
                        ct);
                }
            }
        }

        LastScanUtc = DateTime.UtcNow;
        CleanupEmptySubdirectories(fullRoot);
        if (moved > 0)
        {
            DramaIngested?.Invoke();
        }

        return moved;
    }

    private async Task PollLoopAsync(string watchRoot, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(watchRoot, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Drama Watch Folder poll warning: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(string watchRoot, CancellationToken ct)
    {
        if (!Directory.Exists(watchRoot))
        {
            return;
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var movedAny = false;

        foreach (var file in Directory.EnumerateFiles(watchRoot, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!DramaFilenameParser.IsVideoFile(file))
            {
                continue;
            }

            seen.Add(file);
            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception)
            {
                continue;
            }

            if (size <= 0)
            {
                continue;
            }

            if (_observed.TryGetValue(file, out var prev))
            {
                if (prev.Size != size)
                {
                    _observed[file] = (size, now);
                    continue;
                }

                if (now - prev.FirstStableUtc < StabilityDelay)
                {
                    continue;
                }

                // Skip quietly if still locked by downloader; will retry on next poll tick
                if (!IsFileAccessible(file))
                {
                    continue;
                }

                try
                {
                    if (await _dramaLibrary.IngestWatchFileAsync(file, watchRoot, ct))
                    {
                        _observed.TryRemove(file, out _);
                        movedAny = true;
                        StatusChanged?.Invoke($"Drama Watch Folder imported: {Path.GetFileName(file)}");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"Drama Watch Folder import retry pending for '{file}': {ex.Message}");
                }
            }
            else
            {
                _observed[file] = (size, now);
            }
        }

        foreach (var key in _observed.Keys.ToList())
        {
            if (!seen.Contains(key))
            {
                _observed.TryRemove(key, out _);
            }
        }

        LastScanUtc = DateTime.UtcNow;
        if (movedAny)
        {
            CleanupEmptySubdirectories(watchRoot);
            DramaIngested?.Invoke();
        }
    }

    private static bool IsFileAccessible(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void CleanupEmptySubdirectories(string root)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    try { Directory.Delete(dir); } catch (Exception) { }
                }
            }
        }
        catch (Exception) { }
    }

    public void Dispose() => Stop();
}
