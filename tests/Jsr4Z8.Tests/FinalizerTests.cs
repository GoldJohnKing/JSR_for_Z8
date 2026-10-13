using System.Text;
using Jsr4Z8.Core;
using Jsr4Z8.Core.Dng;
using Jsr4Z8.Core.Native;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Task 9: JsrDngFinalizer — fuse the backend's LinearRaw RGB product with the
/// reference NEF's preserved metadata into JSR_&lt;基准帧名&gt;.dng.
///
/// Fixture fixtures/backend-dng/roi_sample.dng: real backend product
/// (Transformer-JSR V15.3.5, mode=roi @(512,512,1024,768), frames
/// DSC_7576..7582 + dark DSC_7575, fullwell 1102.2, generated 2026-10-13 via
/// --assistant-jsr-job). Measured raw SubIFD: 2048×1536, SamplesPerPixel=3,
/// 16-bit, Photometric=34892, Compression=7 (tifffile LJ strips, predictor 4,
/// 12 strips × 128 rows), BlackLevel=0 / WhiteLevel=65535; IFD0
/// AsShotNeutral=[0.560175,1.0,0.557734], ColorMatrix1 = Z8 profile;
/// file size 9,574,372 bytes. The ROI sits inside the sensor's active area,
/// so the finalized product carries the full-image ActiveArea/DefaultCrop
/// pair (full-frame products get the ×2-scaled (24,16,16512,11008) pair).
/// </summary>
public class FinalizerTests
{
    private const int FixtureW = 2048;
    private const int FixtureH = 1536;

    /// <summary>One Finalize per test run (full fixture LJ decode + re-encode).</summary>
    private static readonly Lazy<(string Final, string Dir)> Finalized = new(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), "jsr4z8-finalizertests");
        try { Directory.Delete(dir, recursive: true); } catch { /* stale run */ }
        Directory.CreateDirectory(dir);
        string final = JsrDngFinalizer.Finalize(
            FindFixture("roi_sample.dng"), SampleFiles.FindSample("DSC_7576.NEF"), dir);
        return (final, dir);
    });

    internal static string FindFixture(string name)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "tests", "Jsr4Z8.Tests", "fixtures", "backend-dng", name);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            $"Fixture not found: tests\\Jsr4Z8.Tests\\fixtures\\backend-dng\\{name} (searched upwards from {AppContext.BaseDirectory})");
    }

    [Fact]
    public void Finalize_WritesJsrNamedDngInDestDir()
    {
        var (final, dir) = Finalized.Value;
        Assert.Equal("JSR_DSC_7576.dng", Path.GetFileName(final));
        Assert.True(Path.IsPathRooted(final), "returned path must be absolute");
        Assert.Equal(Path.GetFullPath(dir), Path.GetFullPath(Path.GetDirectoryName(final)!));
        Assert.True(File.Exists(final), "finalized DNG missing");
    }

    [Fact]
    public void RawSubIfd_LinearRawCompressedAtFixtureGeometry()
    {
        var (final, _) = Finalized.Value;
        byte[] file = File.ReadAllBytes(final);
        Assert.Equal(0x49, file[0]); Assert.Equal(0x49, file[1]);
        Assert.Equal(42, Tiff.U16(file, 2));

        var ifd0 = Tiff.ParseIfd(file, Tiff.U32(file, 4));
        var (_, _, v330) = ifd0.Tags[330];                          // SubIFDs: raw first
        var raw = Tiff.ParseIfd(file, Tiff.U32(v330, 0));

        Assert.Equal(0u, Tiff.U32(raw.Tags[254].Value, 0));         // NewSubfileType = raw
        Assert.Equal((uint)FixtureW, Tiff.U32(raw.Tags[256].Value, 0));
        Assert.Equal((uint)FixtureH, Tiff.U32(raw.Tags[257].Value, 0));
        Assert.Equal(7, Tiff.U16(raw.Tags[259].Value, 0));          // Compression = lossless JPEG
        Assert.Equal(34892, Tiff.U16(raw.Tags[262].Value, 0));      // Photometric = LinearRaw
        Assert.Equal(3, Tiff.U16(raw.Tags[277].Value, 0));          // SamplesPerPixel = RGB
        Assert.Equal(16, Tiff.U16(raw.Tags[258].Value, 0));         // 16-bit
        Assert.Equal(0, Tiff.U16(raw.Tags[50714].Value, 0));        // BlackLevel = 0
        Assert.Equal(65535u, Tiff.U32(raw.Tags[50717].Value, 0));   // WhiteLevel = 65535
    }

    [Fact]
    public void Ifd0_SoftwareIsJsrForZ8_NoArtistTag_ExifAndPreviewsCarried()
    {
        var (final, _) = Finalized.Value;
        byte[] file = File.ReadAllBytes(final);
        var ifd0 = Tiff.ParseIfd(file, Tiff.U32(file, 4));

        // Fact #9 (2026-10-13 user decision): no backend/attribution tags at IFD0
        // level — Software names the tool, Artist is absent entirely.
        Assert.Equal("JSR for Z8", Tiff.Ascii(ifd0.Tags[305].Value));
        Assert.False(ifd0.Tags.ContainsKey(315), "IFD0 must not carry an Artist tag");

        // EXIF relocated from the reference NEF (photographer metadata untouched)
        Assert.True(ifd0.Tags.ContainsKey(0x8769), "ExifIFD pointer missing");
        var exif = Tiff.ParseIfd(file, Tiff.U32(ifd0.Tags[0x8769].Value, 0));
        Assert.Equal(3200, Tiff.U16(exif.Tags[0x8827].Value, 0));
        Assert.Equal("2026:10:08 21:52:23", Tiff.Ascii(exif.Tags[0x9003].Value));
        Assert.True(exif.Tags.ContainsKey(0x927C), "MakerNote not preserved");

        // ≥2 preview IFDs with real JPEG payloads
        int previews = 0;
        CountJpegPreviews(file, ifd0, ref previews);
        var (_, _, v330) = ifd0.Tags[330];
        for (int i = 1; i < 3; i++)
            CountJpegPreviews(file, Tiff.ParseIfd(file, Tiff.U32(v330, i * 4)), ref previews);
        Assert.True(previews >= 2, $"expected ≥2 JPEG preview IFDs, found {previews}");

        // AsShotNeutral carried from the backend product (= 1/cam_mul here)
        var asn = ifd0.Tags[50728].Value;
        Assert.Equal(3u, ifd0.Tags[50728].Count);
        Assert.InRange(Tiff.U32(asn, 0) / 1e6, 0.560175 - 1e-5, 0.560175 + 1e-5);
        Assert.InRange(Tiff.U32(asn, 8) / 1e6, 1.0 - 1e-6, 1.0 + 1e-6);
        Assert.InRange(Tiff.U32(asn, 16) / 1e6, 0.557734 - 1e-5, 0.557734 + 1e-5);
    }

    [Fact]
    public void RawSubIfd_GeometryPairAndOpcodes()
    {
        var (final, _) = Finalized.Value;
        byte[] file = File.ReadAllBytes(final);
        var ifd0 = Tiff.ParseIfd(file, Tiff.U32(file, 4));
        var (_, _, v330) = ifd0.Tags[330];
        var raw = Tiff.ParseIfd(file, Tiff.U32(v330, 0));

        // ROI interior crop → full-image ActiveArea/DefaultCrop pair
        // (defaultScale 1/1, origin 0/0, size = stored geometry)
        var aa = raw.Tags[50829].Value;                             // top,left,bottom,right
        Assert.Equal(0u, Tiff.U32(aa, 0));
        Assert.Equal(0u, Tiff.U32(aa, 4));
        Assert.Equal((uint)FixtureH, Tiff.U32(aa, 8));
        Assert.Equal((uint)FixtureW, Tiff.U32(aa, 12));
        Assert.True(raw.Tags.ContainsKey(50718), "DefaultScale missing");
        Assert.Equal(0u, Tiff.U32(raw.Tags[50719].Value, 0));       // CropOrigin x
        Assert.Equal(0u, Tiff.U32(raw.Tags[50719].Value, 8));       // CropOrigin y
        Assert.Equal((uint)FixtureW, Tiff.U32(raw.Tags[50720].Value, 0));
        Assert.Equal((uint)FixtureH, Tiff.U32(raw.Tags[50720].Value, 8));

        // Lens opcodes from the NEF's NEFInfo (normalized coefficients, carried as-is)
        Assert.True(raw.Tags.TryGetValue(51009, out var ol2), "OpcodeList2 (51009) missing");
        Assert.True(ol2.Value.Length >= 60, "OpcodeList2 too short");
        Assert.Equal(1u, Tiff.Be32(ol2.Value, 0));                  // one opcode
        Assert.Equal(3u, Tiff.Be32(ol2.Value, 4));                  // FixVignetteRadial
        Assert.True(raw.Tags.TryGetValue(51022, out var ol3), "OpcodeList3 (51022) missing");
        Assert.True(ol3.Value.Length >= 160, "OpcodeList3 too short");
        Assert.Equal(1u, Tiff.Be32(ol3.Value, 0));
        Assert.Equal(1u, Tiff.Be32(ol3.Value, 4));                  // WarpRectilinear
    }

    [Fact]
    public void PixelRoundTrip_FinalDecodesExactlyToBackendPixels()
    {
        var (final, _) = Finalized.Value;
        var backend = BackendDngReader.Read(FindFixture("roi_sample.dng"));
        Assert.Equal(FixtureW, backend.W);
        Assert.Equal(FixtureH, backend.H);
        Assert.Equal(3, backend.Samples);
        Assert.True(backend.Data.Length > 0 && backend.Data.All(v => v <= 65535));

        var finalPixels = DecodeFinalRawTiles(final, out int fw, out int fh, out int fs);
        Assert.Equal(FixtureW, fw);
        Assert.Equal(FixtureH, fh);
        Assert.Equal(3, fs);
        Assert.Equal(backend.Data.Length, finalPixels.Length);

        // Lossless: every sample identical; mean deviation therefore < 0.01%.
        double backendMean = Mean(backend.Data), finalMean = Mean(finalPixels);
        Assert.True(Math.Abs(finalMean - backendMean) / Math.Max(backendMean, 1) < 1e-4,
            $"mean deviation {Math.Abs(finalMean - backendMean) / Math.Max(backendMean, 1):e2} ≥ 0.01%");
        for (int i = 0; i < backend.Data.Length; i++)
            if (backend.Data[i] != finalPixels[i])
            {
                Assert.Fail($"first pixel mismatch at {i}: backend {backend.Data[i]} vs final {finalPixels[i]}");
                break;
            }
    }

    [Fact]
    public void RawTiles_AreGenuinelyCompressed()
    {
        // Brief originally asked 体积 < 输入的 20% (plan "-90%" premise). Measured
        // 2026-10-13: the ROI fixture's 9.43 MB LJ strips are real data (zero
        // padding; the plan's "31 MB in 341 MB" reading came from a truncated
        // StripByteCounts list), so the pixel rewrite cannot shrink 5:1, and the
        // reference NEF's ~5 MB of preview JPEGs are embedded by requirement.
        // Honest compression-sanity bound instead (supervisor-approved): the raw
        // SubIFD's tile payload must stay well below the uncompressed payload
        // (backend tifffile ratio 49.9%, ours measured ≈50% with PSV=4).
        var (final, _) = Finalized.Value;
        byte[] file = File.ReadAllBytes(final);
        var ifd0 = Tiff.ParseIfd(file, Tiff.U32(file, 4));
        var (_, _, v330) = ifd0.Tags[330];
        var raw = Tiff.ParseIfd(file, Tiff.U32(v330, 0));
        var counts = raw.Tags[325].Value;                         // TileByteCounts
        long tileBytes = 0;
        for (int i = 0; i < raw.Tags[325].Count; i++)
            tileBytes += Tiff.U32(counts, i * 4);
        long uncompressed = (long)FixtureW * FixtureH * 3 * 2;
        Assert.True(tileBytes < uncompressed * 2 / 3,
            $"tile payload {tileBytes} B ≥ 2/3 of uncompressed {uncompressed} B");
    }

    [Fact]
    public void DecodeStream_MalformedStrip_IsContained_ReturnsErrorCode()
    {
        // Regression lock for the ljd_decode_stream error containment fix
        // (Task 9 review round 1): before it, libjpeg's default error_exit
        // printed and called exit(EXIT_FAILURE) — this very call would have
        // killed the test host. Garbage with no SOI marker must now come back
        // as the documented error code without taking the process down.
        var garbage = new byte[16];
        for (int i = 0; i < garbage.Length; i += 4)
        {
            garbage[i] = 0xDE; garbage[i + 1] = 0xAD;
            garbage[i + 2] = 0xBE; garbage[i + 3] = 0xEF;
        }
        var dst = new ushort[64 * 64 * 3];

        long decoded = DngLjpegNative.DecodeStream(garbage, dst, 0,
            out uint w, out uint h, out uint nc);

        Assert.Equal(-1L, decoded);
    }

        /// <summary>Decodes the finalized DNG's raw SubIFD tile array back to the
    /// full interleaved sample plane (ljd_decode_stream per 256×256 tile,
    /// cropped to the TIFF tile grid).</summary>
    private static ushort[] DecodeFinalRawTiles(string path, out int w, out int h, out int samples)
    {
        byte[] file = File.ReadAllBytes(path);
        var ifd0 = Tiff.ParseIfd(file, Tiff.U32(file, 4));
        var (_, _, v330) = ifd0.Tags[330];
        var raw = Tiff.ParseIfd(file, Tiff.U32(v330, 0));
        w = (int)Tiff.U32(raw.Tags[256].Value, 0);
        h = (int)Tiff.U32(raw.Tags[257].Value, 0);
        samples = Tiff.U16(raw.Tags[277].Value, 0);
        int tw = Tiff.U16(raw.Tags[322].Value, 0), th = Tiff.U16(raw.Tags[323].Value, 0);

        var offsets = raw.Tags[324].Value;                          // TileOffsets blob
        var counts = raw.Tags[325].Value;                           // TileByteCounts blob
        int tilesX = (w + tw - 1) / tw, tilesY = (h + th - 1) / th;
        var pixels = new ushort[(long)w * h * samples];
        var tile = new ushort[tw * th * samples];
        for (int ty = 0; ty < tilesY; ty++)
            for (int tx = 0; tx < tilesX; tx++)
            {
                int i = ty * tilesX + tx;
                uint off = Tiff.U32(offsets, i * 4), len = Tiff.U32(counts, i * 4);
                var strip = file[(int)off..(int)(off + len)];
                long decoded = DngLjpegNative.DecodeStream(strip, tile, 0,
                    out uint sw, out uint sh, out uint sc);
                Assert.Equal((long)tw * th * samples, decoded);
                Assert.Equal((uint)tw, sw);
                Assert.Equal((uint)th, sh);
                Assert.Equal((uint)samples, sc);

                int copyW = Math.Min(tw, w - tx * tw) * samples;
                for (int r = 0; r < th && ty * th + r < h; r++)
                    Array.Copy(tile, (long)r * tw * samples,
                        pixels, ((long)(ty * th + r) * w + tx * tw) * samples, copyW);
            }
        return pixels;
    }

    private static double Mean(ushort[] data)
    {
        double sum = 0;
        foreach (var v in data) sum += v;
        return sum / data.Length;
    }

    private static void CountJpegPreviews(byte[] file, Tiff.Ifd ifd, ref int count)
    {
        if (!ifd.Tags.TryGetValue(513, out var jif) || !ifd.Tags.TryGetValue(514, out var jlen))
            return;
        uint off = Tiff.U32(jif.Value, 0), len = Tiff.U32(jlen.Value, 0);
        if (len < 4 || off + len > file.Length) return;
        if (file[off] == 0xFF && file[off + 1] == 0xD8) count++;
    }

    /// <summary>Minimal little-endian TIFF IFD walker (DngWriterTests parity).</summary>
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
