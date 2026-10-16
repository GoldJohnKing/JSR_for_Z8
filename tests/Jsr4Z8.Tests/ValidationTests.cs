using Jsr4Z8.Core;
using Jsr4Z8.Core.Dark;

using static Jsr4Z8.Tests.MockMeta;
using Jsr4Z8.Core.Model;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// TaskGroup + TaskGroupValidator — 8-file consistency validation.
/// Negative cases inject a mock reader via the Validate(TaskGroup, Func&lt;string,RawMetadata&gt;)
/// seam; the positive case reads the real sample frames DSC_7576..7582 with
/// RawMetadataReader (all Z 8 at ISO 3200 → exact PTC hit 1102.2 e⁻). Non-finite ISO is
/// rejected as NotFiniteIso before it can reach PtcTable.Lookup
/// (Lookup(NaN) would otherwise return a NaN fit value).
/// </summary>
public class ValidationTests
{
    private static readonly string[] FrameNames =
    {
        "DSC_7576.NEF", "DSC_7577.NEF", "DSC_7578.NEF", "DSC_7579.NEF",
        "DSC_7580.NEF", "DSC_7581.NEF", "DSC_7582.NEF",
    };

    private static TaskGroup Group() => new(FrameNames);

    [Fact]
    public void MockGroup_AllZ8Iso3200_CanRunWithExactPtc()
    {
        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader());

        Assert.True(r.CanRun);
        Assert.Empty(r.Issues);
        Assert.NotNull(r.Ptc);
        Assert.True(r.Ptc.Exact);
        Assert.Equal("table", r.Ptc.Source);
        Assert.Equal(1102.2, r.Ptc.FullwellE, 1); // 1/0.0009072403603921569 at ISO 3200
    }

    [Fact]
    public void RealSamples_CanRunWithExactPtc()
    {
        string[] frames = Enumerable.Range(7576, 7).Select(i => SampleFiles.FindSample(Path.Combine("test_frames", "lossless", $"DSC_{i}.NEF"))).ToArray();
        var g = new TaskGroup(frames);

        ValidationResult r = TaskGroupValidator.Validate(g);

        Assert.True(r.CanRun);
        Assert.Empty(r.Issues);
        Assert.NotNull(r.Ptc);
        Assert.True(r.Ptc.Exact);
        Assert.Equal(1102.2, r.Ptc.FullwellE, 1);
    }

    [Fact]
    public void IsoMismatch_IssueNamesFileAndBothIsos()
    {
        var perFile = new Dictionary<string, RawMetadata> { [FrameNames[4]] = Meta(iso: 800) };

        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(iso: 400, perFile: perFile));

        Assert.False(r.CanRun);
        ValidationIssue issue = Assert.Single(r.Issues);
        Assert.Equal(IssueKind.IsoMismatch, issue.Kind);
        Assert.Equal(FrameNames[4], issue.File);
        Assert.Equal("DSC_7580.NEF：非同一 ISO 拍摄：ISO 800（基准帧 400）", issue.MessageZh);
        Assert.Null(r.Ptc);
    }


    [Fact]
    public void NonZ8Model_EveryFileFlaggedNotZ8()
    {
        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(model: "NIKON Z 6_3"));

        Assert.False(r.CanRun);
        Assert.All(r.Issues, i => Assert.Equal(IssueKind.NotZ8, i.Kind));
        Assert.Equal(7, r.Issues.Count); // 7 frames + dark
        Assert.All(r.Issues, i => Assert.Contains("不受支持的机型：NIKON Z 6_3", i.MessageZh));
        Assert.All(r.Issues, i => Assert.Contains("仅支持 Nikon Z8", i.MessageZh));
        Assert.Contains(r.Issues, i => i.File == FrameNames[0] && i.MessageZh.Contains(FrameNames[0]));
    }

    [Fact]
    public void MixedModels_FrameDiffersFromReference_NonSameModelMessage()
    {
        // 机型一致性维度：与基准帧机型不同的非 Z8 帧 → 非同一机型拍摄（区别于
        // 全组同机型的 不受支持的机型）。基准帧仍为 Z8，无其他 issue。
        var mixed = new Dictionary<string, RawMetadata> { [FrameNames[3]] = Meta(model: "NIKON Z 6_3") };
        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(perFile: mixed));

        Assert.False(r.CanRun);
        ValidationIssue issue = Assert.Single(r.Issues);
        Assert.Equal(IssueKind.NotZ8, issue.Kind);
        Assert.Equal(FrameNames[3], issue.File);
        Assert.Contains("非同一机型拍摄", issue.MessageZh);
        Assert.Contains("Z 6_3", issue.MessageZh);   // 该帧机型
        Assert.Contains("Z 8", issue.MessageZh);     // 基准帧机型
    }

    [Fact]
    public void NonNikonMake_CameraMismatchInsteadOfNotZ8()
    {
        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(make: "Canon Inc.", model: "Canon EOS R5"));

        Assert.False(r.CanRun);
        Assert.All(r.Issues, i => Assert.Equal(IssueKind.CameraMismatch, i.Kind));
        Assert.Equal(7, r.Issues.Count);
        Assert.All(r.Issues, i => Assert.Contains("不受支持的机型：Canon Inc. Canon EOS R5", i.MessageZh));
        Assert.DoesNotContain(r.Issues, i => i.Kind == IssueKind.NotZ8);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void FrameCount_SixOrEightFlagsFrameCount(int count)
    {
        string[] frames = FrameNames.Concat(new[] { "DSC_7583.NEF" }).Take(count).ToArray();

        ValidationResult r = TaskGroupValidator.Validate(new TaskGroup(frames), Reader());

        Assert.False(r.CanRun);
        ValidationIssue issue = Assert.Single(r.Issues);
        Assert.Equal(IssueKind.FrameCount, issue.Kind);
        Assert.Contains("7", issue.MessageZh);
        Assert.Contains(count.ToString(), issue.MessageZh);
    }

    [Fact]
    public void UnreadableFrame_IssueNamesFile()
    {
        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(unreadable: new HashSet<string> { FrameNames[3] }));

        Assert.False(r.CanRun);
        ValidationIssue issue = Assert.Single(r.Issues);
        Assert.Equal(IssueKind.Unreadable, issue.Kind);
        Assert.Equal(FrameNames[3], issue.File);
        Assert.Contains("DSC_7579.NEF", issue.MessageZh);
        Assert.Contains("无法读取", issue.MessageZh);
    }

    [Fact]
    public void UnreadableReferenceFrame_NoPtcCascade_JustUnreadable()
    {
        // The reference frame itself unreadable: only the Unreadable issue — no ISO
        // comparison base, no PTC lookup, and (regression) no NullReferenceException
        // from a default tuple that the empty pattern would wrongly match.
        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(unreadable: new HashSet<string> { FrameNames[0] }));

        Assert.False(r.CanRun);
        ValidationIssue issue = Assert.Single(r.Issues);
        Assert.Equal(IssueKind.Unreadable, issue.Kind);
        Assert.Equal(FrameNames[0], issue.File);
        Assert.Null(r.Ptc);
    }

    [Fact]
    public void PresetTable_IsoInTable_CanRunWithDarkMatch()
    {
        // Base ISO 3200 is a preset row: the group is runnable with no dark file and
        // the validation carries the manual calibration pair for the request builder.
        ValidationResult r = TaskGroupValidator.Validate(new TaskGroup(FrameNames), Reader());

        Assert.True(r.CanRun, string.Join("\n", r.Issues.Select(i => i.MessageZh)));
        Assert.Empty(r.Issues);
        Assert.NotNull(r.Ptc);
        Assert.NotNull(r.Dark);
        Assert.Equal(3200d, r.Dark!.Iso);
        Assert.True(r.Dark.BlackDn > 0);
        Assert.True(r.Dark.ReadNoiseDn > 0);
    }

    [Fact]
    public void OffTableIso_FlaggedCaptureFormatUnsupported()
    {
        // ISO 51.5 sits inside the PTC bounds [50, 102400] (fit works) but has no
        // preset dark row — unsupported.
        ValidationResult r = TaskGroupValidator.Validate(new TaskGroup(FrameNames), Reader(iso: 51.5));

        Assert.False(r.CanRun);
        ValidationIssue issue = Assert.Single(r.Issues);
        Assert.Equal(IssueKind.CaptureFormatUnsupported, issue.Kind);
        Assert.Equal(FrameNames[0], issue.File);
        Assert.Contains("不受支持的 ISO：51.5", issue.MessageZh);
        Assert.Null(r.Ptc); // Ptc is non-null iff CanRun (validator contract), even though the ISO itself fits
        Assert.Null(r.Dark);
    }

    [Fact]
    public void TwelveBitReadout_FlaggedCaptureFormatUnsupported()
    {
        // 12-bit readout: unsupported — no preset rows (PTC itself is per-pixel,
        // keyed on model+ISO only).
        var twelveBit = new Dictionary<string, RawMetadata>();
        foreach (string name in FrameNames)
            twelveBit[name] = Meta() with { BitsPerSample = 12, WhiteLevel = 4095 };
        ValidationResult r = TaskGroupValidator.Validate(new TaskGroup(FrameNames), Reader(perFile: twelveBit));

        Assert.False(r.CanRun);
        ValidationIssue issue = Assert.Single(r.Issues);
        Assert.Equal(IssueKind.CaptureFormatUnsupported, issue.Kind);
        Assert.Contains("不受支持的拍摄格式", issue.MessageZh);
        Assert.Contains("12bit", issue.MessageZh);
    }



    [Fact]
    public void HeUniformGroup_AutoMatchesHePresetRow()
    {
        // HE carries its own preset rows (lossy coding adds +3..8% read noise —
        // not interchangeable with lossless rows): a uniform HE group auto-matches
        // by (13, ISO).
        var he = new Dictionary<string, RawMetadata>();
        foreach (string name in FrameNames)
            he[name] = Meta() with { NefCompression = 13 };
        ValidationResult r = TaskGroupValidator.Validate(new TaskGroup(FrameNames), Reader(perFile: he));

        Assert.True(r.CanRun, string.Join("\n", r.Issues.Select(i => i.MessageZh)));
        Assert.NotNull(r.Ptc);   // per-pixel: model+ISO, compression-independent
        Assert.NotNull(r.Dark);  // the HE row, not the lossless one
        DarkMatch heRow = DarkTable.LoadDefault().Lookup(3200, 13)!;
        Assert.Equal(heRow.BlackDn, r.Dark!.BlackDn, 3);
        Assert.Equal(heRow.ReadNoiseDn, r.Dark.ReadNoiseDn, 3);
    }

    [Fact]
    public void HeStarUniformGroup_AutoMatchesHeStarPresetRow()
    {
        // HE* (14) darks were measured into their own preset rows (2026-10-14
        // measurement; the dark sample files were since retired — rows live in the
        // preset table): close to HE but not interchangeable at low ISO
        // — a uniform HE* group auto-matches by (14, ISO) without a dark file.
        var heStar = new Dictionary<string, RawMetadata>();
        foreach (string name in FrameNames)
            heStar[name] = Meta() with { NefCompression = 14 };
        ValidationResult r = TaskGroupValidator.Validate(new TaskGroup(FrameNames), Reader(perFile: heStar));

        Assert.True(r.CanRun, string.Join("\n", r.Issues.Select(i => i.MessageZh)));
        Assert.NotNull(r.Ptc);
        Assert.NotNull(r.Dark);
        DarkMatch heStarRow = DarkTable.LoadDefault().Lookup(3200, 14)!;
        Assert.Equal(heStarRow.BlackDn, r.Dark!.BlackDn, 3);
        Assert.Equal(heStarRow.ReadNoiseDn, r.Dark.ReadNoiseDn, 3);
    }

    [Fact]
    public void DxHeStarGroup_CropBlind_MatchesFxHeStarRow()
    {
        // Crop is not a matching key: a uniform DX HE* group takes the
        // FX-measured HE* row (crop-independent read noise).
        var dxHeStar = new Dictionary<string, RawMetadata>();
        foreach (string name in FrameNames)
            dxHeStar[name] = Meta() with { NefCompression = 14, Width = 5408, Height = 3608 };
        ValidationResult r = TaskGroupValidator.Validate(new TaskGroup(FrameNames), Reader(perFile: dxHeStar));

        Assert.True(r.CanRun, string.Join("\n", r.Issues.Select(i => i.MessageZh)));
        Assert.NotNull(r.Ptc);
        Assert.NotNull(r.Dark);
        DarkMatch fxRow = DarkTable.LoadDefault().Lookup(3200, 14)!;
        Assert.Equal(fxRow.BlackDn, r.Dark!.BlackDn, 3);
        Assert.Equal(fxRow.ReadNoiseDn, r.Dark.ReadNoiseDn, 3);
    }

    [Theory]
    [InlineData(5408, 3608)]  // DX 1.5× crop — crop-blind matching (2026-10-15)
    [InlineData(8280, 4656)]  // 16:9 crop — full FX width, short window
    [InlineData(6900, 5520)]  // 5:4 crop — full FX height, narrow window
    public void AnyUniformCropWindow_RunsOnFxRows(int width, int height)
    {
        // Crop is not a matching key; frames still must be pairwise
        // crop-consistent, but the preset lookup accepts any window.
        var cropped = new Dictionary<string, RawMetadata>();
        foreach (string name in FrameNames)
            cropped[name] = Meta() with { Width = width, Height = height };
        ValidationResult r = TaskGroupValidator.Validate(new TaskGroup(FrameNames), Reader(perFile: cropped));

        Assert.True(r.CanRun, string.Join("\n", r.Issues.Select(i => i.MessageZh)));
        Assert.NotNull(r.Dark);
    }


    [Fact]
    public void DxLosslessGroup_CropBlind_MatchesFxLosslessRow()
    {
        // Crop-blind preset matching: a uniform DX group takes the FX lossless row
        // (read noise is crop-independent).
        var dx = new Dictionary<string, RawMetadata>();
        foreach (string name in FrameNames)
            dx[name] = Meta() with { Width = 5408, Height = 3608 };
        ValidationResult r = TaskGroupValidator.Validate(new TaskGroup(FrameNames), Reader(perFile: dx));

        Assert.True(r.CanRun, string.Join("\n", r.Issues.Select(i => i.MessageZh)));
        Assert.NotNull(r.Ptc);   // per-pixel: model+ISO, crop-independent
        Assert.NotNull(r.Dark);
        DarkMatch fxRow = DarkTable.LoadDefault().Lookup(3200, 3)!;
        Assert.Equal(fxRow.BlackDn, r.Dark!.BlackDn, 3);
        Assert.Equal(fxRow.ReadNoiseDn, r.Dark.ReadNoiseDn, 3);
    }

    [Fact]
    public void MixedGroup_OneTwelveBitFrame_FlaggedBitDepthMismatch()
    {
        // Pairwise five-dimension consistency: one 12-bit frame inside an otherwise
        // supported group names the offending file and blocks the run.
        var mixed = new Dictionary<string, RawMetadata> { [FrameNames[3]] = Meta() with { BitsPerSample = 12 } };
        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(perFile: mixed));

        Assert.False(r.CanRun);
        ValidationIssue issue = Assert.Single(r.Issues);
        Assert.Equal(IssueKind.BitDepthMismatch, issue.Kind);
        Assert.Equal(FrameNames[3], issue.File);
        Assert.Contains("非同一位深拍摄", issue.MessageZh);
        Assert.Contains("12bit", issue.MessageZh);
    }

    [Fact]
    public void MixedGroup_OneDxFrame_FlaggedCropMismatch()
    {
        var mixed = new Dictionary<string, RawMetadata> { [FrameNames[2]] = Meta() with { Width = 5488, Height = 3712 } };
        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(perFile: mixed));

        Assert.False(r.CanRun);
        ValidationIssue issue = Assert.Single(r.Issues);
        Assert.Equal(IssueKind.CropMismatch, issue.Kind);
        Assert.Equal(FrameNames[2], issue.File);
        Assert.Contains("图像尺寸不同", issue.MessageZh);
        Assert.Contains("5488×3712", issue.MessageZh);
    }

    [Fact]
    public void MixedGroup_OneHeFrame_FlaggedCompressionMismatch()
    {
        // One HE frame inside a lossless group: same TIFF code (34713), different
        // NEFCompression — the pairwise check must catch it and name the file.
        var mixed = new Dictionary<string, RawMetadata> { [FrameNames[5]] = Meta() with { NefCompression = 13 } };
        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(perFile: mixed));

        Assert.False(r.CanRun);
        ValidationIssue issue = Assert.Single(r.Issues);
        Assert.Equal(IssueKind.CompressionMismatch, issue.Kind);
        Assert.Equal(FrameNames[5], issue.File);
        Assert.Contains("非同一 RAW 压缩拍摄", issue.MessageZh);
        Assert.Contains("HE", issue.MessageZh);
    }


    [Fact]
    public void PtcUnavailable_WhenReferenceIsoOutsideTableBounds()
    {
        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(iso: 49));

        Assert.False(r.CanRun);
        // Two honest findings: the ISO is outside the PTC bounds AND outside the
        // preset dark table (no manual-dark fallback exists anymore).
        ValidationIssue issue = Assert.Single(r.Issues, i => i.Kind == IssueKind.PtcUnavailable);
        Assert.Equal(FrameNames[0], issue.File); // the reference frame defines the base ISO
        Assert.Contains("不受支持的 ISO：49", issue.MessageZh);
        Assert.Null(r.Ptc);
    }

    [Fact]
    public void NonFiniteIso_RejectedBeforePtcLookup()
    {
        // Reference frame NaN: must be NotFiniteIso, and the NaN must NOT reach
        // PtcTable.Lookup (Lookup(NaN) returns a NaN fit).
        var refNaN = new Dictionary<string, RawMetadata> { [FrameNames[0]] = Meta(iso: double.NaN) };
        ValidationResult rNaN = TaskGroupValidator.Validate(Group(), Reader(perFile: refNaN));
        Assert.False(rNaN.CanRun);
        ValidationIssue nanIssue = Assert.Single(rNaN.Issues);
        Assert.Equal(IssueKind.NotFiniteIso, nanIssue.Kind);
        Assert.Contains("ISO 值无效", nanIssue.MessageZh);
        Assert.Null(rNaN.Ptc);

        // +∞ on a non-reference frame: still a per-file NotFiniteIso, not an IsoMismatch.
        var inf = new Dictionary<string, RawMetadata> { [FrameNames[5]] = Meta(iso: double.PositiveInfinity) };
        ValidationResult rInf = TaskGroupValidator.Validate(Group(), Reader(perFile: inf));
        Assert.False(rInf.CanRun);
        ValidationIssue infIssue = Assert.Single(rInf.Issues);
        Assert.Equal(IssueKind.NotFiniteIso, infIssue.Kind);
        Assert.Equal(FrameNames[5], infIssue.File);
    }

    [Fact]
    public void NormalizeModel_CanonicalizesVendorCaseAndSpaces()
    {
        // "NIKON Z 8" == "Z8" == "Z 8" — all canonicalize to the same form.
        string z8Full = TaskGroupValidator.NormalizeModel("NIKON CORPORATION", "NIKON Z 8");
        Assert.Equal(z8Full, TaskGroupValidator.NormalizeModel("NIKON CORPORATION", "Z8"));
        Assert.Equal(z8Full, TaskGroupValidator.NormalizeModel("NIKON CORPORATION", "Z 8"));
        Assert.Equal(z8Full, TaskGroupValidator.NormalizeModel("Nikon Corporation", "nikon  z8"));
        Assert.Equal("z8", z8Full);

        // "NIKON Z 6_3" normalizes to its own form and fails the Z8 comparison.
        Assert.Equal("z6_3", TaskGroupValidator.NormalizeModel("NIKON CORPORATION", "NIKON Z 6_3"));
        Assert.NotEqual(z8Full, TaskGroupValidator.NormalizeModel("NIKON CORPORATION", "NIKON Z 6_3"));
    }

    [Fact]
    public void BracketedShutterSevenFrames_CanRun()
    {
        // 需求 1（校验侧）：快门不在一致性维度内 — 七帧包围曝光（1/1000..1/8000
        // 半档递进）只要机型/ISO/位深/压缩/画幅 五维一致即可全部运行，无任何 issue。
        double[] shutters =
        {
            0.001, 0.001 / Math.Sqrt(2), 0.0005, 0.0005 / Math.Sqrt(2),
            0.00025, 0.00025 / Math.Sqrt(2), 0.000125,
        };
        var bracketed = new Dictionary<string, RawMetadata>();
        for (int i = 0; i < FrameNames.Length; i++)
            bracketed[FrameNames[i]] = Meta() with { ExposureTime = shutters[i] };

        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(perFile: bracketed));

        Assert.True(r.CanRun, string.Join("\n", r.Issues.Select(i => i.MessageZh)));
        Assert.Empty(r.Issues);
        Assert.NotNull(r.Ptc);
    }
}
