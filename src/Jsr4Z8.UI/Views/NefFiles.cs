using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace Jsr4Z8.UI.Views;

/// <summary>Shared NEF (Nikon RAW) payload helpers for the drop targets and file
/// pickers of the window and the task cards (the Z 8 writes NEF only).</summary>
internal static class NefFiles
{
    /// <summary>Picker/drop path filter: non-null paths whose extension is .nef
    /// (case-insensitive).</summary>
    internal static string[] FilterNefPaths(IEnumerable<string?> paths) =>
        paths.Where(p => !string.IsNullOrEmpty(p) && IsNef(p))
             .Select(p => p!)
             .ToArray();

    /// <summary>The cursor shows Copy only for payloads carrying at least one .NEF —
    /// text/image payloads and NEF-less file lists are rejected.</summary>
    internal static bool HasNefPayload(DragEventArgs e) =>
        e.Data.GetFiles() is { } items && items.Any(f =>
            f.TryGetLocalPath() is { } path && IsNef(path));

    /// <summary>Extension test for .nef (case-insensitive).</summary>
    internal static bool IsNef(string path) =>
        string.Equals(Path.GetExtension(path), ".nef", StringComparison.OrdinalIgnoreCase);
}
