using System.Collections.ObjectModel;

namespace DigitalCenter.ViewModels.Services;

/// <summary>A timestamped runtime event shown in the shell-wide activity feed.</summary>
public sealed record ActivityFeedItem(DateTime OccurredAt, string Message)
{
    public string TimestampText => OccurredAt.ToLocalTime().ToString("HH:mm:ss");
}

/// <summary>
/// Central in-memory activity feed shared by application view models. Updates are
/// marshalled through the UI dispatcher and the feed is bounded to prevent an
/// unattended session from growing memory indefinitely.
/// </summary>
public sealed class ActivityFeedService
{
    private const int MaximumEntries = 80;
    private readonly IUiDispatcher _dispatcher;

    public ActivityFeedService(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public ObservableCollection<ActivityFeedItem> Items { get; } = [];

    public void Publish(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var entry = new ActivityFeedItem(DateTime.Now, message.Trim());
        _dispatcher.Post(() =>
        {
            Items.Insert(0, entry);
            while (Items.Count > MaximumEntries)
            {
                Items.RemoveAt(Items.Count - 1);
            }
        });
    }
}
