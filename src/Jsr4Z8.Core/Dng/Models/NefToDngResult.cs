namespace Jsr4Z8.Core.Dng;

/// <summary>
/// Result of <see cref="NefToDngConverter.Convert"/> — port of Save_Z8's
/// ProcessingResult minus the BlackLevelFixer fields (this project has no
/// notch-repair step).
/// </summary>
public sealed record NefToDngResult
{
    public required string OutputPath { get; init; }

    /// <summary>Failure description; null on success.</summary>
    public string? Error { get; init; }
}
