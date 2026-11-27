using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Tmdb;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Services;

/// <summary>Result of a completed (or failed) import confirmation.</summary>
public sealed record ImportOutcome(bool Success, int? MediaTitleId, string? TargetPath, string Message);

/// <summary>Everything the Management screen shows for a detected Watch Folder file.</summary>
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
    string DetectedLanguage,
    string DestinationFolder,
    string PlannedTargetPath,
    bool TargetExists,
    string? ExistingNote);

/// <summary>Import activity statistics for the Management dashboard.</summary>
public sealed record ImportStats(
    int Total,
    int Imported,
    int Skipped,
    int Failed,
    int NoMatch,
    int Unsupported,
    int AwaitingDestination,
    int Matching);

/// <summary>
/// Watch Folder import and language auto-organization service.
/// When new files are detected in the Watch Folder:
/// 1) Metadata is fetched automatically from TMDb (external IDs first, then title/year).
/// 2) The language is resolved (TMDb original language, filename language tag, or country fallback).
/// 3) When a library root is configured and auto-organization is enabled, the language
///    sub-folder (&lt;LibraryRoot&gt;/&lt;Language&gt;) is created if it does not exist, and the
///    file is automatically moved into that sub-folder and registered in the catalog.
/// 4) Checkpoint stages (Approved → MoveStarted → MovedToLibrary → RegisterStarted → Imported)
///    allow <see cref="RecoverInterruptedAsync"/> to heal interrupted moves at startup.
/// </summary>
public sealed class ImportService
{
    /// <summary>Runtime work status consumed by the shell activity feed.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised whenever an individual file import log changes state, so the Watch Folder screen updates live.</summary>
    public event Action? ActivityChanged;

    private void NotifyActivityChanged()
    {
        var handlers = ActivityChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var callback in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Import activity notification failed: {ex.Message}");
            }
        }
    }

    private void ReportStatus(string message)
    {
        var handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var callback in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                callback(message);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Import status notification failed: {ex.Message}");
            }
        }
    }

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

    /// <summary>
    /// Clears Watch Folder activity history from MetadataImportLogs without
    /// affecting cataloged titles, media files, or any in-flight Approved moves.
    /// </summary>
    public async Task<int> ClearHistoryAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var removed = await db.MetadataImportLogs
            .Where(l => l.Status != ImportStatuses.Approved)
            .ExecuteDeleteAsync(ct);

        if (removed > 0)
        {
            _logger?.Info($"Cleared {removed} Watch Folder history item(s).");
            NotifyActivityChanged();
        }

        return removed;
    }

    // ------------------------------------------- matching & auto-organization

    /// <summary>
    /// Runs the automatic metadata match (and automatic language-subfolder move
    /// when configured) for every Pending import log.
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
            ReportStatus($"Fetching metadata: {log.SourceFile}");
            if (await MatchOneAsync(log, ct))
            {
                changed++;
                NotifyActivityChanged();
            }
        }

        return changed;
    }

    /// <summary>Matches a single log row, auto-organizing into a language subfolder when ready.</summary>
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

        var result = await MatchAndOrganizeTrackedAsync(db, tracked, ct);
        await db.SaveChangesAsync(ct);
        return result;
    }

    private async Task<bool> MatchAndOrganizeTrackedAsync(
        DigitalCenterDbContext db,
        MetadataImportLog log,
        CancellationToken ct)
    {
        var parsed = new FilenameParser().Parse(
            log.SourceFile,
            string.IsNullOrEmpty(log.SourcePath) ? null : Path.GetDirectoryName(log.SourcePath));

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

            if (!outcome.IsMatch)
            {
                log.Status = ImportStatuses.NoMatch;
                log.MetadataSource = null;
                log.ErrorDetails = outcome.Detail;
                _logger?.Info($"Import has no match: '{log.SourceFile}' ({outcome.Detail}).");
                return true;
            }

            log.IsSeries = outcome.IsSeries;
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

            var detectedLanguage = LanguageResolver.Resolve(
                metadata?.OriginalLanguage ?? outcome.DetectedLanguage,
                log.SourceFile,
                metadata?.Countries);
            var languageName = LanguageResolver.ResolveLanguageName(
                metadata?.OriginalLanguage ?? outcome.DetectedLanguage,
                log.SourceFile,
                metadata?.Countries);

            var collectionName = !log.IsSeries
                ? CollectionResolver.Resolve(metadata?.CollectionName, metadata?.Title ?? log.ParsedTitle, log.SourceFile)
                : null;

            log.CategoryName = !string.IsNullOrWhiteSpace(collectionName) ? collectionName : detectedLanguage;
            log.MetadataSource = "TMDb";
            log.Note = !string.IsNullOrWhiteSpace(collectionName)
                ? $"{outcome.Detail} · Collection: {collectionName} (Language: {languageName})"
                : $"{outcome.Detail} · Language: {languageName} → Folder: {detectedLanguage}";
            _logger?.Info($"Import matched: '{log.SourceFile}' → TMDb {outcome.TmdbId} ({outcome.Confidence:P0}, Folder={log.CategoryName}).");

            var settings = _settings.Load();
            var baseRoot = !string.IsNullOrWhiteSpace(collectionName)
                ? (settings.GetEffectiveCollectionsRoot() ?? GetDefaultDestinationFolder(log.IsSeries, settings))
                : GetDefaultDestinationFolder(log.IsSeries, settings);

            var baseRootParent = !string.IsNullOrWhiteSpace(baseRoot)
                ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseRoot)))
                : null;
            var baseRootAvailable = !string.IsNullOrWhiteSpace(baseRoot) &&
                                    (Directory.Exists(baseRoot) || (!string.IsNullOrWhiteSpace(baseRootParent) && Directory.Exists(baseRootParent)));

            // Automatic organization into <CollectionsRoot>/<CollectionName> (for multi-part movies)
            // or <LibraryRoot>/<RegionalFolder> (for standalone movies) when enabled.
            if (settings.AutoOrganizeByLanguage &&
                !settings.AskForDestination &&
                baseRootAvailable)
            {
                var targetFolder = !string.IsNullOrWhiteSpace(collectionName)
                    ? PathPlanner.PlanCollectionFolderPath(baseRoot!, collectionName)
                    : PathPlanner.PlanLanguageFolderPath(baseRoot!, detectedLanguage);
                Directory.CreateDirectory(targetFolder);

                log.ChosenFolder = targetFolder;
                log.Status = ImportStatuses.Approved;
                log.Stage = ImportStages.Approved;
                await db.SaveChangesAsync(ct);

                await ExecuteApprovedImportAsync(db, log, useTmdb: false, ct, preloadedMetadata: metadata);
                return true;
            }

            log.Status = ImportStatuses.Matched;
            if (!string.IsNullOrWhiteSpace(baseRoot))
            {
                log.ChosenFolder = settings.AutoOrganizeByLanguage
                    ? (!string.IsNullOrWhiteSpace(collectionName)
                        ? PathPlanner.PlanCollectionFolderPath(baseRoot, collectionName)
                        : PathPlanner.PlanLanguageFolderPath(baseRoot, detectedLanguage))
                    : Path.GetFullPath(baseRoot);
            }

            return true;
        }
        catch (Exception ex) when (ex is TmdbNotConfiguredException or TmdbRequestException or InvalidOperationException)
        {
            var error = ex is TmdbNotConfiguredException
                ? "Metadata matching is unavailable: no TMDb token is configured."
                : $"Metadata matching is unavailable: {ex.Message}";
            var changed = !string.Equals(log.ErrorDetails, error, StringComparison.Ordinal);
            log.ErrorDetails = error;
            return changed;
        }
    }

    // ------------------------------------------------------------ destination

    private static bool IsPathInsideFolder(string folder, string candidatePath)
    {
        var fullFolder = Path.GetFullPath(folder);
        var fullCandidate = Path.GetFullPath(candidatePath);
        var relative = Path.GetRelativePath(fullFolder, fullCandidate);

        return !Path.IsPathRooted(relative)
               && relative != "."
               && relative != ".."
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>Default library root folder for a matched file (movies root or series root).</summary>
    public string? GetDefaultDestinationFolder(bool isSeries, AppSettings settings) =>
        settings.GetEffectiveRoot(isSeries);

    /// <summary>
    /// Resolves the language subfolder inside the base folder (creating it if needed)
    /// when AutoOrganizeByLanguage is enabled.
    /// </summary>
    public string ResolveLanguageDestinationFolder(string baseFolder, string? detectedLanguage, AppSettings settings)
    {
        var fullBase = Path.GetFullPath(baseFolder);
        if (!settings.AutoOrganizeByLanguage)
        {
            Directory.CreateDirectory(fullBase);
            return fullBase;
        }

        var langFolder = PathPlanner.PlanLanguageFolderPath(fullBase, detectedLanguage);
        Directory.CreateDirectory(langFolder);
        return langFolder;
    }

    /// <summary>Records the operator's destination choice.</summary>
    public async Task ChooseDestinationAsync(int logId, string destinationFolder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder) || !Directory.Exists(destinationFolder))
        {
            throw new InvalidOperationException($"The destination folder does not exist: '{destinationFolder}'.");
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var log = await db.MetadataImportLogs.FirstOrDefaultAsync(l => l.Id == logId, ct)
                  ?? throw new InvalidOperationException("The selected import no longer exists.");

        if (log.Status == ImportStatuses.Imported)
        {
            // Already auto-imported into the language subfolder during detection.
            return;
        }

        if (log.Status != ImportStatuses.Matched)
        {
            throw new InvalidOperationException($"Only matched imports can receive a destination (current status: {log.Status}).");
        }

        var settings = _settings.Load();
        log.ChosenFolder = ResolveLanguageDestinationFolder(destinationFolder, log.CategoryName, settings);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Builds the pre-move preview: source, matched title, language, planned target and collision state.</summary>
    public async Task<ImportPreview> PreviewImportAsync(MetadataImportLog log, string? destinationFolder = null, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var settings = _settings.Load();
        var language = string.IsNullOrWhiteSpace(log.CategoryName)
            ? LanguageResolver.Resolve(null, log.SourceFile)
            : LanguageResolver.MapToCategoryFolder(log.CategoryName);

        var baseFolder = destinationFolder ?? log.ChosenFolder ?? GetDefaultDestinationFolder(log.IsSeries, settings)
                         ?? throw new InvalidOperationException("Choose a destination folder first.");

        var folder = settings.AutoOrganizeByLanguage
            ? PathPlanner.PlanLanguageFolderPath(baseFolder, language)
            : Path.GetFullPath(baseFolder);

        var cleanName = settings.CleanFilenames
            ? PathPlanner.BuildImportFileName(
                settings.MovieFilenameTemplate, settings.SeriesFilenameTemplate,
                log.IsSeries, log.ParsedTitle ?? "Unknown", log.ParsedYear, log.ParsedSeason, log.ParsedEpisode, log.SourceFile)
            : log.SourceFile;

        var targetPath = log.Status == ImportStatuses.Imported && !string.IsNullOrWhiteSpace(log.TargetPath)
            ? log.TargetPath!
            : PathPlanner.PlanImportTargetPath(folder, cleanName);
        var targetExists = File.Exists(targetPath);
        string? note = null;
        if (targetExists && log.Status != ImportStatuses.Imported)
        {
            note = settings.UsbConflictAction.Equals("Overwrite", StringComparison.OrdinalIgnoreCase)
                ? "A file with this name exists; it will be replaced after confirmation."
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
            language,
            folder,
            targetPath,
            targetExists,
            note);
    }

    // -------------------------------------------------------------- confirm

    /// <summary>
    /// Confirms the move of a matched file into the destination folder (or
    /// returns the completed outcome if the file was already auto-organized).
    /// </summary>
    public async Task<ImportOutcome> ConfirmImportAsync(int logId, string? destinationFolder = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var log = await db.MetadataImportLogs.FirstOrDefaultAsync(l => l.Id == logId, ct)
                  ?? throw new InvalidOperationException("The selected import no longer exists.");

        if (log.Status == ImportStatuses.Imported)
        {
            return new ImportOutcome(true, log.MediaTitleId, log.TargetPath, "Imported successfully.");
        }

        if (log.Status == ImportStatuses.Approved)
        {
            return await ExecuteApprovedImportAsync(db, log, useTmdb: false, ct);
        }

        if (log.Status != ImportStatuses.Matched)
        {
            throw new InvalidOperationException(
                $"Only matched imports can be confirmed (current status: {ImportStatusLabels.For(log.Status)}).");
        }

        var settings = _settings.Load();
        var folder = destinationFolder ?? log.ChosenFolder;
        if (string.IsNullOrWhiteSpace(folder) && !settings.AskForDestination)
        {
            folder = GetDefaultDestinationFolder(log.IsSeries, settings);
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new InvalidOperationException(settings.AskForDestination
                ? "Choose an existing destination folder before confirming the move."
                : "Configure a valid default library root or choose a destination folder before confirming.");
        }

        // If the folder is a language subfolder whose parent root exists, create the language subfolder automatically.
        var parentRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        if (!Directory.Exists(folder) && (string.IsNullOrWhiteSpace(parentRoot) || !Directory.Exists(parentRoot)))
        {
            throw new InvalidOperationException($"The destination folder does not exist: '{folder}'.");
        }

        var effectiveFolder = ResolveLanguageDestinationFolder(folder, log.CategoryName, settings);

        log.ChosenFolder = effectiveFolder;
        log.Status = ImportStatuses.Approved;
        log.Stage = ImportStages.Approved;
        log.ErrorDetails = null;
        await db.SaveChangesAsync(ct);

        return await ExecuteApprovedImportAsync(db, log, useTmdb: true, ct);
    }

    /// <summary>Skips a file; if already auto-imported into the library, returns it to the Watch Folder first.</summary>
    public async Task SkipAsync(int logId, string? reason = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var log = await db.MetadataImportLogs.FirstOrDefaultAsync(l => l.Id == logId, ct)
                  ?? throw new InvalidOperationException("The selected import no longer exists.");

        if (log.Status == ImportStatuses.Imported &&
            !string.IsNullOrWhiteSpace(log.TargetPath) &&
            !string.IsNullOrWhiteSpace(log.SourcePath) &&
            File.Exists(log.TargetPath))
        {
            // Revert an auto-moved file back to the Watch Folder when explicitly skipped.
            Directory.CreateDirectory(Path.GetDirectoryName(log.SourcePath)!);
            File.Move(log.TargetPath, log.SourcePath, overwrite: true);

            var mediaFile = await db.MediaFiles.FirstOrDefaultAsync(f => f.FilePath == log.TargetPath, ct);
            if (mediaFile is not null)
            {
                db.MediaFiles.Remove(mediaFile);
            }

            log.TargetPath = null;
            log.Stage = ImportStages.Detected;
        }
        else if (log.Status != ImportStatuses.Matched &&
                 log.Status != ImportStatuses.Pending &&
                 log.Status != ImportStatuses.NoMatch)
        {
            throw new InvalidOperationException($"Only waiting imports can be skipped (current status: {ImportStatusLabels.For(log.Status)}).");
        }

        log.Status = ImportStatuses.Rejected;
        log.ErrorDetails = string.IsNullOrWhiteSpace(reason) ? "Skipped by the operator." : reason;
        await db.SaveChangesAsync(ct);
        _logger?.Info($"Import skipped: '{log.SourceFile}'.");
    }

    /// <summary>Executes (or resumes) an approved import. The log must be in Status=Approved.</summary>
    private async Task<ImportOutcome> ExecuteApprovedImportAsync(
        DigitalCenterDbContext db,
        MetadataImportLog log,
        bool useTmdb,
        CancellationToken ct,
        TmdbTitleMetadata? preloadedMetadata = null)
    {
        var settings = _settings.Load();
        var isSeries = log.IsSeries;
        var sourcePath = log.SourcePath ?? string.Empty;
        var baseFolder = log.ChosenFolder
                         ?? GetDefaultDestinationFolder(isSeries, settings)
                         ?? throw new InvalidOperationException("No destination folder is known for this import.");

        var resuming = log.Stage is ImportStages.MoveStarted or ImportStages.MovedToLibrary or ImportStages.RegisterStarted
                       && !string.IsNullOrEmpty(log.TargetPath);

        string destinationFolder;
        if (resuming)
        {
            destinationFolder = Path.GetDirectoryName(log.TargetPath!) ?? baseFolder;
        }
        else
        {
            var parentDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseFolder)));
            if (!Directory.Exists(baseFolder) && (string.IsNullOrWhiteSpace(parentDir) || !Directory.Exists(parentDir)))
            {
                const string message = "The destination folder no longer exists. Choose a destination again.";
                log.Status = ImportStatuses.Matched;
                log.ErrorDetails = message;
                await db.SaveChangesAsync(ct);
                return new ImportOutcome(false, null, null, message);
            }

            destinationFolder = settings.AutoOrganizeByLanguage && !string.IsNullOrWhiteSpace(log.CategoryName)
                ? PathPlanner.PlanLanguageFolderPath(baseFolder, log.CategoryName)
                : Path.GetFullPath(baseFolder);

            Directory.CreateDirectory(destinationFolder);
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
        var (titleId, posterPath, metadata) = await UpsertTitleAsync(db, log, useTmdb, ct, preloadedMetadata);

        // ---------- 2. Plan and record the destination ----------
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

        if (!IsPathInsideFolder(destinationFolder, targetPath))
        {
            throw new InvalidOperationException($"The planned destination is outside the chosen folder: '{targetPath}'.");
        }

        log.ChosenFolder = destinationFolder;
        log.TargetPath = targetPath;
        log.Stage = ImportStages.MoveStarted;
        await db.SaveChangesAsync(ct);

        // ---------- 3. Move the file (copy → verify → rename → delete source) ----------
        ReportStatus($"Moving file to {Path.GetFileName(destinationFolder)}: {Path.GetFileName(sourcePath)}");
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
        await db.SaveChangesAsync(ct);

        // ---------- 4. Register the file (idempotent) & commit Imported status immediately ----------
        await RegisterFileAsync(db, log, targetPath, isSeries, titleId, ct);

        log.Status = ImportStatuses.Imported;
        log.Stage = ImportStages.Imported;
        log.MediaTitleId = titleId;
        await db.SaveChangesAsync(ct);
        NotifyActivityChanged();

        // ---------- 5. Artwork (best effort; runs after Imported is committed so slow/failed downloads never leave status stuck on "Moving…") ----------
        await TryCacheArtworkAsync(db, log, titleId, posterPath, metadata, ct);

        _logger?.Info($"Import complete: '{log.SourceFile}' → '{targetPath}' (title {titleId}).");
        return new ImportOutcome(true, titleId, targetPath, moveResult.Message ?? "Imported successfully.");
    }

    /// <summary>Creates or reuses title/season/episode rows and applies TMDb metadata when requested.</summary>
    private async Task<(int TitleId, string? PosterPath, TmdbTitleMetadata? Metadata)> UpsertTitleAsync(
        DigitalCenterDbContext db,
        MetadataImportLog log,
        bool useTmdb,
        CancellationToken ct,
        TmdbTitleMetadata? preloadedMetadata = null)
    {
        var kind = log.IsSeries ? MediaKind.Series : MediaKind.Movie;

        MediaTitle? title = null;
        if (log.TmdbId.HasValue)
        {
            title = await db.MediaTitles.FirstOrDefaultAsync(t => t.TmdbId == log.TmdbId.Value, ct);
        }

        title ??= await FindExistingTitleAsync(db, kind, log.ParsedTitle ?? string.Empty, log.ParsedYear, ct);

        TmdbTitleMetadata? metadata = preloadedMetadata;
        string? posterPath = metadata?.PosterPath;
        if (metadata is null && useTmdb && log.TmdbId.HasValue)
        {
            metadata = log.IsSeries
                ? await _tmdb.GetSeriesAsync(log.TmdbId.Value, ct)
                : await _tmdb.GetMovieAsync(log.TmdbId.Value, ct);

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

        var detectedLanguage = LanguageResolver.Resolve(
            metadata?.OriginalLanguage ?? log.CategoryName,
            log.SourceFile,
            metadata?.Countries);
        var languageName = LanguageResolver.ResolveLanguageName(
            metadata?.OriginalLanguage ?? log.CategoryName,
            log.SourceFile,
            metadata?.Countries);
        var collectionName = !log.IsSeries
            ? CollectionResolver.Resolve(metadata?.CollectionName, metadata?.Title ?? log.ParsedTitle, log.SourceFile)
            : null;
        log.CategoryName = !string.IsNullOrWhiteSpace(collectionName) ? collectionName : detectedLanguage;

        if (title is null)
        {
            title = new MediaTitle
            {
                Type = kind,
                Title = log.ParsedTitle ?? "Unknown",
                ReleaseYear = log.ParsedYear,
                OriginalLanguage = languageName
            };
            db.MediaTitles.Add(title);
            await db.SaveChangesAsync(ct);
        }

        title.TmdbId ??= log.TmdbId;
        if (string.IsNullOrWhiteSpace(title.OriginalLanguage))
        {
            title.OriginalLanguage = languageName;
        }

        if (metadata is not null)
        {
            log.MetadataSource = "TMDb";
            MetadataMerge.Apply(db, title, metadata);
            if (string.IsNullOrWhiteSpace(title.OriginalLanguage))
            {
                title.OriginalLanguage = languageName;
            }
            await db.SaveChangesAsync(ct);
        }
        else if (log.MetadataSource is null or "WatchFolder")
        {
            log.MetadataSource = "TMDb match (offline)";
        }

        // Link the regional language category and any movie collection category to the title.
        var categoriesToLink = new List<string>();
        if (!string.IsNullOrWhiteSpace(detectedLanguage))
        {
            categoriesToLink.Add(detectedLanguage);
        }
        if (!string.IsNullOrWhiteSpace(collectionName))
        {
            categoriesToLink.Add($"Collection: {collectionName}");
        }

        foreach (var catName in categoriesToLink.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var category = await db.Categories.FirstOrDefaultAsync(c => c.Type == kind && c.Name == catName, ct);
            if (category is null)
            {
                category = new Category { Type = kind, Name = catName };
                db.Categories.Add(category);
                await db.SaveChangesAsync(ct);
            }

            var hasCategory = await db.TitleCategories.AnyAsync(
                tc => tc.MediaTitleId == title.Id && tc.CategoryId == category.Id, ct);
            if (!hasCategory)
            {
                db.TitleCategories.Add(new TitleCategory { MediaTitleId = title.Id, CategoryId = category.Id });
                await db.SaveChangesAsync(ct);
            }
        }

        log.MediaTitleId = title.Id;
        await db.SaveChangesAsync(ct);
        return (title.Id, posterPath, metadata);
    }

    private static async Task<MediaTitle?> FindExistingTitleAsync(
        DigitalCenterDbContext db,
        MediaKind kind,
        string titleName,
        int? year,
        CancellationToken ct)
    {
        var candidates = await db.MediaTitles
            .Where(t => t.Type == kind && t.Title == titleName)
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

    private async Task<MoveOutcome> MoveSourceIntoLibraryAsync(
        MetadataImportLog log,
        string sourcePath,
        string targetPath,
        CancellationToken ct)
    {
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(targetDirectory))
        {
            return new MoveOutcome(false, "The planned destination path has no directory part.", null);
        }

        if (File.Exists(targetPath))
        {
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

    private async Task RegisterFileAsync(
        DigitalCenterDbContext db,
        MetadataImportLog log,
        string targetPath,
        bool isSeries,
        int titleId,
        CancellationToken ct)
    {
        log.Stage = ImportStages.RegisterStarted;
        await db.SaveChangesAsync(ct);

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
            file.MovieTitleId = titleId;
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<Episode> FindOrCreateEpisodeAsync(
        DigitalCenterDbContext db,
        int titleId,
        int seasonNumber,
        int episodeNumber,
        CancellationToken ct)
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

    private async Task TryCacheArtworkAsync(
        DigitalCenterDbContext db,
        MetadataImportLog log,
        int titleId,
        string? posterPath,
        TmdbTitleMetadata? metadata,
        CancellationToken ct)
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Artwork caching failed for title {titleId}: {ex.Message}");
        }
    }

    private async Task CacheProfilesAsync(
        DigitalCenterDbContext db,
        int titleId,
        TmdbTitleMetadata metadata,
        CancellationToken ct)
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
                continue;
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // One missing or timed-out profile never blocks the rest.
            }
        }
    }

    /// <summary>
    /// Startup recovery: completes or safely fails imports that were approved
    /// but interrupted. Deterministic: no network access during recovery.
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
