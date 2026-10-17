using System.Net;
using System.Text;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Tmdb;
using Xunit;

namespace DigitalCenter.Tests;

public sealed class TmdbProviderTests
{
    private static TmdbMetadataProvider CreateProvider(TmdbOptions options, HttpMessageHandler handler) =>
        new(options, logger: null, httpClient: new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) });

    [Fact]
    public async Task UnconfiguredProvider_Throws_On_Search()
    {
        using var provider = CreateProvider(new TmdbOptions { ReadAccessToken = null }, new StubHandler(_ => throw new AssertionError("no request expected")));
        Assert.False(provider.IsConfigured);
        await Assert.ThrowsAsync<TmdbNotConfiguredException>(() => provider.SearchMoviesAsync("test", null));
    }

    [Fact]
    public async Task SearchMovies_Parses_Canned_Response()
    {
        var json = """
        {
          "page": 1,
          "results": [
            { "id": 27205, "title": "Inception", "overview": "A thief who steals corporate secrets.",
              "release_date": "2010-07-16", "poster_path": "/inception.jpg" },
            { "id": 99999, "title": "Inception: The Cobol Job", "overview": "",
              "release_date": null, "poster_path": null }
          ],
          "total_results": 2
        }
        """;

        using var provider = CreateProvider(
            new TmdbOptions { ReadAccessToken = "test-token" },
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));

        var results = await provider.SearchMoviesAsync("Inception", 2010);

        Assert.Equal(2, results.Count);
        Assert.Equal(27205, results[0].Id);
        Assert.Equal("Inception", results[0].Title);
        Assert.Equal(2010, results[0].Year);
        Assert.Equal("/inception.jpg", results[0].PosterPath);
        Assert.False(results[0].IsSeries);
        Assert.Null(results[1].Year);
    }

    [Fact]
    public async Task SearchSeries_Parses_Canned_Response()
    {
        var json = """
        {
          "results": [
            { "id": 1396, "name": "Breaking Bad", "overview": "Walter White.",
              "first_air_date": "2008-01-20", "poster_path": "/bb.jpg" }
          ]
        }
        """;

        using var provider = CreateProvider(
            new TmdbOptions { ReadAccessToken = "test-token" },
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));

        var results = await provider.SearchSeriesAsync("Breaking Bad", null);

        var single = Assert.Single(results);
        Assert.Equal(1396, single.Id);
        Assert.Equal("Breaking Bad", single.Title);
        Assert.Equal(2008, single.Year);
        Assert.True(single.IsSeries);
    }

    [Fact]
    public async Task GetMovie_Maps_Credits_Genres_Countries()
    {
        var json = """
        {
          "id": 27205,
          "title": "Inception",
          "overview": "Cobb steals secrets.",
          "release_date": "2010-07-16",
          "poster_path": "/inc.jpg",
          "genres": [{ "id": 28, "name": "Action" }, { "id": 878, "name": "Science Fiction" }],
          "production_countries": [{ "iso_3166_1": "US", "name": "United States of America" }],
          "credits": {
            "cast": [
              { "name": "Leonardo DiCaprio", "character": "Cobb" },
              { "name": "Joseph Gordon-Levitt", "character": "Arthur" }
            ],
            "crew": [
              { "name": "Christopher Nolan", "job": "Director" },
              { "name": "Someone Else", "job": "Producer" }
            ]
          }
        }
        """;

        using var provider = CreateProvider(
            new TmdbOptions { ReadAccessToken = "test-token" },
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));

        var metadata = await provider.GetMovieAsync(27205);

        Assert.NotNull(metadata);
        Assert.Equal("Inception", metadata!.Title);
        Assert.Equal(2010, metadata.Year);
        Assert.Equal(2, metadata.Genres.Count);
        Assert.Contains("Action", metadata.Genres);
        Assert.Contains("United States of America", metadata.Countries);
        Assert.Contains(metadata.People, p => p.Name == "Leonardo DiCaprio" && p.Role == "Actor" && p.Character == "Cobb");
        Assert.Contains(metadata.People, p => p.Name == "Christopher Nolan" && p.Role == "Director");
        Assert.DoesNotContain(metadata.People, p => p.Role == "Producer");
    }

    [Fact]
    public async Task GetSeries_Maps_Seasons_And_Episodes()
    {
        var seriesJson = """
        {
          "id": 1396,
          "name": "Breaking Bad",
          "first_air_date": "2008-01-20",
          "status": "Ended",
          "overview": "Chemistry teacher.",
          "genres": [{ "id": 18, "name": "Drama" }],
          "created_by": [{ "name": "Vince Gilligan" }],
          "seasons": [
            { "season_number": 1, "episode_count": 7 },
            { "season_number": 2, "episode_count": 13 }
          ],
          "credits": { "cast": [{ "name": "Bryan Cranston" }], "crew": [] }
        }
        """;
        var season1Json = """
        {
          "season_number": 1,
          "episodes": [
            { "episode_number": 1, "name": "Pilot" },
            { "episode_number": 2, "name": "Cat's in the Bag..." }
          ]
        }
        """;

        using var provider = CreateProvider(
            new TmdbOptions { ReadAccessToken = "test-token" },
            new StubHandler(request =>
            {
                var url = request.RequestUri!.ToString();
                var body = url.Contains("/season/1") ? season1Json : seriesJson;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }));

        var metadata = await provider.GetSeriesAsync(1396);

        Assert.NotNull(metadata);
        Assert.Equal("Ended", metadata!.AiringStatus);
        Assert.Equal(2, metadata.Seasons.Count);
        Assert.Equal(2, metadata.Seasons[0].Episodes.Count);
        Assert.Equal("Pilot", metadata.Seasons[0].Episodes[0].Title);
        Assert.Contains(metadata.People, p => p.Name == "Vince Gilligan" && p.Role == "Creator");
    }

    [Fact]
    public async Task FindByImdbId_Parses_Movie_And_Tv_Results()
    {
        var findJson = """
        {
          "movie_results": [{ "id": 27205, "title": "Inception" }],
          "tv_results": [],
          "person_results": []
        }
        """;

        using var provider = CreateProvider(
            new TmdbOptions { ReadAccessToken = "test-token" },
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(findJson, Encoding.UTF8, "application/json") }));

        var movieHit = await provider.FindByImdbIdAsync("tt1375666");
        Assert.Equal((27205L, false), movieHit);

        var tvJson = """
        {
          "movie_results": [],
          "tv_results": [{ "id": 1396, "name": "Breaking Bad" }]
        }
        """;

        using var tvProvider = CreateProvider(
            new TmdbOptions { ReadAccessToken = "test-token" },
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tvJson, Encoding.UTF8, "application/json") }));

        var tvHit = await tvProvider.FindByImdbIdAsync("tt0903747");
        Assert.Equal((1396L, true), tvHit);

        var emptyJson = "{ \"movie_results\": [], \"tv_results\": [] }";
        using var emptyProvider = CreateProvider(
            new TmdbOptions { ReadAccessToken = "test-token" },
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(emptyJson, Encoding.UTF8, "application/json") }));

        Assert.Null(await emptyProvider.FindByImdbIdAsync("tt0000000"));
    }

    [Fact]
    public async Task HttpError_Is_Reported_False_By_TestConnection_And_Throws_On_Search()
    {
        using var provider = CreateProvider(
            new TmdbOptions { ReadAccessToken = "test-token" },
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        // TestConnection is a soft probe: it reports "not reachable" instead of throwing.
        Assert.False(await provider.TestConnectionAsync());

        // Search surfaces the HTTP failure as TmdbRequestException.
        var exception = await Assert.ThrowsAsync<TmdbRequestException>(() => provider.SearchMoviesAsync("test", null));
        Assert.Contains("401", exception.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class AssertionError(string message) : Exception(message);
}
