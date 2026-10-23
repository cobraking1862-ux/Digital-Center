using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.ViewModels;

/// <summary>One row of the Management recent-activity list.</summary>
public sealed class ActivityItemViewModel
{
    public ActivityItemViewModel(MetadataImportLog log)
    {
        Id = log.Id;
        FileName = log.SourceFile;
        SourcePath = log.SourcePath ?? string.Empty;
        SizeText = log.FileSize is { } size ? UiFormat.FormatBytes(size) : "—";
        Status = log.Status;
        StatusText = ImportStatusLabels.For(log.Status);
        StatusKey = log.Status switch
        {
            ImportStatuses.Imported => "Ok",
            ImportStatuses.Failed => "Bad",
            ImportStatuses.Matched => "Active",
            ImportStatuses.Pending => "Pending",
            ImportStatuses.Approved => "Pending",
            _ => "Neutral"
        };
        DetectedType = log.IsSeries ? "Series" : "Movie";
        MatchedTitle = string.IsNullOrWhiteSpace(log.ParsedTitle) ? "—" : log.ParsedTitle!;
        MatchSummary = log.TmdbId is not null
            ? $"{log.ParsedTitle}{(log.ParsedYear is { } y ? $" ({y})" : "")} · TMDb {log.TmdbId}" +
              (log.Confidence is { } c ? $" · {c:P0}" : "")
            : log.Status;
        Detail = log.ErrorDetails ?? log.Note ?? string.Empty;
        HasDetail = Detail.Length > 0;
        MediaTitleId = log.MediaTitleId;
        TimestampText = log.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        CanConfirm = log.Status is ImportStatuses.Matched or ImportStatuses.Approved;
        CanSkip = log.Status is ImportStatuses.Matched or ImportStatuses.Pending or ImportStatuses.NoMatch;
        IsAwaitingDestination = log.Status == ImportStatuses.Matched;
        MoveHint = CanConfirm
            ? "Browse for a folder and move this file into it"
            : log.Status is ImportStatuses.Pending or ImportStatuses.NoMatch
                ? $"Needs a metadata match before it can be moved (status: {StatusText})"
                : $"Cannot be moved in this state (status: {StatusText})";
    }

    public int Id { get; }
    public string FileName { get; }
    public string SourcePath { get; }
    public string SizeText { get; }
    public string Status { get; }
    public string StatusText { get; }
    public string StatusKey { get; }
    public string DetectedType { get; }
    public string MatchedTitle { get; }
    public string MatchSummary { get; }
    public string Detail { get; }
    public bool HasDetail { get; }
    public int? MediaTitleId { get; }
    public string TimestampText { get; }
    public bool CanConfirm { get; }
    public bool CanSkip { get; }
    public bool IsAwaitingDestination { get; }

    /// <summary>Tooltip of the per-row "Browse / Move" button.</summary>
    public string MoveHint { get; }

    /// <summary>The backing log row (refreshed by the view model when the selection changes).</summary>
    public MetadataImportLog Log { get; internal set; } = null!;
}
