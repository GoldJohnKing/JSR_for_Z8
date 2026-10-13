using System.Diagnostics;
using System.Text;

namespace Jsr4Z8.Core.Backend;

/// <summary>
/// Runs one "Jiangtherapee Bridge.exe --assistant-jsr-job" invocation end to end
/// (protocol facts #1-#4): spawns the harness process — which itself spawns the real
/// jsr_backend.exe child — streams stdout/stderr through ProtocolParser, and appends
/// every received line to the spec's log file (UTF-8, stderr lines prefixed "stderr| ").
/// Success = the single RESULT envelope with state "complete" and host exit code 0;
/// failure messages are distilled from the log tail (SRHQ_NET_ABORT:&lt;stage&gt;|&lt;msg&gt;
/// or a MemoryError line). Cancellation kills the whole process tree with
/// "cmd /c taskkill /T /F /PID": killing only the harness PID would leave the real
/// backend child running. Callbacks and log writes are serialized across the two
/// reader tasks, so onEvent is never invoked concurrently.
/// </summary>
public sealed class BackendRunner
{
    private const string CancelledMessage = "已停止";
    private const string NoResultMessage = "后端未返回结果";
    private const string GenericFailureMessage = "后端执行失败";
    private const string SuccessMessage = "完成";
    private const string StderrPrefix = "stderr| ";
    private const string AbortTag = "SRHQ_NET_ABORT:";
    private const string MemoryErrorPrefix = "MemoryError:";

    /// <summary>Spawns the harness per <paramref name="spec"/> and streams it to completion.</summary>
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

        Directory.CreateDirectory(spec.ConfigDir); // per-run config dir: exclusive lock territory, the runner only creates it
        string? logDir = Path.GetDirectoryName(Path.GetFullPath(spec.LogFile));
        if (!string.IsNullOrEmpty(logDir))
            Directory.CreateDirectory(logDir);

        var gate = new object(); // serializes log writes + onEvent across the two reader tasks
        var clock = Stopwatch.StartNew();
        var lines = new List<string>(); // every received line in arrival order (stderr prefixed): the tail used for message extraction
        string? lastStderr = null;
        string? stderrError = null;
        JsrResult? result = null;

        var psi = new ProcessStartInfo
        {
            FileName = spec.BridgeExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--assistant-jsr-job");
        psi.ArgumentList.Add(spec.RequestJsonPath);
        psi.ArgumentList.Add(spec.ResourcesDir);
        psi.ArgumentList.Add(spec.ConfigDir);

        using var process = new Process { StartInfo = psi };
        using var log = new StreamWriter(
            new FileStream(spec.LogFile, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        { AutoFlush = true };

        process.Start();

        // Tree kill: /T walks harness -> jsr_backend -> any grandchild; /F because the backend
        // does not respond to polite termination. Guarded so cancel + no-RESULT cleanup kill once.
        int killDone = 0;
        void KillTree()
        {
            if (Interlocked.CompareExchange(ref killDone, 1, 0) != 0)
                return;
            try
            {
                using Process? killer = Process.Start(new ProcessStartInfo(
                    "cmd.exe", $"/c taskkill /T /F /PID {process.Id}")
                { UseShellExecute = false, CreateNoWindow = true });
                killer?.WaitForExit(TimeSpan.FromSeconds(10));
            }
            catch (Exception)
            {
                // process already gone or taskkill unavailable; the closing pipes end the readers anyway
            }
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
                    switch (parsed)
                    {
                        case Start start: // envelope line: persisted raw, surfaced as Started
                            lines.Add(line);
                            log.WriteLine(line);
                            onEvent(new Started(start.Json));
                            break;

                        case Result r: // the envelope is persisted, but no event yet: Finished reports the outcome
                            lines.Add(line);
                            log.WriteLine(line);
                            result = r.Json;
                            break;

                        case Log l: // unwrapped text is what the UI log should show
                            lines.Add(l.Text);
                            log.WriteLine(l.Text);
                            onEvent(new LogLine(l.Text, l.Progress));
                            break;

                        case ErrorOnStderr: // protocol error lines belong on stderr; if one lands on stdout keep it verbatim
                            lines.Add(line);
                            log.WriteLine(line);
                            onEvent(new LogLine(line, null));
                            break;

                        case Unrecognized u: // backend chatter without envelope: still a log line, nothing is dropped
                            lines.Add(u.Text);
                            log.WriteLine(u.Text);
                            onEvent(new LogLine(u.Text, null));
                            break;
                    }
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
                    if (parsed is ErrorOnStderr e)
                        stderrError = e.Message;
                    onEvent(new LogLine(text, parsed is Log l ? l.Progress : null));
                }
            }
        });

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        process.WaitForExit();
        registration.Dispose();

        // The harness exited without ever sending RESULT: clean up any backend child it left behind.
        if (result is null)
            KillTree();

        bool cancelled = ct.IsCancellationRequested;
        bool success = !cancelled && result is { State: "complete" } && process.ExitCode == 0;

        string message;
        if (success)
            message = SuccessMessage;
        else if (cancelled)
            message = CancelledMessage;
        else if (result is null)
            message = stderrError ?? lastStderr ?? NoResultMessage;
        else
            message = ExtractFailureMessage(lines) ?? GenericFailureMessage;

        var final = new RunnerResult(success, message, clock.Elapsed);
        lock (gate)
        {
            onEvent(new Finished(final));
        }
        return final;
    }

    /// <summary>Distills a user-readable failure message from the log tail: the last
    /// SRHQ_NET_ABORT:&lt;stage&gt;|&lt;msg&gt; message (what the backend itself chose to report,
    /// works both bare and LOG-wrapped), else the last MemoryError line, else the last
    /// non-empty line.</summary>
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
