using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jsr4Z8.Core.Settings;
using Jsr4Z8.UI.Services;

namespace Jsr4Z8.UI.ViewModels;

/// <summary>
/// Headless ViewModel of the ⚙高级设置 dialog (docs/design/settings.html option B
/// 滑块精调): 显存预算 slider 1–16 GB on a 0.5 grid + numeric box, both bound to
/// VramGb with the live value next to the label; 输出路径 radio 与输入 RAW 同目录 /
/// 自定义 + input + 浏览…; 临时文件路径 radio 系统默认（%TEMP%）/ 自定义 + input +
/// 浏览…; a live 示例输出 line (custom dir, or 基准帧所在目录示意 — placeholder sample
/// JSR_DSC_7576.dng); footer 取消/保存. Save writes through the shared
/// SettingsService (load-modify: only the dialog's own fields change — LastDarkPath
/// etc. survive) and raises SettingsChanged; Cancel discards. The folder picker is the
/// IFolderPicker seam (fake-pickable in tests).
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private const string SampleFileName = "JSR_DSC_7576.dng";
    private const string SameAsInputSample = "示例输出：DSC_7576.NEF 所在目录 \\ " + SampleFileName;

    private readonly SettingsService _service;
    private readonly IFolderPicker? _picker;

    public SettingsViewModel(SettingsService service, IFolderPicker? picker = null)
    {
        _service = service;
        _picker = picker;
        AppSettings s = service.Load();
        VramGb = s.VramGb;
        OutputSameAsInput = s.OutputMode == OutputMode.SameAsInput;
        OutputCustom = s.OutputMode == OutputMode.Custom;
        CustomOutputDir = s.CustomOutputDir ?? "";
        TempDefault = s.TempMode == TempMode.Default;
        TempCustom = s.TempMode == TempMode.Custom;
        CustomTempDir = s.CustomTempDir ?? "";
    }

    /// <summary>Raised after 保存 persisted the settings (the App closes the dialog;
    /// the shared service already made them current for the queue's next run).</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Raised by 取消; the dialog wiring closes without saving.</summary>
    public event EventHandler? CancelRequested;

    private double _vramGb = 4.0;

    /// <summary>Slider AND numeric box both bind here; the setter snaps to the 0.5
    /// grid (AwayFromZero, like BuildJson's rounding) and clamps to [1,16].</summary>
    public double VramGb
    {
        get => _vramGb;
        set
        {
            double q = Quantize(value);
            bool changed = SetProperty(ref _vramGb, q);
            if (value != q)
                // Off-grid push (e.g. 4.2 while stored 4.0): the field is unchanged so
                // SetProperty raised nothing, yet the bound NumericUpDown/Slider still
                // echoes the raw 4.2. Re-raise VramGb so the binding re-reads the
                // quantized value; the echo push it sends back is on-grid and terminal
                // (no event, no loop).
                OnPropertyChanged(nameof(VramGb));
            if (changed)
                OnPropertyChanged(nameof(VramText));
        }
    }

    /// <summary>The live value next to 显存预算（GB）— "4.0".</summary>
    public string VramText => VramGb.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>输出路径: 与输入 RAW 同目录 (bound to its radio).</summary>
    [ObservableProperty] private bool _outputSameAsInput;

    /// <summary>输出路径: 自定义 — also the TextBox row's IsEnabled and the browse gate.</summary>
    [ObservableProperty] private bool _outputCustom;

    [ObservableProperty] private string _customOutputDir = "";

    /// <summary>临时文件路径: 系统默认（%TEMP%）(bound to its radio).</summary>
    [ObservableProperty] private bool _tempDefault;

    /// <summary>临时文件路径: 自定义 — also the TextBox row's IsEnabled and the browse gate.</summary>
    [ObservableProperty] private bool _tempCustom;

    [ObservableProperty] private string _customTempDir = "";

    partial void OnOutputSameAsInputChanged(bool value)
    {
        if (value && OutputCustom)
            OutputCustom = false;
        OnPropertyChanged(nameof(SampleOutputText));
    }

    partial void OnOutputCustomChanged(bool value)
    {
        if (value && OutputSameAsInput)
            OutputSameAsInput = false;
        OnPropertyChanged(nameof(SampleOutputText));
        BrowseOutputCommand.NotifyCanExecuteChanged();
    }

    partial void OnCustomOutputDirChanged(string value) => OnPropertyChanged(nameof(SampleOutputText));

    partial void OnTempDefaultChanged(bool value)
    {
        if (value && TempCustom)
            TempCustom = false;
    }

    partial void OnTempCustomChanged(bool value)
    {
        if (value && TempDefault)
            TempDefault = false;
        BrowseTempCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Live preview line under the form: the custom dir (as typed), or the
    /// 基准帧所在目录示意 with the placeholder sample name.</summary>
    public string SampleOutputText => OutputCustom
        ? $"示例输出：{(string.IsNullOrWhiteSpace(CustomOutputDir) ? "（未设置输出目录）" : CustomOutputDir.Trim())} \\ {SampleFileName}"
        : SameAsInputSample;

    [RelayCommand(CanExecute = nameof(CanBrowseOutput))]
    private async Task BrowseOutputAsync()
    {
        if (_picker is null)
            return;
        string? dir = await _picker.PickFolderAsync("选择输出目录").ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(dir))
            CustomOutputDir = dir;
    }

    private bool CanBrowseOutput() => OutputCustom;

    [RelayCommand(CanExecute = nameof(CanBrowseTemp))]
    private async Task BrowseTempAsync()
    {
        if (_picker is null)
            return;
        string? dir = await _picker.PickFolderAsync("选择临时文件目录").ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(dir))
            CustomTempDir = dir;
    }

    private bool CanBrowseTemp() => TempCustom;

    /// <summary>保存: load-modify-save — only the dialog's own fields change — then
    /// SettingsChanged (the App closes the dialog).</summary>
    [RelayCommand]
    private void Save()
    {
        _service.Save(_service.Current with
        {
            VramGb = VramGb,
            OutputMode = OutputCustom ? OutputMode.Custom : OutputMode.SameAsInput,
            CustomOutputDir = NullIfBlank(CustomOutputDir),
            TempMode = TempCustom ? TempMode.Custom : TempMode.Default,
            CustomTempDir = NullIfBlank(CustomTempDir),
        });
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Nearest 0.5 (AwayFromZero), clamped to the slider's [1,16] range.
    /// NaN is guarded first — Math.Clamp passes NaN through (a NaN slider value
    /// would otherwise poison the field), falling back to the 4 GB plan default.</summary>
    private static double Quantize(double v) =>
        double.IsNaN(v) ? 4.0 : Math.Clamp(Math.Round(v * 2, MidpointRounding.AwayFromZero) / 2, 1.0, 16.0);

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
