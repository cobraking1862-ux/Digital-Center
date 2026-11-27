namespace DigitalCenter.Core.Models;

public static class TicketStatuses
{
    public const string Open = "Open";
    public const string Completed = "Completed";
}

/// <summary>Snapshot of one lookup site's availability result saved on a ticket.</summary>
public sealed class TicketSiteResult
{
    public string SiteName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

/// <summary>A movie/series request ticket filed from Lookup for later manual handling.</summary>
public sealed class LookupTicket
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string MediaType { get; set; } = "Movie";
    public string ImdbId { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }
    public string Status { get; set; } = TicketStatuses.Open;
    public string AvailabilitySummary { get; set; } = string.Empty;
    public List<TicketSiteResult> SiteResults { get; set; } = [];
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
