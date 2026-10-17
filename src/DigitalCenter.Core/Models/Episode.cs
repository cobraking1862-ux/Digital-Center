namespace DigitalCenter.Core.Models;

/// <summary>
/// An episode of a season. Episode files attach here; movie files attach to a
/// MediaTitle instead. The mutual exclusivity is enforced by the
/// CK_MediaFiles_Association check constraint.
/// </summary>
public class Episode
{
    public int Id { get; set; }

    public int SeasonId { get; set; }

    public int EpisodeNumber { get; set; }

    public string? Title { get; set; }

    public long? TmdbId { get; set; }

    public Season Season { get; set; } = null!;

    public ICollection<MediaFile> Files { get; set; } = new List<MediaFile>();
}
