using System.Globalization;
using Jsr4Z8.Core.Queue;
using Jsr4Z8.Core.Settings;
using Jsr4Z8.UI.Services;

namespace Jsr4Z8.UI.ViewModels;

/// <summary>
/// ITaskRunner bridge (Task 13) between task cards and Core's
/// <see cref="TaskQueueRunner"/>: one engine run per card, single-flight (a concurrent
/// entry is a documented no-op that leaves the card Ready — Start/Retry/StartAll are
/// disabled while <see cref="ITaskRunner.IsBusy"/>, this is the defensive backstop),
/// engine events marshaled onto the UI thread through the injectable
/// <see cref="IDispatcher"/> seam (Avalonia in the app, inline in headless tests), and
/// the run outcome mapped to <see cref="TaskGroupViewModel.GoDone"/>/
/// <see cref="TaskGroupViewModel.GoFailed"/> with humanized messages. Progress scale:
/// payload extraction 0-5%, backend percent 5-100 (linear); a failure that kept the
/// job temp dir appends the 诊断目录 hint to the card's error message; a stopped run
/// lands in Failed with stage 已停止 (Retry available). UI progress throttling
/// (post-release fix): the backend streams ~20 progress lines/s with rotating subtask
/// stage names, which made the stage label flicker — ReportProgress/StageText updates
/// now pass a time gate (<see cref="UiProgressGate"/> via an injectable
/// <see cref="TimeProvider"/>): the first update after idle passes immediately,
/// updates inside the gate are coalesced (latest kept, landing when traffic reopens
/// the gate), and terminal outcomes bypass it; LogLines appends are never throttled.
/// </summary>
public sealed class QueueMediator : ITaskRunner
{
    private const string PayloadStage = "准备后端环境";
    private const int PayloadPercentMax = 5;
    private const int BackendPercentBase = 5;
    private const int BackendPercentSpan = 95;
    private const string StoppedStage = "已停止";
    private const string DiagnosticPrefix = "诊断目录：";

    /// <summary>Default UI progress gate: at most ~5 card updates per second.</summary>
    public static readonly TimeSpan UiProgressGate = TimeSpan.FromMilliseconds(200);

    private readonly TaskQueueRunner _engine;
    private readonly ISettingsProvider _settings;
    private readonly IDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _progressGate;
    private int _busy;

    // Throttle bookkeeping (reset per run; single-flight ⇒ at most one live run). All
    // mutations happen inside dispatcher posts, i.e. serialized on the UI thread.
    private bool _uiProgressForwarded;
    private DateTimeOffset _lastUiProgressAt;
    private (int Percent, string Stage)? _pendingUiProgress;

    public QueueMediator(
        TaskQueueRunner? engine = null,
        ISettingsProvider? settings = null,
        IDispatcher? dispatcher = null,
        TimeProvider? clock = null,
        TimeSpan? progressGate = null)
    {
        _engine = engine ?? new TaskQueueRunner();
        _settings = settings ?? new DefaultSettingsProvider();
        _dispatcher = dispatcher ?? new AvaloniaDispatcher();
        _clock = clock ?? TimeProvider.System;
        _progressGate = progressGate ?? UiProgressGate;
    }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public event EventHandler? IsBusyChanged;

    /// <summary>Test hook: every engine event with the card it belongs to, in arrival
    /// order (BackendFinished lets tests timestamp run boundaries).</summary>
    internal event Action<TaskGroupViewModel, QueueEvent>? EventObserved;

    public async Task RunAsync(TaskGroupViewModel vm, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            // Single-flight no-op: nothing ran, so an over-eager start reverts to Ready.
            if (vm.State == TaskState.Running)
                vm.GoReady();
            return;
        }
        _uiProgressForwarded = false;
        _lastUiProgressAt = default;
        _pendingUiProgress = null;
        try
        {
            if (vm.Ptc is not { } ptc)
            {
                _dispatcher.Post(() => vm.GoFailed("缺少 PTC 匹配（任务组未经有效校验）"));
                return;
            }
            try
            {
                TaskRunOutcome outcome = await _engine
                    .RunAsync(vm.Group, ptc, _settings.CurrentSettings, cancellationToken, e => Handle(vm, e))
                    .ConfigureAwait(false);
                ApplyOutcome(vm, outcome);
            }
            catch (Exception ex) // engine contract says it never throws; backstop so a card can never stick in Running
            {
                ApplyOutcome(vm, new TaskRunOutcome(false, false, ex.Message, null, TimeSpan.Zero, null));
            }
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            _dispatcher.Post(() => IsBusyChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    /// <summary>Engine event → card mutation, each marshaled to the UI thread. Progress
    /// updates only apply while the card is still Running (a late progress line after a
    /// user stop must not clobber the stopped/failed card); log lines always append and
    /// are NOT throttled — only ReportProgress/StageText go through the UI gate. Internal
    /// test seam: ProgressThrottleTests replay captured protocol streams through it.</summary>
    internal void Handle(TaskGroupViewModel vm, QueueEvent e)
    {
        EventObserved?.Invoke(vm, e);
        switch (e)
        {
            case PayloadProgress p:
                _dispatcher.Post(() =>
                {
                    if (vm.State != TaskState.Running)
                        return;
                    ReportUiProgress(vm, (int)Math.Round(PayloadPercentMax * p.Fraction), PayloadStage);
                });
                break;

            case BackendStarted s:
                _dispatcher.Post(() =>
                {
                    if (vm.State != TaskState.Running)
                        return;
                    ReportUiProgress(vm, vm.Progress, s.Start.Stage);
                });
                break;

            case BackendLogLine l:
                _dispatcher.Post(() =>
                {
                    vm.LogLines.Add(l.Text);
                    if (l.Progress is { } progress)
                    {
                        if (vm.State != TaskState.Running)
                            return;
                        ReportUiProgress(vm,
                            BackendPercentBase + (int)Math.Round(BackendPercentSpan * progress.Percent / 100.0),
                            progress.Stage);
                    }
                    else
                    {
                        // Non-progress traffic reopens the gate: the latest coalesced
                        // update lands instead of being silently dropped.
                        FlushPendingUiProgress(vm);
                    }
                });
                break;

            // BackendFinished: the outcome returned to RunAsync drives the terminal state.
        }
    }

    /// <summary>Gated card update. (a) first update of a run passes immediately; (b)
    /// updates within the gate are coalesced — only the latest is kept pending; (c) 100%
    /// is a run's last word and bypasses the gate (Done/Failed outcomes bypass too: they
    /// set the terminal state directly in ApplyOutcome).</summary>
    private void ReportUiProgress(TaskGroupViewModel vm, int percent, string stage)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        if (percent >= 100 || !_uiProgressForwarded || now - _lastUiProgressAt >= _progressGate)
            ForwardUiProgress(vm, percent, stage, now);
        else
            _pendingUiProgress = (percent, stage);
    }

    /// <summary>Lands the pending coalesced update once the gate has reopened.</summary>
    private void FlushPendingUiProgress(TaskGroupViewModel vm)
    {
        if (_pendingUiProgress is not { } pending || vm.State != TaskState.Running)
            return;
        DateTimeOffset now = _clock.GetUtcNow();
        if (now - _lastUiProgressAt >= _progressGate)
            ForwardUiProgress(vm, pending.Percent, pending.Stage, now);
    }

    private void ForwardUiProgress(TaskGroupViewModel vm, int percent, string stage, DateTimeOffset now)
    {
        _pendingUiProgress = null;
        _uiProgressForwarded = true;
        _lastUiProgressAt = now;
        vm.ReportProgress(percent, stage);
    }

    private void ApplyOutcome(TaskGroupViewModel vm, TaskRunOutcome outcome)
    {
        _dispatcher.Post(() =>
        {
            if (outcome.Success)
            {
                vm.LogLines.Add($"[完成] 耗时 {outcome.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s · 已输出 {outcome.OutputPath}");
                vm.GoDone(outcome.OutputPath);
            }
            else
            {
                string message = outcome.DiagnosticDir is { } dir
                    ? $"{outcome.Message}\n{DiagnosticPrefix}{dir}"
                    : outcome.Message;
                if (outcome.Cancelled)
                    vm.ReportProgress(vm.Progress, StoppedStage); // stopped-task ruling: stage 已停止, then Failed
                vm.GoFailed(message);
            }
        });
    }
}
