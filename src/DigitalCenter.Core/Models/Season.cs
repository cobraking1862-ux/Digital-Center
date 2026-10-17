namespace DigitalCenter.Core.Models;

/// <summary>A season of a series. Unique per (SeriesDetailsId, SeasonNumber).</summary>
public class Season
{
    public int Id { get; set; }

    public int SeriesDetailsId { get; set; }

    public int SeasonNumber { get; set; }

    public SeriesDetails SeriesDetails { get; set; } = null!;

    public ICollection<Episode> Episodes { get; set; } = new List<Episode>();
}
