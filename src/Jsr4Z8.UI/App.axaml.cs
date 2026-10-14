using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Jsr4Z8.Core.Settings;
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
            // dispatcher the dialog validation uses. Task 14: ONE shared persisted
            // SettingsService (%LOCALAPPDATA%\JSR4Z8\settings.json) backs the queue's
            // ISettingsProvider, the settings dialog and the dark picker's LastDarkPath
            // memory — a Save is visible to the next run without restart.
            var theme = new ThemeService();
            theme.Apply();
            var settings = new SettingsService();
            var runner = new QueueMediator(
                dispatcher: new AvaloniaDispatcher(), settings: new SettingsServiceProvider(settings));
            var main = new MainViewModel(theme, runner);
            main.AddTaskRequested += (_, _) => ShowNewTaskDialog(desktop.MainWindow!, main, settings);
            main.DropFilesRequested += (_, e) => ShowNewTaskDialog(desktop.MainWindow!, main, settings, e);
            main.OpenSettingsRequested += (_, _) => ShowSettingsDialog(desktop.MainWindow!, settings);
            desktop.MainWindow = new MainWindow { DataContext = main };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Task 12 composition: ＋添加任务组 opens the NewTaskDialog; CreateCompleted
    /// turns the validated group into a card via MainViewModel.AddValidated and closes.
    /// Task 14: the dialog VM gets the settings service (dark picker start dir + LastDarkPath memory).
    /// Post-release fix 2: <paramref name="preloadFiles"/> (a main-window drop) opens the
    /// same dialog with those files preloaded as chips — the dark frame stays a user choice.</summary>
    private static void ShowNewTaskDialog(Window owner, MainViewModel main, SettingsService settings,
        IReadOnlyList<string>? preloadFiles = null)
    {
        var vm = new NewTaskViewModel(post: action => Dispatcher.UIThread.Post(action), settings: settings);
        if (preloadFiles is { Count: > 0 })
            vm.PreloadFiles(preloadFiles);
        var dialog = new NewTaskDialog { DataContext = vm };
        vm.CreateCompleted += (_, created) =>
        {
            main.AddValidated(created.Group, created.Validation);
            dialog.Close();
        };
        vm.CancelRequested += (_, _) => dialog.Close();
        dialog.ShowDialog(owner);
    }

    /// <summary>Task 14 composition: ⚙ opens the SettingsDialog; 保存 persists through
    /// the shared service (visible to the queue's next run) and closes; 取消 discards.</summary>
    private static void ShowSettingsDialog(Window owner, SettingsService settings)
    {
        var vm = new SettingsViewModel(settings, new WindowFolderPicker(owner));
        var dialog = new SettingsDialog { DataContext = vm };
        vm.SettingsChanged += (_, _) => dialog.Close();
        vm.CancelRequested += (_, _) => dialog.Close();
        dialog.ShowDialog(owner);
    }
}
