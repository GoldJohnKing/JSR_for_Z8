using Jsr4Z8.Core;
using Jsr4Z8.Core.Model;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Task 5: TaskGroup + TaskGroupValidator — 8-file consistency validation.
/// Negative cases inject a mock reader via the Validate(TaskGroup, Func&lt;string,RawMetadata&gt;)
/// seam; the positive case reads the real samples DSC_7576..7582 + dark DSC_7575 with
/// RawMetadataReader (all Z 8 at ISO 3200 → exact PTC hit 1102.2 e⁻). Non-finite ISO is
/// rejected as NotFiniteIso before it can reach PtcTable.Lookup (Task 4 carried finding:
/// Lookup(NaN) would otherwise return a NaN fit value).
/// </summary>
public class ValidationTests
{
    private const string DarkName = "DSC_7575.NEF";
    private const double BaseIso = 3200.0;

    private static readonly string[] FrameNames =
    {
        "DSC_7576.NEF", "DSC_7577.NEF", "DSC_7578.NEF", "DSC_7579.NEF",
        "DSC_7580.NEF", "DSC_7581.NEF", "DSC_7582.NEF",
    };

    private static RawMetadata Meta(
        double iso = BaseIso,
        string make = "NIKON CORPORATION",
        string model = "NIKON Z 8") => new()
    {
        Make = make,
        Model = model,
        Iso = iso,
        BlackLevel = 1008,
        WhiteLevel = 16383,
        Width = 8280,
        Height = 5520,
        CamMul = new double[] { 1.785, 1.0, 1.793, 1.0 },
        CfaPattern = new byte[] { 0, 1, 3, 2 },
        ExposureTime = 0.01,
        Aperture = 4.0,
        DateTimeOriginal = "",
    };

    /// <summary>Mock reader: per-file overrides win, then the requested file (if any) throws,
    /// everything else returns the group default metadata.</summary>
    private static Func<string, RawMetadata> Reader(
        double iso = BaseIso,
        string make = "NIKON CORPORATION",
        string model = "NIKON Z 8",
        string? unreadable = null,
        Dictionary<string, RawMetadata>? perFile = null) => path =>
    {
        if (unreadable != null && path == unreadable)
            throw new RawReadException($"mock open failure (file={path})");
        if (perFile != null && perFile.TryGetValue(path, out RawMetadata? m))
            return m;
        return Meta(iso, make, model);
    };

    private static TaskGroup Group() => new(FrameNames, DarkName);

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
        string[] frames = Enumerable.Range(7576, 7).Select(i => SampleFiles.FindSample($"DSC_{i}.NEF")).ToArray();
        var g = new TaskGroup(frames, SampleFiles.FindSample("DSC_7575.NEF"));

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
        Assert.Equal("DSC_7580.NEF 的 ISO(800) 与基准帧(400) 不一致", issue.MessageZh);
        Assert.Null(r.Ptc);
    }

    [Fact]
    public void DarkIsoMismatch_IssueNamesDarkFile()
    {
        var perFile = new Dictionary<string, RawMetadata> { [DarkName] = Meta(iso: 1600) };

        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(perFile: perFile));

        Assert.False(r.CanRun);
        ValidationIssue issue = Assert.Single(r.Issues);
        Assert.Equal(IssueKind.IsoMismatch, issue.Kind);
        Assert.Equal(DarkName, issue.File);
        Assert.Contains("DSC_7575.NEF", issue.MessageZh);
    }

    [Fact]
    public void NonZ8Model_EveryFileFlaggedNotZ8()
    {
        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(model: "NIKON Z 6_3"));

        Assert.False(r.CanRun);
        Assert.All(r.Issues, i => Assert.Equal(IssueKind.NotZ8, i.Kind));
        Assert.Equal(8, r.Issues.Count); // 7 frames + dark
        Assert.All(r.Issues, i => Assert.Contains("仅支持 Nikon Z8", i.MessageZh));
        Assert.Contains(r.Issues, i => i.File == FrameNames[0] && i.MessageZh.Contains(FrameNames[0]));
    }

    [Fact]
    public void NonNikonMake_CameraMismatchInsteadOfNotZ8()
    {
        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(make: "Canon Inc.", model: "Canon EOS R5"));

        Assert.False(r.CanRun);
        Assert.All(r.Issues, i => Assert.Equal(IssueKind.CameraMismatch, i.Kind));
        Assert.Equal(8, r.Issues.Count);
        Assert.All(r.Issues, i => Assert.Contains("仅支持 Nikon Z8", i.MessageZh));
        Assert.DoesNotContain(r.Issues, i => i.Kind == IssueKind.NotZ8);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void FrameCount_SixOrEightFlagsFrameCount(int count)
    {
        string[] frames = FrameNames.Concat(new[] { "DSC_7583.NEF" }).Take(count).ToArray();

        ValidationResult r = TaskGroupValidator.Validate(new TaskGroup(frames, DarkName), Reader());

        Assert.False(r.CanRun);
        ValidationIssue issue = Assert.Single(r.Issues);
        Assert.Equal(IssueKind.FrameCount, issue.Kind);
        Assert.Contains("7", issue.MessageZh);
        Assert.Contains(count.ToString(), issue.MessageZh);
    }

    [Fact]
    public void UnreadableFrame_IssueNamesFile()
    {
        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(unreadable: FrameNames[3]));

        Assert.False(r.CanRun);
        ValidationIssue issue = Assert.Single(r.Issues);
        Assert.Equal(IssueKind.Unreadable, issue.Kind);
        Assert.Equal(FrameNames[3], issue.File);
        Assert.Contains("DSC_7579.NEF", issue.MessageZh);
    }

    [Fact]
    public void UnreadableReferenceFrame_NoPtcCascade_JustUnreadable()
    {
        // The reference frame itself unreadable: only the Unreadable issue — no ISO
        // comparison base, no PTC lookup, and (regression) no NullReferenceException
        // from a default tuple that the empty pattern would wrongly match.
        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(unreadable: FrameNames[0]));

        Assert.False(r.CanRun);
        ValidationIssue issue = Assert.Single(r.Issues);
        Assert.Equal(IssueKind.Unreadable, issue.Kind);
        Assert.Equal(FrameNames[0], issue.File);
        Assert.Null(r.Ptc);
    }

    [Fact]
    public void MissingDarkPath_FlaggedAsUnreadable()
    {
        ValidationResult r = TaskGroupValidator.Validate(new TaskGroup(FrameNames, ""), Reader());

        Assert.False(r.CanRun);
        ValidationIssue issue = Assert.Single(r.Issues);
        Assert.Equal(IssueKind.Unreadable, issue.Kind);
        Assert.Contains("黑场", issue.MessageZh);
    }

    [Fact]
    public void PtcUnavailable_WhenReferenceIsoOutsideTableBounds()
    {
        ValidationResult r = TaskGroupValidator.Validate(Group(), Reader(iso: 49));

        Assert.False(r.CanRun);
        ValidationIssue issue = Assert.Single(r.Issues);
        Assert.Equal(IssueKind.PtcUnavailable, issue.Kind);
        Assert.Equal(FrameNames[0], issue.File); // the reference frame defines the base ISO
        Assert.Contains("50", issue.MessageZh);
        Assert.Null(r.Ptc);
    }

    [Fact]
    public void NonFiniteIso_RejectedBeforePtcLookup()
    {
        // Reference frame NaN: must be NotFiniteIso, and the NaN must NOT reach
        // PtcTable.Lookup (Task 4 carried finding: Lookup(NaN) returns a NaN fit).
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
}
