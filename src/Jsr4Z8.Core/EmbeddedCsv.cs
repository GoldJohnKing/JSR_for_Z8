using System.Reflection;

namespace Jsr4Z8.Core;

/// <summary>Shared loader for the embedded CSV calibration tables (PTC / dark
/// presets): one manifest resource per table, whole-file text out.</summary>
internal static class EmbeddedCsv
{
    /// <summary>Relative ISO comparison tolerance for exact-key table lookups.</summary>
    public const double ExactTolerance = 1e-6;

    /// <summary>Reads an embedded CSV resource; throws naming it if absent.</summary>
    public static string Read(string resourceName)
    {
        Assembly assembly = typeof(EmbeddedCsv).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
