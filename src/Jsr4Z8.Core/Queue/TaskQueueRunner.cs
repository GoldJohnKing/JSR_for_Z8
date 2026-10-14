using System.Diagnostics;
using System.Text;
using Jsr4Z8.Core.Backend;
using Jsr4Z8.Core.Dng;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Payload;
using Jsr4Z8.Core.Ptc;
using Jsr4Z8.Core.Settings;

namespace Jsr4Z8.Core.Queue;

/// <summary>Outcome of one queued task run. <see cref="DiagnosticDir"/> is non-null
/// exactly when the job temp dir was KEPT for diagnosis (any failure — see the run
/// steps below); null on success (temp deleted) and on failures that predate dir creation.</summary>
public sealed record TaskRunOutcome(
    bool Success,
    bool Cancelled,
    string Message,
    string? OutputPath,
    TimeSpan Elapsed,
    string? DiagnosticDir);

/// <summary>Events surfaced by <see cref="TaskQueueRunner"/> while one run is in flight.
/// <see cref="BackendFinished"/> mirrors the underlying BackendRunner event so observers
/// can timestamp run boundaries; the terminal state itself is delivered via
/// <see cref="TaskRunOutcome"/>, not an event.</summary>
public abstract record QueueEvent;

/// <summary>Backend payload extraction/verification progress, 0..1 (first run only).</summary>
public sealed record PayloadProgress(double Fraction) : QueueEvent;

/// <summary>The backend process reported its START envelope.</summary>
public sealed record BackendStarted(JsrStart Start) : QueueEvent;

/// <summary>One backend stdout/stderr line; Progress is set on SRHQ progress lines.</summary>
public sealed record BackendLogLine(string Text, ProgressLine? Progress) : QueueEvent;

/// <summary>The backend run ended; the queue continues with finalization or the failure path.</summary>
public sealed record BackendFinished(RunnerResult Result) : QueueEvent;

/// <summary>
/// Runs ONE task group end to end behind the sequential queue (one at a time; the
/// caller — the UI's queue mediator — owns the loop and the single-flight gate):
/// ① <see cref="PayloadManager.EnsureExtracted"/> (progress → 0-5% of the card),
/// ② per-run temp dir &lt;TempBase&gt;\Jsr4Z8\&lt;jobId&gt;\ with its own cfg\ config dir
/// (protocol fact #3: the backend locks its config dir exclusively, so it must be
/// unique per run), ③ request.json via <see cref="BackendRequest.BuildJson"/>
/// (memory = fixed 64 GB — no VRAM budget, the driver allocates; fullwell = the
/// validated PTC match; output = the job dir, prefix = jobId), ④ <see cref="BackendRunner.RunAsync"/> with events forwarded,
/// ⑤ on backend success the newest &lt;prefix&gt;*.dng under the job dir (the backend
/// nests it in &lt;prefix&gt;_V15_3_5_K7_&lt;ts&gt;_&lt;n&gt;\) is finalized via
/// <see cref="JsrDngFinalizer.Finalize"/> into JSR_&lt;基准帧名&gt;.dng next to the
/// reference frame (or the configured custom dir), ⑥ success deletes the job temp
/// dir, any failure keeps it for diagnosis (path in the outcome). Never throws:
/// failures and cancellation come back as a <see cref="TaskRunOutcome"/>.
/// </summary>
public sealed class TaskQueueRunner
{
    /// <summary>The bridge executable inside the extracted payload root.</summary>
    public const string BridgeFileName = "Jiangtherapee Bridge.exe";

    private const string NoDngMessage = "后端未产出 DNG";
    private const string CancelledMessage = "已停止";

    /// <summary>Failed/stopped diagnostic dirs (final-review finding 4): job temp dirs
    /// are ~3 GB each and accumulate forever without pruning. Once per process, the
    /// first run fires a sweep of <see cref="SweepStaleDiagnosticDirs"/>.</summary>
    public const int DiagnosticDirMaxAgeDays = 7;

    private static int _startupSweepDone;

    private readonly PayloadManager _payload;

    /// <summary>Test seam: replaces payload resolution — receives the payload-phase
    /// progress reporter, returns the bridge executable and resources dir to run. The
    /// default path extracts/verifies the embedded payload and resolves
    /// <see cref="BridgeFileName"/> + resources\ inside it.</summary>
    internal Func<IProgress<double>?, BackendPayload>? ResolveBackendOverride { get; set; }

    /// <summary>Test seam: replaces <see cref="JsrDngFinalizer.Finalize"/> —
    /// (backendDngPath, referenceNefPath, destDir) → written path.</summary>
    internal Func<string, string, string, string>? FinalizeOverride { get; set; }

    public TaskQueueRunner(PayloadManager? payload = null)
    {
        _payload = payload ?? new PayloadManager();
    }

    /// <summary>The backend launch resolved from the payload root.</summary>
    internal sealed record BackendPayload(string BridgeExe, string ResourcesDir);

    /// <summary>Runs one group through the six steps above; events go to
    /// <paramref name="onEvent"/> on background threads (the caller marshals).</summary>
    public async Task<TaskRunOutcome> RunAsync(
        TaskGroup g, PtcMatch ptc, AppSettings settings, CancellationToken ct, Action<QueueEvent>? onEvent = null)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(ptc);
        ArgumentNullException.ThrowIfNull(settings);

        var clock = Stopwatch.StartNew();
        string jobId = Guid.NewGuid().ToString("N");
        string jobDir = Path.Combine(ResolveTempBase(settings), "Jsr4Z8", jobId);

        // Final-review finding 4: once per process, prune stale diagnostic dirs older
        // than DiagnosticDirMaxAgeDays (the current job excluded). Fire-and-forget on a
        // background thread — a ~3 GB recursive delete must not delay the first run,
        // and the sweep swallows every IO error so it can never crash one either.
        if (Interlocked.Exchange(ref _startupSweepDone, 1) == 0)
        {
            AppSettings sweepSettings = settings;
            string currentJobId = jobId;
            _ = Task.Run(() => SweepStaleDiagnosticDirs(sweepSettings, currentJobId));
        }

        try
        {
            // ① payload: off the caller thread — EnsureExtracted is synchronous work
            // (first run = full extraction with streaming verify, then a session cache).
            var payloadProgress = new DelegateProgress(f => onEvent?.Invoke(new PayloadProgress(f)));
            BackendPayload payload = ResolveBackendOverride is { } resolve
                ? await Task.Run(() => resolve(payloadProgress), ct).ConfigureAwait(false)
                : await Task.Run(() =>
                {
                    string root = _payload.EnsureExtracted(payloadProgress);
                    return new BackendPayload(Path.Combine(root, BridgeFileName), Path.Combine(root, "resources"));
                }, ct).ConfigureAwait(false);

            // ② per-run temp dir (jobId is unique) with its own config dir.
            Directory.CreateDirectory(jobDir);
            string configDir = Path.Combine(jobDir, "cfg");
            string requestPath = Path.Combine(jobDir, "request.json");
            string logFile = Path.Combine(jobDir, "job.log");

            // ③ request.json: UTF-8 without BOM (backend contract), output = job dir, prefix = jobId.
            // memory is fixed at 64 GB — the VRAM budget feature was removed post-release
            // (user directive 始终不限制); the backend/driver now owns allocation, and
            // driver-level OOM errors flow through the generic humanized failure path.
            string request = BackendRequest.BuildJson(g, ptc, memoryGb: 64, jobDir, jobId);
            File.WriteAllText(requestPath, request, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // ④ backend run; events forwarded for the UI to map onto the card.
            RunnerResult result = await new BackendRunner().RunAsync(
                new BackendRunSpec(payload.BridgeExe, payload.ResourcesDir, configDir, requestPath, logFile),
                e => onEvent?.Invoke(Map(e)),
                ct).ConfigureAwait(false);

            if (!result.Success)
                return new TaskRunOutcome(false, ct.IsCancellationRequested, result.Message, null, clock.Elapsed, jobDir);

            // ⑤ the backend product: newest <prefix>*.dng anywhere under the job dir,
            // skipping *.partial segments (Task 13 carried review fix).
            string? backendDng = FindNewestProductDng(jobDir, jobId);
            if (backendDng is null)
                return new TaskRunOutcome(false, false, NoDngMessage, null, clock.Elapsed, jobDir);

            string destDir = settings.OutputMode == OutputMode.Custom && !string.IsNullOrWhiteSpace(settings.CustomOutputDir)
                ? settings.CustomOutputDir
                : Path.GetDirectoryName(Path.GetFullPath(g.ReferencePath))
                  ?? throw new InvalidOperationException("基准帧路径没有所在目录");
            string written = FinalizeOverride is { } finalize
                ? finalize(backendDng, g.ReferencePath, destDir)
                // Final-review finding 8: cross-check warnings land in the job log
                // (kept diagnostic dirs) instead of the invisible Console.Error.
                : JsrDngFinalizer.Finalize(backendDng, g.ReferencePath, destDir,
                    line => AppendJobLog(logFile, line));

            // ⑥ success: clean the whole job dir (request/cfg/log/product). Best effort —
            // a locked file must not turn a finished job into a reported failure.
            try
            {
                Directory.Delete(jobDir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return new TaskRunOutcome(true, false, result.Message, written, clock.Elapsed, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new TaskRunOutcome(false, true, CancelledMessage, null, clock.Elapsed, Kept(jobDir));
        }
        catch (Exception ex) // queue robustness: one task's crash must not take down the queue
        {
            return new TaskRunOutcome(false, false, ex.Message, null, clock.Elapsed, Kept(jobDir));
        }
    }

    private static QueueEvent Map(RunnerEvent e) => e switch
    {
        Started s => new BackendStarted(s.Json),
        LogLine l => new BackendLogLine(l.Text, l.Progress),
        Finished f => new BackendFinished(f.Result),
        _ => throw new InvalidOperationException($"未知 RunnerEvent: {e.GetType().Name}"),
    };

    private static string ResolveTempBase(AppSettings s) =>
        s.TempMode == TempMode.Custom && !string.IsNullOrWhiteSpace(s.CustomTempDir)
            ? s.CustomTempDir
            : Path.GetTempPath();

    /// <summary>Deletes every &lt;TempBase&gt;\Jsr4Z8\&lt;dir&gt; whose LastWriteTimeUtc is older
    /// than <see cref="DiagnosticDirMaxAgeDays"/> days (the ~3 GB diagnostic dirs kept
    /// by failed/stopped runs — final-review finding 4), skipping the given job id and
    /// swallowing all IO errors: best effort, never fatal to a run. The temp base is
    /// resolved from the CURRENT settings at sweep time. Internal for the mtime-based
    /// unit test (fabricated old/new dirs).</summary>
    internal static void SweepStaleDiagnosticDirs(AppSettings settings, string? excludeJobId = null)
    {
        try
        {
            string root = Path.Combine(ResolveTempBase(settings), "Jsr4Z8");
            if (!Directory.Exists(root))
                return;
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(DiagnosticDirMaxAgeDays);
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                if (string.Equals(Path.GetFileName(dir), excludeJobId, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                        Directory.Delete(dir, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception)
        {
            // The sweep must never take a run down with it.
        }
    }

    /// <summary>Success-path product glob (internal test seam): the newest
    /// <paramref name="prefix"/>*.dng anywhere under <paramref name="root"/>, skipping
    /// any path that traverses a *.partial segment — the backend's resume leftovers
    /// (Task 13 carried review fix).</summary>
    internal static string? FindNewestProductDng(string root, string prefix) =>
        Directory.EnumerateFiles(root, prefix + "*.dng", SearchOption.AllDirectories)
            .Where(p => !HasPartialSegment(p))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    /// <summary>True when any path segment ends with .partial (case-insensitive).</summary>
    private static bool HasPartialSegment(string path)
    {
        foreach (string segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (segment.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string? Kept(string jobDir) => Directory.Exists(jobDir) ? jobDir : null;

    /// <summary>Appends one finalizer warning line to the run's job.log. Best effort —
    /// a locked/missing log must never fail a run that is about to succeed.</summary>
    private static void AppendJobLog(string logFile, string line)
    {
        try
        {
            File.AppendAllText(logFile, line + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Plain IProgress adapter: reports on the calling thread, no
    /// SynchronizationContext marshaling (the queue's consumer marshals itself).</summary>
    private sealed class DelegateProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
