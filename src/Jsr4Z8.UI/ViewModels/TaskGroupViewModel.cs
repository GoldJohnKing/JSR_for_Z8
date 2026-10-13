using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Ptc;
using Jsr4Z8.UI.Services;

namespace Jsr4Z8.UI.ViewModels;

/// <summary>
/// Lifecycle of one task card (docs/design/card-states.html): Blocked ④校验失败/⑤机型不支持,
/// Ready ①, Running ②, Done ③, Failed ⑥运行失败.
/// </summary>
public enum TaskState
{
    Blocked,
    Ready,
    Running,
    Done,
    Failed,
}

/// <summary>One file chip in the card's chip row; the reference frame is FramePaths[0].</summary>
public sealed record FrameChipViewModel(string FileName, bool IsReference);

/// <summary>
/// Pure-state ViewModel for one task-group card. Binds Core's TaskGroup/ValidationResult
/// without mutating them and is deliberately UI-thread-agnostic — no Avalonia dispatcher or
/// Application dependency — so it runs headless under xUnit and Task 13 can drive it from
/// background threads via the queue mediator. Invalid state transitions are ignored
/// (never thrown): see <see cref="GoRunning"/>.
/// </summary>
public partial class TaskGroupViewModel : ObservableObject
{
    private readonly ITaskRunner _runner;
    private readonly PtcMatch? _ptc;
    private CancellationTokenSource? _cts;

    public Guid Id { get; } = Guid.NewGuid();

    public TaskGroup Group { get; }

    public string ReferenceName { get; }

    public string DarkName { get; }

    /// <summary>Output file the pipeline produces: JSR_&lt;基准帧名&gt;.dng.</summary>
    public string OutputName { get; }

    public ObservableCollection<FrameChipViewModel> FrameChips { get; }

    /// <summary>Card log; expansion is manual-only per the user's card-states.html decision.</summary>
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>Raised by the Remove command; the owning MainViewModel removes this card.</summary>
    public event EventHandler? RemoveRequested;

    [ObservableProperty] private TaskState _state;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private string _stageText = "";
    [ObservableProperty] private bool _isLogExpanded;
    [ObservableProperty] private string _issuesText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string? _outputPath;

    public TaskGroupViewModel(TaskGroup group, ValidationResult? validation = null, ITaskRunner? runner = null)
    {
        Group = group;
        _runner = runner ?? NullTaskRunner.Instance;
        _ptc = validation?.Ptc;
        FrameChips = new(group.FramePaths
            .Select((path, i) => new FrameChipViewModel(Path.GetFileName(path), i == 0)));
        ReferenceName = group.FramePaths.Count > 0 ? Path.GetFileName(group.ReferencePath) : "";
        DarkName = Path.GetFileName(group.DarkPath);
        OutputName = $"JSR_{Path.GetFileNameWithoutExtension(ReferenceName)}.dng";
        _state = validation is { CanRun: true } ? TaskState.Ready : TaskState.Blocked;
        if (validation is { CanRun: false })
            _issuesText = string.Join("\n", validation.Issues.Select(i => i.MessageZh));
    }

    /// <summary>Badge text (card-states.html): 就绪 / 运行阶段文字 / 已完成 / 失败 / 无法执行.
    /// While Running the settable <see cref="StageText"/> IS the badge text.</summary>
    public string StateText => State switch
    {
        TaskState.Ready => "就绪",
        TaskState.Running => string.IsNullOrEmpty(StageText) ? "运行中" : StageText,
        TaskState.Done => "已完成",
        TaskState.Failed => "失败",
        _ => "无法执行",
    };

    /// <summary>Drives the progress bar row visibility (mockup ② shows it only while running).</summary>
    public bool IsRunning => State == TaskState.Running;

    /// <summary>Meta line under the chips: PTC 满阱 + output name (the written path once Done).</summary>
    public string MetaText
    {
        get
        {
            if (State == TaskState.Done)
                return $"已输出 {OutputPath ?? OutputName}";
            var parts = new List<string>();
            if (_ptc is { } ptc)
                parts.Add($"PTC {Fmt(ptc.FullwellE)} e⁻");
            parts.Add($"待输出 {OutputName}");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// State machine edges: Blocked→Ready (revalidated); Ready→Running (start) / Blocked
    /// (revalidate) / Failed (immediate run error); Running→Done/Failed (finish) / Ready
    /// (stop); Done→Ready (requeue); Failed→Running (retry) / Ready. Every Go* returns
    /// true iff the state CHANGED:
    /// same-state is a no-op returning false, and any other edge is IGNORED (returns
    /// false, no throw) — Task 13 pushes backend events from background threads, where a
    /// late event (e.g. progress arriving after a user stop) must never crash the app.
    /// </summary>
    public bool GoReady()
    {
        if (!Transition(TaskState.Ready, TaskState.Blocked, TaskState.Running, TaskState.Done, TaskState.Failed))
            return false;
        Progress = 0;
        StageText = "";
        ErrorText = "";
        return true;
    }

    public bool GoBlocked(string? issuesText = null)
    {
        if (!Transition(TaskState.Blocked, TaskState.Ready, TaskState.Failed))
            return false;
        if (issuesText != null)
            IssuesText = issuesText;
        Progress = 0;
        StageText = "";
        ErrorText = "";
        return true;
    }

    public bool GoRunning()
    {
        if (!Transition(TaskState.Running, TaskState.Ready, TaskState.Failed))
            return false;
        Progress = 0;
        StageText = "";
        return true;
    }

    public bool GoDone(string? outputPath = null)
    {
        if (!Transition(TaskState.Done, TaskState.Running))
            return false;
        Progress = 100;
        if (outputPath != null)
            OutputPath = outputPath;
        return true;
    }

    public bool GoFailed(string? message = null)
    {
        if (!Transition(TaskState.Failed, TaskState.Running, TaskState.Ready))
            return false;
        ErrorText = message ?? "";
        return true;
    }

    /// <summary>Backend progress in percent, clamped to 0-100 (Task 6 carried finding: raw
    /// PROGRESS lines may exceed 100); the optional stage text becomes the badge text.</summary>
    public void ReportProgress(int percent, string? stage = null)
    {
        Progress = Math.Clamp(percent, 0, 100);
        if (stage != null)
            StageText = stage;
    }

    private bool Transition(TaskState to, params TaskState[] from)
    {
        if (State == to)
            return false;
        if (Array.IndexOf(from, State) < 0)
            return false;
        State = to;
        return true;
    }

    /// <summary>Start (and Retry, below): enter Running, then hand the card to the runner.
    /// NullTaskRunner completes without touching the card, so it honestly stays Running
    /// until Task 13's real queue engine arrives. Faults land the card in Failed.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    public async Task StartAsync()
    {
        if (!GoRunning())
            return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            await _runner.RunAsync(this, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            GoFailed("已停止");
        }
        catch (Exception ex)
        {
            GoFailed(ex.Message);
        }
    }

    private bool CanStart() => State == TaskState.Ready;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => StartAsync();

    private bool CanRetry() => State == TaskState.Failed;

    /// <summary>Stops the in-flight run by cancelling its token; the runner's continuation
    /// moves the card to Failed("已停止").</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    private bool CanStop() => State == TaskState.Running;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove() => RemoveRequested?.Invoke(this, EventArgs.Empty);

    // Per the brief's table: Remove is enabled for Done/Failed/Blocked only (Ready/Running
    // rows show just their primary action). NOTE: mockup ① also draws ✕移除 on Ready —
    // flagged to the controller; flipping it is a one-line change here.
    private bool CanRemove() => State is TaskState.Blocked or TaskState.Done or TaskState.Failed;

    /// <summary>Task 13 sets OutputPath via GoDone and wires the shell open (explorer);
    /// skeleton no-op so the button exists with correct availability.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
    }

    private bool CanOpenFolder() => State == TaskState.Done;

    [RelayCommand]
    private void ToggleLog() => IsLogExpanded = !IsLogExpanded;

    partial void OnStateChanged(TaskState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(MetaText));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    partial void OnStageTextChanged(string value) => OnPropertyChanged(nameof(StateText));

    partial void OnOutputPathChanged(string? value) => OnPropertyChanged(nameof(MetaText));

    private static string Fmt(double fullwell) =>
        fullwell.ToString(fullwell == Math.Truncate(fullwell) ? "N0" : "N1", CultureInfo.InvariantCulture);
}
