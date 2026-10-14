using Avalonia.Controls;

namespace Jsr4Z8.UI.Views;

/// <summary>
/// ⚙高级设置 dialog (docs/design/settings.html option B; the VRAM slider row was
/// removed post-release — 始终不限制). Per the headless-VM contract this code-behind
/// is XAML plumbing only — SettingsViewModel holds every rule; the folder picker
/// arrives as IFolderPicker from the App composition root, and 取消/保存 close the
/// dialog through the VM's events.
/// </summary>
public partial class SettingsDialog : Window
{
    public SettingsDialog() => InitializeComponent();
}
