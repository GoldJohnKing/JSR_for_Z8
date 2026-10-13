using System.ComponentModel;
using Avalonia.Controls;
using Jsr4Z8.UI.ViewModels;

namespace Jsr4Z8.UI.Views;

/// <summary>
/// Card visuals for one TaskGroupViewModel. The state classes (st-ready/st-running/
/// st-done/st-failed/st-blocked on the root border, consumed by this control's styles)
/// are maintained in code-behind from the VM's State because Avalonia has no
/// string→Classes binding; everything else binds straight to the VM.
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
    }
}
