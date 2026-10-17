namespace DigitalCenter.ViewModels.Services;

/// <summary>
/// Marshals work onto the UI thread. The WPF host implements this with the
/// Dispatcher; view models stay platform-independent and testable.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Queues the action on the UI thread (fire and forget).</summary>
    void Post(Action action);
}
