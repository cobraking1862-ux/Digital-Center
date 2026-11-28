using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Services;
using Xunit;

namespace DigitalCenter.Tests;

public sealed class BigOneBrowserDownloadWorkflowTests
{
    private static readonly Uri ListingUri = new("https://new1.bollyflix.tours/?s=tt0241527");
    private static readonly Uri MovieUri = new("https://new1.bollyflix.tours/harry-potter-2001/");

    [Fact]
    public void DefaultLookupSites_IncludeBollyFlixVisibleBrowserTarget()
    {
        var site = ImdbLookupService.GetDefaultSites()
            .Single(candidate => candidate.Id == "builtin-bollyflix");

        Assert.True(site.IsEnabled);
        Assert.True(site.IsBuiltIn);
        Assert.Equal("BollyFlix", site.Name);
        Assert.Equal("https://new1.bollyflix.tours/?s={imdb_id}", site.UrlTemplate);
    }

    [Fact]
    public void SettingsMigration_AddsBollyFlixWithoutRemovingCustomLookupSites()
    {
        var root = Path.Combine(Path.GetTempPath(), "DigitalCenterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new SettingsService(new AppPaths { DataRoot = root });
            service.Save(new AppSettings
            {
                Version = 10,
                LookupSites =
                [
                    new LookupSiteEntry
                    {
                        Id = "custom-katmovie",
                        Name = "KatMovie",
                        UrlTemplate = "https://katmovie.example/?s={imdb_id}",
                        IsEnabled = true
                    }
                ]
            });

            var migrated = service.Load();

            Assert.Equal(11, migrated.Version);
            Assert.Contains(migrated.LookupSites, site => site.Id == "custom-katmovie");
            Assert.Contains(migrated.LookupSites, site => site.Id == "builtin-bollyflix");
            Assert.Equal(2, migrated.LookupSites.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DownloadSettings_MigratesOnlyLegacyBollyFlixBindingToVisibleBrowser()
    {
        var root = Path.Combine(Path.GetTempPath(), "DigitalCenterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new DownloadSettingsService(new AppPaths { DataRoot = root });
            service.SetProviderIdForSite("custom-bollyflix", DownloadWorkflowIds.MultiStepLink);
            service.SetProviderIdForSite("custom-katmovie", DownloadWorkflowIds.MultiStepLink);
            service.SetProviderIdForSite("custom-old-bollyflix", "big-one");

            var migrated = service.MigrateBollyFlixBrowserBinding(
            [
                new LookupSiteEntry
                {
                    Id = "custom-bollyflix",
                    Name = "BollyFlix",
                    UrlTemplate = "https://new1.bollyflix.tours/?s={imdb_id}"
                },
                new LookupSiteEntry
                {
                    Id = "custom-katmovie",
                    Name = "KatMovie",
                    UrlTemplate = "https://katmovie.example/?s={imdb_id}"
                },
                new LookupSiteEntry
                {
                    Id = "custom-old-bollyflix",
                    Name = "BollyFlix",
                    UrlTemplate = "https://new1.bollyflix.tours/?s={imdb_id}"
                }
            ]);

            Assert.True(migrated);
            Assert.Equal(DownloadWorkflowIds.BigOneBrowser, service.GetProviderIdForSite("custom-bollyflix"));
            Assert.Equal(DownloadWorkflowIds.MultiStepLink, service.GetProviderIdForSite("custom-katmovie"));
            Assert.Equal(DownloadWorkflowIds.BigOneBrowser, service.GetProviderIdForSite("custom-old-bollyflix"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParseTitlePageUri_ExtractsMoviePermalinkFromCapturedListingShape()
    {
        const string html = """
            <div class="post-cards">
              <article id="post-63101" class="latestPost excerpt">
                <a href="https://new1.bollyflix.tours/harry-potter-2001/" class="post-image post-image-left">
                  <div class="featured-thumbnail"><img src="/poster.jpg" alt="Harry Potter" /></div>
                </a>
                <header>
                  <h2 class="title front-view-title">
                    <a href="https://new1.bollyflix.tours/harry-potter-2001/">Harry Potter and the Sorcerer's Stone (2001)</a>
                  </h2>
                </header>
              </article>
            </div>
            """;

        var result = BigOneBrowserDownloadWorkflow.ParseTitlePageUri(html, ListingUri);

        Assert.NotNull(result);
        Assert.Equal(MovieUri, result);
    }

    [Fact]
    public void ParseTitlePageUri_RejectsExternalOrUnexpectedMovieLinks()
    {
        const string html = """
            <div class="post-cards">
              <article class="latestPost excerpt">
                <h2 class="title front-view-title">
                  <a href="https://attacker.example.test/movie/">Movie</a>
                </h2>
              </article>
            </div>
            """;

        Assert.Null(BigOneBrowserDownloadWorkflow.ParseTitlePageUri(html, ListingUri));
    }

    [Fact]
    public void ParseQualityOptions_AssociatesGoogleDriveLinkWithItsOwnHeading()
    {
        const string html = """
            <div class="post-single-content">
              <div class="thecontent clearfix">
                <h5><span>Download Harry Potter 480p [500MB]</span></h5>
                <p>
                  <a class="dl" href="https://dl.fastdlserver.site/?id=SAFE_480&amp;type=file">Google Drive</a>
                  <a class="dl" href="https://linksmod.top/view/MIRROR_480">Download Links</a>
                </p>
                <h5><span>Download Harry Potter 720p [1.3GB]</span></h5>
                <p>
                  <a class="dl" href="https://dl.fastdlserver.site/?id=SAFE_720&amp;type=file">Google Drive</a>
                  <a class="dl" href="https://linksmod.top/view/MIRROR_720">Download Links</a>
                </p>
                <h5><span>Download Harry Potter 1080p [2.8GB]</span></h5>
                <p>
                  <a class="dl" href="https://dl.fastdlserver.site/?id=SAFE_1080&amp;type=file">Google Drive</a>
                  <a class="dl" href="https://linksmod.top/view/MIRROR_1080">Download Links</a>
                </p>
                <h5><span>Download Harry Potter 2160p [16GB]</span></h5>
                <p>
                  <a class="dl" href="https://dl.fastdlserver.site/?id=SAFE_2160&amp;type=file">Google Drive</a>
                </p>
              </div>
            </div>
            """;

        var options = BigOneBrowserDownloadWorkflow.ParseQualityOptions(html, MovieUri);

        Assert.Equal(new[] { "480p", "720p", "1080p", "2160p" }, options.Select(option => option.QualityLabel));
        Assert.All(options, option => Assert.Equal("dl.fastdlserver.site", option.TargetUrl.Host));
        Assert.DoesNotContain(options, option => option.TargetUrl.Host == "linksmod.top");
        Assert.Equal("720p", QualityPreferenceMatcher.SelectBestOption(options, "720p")?.QualityLabel);
    }

    [Fact]
    public void ParseQualityOptions_RejectsFastDlUrlsWithUnexpectedHostOrQuery()
    {
        const string html = """
            <div class="post-single-content"><div class="thecontent">
              <h5>Movie 720p</h5>
              <p><a class="dl" href="https://attacker.example.test/?id=SAFE&amp;type=file">Google Drive</a></p>
              <h5>Movie 1080p</h5>
              <p><a class="dl" href="https://dl.fastdlserver.site/?id=SAFE">Google Drive</a></p>
            </div></div>
            """;

        Assert.Empty(BigOneBrowserDownloadWorkflow.ParseQualityOptions(html, MovieUri));
    }

    [Fact]
    public void ParseFastCloudUri_ResolvesOnlyRootRelativeGdFlixCloudLink()
    {
        var fileHostPage = new Uri("https://new10.gdflix.io/file/iif5zjN7hPXqDaXdAxaq");
        const string html = """
            <div class="card" id="cf_captcha">
              <a href="/login?ref=/file/SAFE">Login To DL [10GBPS]</a>
              <a href="/cloud/1795845182/iif5zjN7hPXqDaXdAxaq" target="_blank">
                <b>FAST CLOUD / ZIPDISK</b>
              </a>
            </div>
            """;

        var cloudUri = BigOneBrowserDownloadWorkflow.ParseFastCloudUri(html, fileHostPage);

        Assert.NotNull(cloudUri);
        Assert.Equal("new10.gdflix.io", cloudUri!.Host);
        Assert.Equal("/cloud/1795845182/iif5zjN7hPXqDaXdAxaq", cloudUri.AbsolutePath);
    }

    [Fact]
    public void ParseFastCloudUri_RejectsExternalCloudHosts()
    {
        var fileHostPage = new Uri("https://new10.gdflix.io/file/SAFE");
        const string html = """
            <a href="https://attacker.example.test/cloud/SAFE">FAST CLOUD</a>
            """;

        Assert.Null(BigOneBrowserDownloadWorkflow.ParseFastCloudUri(html, fileHostPage));
    }

    [Fact]
    public void ParseCloudResumeUri_AcceptsObservedWorkerHostAndStaticAnchorWithoutQuickReadyButton()
    {
        var cloudPage = new Uri("https://new10.gdflix.io/cloud/1795845182/iif5zjN7hPXqDaXdAxaq");
        const string html = """
            <div class="card" id="cf_captcha">
              <a href="https://cloud-dl.px6xkyh05ea.workers.dev/SAFE_SIGNED_FILE/Sample%20720p.mkv.zip?bytes=1000">
                Cloud Resume Download
              </a>
              <button id="quick">Generate Quick Link</button>
            </div>
            """;

        var result = BigOneBrowserDownloadWorkflow.ParseCloudResumeUri(html, cloudPage);

        Assert.NotNull(result);
        Assert.Equal("cloud-dl.px6xkyh05ea.workers.dev", result!.Host);
        Assert.Contains("Sample%20720p.mkv.zip", result.AbsoluteUri);
    }

    [Fact]
    public void ParseFastCloudUri_AcceptsCurrentAndRedirectGdFlixHosts()
    {
        const string html = """
            <a href="/cloud/SAFE_FILE/SAFE_TOKEN"><b>FAST CLOUD / ZIPDISK</b></a>
            """;

        var currentHost = BigOneBrowserDownloadWorkflow.ParseFastCloudUri(
            html,
            new Uri("https://new1.gdflix.io/file/SAFE_FILE"));
        var redirectHost = BigOneBrowserDownloadWorkflow.ParseFastCloudUri(
            html,
            new Uri("https://gdflix.dev/file/SAFE_FILE"));

        Assert.NotNull(currentHost);
        Assert.Equal("new1.gdflix.io", currentHost!.Host);
        Assert.NotNull(redirectHost);
        Assert.Equal("gdflix.dev", redirectHost!.Host);
    }

    [Fact]
    public void ParseCloudResumeUri_AllowsDynamicButtonWithoutStaticDownloadHref()
    {
        var cloudPage = new Uri("https://new10.gdflix.io/cloud/SAFE_FILE");
        const string html = """
            <button id="resume">Cloud Resume Download</button>
            <button id="direct">Direct</button>
            """;

        // A dynamic action has no safe static href. The live workflow must wait for
        // the user's Cloud Resume click and validate the actual browser Download event.
        Assert.Null(BigOneBrowserDownloadWorkflow.ParseCloudResumeUri(html, cloudPage));
    }

    [Fact]
    public void ParseCloudResumeUri_RejectsUnapprovedWorkerSubdomains()
    {
        var cloudPage = new Uri("https://new10.gdflix.io/cloud/SAFE");
        const string html = """
            <a href="https://cloud-dl.attacker.workers.dev/SAFE_FILE.zip">Cloud Resume Download</a>
            """;

        Assert.Null(BigOneBrowserDownloadWorkflow.ParseCloudResumeUri(html, cloudPage));
    }

    [Fact]
    public void ParseCloudResumeUri_UsesQuickDownloadReadyOnlyWhenStaticLinkExists()
    {
        var cloudPage = new Uri("https://new10.gdflix.io/cloud/SAFE");
        const string html = """
            <a href="https://quick.cloudpaglu.site/SAFE_FILE.zip">Quick Download (Ready)</a>
            """;

        var result = BigOneBrowserDownloadWorkflow.ParseCloudResumeUri(html, cloudPage);

        Assert.NotNull(result);
        Assert.Equal("quick.cloudpaglu.site", result!.Host);
    }

    [Fact]
    public void ChallengeDetection_DoesNotTreatCaptchaContainerIdOrTurnstileScriptAsChallengeText()
    {
        const string ordinaryVisibleText = """
            File Information
            Name: Sample Movie 720p.mkv
            FAST CLOUD / ZIPDISK
            Login To DL
            """;

        Assert.False(BigOneBrowserDownloadWorkflow.IsHumanVerificationText(ordinaryVisibleText));
        Assert.True(BigOneBrowserDownloadWorkflow.IsHumanVerificationText("Just a moment. Verify you are human to continue."));
        Assert.True(BigOneBrowserDownloadWorkflow.IsHumanVerificationText("Checking your browser before accessing the page."));
    }

    [Fact]
    public void FinalDestinationPolicy_UsesExactHttpsHosts()
    {
        Assert.True(BigOneBrowserDownloadWorkflow.IsApprovedFinalDestination(
            new Uri("https://cloud-dl.px6xkyh05ea.workers.dev/SIGNED_FILE.zip")));
        Assert.False(BigOneBrowserDownloadWorkflow.IsApprovedFinalDestination(
            new Uri("http://cloud-dl.px6xkyh05ea.workers.dev/SIGNED_FILE.zip")));
        Assert.False(BigOneBrowserDownloadWorkflow.IsApprovedFinalDestination(
            new Uri("https://cloud-dl.px6xkyh05ea.workers.dev.attacker.test/SIGNED_FILE.zip")));
        Assert.False(BigOneBrowserDownloadWorkflow.IsApprovedFinalDestination(
            new Uri("https://attacker.example.test/SIGNED_FILE.zip")));
    }
}
