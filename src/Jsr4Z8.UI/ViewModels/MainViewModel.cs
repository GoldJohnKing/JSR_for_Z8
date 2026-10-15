using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jsr4Z8.Core.Model;
using Jsr4Z8.UI.Services;

namespace Jsr4Z8.UI.ViewModels;

/// <summary>
/// Root ViewModel of the card-flow main window (docs/design/layout.html option C): the
/// vertical task list plus the top-bar commands. Pure state, headless-testable. Group
/// creation is inline since Task 4 (the 新建任务组 dialog is gone): dropped/picked NEF
/// files go through <see cref="AddFilesAsync"/>, which creates and validates the group
/// on a background thread (TaskGroupCreator) and posts the card back through the
/// injectable <c>post</c> seam — Dispatcher.UIThread in the app, inline in tests. The
/// settings dialog is still opened by the app composition root through
/// <see cref="OpenSettingsRequested"/>, keeping this VM free of windowing dependencies.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ThemeService _theme;
    private readonly ITaskRunner _runner;
    private readonly Action<Action> _post;
    private CancellationTokenSource? _queueCts;

    public ObservableCollection<TaskGroupViewModel> Tasks { get; } = new();

    /// <summary>Inert since Task 4 removed the dialog: the pre-Task-6 MainWindow top bar
    /// still binds ＋添加任务组 (<see cref="AddTaskCommand"/>), but nothing subscribes
    /// anymore. Task 6's window rewrite removes button and event together.</summary>
    public event EventHandler? AddTaskRequested;

    public event EventHandler? OpenSettingsRequested;

    public MainViewModel(ThemeService? theme = null, ITaskRunner? runner = null, Action<Action>? post = null)
    {
        _theme = theme ?? new ThemeService();
        _runner = runner ?? NullTaskRunner.Instance;
        _post = post ?? (action => action());
        _runner.IsBusyChanged += OnRunnerBusyChanged;
        Tasks.CollectionChanged += OnTasksChanged;
        RefreshStatusText();
    }

    /// <summary>Theme state for the top-bar toggle (Mode/ModeText bindable).</summary>
    public ThemeService Theme => _theme;

    /// <summary>True while the list is empty — drives the empty-state guide
    /// (visual only; dropping files anywhere on the window reaches
    /// <see cref="AddFilesAsync"/>).</summary>
    [ObservableProperty] private bool _hasNoTasks = true;

    /// <summary>Drag-hover state of the whole window shell — drives the full-window drop
    /// overlay (wired by Task 6's window rewrite).</summary>
    [ObservableProperty] private bool _isDragOver;

    /// <summary>Concurrent in-flight group creations — one per <see cref="AddFilesAsync"/>
    /// call that has not posted its card back yet. Drives the 正在校验 status line.</summary>
    [ObservableProperty] private int _validatingCount;

    /// <summary>Bottom-bar status line: 正在校验 → 正在处理 → 拖放引导 → 共 N 组.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>Derived card counts for the overview bar — refresh with every card State
    /// change and every Tasks collection change.</summary>
    public int ReadyCount => Tasks.Count(t => t.State == TaskState.Ready);

    public int DoneCount => Tasks.Count(t => t.State == TaskState.Done);

    public int FailedCount => Tasks.Count(t => t.State == TaskState.Failed);

    public int BlockedCount => Tasks.Count(t => t.State == TaskState.Blocked);

    /// <summary>Entry point for new cards: wires the card's RemoveRequested back into
    /// Tasks. Adding via Tasks directly bypasses that wiring.</summary>
    public void Add(TaskGroupViewModel vm)
    {
        vm.RemoveRequested += (_, _) => Tasks.Remove(vm);
        Tasks.Add(vm);
    }

    /// <summary>The posted-back result of TaskGroupCreator: the validated group becomes a
    /// card — Ready when CanRun, Blocked with the issues shown in the card's block
    /// otherwise (an 8-file drop or an unreadable frame still creates its card). Cards
    /// share this VM's runner (the queue mediator; NullTaskRunner headless).</summary>
    public void AddValidated(TaskGroup group, ValidationResult validation) =>
        Add(new TaskGroupViewModel(group, validation, _runner));

    /// <summary>Inline group creation: NEF files dropped/picked anywhere on the main
    /// window. A payload without any .NEF is a no-op (no card, no status flicker);
    /// otherwise the group is created and validated on a background thread and the card
    /// is posted back through <c>post</c>. <see cref="ValidatingCount"/> brackets the
    /// flight; the UI thread is never blocked.</summary>
    public async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 ||
            !paths.Any(p => string.Equals(Path.GetExtension(p), ".nef", StringComparison.OrdinalIgnoreCase)))
            return;
        ValidatingCount++;
        CreatedTaskGroup? created;
        try
        {
            created = await TaskGroupCreator.CreateAsync(paths).ConfigureAwait(false);
        }
        catch
        {
            _post(ValidatedFlightEnded); // never strand a stuck 正在校验 line
            throw;
        }
        _post(() =>
        {
            if (created is { } c)
                AddValidated(c.Group, c.Validation);
            ValidatedFlightEnded();
        });
    }

    /// <summary>Pre-Task-6 MainWindow shim: the old code-behind calls this void-style
    /// from its drop handler. Fire-and-forget into <see cref="AddFilesAsync"/> — Task 6's
    /// rewrite awaits the async path directly.</summary>
    public void DropFiles(IReadOnlyList<string> paths) => _ = AddFilesAsync(paths);

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

    /// <summary>清除全部: removes every card that is not Running — a running card keeps
    /// its single-flight slot (■停止 is its only way out) and the queue continues. The
    /// snapshot makes the loop CollectionChanged-safe.</summary>
    [RelayCommand]
    private void ClearAll()
    {
        foreach (TaskGroupViewModel vm in Tasks.Where(t => t.State != TaskState.Running).ToList())
            Tasks.Remove(vm);
    }

    [RelayCommand]
    private void AddTask() => AddTaskRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ToggleTheme() => _theme.Cycle();

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (TaskGroupViewModel vm in e.NewItems)
                vm.PropertyChanged += OnCardPropertyChanged;
        if (e.OldItems != null)
            foreach (TaskGroupViewModel vm in e.OldItems)
                vm.PropertyChanged -= OnCardPropertyChanged;
        HasNoTasks = Tasks.Count == 0;
        StartAllCommand.NotifyCanExecuteChanged();
        RefreshCounts();
    }

    /// <summary>Card state changes drive the derived counts (and through them the
    /// status line) — a card finishing flips DoneCount without touching the collection.</summary>
    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskGroupViewModel.State))
            RefreshCounts();
    }

    private void OnRunnerBusyChanged(object? sender, EventArgs e)
    {
        StartAllCommand.NotifyCanExecuteChanged();
        RefreshStatusText();
    }

    /// <summary>One validation flight landed (its card, if any, already added): settle the
    /// counter — <see cref="OnValidatingCountChanged"/> refreshes the status line.</summary>
    private void ValidatedFlightEnded() => ValidatingCount--;

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(ReadyCount));
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(BlockedCount));
        RefreshStatusText();
    }

    private void RefreshStatusText() =>
        StatusText = ValidatingCount > 0 ? $"正在校验 {ValidatingCount} 组新建任务…"
            : _runner.IsBusy ? "正在处理任务组…"
            : Tasks.Count == 0 ? "拖放 7 张 RAW 开始"
            : $"共 {Tasks.Count} 个任务组";

    partial void OnValidatingCountChanged(int value) => RefreshStatusText();
}
