using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Jsr4Z8.UI.ViewModels;

namespace Jsr4Z8.UI.Views;

/// <summary>
/// Card-flow main window (docs/design/layout.html option C). DataContext is the
/// MainViewModel built in App.OnFrameworkInitializationCompleted. Dialog events
/// (AddTaskRequested → NewTaskDialog, Task 12; OpenSettings → SettingsDialog, Task 14)
/// are wired by App. Post-release fix 2: the window root itself is a drop target —
/// RAW files dropped anywhere on the shell open the 新建任务组 dialog pre-filled with
/// them (MainViewModel.DropFiles → DropFilesRequested; the dialog then behaves exactly
/// as if the files had been dropped onto its own zone).
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Routed drag events are wired here (inherited CLR events are not XAML-attachable),
        // on the window root — the same proven pattern NewTaskDialog uses on its drop zone.
        // The root carries DragDrop.AllowDrop=true (XAML) so drops anywhere route here.
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasNefPayload(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not MainViewModel vm || !HasNefPayload(e))
            return;
        // All local paths go to the VM; NewTaskViewModel filters to .NEF (a mixed
        // NEF+jpg drop prefills only the NEF files).
        vm.DropFiles(e.Data.GetFiles()?
            .Select(f => f.TryGetLocalPath()).OfType<string>()
            .ToArray() ?? Array.Empty<string>());
    }

    /// <summary>The cursor shows Copy only for payloads carrying at least one .NEF — the
    /// Z 8 writes NEF only; text/image payloads and raw-less file lists are rejected.</summary>
    private static bool HasNefPayload(DragEventArgs e) =>
        e.Data.GetFiles() is { } items && items.Any(f =>
            f.TryGetLocalPath() is { } path &&
            string.Equals(Path.GetExtension(path), ".nef", StringComparison.OrdinalIgnoreCase));
}
