using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DigitalCenter.Tests;

/// <summary>
/// The Watch Folder has exactly one owner: the Management workspace writes its
/// path and monitoring switches, Settings never does. These tests pin that
/// contract — a save from either screen keeps what the other screen owns, the
/// single monitor follows a path change, and changing the path never moves media
/// or erases records.
/// </summary>
public sealed class WatchFolderOwnershipTests : IDisposable
{
    private readonly TestEnvironment _env = TestEnvironment.CreateAsync().Result;

    public void Dispose() => _env.Dispose();

    private string MoviesRoot => Path.Combine(_env.LibraryDir, "Movies");
    private string SeriesRoot => Path.Combine(_env.LibraryDir, "Series");

    [Fact]
    public void A_Settings_Save_Keeps_The_Watch_Folder_Owned_By_Management()
    {
        _env.SaveSettings();
        Directory.CreateDirectory(MoviesRoot);
        Directory.CreateDirectory(SeriesRoot);

        // Management saves the Watch Folder (path + monitoring switches).
        _env.Settings.Update(settings =>
        {
            settings.WatchFolderPath = _env.WatchDir;
            settings.WatchFolderEnabled = true;
            settings.ScanOnStartup = true;
            settings.WatchFolderIncludeSubfolders = true;
            settings.StabilityDelaySeconds = 12;
        });

        // Settings saves only what it owns — through the same load-then-apply
        // path the Settings view model uses. Building a fresh AppSettings here
        // is what used to wipe the Watch Folder.
        _env.Settings.Update(settings =>
        {
            settings.DownloadPosters = false;
            settings.UsbCopySubfolder = "Movies";
            settings.MinimumMatchConfidence = 0.72;
        });

        var saved = _env.Settings.Load();
        Assert.Equal(_env.WatchDir, saved.WatchFolderPath);
        Assert.True(saved.WatchFolderEnabled);
        Assert.True(saved.ScanOnStartup);
        Assert.True(saved.WatchFolderIncludeSubfolders);
        Assert.Equal(12, saved.StabilityDelaySeconds);
        Assert.Equal(0.72, saved.MinimumMatchConfidence, 3);
        Assert.False(saved.DownloadPosters);
        Assert.Equal("Movies", saved.UsbCopySubfolder);
    }

    [Fact]
    public void A_Management_Save_Keeps_The_Settings_Owned_Values()
    {
        _env.SaveSettings();
        _env.Settings.Update(settings =>
        {
            settings.MoviesRoot = MoviesRoot;
            settings.SeriesRoot = SeriesRoot;
            settings.VerifyHashes = true;
            settings.MovieFilenameTemplate = "{Title} ({Year})";
        });

        var newWatch = Path.Combine(_env.Root, "Incoming");
        Directory.CreateDirectory(newWatch);

        _env.Settings.Update(settings =>
        {
            settings.WatchFolderPath = newWatch;
            settings.StabilityDelaySeconds = 5;
        });

        var saved = _env.Settings.Load();
        Assert.Equal(newWatch, saved.WatchFolderPath);
        Assert.Equal(5, saved.StabilityDelaySeconds);
        Assert.Equal(MoviesRoot, saved.MoviesRoot);
        Assert.Equal(SeriesRoot, saved.SeriesRoot);
        Assert.True(saved.VerifyHashes);
        Assert.Equal("{Title} ({Year})", saved.MovieFilenameTemplate);
    }

    [Fact]
    public async Task Changing_The_Path_Moves_The_Single_Monitor_And_Never_Touches_Files_Or_Records()
    {
        _env.SaveSettings();
        var newWatch = Path.Combine(_env.Root, "Incoming");
        Directory.CreateDirectory(newWatch);

        // A file detected under the old path, with an import record.
        var sourcePath = Path.Combine(_env.WatchDir, "Some.Movie.2021.mkv");
        File.WriteAllText(sourcePath, "video-data");
        var scan = await _env.WatchFolder.RescanOnceAsync(_env.WatchDir);
        Assert.Equal(1, scan.NewPending);

        _env.WatchFolder.Start(_env.WatchDir, TimeSpan.FromMilliseconds(50));
        Assert.True(_env.WatchFolder.IsRunning);
        Assert.Equal(_env.WatchDir, _env.WatchFolder.CurrentPath);

        // Management saves a new path; the monitor follows it.
        _env.Settings.Update(settings => settings.WatchFolderPath = newWatch);
        _env.WatchFolder.ChangePath(newWatch);

        Assert.True(_env.WatchFolder.IsRunning);
        Assert.Equal(newWatch, _env.WatchFolder.CurrentPath);

        // Nothing was moved and no record was erased.
        Assert.True(File.Exists(sourcePath), "Changing the Watch Folder path must never move media.");
        await using (var db = await _env.Factory.CreateDbContextAsync())
        {
            var log = db.MetadataImportLogs.Single();
            Assert.Equal(sourcePath, log.SourcePath);
            Assert.Equal("Some.Movie.2021.mkv", log.SourceFile);
        }

        // Only the new folder is scanned from now on.
        File.WriteAllText(Path.Combine(_env.WatchDir, "Old.Folder.Movie.2020.mkv"), "video-data");
        File.WriteAllText(Path.Combine(newWatch, "New.Folder.Movie.2022.mkv"), "video-data");
        var after = await _env.WatchFolder.RescanConfiguredAsync();

        Assert.Equal(1, after.NewPending);
        await using (var db = await _env.Factory.CreateDbContextAsync())
        {
            Assert.Equal(2, db.MetadataImportLogs.Count());
            Assert.Contains(db.MetadataImportLogs.ToList(), l => l.SourceFile == "New.Folder.Movie.2022.mkv");
            Assert.DoesNotContain(db.MetadataImportLogs.ToList(), l => l.SourceFile == "Old.Folder.Movie.2020.mkv");
        }

        _env.WatchFolder.Stop();
        Assert.False(_env.WatchFolder.IsRunning);
        Assert.Null(_env.WatchFolder.CurrentPath);
    }

    [Fact]
    public void Starting_With_The_Same_Path_Does_Not_Restart_The_Monitor()
    {
        _env.SaveSettings();
        _env.WatchFolder.Start(_env.WatchDir, TimeSpan.FromMilliseconds(50));
        var first = _env.WatchFolder.CurrentPath;

        _env.WatchFolder.Start(_env.WatchDir, TimeSpan.FromMilliseconds(50));

        Assert.True(_env.WatchFolder.IsRunning);
        Assert.Equal(first, _env.WatchFolder.CurrentPath);
        _env.WatchFolder.Stop();
    }

    [Fact]
    public async Task Rescan_Of_A_Configured_But_Missing_Folder_Is_Empty_Not_Fatal()
    {
        _env.Settings.Update(settings => settings.WatchFolderPath = Path.Combine(_env.Root, "does-not-exist"));

        var result = await _env.WatchFolder.RescanConfiguredAsync();

        Assert.Equal(0, result.FilesSeen);
        Assert.Equal(0, result.NewPending);
    }

    [Fact]
    public void Watch_Folder_Validation_Rejects_Unusable_Paths()
    {
        _env.SaveSettings();

        var relative = SettingsService.ValidateWatchFolder(new AppSettings { WatchFolderPath = @"Watch\Incoming" });
        Assert.Contains(relative, issue => issue.Contains("absolute", StringComparison.OrdinalIgnoreCase));

        var missing = SettingsService.ValidateWatchFolder(new AppSettings { WatchFolderPath = Path.Combine(_env.Root, "nope") });
        Assert.Contains(missing, issue => issue.Contains("does not exist", StringComparison.OrdinalIgnoreCase));

        var insideLibrary = SettingsService.ValidateWatchFolder(new AppSettings
        {
            MoviesRoot = MoviesRoot,
            SeriesRoot = SeriesRoot,
            WatchFolderPath = Path.Combine(MoviesRoot, "Incoming")
        });
        Assert.Contains(insideLibrary, issue => issue.Contains("outside the movies library root", StringComparison.OrdinalIgnoreCase));

        var badDelay = SettingsService.ValidateWatchFolder(new AppSettings { WatchFolderPath = _env.WatchDir, StabilityDelaySeconds = 1 });
        Assert.Contains(badDelay, issue => issue.Contains("stability", StringComparison.OrdinalIgnoreCase));

        var valid = SettingsService.ValidateWatchFolder(new AppSettings
        {
            MoviesRoot = MoviesRoot,
            SeriesRoot = SeriesRoot,
            WatchFolderPath = _env.WatchDir,
            StabilityDelaySeconds = 8
        });
        Assert.Empty(valid);
    }

    [Fact]
    public async Task A_Series_Episode_Detected_In_The_Watch_Folder_Is_Never_Logged_As_A_Movie()
    {
        _env.SaveSettings();

        var seasonDir = Path.Combine(_env.WatchDir, "Reacher", "Season 1");
        Directory.CreateDirectory(seasonDir);
        await File.WriteAllTextAsync(Path.Combine(seasonDir, "Reacher.S01.E01.mkv"), "video-data");

        await _env.WatchFolder.RescanConfiguredAsync();

        await using var db = await _env.Factory.CreateDbContextAsync();
        var log = await db.MetadataImportLogs.SingleAsync();
        Assert.True(log.IsSeries);
        Assert.Equal(1, log.ParsedSeason);
        Assert.Equal(1, log.ParsedEpisode);
        Assert.Equal("Reacher", log.ParsedTitle);
        Assert.Equal(ImportStatuses.Pending, log.Status);
    }
}
