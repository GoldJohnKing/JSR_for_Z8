using Jsr4Z8.Core.Dng;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// AsShotNeutral cross-check warning: the finalizer
/// compares the backend product's AsShotNeutral against 1/cam_mul derived from
/// the reference NEF via LibRaw and warn-logs deviations &gt; 1e-4 — never
/// fatal, the backend value carries. The warning path had zero coverage: a
/// regression that stops emitting it (or makes it throw) would silence the
/// only signal that a backend color change disagrees with the reference
/// frame's white balance.
/// </summary>
public class FinalizerCrossCheckWarnTests
{
    private const ushort TagAsShotNeutral = 50728;

    /// <summary>Injects a +0.05/component drift and asserts the REAL Finalize
    /// routes the warnings to the log callback while still producing the DNG.</summary>
    [Fact]
    public void DriftedAsShotNeutral_LogCallbackReceivesWarning_FinalizeStillSucceeds()
    {
        string fixture = FinalizerTests.FindFixture("roi_sample.dng");
        using var temp = new TestTemp();
        string dir = temp.NewDir();
        string drifted = Path.Combine(dir, "roi_sample_asn_drift.dng");
        PatchAsShotNeutralDrift(fixture, drifted, drift: 0.05);

        var warnings = new List<string>();
        string final = JsrDngFinalizer.Finalize(drifted, SampleFiles.FindSample(Path.Combine("test_frames","lossless","DSC_7576.NEF")),
            dir, log: warnings.Add);

        // Fusion succeeded despite the drift (never fatal)…
        Assert.True(File.Exists(final) && new FileInfo(final).Length > 1_000_000,
            "Finalize must still produce the product DNG");
        Assert.Equal("JSR_DSC_7576.dng", Path.GetFileName(final));

        // …and the log callback saw one warning per drifted component, naming
        // the 1e-4 threshold and the offending component index.
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("WARN AsShotNeutral[0]"));
        Assert.Contains(warnings, w => w.Contains("WARN AsShotNeutral[1]"));
        Assert.Contains(warnings, w => w.Contains("WARN AsShotNeutral[2]"));
        Assert.Contains("1e-4", warnings[0]);

        // The drifted (backend) value carries into the product, not the
        // cam_mul-derived one — the cross-check warns, it does not correct.
        byte[] file = File.ReadAllBytes(final);
        var ifd0 = TestTiff.ParseIfd(file, TestTiff.U32(file, 4));
        Assert.Equal(3u, ifd0.Tags[TagAsShotNeutral].Count);
        var asn = ifd0.Tags[TagAsShotNeutral].Value;
        Assert.InRange(TestTiff.U32(asn, 0) / 1e6, 0.560175 + 0.05 - 1e-5, 0.560175 + 0.05 + 1e-5);
        Assert.InRange(TestTiff.U32(asn, 8) / 1e6, 1.0 + 0.05 - 1e-6, 1.0 + 0.05 + 1e-6);
        Assert.InRange(TestTiff.U32(asn, 16) / 1e6, 0.557734 + 0.05 - 1e-5, 0.557734 + 0.05 + 1e-5);
    }

    [Fact]
    public void MatchingAsShotNeutral_NoWarningLogged()
    {
        // Control: the committed fixture's AsShotNeutral agrees with the
        // reference NEF's cam_mul (≈1e-6 rounding) — the cross-check must stay
        // silent, proving the drift test fires on drift, not unconditionally.
        using var temp = new TestTemp();
        string dir = temp.NewDir();
        var warnings = new List<string>();
        string final = JsrDngFinalizer.Finalize(FinalizerTests.FindFixture("roi_sample.dng"),
            SampleFiles.FindSample(Path.Combine("test_frames","lossless","DSC_7576.NEF")), dir, log: warnings.Add);
        Assert.True(File.Exists(final));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// Copies the fixture and injects an AsShotNeutral that drifts
    /// <paramref name="drift"/> per component from the reference cam_mul-derived
    /// values (fixture ASN = [0.560175, 1.0, 0.557734], denominators 10⁶).
    ///
    /// The fixture's 3-RATIONAL entry is REWRITTEN IN PLACE to Count=6 pointing at a
    /// 48-byte blob appended at the word-aligned EOF — six rationals, the drifted
    /// values twice. Count=6 because BackendDngReader's TIFF walker under-reads
    /// RATIONAL arrays (it takes cnt u32s, not 2·cnt), so a count-3 entry yields too
    /// few pairs and the reader reports AsShotNeutral = null (known product defect;
    /// the finalizer then falls back to 1/cam_mul, which coincides with the
    /// fixture's values). With Count=6 the current reader pairs the first six u32s
    /// (= the drifted values) and a future fixed reader takes the first three of its
    /// six pairs (= the same drifted values) — the injection survives the reader fix
    /// either way.
    /// </summary>
    private static void PatchAsShotNeutralDrift(string src, string dst, double drift)
    {
        byte[] file = File.ReadAllBytes(src);
        var entries = TestTiff.ParseIfdEntries(file, TestTiff.U32(file, 4), out _);
        var asn = entries.Single(e => e.Tag == TagAsShotNeutral);
        Assert.Equal(5u, asn.Type);                     // RATIONAL
        Assert.Equal(3u, asn.Count);
        Assert.True(asn.IsOutOfLine, "3 RATIONALs (24 B) must live out-of-line");
        for (int i = 0; i < 3; i++)
            Assert.Equal(1_000_000u, TestTiff.U32(asn.Data, i * 8 + 4));

        uint[] drifted =
        {
            TestTiff.U32(asn.Data, 0) + (uint)Math.Round(drift * 1e6),
            TestTiff.U32(asn.Data, 8) + (uint)Math.Round(drift * 1e6),
            TestTiff.U32(asn.Data, 16) + (uint)Math.Round(drift * 1e6),
        };

        long blob = (file.Length + 1) & ~1L;            // word-aligned EOF
        Array.Resize(ref file, (int)(blob + 48));
        for (int k = 0; k < 6; k++)                     // drifted values twice
        {
            uint num = drifted[k % 3], den = 1_000_000;
            file[blob + k * 8] = (byte)num;
            file[blob + k * 8 + 1] = (byte)(num >> 8);
            file[blob + k * 8 + 2] = (byte)(num >> 16);
            file[blob + k * 8 + 3] = (byte)(num >> 24);
            file[blob + k * 8 + 4] = (byte)den;
            file[blob + k * 8 + 5] = (byte)(den >> 8);
            file[blob + k * 8 + 6] = (byte)(den >> 16);
            file[blob + k * 8 + 7] = (byte)(den >> 24);
        }

        long entry = asn.EntryOffset;                    // rewrite Count + value offset in place
        file[entry + 4] = 6; file[entry + 5] = 0; file[entry + 6] = 0; file[entry + 7] = 0;
        file[entry + 8] = (byte)blob;
        file[entry + 9] = (byte)(blob >> 8);
        file[entry + 10] = (byte)(blob >> 16);
        file[entry + 11] = (byte)(blob >> 24);
        File.WriteAllBytes(dst, file);
    }
}
