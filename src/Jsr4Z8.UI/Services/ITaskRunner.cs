using Jsr4Z8.UI.ViewModels;

namespace Jsr4Z8.UI.Services;

/// <summary>
/// Execution seam between a task card and the real pipeline (Task 13's queue engine):
/// invoked once the card has entered Running; the implementation owns progress/log
/// events and finishes the card through <see cref="TaskGroupViewModel.GoDone"/> /
/// <see cref="TaskGroupViewModel.GoFailed"/>.
/// </summary>
public interface ITaskRunner
{
    Task RunAsync(TaskGroupViewModel vm, CancellationToken cancellationToken);
}

/// <summary>
/// Skeleton no-op runner used until Task 13 provides the real queue engine: completes
/// immediately without touching the card, so a started card honestly stays Running
/// (nothing is wired yet). Injected as the constructor default everywhere.
/// </summary>
public sealed class NullTaskRunner : ITaskRunner
{
    public static readonly NullTaskRunner Instance = new();

    public Task RunAsync(TaskGroupViewModel vm, CancellationToken cancellationToken) => Task.CompletedTask;
}
