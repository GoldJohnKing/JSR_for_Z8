namespace Jsr4Z8.Core.Model;

/// <summary>
/// One JSR job: 7 light frames plus 1 dark frame. <see cref="FramePaths"/> is
/// supplied by the caller already deduplicated and sorted by filename in natural
/// ascending order (the new-task dialog owns that ordering); the first frame is
/// the reference frame every later rule compares against.
/// </summary>
public sealed record TaskGroup(IReadOnlyList<string> FramePaths, string DarkPath)
{
    /// <summary>The reference (first) frame. Only meaningful when FramePaths is non-empty.</summary>
    public string ReferencePath => FramePaths[0];
}
