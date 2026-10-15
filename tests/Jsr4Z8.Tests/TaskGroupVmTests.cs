using Jsr4Z8.Core;
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

    /// <summary>Mock Z8 metadata (same shape as TaskGroupCreatorTests): ISO3200, 14bit
    /// lossless, 1/8000s, f/4.</summary>
    private static RawMetadata Meta(
        double iso = 3200, double exposure = 0.000125, double aperture = 4.0,
        string dateTime = "") => new()
    {
        Make = "NIKON CORPORATION",
        Model = "NIKON Z 8",
        Iso = iso,
        BlackLevel = 1008,
        WhiteLevel = 16383,
        BitsPerSample = 14,
        Compression = 34713,
        NefCompression = 3,
        Width = 8280,
        Height = 5520,
        CamMul = new double[] { 1.785, 1, 1.793, 1 },
        CfaPattern = new byte[] { 0, 1, 3, 2 },
        ExposureTime = exposure,
        Aperture = aperture,
        DateTimeOriginal = dateTime,
    };

    /// <summary>Mock reader for the VM's <c>read</c> seam: per-file overrides win,
    /// everything else reads as Meta().</summary>
    private static Func<string, RawMetadata> Reader(
        Dictionary<string, RawMetadata>? perFile = null) => path =>
        perFile is not null && perFile.TryGetValue(path, out RawMetadata? m) ? m : Meta();

    private static readonly ValidationResult ValidResult = new(
        true, Array.Empty<ValidationIssue>(), new PtcMatch(9512, true, "表值"),
        new Jsr4Z8.Core.Dark.DarkMatch(3200, 1005.89, 14.54, "preset"));

    private static TaskGroupViewModel ReadyVm(
        Func<string, RawMetadata>? read = null,
        IReadOnlyDictionary<string, RawMetadata>? metadata = null) => new(
        new TaskGroup(Frames), ValidResult, null, metadata, read);

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

    /// <summary>Task 3 (星标基准, no reorder): starring a chip only moves the ★ — chips
    /// stay in natural filename order — while <see cref="TaskGroupViewModel.Group"/>
    /// is rebuilt with the starred frame first (the remaining frames keep display
    /// order) and ReferenceName/OutputName follow. Starring does NOT revalidate: the
    /// state and issues stay as they were (PTC/dark depend on ISO+compression, which
    /// are group-uniform when runnable).</summary>
    [Fact]
    public void Star_NoReorder_GroupContractKept()
    {
        var vm = ReadyVm(read: Reader(),
            metadata: Frames.ToDictionary(f => f, _ => Meta(), StringComparer.OrdinalIgnoreCase));
        string[] before = vm.FrameChips.Select(c => c.FileName).ToArray();
        Assert.Equal("DSC_7576.NEF", vm.ReferenceName);       // equal exposures → natural first

        vm.SetReferenceCommand.Execute(vm.FrameChips[3]);      // ★ 4th chip: DSC_7579

        Assert.Equal(before, vm.FrameChips.Select(c => c.FileName).ToArray()); // 星标不重排 chips
        Assert.True(vm.FrameChips[3].IsReference);
        Assert.False(vm.FrameChips[0].IsReference);
        Assert.Equal(Frames[3], vm.Group.FramePaths[0]);        // Group=[基准,其余显示序]
        Assert.Equal(
            new[] { Frames[3], Frames[0], Frames[1], Frames[2], Frames[4], Frames[5], Frames[6] },
            vm.Group.FramePaths);
        Assert.Equal("DSC_7579.NEF", vm.ReferenceName);
        Assert.Equal("JSR_DSC_7579.dng", vm.OutputName);
        Assert.Equal(TaskState.Ready, vm.State);                // no revalidation on star
        Assert.Equal(string.Empty, vm.IssuesText);
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

    // ---- Task 3: 渐进补齐/移除/星标基准/悬浮参数 ----------------------------------

    /// <summary>Progressive top-up: a 3-frame card is Blocked on 帧数不符; adding the
    /// missing 4 revalidates through the read seam → Ready with refreshed Ptc/Dark
    /// (the runner reads those when it starts) and cleared issues.</summary>
    [Fact]
    public async Task AddBelowSeven_BlockedThenTopUp_Ready()
    {
        string[] first3 = Frames.Take(3).ToArray();
        var vm = new TaskGroupViewModel(new TaskGroup(first3),
            new ValidationResult(false,
                new[] { new ValidationIssue("", IssueKind.FrameCount,
                    $"帧数不符：任务组需要 7 帧，实际 {first3.Length} 帧") },
                null),
            read: Reader());
        Assert.Equal(TaskState.Blocked, vm.State);
        Assert.Contains("帧数不符", vm.IssuesText);
        Assert.Null(vm.Ptc);

        await vm.AddFilesAsync(Frames.Skip(3).ToArray());

        Assert.Equal(TaskState.Ready, vm.State);
        Assert.Equal(Frames, vm.FrameChips.Select(c => c.Path).ToArray());   // 自然序 chips
        Assert.NotNull(vm.Ptc);
        Assert.NotNull(vm.Dark);
        Assert.Equal(string.Empty, vm.IssuesText);
    }

    /// <summary>Over-shooting blocks again: a Ready 7-frame card that gains an 8th
    /// flips to Blocked with 帧数不符 (实际 8 帧) and null Ptc/Dark — the runner must
    /// never start with a stale match captured for the old set.</summary>
    [Fact]
    public async Task AddBeyondSeven_BlockedWithFrameCount()
    {
        var vm = ReadyVm(read: Reader());
        Assert.Equal(TaskState.Ready, vm.State);

        await vm.AddFilesAsync(new[] { @"C:\Photos\DSC_7583.NEF" });

        Assert.Equal(TaskState.Blocked, vm.State);
        Assert.Equal(8, vm.FrameChips.Count);
        Assert.Contains("帧数不符：任务组需要 7 帧，实际 8 帧", vm.IssuesText);
        Assert.Null(vm.Ptc);
        Assert.Null(vm.Dark);
    }

    /// <summary>Controller conflict scan 4: case-variant and exact duplicate paths —
    /// plus non-NEF strays — never create duplicate frames (OrdinalIgnoreCase dedupe
    /// vs the existing set; a fully filtered payload is a no-op that keeps the card
    /// Ready without even touching the reader).</summary>
    [Fact]
    public async Task AddDuplicatePath_NoDupFrame()
    {
        var vm = ReadyVm(read: Reader());
        Assert.Equal(TaskState.Ready, vm.State);

        await vm.AddFilesAsync(new[] { Frames[0].ToLowerInvariant(), Frames[2], @"C:\Photos\notes.txt" });

        Assert.Equal(7, vm.FrameChips.Count);
        Assert.Equal(TaskState.Ready, vm.State);
        Assert.Equal(Frames[0], vm.Group.ReferencePath);
    }

    /// <summary>Manual-star fallback (user ruling): X-ing the starred frame hands the
    /// reference back to the automatic exposure-middle rule (here equal exposures →
    /// natural first), no dangling manual reference.</summary>
    [Fact]
    public void RemoveReference_ManualStar_FallsBackToAuto()
    {
        var vm = ReadyVm(read: Reader());
        vm.SetReferenceCommand.Execute(vm.FrameChips[4]);           // ★ 第 5 帧
        Assert.Equal(Frames[4], vm.Group.ReferencePath);

        vm.RemoveFile(vm.FrameChips[4]);                            // X 掉星标帧

        Assert.Equal(6, vm.FrameChips.Count);
        Assert.DoesNotContain(Frames[4], vm.Group.FramePaths);
        Assert.Equal(Frames[0], vm.Group.ReferencePath);            // 自动规则接管
        Assert.True(vm.FrameChips[0].IsReference);
        Assert.Equal(TaskState.Blocked, vm.State);                  // 6 ≠ 7
        Assert.Contains("帧数不符", vm.IssuesText);
    }

    /// <summary>Removing the last frame removes the card (RemoveRequested — the owning
    /// MainViewModel takes it out of Tasks) instead of leaving an empty group behind.</summary>
    [Fact]
    public void RemoveLastFile_RemovesCard()
    {
        string one = @"C:\Photos\DSC_7576.NEF";
        var vm = new TaskGroupViewModel(new TaskGroup(new[] { one }),
            new ValidationResult(false,
                new[] { new ValidationIssue("", IssueKind.FrameCount,
                    "帧数不符：任务组需要 7 帧，实际 1 帧") },
                null),
            read: Reader());
        bool raised = false;
        vm.RemoveRequested += (_, _) => raised = true;

        vm.RemoveFile(vm.FrameChips[0]);

        Assert.True(raised);
    }

    /// <summary>Controller conflict scan 1: Running/Done/Failed cards are locked —
    /// AddFilesAsync/RemoveFile/SetReference are all no-ops there (the frame set,
    /// reference and state survive untouched).</summary>
    [Fact]
    public async Task LockedStates_RejectEdits()
    {
        foreach (TaskState state in new[] { TaskState.Running, TaskState.Done, TaskState.Failed })
        {
            TaskGroupViewModel vm = To(state);
            Assert.False(vm.IsEditable);
            string reference = vm.Group.ReferencePath;

            await vm.AddFilesAsync(new[] { @"C:\Photos\DSC_7583.NEF" });
            vm.RemoveFile(vm.FrameChips[0]);
            vm.SetReferenceCommand.Execute(vm.FrameChips[3]);

            Assert.Equal(state, vm.State);
            Assert.Equal(7, vm.FrameChips.Count);
            Assert.Equal(reference, vm.Group.ReferencePath);
            Assert.True(vm.FrameChips[0].IsReference);
        }
    }

    /// <summary>Hover info per chip: one line per capture parameter (机型/快门/光圈/
    /// ISO/位深/压缩/画幅/拍摄时间), camera-style shutter (1/8000s) and f-number (f/4)
    /// formatting, — for missing values (快门/光圈/拍摄时间) and 无法读取 for a file
    /// whose metadata never made it into the cache.</summary>
    [Fact]
    public void InfoText_ContainsShutterIsoModel()
    {
        IReadOnlyDictionary<string, RawMetadata> metadata = Frames.Take(6).ToDictionary(
            f => f,
            f => f == Frames[5] ? Meta(exposure: 0, aperture: 0) : Meta(dateTime: "2024:05:01 10:00:00"),
            StringComparer.OrdinalIgnoreCase);                       // Frames[6] 无元数据 → 无法读取
        var vm = ReadyVm(read: Reader(), metadata: metadata);

        string info = vm.FrameChips[0].InfoText;
        Assert.Contains("机型 NIKON CORPORATION NIKON Z 8", info);
        Assert.Contains("快门 1/8000s", info);
        Assert.Contains("光圈 f/4", info);
        Assert.Contains("ISO 3200", info);
        Assert.Contains("位深 14bit", info);
        Assert.Contains("压缩 无损", info);
        Assert.Contains("画幅 8280×5520", info);
        Assert.Contains("拍摄时间 2024:05:01 10:00:00", info);

        string missing = vm.FrameChips[5].InfoText;
        Assert.Contains("快门 —", missing);
        Assert.Contains("光圈 —", missing);
        Assert.Contains("拍摄时间 —", missing);

        Assert.Equal("无法读取", vm.FrameChips[6].InfoText);
    }

    /// <summary>Automatic reference recomputes on add: a 3-frame bracket's middle
    /// exposure (Frames[1]) yields to the 7-frame set's middle exposure (Frames[3])
    /// once the remaining 4 arrive — the same exposure-middle rule the creator used.</summary>
    [Fact]
    public async Task AutoReference_RecomputedOnAdd()
    {
        double[] exposures =
        {
            1.0 / 8000, 1.0 / 4000, 1.0 / 2000, 1.0 / 1000, 1.0 / 500, 1.0 / 250, 1.0 / 125,
        };
        Dictionary<string, RawMetadata> perFile = Frames.Zip(exposures).ToDictionary(
            p => p.First, p => Meta(exposure: p.Second), StringComparer.OrdinalIgnoreCase);
        Func<string, RawMetadata> read = path =>
            perFile.TryGetValue(path, out RawMetadata? m) ? m : Meta();

        CreatedTaskGroup? created = await TaskGroupCreator.CreateAsync(
            new[] { Frames[0], Frames[1], Frames[6] }, read);
        Assert.NotNull(created);
        var vm = new TaskGroupViewModel(created!.Group, created.Validation,
            metadata: created.Metadata, read: read);
        Assert.Equal(Frames[1], vm.Group.ReferencePath);           // 3 帧包围的中间曝光

        await vm.AddFilesAsync(new[] { Frames[2], Frames[3], Frames[4], Frames[5] });

        Assert.Equal(Frames[3], vm.Group.ReferencePath);           // 7 帧集合的中间曝光（1/1000）
        Assert.Equal(TaskState.Ready, vm.State);
        Assert.True(vm.FrameChips[3].IsReference);
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
