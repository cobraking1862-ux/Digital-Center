using DigitalCenter.Core.Services;
using Xunit;

namespace DigitalCenter.Tests;

/// <summary>
/// Filename parsing for the highest-risk patterns: movie titles with years,
/// series season/episode markers, ambiguous and malformed names.
/// </summary>
public class FilenameParserTests
{
    private readonly FilenameParser _parser = new();

    // ---------- Movies ----------

    [Theory]
    [InlineData("Inception (2010).mkv", "Inception", 2010)]
    [InlineData("Interstellar.2014.1080p.mkv", "Interstellar", 2014)]
    [InlineData("The.Dark.Knight.2008.720p.BluRay.x264-NeZu.mkv", "The Dark Knight", 2008)]
    [InlineData("Blade Runner 2049 (2017).mkv", "Blade Runner 2049", 2017)]
    [InlineData("Blade.Runner.2049.2017.1080p.mkv", "Blade Runner 2049", 2017)]
    [InlineData("2012.2009.1080p.mkv", "2012", 2009)]
    [InlineData("Parasite.2019.KOREAN.1080p.WEB-DL.x264.mkv", "Parasite", 2019)]
    [InlineData("1917 (2019).mp4", "1917", 2019)]
    public void Parses_Movie_Title_And_Year(string fileName, string expectedTitle, int expectedYear)
    {
        var parsed = _parser.Parse(fileName);

        Assert.False(parsed.IsSeries);
        Assert.Equal(expectedTitle, parsed.Title);
        Assert.Equal(expectedYear, parsed.Year);
        Assert.True(parsed.Confident);
    }

    [Fact]
    public void Parses_Movie_Without_Year_As_Title_Only()
    {
        var parsed = _parser.Parse("Movie.Without.Year.mkv");

        Assert.Equal("Movie Without Year", parsed.Title);
        Assert.Null(parsed.Year);
        Assert.True(parsed.Confident);
    }

    [Fact]
    public void Cuts_Quality_Noise_When_No_Year_Present()
    {
        var parsed = _parser.Parse("Some.Great.Film.1080p.WEBRip.x264.mkv");

        Assert.Equal("Some Great Film", parsed.Title);
        Assert.Null(parsed.Year);
    }

    [Fact]
    public void Malformed_Quality_Only_Name_Is_Not_Confident()
    {
        var parsed = _parser.Parse("1080p.mkv");

        Assert.False(parsed.IsSeries);
        Assert.True(string.IsNullOrEmpty(parsed.Title) || !parsed.Confident);
    }

    [Fact]
    public void Plain_Name_Is_Cleaned_And_Kept()
    {
        var parsed = _parser.Parse("random_video_file.mkv");

        Assert.Equal("random video file", parsed.Title);
        Assert.Null(parsed.Year);
    }

    // ---------- Series ----------

    [Theory]
    [InlineData("Example Series S01E02.mkv", "Example Series", 1, 2)]
    [InlineData("Example.Series.1x02.mkv", "Example Series", 1, 2)]
    [InlineData("Vikings.S05E08.720p.WEB-DL.mkv", "Vikings", 5, 8)]
    [InlineData("example series s01e02 720p.mkv", "example series", 1, 2)]
    [InlineData("Community S01E01 - Pilot.mp4", "Community", 1, 1)]
    [InlineData("Show.S02E10E11.mkv", "Show", 2, 10)]
    public void Parses_Series_Season_And_Episode(string fileName, string expectedTitle, int season, int episode)
    {
        var parsed = _parser.Parse(fileName);

        Assert.True(parsed.IsSeries);
        Assert.Equal(expectedTitle, parsed.Title);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
    }

    [Fact]
    public void Uses_Season_Folder_Hint_For_Episode_Only_Names()
    {
        var parsed = _parser.Parse("Show - 05.mkv", "/library/Series/English/Example/Season 02");

        Assert.True(parsed.IsSeries);
        Assert.Equal("Show", parsed.Title);
        Assert.Equal(2, parsed.Season);
        Assert.Equal(5, parsed.Episode);
    }

    [Fact]
    public void Uses_Season_Folder_Hint_For_Bare_Number_Names()
    {
        var parsed = _parser.Parse("Show 05.mkv", "/library/Series/English/Example/S03");

        Assert.True(parsed.IsSeries);
        Assert.Equal(3, parsed.Season);
        Assert.Equal(5, parsed.Episode);
    }

    [Fact]
    public void Uses_Season_Folder_Hint_For_E_Prefixed_Names()
    {
        var parsed = _parser.Parse("Show.E05.mkv", "/library/Series/English/Example/Season 1");

        Assert.True(parsed.IsSeries);
        Assert.Equal(1, parsed.Season);
        Assert.Equal(5, parsed.Episode);
    }

    [Fact]
    public void Series_Detection_Does_Not_Treat_Resolution_As_Marker()
    {
        var parsed = _parser.Parse("My.Movie.1920x1080.mkv");

        // "1920x1080" cannot match the NxNN series pattern (word boundary + digit count).
        Assert.False(parsed.IsSeries);
        Assert.Equal("My Movie", parsed.Title);
    }

    [Fact]
    public void Series_Without_Title_Before_Marker_Is_Failed()
    {
        var parsed = _parser.Parse("S01E02.mkv");

        Assert.True(parsed.IsSeries);
        Assert.Equal(string.Empty, parsed.Title);
        Assert.False(parsed.Confident);
    }
}
