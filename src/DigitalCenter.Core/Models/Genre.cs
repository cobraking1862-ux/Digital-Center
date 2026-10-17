namespace DigitalCenter.Core.Models;

public class Genre
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public ICollection<TitleGenre> Titles { get; set; } = new List<TitleGenre>();
}

public class TitleGenre
{
    public int MediaTitleId { get; set; }

    public int GenreId { get; set; }

    public MediaTitle MediaTitle { get; set; } = null!;

    public Genre Genre { get; set; } = null!;
}
