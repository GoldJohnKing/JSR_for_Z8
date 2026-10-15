using Jsr4Z8.Core;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Ptc;
using Jsr4Z8.UI.ViewModels;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Task 12: headless tests for the 新建任务组 dialog ViewModel (docs/design/
/// add-task-flow-v3.html). The VM debounces validation on a background thread with a
/// 1 ms delay here (production 300 ms); ValidationIdle is the test seam that awaits the
/// settled pass. Negative cases inject mock readers; the final test reads the real
/// samples DSC_7576..7582 through the production LibRaw reader
/// (integration-style, ~1-2 s).
/// </summary>
public class NewTaskVmTests
{
    private static readonly string[] Frames =
        Enumerable.Range(7576, 7).Select(i => $@"C:\Photos\DSC_{i}.NEF").ToArray();


    private static RawMetadata Meta(
        double iso = 3200,
        string make = "NIKON CORPORATION",
        string model = "NIKON Z 8") => new()
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
        ExposureTime = 0.000125,   // 1/8000 s
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

    private static NewTaskViewModel Vm(Func<string, RawMetadata>? read = null, TimeSpan? debounce = null) =>
        new(read: read ?? Reader(), debounce: debounce ?? TimeSpan.FromMilliseconds(1));

    [Fact]
    public void Drop_ShuffledSeven_SortsNaturally_ReferenceIsFirst()
    {
        int[] shuffled = { 7582, 7576, 7579, 7577, 7581, 7578, 7580 };
        var vm = Vm();
        vm.HandleFilesDropped(shuffled.Select(i => $@"C:\Photos\DSC_{i}.NEF"));

        Assert.Equal(Frames, vm.Frames.Select(f => f.Path).ToArray());   // 文件名自然升序
        Assert.True(vm.Frames[0].IsReference);                            // 第 1 张为基准
        Assert.All(vm.Frames.Skip(1), f => Assert.False(f.IsReference));
        Assert.True(vm.HasFrames);
        Assert.False(vm.HasNoFrames);
    }

    [Fact]
    public void Drop_NaturalOrder_NotLexicographic()
    {
        var vm = Vm();
        vm.HandleFilesDropped(new[] { @"C:\P\DSC_10.NEF", @"C:\P\DSC_2.NEF", @"C:\P\DSC_1.NEF" });

        Assert.Equal(new[] { "DSC_1.NEF", "DSC_2.NEF", "DSC_10.NEF" },
            vm.Frames.Select(f => f.FileName).ToArray());   // DSC_2 < DSC_10 (not "10" < "2")
    }

    [Fact]
    public void Drop_KeepsOnlyNef_CaseInsensitive_Deduped()
    {
        var vm = Vm();
        vm.HandleFilesDropped(new[]
        {
            @"C:\P\a.NEF", @"C:\P\a.nef", @"C:\P\b.NEF",
            @"C:\P\c.nrW", @"C:\P\sidecar.jpg", @"C:\P\notes.txt",   // Z8 is NEF-only: .nrw rejected
        });

        Assert.Equal(new[] { "a.NEF", "b.NEF" }, vm.Frames.Select(f => f.FileName).ToArray());
    }

    // ---- PreloadFiles: main-window drop prefill (post-release fix 2) ----------------

    /// <summary>A main-window drop opens the dialog pre-filled: same dedupe/.nef filter/
    /// natural sort as an in-dialog drop (a fresh dialog's prefill IS its full set).</summary>
    [Fact]
    public void Preload_SortsDedupesFilters_LikeInDialogDrop()
    {
        var vm = Vm();
        int[] shuffled = { 7582, 7576, 7579 };
        vm.PreloadFiles(shuffled
            .Select(i => $@"C:\Photos\DSC_{i}.NEF")
            .Append(@"C:\Photos\DSC_7576.nef")                      // case-insensitive dupe
            .Append(@"C:\Photos\sidecar.jpg")                      // non-NEF filtered out
            .Append(@"C:\Photos\DSC_0001.nrW"));                   // .nrw rejected

        Assert.Equal(new[] { @"C:\Photos\DSC_7576.NEF", @"C:\Photos\DSC_7579.NEF", @"C:\Photos\DSC_7582.NEF" },
            vm.Frames.Select(f => f.Path).ToArray());
        Assert.True(vm.Frames[0].IsReference);
        Assert.True(vm.HasFrames);
    }

    /// <summary>A prefill validates and enables 创建任务组 — the dark calibration is
    /// preset auto-matched (the manual dark picker was removed 2026-10-15).</summary>
    [Fact]
    public async Task Preload_AutoMatchesPreset_CanRun()
    {
        var vm = Vm();
        vm.PreloadFiles(Frames);
        await vm.ValidationIdle;

        Assert.True(vm.State.CanRun);
        Assert.True(vm.CreateCommand.CanExecute(null));
        Assert.Contains("自动匹配", vm.State.PtcInfoText);
    }

    /// <summary>8+ dropped files ALL become frames — exactly the in-dialog behavior — so
    /// the dialog opens showing the 帧数不符 issue instead of silently dropping extras.</summary>
    [Fact]
    public async Task Preload_EightFiles_AllBecomeFrames_FrameCountIssue()
    {
        var vm = Vm();
        vm.PreloadFiles(Frames.Append(@"C:\Photos\DSC_7583.NEF"));  // 8 distinct NEF files
        await vm.ValidationIdle;

        Assert.Equal(8, vm.Frames.Count);
        Assert.False(vm.State.CanRun);
        Assert.Contains("帧数不符", vm.State.IssuesText);               // 8 ≠ 7 → 需要补齐提示
        Assert.False(vm.CreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task NonZ8Model_CreateDisabled_IssueNamesModel()
    {
        var vm = Vm(Reader(perFile: new Dictionary<string, RawMetadata>
        {
            [Frames[3]] = Meta(model: "NIKON Z 6_3"),
        }));
        vm.HandleFilesDropped(Frames);
        await vm.ValidationIdle;

        Assert.False(vm.State.CanRun);
        Assert.False(vm.CreateCommand.CanExecute(null));
        Assert.Contains("Z 6_3", vm.State.IssuesText);                 // 文案含机型名
        Assert.Contains("仅支持 Nikon Z8", vm.State.IssuesText);
    }

    [Fact]
    public async Task RemoveOneFrame_FrameCountIssue()
    {
        var vm = Vm();
        vm.HandleFilesDropped(Frames);
        await vm.ValidationIdle;
        Assert.True(vm.State.CanRun);

        vm.RemoveChipCommand.Execute(vm.Frames[3]);
        Assert.Equal(6, vm.Frames.Count);
        Assert.False(vm.CreateCommand.CanExecute(null));               // immediate invalidate, no stale ✓
        await vm.ValidationIdle;

        Assert.False(vm.State.CanRun);
        Assert.Contains("帧数不符", vm.State.IssuesText);
        Assert.False(vm.CreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task RemoveReferenceChip_PromotesNextToReference()
    {
        var vm = Vm();
        vm.HandleFilesDropped(Frames);
        vm.RemoveChipCommand.Execute(vm.Frames[0]);                    // remove the ★基准 chip

        Assert.Equal(Frames[1], vm.Frames[0].Path);
        Assert.True(vm.Frames[0].IsReference);
        await vm.ValidationIdle;
        Assert.Contains("帧数不符", vm.State.IssuesText);
    }


    [Fact]
    public async Task UnreadableFile_RedChipAndIssue()
    {
        var vm = Vm(Reader(unreadable: new HashSet<string> { Frames[5] }));
        vm.HandleFilesDropped(Frames);
        await vm.ValidationIdle;

        Assert.False(vm.State.CanRun);
        Assert.True(vm.Frames[5].IsError);                             // 红色芯片
        Assert.All(vm.Frames.Where((_, i) => i != 5), f => Assert.False(f.IsError));
        Assert.Contains("无法读取", vm.State.IssuesText);
        Assert.False(vm.CreateCommand.CanExecute(null));
    }

    /// <summary>Click a chip: 与现任互换 — the new ★ takes slot 0, the old one its slot; the
    /// emitted TaskGroup order is ★ first, the rest keep relative order.</summary>
    [Fact]
    public async Task SetReference_SwapsWithCurrent_GroupOrderStarFirst()
    {
        var vm = Vm();
        vm.HandleFilesDropped(Frames);
        await vm.ValidationIdle;
        Assert.True(vm.State.CanRun);

        FrameChipVm newRef = vm.Frames[4];                             // DSC_7580
        vm.SetReferenceCommand.Execute(newRef);
        Assert.Same(newRef, vm.Frames[0]);
        Assert.True(newRef.IsReference);
        Assert.Equal(Frames[0], vm.Frames[4].Path);                    // old ★ took the swapped slot
        Assert.Equal(Frames[1], vm.Frames[1].Path);                    // the rest unchanged
        await vm.ValidationIdle;                                       // reference change re-validates (PTC base)

        CreatedTaskGroup? created = null;
        vm.CreateCompleted += (_, e) => created = e;
        Assert.True(vm.CreateCommand.CanExecute(null));
        vm.CreateCommand.Execute(null);

        Assert.NotNull(created);
        Assert.Equal(new[] { Frames[4], Frames[1], Frames[2], Frames[3], Frames[0], Frames[5], Frames[6] },
            created!.Group.FramePaths);
        Assert.Equal(Frames[4], created.Group.ReferencePath);
        Assert.True(created.Validation.CanRun);
    }

    [Fact]
    public async Task ValidGroup_PassLineDarkTagAndCreate()
    {
        var vm = Vm();
        vm.HandleFilesDropped(Frames);
        await vm.ValidationIdle;

        Assert.True(vm.State.CanRun);
        Assert.Equal(string.Empty, vm.State.IssuesText);
        Assert.Equal("✓ 7 个文件机型/ISO 一致 · PTC 匹配 Nikon Z8·ISO3200 → 满阱 1,102 e⁻（表值） · 黑场已按 ISO 自动匹配预置参数",
            vm.State.PtcInfoText);

        CreatedTaskGroup? created = null;
        vm.CreateCompleted += (_, e) => created = e;
        vm.CreateCommand.Execute(null);

        Assert.NotNull(created);
        Assert.Equal(Frames, created!.Group.FramePaths);
        Assert.True(created.Validation.CanRun);
        Assert.Equal(1102.2, created.Validation.Ptc!.FullwellE, 1);    // ISO 3200 exact table hit
    }

    [Fact]
    public void Cancel_RaisesCancelRequested()
    {
        var vm = Vm();
        int raised = 0;
        vm.CancelRequested += (_, _) => raised++;
        vm.CancelCommand.Execute(null);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task EmptyDialog_NoValidationLine_IgnoresNonNefDrop()
    {
        var vm = Vm();
        Assert.False(vm.State.CanRun);
        Assert.Equal(string.Empty, vm.State.IssuesText);               // 阶段一空面板：无校验行

        vm.HandleFilesDropped(new[] { @"C:\P\x.jpg", @"C:\P\y.nrW" }); // nothing usable → no-op
        await vm.ValidationIdle;
        Assert.Empty(vm.Frames);
        Assert.Equal(string.Empty, vm.State.IssuesText);
    }

    /// <summary>Two rapid drops: the first pass is cancelled mid-debounce, so its files are
    /// never read — the second set's validation is the only one that runs (300 ms 防抖).</summary>
    [Fact]
    public async Task RapidSecondDrop_SupersedesPendingValidation()
    {
        int reads = 0;
        Func<string, RawMetadata> read = _ => { reads++; return Meta(); };
        var vm = Vm(read, debounce: TimeSpan.FromMilliseconds(400));
        string[] setB = Frames.Select(p => p.Replace(@"C:\Photos", @"C:\Other")).ToArray();

        vm.HandleFilesDropped(Frames);
        await Task.Delay(50);
        vm.HandleFilesDropped(setB);
        await vm.ValidationIdle;

        Assert.Equal(setB, vm.Frames.Select(f => f.Path).ToArray());
        Assert.Equal(7, reads);                                        // set A cancelled: only set B was read
        Assert.True(vm.State.CanRun);                                  // no dark needed: preset auto-match
    }

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
        Assert.Equal(TaskState.Blocked, main.Tasks[1].State);          // defensive: Create is gated on CanRun
    }

    /// <summary>Integration-style: production LibRaw reader on the real samples
    /// (mockup v3 阶段二). All 7 frames are Nikon Z 8 at ISO 3200 → exact PTC hit.</summary>
    [Fact]
    public async Task RealSamples_ProductionReader_CanRun()
    {
        var vm = new NewTaskViewModel();                               // real reader, real 300 ms debounce
        int[] shuffled = { 7582, 7576, 7579, 7577, 7581, 7578, 7580 };
        vm.HandleFilesDropped(shuffled.Select(i => SampleFiles.FindSample(Path.Combine("test_frames", "FX", "Lossless", $"DSC_{i}.NEF"))));
        await vm.ValidationIdle;

        Assert.True(vm.State.CanRun);
        Assert.Equal(string.Empty, vm.State.IssuesText);
        Assert.StartsWith("✓ 7 个文件机型/ISO 一致 · PTC 匹配 Nikon Z8·ISO3200 → 满阱 1,102 e⁻（表值）",
            vm.State.PtcInfoText);
        Assert.All(vm.Frames, f => Assert.False(f.IsError));
        Assert.True(vm.CreateCommand.CanExecute(null));
    }
}
