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

    /// <summary>
    /// Every marker format the Watch Folder must understand. These are the
    /// regressions behind "Reacher.S01.E01.mkv was imported as a movie":
    /// the separator between the season and the episode is normalized, the
    /// "Season N Episode M" wording is understood without a folder hint, and
    /// the season/episode tokens are cut out of the title that becomes the
    /// search query.
    /// </summary>
    [Theory]
    [InlineData("Reacher.S01.E01.mkv", "Reacher", null, 1, 1)]
    [InlineData("Reacher.S01E01.mkv", "Reacher", null, 1, 1)]
    [InlineData("Reacher Season 1 Episode 1.mkv", "Reacher", null, 1, 1)]
    [InlineData("Reacher.1x01.mkv", "Reacher", null, 1, 1)]
    [InlineData("Reacher S01 E01.mkv", "Reacher", null, 1, 1)]
    [InlineData("Reacher.S01_E01.mkv", "Reacher", null, 1, 1)]
    [InlineData("Reacher.S01-E01.mkv", "Reacher", null, 1, 1)]
    [InlineData("Reacher.s01.e01.mkv", "Reacher", null, 1, 1)]
    [InlineData("reacher.s1.e1.mkv", "reacher", null, 1, 1)]
    [InlineData("Reacher.Season.01.Episode.05.mkv", "Reacher", null, 1, 5)]
    [InlineData("Reacher.Season.1.Episode.1.mkv", "Reacher", null, 1, 1)]
    [InlineData("Reacher Season 1 Episode 1 1080p.mkv", "Reacher", null, 1, 1)]
    [InlineData("Reacher.Season.1.Episode.1.720p.HDTV.x264.mkv", "Reacher", null, 1, 1)]
    [InlineData("Reacher.Season.2.E05.mkv", "Reacher", null, 2, 5)]
    [InlineData("Reacher - S01E01 - Pilot.mkv", "Reacher", null, 1, 1)]
    [InlineData("Reacher.S01.E01.1080p.WEB-DL.x264-NTb.mkv", "Reacher", null, 1, 1)]
    [InlineData("Reacher.S01.E01.2160p.UHD.BluRay.DV.x265.mkv", "Reacher", null, 1, 1)]
    [InlineData("Reacher.S01.E01 (2).mkv", "Reacher", null, 1, 1)]
    [InlineData("Reacher.S01.E01.E02.mkv", "Reacher", null, 1, 1)]
    [InlineData("Reacher.2022.S01.E01.mkv", "Reacher", 2022, 1, 1)]
    [InlineData("Reacher (2022) S01E01.mkv", "Reacher", 2022, 1, 1)]
    [InlineData("Reacher_2022_S01E01_720p_HDTV_x264.mkv", "Reacher", 2022, 1, 1)]
    [InlineData("The.Last.Of.Us.S01.E03.2160p.mkv", "The Last Of Us", null, 1, 3)]
    [InlineData("House.of.the.Dragon.1x08.HDTV.mkv", "House of the Dragon", null, 1, 8)]
    [InlineData("The.Office.US.S03.E12.720p.mkv", "The Office US", null, 3, 12)]
    [InlineData("Show.S01.E01.[tmdb-tv-1399].mkv", "Show", null, 1, 1)]
    public void Parses_Every_Supported_Series_Marker_Format(
        string fileName, string expectedTitle, int? expectedYear, int season, int episode)
    {
        var parsed = _parser.Parse(fileName);

        Assert.True(parsed.IsSeries, $"{fileName} must be classified as a TV episode, never as a movie.");
        Assert.Equal(expectedTitle, parsed.Title);
        Assert.Equal(expectedYear, parsed.Year);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
        Assert.True(parsed.Confident);
    }

    [Fact]
    public void Series_Marker_Never_Survives_Into_The_Search_Title()
    {
        foreach (var fileName in new[] { "Reacher.S01.E01.mkv", "Reacher Season 1 Episode 1.mkv", "Reacher.1x01.mkv" })
        {
            var parsed = _parser.Parse(fileName);

            Assert.Equal("Reacher", parsed.Title);
            Assert.DoesNotContain("S01", parsed.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("E01", parsed.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Season", parsed.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Episode", parsed.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("1x01", parsed.Title, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Series years follow exactly the rules the movie path already used: a
    /// trailing 19xx/20xx word is the release year, a year in the middle of a
    /// title is part of the title, and a name that is ambiguous ("Blade Runner
    /// 2049" with no other year) resolves the same way in both paths.
    /// </summary>
    [Fact]
    public void Series_Year_Handling_Matches_The_Movie_Rules()
    {
        var midYear = _parser.Parse("Blade.Runner.2049.1997.S01.E01.mkv");
        Assert.True(midYear.IsSeries);
        Assert.Equal("Blade Runner 2049", midYear.Title);
        Assert.Equal(1997, midYear.Year);

        var trailing = _parser.Parse("Blade.Runner.2049.S01.E01.mkv");
        Assert.Equal("Blade Runner", trailing.Title);
        Assert.Equal(2049, trailing.Year);

        var releaseYear = _parser.Parse("Reacher.2022.S01.E01.mkv");
        Assert.Equal("Reacher", releaseYear.Title);
        Assert.Equal(2022, releaseYear.Year);

        var noYear = _parser.Parse("Reacher.S01.E01.mkv");
        Assert.Equal("Reacher", noYear.Title);
        Assert.Null(noYear.Year);
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
