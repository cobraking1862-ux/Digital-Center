namespace DigitalCenter.Core.Models;

public class Country
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public ICollection<TitleCountry> Titles { get; set; } = new List<TitleCountry>();
}

public class TitleCountry
{
    public int MediaTitleId { get; set; }

    public int CountryId { get; set; }

    public MediaTitle MediaTitle { get; set; } = null!;

    public Country Country { get; set; } = null!;
}
