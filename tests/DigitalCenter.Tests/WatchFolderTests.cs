using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using Xunit;

namespace DigitalCenter.Tests;

public sealed class WatchFolderTests : IDisposable
{
    private readonly TestEnvironment _env = TestEnvironment.CreateAsync().Result;

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task StableFile_Is_Detected_As_Pending()
    {
        await File.WriteAllTextAsync(Path.Combine(_env.WatchDir, "Some.Movie.2022.mkv"), "abc");

        var result = await _env.WatchFolder.ScanOnceAsync(_env.WatchDir);

        Assert.Equal(1, result.FilesSeen);
        Assert.Equal(1, result.NewPending);
        Assert.Equal(0, result.Unstable);

        await using var db = await _env.Factory.CreateDbContextAsync();
        var log = db.MetadataImportLogs.ToList().Single();
        Assert.Equal(ImportStatuses.Pending, log.Status);
        Assert.Equal(ImportStages.Detected, log.Stage);
        Assert.Equal("Some.Movie.2022.mkv", log.SourceFile);
        Assert.Contains(_env.WatchDir, log.SourcePath);
        Assert.False(log.IsSeries);
    }

    [Fact]
    public async Task SeriesFile_Picks_Up_Season_From_Folder()
    {
        var seasonDir = Path.Combine(_env.WatchDir, "Show Name", "Season 2");
        Directory.CreateDirectory(seasonDir);
        await File.WriteAllTextAsync(Path.Combine(seasonDir, "Episode 5.mkv"), "abc");

        await _env.WatchFolder.ScanOnceAsync(_env.WatchDir);

        await using var db = await _env.Factory.CreateDbContextAsync();
        var log = db.MetadataImportLogs.ToList().Single();
        Assert.True(log.IsSeries);
        Assert.Equal(2, log.ParsedSeason);
        Assert.Equal(5, log.ParsedEpisode);
        Assert.Equal("Show Name", log.ParsedTitle);
    }

    [Fact]
    public async Task StillGrowingFile_Is_Not_Detected_Yet()
    {
        var path = Path.Combine(_env.WatchDir, "Growing.Movie.2023.mkv");
        await File.WriteAllTextAsync(path, "start");

        // Start a "download" that keeps writing (and flushing) well beyond
        // the stability window, and wait until the first append is visible
        // so the scan is guaranteed to observe a changing size.
        var writerTask = Task.Run(async () =>
        {
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(30);
                await stream.WriteAsync(new byte[4096]);
                await stream.FlushAsync();
            }
        });

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (new FileInfo(path).Length == 5 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            var result = await _env.WatchFolder.ScanOnceAsync(_env.WatchDir);
            Assert.Equal(1, result.Unstable);
            Assert.Equal(0, result.NewPending);
        }
        finally
        {
            await writerTask;
        }

        // Once writing stops, the next pass picks the file up.
        var secondPass = await _env.WatchFolder.ScanOnceAsync(_env.WatchDir);
        Assert.Equal(1, secondPass.NewPending);
    }

    [Fact]
    public async Task KnownFile_Is_Not_Detected_Twice()
    {
        await File.WriteAllTextAsync(Path.Combine(_env.WatchDir, "Known.2020.mkv"), "abc");
        await _env.WatchFolder.ScanOnceAsync(_env.WatchDir);

        var second = await _env.WatchFolder.ScanOnceAsync(_env.WatchDir);

        Assert.Equal(0, second.NewPending);
        Assert.Equal(1, second.AlreadyKnown);

        await using var db = await _env.Factory.CreateDbContextAsync();
        Assert.Single(db.MetadataImportLogs);
    }

    [Fact]
    public async Task NonMediaFiles_Are_Logged_Unsupported_And_Kept()
    {
        await File.WriteAllTextAsync(Path.Combine(_env.WatchDir, "readme.txt"), "text");
        await File.WriteAllTextAsync(Path.Combine(_env.WatchDir, "Avengers.Endgame.zip"), "zip");

        var result = await _env.WatchFolder.ScanOnceAsync(_env.WatchDir);

        Assert.Equal(2, result.FilesSeen);
        Assert.Equal(0, result.NewPending);
        Assert.Equal(2, result.UnsupportedLogged);

        await using var db = await _env.Factory.CreateDbContextAsync();
        var logs = db.MetadataImportLogs.ToList();
        Assert.Equal(2, logs.Count);
        Assert.All(logs, l =>
        {
            Assert.Equal(ImportStatuses.Unsupported, l.Status);
            Assert.True(File.Exists(l.SourcePath), "Unsupported files are never touched.");
        });
    }

    [Fact]
    public async Task MissingWatchFolder_Returns_Empty_Result_Without_Throwing()
    {
        var result = await _env.WatchFolder.ScanOnceAsync(Path.Combine(_env.Root, "does-not-exist"));
        Assert.Equal(0, result.FilesSeen);
    }
}
