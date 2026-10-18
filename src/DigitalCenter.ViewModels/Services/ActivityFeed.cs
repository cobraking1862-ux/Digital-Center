using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DigitalCenter.ViewModels.Services;

/// <summary>
/// What a feed entry describes. The value only drives presentation (icon and
/// colour) in the sidebar, so view models never have to know about glyphs.
/// </summary>
public enum ActivityKind
{
    /// <summary>Screen or category navigation ("Browsing Movies").</summary>
    Navigation,

    /// <summary>USB cart changes ("Movie added to cart successfully").</summary>
    Cart,

    /// <summary>Work happening in the background (Watch Folder, TMDb).</summary>
    Background,

    /// <summary>A file is being moved or copied.</summary>
    FileOperation,

    /// <summary>Neutral application event (startup, settings saved).</summary>
    System,

    /// <summary>An operation finished successfully.</summary>
    Success,

    /// <summary>Something needs attention but did not fail.</summary>
    Warning,

    /// <summary>An operation failed.</summary>
    Error
}

/// <summary>One line of the consolidated activity feed.</summary>
public sealed record ActivityEntry(DateTime TimestampUtc, string Message, ActivityKind Kind)
{
    public string TimeText => TimestampUtc.ToLocalTime().ToString("HH:mm:ss");

    public string KindText => Kind switch
    {
        ActivityKind.Navigation => "Navigation",
        ActivityKind.Cart => "Cart",
        ActivityKind.Background => "Background",
        ActivityKind.FileOperation => "Files",
        ActivityKind.Success => "Done",
        ActivityKind.Warning => "Warning",
        ActivityKind.Error => "Error",
        _ => "System"
    };
}

/// <summary>
/// The ONE place runtime feedback is collected. Every screen publishes its
/// navigation, cart, background, file-operation and system messages here and the
/// shell shows them in the sidebar under "Current Activity &amp; Status", so the
/// operator no longer has to watch five different status lines to follow what
/// the application is doing.
///
/// Publishing is thread-safe: entries are marshalled onto the UI thread through
/// <see cref="IUiDispatcher"/>, so a Watch Folder pass or a copy run can report
/// from a worker thread without touching the collection from the wrong thread.
/// The feed is bounded (<see cref="Capacity"/>) — the oldest entries are dropped,
/// which keeps the list responsive during long copy runs.
/// </summary>
public sealed partial class ActivityFeed : ObservableObject
{
    /// <summary>Maximum number of entries kept in memory.</summary>
    public const int Capacity = 200;

    private readonly IUiDispatcher _ui;

    public ActivityFeed(IUiDispatcher ui)
    {
        _ui = ui;
    }

    /// <summary>Newest entry first, so the sidebar never has to scroll to catch up.</summary>
    public ObservableCollection<ActivityEntry> Entries { get; } = [];

    [ObservableProperty]
    public partial string LatestMessage { get; set; } = "Idle — nothing has happened yet.";

    [ObservableProperty]
    public partial int Count { get; set; }

    public bool HasEntries => Count > 0;

    /// <summary>Adds one entry. Safe to call from any thread.</summary>
    public void Publish(string message, ActivityKind kind = ActivityKind.System)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var entry = new ActivityEntry(DateTime.UtcNow, message.Trim(), kind);
        _ui.Post(() =>
        {
            Entries.Insert(0, entry);
            while (Entries.Count > Capacity)
            {
                Entries.RemoveAt(Entries.Count - 1);
            }

            LatestMessage = entry.Message;
            Count = Entries.Count;
            OnPropertyChanged(nameof(HasEntries));
        });
    }

    /// <summary>Empties the feed (the "Clear" affordance in the sidebar).</summary>
    public void Clear()
    {
        _ui.Post(() =>
        {
            Entries.Clear();
            LatestMessage = "Idle — nothing has happened yet.";
            Count = 0;
            OnPropertyChanged(nameof(HasEntries));
        });
    }
}
