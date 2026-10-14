using System.Text;
using Jsr4Z8.Core.Backend;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Ptc;
using Jsr4Z8.Core.Queue;
using Jsr4Z8.Core.Settings;
using Jsr4Z8.UI.Services;
using Jsr4Z8.UI.ViewModels;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Post-release fix (stage-label flicker): the mediator's UI progress gate. The real
/// capture fixtures\protocol\stdout3.log (16175 lines, ~7.2k of them SRHQ progress at
/// ~20/s with rotating subtask stage names) is replayed line by line through
/// ProtocolParser → QueueEvent → QueueMediator.Handle with a synchronous dispatcher
/// and a fake clock, asserting the contract:
/// (1) the final Progress/StageText land correctly (coalescing, terminal bypass);
/// (2) with the default 200 ms gate, StageText+Progress PropertyChanged collapses
///     from per-line to a gate-bounded count;
/// (3) with the gate disabled (TimeSpan.Zero ⇒ every update passes — the old
///     behavior), the change count matches a bare unthrottled card replay exactly;
/// (d) LogLines appends are never throttled. A synthetic test pins the gate
/// boundaries precisely. Windows-host tests.
/// </summary>
public class ProgressThrottleTests
{
    private const string DarkPath = @"C:\Photos\DSC_7575.NEF";

    private static readonly string[] Frames =
        Enumerable.Range(7576, 7).Select(i => $@"C:\Photos\DSC_{i}.NEF").ToArray();

    /// <summary>Deterministic clock: tests advance it explicitly per replayed line.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan step) => _now += step;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class SyncDispatcher : IDispatcher
    {
        public void Post(Action action) => action();
    }

    private static QueueMediator Mediator(TimeProvider clock, TimeSpan? gate) =>
        new(engine: new TaskQueueRunner(),
            settings: new FakeSettings(),
            dispatcher: new SyncDispatcher(),
            clock: clock,
            progressGate: gate);

    private sealed class FakeSettings : ISettingsProvider
    {
        public AppSettings CurrentSettings { get; } = new();
    }

    /// <summary>Ready→Running card wired to the (engineless) mediator: Handle is driven
    /// directly (the engine/run-outcome path is QueueRunnerTests' business).</summary>
    private static TaskGroupViewModel RunningCard(QueueMediator mediator)
    {
        TaskGroupViewModel vm = new(
            new TaskGroup(Frames, DarkPath),
            new ValidationResult(true, Array.Empty<ValidationIssue>(), new PtcMatch(9512, true, "表值")),
            mediator);
        Assert.True(vm.GoRunning());
        return vm;
    }

    /// <summary>Counts PropertyChanged for Progress + StageText (the two flicker surfaces).</summary>
    private sealed class ChangeCounter : IDisposable
    {
        private readonly TaskGroupViewModel _vm;
        public int Count;

        public ChangeCounter(TaskGroupViewModel vm)
        {
            _vm = vm;
            _vm.PropertyChanged += OnChanged;
        }

        private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
            Count += e.PropertyName is nameof(TaskGroupViewModel.Progress) or nameof(TaskGroupViewModel.StageText) ? 1 : 0;

        public void Dispose() => _vm.PropertyChanged -= OnChanged;
    }

    private static string[] FixtureLines()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "fixtures", "protocol", "stdout3.log");
            if (File.Exists(candidate))
            {
                string[] lines = File.ReadAllText(candidate, Encoding.UTF8).Split('\n');
                if (lines.Length > 0 && lines[^1].Length == 0)
                    lines = lines[..^1];
                return Array.ConvertAll(lines, l => l.EndsWith('\r') ? l[..^1] : l);
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException($"Fixture not found: fixtures\\protocol\\stdout3.log (searched upwards from {AppContext.BaseDirectory})");
    }

    /// <summary>Maps one parsed protocol event onto the queue event the mediator consumes
    /// (BackendRunner's own mapping: Start→Started, Log/Unrecognized/ErrorOnStderr→LogLine,
    /// Result→no per-line event — the outcome drives the terminal state instead).</summary>
    private static QueueEvent? ToQueueEvent(ProtocolEvent parsed) => parsed switch
    {
        Start s => new BackendStarted(s.Json),
        Log l => new BackendLogLine(l.Text, l.Progress),
        Unrecognized u => new BackendLogLine(u.Text, null),
        ErrorOnStderr e => new BackendLogLine("stderr| " + e.Message, null),
        _ => null,
    };

    // ---------------------------------------------------------------- gate semantics (synthetic)

    [Fact]
    public void ProgressGate_FirstPasses_WithinDropsAndCoalesces_ReopenLandsPending_TerminalBypasses()
    {
        var clock = new FakeClock();
        QueueMediator mediator = Mediator(clock, gate: QueueMediator.UiProgressGate);
        TaskGroupViewModel vm = RunningCard(mediator);

        // (a) first update of a run passes immediately.
        mediator.Handle(vm, new BackendLogLine("SRHQ_V12_PROGRESS|20|阶段A", new ProgressLine(20, "阶段A")));
        Assert.Equal(5 + 95 * 20 / 100, vm.Progress);          // 24
        Assert.Equal("阶段A", vm.StageText);

        // (b) inside the gate: dropped and coalesced — only the latest is kept pending.
        clock.Advance(TimeSpan.FromMilliseconds(50));
        mediator.Handle(vm, new BackendLogLine("SRHQ_V12_PROGRESS|40|阶段B", new ProgressLine(40, "阶段B")));
        Assert.Equal(24, vm.Progress);                          // unchanged: dropped
        Assert.Equal("阶段A", vm.StageText);

        // Still within the gate (150 ms since the forward): a plain log line must NOT
        // flush the pending update, but the line itself still lands in the log (d).
        clock.Advance(TimeSpan.FromMilliseconds(100));
        mediator.Handle(vm, new BackendLogLine("plain traffic", null));
        Assert.Equal(24, vm.Progress);
        Assert.Equal("阶段A", vm.StageText);
        Assert.Contains("plain traffic", vm.LogLines);

        // Gate reopened (210 ms since the forward): plain traffic lands the LATEST
        // coalesced update — the final state was kept, not dropped.
        clock.Advance(TimeSpan.FromMilliseconds(60));
        mediator.Handle(vm, new BackendLogLine("plain traffic 2", null));
        Assert.Equal(5 + 95 * 40 / 100, vm.Progress);           // 43 — 阶段B landed
        Assert.Equal("阶段B", vm.StageText);

        // (c) terminal percent bypasses the gate even well inside it (10 ms later).
        clock.Advance(TimeSpan.FromMilliseconds(10));
        mediator.Handle(vm, new BackendLogLine("SRHQ_V12_PROGRESS|100|导出完成", new ProgressLine(100, "导出完成")));
        Assert.Equal(100, vm.Progress);
        Assert.Equal("导出完成", vm.StageText);
    }

    [Fact]
    public void ProgressGate_LateProgressAfterStop_NeverClobbersTheStoppedCard()
    {
        var clock = new FakeClock();
        QueueMediator mediator = Mediator(clock, gate: QueueMediator.UiProgressGate);
        TaskGroupViewModel vm = RunningCard(mediator);
        mediator.Handle(vm, new BackendLogLine("SRHQ_V12_PROGRESS|20|阶段A", new ProgressLine(20, "阶段A")));
        vm.GoFailed("已停止");

        clock.Advance(TimeSpan.FromMilliseconds(300));          // gate wide open
        mediator.Handle(vm, new BackendLogLine("SRHQ_V12_PROGRESS|40|阶段B", new ProgressLine(40, "阶段B")));

        Assert.Equal(TaskState.Failed, vm.State);
        Assert.Equal(24, vm.Progress);                          // stopped card not clobbered
        Assert.Equal("阶段A", vm.StageText);
        Assert.Contains("SRHQ_V12_PROGRESS|40|阶段B", vm.LogLines);  // but the line still logged (d)
    }

    // ---------------------------------------------------------------- fixture replay

    /// <summary>Replays the 16175-line real success capture through parser → mediator.
    /// The fake clock advances 0.5 ms per line (~8 s of simulated time): the gate is
    /// time-based, so the assertion bounds UI updates by gate openings, independent of
    /// line count. The coalesced pending updates plus the fixture's trailing 100 % line
    /// must land the final state exactly.</summary>
    [Fact]
    public void FixtureReplay_DefaultGate_CollapsesUiUpdates_FinalStateLands()
    {
        string[] lines = FixtureLines();
        var clock = new FakeClock();
        QueueMediator mediator = Mediator(clock, gate: QueueMediator.UiProgressGate);
        TaskGroupViewModel vm = RunningCard(mediator);
        using var counter = new ChangeCounter(vm);

        foreach (string line in lines)
        {
            clock.Advance(TimeSpan.FromMilliseconds(0.5));
            if (ToQueueEvent(ProtocolParser.ParseLine(line)) is { } e)
                mediator.Handle(vm, e);
        }

        // (1) final state landed: the run ends on SRHQ_V12_PROGRESS|100|导出完成.
        Assert.Equal(100, vm.Progress);
        Assert.Equal("导出完成", vm.StageText);

        // (2) 16175 lines → gate-bounded UI mutations, not per-line ones. The replay
        // simulates ~8 s of stream time ⇒ ~40 gate openings — the floor proves the gate
        // really reopens (a stuck-closed gate would collapse to first+terminal ≈ 2),
        // the ceiling is the flicker budget.
        Assert.InRange(counter.Count, 20, 150);

        // (d) log appends are never throttled: every non-envelope line landed.
        Assert.Equal(lines.Length - 2, vm.LogLines.Count);      // minus START + RESULT envelopes
    }

    /// <summary>Sanity for the disabled gate (TimeSpan.Zero ⇒ zero elapsed requirement ⇒
    /// every update forwards — the old pre-fix behavior): the change count must match a
    /// bare card replayed with the same unthrottled mapping EXACTLY, and stay an order
    /// of magnitude above the gated count.</summary>
    [Fact]
    public void FixtureReplay_GateDisabled_MatchesOldUnthrottledBehavior()
    {
        string[] lines = FixtureLines();

        var clock = new FakeClock();
        QueueMediator mediator = Mediator(clock, gate: TimeSpan.Zero);
        TaskGroupViewModel vm = RunningCard(mediator);
        using var counter = new ChangeCounter(vm);
        foreach (string line in lines)
        {
            clock.Advance(TimeSpan.FromMilliseconds(0.5));
            if (ToQueueEvent(ProtocolParser.ParseLine(line)) is { } e)
                mediator.Handle(vm, e);
        }

        // Baseline: the same replay against a bare card with the mediator's exact
        // pre-fix mapping (START sets the stage; every line appends; progress lines
        // ReportProgress — the code as it was before the gate).
        TaskGroupViewModel bare = RunningCard(Mediator(clock, gate: TimeSpan.Zero));
        using var baseline = new ChangeCounter(bare);
        foreach (string line in lines)
        {
            switch (ToQueueEvent(ProtocolParser.ParseLine(line)))
            {
                case BackendStarted s:
                    bare.ReportProgress(bare.Progress, s.Start.Stage);
                    break;
                case BackendLogLine l:
                    bare.LogLines.Add(l.Text);
                    if (l.Progress is { } p)
                        bare.ReportProgress(5 + (int)Math.Round(95 * p.Percent / 100.0), p.Stage);
                    break;
            }
        }

        Assert.Equal(bare.Progress, vm.Progress);               // both end on the last line's state
        Assert.Equal(bare.StageText, vm.StageText);
        Assert.Equal(baseline.Count, counter.Count);             // old behavior reproduced exactly
        Assert.True(counter.Count > 1000,
            $"expected the old per-line behavior to stay expensive, got {counter.Count}");
        Assert.Equal(lines.Length - 2, vm.LogLines.Count);
    }
}
