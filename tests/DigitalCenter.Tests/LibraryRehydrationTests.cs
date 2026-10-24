using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels;
using DigitalCenter.ViewModels.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DigitalCenter.Tests;

/// <summary>
/// Regression coverage for cold-start catalog rehydration. Reference data and
/// homepage rows must use the same awaited lifecycle; a second caller joining
/// an active refresh must not complete before the homepage collections are filled.
/// </summary>
public sealed class LibraryRehydrationTests
{
    [Fact]
    public async Task Awaited_Startup_Refresh_Rehydrates_Persisted_Home_Sections()
    {
        using var env = await TestEnvironment.CreateAsync();
        await SeedCatalogAsync(env);

        // A new query service and view model over the existing database emulate
        // constructing the services again after the process was closed. Delaying
        // context creation makes the constructor/InitializeAsync overlap
        // deterministic instead of depending on how quickly SQLite responds.
        var factory = new DelayedDbContextFactory(env.Factory, TimeSpan.FromMilliseconds(75));
        var query = new LibraryQueryService(factory, env.Initializer.FullTextSearchEnabled);
        var library = CreateLibraryViewModel(env, query);

        var earlyLoad = library.RefreshSafeAsync();      // old constructor / Loaded path
        var startupLoad = library.RefreshSafeAsync();   // awaited InitializeAsync path

        await startupLoad;

        // Capture state at the exact moment the awaited startup call completes.
        // With the old early-return coalescer these are all empty even though
        // earlyLoad is still querying the persisted rows in the background.
        var movieCount = library.HomeMovies.Count;
        var seriesCount = library.HomeSeries.Count;
        var yearOptions = library.YearOptions.ToArray();
        var languageOptions = library.LanguageOptions.ToArray();

        await earlyLoad; // also keeps the old implementation inside test cleanup

        Assert.Equal(1, movieCount);
        Assert.Equal(1, seriesCount);
        Assert.Equal("Restart Movie", library.HomeMovies.Single().Title);
        Assert.Equal("Restart Series", library.HomeSeries.Single().Title);
        Assert.Contains("2024", yearOptions);
        Assert.Contains("en", languageOptions);
        Assert.Equal("1 movie(s) · 1 series", library.TotalMatchesText);
        Assert.Equal(string.Empty, library.LoadError);
    }

    private static async Task SeedCatalogAsync(TestEnvironment env)
    {
        await using var db = await env.Factory.CreateDbContextAsync();

        var movie = new MediaTitle
        {
            Type = MediaKind.Movie,
            Title = "Restart Movie",
            ReleaseYear = 2024,
            OriginalLanguage = "en"
        };
        var series = new MediaTitle
        {
            Type = MediaKind.Series,
            Title = "Restart Series",
            ReleaseYear = 2023,
            OriginalLanguage = "ur"
        };
        var genre = new Genre { Name = "Drama" };

        db.MediaTitles.AddRange(movie, series);
        db.Genres.Add(genre);
        await db.SaveChangesAsync();

        db.TitleGenres.AddRange(
            new TitleGenre { MediaTitleId = movie.Id, GenreId = genre.Id },
            new TitleGenre { MediaTitleId = series.Id, GenreId = genre.Id });
        await db.SaveChangesAsync();
    }

    private static LibraryViewModel CreateLibraryViewModel(TestEnvironment env, LibraryQueryService query)
    {
        var dispatcher = new ImmediateUiDispatcher();
        return new LibraryViewModel(
            query,
            env.CopyService,
            env.Settings,
            new ActivityFeed(dispatcher),
            new NoOpDialogService(),
            new NoOpLogger());
    }

    private sealed class DelayedDbContextFactory(
        IDbContextFactory<DigitalCenterDbContext> inner,
        TimeSpan delay) : IDbContextFactory<DigitalCenterDbContext>
    {
        public DigitalCenterDbContext CreateDbContext() => inner.CreateDbContext();

        public async Task<DigitalCenterDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return await inner.CreateDbContextAsync(cancellationToken);
        }
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message, Exception? exception = null)
        {
        }
    }

    private sealed class NoOpDialogService : IDialogService
    {
        public void Info(string title, string message)
        {
        }

        public void Error(string title, string message)
        {
        }

        public bool Confirm(string title, string message) => true;

        public string? PickFolder(string? initialPath = null, string? title = null) => null;

        public string? PickFile(string title, string filter) => null;

        public void OpenInExplorer(string path)
        {
        }

        public void OpenUrl(string url)
        {
        }
    }
}
