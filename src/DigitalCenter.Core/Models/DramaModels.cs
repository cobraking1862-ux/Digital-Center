namespace DigitalCenter.Core.Models;

public static class DramaStatuses
{
    public const string Ongoing = "Ongoing";
    public const string Completed = "Completed";
    public const string OnHold = "On Hold";

    public static readonly IReadOnlyList<string> All =
    [
        Ongoing,
        Completed,
        OnHold
    ];
}

public static class WatchFolderIssueTypes
{
    public const string Movie = "Movie";
    public const string Drama = "Drama";
}

public static class WatchFolderIssueCategories
{
    public const string Failed = "Failed";
    public const string Interrupted = "Interrupted";
    public const string LowConfidence = "Low Confidence";
}

/// <summary>A drama / series catalog entry enriched from TMDb and stored locally.</summary>
public sealed class DramaSeries
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? Overview { get; set; }
    public string? Language { get; set; }
    public string? Channel { get; set; }
    public string Status { get; set; } = DramaStatuses.Ongoing;
    public bool StatusLocked { get; set; }
    public bool TitleLocked { get; set; }
    public int? ExpectedEpisodeCount { get; set; }
    public long? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public double? VoteAverage { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? Genres { get; set; }
    public DateTime? LastScannedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Aggregated stats populated on query
    public int EpisodeCount { get; set; }
    public int AvailableEpisodeCount { get; set; }
    public int SeasonCount { get; set; }
    public long TotalSizeBytes { get; set; }
}

/// <summary>Cast member associated with a drama (populated from TMDb TV credits).</summary>
public sealed class DramaCastMember
{
    public int Id { get; set; }
    public int DramaId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Character { get; set; }
    public string? ProfilePath { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>One local video file representing an episode of a drama, grouped by SeasonNumber.</summary>
public sealed class DramaEpisode
{
    public int Id { get; set; }
    public int DramaId { get; set; }
    public string DramaTitle { get; set; } = string.Empty;
    public string? DramaPosterPath { get; set; }
    public int SeasonNumber { get; set; } = 1;
    public int EpisodeNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string? YouTubeVideoId { get; set; }
    public int CopyCount { get; set; }
    public DateTime? LastCopiedAt { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public bool ExistsOnDisk { get; set; } = true;
    public bool IsInUsbCart { get; set; }
}

/// <summary>Persistent log row for a drama episode imported by the Drama Watch Folder.</summary>
public sealed class DramaWatchHistoryEntry
{
    public int Id { get; set; }
    public int? DramaId { get; set; }
    public string DramaTitle { get; set; } = string.Empty;
    public int SeasonNumber { get; set; } = 1;
    public int EpisodeNumber { get; set; }
    public string SourceFileName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Status { get; set; } = "Imported";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>Unified failure, interrupted-move, or low-confidence issue for Maintenance.</summary>
public sealed class WatchFolderIssue
{
    public int Id { get; set; }
    public int? MovieImportLogId { get; set; }
    public string MediaType { get; set; } = WatchFolderIssueTypes.Movie;
    public string Category { get; set; } = WatchFolderIssueCategories.Failed;
    public string FileName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public sealed record DramaScanResult(
    int DramasFound,
    int DramasCreated,
    int DramasMatchedTmdb,
    int EpisodesRegistered,
    int EpisodesUpdated,
    int EpisodesRelocated,
    TimeSpan Duration);

public sealed record DramaReanalyzeResult(
    int DramasAnalyzed,
    int EpisodesReparsed,
    int EpisodesRelocated,
    int MissingEpisodesFound,
    int TmdbRefreshed,
    TimeSpan Duration);
