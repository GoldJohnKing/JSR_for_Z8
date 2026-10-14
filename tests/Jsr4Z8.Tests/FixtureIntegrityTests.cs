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
        // real captured backend stdout (protocol facts #1-#4)
        { "protocol/stdout2.log", "32f745aac1445031efcecc85b6eafbc8ca1b67bbed5c68fe8d0f3323740c9381" },
        { "protocol/stdout3.log", "58e941f4aa0715d628dc544329ae2103ac595666f003b54931e0ac57a4a5615b" },
        { "protocol/success.log", "58e941f4aa0715d628dc544329ae2103ac595666f003b54931e0ac57a4a5615b" },
        // real backend product DNG (foreign PIL/tifffile LJ stream)
        { "backend-dng/roi_sample.dng", "9d51d3b58756348bbdd8bc55dd04bbdc1eb52398b29eac0cfcffd9dd38db8127" },
        // stub backend harnesses (BackendRunnerTests)
        { "stub_backend/stub_backend.bat", "7f998740236716f8b132f7dd858c4a09aaf83f275ec9e1be9e307be9ebae5b35" },
        { "stub_backend/stub_dng.bat", "1ab02a82c3be9f4860d8d4bc8bd4ccb6443662d51b509726103bbbed4d143b84" },
        { "stub_backend/stub_fail.bat", "1460152dfb67f9d17dbae5f3ecfdca1b76868ab060836f94aae6c8d2e1a5430f" },
        { "stub_backend/stub_fail_envelope.bat", "abec05c21eb95178ba385f262b39f3736991fa4a84d61bc6a5a71fa69c467df4" },
        { "stub_backend/stub_kill.bat", "60b5feb2d3425aa8d0227d593c150fb567f434a4d1c91b5dd60192de27669c3c" },
        { "stub_backend/stub_orphan.bat", "354da2f7b509588f205071879f2dbaaf585f537665c206e65d20fb1a3ecf226f" },
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
