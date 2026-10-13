using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Jsr4Z8.UI.Services;

/// <summary>
/// Folder-picker seam for the ⚙高级设置 dialog (Task 14): the ViewModel stays
/// headless-testable while production opens the OS picker through the host window's
/// StorageProvider (the same mechanism NewTaskDialog uses for files).
/// </summary>
public interface IFolderPicker
{
    /// <summary>Opens a folder picker; returns the picked path, or null when cancelled.</summary>
    Task<string?> PickFolderAsync(string title);
}

/// <summary>Production implementation: the owner window's StorageProvider.</summary>
public sealed class WindowFolderPicker : IFolderPicker
{
    private readonly TopLevel _owner;

    public WindowFolderPicker(TopLevel owner) => _owner = owner;

    public async Task<string?> PickFolderAsync(string title)
    {
        IReadOnlyList<IStorageFolder> folders = await _owner.StorageProvider
            .OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title })
            .ConfigureAwait(true);
        return folders is [{ } folder] ? folder.TryGetLocalPath() : null;
    }
}
