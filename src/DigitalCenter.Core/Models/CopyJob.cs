namespace DigitalCenter.Core.Models;

/// <summary>Well-known values of <see cref="MetadataImportLog.Status"/>.</summary>
public static class ImportStatuses
{
    /// <summary>File detected; automatic metadata matching has not finished yet.</summary>
    public const string Pending = "Pending";
    /// <summary>A reliable TMDb match was found; the file waits for a destination choice.</summary>
    public const string Matched = "Matched";
    /// <summary>No reliable match was found; the file stays in the Watch Folder untouched.</summary>
    public const string NoMatch = "NoMatch";
    /// <summary>The file type is not a supported media file; the file stays in place.</summary>
    public const string Unsupported = "Unsupported";
    public const string Approved = "Approved";
    /// <summary>Skipped (or rejected) by the operator; the file stays in place.</summary>
    public const string Rejected = "Rejected";
    public const string Imported = "Imported";
    public const string Failed = "Failed";
}

/// <summary>Friendly labels for <see cref="ImportStatuses"/> used by the Management activity list.</summary>
public static class ImportStatusLabels
{
    public static string For(string status) => status switch
    {
        ImportStatuses.Pending => "Detected / matching",
        ImportStatuses.Matched => "Awaiting destination",
        ImportStatuses.NoMatch => "No Match",
        ImportStatuses.Unsupported => "Unsupported",
        ImportStatuses.Approved => "Moving…",
        ImportStatuses.Rejected => "Skipped",
        ImportStatuses.Imported => "Imported",
        ImportStatuses.Failed => "Failed",
        _ => status
    };
}

/// <summary>
/// Well-known values of <see cref="MetadataImportLog.Stage"/>. The stage
/// records how far an approved import progressed so an interrupted import
/// (application crash, power loss) can be detected and completed or safely
/// failed at the next startup. Moving a file is not transactional with the
/// database, hence the explicit checkpoints.
/// </summary>
public static class ImportStages
{
    public const string Detected = "Detected";
    public const string Approved = "Approved";
    public const string MoveStarted = "MoveStarted";
    public const string MovedToLibrary = "MovedToLibrary";
    public const string RegisterStarted = "RegisterStarted";
    public const string Imported = "Imported";
}

/// <summary>Well-known values of <see cref="CopyJob.Status"/>.</summary>
public static class CopyJobStatuses
{
    public const string Queued = "Queued";
    public const string Copying = "Copying";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string Skipped = "Skipped";
}

/// <summary>
/// A pending or executed copy of one media file to a USB destination. This
/// table is both the operator-visible queue and the persisted copy log. The
/// target is stored relative to the chosen destination root because the USB
/// drive letter is only known when a copy run actually starts.
/// </summary>
public class CopyJob
{
    public int Id { get; set; }

    /// <summary>Catalog file this job was created from (kept for traceability; may be removed later).</summary>
    public int? MediaFileId { get; set; }

    /// <summary>Full source path snapshot taken when the job was queued.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Target path relative to the destination root (mirrors the library layout).</summary>
    public string RelativeTargetPath { get; set; } = string.Empty;

    /// <summary>File name only, for compact display.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>"Title (Year)" of the owning title, for display.</summary>
    public string TitleDisplay { get; set; } = string.Empty;

    public long FileSize { get; set; }

    /// <summary>Queued / Copying / Completed / Failed / Cancelled / Skipped.</summary>
    public string Status { get; set; } = CopyJobStatuses.Queued;

    public string? ErrorMessage { get; set; }

    /// <summary>Extra outcome note (e.g. "target already existed and was skipped").</summary>
    public string? ConflictNote { get; set; }

    /// <summary>Destination root used during the last copy attempt (informational).</summary>
    public string? DestinationRoot { get; set; }

    public long? BytesCopied { get; set; }

    /// <summary>True when the copy finished and the size (and optionally hash) was verified.</summary>
    public bool Verified { get; set; }

    public DateTime QueuedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public MediaFile? MediaFile { get; set; }
}
