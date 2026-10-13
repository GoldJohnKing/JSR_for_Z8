using Avalonia.Controls;

namespace Jsr4Z8.UI.Views;

/// <summary>
/// Card-flow main window (docs/design/layout.html option C). DataContext is the
/// MainViewModel built in App.OnFrameworkInitializationCompleted. Dialog events
/// (AddTaskRequested → NewTaskDialog, Task 12) get wired by views in later tasks;
/// this shell only hosts the top bar, the card column and the empty-state hint.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
