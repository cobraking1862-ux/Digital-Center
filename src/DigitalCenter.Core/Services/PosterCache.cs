using DigitalCenter.Core.Configuration;

namespace DigitalCenter.Core.Services;

/// <summary>
/// Stores artwork as local image files under the application data root (never
/// as database blobs). <see cref="Models.MediaTitle.PosterPath"/> and
/// <see cref="Models.MediaTitle.BackdropPath"/> hold only the file names; the
/// absolute location is resolved here so the cache folder can move with the
/// data root. Person profiles use the same folder (prefix "p").
/// </summary>
public sealed class PosterCache
{
    private readonly AppPaths _paths;

    public PosterCache(AppPaths paths)
    {
        _paths = paths;
    }

    public string GetAbsolutePath(string? posterPath)
    {
        if (string.IsNullOrWhiteSpace(posterPath))
        {
            return string.Empty;
        }

        return Path.Combine(_paths.PostersDirectory, posterPath);
    }

    /// <summary>
    /// Writes the poster for a title as "{mediaTitleId}.jpg" (overwriting any
    /// previous artwork for the same title) and returns the file name to
    /// store in <see cref="Models.MediaTitle.PosterPath"/>.
    /// </summary>
    public async Task<string> SaveAsync(int mediaTitleId, Stream content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_paths.PostersDirectory);
        var fileName = $"{mediaTitleId}.jpg";
        var target = Path.Combine(_paths.PostersDirectory, fileName);

        await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, ct);
        return fileName;
    }

    /// <summary>Writes the backdrop of a title as "{mediaTitleId}-backdrop.jpg" and returns the file name.</summary>
    public async Task<string> SaveBackdropAsync(int mediaTitleId, Stream content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_paths.PostersDirectory);
        var fileName = $"{mediaTitleId}-backdrop.jpg";
        var target = Path.Combine(_paths.PostersDirectory, fileName);

        await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, ct);
        return fileName;
    }

    /// <summary>Writes a person profile image as "p{tmdbPersonId}.jpg" (or a hash for unknown ids) and returns the file name.</summary>
    public async Task<string> SaveProfileAsync(long tmdbPersonId, Stream content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_paths.PostersDirectory);
        var fileName = $"p{tmdbPersonId}.jpg";
        var target = Path.Combine(_paths.PostersDirectory, fileName);

        await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, ct);
        return fileName;
    }

    public bool Exists(string? posterPath) =>
        !string.IsNullOrWhiteSpace(posterPath) && File.Exists(GetAbsolutePath(posterPath));
}
