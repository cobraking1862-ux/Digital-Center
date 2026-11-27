using System.Net;
using DigitalCenter.Core.Services;
using Xunit;

namespace DigitalCenter.Tests;

public class DownloadProviderTests
{
    private static DownloadRequest MovieRequest(string url, string quality = "1080p") =>
        new(
            SourceId: "site-1",
            SourceName: "BollyFlix",
            SourcePageUrl: url,
            ImdbId: "tt0241527",
            MovieTitle: "Harry Potter and the Sorcerer's Stone (2001)",
            Year: 2001,
            PreferredQuality: quality);

    // page1.txt — search results page excerpt
    private const string SearchPageHtml = """
        <div class="post-cards">
            <article id="post-63101" class="latestPost excerpt">
                <a href="https://new1.bollyflix.tours/harry-potter-2001-dual-audios-hindi-english-movie/" title="Download Harry Potter and the Sorcerer’s Stone (2001) {Hindi-English} 480p | 720p | 1080p | 2160p BluRay ESub" class="post-image post-image-left">
                    <div class="featured-thumbnail"><img src="https://bflixcdn.xyz/wp-content/uploads/2020/04/Harry-Potter-and-the-Sorcerers-Stone-2001.jpg"></div>
                </a>
                <header><h2 class="title front-view-title"><a href="https://new1.bollyflix.tours/harry-potter-2001-dual-audios-hindi-english-movie/">Download Harry Potter and the Sorcerer’s Stone (2001) {Hindi-English} 480p | 720p | 1080p | 2160p BluRay ESub</a></h2></header>
            </article>
        </div>
        """;

    // page2.txt — movie page with fastdlserver Google Drive links + linksmod mirrors
    private const string MoviePageHtml = """
        <div class="thecontent clearfix">
            <h5 style="text-align: center;"><span style="color: #13BF3C;">Download Harry Potter (2001) Dual Audio {Hindi-English} 480p [500MB]</span></h5>
            <p style="text-align: center;">
                <a class="dl" href="https://dl.fastdlserver.site/?id=cTN5S2RWMU00TEExOSs5bGE5cVBNT3dlcDN4U01NRzFyb210S3haSFNRbFM2R2ZTb2hyRktNWXp2V0tNUnhMTg==&amp;type=file"> Google Drive</a>
                <a class="dl" href="https://linksmod.top/view/ZCYa2eZP34"> Download Links </a>
            </p>
            <h5 style="text-align: center;"><span style="color: #13BF3C;">Download Harry Potter (2001) Dual Audio {Hindi-English} 720p [1.3GB]</span></h5>
            <p style="text-align: center;">
                <a class="dl" href="https://dl.fastdlserver.site/?id=RXk2aHk2TlZ4c0UvM0RMaWZKS0p6UlBlVE90Q2k4MUpJZE5XVWtNelNjdFRvaFFBTldRMENTdHRJTzZrYzRGNg==&amp;type=file"> Google Drive</a>
                <a class="dl" href="https://linksmod.top/view/km34yQ8QDf"> Download Links </a>
            </p>
            <h5 style="text-align: center;"><span style="color: #13BF3C;">Download Harry Potter (2001) Dual Audio {Hindi-English} 1080p [2.8GB]</span></h5>
            <p style="text-align: center;">
                <a class="dl" href="https://dl.fastdlserver.site/?id=RUt5Y1AyeHh1MUluN29zUUdQYUNtWVZlTWQrVlJDcFZKY045d0JpZW5qam9KRHBKZzFJeUx3azAvRFVFRjRZeQ==&amp;type=file"> Google Drive</a>
                <a class="dl" href="https://linksmod.top/view/FQwH47AM3X"> Download Links </a>
            </p>
            <h5 style="text-align: center;"><span style="color: #13BF3C;">Download Harry Potter (2001) Dual Audio {Hindi-English} 2160p [16GB]</span></h5>
            <p style="text-align: center;">
                <a class="dl" href="https://dl.fastdlserver.site/?id=WG5nb3lNZ1NtNmlUd0NQQ2JLVGl0S0xjcWNGS200N0tTUyttQjBsWENBK25UeDJ4S3FiZ3lqckhNeGJGS2JYTA==&amp;type=file"> Google Drive</a>
            </p>
        </div>
        """;

    // page3.txt — gdflix file page with /cloud/ and /login anchors
    private const string FilePageHtml = """
        <div class="text-center">
            <a href="/login?ref=/file/iif5zjN7hPXqDaXdAxaq" class="btn btn-primary"> Login To DL [10GBPS] </a><br>
            <a href="https://busycdn.instadl.rest/ebf5265f4487540498c290321ae1b8376e632706837675dbac41cb9b034503fbcc012d047e6a1606502ed91d8c3c1f2c82f1aeb3e12516d0cf5695541b654d38898ea1e4b2a1430728c881c71684a6c65d0d10a4c81ef5eadf0e534e32d4b521fe5939b69889aeebdead55d592b188e2a9374fa192fd0daae7fde661c0e28ed1::b091b6e0887144fcc52b78ec1f505834?bytes=1390209917" target="_blank" class="btn btn-danger"> Instant DL [10GBPS]</a>
            <a href="/cloud/1795759148/iif5zjN7hPXqDaXdAxaq" target="_blank" class="btn btn-secondary"> FAST CLOUD / ZIPDISK</a>
            <a href="https://filesgram.xyz/?start=3_2918y1v9&amp;bot=gdflix30_bot" target="_blank" class="btn btn-info"> Telegram File</a>
            <a href="https://goflix.sbs/en/mirror/99e04e9b8d08a3cf3467da9c258cf5e0" target="_blank" class="btn btn-outline-info"> GoFile [Multiup] </a>
        </div>
        """;

    // page4.txt — cloud page with the final direct file links
    private const string CloudPageHtml = """
        <div class="text-center">
            <a href="https://cloud-dl.bj1zcnlye7n.workers.dev/7ea01b00180d8c54e59db92346b8ab4a9c0acca491a68ba6aa00db51b8096d32965b38761355727f5b04dc0c57d45bfb941c1037f57475248a9343112b0ef9e42f46f1d4d6d91c642fffaff50f8cca2d6a25154ce7fa9b35ca51190b368df6f2d27a3337888f26fa09f5d7d7712b2f10c2292fddd42e1e3d50960efc2269a1e2c1b55e56288a97f7b8d4d14d9733684e3282a6f7ee25dbff6e45b02c5920c672d8929728628aadd212386b9219ea0e38::dc9a09997571b3eee7c27d9bf8f4c1fe/Harry%20Potter%20and%20the%20Sorcerers%20Stone%20(2001)%20Dual%20Audio%20{Hindi-English}%20720p%20BluRay%201.3GB%20ESub%20[BollYFlix].mkv.zip?bytes=1390209917" target="_blank" class="btn btn-success"> Cloud Resume Download </a>
            <br>
            <a href="https://quick.cloudpaglu.site/01f7fa7874f4bd0cf91f9947160e1378199bcc84d20f1de4d29404f1f8910668::c87fffacf65d19a523dd02066ab368de/d32de6b8b4e7983e7c8c822b625062a00c924e2793e0392b9296480bea5dedd981c01f9058e6fabca08adb5e07c46755e846d300e052eec711b900f2d77101549ac9155e6b6158d26b38dfa6d1cf29dbe25faee0d8d56d4807a49676b76e9cb8d0e09d48a9261aa84ab8ae5d2a3e8fd2ed8e3abf6609466233193ae2cad9c4a4441409b09f2609e9dc77d35c4286d3fca5807048438493cbf56cb0cc68d8e720a174dce9de4fc64d219b75dd8617f0c4::7d559060fad716baf674834c322a0fab/Harry%20Potter%20and%20the%20Sorcerers%20Stone%20(2001)%20Dual%20Audio%20{Hindi-English}%20720p%20BluRay%201.3GB%20ESub%20[BollYFlix].mkv.zip?bytes=1390209917" target="_blank" class="btn btn-danger"> Quick Download (Ready) </a>
        </div>
        """;

    private const string SearchUrl = "https://new1.bollyflix.tours/?s=harry+potter+2001";
    private const string MovieUrl = "https://new1.bollyflix.tours/harry-potter-2001-dual-audios-hindi-english-movie/";
    private const string FastDl1080 = "https://dl.fastdlserver.site/?id=RUt5Y1AyeHh1MUluN29zUUdQYUNtWVZlTWQrVlJDcFZKY045d0JpZW5qam9KRHBKZzFJeUx3azAvRFVFRjRZeQ==&type=file";
    private const string FilePageUrl = "https://new10.gdflix.io/file/iif5zjN7hPXqDaXdAxaq";
    private const string CloudUrl = "https://new10.gdflix.io/cloud/1795759148/iif5zjN7hPXqDaXdAxaq";

    /// <summary>Builds the mock chain: search → movie → fastdlserver(302) → gdflix file → cloud → direct file.</summary>
    private static MockHttpMessageHandler BuildRealWorldChain()
    {
        return new MockHttpMessageHandler()
            .Map(SearchUrl, new MockHttpMessageHandler.Response(200, SearchPageHtml))
            .Map(MovieUrl, new MockHttpMessageHandler.Response(200, MoviePageHtml))
            .MapHost("dl.fastdlserver.site", new MockHttpMessageHandler.Response(302, string.Empty, Location: FilePageUrl))
            .Map(FilePageUrl, new MockHttpMessageHandler.Response(200, FilePageHtml))
            .Map(CloudUrl, new MockHttpMessageHandler.Response(200, CloudPageHtml))
            .MapHost("linksmod.top", new MockHttpMessageHandler.Response(200, "<html>mirror list</html>"));
    }

    private static MultiStepLinkDownloadWorkflow CreateWorkflow(MockHttpMessageHandler handler) =>
        new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) });

    [Fact]
    public void RootPathWithQuery_IsNot_NavigationTaxonomy()
    {
        // dl.fastdlserver.site/?id=... must NOT be classified as navigation (regression: it was silently dropped).
        var uri = new Uri("https://dl.fastdlserver.site/?id=abc&type=file");
        Assert.False(WorkflowHttpHelpers.IsNavigationTaxonomyUri(uri));
    }

    [Fact]
    public void CloudLink_IsRewritten_ToNew10GdflixHost()
    {
        // /cloud/<id>/<token> resolved against a mirror host must be pinned to new10.gdflix.io.
        var baseUri = new Uri("https://new10.gdflix.io/file/iif5zjN7hPXqDaXdAxaq");
        var cloud = new Uri(baseUri, "/cloud/1795759148/iif5zjN7hPXqDaXdAxaq");
        var rewritten = WorkflowHttpHelpers.TryRewriteCloudLink(cloud);
        Assert.NotNull(rewritten);
        Assert.Equal("new10.gdflix.io", rewritten.Host);
        Assert.Equal("/cloud/1795759148/iif5zjN7hPXqDaXdAxaq", rewritten.AbsolutePath);
    }

    [Fact]
    public async Task MultiStepWorkflow_FollowsFastDlRedirectToCloudDirectFile_1080p()
    {
        var handler = BuildRealWorldChain();
        var workflow = CreateWorkflow(handler);

        var outcome = await workflow.ResolveAsync(MovieRequest(SearchUrl, "1080p"));

        Assert.True(outcome.IsSuccess, outcome.Message);
        Assert.NotNull(outcome.Download);
        Assert.Equal("1080p", outcome.Download!.Quality);

        // The fastdlserver 1080p link must have been fetched (not linksmod.top).
        Assert.Contains(handler.RequestedUris, u => u.Host == "dl.fastdlserver.site");
        Assert.DoesNotContain(handler.RequestedUris, u => u.Host == "linksmod.top");

        // The cloud page must have been fetched on new10.gdflix.io.
        Assert.Contains(handler.RequestedUris, u =>
            u.Host == "new10.gdflix.io" && u.AbsolutePath.StartsWith("/cloud/"));

        // Final URL is the direct .mkv.zip file from page4.
        Assert.EndsWith(".mkv.zip", outcome.Download.DownloadUrl.AbsolutePath);
        Assert.Contains("cloud-dl", outcome.Download.DownloadUrl.Host);
    }

    [Fact]
    public async Task MultiStepWorkflow_DownwardFallback_Picks720pWhen1080pMissing()
    {
        // Same chain but only the 720p fastdlserver link present on the movie page.
        var moviePage720Only = """
            <h5>Download Harry Potter (2001) Dual Audio {Hindi-English} 720p [1.3GB]</h5>
            <p><a class="dl" href="https://dl.fastdlserver.site/?id=RXk2aHk2TlZ4c0UvM0RMaWZKS0p6UlBlVE90Q2k4MUpJZE5XVWtNelNjdFRvaFFBTldRMENTdHRJTzZrYzRGNg==&amp;type=file"> Google Drive</a></p>
            """;
        var handler = new MockHttpMessageHandler()
            .Map(MovieUrl, new MockHttpMessageHandler.Response(200, moviePage720Only))
            .MapHost("dl.fastdlserver.site", new MockHttpMessageHandler.Response(302, string.Empty, Location: FilePageUrl))
            .Map(FilePageUrl, new MockHttpMessageHandler.Response(200, FilePageHtml))
            .Map(CloudUrl, new MockHttpMessageHandler.Response(200, CloudPageHtml));
        var workflow = CreateWorkflow(handler);

        var outcome = await workflow.ResolveAsync(MovieRequest(MovieUrl, "1080p"));

        Assert.True(outcome.IsSuccess, outcome.Message);
        Assert.Equal("720p", outcome.Download!.Quality);
    }

    [Fact]
    public async Task MultiStepWorkflow_BailsToBrowser_OnChallengePage()
    {
        var handler = new MockHttpMessageHandler()
            .Map(MovieUrl, new MockHttpMessageHandler.Response(403, "Just a moment..."));
        var workflow = CreateWorkflow(handler);

        var outcome = await workflow.ResolveAsync(MovieRequest(MovieUrl));

        Assert.Equal(DownloadResolveStatus.NeedsBrowser, outcome.Status);
    }

    [Fact]
    public async Task DirectLinkWorkflow_ResolvesDirectFileAnchor()
    {
        var handler = new MockHttpMessageHandler()
            .Map(MovieUrl, new MockHttpMessageHandler.Response(200, CloudPageHtml));
        var workflow = new DirectLinkDownloadWorkflow(new HttpClient(handler));

        var outcome = await workflow.ResolveAsync(MovieRequest(MovieUrl, "720p"));

        Assert.True(outcome.IsSuccess, outcome.Message);
        Assert.EndsWith(".mkv.zip", outcome.Download!.DownloadUrl.AbsolutePath);
    }

    [Fact]
    public void QualityMatcher_FallsBackDownward()
    {
        var options = new[]
        {
            new DownloadOption("480p", new Uri("https://example.com/480")),
            new DownloadOption("720p", new Uri("https://example.com/720")),
            new DownloadOption("2160p", new Uri("https://example.com/2160"))
        };

        var best = QualityPreferenceMatcher.SelectBestOption(options, "1080p");

        Assert.NotNull(best);
        Assert.Equal("720p", best!.QualityLabel);
    }

    [Fact]
    public void IdmArguments_AreNonSilent()
    {
        var args = IdmLauncherService.BuildIdmArguments(
            new Uri("https://cloud-dl.example.dev/file/movie.mkv.zip"),
            @"D:\Movies\Watch",
            "movie.mkv.zip");

        Assert.Contains("/d \"https://cloud-dl.example.dev/file/movie.mkv.zip\"", args);
        Assert.Contains("/p \"D:\\Movies\\Watch\"", args);
        Assert.Contains("/f \"movie.mkv.zip\"", args);
        Assert.DoesNotContain("/n", args);
    }
}
