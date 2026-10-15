using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Jsr4Z8.Core.Queue;
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
            // dispatcher the inline group creation uses. ONE shared persisted
            // SettingsService (%LOCALAPPDATA%\JSR4Z8\settings.json) backs the queue's
            // ISettingsProvider, the settings dialog and the startup sweep — a Save is
            // visible to the next run without restart.
            var theme = new ThemeService();
            theme.Apply();
            var settings = new SettingsService();
            // Startup sweep (Task 2's SweepTempRoot): failed/stopped runs delete their
            // own job temp dir, so anything left under <TempBase>\Jsr4Z8 is crash debris —
            // background, fire-and-forget, never blocks or takes the launch down.
            _ = Task.Run(() => TaskQueueRunner.SweepTempRoot(
                new SettingsServiceProvider(settings).CurrentSettings));
            var runner = new QueueMediator(
                dispatcher: new AvaloniaDispatcher(), settings: new SettingsServiceProvider(settings));
            // Group creation is inline since Task 4: no new-task dialog anymore — dropped
            // or picked NEF files become cards directly, posted onto the UI thread here.
            var main = new MainViewModel(theme, runner, post: action => Dispatcher.UIThread.Post(action));
            main.OpenSettingsRequested += (_, _) => ShowSettingsDialog(desktop.MainWindow!, settings);
            desktop.MainWindow = new MainWindow { DataContext = main };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Task 14 composition: ⚙ opens the SettingsDialog; 保存 persists through the
    /// shared service (visible to the queue's next run) and closes; 取消 discards.</summary>
    private static void ShowSettingsDialog(Window owner, SettingsService settings)
    {
        var vm = new SettingsViewModel(settings, new WindowFolderPicker(owner));
        var dialog = new SettingsDialog { DataContext = vm };
        vm.SettingsChanged += (_, _) => dialog.Close();
        vm.CancelRequested += (_, _) => dialog.Close();
        dialog.ShowDialog(owner);
    }
}
