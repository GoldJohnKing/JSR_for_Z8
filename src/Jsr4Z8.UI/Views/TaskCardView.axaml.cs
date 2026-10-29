using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Jsr4Z8.UI.ViewModels;

namespace Jsr4Z8.UI.Views;

/// <summary>
/// Card visuals for one TaskGroupViewModel. The state classes (st-ready/st-running/
/// st-done/st-failed/st-blocked on the root border, consumed by this control's styles)
/// are maintained in code-behind from the VM's State because Avalonia has no
/// string→Classes binding; everything else binds straight to the VM. Edit interactions
/// (★设基准/✕移除帧 via the commands' CanExecute, the chips host IsEnabled gate below)
/// and the windowing-only concerns live here too: the card-level RAW picker (添加 RAW…)
/// and the card drop target (卡内补齐) — the latter swallows DragOver/Drop so the
/// window-level handlers never double-handle a card drop.
/// </summary>
public partial class TaskCardView : UserControl
{
    private INotifyPropertyChanged? _subscribed;

    public TaskCardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindStateClasses();
        // 卡内拖放补齐：拖放路由事件没有 XAML 可接线的 CLR 包装，走 AddHandler
        //（同 MainWindow 窗口级模式）；挂在 this 上吃 Root 及其子元素的冒泡。
        AddHandler(DragDrop.DragOverEvent, OnCardDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnCardDragLeave);
        AddHandler(DragDrop.DropEvent, OnCardDrop);
    }

    private void BindStateClasses()
    {
        if (_subscribed != null)
            _subscribed.PropertyChanged -= VmOnPropertyChanged;
        _subscribed = DataContext as INotifyPropertyChanged;
        if (_subscribed != null)
            _subscribed.PropertyChanged += VmOnPropertyChanged;
        UpdateStateClasses();
    }

    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskGroupViewModel.State))
            UpdateStateClasses();
    }

    private void UpdateStateClasses()
    {
        var state = (DataContext as TaskGroupViewModel)?.State;
        Root.Classes.Set("st-ready", state == TaskState.Ready);
        Root.Classes.Set("st-running", state == TaskState.Running);
        Root.Classes.Set("st-done", state == TaskState.Done);
        Root.Classes.Set("st-failed", state == TaskState.Failed);
        Root.Classes.Set("st-blocked", state == TaskState.Blocked);
        // 卡内编辑（★/✕/悬浮）只在 Ready/Blocked 有意义（与 IsEditable 一致）；
        // 其余状态整排只作展示，禁掉点击免得给出死光标。锁定卡的拖放高亮一并清掉。
        bool editable = state is TaskState.Ready or TaskState.Blocked;
        ChipsHost.IsEnabled = editable;
        if (!editable)
            Root.Classes.Set("st-droptarget", false);
    }

    // ---------- 卡内补齐：添加 RAW… ----------

    /// <summary>头部「添加 RAW…」：NEF 多选选择器 → <see cref="TaskGroupViewModel.AddFilesAsync"/>
    /// （同 MainWindow.OnBrowseFiles 的选择器模式，选择器逻辑放 code-behind）。</summary>
    private async void OnAddRawFiles(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TaskGroupViewModel { IsEditable: true } vm)
            return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        string[] paths = await NefFiles.OpenNefPickerAsync(storage, "添加 RAW（Nikon NEF）");
        if (paths.Length > 0)
            await vm.AddFilesAsync(paths);
    }

    // ---------- 卡内补齐：拖放（窗口级保持：卡未处理时冒泡新建组） ----------

    private void OnCardDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        bool accept = DataContext is TaskGroupViewModel { IsEditable: true } && NefFiles.HasNefPayload(e);
        e.DragEffects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        Root.Classes.Set("st-droptarget", accept);
    }

    private async void OnCardDrop(object? sender, DragEventArgs e)
    {
        // 先吞再补齐：窗口级 Drop 不再对卡上落点新建组。
        e.Handled = true;
        Root.Classes.Set("st-droptarget", false);
        if (DataContext is not TaskGroupViewModel vm)
            return;
        string[] paths = NefFiles.FilterNefPaths(
            e.Data.GetFiles()?.Select(f => f.TryGetLocalPath())
            ?? Enumerable.Empty<string?>());
        if (paths.Length > 0)
            await vm.AddFilesAsync(paths);          // VM 自带 IsEditable 守卫
    }

    private void OnCardDragLeave(object? sender, DragEventArgs e)
    {
        Root.Classes.Set("st-droptarget", false);
    }
}
