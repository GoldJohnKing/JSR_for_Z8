using Jsr4Z8.Core;
using Jsr4Z8.Core.Dark;

using static Jsr4Z8.Tests.MockMeta;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Ptc;
using Jsr4Z8.UI.ViewModels;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Headless tests for inline group creation — TaskGroupCreator: a drop/pick
/// of NEF files becomes a validated TaskGroup
/// directly in the main window: .nef filter/dedupe/natural sort, the exposure-bracketing
/// reference pick (equal exposures → natural first; 7 differing shutters → the middle
/// exposure), and TaskGroupValidator through a per-call memoized reader. The
/// MainViewModel section covers AddFilesAsync (counter + inline post + derived counts)
/// and 清除全部. Negative cases inject mock readers; the integration tests read the
/// real samples (equal-exposure and bracketing sets) through the production LibRaw
/// reader (~1-2 s each).
/// </summary>
public class TaskGroupCreatorTests
{
    private static readonly string[] Frames =
        Enumerable.Range(7576, 7).Select(i => $@"C:\Photos\DSC_{i}.NEF").ToArray();

    /// <summary>Per-file ExposureTime overrides for the whole Frames set, in file order.</summary>
    private static Dictionary<string, RawMetadata> Exposures(params double[] seconds) =>
        Frames.Zip(seconds).ToDictionary(
            p => p.First, p => Meta(exposure: p.Second), StringComparer.OrdinalIgnoreCase);

    /// <summary>CreateAsync with the mock reader, asserting a group came back.</summary>
    private static async Task<CreatedTaskGroup> Create(
        IEnumerable<string> paths, Func<string, RawMetadata>? read = null)
    {
        CreatedTaskGroup? created = await TaskGroupCreator.CreateAsync(paths.ToArray(), read ?? Reader());
        Assert.NotNull(created);
        return created!;
    }

    private static string[] Shuffled() => new[] { 7582, 7576, 7579, 7577, 7581, 7578, 7580 }
        .Select(i => $@"C:\Photos\DSC_{i}.NEF").ToArray();

    /// <summary>注入式 read 缝：后台读取阻塞在门阀上，放行后返回 MockMeta 元数据
    /// ——「即时建卡/校验飞行」类时序测试的公共样板（与卡片 VM 的 read 缝语义一致）。</summary>
    private static Func<string, RawMetadata> GatedRead(TaskCompletionSource<bool> gate) =>
        _ => { gate.Task.Wait(); return Meta(); };

    // ---- TaskGroupCreator: filter / order / reference pick ------------------------

    [Fact]
    public async Task Create_ShuffledSeven_EqualExposure_ReferenceIsNaturalFirst()
    {
        CreatedTaskGroup created = await Create(Shuffled());

        Assert.Equal(Frames, created.Group.FramePaths);                 // 文件名自然升序
        Assert.Equal(Frames[0], created.Group.ReferencePath);           // 同曝光 → 第 1 张为基准
        Assert.True(created.Validation.CanRun);
    }

    [Fact]
    public async Task Create_BracketedSeven_MiddleExposureIsReference()
    {
        // 7-frame shutter bracket, fed in shuffled order; only the shutters differ.
        Dictionary<string, RawMetadata> perFile = Exposures(
            1.0 / 8000, 1.0 / 4000, 1.0 / 2000, 1.0 / 1000, 1.0 / 500, 1.0 / 250, 1.0 / 125);
        CreatedTaskGroup created = await Create(Shuffled(), Reader(perFile));

        Assert.Equal(Frames[3], created.Group.ReferencePath);           // 1/1000 = 中间曝光（index 7/2）
        Assert.Equal(                                                    // 其余 6 帧自然序在后
            new[] { Frames[3], Frames[0], Frames[1], Frames[2], Frames[4], Frames[5], Frames[6] },
            created.Group.FramePaths);
        Assert.True(created.Validation.CanRun);                         // 仅快门不同不阻断
    }

    [Fact]
    public async Task Create_BracketedTiedExposures_DeterministicPick()
    {
        // exposures {1,1,2,2,4,4,8} on DSC_7576..7582: ascending (exposure, natural
        // name) order is 7576,7577,7578,7579,7580,7581,7582 — index Count/2 = 3 picks
        // the 4th position, deterministically the second of the tied 2s.
        Dictionary<string, RawMetadata> perFile = Exposures(1, 1, 2, 2, 4, 4, 8);
        CreatedTaskGroup created = await Create(Frames, Reader(perFile));

        Assert.Equal(Frames[3], created.Group.ReferencePath);           // 升序稳定排序后第 4 位
    }

    [Fact]
    public async Task Create_FilterDedupeNatural()
    {
        CreatedTaskGroup created = await Create(new[]
        {
            @"C:\P\DSC_10.NEF", @"C:\P\DSC_2.NEF", @"C:\P\DSC_1.NEF",
            @"C:\P\DSC_1.nef",                                      // case-insensitive dupe
            @"C:\P\c.nrW", @"C:\P\sidecar.jpg", @"C:\P\notes.txt",   // Z8 is NEF-only: .nrw/jpg rejected
        });

        Assert.Equal(new[] { @"C:\P\DSC_1.NEF", @"C:\P\DSC_2.NEF", @"C:\P\DSC_10.NEF" },
            created.Group.FramePaths);                                // 去重后自然升序（2 < 10）
    }

    /// <summary>A non-NEF-only payload must not create anything — the
    /// caller no-ops, so a stray drop can never yield an empty card or a status flicker.</summary>
    [Fact]
    public async Task Create_NonNefOnly_ReturnsNull()
    {
        Assert.Null(await TaskGroupCreator.CreateAsync(["a.jpg", "B.NRW", "c.nef.txt"]));
    }

    /// <summary>The creation result carries the per-path metadata (the memoized
    /// read pass the reference pick and validation already used) so the card VM can
    /// seed its caches and build hover texts without re-reading — keys are the frame
    /// paths, case-insensitive (Windows), readable frames only.</summary>
    [Fact]
    public async Task CreateAsync_ReturnsMetadata()
    {
        CreatedTaskGroup created = await Create(Frames);

        Assert.Equal(7, created.Metadata.Count);
        Assert.Equal(Frames, created.Metadata.Keys.OrderBy(p => p, StringComparer.Ordinal));
        Assert.All(created.Metadata.Values, m => Assert.Equal("NIKON Z 8", m.Model));
        RawMetadata first = created.Metadata[Frames[0].ToLowerInvariant()];  // 大小写不敏感键
        Assert.Equal("NIKON Z 8", first.Model);
    }

    /// <summary>Public contract of the extracted exposure-middle pick (the card VM
    /// re-runs it on every add/remove): equal exposures → the natural-first frame;
    /// a bracket → the (ExposureTime, natural name)-ascending middle (index Count/2);
    /// ties resolve deterministically down that order.</summary>
    [Fact]
    public void PickReference_PublicContract()
    {
        // 等曝光 → 自然序第 1 张
        Assert.Equal(Frames[0], TaskGroupCreator.PickReference(Frames, Reader()));

        // 曝光包围 → 升序中间位（index 7/2 = 3 → 1/1000）
        Dictionary<string, RawMetadata> perFile = Exposures(
            1.0 / 8000, 1.0 / 4000, 1.0 / 2000, 1.0 / 1000, 1.0 / 500, 1.0 / 250, 1.0 / 125);
        Assert.Equal(Frames[3], TaskGroupCreator.PickReference(Frames, Reader(perFile)));

        // 并列曝光 {1,1,2,2,4,4,8} → 确定性取并列 2 的第 2 张（升序第 4 位）
        Dictionary<string, RawMetadata> tied = Exposures(1, 1, 2, 2, 4, 4, 8);
        Assert.Equal(Frames[3], TaskGroupCreator.PickReference(Frames, Reader(tied)));
    }

    [Fact]
    public async Task Create_EightFrames_BlockedWithFrameCount()
    {
        CreatedTaskGroup created = await Create(Frames.Append(@"C:\Photos\DSC_7583.NEF"));

        Assert.False(created.Validation.CanRun);                       // 8 ≠ 7：卡仍建，错误进区块
        Assert.Contains(created.Validation.Issues, i => i.MessageZh.Contains("帧数不符"));
    }

    [Fact]
    public async Task Create_UnreadableFrame_BlockedNamingFile()
    {
        CreatedTaskGroup created = await Create(Frames,
            Reader(unreadable: new HashSet<string> { Frames[5] }));

        Assert.False(created.Validation.CanRun);
        Assert.Contains(created.Validation.Issues, i =>
            i.MessageZh.Contains("DSC_7581.NEF") && i.MessageZh.Contains("无法读取"));
    }

    /// <summary>钉死"仅快门"边界: a shutter bracket validates fine, but mixing in one
    /// frame with a different ISO is blocked as IsoMismatch.</summary>
    [Fact]
    public async Task Create_BracketedButIsoDiffers_Blocked()
    {
        Dictionary<string, RawMetadata> perFile = Exposures(
            1.0 / 8000, 1.0 / 4000, 1.0 / 2000, 1.0 / 1000, 1.0 / 500, 1.0 / 250, 1.0 / 125);
        perFile[Frames[4]] = Meta(iso: 6400, exposure: 1.0 / 500);     // 第 5 帧 ISO 不同
        CreatedTaskGroup created = await Create(Frames, Reader(perFile));

        Assert.False(created.Validation.CanRun);
        Assert.Contains(created.Validation.Issues, i => i.Kind == IssueKind.IsoMismatch);
    }

    /// <summary>Integration-style: production LibRaw reader on the real samples — a real
    /// same-exposure 7-frame set stays natural-ordered with DSC_7576 as 基准帧.</summary>
    [Fact]
    public async Task Create_RealSamples_CanRun()
    {
        string[] expected = Enumerable.Range(7576, 7).Select(i => SampleFiles.FindSample(
            Path.Combine("test_frames", "lossless", $"DSC_{i}.NEF"))).ToArray();
        CreatedTaskGroup? created = await TaskGroupCreator.CreateAsync(Shuffled()
            .Select(p => SampleFiles.FindSample(Path.Combine("test_frames", "lossless",
                Path.GetFileName(p))))
            .ToArray());

        Assert.NotNull(created);
        Assert.True(created!.Validation.CanRun);
        Assert.Equal(expected, created.Group.FramePaths);
        Assert.Equal("DSC_7576.NEF", Path.GetFileName(created.Group.ReferencePath));
    }

    /// <summary>Integration-style: the real bracketing set test_frames\unequal_exposure
    /// (2026-10-15, ISO 3200 / f4 constant): a 7-frame 1/3-EV shutter ladder 1/200…1/50
    /// around 1/100, shot with the center frame first (DSC_7855=1/100, then 1/200, 1/160,
    /// 1/125, 1/80, 1/60, 1/50) — non-monotonic on purpose. The PickReference ascending
    /// sort still takes index 7/2 = 3 → the 1/100 frame as 基准帧, and a shutter-only
    /// bracket stays CanRun (ISO 恒定 → lossless 预置行).</summary>
    [Fact]
    public async Task Create_RealSamples_UnequalExposure_MiddleExposureReference()
    {
        string[] frames = Enumerable.Range(7855, 7).Select(i => SampleFiles.FindSample(
            Path.Combine("test_frames", "unequal_exposure", $"DSC_{i}.NEF"))).ToArray();
        string[] shuffled = new[] { 7861, 7856, 7859, 7855, 7858, 7860, 7857 }.Select(i => SampleFiles.FindSample(
            Path.Combine("test_frames", "unequal_exposure", $"DSC_{i}.NEF"))).ToArray();

        CreatedTaskGroup? created = await TaskGroupCreator.CreateAsync(shuffled);  // 输入顺序无关：乱序喂入

        Assert.NotNull(created);
        Assert.True(created!.Validation.CanRun, string.Join("\n", created.Validation.Issues.Select(i => i.MessageZh)));
        // 正性断言（自足化）：EXIF 真实可读（ExposureTime > 0）——全 0 时下面的 /2 检查恒真，
        // 且基准会退化为自然首帧，测试就不再钉住中位选择本身。
        Assert.All(frames, p => Assert.True(created.Metadata[p].ExposureTime > 0));
        // 真实 EXIF：快门不全等（7856 的快门 = 7855 的一半）——确系曝光包围，非同曝光回退
        Assert.Equal(created.Metadata[frames[0]].ExposureTime / 2,
            created.Metadata[frames[1]].ExposureTime, 6);
        Assert.EndsWith("DSC_7855.NEF", created.Group.ReferencePath); // 1/100 = 升序第 4 位 = 中间曝光
        Assert.Equal(frames, created.Group.FramePaths);                // 基准在前，其余自然序
    }

    // ---- MainViewModel: inline card creation + counts -----------------------------

    private static TaskGroupViewModel ReadyVm() => new(
        new TaskGroup(Frames),
        new ValidationResult(true, Array.Empty<ValidationIssue>(), new PtcMatch(9512, true, "表值"),
            new DarkMatch(3200, 1005.89, 14.54, "preset")));

    /// <summary>Inline post (headless): the created card lands on the calling flow.</summary>
    private static MainViewModel MainVm() => new();

    /// <summary>Integration-style (the production reader is the only read path here):
    /// a valid drop becomes one Ready card — the card is created immediately and
    /// validates itself (the Loading flight settles before the await returns) — and
    /// the derived counts follow the card's state changes.</summary>
    [Fact]
    public async Task AddFilesAsync_CreatesCardViaPost()
    {
        MainViewModel main = MainVm();
        await main.AddFilesAsync(Frames.Select(p => SampleFiles.FindSample(Path.Combine(
            "test_frames", "lossless", Path.GetFileName(p)))).ToArray());

        TaskGroupViewModel card = Assert.Single(main.Tasks);
        Assert.Equal(TaskState.Ready, card.State);
        Assert.Equal(0, main.ValidatingCount);
        Assert.Equal(1, main.ReadyCount);
        Assert.Equal(string.Empty, main.StatusText);

        card.GoRunning();                                              // card state changes refresh the counts
        card.GoDone(null);
        Assert.Equal(0, main.ReadyCount);
        Assert.Equal(1, main.DoneCount);
    }

    /// <summary>即时建卡：默认 inline post 下 AddFilesAsync 的同步段恰好推进到
    /// ValidateAsync 的首个 await（在建卡之后），因此调用返回的 Task 尚未完成时卡
    /// 已入列且处于校验中（Loading 覆盖层的数据源）——本测试钉死这一顺序；释放门阀
    /// 后落就绪。read 缝用 TCS 门阀挡住后台读取（注入式 read 缝，同卡片 VM 一致）。</summary>
    [Fact]
    public async Task AddFiles_CardAppearsImmediately_ThenValidates()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, RawMetadata> read = GatedRead(gate);
        var main = new MainViewModel(read: read);

        Task flight = main.AddFilesAsync(Frames);

        Assert.Single(main.Tasks);                            // 卡已出现，不等校验
        TaskGroupViewModel card = main.Tasks[0];
        Assert.True(card.IsValidating);
        Assert.Equal("校验中", card.StateText);
        Assert.Equal(string.Empty, card.IssuesText);           // 即时建卡初始态

        gate.SetResult(true);
        await flight;

        Assert.False(card.IsValidating);
        Assert.Equal(TaskState.Ready, card.State);
        Assert.Equal(Frames, card.Group.FramePaths);
        Assert.Equal(0, main.ValidatingCount);
        Assert.Equal(1, main.ReadyCount);
    }

    /// <summary>派生计数：ValidatingCount = Tasks.Count(t => t.IsValidating)，
    /// 随卡属性通知即时刷新 —— 校验中=1（状态行 正在校验 1 组新建任务…），校验中的卡
    /// 被移除即回落 0（派生语义下无需手工递减，飞行中的卡移除也不会悬死状态行）。</summary>
    [Fact]
    public async Task ValidatingCount_DerivedFromCards()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, RawMetadata> read = GatedRead(gate);
        var main = new MainViewModel(read: read);

        Task flight = main.AddFilesAsync(Frames);
        Assert.Equal(1, main.ValidatingCount);
        Assert.Equal("正在校验 1 组新建任务…", main.StatusText);

        main.Tasks[0].RemoveCommand.Execute(null);             // 校验中的卡被移除 → 计数即时回落
        Assert.Empty(main.Tasks);
        Assert.Equal(0, main.ValidatingCount);
        Assert.Equal("拖放 7 张 RAW 开始", main.StatusText);

        gate.SetResult(true);
        await flight;                                          // 分离的卡自行落定，不再驱动计数
        Assert.Equal(0, main.ValidatingCount);
    }

    /// <summary>Main-window side: a non-NEF-only drop is a no-op — no
    /// card, counter untouched, status line unchanged (no 正在校验 flicker).</summary>
    [Fact]
    public async Task AddFilesAsync_EmptyNef_NoCardNoStatusFlicker()
    {
        MainViewModel main = MainVm();
        Assert.Equal("拖放 7 张 RAW 开始", main.StatusText);

        await main.AddFilesAsync(new[] { @"C:\P\a.jpg", @"C:\P\b.nrW", @"C:\P\c.txt" });

        Assert.Empty(main.Tasks);
        Assert.Equal(0, main.ValidatingCount);
        Assert.Equal("拖放 7 张 RAW 开始", main.StatusText);
    }

    /// <summary>Concurrency: two drops whose flights overlap each get
    /// their own card — two independently validating cards, no cross-talk, and the
    /// derived counter tracks both until both land. The read seam blocks on a TCS gate
    /// so the mid-flight count/status assertions are deterministic (the mock reader
    /// replaces the real-sample reads here; AddFilesAsync_CreatesCardViaPost keeps the
    /// production LibRaw coverage).</summary>
    [Fact]
    public async Task AddFilesAsync_ConcurrentDrops_TwoCards()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, RawMetadata> read = GatedRead(gate);
        var main = new MainViewModel(read: read);
        string[] setA = Frames;
        string[] setB = Enumerable.Range(7652, 7).Select(i => $@"C:\Photos\DSC_{i}.NEF").ToArray();

        Task first = main.AddFilesAsync(setA);
        Task second = main.AddFilesAsync(setB);                        // sent before the first lands
        Assert.Equal(2, main.ValidatingCount);                         // both flights in the air (derived)
        Assert.Equal("正在校验 2 组新建任务…", main.StatusText);
        gate.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(2, main.Tasks.Count);
        Assert.All(main.Tasks, t => Assert.Equal(TaskState.Ready, t.State));
        Assert.Equal(2, main.ReadyCount);
        Assert.Equal(0, main.ValidatingCount);
        Assert.Equal(string.Empty, main.StatusText);
        // Completion order across the concurrent flights is not deterministic —
        // compare as a set (sorted), not by Tasks[0]/Tasks[1] slots:
        // both reference paths present, exactly once each.
        string[] references = main.Tasks
            .Select(t => Path.GetFileName(t.Group.ReferencePath))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "DSC_7576.NEF", "DSC_7652.NEF" }, references);
    }

    [Fact]
    public void ClearAll_KeepsRunningRemovesRest()
    {
        MainViewModel main = MainVm();
        TaskGroupViewModel ready = ReadyVm();
        TaskGroupViewModel running = ReadyVm();
        TaskGroupViewModel done = ReadyVm();
        running.GoRunning();
        done.GoRunning();
        done.GoDone(null);
        main.Add(ready);
        main.Add(running);
        main.Add(done);

        main.ClearAllCommand.Execute(null);

        Assert.Same(running, Assert.Single(main.Tasks));               // Running 卡不动
        Assert.Equal(0, main.ReadyCount);
        Assert.Equal(0, main.DoneCount);
        Assert.Equal(string.Empty, main.StatusText);
    }

    /// <summary>AddValidated (the post-back entry): CanRun → Ready card; CanRun=false is
    /// defensive — a Blocked card with the issues in its block.</summary>
    [Fact]
    public void MainAddValidated_CreatesReadyAndBlockedCards()
    {
        var main = new MainViewModel();
        main.AddValidated(new TaskGroup(Frames),
            new ValidationResult(true, Array.Empty<ValidationIssue>(), new PtcMatch(1102.2, true, "table")));
        Assert.Equal(TaskState.Ready, Assert.Single(main.Tasks).State);

        main.AddValidated(new TaskGroup(Frames),
            new ValidationResult(false,
                new[] { new ValidationIssue("", IssueKind.FrameCount, "帧数不符") }, null));
        Assert.Equal(TaskState.Blocked, main.Tasks[1].State);
    }
}
