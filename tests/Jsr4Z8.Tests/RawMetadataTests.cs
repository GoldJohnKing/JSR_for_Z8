using Jsr4Z8.Core;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Task 3: RawMetadataReader (LibRaw-backed metadata) and NefAssetExtractor
/// (ported Save_Z8 TiffIfdExtractor) against samples/DSC_7576.NEF.
/// Facts measured 2026-10-13 on that file (LibRaw raw_width/raw_height are
/// the padded sensor dims 8280×5520, not the cropped 8256×5504 the plan
/// estimated; WhiteLevel 16383 here equals the declared 14-bit maximum).
/// </summary>
public class RawMetadataTests
{
    [Fact]
    public void Read_HiIsoDarkFrame_LibRawExpandsExif65535To102400()
    {
        // The preset dark table keys the Hi step as 102400: EXIF writes 65535,
        // LibRaw reports the expansion.
        RawMetadata m = RawMetadataReader.Read(FindSample(Path.Combine("dark_frames", "FX", "Lossless", "DSC_7642.NEF")));

        Assert.Equal(102400d, m.Iso);
        Assert.Equal(14, m.BitsPerSample);      // TIFF BitsPerSample of the raw strip
        Assert.Equal(34713, m.Compression);     // TIFF Compression: the NEF family code
        Assert.Equal(3, m.NefCompression);      // MakerNote NEFCompression: lossless
        Assert.Equal(16383, m.WhiteLevel); // 14-bit full-frame readout, like every preset row
    }

    [Fact]
    public void Read_HeDarkFrame_TiffCompressionCannotDiscriminate()
    {
        // HE NEFs carry the SAME TIFF Compression (34713) and BitsPerSample (14)
        // as lossless — only the MakerNote NEFCompression field (13 = HE,
        // 14 = HE*) tells them apart; the preset gate relies on it.
        RawMetadata m = RawMetadataReader.Read(FindSample(Path.Combine("dark_frames", "FX", "HE", "DSC_7662.NEF")));

        Assert.Equal(64d, m.Iso);
        Assert.Equal(14, m.BitsPerSample);
        Assert.Equal(34713, m.Compression);     // identical to lossless — the trap
        Assert.Equal(13, m.NefCompression);     // High Efficiency
        Assert.Equal(1008, m.BlackLevel);
        Assert.Equal(16383, m.WhiteLevel);
        Assert.Equal(8280, m.Width);
        Assert.Equal(5520, m.Height);
    }

    [Fact]
    public void Read_HeStarDarkFrame_NefCompression14_SameTiffCode()
    {
        // HE*: same TIFF Compression (34713) and 14-bit shape as lossless and
        // HE — NEFCompression (14) is the only discriminator.
        RawMetadata m = RawMetadataReader.Read(FindSample(Path.Combine("dark_frames", "FX", "HE Star", "DSC_7696.NEF")));

        Assert.Equal(64d, m.Iso);
        Assert.Equal(14, m.BitsPerSample);
        Assert.Equal(34713, m.Compression);
        Assert.Equal(14, m.NefCompression);     // High Efficiency*
        Assert.Equal(1008, m.BlackLevel);
        Assert.Equal(16383, m.WhiteLevel);
        Assert.Equal(8280, m.Width);
        Assert.Equal(5520, m.Height);
    }


    [Fact]
    public void Read_SampleNef()
    {
        RawMetadata m = RawMetadataReader.Read(FindSample(Path.Combine("test_frames","FX","Lossless","DSC_7576.NEF")));

        Assert.Equal("NIKON CORPORATION", m.Make);
        Assert.Equal("NIKON Z 8", m.Model);
        Assert.Equal(3200d, m.Iso);
        Assert.Equal(14, m.BitsPerSample);
        Assert.Equal(34713, m.Compression);
        Assert.Equal(3, m.NefCompression);
        Assert.Equal(1008, m.BlackLevel);
        Assert.Equal(16383, m.WhiteLevel);
        Assert.Equal(8280, m.Width);
        Assert.Equal(5520, m.Height);

        // As-shot WB multipliers: G normalized to 1, R/B > 1, so the DNG
        // AsShotNeutral (= 1/cam_mul) is [0.560175, 1.0, 0.557734, ...].
        Assert.Equal(4, m.CamMul.Length);
        Assert.Equal(1.0, m.CamMul[1], 5);
        Assert.True(m.CamMul[0] > 1.0 && m.CamMul[2] > 1.0,
            $"cam_mul R/B expected >1, got [{string.Join(", ", m.CamMul)}]");

        // LibRaw 2×2 CFA filter codes: 0,1 / 3,2 = RGGB.
        Assert.Equal(new byte[] { 0, 1, 3, 2 }, m.CfaPattern);

        // EXIF capture info carried from the NEF's EXIF IFD.
        Assert.Equal(0.010, m.ExposureTime, 6);   // 10/1000 s
        Assert.Equal(4.0, m.Aperture, 3);         // 400/100 = f/4.0
        Assert.Equal("2026:10:08 21:52:23", m.DateTimeOriginal);
    }

    [Fact]
    public void Extract_SampleNef_HasExifPreviewsNefInfo()
    {
        NefAssets a = NefAssetExtractor.Extract(FindSample(Path.Combine("test_frames","FX","Lossless","DSC_7576.NEF")));

        // EXIF source block covers IFD0 through the EXIF IFD, so it contains
        // the Make string data.
        Assert.NotEmpty(a.ExifBlock);
        Assert.Contains("NIKON CORPORATION", System.Text.Encoding.ASCII.GetString(a.ExifBlock));

        // Z8 NEFs embed three JPEG previews (thumbnail, medium, full-size);
        // surface exposes at most three, smallest first.
        Assert.InRange(a.Previews.Length, 2, 3);
        Assert.True(a.Previews[0].Jpeg.Length <= a.Previews[^1].Jpeg.Length,
            "previews must be sorted smallest-first");
        foreach (PreviewImage p in a.Previews)
        {
            Assert.True(p.W > 0 && p.H > 0, $"preview dims {p.W}×{p.H}");
            Assert.True(p.Jpeg.Length > 4 && p.Jpeg[0] == 0xFF && p.Jpeg[1] == 0xD8,
                "preview must be a JPEG (SOI)");
        }

        Assert.NotEmpty(a.NefInfo);
    }

    [Fact]
    public void Read_TruncatedNef_Throws()
    {
        string src = FindSample(Path.Combine("test_frames","FX","Lossless","DSC_7576.NEF"));
        string dir = Path.Combine(Path.GetTempPath(), "jsr4z8-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string truncated = Path.Combine(dir, "DSC_7576_truncated.NEF");
            File.Copy(src, truncated);
            using (FileStream fs = File.Open(truncated, FileMode.Open, FileAccess.Write))
                fs.SetLength(64 * 1024); // keep only the first 64KB

            Assert.Throws<RawReadException>(() => RawMetadataReader.Read(truncated));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Read_MissingOrNonNef_Throws()
    {
        string missing = Path.Combine(Path.GetTempPath(), "jsr4z8-missing-" + Guid.NewGuid().ToString("N") + ".NEF");
        Assert.Throws<RawReadException>(() => RawMetadataReader.Read(missing));

        string notRaw = Path.Combine(Path.GetTempPath(), "jsr4z8-not-nef-" + Guid.NewGuid().ToString("N") + ".NEF");
        File.WriteAllText(notRaw, "this is not a raw file");
        try
        {
            Assert.Throws<RawReadException>(() => RawMetadataReader.Read(notRaw));
        }
        finally
        {
            File.Delete(notRaw);
        }
    }

    /// <summary>Locates samples\&lt;name&gt; by walking up from the test output directory to the repo root.</summary>
    private static string FindSample(string name) => SampleFiles.FindSample(name);
}
