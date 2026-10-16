using Jsr4Z8.Core.Native;

namespace Jsr4Z8.Core.Dng;

/// <summary>
/// Fusion: backend JSR product DNG (demosaiced linear RGB, ×2
/// super-resolution) + reference NEF metadata → the final
/// JSR_&lt;基准帧名&gt;.dng. The backend's pixel data and color tags are
/// carried forward losslessly (re-encoded as 256×256 LJ tiles); identity,
/// EXIF/GPS/Interop/XMP, previews and lens opcodes come from the reference
/// NEF; geometry (ActiveArea/DefaultCrop) is adapted to the ×2 backend
/// domain. IFD0 attribution is rewritten: Software = "JSR for
/// Z8", no Artist tag (backend and NEF attribution suppressed at IFD0 level;
/// NEF EXIF-region photographer metadata migrates untouched).
/// </summary>
public static class JsrDngFinalizer
{
    private const string SoftwareTag = "JSR for Z8";

    /// <summary>
    /// Fuses <paramref name="backendDngPath"/> with <paramref name="referenceNefPath"/>'s
    /// metadata into <paramref name="destDir"/>/JSR_&lt;基准帧名&gt;.dng and returns the
    /// absolute output path. <paramref name="log"/> (optional)
    /// receives the AsShotNeutral cross-check warnings — null keeps the CLI/selftest
    /// behavior of writing them to Console.Error; the queue runner routes them into the
    /// job log so they are visible in the GUI's kept diagnostic dirs.
    /// <paramref name="layout"/> selects the raw payload storage (default Tiles =
    /// the shipped product behavior; Strips = backend tifffile layout parity, the
    /// ACR-compatibility bisection knob).
    /// The four <c>include*</c> toggles are diagnostic seams (ACR bisection):
    /// they suppress groups of Save_Z8-lineage wrapper
    /// features so the bisection harness can emit cumulative strip-down
    /// variants (V0 minimal → full). Defaults true = product behavior unchanged.
    /// <paramref name="passthroughRawStreams"/> and <paramref name="cropW"/>/
    /// <paramref name="cropH"/> are diagnostic seams:
    /// passthrough re-emits the backend's own LJ strip bytes verbatim (no decode/
    /// re-encode; requires Strips layout), crop truncates the decoded image to
    /// W×H from origin (0,0) before finalizing — the size ladder probing ACR's
    /// dimension threshold; the dims-mismatch fallback then yields a full-image
    /// ActiveArea/DefaultCrop pair. Harness-only; the product calls neither.
    /// </summary>
    public static string Finalize(string backendDngPath, string referenceNefPath, string destDir,
        Action<string>? log = null, RawLayout layout = RawLayout.Tiles,
        bool includeGeometryTags = true, bool includeOpcodes = true,
        bool includePreviews = true, bool includeExifGps = true,
        bool passthroughRawStreams = false, int? cropW = null, int? cropH = null)
    {
        if (passthroughRawStreams && (cropW != null || cropH != null))
            throw new ArgumentException("passthrough 与 crop 互斥（passthrough 不解码，无像素可裁）");
        if (cropW.HasValue != cropH.HasValue)
            throw new ArgumentException("cropW/cropH 必须成对设置");
        if (passthroughRawStreams && layout != RawLayout.Strips)
            throw new ArgumentException("passthroughRawStreams 只支持 RawLayout.Strips（条带字节逐条搬运）");

        BackendDng backend;
        byte[][]? rawStreams = null;
        if (passthroughRawStreams)
        {
            // No pixel decode: metadata + the encoded strip bytes verbatim.
            (backend, rawStreams) = BackendDngReader.ReadStrips(backendDngPath);
        }
        else
        {
            backend = BackendDngReader.Read(backendDngPath);
            if (cropW is int cw && cropH is int ch)
            {
                if (cw <= 0 || ch <= 0 || cw > backend.W || ch > backend.H)
                    throw new ArgumentOutOfRangeException(
                        $"crop {cw}×{ch} 越界（后端 {backend.W}×{backend.H}）");
                // Top-left crop: rows [0,ch) × cols [0,cw) repacked row-major.
                var cropped = new ushort[(long)cw * ch * backend.Samples];
                for (int row = 0; row < ch; row++)
                    Array.Copy(backend.Data, (long)row * backend.W * backend.Samples,
                        cropped, (long)row * cw * backend.Samples, (long)cw * backend.Samples);
                backend = backend with { Data = cropped, W = cw, H = ch };
            }
        }
        if (backend.Photometric != 34892 || backend.Samples != 3)
            throw new InvalidDataException(
                $"后端 DNG 不是 LinearRaw RGB（Photometric={backend.Photometric}, Samples={backend.Samples}）");

        var meta = RawMetadataReader.Read(referenceNefPath);
        var assets = NefAssetExtractor.Extract(referenceNefPath);

        CrossCheckAsShotNeutral(backend, meta, referenceNefPath, log);

        var (activeArea, crop) = Geometry(backend.W, backend.H, meta, assets);

        var payload = new RawPayload
        {
            Data = backend.Data,               // empty in passthrough mode (RawStreams carries the bytes)
            W = backend.W,
            H = backend.H,
            Samples = backend.Samples,
            IsCfa = false,
        };
        var options = new DngWriteOptions
        {
            Compress = true,
            SamplesPerPixel = backend.Samples,
            PhotometricInterpretation = backend.Photometric,
            BlackLevel = backend.BlackLevel,
            WhiteLevel = backend.WhiteLevel,
            Software = SoftwareTag,
            OmitIfd0Artist = true,
            AsShotNeutral = backend.AsShotNeutral,
            ColorMatrix1 = backend.ColorMatrix1,
            ActiveArea = activeArea,
            DefaultCrop = crop,
            Layout = layout,
            // Diagnostic seam: passthrough strips — the
            // backend's own encoded stream bytes instead of a re-encode.
            RawStreams = rawStreams,
            // Diagnostic seams: pass-through of the
            // bisection toggles; defaults true = the product file.
            IncludeGeometryTags = includeGeometryTags,
            IncludeOpcodes = includeOpcodes,
            IncludePreviews = includePreviews,
            IncludeExifGps = includeExifGps,
        };

        Directory.CreateDirectory(destDir);
        string dest = Path.GetFullPath(Path.Combine(destDir,
            $"JSR_{Path.GetFileNameWithoutExtension(referenceNefPath)}.dng"));
        DngComposer.Compose(payload, meta, assets, dest, options);
        return dest;
    }

    /// <summary>Backend AsShotNeutral vs 1/cam_mul per component: deviations
    /// beyond 1e-4 are warn-logged (never fatal — the backend value carries). Warnings
    /// go to <paramref name="log"/> when given (the queue's job log), else
    /// Console.Error (CLI/selftest).</summary>
    private static void CrossCheckAsShotNeutral(BackendDng backend, RawMetadata meta, string referenceNefPath,
        Action<string>? log)
    {
        if (backend.AsShotNeutral is not { Length: >= 3 } || meta.CamMul is not { Length: >= 3 })
            return;
        for (int i = 0; i < 3; i++)
        {
            double derived = meta.CamMul[i] > 0 ? 1.0 / meta.CamMul[i] : double.NaN;
            double deviation = Math.Abs(backend.AsShotNeutral[i] - derived);
            if (deviation > 1e-4)
            {
                string warning =
                    $"[JsrDngFinalizer] WARN AsShotNeutral[{i}] 偏差 {deviation:e2} > 1e-4: " +
                    $"后端 {backend.AsShotNeutral[i]} vs LibRaw cam_mul 推导 {derived} ({referenceNefPath})";
                if (log is { } sink)
                    sink(warning);
                else
                    Console.Error.WriteLine(warning);
            }
        }
    }

    /// <summary>
    /// ActiveArea/DefaultCrop pair in the backend's ×2 domain. The reference
    /// frame's active crop (largest NEF preview = camera active area, origin
    /// centered &amp; even in the padded frame: Z8 (12,8,8256,5504)) scales ×2
    /// for full-frame backend products (16560×11040 → ActiveArea
    /// (16,24,11024,16536), crop origin (0,0) size (16512,11008)). ROI products
    /// are interior crops of the active area — every stored pixel is active —
    /// so they get the full-image pair. DefaultCropOrigin is expressed
    /// RELATIVE to the ActiveArea origin per the DNG spec (an absolute origin
    /// would make CropOrigin+CropSize overflow the ActiveArea edge → invalid
    /// geometry; ACR rejects such files).
    /// Invariant: crop origin + size ≤ ActiveArea width/height.
    /// </summary>
    internal static (uint[] ActiveArea, (uint X, uint Y, uint W, uint H) Crop) Geometry(
        int backendW, int backendH, RawMetadata meta, NefAssets assets)
    {
        uint x = 0, y = 0, w = (uint)backendW, h = (uint)backendH;

        var reference = assets.Previews.OrderByDescending(p => (long)p.W * p.H).FirstOrDefault();
        if (backendW == 2 * meta.Width && backendH == 2 * meta.Height && reference != null)
        {
            uint rw = (uint)reference.W * 2, rh = (uint)reference.H * 2;
            // Negative-centering guard: a preview larger than the padded raw
            // frame would wrap through the (uint) casts below into an absurd
            // ActiveArea/DefaultCrop pair — reject the malformed reference
            // instead (Z8 previews are strictly inside the raw frame).
            if (rw > 2L * meta.Width || rh > 2L * meta.Height)
                throw new InvalidDataException(
                    $"基准帧预览 {reference.W}×{reference.H} 的 2 倍超过 padded raw 帧 " +
                    $"{meta.Width * 2}×{meta.Height * 2}，无法居中（预览尺寸异常）");
            x = (uint)(((2L * meta.Width - rw) / 2) & ~1L);
            y = (uint)(((2L * meta.Height - rh) / 2) & ~1L);
            w = rw;
            h = rh;
        }

        // DNG ActiveArea order: top, left, bottom, right.
        uint[] activeArea = { y, x, y + h, x + w };

        // DefaultCropOrigin must be relative to the
        // ActiveArea origin, not to the stored frame. The default crop
        // coincides with the active area, so the relative origin collapses to
        // (0,0); an absolute (x,y) would make CropOrigin+CropSize run past
        // the ActiveArea edge (Z8 ×2: 24+16512 > 16536−24) → invalid geometry,
        // ACR rejects such a file.
        uint cropOriginX = x - activeArea[1];   // cropAbsX − activeLeft
        uint cropOriginY = y - activeArea[0];   // cropAbsY − activeTop
        return (activeArea, (cropOriginX, cropOriginY, w, h));
    }
}
