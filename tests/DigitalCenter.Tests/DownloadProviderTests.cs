using DigitalCenter.Core.Services;
using Xunit;

namespace DigitalCenter.Tests;

/// <summary>
/// Tests for the shared download pipeline used by the BigOne visible browser
/// workflow: quality preference matching and the IDM launcher argument contract.
/// The legacy direct-link and multi-step HTTP-crawler workflows were removed;
/// BigOneBrowserDownloadWorkflowTests covers the remaining provider.
/// </summary>
public class DownloadProviderTests
{
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
