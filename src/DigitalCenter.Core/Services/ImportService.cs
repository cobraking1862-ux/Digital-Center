using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Tmdb;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Services;

/// <summary>Result of a completed (or failed) import confirmation.</summary>
public sealed record ImportOutcome(bool Success, int? MediaTitleId, string? TargetPath, string Message);

/// <summary>Everything the Management screen shows before the operator confirms a move.</summary>
public sealed record ImportPreview(
    int LogId,
    string FileName,
    string SourcePath,
    long? FileSize,
    bool IsSeries,
    string MatchedTitle,
    int? MatchedYear,
    long? TmdbId,
    double? Confidence,
    string DestinationFolder,
    string PlannedTargetPath,
    bool TargetExists,
    string? ExistingNote);

/// <summary>Import activity statistics for the Management dashboard.</summary>
public sealed record ImportStats(int Total, int Imported, int Skipped, int Failed, int NoMatch, int Unsupported, int AwaitingDestination, int Matching);

/// <summary>
/// The Watch Folder import workflow (DigitalCenter 5.0). Files are matched
/// automatically — external identifiers first, then title/year — and nothing
/// is moved before the operator has chosen a destination folder and confirmed
/// the move. Files without a reliable match are marked No Match and stay in
/// place. Because a file move cannot be made atomic with database writes, the
/// import records its progress in <see cref="MetadataImportLog.Stage"/>
/// checkpoints: Approved → MoveStarted → MovedToLibrary → RegisterStarted →
/// Imported. <see cref="RecoverInterruptedAsync"/> completes or safely fails
/// imports that died between two checkpoints.
/// </summary>
public sealed class ImportService
{
    private readonly IDbContextFactory<DigitalCenterDbContext> _factory;
    private readonly ITmdbMetadataProvider _tmdb;
    private readonly SettingsService _settings;
    private readonly PosterCache _posters;
    private readonly IAppLogger? _logger;

    public ImportService(
        IDbContextFactory<DigitalCenterDbContext> factory,
        ITmdbMetadataProvider tmdb,
        SettingsService settings,
        PosterCache posters,
        IAppLogger? logger = null)
    {
        _factory = factory;
        _tmdb = tmdb;
        _settings = settings;
        _posters = posters;
        _logger = logger;
    }

    // ---------------------------------------------------------------- queries

    public async Task<IReadOnlyList<MetadataImportLog>> GetActivityAsync(int take = 200, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.MetadataImportLogs
            .AsNoTracking()
            .OrderByDescending(l => l.Timestamp)
            .ThenByDescending(l => l.Id)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<MetadataImportLog?> GetLogAsync(int logId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.MetadataImportLogs.AsNoTracking().FirstOrDefaultAsync(l => l.Id == logId, ct);
    }

    public async Task<ImportStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var statuses = await db.MetadataImportLogs
            .GroupBy(l => l.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var total = statuses.Sum(s => s.Count);
        int Count(string status) => statuses.FirstOrDefault(s => s.Key == status)?.Count ?? 0;

        return new ImportStats(
            Total: total,
            Imported: Count(ImportStatuses.Imported),
            Skipped: Count(ImportStatuses.Rejected),
            Failed: Count(ImportStatuses.Failed),
            NoMatch: Count(ImportStatuses.NoMatch),
            Unsupported: Count(ImportStatuses.Unsupported),
            AwaitingDestination: Count(ImportStatuses.Matched),
            Matching: Count(ImportStatuses.Pending) + Count(ImportStatuses.Approved));
    }

    // ------------------------------------------------------------- matching

    /// <summary>
    /// Runs the automatic match for every Pending import log. Called by the
    /// Watch Folder pass, the "Rescan Now" action and application startup, so
    /// files detected while TMDb was offline are retried automatically.
    /// Returns the number of logs that changed state.
    /// </summary>
    public async Task<int> MatchPendingAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pending = await db.MetadataImportLogs
            .Where(l => l.Status == ImportStatuses.Pending)
            .OrderBy(l => l.Id)
            .ToListAsync(ct);

        var changed = 0;
        foreach (var log in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (await MatchOneAsync(log, ct))
            {
                changed++;
            }
        }

        return changed;
    }

    /// <summary>Matches a single log row and persists the result. Returns true when the row changed.</summary>
    public async Task<bool> MatchOneAsync(MetadataImportLog log, CancellationToken ct = default)
    {
        if (log.Status != ImportStatuses.Pending)
        {
            return false;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var tracked = await db.MetadataImportLogs.FirstOrDefaultAsync(l => l.Id == log.Id, ct);
        if (tracked is null || tracked.Status != ImportStatuses.Pending)
        {
            return false;
        }

        var result = await MatchTrackedAsync(tracked, ct);
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Core matching used by both entry points; the caller owns SaveChanges.</summary>
    private async Task<bool> MatchTrackedAsync(MetadataImportLog log, CancellationToken ct)
    {
        var parsed = new FilenameParser().Parse(
            log.SourceFile,
            string.IsNullOrEmpty(log.SourcePath) ? null : Path.GetDirectoryName(log.SourcePath));

        // Non-media files were already marked Unsupported at detection time;
        // a pending row without any usable signal can never match.
        if (!parsed.HasExternalId && string.IsNullOrWhiteSpace(parsed.Title))
        {
            log.Status = ImportStatuses.NoMatch;
            log.Confidence = 0;
            log.ErrorDetails = "The file name contains neither an identifier nor a usable title; the file stays in place.";
            _logger?.Info($"Import match failed (no signal): '{log.SourceFile}'.");
            return true;
        }

        try
        {
            var matcher = new MetadataMatcher(_tmdb, _settings);
            var outcome = await matcher.MatchAsync(parsed, ct);
            log.Confidence = outcome.Confidence;
            log.TmdbId = outcome.TmdbId;
            log.ErrorDetails = null;

            if (outcome.IsMatch)
            {
                log.IsSeries = outcome.IsSeries;
                // TMDb metadata wins over the parsed name when it arrives.
                var metadata = outcome.IsSeries
                    ? await _tmdb.GetSeriesAsync(outcome.TmdbId!.Value, ct)
                    : await _tmdb.GetMovieAsync(outcome.TmdbId!.Value, ct);
                if (metadata is not null)
                {
                    if (!string.IsNullOrWhiteSpace(metadata.Title))
                    {
                        log.ParsedTitle = metadata.Title;
                    }

                    if (metadata.Year.HasValue)
                    {
                        log.ParsedYear = metadata.Year.Value;
                    }
                }

                log.Status = ImportStatuses.Matched;
                log.MetadataSource = "TMDb";
                log.Note = outcome.Detail;
                _logger?.Info($"Import matched: '{log.SourceFile}' → TMDb {outcome.TmdbId} ({outcome.Confidence:P0}).");
            }
            else
            {
                log.Status = ImportStatuses.NoMatch;
                log.MetadataSource = null;
                log.ErrorDetails = outcome.Detail;
                _logger?.Info($"Import has no match: '{log.SourceFile}' ({outcome.Detail}).");
            }

            return true;
        }
        catch (Exception ex) when (ex is TmdbNotConfiguredException or TmdbRequestException or InvalidOperationException)
        {
            // Stay Pending: the next pass (rescan / startup / next poll) retries.
            log.ErrorDetails = ex is TmdbNotConfiguredException
                ? "Metadata matching is unavailable: no TMDb token is configured."
                : $"Metadata matching is unavailable: {ex.Message}";
            return false;
        }
    }

    // ------------------------------------------------------------ destination

    /// <summary>Default destination folder for a matched file (movies root or series root).</summary>
    public string? GetDefaultDestinationFolder(bool isSeries, AppSettings settings) =>
        isSeries ? settings.SeriesRoot : settings.MoviesRoot;

    /// <summary>Records the operator's destination choice (step 6 of the workflow).</summary>
    public async Task ChooseDestinationAsync(int logId, string destinationFolder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder) || !Directory.Exists(destinationFolder))
        {
            throw new InvalidOperationException($"The destination folder does not exist: '{destinationFolder}'.");
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var log = await db.MetadataImportLogs.FirstOrDefaultAsync(l => l.Id == logId, ct)
                  ?? throw new InvalidOperationException("The selected import no longer exists.");

        if (log.Status != ImportStatuses.Matched)
        {
            throw new InvalidOperationException($"Only matched imports can receive a destination (current status: {log.Status}).");
        }

        log.ChosenFolder = Path.GetFullPath(destinationFolder);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Builds the pre-move preview: source, matched title, planned target and collision state.</summary>
    public async Task<ImportPreview> PreviewImportAsync(MetadataImportLog log, string? destinationFolder = null, CancellationToken ct = default)
    {
        var settings = _settings.Load();
        var folder = destinationFolder ?? log.ChosenFolder ?? GetDefaultDestinationFolder(log.IsSeries, settings)
                     ?? throw new InvalidOperationException("Choose a destination folder first.");

        var cleanName = settings.CleanFilenames
            ? PathPlanner.BuildImportFileName(
                settings.MovieFilenameTemplate, settings.SeriesFilenameTemplate,
                log.IsSeries, log.ParsedTitle ?? "Unknown", log.ParsedYear, log.ParsedSeason, log.ParsedEpisode, log.SourceFile)
            : log.SourceFile;

        var targetPath = PathPlanner.PlanImportTargetPath(folder, cleanName);
        var targetExists = File.Exists(targetPath);
        string? note = null;
        if (targetExists)
        {
            note = settings.UsbConflictAction.Equals("Overwrite", StringComparison.OrdinalIgnoreCase)
                ? "A file with this name exists; it will be replaced after your confirmation."
                : "A file with this name exists; the import will be saved under a numbered name.";
        }

        return new ImportPreview(
            log.Id,
            log.SourceFile,
            log.SourcePath ?? string.Empty,
            log.FileSize,
            log.IsSeries,
            log.ParsedTitle ?? "Unknown",
            log.ParsedYear,
            log.TmdbId,
            log.Confidence,
            folder,
            targetPath,
            targetExists,
            note);
    }

    // -------------------------------------------------------------- confirm

    /// <summary>
    /// Confirms the move of a matched file into the chosen destination folder
    /// (steps 7-10 of the workflow). Nothing runs before this call.
    /// </summary>
    public async Task<ImportOutcome> ConfirmImportAsync(int logId, string? destinationFolder = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var log = await db.MetadataImportLogs.FirstOrDefaultAsync(l => l.Id == logId, ct)
                  ?? throw new InvalidOperationException("The selected import no longer exists.");

        if (log.Status == ImportStatuses.Approved)
        {
            // A previous confirmation crashed before the move; resume it.
            return await ExecuteApprovedImportAsync(db, log, useTmdb: false, ct);
        }

        if (log.Status != ImportStatuses.Matched)
        {
            throw new InvalidOperationException(
                $"Only matched imports can be confirmed (current status: {ImportStatusLabels.For(log.Status)}).");
        }

        var settings = _settings.Load();
        var folder = destinationFolder ?? log.ChosenFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            throw new InvalidOperationException("Choose an existing destination folder before confirming the move.");
        }

        log.ChosenFolder = Path.GetFullPath(folder);
        log.Status = ImportStatuses.Approved;
        log.Stage = ImportStages.Approved;
        log.ErrorDetails = null;
        await db.SaveChangesAsync(ct); // Checkpoint: crash here → recovery re-runs the whole pipeline.

        return await ExecuteApprovedImportAsync(db, log, useTmdb: true, ct);
    }

    /// <summary>Skips a matched file; the file stays in the Watch Folder untouched.</summary>
    public async Task SkipAsync(int logId, string? reason = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var log = await db.MetadataImportLogs.FirstOrDefaultAsync(l => l.Id == logId, ct)
                  ?? throw new InvalidOperationException("The selected import no longer exists.");

        if (log.Status != ImportStatuses.Matched && log.Status != ImportStatuses.Pending && log.Status != ImportStatuses.NoMatch)
        {
            throw new InvalidOperationException($"Only waiting imports can be skipped (current status: {ImportStatusLabels.For(log.Status)}).");
        }

        log.Status = ImportStatuses.Rejected;
        log.ErrorDetails = string.IsNullOrWhiteSpace(reason) ? "Skipped by the operator." : reason;
        await db.SaveChangesAsync(ct);
        _logger?.Info($"Import skipped: '{log.SourceFile}'.");
    }

    /// <summary>Executes (or resumes) an approved import. The log must be in Status=Approved.</summary>
    private async Task<ImportOutcome> ExecuteApprovedImportAsync(DigitalCenterDbContext db, MetadataImportLog log, bool useTmdb, CancellationToken ct)
    {
        var settings = _settings.Load();
        var isSeries = log.IsSeries;
        var sourcePath = log.SourcePath ?? string.Empty;
        var destinationFolder = log.ChosenFolder
                                ?? GetDefaultDestinationFolder(isSeries, settings)
                                ?? throw new InvalidOperationException("No destination folder is known for this import.");

        if (!Directory.Exists(destinationFolder))
        {
            const string message = "The destination folder no longer exists. Choose a destination again.";
            log.Status = ImportStatuses.Matched; // Back to the operator, the file was not touched.
            log.ErrorDetails = message;
            await db.SaveChangesAsync(ct);
            return new ImportOutcome(false, null, null, message);
        }

        if (isSeries && (!log.ParsedSeason.HasValue || !log.ParsedEpisode.HasValue))
        {
            const string message = "The import is marked as a series episode but has no season/episode number.";
            log.Status = ImportStatuses.Failed;
            log.ErrorDetails = message;
            await db.SaveChangesAsync(ct);
            return new ImportOutcome(false, null, null, message);
        }

        // ---------- 1. Metadata upsert (idempotent) ----------
        var (titleId, posterPath, metadata) = await UpsertTitleAsync(db, log, useTmdb, ct);

        // ---------- 2. Plan and record the destination ----------
        // When recovery resumes an import that already passed the MoveStarted
        // checkpoint, the recorded target is reused as-is (the interrupted
        // copy may have completed it). A fresh import resolves file name
        // collisions with a numeric suffix instead of ever overwriting.
        var resuming = log.Stage is ImportStages.MoveStarted or ImportStages.MovedToLibrary or ImportStages.RegisterStarted
                       && !string.IsNullOrEmpty(log.TargetPath);

        string targetPath;
        if (resuming)
        {
            targetPath = log.TargetPath!;
        }
        else
        {
            var cleanName = settings.CleanFilenames
                ? PathPlanner.BuildImportFileName(
                    settings.MovieFilenameTemplate, settings.SeriesFilenameTemplate,
                    isSeries, log.ParsedTitle ?? "Unknown", log.ParsedYear, log.ParsedSeason, log.ParsedEpisode, log.SourceFile)
                : log.SourceFile;

            var fileName = PathPlanner.ResolveFileNameConflict(destinationFolder, PathPlanner.SanitizeFileName(cleanName));
            targetPath = Path.Combine(destinationFolder, fileName);
        }

        if (!Path.GetFullPath(targetPath).StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationFolder)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The planned destination is outside the chosen folder: '{targetPath}'.");
        }

        log.TargetPath = targetPath;
        log.Stage = ImportStages.MoveStarted;
        await db.SaveChangesAsync(ct); // Checkpoint.

        // ---------- 3. Move the file (copy → verify → rename → delete source) ----------
        var moveResult = await MoveSourceIntoLibraryAsync(log, sourcePath, targetPath, ct);
        if (!moveResult.Success)
        {
            log.Status = ImportStatuses.Failed;
            log.ErrorDetails = moveResult.Message;
            await db.SaveChangesAsync(ct);
            _logger?.Error($"Import of '{log.SourceFile}' failed: {moveResult.Message}");
            return new ImportOutcome(false, titleId, targetPath, moveResult.Message!);
        }

        log.Note = moveResult.Note;
        log.Stage = ImportStages.MovedToLibrary;
        await db.SaveChangesAsync(ct); // Checkpoint: full copy exists in the library.

        // ---------- 4. Register the file (idempotent) ----------
        await RegisterFileAsync(db, log, targetPath, isSeries, titleId, ct);

        // ---------- 5. Artwork (best effort; a failure never fails the import) ----------
        await TryCacheArtworkAsync(db, log, titleId, posterPath, metadata, ct);

        log.Status = ImportStatuses.Imported;
        log.Stage = ImportStages.Imported;
        log.MediaTitleId = titleId;
        await db.SaveChangesAsync(ct);

        _logger?.Info($"Import complete: '{log.SourceFile}' → '{targetPath}' (title {titleId}).");
        return new ImportOutcome(true, titleId, targetPath, moveResult.Message ?? "Imported successfully.");
    }

    /// <summary>Creates or reuses title/season/episode rows and applies TMDb metadata when requested.</summary>
    private async Task<(int TitleId, string? PosterPath, TmdbTitleMetadata? Metadata)> UpsertTitleAsync(DigitalCenterDbContext db, MetadataImportLog log, bool useTmdb, CancellationToken ct)
    {
        var kind = log.IsSeries ? MediaKind.Series : MediaKind.Movie;

        // A previous partial run may have registered the TMDb id already.
        MediaTitle? title = null;
        if (log.TmdbId.HasValue)
        {
            title = await db.MediaTitles.FirstOrDefaultAsync(t => t.TmdbId == log.TmdbId.Value, ct);
        }

        title ??= await FindExistingTitleAsync(db, kind, log.ParsedTitle ?? string.Empty, log.ParsedYear, ct);

        TmdbTitleMetadata? metadata = null;
        string? posterPath = null;
        if (useTmdb && log.TmdbId.HasValue)
        {
            metadata = log.IsSeries
                ? await _tmdb.GetSeriesAsync(log.TmdbId.Value, ct)
                : await _tmdb.GetMovieAsync(log.TmdbId.Value, ct);

            // TMDb result wins over the parsed name when available.
            if (metadata is not null)
            {
                posterPath = metadata.PosterPath;
                if (!string.IsNullOrWhiteSpace(metadata.Title))
                {
                    log.ParsedTitle = metadata.Title;
                }

                if (metadata.Year.HasValue)
                {
                    log.ParsedYear = metadata.Year.Value;
                }
            }
        }

        if (title is null)
        {
            title = new MediaTitle
            {
                Type = kind,
                Title = log.ParsedTitle ?? "Unknown",
                ReleaseYear = log.ParsedYear
            };
            db.MediaTitles.Add(title);
            await db.SaveChangesAsync(ct); // Need the id.
        }

        // Stamp the TMDb id on titles found by name (unique index is safe:
        // the TMDb-id lookup above found no other holder).
        title.TmdbId ??= log.TmdbId;

        if (metadata is not null)
        {
            log.MetadataSource = "TMDb";
            MetadataMerge.Apply(db, title, metadata);
            await db.SaveChangesAsync(ct);
        }
        else if (log.MetadataSource is null or "WatchFolder")
        {
            // No online metadata was (or could be) applied: the catalog entry
            // comes from the matched/parsed file name.
            log.MetadataSource = "TMDb match (offline)";
        }

        log.MediaTitleId = title.Id;
        await db.SaveChangesAsync(ct);
        return (title.Id, posterPath, metadata);
    }

    private static async Task<MediaTitle?> FindExistingTitleAsync(DigitalCenterDbContext db, MediaKind kind, string titleName, int? year, CancellationToken ct)
    {
        var candidates = await db.MediaTitles
            .Where(t => t.Type == kind && t.Title == titleName) // COLLATE NOCASE
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            var single = candidates[0];
            return !year.HasValue || !single.ReleaseYear.HasValue || single.ReleaseYear == year.Value
                ? single
                : null;
        }

        return year.HasValue ? candidates.FirstOrDefault(t => t.ReleaseYear == year.Value) : null;
    }

    private sealed record MoveOutcome(bool Success, string? Message, string? Note);

    /// <summary>
    /// Copies source → "{target}.dcenter-partial", verifies the size, renames
    /// to the final name and only then deletes the source. Every state is
    /// recoverable (see <see cref="RecoverInterruptedAsync"/>).
    /// </summary>
    private async Task<MoveOutcome> MoveSourceIntoLibraryAsync(MetadataImportLog log, string sourcePath, string targetPath, CancellationToken ct)
    {
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(targetDirectory))
        {
            return new MoveOutcome(false, "The planned destination path has no directory part.", null);
        }

        if (File.Exists(targetPath))
        {
            // A previous run already completed the copy (crash between the
            // rename and the checkpoint). Treat the existing file as the
            // result; the operator is informed via the note.
            var existingSize = new FileInfo(targetPath).Length;
            if (log.FileSize.HasValue && existingSize != log.FileSize.Value)
            {
                return new MoveOutcome(false,
                    $"A different file already exists at the destination ({existingSize} bytes vs. expected {log.FileSize.Value}). " +
                    "The import was stopped to avoid overwriting data.", null);
            }

            TryDelete(sourcePath);
            return new MoveOutcome(true, null, "An identical file already existed at the destination and was kept.");
        }

        if (!File.Exists(sourcePath))
        {
            return new MoveOutcome(false,
                $"The source file is no longer present in the Watch Folder: '{sourcePath}'. The file was not imported.", null);
        }

        Directory.CreateDirectory(targetDirectory);
        var partialPath = targetPath + ".dcenter-partial";

        try
        {
            const int bufferSize = 1024 * 1024;
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true))
            await using (var destination = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
            {
                await source.CopyToAsync(destination, bufferSize, ct);
                await destination.FlushAsync(ct);
            }

            var copied = new FileInfo(partialPath).Length;
            var expected = new FileInfo(sourcePath).Length;
            if (copied != expected)
            {
                TryDelete(partialPath);
                return new MoveOutcome(false, $"The copied file size ({copied} bytes) differs from the source ({expected} bytes). The import was aborted.", null);
            }

            File.Move(partialPath, targetPath, overwrite: false);
            TryDelete(sourcePath);

            var note = File.Exists(sourcePath)
                ? "The copy succeeded but the original could not be removed from the Watch Folder; remove it manually."
                : null;
            return new MoveOutcome(true, null, note);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partialPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(partialPath);
            return new MoveOutcome(false, $"The file could not be moved into the library: {ex.Message}", null);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Source stays in place; the import note tells the operator.
        }
    }

    private async Task RegisterFileAsync(DigitalCenterDbContext db, MetadataImportLog log, string targetPath, bool isSeries, int titleId, CancellationToken ct)
    {
        log.Stage = ImportStages.RegisterStarted;
        await db.SaveChangesAsync(ct);

        // Idempotent: a crashed run may have registered the file already.
        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.FilePath == targetPath, ct);
        long size;
        try
        {
            size = new FileInfo(targetPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            size = log.FileSize ?? 0;
        }

        if (file is null)
        {
            file = new MediaFile { FilePath = targetPath, FileSize = size };
            if (isSeries)
            {
                var episode = await FindOrCreateEpisodeAsync(db, titleId, log.ParsedSeason!.Value, log.ParsedEpisode!.Value, ct);
                file.EpisodeId = episode.Id;
            }
            else
            {
                file.MovieTitleId = titleId;
            }

            db.MediaFiles.Add(file);
        }
        else if (file.MovieTitleId is null && file.EpisodeId is null)
        {
            // Should not happen (check constraint), but stay defensive.
            file.MovieTitleId = titleId;
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<Episode> FindOrCreateEpisodeAsync(DigitalCenterDbContext db, int titleId, int seasonNumber, int episodeNumber, CancellationToken ct)
    {
        var details = await db.SeriesDetails.FirstOrDefaultAsync(sd => sd.MediaTitleId == titleId, ct);
        if (details is null)
        {
            details = new SeriesDetails { MediaTitleId = titleId };
            db.SeriesDetails.Add(details);
            await db.SaveChangesAsync(ct);
        }

        var season = await db.Seasons.FirstOrDefaultAsync(s => s.SeriesDetailsId == titleId && s.SeasonNumber == seasonNumber, ct);
        if (season is null)
        {
            season = new Season { SeriesDetailsId = titleId, SeasonNumber = seasonNumber };
            db.Seasons.Add(season);
            await db.SaveChangesAsync(ct);
        }

        var episode = await db.Episodes.FirstOrDefaultAsync(e => e.SeasonId == season.Id && e.EpisodeNumber == episodeNumber, ct);
        if (episode is null)
        {
            episode = new Episode
            {
                SeasonId = season.Id,
                EpisodeNumber = episodeNumber,
                Title = $"Episode {episodeNumber}"
            };
            db.Episodes.Add(episode);
            await db.SaveChangesAsync(ct);
        }

        return episode;
    }

    private async Task TryCacheArtworkAsync(DigitalCenterDbContext db, MetadataImportLog log, int titleId, string? posterPath, TmdbTitleMetadata? metadata, CancellationToken ct)
    {
        try
        {
            var settings = _settings.Load();
            if (!_tmdb.IsConfigured || log.TmdbId is null)
            {
                return;
            }

            var title = await db.MediaTitles.FirstAsync(t => t.Id == titleId, ct);

            if (settings.DownloadPosters &&
                !string.IsNullOrWhiteSpace(posterPath) &&
                !_posters.Exists(title.PosterPath))
            {
                await using var stream = await _tmdb.DownloadPosterAsync(posterPath, ct);
                title.PosterPath = await _posters.SaveAsync(titleId, stream, ct);
                title.TmdbPosterPath = posterPath;
                await db.SaveChangesAsync(ct);
            }

            if (settings.DownloadBackdrops &&
                !string.IsNullOrWhiteSpace(title.TmdbBackdropPath) &&
                !_posters.Exists(title.BackdropPath))
            {
                await using var stream = await _tmdb.DownloadBackdropAsync(title.TmdbBackdropPath, ct);
                title.BackdropPath = await _posters.SaveBackdropAsync(titleId, stream, ct);
                await db.SaveChangesAsync(ct);
            }

            if (settings.DownloadCastProfiles && metadata is not null)
            {
                await CacheProfilesAsync(db, titleId, metadata, ct);
            }
        }
        catch (Exception ex) when (ex is TmdbRequestException or TmdbNotConfiguredException or IOException or InvalidOperationException)
        {
            _logger?.Warn($"Artwork caching failed for title {titleId}: {ex.Message}");
        }
    }

    private async Task CacheProfilesAsync(DigitalCenterDbContext db, int titleId, TmdbTitleMetadata metadata, CancellationToken ct)
    {
        var people = await db.TitlePeople
            .Where(tp => tp.MediaTitleId == titleId)
            .Include(tp => tp.Person)
            .ToListAsync(ct);

        foreach (var credit in metadata.People)
        {
            ct.ThrowIfCancellationRequested();
            if (credit.TmdbPersonId is null || string.IsNullOrWhiteSpace(credit.ProfilePath))
            {
                continue; // No TMDb image reference for this person.
            }

            var person = people.FirstOrDefault(p => p.Person.TmdbPersonId == credit.TmdbPersonId.Value)?.Person;
            if (person is null)
            {
                continue;
            }

            var cacheName = $"p{credit.TmdbPersonId.Value}.jpg";
            if (_posters.Exists(cacheName))
            {
                person.ProfilePath = cacheName;
                continue;
            }

            try
            {
                await using var stream = await _tmdb.DownloadProfileAsync(credit.ProfilePath, ct);
                await _posters.SaveProfileAsync(credit.TmdbPersonId.Value, stream, ct);
                person.ProfilePath = cacheName;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is TmdbRequestException or IOException)
            {
                // One missing profile never blocks the rest.
            }
        }
    }

    /// <summary>
    /// Startup recovery: completes or safely fails imports that were approved
    /// but interrupted. Deterministic: no network access during recovery.
    /// Returns a human-readable report for the operator.
    /// </summary>
    public async Task<IReadOnlyList<string>> RecoverInterruptedAsync(CancellationToken ct = default)
    {
        var report = new List<string>();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var interrupted = await db.MetadataImportLogs
            .Where(l => l.Status == ImportStatuses.Approved)
            .OrderBy(l => l.Id)
            .ToListAsync(ct);

        if (interrupted.Count == 0)
        {
            return report;
        }

        _logger?.Warn($"Found {interrupted.Count} interrupted import(s); starting recovery.");

        foreach (var log in interrupted)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await ExecuteApprovedImportAsync(db, log, useTmdb: false, ct);
                report.Add(outcome.Success
                    ? $"Recovered import '{log.SourceFile}' → {outcome.TargetPath}"
                    : $"Import '{log.SourceFile}' failed during recovery: {outcome.Message}");
            }
            catch (Exception ex)
            {
                log.Status = ImportStatuses.Failed;
                log.ErrorDetails = $"Recovery failed: {ex.Message}";
                await db.SaveChangesAsync(ct);
                report.Add($"Import '{log.SourceFile}' could not be recovered: {ex.Message}");
                _logger?.Error($"Recovery failed for import {log.Id} ('{log.SourceFile}').", ex);
            }
        }

        return report;
    }
}
