using Jsr4Z8.Core;
using Jsr4Z8.Core.Dark;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Ptc;
using Jsr4Z8.UI.ViewModels;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Task 4: headless tests for inline group creation — TaskGroupCreator replaces the
/// deleted 新建任务组 dialog. A drop/pick of NEF files becomes a validated TaskGroup
/// directly in the main window: .nef filter/dedupe/natural sort, the exposure-bracketing
/// reference pick (equal exposures → natural first; 7 differing shutters → the middle
/// exposure), and TaskGroupValidator through a per-call memoized reader. The
/// MainViewModel section covers AddFilesAsync (counter + inline post + derived counts)
/// and 清除全部. Negative cases inject mock readers; the two integration tests read the
/// real samples through the production LibRaw reader (~1-2 s each).
/// </summary>
public class TaskGroupCreatorTests
{
    private static readonly string[] Frames =
        Enumerable.Range(7576, 7).Select(i => $@"C:\Photos\DSC_{i}.NEF").ToArray();

    private static RawMetadata Meta(
        double iso = 3200,
        string make = "NIKON CORPORATION",
        string model = "NIKON Z 8",
        double exposure = 0.000125) => new()
    {
        Make = make,
        Model = model,
        Iso = iso,
        BlackLevel = 1008,
        WhiteLevel = 16383,
        BitsPerSample = 14,
        Compression = 34713,
        NefCompression = 3,
        Width = 8280,
        Height = 5520,
        CamMul = new double[] { 1.785, 1.0, 1.793, 1.0 },
        CfaPattern = new byte[] { 0, 1, 3, 2 },
        ExposureTime = exposure,   // 1/8000 s by default — equal across the set
        Aperture = 4.0,
        DateTimeOriginal = "",
    };

    /// <summary>Mock reader: per-file metadata overrides win, listed files throw RawReadException.</summary>
    private static Func<string, RawMetadata> Reader(
        Dictionary<string, RawMetadata>? perFile = null,
        HashSet<string>? unreadable = null) => path =>
    {
        if (unreadable is not null && unreadable.Contains(path))
            throw new RawReadException($"mock open failure (file={path})");
        if (perFile is not null && perFile.TryGetValue(path, out RawMetadata? m))
            return m;
        return Meta();
    };

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

    /// <summary>Review Focus 1: a non-NEF-only payload must not create anything — the
    /// caller no-ops, so a stray drop can never yield an empty card or a status flicker.</summary>
    [Fact]
    public async Task Create_NonNefOnly_ReturnsNull()
    {
        Assert.Null(await TaskGroupCreator.CreateAsync(["a.jpg", "B.NRW", "c.nef.txt"]));
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
            Path.Combine("test_frames", "FX", "Lossless", $"DSC_{i}.NEF"))).ToArray();
        CreatedTaskGroup? created = await TaskGroupCreator.CreateAsync(Shuffled()
            .Select(p => SampleFiles.FindSample(Path.Combine("test_frames", "FX", "Lossless",
                Path.GetFileName(p))))
            .ToArray());

        Assert.NotNull(created);
        Assert.True(created!.Validation.CanRun);
        Assert.Equal(expected, created.Group.FramePaths);
        Assert.Equal("DSC_7576.NEF", Path.GetFileName(created.Group.ReferencePath));
    }

    // ---- MainViewModel: inline card creation + counts -----------------------------

    private static TaskGroupViewModel ReadyVm() => new(
        new TaskGroup(Frames),
        new ValidationResult(true, Array.Empty<ValidationIssue>(), new PtcMatch(9512, true, "表值"),
            new DarkMatch(3200, 1005.89, 14.54, "preset")));

    /// <summary>Inline post (headless): the created card lands on the calling flow.</summary>
    private static MainViewModel MainVm() => new();

    /// <summary>Integration-style (the production reader is the only read path here):
    /// a valid drop becomes one Ready card via the post seam, the counter settles back
    /// to 0, and the derived counts follow the card's state changes.</summary>
    [Fact]
    public async Task AddFilesAsync_CreatesCardViaPost()
    {
        MainViewModel main = MainVm();
        await main.AddFilesAsync(Frames.Select(p => SampleFiles.FindSample(Path.Combine(
            "test_frames", "FX", "Lossless", Path.GetFileName(p)))).ToArray());

        TaskGroupViewModel card = Assert.Single(main.Tasks);
        Assert.Equal(TaskState.Ready, card.State);
        Assert.Equal(0, main.ValidatingCount);
        Assert.Equal(1, main.ReadyCount);
        Assert.Equal("共 1 个任务组", main.StatusText);

        card.GoRunning();                                              // card state changes refresh the counts
        card.GoDone(null);
        Assert.Equal(0, main.ReadyCount);
        Assert.Equal(1, main.DoneCount);
    }

    /// <summary>Review Focus 1 (main-window side): a non-NEF-only drop is a no-op — no
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

    /// <summary>Review Focus 2 (concurrency): two drops whose flights overlap each get
    /// their own card — two independent TaskGroupCreator runs (per-call memoized
    /// readers), no cross-talk, and the counter tracks both until both land.</summary>
    [Fact]
    public async Task AddFilesAsync_ConcurrentDrops_TwoCards()
    {
        MainViewModel main = MainVm();
        string[] setA = Enumerable.Range(7576, 7).Select(i => SampleFiles.FindSample(
            Path.Combine("test_frames", "FX", "Lossless", $"DSC_{i}.NEF"))).ToArray();
        string[] setB = Enumerable.Range(7652, 7).Select(i => SampleFiles.FindSample(
            Path.Combine("test_frames", "FX", "he_frames", $"DSC_{i}.NEF"))).ToArray();

        Task first = main.AddFilesAsync(setA);
        Task second = main.AddFilesAsync(setB);                        // sent before the first lands
        Assert.Equal(2, main.ValidatingCount);                         // both flights in the air
        await Task.WhenAll(first, second);

        Assert.Equal(2, main.Tasks.Count);
        Assert.All(main.Tasks, t => Assert.Equal(TaskState.Ready, t.State));
        Assert.Equal(2, main.ReadyCount);
        Assert.Equal(0, main.ValidatingCount);
        Assert.Equal("共 2 个任务组", main.StatusText);
        // Final-review F7: completion order across the concurrent flights is not
        // deterministic — compare as a set (sorted), not by Tasks[0]/Tasks[1] slots:
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
        Assert.Equal("共 1 个任务组", main.StatusText);
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
