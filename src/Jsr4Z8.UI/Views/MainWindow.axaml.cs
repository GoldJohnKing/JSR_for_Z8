using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Jsr4Z8.UI.Services;
using Jsr4Z8.UI.ViewModels;

namespace Jsr4Z8.UI.Views;

/// <summary>
/// Save_Z8-style main window shell. All state lives in MainViewModel; this code-behind
/// only bridges windowing-only concerns: full-window drag-and-drop (hover overlay with
/// the DragOver-renewed 600ms DispatcherTimer fade pattern), the shared RAW file
/// picker, the theme button's icon/tooltip sync and the assembly-version read.
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? ViewModel => DataContext as MainViewModel;

    // DragLeave/DragOver ordering is unstable while hovering child elements — renew the
    // timer on every DragOver and collapse the overlay when it expires, so the
    // full-window drop overlay reliably disappears (Save_Z8 pattern).
    private readonly DispatcherTimer _dragHideTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(600)
    };

    public MainWindow()
    {
        InitializeComponent();
        // Drop target opt-in (Avalonia defaults to false → OLE reports no effects).
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        _dragHideTimer.Tick += (_, _) =>
        {
            _dragHideTimer.Stop();
            if (ViewModel is { } vm)
                vm.IsDragOver = false;
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (ViewModel is { } vm)
            vm.AppVersion = ReadAppVersion();
        ApplyThemeVisuals();
    }

    /// <summary>Assembly version as "v{major}.{minor}.{build}"; empty when unreadable.</summary>
    private static string ReadAppVersion()
    {
        try
        {
            Version? v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            return "";
        }
    }

    // ---------- 拖放 ----------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        bool nef = HasNefPayload(e);
        e.DragEffects = nef ? DragDropEffects.Copy : DragDropEffects.None;
        if (ViewModel is { } vm)
            vm.IsDragOver = nef;
        _dragHideTimer.Stop();
        _dragHideTimer.Start();
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragHideTimer.Stop();
        if (ViewModel is not { } vm)
            return;
        vm.IsDragOver = false;
        string[] paths = FilterNefPaths(
            e.Data.GetFiles()?.Select(f => f.TryGetLocalPath())
            ?? Enumerable.Empty<string?>());
        if (paths.Length > 0)
            await vm.AddFilesAsync(paths);
    }

    // ---------- 文件选择（空态按钮与顶栏「添加 RAW…」共用）----------

    private async void OnBrowseFiles(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 7 张 RAW（Nikon NEF）",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Nikon RAW") { Patterns = new[] { "*.nef" } }
            }
        });

        string[] paths = FilterNefPaths(files.Select(f => f.TryGetLocalPath()));
        if (paths.Length > 0 && ViewModel is { } vm)
            await vm.AddFilesAsync(paths);
    }

    /// <summary>拖放与文件选择器共用的 NEF 过滤：非空且扩展名为 .nef（忽略大小写）。</summary>
    private static string[] FilterNefPaths(IEnumerable<string?> paths) =>
        paths.Where(p => !string.IsNullOrEmpty(p) && IsNef(p))
             .Select(p => p!)
             .ToArray();

    /// <summary>The cursor shows Copy only for payloads carrying at least one .NEF — the
    /// Z 8 writes NEF only; text/image payloads and NEF-less file lists are rejected.</summary>
    private static bool HasNefPayload(DragEventArgs e) =>
        e.Data.GetFiles() is { } items && items.Any(f =>
            f.TryGetLocalPath() is { } path && IsNef(path));

    private static bool IsNef(string path) =>
        string.Equals(Path.GetExtension(path), ".nef", StringComparison.OrdinalIgnoreCase);

    // ---------- 主题切换 ----------

    private void OnThemeToggle(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        vm.ToggleThemeCommand.Execute(null);
        ApplyThemeVisuals();
    }

    /// <summary>Moon while Dark/Default, Sun while Light (3-mode cycle); the tooltip
    /// always names the current mode.</summary>
    private void ApplyThemeVisuals()
    {
        if (ViewModel is not { } vm)
            return;
        bool light = vm.Theme.Mode == ThemeMode.Light;
        ThemeMoonIcon.IsVisible = !light;
        ThemeSunIcon.IsVisible = light;
        ToolTip.SetTip(ThemeButton, $"切换主题（当前：{vm.Theme.ModeText}）");
    }
}
