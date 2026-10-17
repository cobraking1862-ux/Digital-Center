namespace DigitalCenter.Core.Models;

/// <summary>
/// Series-specific extension of a <see cref="MediaTitle"/> whose
/// <see cref="MediaTitle.Type"/> is <see cref="MediaKind.Series"/>.
/// 1:1 with the parent title; the primary key is the parent id.
/// </summary>
public class SeriesDetails
{
    public int MediaTitleId { get; set; }

    /// <summary>e.g. "Returning Series", "Ended", "Canceled".</summary>
    public string? AiringStatus { get; set; }

    /// <summary>Number of seasons known at the last metadata import, if any.</summary>
    public int? TotalSeasons { get; set; }

    public MediaTitle MediaTitle { get; set; } = null!;

    public ICollection<Season> Seasons { get; set; } = new List<Season>();
}
