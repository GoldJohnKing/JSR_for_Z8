using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jsr4Z8.Core.Model;
using Jsr4Z8.UI.Services;

namespace Jsr4Z8.UI.ViewModels;

/// <summary>
/// Root ViewModel of the card-flow main window (docs/design/layout.html option C): the
/// vertical task list plus the top-bar commands. Pure state, headless-testable — the
/// dialogs are opened by the app composition root through the *Requested events (App
/// wires AddTask → NewTaskDialog since Task 12; OpenSettings → SettingsDialog in Task 14),
/// keeping this VM free of windowing dependencies.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ThemeService _theme;
    private readonly ITaskRunner _runner;
    private CancellationTokenSource? _queueCts;

    public ObservableCollection<TaskGroupViewModel> Tasks { get; } = new();

    public event EventHandler? AddTaskRequested;

    /// <summary>Main-window drag-drop (post-release fix 2): RAW files dropped anywhere on
    /// the shell raise this with their local paths; App answers with the same NewTaskDialog
    /// flow as ＋添加任务组, pre-filled with the files (the dark frame stays a user choice).
    /// The window's drag wiring only forwards payloads carrying at least one .NEF.</summary>
    public event EventHandler<IReadOnlyList<string>>? DropFilesRequested;

    public event EventHandler? OpenSettingsRequested;

    public MainViewModel(ThemeService? theme = null, ITaskRunner? runner = null)
    {
        _theme = theme ?? new ThemeService();
        _runner = runner ?? NullTaskRunner.Instance;
        _runner.IsBusyChanged += (_, _) => StartAllCommand.NotifyCanExecuteChanged();
        Tasks.CollectionChanged += OnTasksChanged;
    }

    /// <summary>Theme state for the top-bar toggle (Mode/ModeText bindable).</summary>
    public ThemeService Theme => _theme;

    /// <summary>True while the list is empty — drives the dashed drop-hint overlay
    /// (visual only; dropping files anywhere on the window is handled by the shell's
    /// drag wiring and reaches <see cref="DropFiles"/>).</summary>
    [ObservableProperty] private bool _hasNoTasks = true;

    /// <summary>Entry point for new cards: wires the card's RemoveRequested back into
    /// Tasks. Adding via Tasks directly bypasses that wiring.</summary>
    public void Add(TaskGroupViewModel vm)
    {
        vm.RemoveRequested += (_, _) => Tasks.Remove(vm);
        Tasks.Add(vm);
    }

    /// <summary>Task 12's dialog completion (NewTaskDialog CreateCompleted): the validated
    /// group becomes a card — Ready when CanRun (创建任务组 is gated on it), Blocked with
    /// the issues defensively otherwise (Create cannot fire without CanRun today). Cards
    /// share this VM's runner (Task 13: the queue mediator; NullTaskRunner headless).</summary>
    public void AddValidated(TaskGroup group, ValidationResult validation) =>
        Add(new TaskGroupViewModel(group, validation, _runner));

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HasNoTasks = Tasks.Count == 0;
        StartAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AddTask() => AddTaskRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Code-behind entry for a main-window file drop: forwards the local paths to
    /// <see cref="DropFilesRequested"/>. A prefilter happens in the window (payload must
    /// carry a .NEF); an empty call is a no-op so a stray drop cannot open an empty dialog.</summary>
    public void DropFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;
        DropFilesRequested?.Invoke(this, paths);
    }

    /// <summary>Test seam: the queue-level token source of an in-flight 全部开始 run
    /// (null while none is running). Cancelling it stops the CURRENT card within the
    /// stop budget AND prevents later cards from starting; a per-card ■停止 cancels only
    /// that card — the queue continues with the remaining ones.</summary>
    internal CancellationTokenSource? ActiveQueueCts => _queueCts;

    /// <summary>Starts every Ready card sequentially: one at a time (the runner is
    /// single-flight), each to completion, continuing after failures and per-card stops.
    /// Per the card-states mockups there is no StopAll; the queue-level source is the
    /// test/lifecycle seam above.</summary>
    [RelayCommand(CanExecute = nameof(CanStartAll))]
    private async Task StartAllAsync()
    {
        if (_runner.IsBusy)
            return; // single-flight backstop (the button is already disabled while busy)
        using var queueCts = new CancellationTokenSource();
        _queueCts = queueCts;
        try
        {
            foreach (TaskGroupViewModel vm in Tasks.Where(t => t.State == TaskState.Ready).ToList())
            {
                if (queueCts.IsCancellationRequested)
                    break; // queue-level stop: later tasks are never started
                if (!Tasks.Contains(vm))
                    continue; // card removed while an earlier one ran
                await vm.StartAsync(queueCts.Token);
            }
        }
        finally
        {
            _queueCts = null;
        }
    }

    private bool CanStartAll() => !_runner.IsBusy && Tasks.Any(t => t.State == TaskState.Ready);

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ToggleTheme() => _theme.Cycle();
}
