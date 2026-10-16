using Jsr4Z8.Core;
using Jsr4Z8.Core.Dng;
using Jsr4Z8.Core.Native;

namespace Jsr4Z8.Tests;

/// <summary>Test-only NEF→DNG conversion (Save_Z8 lineage): LibRaw decodes the
/// mosaic pixels, DngComposer writes the complete compressed CFA DNG in the
/// native 14-bit domain (BL=1008/WL=15892, ADC parity). Feeds DngWriterTests
/// and the "dngwriter-cfa" spec-invariant artifact.</summary>
internal static class NefToDngConverter
{
    /// <summary>Convert one NEF to a complete lossless-JPEG DNG (Compression=7).
    /// The decoded LibRaw mosaic samples pass through unmodified — the output
    /// stores the native 14-bit DN and only the BL/WL tags describe the domain.</summary>
    internal static NefToDngResult Convert(string nefPath, string destDngPath)
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
                CfaPattern = "RGGB",
            };
            DngComposer.Compose(payload, meta, assets, destDngPath, new DngWriteOptions
            {
                Compress = true,
                SamplesPerPixel = 1,
                PhotometricInterpretation = 32803,   // CFA
                BlackLevel = 1008,
                WhiteLevel = 15892,
                CfaPattern = MapCfaColors(cfa),
            });
            return new NefToDngResult { OutputPath = destDngPath };
        }
        catch (Exception ex)
        {
            return new NefToDngResult { OutputPath = destDngPath, Error = ex.Message };
        }
    }

    /// <summary>LibRaw 2×2 filter codes (0=R, 1/3=G, 2=B) → DNG CFAPattern colors (0=R, 1=G, 2=B).</summary>
    private static byte[] MapCfaColors(byte[] cfa) =>
        Array.ConvertAll(cfa, c => c switch { 0 => (byte)0, 2 => (byte)2, _ => (byte)1 });
}

/// <summary>Conversion outcome: output path, null <see cref="Error"/> on success.</summary>
internal sealed record NefToDngResult
{
    public required string OutputPath { get; init; }

    /// <summary>Failure description; null on success.</summary>
    public string? Error { get; init; }
}
