using DigitalCenter.Core.Configuration;
using Xunit;

namespace DigitalCenter.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly TestEnvironment _env = TestEnvironment.CreateAsync().GetAwaiter().GetResult();

    [Fact]
    public void Settings_Round_Trip()
    {
        var service = new SettingsService(_env.Paths);
        var settings = new AppSettings
        {
            MoviesRoot = Path.Combine(_env.Root, "Library", "Movies"),
            SeriesRoot = Path.Combine(_env.Root, "Library", "Series"),
            WatchFolderPath = _env.WatchDir,
            StabilityDelaySeconds = 10,
            MinimumMatchConfidence = 0.75,
            CleanFilenames = false
        };

        service.Save(settings);
        var loaded = service.Load();

        Assert.Equal(settings.MoviesRoot, loaded.MoviesRoot);
        Assert.Equal(settings.SeriesRoot, loaded.SeriesRoot);
        Assert.Equal(settings.WatchFolderPath, loaded.WatchFolderPath);
        Assert.Equal(10, loaded.StabilityDelaySeconds);
        Assert.Equal(0.75, loaded.MinimumMatchConfidence);
        Assert.False(loaded.CleanFilenames);
    }

    [Fact]
    public void Load_Without_File_Returns_Defaults()
    {
        var service = new SettingsService(_env.Paths);
        var settings = service.Load();

        Assert.Null(settings.MoviesRoot);
        Assert.Null(settings.SeriesRoot);
        Assert.Equal("Skip", settings.UsbConflictAction);
        Assert.True(settings.CleanFilenames);
        Assert.Equal(8, settings.StabilityDelaySeconds);
    }

    [Fact]
    public void Legacy_LibraryRoot_Is_Migrated_To_Dual_Roots()
    {
        var service = new SettingsService(_env.Paths);
        var legacyRoot = Path.Combine(_env.Root, "OldLibrary");
        Directory.CreateDirectory(legacyRoot);
        service.Save(new AppSettings { LibraryRoot = legacyRoot, Version = 1 });

        var loaded = service.Load();

        Assert.Equal(Path.Combine(legacyRoot, "Movies"), loaded.MoviesRoot);
        Assert.Equal(Path.Combine(legacyRoot, "Series"), loaded.SeriesRoot);
    }

    [Fact]
    public void Validation_Rejects_Watch_Folder_Inside_Library_Roots()
    {
        var moviesRoot = Path.Combine(_env.Root, "Library", "Movies");
        Directory.CreateDirectory(moviesRoot);
        var settings = new AppSettings
        {
            MoviesRoot = moviesRoot,
            WatchFolderPath = Path.Combine(moviesRoot, "Watch")
        };

        var issues = SettingsService.Validate(settings);

        Assert.Contains(issues, i => i.Contains("outside", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validation_Requires_Absolute_Configured_Paths()
    {
        var settings = new AppSettings
        {
            MoviesRoot = "relative/path",
            WatchFolderPath = "watch"
        };

        var issues = SettingsService.Validate(settings);

        Assert.Equal(2, issues.Count(i => i.Contains("absolute", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Validation_Reports_Missing_Folders()
    {
        var settings = new AppSettings
        {
            MoviesRoot = Path.Combine(_env.Root, "does-not-exist"),
            WatchFolderPath = Path.Combine(_env.Root, "also-missing")
        };

        var issues = SettingsService.Validate(settings);

        Assert.Equal(2, issues.Count(i => i.Contains("does not exist", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Valid_Settings_Produce_No_Issues()
    {
        var moviesRoot = Path.Combine(_env.LibraryDir, "Movies");
        var seriesRoot = Path.Combine(_env.LibraryDir, "Series");
        Directory.CreateDirectory(moviesRoot);
        Directory.CreateDirectory(seriesRoot);

        var settings = new AppSettings
        {
            MoviesRoot = moviesRoot,
            SeriesRoot = seriesRoot,
            WatchFolderPath = _env.WatchDir
        };

        Assert.Empty(SettingsService.Validate(settings));
    }

    [Fact]
    public void Validation_Rejects_Template_Without_Title()
    {
        var settings = new AppSettings { MovieFilenameTemplate = "{Year}" };

        var issues = SettingsService.Validate(settings);

        Assert.Contains(issues, i => i.Contains("{Title}", StringComparison.Ordinal));
    }

    [Fact]
    public void Validation_Rejects_Out_Of_Range_Confidence()
    {
        var settings = new AppSettings { MinimumMatchConfidence = 0.10 };

        var issues = SettingsService.Validate(settings);

        Assert.Contains(issues, i => i.Contains("confidence", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => _env.Dispose();
}
