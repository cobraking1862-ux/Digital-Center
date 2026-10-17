namespace DigitalCenter.Core.Models;

/// <summary>
/// A media file on disk. Exactly one of <see cref="MovieTitleId"/> or
/// <see cref="EpisodeId"/> must be set (CK_MediaFiles_Association).
/// The path is unique case-insensitively (COLLATE NOCASE) to prevent duplicate
/// registration on Windows.
/// </summary>
public class MediaFile
{
    public int Id { get; set; }

    /// <summary>Set when this file belongs to a movie.</summary>
    public int? MovieTitleId { get; set; }

    /// <summary>Set when this file belongs to a series episode.</summary>
    public int? EpisodeId { get; set; }

    public string FilePath { get; set; } = string.Empty;

    public long FileSize { get; set; }

    public DateTime AddedAt { get; set; }

    public MediaTitle? MovieTitle { get; set; }

    public Episode? Episode { get; set; }
}
