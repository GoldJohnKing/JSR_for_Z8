using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
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
/// Task 13: the sequential queue wiring — Core's TaskQueueRunner + UI's QueueMediator +
/// MainViewModel.StartAll — against the cmd.exe stub harnesses in fixtures\stub_backend\
/// (BackendRunnerTests already prove the process runner itself; these tests prove the
/// queue semantics): sequential one-at-a-time execution, failure/stop continuation,
/// per-run temp-dir lifecycle (success deletes, failure keeps for diagnosis), the
/// missing-product-DNG guard, and the mediator's RunnerEvent → card-state mapping
/// (payload 0-5%, backend 5-100%, 诊断目录 failure hint). The payload and finalizer
/// are injected through TaskQueueRunner's internal seams; the backend itself stays
/// REAL (stub .bat through the same Process path as "Jiangtherapee Bridge.exe").
/// Windows-host tests.
/// </summary>
public class QueueRunnerTests : IClassFixture<TestTemp>
{
    private readonly TestTemp _temp;

    public QueueRunnerTests(TestTemp temp) => _temp = temp;

    private static readonly string[] Frames =
        Enumerable.Range(7576, 7).Select(i => $@"C:\Photos\DSC_{i}.NEF").ToArray();

    private const string DarkPath = @"C:\Photos\DSC_7575.NEF";

    /// <summary>Locates a stub .bat under fixtures\stub_backend\.</summary>
    private static string StubPath(string name) => SampleFiles.FindUp(Path.Combine("fixtures", "stub_backend", name));

    /// <summary>Headless dispatcher: posts run inline (the tests then observe every
    /// mutation synchronously, in arrival order).</summary>
    private sealed class SyncDispatcher : IDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class FakeSettings : ISettingsProvider
    {
        public AppSettings CurrentSettings { get; set; } = new();
    }

    /// <summary>One queue stack (engine + mediator) whose backend runs the given stub
    /// .bat harnesses in order (sequential queue ⇒ dequeue order = execution order),
    /// whose temp base is a test-scoped directory, and whose finalizer is a recording fake.</summary>
    private sealed class Harness
    {
        public readonly string TempBase;
        public readonly FakeSettings Settings = new();
        public readonly TaskQueueRunner Engine = new();
        public readonly QueueMediator Mediator;
        public readonly List<(string BackendDng, string ReferenceNef, string DestDir)> FinalizeCalls = new();

        public Harness(TestTemp temp, params string[] batNames)
        {
            TempBase = temp.NewDir();
            Settings.CurrentSettings = new AppSettings(TempMode: TempMode.Custom, CustomTempDir: TempBase);
            var bats = new ConcurrentQueue<string>(batNames.Select(StubPath));
            Engine.ResolveBackendOverride = progress =>
            {
                progress?.Report(1.0); // payload phase completes → 0-5% card progress
                bats.TryDequeue(out string? bat);
                return new TaskQueueRunner.BackendPayload(bat ?? StubPath("stub_backend.bat"), TempBase);
            };
            Engine.FinalizeOverride = (backendDng, referenceNef, destDir) =>
            {
                lock (FinalizeCalls)
                    FinalizeCalls.Add((backendDng, referenceNef, destDir));
                return Path.Combine(destDir, "JSR_fake.dng");
            };
            Mediator = new QueueMediator(Engine, Settings, new SyncDispatcher());
        }

        /// <summary>The Jsr4Z8 job-temp root under this harness's temp base (job dirs land inside).</summary>
        public string JsrTempRoot => Path.Combine(TempBase, "Jsr4Z8");
    }

    /// <summary>A Ready card wired to the harness mediator (validation carries the PTC match).</summary>
    private static TaskGroupViewModel Card(QueueMediator runner) => new(
        new TaskGroup(Frames, DarkPath),
        new ValidationResult(true, Array.Empty<ValidationIssue>(), new PtcMatch(9512, true, "表值")),
        runner);

    /// <summary>Polls <paramref name="condition"/> until true or the deadline passes; true on the final check too.</summary>
    private static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }

    // ---------------------------------------------------------------- engine: temp-dir lifecycle

    [Fact]
    public async Task Success_DeletesTempDir_FinalizesGlobbedDng_WritesOutputNextToReference()
    {
        var h = new Harness(_temp, "stub_dng.bat");
        TaskGroupViewModel vm = Card(h.Mediator);
        try
        {
            await vm.StartAsync();

            Assert.Equal(TaskState.Done, vm.State);
            Assert.Equal(100, vm.Progress);
            Assert.Equal(Path.Combine(Path.GetDirectoryName(Frames[0])!, "JSR_fake.dng"), vm.OutputPath);
            Assert.Contains("已输出", vm.MetaText);
            Assert.Contains(vm.LogLines, l => l.Contains("模拟后端启动"));   // backend lines forwarded to the card log
            Assert.Contains(vm.LogLines, l => l.Contains("已输出"));        // [完成] line with the final path

            // Finalize received the **/<prefix>*.dng glob hit, the reference NEF and the SameAsInput dest dir.
            (string backendDng, string referenceNef, string destDir) call;
            lock (h.FinalizeCalls)
                call = Assert.Single(h.FinalizeCalls);
            Assert.Equal(Frames[0], call.referenceNef);
            Assert.Equal(Path.GetDirectoryName(Frames[0]), call.destDir);
            Assert.Contains("_V15_3_5_K7", call.backendDng);                 // found inside the nested product dir
            Assert.EndsWith(".dng", call.backendDng);

            // Success policy: the job temp dir (request.json / cfg / job.log) is deleted.
            Assert.False(Directory.Exists(h.JsrTempRoot) && Directory.EnumerateFileSystemEntries(h.JsrTempRoot).Any(),
                $"job temp dir must be deleted after success: {h.JsrTempRoot}");
        }
        finally
        {
            try { Directory.Delete(h.TempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task BackendCompleteWithoutDng_Fails_KeepsTemp_FinalizerNotCalled()
    {
        var h = new Harness(_temp, "stub_backend.bat"); // RESULT complete but no product file
        TaskGroupViewModel vm = Card(h.Mediator);
        try
        {
            await vm.StartAsync();

            Assert.Equal(TaskState.Failed, vm.State);
            Assert.Contains("后端未产出 DNG", vm.ErrorText);
            Assert.Contains("诊断目录：", vm.ErrorText);   // temp kept → hint names it
            lock (h.FinalizeCalls) Assert.Empty(h.FinalizeCalls);

            string[] kept = Directory.GetDirectories(h.JsrTempRoot);
            string jobDir = Assert.Single(kept);
            Assert.True(File.Exists(Path.Combine(jobDir, "job.log")), "runner log must be in the kept temp dir");
            Assert.True(File.Exists(Path.Combine(jobDir, "request.json")));
            Assert.True(Directory.Exists(Path.Combine(jobDir, "cfg")));   // per-run config dir
        }
        finally
        {
            try { Directory.Delete(h.TempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task BackendFailure_KeepsTempDir_HumanizedMessageWithDiagnosticHint()
    {
        var h = new Harness(_temp, "stub_fail.bat");
        TaskGroupViewModel vm = Card(h.Mediator);
        try
        {
            await vm.StartAsync();

            Assert.Equal(TaskState.Failed, vm.State);
            Assert.Contains("模拟失败原因", vm.ErrorText);   // SRHQ_NET_ABORT extract, not the raw tail
            Assert.DoesNotContain("MemoryError", vm.ErrorText);
            Assert.Contains("诊断目录：", vm.ErrorText);
            lock (h.FinalizeCalls) Assert.Empty(h.FinalizeCalls);
            Assert.Single(Directory.GetDirectories(h.JsrTempRoot));
        }
        finally
        {
            try { Directory.Delete(h.TempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- engine: stale diagnostic-dir sweep

    /// <summary>Final-review finding 4: failed/stopped runs keep ~3 GB diagnostic dirs
    /// that would accumulate forever — the sweep deletes job dirs older than 7 days
    /// (mtime-fabricated: -8 days deleted, -1 day kept) and never touches the current
    /// job's dir even when it is old.</summary>
    [Fact]
    public void SweepStaleDiagnosticDirs_DeletesOld_KeepsFreshAndExcluded()
    {
        string tempBase = _temp.NewDir();
        string root = Path.Combine(tempBase, "Jsr4Z8");
        string old = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string fresh = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string current = Path.Combine(root, "currentjob");
        foreach (string dir in new[] { old, fresh, current })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "request.json"), "{}");
        }
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromDays(8));
        Directory.SetLastWriteTimeUtc(fresh, DateTime.UtcNow - TimeSpan.FromDays(1));
        Directory.SetLastWriteTimeUtc(current, DateTime.UtcNow - TimeSpan.FromDays(8));
        var settings = new AppSettings(TempMode: TempMode.Custom, CustomTempDir: tempBase);
        try
        {
            TaskQueueRunner.SweepStaleDiagnosticDirs(settings, excludeJobId: "currentjob");

            Assert.False(Directory.Exists(old), "8-day-old diagnostic dir must be swept");
            Assert.True(Directory.Exists(fresh), "1-day-old dir must be kept");
            Assert.True(Directory.Exists(current), "current job's dir must never be swept");
        }
        finally
        {
            try { Directory.Delete(tempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- queue: sequential + continuation

    [Fact]
    public async Task StartAll_TwoTasks_SecondStartsAfterFirstFinishes()
    {
        var h = new Harness(_temp, "stub_dng.bat", "stub_dng.bat");
        var main = new MainViewModel(null, h.Mediator);
        TaskGroupViewModel vm1 = Card(h.Mediator), vm2 = Card(h.Mediator);
        main.Add(vm1);
        main.Add(vm2);

        long t1Finished = 0, t2Started = 0;
        TaskState vm1StateAtVm2Start = TaskState.Ready;
        h.Mediator.EventObserved += (vm, e) =>
        {
            if (e is BackendFinished && vm == vm1) t1Finished = Stopwatch.GetTimestamp();
            if (e is BackendStarted && vm == vm2)
            {
                t2Started = Stopwatch.GetTimestamp();
                vm1StateAtVm2Start = vm1.State; // terminal already ⇒ the second started after the first finished
            }
        };

        main.StartAllCommand.Execute(null);
        Assert.True(await WaitAsync(() => vm1.State == TaskState.Done && vm2.State == TaskState.Done, TimeSpan.FromSeconds(20)),
            $"queue did not finish both: vm1={vm1.State} vm2={vm2.State}");

        Assert.True(t2Started >= t1Finished, $"second Started ({t2Started}) must follow first Finished ({t1Finished})");
        Assert.True(vm1StateAtVm2Start is TaskState.Done or TaskState.Failed,
            $"first card must be terminal when the second starts, was {vm1StateAtVm2Start}");
    }

    [Fact]
    public async Task StartAll_FirstFails_SecondStillRuns()
    {
        var h = new Harness(_temp, "stub_fail.bat", "stub_dng.bat");
        var main = new MainViewModel(null, h.Mediator);
        TaskGroupViewModel vm1 = Card(h.Mediator), vm2 = Card(h.Mediator);
        main.Add(vm1);
        main.Add(vm2);

        main.StartAllCommand.Execute(null);
        Assert.True(await WaitAsync(() => vm1.State == TaskState.Failed && vm2.State == TaskState.Done, TimeSpan.FromSeconds(20)),
            $"failure must not block later groups: vm1={vm1.State} vm2={vm2.State}");
    }

    [Fact]
    public async Task StartAll_QueueLevelCancel_StopsCurrentWithin3s_LaterTasksNotStarted()
    {
        string tick = _temp.NewFilePath(".tick");
        var h = new Harness(_temp, "stub_kill.bat", "stub_dng.bat");
        var main = new MainViewModel(null, h.Mediator);
        TaskGroupViewModel vm1 = Card(h.Mediator), vm2 = Card(h.Mediator);
        main.Add(vm1);
        main.Add(vm2);
        try
        {
            Environment.SetEnvironmentVariable("JSR4Z8_TEST_TICK", tick);
            main.StartAllCommand.Execute(null);
            Assert.True(await WaitAsync(() => main.ActiveQueueCts != null && vm1.State == TaskState.Running, TimeSpan.FromSeconds(8)),
                "first task never started");

            main.ActiveQueueCts!.Cancel();
            Assert.True(await WaitAsync(() => vm1.State == TaskState.Failed, TimeSpan.FromSeconds(3)),
                "current task must be stopped within 3s of the queue-level cancel");
            Assert.Contains("已停止", vm1.ErrorText);
            Assert.Equal("已停止", vm1.StageText);       // stopped-task ruling: Failed state, 已停止 stage
            Assert.True(vm1.RetryCommand.CanExecute(null)); // Retry stays available

            Assert.True(await WaitAsync(() => main.ActiveQueueCts == null, TimeSpan.FromSeconds(5)), "queue loop never exited");
            await Task.Delay(300);                       // grace: a late start of vm2 would land here
            Assert.Equal(TaskState.Ready, vm2.State);    // later tasks are never started
        }
        finally
        {
            Environment.SetEnvironmentVariable("JSR4Z8_TEST_TICK", null);
            try { File.Delete(tick); } catch (IOException) { }
            try { Directory.Delete(h.TempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task StartAll_StopCurrentTask_QueueContinuesWithRemaining()
    {
        string tick = _temp.NewFilePath(".tick");
        var h = new Harness(_temp, "stub_kill.bat", "stub_dng.bat");
        var main = new MainViewModel(null, h.Mediator);
        TaskGroupViewModel vm1 = Card(h.Mediator), vm2 = Card(h.Mediator);
        main.Add(vm1);
        main.Add(vm2);
        try
        {
            Environment.SetEnvironmentVariable("JSR4Z8_TEST_TICK", tick);
            main.StartAllCommand.Execute(null);
            Assert.True(await WaitAsync(() => vm1.State == TaskState.Running, TimeSpan.FromSeconds(8)),
                "first task never started");

            vm1.StopCommand.Execute(null);               // per-card stop: only this run's token
            Assert.True(await WaitAsync(() => vm1.State == TaskState.Failed, TimeSpan.FromSeconds(3)),
                "stopped card must reach Failed within 3s");
            Assert.Contains("已停止", vm1.ErrorText);

            // The queue continues with the remaining tasks.
            Assert.True(await WaitAsync(() => vm2.State == TaskState.Done, TimeSpan.FromSeconds(20)),
                $"queue must continue after a stop: vm2={vm2.State}");
            Assert.Equal(TaskState.Failed, vm1.State);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JSR4Z8_TEST_TICK", null);
            try { File.Delete(tick); } catch (IOException) { }
            try { Directory.Delete(h.TempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- single-flight

    [Fact]
    public async Task PerCardStart_DisabledWhileQueueBusy_ConcurrentEntryIsNoOp()
    {
        string tick = _temp.NewFilePath(".tick");
        var h = new Harness(_temp, "stub_kill.bat");
        TaskGroupViewModel vm1 = Card(h.Mediator), vm2 = Card(h.Mediator);
        try
        {
            Environment.SetEnvironmentVariable("JSR4Z8_TEST_TICK", tick);
            Task run1 = vm1.StartAsync();
            Assert.True(await WaitAsync(() => h.Mediator.IsBusy && vm1.State == TaskState.Running, TimeSpan.FromSeconds(8)),
                "first task never started");

            // Single-flight contract: another card's 开始此组 is disabled while the queue is busy,
            // and a forced concurrent entry is a no-op (card not run, left Ready).
            Assert.False(vm2.StartCommand.CanExecute(null));
            Assert.False(vm2.RetryCommand.CanExecute(null));
            await h.Mediator.RunAsync(vm2, CancellationToken.None);
            Assert.Equal(TaskState.Ready, vm2.State);

            vm1.StopCommand.Execute(null);
            Assert.True(await WaitAsync(() => vm1.State == TaskState.Failed, TimeSpan.FromSeconds(3)));
            await run1;
            Assert.False(h.Mediator.IsBusy);
            Assert.True(vm2.StartCommand.CanExecute(null));  // re-enabled once idle
        }
        finally
        {
            Environment.SetEnvironmentVariable("JSR4Z8_TEST_TICK", null);
            try { File.Delete(tick); } catch (IOException) { }
            try { Directory.Delete(h.TempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Task 13: QueueMediator's RunnerEvent → card mapping (headless, synchronous
/// dispatcher): payload 0-5%, backend percent 5-100, log lines, stage text, terminal
/// states with humanized messages. progressGate: TimeSpan.Zero disables the UI
/// throttle — these tests pin the pure event→percent mapping over a fast stub whose
/// lines all arrive inside the default 200 ms gate; the throttle itself is pinned by
/// ProgressThrottleTests.</summary>
public class QueueMediatorTests : IClassFixture<TestTemp>
{
    private readonly TestTemp _temp;

    public QueueMediatorTests(TestTemp temp) => _temp = temp;

    private static readonly string[] Frames =
        Enumerable.Range(7576, 7).Select(i => $@"C:\Photos\DSC_{i}.NEF").ToArray();

    private const string DarkPath = @"C:\Photos\DSC_7575.NEF";

    private sealed class SyncDispatcher : IDispatcher
    {
        public void Post(Action action) => action();
    }

    private static TaskGroupViewModel Card(ITaskRunner runner, PtcMatch? ptc = null) => new(
        new TaskGroup(Frames, DarkPath),
        new ValidationResult(true, Array.Empty<ValidationIssue>(), ptc),
        runner);

    private static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }

    private static string StubPath(string name) => SampleFiles.FindUp(Path.Combine("fixtures", "stub_backend", name));

    /// <summary>Engine pointed at one stub, no temp-dir assertions — pure event mapping.
    /// The finalizer is faked so the mapping test observes the mediator, not the real
    /// DNG pipeline (FinalizerTests cover that).</summary>
    private (QueueMediator Mediator, string TempBase) MediatorWithStub(string bat)
    {
        string tempBase = _temp.NewDir();
        var engine = new TaskQueueRunner();
        engine.ResolveBackendOverride = progress =>
        {
            progress?.Report(1.0); // payload phase completes → 0-5% card progress
            return new TaskQueueRunner.BackendPayload(StubPath(bat), tempBase);
        };
        engine.FinalizeOverride = (backendDng, referenceNef, destDir) => Path.Combine(destDir, "JSR_fake.dng");
        // Job dirs land under the settings temp base — per-run GUID under the fixture,
        // never a fixed %TEMP% name that would survive the run.
        var settings = new FakeSettingsProvider(_temp.NewDir());
        return (new QueueMediator(engine, settings, new SyncDispatcher(), progressGate: TimeSpan.Zero), tempBase);
    }

    private sealed class FakeSettingsProvider : ISettingsProvider
    {
        public AppSettings CurrentSettings { get; }

        public FakeSettingsProvider(string tempDir) =>
            CurrentSettings = new AppSettings(TempMode: TempMode.Custom, CustomTempDir: tempDir);
    }

    [Fact]
    public async Task EventMapping_Payload0To5_Backend5To100_StageAndLogLines()
    {
        var (mediator, tempBase) = MediatorWithStub("stub_dng.bat");
        try
        {
            int progressAtStart = -1, progressAtFinish = -1;
            mediator.EventObserved += (vm, e) =>
            {
                if (e is BackendStarted) progressAtStart = vm.Progress;
                if (e is BackendFinished) progressAtFinish = vm.Progress;
            };
            TaskGroupViewModel vm = Card(mediator, new PtcMatch(9512, true, "表值"));

            await vm.StartAsync();

            // Payload phase completed → exactly the 5% ceiling before the backend reports anything.
            Assert.Equal(5, progressAtStart);
            // stub's SRHQ_V12_PROGRESS|40 → 5 + 95*40/100 = 43 (ReportProgress clamps the rest).
            Assert.Equal(43, progressAtFinish);
            Assert.Equal(100, vm.Progress);                    // Done pins 100
            Assert.Equal("模拟阶段", vm.StageText);            // backend stage became the badge text
            Assert.Equal(TaskState.Done, vm.State);
            Assert.Contains("模拟后端启动", vm.LogLines);
        }
        finally
        {
            try { Directory.Delete(tempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task MissingPtc_FailsWithClearMessage()
    {
        var (mediator, tempBase) = MediatorWithStub("stub_dng.bat");
        try
        {
            TaskGroupViewModel vm = Card(mediator, ptc: null);
            await vm.StartAsync();
            Assert.Equal(TaskState.Failed, vm.State);
            Assert.Contains("PTC", vm.ErrorText);
            Assert.False(Directory.Exists(Path.Combine(tempBase, "Jsr4Z8")));
            Assert.False(mediator.IsBusy); // guard path released the single-flight gate
        }
        finally
        {
            try { Directory.Delete(tempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
