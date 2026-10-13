using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Jsr4Z8.UI.ViewModels;

namespace Jsr4Z8.UI.Views;

/// <summary>
/// 新建任务组 dialog (docs/design/add-task-flow-v3.html). Per the headless-VM contract
/// this code-behind only wires interaction onto <see cref="NewTaskViewModel"/>: drag-drop
/// and click-to-pick for the frame chips, the OS file picker for the dark frame (start
/// dir = the persisted LastDarkPath's folder — Task 14), and chip clicks. All rules —
/// filtering, ordering, validation, gating — live in the VM.
/// </summary>
public partial class NewTaskDialog : Window
{
    /// <summary>Z 8 writes NEF only; .nrw and friends are filtered out everywhere.</summary>
    private static readonly FilePickerFileType NefFile = new("Nikon NEF (RAW)") { Patterns = new[] { "*.nef" } };

    public NewTaskDialog()
    {
        InitializeComponent();
        // Routed drag events are wired here (inherited CLR events are not XAML-attachable).
        DropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private NewTaskViewModel? Vm => DataContext as NewTaskViewModel;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Vm is not { } vm || e.Data.GetFiles() is not { } items)
            return;
        vm.HandleFilesDropped(items.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    /// <summary>Click-to-pick on the empty drop zone (“或点击选择”); chips present → no-op.</summary>
    private void OnDropZonePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { HasNoFrames: true } vm)
            _ = PickFramesAsync(vm);
    }

    /// <summary>Click a chip → swap it with the current ★基准 (与现任互换).</summary>
    private void OnChipPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: FrameChipVm chip } && Vm is { } vm)
            vm.SetReferenceCommand.Execute(chip);
    }

    /// <summary>Both dark rows: empty dashed button and the set row's 更换… open the picker.</summary>
    private void OnDarkRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm)
            _ = PickDarkAsync(vm);
    }

    private async Task PickFramesAsync(NewTaskViewModel vm)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 7 张 RAW 帧（Nikon NEF）",
            AllowMultiple = true,
            FileTypeFilter = new[] { NefFile },
        });
        vm.HandleFilesDropped(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private async Task PickDarkAsync(NewTaskViewModel vm)
    {
        var options = new FilePickerOpenOptions
        {
            Title = "选择黑场文件（Nikon NEF）",
            AllowMultiple = false,
            FileTypeFilter = new[] { NefFile },
        };
        if (vm.LastDarkDir is { } dir && Directory.Exists(dir))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(dir);
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(options);
        if (files is [{ } picked] && picked.TryGetLocalPath() is { } path)
            vm.SelectDark(path);   // SelectDark persists LastDarkPath (next picker starts in its folder)
    }
}
