namespace Jsr4Z8.Core.Dng;

/// <summary>
/// Output-domain parameters for <see cref="DngWriter"/>. Everything Save_Z8's
/// writer hardcoded per CFA/compressed mode is parameterized here so the same
/// engine writes both the Task 8 CFA product and the Task 9 LinearRaw RGB
/// product. Defaults describe the compressed Z8 CFA profile.
/// </summary>
public sealed record DngWriteOptions
{
    /// <summary>true = 256×256 lossless-JPEG tiles (Compression=7); false = uncompressed strips.</summary>
    public bool Compress { get; init; } = true;

    /// <summary>Samples per pixel (1 = CFA, 3 = LinearRaw RGB).</summary>
    public int SamplesPerPixel { get; init; } = 1;

    /// <summary>32803 = CFA, 34892 = LinearRaw.</summary>
    public ushort PhotometricInterpretation { get; init; } = 32803;

    /// <summary>Output BlackLevel in the payload's own domain (ADC 14-bit profile: 1008).</summary>
    public int BlackLevel { get; init; } = 1008;

    /// <summary>Output WhiteLevel in the payload's own domain (ADC 14-bit profile: 15892).</summary>
    public int WhiteLevel { get; init; } = 15892;

    /// <summary>Uncompressed-mode sample expansion (CFA 14→16-bit domain = 2); 0 = keep native values.</summary>
    public int PixelBitShift { get; init; }

    /// <summary>DNG CFAPattern colors (0=Red, 1=Green, 2=Blue), 2×2 row-major,
    /// length 4; null = non-CFA payload (no CFA tags emitted).</summary>
    public byte[]? CfaPattern { get; init; }
}
