namespace DigitalCenter.Core.Models;

public class Person
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>TMDb person id, when the person came from an online import.</summary>
    public long? TmdbPersonId { get; set; }

    /// <summary>Relative path (under the artwork cache) of the profile image, if any.</summary>
    public string? ProfilePath { get; set; }

    public ICollection<TitlePerson> Titles { get; set; } = new List<TitlePerson>();
}

/// <summary>Links a person to a title with a role such as "Actor" or "Director".</summary>
public class TitlePerson
{
    public int MediaTitleId { get; set; }

    public int PersonId { get; set; }

    public string Role { get; set; } = "Actor";

    /// <summary>Character name for actors; job description for crew ("Director", "Screenplay").</summary>
    public string? Character { get; set; }

    public MediaTitle MediaTitle { get; set; } = null!;

    public Person Person { get; set; } = null!;
}
