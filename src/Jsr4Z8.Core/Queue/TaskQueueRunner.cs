using System.Diagnostics;
using Jsr4Z8.Core.Backend;
using Jsr4Z8.Core.Dng;
using Jsr4Z8.Core.Dark;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Payload;
using Jsr4Z8.Core.Ptc;

namespace Jsr4Z8.Core.Queue;

/// <summary>Outcome of one queued task run: success/cancel flags, the humanized
/// message, the written DNG path on success, and the wall-clock duration. The job
/// temp dir is deleted on EVERY terminal outcome (success and failure/stopped alike —
/// 失败/停止即删), so no diagnostic path is carried.</summary>
public sealed record TaskRunOutcome(
    bool Success,
    bool Cancelled,
    string Message,
    string? OutputPath,
    TimeSpan Elapsed);

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
/// ② per-run temp dir &lt;TempBase&gt;\Jsr4Z8\&lt;jobId&gt;\ (unique per run — the direct
/// invocation needs no cfg dir), ③ the jsr_backend.exe argv via
/// <see cref="BackendRequest.BuildArguments"/> (no memory budget at all — 不设预算，驱动自行分配;
/// fullwell = the validated PTC match rounded to 1 decimal; output = the job dir,
/// prefix = jobId), ④ <see cref="BackendRunner.RunAsync"/> — the DIRECT engine
/// invocation (no Bridge, no request.json) — with events forwarded, ⑤ on backend
/// success the newest &lt;prefix&gt;*.dng under the job dir (the engine nests it in
/// &lt;prefix&gt;_V15_3_5_K7_&lt;ts&gt;_&lt;n&gt;\) is finalized via
/// <see cref="JsrDngFinalizer.Finalize"/> into JSR_&lt;基准帧名&gt;.dng next to the
/// reference frame, ⑥ every terminal outcome deletes
/// the job temp dir (success cleanup and the failure/stopped paths alike; leftover
/// debris is handled by the startup <see cref="SweepTempRoot"/>). Never throws:
/// failures and cancellation come back as a <see cref="TaskRunOutcome"/>.
/// </summary>
public sealed class TaskQueueRunner
{
    /// <summary>The backend engine executable inside the extracted payload, relative to
    /// the payload root: the pre-extracted runtime tree resources\jsr_backend_runtime\
    /// (pack-time CArchive pre-extraction, <see cref="Payload.PayloadPacker.EngineRuntimeDir"/>)
    /// carries the engine exe at its root, and BackendRunner's _PYI_* spawn injection
    /// runs exactly this copy against the tree — zero %TEMP% _MEI (experiment
    /// docs/superpowers/notes/2026-10-16-meipass2-experiment.md).</summary>
    public const string EngineRelativePath = @"resources\jsr_backend_runtime\jsr_backend.exe";

    private const string NoDngMessage = "后端未产出 DNG";
    private const string CancelledMessage = "已停止";

    private readonly PayloadManager _payload;

    /// <summary>Test seam: replaces payload resolution — receives the payload-phase
    /// progress reporter, returns the engine executable to run. The default path
    /// extracts/verifies the embedded payload and resolves
    /// <see cref="EngineRelativePath"/> inside it.</summary>
    internal Func<IProgress<double>?, BackendPayload>? ResolveBackendOverride { get; set; }

    /// <summary>Test seam: overrides the per-job temp root base (production null →
    /// <see cref="Path.GetTempPath"/>) — hermetic queue/sweep tests point it at a
    /// fixture-owned directory.</summary>
    internal string? TempBaseOverride { get; set; }

    /// <summary>Test seam: replaces <see cref="JsrDngFinalizer.Finalize"/> —
    /// (backendDngPath, referenceNefPath, destDir) → written path.</summary>
    internal Func<string, string, string, string>? FinalizeOverride { get; set; }

    public TaskQueueRunner(PayloadManager? payload = null)
    {
        _payload = payload ?? new PayloadManager();
    }

    /// <summary>The backend engine executable resolved from the payload root.</summary>
    internal sealed record BackendPayload(string EngineExe);

    /// <summary>Runs one group through the six steps above; events go to
    /// <paramref name="onEvent"/> on background threads (the caller marshals).</summary>
    public async Task<TaskRunOutcome> RunAsync(
        TaskGroup g, PtcMatch ptc, DarkMatch dark, CancellationToken ct, Action<QueueEvent>? onEvent = null)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(ptc);
        ArgumentNullException.ThrowIfNull(dark);

        var clock = Stopwatch.StartNew();
        string jobId = Guid.NewGuid().ToString("N");
        string jobDir = Path.Combine(TempBase, "Jsr4Z8", jobId);

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
                    return new BackendPayload(Path.Combine(root, EngineRelativePath));
                }, ct).ConfigureAwait(false);

            // ② per-run temp dir (jobId is unique — the engine's output dir).
            Directory.CreateDirectory(jobDir);
            string logFile = Path.Combine(jobDir, "job.log");

            // ③ the engine argv (probe-verified contract): output = job dir, prefix =
            // jobId, one --raw-frame per frame. No memory budget is sent (不设预算，驱动
            // 自行分配): the engine/driver owns allocation, and driver-level OOM errors
            // flow through the generic humanized failure path.
            IReadOnlyList<string> arguments = BackendRequest.BuildArguments(g, ptc, dark, jobDir, jobId);

            // ④ the direct engine run; events forwarded for the UI to map onto the card.
            RunnerResult result = await new BackendRunner().RunAsync(
                new BackendRunSpec(payload.EngineExe, arguments, logFile),
                e => onEvent?.Invoke(Map(e)),
                ct).ConfigureAwait(false);

            if (!result.Success)
            {
                TryDeleteDir(jobDir); // 失败即删：不再保留多 GB 诊断目录
                return new TaskRunOutcome(false, ct.IsCancellationRequested, result.Message, null, clock.Elapsed);
            }

            // ⑤ the backend product: newest <prefix>*.dng anywhere under the job dir,
            // skipping *.partial segments (the backend's resume leftovers).
            string? backendDng = FindNewestProductDng(jobDir, jobId);
            if (backendDng is null)
            {
                TryDeleteDir(jobDir);
                return new TaskRunOutcome(false, false, NoDngMessage, null, clock.Elapsed);
            }

            string destDir = Path.GetDirectoryName(Path.GetFullPath(g.ReferencePath))
                ?? throw new InvalidOperationException("基准帧路径没有所在目录");
            string written = FinalizeOverride is { } finalize
                ? finalize(backendDng, g.ReferencePath, destDir)
                // Cross-check warnings go to the job log instead of the invisible
                // Console.Error — job.log lives for the run's lifetime only (deleted
                // with the job dir on every terminal outcome).
                : JsrDngFinalizer.Finalize(backendDng, g.ReferencePath, destDir,
                    line => AppendJobLog(logFile, line));

            // ⑥ success: clean the whole job dir (engine output + log + product). Best
            // effort — a locked file must not turn a finished job into a reported failure.
            TryDeleteDir(jobDir);

            return new TaskRunOutcome(true, false, result.Message, written, clock.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDeleteDir(jobDir); // stopped runs delete their temp dir too
            return new TaskRunOutcome(false, true, CancelledMessage, null, clock.Elapsed);
        }
        catch (Exception ex) // queue robustness: one task's crash must not take down the queue
        {
            TryDeleteDir(jobDir);
            return new TaskRunOutcome(false, false, ex.Message, null, clock.Elapsed);
        }
    }

    private static QueueEvent Map(RunnerEvent e) => e switch
    {
        Started s => new BackendStarted(s.Start),
        LogLine l => new BackendLogLine(l.Text, l.Progress),
        Finished f => new BackendFinished(f.Result),
        _ => throw new InvalidOperationException($"未知 RunnerEvent: {e.GetType().Name}"),
    };

    private string TempBase => TempBaseOverride ?? Path.GetTempPath();

    /// <summary>Sweep grace window: dirs under the Jsr4Z8 temp root
    /// whose LastWriteTimeUtc is newer than this before the sweep's own clock are
    /// skipped. The same-instance race: a slow multi-GB sweep may still be iterating
    /// when the user starts the first run seconds after launch — the fresh job dir
    /// must survive it (cross-instance is the app's single-instance mutex's job).</summary>
    private static readonly TimeSpan SweepGraceWindow = TimeSpan.FromMinutes(2);

    /// <summary>Startup sweep: deletes every &lt;TempBase&gt;\Jsr4Z8\&lt;dir&gt; whose
    /// LastWriteTimeUtc is older than <see cref="SweepGraceWindow"/>. Failed/stopped
    /// runs delete their job dir themselves (失败即删), so anything still under the
    /// root is debris from a crashed/killed process. Best effort — every IO error is
    /// swallowed, never fatal to a launch. The app wires it once at entry.</summary>
    public static void SweepTempRoot() => SweepUnder(Path.Combine(Path.GetTempPath(), "Jsr4Z8"));

    /// <summary>Test seam: the sweep against an explicit temp base (hermetic
    /// fixture-owned dir instead of the real %TEMP%).</summary>
    internal static void SweepTempRoot(string tempBase) =>
        SweepUnder(Path.Combine(tempBase, "Jsr4Z8"));

    private static void SweepUnder(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return;
            DateTime cutoff = DateTime.UtcNow - SweepGraceWindow;
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                DateTime lastWrite;
                try
                {
                    lastWrite = Directory.GetLastWriteTimeUtc(dir);
                }
                catch (IOException) { continue; }   // vanished between enumerate and check
                catch (UnauthorizedAccessException) { continue; }
                if (lastWrite < cutoff)
                    TryDeleteDir(dir);
            }
        }
        catch (Exception)
        {
            // The sweep must never take a launch down with it.
        }
    }

    /// <summary>Success-path product glob (internal test seam): the newest
    /// <paramref name="prefix"/>*.dng anywhere under <paramref name="root"/>, skipping
    /// any path that traverses a *.partial segment — the backend's resume leftovers.</summary>
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

    /// <summary>Best-effort recursive delete — the temp cleanup on every terminal
    /// outcome. A locked/missing dir must never surface as an error of its own
    /// (DirectoryNotFoundException is an IOException, so a never-created job dir is
    /// swallowed too).</summary>
    private static void TryDeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

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
