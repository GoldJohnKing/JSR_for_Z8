namespace Jsr4Z8.Core.Model;

/// <summary>
/// One JSR job: 7 light frames. <see cref="FramePaths"/> is supplied by the caller
/// already deduplicated and sorted by filename in natural ascending order (the
/// creator's natural sort owns that ordering); the first frame is the reference frame
/// every later rule compares against. Dark calibration is always resolved from the
/// embedded preset table by the reference frame's (compression, ISO).
/// </summary>
public sealed record TaskGroup(IReadOnlyList<string> FramePaths)
{
    /// <summary>The reference (first) frame. Only meaningful when FramePaths is non-empty.</summary>
    public string ReferencePath => FramePaths[0];
}
