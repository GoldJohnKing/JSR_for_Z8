using System.Text;
using Jsr4Z8.Core.Backend;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Task 7: BackendRunner against the cmd.exe stubs in fixtures\stub_backend\
/// (success / fail / long-running-with-heartbeat-child / dies-without-RESULT-and-a-detached-orphan). The stubs replay the
/// measured wire shape of "Jiangtherapee Bridge.exe --assistant-jsr-job" and
/// spawn their own children, so cancellation must kill the whole process tree
/// (taskkill /T /F), and every streamed line must land in the run log file.
/// Windows-host tests: the stubs are .bat files driven through the same
/// Process path the real bridge executable will use.
/// </summary>
public class BackendRunnerTests
{
    /// <summary>Locates a stub .bat under fixtures\stub_backend\ by walking up from the test output directory.</summary>
    private static string StubPath(string name)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "fixtures", "stub_backend", name);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException($"Stub not found: fixtures\\stub_backend\\{name} (searched upwards from {AppContext.BaseDirectory})");
    }

    /// <summary>A run spec pointing at a stub harness, with temp resources/config/log paths and a minimal request JSON.</summary>
    private static BackendRunSpec NewSpec(string batName)
    {
        string root = Path.Combine(Path.GetTempPath(), "jsr4z8_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "resources"));
        File.WriteAllText(Path.Combine(root, "request.json"), "{\"paths\":[],\"parameters\":{}}");
        return new BackendRunSpec(
            BridgeExe: StubPath(batName),
            ResourcesDir: Path.Combine(root, "resources"),
            ConfigDir: Path.Combine(root, "config"), // must not exist yet: the runner creates the per-run lock dir
            RequestJsonPath: Path.Combine(root, "request.json"),
            LogFile: Path.Combine(root, "run.log"));
    }

    [Fact]
    public async Task FullRun_StubBackend_StreamsStartProgressResultAndPersistsLog()
    {
        BackendRunSpec spec = NewSpec("stub_backend.bat");
        var events = new List<RunnerEvent>();

        RunnerResult result = await new BackendRunner().RunAsync(spec, events.Add, CancellationToken.None);

        Assert.True(result.Success, $"Message: {result.Message}");
        Assert.True(result.Elapsed > TimeSpan.Zero);

        // Started event carries the parsed START json.
        Assert.Contains(events, e => e is Started { Json: { JobId: "stub-1", State: "running", Engine: "stormer7" } });

        // At least one LogLine carries the bare SRHQ progress line (42 / 模拟阶段).
        Assert.Contains(events, e => e is LogLine { Progress: { Percent: 42, Stage: "模拟阶段" } });

        // Exactly one Finished event, last, wrapping the returned result.
        Finished finished = Assert.IsType<Finished>(events[^1]);
        Assert.True(finished.Result.Success);

        // Log persistence: every streamed line (envelope + progress) is in the file.
        Assert.True(File.Exists(spec.LogFile));
        string log = File.ReadAllText(spec.LogFile, Encoding.UTF8);
        Assert.Contains("ASSISTANT_JSR_START|", log);
        Assert.Contains("SRHQ_V12_PROGRESS|42|模拟阶段", log);
        Assert.Contains("done", log); // RESULT envelope line is persisted too

        // The runner creates the per-run config dir (exclusive-lock territory, fact #3).
        Assert.True(Directory.Exists(spec.ConfigDir), $"config dir missing: {spec.ConfigDir}");
    }

    [Fact]
    public async Task Fail_StubFail_ExtractsNetAbortMessageFromTail()
    {
        BackendRunSpec spec = NewSpec("stub_fail.bat");
        var events = new List<RunnerEvent>();

        RunnerResult result = await new BackendRunner().RunAsync(spec, events.Add, CancellationToken.None);

        // RESULT state=failed + host exit 1 → failure.
        Assert.False(result.Success);

        // Message is the humanized extract: SRHQ_NET_ABORT msg beats the raw MemoryError line.
        Assert.Contains("模拟失败原因", result.Message);
        Assert.DoesNotContain("MemoryError", result.Message);

        // stderr is part of the tail: surfaced as a prefixed LogLine and persisted to the log file.
        Assert.Contains(events, e => e is LogLine { Text: string t } && t.StartsWith("stderr| ASSISTANT_JSR_ERROR", StringComparison.Ordinal));
        string log = File.ReadAllText(spec.LogFile, Encoding.UTF8);
        Assert.Contains("stderr| ASSISTANT_JSR_ERROR|模拟失败", log);
        Assert.Contains("MemoryError:", log);
        Assert.IsType<Finished>(events[^1]);
    }

    [Fact]
    public async Task Cancel_StubKill_KillsWholeTreeAndReturnsStopped()
    {
        BackendRunSpec spec = NewSpec("stub_kill.bat");
        string tick = Path.Combine(Path.GetTempPath(), $"jsr4z8_test_{Guid.NewGuid():N}.tick");
        try
        {
            // The stub's heartbeat child (a grandchild of the runner's harness process)
            // writes this file every 250ms while it stays alive.
            Environment.SetEnvironmentVariable("JSR4Z8_TEST_TICK", tick);
            var events = new List<RunnerEvent>();
            using var cts = new CancellationTokenSource();

            Task<RunnerResult> run = new BackendRunner().RunAsync(spec, events.Add, cts.Token);

            // Wait until the heartbeat child is provably alive, then cancel mid-run.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (!File.Exists(tick) && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.True(File.Exists(tick), "heartbeat child never started");
            cts.Cancel();

            // RunAsync must return within 3s of the cancel.
            Task completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(run, completed);

            RunnerResult result = await run;
            Assert.False(result.Success);
            Assert.Contains("已停止", result.Message);
            Assert.Contains(events, e => e is Started { Json: { JobId: "stub-kill" } });
            Assert.IsType<Finished>(events[^1]);

            // Tree kill: 1s after cancel the heartbeat must have stopped (no write in the last 700ms).
            await Task.Delay(TimeSpan.FromSeconds(1));
            DateTime lastWrite = File.GetLastWriteTimeUtc(tick);
            TimeSpan age = DateTime.UtcNow - lastWrite;
            Assert.True(age > TimeSpan.FromMilliseconds(700),
                $"heartbeat still alive: last write {age.TotalMilliseconds:F0}ms ago");
        }
        finally
        {
            Environment.SetEnvironmentVariable("JSR4Z8_TEST_TICK", null);
            try { File.Delete(tick); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Orphan_StubOrphan_NoResultKillsBackendPidFromStartEnvelope()
    {
        BackendRunSpec spec = NewSpec("stub_orphan.bat");
        string tick = Path.Combine(Path.GetTempPath(), $"jsr4z8_test_{Guid.NewGuid():N}.tick");
        try
        {
            // The stub spawns a detached heartbeat child, reports THAT pid in the START
            // envelope, then exits without a RESULT envelope. The heartbeat is an orphan
            // the cancel path never saw: only the START-pid cleanup can kill it.
            Environment.SetEnvironmentVariable("JSR4Z8_TEST_TICK", tick);
            var events = new List<RunnerEvent>();

            Task<RunnerResult> run = new BackendRunner().RunAsync(spec, events.Add, CancellationToken.None);
            Task completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(run, completed);

            RunnerResult result = await run;
            Assert.False(result.Success);
            Assert.Contains("后端未返回结果", result.Message);
            Assert.Contains(events, e => e is Started { Json: { Pid: > 0 } });
            Assert.IsType<Finished>(events[^1]);

            // Within ~1.5s of the run ending the orphan must be dead: its heartbeat stopped.
            Assert.True(File.Exists(tick), "orphan child never started");
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            DateTime lastWrite = File.GetLastWriteTimeUtc(tick);
            TimeSpan age = DateTime.UtcNow - lastWrite;
            Assert.True(age > TimeSpan.FromMilliseconds(700),
                $"orphan heartbeat still alive: last write {age.TotalMilliseconds:F0}ms ago");
        }
        finally
        {
            Environment.SetEnvironmentVariable("JSR4Z8_TEST_TICK", null);
            try { File.Delete(tick); } catch (IOException) { }
        }
    }
}
