using System.Net;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Services;
using Xunit;

namespace DigitalCenter.Tests;

public sealed class BigOneDownloadWorkflowTests
{
    private const string SearchUrl = "https://new1.bollyflix.tours/?s=sample+title";
    private const string MovieUrl = "https://new1.bollyflix.tours/sample-title/";
    private const string FastDl1080Url = "https://dl.fastdlserver.site/?id=SAFE_1080&type=file";
    private const string FileHostUrl = "https://new10.gdflix.io/file/SAFE_FILE_ID";
    private const string CloudUrl = "https://new10.gdflix.io/cloud/SAFE_FILE_ID/SAFE_TOKEN";

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BigOne", name));

    private static DownloadRequest MovieRequest(string sourceUrl, string quality = "1080p") =>
        new(
            SourceId: DownloadWorkflowIds.BigOneSite,
            SourceName: "BollyFlix",
            SourcePageUrl: sourceUrl,
            ImdbId: "tt0000000",
            MovieTitle: "Sample Title (2026)",
            Year: 2026,
            PreferredQuality: quality);

    private static MockHttpMessageHandler CreateCompleteChain(
        MockHttpMessageHandler.Response? finalCloudResponse = null,
        string? fastDlRedirect = null)
    {
        var handler = new MockHttpMessageHandler()
            .Map(SearchUrl, new MockHttpMessageHandler.Response(200, Fixture("page1.html")))
            .Map(MovieUrl, new MockHttpMessageHandler.Response(200, Fixture("page2.html")))
            .Map(FastDl1080Url, new MockHttpMessageHandler.Response(
                302, string.Empty, Location: fastDlRedirect ?? FileHostUrl))
            .Map(FileHostUrl, new MockHttpMessageHandler.Response(200, Fixture("page3.html")))
            .Map(CloudUrl, new MockHttpMessageHandler.Response(200, Fixture("page4.html")))
            .MapHost("cloud-dl.bj1zcnlye7n.workers.dev",
                finalCloudResponse ?? new MockHttpMessageHandler.Response(
                    200, string.Empty, "application/zip", FileName: "SampleTitle.1080p.mkv.zip", IsAttachment: true))
            .MapHost("quick.cloudpaglu.site",
                new MockHttpMessageHandler.Response(404, "not found"));
        return handler;
    }

    [Fact]
    public void Page1_RecognizesTheBollyFlixListingAndTitleCard()
    {
        var listing = Fixture("page1.html");

        Assert.True(BigOneDownloadWorkflow.TryExtractTitlePage(
            listing, new Uri(SearchUrl), out var titleUri));
        Assert.Equal(MovieUrl, titleUri.AbsoluteUri);
    }

    [Fact]
    public void Page2_RecognizesAllFourIndependentQualitySections()
    {
        var options = BigOneDownloadWorkflow.ParseQualityOptions(Fixture("page2.html"), new Uri(MovieUrl));

        Assert.Equal(4, options.Count);
        Assert.Equal(new[] { "480p", "720p", "1080p", "2160p" }, options.Select(option => option.QualityLabel));
        Assert.All(options, option => Assert.Equal("dl.fastdlserver.site", option.TargetUrl.Host));
        Assert.All(options, option => Assert.Equal("file", Uri.UnescapeDataString(option.TargetUrl.Query).Split('&').Last().Split('=').Last()));
        Assert.DoesNotContain(options, option => option.TargetUrl.Host.Equals("linksmod.top", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Page2_DoesNotAttachLinksmodOrOtherQualitySectionsToFastDlOptions()
    {
        var html = """
            <div class="post-single-content"><div class="thecontent clearfix">
              <h5>Download A 480p</h5>
              <a class="dl" href="https://dl.fastdlserver.site/?id=SAFE_A&amp;type=file">Google Drive</a>
              <a class="dl" href="https://linksmod.top/view/SAFE_A">Download Links</a>
              <h5>Download A 720p</h5>
              <a class="dl" href="https://dl.fastdlserver.site/?id=SAFE_B&amp;type=file">Google Drive</a>
            </div></div>
            """;

        var options = BigOneDownloadWorkflow.ParseQualityOptions(html, new Uri(MovieUrl));

        Assert.Equal(2, options.Count);
        Assert.Equal(new[] { "480p", "720p" }, options.Select(option => option.QualityLabel));
        Assert.DoesNotContain(options, option => option.TargetUrl.Host == "linksmod.top");
    }

    [Fact]
    public void Page2_LinksmodOnlyDoesNotBecomeAFastDlCandidate()
    {
        var html = """
            <div class="post-single-content"><div class="thecontent clearfix">
              <h5>Download A 1080p</h5>
              <a class="dl" href="https://linksmod.top/view/SAFE_MIRROR">Download Links</a>
            </div></div>
            """;

        var options = BigOneDownloadWorkflow.ParseQualityOptions(html, new Uri(MovieUrl));

        Assert.Empty(options);
    }

    [Fact]
    public void QualityMatcher_PreservesPreferredQualityAndFallsBackDownward()
    {
        var options = BigOneDownloadWorkflow.ParseQualityOptions(Fixture("page2.html"), new Uri(MovieUrl));

        Assert.Equal("1080p", QualityPreferenceMatcher.SelectBestOption(options, "1080p")?.QualityLabel);
        var lowerOnly = options.Where(option => option.QualityLabel is "480p" or "720p").ToList();
        Assert.Equal("720p", QualityPreferenceMatcher.SelectBestOption(lowerOnly, "1080p")?.QualityLabel);
    }

    [Fact]
    public void ResolveCloudPath_AcceptsOnlyRootRelativePathOnTheConfiguredOrigin()
    {
        var origin = new Uri("https://new10.gdflix.io/");

        var resolved = BigOneDownloadWorkflow.ResolveCloudPath("/cloud/SAFE_ID/SAFE_TOKEN?x=1", origin);

        Assert.NotNull(resolved);
        Assert.Equal("new10.gdflix.io", resolved!.Host);
        Assert.Equal("/cloud/SAFE_ID/SAFE_TOKEN", resolved.AbsolutePath);
        Assert.Equal("?x=1", resolved.Query);
        Assert.Null(BigOneDownloadWorkflow.ResolveCloudPath("https://evil.example.test/cloud/SAFE", origin));
        Assert.Null(BigOneDownloadWorkflow.ResolveCloudPath("//evil.example.test/cloud/SAFE", origin));
        Assert.Null(BigOneDownloadWorkflow.ResolveCloudPath("/cloud/SAFE", new Uri("https://evil.example.test/")));
        Assert.Null(BigOneDownloadWorkflow.ResolveCloudPath("/cloud/SAFE", new Uri("http://new10.gdflix.io/")));
        Assert.Null(BigOneDownloadWorkflow.ResolveCloudPath("/cloud/SAFE", new Uri("https://new10.gdflix.io:8443/")));
        Assert.Null(BigOneDownloadWorkflow.ResolveCloudPath("/cloud/SAFE", new Uri("https://localhost/")));
    }

    [Fact]
    public void BigOneOptions_RejectsUnapprovedOriginsAndUnsafeUrls()
    {
        using var http = new HttpClient(new MockHttpMessageHandler());

        Assert.Throws<ArgumentException>(() =>
            new BigOneDownloadWorkflow(http, new BigOneOptions("https://evil.example.test/")));
        Assert.Throws<ArgumentException>(() =>
            new BigOneDownloadWorkflow(http, new BigOneOptions("https://user@new10.gdflix.io/")));
        Assert.Throws<ArgumentException>(() =>
            new BigOneDownloadWorkflow(http, new BigOneOptions("https://new10.gdflix.io:8443/")));
    }

    [Fact]
    public async Task Workflow_ValidatesEveryStageAndOnlyHandsVerifiedDestinationToIdm()
    {
        var handler = CreateCompleteChain();
        using var http = new HttpClient(handler);
        var workflow = new BigOneDownloadWorkflow(http);
        var messages = new List<string>();
        var progress = new Progress<string>(messages.Add);

        var outcome = await workflow.ResolveAsync(MovieRequest(SearchUrl, "1080p"), progress);

        Assert.True(outcome.IsSuccess, outcome.Message);
        Assert.NotNull(outcome.Download);
        Assert.Equal("1080p", outcome.Download!.Quality);
        Assert.Equal("cloud-dl.bj1zcnlye7n.workers.dev", outcome.Download.DownloadUrl.Host);
        Assert.Equal("SampleTitle.1080p.mkv.zip", outcome.Download.FileName);
        Assert.Contains(handler.RequestedUris, uri => uri.AbsoluteUri == SearchUrl);
        Assert.Contains(handler.RequestedUris, uri => uri.AbsoluteUri == MovieUrl);
        Assert.Contains(handler.RequestedUris, uri => uri.AbsoluteUri == FastDl1080Url);
        Assert.Contains(handler.RequestedUris, uri => uri.AbsoluteUri == FileHostUrl);
        Assert.Contains(handler.RequestedUris, uri => uri.AbsoluteUri == CloudUrl);
        Assert.DoesNotContain(handler.RequestedUris, uri => uri.Host == "linksmod.top");
        Assert.DoesNotContain(handler.RequestedUris, uri => uri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handler.RequestedUris, uri => uri.Host == "busycdn.instadl.rest");
        Assert.DoesNotContain(handler.RequestedUris, uri => uri.Host == "filesgram.example.test");
        Assert.DoesNotContain(handler.RequestedUris, uri => uri.Host == "goflix.example.test");
    }

    [Fact]
    public async Task Workflow_UsesDownwardQualityFallbackAcrossBigOneStages()
    {
        var qualityLimitedPage = """
            <div class="post-single-content"><div class="thecontent clearfix">
              <h5>Download Sample Title 480p</h5>
              <a class="dl" href="https://dl.fastdlserver.site/?id=SAFE_480&amp;type=file">Google Drive</a>
              <h5>Download Sample Title 720p</h5>
              <a class="dl" href="https://dl.fastdlserver.site/?id=SAFE_720&amp;type=file">Google Drive</a>
            </div></div>
            """;
        var handler = new MockHttpMessageHandler()
            .Map(MovieUrl, new MockHttpMessageHandler.Response(200, qualityLimitedPage))
            .Map("https://dl.fastdlserver.site/?id=SAFE_720&type=file",
                new MockHttpMessageHandler.Response(302, string.Empty, Location: FileHostUrl))
            .Map(FileHostUrl, new MockHttpMessageHandler.Response(200, Fixture("page3.html")))
            .Map(CloudUrl, new MockHttpMessageHandler.Response(200, Fixture("page4.html")))
            .MapHost("cloud-dl.bj1zcnlye7n.workers.dev",
                new MockHttpMessageHandler.Response(200, string.Empty, "application/zip", FileName: "SampleTitle.1080p.mkv.zip", IsAttachment: true));
        using var http = new HttpClient(handler);
        var workflow = new BigOneDownloadWorkflow(http);

        var outcome = await workflow.ResolveAsync(MovieRequest(MovieUrl, "1080p"));

        Assert.True(outcome.IsSuccess, outcome.Message);
        Assert.Equal("720p", outcome.Download!.Quality);
        Assert.Contains(handler.RequestedUris, uri => uri.Query.Contains("SAFE_720", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.RequestedUris, uri => uri.Query.Contains("SAFE_480", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Workflow_NeverFetchesLinksmodWhenFastDlIsMissing()
    {
        var linksmodOnly = """
            <div class="post-single-content"><div class="thecontent clearfix">
              <h5>Download Sample Title 1080p</h5>
              <a class="dl" href="https://linksmod.top/view/SAFE_MIRROR">Download Links</a>
            </div></div>
            """;
        var handler = new MockHttpMessageHandler()
            .Map(MovieUrl, new MockHttpMessageHandler.Response(200, linksmodOnly));
        using var http = new HttpClient(handler);
        var workflow = new BigOneDownloadWorkflow(http);

        var outcome = await workflow.ResolveAsync(MovieRequest(MovieUrl));

        Assert.Equal(DownloadResolveStatus.NeedsBrowser, outcome.Status);
        Assert.Equal(MovieUrl, outcome.FallbackBrowserUrl?.AbsoluteUri);
        Assert.DoesNotContain(handler.RequestedUris, uri => uri.Host == "linksmod.top");
    }

    [Fact]
    public async Task Workflow_RejectsUnexpectedRedirectBeforeRequestingItsDestination()
    {
        var handler = CreateCompleteChain(
            fastDlRedirect: "https://unexpected.example.test/file/SAFE");
        using var http = new HttpClient(handler);
        var workflow = new BigOneDownloadWorkflow(http);

        var outcome = await workflow.ResolveAsync(MovieRequest(SearchUrl));

        Assert.Equal(DownloadResolveStatus.NeedsBrowser, outcome.Status);
        Assert.DoesNotContain(handler.RequestedUris, uri => uri.Host == "unexpected.example.test");
    }

    [Fact]
    public async Task Workflow_DoesNotTreatCloudLandingPageOrZipNameAsVerifiedDownload()
    {
        var handler = CreateCompleteChain(
            finalCloudResponse: new MockHttpMessageHandler.Response(200, "<html>Still an HTML landing page</html>", "text/html"));
        using var http = new HttpClient(handler);
        var workflow = new BigOneDownloadWorkflow(http);

        var outcome = await workflow.ResolveAsync(MovieRequest(SearchUrl));

        Assert.Equal(DownloadResolveStatus.NeedsBrowser, outcome.Status);
        Assert.Null(outcome.Download);
        Assert.Equal(MovieUrl, outcome.FallbackBrowserUrl?.AbsoluteUri);
    }

    [Fact]
    public async Task Workflow_ReturnsBrowserFallbackWhenFileHostNeedsInteractiveAccess()
    {
        var handler = new MockHttpMessageHandler()
            .Map(MovieUrl, new MockHttpMessageHandler.Response(200, Fixture("page2.html")))
            .Map(FastDl1080Url, new MockHttpMessageHandler.Response(302, string.Empty, Location: FileHostUrl))
            .Map(FileHostUrl, new MockHttpMessageHandler.Response(200, "<html>CAPTCHA: verify you are human</html>"));
        using var http = new HttpClient(handler);
        var workflow = new BigOneDownloadWorkflow(http);

        var outcome = await workflow.ResolveAsync(MovieRequest(MovieUrl));

        Assert.Equal(DownloadResolveStatus.NeedsBrowser, outcome.Status);
        Assert.Equal(MovieUrl, outcome.FallbackBrowserUrl?.AbsoluteUri);
        Assert.DoesNotContain(handler.RequestedUris, uri => uri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Workflow_RejectsUnexpectedFinalRedirect()
    {
        var handler = CreateCompleteChain()
            .MapHost("cloud-dl.bj1zcnlye7n.workers.dev",
                new MockHttpMessageHandler.Response(302, string.Empty, Location: "https://127.0.0.1/private"));
        using var http = new HttpClient(handler);
        var workflow = new BigOneDownloadWorkflow(http);

        var outcome = await workflow.ResolveAsync(MovieRequest(SearchUrl));

        Assert.Equal(DownloadResolveStatus.NeedsBrowser, outcome.Status);
        Assert.DoesNotContain(handler.RequestedUris, uri => IPAddress.TryParse(uri.Host, out _));
    }

    [Fact]
    public async Task Dispatcher_RejectsBigOneForUnrelatedSitesAndStaleAssignments()
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler);
        var dispatcher = new DownloadWorkflowDispatcher(new BigOneDownloadWorkflow(http));

        var outcome = await dispatcher.ResolveForSiteAsync(
            "builtin-imdb", DownloadWorkflowIds.BigOne, MovieRequest(MovieUrl));

        Assert.Equal(DownloadResolveStatus.NotFound, outcome.Status);
        Assert.Empty(handler.RequestedUris);
    }

    [Fact]
    public async Task Dispatcher_DoesNotPretendBigOIsImplemented()
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler);
        var dispatcher = new DownloadWorkflowDispatcher(new BigOneDownloadWorkflow(http));

        var outcome = await dispatcher.ResolveForSiteAsync(
            "another-site", "big-o", MovieRequest(MovieUrl));

        Assert.Equal(DownloadResolveStatus.NotFound, outcome.Status);
        Assert.Empty(handler.RequestedUris);
    }

    [Fact]
    public async Task Dispatcher_RejectsNonBollyFlixSourceEvenWhenSiteIdMatches()
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler);
        var dispatcher = new DownloadWorkflowDispatcher(new BigOneDownloadWorkflow(http));
        var request = MovieRequest("https://example.test/sample-title/");

        var outcome = await dispatcher.ResolveForSiteAsync(
            DownloadWorkflowIds.BigOneSite, DownloadWorkflowIds.BigOne, request);

        Assert.Equal(DownloadResolveStatus.NeedsBrowser, outcome.Status);
        Assert.Empty(handler.RequestedUris);
    }

    [Fact]
    public void LookupSiteMigration_AddsStableBollyFlixEntryWithoutReplacingExistingSites()
    {
        var settings = new AppSettings
        {
            Version = 10,
            LookupSites =
            [
                new LookupSiteEntry
                {
                    Id = "custom-mkvcinemas",
                    Name = "My MKVCinemas",
                    UrlTemplate = "https://custom.example.test/?s={title}",
                    IsEnabled = false,
                    IsBuiltIn = false
                }
            ]
        };

        SettingsService.MigrateLegacy(settings);

        Assert.Equal(11, settings.Version);
        var bollyFlix = Assert.Single(settings.LookupSites.Where(site => site.Id == DownloadWorkflowIds.BigOneSite));
        Assert.Equal("BollyFlix", bollyFlix.Name);
        Assert.Equal("https://new1.bollyflix.tours/?s={imdb_id}", bollyFlix.UrlTemplate);
        Assert.True(bollyFlix.IsBuiltIn);
        Assert.True(bollyFlix.IsEnabled);
        Assert.Contains(settings.LookupSites, site => site.Id == "custom-mkvcinemas" && site.UrlTemplate == "https://custom.example.test/?s={title}");
    }

    [Fact]
    public void LookupSiteMigration_DoesNotReAddAnIntentionallyRemovedBollyFlixSite()
    {
        var settings = new AppSettings { Version = 11, LookupSites = [] };

        SettingsService.MigrateLegacy(settings);

        Assert.Empty(settings.LookupSites);
        Assert.Equal(11, settings.Version);
    }

    [Fact]
    public void DefaultLookupSites_ExposeBollyFlixWithTheStableSiteId()
    {
        var bollyFlix = Assert.Single(ImdbLookupService.GetDefaultSites().Where(site => site.Id == DownloadWorkflowIds.BigOneSite));

        Assert.Equal("https://new1.bollyflix.tours/?s={imdb_id}", bollyFlix.UrlTemplate);
        Assert.True(bollyFlix.IsBuiltIn);
    }

    [Fact]
    public void IdmArguments_PreserveTheConfirmationDialogAndAreNotSilent()
    {
        var args = IdmLauncherService.BuildIdmArguments(
            new Uri("https://downloads.example.test/sample.mkv"),
            @"D:\Movies\Watch",
            "sample.mkv");

        Assert.Contains("/d \"https://downloads.example.test/sample.mkv\"", args);
        Assert.Contains("/p \"D:\\Movies\\Watch\"", args);
        Assert.Contains("/f \"sample.mkv\"", args);
        Assert.DoesNotContain("/n", args);
    }

    [Fact]
    public void DownloadSettings_RemovesOnlyObsoleteBindingsAndPreservesUnrelatedValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "DigitalCenterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settingsPath = Path.Combine(root, "download-settings.json");
            File.WriteAllText(settingsPath, """
                {
                  "IdmExecutablePath": "C:\Tools\IDMan.exe",
                  "PreferredQuality": "720p",
                  "RouteIdmToMoviesWatchFolder": false,
                  "AutoCompleteTicketsOnImport": false,
                  "SiteWorkflowBindings": {
                    "old-direct": "direct-link",
                    "old-multi": "multi-step-link",
                    "unrelated": "custom-workflow",
                    "builtin-bollyflix": "big-one",
                    "future-bigo": "big-o"
                  }
                }
                """);
            var service = new DownloadSettingsService(new AppPaths { DataRoot = root });

            var loaded = service.Load();

            Assert.Equal("C:\Tools\IDMan.exe", loaded.IdmExecutablePath);
            Assert.Equal("720p", loaded.PreferredQuality);
            Assert.False(loaded.RouteIdmToMoviesWatchFolder);
            Assert.False(loaded.AutoCompleteTicketsOnImport);
            Assert.Equal("custom-workflow", loaded.SiteWorkflowBindings["unrelated"]);
            Assert.Equal("big-one", loaded.SiteWorkflowBindings[DownloadWorkflowIds.BigOneSite]);
            Assert.Equal("big-o", loaded.SiteWorkflowBindings["future-bigo"]);
            Assert.DoesNotContain("old-direct", loaded.SiteWorkflowBindings.Keys);
            Assert.DoesNotContain("old-multi", loaded.SiteWorkflowBindings.Keys);
            var persisted = File.ReadAllText(settingsPath);
            Assert.DoesNotContain("direct-link", persisted);
            Assert.DoesNotContain("multi-step-link", persisted);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DownloadSettings_DoesNotOverwriteMalformedSettingsFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "DigitalCenterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settingsPath = Path.Combine(root, "download-settings.json");
            const string malformed = "{ not valid json";
            File.WriteAllText(settingsPath, malformed);
            var service = new DownloadSettingsService(new AppPaths { DataRoot = root });

            var loaded = service.Load();

            Assert.Empty(loaded.SiteWorkflowBindings);
            Assert.Equal(malformed, File.ReadAllText(settingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
