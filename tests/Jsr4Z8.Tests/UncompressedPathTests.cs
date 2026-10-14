using Jsr4Z8.Core;
using Jsr4Z8.Core.Dng;
using Jsr4Z8.Core.Native;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Uncompressed output path (DngWriteOptions.Compress=false, Compression=1):
/// Save_Z8/ADC store the CFA payload in a ×4 16-bit domain — sample values
/// shifted x&lt;&lt;2 with BlackLevel 4032 / WhiteLevel 63568 (1008×4 / 15892×4),
/// one full-height strip, no LJ anywhere (DngComposer's documented domain
/// policy; "两套域等价，勿混用"). The compressed path has round-trip coverage;
/// the uncompressed path had none — a regression here (wrong shift, wrong
/// BL/WL pairing, strip-tag mistakes) would only surface as an unreadable
/// export in a diagnostic build. Round-trips through BackendDngReader, an
/// independent-lineage TIFF reader, plus a LibRaw handle-level open.
/// </summary>
public class UncompressedPathTests
{
    /// <summary>One uncompressed CFA conversion per test session (full 8280×5520
    /// payload, ~91 MB written) — reuses the NEF decode the writer path uses.</summary>
    private static readonly Lazy<(string Dng, ushort[] Source)> UncompressedCfa = new(() =>
    {
        string nef = SampleFiles.FindSample(Path.Combine("test_frames", "FX", "Lossless", "DSC_7576.NEF"));
        var (src, w, h, _, _, cfa, _) = LibRawNative.Decode(nef);
        var payload = new RawPayload
        {
            Data = src,
            W = w,
            H = h,
            Samples = 1,
            IsCfa = true,
            // LibRaw 2×2 filter codes (0=R, 1/3=G, 2=B) → phase chars — the same
            // mapping NefToDngConverter applies before DngComposer.
            CfaPattern = new string(Array.ConvertAll(cfa, c => c switch { 0 => 'R', 2 => 'B', _ => 'G' })),
        };
        string dir = Path.Combine(Path.GetTempPath(), "jsr4z8-uncompressedtests");
        try { Directory.Delete(dir, recursive: true); } catch { /* stale run */ }
        Directory.CreateDirectory(dir);
        string dng = Path.Combine(dir, "DSC_7576_uncompressed.dng");
        DngComposer.Compose(payload, RawMetadataReader.Read(nef), NefAssetExtractor.Extract(nef),
            dng, compress: false);
        return (dng, src);
    });

    [Fact]
    public void CfaUncompressed_Compression1_X4DomainTags_SingleFullHeightStrip()
    {
        var (dng, _) = UncompressedCfa.Value;
        byte[] file = File.ReadAllBytes(dng);
        Assert.Equal(0x49, file[0]); Assert.Equal(0x49, file[1]);
        Assert.Equal(42, TestTiff.U16(file, 2));

        var ifd0 = TestTiff.ParseIfd(file, TestTiff.U32(file, 4));
        var raw = TestTiff.ParseIfd(file, TestTiff.U32(ifd0.Tags[330].Value, 0));    // raw first SubIFD

        Assert.Equal(1, TestTiff.U16(raw.Tags[259].Value, 0));      // Compression = 1 (uncompressed)
        Assert.Equal(32803, TestTiff.U16(raw.Tags[262].Value, 0));  // still CFA photometric
        Assert.Equal(8280u, TestTiff.U32(raw.Tags[256].Value, 0));
        Assert.Equal(5520u, TestTiff.U32(raw.Tags[257].Value, 0));

        // ×4 16-bit domain: BL 4032 on all four 2×2 repeat positions, WL 63568.
        Assert.Equal(4u, raw.Tags[50714].Count);
        for (int i = 0; i < 4; i++)
            Assert.Equal(4032, TestTiff.U16(raw.Tags[50714].Value, i * 2));
        Assert.Equal(63568u, TestTiff.U32(raw.Tags[50717].Value, 0));

        // One full-height strip, tile tags absent entirely.
        Assert.Equal(5520u, TestTiff.U32(raw.Tags[278].Value, 0));  // RowsPerStrip = H
        Assert.Equal(1u, raw.Tags[273].Count);
        Assert.Equal((uint)8280 * 5520 * 2, TestTiff.U32(raw.Tags[279].Value, 0));   // W·H·2 bytes
        foreach (ushort tileTag in new ushort[] { 322, 323, 324, 325 })
            Assert.False(raw.Tags.ContainsKey(tileTag), $"tile tag {tileTag} must be absent when uncompressed");
    }

    [Fact]
    public void CfaUncompressed_RoundTripsExactlyThroughIndependentReader()
    {
        // BackendDngReader is the foreign-stream lineage reader (plain TIFF
        // walk, no writer-model assumptions): every stored sample must equal
        // the source LibRaw decode shifted x<<2 (the documented domain rule,
        // clamped to u16).
        var (dng, src) = UncompressedCfa.Value;
        var read = BackendDngReader.Read(dng);
        Assert.Equal(8280, read.W);
        Assert.Equal(5520, read.H);
        Assert.Equal(1, read.Samples);
        Assert.Equal(32803, read.Photometric);
        Assert.Equal(4032, read.BlackLevel);
        Assert.Equal(63568, read.WhiteLevel);      // ×4-domain WL tag carried through

        Assert.Equal(src.LongLength, read.Data.LongLength);
        for (long i = 0; i < src.LongLength; i++)
        {
            ushort expected = (ushort)Math.Min(src[i] << 2, 65535);
            if (read.Data[i] != expected)
            {
                Assert.Fail($"first mismatch at {i}: stored {read.Data[i]} != src<<2 {expected} (src {src[i]})");
            }
        }
    }

    [Fact]
    public void CfaUncompressed_LibRawOpensIt()
    {
        // Handle-level readability by a real raw library (the diagnostic-build
        // consumer of the uncompressed path): dims = padded stored frame.
        var (dng, _) = UncompressedCfa.Value;
        IntPtr h = LibRawNative.OpenRaw(dng, out int error);
        Assert.NotEqual(IntPtr.Zero, h);
        try
        {
            Assert.Equal(8280, LibRawNative.GetWidth(h));
            Assert.Equal(5520, LibRawNative.GetHeight(h));
            Assert.Equal("Nikon", LibRawNative.GetMake(h));
            Assert.Equal("Nikon Z 8", LibRawNative.GetModel(h));
        }
        finally { LibRawNative.Close(h); }
    }

    [Fact]
    public void LinearRawUncompressed_SmallSynthetic_RoundTripsExactly()
    {
        // The LinearRaw half of the uncompressed path (Compress=false with a
        // non-CFA payload): no domain shift (already 16-bit), exact bytes back.
        const int W = 64, H = 50;                    // deliberately not tile/strip aligned
        var pixels = new ushort[(long)W * H * 3];
        for (long i = 0; i < pixels.Length; i++)
            pixels[i] = (ushort)((i * 2654435761L) & 0xFFFF);

        string dir = Path.Combine(Path.GetTempPath(), "jsr4z8-uncompressedtests-linear");
        try { Directory.Delete(dir, recursive: true); } catch { /* stale run */ }
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "linearraw_uncompressed.dng");
        DngWriter.Write(path, pixels, W, H, new DngWriteOptions
        {
            Compress = false,
            SamplesPerPixel = 3,
            PhotometricInterpretation = 34892,
            BlackLevel = 0,
            WhiteLevel = 65535,
        }, originalMetadata: null);

        byte[] file = File.ReadAllBytes(path);
        var raw = TestTiff.ParseIfd(file, TestTiff.U32(file, 4));
        Assert.Equal(1, TestTiff.U16(raw.Tags[259].Value, 0));
        Assert.Equal((uint)H, TestTiff.U32(raw.Tags[278].Value, 0));
        Assert.Equal(1u, raw.Tags[273].Count);

        var read = BackendDngReader.Read(path);
        Assert.Equal(W, read.W);
        Assert.Equal(H, read.H);
        Assert.Equal(3, read.Samples);
        Assert.Equal(pixels.LongLength, read.Data.LongLength);
        for (long i = 0; i < pixels.LongLength; i++)
            if (pixels[i] != read.Data[i])
            {
                Assert.Fail($"first mismatch at {i}: stored {read.Data[i]} != src {pixels[i]}");
            }
    }
}
