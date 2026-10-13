using System.Text.Json;
using Jsr4Z8.Core;
using Jsr4Z8.Core.Queue;
using Jsr4Z8.Core.Settings;
using Jsr4Z8.UI.Services;
using Jsr4Z8.UI.ViewModels;
using Xunit;

namespace Jsr4Z8.Tests.Settings;

/// <summary>
/// Task 14: settings persistence + the ⚙高级设置 dialog. SettingsServiceTests cover the
/// JSON file contract under %LOCALAPPDATA% (injectable base dir): defaults on missing
/// file, full round-trip, corrupt → defaults rewritten, VramGb clamped [1,16] on load
/// AND save, atomic save (tmp+rename, no leftovers), and the ISettingsProvider adapter
/// (CurrentSettings is the shared service's in-memory current, set on Save).
/// SettingsViewModelTests drive the dialog VM headless (docs/design/settings.html option
/// B): 0.5-grid slider/numeric sync + live value, exclusive radio pairs, live 示例输出
/// for both output modes, the IFolderPicker browse seam, Save (persist +
/// SettingsChanged, LastDarkPath preserved via load-modify-save) and Cancel (discard).
/// SuccessGlobTests pin the Task 13 carried fix: the success-path product glob skips
/// *.partial segments. Windows-host tests.
/// </summary>
public class SettingsServiceTests
{
    private static string NewBaseDir() =>
        Path.Combine(Path.GetTempPath(), "jsr4z8_set_" + Guid.NewGuid().ToString("N"));

    private static string SettingsFile(string baseDir) =>
        Path.Combine(baseDir, SettingsService.AppDirName, SettingsService.FileName);

    private static void Cleanup(string baseDir)
    {
        try { Directory.Delete(baseDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults_NoFileWritten()
    {
        string dir = NewBaseDir();
        try
        {
            var svc = new SettingsService(dir);

            Assert.Equal(new AppSettings(), svc.Load());
            Assert.Equal(new AppSettings(), svc.Current);
            Assert.False(File.Exists(SettingsFile(dir)), "missing file must not be written on load");
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        string dir = NewBaseDir();
        try
        {
            var svc = new SettingsService(dir);
            var saved = new AppSettings(
                6.5, OutputMode.Custom, @"D:\Photos\JSR 输出",
                TempMode.Custom, @"E:\自定义临时", @"C:\Dark\DSC_7575.NEF");
            svc.Save(saved);

            Assert.Equal(saved, svc.Load());                        // same instance re-reads disk
            Assert.Equal(saved, new SettingsService(dir).Load());   // fresh instance reads disk
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void CorruptJson_LoadReturnsDefaults_AndRewritesFile()
    {
        string dir = NewBaseDir();
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile(dir))!);
        File.WriteAllText(SettingsFile(dir), "{ 这不是 JSON");
        try
        {
            var svc = new SettingsService(dir);

            Assert.Equal(new AppSettings(), svc.Load());                       // defaults, not a crash
            Assert.Equal(new AppSettings(), new SettingsService(dir).Load());  // file rewritten clean
            Assert.DoesNotContain("这不是", File.ReadAllText(SettingsFile(dir)));
        }
        finally { Cleanup(dir); }
    }

    [Theory]
    [InlineData(99, 16)]
    [InlineData(0, 1)]
    public void Load_ClampsVramGb(double inFile, double expected)
    {
        string dir = NewBaseDir();
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile(dir))!);
        File.WriteAllText(SettingsFile(dir), JsonSerializer.Serialize(new { VramGb = inFile }));
        try
        {
            Assert.Equal(expected, new SettingsService(dir).Load().VramGb);
        }
        finally { Cleanup(dir); }
    }

    [Theory]
    [InlineData(99, 16)]
    [InlineData(0, 1)]
    public void Save_ClampsVramGb_ReturnsClamped(double saved, double expected)
    {
        string dir = NewBaseDir();
        try
        {
            var svc = new SettingsService(dir);

            Assert.Equal(expected, svc.Save(new AppSettings(saved)).VramGb);
            Assert.Equal(expected, svc.Load().VramGb);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Save_IsAtomic_NoTmpFileLeftBehind()
    {
        string dir = NewBaseDir();
        try
        {
            var svc = new SettingsService(dir);
            svc.Save(new AppSettings(8.0));

            Assert.True(File.Exists(SettingsFile(dir)));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(SettingsFile(dir))!, "*.tmp"));
            Assert.Equal(8.0, svc.Load().VramGb);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Provider_CurrentSettings_ReflectsSaveThroughSharedService()
    {
        string dir = NewBaseDir();
        try
        {
            var svc = new SettingsService(dir);
            ISettingsProvider provider = new SettingsServiceProvider(svc);

            Assert.Equal(4.0, provider.CurrentSettings.VramGb);    // ctor load → plan default
            svc.Save(new AppSettings(12.0));
            Assert.Equal(12.0, provider.CurrentSettings.VramGb);   // in-memory set on Save
            svc.Save(new AppSettings(2.5));
            Assert.Equal(2.5, provider.CurrentSettings.VramGb);
        }
        finally { Cleanup(dir); }
    }
}

public class SettingsViewModelTests
{
    /// <summary>PickFolderAsync returns an already-completed task, so the VM's
    /// await continues inline and Execute(null) is synchronous for assertions.</summary>
    private sealed class FakePicker : IFolderPicker
    {
        public string? Result { get; set; }

        public Task<string?> PickFolderAsync(string title) => Task.FromResult(Result);
    }

    private static (SettingsViewModel Vm, SettingsService Svc, string Dir) MakeVm(
        AppSettings initial, IFolderPicker? picker = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "jsr4z8_svm_" + Guid.NewGuid().ToString("N"));
        var svc = new SettingsService(dir);
        svc.Save(initial);
        return (new SettingsViewModel(svc, picker), svc, dir);
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Vram_05Grid_Clamps_LiveTextFollows()
    {
        var (vm, _, dir) = MakeVm(new AppSettings(4.0));
        try
        {
            Assert.Equal(4.0, vm.VramGb);                          // ctor loads from disk
            Assert.Equal("4.0", vm.VramText);

            vm.VramGb = 4.3;                                       // slider/numeric grid: nearest 0.5
            Assert.Equal(4.5, vm.VramGb);
            Assert.Equal("4.5", vm.VramText);
            vm.VramGb = 0;                                         // clamp [1,16]
            Assert.Equal(1.0, vm.VramGb);
            vm.VramGb = 20;
            Assert.Equal(16.0, vm.VramGb);
            Assert.Equal("16.0", vm.VramText);
            vm.VramGb = 4;
            Assert.Equal("4.0", vm.VramText);
            vm.VramGb = double.NaN;                                  // Math.Clamp passes NaN through — guarded
            Assert.Equal(4.0, vm.VramGb);
            Assert.Equal("4.0", vm.VramText);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>Task 14 review fix: an off-grid push whose quantized value EQUALS the
    /// stored one (4.2 → 4.0 while already 4.0) must still raise PropertyChanged for
    /// VramGb — otherwise the bound NumericUpDown/Slider keeps echoing the raw 4.2
    /// while VramText and Save use 4.0. The re-raise pushes the quantized value back;
    /// the echo's second push is on-grid and raises nothing (converges, no loop).</summary>
    [Fact]
    public void Vram_OffGridInputEqualStoredValue_ReRaisesVramGb_DisplayConverges()
    {
        var (vm, _, dir) = MakeVm(new AppSettings(4.0));
        try
        {
            int raised = 0;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SettingsViewModel.VramGb)) raised++; };

            vm.VramGb = 4.2;                                        // the raw push a bound control makes
            Assert.Equal(4.0, vm.VramGb);                           // read back: quantized, not 4.2
            Assert.Equal(1, raised);                                // …and the control was told to re-read it

            vm.VramGb = vm.VramGb;                                  // the binding's echo push: on-grid, terminal
            Assert.Equal(1, raised);                                // no further event — no ping-pong loop
            Assert.Equal("4.0", vm.VramText);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void OutputRadio_Exclusive_TextBoxFlag_SampleTextLive()
    {
        var (vm, _, dir) = MakeVm(new AppSettings(4.0, OutputMode.Custom, @"D:\Photos\JSR 输出"));
        try
        {
            Assert.True(vm.OutputCustom);
            Assert.False(vm.OutputSameAsInput);
            Assert.Equal(@"示例输出：D:\Photos\JSR 输出 \ JSR_DSC_7576.dng", vm.SampleOutputText);

            vm.CustomOutputDir = @"E:\Out2";                       // 示例输出 follows typing live
            Assert.Equal(@"示例输出：E:\Out2 \ JSR_DSC_7576.dng", vm.SampleOutputText);

            vm.OutputSameAsInput = true;                           // radio pair is exclusive
            Assert.False(vm.OutputCustom);
            Assert.Equal(@"示例输出：DSC_7576.NEF 所在目录 \ JSR_DSC_7576.dng", vm.SampleOutputText);

            vm.OutputCustom = true;                                // and back
            Assert.False(vm.OutputSameAsInput);
            Assert.Equal(@"示例输出：E:\Out2 \ JSR_DSC_7576.dng", vm.SampleOutputText);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void TempRadio_Exclusive()
    {
        var (vm, _, dir) = MakeVm(new AppSettings());
        try
        {
            Assert.True(vm.TempDefault);
            Assert.False(vm.TempCustom);
            vm.TempCustom = true;
            Assert.False(vm.TempDefault);
            vm.TempDefault = true;
            Assert.False(vm.TempCustom);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Browse_PickerSeam_SetsCustomDir_CommandsGatedOnCustom()
    {
        var picker = new FakePicker { Result = @"D:\Picked" };
        var (vm, _, dir) = MakeVm(new AppSettings(), picker);
        try
        {
            Assert.False(vm.BrowseOutputCommand.CanExecute(null)); // same-as-input mode: no browse
            vm.OutputCustom = true;
            Assert.True(vm.BrowseOutputCommand.CanExecute(null));
            vm.BrowseOutputCommand.Execute(null);
            Assert.Equal(@"D:\Picked", vm.CustomOutputDir);

            Assert.False(vm.BrowseTempCommand.CanExecute(null));   // system-temp mode
            vm.TempCustom = true;
            picker.Result = @"E:\PTmp";
            vm.BrowseTempCommand.Execute(null);
            Assert.Equal(@"E:\PTmp", vm.CustomTempDir);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Save_PersistsAllDialogFields_RaisesSettingsChanged_PreservesLastDarkPath()
    {
        var (vm, svc, dir) = MakeVm(new AppSettings(
            4.0, OutputMode.SameAsInput, null, TempMode.Default, null, @"C:\Dark\DSC_7575.NEF"));
        try
        {
            vm.VramGb = 8.5;
            vm.OutputCustom = true;
            vm.CustomOutputDir = @"D:\O";
            vm.TempCustom = true;
            vm.CustomTempDir = @"E:\T";
            int raised = 0;
            vm.SettingsChanged += (_, _) => raised++;

            vm.SaveCommand.Execute(null);

            Assert.Equal(1, raised);
            AppSettings loaded = svc.Load();
            Assert.Equal(8.5, loaded.VramGb);
            Assert.Equal(OutputMode.Custom, loaded.OutputMode);
            Assert.Equal(@"D:\O", loaded.CustomOutputDir);
            Assert.Equal(TempMode.Custom, loaded.TempMode);
            Assert.Equal(@"E:\T", loaded.CustomTempDir);
            Assert.Equal(@"C:\Dark\DSC_7575.NEF", loaded.LastDarkPath);  // load-modify-save: not clobbered
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Cancel_Discards_NoPersistNoSettingsChanged()
    {
        var (vm, svc, dir) = MakeVm(new AppSettings(2.0));
        try
        {
            vm.VramGb = 16;
            vm.OutputCustom = true;
            int cancelled = 0, changed = 0;
            vm.CancelRequested += (_, _) => cancelled++;
            vm.SettingsChanged += (_, _) => changed++;

            vm.CancelCommand.Execute(null);

            Assert.Equal(1, cancelled);
            Assert.Equal(0, changed);
            AppSettings loaded = svc.Load();
            Assert.Equal(2.0, loaded.VramGb);                       // nothing persisted
            Assert.Equal(OutputMode.SameAsInput, loaded.OutputMode);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>Task 14 dark-picker memory: SelectDark persists LastDarkPath via
    /// load-modify-save (every other field untouched) and exposes the folder as the
    /// next picker's start dir.</summary>
    [Fact]
    public void NewTask_SelectDark_PersistsLastDarkPath_WithoutClobbering()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jsr4z8_snt_" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new SettingsService(dir);
            svc.Save(new AppSettings(6.0, OutputMode.Custom, @"D:\O", TempMode.Custom, @"E:\T"));
            var vm = new NewTaskViewModel(read: _ => throw new RawReadException("mock"), settings: svc);

            vm.SelectDark(@"C:\Dark2\DSC_7000.NEF");

            AppSettings loaded = svc.Load();
            Assert.Equal(@"C:\Dark2\DSC_7000.NEF", loaded.LastDarkPath);
            Assert.Equal(6.0, loaded.VramGb);                       // untouched
            Assert.Equal(OutputMode.Custom, loaded.OutputMode);
            Assert.Equal(@"D:\O", loaded.CustomOutputDir);
            Assert.Equal(TempMode.Custom, loaded.TempMode);
            Assert.Equal(@"E:\T", loaded.CustomTempDir);
            Assert.Equal(@"C:\Dark2", vm.LastDarkDir);              // next picker's start folder

            // A fresh VM (next app session) sees the persisted dir too.
            Assert.Equal(@"C:\Dark2", new NewTaskViewModel(settings: svc).LastDarkDir);
        }
        finally { Cleanup(dir); }
    }
}

/// <summary>Task 13 carried fix (done in Task 14): the success-path product glob must
/// skip any path that traverses a *.partial segment — the backend's resume leftovers —
/// even when the file inside is NEWER than the real product.</summary>
public class SuccessGlobTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "jsr4z8_glob_" + Guid.NewGuid().ToString("N"));

    private static string Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void FindNewestProductDng_SkipsPartialSegments_EvenWhenNewer()
    {
        string root = NewRoot();
        try
        {
            string real = Write(Path.Combine(root, "job_V15_3_5_K7_1_1", "job_product.dng"));
            string partialTop = Write(Path.Combine(root, "job_dir.partial", "job_newer.dng"));
            string partialNested = Write(Path.Combine(root, "nested", "job_b.partial", "deep", "job_newest.dng"));
            DateTime now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(real, now.AddMinutes(-1));          // real product is the OLDEST
            File.SetLastWriteTimeUtc(partialTop, now);
            File.SetLastWriteTimeUtc(partialNested, now.AddSeconds(30));

            Assert.Equal(real, TaskQueueRunner.FindNewestProductDng(root, "job"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FindNewestProductDng_StillPicksNewestProduct()
    {
        string root = NewRoot();
        try
        {
            string older = Write(Path.Combine(root, "job_V15_3_5_K7_1_1", "job_old.dng"));
            string newer = Write(Path.Combine(root, "job_V15_3_5_K7_9_9", "job_new.dng"));
            DateTime now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(older, now.AddMinutes(-5));
            File.SetLastWriteTimeUtc(newer, now);

            Assert.Equal(newer, TaskQueueRunner.FindNewestProductDng(root, "job"));
        }
        finally { Cleanup(root); }
    }
}
