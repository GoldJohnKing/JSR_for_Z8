using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Ptc;
using Jsr4Z8.UI.Services;
using Jsr4Z8.UI.ViewModels;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Task 11: headless ViewModel tests for the card-flow main window
/// (MainViewModel + TaskGroupViewModel state machine). The VMs carry pure state
/// only — no Avalonia dispatcher dependency — so they are testable in xUnit
/// without a UI thread. ThemeService lives here too (same brief file list):
/// its Apply() must be a no-op when no Avalonia Application exists.
/// </summary>
public class TaskGroupVmTests
{
    private static readonly string[] Frames =
        Enumerable.Range(7576, 7).Select(i => $@"C:\Photos\DSC_{i}.NEF").ToArray();

    private const string DarkPath = @"C:\Photos\DSC_7575.NEF";

    private static TaskGroupViewModel ReadyVm() => new(
        new TaskGroup(Frames, DarkPath),
        new ValidationResult(true, Array.Empty<ValidationIssue>(), new PtcMatch(9512, true, "表值")));

    private static TaskGroupViewModel BlockedVm() => new(
        new TaskGroup(Frames, DarkPath),
        new ValidationResult(false,
            new[] { new ValidationIssue("", IssueKind.FrameCount, "帧数不符：任务组需要 7 帧，实际 7 帧") },
            null));

    /// <summary>Drives a fresh Ready VM into the requested state through valid edges only.</summary>
    private static TaskGroupViewModel To(TaskState state)
    {
        var vm = ReadyVm();
        switch (state)
        {
            case TaskState.Running: vm.GoRunning(); break;
            case TaskState.Done: vm.GoRunning(); vm.GoDone(null); break;
            case TaskState.Failed: vm.GoRunning(); vm.GoFailed("boom"); break;
            case TaskState.Blocked: vm.GoBlocked("帧数不符"); break;
        }
        return vm;
    }

    [Fact]
    public void StartAll_CanExecute_False_WhenNoTasks()
    {
        var main = new MainViewModel();
        Assert.Empty(main.Tasks);
        Assert.True(main.HasNoTasks);
        Assert.False(main.StartAllCommand.CanExecute(null));
    }

    [Fact]
    public void StartAll_CanExecute_True_AfterReadyTaskAdded()
    {
        var main = new MainViewModel();
        main.Add(ReadyVm());
        Assert.Single(main.Tasks);
        Assert.False(main.HasNoTasks);
        Assert.True(main.StartAllCommand.CanExecute(null));
    }

    [Fact]
    public void Lifecycle_ReadyToRunningToDone_SetsProgressAndCommands()
    {
        var vm = ReadyVm();
        Assert.Equal(TaskState.Ready, vm.State);
        Assert.Equal("就绪", vm.StateText);

        Assert.True(vm.GoRunning());
        Assert.Equal(TaskState.Running, vm.State);
        Assert.Equal(0, vm.Progress);
        vm.ReportProgress(42, "对齐 tile 3/8");
        Assert.Equal(42, vm.Progress);
        Assert.Equal("对齐 tile 3/8", vm.StageText);
        Assert.Equal("对齐 tile 3/8", vm.StateText);

        const string output = @"D:\Photos\JSR_DSC_7576.dng";
        Assert.True(vm.GoDone(output));
        Assert.Equal(TaskState.Done, vm.State);
        Assert.Equal(100, vm.Progress);
        Assert.Equal("已完成", vm.StateText);
        Assert.Equal(output, vm.OutputPath);
        Assert.True(vm.OpenFolderCommand.CanExecute(null));
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    /// <summary>Documented contract: invalid transitions are IGNORED (no throw) and
    /// return false — Task 13 pushes backend events from background threads, where a
    /// late event (e.g. progress after a user stop) must never crash the app. A
    /// transition returns true iff the state actually changed (same-state is a no-op).</summary>
    [Fact]
    public void InvalidTransitions_AreIgnored_AndReturnFalse()
    {
        var vm = ReadyVm();
        Assert.True(vm.GoRunning());
        Assert.True(vm.GoDone(null));

        Assert.False(vm.GoRunning());          // Done → Running is invalid (retry goes via GoReady)
        Assert.Equal(TaskState.Done, vm.State);
        Assert.False(vm.GoDone(null));         // same-state no-op
        Assert.False(vm.GoBlocked("x"));       // Done → Blocked invalid
        Assert.Equal(TaskState.Done, vm.State);

        var fresh = ReadyVm();
        Assert.False(fresh.GoDone(null));      // Ready → Done invalid (never ran)
        Assert.True(fresh.GoFailed("x"));     // Ready → Failed is valid (immediate run error)
        Assert.Equal(TaskState.Failed, fresh.State);
        Assert.True(fresh.GoRunning());        // Failed → Running is the retry edge
    }

    [Fact]
    public void LogLinesAppend_And_ToggleLogFlipsExpanded()
    {
        var vm = ReadyVm();
        Assert.Empty(vm.LogLines);
        Assert.False(vm.IsLogExpanded);

        vm.LogLines.Add("[19:22:31] 黑场 DSC_7575.NEF 已加载（ISO400/1/8000s）");
        vm.ToggleLogCommand.Execute(null);
        Assert.True(vm.IsLogExpanded);
        vm.ToggleLogCommand.Execute(null);
        Assert.False(vm.IsLogExpanded);
        Assert.Single(vm.LogLines);
    }

    [Fact]
    public void StartAll_CanExecute_False_WhenOnlyBlockedAndDoneRemain()
    {
        var main = new MainViewModel();
        main.Add(BlockedVm());
        var done = ReadyVm();
        Assert.True(done.GoRunning());
        Assert.True(done.GoDone(null));
        main.Add(done);
        Assert.All(main.Tasks, t => Assert.NotEqual(TaskState.Ready, t.State));
        Assert.False(main.StartAllCommand.CanExecute(null));
    }

    /// <summary>Per docs/design/card-states.html: Ready→Start+Remove; Running→Stop only;
    /// Done→OpenFolder+Remove; Failed→Retry+Remove; Blocked→Remove only.</summary>
    [Fact]
    public void CommandAvailability_PerState()
    {
        var ready = To(TaskState.Ready);
        Assert.True(ready.StartCommand.CanExecute(null));
        Assert.True(ready.RemoveCommand.CanExecute(null));   // mockup ① shows ✕移除 on Ready
        Assert.False(ready.StopCommand.CanExecute(null));
        Assert.False(ready.RetryCommand.CanExecute(null));
        Assert.False(ready.OpenFolderCommand.CanExecute(null));

        var running = To(TaskState.Running);
        Assert.True(running.StopCommand.CanExecute(null));
        Assert.False(running.StartCommand.CanExecute(null));
        Assert.False(running.RemoveCommand.CanExecute(null)); // mockup ② shows only ■停止
        Assert.False(running.RetryCommand.CanExecute(null));
        Assert.False(running.OpenFolderCommand.CanExecute(null));

        var done = To(TaskState.Done);
        Assert.True(done.OpenFolderCommand.CanExecute(null));
        Assert.True(done.RemoveCommand.CanExecute(null));
        Assert.False(done.StartCommand.CanExecute(null));
        Assert.False(done.StopCommand.CanExecute(null));
        Assert.False(done.RetryCommand.CanExecute(null));

        var failed = To(TaskState.Failed);
        Assert.True(failed.RetryCommand.CanExecute(null));
        Assert.True(failed.RemoveCommand.CanExecute(null));
        Assert.False(failed.StartCommand.CanExecute(null));
        Assert.False(failed.StopCommand.CanExecute(null));
        Assert.False(failed.OpenFolderCommand.CanExecute(null));

        var blocked = To(TaskState.Blocked);
        Assert.True(blocked.RemoveCommand.CanExecute(null));
        Assert.False(blocked.StartCommand.CanExecute(null));
        Assert.False(blocked.StopCommand.CanExecute(null));
        Assert.False(blocked.RetryCommand.CanExecute(null));
        Assert.False(blocked.OpenFolderCommand.CanExecute(null));
    }

    /// <summary>Task 6 carried finding: backend PROGRESS lines can exceed 100 —
    /// the VM clamps before display.</summary>
    [Fact]
    public void ReportProgress_ClampsTo0And100()
    {
        var vm = ReadyVm();
        vm.GoRunning();
        vm.ReportProgress(120, null);
        Assert.Equal(100, vm.Progress);
        vm.ReportProgress(-5, null);
        Assert.Equal(0, vm.Progress);
        vm.ReportProgress(42, "对齐 tile 3/8");
        Assert.Equal(42, vm.Progress);
        Assert.Contains("对齐", vm.StateText);
    }

    [Fact]
    public void RemoveCommand_RaisesRemoveRequested_AndMainViewModelRemoves()
    {
        var main = new MainViewModel();
        var vm = ReadyVm();
        main.Add(vm);
        vm.GoRunning();
        vm.GoFailed("显存预算不足");            // Failed → removable
        vm.RemoveCommand.Execute(null);
        Assert.Empty(main.Tasks);
        Assert.True(main.HasNoTasks);
        Assert.False(main.StartAllCommand.CanExecute(null));
    }

    [Fact]
    public void BlockedVm_ShowsJoinedIssues()
    {
        var vm = BlockedVm();
        Assert.Equal(TaskState.Blocked, vm.State);
        Assert.Equal("无法执行", vm.StateText);
        Assert.Contains("帧数不符", vm.IssuesText);
    }
}

/// <summary>ThemeService: default Dark (user choice C), cycle Dark→Light→Default→Dark.
/// Headless-safe: with no Avalonia Application running, Cycle/Apply must not throw.</summary>
public class ThemeServiceTests
{
    [Fact]
    public void Cycle_StartsDark_ThenLightThenDefaultThenDark()
    {
        var theme = new ThemeService();
        Assert.Equal(ThemeMode.Dark, theme.Mode);      // default dark
        Assert.Equal("暗色", theme.ModeText);

        theme.Cycle();
        Assert.Equal(ThemeMode.Light, theme.Mode);
        Assert.Equal("亮色", theme.ModeText);

        theme.Cycle();
        Assert.Equal(ThemeMode.Default, theme.Mode);   // follow the system
        Assert.Equal("跟随系统", theme.ModeText);

        theme.Cycle();
        Assert.Equal(ThemeMode.Dark, theme.Mode);
    }

    [Fact]
    public void Apply_IsHeadlessSafe_WithoutAvaloniaApplication()
    {
        var theme = new ThemeService();
        theme.Cycle();
        theme.Apply();                                 // Application.Current is null under xUnit
        Assert.Equal(ThemeMode.Light, theme.Mode);
    }
}
