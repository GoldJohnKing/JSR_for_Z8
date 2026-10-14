using System;
using System.IO;
using System.Linq;
using Jsr4Z8.Core.Native;

namespace Jsr4Z8.Core.Dng;

/// <summary>
/// Task 9 fusion: backend JSR product DNG (demosaiced linear RGB, ×2
/// super-resolution) + reference NEF metadata → the final
/// JSR_&lt;基准帧名&gt;.dng. The backend's pixel data and color tags are
/// carried forward losslessly (re-encoded as 256×256 LJ tiles); identity,
/// EXIF/GPS/Interop/XMP, previews and lens opcodes come from the reference
/// NEF; geometry (ActiveArea/DefaultCrop) is adapted to the ×2 backend
/// domain. IFD0 attribution is rewritten per fact #9: Software = "JSR for
/// Z8", no Artist tag (backend and NEF attribution suppressed at IFD0 level;
/// NEF EXIF-region photographer metadata migrates untouched).
/// </summary>
public static class JsrDngFinalizer
{
    private const string SoftwareTag = "JSR for Z8";

    /// <summary>
    /// Fuses <paramref name="backendDngPath"/> with <paramref name="referenceNefPath"/>'s
    /// metadata into <paramref name="destDir"/>/JSR_&lt;基准帧名&gt;.dng and returns the
    /// absolute output path. <paramref name="log"/> (optional, final-review finding 8)
    /// receives the AsShotNeutral cross-check warnings — null keeps the CLI/selftest
    /// behavior of writing them to Console.Error; the queue runner routes them into the
    /// job log so they are visible in the GUI's kept diagnostic dirs.
    /// <paramref name="layout"/> selects the raw payload storage (default Tiles =
    /// the shipped product behavior; Strips = backend tifffile layout parity, the
    /// ACR-compatibility bisection knob).
    /// </summary>
    public static string Finalize(string backendDngPath, string referenceNefPath, string destDir,
        Action<string>? log = null, RawLayout layout = RawLayout.Tiles)
    {
        var backend = BackendDngReader.Read(backendDngPath);
        if (backend.Photometric != 34892 || backend.Samples != 3)
            throw new InvalidDataException(
                $"后端 DNG 不是 LinearRaw RGB（Photometric={backend.Photometric}, Samples={backend.Samples}）");

        var meta = RawMetadataReader.Read(referenceNefPath);
        var assets = NefAssetExtractor.Extract(referenceNefPath);

        CrossCheckAsShotNeutral(backend, meta, referenceNefPath, log);

        var (activeArea, crop) = Geometry(backend.W, backend.H, meta, assets);

        var payload = new RawPayload
        {
            Data = backend.Data,
            W = backend.W,
            H = backend.H,
            Samples = backend.Samples,
            IsCfa = false,
            CfaPattern = null,
        };
        var options = new DngWriteOptions
        {
            Compress = true,
            SamplesPerPixel = backend.Samples,
            PhotometricInterpretation = backend.Photometric,
            BlackLevel = backend.BlackLevel,
            WhiteLevel = backend.WhiteLevel,
            PixelBitShift = 0,
            Software = SoftwareTag,
            OmitIfd0Artist = true,
            AsShotNeutral = backend.AsShotNeutral,
            ColorMatrix1 = backend.ColorMatrix1,
            ActiveArea = activeArea,
            DefaultCrop = crop,
            Layout = layout,
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
    /// Console.Error (CLI/selftest) — final-review finding 8.</summary>
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
    /// for full-frame backend products (16560×11040 → (24,16,16512,11008)).
    /// ROI products are interior crops of the active area — every stored pixel
    /// is active — so they get the full-image pair.
    /// </summary>
    private static (uint[] ActiveArea, (uint X, uint Y, uint W, uint H) Crop) Geometry(
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
        return (activeArea, (x, y, w, h));
    }
}
