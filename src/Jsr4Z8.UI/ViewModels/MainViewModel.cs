using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jsr4Z8.Core;
using Jsr4Z8.Core.Model;
using Jsr4Z8.UI.Services;

namespace Jsr4Z8.UI.ViewModels;

/// <summary>
/// Root ViewModel of the card-flow main window: the
/// vertical task list plus the top-bar commands. Pure state, headless-testable. Group
/// creation is inline: dropped/picked NEF files go
/// through <see cref="AddFilesAsync"/>, which creates the card IMMEDIATELY through the
/// injectable <c>post</c> seam (Dispatcher.UIThread in the app, inline in tests) —
/// validation:null, 校验中 overlay — and then hands the card its own background
/// validation flight (<see cref="TaskGroupViewModel.ValidateAsync"/>); the card, not
/// this VM, owns the validation lifecycle: drop → card appears at once → Ready/
/// Blocked when the flight lands.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ThemeService _theme;
    private readonly ITaskRunner _runner;
    private readonly Action<Action> _post;
    private readonly Func<string, RawMetadata>? _read;
    private CancellationTokenSource? _queueCts;

    public ObservableCollection<TaskGroupViewModel> Tasks { get; } = new();

    public MainViewModel(ThemeService? theme = null, ITaskRunner? runner = null, Action<Action>? post = null,
        Func<string, RawMetadata>? read = null)
    {
        _theme = theme ?? new ThemeService();
        _runner = runner ?? NullTaskRunner.Instance;
        _post = post ?? (action => action());
        _read = read;   // null = the production RawMetadataReader (the card's own default)
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
    /// overlay.</summary>
    [ObservableProperty] private bool _isDragOver;

    /// <summary>Derived: how many cards are mid-validation-flight
    /// (<c>Tasks.Count(t => t.IsValidating)</c>) — recomputed with every card
    /// IsValidating/State change and every Tasks collection change (a validating card
    /// removed mid-flight settles the count immediately; no manual bookkeeping can
    /// strand the 正在校验 line). Drives the 正在校验 status line.</summary>
    [ObservableProperty] private int _validatingCount;

    /// <summary>Bottom-bar status line: 正在校验 → 正在处理 → 拖放引导 → 共 N 组.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>Assembly version for the top-right corner ("v0.1.0"); assigned by the
    /// window's OnOpened, empty before that. Pure view-supporting surface.</summary>
    [ObservableProperty] private string _appVersion = "";

    /// <summary>后端载荷版本 for the top-right corner's second line ("v18.2.1"，显示为
    /// JSR v18.2.1)；由窗口 OnOpened 从嵌入 manifest 的 backendVersion 字段
    /// 读取赋值（pack-payload 打包期写入，重打包自动更新），空 = 隐藏整行。</summary>
    [ObservableProperty] private string _backendVersion = "";

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

    /// <summary>The seeded-validated creation path: an ALREADY-validated group becomes
    /// a card — Ready when CanRun, Blocked with the issues shown in the card's block
    /// otherwise — with the creator's per-path metadata so hover texts and cache-backed
    /// edits need no re-read. The main drop flow creates cards immediately through
    /// <see cref="AddFilesAsync"/> instead (the card validates itself); this is the
    /// test seeding entry. Cards share this
    /// VM's runner (the queue mediator; NullTaskRunner headless).</summary>
    public void AddValidated(TaskGroup group, ValidationResult validation,
        IReadOnlyDictionary<string, RawMetadata>? metadata = null) =>
        Add(new TaskGroupViewModel(group, validation, _runner, metadata));

    /// <summary>Inline group creation (即时建卡): NEF files dropped/picked anywhere on
    /// the main window. A payload without any .NEF is a no-op (no card, no status
    /// flicker); otherwise the card is created IMMEDIATELY through <c>post</c> —
    /// <see cref="Add"/> wiring (RemoveRequested + counter subscriptions) — in the
    /// 校验中 state (validation:null ctor path), and then validates itself on a
    /// background thread through <see cref="TaskGroupViewModel.ValidateAsync"/> (the
    /// Loading overlay covers the whole flight). With the inline test post the
    /// synchronous part runs exactly through card creation and up to ValidateAsync's
    /// first await, so the un-awaited call's Task is pending while the card is already
    /// in Tasks and validating; with the real dispatcher post the flight simply
    /// continues on the UI thread (awaiting it then is a no-op — the flight owns its
    /// own settle-and-notify, exceptions included).</summary>
    public async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        string[] frames = paths
            .Where(p => string.Equals(Path.GetExtension(p), ".nef", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (frames.Length == 0)
            return;
        Task flight = Task.CompletedTask;
        _post(() =>
        {
            var card = new TaskGroupViewModel(new TaskGroup(frames), validation: null,
                runner: _runner, read: _read);
            Add(card);
            flight = card.ValidateAsync();
        });
        await flight;
    }

    /// <summary>Test seam: the queue-level token source of an in-flight 全部开始 run
    /// (null while none is running). Cancelling it stops the CURRENT card within the
    /// stop budget AND prevents later cards from starting; a per-card ■停止 cancels only
    /// that card — the queue continues with the remaining ones.</summary>
    internal CancellationTokenSource? ActiveQueueCts => _queueCts;

    /// <summary>Starts every Ready card sequentially: one at a time (the runner is
    /// single-flight), each to completion, continuing after failures and per-card stops.
    /// There is no StopAll; the queue-level source is the
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
            // Skip validating cards too — a validating card's set and Ptc/Dark are
            // mid-recompute; the runner must never pick up a half-validated group
            // (StartAll calls StartAsync directly, bypassing CanStart).
            foreach (TaskGroupViewModel vm in Tasks
                .Where(t => t.State == TaskState.Ready && !t.IsValidating).ToList())
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
    /// status line) — a card finishing flips DoneCount without touching the collection.
    /// IsValidating changes land here too, re-deriving ValidatingCount (both
    /// directions — flight started and flight landed). A State change ALSO re-raises
    /// the queue button's CanExecuteChanged: Avalonia's Button caches CanExecute and
    /// re-queries it only on this event, and a validation flight landing Ready (or a
    /// run starting/finishing) changes <see cref="CanStartAll"/> WITHOUT any Tasks
    /// collection change — without the notify, 快速两连拖 (both cards land Ready after
    /// the last Add already happened) leaves 全部开始 cached-disabled forever.</summary>
    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TaskGroupViewModel.State) or nameof(TaskGroupViewModel.IsValidating))
        {
            RefreshCounts();
            StartAllCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnRunnerBusyChanged(object? sender, EventArgs e)
    {
        StartAllCommand.NotifyCanExecuteChanged();
        RefreshStatusText();
    }

    private void RefreshCounts()
    {
        ValidatingCount = Tasks.Count(t => t.IsValidating);   // derived: no manual flights
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
            : "";

    partial void OnValidatingCountChanged(int value) => RefreshStatusText();
}
