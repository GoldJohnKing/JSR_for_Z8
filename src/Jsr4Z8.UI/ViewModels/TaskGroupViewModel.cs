using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jsr4Z8.Core.Dark;
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
/// Pure-state ViewModel for one task-group card. Wraps Core's TaskGroup/ValidationResult
/// without mutating them (a reference swap replaces the group instance, notified via
/// <see cref="Group"/>) and is deliberately UI-thread-agnostic — no Avalonia dispatcher
/// or Application dependency — so it runs headless under xUnit and Task 13 can drive it
/// from background threads via the queue mediator. Invalid state transitions are
/// ignored (never thrown): see <see cref="GoRunning"/>.
/// </summary>
public partial class TaskGroupViewModel : ObservableObject
{
    private readonly ITaskRunner _runner;
    private readonly PtcMatch? _ptc;
    private readonly DarkMatch? _dark;
    private CancellationTokenSource? _cts;

    /// <summary>The 7-frame job; <see cref="SetReferenceCommand"/> replaces the instance
    /// (with change notification) when the card swaps its reference frame.</summary>
    public TaskGroup Group { get; private set; }

    /// <summary>The PTC match captured at creation validation — the queue's request
    /// JSON needs its fullwell (BuildJson rounds inside); null only for unvalidated cards.</summary>
    public PtcMatch? Ptc => _ptc;


    /// <summary>The preset dark resolved at creation validation — the queue's request
    /// JSON needs its black/read-noise pair; null only for unvalidated cards.</summary>
    public DarkMatch? Dark => _dark;

    /// <summary>Reference frame filename (FramePaths[0]); follows a reference swap.</summary>
    [ObservableProperty] private string _referenceName = "";

    /// <summary>Output file the pipeline produces: JSR_&lt;基准帧名&gt;.dng; follows a
    /// reference swap.</summary>
    [ObservableProperty] private string _outputName = "";

    public ObservableCollection<FrameChipViewModel> FrameChips { get; }

    /// <summary>Raised by the Remove command; the owning MainViewModel removes this card.</summary>
    public event EventHandler? RemoveRequested;

    [ObservableProperty] private TaskState _state;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private string _stageText = "";
    [ObservableProperty] private string _issuesText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string? _outputPath;

    public TaskGroupViewModel(TaskGroup group, ValidationResult? validation = null, ITaskRunner? runner = null)
    {
        Group = group;
        _runner = runner ?? NullTaskRunner.Instance;
        _runner.IsBusyChanged += OnRunnerBusyChanged;
        _ptc = validation?.Ptc;
        FrameChips = new(group.FramePaths
            .Select((path, i) => new FrameChipViewModel(Path.GetFileName(path), i == 0)));
        ReferenceName = group.FramePaths.Count > 0 ? Path.GetFileName(group.ReferencePath) : "";
        _dark = validation?.Dark;
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

    /// <summary>Meta line under the chips: the pending output name on Ready/Blocked,
    /// the written path once Done; empty while Running/Failed (the stage badge and
    /// error bar carry those states).</summary>
    public string MetaText => State switch
    {
        TaskState.Done => $"已输出 {OutputPath ?? OutputName}",
        TaskState.Ready or TaskState.Blocked => $"待输出 {OutputName}",
        _ => "",
    };

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
        ErrorText = ""; // retry after failure must not show the stale error bar
        return true;
    }

    public bool GoDone(string? outputPath = null)
    {
        if (!Transition(TaskState.Done, TaskState.Running))
            return false;
        Progress = 100;
        ErrorText = ""; // defense: a success after a failure must never show a stale error
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
    /// The queue runner maps engine events onto the card and finishes it through
    /// GoDone/GoFailed. The optional external token (queue-level entry, below) links a
    /// 全部开始-level stop into this run; the card's own ■停止 cancels only its own token.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    public Task StartAsync() => StartCoreAsync(CancellationToken.None);

    /// <summary>Queue-level entry (MainViewModel.StartAll): cancelling externalToken stops
    /// this run together with the whole queue; the card's own Stop stops only this run.</summary>
    public Task StartAsync(CancellationToken externalToken) => StartCoreAsync(externalToken);

    private async Task StartCoreAsync(CancellationToken externalToken)
    {
        if (!GoRunning())
            return;
        _cts?.Dispose();
        _cts = externalToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
            : new CancellationTokenSource();
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

    // Single-flight (Task 13): while the queue is running another card, 开始此组/重试
    // are disabled — one run at a time.
    private bool CanStart() => State == TaskState.Ready && !_runner.IsBusy;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => StartAsync();

    private bool CanRetry() => State == TaskState.Failed && !_runner.IsBusy;

    private void OnRunnerBusyChanged(object? sender, EventArgs e)
    {
        StartCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Stops the in-flight run by cancelling its token; the runner's continuation
    /// moves the card to Failed("已停止").</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    private bool CanStop() => State == TaskState.Running;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove() => RemoveRequested?.Invoke(this, EventArgs.Empty);

    // Per docs/design/card-states.html: ✕移除 shows on Blocked(④⑤)/Ready(①)/Done(③)/Failed(⑥)
    // cards; Running(②) shows only ■停止.
    private bool CanRemove() => State is TaskState.Blocked or TaskState.Ready or TaskState.Done or TaskState.Failed;

    /// <summary>卡内换基准 (docs/design decision 4): clicking a chip while Ready/Blocked
    /// makes that frame the reference — chips [0]/[i] swap slots and ★ flags, and
    /// <see cref="Group"/> is rebuilt with the clicked frame first (the remaining
    /// frames keep their chip order); ReferenceName/OutputName follow. Not available
    /// on Running/Done/Failed cards (a Done output was produced from the old
    /// reference; a Running group is in flight). Ptc/Dark stay as captured: the five
    /// consistency dimensions are group-wide, so the preset matches never depend on
    /// which frame is the reference (Blocked groups captured null either way).</summary>
    [RelayCommand(CanExecute = nameof(CanSetReference))]
    private void SetReference(FrameChipViewModel chip)
    {
        int i = FrameChips.IndexOf(chip);
        if (i <= 0 || State is not (TaskState.Ready or TaskState.Blocked))
            return;
        FrameChipViewModel oldReference = FrameChips[0];
        FrameChips[0] = FrameChips[i] with { IsReference = true };
        FrameChips[i] = oldReference with { IsReference = false };
        string[] paths = Group.FramePaths.ToArray();
        (paths[0], paths[i]) = (paths[i], paths[0]);
        Group = new TaskGroup(paths);
        OnPropertyChanged(nameof(Group));
        ReferenceName = Path.GetFileName(Group.ReferencePath);
        OutputName = $"JSR_{Path.GetFileNameWithoutExtension(ReferenceName)}.dng";
    }

    private bool CanSetReference() => State is TaskState.Ready or TaskState.Blocked;

    /// <summary>Done card's 📂 button (final-review finding 1): opens Explorer with the
    /// written output file selected (/select). Guards: a null/empty or unresolvable
    /// OutputPath is a no-op; when the containing folder still exists but the file was
    /// moved, the folder opens WITHOUT /select; the whole command is wrapped so a shell
    /// failure can never throw out of the button handler.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(OutputPath))
                return;
            string? dir = Path.GetDirectoryName(Path.GetFullPath(OutputPath));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return;
            string arguments = File.Exists(OutputPath)
                ? $"/select,\"{OutputPath}\""
                : $"\"{dir}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Shell wiring (final-review finding 1): never throw from the command.
        }
    }

    private bool CanOpenFolder() => State == TaskState.Done;

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
        SetReferenceCommand.NotifyCanExecuteChanged();
    }

    partial void OnStageTextChanged(string value) => OnPropertyChanged(nameof(StateText));

    partial void OnOutputPathChanged(string? value) => OnPropertyChanged(nameof(MetaText));

    partial void OnOutputNameChanged(string value) => OnPropertyChanged(nameof(MetaText));
}
