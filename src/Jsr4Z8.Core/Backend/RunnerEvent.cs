namespace Jsr4Z8.Core.Backend;

/// <summary>Outcome of one backend run: payload of the single Finished event and of RunAsync.</summary>
public sealed record RunnerResult(bool Success, string Message, TimeSpan Elapsed);

/// <summary>
/// One backend invocation. Each run must get its own <see cref="ConfigDir"/>: the real
/// backend treats it as an exclusive lock (protocol fact #3), which is how concurrent
/// tasks stay out of each other's way.
/// </summary>
public sealed record BackendRunSpec(
    string BridgeExe,
    string ResourcesDir,
    string ConfigDir,
    string RequestJsonPath,
    string LogFile);

/// <summary>Events surfaced by <see cref="BackendRunner"/> while a run is in flight.</summary>
public abstract record RunnerEvent;

/// <summary>The ASSISTANT_JSR_START envelope was received.</summary>
public sealed record Started(JsrStart Json) : RunnerEvent;

/// <summary>
/// One backend log line. <see cref="Progress"/> is set when the line is an SRHQ progress
/// line; stderr lines carry the "stderr| " prefix in <see cref="Text"/> so the tail keeps
/// both streams distinguishable.
/// </summary>
public sealed record LogLine(string Text, ProgressLine? Progress) : RunnerEvent;

/// <summary>The run ended (success, failure, or cancellation); always the last event.</summary>
public sealed record Finished(RunnerResult Result) : RunnerEvent;
