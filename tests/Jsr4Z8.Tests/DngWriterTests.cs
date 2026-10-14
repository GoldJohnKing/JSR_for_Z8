using System.Text;
using Jsr4Z8.Core;
using Jsr4Z8.Core.Dng;
using Jsr4Z8.Core.Native;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Task 8: DngWriter port — NefToDngConverter.Convert on samples/DSC_7576.NEF
/// must produce a complete lossless DNG whose structure reads back correctly
/// (Save_Z8 tests/SaveZ8.Tests read-back pattern). Facts measured 2026-10-13
/// on that file: 3 embedded previews (160×120 / 1620×1080 / 8256×5504),
/// IFD0 at file offset 8, EXIF ISO 3200, NEFInfo block 8 vignette flag ≠ 0
/// (Vignette Control ON → both OpcodeList2/3 emitted).
/// </summary>
public class DngWriterTests
{
    /// <summary>One conversion per test run (full 8280×5520 LJPEG encode, ~seconds).</summary>
    private static readonly Lazy<(string Dng, NefToDngResult Result)> Converted = new(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), "jsr4z8-dngtests");
        try { Directory.Delete(dir, recursive: true); } catch { /* stale run */ }
        Directory.CreateDirectory(dir);
        string dng = Path.Combine(dir, "DSC_7576.dng");
        var result = NefToDngConverter.Convert(SampleFiles.FindSample("DSC_7576.NEF"), dng);
        return (dng, result);
    });

    private static byte[] DngBytes => File.ReadAllBytes(Converted.Value.Dng);

    [Fact]
    public void LjeSelftest_EncoderRoundTripIsClean()
    {
        // Encoder sanity FIRST (brief): native encode+decode self-test, 0 mismatches.
        Assert.Equal(0L, DngLjpegNative.lje_selftest());
    }

    [Fact]
    public void Convert_SampleNef_WritesDngWithoutError()
    {
        var (_, result) = Converted.Value;
        Assert.Null(result.Error);
        Assert.True(File.Exists(result.OutputPath), "output DNG missing");
        Assert.True(new FileInfo(result.OutputPath).Length > 1_000_000,
            "output suspiciously small for a 45.7MP raw");
    }

    [Fact]
    public void RawSubIfd_CompressedCfaInNative14BitDomain()
    {
        Assert.Null(Converted.Value.Result.Error);
        byte[] file = DngBytes;
        Assert.Equal(0x49, file[0]); Assert.Equal(0x49, file[1]);       // II
        Assert.Equal(42, Tiff.U16(file, 2));                             // TIFF magic

        var ifd0 = Tiff.ParseIfd(file, Tiff.U32(file, 4));

        // ADC topology: IFD0 thumbnail, raw IFD first inside SubIFDs (330)
        Assert.True(ifd0.Tags.ContainsKey(330), "SubIFDs tag missing in IFD0");
        var (_, c330, v330) = ifd0.Tags[330];
        Assert.Equal(3u, c330);                                          // raw + full + medium preview
        var raw = Tiff.ParseIfd(file, Tiff.U32(v330, 0));

        Assert.Equal(0u, Tiff.U32(raw.Tags[254].Value, 0));              // NewSubfileType = raw
        Assert.Equal(8280u, Tiff.U32(raw.Tags[256].Value, 0));           // padded sensor width
        Assert.Equal(5520u, Tiff.U32(raw.Tags[257].Value, 0));
        Assert.Equal(7, Tiff.U16(raw.Tags[259].Value, 0));               // Compression=7 lossless JPEG
        Assert.Equal(32803, Tiff.U16(raw.Tags[262].Value, 0));           // Photometric = CFA
        Assert.Equal(16, Tiff.U16(raw.Tags[258].Value, 0));              // BitsPerSample
        Assert.Equal(1, Tiff.U16(raw.Tags[277].Value, 0));               // SamplesPerPixel

        // Tiled layout (256×256), strip tags absent
        Assert.Equal(256, Tiff.U16(raw.Tags[322].Value, 0));
        Assert.Equal(256, Tiff.U16(raw.Tags[323].Value, 0));
        Assert.False(raw.Tags.ContainsKey(273), "strip offsets must be absent in tiled mode");
        Assert.False(raw.Tags.ContainsKey(279), "strip byte counts must be absent in tiled mode");
        Assert.Equal((uint)((8280 + 255) / 256) * ((5520 + 255) / 256), raw.Tags[324].Count); // 33×22=726 tiles
        uint tile0 = Tiff.U32(raw.Tags[324].Value, 0);
        Assert.Equal(0xFF, file[tile0]); Assert.Equal(0xD8, file[tile0 + 1]);
        Assert.Equal(0xFF, file[tile0 + 2]); Assert.Equal(0xC3, file[tile0 + 3]); // SOF3 lossless

        // Native 14-bit domain (ADC parity) — NOT RawMetadata.WhiteLevel (16383)
        var (_, blCount, bl) = raw.Tags[50714];
        Assert.Equal(4u, blCount);                                       // 2×2 BlackLevelRepeatDim
        for (int i = 0; i < 4; i++) Assert.Equal(1008, Tiff.U16(bl, i * 2));
        Assert.Equal(15892u, Tiff.U32(raw.Tags[50717].Value, 0));        // WhiteLevel

        // CFA phase RGGB (LibRaw 0,1,3,2 → DNG colors 0,1,1,2)
        Assert.Equal(new byte[] { 0, 1, 1, 2 }, raw.Tags[33422].Value);

        // ActiveArea = full padded frame, paired with DefaultScale/CropOrigin/CropSize
        // (crop derived from the largest preview 8256×5504, origin centered & CFA-even)
        var aa = raw.Tags[50829].Value;
        Assert.Equal(0u, Tiff.U32(aa, 0));
        Assert.Equal(0u, Tiff.U32(aa, 4));
        Assert.Equal(5520u, Tiff.U32(aa, 8));
        Assert.Equal(8280u, Tiff.U32(aa, 12));
        Assert.Equal(12u, Tiff.U32(raw.Tags[50719].Value, 0));           // DefaultCropOrigin x
        Assert.Equal(8u, Tiff.U32(raw.Tags[50719].Value, 8));            // DefaultCropOrigin y
        Assert.Equal(8256u, Tiff.U32(raw.Tags[50720].Value, 0));         // DefaultCropSize w
        Assert.Equal(5504u, Tiff.U32(raw.Tags[50720].Value, 8));         // DefaultCropSize h
        Assert.True(raw.Tags.ContainsKey(50718), "DefaultScale missing");

        // OpcodeList2/3 present, non-empty, big-endian, single known opcode each
        Assert.True(raw.Tags.TryGetValue(51009, out var ol2), "OpcodeList2 (51009) missing");
        Assert.True(ol2.Value.Length >= 60, "OpcodeList2 too short");
        Assert.Equal(1u, Tiff.Be32(ol2.Value, 0));                       // one opcode
        Assert.Equal(3u, Tiff.Be32(ol2.Value, 4));                       // FixVignetteRadial
        Assert.True(raw.Tags.TryGetValue(51022, out var ol3), "OpcodeList3 (51022) missing");
        Assert.True(ol3.Value.Length >= 160, "OpcodeList3 too short");
        Assert.Equal(1u, Tiff.Be32(ol3.Value, 0));                       // one opcode
        Assert.Equal(1u, Tiff.Be32(ol3.Value, 4));                       // WarpRectilinear
    }

    [Fact]
    public void Ifd0_CarriesBaselineExifAndPreviews()
    {
        Assert.Null(Converted.Value.Result.Error);
        byte[] file = DngBytes;
        var ifd0 = Tiff.ParseIfd(file, Tiff.U32(file, 4));

        // Writer's ADC-parity identity tags
        Assert.Equal("Nikon", Tiff.Ascii(ifd0.Tags[271].Value));         // Make
        Assert.Equal("Nikon Z 8", Tiff.Ascii(ifd0.Tags[272].Value));     // Model
        Assert.Equal(1u, Tiff.U32(ifd0.Tags[254].Value, 0));             // IFD0 = thumbnail (NST=1)

        // DNG baseline/color tags
        Assert.Equal(new byte[] { 1, 6, 0, 0 }, ifd0.Tags[50706].Value); // DNGVersion 1.6.0.0
        Assert.Equal("Nikon Z 8", Tiff.Ascii(ifd0.Tags[50708].Value));   // UniqueCameraModel
        Assert.True(ifd0.Tags.ContainsKey(50721), "ColorMatrix1 missing");
        Assert.True(ifd0.Tags.ContainsKey(50728), "AsShotNeutral missing");

        // AsShotNeutral = 1/cam_mul per component (probe: ≈[0.560175, 1.0, 0.557734]);
        // three RATIONALs, 8 bytes each (numerator at 0/8/16, denominator 1000000)
        var asn = ifd0.Tags[50728].Value;
        Assert.Equal(3u, ifd0.Tags[50728].Count);
        Assert.InRange(Tiff.U32(asn, 0) / 1e6, 0.560175 - 1e-5, 0.560175 + 1e-5);
        Assert.InRange(Tiff.U32(asn, 8) / 1e6, 1.0 - 1e-6, 1.0 + 1e-6);
        Assert.InRange(Tiff.U32(asn, 16) / 1e6, 0.557734 - 1e-5, 0.557734 + 1e-5);
        Assert.Equal(1000000u, Tiff.U32(asn, 4));

        // EXIF IFD relocated from the source NEF
        Assert.True(ifd0.Tags.ContainsKey(0x8769), "ExifIFD pointer missing in IFD0");
        var exif = Tiff.ParseIfd(file, Tiff.U32(ifd0.Tags[0x8769].Value, 0));
        Assert.Equal(3200, Tiff.U16(exif.Tags[0x8827].Value, 0));        // ISOSpeedRatings
        Assert.Equal(10u, Tiff.U32(exif.Tags[0x829A].Value, 0));         // ExposureTime 10/1000 s
        Assert.Equal(1000u, Tiff.U32(exif.Tags[0x829A].Value, 4));       // = 0.010 s (RawMetadata parity)
        Assert.Equal("2026:10:08 21:52:23", Tiff.Ascii(exif.Tags[0x9003].Value));
        Assert.True(exif.Tags.ContainsKey(0x927C), "MakerNote not preserved");

        // GPS IFD (source has GPSVersionID only)
        Assert.True(ifd0.Tags.ContainsKey(0x8825), "GPS IFD pointer missing in IFD0");
        var gps = Tiff.ParseIfd(file, Tiff.U32(ifd0.Tags[0x8825].Value, 0));
        Assert.Equal(new byte[] { 2, 3, 0, 0 }, gps.Tags[0x0000].Value);

        // ≥2 preview IFDs with real JPEG payloads (IFD0 thumbnail + SubIFD previews)
        int jpegPreviews = 0;
        CountJpegPreviews(file, ifd0, ref jpegPreviews);
        var (_, _, v330) = ifd0.Tags[330];
        var full = Tiff.ParseIfd(file, Tiff.U32(v330, 4));               // SubIFDs[1] = full-size preview
        Assert.Equal(8256u, Tiff.U32(full.Tags[256].Value, 0));
        Assert.Equal(5504u, Tiff.U32(full.Tags[257].Value, 0));
        CountJpegPreviews(file, full, ref jpegPreviews);
        var medium = Tiff.ParseIfd(file, Tiff.U32(v330, 8));             // SubIFDs[2] = medium preview
        Assert.Equal(1620u, Tiff.U32(medium.Tags[256].Value, 0));
        Assert.Equal(1080u, Tiff.U32(medium.Tags[257].Value, 0));
        CountJpegPreviews(file, medium, ref jpegPreviews);
        Assert.True(jpegPreviews >= 2, $"expected ≥2 JPEG preview IFDs, found {jpegPreviews}");
    }

    [Fact]
    public void LibRaw_OpensProduct_AndPixelsRoundTripExactly()
    {
        Assert.Null(Converted.Value.Result.Error);
        string dng = Converted.Value.Dng;

        // Handle-level open (Task 2 surface): product must be a readable raw
        IntPtr h = LibRawNative.OpenRaw(dng, out int error);
        Assert.NotEqual(IntPtr.Zero, h);
        try
        {
            // 以实际为准: the stored raw plane is the padded frame; DefaultCrop trims
            // the displayed image to 8256×5504 (Save_Z8 stores the same padded frame).
            Assert.Equal(8280, LibRawNative.GetWidth(h));
            Assert.Equal(5520, LibRawNative.GetHeight(h));
        }
        finally { LibRawNative.Close(h); }

        // Save_Z8 DngCompressedTests.PixelRoundTrip parity: decoding the product
        // through LibRaw must return the exact samples LibRaw decodes from the NEF
        // (compressed DNG keeps the native 14-bit domain — no scaling anywhere).
        var (product, pw, ph, _, _, _, _) = LibRawNative.Decode(dng);
        var (source, sw, sh, _, _, _, _) = LibRawNative.Decode(SampleFiles.FindSample("DSC_7576.NEF"));
        Assert.Equal(sw, pw);
        Assert.Equal(sh, ph);
        Assert.Equal(source.Length, product.Length);
        for (int i = 0; i < source.Length; i++)
            if (source[i] != product[i])
            { Assert.Fail($"first pixel mismatch at {i}: NEF {source[i]} vs DNG {product[i]}"); break; }
    }

    [Fact]
    public void LinearRaw_StripLayout_PartialLastStripEncodesActualRows()
    {
        // RawLayout.Strips (backend tifffile parity): 128-row LJ bands, final band
        // = actual remaining rows (backend writes 87 strips for 11040 rows, last
        // = 32 rows). Exercised here with 200 rows → 2 strips: 128 + 72.
        const int W = 64, H = 200;
        var pixels = new ushort[(long)W * H * 3];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (ushort)((i * 1103515245 + 12345) & 0xFFFF);

        string dir = Path.Combine(Path.GetTempPath(), "jsr4z8-dngtests-strips");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "linearraw_strips.dng");
        DngWriter.Write(path, pixels, W, H, new DngWriteOptions
        {
            Compress = true,
            SamplesPerPixel = 3,
            PhotometricInterpretation = 34892,
            BlackLevel = 0,
            WhiteLevel = 65535,
            Layout = RawLayout.Strips,
        }, originalMetadata: null);

        byte[] file = File.ReadAllBytes(path);
        Assert.Equal(0x49, file[0]); Assert.Equal(0x49, file[1]);
        var raw = Tiff.ParseIfd(file, Tiff.U32(file, 4));     // no previews → raw in IFD0

        Assert.Equal(7, Tiff.U16(raw.Tags[259].Value, 0));
        Assert.Equal(34892, Tiff.U16(raw.Tags[262].Value, 0));
        Assert.Equal(3, Tiff.U16(raw.Tags[277].Value, 0));
        Assert.Equal(128u, Tiff.U32(raw.Tags[278].Value, 0));
        Assert.Equal(2u, raw.Tags[273].Count);                // ceil(200/128)
        Assert.Equal(2u, raw.Tags[279].Count);
        foreach (ushort tileTag in new ushort[] { 322, 323, 324, 325 })
            Assert.False(raw.Tags.ContainsKey(tileTag), $"tile tag {tileTag} must be absent in strip layout");

        // Round-trip: each strip decodes to its own geometry, second = 72 rows
        var offsets = raw.Tags[273].Value;
        var counts = raw.Tags[279].Value;
        var decoded = new ushort[(long)W * H * 3];
        var band = new ushort[(long)W * 128 * 3];
        for (int i = 0; i < 2; i++)
        {
            int rows = Math.Min(128, H - i * 128);
            uint off = Tiff.U32(offsets, i * 4), len = Tiff.U32(counts, i * 4);
            long n = DngLjpegNative.DecodeStream(file[(int)off..(int)(off + len)], band, 0,
                out uint sw, out uint sh, out uint sc);
            Assert.Equal((long)W * rows * 3, n);
            Assert.Equal((uint)W, sw);
            Assert.Equal((uint)rows, sh);
            Assert.Equal(3u, sc);
            Array.Copy(band, 0, decoded, (long)i * 128 * W * 3, (long)rows * W * 3);
        }
        for (int i = 0; i < pixels.Length; i++)
            if (pixels[i] != decoded[i])
            { Assert.Fail($"first pixel mismatch at {i}: src {pixels[i]} vs strip {decoded[i]}"); break; }
    }

    private static void CountJpegPreviews(byte[] file, Tiff.Ifd ifd, ref int count)
    {
        if (!ifd.Tags.TryGetValue(513, out var jif) || !ifd.Tags.TryGetValue(514, out var jlen))
            return;
        uint off = Tiff.U32(jif.Value, 0), len = Tiff.U32(jlen.Value, 0);
        if (len < 4 || off + len > file.Length) return;
        if (file[off] == 0xFF && file[off + 1] == 0xD8) count++;
    }

    /// <summary>Minimal little-endian TIFF IFD walker (Save_Z8 TestTiff read-back pattern).</summary>
    private static class Tiff
    {
        internal sealed class Ifd
        {
            public Dictionary<ushort, (ushort Type, uint Count, byte[] Value)> Tags = new();
        }

        internal static ushort U16(byte[] b, int i) => (ushort)(b[i] | (b[i + 1] << 8));

        internal static uint U32(byte[] b, int i) =>
            (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));

        internal static uint Be32(byte[] b, int i) =>
            (uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);

        internal static string Ascii(byte[] b) => Encoding.ASCII.GetString(b).TrimEnd('\0');

        internal static Ifd ParseIfd(byte[] file, uint offset)
        {
            var ifd = new Ifd();
            int count = U16(file, (int)offset);
            for (int i = 0; i < count; i++)
            {
                long p = offset + 2 + i * 12L;
                ushort tag = U16(file, (int)p);
                ushort type = U16(file, (int)p + 2);
                uint cnt = U32(file, (int)p + 4);
                uint val = U32(file, (int)p + 8);
                int typeSize = type switch
                {
                    1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4,
                    5 or 10 or 12 => 8, _ => 1,
                };
                int size = typeSize * (int)cnt;
                var data = new byte[Math.Max(size, 0)];
                if (size <= 4)
                    for (int j = 0; j < size; j++) data[j] = (byte)(val >> (8 * j));
                else
                    Array.Copy(file, val, data, 0, size);
                ifd.Tags[tag] = (type, cnt, data);
            }
            return ifd;
        }
    }
}
