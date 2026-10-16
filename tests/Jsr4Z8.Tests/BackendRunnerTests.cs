using System.Text;
using Jsr4Z8.Core.Backend;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// BackendRunner against the cmd.exe stub engines in
/// fixtures\stub_backend\ — bare-line stdout (success / fail / dual-instance
/// self-respawn / long-running-with-heartbeat-grandchild). The stubs replay
/// the measured process shapes of jsr_backend.exe (zero ASSISTANT_* envelopes,
/// SRHQ bare lines; the dual stub replays the non-injected spawn shape: exit
/// code relayed by the spawned parent, same-argv child + unnamed grandchild in
/// the tree), so cancellation
/// must keep the whole-tree kill (taskkill /T /F), Started is the runner's own
/// synthesis, and every streamed line must land in the run log file. stub_env.bat
/// instead echoes the _PYI_* spawn-injection triple for its character-exact lock,
/// plus its own %~f0 for the same-path lock (_PYI_ARCHIVE_FILE
/// must equal the spawned exe's own path — a foreign archive is silently ignored).
/// Windows-host tests: the stubs are .bat files driven through the same Process
/// path the real engine executable uses.
/// </summary>
public class BackendRunnerTests : IClassFixture<TestTemp>
{
    private readonly TestTemp _temp;

    public BackendRunnerTests(TestTemp temp) => _temp = temp;

    /// <summary>Locates a stub .bat under fixtures\stub_backend\.</summary>
    private static string StubPath(string name) => SampleFiles.FindUp(Path.Combine("fixtures", "stub_backend", name));

    /// <summary>A run spec pointing at a stub engine, with a representative direct
    /// argv (the stubs parse --output-dir/--out-prefix the way stub_dng.bat does)
    /// and a temp log path. No request.json, no config dir — the direct protocol
    /// needs neither.</summary>
    private BackendRunSpec NewSpec(string batName)
    {
        string root = _temp.NewDir();
        return new BackendRunSpec(
            EngineExe: StubPath(batName),
            Arguments: new[] { "--output-dir", root, "--out-prefix", "stub" },
            LogFile: Path.Combine(root, "run.log"));
    }

    [Fact]
    public async Task FullRun_StubEngine_BareLines_SynthesizesStart_StreamsProgress_PersistsLog()
    {
        BackendRunSpec spec = NewSpec("stub_backend.bat");
        var events = new List<RunnerEvent>();

        RunnerResult result = await new BackendRunner().RunAsync(spec, events.Add, CancellationToken.None);

        Assert.True(result.Success, $"Message: {result.Message}");
        Assert.True(result.Elapsed > TimeSpan.Zero);

        // Started is synthesized at spawn: the direct protocol has no START envelope.
        // pid = the spawned engine (tree-kill root), stage 重建, the request's own
        // engine/mode constants, compressor = the engine executable path.
        Started started = Assert.IsType<Started>(events[0]);
        Assert.Equal("重建", started.Start.Stage);
        Assert.Equal("stormer7", started.Start.Engine);
        Assert.Equal("full", started.Start.Mode);
        Assert.Equal(spec.EngineExe, started.Start.Compressor);
        Assert.True(started.Start.Pid > 0);

        // Bare progress lines flow through the parser: V12 percent/stage, and the
        // export-phase ETA mapped onto 95..100 ("DNG 导出"). The PTC echo (ruling 3)
        // and the DONE tail are ordinary lines with Progress null.
        Assert.Contains(events, e => e is LogLine { Progress: { Percent: 42, Stage: "模拟阶段" } });
        Assert.Contains(events, e => e is LogLine { Progress: { Percent: 99, Stage: "DNG 导出" } });
        Assert.Contains(events, e => e is LogLine { Text: var t, Progress: null } && t.StartsWith("PTC ", StringComparison.Ordinal));
        Assert.Contains(events, e => e is LogLine { Text: "DONE total 1.0s", Progress: null });
        Assert.DoesNotContain(events, e => e is LogLine { Text: var t } && t.Contains("ASSISTANT", StringComparison.Ordinal));

        // Exactly one Finished event, last, wrapping the returned result.
        Finished finished = Assert.IsType<Finished>(events[^1]);
        Assert.True(finished.Result.Success);

        // Log persistence: every streamed bare line is in the file.
        Assert.True(File.Exists(spec.LogFile));
        string log = File.ReadAllText(spec.LogFile, Encoding.UTF8);
        Assert.Contains("SRHQ_V12_PROGRESS|42|模拟阶段", log);
        Assert.Contains("SRHQ_ETA_PROGRESS|dng|10880|11040", log);
        Assert.Contains("DONE total 1.0s", log);
        Assert.DoesNotContain("ASSISTANT", log);
    }

    [Fact]
    public async Task Fail_StubFail_ExtractsNetAbortMessageFromBareTail()
    {
        BackendRunSpec spec = NewSpec("stub_fail.bat");
        var events = new List<RunnerEvent>();

        RunnerResult result = await new BackendRunner().RunAsync(spec, events.Add, CancellationToken.None);

        // Bare exit 1 → failure; the message is the humanized extract: the bare
        // SRHQ_NET_ABORT msg beats the raw MemoryError line.
        Assert.False(result.Success);
        Assert.Contains("模拟失败原因", result.Message);
        Assert.DoesNotContain("MemoryError", result.Message);

        // stderr is part of the tail: surfaced as a prefixed LogLine and persisted.
        Assert.Contains(events, e => e is LogLine { Text: "stderr| ValueError: seven distinct RAW frames are required" });
        string log = File.ReadAllText(spec.LogFile, Encoding.UTF8);
        Assert.Contains("stderr| ValueError: seven distinct RAW frames are required", log);
        Assert.Contains("SRHQ_NET_ABORT:runtime|模拟失败原因", log);
        Assert.Contains("MemoryError:", log);
        Assert.IsType<Finished>(events[^1]);
    }

    [Fact]
    public async Task DualInstance_StubDual_WaitsForParent_TakesParentExitCode()
    {
        // The engine's NON-INJECTED spawn shape self-respawns a same-argv child
        // ~2s in; the child does the work and exits FIRST, the parent relays
        // its exit code 0.3-0.5s later. The stub parent streams one more line AFTER
        // the child's death and only then exits 0 — the runner must take the PARENT:
        // its lines, its exit code, its WaitForExit.
        BackendRunSpec spec = NewSpec("stub_dual.bat");
        var events = new List<RunnerEvent>();

        RunnerResult result = await new BackendRunner().RunAsync(spec, events.Add, CancellationToken.None);

        Assert.True(result.Success, $"Message: {result.Message}");

        // Both instances share the inherited stdout pipe: the child's line AND the
        // parent's post-child line all arrived — waiting on the child alone would
        // have missed the parent's final word.
        Assert.Contains(events, e => e is LogLine { Progress: { Percent: 50, Stage: "子实例同 argv 干活" } });
        Assert.Contains(events, e => e is LogLine { Progress: { Percent: 100, Stage: "父实例转述子实例退出码" } });
        Assert.IsType<Finished>(events[^1]);

        string log = File.ReadAllText(spec.LogFile, Encoding.UTF8);
        Assert.Contains("子实例同 argv 干活", log);
        Assert.Contains("父实例转述子实例退出码", log);
    }

    /// <summary>Locks the _PYI_* spawn injection character-exact: the stub echoes the
    /// three variables BackendRunner must set on every engine spawn (the pre-extracted
    /// tree runtime contract, experiment e1-c3) — one wrong character and the
    /// bootloader's reset block strips them, silently falling back to per-run
    /// self-extraction.</summary>
    [Fact]
    public async Task SpawnInjectsPyiTriple_StubEnvEcho_CharacterExactExperimentValues()
    {
        BackendRunSpec spec = NewSpec("stub_env.bat");
        var events = new List<RunnerEvent>();

        RunnerResult result = await new BackendRunner().RunAsync(spec, events.Add, CancellationToken.None);

        Assert.True(result.Success, $"Message: {result.Message}");

        // Experiment recipe (docs/superpowers/notes/2026-10-16-meipass2-experiment.md
        // e1-c3): archive file = the engine executable's FULL path (the CArchive),
        // home dir = its containing tree directory (no trailing separator), level = "0".
        // Full-string equality — Contains would hide a single wrong character.
        string engineFull = Path.GetFullPath(spec.EngineExe);
        string treeDir = Path.GetDirectoryName(engineFull)!;
        Assert.Contains(events, e => e is LogLine { Progress: null } l && l.Text == "_PYI_ARCHIVE_FILE=" + engineFull);
        Assert.Contains(events, e => e is LogLine { Progress: null } l && l.Text == "_PYI_APPLICATION_HOME_DIR=" + treeDir);
        Assert.Contains(events, e => e is LogLine { Progress: null } l && l.Text == "_PYI_PARENT_PROCESS_LEVEL=0");
        Assert.IsType<Finished>(events[^1]);
    }

    /// <summary>Fact A lock (evaluation docs/superpowers/notes/2026-10-16-thin-archive-probe.md
    /// 机制事实 #2): a _PYI_ARCHIVE_FILE pointing at anything other than the SPAWNED exe's
    /// own path is silently ignored — the bootloader's reset block then self-extracts the
    /// parent's real archive (per-run %TEMP% _MEI), which is exactly the regression this
    /// deployment form exists to prevent. stub_env.bat echoes its own %~f0 next to the
    /// triple: echo-vs-echo string equality (no C#-side path recomputation) proves the
    /// injected archive IS the process we spawned, and the home dir is that exe's own
    /// directory. Complements the character-exact lock above (values vs. experiment
    /// recipe); either breaking independently names its own fact.</summary>
    [Fact]
    public async Task SpawnInjectsPyiTriple_ArchiveFileEqualsSpawnedExeOwnPath_StubEnvEcho()
    {
        BackendRunSpec spec = NewSpec("stub_env.bat");
        var events = new List<RunnerEvent>();

        RunnerResult result = await new BackendRunner().RunAsync(spec, events.Add, CancellationToken.None);

        Assert.True(result.Success, $"Message: {result.Message}");
        string selfPath = EchoValue(events, "_PYI_SELF_PATH=");
        Assert.Equal(selfPath, EchoValue(events, "_PYI_ARCHIVE_FILE="));
        Assert.Equal(Path.GetDirectoryName(selfPath), EchoValue(events, "_PYI_APPLICATION_HOME_DIR="));
        Assert.IsType<Finished>(events[^1]);
    }

    /// <summary>The value behind the stub's single "&lt;prefix&gt;&lt;value&gt;" echo line —
    /// Assert.Single also locks that the stub echoed it exactly once.</summary>
    private static string EchoValue(List<RunnerEvent> events, string prefix)
    {
        string[] values = events
            .OfType<LogLine>()
            .Where(l => l.Progress is null && l.Text.StartsWith(prefix, StringComparison.Ordinal))
            .Select(l => l.Text[prefix.Length..])
            .ToArray();
        return Assert.Single(values);
    }

    [Fact]
    public async Task Cancel_StubKill_KillsWholeTreeAndReturnsStopped()
    {
        // The stub derives its heartbeat file from its --output-dir argv — unique per
        // run, like the queue's GUID job dirs (no process-global test environment:
        // parallel xUnit classes would race on a shared env var).
        string root = _temp.NewDir();
        var spec = new BackendRunSpec(
            EngineExe: StubPath("stub_kill.bat"),
            Arguments: new[] { "--output-dir", root, "--out-prefix", "stub" },
            LogFile: Path.Combine(root, "run.log"));
        string tick = Path.Combine(root, "tick");
        try
        {
            // The stub's heartbeat child (a grandchild of the runner's spawned process —
            // the tree shape of the real engine's same-argv child plus the unnamed
            // third grandchild) writes <output-dir>\tick every 250ms while it stays alive.
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
            Assert.Contains(events, e => e is Started { Start: { Stage: "重建", Pid: > 0 } });
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
            try { File.Delete(tick); } catch (IOException) { }
        }
    }
}
