namespace Jsr4Z8.Core.Dng;

/// <summary>Storage layout of the compressed raw payload.
/// Tiles = 256×256 LJ tiles (ADC -c profile, the CFA product); Strips =
/// 128-row LJ strips (backend tifffile LinearRaw parity — the ACR-accepted
/// layout of the backend's own product). Strips is valid only for compressed
/// non-CFA payloads (the writer rejects other combinations).</summary>
public enum RawLayout
{
    Tiles,
    Strips,
}

/// <summary>
/// Output-domain parameters for <see cref="DngWriter"/>. Everything Save_Z8's
/// writer hardcoded per CFA/compressed mode is parameterized here so the same
/// engine writes both the CFA product and the LinearRaw RGB product.
/// Defaults describe the compressed Z8 CFA profile.
/// </summary>
public sealed record DngWriteOptions
{
    /// <summary>true = 256×256 lossless-JPEG tiles (Compression=7); false = uncompressed strips.</summary>
    public bool Compress { get; init; } = true;

    /// <summary>Raw payload storage layout (see <see cref="RawLayout"/>).
    /// Default <see cref="RawLayout.Tiles"/> preserves the ADC -c tiled
    /// profile; LinearRaw callers may opt into <see cref="RawLayout.Strips"/>
    /// for backend layout parity.</summary>
    public RawLayout Layout { get; init; } = RawLayout.Tiles;

    /// <summary>Samples per pixel (1 = CFA, 3 = LinearRaw RGB).</summary>
    public int SamplesPerPixel { get; init; } = 1;

    /// <summary>32803 = CFA, 34892 = LinearRaw.</summary>
    public ushort PhotometricInterpretation { get; init; } = 32803;

    /// <summary>Output BlackLevel in the payload's own domain (ADC 14-bit profile: 1008); callers must set it explicitly.</summary>
    public int BlackLevel { get; init; }

    /// <summary>Output WhiteLevel in the payload's own domain (ADC 14-bit profile: 15892); callers must set it explicitly.</summary>
    public int WhiteLevel { get; init; }

    /// <summary>Uncompressed-mode sample expansion (CFA 14→16-bit domain = 2); 0 = keep native values.</summary>
    public int PixelBitShift { get; init; }

    /// <summary>DNG CFAPattern colors (0=Red, 1=Green, 2=Blue), 2×2 row-major,
    /// length 4; null = non-CFA payload (no CFA tags emitted).</summary>
    public byte[]? CfaPattern { get; init; }

    /// <summary>Forces the IFD0 Software tag (0x0131) to this value (the source
    /// NEF's Software copy is suppressed); null = keep the Save_Z8 copy
    /// behavior. The finalizer sets "JSR for Z8" (no backend
    /// attribution).
    /// </summary>
    public string? Software { get; init; }

    /// <summary>When true, no Artist tag (0x013B) is emitted at IFD0 (the source
    /// copy is suppressed). EXIF-region photographer metadata migrates untouched.
    /// </summary>
    public bool OmitIfd0Artist { get; init; }

    /// <summary>Explicit AsShotNeutral [R,G,B], serialized like the
    /// cameraWb-derived value (denominator 10⁶); overrides <c>cameraWb</c>.
    /// The finalizer carries the backend product's value verbatim.</summary>
    public float[]? AsShotNeutral { get; init; }

    /// <summary>Explicit ColorMatrix1 (9 values, row-major); null = the
    /// calibrated Z8 default. The finalizer carries the backend product's matrix.</summary>
    public double[]? ColorMatrix1 { get; init; }

    /// <summary>Explicit ActiveArea [top,left,bottom,right]; null = full stored
    /// frame. The finalizer writes the ×2-scaled sensor active area (full-frame
    /// products) or the full image (interior ROI products).</summary>
    public uint[]? ActiveArea { get; init; }

    /// <summary>Explicit DefaultCrop (origin X,Y + size W,H), emitted as a
    /// DefaultScale(1,1)/CropOrigin/CropSize pair; the origin is relative to
    /// the ActiveArea origin (DNG spec), so
    /// origin+size must fit inside the ActiveArea. null = derive the crop
    /// from the largest embedded preview (Save_Z8 behavior, ActiveArea origin
    /// (0,0) → relative and absolute origins coincide).</summary>
    public (uint X, uint Y, uint W, uint H)? DefaultCrop { get; init; }

    // ---- diagnostic seams (ACR bisection) ----
    // These four toggles exist so the bisection harness (tools/finalize-variant)
    // can emit cumulative strip-down variants of the product file
    // (V0 minimal → full) and isolate which Save_Z8-lineage wrapper feature,
    // if any, breaks ACR. Defaults true = product behavior unchanged; product
    // callers never set them.

    /// <summary>false = emit no DefaultScale/DefaultCropOrigin/DefaultCropSize
    /// and no ActiveArea (readers then treat the full stored image as both the
    /// active area and the default crop).</summary>
    public bool IncludeGeometryTags { get; init; } = true;

    /// <summary>false = emit no OpcodeList2/OpcodeList3 (lens vignette/warp
    /// opcodes from the NEF's NEFInfo).</summary>
    public bool IncludeOpcodes { get; init; } = true;

    /// <summary>false = emit no preview SubIFDs (the IFD0 thumbnail stays).</summary>
    public bool IncludePreviews { get; init; } = true;

    /// <summary>false = emit no EXIF/GPS IFDs, no XMP/Software/Artist copy, no
    /// forced Software attribution and no BaselineExposure/Noise/Sharpness —
    /// the whole NEF-migration metadata block.</summary>
    public bool IncludeExifGps { get; init; } = true;

    // ---- diagnostic seams (ACR bisection: passthrough) ----

    /// <summary>Pre-encoded lossless-JPEG strip streams emitted VERBATIM
    /// (passthrough seam): when set, the writer skips encoding and writes these
    /// bytes as the strips payload — the backend's own entropy bytes inside the
    /// product wrapper, isolating re-encode-vs-wrapper in the ACR bisection.
    /// Valid only on the compressed LinearRaw strips path (Compress=true,
    /// Layout=Strips, no CfaPattern); the pixel array is then ignored (may be
    /// empty) and stream boundaries must match the writer's RowsPerStrip=128
    /// bands (ceil(H/128) streams, short final band — the backend tifffile
    /// convention; the writer validates the count).</summary>
    public IReadOnlyList<byte[]>? RawStreams { get; init; }
}
