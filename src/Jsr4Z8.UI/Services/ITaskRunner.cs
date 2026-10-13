using Jsr4Z8.UI.ViewModels;

namespace Jsr4Z8.UI.Services;

/// <summary>
/// Execution seam between a task card and the real pipeline (Task 13's queue engine):
/// invoked once the card has entered Running; the implementation owns progress/log
/// events and finishes the card through <see cref="TaskGroupViewModel.GoDone"/> /
/// <see cref="TaskGroupViewModel.GoFailed"/>. <see cref="IsBusy"/> is the queue's
/// single-flight gate — one run at a time; Start/Retry are disabled while it is set.
/// </summary>
public interface ITaskRunner
{
    Task RunAsync(TaskGroupViewModel vm, CancellationToken cancellationToken);

    /// <summary>True while a run is in flight; gates Start/Retry/StartAll.</summary>
    bool IsBusy { get; }

    /// <summary>Raised (on the UI thread in production) whenever IsBusy flips.</summary>
    event EventHandler? IsBusyChanged;
}

/// <summary>
/// Skeleton no-op runner used where no pipeline is wired (default constructor argument):
/// completes immediately without touching the card, so a started card honestly stays
/// Running. Never busy, never raises IsBusyChanged.
/// </summary>
public sealed class NullTaskRunner : ITaskRunner
{
    public static readonly NullTaskRunner Instance = new();

    public Task RunAsync(TaskGroupViewModel vm, CancellationToken cancellationToken) => Task.CompletedTask;

    public bool IsBusy => false;

    // Empty on purpose: CS0067 avoidance for an event that never fires.
    public event EventHandler? IsBusyChanged { add { } remove { } }
}
