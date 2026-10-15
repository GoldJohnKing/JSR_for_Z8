using Avalonia.Threading;

namespace Jsr4Z8.UI.Services;

/// <summary>
/// UI-thread marshal seam for components that receive events on background threads
/// (the queue). Production wraps Avalonia's Dispatcher.UIThread; headless tests
/// inject a synchronous dispatcher that runs posts inline.
/// </summary>
public interface IDispatcher
{
    /// <summary>Queues <paramref name="action"/> onto the UI thread.</summary>
    void Post(Action action);
}

/// <summary>Production implementation: the Avalonia UI thread.</summary>
public sealed class AvaloniaDispatcher : IDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
