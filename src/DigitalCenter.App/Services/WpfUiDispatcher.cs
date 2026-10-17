using System.Windows;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.App.Services;

/// <summary>Maps the WPF Dispatcher onto the platform-independent IUiDispatcher.</summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
