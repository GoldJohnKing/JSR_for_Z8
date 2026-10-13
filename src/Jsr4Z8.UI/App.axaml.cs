using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            // Minimal constructor DI (Task 13 replaces NullTaskRunner with the real queue runner).
            var theme = new ThemeService();
            theme.Apply();
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(theme) };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
