namespace Jsr4Z8.Core.Backend;

/// <summary>Outcome of one backend run: payload of the single Finished event and of RunAsync.</summary>
public sealed record RunnerResult(bool Success, string Message, TimeSpan Elapsed);

/// <summary>
/// The run-identity snapshot BackendRunner synthesizes at engine spawn (the direct
/// bare-line protocol has no START envelope): stage/engine/mode are the request's
/// own constants (重建 / stormer7 / full), compressor is the engine executable path,
/// and pid is the spawned engine parent — the root of the tree kill and the process
/// whose exit code the run resolves to.
/// </summary>
public sealed record JsrStart(string Stage, string Engine, string Mode, string Compressor, int Pid);

/// <summary>
/// One direct jsr_backend.exe invocation: the engine executable, its argv
/// (<see cref="BackendRequest.BuildArguments"/>), and the job log file every
/// streamed line is appended to. No request.json and no config dir — the direct
/// protocol needs neither (no cfg, no preview chain; the spawned process's
/// exit code is the run's exit code).
/// </summary>
public sealed record BackendRunSpec(
    string EngineExe,
    IReadOnlyList<string> Arguments,
    string LogFile);

/// <summary>Events surfaced by <see cref="BackendRunner"/> while a run is in flight.</summary>
public abstract record RunnerEvent;

/// <summary>The engine process was spawned; carries the synthesized run identity.</summary>
public sealed record Started(JsrStart Start) : RunnerEvent;

/// <summary>
/// One engine stdout/stderr line. <see cref="Progress"/> is set when the line is an
/// SRHQ progress line; stderr lines carry the "stderr| " prefix in <see cref="Text"/>
/// so the tail keeps both streams distinguishable.
/// </summary>
public sealed record LogLine(string Text, ProgressLine? Progress) : RunnerEvent;

/// <summary>The run ended (success, failure, or cancellation); always the last event.</summary>
public sealed record Finished(RunnerResult Result) : RunnerEvent;
