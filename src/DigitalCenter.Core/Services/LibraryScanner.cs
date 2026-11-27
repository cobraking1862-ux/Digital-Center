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
    public int FilesRelocated { get; init; }
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
/// in the catalog. When an existing catalog file has been moved to a new path
/// on disk, its MediaFile path is updated in place without creating duplicates;
/// files that are genuinely missing on disk are never deleted automatically.
/// </summary>
public sealed class LibraryScanner
{
    public static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".mpg", ".mpeg", ".ts", ".webm"
    };

    private const int SaveBatchSize = 100;

    private sealed class ScanState
    {
        public int MovieFilesRegistered;
        public int EpisodeFilesRegistered;
        public int FilesRelocated;
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

        // Identify existing catalog files whose old path no longer exists on disk
        // so moved files can be updated in place instead of creating duplicate entries.
        var allCatalogFiles = await db.MediaFiles
            .Include(f => f.MovieTitle)
            .Include(f => f.Episode!).ThenInclude(e => e.Season).ThenInclude(s => s.SeriesDetails).ThenInclude(sd => sd.MediaTitle)
            .ToListAsync(ct);

        var missingFiles = allCatalogFiles
            .Where(f => !File.Exists(f.FilePath))
            .ToList();

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
                var (domainKind, folderLanguage, collectionName) = Classify(rootFullPath, filePath);

                if (await RegisterFileAsync(db, filePath, domainKind, folderLanguage, collectionName, missingFiles, state, ct))
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
            FilesRelocated = state.FilesRelocated,
            NewTitlesCreated = state.NewTitlesCreated,
            NewSeasonsCreated = state.NewSeasonsCreated,
            NewEpisodesCreated = state.NewEpisodesCreated,
            DuplicatePathsSkipped = state.DuplicatePathsSkipped,
            UnsupportedFiles = state.UnsupportedFiles,
            UnparsedFiles = state.Skipped.Count(s => s.Reason.StartsWith("Unparsed", StringComparison.Ordinal)),
            Duration = stopwatch.Elapsed,
            Skipped = state.Skipped
        };

        _logger?.Info($"Scan of '{libraryRoot}' finished: {result.FilesSeen} files seen, {result.MovieFilesRegistered} movie and {result.EpisodeFilesRegistered} episode files registered, {result.FilesRelocated} relocated.");
        return result;
    }

    private static (MediaKind Kind, string? FolderLanguage, string? CollectionName) Classify(string root, string filePath)
    {
        var relative = Path.GetRelativePath(root, filePath);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var kind = MediaKind.Movie;
        string? language = null;
        string? collectionName = null;

        var rootFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        if (rootFolderName.Equals("Collections", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
        {
            collectionName = CollectionResolver.NormalizeCollectionName(parts[0]);
        }
        else if (parts.Length >= 1)
        {
            var top = parts[0];
            if (top.Equals("Series", StringComparison.OrdinalIgnoreCase))
            {
                kind = MediaKind.Series;
                if (parts.Length >= 2 && LanguageResolver.IsKnownLanguageName(parts[1]))
                {
                    language = LanguageResolver.FromLanguageCodeOrName(parts[1]);
                }
            }
            else if (top.Equals("Collections", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length >= 3)
                {
                    collectionName = CollectionResolver.NormalizeCollectionName(parts[1]);
                }
            }
            else if (top.Equals("Movies", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length >= 3 && parts[1].Equals("Collections", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Length >= 4)
                    {
                        collectionName = CollectionResolver.NormalizeCollectionName(parts[2]);
                    }
                }
                else if (parts.Length >= 2 && LanguageResolver.IsKnownLanguageName(parts[1]))
                {
                    language = LanguageResolver.FromLanguageCodeOrName(parts[1]);
                }
            }
            else if (LanguageResolver.IsKnownLanguageName(top))
            {
                language = LanguageResolver.FromLanguageCodeOrName(top);
            }
        }

        return (kind, language, collectionName);
    }

    private async Task<bool> RegisterFileAsync(
        DigitalCenterDbContext db,
        string filePath,
        MediaKind domainKind,
        string? folderLanguage,
        string? folderCollection,
        List<MediaFile> missingFiles,
        ScanState state,
        CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(filePath);

        if (db.MediaFiles.Local.Any(f => string.Equals(f.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)) ||
            await db.MediaFiles.AnyAsync(f => f.FilePath == fullPath, ct))
        {
            state.DuplicatePathsSkipped++;
            return false;
        }

        var fileName = Path.GetFileName(fullPath);
        var fileSize = GetFileSize(fullPath);
        var directoryHint = Path.GetDirectoryName(fullPath);
        var parsed = _parser.Parse(fileName, directoryHint);

        // 1. Check if this file is a moved copy of an existing catalog file whose old path is missing.
        var relocated = FindRelocatedCandidate(missingFiles, fileName, fileSize, parsed);
        if (relocated is not null)
        {
            var oldPath = relocated.FilePath;
            relocated.FilePath = fullPath;
            relocated.FileSize = fileSize;
            missingFiles.Remove(relocated);

            if (relocated.MovieTitle is not null)
            {
                var resolvedCollection = folderCollection ?? CollectionResolver.Resolve(null, parsed.Title, fileName);
                await EnsureCollectionCategoryAsync(db, relocated.MovieTitle.Id, resolvedCollection, ct);
            }

            state.FilesRelocated++;
            _logger?.Info($"Relocated catalog file: '{oldPath}' -> '{fullPath}'.");
            return true;
        }

        if (domainKind == MediaKind.Series && !parsed.IsSeries)
        {
            state.Skipped.Add(new ScanSkippedFile(fullPath, "Unparsed: file in the Series tree without a recognizable season/episode marker."));
            return false;
        }

        var detectedLanguage = folderLanguage ?? LanguageResolver.FromFileName(fileName);

        MediaTitle title;
        MediaFile file;

        if (parsed.IsSeries)
        {
            if (!parsed.Season.HasValue || !parsed.Episode.HasValue)
            {
                state.Skipped.Add(new ScanSkippedFile(fullPath, "Unparsed: series file without season/episode number."));
                return false;
            }

            title = await FindOrCreateTitleAsync(db, MediaKind.Series, parsed.Title, null, detectedLanguage, state, ct);
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
                await db.SaveChangesAsync(ct);
                state.NewSeasonsCreated++;
            }

            var episode = await db.Episodes.FirstOrDefaultAsync(e => e.SeasonId == season.Id && e.EpisodeNumber == parsed.Episode.Value, ct);
            if (episode is null)
            {
                episode = new Episode { SeasonId = season.Id, EpisodeNumber = parsed.Episode.Value };
                db.Episodes.Add(episode);
                await db.SaveChangesAsync(ct);
                state.NewEpisodesCreated++;
            }

            file = new MediaFile { EpisodeId = episode.Id, FilePath = fullPath, FileSize = fileSize };
            db.MediaFiles.Add(file);
            state.EpisodeFilesRegistered++;
        }
        else
        {
            title = await FindOrCreateTitleAsync(db, MediaKind.Movie, parsed.Title, parsed.Year, detectedLanguage, state, ct);
            var resolvedCollection = folderCollection ?? CollectionResolver.Resolve(null, parsed.Title, fileName);
            await EnsureCollectionCategoryAsync(db, title.Id, resolvedCollection, ct);

            file = new MediaFile { MovieTitleId = title.Id, FilePath = fullPath, FileSize = fileSize };
            db.MediaFiles.Add(file);
            state.MovieFilesRegistered++;
        }

        return true;
    }

    private static MediaFile? FindRelocatedCandidate(
        List<MediaFile> missingFiles,
        string fileName,
        long fileSize,
        ParsedFileName parsed)
    {
        if (missingFiles.Count == 0)
        {
            return null;
        }

        // Exact filename + file size match (most reliable for moved files).
        if (fileSize > 0)
        {
            var exact = missingFiles.FirstOrDefault(m =>
                m.FileSize == fileSize &&
                string.Equals(Path.GetFileName(m.FilePath), fileName, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        // Same filename match when size is 0 or unchanged name.
        var byName = missingFiles.FirstOrDefault(m =>
            string.Equals(Path.GetFileName(m.FilePath), fileName, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        // Same movie title (+ year) or episode with exact file size (handles move + rename).
        if (fileSize > 0 && !string.IsNullOrWhiteSpace(parsed.Title))
        {
            if (parsed.IsSeries && parsed.Season.HasValue && parsed.Episode.HasValue)
            {
                return missingFiles.FirstOrDefault(m =>
                    m.FileSize == fileSize &&
                    m.Episode?.EpisodeNumber == parsed.Episode.Value &&
                    m.Episode.Season?.SeasonNumber == parsed.Season.Value &&
                    string.Equals(m.Episode.Season.SeriesDetails?.MediaTitle?.Title, parsed.Title, StringComparison.OrdinalIgnoreCase));
            }

            if (!parsed.IsSeries)
            {
                return missingFiles.FirstOrDefault(m =>
                    m.FileSize == fileSize &&
                    m.MovieTitle is not null &&
                    string.Equals(m.MovieTitle.Title, parsed.Title, StringComparison.OrdinalIgnoreCase) &&
                    (!parsed.Year.HasValue || !m.MovieTitle.ReleaseYear.HasValue || m.MovieTitle.ReleaseYear == parsed.Year));
            }
        }

        return null;
    }

    private static async Task EnsureCollectionCategoryAsync(
        DigitalCenterDbContext db,
        int titleId,
        string? resolvedCollection,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(resolvedCollection))
        {
            return;
        }

        var catName = $"Collection: {resolvedCollection}";
        var category = db.Categories.Local.FirstOrDefault(c => c.Type == MediaKind.Movie && string.Equals(c.Name, catName, StringComparison.OrdinalIgnoreCase))
                       ?? await db.Categories.FirstOrDefaultAsync(c => c.Type == MediaKind.Movie && c.Name == catName, ct);
        if (category is null)
        {
            category = new Category { Type = MediaKind.Movie, Name = catName };
            db.Categories.Add(category);
            await db.SaveChangesAsync(ct);
        }

        var hasCat = db.TitleCategories.Local.Any(tc => tc.MediaTitleId == titleId && tc.CategoryId == category.Id)
                     || await db.TitleCategories.AnyAsync(tc => tc.MediaTitleId == titleId && tc.CategoryId == category.Id, ct);
        if (!hasCat)
        {
            db.TitleCategories.Add(new TitleCategory { MediaTitleId = titleId, CategoryId = category.Id, Category = category });
        }
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
        string? detectedLanguage,
        ScanState state,
        CancellationToken ct)
    {
        var candidates = await db.MediaTitles
            .Where(t => t.Type == kind && t.Title == parsedTitle)
            .ToListAsync(ct);

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
            if (string.IsNullOrWhiteSpace(match.OriginalLanguage) && !string.IsNullOrWhiteSpace(detectedLanguage))
            {
                match.OriginalLanguage = detectedLanguage;
            }
            return match;
        }

        var now = DateTime.UtcNow;
        var title = new MediaTitle
        {
            Type = kind,
            Title = parsedTitle,
            ReleaseYear = parsedYear,
            OriginalLanguage = detectedLanguage,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.MediaTitles.Add(title);
        await db.SaveChangesAsync(ct);
        state.NewTitlesCreated++;
        return title;
    }
}
