namespace DigitalCenter.Core.Models;

/// <summary>
/// An operator-facing organizational label for the library folder structure
/// (e.g. Hollywood, Bollywood, English). A category belongs to either movie
/// or series organization; it is not a genre or a country.
/// </summary>
public class Category
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Whether this category organizes movies or series.</summary>
    public MediaKind Type { get; set; }

    public ICollection<TitleCategory> Titles { get; set; } = new List<TitleCategory>();
}

public class TitleCategory
{
    public int MediaTitleId { get; set; }

    public int CategoryId { get; set; }

    public MediaTitle MediaTitle { get; set; } = null!;

    public Category Category { get; set; } = null!;
}
