namespace DigitalCenter.Core.Models;

/// <summary>
/// Kind of catalogued media. Stored as the strings "Movie" / "Series" in the
/// database and guarded by a CHECK constraint (CK_MediaTitles_Type).
/// </summary>
public enum MediaKind
{
    Movie,
    Series
}
