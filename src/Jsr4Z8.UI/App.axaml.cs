using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Jsr4Z8.Core.Queue;
using Jsr4Z8.Core.Settings;
using Jsr4Z8.UI.Services;
using Jsr4Z8.UI.ViewModels;
using Jsr4Z8.UI.Views;

namespace Jsr4Z8.UI;

public partial class App : Application
{
    /// <summary>Single-instance guard (final-review F1): held for the process lifetime
    /// (process exit releases it). Without it a second launch would sweep the first
    /// instance's in-flight job dir (SweepTempRoot) and prune its payload dir
    /// (PruneStaleVersions) mid-run.</summary>
    private Mutex? _singleInstanceMutex;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\JSR4Z8_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                // Already running: show the notice and stop — no sweep, no queue, no main
                // window (closing the notice shuts the process down via the default
                // OnLastWindowClose mode). This handle never owned the mutex → dispose it.
                desktop.MainWindow = new SingleInstanceNoticeWindow();
                _singleInstanceMutex.Dispose();
                base.OnFrameworkInitializationCompleted();
                return;
            }
            // Minimal constructor DI (Task 13): the queue mediator replaces NullTaskRunner —
            // engine events are marshaled back onto the UI thread through the same
            // dispatcher the inline group creation uses. Settings are gone (2026-10-16
            // user ruling: the settings feature was removed wholesale) — the queue runs
            // on the mediator's DefaultSettingsProvider, i.e. plan defaults forever.
            var theme = new ThemeService();
            theme.Apply();
            // Startup sweep (Task 2's SweepTempRoot): failed/stopped runs delete their
            // own job temp dir, so anything left under <TempBase>\Jsr4Z8 is crash debris —
            // background, fire-and-forget, never blocks or takes the launch down.
            _ = Task.Run(() => TaskQueueRunner.SweepTempRoot(new AppSettings()));
            var runner = new QueueMediator(dispatcher: new AvaloniaDispatcher());
            // Group creation is inline since Task 4: no new-task dialog anymore — dropped
            // or picked NEF files become cards directly, posted onto the UI thread here.
            var main = new MainViewModel(theme, runner, post: action => Dispatcher.UIThread.Post(action));
            desktop.MainWindow = new MainWindow { DataContext = main };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Second-launch notice (final-review F1): the real instance already holds
    /// <c>Local\JSR4Z8_SingleInstance</c>. Pure code-behind (no XAML file needed) — the
    /// application-level SemiTheme styles the button/text automatically.</summary>
    private sealed class SingleInstanceNoticeWindow : Window
    {
        public SingleInstanceNoticeWindow()
        {
            Title = "JSR for Z8";
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            SizeToContent = SizeToContent.WidthAndHeight;
            CanResize = false;
            var close = new Button
            {
                Content = "关闭",
                MinWidth = 88,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            close.Click += (_, _) => Close();
            Content = new StackPanel
            {
                Margin = new Thickness(28),
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = "JSR for Z8 已在运行，请勿重复启动。",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 380,
                    },
                    close,
                },
            };
        }
    }
}
