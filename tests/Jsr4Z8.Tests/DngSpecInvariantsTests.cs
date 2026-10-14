using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Experience-driven spec invariants (see <see cref="DngSpecInvariants"/>):
/// every DNG our pipeline produces — the finalizer's tile-layout product, its
/// strip-layout variant, the V0 feature-suppression variant and the
/// DngWriter/NefToDngConverter CFA product — must satisfy the container-level
/// TIFF/DNG rules that the ACR rejection post-mortems showed local judges do
/// not check. Pins the incident classes: tag-order/alignment regressions,
/// unit-count/SOF geometry drift (edge padding vs actual-rows conventions),
/// and another crop-origin absolute/relative mixup (post-release fix 8).
/// </summary>
public class DngSpecInvariantsTests
{
    public static TheoryData<string> Products => new()
    {
        "finalizer-tiles",     // JsrDngFinalizer default (product behavior)
        "finalizer-strips",    // RawLayout.Strips (backend tifffile parity)
        "finalizer-v0",        // all four feature-suppression seams off
        "dngwriter-cfa",       // NefToDngConverter/DngComposer CFA product
    };

    private static string ArtifactPath(string key) => key switch
    {
        "finalizer-tiles" => FinalizerTests.Finalized.Value.Final,
        "finalizer-strips" => FinalizerTests.FinalizedStrips.Value.Final,
        "finalizer-v0" => FinalizerTests.FinalizedV0.Value.Final,
        "dngwriter-cfa" => DngWriterTests.Converted.Value.Dng,
        _ => throw new ArgumentException($"unknown product key: {key}", nameof(key)),
    };

    [Theory]
    [MemberData(nameof(Products))]
    public void ProducedDng_SatisfiesContainerInvariants(string key)
    {
        // Reuses the existing per-session artifacts (same Lazy instances the
        // Finalizer/DngWriter facts assert against) — no extra encode runs.
        // (a)-(f) all run inside the shared helper; (c)/(d) walk every strip/
        // tile's SOF3 in the real bytes, (e) is skipped only when the geometry
        // tags are absent (V0 variant).
        DngSpecInvariants.AssertDngInvariants(ArtifactPath(key));
    }
}
