namespace Jsr4Z8.Core.Settings;

/// <summary>Where the final JSR_&lt;基准帧名&gt;.dng lands.</summary>
public enum OutputMode
{
    /// <summary>Next to the reference frame (its directory).</summary>
    SameAsInput,

    /// <summary>The user-configured <see cref="AppSettings.CustomOutputDir"/>.</summary>
    Custom,
}

/// <summary>Where per-job temp dirs (&lt;TempBase&gt;\Jsr4Z8\&lt;jobId&gt;\) are created.</summary>
public enum TempMode
{
    /// <summary>The OS temp path (Path.GetTempPath()).</summary>
    Default,

    /// <summary>The user-configured <see cref="AppSettings.CustomTempDir"/>.</summary>
    Custom,
}

/// <summary>
/// App-level settings: a pure record with plan defaults (output next to the reference
/// frame, OS temp). The settings feature was removed wholesale (2026-10-16 user
/// ruling) — the app runs on these defaults forever; the queue reads the values through
/// the UI's ISettingsProvider seam and never mutates them. The VRAM budget field was
/// removed post-release and the request now omits the memory key entirely (user
/// directive: 不设预算 — the driver allocates).
/// </summary>
public sealed record AppSettings(
    OutputMode OutputMode = OutputMode.SameAsInput,
    string? CustomOutputDir = null,
    TempMode TempMode = TempMode.Default,
    string? CustomTempDir = null);
