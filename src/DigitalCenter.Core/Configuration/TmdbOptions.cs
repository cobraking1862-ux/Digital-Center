namespace DigitalCenter.Core.Configuration;

/// <summary>
/// TMDb connection options. The read access token is loaded from
/// configuration only (appsettings.json next to the executable, environment
/// variable Tmdb__ReadAccessToken, or .NET user secrets in development).
/// It is never hard-coded, never shown in the UI and never written to logs.
/// </summary>
public sealed record TmdbOptions
{
    public const string TokenConfigKey = "Tmdb:ReadAccessToken";

    public string? ReadAccessToken { get; init; }

    /// <summary>Base URL of the TMDb API (overridable for tests).</summary>
    public string ApiBaseUrl { get; init; } = "https://api.themoviedb.org/3/";

    /// <summary>Base URL used to download images; the size segment and the asset path are appended per request.</summary>
    public string ImageBaseUrl { get; init; } = "https://image.tmdb.org/t/p";

    /// <summary>Image size segment used for posters.</summary>
    public string PosterSize { get; init; } = "w500";

    /// <summary>Image size segment used for backdrops.</summary>
    public string BackdropSize { get; init; } = "w780";

    /// <summary>Image size segment used for person profiles.</summary>
    public string ProfileSize { get; init; } = "w185";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ReadAccessToken);

    public static TmdbOptions Unconfigured { get; } = new();
}
