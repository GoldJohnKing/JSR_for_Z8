using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Jsr4Z8.UI.ViewModels;

namespace Jsr4Z8.UI.Views;

/// <summary>
/// Card visuals for one TaskGroupViewModel. The state classes (st-ready/st-running/
/// st-done/st-failed/st-blocked on the root border, consumed by this control's styles)
/// are maintained in code-behind from the VM's State because Avalonia has no
/// string→Classes binding; everything else binds straight to the VM. The chip row's
/// click-to-set-reference interaction forwards to SetReferenceCommand and is gated to
/// Ready/Blocked cards (matching the command's own CanExecute).
/// </summary>
public partial class TaskCardView : UserControl
{
    private INotifyPropertyChanged? _subscribed;

    public TaskCardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindStateClasses();
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
        // 换基准只在 Ready/Blocked 有意义（与 SetReferenceCommand.CanExecute 一致）；
        // 其余状态芯片只作展示，禁掉点击免得给出死光标。
        ChipsHost.IsEnabled = state is TaskState.Ready or TaskState.Blocked;
    }

    /// <summary>点芯片 → 该帧设为基准（VM 自带状态守卫，这里的启用门只负责手感）。</summary>
    private void OnChipPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: FrameChipViewModel chip } &&
            DataContext is TaskGroupViewModel vm)
        {
            vm.SetReferenceCommand.Execute(chip);
        }
    }
}
