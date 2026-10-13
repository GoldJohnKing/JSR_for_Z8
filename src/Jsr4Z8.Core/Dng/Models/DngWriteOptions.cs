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

    /// <summary>Forces the IFD0 Software tag (0x0131) to this value (the source
    /// NEF's Software copy is suppressed); null = keep the Save_Z8 copy
    /// behavior. Task 9 sets "JSR for Z8" (fact #9: no backend attribution).
    /// </summary>
    public string? Software { get; init; }

    /// <summary>When true, no Artist tag (0x013B) is emitted at IFD0 (the source
    /// copy is suppressed). EXIF-region photographer metadata migrates untouched.
    /// </summary>
    public bool OmitIfd0Artist { get; init; }

    /// <summary>Explicit AsShotNeutral [R,G,B], serialized like the
    /// cameraWb-derived value (denominator 10⁶); overrides <c>cameraWb</c>.
    /// Task 9 carries the backend product's value verbatim.</summary>
    public float[]? AsShotNeutral { get; init; }

    /// <summary>Explicit ColorMatrix1 (9 values, row-major); null = the
    /// calibrated Z8 default. Task 9 carries the backend product's matrix.</summary>
    public double[]? ColorMatrix1 { get; init; }

    /// <summary>Explicit ActiveArea [top,left,bottom,right]; null = full stored
    /// frame. Task 9 writes the ×2-scaled sensor active area (full-frame
    /// products) or the full image (interior ROI products).</summary>
    public uint[]? ActiveArea { get; init; }

    /// <summary>Explicit DefaultCrop (origin X,Y + size W,H), emitted as a
    /// DefaultScale(1,1)/CropOrigin/CropSize pair; null = derive the crop from
    /// the largest embedded preview (Save_Z8 behavior).</summary>
    public (uint X, uint Y, uint W, uint H)? DefaultCrop { get; init; }
}
