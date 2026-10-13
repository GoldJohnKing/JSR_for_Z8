using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Jsr4Z8.UI.Services;

/// <summary>Top-bar theme toggle modes; <see cref="Default"/> follows the OS.</summary>
public enum ThemeMode
{
    Default,
    Dark,
    Light,
}

/// <summary>
/// In-memory theme state: default Dark (user choice C; persistence deliberately deferred —
/// Task 14 owns settings storage). Propagates the mode to Avalonia's
/// <see cref="Application.RequestedThemeVariant"/> — Semi.Avalonia 11.2.x ships its
/// palettes as ThemeDictionaries and follows that variant (SemiTheme itself exposes no
/// variant property). Headless-safe: Apply() is a no-op when no Avalonia Application
/// exists, so the service is unit-testable without a UI.
/// </summary>
public partial class ThemeService : ObservableObject
{
    private ThemeMode _mode = ThemeMode.Dark;

    public ThemeMode Mode
    {
        get => _mode;
        private set
        {
            if (SetProperty(ref _mode, value))
                OnPropertyChanged(nameof(ModeText));
        }
    }

    public string ModeText => Mode switch
    {
        ThemeMode.Dark => "暗色",
        ThemeMode.Light => "亮色",
        _ => "跟随系统",
    };

    /// <summary>Cycle order: Dark → Light → Default → Dark.</summary>
    public void Cycle()
    {
        Mode = Mode switch
        {
            ThemeMode.Dark => ThemeMode.Light,
            ThemeMode.Light => ThemeMode.Default,
            _ => ThemeMode.Dark,
        };
        Apply();
    }

    /// <summary>Propagates Mode to the running application (no-op headless).</summary>
    public void Apply()
    {
        if (Application.Current is null)
            return;

        Application.Current.RequestedThemeVariant = Mode switch
        {
            ThemeMode.Dark => ThemeVariant.Dark,
            ThemeMode.Light => ThemeVariant.Light,
            _ => null, // Default: inherit from the OS
        };
    }
}
