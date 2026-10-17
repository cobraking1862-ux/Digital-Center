using System.Diagnostics;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Services;

public sealed record ScanProgress(int FilesSeen, int Processed, string CurrentFile);

public sealed record ScanSkippedFile(string Path, string Reason);

public sealed record ScanResult
{
    public int FilesSeen { get; init; }
    public int MovieFilesRegistered { get; init; }
    public int EpisodeFilesRegistered { get; init; }
    public int NewTitlesCreated { get; init; }
    public int NewSeasonsCreated { get; init; }
    public int NewEpisodesCreated { get; init; }
    public int DuplicatePathsSkipped { get; init; }
    public int UnparsedFiles { get; init; }
    public int UnsupportedFiles { get; init; }
    public TimeSpan Duration { get; init; }
    public List<ScanSkippedFile> Skipped { get; init; } = new();
}

/// <summary>
/// Walks the configured library root and registers every media file it finds
/// in the catalog. This scanner never moves, renames or deletes anything; it
/// only reads the filesystem and writes database rows, so running it is safe
/// at any time. Duplicate paths are rejected case-insensitively.
/// </summary>
public sealed class LibraryScanner
{
    public static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".mpg", ".mpeg", ".ts", ".webm"
    };

    private const int SaveBatchSize = 100;

    /// <summary>Mutable accumulator used while a scan runs; converted to a <see cref="ScanResult"/> at the end.</summary>
    private sealed class ScanState
    {
        public int MovieFilesRegistered;
        public int EpisodeFilesRegistered;
        public int NewTitlesCreated;
        public int NewSeasonsCreated;
        public int NewEpisodesCreated;
        public int DuplicatePathsSkipped;
        public int UnsupportedFiles;
        public List<ScanSkippedFile> Skipped { get; } = new();
    }

    private readonly IDbContextFactory<DigitalCenterDbContext> _factory;
    private readonly FilenameParser _parser;
    private readonly IAppLogger? _logger;

    public LibraryScanner(IDbContextFactory<DigitalCenterDbContext> factory, FilenameParser parser, IAppLogger? logger = null)
    {
        _factory = factory;
        _parser = parser;
        _logger = logger;
    }

    public async Task<ScanResult> ScanAsync(string libraryRoot, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
        {
            throw new DirectoryNotFoundException($"Library root does not exist: '{libraryRoot}'.");
        }

        var stopwatch = Stopwatch.StartNew();
        var state = new ScanState();
        var seen = 0;
        var processed = 0;
        var sinceLastSave = 0;

        var rootFullPath = Path.GetFullPath(libraryRoot);

        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        foreach (var filePath in Directory.EnumerateFiles(rootFullPath, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            seen++;
            progress?.Report(new ScanProgress(seen, processed, filePath));

            var extension = Path.GetExtension(filePath);
            if (!MediaExtensions.Contains(extension))
            {
                state.UnsupportedFiles++;
                continue;
            }

            try
            {
                var domainKind = Classify(rootFullPath, filePath);

                if (await RegisterFileAsync(db, filePath, domainKind, state, ct))
                {
                    processed++;
                    sinceLastSave++;
                    if (sinceLastSave >= SaveBatchSize)
                    {
                        await db.SaveChangesAsync(ct);
                        sinceLastSave = 0;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to register '{filePath}'.", ex);
                state.Skipped.Add(new ScanSkippedFile(filePath, $"Error: {ex.Message}"));
            }
        }

        if (sinceLastSave > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);

        stopwatch.Stop();
        var result = new ScanResult
        {
            FilesSeen = seen,
            MovieFilesRegistered = state.MovieFilesRegistered,
            EpisodeFilesRegistered = state.EpisodeFilesRegistered,
            NewTitlesCreated = state.NewTitlesCreated,
            NewSeasonsCreated = state.NewSeasonsCreated,
            NewEpisodesCreated = state.NewEpisodesCreated,
            DuplicatePathsSkipped = state.DuplicatePathsSkipped,
            UnsupportedFiles = state.UnsupportedFiles,
            UnparsedFiles = state.Skipped.Count(s => s.Reason.StartsWith("Unparsed", StringComparison.Ordinal)),
            Duration = stopwatch.Elapsed,
            Skipped = state.Skipped
        };

        _logger?.Info($"Scan of '{libraryRoot}' finished: {result.FilesSeen} files seen, {result.MovieFilesRegistered} movie and {result.EpisodeFilesRegistered} episode files registered.");
        return result;
    }

    /// <summary>Decides movie vs series handling from the top-level folder, falling back to the parse result.</summary>
    private static MediaKind Classify(string root, string filePath)
    {
        var relative = Path.GetRelativePath(root, filePath);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (parts.Length >= 1)
        {
            var top = parts[0];
            if (top.Equals("Series", StringComparison.OrdinalIgnoreCase))
            {
                return MediaKind.Series;
            }
        }

        return MediaKind.Movie;
    }

    private async Task<bool> RegisterFileAsync(
        DigitalCenterDbContext db,
        string filePath,
        MediaKind domainKind,
        ScanState state,
        CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(filePath);

        if (await db.MediaFiles.AnyAsync(f => f.FilePath == fullPath, ct))
        {
            state.DuplicatePathsSkipped++;
            return false;
        }

        var fileName = Path.GetFileName(fullPath);
        var directoryHint = Path.GetDirectoryName(fullPath);
        var parsed = _parser.Parse(fileName, directoryHint);

        MediaTitle title;
        MediaFile file;

        // A file living under the Series tree must never silently become a
        // movie record; without a season/episode marker it stays unparsed.
        if (domainKind == MediaKind.Series && !parsed.IsSeries)
        {
            state.Skipped.Add(new ScanSkippedFile(fullPath, "Unparsed: file in the Series tree without a recognizable season/episode marker."));
            return false;
        }

        if (parsed.IsSeries)
        {
            if (!parsed.Season.HasValue || !parsed.Episode.HasValue)
            {
                state.Skipped.Add(new ScanSkippedFile(fullPath, "Unparsed: series file without season/episode number."));
                return false;
            }

            title = await FindOrCreateTitleAsync(db, MediaKind.Series, parsed.Title, null, state, ct);
            var details = await db.SeriesDetails.FirstOrDefaultAsync(sd => sd.MediaTitleId == title.Id, ct);
            if (details is null)
            {
                details = new SeriesDetails { MediaTitleId = title.Id };
                db.SeriesDetails.Add(details);
            }

            var season = await db.Seasons.FirstOrDefaultAsync(s => s.SeriesDetailsId == title.Id && s.SeasonNumber == parsed.Season.Value, ct);
            if (season is null)
            {
                season = new Season { SeriesDetailsId = title.Id, SeasonNumber = parsed.Season.Value };
                db.Seasons.Add(season);
                await db.SaveChangesAsync(ct); // need season id before creating the episode
                state.NewSeasonsCreated++;
            }

            var episode = await db.Episodes.FirstOrDefaultAsync(e => e.SeasonId == season.Id && e.EpisodeNumber == parsed.Episode.Value, ct);
            if (episode is null)
            {
                episode = new Episode { SeasonId = season.Id, EpisodeNumber = parsed.Episode.Value };
                db.Episodes.Add(episode);
                await db.SaveChangesAsync(ct); // need episode id before creating the file
                state.NewEpisodesCreated++;
            }

            file = new MediaFile { EpisodeId = episode.Id, FilePath = fullPath, FileSize = GetFileSize(fullPath) };
            db.MediaFiles.Add(file);
            state.EpisodeFilesRegistered++;
        }
        else
        {
            title = await FindOrCreateTitleAsync(db, MediaKind.Movie, parsed.Title, parsed.Year, state, ct);
            file = new MediaFile { MovieTitleId = title.Id, FilePath = fullPath, FileSize = GetFileSize(fullPath) };
            db.MediaFiles.Add(file);
            state.MovieFilesRegistered++;
        }

        return true;
    }

    private static long GetFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private async Task<MediaTitle> FindOrCreateTitleAsync(
        DigitalCenterDbContext db,
        MediaKind kind,
        string parsedTitle,
        int? parsedYear,
        ScanState state,
        CancellationToken ct)
    {
        var candidates = await db.MediaTitles
            .Where(t => t.Type == kind && t.Title == parsedTitle)
            .ToListAsync(ct); // Title has COLLATE NOCASE, so '=' is case-insensitive

        MediaTitle? match = null;
        if (candidates.Count == 1)
        {
            var single = candidates[0];
            if (!parsedYear.HasValue || !single.ReleaseYear.HasValue || single.ReleaseYear == parsedYear)
            {
                match = single;
            }
        }
        else if (candidates.Count > 1)
        {
            match = parsedYear.HasValue
                ? candidates.FirstOrDefault(t => t.ReleaseYear == parsedYear.Value)
                : null;
        }

        if (match is not null)
        {
            return match;
        }

        var now = DateTime.UtcNow;
        var title = new MediaTitle
        {
            Type = kind,
            Title = parsedTitle,
            ReleaseYear = parsedYear,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.MediaTitles.Add(title);
        await db.SaveChangesAsync(ct);
        state.NewTitlesCreated++;
        return title;
    }
}
