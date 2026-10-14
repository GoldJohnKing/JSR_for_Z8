namespace Jsr4Z8.Core;

/// <summary>
/// Metadata for one NEF frame. Camera identity, ISO, black/white levels,
/// dimensions, CFA and WB multipliers come from LibRaw (Task 2 handle
/// wrapper); exposure/aperture/capture time come from the NEF's EXIF IFD.
/// </summary>
public sealed record RawMetadata
{
    /// <summary>Verbatim TIFF Make tag, e.g. "NIKON CORPORATION".</summary>
    public required string Make { get; init; }

    /// <summary>Verbatim TIFF Model tag, e.g. "NIKON Z 8".</summary>
    public required string Model { get; init; }

    /// <summary>As-shot ISO speed (LibRaw imgdata.other.iso_speed).</summary>
    public required double Iso { get; init; }

    /// <summary>Black level in 14-bit DN (LibRaw imgdata.color.black).</summary>
    public required int BlackLevel { get; init; }

    /// <summary>White level (LibRaw imgdata.color.maximum).</summary>
    public required int WhiteLevel { get; init; }

    /// <summary>TIFF BitsPerSample of the raw strip (LibRaw tiff_bps): 14 standard,
    /// 12 in high-speed readout modes.</summary>
    public required int BitsPerSample { get; init; }

    /// <summary>TIFF Compression code of the raw strip (LibRaw tiff_compress).
    /// NOTE: every Z8 NEF — lossless AND HE/HE* — carries 34713 here; see
    /// <see cref="NefCompression"/> for the real discriminator.</summary>
    public required int Compression { get; init; }

    /// <summary>Nikon MakerNote NEFCompression: 3 = lossless, 13 = HE, 14 = HE*.
    /// HE/HE* share the TIFF Compression code with lossless (34713) — this is the
    /// only reliable discriminator (gates the preset dark table).</summary>
    public required int NefCompression { get; init; }

    /// <summary>Padded raw sensor width (LibRaw raw_width, e.g. 8280 for Z 8).</summary>
    public required int Width { get; init; }

    /// <summary>Padded raw sensor height (LibRaw raw_height, e.g. 5520 for Z 8).</summary>
    public required int Height { get; init; }

    /// <summary>As-shot WB multipliers (R,G1,B,G2), length 4 (LibRaw cam_mul).</summary>
    public required double[] CamMul { get; init; }

    /// <summary>2×2 CFA filter codes in row-major order, length 4 (0,1,3,2 = RGGB).</summary>
    public required byte[] CfaPattern { get; init; }

    /// <summary>Exposure time in seconds from EXIF 0x829A (0 if absent).</summary>
    public required double ExposureTime { get; init; }

    /// <summary>Aperture f-number from EXIF 0x829D (0 if absent).</summary>
    public required double Aperture { get; init; }

    /// <summary>Capture time from EXIF 0x9003, verbatim "yyyy:MM:dd HH:mm:ss"
    /// (empty if absent).</summary>
    public required string DateTimeOriginal { get; init; }
}
