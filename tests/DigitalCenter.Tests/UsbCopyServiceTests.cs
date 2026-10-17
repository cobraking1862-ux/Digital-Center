using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DigitalCenter.Tests;

public sealed class UsbCopyServiceTests : IDisposable
{
    private readonly TestEnvironment _env = TestEnvironment.CreateAsync().Result;

    public void Dispose() => _env.Dispose();

    /// <summary>Creates a library with one movie file registered in the catalog and on disk.</summary>
    private async Task<(int FileId, string SourcePath, string LibraryRelative)> CreateMovieFileAsync(
        string fileName = "Inception (2010).mkv", string content = "video-data")
    {
        var relative = Path.Combine("Movies", "Hollywood", "Inception (2010)", fileName);
        var fullPath = Path.Combine(_env.LibraryDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);

        await using var db = await _env.Factory.CreateDbContextAsync();
        var title = new MediaTitle { Type = MediaKind.Movie, Title = "Inception", ReleaseYear = 2010 };
        db.MediaTitles.Add(title);
        await db.SaveChangesAsync();
        var file = new MediaFile
        {
            MovieTitleId = title.Id,
            FilePath = fullPath,
            FileSize = new FileInfo(fullPath).Length
        };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();
        return (file.Id, fullPath, relative);
    }

    private string DriveRoot => Path.Combine(_env.Root, "USB");

    [Fact]
    public async Task Copy_Completes_Verifies_And_Keeps_Source()
    {
        var (fileId, sourcePath, relative) = await CreateMovieFileAsync();
        var result = await _env.CopyService.EnqueueAsync([fileId], _env.LibraryDir);
        Assert.Equal(1, result.Queued);

        var summary = await _env.CopyService.RunAsync(
            new CopyRunOptions(DriveRoot, CopyConflictAction.Skip, CopyVerifyMode.Size),
            progress: null);

        Assert.Equal(1, summary.Completed);
        Assert.Equal(0, summary.Failed);
        var target = Path.Combine(DriveRoot, relative);
        Assert.True(File.Exists(target));
        Assert.Equal(await File.ReadAllTextAsync(sourcePath), await File.ReadAllTextAsync(target));

        await using var db = await _env.Factory.CreateDbContextAsync();
        var job = await db.CopyJobs.SingleAsync();
        Assert.Equal(CopyJobStatuses.Completed, job.Status);
        Assert.True(job.Verified);
        Assert.Equal(job.FileSize, job.BytesCopied);
        Assert.NotNull(job.CompletedAt);
        Assert.True(File.Exists(sourcePath)); // Sources are never deleted.
    }

    [Fact]
    public async Task ExistingTarget_With_Skip_Is_Skipped()
    {
        var (fileId, _, relative) = await CreateMovieFileAsync(content: "original");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(DriveRoot, relative))!);
        await File.WriteAllTextAsync(Path.Combine(DriveRoot, relative), "keep-me");

        await _env.CopyService.EnqueueAsync([fileId], _env.LibraryDir);
        var summary = await _env.CopyService.RunAsync(
            new CopyRunOptions(DriveRoot, CopyConflictAction.Skip, CopyVerifyMode.Size), null);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal("keep-me", await File.ReadAllTextAsync(Path.Combine(DriveRoot, relative)));

        await using var db = await _env.Factory.CreateDbContextAsync();
        var job = await db.CopyJobs.SingleAsync();
        Assert.Equal(CopyJobStatuses.Skipped, job.Status);
        Assert.Contains("skipped", job.ConflictNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExistingTarget_With_Overwrite_Is_Replaced()
    {
        var (fileId, _, relative) = await CreateMovieFileAsync(content: "new-content");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(DriveRoot, relative))!);
        await File.WriteAllTextAsync(Path.Combine(DriveRoot, relative), "old-content");

        await _env.CopyService.EnqueueAsync([fileId], _env.LibraryDir);
        var summary = await _env.CopyService.RunAsync(
            new CopyRunOptions(DriveRoot, CopyConflictAction.Overwrite, CopyVerifyMode.Size), null);

        Assert.Equal(1, summary.Completed);
        Assert.Equal("new-content", await File.ReadAllTextAsync(Path.Combine(DriveRoot, relative)));
    }

    [Fact]
    public async Task ExistingTarget_With_Rename_Gets_Numeric_Suffix()
    {
        var (fileId, _, relative) = await CreateMovieFileAsync(content: "second copy");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(DriveRoot, relative))!);
        await File.WriteAllTextAsync(Path.Combine(DriveRoot, relative), "first copy");

        await _env.CopyService.EnqueueAsync([fileId], _env.LibraryDir);
        var summary = await _env.CopyService.RunAsync(
            new CopyRunOptions(DriveRoot, CopyConflictAction.Rename, CopyVerifyMode.Size), null);

        Assert.Equal(1, summary.Completed);
        var renamed = Path.Combine(
            Path.GetDirectoryName(Path.Combine(DriveRoot, relative))!,
            "Inception (2010) (2).mkv");
        Assert.True(File.Exists(renamed));
        Assert.Equal("second copy", await File.ReadAllTextAsync(renamed));
        Assert.Equal("first copy", await File.ReadAllTextAsync(Path.Combine(DriveRoot, relative)));
    }

    [Fact]
    public async Task InsufficientSpace_Fails_Job_Without_Partial_Files()
    {
        var (fileId, sourcePath, relative) = await CreateMovieFileAsync();
        _env.Drives.FreeSpace = 2; // Far too little.

        await _env.CopyService.EnqueueAsync([fileId], _env.LibraryDir);
        var summary = await _env.CopyService.RunAsync(
            new CopyRunOptions(DriveRoot, CopyConflictAction.Skip, CopyVerifyMode.Size), null);

        Assert.Equal(1, summary.Failed);
        Assert.False(File.Exists(Path.Combine(DriveRoot, relative) + ".dcenter-tmp"));
        Assert.False(File.Exists(Path.Combine(DriveRoot, relative)));

        await using var db = await _env.Factory.CreateDbContextAsync();
        var job = await db.CopyJobs.SingleAsync();
        Assert.Equal(CopyJobStatuses.Failed, job.Status);
        Assert.Contains("Insufficient space", job.ErrorMessage);
    }

    [Fact]
    public async Task MissingSource_Fails_With_Clear_Message()
    {
        var (fileId, sourcePath, _) = await CreateMovieFileAsync();
        File.Delete(sourcePath);

        await _env.CopyService.EnqueueAsync([fileId], _env.LibraryDir);
        var summary = await _env.CopyService.RunAsync(
            new CopyRunOptions(DriveRoot, CopyConflictAction.Skip, CopyVerifyMode.Size), null);

        Assert.Equal(1, summary.Failed);
        await using var db = await _env.Factory.CreateDbContextAsync();
        var job = await db.CopyJobs.SingleAsync();
        Assert.Contains("source file is missing", job.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancel_Returns_Current_Job_To_Queue_And_Stops()
    {
        var (fileId1, _, _) = await CreateMovieFileAsync("Movie.A.mkv", new string('a', 400_000));
        var (fileId2, _, _) = await CreateMovieFileAsync("Movie.B.mkv", new string('b', 400_000));
        await _env.CopyService.EnqueueAsync([fileId1, fileId2], _env.LibraryDir);

        // Block job 1 inside the copy (temp file already created) until the
        // test cancels — makes the cancellation fully deterministic.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _env.CopyService.CopyStartHook = _ => gate.Task;

        var runTask = _env.CopyService.RunAsync(
            new CopyRunOptions(DriveRoot, CopyConflictAction.Skip, CopyVerifyMode.Size),
            progress: null);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline &&
               !Directory.GetFiles(DriveRoot, "*.dcenter-tmp", SearchOption.AllDirectories).Any())
        {
            await Task.Delay(10);
        }

        Assert.True(
            Directory.GetFiles(DriveRoot, "*.dcenter-tmp", SearchOption.AllDirectories).Any(),
            "The copy never started (no temp file appeared).");

        _env.CopyService.Cancel();
        gate.SetResult();
        var summary = await runTask;

        Assert.True(summary.WasCancelled);

        // Job 1 was cancelled mid-copy and returned to the queue; job 2 was
        // never started and is queued as well. No temp leftovers remain.
        await using var db = await _env.Factory.CreateDbContextAsync();
        var jobs = await db.CopyJobs.OrderBy(j => j.Id).ToListAsync();
        Assert.All(jobs, j => Assert.Equal(CopyJobStatuses.Queued, j.Status));
        Assert.DoesNotContain(Directory.GetFiles(DriveRoot, "*", SearchOption.AllDirectories), f => f.EndsWith(".dcenter-tmp", StringComparison.Ordinal));

        // The queue can be re-run later and completes everything.
        _env.CopyService.CopyStartHook = null;
        await _env.CopyService.RunAsync(
            new CopyRunOptions(DriveRoot, CopyConflictAction.Skip, CopyVerifyMode.Size), null);

        await using var verify = await _env.Factory.CreateDbContextAsync();
        var finalJobs = await verify.CopyJobs.OrderBy(j => j.Id).ToListAsync();
        Assert.All(finalJobs, j => Assert.Equal(CopyJobStatuses.Completed, j.Status));
        Assert.True(File.Exists(Path.Combine(DriveRoot, "Movies", "Hollywood", "Inception (2010)", "Movie.A.mkv")));
        Assert.True(File.Exists(Path.Combine(DriveRoot, "Movies", "Hollywood", "Inception (2010)", "Movie.B.mkv")));
    }

    [Fact]
    public async Task RetryFailed_Puts_Jobs_Back_In_Queue()
    {
        var (fileId, sourcePath, _) = await CreateMovieFileAsync();
        File.Delete(sourcePath);
        await _env.CopyService.EnqueueAsync([fileId], _env.LibraryDir);
        await _env.CopyService.RunAsync(new CopyRunOptions(DriveRoot, CopyConflictAction.Skip, CopyVerifyMode.Size), null);

        var retried = await _env.CopyService.RetryFailedAsync();
        Assert.Equal(1, retried);

        await using var db = await _env.Factory.CreateDbContextAsync();
        var job = await db.CopyJobs.SingleAsync();
        Assert.Equal(CopyJobStatuses.Queued, job.Status);
        Assert.Null(job.ErrorMessage);
    }

    [Fact]
    public async Task Queue_Persists_Across_Service_Instances()
    {
        var (fileId, _, _) = await CreateMovieFileAsync();
        await _env.CopyService.EnqueueAsync([fileId], _env.LibraryDir);

        // A brand new service instance (e.g. after an app restart) sees the queue.
        var fresh = new UsbCopyService(_env.Factory, _env.Drives);
        var jobs = await fresh.GetJobsAsync();
        var queued = Assert.Single(jobs);
        Assert.Equal(CopyJobStatuses.Queued, queued.Status);
        Assert.Equal("Inception (2010).mkv", queued.FileName);
    }

    [Fact]
    public async Task Enqueue_Duplicates_Are_Prevented()
    {
        var (fileId, _, _) = await CreateMovieFileAsync();
        var first = await _env.CopyService.EnqueueAsync([fileId], _env.LibraryDir);
        var second = await _env.CopyService.EnqueueAsync([fileId], _env.LibraryDir);

        Assert.Equal(1, first.Queued);
        Assert.Equal(0, second.Queued);
        Assert.Equal(1, second.AlreadyQueued);
    }

    [Fact]
    public async Task Estimate_Accounts_For_Skip_Of_Existing_Targets()
    {
        var (fileId, _, relative) = await CreateMovieFileAsync(content: "estimation");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(DriveRoot, relative))!);
        await File.WriteAllTextAsync(Path.Combine(DriveRoot, relative), "already there");

        await _env.CopyService.EnqueueAsync([fileId], _env.LibraryDir);

        var (requiredSkip, _, _) = await _env.CopyService.EstimateAsync(DriveRoot, CopyConflictAction.Skip);
        var (requiredOverwrite, _, _) = await _env.CopyService.EstimateAsync(DriveRoot, CopyConflictAction.Overwrite);

        Assert.Equal(0, requiredSkip);       // Existing target would be skipped.
        Assert.True(requiredOverwrite > 0);  // Overwrite needs the full size again.
    }

    [Fact]
    public async Task Series_Files_Target_Their_Episode_Relative_Path()
    {
        // Register a series file directly through the DB like the scanner would.
        var relative = Path.Combine("Series", "English", "Breaking Bad (2008)", "Season 01", "Breaking.Bad.S01E02.mkv");
        var fullPath = Path.Combine(_env.LibraryDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "episode");

        await using var db = await _env.Factory.CreateDbContextAsync();
        var title = new MediaTitle { Type = MediaKind.Series, Title = "Breaking Bad", ReleaseYear = 2008 };
        db.MediaTitles.Add(title);
        await db.SaveChangesAsync();
        var details = new SeriesDetails { MediaTitleId = title.Id };
        db.SeriesDetails.Add(details);
        var season = new Season { SeriesDetailsId = title.Id, SeasonNumber = 1 };
        db.Seasons.Add(season);
        await db.SaveChangesAsync();
        var episode = new Episode { SeasonId = season.Id, EpisodeNumber = 2, Title = "Cat's in the Bag..." };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();
        var file = new MediaFile { EpisodeId = episode.Id, FilePath = fullPath, FileSize = new FileInfo(fullPath).Length };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();

        var result = await _env.CopyService.EnqueueAsync([file.Id], _env.LibraryDir);
        Assert.Equal(1, result.Queued);

        var summary = await _env.CopyService.RunAsync(
            new CopyRunOptions(DriveRoot, CopyConflictAction.Skip, CopyVerifyMode.Size), null);
        Assert.Equal(1, summary.Completed);

        await using var verify = await _env.Factory.CreateDbContextAsync();
        var job = await verify.CopyJobs.SingleAsync();
        Assert.Equal(relative, job.RelativeTargetPath);
        Assert.True(File.Exists(Path.Combine(DriveRoot, relative)));
    }

    /// <summary>Records every progress report synchronously, so report counts can be asserted.</summary>
    private sealed class RecordingProgress : IProgress<CopyProgressReport>
    {
        public List<CopyProgressReport> Reports { get; } = [];

        public void Report(CopyProgressReport value) => Reports.Add(value);
    }

    [Fact]
    public async Task Progress_Reports_Are_Throttled_But_Still_End_At_One_Hundred_Percent()
    {
        const int size = 4 * 1024 * 1024; // four 1 MiB chunks
        var (throttledFileId, _, _) = await CreateMovieFileAsync("Throttled.mkv", new string('a', size));
        var (chattyFileId, _, _) = await CreateMovieFileAsync("Chatty.mkv", new string('b', size));

        // One copy with the production throttle …
        await _env.CopyService.EnqueueAsync([throttledFileId], _env.LibraryDir);
        _env.CopyService.ProgressReportInterval = TimeSpan.FromMinutes(5);
        var throttled = new RecordingProgress();
        var throttledSummary = await _env.CopyService.RunAsync(
            new CopyRunOptions(DriveRoot, CopyConflictAction.Skip, CopyVerifyMode.Size), throttled);
        Assert.Equal(1, throttledSummary.Completed);

        // … and the same copy reporting every chunk (the old behaviour).
        await _env.CopyService.EnqueueAsync([chattyFileId], _env.LibraryDir);
        _env.CopyService.ProgressReportInterval = TimeSpan.Zero;
        var chatty = new RecordingProgress();
        var chattySummary = await _env.CopyService.RunAsync(
            new CopyRunOptions(DriveRoot, CopyConflictAction.Skip, CopyVerifyMode.Size), chatty);
        Assert.Equal(1, chattySummary.Completed);

        // Both copies publish the finished file, so the UI never looks stalled.
        Assert.Contains(throttled.Reports, r => r.CurrentFilePercent >= 100.0);
        Assert.Contains(chatty.Reports, r => r.CurrentFilePercent >= 100.0);

        // The throttle removes the per-chunk UI churn: start + first chunk +
        // finished file + run summary, instead of one report per MiB.
        Assert.True(throttled.Reports.Count <= 4,
            $"Throttled run published {throttled.Reports.Count} reports: {string.Join(" | ", throttled.Reports.Select(r => r.Message))}");
        Assert.True(chatty.Reports.Count > throttled.Reports.Count,
            $"Unthrottled ({chatty.Reports.Count}) must publish more reports than throttled ({throttled.Reports.Count}).");
    }

    [Fact]
    public async Task Hash_Verification_Still_Runs_On_A_Multi_Chunk_Copy()
    {
        var (fileId, sourcePath, relative) = await CreateMovieFileAsync("Verified.mkv", new string('c', 3 * 1024 * 1024));
        await _env.CopyService.EnqueueAsync([fileId], _env.LibraryDir);

        var summary = await _env.CopyService.RunAsync(
            new CopyRunOptions(DriveRoot, CopyConflictAction.Skip, CopyVerifyMode.SizeAndHash), null);

        Assert.Equal(1, summary.Completed);
        Assert.Equal(0, summary.Failed);

        var target = Path.Combine(DriveRoot, relative);
        Assert.Equal(await File.ReadAllTextAsync(sourcePath), await File.ReadAllTextAsync(target));

        await using var db = await _env.Factory.CreateDbContextAsync();
        var job = await db.CopyJobs.SingleAsync();
        // The source hash is now computed while the source streams past; the
        // verification itself must never be dropped to make a copy look faster.
        Assert.True(job.Verified, "SHA-256 verification must still pass for a copy larger than one chunk.");
        Assert.Equal(job.FileSize, job.BytesCopied);
        Assert.False(File.Exists(target + ".dcenter-tmp"));
    }

    [Fact]
    public async Task A_File_Queued_Without_A_Known_Size_Is_Refreshed_From_Disk_And_Copied()
    {
        var (fileId, sourcePath, relative) = await CreateMovieFileAsync("UnknownSize.mkv", new string('d', 5_000));

        // Simulate a job whose queued size was unknown (0): the copy must take
        // the real size from the disk instead of failing its own size check.
        await using (var db = await _env.Factory.CreateDbContextAsync())
        {
            var mediaFile = await db.MediaFiles.SingleAsync(f => f.Id == fileId);
            mediaFile.FileSize = 0;
            await db.SaveChangesAsync();
        }

        await _env.CopyService.EnqueueAsync([fileId], _env.LibraryDir);
        var summary = await _env.CopyService.RunAsync(
            new CopyRunOptions(DriveRoot, CopyConflictAction.Skip, CopyVerifyMode.SizeAndHash), null);

        Assert.Equal(1, summary.Completed);
        Assert.Equal(0, summary.Failed);
        Assert.True(File.Exists(Path.Combine(DriveRoot, relative)));

        await using var verify = await _env.Factory.CreateDbContextAsync();
        var job = await verify.CopyJobs.SingleAsync();
        Assert.Equal(new FileInfo(sourcePath).Length, job.FileSize);
        Assert.Equal(job.FileSize, job.BytesCopied);
        Assert.True(job.Verified);
    }
}
