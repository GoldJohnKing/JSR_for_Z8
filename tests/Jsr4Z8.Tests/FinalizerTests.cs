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
/// pair (full-frame products get the ×2-scaled ActiveArea
/// (16,24,11024,16536) with the ActiveArea-relative crop (0,0,16512,11008)).
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

    /// <summary>Strip-layout variant (ACR bisection: RawLayout.Strips = backend
    /// tifffile storage parity instead of tiles). Same fixture, one run per
    /// test session.</summary>
    private static readonly Lazy<(string Final, string Dir)> FinalizedStrips = new(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), "jsr4z8-finalizertests-strips");
        try { Directory.Delete(dir, recursive: true); } catch { /* stale run */ }
        Directory.CreateDirectory(dir);
        string final = JsrDngFinalizer.Finalize(
            FindFixture("roi_sample.dng"), SampleFiles.FindSample("DSC_7576.NEF"), dir,
            layout: RawLayout.Strips);
        return (final, dir);
    });

    /// <summary>ACR bisection V0 variant (post-release fix 4): all four
    /// feature-suppression seams off — backend-like minimal output (raw +
    /// thumbnail + DNG basics). Strip layout to mirror the harness's V0
    /// artifact. One run per test session.</summary>
    private static readonly Lazy<(string Final, string Dir)> FinalizedV0 = new(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), "jsr4z8-finalizertests-v0");
        try { Directory.Delete(dir, recursive: true); } catch { /* stale run */ }
        Directory.CreateDirectory(dir);
        string final = JsrDngFinalizer.Finalize(
            FindFixture("roi_sample.dng"), SampleFiles.FindSample("DSC_7576.NEF"), dir,
            layout: RawLayout.Strips,
            includeGeometryTags: false, includeOpcodes: false,
            includePreviews: false, includeExifGps: false);
        return (final, dir);
    });

    /// <summary>Passthrough variant (post-release fix 5): the backend's own
    /// LJ strip bytes verbatim inside the product wrapper — no decode, no
    /// re-encode. One run per test session.</summary>
    private static readonly Lazy<(string Final, string Dir)> FinalizedPassthrough = new(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), "jsr4z8-finalizertests-passthrough");
        try { Directory.Delete(dir, recursive: true); } catch { /* stale run */ }
        Directory.CreateDirectory(dir);
        string final = JsrDngFinalizer.Finalize(
            FindFixture("roi_sample.dng"), SampleFiles.FindSample("DSC_7576.NEF"), dir,
            layout: RawLayout.Strips, passthroughRawStreams: true);
        return (final, dir);
    });

    /// <summary>Reference-frame metadata/assets for the geometry-pair facts
    /// (post-release fix 8): reads the real sample NEF once per test session —
    /// metadata + preview inventory only, no pixel decode.</summary>
    private static readonly Lazy<(RawMetadata Meta, NefAssets Assets)> Reference = new(() =>
    {
        string nef = SampleFiles.FindSample("DSC_7576.NEF");
        return (RawMetadataReader.Read(nef), NefAssetExtractor.Extract(nef));
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

    /// <summary>Final-review finding 5: the reference NEF's XMP packet (IFD0 tag 700,
    /// verified present in samples\DSC_7576.NEF — 32,768-byte xpacket) must migrate
    /// into the finalized DNG's IFD0 with its payload intact.</summary>
    [Fact]
    public void Ifd0_XmpPacketMigratedFromReferenceNef()
    {
        var (final, _) = Finalized.Value;
        byte[] file = File.ReadAllBytes(final);
        var ifd0 = Tiff.ParseIfd(file, Tiff.U32(file, 4));

        Assert.True(ifd0.Tags.TryGetValue(700, out var xmp), "IFD0 tag 700 (XMP) missing");
        Assert.True(xmp.Count > 0 && xmp.Value.Length > 0, "XMP tag present but empty");
        Assert.StartsWith("<?xpacket", Encoding.ASCII.GetString(xmp.Value), StringComparison.Ordinal);
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

    /// <summary>Post-release fix 8 — full-dims (×2) path: the ActiveArea keeps
    /// the nonzero sensor-margin origin, and DefaultCropOrigin must be expressed
    /// relative to it (DNG spec), not in absolute stored-frame coordinates.</summary>
    [Fact]
    public void Geometry_FullDimsPair_ActiveAreaRelativeCropOrigin()
    {
        var (meta, assets) = Reference.Value;
        var (activeArea, crop) = JsrDngFinalizer.Geometry(2 * meta.Width, 2 * meta.Height, meta, assets);

        // Z8 ×2: padded frame 16560×11040, sensor active 8256×5504, margins
        // even-centered (24,16) → ActiveArea=(top,left,bottom,right).
        Assert.Equal(new uint[] { 16, 24, 11024, 16536 }, activeArea);
        Assert.Equal(0u, crop.X);                                   // ActiveArea-relative
        Assert.Equal(0u, crop.Y);
        Assert.Equal(16512u, crop.W);
        Assert.Equal(11008u, crop.H);
        AssertCropWithinActiveArea(crop, activeArea);
    }

    /// <summary>Dims-mismatch fallback (ROI products, harness crop ladder):
    /// full-image ActiveArea/DefaultCrop pair — ActiveArea origin (0,0) so the
    /// relative and absolute crop origins coincide; the invariant holds.</summary>
    [Fact]
    public void Geometry_DimsMismatchFallback_FullImagePair_InvariantHolds()
    {
        var (meta, assets) = Reference.Value;
        var (activeArea, crop) = JsrDngFinalizer.Geometry(FixtureW, FixtureH, meta, assets);

        Assert.Equal(new uint[] { 0, 0, (uint)FixtureH, (uint)FixtureW }, activeArea);
        Assert.Equal(0u, crop.X);
        Assert.Equal(0u, crop.Y);
        Assert.Equal((uint)FixtureW, crop.W);
        Assert.Equal((uint)FixtureH, crop.H);
        AssertCropWithinActiveArea(crop, activeArea);
    }

    /// <summary>Regression lock for post-release fix 8 (root cause of the ACR
    /// full-frame rejection): DefaultCropOrigin is an ActiveArea-relative
    /// offset. At full size the pre-fix absolute origin (24,16) made
    /// CropOrigin+CropSize overflow the ActiveArea edge → invalid geometry.</summary>
    [Fact]
    public void CropOrigin_MustBeActiveAreaRelative_FullSizeNoOverflow()
    {
        var (meta, assets) = Reference.Value;
        var (activeArea, crop) = JsrDngFinalizer.Geometry(2 * meta.Width, 2 * meta.Height, meta, assets);

        // CropOrigin+CropSize must stay inside the ActiveArea extents …
        AssertCropWithinActiveArea(crop, activeArea);
        // … i.e. the relative origin is (0,0), never the absolute (24,16).
        Assert.Equal(0u, crop.X);
        Assert.Equal(0u, crop.Y);
    }

    /// <summary>DNG invariant: DefaultCropOrigin+DefaultCropSize must fit
    /// inside the ActiveArea ([top,left,bottom,right]); the crop origin is
    /// ActiveArea-relative (post-release fix 8).</summary>
    private static void AssertCropWithinActiveArea((uint X, uint Y, uint W, uint H) crop, uint[] activeArea)
    {
        Assert.True(crop.X + crop.W <= activeArea[3] - activeArea[1],
            $"CropOrigin.X {crop.X} + CropSize.W {crop.W} exceeds ActiveArea width {activeArea[3] - activeArea[1]}");
        Assert.True(crop.Y + crop.H <= activeArea[2] - activeArea[0],
            $"CropOrigin.Y {crop.Y} + CropSize.H {crop.H} exceeds ActiveArea height {activeArea[2] - activeArea[0]}");
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
    public void Finalize_StripLayout_RawSubIfdUsesStripTags_NoTileTags()
    {
        var (final, _) = FinalizedStrips.Value;
        byte[] file = File.ReadAllBytes(final);
        Assert.Equal(0x49, file[0]); Assert.Equal(0x49, file[1]);
        Assert.Equal(42, Tiff.U16(file, 2));                  // file parses end-to-end

        var ifd0 = Tiff.ParseIfd(file, Tiff.U32(file, 4));
        var (_, _, v330) = ifd0.Tags[330];                    // SubIFDs: raw first
        var raw = Tiff.ParseIfd(file, Tiff.U32(v330, 0));

        // Strip layout: 273/278/279 present, tile tags absent entirely
        Assert.Equal(7, Tiff.U16(raw.Tags[259].Value, 0));    // still lossless JPEG
        Assert.Equal(34892, Tiff.U16(raw.Tags[262].Value, 0));
        Assert.True(raw.Tags.ContainsKey(273), "StripOffsets (273) missing");
        Assert.Equal(128u, Tiff.U32(raw.Tags[278].Value, 0)); // RowsPerStrip = 128 (backend parity)
        Assert.True(raw.Tags.ContainsKey(279), "StripByteCounts (279) missing");
        foreach (ushort tileTag in new ushort[] { 322, 323, 324, 325 })
            Assert.False(raw.Tags.ContainsKey(tileTag), $"tile tag {tileTag} must be absent in strip layout");

        // Fixture geometry: 1536 rows / 128 = 12 strips exactly
        Assert.Equal(12u, raw.Tags[273].Count);
        Assert.Equal(12u, raw.Tags[279].Count);
        uint strip0 = Tiff.U32(raw.Tags[273].Value, 0);
        Assert.Equal(0xFF, file[strip0]); Assert.Equal(0xD8, file[strip0 + 1]);   // SOI
    }

    [Fact]
    public void Finalize_StripLayout_PixelRoundTripExact()
    {
        var (final, _) = FinalizedStrips.Value;
        var backend = BackendDngReader.Read(FindFixture("roi_sample.dng"));

        var finalPixels = DecodeFinalRawStrips(final, out int fw, out int fh, out int fs);
        Assert.Equal(FixtureW, fw);
        Assert.Equal(FixtureH, fh);
        Assert.Equal(3, fs);
        Assert.Equal(backend.Data.Length, finalPixels.Length);

        for (int i = 0; i < backend.Data.Length; i++)
            if (backend.Data[i] != finalPixels[i])
            {
                Assert.Fail($"first pixel mismatch at {i}: backend {backend.Data[i]} vs strip-layout {finalPixels[i]}");
                break;
            }
    }

    /// <summary>Post-release fix 4 — diagnostic seams: the four feature-suppression
    /// toggles produce a backend-like minimal V0 (no geometry quartet, no opcodes,
    /// no preview SubIFDs, no EXIF/GPS/XMP/Software/Baseline block) while the IFD0
    /// thumbnail, the raw payload and the DNG basics stay; the default (full)
    /// output is unchanged — the existing facts still hold on the same Lazy.</summary>
    [Fact]
    public void Finalize_V0_SuppressionSeams_DropWrapperFeatures_KeepThumbnailRawAndBasics()
    {
        var (v0, _) = FinalizedV0.Value;
        byte[] file = File.ReadAllBytes(v0);
        Assert.Equal(0x49, file[0]); Assert.Equal(0x49, file[1]);
        Assert.Equal(42, Tiff.U16(file, 2));                    // file parses end-to-end

        var ifd0 = Tiff.ParseIfd(file, Tiff.U32(file, 4));

        // IFD0 stays the thumbnail (JPEG interchange pair intact)…
        Assert.True(ifd0.Tags.ContainsKey(513), "IFD0 thumbnail must stay");
        Assert.True(ifd0.Tags.ContainsKey(514), "IFD0 thumbnail length tag must stay");

        // …but the SubIFDs list is raw-only: no preview IFDs.
        Assert.Equal(1u, ifd0.Tags[330].Count);

        // Whole NEF-migration block suppressed: no EXIF pointer (34665), no GPS
        // (0x8825), no XMP (700), no Software (305)/Artist (315), no Baseline*.
        foreach (ushort tag in new ushort[] { 34665, 0x8825, 700, 305, 315, 50730, 50731, 50732 })
            Assert.False(ifd0.Tags.ContainsKey(tag), $"V0 IFD0 must not carry tag {tag}");

        // DNG basics stay: identity + color/exposure calibration of the product.
        Assert.Equal("Nikon Z 8", Tiff.Ascii(ifd0.Tags[272].Value));
        Assert.True(ifd0.Tags.ContainsKey(50706), "DNGVersion must stay");
        Assert.True(ifd0.Tags.ContainsKey(50708), "UniqueCameraModel must stay");
        Assert.True(ifd0.Tags.ContainsKey(50721), "ColorMatrix1 must stay");
        Assert.True(ifd0.Tags.ContainsKey(50728), "AsShotNeutral must stay");

        var raw = Tiff.ParseIfd(file, Tiff.U32(ifd0.Tags[330].Value, 0));

        // Geometry quartet + both opcode lists suppressed in the raw SubIFD.
        foreach (ushort tag in new ushort[] { 50718, 50719, 50720, 50829, 51009, 51022 })
            Assert.False(raw.Tags.ContainsKey(tag), $"V0 raw SubIFD must not carry tag {tag}");

        // The payload itself is untouched: LinearRaw RGB, 16-bit, 12 strips of 128 rows.
        Assert.Equal(34892, Tiff.U16(raw.Tags[262].Value, 0));
        Assert.Equal(3, Tiff.U16(raw.Tags[277].Value, 0));
        Assert.Equal(12u, raw.Tags[273].Count);
        Assert.Equal(128u, Tiff.U32(raw.Tags[278].Value, 0));

        // Full output (default seams = product file) still carries everything
        // — same Lazy the pre-existing facts assert against.
        var (full, _) = Finalized.Value;
        byte[] fullFile = File.ReadAllBytes(full);
        var fullIfd0 = Tiff.ParseIfd(fullFile, Tiff.U32(fullFile, 4));
        var fullRaw = Tiff.ParseIfd(fullFile, Tiff.U32(fullIfd0.Tags[330].Value, 0));
        Assert.True(fullIfd0.Tags.ContainsKey(34665), "full: EXIF pointer");
        Assert.True(fullIfd0.Tags.ContainsKey(700), "full: XMP");
        Assert.True(fullIfd0.Tags[330].Count >= 3, "full: raw + preview SubIFDs");
        Assert.True(fullRaw.Tags.ContainsKey(50829), "full: ActiveArea");
        Assert.True(fullRaw.Tags.ContainsKey(51009), "full: OpcodeList2");
    }

    /// <summary>Post-release fix 5 — passthrough seam: Finalize with
    /// passthroughRawStreams re-emits the backend's own strip bytes verbatim
    /// (every strip byte-identical, strip count/geometry unchanged) while the
    /// wrapper stays the full product file (EXIF, previews, geometry).</summary>
    [Fact]
    public void Finalize_Passthrough_BackendStripBytesVerbatimInsideFullWrapper()
    {
        var (final, _) = FinalizedPassthrough.Value;

        // The artifact's strips must be the fixture's own bytes — no re-encode.
        var (_, srcStreams) = BackendDngReader.ReadStrips(FindFixture("roi_sample.dng"));
        var (artInfo, artStreams) = BackendDngReader.ReadStrips(final);
        Assert.Equal(FixtureW, artInfo.W);
        Assert.Equal(FixtureH, artInfo.H);
        Assert.Equal(3, artInfo.Samples);
        Assert.Equal(srcStreams.Length, artStreams.Length);
        Assert.Equal(12, artStreams.Length);                 // 1536 rows / 128
        for (int i = 0; i < srcStreams.Length; i++)
            Assert.True(srcStreams[i].AsSpan().SequenceEqual(artStreams[i]),
                $"strip {i} bytes differ — passthrough must be byte-identical");

        // The wrapper is still the full product file: EXIF + previews carried,
        // raw SubIFD in strips layout with the geometry quartet.
        byte[] file = File.ReadAllBytes(final);
        var ifd0 = Tiff.ParseIfd(file, Tiff.U32(file, 4));
        Assert.True(ifd0.Tags.ContainsKey(34665), "EXIF pointer missing — wrapper must stay full");
        Assert.True(ifd0.Tags.ContainsKey(700), "XMP missing — wrapper must stay full");
        Assert.True(ifd0.Tags[330].Count >= 3, "raw + preview SubIFDs missing");
        var (_, _, v330) = ifd0.Tags[330];
        var raw = Tiff.ParseIfd(file, Tiff.U32(v330, 0));
        Assert.Equal(7, Tiff.U16(raw.Tags[259].Value, 0));   // lossless JPEG
        Assert.Equal(34892, Tiff.U16(raw.Tags[262].Value, 0));
        Assert.Equal(128u, Tiff.U32(raw.Tags[278].Value, 0));
        Assert.True(raw.Tags.ContainsKey(50829), "ActiveArea missing — wrapper must stay full");
        Assert.True(raw.Tags.ContainsKey(51009), "OpcodeList2 missing — wrapper must stay full");
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

        /// <summary>Decodes the finalized DNG's raw SubIFD strip array back to the
    /// full interleaved sample plane (ljd_decode_stream per 128-row strip; the
    /// final partial strip carries its actual row count, like the backend).</summary>
    private static ushort[] DecodeFinalRawStrips(string path, out int w, out int h, out int samples)
    {
        byte[] file = File.ReadAllBytes(path);
        var ifd0 = Tiff.ParseIfd(file, Tiff.U32(file, 4));
        var (_, _, v330) = ifd0.Tags[330];
        var raw = Tiff.ParseIfd(file, Tiff.U32(v330, 0));
        w = (int)Tiff.U32(raw.Tags[256].Value, 0);
        h = (int)Tiff.U32(raw.Tags[257].Value, 0);
        samples = Tiff.U16(raw.Tags[277].Value, 0);
        int rowsPerStrip = (int)Tiff.U32(raw.Tags[278].Value, 0);

        var offsets = raw.Tags[273].Value;                          // StripOffsets blob
        var counts = raw.Tags[279].Value;                           // StripByteCounts blob
        var pixels = new ushort[(long)w * h * samples];
        var strip = new ushort[(long)w * rowsPerStrip * samples];
        for (int i = 0; i < raw.Tags[273].Count; i++)
        {
            int rows = Math.Min(rowsPerStrip, h - i * rowsPerStrip);
            uint off = Tiff.U32(offsets, i * 4), len = Tiff.U32(counts, i * 4);
            long decoded = DngLjpegNative.DecodeStream(file[(int)off..(int)(off + len)], strip, 0,
                out uint sw, out uint sh, out uint sc);
            Assert.Equal((long)w * rows * samples, decoded);
            Assert.Equal((uint)w, sw);
            Assert.Equal((uint)rows, sh);
            Assert.Equal((uint)samples, sc);

            // Strips tile the image vertically: band rows are contiguous row-major.
            Array.Copy(strip, 0, pixels, (long)i * rowsPerStrip * w * samples,
                (long)rows * w * samples);
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
