using System;
using Jsr4Z8.Core.Native;

namespace Jsr4Z8.Core.Dng;

/// <summary>
/// High-level pipeline: decode NEF → write complete DNG. Port of Save_Z8's
/// NefProcessor minus the BlackLevelFixer step (this project has no notch
/// repair): LibRaw decodes the mosaic pixels, the payload passes through to
/// <see cref="DngComposer"/> with metadata + assets from Tasks 2/3.
/// </summary>
public static class NefToDngConverter
{
    /// <summary>
    /// Convert one NEF to a complete lossless-JPEG DNG (Compression=7). The
    /// decoded LibRaw mosaic samples pass through unmodified — Save_Z8's
    /// compressed output stores the native 14-bit DN and only the BL/WL tags
    /// (1008/15892) describe the domain (NefProcessor parity; no fix step).
    /// </summary>
    public static NefToDngResult Convert(string nefPath, string destDngPath)
    {
        try
        {
            var meta = RawMetadataReader.Read(nefPath);
            var assets = NefAssetExtractor.Extract(nefPath);
            var (pixels, width, height, _, _, cfa, _) = LibRawNative.Decode(nefPath);

            var payload = new RawPayload
            {
                Data = pixels,
                W = width,
                H = height,
                Samples = 1,
                IsCfa = true,
                CfaPattern = CfaPhaseString(cfa),
            };
            DngComposer.Compose(payload, meta, assets, destDngPath);
            return new NefToDngResult { OutputPath = destDngPath };
        }
        catch (Exception ex)
        {
            return new NefToDngResult { OutputPath = destDngPath, Error = ex.Message };
        }
    }

    /// <summary>LibRaw 2×2 filter codes (0=R, 1=G1, 2=B, 3=G2) → phase string "RGGB".</summary>
    private static string CfaPhaseString(byte[] cfa) =>
        new(Array.ConvertAll(cfa, c => c switch { 0 => 'R', 2 => 'B', _ => 'G' }));
}
