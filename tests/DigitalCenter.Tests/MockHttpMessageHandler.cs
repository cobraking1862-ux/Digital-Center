using System.Net;

namespace DigitalCenter.Tests;

/// <summary>
/// Scriptable HttpMessageHandler that maps request URLs to canned responses
/// (optionally following redirect chains), so workflow providers can be tested
/// against captured real-world page HTML without network access.
/// </summary>
public sealed class MockHttpMessageHandler : HttpMessageHandler
{
    public sealed record Response(int StatusCode, string Body, string ContentType = "text/html", string? Location = null);

    private readonly Dictionary<string, Response> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Response> _hostRoutes = new(StringComparer.OrdinalIgnoreCase);
    public List<Uri> RequestedUris { get; } = [];

    /// <summary>Prefix routes: any URL starting with this key returns the response.</summary>
    public MockHttpMessageHandler Map(string urlPrefix, Response response)
    {
        _routes[urlPrefix] = response;
        return this;
    }

    /// <summary>Host routes: any URL whose host equals this key returns the response.</summary>
    public MockHttpMessageHandler MapHost(string host, Response response)
    {
        _hostRoutes[host] = response;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Request has no URI.");
        RequestedUris.Add(uri);

        Response? response = null;
        if (_hostRoutes.TryGetValue(uri.Host, out var hostResponse))
        {
            response = hostResponse;
        }
        else
        {
            foreach (var (prefix, candidate) in _routes)
            {
                if (uri.AbsoluteUri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    response = candidate;
                    break;
                }
            }
        }

        response ??= new Response(404, "not found");

        var message = new HttpResponseMessage((HttpStatusCode)response.StatusCode)
        {
            Content = new StringContent(response.Body, System.Text.Encoding.UTF8, response.ContentType),
            RequestMessage = request
        };

        if (response.Location is not null)
        {
            message.Headers.Location = new Uri(response.Location, UriKind.RelativeOrAbsolute);
        }

        return Task.FromResult(message);
    }
}
