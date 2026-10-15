using System.Diagnostics;
using System.Text;

namespace Jsr4Z8.Core.Backend;

/// <summary>
/// Runs one DIRECT jsr_backend.exe invocation end to end (2026-10-15 probe §7):
/// spawns the engine executable with the argv from the spec — no Bridge, no
/// request.json, no config dir — and streams its bare stdout lines (zero
/// ASSISTANT_* envelopes): SRHQ progress parsing stays in ProtocolParser, every
/// line is forwarded as LogLine and appended to the spec's log file (UTF-8,
/// stderr lines prefixed "stderr| "). Started is synthesized at spawn (pid = the
/// engine parent we spawned — the same-argv child it relays never gets a pid of
/// its own on this side), Finished at exit from the parent's ExitCode plus the
/// humanized tail extraction (SRHQ_NET_ABORT:&lt;stage&gt;|&lt;msg&gt; or a
/// MemoryError line). Cancellation kills the whole process tree with
/// "cmd /c taskkill /T /F /PID": every engine invocation self-respawns a
/// same-argv child ~2s in and the tree can hold a third unnamed grandchild —
/// killing only the spawned pid's direct child would leak them. Callbacks and
/// log writes are serialized across the two reader tasks, so onEvent is never
/// invoked concurrently.
/// </summary>
public sealed class BackendRunner
{
    private const string CancelledMessage = "已停止";
    private const string GenericFailureMessage = "后端执行失败";
    private const string SuccessMessage = "完成";
    private const string StderrPrefix = "stderr| ";
    private const string AbortTag = "SRHQ_NET_ABORT:";
    private const string MemoryErrorPrefix = "MemoryError:";
    private const string StartedStage = "重建";
    private const string StartedEngine = "stormer7";
    private const string StartedMode = "full";

    /// <summary>Spawns the engine per <paramref name="spec"/> and streams it to completion.</summary>
    public async Task<RunnerResult> RunAsync(BackendRunSpec spec, Action<RunnerEvent> onEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(onEvent);

        if (ct.IsCancellationRequested)
        {
            var neverStarted = new RunnerResult(false, CancelledMessage, TimeSpan.Zero);
            onEvent(new Finished(neverStarted));
            return neverStarted;
        }

        string? logDir = Path.GetDirectoryName(Path.GetFullPath(spec.LogFile));
        if (!string.IsNullOrEmpty(logDir))
            Directory.CreateDirectory(logDir);

        var gate = new object(); // serializes log writes + onEvent across the two reader tasks
        var clock = Stopwatch.StartNew();
        var lines = new List<string>(); // every received line in arrival order (stderr prefixed): the tail used for message extraction
        string? lastStderr = null;

        var psi = new ProcessStartInfo
        {
            FileName = spec.EngineExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in spec.Arguments)
            psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi };
        using var log = new StreamWriter(
            new FileStream(spec.LogFile, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        { AutoFlush = true };

        process.Start();
        int enginePid = process.Id; // captured once: the kill task outlives the disposed Process object

        // The bare-line protocol has no START envelope: synthesize it. Our own pid IS
        // the root — the same-argv child the engine spawns never surfaces here.
        lock (gate)
        {
            onEvent(new Started(new JsrStart(StartedStage, StartedEngine, StartedMode, spec.EngineExe, enginePid)));
        }

        // Tree kill: /T walks engine parent -> same-argv child -> the unnamed third
        // grandchild; /F because the engine does not respond to polite termination.
        // Guarded so cancel + failure-cleanup kill once. taskkill runs on the thread
        // pool: ct.Register callbacks may fire on a UI thread, which must not block
        // waiting for the killer to exit.
        int killDone = 0;
        int killExitCode = 0; // 0 = not run or succeeded; >0 = taskkill's exit code; -1 timeout; -2 not started
        Task killTask = Task.CompletedTask;
        void KillTree()
        {
            if (Interlocked.CompareExchange(ref killDone, 1, 0) != 0)
                return;
            killTask = Task.Run(() => killExitCode = RunTaskKill(enginePid));
        }

        using CancellationTokenRegistration registration = ct.Register(KillTree);

        Task stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                ProtocolEvent parsed = ProtocolParser.ParseLine(line);
                lock (gate)
                {
                    lines.Add(line);
                    log.WriteLine(line);
                    onEvent(new LogLine(line, parsed is Log l ? l.Progress : null));
                }
            }
        });

        Task stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                ProtocolEvent parsed = ProtocolParser.ParseLine(line);
                string text = StderrPrefix + line;
                lock (gate)
                {
                    lines.Add(text);
                    log.WriteLine(text);
                    if (line.Length > 0)
                        lastStderr = line;
                    onEvent(new LogLine(text, parsed is Log l ? l.Progress : null));
                }
            }
        });

        // Waiting the pipes (both writers gone) AND the parent process: the parent
        // relays the same-argv child's exit code 0.3-0.5s after the child dies.
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        registration.Dispose();

        bool cancelled = ct.IsCancellationRequested;
        bool success = !cancelled && process.ExitCode == 0;

        // Belt-and-braces (the old code killed the START-envelope pid here): a
        // failed/killed parent may have left its same-argv child or the unnamed
        // grandchild running — /T on our own root pid reaps whatever is still
        // attached to the tree. Best effort; harmless when the tree is already gone.
        if (!success)
            KillTree();

        await killTask.ConfigureAwait(false); // taskkill's exit code is final before the message is composed

        string message;
        if (success)
            message = SuccessMessage;
        else if (cancelled)
        {
            message = CancelledMessage;
            if (killExitCode != 0) // a failed tree kill must not be reported as a clean stop
                message += $"（taskkill 退出码 {killExitCode}）";
        }
        else
            message = ExtractFailureMessage(lines) ?? lastStderr ?? GenericFailureMessage;

        var final = new RunnerResult(success, message, clock.Elapsed);
        lock (gate)
        {
            onEvent(new Finished(final));
        }
        return final;
    }

    /// <summary>Runs "cmd /c taskkill /T /F /PID &lt;pid&gt;" synchronously and returns its outcome:
    /// 0 on success, taskkill's own nonzero exit code on failure, -1 when taskkill did not
    /// exit within 10s, -2 when it could not be started (process already gone, taskkill
    /// unavailable). Best effort only — a failed kill never fails the run itself.</summary>
    private static int RunTaskKill(int pid)
    {
        try
        {
            using Process? killer = Process.Start(new ProcessStartInfo(
                "cmd.exe", $"/c taskkill /T /F /PID {pid}")
            { UseShellExecute = false, CreateNoWindow = true });
            if (killer is null)
                return -2;
            return killer.WaitForExit(TimeSpan.FromSeconds(10)) ? killer.ExitCode : -1;
        }
        catch (Exception)
        {
            return -2; // process already gone or taskkill unavailable; the closing pipes end the readers anyway
        }
    }

    /// <summary>Distills a user-readable failure message from the log tail: the last
    /// SRHQ_NET_ABORT:&lt;stage&gt;|&lt;msg&gt; message (what the engine itself chose to
    /// report, a bare stdout line in the direct protocol), else the last MemoryError
    /// line, else the last non-empty line.</summary>
    private static string? ExtractFailureMessage(List<string> lines)
    {
        string? lastAbort = null, lastMemory = null, lastNonEmpty = null;
        foreach (string line in lines)
        {
            if (line.Length == 0)
                continue;
            lastNonEmpty = line;
            if (line.Contains(AbortTag, StringComparison.Ordinal))
                lastAbort = line;
            else if (line.StartsWith(MemoryErrorPrefix, StringComparison.Ordinal))
                lastMemory = line;
        }

        if (lastAbort is not null)
        {
            int tag = lastAbort.IndexOf(AbortTag, StringComparison.Ordinal);
            int pipe = lastAbort.IndexOf('|', tag);
            return pipe >= 0 && pipe + 1 < lastAbort.Length ? lastAbort[(pipe + 1)..] : lastAbort;
        }

        return lastMemory ?? lastNonEmpty;
    }
}
