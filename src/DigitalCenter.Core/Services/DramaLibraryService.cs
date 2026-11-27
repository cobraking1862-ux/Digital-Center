using System.Diagnostics;
using System.Globalization;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Tmdb;
using Microsoft.Data.Sqlite;

namespace DigitalCenter.Core.Services;

/// <summary>
/// Manages the Drama Catalog (mirroring the Movies catalog), TMDb TV metadata &amp;
/// artwork enrichment, season-separated episode tracking, Watch Folder ingestion
/// (clean-renaming and moving episodes into &lt;DramaRoot&gt;\&lt;Drama Name&gt;\),
/// moved/missing episode handling, re-analysis, persistent Drama Watch Folder
/// import history, and the USB Cart's persistent Dramas bucket.
/// </summary>
public sealed class DramaLibraryService
{
    public event Action? LibraryChanged;
    public event Action? DramaCartChanged;
    public event Action? WatchHistoryChanged;

    private readonly AppPaths _paths;
    private readonly ITmdbMetadataProvider _tmdb;
    private readonly PosterCache _posters;
    private readonly SettingsService _settings;
    private readonly WatchFolderIssueService? _issues;
    private readonly IAppLogger? _logger;

    public DramaLibraryService(
        AppPaths paths,
        ITmdbMetadataProvider tmdb,
        PosterCache posters,
        SettingsService settings,
        IAppLogger? logger = null,
        WatchFolderIssueService? issues = null)
    {
        _paths = paths;
        _tmdb = tmdb;
        _posters = posters;
        _settings = settings;
        _logger = logger;
        _issues = issues;
    }

    private string ConnectionString => $"Data Source={_paths.DatabaseFile};Default Timeout=30;Pooling=True";

    public async Task<DramaScanResult> ScanDramaRootAsync(
        string dramaRoot,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(dramaRoot) || !Directory.Exists(dramaRoot))
        {
            throw new DirectoryNotFoundException($"Drama root folder does not exist: '{dramaRoot}'.");
        }

        var rootFull = Path.GetFullPath(dramaRoot.Trim());
        var dramasFound = 0;
        var dramasCreated = 0;
        var dramasMatchedTmdb = 0;
        var episodesRegistered = 0;
        var episodesUpdated = 0;
        var episodesRelocated = 0;

        var enumOpts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        foreach (var subDir in Directory.EnumerateDirectories(rootFull))
        {
            ct.ThrowIfCancellationRequested();
            var folderName = Path.GetFileName(subDir);
            if (string.IsNullOrWhiteSpace(folderName) || folderName.StartsWith('.'))
            {
                continue;
            }

            var videoFiles = Directory.EnumerateFiles(subDir, "*", enumOpts)
                .Where(DramaFilenameParser.IsVideoFile)
                .ToList();

            if (videoFiles.Count == 0)
            {
                continue;
            }

            dramasFound++;
            progress?.Report($"Scanning drama: {folderName} ({videoFiles.Count} episode(s))…");

            var sampleParsed = DramaFilenameParser.Parse(Path.GetFileName(videoFiles[0]), folderName);
            var localPoster = DramaFilenameParser.FindPosterInFolder(subDir);

            var (dramaId, created, hasTmdb) = await UpsertDramaAsync(
                conn,
                title: folderName,
                folderPath: Path.GetFullPath(subDir),
                channel: sampleParsed.DetectedChannel,
                localPosterPath: localPoster,
                ct: ct);

            if (created) dramasCreated++;

            foreach (var file in videoFiles)
            {
                ct.ThrowIfCancellationRequested();
                var parentName = Path.GetFileName(Path.GetDirectoryName(file));
                var seasonHint = DramaFilenameParser.TryParseSeasonFolder(parentName);
                var parsed = DramaFilenameParser.Parse(Path.GetFileName(file), folderName, seasonHint);
                var res = await UpsertEpisodeAsync(conn, dramaId, file, parsed, ct);
                if (res == 1) episodesRegistered++;
                else if (res == 2) episodesUpdated++;
                else if (res == 3) episodesRelocated++;
            }

            if ((created || !hasTmdb) && _tmdb.IsConfigured)
            {
                progress?.Report($"Fetching TMDb details for '{folderName}'…");
                if (await EnrichDramaFromTmdbAsync(dramaId, folderName, ct))
                {
                    dramasMatchedTmdb++;
                }
            }
        }

        sw.Stop();
        _logger?.Info($"Drama scan finished: {dramasFound} drama(s), {dramasMatchedTmdb} TMDb-matched, {episodesRegistered} new, {episodesRelocated} relocated.");
        LibraryChanged?.Invoke();

        return new DramaScanResult(dramasFound, dramasCreated, dramasMatchedTmdb, episodesRegistered, episodesUpdated, episodesRelocated, sw.Elapsed);
    }

    public async Task<DramaReanalyzeResult> ReanalyzeDramasAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var dramasAnalyzed = 0;
        var episodesReparsed = 0;
        var episodesRelocated = 0;
        var missingEpisodesFound = 0;
        var tmdbRefreshed = 0;

        var dramas = await GetDramasAsync(ct: ct);
        var enumOpts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        foreach (var drama in dramas)
        {
            ct.ThrowIfCancellationRequested();
            dramasAnalyzed++;
            progress?.Report($"Re-analyzing '{drama.Title}' ({dramasAnalyzed}/{dramas.Count})…");

            if (Directory.Exists(drama.FolderPath))
            {
                var folderName = Path.GetFileName(drama.FolderPath);
                var diskFiles = Directory.EnumerateFiles(drama.FolderPath, "*", enumOpts)
                    .Where(DramaFilenameParser.IsVideoFile)
                    .ToList();

                foreach (var file in diskFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var parentName = Path.GetFileName(Path.GetDirectoryName(file));
                    var seasonHint = DramaFilenameParser.TryParseSeasonFolder(parentName);
                    var parsed = DramaFilenameParser.Parse(Path.GetFileName(file), folderName, seasonHint);
                    var res = await UpsertEpisodeAsync(conn, drama.Id, file, parsed, ct, forceReparse: true);
                    if (res == 3) episodesRelocated++;
                    else if (res == 2 || res == 1) episodesReparsed++;
                }
            }

            var currentEps = await GetEpisodesAsync(drama.Id, ct);
            missingEpisodesFound += currentEps.Count(e => !e.ExistsOnDisk);

            if (!drama.TmdbId.HasValue && _tmdb.IsConfigured)
            {
                if (await EnrichDramaFromTmdbAsync(drama.Id, drama.Title, ct))
                {
                    tmdbRefreshed++;
                }
            }
        }

        sw.Stop();
        _logger?.Info($"Drama re-analysis finished: {dramasAnalyzed} drama(s), {episodesReparsed} re-parsed, {episodesRelocated} relocated, {missingEpisodesFound} missing.");
        LibraryChanged?.Invoke();

        return new DramaReanalyzeResult(dramasAnalyzed, episodesReparsed, episodesRelocated, missingEpisodesFound, tmdbRefreshed, sw.Elapsed);
    }

    public async Task<IReadOnlyList<DramaEpisode>> GetMissingDramaEpisodesAsync(CancellationToken ct = default)
    {
        var missing = new List<DramaEpisode>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.Id, e.DramaId, d.Title, d.PosterPath, e.SeasonNumber, e.EpisodeNumber, e.Title, e.FileName,
                   e.FilePath, e.FileSize, e.YouTubeVideoId, e.CopyCount, e.LastCopiedAt, e.AddedAt,
                   CASE WHEN c.EpisodeId IS NOT NULL THEN 1 ELSE 0 END
            FROM DramaEpisodes e
            INNER JOIN Dramas d ON d.Id = e.DramaId
            LEFT JOIN DramaCartItems c ON c.EpisodeId = e.Id
            ORDER BY d.Title COLLATE NOCASE ASC, e.SeasonNumber ASC, e.EpisodeNumber ASC;
            """;

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var ep = ReadEpisode(r);
            ep.ExistsOnDisk = File.Exists(ep.FilePath);
            if (!ep.ExistsOnDisk)
            {
                missing.Add(ep);
            }
        }

        return missing;
    }

    public async Task<int> RemoveMissingDramaEpisodesAsync(CancellationToken ct = default)
    {
        var missing = await GetMissingDramaEpisodesAsync(ct);
        if (missing.Count == 0)
        {
            return 0;
        }

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        var removed = 0;
        foreach (var ep in missing)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM DramaCartItems WHERE EpisodeId = @id;
                DELETE FROM DramaEpisodes WHERE Id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", ep.Id);
            await cmd.ExecuteNonQueryAsync(ct);
            removed++;
        }

        if (removed > 0)
        {
            _logger?.Info($"Removed {removed} missing drama episode entry(ies) from catalog.");
            LibraryChanged?.Invoke();
            DramaCartChanged?.Invoke();
        }

        return removed;
    }

    public async Task<bool> IngestWatchFileAsync(string sourceFilePath, string watchRoot, CancellationToken ct = default)
    {
        if (!File.Exists(sourceFilePath) || !DramaFilenameParser.IsVideoFile(sourceFilePath))
        {
            return false;
        }

        var fileFull = Path.GetFullPath(sourceFilePath);
        var settings = _settings.Load();
        if (string.IsNullOrWhiteSpace(settings.DramaRoot))
        {
            const string reason = "Drama Root is not configured in Settings.";
            _logger?.Warn($"Drama Watch Folder skipped '{Path.GetFileName(fileFull)}' because {reason}");
            if (_issues is not null)
            {
                await _issues.RecordIssueAsync(WatchFolderIssueTypes.Drama, WatchFolderIssueCategories.Failed, fileFull, reason, ct);
            }
            return false;
        }

        var watchFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(watchRoot));
        var relPath = Path.GetRelativePath(watchFull, fileFull);
        var parts = relPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        string? dramaFolderHint = parts.Length >= 2 ? parts[0] : null;
        int? seasonHint = parts.Length >= 3 ? DramaFilenameParser.TryParseSeasonFolder(parts[1]) : null;

        var parsed = DramaFilenameParser.Parse(Path.GetFileName(fileFull), dramaFolderHint, seasonHint);
        var dramaName = !string.IsNullOrWhiteSpace(dramaFolderHint)
            ? PathPlanner.SanitizeName(dramaFolderHint)
            : PathPlanner.SanitizeName(parsed.InferredDramaTitle);

        if (string.IsNullOrWhiteSpace(dramaName))
        {
            const string reason = "Could not infer drama name from subfolder or filename.";
            if (_issues is not null)
            {
                await _issues.RecordIssueAsync(WatchFolderIssueTypes.Drama, WatchFolderIssueCategories.LowConfidence, fileFull, reason, ct);
            }
            return false;
        }

        long fileSize = 0;
        try { fileSize = new FileInfo(fileFull).Length; } catch (Exception) { }

        var targetDramaDir = Path.Combine(Path.GetFullPath(settings.DramaRoot.Trim()), dramaName);
        Directory.CreateDirectory(targetDramaDir);

        var desiredFileName = settings.CleanFilenames
            ? DramaFilenameParser.BuildCleanEpisodeFileName(dramaName, parsed, fileFull)
            : Path.GetFileName(fileFull);

        var targetFileName = PathPlanner.ResolveFileNameConflict(targetDramaDir, desiredFileName);
        var targetFilePath = Path.Combine(targetDramaDir, targetFileName);

        try
        {
            MoveFileAcrossVolumesSafe(fileFull, targetFilePath);
        }
        catch (IOException ex)
        {
            if (_issues is not null)
            {
                await _issues.RecordIssueAsync(
                    WatchFolderIssueTypes.Drama,
                    WatchFolderIssueCategories.Interrupted,
                    fileFull,
                    $"Move interrupted or file locked: {ex.Message}",
                    ct);
            }
            throw;
        }
        catch (Exception ex)
        {
            if (_issues is not null)
            {
                await _issues.RecordIssueAsync(
                    WatchFolderIssueTypes.Drama,
                    WatchFolderIssueCategories.Failed,
                    fileFull,
                    ex.Message,
                    ct);
            }
            throw;
        }

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        var localPoster = DramaFilenameParser.FindPosterInFolder(targetDramaDir);
        var (dramaId, created, hasTmdb) = await UpsertDramaAsync(
            conn,
            title: dramaName,
            folderPath: targetDramaDir,
            channel: parsed.DetectedChannel,
            localPosterPath: localPoster,
            ct: ct);

        await UpsertEpisodeAsync(conn, dramaId, targetFilePath, parsed, ct);

        if ((created || !hasTmdb) && _tmdb.IsConfigured)
        {
            await EnrichDramaFromTmdbAsync(dramaId, dramaName, ct);
        }

        await RecordWatchHistoryAsync(
            dramaId: dramaId,
            dramaTitle: dramaName,
            seasonNumber: parsed.SeasonNumber,
            episodeNumber: parsed.EpisodeNumber,
            sourceFileName: Path.GetFileName(fileFull),
            sourcePath: fileFull,
            targetPath: targetFilePath,
            fileSize: fileSize,
            status: "Imported",
            ct: ct);

        if (_issues is not null)
        {
            await _issues.ResolveIssueAsync(WatchFolderIssueTypes.Drama, fileFull, ct);
        }

        _logger?.Info($"Drama Watch Folder moved '{Path.GetFileName(fileFull)}' -> '{targetFilePath}'.");
        LibraryChanged?.Invoke();
        return true;
    }

    // --------------------------------------------------------- Drama Watch History

    public async Task RecordWatchHistoryAsync(
        int? dramaId,
        string dramaTitle,
        int seasonNumber,
        int episodeNumber,
        string sourceFileName,
        string sourcePath,
        string targetPath,
        long fileSize,
        string status = "Imported",
        CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DramaWatchHistory
                (DramaId, DramaTitle, SeasonNumber, EpisodeNumber, SourceFileName, SourcePath, TargetPath, FileSize, Status, Timestamp)
            VALUES
                (@did, @title, @season, @ep, @fname, @src, @dst, @size, @status, @now);
            """;
        cmd.Parameters.AddWithValue("@did", dramaId.HasValue ? dramaId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@title", dramaTitle.Trim());
        cmd.Parameters.AddWithValue("@season", seasonNumber <= 0 ? 1 : seasonNumber);
        cmd.Parameters.AddWithValue("@ep", Math.Max(0, episodeNumber));
        cmd.Parameters.AddWithValue("@fname", sourceFileName);
        cmd.Parameters.AddWithValue("@src", sourcePath);
        cmd.Parameters.AddWithValue("@dst", targetPath);
        cmd.Parameters.AddWithValue("@size", Math.Max(0, fileSize));
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);

        WatchHistoryChanged?.Invoke();
    }

    public async Task<IReadOnlyList<DramaWatchHistoryEntry>> GetWatchHistoryAsync(int maxRows = 200, CancellationToken ct = default)
    {
        var list = new List<DramaWatchHistoryEntry>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, DramaId, DramaTitle, SeasonNumber, EpisodeNumber, SourceFileName, SourcePath, TargetPath, FileSize, Status, Timestamp
            FROM DramaWatchHistory
            ORDER BY Timestamp DESC, Id DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(maxRows, 1, 1000));

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new DramaWatchHistoryEntry
            {
                Id = r.GetInt32(0),
                DramaId = r.IsDBNull(1) ? null : r.GetInt32(1),
                DramaTitle = r.GetString(2),
                SeasonNumber = r.GetInt32(3),
                EpisodeNumber = r.GetInt32(4),
                SourceFileName = r.GetString(5),
                SourcePath = r.GetString(6),
                TargetPath = r.GetString(7),
                FileSize = r.GetInt64(8),
                Status = r.GetString(9),
                Timestamp = DateTime.TryParse(r.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
                    ? ts
                    : DateTime.UtcNow
            });
        }

        return list;
    }

    public async Task<int> GetWatchHistoryCountAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DramaWatchHistory;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<int> ClearWatchHistoryAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM DramaWatchHistory;";
        var removed = await cmd.ExecuteNonQueryAsync(ct);
        if (removed > 0)
        {
            _logger?.Info($"Cleared {removed} Drama Watch Folder history item(s).");
            WatchHistoryChanged?.Invoke();
        }

        return removed;
    }

    public async Task<bool> EnrichDramaFromTmdbAsync(int dramaId, string? searchTitleOverride = null, CancellationToken ct = default)
    {
        if (!_tmdb.IsConfigured)
        {
            return false;
        }

        var drama = await GetDramaByIdAsync(dramaId, ct);
        if (drama is null)
        {
            return false;
        }

        var query = string.IsNullOrWhiteSpace(searchTitleOverride) ? drama.Title : searchTitleOverride.Trim();
        try
        {
            long resolvedTmdbId;
            if (drama.TmdbId is > 0)
            {
                // Use the saved/pinned TMDb TV ID directly instead of fuzzy-searching by title
                resolvedTmdbId = drama.TmdbId.Value;
            }
            else
            {
                var candidates = await _tmdb.SearchSeriesAsync(query, drama.Year, ct);
                if (candidates.Count == 0)
                {
                    return false;
                }

                var minConfidence = Math.Clamp(_settings.Load().MinimumMatchConfidence, 0.30, 0.95);
                var best = candidates
                    .Select(c => new
                    {
                        Candidate = c,
                        Score = Math.Max(
                            TitleSimilarity.Score(query, c.Title),
                            TitleSimilarity.Score(query, c.OriginalTitle))
                    })
                    .OrderByDescending(x => x.Score)
                    .First();

                if (best.Score < Math.Min(0.45, minConfidence))
                {
                    return false;
                }

                resolvedTmdbId = best.Candidate.Id;
            }

            var meta = await _tmdb.GetSeriesAsync(resolvedTmdbId, ct);
            if (meta is null)
            {
                return false;
            }

            var settings = _settings.Load();
            string? cachedPoster = drama.PosterPath;
            string? cachedBackdrop = drama.BackdropPath;

            if (settings.DownloadPosters && !string.IsNullOrWhiteSpace(meta.PosterPath))
            {
                try
                {
                    await using var stream = await _tmdb.DownloadPosterAsync(meta.PosterPath, ct);
                    cachedPoster = await SaveDramaArtworkAsync($"drama-{dramaId}.jpg", stream, ct);
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"Drama poster download failed for '{drama.Title}': {ex.Message}");
                }
            }

            if (settings.DownloadBackdrops && !string.IsNullOrWhiteSpace(meta.BackdropPath))
            {
                try
                {
                    await using var stream = await _tmdb.DownloadBackdropAsync(meta.BackdropPath, ct);
                    cachedBackdrop = await SaveDramaArtworkAsync($"drama-{dramaId}-backdrop.jpg", stream, ct);
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"Drama backdrop download failed for '{drama.Title}': {ex.Message}");
                }
            }

            var mappedStatus = MapAiringStatus(meta.AiringStatus, drama.Status);
            var genresText = meta.Genres.Count > 0 ? string.Join(", ", meta.Genres) : drama.Genres;
            var expectedEps = meta.Seasons
                .Where(s => s.SeasonNumber > 0)
                .Sum(s => s.Episodes.Count);

            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct);

            await using (var upd = conn.CreateCommand())
            {
                upd.CommandText = """
                    UPDATE Dramas
                    SET Title = CASE WHEN TitleLocked = 1 THEN Title ELSE @title END,
                        OriginalTitle = @origTitle,
                        Year = COALESCE(@year, Year),
                        Overview = COALESCE(@overview, Overview),
                        Language = COALESCE(@lang, Language),
                        Status = CASE WHEN StatusLocked = 1 THEN Status ELSE @status END,
                        ExpectedEpisodeCount = COALESCE(@expectedEps, ExpectedEpisodeCount),
                        TmdbId = @tmdbId,
                        ImdbId = COALESCE(@imdbId, ImdbId),
                        VoteAverage = COALESCE(@vote, VoteAverage),
                        PosterPath = COALESCE(@poster, PosterPath),
                        BackdropPath = COALESCE(@backdrop, BackdropPath),
                        Genres = COALESCE(@genres, Genres)
                    WHERE Id = @id;
                    """;
                upd.Parameters.AddWithValue("@title", string.IsNullOrWhiteSpace(meta.Title) ? drama.Title : meta.Title.Trim());
                upd.Parameters.AddWithValue("@origTitle", (object?)meta.OriginalTitle ?? DBNull.Value);
                upd.Parameters.AddWithValue("@year", meta.Year.HasValue ? meta.Year.Value : DBNull.Value);
                upd.Parameters.AddWithValue("@overview", string.IsNullOrWhiteSpace(meta.Synopsis) ? DBNull.Value : meta.Synopsis.Trim());
                upd.Parameters.AddWithValue("@lang", string.IsNullOrWhiteSpace(meta.OriginalLanguage) ? DBNull.Value : meta.OriginalLanguage.Trim());
                upd.Parameters.AddWithValue("@status", mappedStatus);
                upd.Parameters.AddWithValue("@expectedEps", expectedEps > 0 ? expectedEps : DBNull.Value);
                upd.Parameters.AddWithValue("@tmdbId", resolvedTmdbId);
                upd.Parameters.AddWithValue("@imdbId", string.IsNullOrWhiteSpace(meta.ImdbId) ? DBNull.Value : meta.ImdbId.Trim());
                upd.Parameters.AddWithValue("@vote", meta.VoteAverage.HasValue ? meta.VoteAverage.Value : DBNull.Value);
                upd.Parameters.AddWithValue("@poster", string.IsNullOrWhiteSpace(cachedPoster) ? DBNull.Value : cachedPoster);
                upd.Parameters.AddWithValue("@backdrop", string.IsNullOrWhiteSpace(cachedBackdrop) ? DBNull.Value : cachedBackdrop);
                upd.Parameters.AddWithValue("@genres", string.IsNullOrWhiteSpace(genresText) ? DBNull.Value : genresText);
                upd.Parameters.AddWithValue("@id", dramaId);
                await upd.ExecuteNonQueryAsync(ct);
            }

            var actors = meta.People
                .Where(p => string.Equals(p.Role, "Actor", StringComparison.OrdinalIgnoreCase))
                .Take(16)
                .ToList();

            if (actors.Count > 0)
            {
                await using (var del = conn.CreateCommand())
                {
                    del.CommandText = "DELETE FROM DramaCast WHERE DramaId = @id;";
                    del.Parameters.AddWithValue("@id", dramaId);
                    await del.ExecuteNonQueryAsync(ct);
                }

                for (var i = 0; i < actors.Count; i++)
                {
                    var actor = actors[i];
                    string? profileFile = null;
                    if (settings.DownloadCastProfiles && !string.IsNullOrWhiteSpace(actor.ProfilePath) && actor.TmdbPersonId.HasValue)
                    {
                        try
                        {
                            var expected = $"p{actor.TmdbPersonId.Value}.jpg";
                            if (_posters.Exists(expected))
                            {
                                profileFile = expected;
                            }
                            else
                            {
                                await using var pStream = await _tmdb.DownloadProfileAsync(actor.ProfilePath, ct);
                                profileFile = await _posters.SaveProfileAsync(actor.TmdbPersonId.Value, pStream, ct);
                            }
                        }
                        catch (Exception) { }
                    }

                    await using var ins = conn.CreateCommand();
                    ins.CommandText = """
                        INSERT INTO DramaCast (DramaId, Name, Character, ProfilePath, SortOrder)
                        VALUES (@did, @name, @char, @profile, @sort);
                        """;
                    ins.Parameters.AddWithValue("@did", dramaId);
                    ins.Parameters.AddWithValue("@name", actor.Name);
                    ins.Parameters.AddWithValue("@char", (object?)actor.Character ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@profile", (object?)profileFile ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@sort", i);
                    await ins.ExecuteNonQueryAsync(ct);
                }
            }

            LibraryChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"TMDb TV lookup failed for drama '{query}': {ex.Message}");
            return false;
        }
    }

    public async Task<IReadOnlyList<DramaSeries>> GetDramasAsync(
        string? search = null,
        string? status = null,
        int sortIndex = 0,
        CancellationToken ct = default)
    {
        var dramas = new List<DramaSeries>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        var orderBy = sortIndex switch
        {
            1 => "Title COLLATE NOCASE DESC",
            2 => "CreatedAt DESC, Id DESC",
            _ => "Title COLLATE NOCASE ASC"
        };

        await using (var cmd = conn.CreateCommand())
        {
            var conditions = new List<string>();
            if (!string.IsNullOrWhiteSpace(search))
            {
                conditions.Add("(Title LIKE @q OR OriginalTitle LIKE @q OR Channel LIKE @q OR Genres LIKE @q OR Overview LIKE @q)");
                cmd.Parameters.AddWithValue("@q", $"%{search.Trim()}%");
            }

            if (!string.IsNullOrWhiteSpace(status) && !status.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                conditions.Add("Status = @st");
                cmd.Parameters.AddWithValue("@st", status.Trim());
            }

            var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
            cmd.CommandText = $"""
                SELECT Id, Title, OriginalTitle, FolderPath, Year, Overview, Language, Channel,
                       Status, StatusLocked, TitleLocked, ExpectedEpisodeCount, TmdbId, ImdbId, VoteAverage,
                       PosterPath, BackdropPath, Genres, LastScannedAt, CreatedAt
                FROM Dramas
                {where}
                ORDER BY {orderBy};
                """;

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                dramas.Add(ReadDrama(r));
            }
        }

        if (dramas.Count == 0)
        {
            return dramas;
        }

        var stats = new Dictionary<int, List<(int Season, long Size, string FilePath)>>();
        await using (var epCmd = conn.CreateCommand())
        {
            epCmd.CommandText = "SELECT DramaId, SeasonNumber, FileSize, FilePath FROM DramaEpisodes;";
            await using var r = await epCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var did = r.GetInt32(0);
                if (!stats.TryGetValue(did, out var list))
                {
                    list = [];
                    stats[did] = list;
                }

                list.Add((r.GetInt32(1), r.GetInt64(2), r.GetString(3)));
            }
        }

        foreach (var d in dramas)
        {
            if (stats.TryGetValue(d.Id, out var eps))
            {
                d.EpisodeCount = eps.Count;
                d.AvailableEpisodeCount = eps.Count(e => File.Exists(e.FilePath));
                d.SeasonCount = eps.Select(e => e.Season).Distinct().Count();
                d.TotalSizeBytes = eps.Sum(e => e.Size);
            }
        }

        return dramas;
    }

    public async Task<DramaSeries?> GetDramaByIdAsync(int dramaId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Title, OriginalTitle, FolderPath, Year, Overview, Language, Channel,
                   Status, StatusLocked, TitleLocked, ExpectedEpisodeCount, TmdbId, ImdbId, VoteAverage,
                   PosterPath, BackdropPath, Genres, LastScannedAt, CreatedAt
            FROM Dramas
            WHERE Id = @id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@id", dramaId);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            return null;
        }

        var drama = ReadDrama(r);
        await r.CloseAsync();

        var eps = await GetEpisodesAsync(dramaId, ct);
        drama.EpisodeCount = eps.Count;
        drama.AvailableEpisodeCount = eps.Count(e => e.ExistsOnDisk);
        drama.SeasonCount = eps.Select(e => e.SeasonNumber).Distinct().Count();
        drama.TotalSizeBytes = eps.Sum(e => e.FileSize);
        return drama;
    }

    public async Task<IReadOnlyList<DramaEpisode>> GetEpisodesAsync(int dramaId, CancellationToken ct = default)
    {
        var list = new List<DramaEpisode>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.Id, e.DramaId, d.Title, d.PosterPath, e.SeasonNumber, e.EpisodeNumber, e.Title, e.FileName,
                   e.FilePath, e.FileSize, e.YouTubeVideoId, e.CopyCount, e.LastCopiedAt, e.AddedAt,
                   CASE WHEN c.EpisodeId IS NOT NULL THEN 1 ELSE 0 END
            FROM DramaEpisodes e
            INNER JOIN Dramas d ON d.Id = e.DramaId
            LEFT JOIN DramaCartItems c ON c.EpisodeId = e.Id
            WHERE e.DramaId = @dramaId
            ORDER BY e.SeasonNumber ASC,
                     CASE WHEN e.EpisodeNumber > 0 THEN 0 ELSE 1 END,
                     e.EpisodeNumber ASC,
                     e.FileName COLLATE NOCASE ASC;
            """;
        cmd.Parameters.AddWithValue("@dramaId", dramaId);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var ep = ReadEpisode(r);
            ep.ExistsOnDisk = File.Exists(ep.FilePath);
            list.Add(ep);
        }

        return list;
    }

    public async Task<IReadOnlyList<DramaCastMember>> GetCastAsync(int dramaId, CancellationToken ct = default)
    {
        var list = new List<DramaCastMember>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, DramaId, Name, Character, ProfilePath, SortOrder
            FROM DramaCast
            WHERE DramaId = @id
            ORDER BY SortOrder ASC, Id ASC;
            """;
        cmd.Parameters.AddWithValue("@id", dramaId);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new DramaCastMember
            {
                Id = r.GetInt32(0),
                DramaId = r.GetInt32(1),
                Name = r.GetString(2),
                Character = r.IsDBNull(3) ? null : r.GetString(3),
                ProfilePath = r.IsDBNull(4) ? null : r.GetString(4),
                SortOrder = r.GetInt32(5)
            });
        }

        return list;
    }

    public async Task UpdateDramaMetadataAsync(
        int dramaId,
        string title,
        string status,
        long? tmdbId,
        string? overview,
        string? customPosterFilePath,
        CancellationToken ct = default)
    {
        string? storedPoster = null;
        if (!string.IsNullOrWhiteSpace(customPosterFilePath) && File.Exists(customPosterFilePath))
        {
            await using var fs = File.OpenRead(customPosterFilePath);
            storedPoster = await SaveDramaArtworkAsync($"drama-{dramaId}.jpg", fs, ct);
        }

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        // Saving metadata locks both manual Title and Status against TMDb refresh overwrites,
        // and pins the TMDb TV ID so Refresh TMDb fetches that exact show.
        cmd.CommandText = """
            UPDATE Dramas
            SET Title = @title,
                TitleLocked = 1,
                Status = @status,
                StatusLocked = 1,
                TmdbId = @tmdbId,
                Overview = @overview,
                PosterPath = COALESCE(@poster, PosterPath)
            WHERE Id = @id;
            """;
        cmd.Parameters.AddWithValue("@title", title.Trim());
        cmd.Parameters.AddWithValue("@status", string.IsNullOrWhiteSpace(status) ? DramaStatuses.Ongoing : status.Trim());
        cmd.Parameters.AddWithValue("@tmdbId", tmdbId is > 0 ? tmdbId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@overview", string.IsNullOrWhiteSpace(overview) ? DBNull.Value : overview.Trim());
        cmd.Parameters.AddWithValue("@poster", (object?)storedPoster ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", dramaId);
        await cmd.ExecuteNonQueryAsync(ct);

        LibraryChanged?.Invoke();
    }

    public async Task DeleteDramaFromCatalogAsync(int dramaId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM DramaCartItems WHERE EpisodeId IN (SELECT Id FROM DramaEpisodes WHERE DramaId = @id);
                DELETE FROM DramaCast WHERE DramaId = @id;
                DELETE FROM DramaEpisodes WHERE DramaId = @id;
                DELETE FROM Dramas WHERE Id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", dramaId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        LibraryChanged?.Invoke();
        DramaCartChanged?.Invoke();
    }

    public async Task SetEpisodeInCartAsync(int episodeId, bool inCart, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        if (inCart)
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO DramaCartItems (EpisodeId, AddedAt)
                VALUES (@id, @now);
                """;
            cmd.Parameters.AddWithValue("@id", episodeId);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        else
        {
            cmd.CommandText = "DELETE FROM DramaCartItems WHERE EpisodeId = @id;";
            cmd.Parameters.AddWithValue("@id", episodeId);
        }

        await cmd.ExecuteNonQueryAsync(ct);
        DramaCartChanged?.Invoke();
    }

    public async Task<IReadOnlyList<DramaEpisode>> GetDramaCartEpisodesAsync(CancellationToken ct = default)
    {
        var list = new List<DramaEpisode>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.Id, e.DramaId, d.Title, d.PosterPath, e.SeasonNumber, e.EpisodeNumber, e.Title, e.FileName,
                   e.FilePath, e.FileSize, e.YouTubeVideoId, e.CopyCount, e.LastCopiedAt, e.AddedAt, 1
            FROM DramaCartItems c
            INNER JOIN DramaEpisodes e ON e.Id = c.EpisodeId
            INNER JOIN Dramas d ON d.Id = e.DramaId
            ORDER BY d.Title COLLATE NOCASE ASC, e.SeasonNumber ASC, e.EpisodeNumber ASC, e.FileName COLLATE NOCASE ASC;
            """;

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var ep = ReadEpisode(r);
            ep.ExistsOnDisk = File.Exists(ep.FilePath);
            list.Add(ep);
        }

        return list;
    }

    public async Task<int> GetDramaCartCountAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DramaCartItems;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task ClearDramaCartAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM DramaCartItems;";
        await cmd.ExecuteNonQueryAsync(ct);
        DramaCartChanged?.Invoke();
    }

    public async Task RecordEpisodeCopiedAndDequeueAsync(int episodeId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE DramaEpisodes
            SET CopyCount = CopyCount + 1,
                LastCopiedAt = @now
            WHERE Id = @id;
            DELETE FROM DramaCartItems WHERE EpisodeId = @id;
            """;
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@id", episodeId);
        await cmd.ExecuteNonQueryAsync(ct);
        DramaCartChanged?.Invoke();
    }

    private async Task<string> SaveDramaArtworkAsync(string fileName, Stream content, CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.PostersDirectory);
        var target = Path.Combine(_paths.PostersDirectory, fileName);
        await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, ct);
        return fileName;
    }

    private static string MapAiringStatus(string? tmdbStatus, string fallback)
    {
        if (string.IsNullOrWhiteSpace(tmdbStatus)) return fallback;
        if (tmdbStatus.Contains("Ended", StringComparison.OrdinalIgnoreCase) ||
            tmdbStatus.Contains("Canceled", StringComparison.OrdinalIgnoreCase) ||
            tmdbStatus.Contains("Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return DramaStatuses.Completed;
        }

        if (tmdbStatus.Contains("Returning", StringComparison.OrdinalIgnoreCase) ||
            tmdbStatus.Contains("In Production", StringComparison.OrdinalIgnoreCase))
        {
            return DramaStatuses.Ongoing;
        }

        return fallback;
    }

    private static void MoveFileAcrossVolumesSafe(string source, string destination)
    {
        try
        {
            File.Move(source, destination);
        }
        catch (IOException)
        {
            var tempDest = destination + ".dcpart";
            try
            {
                File.Copy(source, tempDest, overwrite: true);
                File.Move(tempDest, destination, overwrite: false);
                File.Delete(source);
            }
            catch
            {
                try { if (File.Exists(tempDest)) File.Delete(tempDest); } catch (Exception) { }
                throw;
            }
        }
    }

    private static async Task<(int Id, bool Created, bool HasTmdb)> UpsertDramaAsync(
        SqliteConnection conn,
        string title,
        string folderPath,
        string? channel,
        string? localPosterPath,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await using (var select = conn.CreateCommand())
        {
            select.CommandText = "SELECT Id, TmdbId, PosterPath, Channel FROM Dramas WHERE FolderPath = @path LIMIT 1;";
            select.Parameters.AddWithValue("@path", folderPath);
            await using var r = await select.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                var id = r.GetInt32(0);
                var hasTmdb = !r.IsDBNull(1);
                var existingPoster = r.IsDBNull(2) ? null : r.GetString(2);
                var existingChannel = r.IsDBNull(3) ? null : r.GetString(3);
                await r.CloseAsync();

                await using var upd = conn.CreateCommand();
                upd.CommandText = """
                    UPDATE Dramas
                    SET LastScannedAt = @now,
                        PosterPath = COALESCE(@poster, PosterPath),
                        Channel = COALESCE(@channel, Channel)
                    WHERE Id = @id;
                    """;
                upd.Parameters.AddWithValue("@now", now);
                upd.Parameters.AddWithValue("@poster", (object?)(string.IsNullOrWhiteSpace(existingPoster) ? localPosterPath : existingPoster) ?? DBNull.Value);
                upd.Parameters.AddWithValue("@channel", (object?)(string.IsNullOrWhiteSpace(existingChannel) ? channel : existingChannel) ?? DBNull.Value);
                upd.Parameters.AddWithValue("@id", id);
                await upd.ExecuteNonQueryAsync(ct);
                return (id, false, hasTmdb);
            }
        }

        await using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT INTO Dramas (Title, FolderPath, Channel, Status, StatusLocked, TitleLocked, PosterPath, LastScannedAt, CreatedAt)
            VALUES (@title, @path, @channel, @status, 0, 0, @poster, @now, @now);
            SELECT last_insert_rowid();
            """;
        ins.Parameters.AddWithValue("@title", title.Trim());
        ins.Parameters.AddWithValue("@path", folderPath);
        ins.Parameters.AddWithValue("@channel", (object?)channel ?? DBNull.Value);
        ins.Parameters.AddWithValue("@status", DramaStatuses.Ongoing);
        ins.Parameters.AddWithValue("@poster", (object?)localPosterPath ?? DBNull.Value);
        ins.Parameters.AddWithValue("@now", now);

        var newId = Convert.ToInt32(await ins.ExecuteScalarAsync(ct));
        return (newId, true, false);
    }

    private static async Task<int> UpsertEpisodeAsync(
        SqliteConnection conn,
        int dramaId,
        string filePath,
        ParsedDramaEpisode parsed,
        CancellationToken ct,
        bool forceReparse = false)
    {
        var fullPath = Path.GetFullPath(filePath);
        var fileName = Path.GetFileName(fullPath);
        long size = 0;
        try { size = new FileInfo(fullPath).Length; } catch (Exception) { }

        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT Id, FileSize, SeasonNumber, EpisodeNumber, Title FROM DramaEpisodes WHERE FilePath = @path LIMIT 1;";
            check.Parameters.AddWithValue("@path", fullPath);
            await using var r = await check.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                var id = r.GetInt32(0);
                var oldSize = r.GetInt64(1);
                var oldSeason = r.GetInt32(2);
                var oldEp = r.GetInt32(3);
                var oldTitle = r.GetString(4);
                await r.CloseAsync();

                var needsUpdate = oldSize != size ||
                                  (oldEp == 0 && parsed.EpisodeNumber > 0) ||
                                  (forceReparse && (oldSeason != parsed.SeasonNumber || oldEp != parsed.EpisodeNumber || !string.Equals(oldTitle, parsed.CleanEpisodeTitle, StringComparison.Ordinal)));

                if (needsUpdate)
                {
                    await using var upd = conn.CreateCommand();
                    upd.CommandText = """
                        UPDATE DramaEpisodes
                        SET FileSize = @size,
                            SeasonNumber = @season,
                            EpisodeNumber = CASE WHEN @force = 1 OR EpisodeNumber = 0 THEN @ep ELSE EpisodeNumber END,
                            Title = CASE WHEN @force = 1 THEN @title ELSE Title END
                        WHERE Id = @id;
                        """;
                    upd.Parameters.AddWithValue("@size", size);
                    upd.Parameters.AddWithValue("@season", parsed.SeasonNumber);
                    upd.Parameters.AddWithValue("@ep", parsed.EpisodeNumber);
                    upd.Parameters.AddWithValue("@title", parsed.CleanEpisodeTitle);
                    upd.Parameters.AddWithValue("@force", forceReparse ? 1 : 0);
                    upd.Parameters.AddWithValue("@id", id);
                    await upd.ExecuteNonQueryAsync(ct);
                    return 2;
                }

                return 0;
            }
        }

        var missingCandidates = new List<(int Id, string FileName, string FilePath, long FileSize, int Season, int Ep)>();
        await using (var mCmd = conn.CreateCommand())
        {
            mCmd.CommandText = "SELECT Id, FileName, FilePath, FileSize, SeasonNumber, EpisodeNumber FROM DramaEpisodes WHERE DramaId = @did;";
            mCmd.Parameters.AddWithValue("@did", dramaId);
            await using var r = await mCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var p = r.GetString(2);
                if (!File.Exists(p))
                {
                    missingCandidates.Add((r.GetInt32(0), r.GetString(1), p, r.GetInt64(3), r.GetInt32(4), r.GetInt32(5)));
                }
            }
        }

        var relocated = missingCandidates.FirstOrDefault(m =>
            string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase) ||
            (size > 0 && m.FileSize == size) ||
            (parsed.EpisodeNumber > 0 && m.Season == parsed.SeasonNumber && m.Ep == parsed.EpisodeNumber));

        if (relocated.Id > 0)
        {
            await using var relUpd = conn.CreateCommand();
            relUpd.CommandText = """
                UPDATE DramaEpisodes
                SET FileName = @fname,
                    FilePath = @fpath,
                    FileSize = @size,
                    SeasonNumber = @season,
                    EpisodeNumber = CASE WHEN @ep > 0 THEN @ep ELSE EpisodeNumber END,
                    Title = @title
                WHERE Id = @id;
                """;
            relUpd.Parameters.AddWithValue("@fname", fileName);
            relUpd.Parameters.AddWithValue("@fpath", fullPath);
            relUpd.Parameters.AddWithValue("@size", size);
            relUpd.Parameters.AddWithValue("@season", parsed.SeasonNumber);
            relUpd.Parameters.AddWithValue("@ep", parsed.EpisodeNumber);
            relUpd.Parameters.AddWithValue("@title", parsed.CleanEpisodeTitle);
            relUpd.Parameters.AddWithValue("@id", relocated.Id);
            await relUpd.ExecuteNonQueryAsync(ct);
            return 3;
        }

        await using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT INTO DramaEpisodes (DramaId, SeasonNumber, EpisodeNumber, Title, FileName, FilePath, FileSize, YouTubeVideoId, CopyCount, AddedAt)
            VALUES (@did, @season, @ep, @title, @fname, @fpath, @size, @yt, 0, @now);
            """;
        ins.Parameters.AddWithValue("@did", dramaId);
        ins.Parameters.AddWithValue("@season", parsed.SeasonNumber);
        ins.Parameters.AddWithValue("@ep", parsed.EpisodeNumber);
        ins.Parameters.AddWithValue("@title", parsed.CleanEpisodeTitle);
        ins.Parameters.AddWithValue("@fname", fileName);
        ins.Parameters.AddWithValue("@fpath", fullPath);
        ins.Parameters.AddWithValue("@size", size);
        ins.Parameters.AddWithValue("@yt", (object?)parsed.YouTubeVideoId ?? DBNull.Value);
        ins.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await ins.ExecuteNonQueryAsync(ct);
        return 1;
    }

    private static DramaSeries ReadDrama(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Title = r.GetString(1),
        OriginalTitle = r.IsDBNull(2) ? null : r.GetString(2),
        FolderPath = r.GetString(3),
        Year = r.IsDBNull(4) ? null : r.GetInt32(4),
        Overview = r.IsDBNull(5) ? null : r.GetString(5),
        Language = r.IsDBNull(6) ? null : r.GetString(6),
        Channel = r.IsDBNull(7) ? null : r.GetString(7),
        Status = r.GetString(8),
        StatusLocked = !r.IsDBNull(9) && r.GetInt32(9) != 0,
        TitleLocked = !r.IsDBNull(10) && r.GetInt32(10) != 0,
        ExpectedEpisodeCount = r.IsDBNull(11) ? null : r.GetInt32(11),
        TmdbId = r.IsDBNull(12) ? null : r.GetInt64(12),
        ImdbId = r.IsDBNull(13) ? null : r.GetString(13),
        VoteAverage = r.IsDBNull(14) ? null : r.GetDouble(14),
        PosterPath = r.IsDBNull(15) ? null : r.GetString(15),
        BackdropPath = r.IsDBNull(16) ? null : r.GetString(16),
        Genres = r.IsDBNull(17) ? null : r.GetString(17),
        LastScannedAt = !r.IsDBNull(18) && DateTime.TryParse(r.GetString(18), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ls) ? ls : null,
        CreatedAt = DateTime.TryParse(r.GetString(19), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var cr) ? cr : DateTime.UtcNow
    };

    private static DramaEpisode ReadEpisode(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(0),
        DramaId = r.GetInt32(1),
        DramaTitle = r.GetString(2),
        DramaPosterPath = r.IsDBNull(3) ? null : r.GetString(3),
        SeasonNumber = r.GetInt32(4),
        EpisodeNumber = r.GetInt32(5),
        Title = r.GetString(6),
        FileName = r.GetString(7),
        FilePath = r.GetString(8),
        FileSize = r.GetInt64(9),
        YouTubeVideoId = r.IsDBNull(10) ? null : r.GetString(10),
        CopyCount = r.GetInt32(11),
        LastCopiedAt = !r.IsDBNull(12) && DateTime.TryParse(r.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lc) ? lc : null,
        AddedAt = DateTime.TryParse(r.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ad) ? ad : DateTime.UtcNow,
        IsInUsbCart = r.GetInt32(14) != 0
    };
}
