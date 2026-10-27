using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Jsr4Z8.Core.Payload;
using Jsr4Z8.UI.Services;
using Jsr4Z8.UI.ViewModels;

namespace Jsr4Z8.UI.Views;

/// <summary>
/// Save_Z8-style main window shell. All state lives in MainViewModel; this code-behind
/// only bridges windowing-only concerns: full-window drag-and-drop (hover overlay that
/// hides instantly when a card takes over the drag, plus on DragLeave), the shared RAW
/// file picker, the theme button's icon/tooltip sync and the assembly-version read.
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? ViewModel => DataContext as MainViewModel;

    // FALLBACK ONLY: while an OLE drag's modal loop runs with a still mouse, nothing
    // pumps the dispatcher (no DragOver callbacks), so a Background-priority
    // DispatcherTimer cannot tick — the overlay hide must NOT depend on it. The
    // primary hide paths are the handledEventsToo DragOver branch (a card took over)
    // and DragLeave; this timer only catches a drag that stalled over empty space
    // once the dispatcher is being pumped again (Save_Z8 pattern, 600ms).
    private readonly DispatcherTimer _dragHideTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(600)
    };

    public MainWindow()
    {
        InitializeComponent();
        // Drop target opt-in (Avalonia defaults to false → OLE reports no effects).
        DragDrop.SetAllowDrop(this, true);
        // handledEventsToo: cards consume DragOver (e.Handled) for 卡内补齐 — the window
        // still needs to see those events to hide its overlay instantly.
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
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
        {
            vm.AppVersion = ReadAppVersion();
            vm.BackendVersion = ReadBackendVersion();
        }
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

    /// <summary>Backend payload version as "v18.1" from the embedded manifest's
    /// backendVersion (written by pack-payload at build time — repacking a new
    /// reference/ version updates it automatically); empty when absent/unreadable
    /// (fast dev builds) so the top-right line hides.</summary>
    private static string ReadBackendVersion() =>
        PayloadManager.TryReadBackendVersion() is { Length: > 0 } v ? $"v{v.TrimStart('v')}" : "";

    // ---------- 外部链接（算法出处 / GitHub 仓库） ----------

    private void OnOpenAuthorRepo(object? sender, RoutedEventArgs e)
        => OpenExternalUrl("https://github.com/y-g-jiang/Jiangtherapee-Super-Resolution-World-Best");

    private void OnOpenGitHub(object? sender, RoutedEventArgs e)
        => OpenExternalUrl("https://github.com/GoldJohnKing/JSR_for_Z8");

    /// <summary>Shell-opens an external URL; never throws (display-only affordance).</summary>
    private static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 打不开浏览器不是主流程故障：静默降级（链接仅为署名展示）
        }
    }

    // ---------- 拖放 ----------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // A descendant (task card) already consumed the drag — kill the window-level
        // hint instantly and leave e.Handled/effects to the card.
        if (e.Handled)
        {
            if (ViewModel is { } vmHide)
                vmHide.IsDragOver = false;
            _dragHideTimer.Stop();
            return;
        }

        e.Handled = true;
        bool nef = NefFiles.HasNefPayload(e);
        e.DragEffects = nef ? DragDropEffects.Copy : DragDropEffects.None;
        if (ViewModel is { } vm)
            vm.IsDragOver = nef;
        _dragHideTimer.Stop();
        _dragHideTimer.Start();
    }

    /// <summary>The drag left the window entirely — hide the overlay immediately
    /// (primary hide path; the timer is only a fallback, see _dragHideTimer).</summary>
    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _dragHideTimer.Stop();
        if (ViewModel is { } vm)
            vm.IsDragOver = false;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragHideTimer.Stop();
        if (ViewModel is not { } vm)
            return;
        vm.IsDragOver = false;
        string[] paths = NefFiles.FilterNefPaths(
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

        string[] paths = NefFiles.FilterNefPaths(files.Select(f => f.TryGetLocalPath()));
        if (paths.Length > 0 && ViewModel is { } vm)
            await vm.AddFilesAsync(paths);
    }

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
