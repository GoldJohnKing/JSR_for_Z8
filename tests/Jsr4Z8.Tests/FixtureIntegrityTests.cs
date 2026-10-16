using System.Security.Cryptography;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Fixture integrity canary: the committed fixtures are load-bearing ground
/// truth — protocol tests pin the wire shapes against the real captured logs,
/// finalizer/foreign-stream tests decode the real backend DNG, runner tests
/// exec the stub bats. A silent edit/regeneration (line-ending rewrite,
/// re-capture, editor mangle) would shift what the suites measure without any
/// test noticing. SHA256-lock every committed fixture; a failure means the
/// bytes changed — regenerate deliberately and update the hash constant, or
/// investigate.
/// </summary>
public class FixtureIntegrityTests
{
    /// <summary>(repo-relative fixture path, SHA256 of the committed bytes).</summary>
    public static TheoryData<string, string> LockedFixtures => new()
    {
        // real captured backend stdout (the Bridge-era envelope logs)
        { "protocol/stdout2.log", "32f745aac1445031efcecc85b6eafbc8ca1b67bbed5c68fe8d0f3323740c9381" },
        { "protocol/stdout3.log", "58e941f4aa0715d628dc544329ae2103ac595666f003b54931e0ac57a4a5615b" },
        // real captured DIRECT-RUN stdout (2026-10-15 probe §7: bare lines, zero envelopes)
        { "protocol/direct_success.log", "eaafc02c391b6f253495238c5ca79c6355b9df34bc539d99840aba283ce60cf0" },
        { "protocol/direct_fail.log", "1c867d7279a2fbb37056c6c4b3cdfa1706d52f628f34ac2b8b98d06ee27dd288" },
        // real backend product DNG (foreign PIL/tifffile LJ stream)
        { "backend-dng/roi_sample.dng", "9d51d3b58756348bbdd8bc55dd04bbdc1eb52398b29eac0cfcffd9dd38db8127" },
        // stub backend engines (BackendRunnerTests; stub_dual_* replicate the
        // engine's same-argv child self-respawn, probe §7.3)
        { "stub_backend/stub_backend.bat", "a7717c7263a00e29fbc850a81d41ce609d4852e201b956f9c84e0375b47644c4" },
        { "stub_backend/stub_dng.bat", "29746cf8057b25fdf616e7c1047f9f2bb9a1b202a9451f04db02a421955a94cf" },
        { "stub_backend/stub_dual.bat", "a4112811ca31765f0e082cb4e86a74f64eac03661fcecc6e84d5b6ed2b653ccc" },
        { "stub_backend/stub_dual_child.bat", "787d4d094392c4efdc77a0e190865a8e98c61f654bcb7989794be17f1ce7abed" },
        { "stub_backend/stub_fail.bat", "e6c96684ea0e30ee4ab9ebf100446054ede3044b184e1d44c0cd305e2fce1e73" },
        { "stub_backend/stub_kill.bat", "a89693405df8235a6108ca30b806a0575c8f83d7a00e1486246f6b5682faf4af" },
        // _PYI_* injection echo stub (2026-10-16 meipass2 experiment e1-c3): BackendRunnerTests
        // locks the spawn-injected triple character-exact (plus its own %~f0: fact A's
        // same-path lock, 2026-10-16-thin-archive evaluation) against it
        { "stub_backend/stub_env.bat", "0234636808547b9804da82e8562de60cdd9e5e7694ab66a6d9b59b34edce2571" },
    };

    [Theory]
    [MemberData(nameof(LockedFixtures))]
    public void CommittedFixture_BytesMatchLockedSha256(string relativePath, string expectedSha256)
    {
        string path = Path.Combine(FixturesRoot(), relativePath);
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        Assert.True(actual == expectedSha256,
            $"{relativePath}: SHA256 mismatch.\n  expected {expectedSha256}\n  actual   {actual}\n" +
            "The committed fixture bytes changed. These fixtures are measurement ground truth — " +
            "if the change is deliberate (fresh capture/regeneration), update the hash constant here " +
            "in the same commit and re-derive any dependent pinned facts; otherwise investigate who " +
            "touched the file.");
    }

    [Fact]
    public void FixtureDirectory_ContainsExactlyTheLockedSet()
    {
        // Guard against silent additions/removals: whatever lives under
        // fixtures/ must be pinned above (a new fixture without a lock would
        // ride unmeasured through every future regeneration).
        var onDisk = Directory.EnumerateFiles(FixturesRoot(), "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(FixturesRoot(), p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var locked = LockedFixtures.Select(r => (string)r[0])
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        Assert.True(onDisk.SequenceEqual(locked),
            $"fixtures/ tree and the locked set differ.\n  on disk only: [{string.Join(", ", onDisk.Except(locked))}]\n" +
            $"  locked only: [{string.Join(", ", locked.Except(onDisk))}]\n" +
            "Add the new fixture to LockedFixtures (with its SHA256) or remove the stale entry.");
    }

    /// <summary>The canonical committed fixtures tree (repo copy, not the partial
    /// *.log/*.bat copy the test build stages into its output directory).</summary>
    private static string FixturesRoot() =>
        Path.Combine(SampleFiles.RepoRoot(), "tests", "Jsr4Z8.Tests", "fixtures");
}
