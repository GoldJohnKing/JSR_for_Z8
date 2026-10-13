using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Jsr4Z8.UI.Services;
using Jsr4Z8.UI.ViewModels;
using Jsr4Z8.UI.Views;

namespace Jsr4Z8.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Minimal constructor DI (Task 13): the queue mediator replaces NullTaskRunner —
            // engine events are marshaled back onto the UI thread through the same
            // dispatcher the dialog validation uses.
            var theme = new ThemeService();
            theme.Apply();
            var runner = new QueueMediator(dispatcher: new AvaloniaDispatcher());
            var main = new MainViewModel(theme, runner);
            main.AddTaskRequested += (_, _) => ShowNewTaskDialog(desktop.MainWindow!, main);
            desktop.MainWindow = new MainWindow { DataContext = main };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Task 12 composition: ＋添加任务组 opens the NewTaskDialog; CreateCompleted
    /// turns the validated group into a card via MainViewModel.AddValidated and closes.</summary>
    private static void ShowNewTaskDialog(Window owner, MainViewModel main)
    {
        var vm = new NewTaskViewModel(post: action => Dispatcher.UIThread.Post(action));
        var dialog = new NewTaskDialog { DataContext = vm };
        vm.CreateCompleted += (_, created) =>
        {
            main.AddValidated(created.Group, created.Validation);
            dialog.Close();
        };
        vm.CancelRequested += (_, _) => dialog.Close();
        dialog.ShowDialog(owner);
    }
}
