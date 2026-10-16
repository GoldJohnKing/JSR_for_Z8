using System.Collections.Concurrent;
using System.Diagnostics;
using Jsr4Z8.Core.Backend;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Ptc;
using Jsr4Z8.Core.Queue;
using Jsr4Z8.UI.Services;
using Jsr4Z8.UI.ViewModels;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// The sequential queue wiring — Core's TaskQueueRunner + UI's QueueMediator +
/// MainViewModel.StartAll — against the cmd.exe stub engines in fixtures\stub_backend\
/// (BackendRunnerTests already prove the process runner itself; these tests prove the
/// queue semantics): sequential one-at-a-time execution, failure/stop continuation,
/// per-run temp-dir lifecycle (EVERY terminal outcome deletes the job dir — 失败/停止即删),
/// the missing-product-DNG guard, the success-path product glob, and the mediator's
/// RunnerEvent → card-state mapping
/// (payload 0-5%, backend 5-100%). The payload and finalizer
/// are injected through TaskQueueRunner's internal seams; the backend itself stays
/// REAL (stub .bat through the same Process path as jsr_backend.exe — direct
/// invoke: argv in, bare lines out). Windows-host tests.
/// </summary>
public class QueueRunnerTests : IClassFixture<TestTemp>
{
    private readonly TestTemp _temp;

    public QueueRunnerTests(TestTemp temp) => _temp = temp;

    private static readonly string[] Frames =
        Enumerable.Range(7576, 7).Select(i => $@"C:\Photos\DSC_{i}.NEF").ToArray();


    /// <summary>Locates a stub .bat under fixtures\stub_backend\.</summary>
    private static string StubPath(string name) => SampleFiles.FindUp(Path.Combine("fixtures", "stub_backend", name));

    /// <summary>The no-leftover invariant: the Jsr4Z8 temp root holds no job dir. An
    /// absent root satisfies it — under parallel-class load a cancel can race ahead
    /// of the job-dir creation, and "nothing on disk" is exactly the desired end state
    /// (Assert.Empty on GetDirectories would throw on the missing root instead).</summary>
    private static void AssertNoJobDirLeft(string jsrTempRoot)
    {
        Assert.True(!Directory.Exists(jsrTempRoot) || Directory.GetDirectories(jsrTempRoot).Length == 0,
            $"job temp root must hold no job dir: {jsrTempRoot}");
    }

    /// <summary>Headless dispatcher: posts run inline (the tests then observe every
    /// mutation synchronously, in arrival order).</summary>
    private sealed class SyncDispatcher : IDispatcher
    {
        public void Post(Action action) => action();
    }

    /// <summary>One queue stack (engine + mediator) whose backend runs the given stub
    /// .bat engines in order (sequential queue ⇒ dequeue order = execution order),
    /// whose temp base is a test-scoped directory, and whose finalizer is a recording fake.</summary>
    private sealed class Harness
    {
        public readonly string TempBase;
        public readonly TaskQueueRunner Engine = new();
        public readonly QueueMediator Mediator;
        public readonly List<(string BackendDng, string ReferenceNef, string DestDir)> FinalizeCalls = new();

        public Harness(TestTemp temp, params string[] batNames)
        {
            TempBase = temp.NewDir();
            Engine.TempBaseOverride = TempBase;
            var bats = new ConcurrentQueue<string>(batNames.Select(StubPath));
            Engine.ResolveBackendOverride = progress =>
            {
                progress?.Report(1.0); // payload phase completes → 0-5% card progress
                bats.TryDequeue(out string? bat);
                return new TaskQueueRunner.BackendPayload(bat ?? StubPath("stub_backend.bat"));
            };
            Engine.FinalizeOverride = (backendDng, referenceNef, destDir) =>
            {
                lock (FinalizeCalls)
                    FinalizeCalls.Add((backendDng, referenceNef, destDir));
                return Path.Combine(destDir, "JSR_fake.dng");
            };
            Mediator = new QueueMediator(Engine, new SyncDispatcher());
        }

        /// <summary>The Jsr4Z8 job-temp root under this harness's temp base (job dirs land inside).</summary>
        public string JsrTempRoot => Path.Combine(TempBase, "Jsr4Z8");
    }

    /// <summary>A Ready card wired to the harness mediator (validation carries the PTC match).</summary>
    private static TaskGroupViewModel Card(QueueMediator runner) => new(
        new TaskGroup(Frames),
        new ValidationResult(true, Array.Empty<ValidationIssue>(), new PtcMatch(9512, true, "表值"),
            new Jsr4Z8.Core.Dark.DarkMatch(3200, 1005.89, 14.54, "preset")),
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

    // ---------------------------------------------------------------- engine: payload path

    /// <summary>Locks the engine path join character-exact: the engine executable is
    /// the pre-extracted runtime tree's root copy resources\jsr_backend_runtime\jsr_backend.exe
    /// (pack-time CArchive pre-extraction, PayloadPacker.EngineRuntimeDir) — the _PYI_*
    /// injection contract runs THAT exe against THAT tree. Any other spelling (e.g. the
    /// old onefile resources\jsr_backend.exe) silently breaks the injection: the
    /// bootloader's reset block strips the vars and every run self-extracts again.</summary>
    [Fact]
    public void EngineRelativePath_PinsPreextractedTreeJoin()
    {
        Assert.Equal(@"resources\jsr_backend_runtime\jsr_backend.exe", TaskQueueRunner.EngineRelativePath);
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
            Assert.Contains("JSR_fake.dng", vm.MetaText);       // Done meta line carries the written path

            // Finalize received the **/<prefix>*.dng glob hit, the reference NEF and the SameAsInput dest dir.
            (string backendDng, string referenceNef, string destDir) call;
            lock (h.FinalizeCalls)
                call = Assert.Single(h.FinalizeCalls);
            Assert.Equal(Frames[0], call.referenceNef);
            Assert.Equal(Path.GetDirectoryName(Frames[0]), call.destDir);
            Assert.Contains("_V15_3_5_K7", call.backendDng);                 // found inside the nested product dir
            Assert.EndsWith(".dng", call.backendDng);

            // Success policy: the job temp dir (job.log / engine product) is deleted.
            Assert.False(Directory.Exists(h.JsrTempRoot) && Directory.EnumerateFileSystemEntries(h.JsrTempRoot).Any(),
                $"job temp dir must be deleted after success: {h.JsrTempRoot}");
        }
        finally
        {
            try { Directory.Delete(h.TempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task BackendCompleteWithoutDng_Fails_DeletesTemp_FinalizerNotCalled()
    {
        var h = new Harness(_temp, "stub_backend.bat"); // bare success exit 0 but no product file
        TaskGroupViewModel vm = Card(h.Mediator);
        try
        {
            await vm.StartAsync();

            Assert.Equal(TaskState.Failed, vm.State);
            Assert.Contains("后端未产出 DNG", vm.ErrorText);
            Assert.DoesNotContain("诊断目录：", vm.ErrorText);   // temp is deleted → no hint
            lock (h.FinalizeCalls) Assert.Empty(h.FinalizeCalls);

            AssertNoJobDirLeft(h.JsrTempRoot);   // temp root holds no leftover dir
        }
        finally
        {
            try { Directory.Delete(h.TempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task BackendFailure_DeletesTempDir_HumanizedMessage()
    {
        var h = new Harness(_temp, "stub_fail.bat");
        TaskGroupViewModel vm = Card(h.Mediator);
        try
        {
            await vm.StartAsync();

            Assert.Equal(TaskState.Failed, vm.State);
            Assert.Contains("模拟失败原因", vm.ErrorText);   // SRHQ_NET_ABORT extract, not the raw tail
            Assert.DoesNotContain("MemoryError", vm.ErrorText);
            Assert.DoesNotContain("诊断目录：", vm.ErrorText);
            lock (h.FinalizeCalls) Assert.Empty(h.FinalizeCalls);
            AssertNoJobDirLeft(h.JsrTempRoot);   // failure now deletes the job dir
        }
        finally
        {
            try { Directory.Delete(h.TempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A queue-level stop mid-run must ALSO delete the job temp dir (the
    /// stopped run's diagnostics die with it).</summary>
    [Fact]
    public async Task CancelledRun_DeletesTempDir()
    {
        var h = new Harness(_temp, "stub_kill.bat");
        var main = new MainViewModel(null, h.Mediator);
        TaskGroupViewModel vm = Card(h.Mediator);
        main.Add(vm);
        try
        {
            main.StartAllCommand.Execute(null);
            Assert.True(await WaitAsync(() => main.ActiveQueueCts != null && vm.State == TaskState.Running, TimeSpan.FromSeconds(8)),
                "task never started");

            main.ActiveQueueCts!.Cancel();
            Assert.True(await WaitAsync(() => vm.State == TaskState.Failed, TimeSpan.FromSeconds(3)),
                "cancelled task must reach Failed within 3s");
            Assert.Contains("已停止", vm.ErrorText);
            Assert.DoesNotContain("诊断目录：", vm.ErrorText);
            Assert.True(await WaitAsync(() => main.ActiveQueueCts == null, TimeSpan.FromSeconds(5)), "queue loop never exited");

            AssertNoJobDirLeft(h.JsrTempRoot);   // stopped run leaves nothing behind
        }
        finally
        {
            try { Directory.Delete(h.TempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- engine: startup temp-root sweep

    /// <summary>Startup sweep: failed/stopped runs delete their job dir themselves,
    /// so anything left under the Jsr4Z8 temp root is debris from a crashed/killed
    /// process — the sweep deletes ALL of it once past the grace window (any fabricated
    /// mtime older than 2 minutes is swept).</summary>
    [Fact]
    public void SweepTempRoot_DeletesAllLeftovers_OlderThanGraceWindow()
    {
        string tempBase = _temp.NewDir();
        string root = Path.Combine(tempBase, "Jsr4Z8");
        string old = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string fresh = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string current = Path.Combine(root, "currentjob");
        foreach (string dir in new[] { old, fresh, current })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "job.log"), "{}");
        }
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromDays(8));
        Directory.SetLastWriteTimeUtc(fresh, DateTime.UtcNow - TimeSpan.FromDays(1));
        Directory.SetLastWriteTimeUtc(current, DateTime.UtcNow - TimeSpan.FromDays(8));
        try
        {
            TaskQueueRunner.SweepTempRoot(tempBase);

            Assert.False(Directory.Exists(old), "8-day-old leftover must be swept");
            Assert.False(Directory.Exists(fresh), "1-day-old leftover must be swept too — no age cutoff beyond the grace window");
            Assert.False(Directory.Exists(current), "no id exclusion: everything past the grace window goes");
            Assert.Empty(Directory.GetDirectories(root));   // the root itself survives, emptied
        }
        finally
        {
            try { Directory.Delete(tempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The sweep skips dirs whose LastWriteTimeUtc is within
    /// the grace window — the same-instance race where a slow multi-GB sweep is still
    /// iterating when the user starts the first run seconds after launch (the fresh job
    /// dir must survive). A dir older than the window is swept.</summary>
    [Fact]
    public void SweepTempRoot_GraceWindow_KeepsFreshDir_SweepsOlder()
    {
        string tempBase = _temp.NewDir();
        string root = Path.Combine(tempBase, "Jsr4Z8");
        string fresh = Path.Combine(root, "just-created-job");   // keeps its real (current) mtime
        string old = Path.Combine(root, Guid.NewGuid().ToString("N"));
        foreach (string dir in new[] { fresh, old })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "job.log"), "{}");
        }
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromDays(8));
        try
        {
            TaskQueueRunner.SweepTempRoot(tempBase);

            Assert.True(Directory.Exists(fresh), "dir written within the grace window must be kept");
            Assert.False(Directory.Exists(old), "dir older than the grace window must be swept");
            Assert.Equal(new[] { fresh }, Directory.GetDirectories(root));   // exactly the fresh one remains
        }
        finally
        {
            try { Directory.Delete(tempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- engine: success-path product glob

    /// <summary>The success-path product glob must skip any path that traverses a
    /// *.partial segment — the backend's resume leftovers — even when the file inside
    /// is NEWER than the real product.</summary>
    [Fact]
    public void FindNewestProductDng_SkipsPartialSegments_EvenWhenNewer()
    {
        string root = _temp.NewDir();
        string Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
            return path;
        }

        string real = Write(Path.Combine(root, "job_V15_3_5_K7_1_1", "job_product.dng"));
        string partialTop = Write(Path.Combine(root, "job_dir.partial", "job_newer.dng"));
        string partialNested = Write(Path.Combine(root, "nested", "job_b.partial", "deep", "job_newest.dng"));
        DateTime now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(real, now.AddMinutes(-1));          // real product is the OLDEST
        File.SetLastWriteTimeUtc(partialTop, now);
        File.SetLastWriteTimeUtc(partialNested, now.AddSeconds(30));

        Assert.Equal(real, TaskQueueRunner.FindNewestProductDng(root, "job"));
    }

    [Fact]
    public void FindNewestProductDng_StillPicksNewestProduct()
    {
        string root = _temp.NewDir();
        string Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
            return path;
        }

        string older = Write(Path.Combine(root, "job_V15_3_5_K7_1_1", "job_old.dng"));
        string newer = Write(Path.Combine(root, "job_V15_3_5_K7_9_9", "job_new.dng"));
        DateTime now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(older, now.AddMinutes(-5));
        File.SetLastWriteTimeUtc(newer, now);

        Assert.Equal(newer, TaskQueueRunner.FindNewestProductDng(root, "job"));
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
        var h = new Harness(_temp, "stub_kill.bat", "stub_dng.bat");
        var main = new MainViewModel(null, h.Mediator);
        TaskGroupViewModel vm1 = Card(h.Mediator), vm2 = Card(h.Mediator);
        main.Add(vm1);
        main.Add(vm2);
        try
        {
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
            try { Directory.Delete(h.TempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task StartAll_StopCurrentTask_QueueContinuesWithRemaining()
    {
        var h = new Harness(_temp, "stub_kill.bat", "stub_dng.bat");
        var main = new MainViewModel(null, h.Mediator);
        TaskGroupViewModel vm1 = Card(h.Mediator), vm2 = Card(h.Mediator);
        main.Add(vm1);
        main.Add(vm2);
        try
        {
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
            try { Directory.Delete(h.TempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- single-flight

    [Fact]
    public async Task PerCardStart_DisabledWhileQueueBusy_ConcurrentEntryIsNoOp()
    {
        var h = new Harness(_temp, "stub_kill.bat");
        TaskGroupViewModel vm1 = Card(h.Mediator), vm2 = Card(h.Mediator);
        try
        {
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
            try { Directory.Delete(h.TempBase, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>QueueMediator's RunnerEvent → card mapping (headless, synchronous
/// dispatcher): payload 0-5%, backend percent 5-100, stage text, terminal
/// states with humanized messages (backend log lines are consumed for progress only —
/// the card carries no log surface). progressGate: TimeSpan.Zero disables
/// the UI throttle — these tests pin the pure event→percent mapping over a fast stub
/// whose lines all arrive inside the default 200 ms gate; the throttle itself is pinned
/// by ProgressThrottleTests.</summary>
public class QueueMediatorTests : IClassFixture<TestTemp>
{
    private readonly TestTemp _temp;

    public QueueMediatorTests(TestTemp temp) => _temp = temp;

    private static readonly string[] Frames =
        Enumerable.Range(7576, 7).Select(i => $@"C:\Photos\DSC_{i}.NEF").ToArray();


    private sealed class SyncDispatcher : IDispatcher
    {
        public void Post(Action action) => action();
    }

    private static TaskGroupViewModel Card(ITaskRunner runner, PtcMatch? ptc = null) => new(
        new TaskGroup(Frames),
        new ValidationResult(true, Array.Empty<ValidationIssue>(), ptc, new Jsr4Z8.Core.Dark.DarkMatch(3200, 1005.89, 14.54, "preset")),
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
            return new TaskQueueRunner.BackendPayload(StubPath(bat));
        };
        engine.FinalizeOverride = (backendDng, referenceNef, destDir) => Path.Combine(destDir, "JSR_fake.dng");
        // Job dirs land under the engine's overridden temp base — per-run GUID under
        // the fixture, never a fixed %TEMP% name that would survive the run.
        engine.TempBaseOverride = tempBase;
        return (new QueueMediator(engine, new SyncDispatcher(), progressGate: TimeSpan.Zero), tempBase);
    }

    [Fact]
    public async Task EventMapping_Payload0To5_Backend5To100_StageText()
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
