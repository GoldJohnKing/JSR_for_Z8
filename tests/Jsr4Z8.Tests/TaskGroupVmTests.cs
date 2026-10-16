using System.Collections.Concurrent;
using Jsr4Z8.Core;

using static Jsr4Z8.Tests.MockMeta;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Ptc;
using Jsr4Z8.UI.Services;
using Jsr4Z8.UI.ViewModels;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Headless ViewModel tests for the card-flow main window
/// (MainViewModel + TaskGroupViewModel state machine). The VMs carry pure state
/// only — no Avalonia dispatcher dependency — so they are testable in xUnit
/// without a UI thread. ThemeService tests live here too: its Apply() must be
/// a no-op when no Avalonia Application exists.
/// </summary>
public class TaskGroupVmTests
{
    private static readonly string[] Frames =
        Enumerable.Range(7576, 7).Select(i => $@"C:\Photos\DSC_{i}.NEF").ToArray();

    /// <summary>A second, disjoint 7-frame set — the 快速两连拖 regression drops
    /// Frames and Frames2 as two separate cards.</summary>
    private static readonly string[] Frames2 =
        Enumerable.Range(7583, 7).Select(i => $@"C:\Photos\DSC_{i}.NEF").ToArray();

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
        Assert.Equal("待输出 JSR_DSC_7576.dng", vm.MetaText);   // meta line is the output name only
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
    /// return false — backend events arrive from background threads, where a
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

    /// <summary>快速两连拖回归：第二组在第一组校验未完成时拖入（两组同时校验），
    /// 两组落地 Ready 之后不再有任何 Tasks 集合变化 —— Avalonia Button 缓存 CanExecute
    /// 并只在命令的 CanExecuteChanged 时重查，所以卡片 State 变化必须通知
    /// StartAllCommand，否则“全部开始”按钮的缓存停留在 false，永远死灰。慢速路径
    /// （先等第一组 Ready 再拖第二组）靠第二次 Tasks.Add 的集合通知“意外”救活，
    /// 掩盖了该缺陷。</summary>
    [Fact]
    public async Task StartAll_CanExecuteNotified_WhenFastDroppedFlightsBothLandReady()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, RawMetadata> read = _ =>
        {
            gate.Task.Wait();
            return Meta();
        };
        var main = new MainViewModel(read: read);

        Task drop1 = main.AddFilesAsync(Frames);    // 卡 1 即时入列，校验飞行阻塞在 gate
        Task drop2 = main.AddFilesAsync(Frames2);   // 第一组仍在校验中 → 第二组也入列

        Assert.Equal(2, main.Tasks.Count);                              // 两卡都已建
        Assert.All(main.Tasks, t => Assert.True(t.IsValidating));       // 两组同时校验

        int raised = 0;   // Avalonia Button 重查 CanExecute 的唯一信号
        main.StartAllCommand.CanExecuteChanged += (_, _) => raised++;

        gate.SetResult(true);
        await Task.WhenAll(drop1, drop2);

        Assert.All(main.Tasks, t => Assert.Equal(TaskState.Ready, t.State)); // 两组都合法
        Assert.True(main.StartAllCommand.CanExecute(null));
        Assert.True(raised > 0);                                        // 缺陷：State 落地不通知 → 0
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

    /// <summary>Per-state command surface: Ready→Start+Remove; Running→Stop only;
    /// Done→OpenFolder+Remove; Failed→Retry+Remove; Blocked→Remove only.</summary>
    [Fact]
    public void CommandAvailability_PerState()
    {
        var ready = To(TaskState.Ready);
        Assert.True(ready.StartCommand.CanExecute(null));
        Assert.True(ready.RemoveCommand.CanExecute(null));   // ✕移除 shows on Ready
        Assert.False(ready.StopCommand.CanExecute(null));
        Assert.False(ready.RetryCommand.CanExecute(null));
        Assert.False(ready.OpenFolderCommand.CanExecute(null));

        var running = To(TaskState.Running);
        Assert.True(running.StopCommand.CanExecute(null));
        Assert.False(running.StartCommand.CanExecute(null));
        Assert.False(running.RemoveCommand.CanExecute(null)); // Running shows only ■停止
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

    /// <summary>星标基准 (no reorder): starring a chip only moves the ★ — chips
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

    /// <summary>The swap is a Ready/Blocked-only interaction —
    /// Running/Done/Failed cards disable it (a Done output was produced from the old
    /// reference; a Running group is in flight).</summary>
    [Fact]
    public void SetReference_DisabledWhenRunningOrDone()
    {
        Assert.True(To(TaskState.Blocked).SetReferenceCommand.CanExecute(null));  // Blocked keeps the swap
        foreach (TaskState state in new[] { TaskState.Running, TaskState.Done, TaskState.Failed })
            Assert.False(To(state).SetReferenceCommand.CanExecute(null));
    }

    /// <summary>Backend PROGRESS lines can exceed 100 —
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

    /// <summary>Bottom state bar: the bar is always visible — it tracks Progress
    /// only while Running and reads a full bar (the state color fills 100% width) in
    /// every other state. The XAML state→color mapping is compile-verified only.</summary>
    [Fact]
    public void BarValue_RunningTracksProgress_OtherStatesFull()
    {
        var vm = ReadyVm();
        Assert.Equal(100, vm.BarValue);                 // Ready → 满宽灰

        Assert.True(vm.GoRunning());
        Assert.Equal(0, vm.BarValue);                   // Running 从 0 起跟踪
        vm.ReportProgress(42, null);
        Assert.Equal(42, vm.BarValue);

        Assert.True(vm.GoDone(null));
        Assert.Equal(100, vm.BarValue);                 // Done → 满宽绿

        Assert.Equal(100, BlockedVm().BarValue);        // Blocked → 满宽警示色
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

    /// <summary>重试不清除失败提示: a Failed card's
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

    // ---- 渐进补齐/移除/星标基准/悬浮参数 ----------------------------------

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

    /// <summary>Case-variant and exact duplicate paths — plus non-NEF strays —
    /// never create duplicate frames (OrdinalIgnoreCase dedupe
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

    /// <summary>Manual-star fallback: X-ing the starred frame hands the
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

    /// <summary>Running/Done/Failed cards are locked —
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

        // 未读取（既无缓存也无失败记录）→ 空文本（尚未读盘，不是失败）；
        // 无法读取 只属于记录过读取失败的路径（ReadCached_FallbackReadFailure_
        // RecordsAndRethrows 钉死该分支）。
        Assert.Equal(string.Empty, vm.FrameChips[6].InfoText);
    }

    /// <summary>ReadCached's fallback real read
    /// must record its failure into _readErrors BEFORE rethrowing — so the same
    /// uncached-unreadable path is read from disk exactly once and every later
    /// ReadCached call replays the recorded exception instead of re-reading (the
    /// rethrow itself is what PickReference/Validate already tolerate: the frame is
    /// flagged 无法读取, never crashes the card).</summary>
    [Fact]
    public void ReadCached_FallbackReadFailure_RecordsAndRethrows()
    {
        string failing = Frames[6];
        int reads = 0;
        Func<string, RawMetadata> read = path =>
        {
            if (string.Equals(path, failing, StringComparison.OrdinalIgnoreCase))
            {
                reads++;
                throw new RawReadException("EXIF 解析失败");
            }
            return Meta();
        };
        // Card built Ready with 6 of 7 frames cached; the failing 7th is uncached, so
        // the next recompute falls through to the real reader for it.
        var vm = new TaskGroupViewModel(new TaskGroup(Frames), ValidResult,
            metadata: Frames.Take(6).ToDictionary(f => f, _ => Meta(), StringComparer.OrdinalIgnoreCase),
            read: read);

        vm.RemoveFile(vm.FrameChips[0]);                    // any set change forces a recompute

        Assert.Equal(1, reads);                            // 读盘恰一次，之后走 _readErrors 回放
        Assert.Equal(TaskState.Blocked, vm.State);          // rethrow → validator flags 无法读取
        Assert.Contains("无法读取", vm.IssuesText);
        Assert.Equal("无法读取", vm.FrameChips[^1].InfoText);
    }

    /// <summary>Overlapping concurrent
    /// AddFilesAsync calls each dedupe against the pre-add snapshot, so both land the
    /// same paths — the recompute's display projection must never let a duplicate
    /// frame materialize. The read seam blocks on a TaskCompletionSource so both calls
    /// are provably past their dedupe check (and mid-read) before either merges; the
    /// merges themselves run on a single-threaded pump (the UI dispatcher's
    /// serialization contract — the VM is UI-thread-affine for mutations), so the
    /// double AddRange lands back-to-back exactly as it would behind the real dispatcher.</summary>
    [Fact]
    public async Task ConcurrentAdd_SamePath_NoDuplicateFrame()
    {
        string[] first3 = Frames.Take(3).ToArray();
        string[] rest = Frames.Skip(3).ToArray();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, RawMetadata> read = _ =>
        {
            gate.Task.Wait();
            return Meta();
        };
        var vm = new TaskGroupViewModel(new TaskGroup(first3),
            new ValidationResult(false,
                new[] { new ValidationIssue("", IssueKind.FrameCount,
                    $"帧数不符：任务组需要 7 帧，实际 {first3.Length} 帧") },
                null),
            read: read);

        var pump = new SerialPump();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? fault = null;
        var uiThread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(pump);
            try
            {
                Task a = vm.AddFilesAsync(rest);                // blocks in its background read
                Task b = vm.AddFilesAsync(rest);                // dedupes vs the SAME pre-add snapshot
                gate.SetResult(true);
                pump.RunUntilComplete(a, b);                    // merges serialize, one at a time
            }
            catch (Exception ex)
            {
                fault = ex;
            }
            finally
            {
                done.SetResult(true);
            }
        });
        uiThread.Start();
        await done.Task;

        Assert.Null(fault);
        Assert.Equal(TaskState.Ready, vm.State);
        Assert.Equal(7, vm.FrameChips.Count);                   // 双份 fresh 不得产出重复帧
        Assert.Equal(7, vm.Group.FramePaths.Count);
        Assert.Equal(Frames, vm.FrameChips.Select(c => c.Path).ToArray());
        Assert.Equal(Frames, vm.Group.FramePaths);
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

    // ---- ±EV 偏差胶囊 -------------------------------------------------------

    /// <summary>Bracketed card: every chip's EvText is the EV deviation relative to the
    /// FIXED middle-exposure anchor (the creator's auto pick 1/1000 — 0EV 锚点恒为中间
    /// 曝光，不随基准帧切换) — "+"/"-" with one decimal, the anchor frame itself
    /// "0.0 EV" — and <see cref="TaskGroupViewModel.IsBracketed"/> is true, so the
    /// view swaps the ★ button for the ±EV tag. A manual reference on the 1/500 frame
    /// moves the 基准/★ but leaves every EV anchored to the 1/1000 middle.</summary>
    [Fact]
    public async Task Bracketed_EvTexts_FixedMiddleAnchor()
    {
        var vm = await BracketedVmAsync();

        Assert.True(vm.IsBracketed);
        Assert.Equal(Frames[3], vm.Group.ReferencePath);           // 中间曝光 1/1000 = 基准
        Assert.Equal(
            new[] { "-3.0 EV", "-2.0 EV", "-1.0 EV", "0.0 EV", "+1.0 EV", "+2.0 EV", "+3.0 EV" },
            vm.FrameChips.Select(c => c.EvText).ToArray());

        // 手动基准 = 1/500 帧：EV 仍锚定 1/1000 中间曝光（固定锚点，不随基准切换）
        vm.SetReferenceCommand.Execute(vm.FrameChips[4]);

        Assert.True(vm.FrameChips[4].IsReference);
        Assert.False(vm.FrameChips[3].IsReference);                // 中间曝光帧不再是基准
        Assert.Equal("0.0 EV", vm.FrameChips[3].EvText);           // …却仍恒为 0EV 锚点
        Assert.Equal(
            new[] { "-3.0 EV", "-2.0 EV", "-1.0 EV", "0.0 EV", "+1.0 EV", "+2.0 EV", "+3.0 EV" },
            vm.FrameChips.Select(c => c.EvText).ToArray());
    }

    /// <summary>Equal exposures keep the star path: IsBracketed false and every EvText
    /// reads 0.0 EV (equal cards show ★, not the ±EV tag).</summary>
    [Fact]
    public void EqualExposure_NotBracketed_StarPath()
    {
        var vm = ReadyVm(read: Reader(),
            metadata: Frames.ToDictionary(f => f, _ => Meta(), StringComparer.OrdinalIgnoreCase));

        Assert.False(vm.IsBracketed);
        Assert.All(vm.FrameChips, c => Assert.Equal("0.0 EV", c.EvText));
    }

    /// <summary>Fixed anchor (EV 恒以中间曝光为 0EV 锚点): starring the 1/500 frame moves
    /// the 基准/★ but leaves EVERY chip's EvText untouched (the EVs are stable identifiers
    /// of each frame's bracket position — before/after asserted as exact strings), and
    /// chips never reorder.</summary>
    [Fact]
    public async Task SetReference_DoesNotChangeEv_FixedMiddleAnchor()
    {
        var vm = await BracketedVmAsync();
        string[] before = vm.FrameChips.Select(c => c.EvText).ToArray();
        Assert.Equal(
            new[] { "-3.0 EV", "-2.0 EV", "-1.0 EV", "0.0 EV", "+1.0 EV", "+2.0 EV", "+3.0 EV" },
            before);

        vm.SetReferenceCommand.Execute(vm.FrameChips[4]);          // 1/500 帧设为基准

        Assert.True(vm.FrameChips[4].IsReference);                 // 基准移到 1/500
        Assert.False(vm.FrameChips[3].IsReference);
        Assert.Equal(before, vm.FrameChips.Select(c => c.EvText).ToArray());   // EV 全部不变
        Assert.Equal("0.0 EV", vm.FrameChips[3].EvText);           // 中间曝光恒 0EV（非基准也如此）
        Assert.Equal(Frames, vm.FrameChips.Select(c => c.Path).ToArray());   // 星标不重排
    }

    /// <summary>An unreadable frame carries no exposure signal: its tag reads — while
    /// the readable frames keep numeric EVs against the fixed middle-exposure anchor
    /// (PickReference sorts the unreadable frame's 0 exposure first, so the 1/2000
    /// frame lands mid-list and becomes the anchor), and IsBracketed is computed from
    /// the readable frames only (same missing-metadata condition as 无法读取).</summary>
    [Fact]
    public void UnreadableFrame_EvDash()
    {
        double[] exposures = { 1.0 / 8000, 1.0 / 4000, 1.0 / 2000, 1.0 / 1000, 1.0 / 500, 1.0 / 250 };
        Dictionary<string, RawMetadata> metadata = Frames.Take(6).Zip(exposures).ToDictionary(
            p => p.First, p => Meta(exposure: p.Second),
            StringComparer.OrdinalIgnoreCase);                     // Frames[6] 不入缓存 = 不可读
        var vm = ReadyVm(read: Reader(), metadata: metadata);

        Assert.True(vm.IsBracketed);                              // 只看可读帧
        Assert.Equal(
            new[] { "-2.0 EV", "-1.0 EV", "0.0 EV", "+1.0 EV", "+2.0 EV", "+3.0 EV", "—" },
            vm.FrameChips.Select(c => c.EvText).ToArray());
        Assert.Equal(string.Empty, vm.FrameChips[6].InfoText);   // 未读取 → 空（F4），非 无法读取
    }

    // ---- 即时建卡 + 卡片级校验 Loading ----------------------------------------

    /// <summary>校验飞行中读取抛错（非 RawReadException）不得悬死
    /// Loading —— 卡落 Blocked、IssuesText 给人话文案，IsValidating 必然复位。</summary>
    [Fact]
    public async Task ValidateAsync_ReadThrows_BlockedWithFriendlyMessage_NotStuckLoading()
    {
        Func<string, RawMetadata> read = _ => throw new IOException("disk gone");
        var card = new TaskGroupViewModel(new TaskGroup(Frames), validation: null, read: read);

        await card.ValidateAsync();

        Assert.False(card.IsValidating);
        Assert.Equal(TaskState.Blocked, card.State);
        Assert.Contains("校验时发生错误", card.IssuesText);
        Assert.Contains("disk gone", card.IssuesText);
    }

    /// <summary>校验中整卡编辑入口全锁（IsEditable=false —— ★/✕/添加 RAW
    /// 全部走它），状态胶囊文字为 校验中；落定后按状态恢复。</summary>
    [Fact]
    public async Task IsEditable_FalseWhileValidating()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, RawMetadata> read = _ =>
        {
            gate.Task.Wait();
            return Meta();
        };
        var card = new TaskGroupViewModel(new TaskGroup(Frames), validation: null, read: read);
        Assert.True(card.IsEditable);                        // Blocked（尚未校验）仍可编辑
        Assert.Equal(string.Empty, card.IssuesText);         // 即时建卡初始态：空 issues

        Task flight = card.ValidateAsync();
        Assert.True(card.IsValidating);
        Assert.Equal("校验中", card.StateText);
        Assert.False(card.IsEditable);
        Assert.False(card.SetReferenceCommand.CanExecute(null));
        Assert.False(card.RemoveFileCommand.CanExecute(null));

        gate.SetResult(true);
        await flight;

        Assert.False(card.IsValidating);
        Assert.Equal(TaskState.Ready, card.State);
        Assert.True(card.IsEditable);
        Assert.Equal("就绪", card.StateText);
    }

    /// <summary>补齐（卡片级 AddFilesAsync 顶-up）同样用 IsValidating 包裹整个飞行：
    /// 读帧期间卡处于校验中、编辑锁定；异常路径（非 RawReadException）也不悬死 ——
    /// finally 必复位（校验飞行两入口同款保证）。</summary>
    [Fact]
    public async Task AddFiles_TopUp_BracketsLoading_NeverStuck()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool fail = false;
        Func<string, RawMetadata> read = _ =>
        {
            if (fail)
                throw new IOException("disk gone");
            gate.Task.Wait();
            return Meta();
        };
        var vm = ReadyVm(read: read);

        Task flight = vm.AddFilesAsync(new[] { @"C:\Photos\DSC_7583.NEF" });
        Assert.True(vm.IsValidating);
        Assert.False(vm.IsEditable);
        Assert.Equal("校验中", vm.StateText);

        gate.SetResult(true);
        await flight;
        Assert.False(vm.IsValidating);
        Assert.Equal(TaskState.Blocked, vm.State);           // 8 帧 → 帧数不符
        Assert.Contains("帧数不符", vm.IssuesText);

        fail = true;                                         // 异常路径：loading 不得悬死
        await Assert.ThrowsAsync<IOException>(
            () => vm.AddFilesAsync(new[] { @"C:\Photos\DSC_7584.NEF" }));
        Assert.False(vm.IsValidating);
    }

    /// <summary>Seven bracketed frames 1/8000→1/125 through the creator (reference =
    /// middle exposure 1/1000), seeded into a card the way the production flow does.</summary>
    private static async Task<TaskGroupViewModel> BracketedVmAsync()
    {
        double[] exposures =
        {
            1.0 / 8000, 1.0 / 4000, 1.0 / 2000, 1.0 / 1000, 1.0 / 500, 1.0 / 250, 1.0 / 125,
        };
        Dictionary<string, RawMetadata> perFile = Frames.Zip(exposures).ToDictionary(
            p => p.First, p => Meta(exposure: p.Second), StringComparer.OrdinalIgnoreCase);
        Func<string, RawMetadata> read = path =>
            perFile.TryGetValue(path, out RawMetadata? m) ? m : Meta();

        CreatedTaskGroup? created = await TaskGroupCreator.CreateAsync(Frames, read);
        Assert.NotNull(created);
        return new TaskGroupViewModel(created!.Group, created.Validation,
            metadata: created.Metadata, read: read);
    }

    [Fact]
    public void BackendVersion_DefaultsEmpty_ThenSettable()
    {
        var main = new MainViewModel();

        Assert.Equal("", main.BackendVersion);

        main.BackendVersion = "v2.3.27";          // MainWindow.OnOpened 赋值（同 AppVersion）
        Assert.Equal("v2.3.27", main.BackendVersion);
    }
}

/// <summary>ThemeService: default Dark, cycle Dark→Light→Default→Dark.
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

/// <summary>Single-threaded SynchronizationContext for ConcurrentAdd_SamePath_
/// NoDuplicateFrame: await continuations posted here execute one at a time on the
/// pumping thread — the same serialization the Avalonia UI dispatcher gives the VM's
/// UI-thread-affine mutations in production (without it the two merge sections would
/// race on the shared state, which the real app never allows to happen).</summary>
internal sealed class SerialPump : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public void RunUntilComplete(params Task[] tasks)
    {
        while (!Task.WhenAll(tasks).IsCompleted)
        {
            (SendOrPostCallback callback, object? state) = _queue.Take();
            callback(state);
        }
    }
}
