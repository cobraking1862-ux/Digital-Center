namespace DigitalCenter.Core.Models;

/// <summary>
/// One metadata-import attempt (Watch Folder workflow). The operator's
/// approval/rejection and any failure details are recorded here so an
/// interrupted import can be detected and recovered, and so every decision
/// (approved, rejected, failed) stays visible to the operator.
/// </summary>
public class MetadataImportLog
{
    public int Id { get; set; }

    /// <summary>Source file name or import identifier.</summary>
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>Full path of the source file at detection time. The file stays in the Watch Folder until approval.</summary>
    public string? SourcePath { get; set; }

    /// <summary>Pending / Approved / Rejected / Imported / Failed.</summary>
    public string Status { get; set; } = "Pending";

    /// <summary>How far an approved import progressed (see <see cref="ImportStages"/>). Null until approved.</summary>
    public string? Stage { get; set; }

    /// <summary>"TMDb", "Filename" (offline import), "Scan", ...</summary>
    public string? MetadataSource { get; set; }

    public long? TmdbId { get; set; }

    public long? FileSize { get; set; }

    /// <summary>True when the parsed name looked like a series episode (S01E02 / 1x02 / folder season hint).</summary>
    public bool IsSeries { get; set; }

    public string? ParsedTitle { get; set; }

    public int? ParsedYear { get; set; }

    public int? ParsedSeason { get; set; }

    public int? ParsedEpisode { get; set; }

    /// <summary>Category chosen by the operator at approval time (decides the destination folder).</summary>
    public string? CategoryName { get; set; }

    /// <summary>Planned or actual destination path inside the library.</summary>
    public string? TargetPath { get; set; }

    /// <summary>Title that received the file, so the UI can link to details.</summary>
    public int? MediaTitleId { get; set; }

    /// <summary>Human-readable note for edge cases (e.g. source could not be removed after a successful copy).</summary>
    public string? Note { get; set; }

    /// <summary>Confidence of the automatic metadata match (0-1), when one was computed.</summary>
    public double? Confidence { get; set; }

    /// <summary>Folder the operator chose as the import destination (before confirmation).</summary>
    public string? ChosenFolder { get; set; }

    /// <summary>Title that received the file (null until the import completes).</summary>
    public MediaTitle? MediaTitle { get; set; }

    public DateTime Timestamp { get; set; }

    public string? ErrorDetails { get; set; }
}
