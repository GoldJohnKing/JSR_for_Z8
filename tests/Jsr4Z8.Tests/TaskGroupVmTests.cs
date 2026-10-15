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


    private static TaskGroupViewModel ReadyVm() => new(
        new TaskGroup(Frames),
        new ValidationResult(true, Array.Empty<ValidationIssue>(), new PtcMatch(9512, true, "表值"),
            new Jsr4Z8.Core.Dark.DarkMatch(3200, 1005.89, 14.54, "preset")));

    private static TaskGroupViewModel BlockedVm() => new(
        new TaskGroup(Frames),
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
        Assert.Equal("待输出 JSR_DSC_7576.dng", vm.MetaText);   // Task 5: meta line is the output name only
        Assert.DoesNotContain("PTC", vm.MetaText);
        Assert.DoesNotContain("黑场", vm.MetaText);

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
        Assert.Equal($"已输出 {output}", vm.MetaText);          // Done shows the written path
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

    /// <summary>Task 5 (卡内换基准): clicking a chip on a Ready card makes that frame
    /// the reference — chips [0]/[i] swap slots and ★ flags, Group.FramePaths is rebuilt
    /// with the new reference first (remaining frames keep their chip order), and
    /// ReferenceName/OutputName follow (JSR_&lt;基准帧名&gt;.dng).</summary>
    [Fact]
    public void SetReference_SwapsGroupOrderAndOutputName()
    {
        var vm = ReadyVm();
        Assert.True(vm.SetReferenceCommand.CanExecute(null));   // Ready cards allow the swap
        vm.SetReferenceCommand.Execute(vm.FrameChips[3]);       // 4th chip: DSC_7579

        Assert.Equal(Frames[3], vm.Group.FramePaths[0]);        // clicked frame is the new reference
        Assert.Equal("DSC_7579.NEF", vm.ReferenceName);
        Assert.Equal("JSR_DSC_7579.dng", vm.OutputName);
        Assert.Equal("待输出 JSR_DSC_7579.dng", vm.MetaText);
        Assert.True(vm.FrameChips[0].IsReference);
        Assert.Equal("DSC_7579.NEF", vm.FrameChips[0].FileName);
        Assert.False(vm.FrameChips[3].IsReference);             // old reference demoted in place
        Assert.Equal("DSC_7576.NEF", vm.FrameChips[3].FileName);
        // Swap, not rotate: only slots 0 and 3 exchange; the rest keep their order.
        Assert.Equal(
            new[] { Frames[3], Frames[1], Frames[2], Frames[0], Frames[4], Frames[5], Frames[6] },
            vm.Group.FramePaths);
    }

    /// <summary>Review Focus 5: the swap is a Ready/Blocked-only interaction —
    /// Running/Done/Failed cards disable it (a Done output was produced from the old
    /// reference; a Running group is in flight).</summary>
    [Fact]
    public void SetReference_DisabledWhenRunningOrDone()
    {
        Assert.True(To(TaskState.Blocked).SetReferenceCommand.CanExecute(null));  // Blocked keeps the swap
        foreach (TaskState state in new[] { TaskState.Running, TaskState.Done, TaskState.Failed })
            Assert.False(To(state).SetReferenceCommand.CanExecute(null));
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

    /// <summary>Post-release fix 10 (Bug B: 重试不清除失败提示): a Failed card's
    /// ErrorText drives the always-visible error bar in TaskCardView, so a retry run
    /// must clear it the moment the card re-enters Running (the old failure must not
    /// persist through — or past — the new run), and Done must clear it too (a success
    /// after a failure must never show a stale error). NullTaskRunner completes without
    /// touching the card, so the card honestly stays Running after Start/Retry.</summary>
    [Fact]
    public async Task Retry_ClearsStaleErrorText_RunningThenDone()
    {
        var vm = ReadyVm();
        Assert.True(vm.GoRunning());
        Assert.True(vm.GoFailed("后端未返回结果"));
        Assert.Equal("后端未返回结果", vm.ErrorText);

        // Retry (Failed → Running): the error bar is gone immediately.
        await vm.RetryCommand.ExecuteAsync(null);
        Assert.Equal(TaskState.Running, vm.State);
        Assert.Equal(string.Empty, vm.ErrorText);

        // And completing that retry leaves no stale error either.
        Assert.True(vm.GoDone(@"D:\Photos\JSR_DSC_7576.dng"));
        Assert.Equal(TaskState.Done, vm.State);
        Assert.Equal(string.Empty, vm.ErrorText);

        // Start from Ready behaves the same after an immediate run error (Ready → Failed).
        var fresh = ReadyVm();
        Assert.True(fresh.GoFailed("立即失败"));
        await fresh.StartAsync();
        Assert.Equal(TaskState.Running, fresh.State);
        Assert.Equal(string.Empty, fresh.ErrorText);
    }

    /// <summary>Same bar, other exits out of Failed: GoReady (requeue) and GoBlocked
    /// (revalidation) must clear ErrorText — pinned here so the fix cannot regress
    /// behind the GoRunning/GoDone change.</summary>
    [Fact]
    public void GoReady_And_GoBlocked_ClearErrorText()
    {
        var vm = ReadyVm();
        Assert.True(vm.GoRunning());
        Assert.True(vm.GoFailed("boom"));
        Assert.Equal("boom", vm.ErrorText);

        Assert.True(vm.GoReady());
        Assert.Equal(string.Empty, vm.ErrorText);

        Assert.True(vm.GoRunning());
        Assert.True(vm.GoFailed("boom"));
        Assert.True(vm.GoBlocked("帧数不符"));
        Assert.Equal(string.Empty, vm.ErrorText);
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
