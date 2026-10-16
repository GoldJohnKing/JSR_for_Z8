using Jsr4Z8.Core;

namespace Jsr4Z8.Tests;

/// <summary>Shared mock factory: Z8 raw metadata (14bit lossless, 8280×5520 FX;
/// defaults ISO 3200, 1/8000 s, f/4) and the mock read seam built on it.</summary>
internal static class MockMeta
{
    /// <summary>One mock <see cref="RawMetadata"/>; every field overridable.</summary>
    internal static RawMetadata Meta(
        double iso = 3200,
        double exposure = 0.000125,
        double aperture = 4.0,
        string make = "NIKON CORPORATION",
        string model = "NIKON Z 8",
        string dateTime = "") => new()
    {
        Make = make,
        Model = model,
        Iso = iso,
        BlackLevel = 1008,
        WhiteLevel = 16383,
        BitsPerSample = 14,
        Compression = 34713,
        NefCompression = 3,
        Width = 8280,
        Height = 5520,
        CamMul = new double[] { 1.785, 1.0, 1.793, 1.0 },
        CfaPattern = new byte[] { 0, 1, 3, 2 },
        ExposureTime = exposure,
        Aperture = aperture,
        DateTimeOriginal = dateTime,
    };

    /// <summary>Mock reader: per-file overrides win, then unreadable files throw
    /// <see cref="RawReadException"/>, everything else reads as
    /// <see cref="Meta"/>(iso, make, model, exposure).</summary>
    internal static Func<string, RawMetadata> Reader(
        Dictionary<string, RawMetadata>? perFile = null,
        HashSet<string>? unreadable = null,
        double iso = 3200,
        string make = "NIKON CORPORATION",
        string model = "NIKON Z 8",
        double exposure = 0.000125) => path =>
    {
        if (unreadable is not null && unreadable.Contains(path))
            throw new RawReadException($"mock open failure (file={path})");
        if (perFile is not null && perFile.TryGetValue(path, out RawMetadata? m))
            return m;
        return Meta(iso, exposure, make: make, model: model);
    };
}
