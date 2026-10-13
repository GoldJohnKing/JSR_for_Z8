using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jsr4Z8.UI.Services;

namespace Jsr4Z8.UI.ViewModels;

/// <summary>
/// Root ViewModel of the card-flow main window (docs/design/layout.html option C): the
/// vertical task list plus the top-bar commands. Pure state, headless-testable — the
/// dialogs are opened by the views through the *Requested events (AddTask → NewTaskDialog
/// in Task 12, OpenSettings → SettingsDialog in Task 14), keeping this VM free of
/// windowing dependencies.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ThemeService _theme;

    public ObservableCollection<TaskGroupViewModel> Tasks { get; } = new();

    public event EventHandler? AddTaskRequested;

    public event EventHandler? OpenSettingsRequested;

    public MainViewModel(ThemeService? theme = null)
    {
        _theme = theme ?? new ThemeService();
        Tasks.CollectionChanged += OnTasksChanged;
    }

    /// <summary>Theme state for the top-bar toggle (Mode/ModeText bindable).</summary>
    public ThemeService Theme => _theme;

    /// <summary>True while the list is empty — drives the dashed drop-hint overlay
    /// (drop wiring itself is Task 12; the hint is visual only).</summary>
    [ObservableProperty] private bool _hasNoTasks = true;

    /// <summary>Entry point for new cards (Task 12's dialog calls this): wires the card's
    /// RemoveRequested back into Tasks. Adding via Tasks directly bypasses that wiring.</summary>
    public void Add(TaskGroupViewModel vm)
    {
        vm.RemoveRequested += (_, _) => Tasks.Remove(vm);
        Tasks.Add(vm);
    }

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HasNoTasks = Tasks.Count == 0;
        StartAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AddTask() => AddTaskRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Starts every Ready card sequentially (the queue model Task 13 formalizes).</summary>
    [RelayCommand(CanExecute = nameof(CanStartAll))]
    private async Task StartAllAsync()
    {
        foreach (var vm in Tasks.Where(t => t.State == TaskState.Ready).ToList())
            await vm.StartAsync();
    }

    private bool CanStartAll() => Tasks.Any(t => t.State == TaskState.Ready);

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ToggleTheme() => _theme.Cycle();
}
