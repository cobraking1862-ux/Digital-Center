using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Services;
using DigitalCenter.Core.Tmdb;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Tests;

/// <summary>
/// Creates an isolated environment (temp data root + temp SQLite database +
/// initialized services) per test. Nothing here touches a real library, the
/// network or a real TMDb account.
/// </summary>
public sealed class TestEnvironment : IDisposable
{
    public string Root { get; }
    public AppPaths Paths { get; }
    public IDbContextFactory<DigitalCenterDbContext> Factory { get; }
    public DatabaseInitializer Initializer { get; }
    public FilenameParser Parser { get; }
    public LibraryScanner Scanner { get; }
    public BackupService BackupService { get; }
    public LibraryQueryService QueryService { get; }
    public PosterCache Posters { get; }
    public ImportService Imports { get; }
    public WatchFolderService WatchFolder { get; }
    public UsbCopyService CopyService { get; }
    public MaintenanceService Maintenance { get; }
    public SettingsService Settings { get; }
    public FakeTmdbProvider Tmdb { get; } = new();
    public FakeDriveProbe Drives { get; } = new();

    /// <summary>The fake library root used by tests that need one.</summary>
    public string LibraryDir { get; } = string.Empty;

    /// <summary>The fake watch folder used by tests that need one.</summary>
    public string WatchDir { get; } = string.Empty;

    private TestEnvironment(string root, AppPaths paths)
    {
        Root = root;
        Paths = paths;
        LibraryDir = Path.Combine(root, "Library");
        WatchDir = Path.Combine(root, "Watch");
        Directory.CreateDirectory(LibraryDir);
        Directory.CreateDirectory(WatchDir);

        Factory = new DbContextFactoryProvider(paths.DatabaseFile);
        Parser = new FilenameParser();
        Scanner = new LibraryScanner(Factory, Parser);
        BackupService = new BackupService(paths);
        QueryService = new LibraryQueryService(Factory, ftsEnabled: true);
        Posters = new PosterCache(paths);
        Settings = new SettingsService(paths);
        Imports = new ImportService(Factory, Tmdb, Settings, Posters);
        WatchFolder = new WatchFolderService(Factory, Parser, Imports, Settings)
        {
            StabilityDelay = TimeSpan.FromMilliseconds(60)
        };
        CopyService = new UsbCopyService(Factory, Drives);
        Maintenance = new MaintenanceService(Factory);
        Initializer = new DatabaseInitializer(Factory, new FileLogger(paths.LogsDirectory));
    }

    public static async Task<TestEnvironment> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "digitalcenter-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths { DataRoot = root };
        paths.EnsureDataDirectories();

        var env = new TestEnvironment(root, paths);
        await env.Initializer.InitializeAsync();
        return env;
    }

    public string NewLibraryDir() => LibraryDir;

    /// <summary>Saves valid settings pointing at the fake library/watch folders.</summary>
    public void SaveSettings()
    {
        Settings.Save(new AppSettings
        {
            MoviesRoot = Path.Combine(LibraryDir, "Movies"),
            SeriesRoot = Path.Combine(LibraryDir, "Series"),
            WatchFolderPath = WatchDir,
            StabilityDelaySeconds = 2
        });
        Directory.CreateDirectory(Path.Combine(LibraryDir, "Movies"));
        Directory.CreateDirectory(Path.Combine(LibraryDir, "Series"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup; temp dirs are harmless leftovers.
        }
    }

    /// <summary>Simplified provider wrapper so tests can also run ad-hoc queries.</summary>
    private sealed class DbContextFactoryProvider : IDbContextFactory<DigitalCenterDbContext>
    {
        private readonly string _databaseFile;

        public DbContextFactoryProvider(string databaseFile)
        {
            _databaseFile = databaseFile;
        }

        public DigitalCenterDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<DigitalCenterDbContext>()
                .UseSqlite($"Data Source={_databaseFile}")
                .Options;
            return new DigitalCenterDbContext(options);
        }

        public SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection($"Data Source={_databaseFile}");
            connection.Open();
            return connection;
        }
    }
}

/// <summary>
/// TMDb test double: fully offline, scriptable, never touches the network.
/// Used by the import workflow tests to prove the approval flow end-to-end.
/// </summary>
public sealed class FakeTmdbProvider : ITmdbMetadataProvider
{
    public bool Configured { get; set; } = true;

    public IReadOnlyList<TmdbSearchCandidate> MovieSearchResults { get; set; } = [];
    public IReadOnlyList<TmdbSearchCandidate> SeriesSearchResults { get; set; } = [];
    public Dictionary<long, TmdbTitleMetadata> Movies { get; } = [];
    public Dictionary<long, TmdbTitleMetadata> Series { get; } = [];
    public Dictionary<string, (long Id, bool IsSeries)> ImdbIndex { get; } = [];
    public List<string> ImdbLookups { get; } = [];
    public bool ThrowOnSearch { get; set; }
    public int MovieDetailCalls { get; private set; }
    public int SeriesDetailCalls { get; private set; }
    public int SeriesSummaryCalls { get; private set; }
    public List<(long SeriesId, int Season)> SeasonCalls { get; } = [];
    public byte[] PosterBytes { get; set; } = [0x89, 0x50, 0x4E, 0x47]; // PNG header bytes

    public bool IsConfigured => Configured;

    public Task<IReadOnlyList<TmdbSearchCandidate>> SearchMoviesAsync(string query, int? year, CancellationToken ct = default)
    {
        if (ThrowOnSearch)
        {
            throw new TmdbRequestException("offline (fake)");
        }

        if (!Configured)
        {
            throw new TmdbNotConfiguredException();
        }

        return Task.FromResult(MovieSearchResults);
    }

    public Task<IReadOnlyList<TmdbSearchCandidate>> SearchSeriesAsync(string query, int? year, CancellationToken ct = default)
    {
        if (ThrowOnSearch)
        {
            throw new TmdbRequestException("offline (fake)");
        }

        if (!Configured)
        {
            throw new TmdbNotConfiguredException();
        }

        return Task.FromResult(SeriesSearchResults);
    }

    public Task<TmdbTitleMetadata?> GetMovieAsync(long tmdbId, CancellationToken ct = default)
    {
        MovieDetailCalls++;
        if (!Configured)
        {
            throw new TmdbNotConfiguredException();
        }

        return Task.FromResult(Movies.TryGetValue(tmdbId, out var metadata) ? metadata : null);
    }

    public Task<TmdbTitleMetadata?> GetSeriesAsync(long tmdbId, CancellationToken ct = default)
    {
        SeriesDetailCalls++;
        if (!Configured)
        {
            throw new TmdbNotConfiguredException();
        }

        return Task.FromResult(Series.TryGetValue(tmdbId, out var metadata) ? metadata : null);
    }

    /// <summary>Summary semantics: the series itself, without episode lists (one request).</summary>
    public Task<TmdbTitleMetadata?> GetSeriesSummaryAsync(long tmdbId, CancellationToken ct = default)
    {
        SeriesSummaryCalls++;
        if (!Configured)
        {
            throw new TmdbNotConfiguredException();
        }

        if (!Series.TryGetValue(tmdbId, out var metadata))
        {
            return Task.FromResult<TmdbTitleMetadata?>(null);
        }

        return Task.FromResult<TmdbTitleMetadata?>(metadata with
        {
            Seasons = [],
            SeasonCount = metadata.SeasonCount ?? metadata.Seasons.Count
        });
    }

    /// <summary>One season's episodes, or null when the series does not have that season.</summary>
    public Task<TmdbSeasonMetadata?> GetSeriesSeasonAsync(long tmdbId, int seasonNumber, CancellationToken ct = default)
    {
        SeasonCalls.Add((tmdbId, seasonNumber));
        if (!Configured)
        {
            throw new TmdbNotConfiguredException();
        }

        if (!Series.TryGetValue(tmdbId, out var metadata))
        {
            return Task.FromResult<TmdbSeasonMetadata?>(null);
        }

        var season = metadata.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber);
        return Task.FromResult<TmdbSeasonMetadata?>(SeasonUnavailable ? null : season);
    }

    /// <summary>Simulates an unavailable season (TMDb 404 / offline): the import must still succeed.</summary>
    public bool SeasonUnavailable { get; set; }

    public Task<Stream> DownloadPosterAsync(string posterPath, CancellationToken ct = default)
    {
        if (!Configured)
        {
            throw new TmdbNotConfiguredException();
        }

        return Task.FromResult<Stream>(new MemoryStream(PosterBytes, writable: false));
    }

    public Task<(long Id, bool IsSeries)?> FindByImdbIdAsync(string imdbId, CancellationToken ct = default)
    {
        if (!Configured)
        {
            throw new TmdbNotConfiguredException();
        }

        ImdbLookups.Add(imdbId);
        return Task.FromResult(ImdbIndex.TryGetValue(imdbId, out var hit) ? ((long Id, bool IsSeries)?)hit : null);
    }

    public Task<Stream> DownloadBackdropAsync(string backdropPath, CancellationToken ct = default)
    {
        if (!Configured)
        {
            throw new TmdbNotConfiguredException();
        }

        return Task.FromResult<Stream>(new MemoryStream(PosterBytes, writable: false));
    }

    public Task<Stream> DownloadProfileAsync(string profilePath, CancellationToken ct = default)
    {
        if (!Configured)
        {
            throw new TmdbNotConfiguredException();
        }

        return Task.FromResult<Stream>(new MemoryStream(PosterBytes, writable: false));
    }

    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(Configured);
}

/// <summary>Drive probe test double with scriptable free space.</summary>
public sealed class FakeDriveProbe : IDriveProbe
{
    public bool Ready { get; set; } = true;
    public long FreeSpace { get; set; } = 1024L * 1024 * 1024 * 100; // 100 GB

    public IReadOnlyList<UsbDriveOption> GetCandidateDrives(bool includeFixedDrives = false) =>
    [
        new UsbDriveOption("E:\\", "TESTUSB", "Removable", FreeSpace, FreeSpace + 1024)
    ];

    public long GetFreeSpace(string rootPath) => Ready ? FreeSpace : 0;

    public bool IsReady(string rootPath) => Ready;
}
