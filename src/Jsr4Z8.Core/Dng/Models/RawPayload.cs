namespace Jsr4Z8.Core.Dng;

/// <summary>
/// Pixel payload handed to <see cref="DngComposer"/> — the replaceable pixel
/// source behind the DNG pipeline: Task 8 feeds the LibRaw-decoded NEF CFA
/// mosaic (Samples=1, IsCfa=true); Task 9 feeds the backend's LinearRaw RGB
/// (Samples=3, IsCfa=false).
/// </summary>
public sealed record RawPayload
{
    /// <summary>Interleaved samples, row-major; length must be W·H·Samples.</summary>
    public required ushort[] Data { get; init; }

    public required int W { get; init; }

    public required int H { get; init; }

    /// <summary>Samples per pixel: 1 = CFA mosaic, 3 = LinearRaw RGB.</summary>
    public required int Samples { get; init; }

    public required bool IsCfa { get; init; }

    /// <summary>2×2 mosaic phase, row-major, chars from {R,G,B} (e.g. "RGGB");
    /// null when <see cref="IsCfa"/> is false.</summary>
    public string? CfaPattern { get; init; }
}
