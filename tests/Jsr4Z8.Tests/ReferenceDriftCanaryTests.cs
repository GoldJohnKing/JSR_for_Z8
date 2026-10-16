using System.Security.Cryptography;
using System.Text.Json;
using Jsr4Z8.Core.Ptc;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Reference-tree drift canary. The untracked reference/
/// (Jiangtherapee-Bridge-2.3.27-Windows-x64, ~854 MB) is the payload source
/// ("勿改一字节" — AGENT §8) and the origin of the embedded PTC table; a new
/// backend release drops a new tree in place, and nothing currently notices
/// that the shipped payload / embedded calibration silently stopped matching
/// the reference. Checks (all skip silently when reference/ is absent — fresh
/// clones, CI):
///  (a) the embedded PtcTable has exactly the 12 Nikon Z8 rows with the
///      expected ISO ladder (a truncated/extended extraction screams here);
///  (b) the reference ptc_index.csv's Nikon Z8 rows are byte-equal to the
///      embedded resource — a backend table update without re-extraction
///      (fullwell values → wrong PTC gain → wrong super-resolution weights)
///      is the pinned incident;
///  (c) FILES-SHA256.json (the vendor's own manifest) agrees with the actual
///      reference bytes on a 20-file sample (exe, DLLs, weights, PTC csv) —
///      overlapping the packer's hashing with the manifest catches local
///      corruption of the payload source.
/// </summary>
public class ReferenceDriftCanaryTests
{
    private static readonly int[] ExpectedIsoLadder =
        { 50, 100, 200, 400, 800, 1600, 3200, 6400, 12800, 25600, 51200, 102400 };

    private static string ReferenceRoot() =>
        Path.Combine(SampleFiles.RepoRoot(), "reference", "Jiangtherapee-Bridge-2.3.27-Windows-x64");

    [Fact]
    public void EmbeddedPtcTable_ExactlyTwelveZ8Rows_ExpectedIsoLadder()
    {
        var rows = PtcTable.LoadDefault().Rows;
        Assert.Equal(12, rows.Count);
        Assert.Equal(
            ExpectedIsoLadder.Select(i => (double)i).ToList(),
            rows.Select(r => r.Iso).ToList());
        Assert.All(rows, r => Assert.True(r.Alpha > 0 && r.Alpha < 1, $"alpha out of range: {r.Alpha}"));
    }

    [Fact]
    public void ReferencePtcIndex_NikonZ8Rows_MatchEmbeddedResource()
    {
        string csv = Path.Combine(ReferenceRoot(), "resources", "_internal", "ptc_index.csv");
        if (!File.Exists(csv))
            return;                                     // fresh clone: no reference/ tree

        // The embedded ptc_z8.csv = the reference csv's header + its Nikon Z8
        // rows, extracted verbatim. Line-exact comparison: any numeric drift in
        // the vendor table (or a re-extraction with different rows) fails.
        List<string> reference = ReadLines(csv);
        Assert.True(reference.Count > 100, "ptc_index.csv suspiciously small");
        List<string> z8Rows = reference.Where(l =>
        {
            string[] f = l.Split(',');
            return f.Length > 2 && f[0] == "Nikon" && f[1] == "Z8";
        }).ToList();
        Assert.Equal(12, z8Rows.Count);

        List<string> embedded = ReadEmbeddedPtcCsv();
        Assert.Equal(reference[0], embedded[0]);        // header carried verbatim
        Assert.Equal(z8Rows, embedded.Skip(1).ToList());
    }

    [Fact]
    public void ReferenceFilesSha256Manifest_OverlapsActualBytesOnSample()
    {
        string manifestPath = Path.Combine(ReferenceRoot(), "FILES-SHA256.json");
        if (!File.Exists(manifestPath))
            return;                                     // fresh clone: no reference/ tree

        // 20-file sample spanning the payload's substance: the exe, runtime
        // DLLs, both model weight files, the PTC csv and the Python extension
        // modules. ~26 MB hashed — bounded test cost, full-trust overlap with
        // the vendor manifest the packer's own hashing builds on.
        string[] sample =
        {
            "Jiangtherapee Bridge.exe",
            "resources/_internal/python310.dll",
            "resources/_internal/libcrypto-1_1.dll",
            "resources/_internal/libssl-1_1.dll",
            "resources/_internal/MSVCP140.dll",
            "resources/_internal/ucrtbase.dll",
            "resources/_internal/wgpu_reconstruction/weights.npz",
            "resources/_internal/wgpu/assets_v15_2/spynet.npz",
            "resources/_internal/ptc_index.csv",
            "resources/_internal/_asyncio.pyd",
            "resources/_internal/_bz2.pyd",
            "resources/_internal/_cffi_backend.cp310-win_amd64.pyd",
            "resources/_internal/_ctypes.pyd",
            "resources/_internal/_decimal.pyd",
            "resources/_internal/_elementtree.pyd",
            "resources/_internal/_hashlib.pyd",
            "resources/_internal/_lzma.pyd",
            "resources/_internal/_ssl.pyd",
            "resources/_internal/pyexpat.pyd",
            "resources/_internal/unicodedata.pyd",
        };
        Assert.Equal(20, sample.Length);

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var byPath = doc.RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("path").GetString()!, StringComparer.Ordinal);
        foreach (string rel in sample)
        {
            Assert.True(byPath.TryGetValue(rel, out JsonElement entry), $"manifest lacks {rel}");
            string path = Path.Combine(ReferenceRoot(), rel);
            Assert.True(File.Exists(path), $"manifest entry {rel} has no file on disk");
            using FileStream fs = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            string expected = entry.GetProperty("sha256").GetString()!;
            long expectedBytes = entry.GetProperty("bytes").GetInt64();
            Assert.True(actual == expected,
                $"{rel}: SHA256 drifted from the vendor manifest (local corruption or an " +
                $"in-place edit of the payload source).\n  manifest {expected}\n  actual   {actual}");
            Assert.Equal(expectedBytes, fs.Length);
        }
    }

    private static List<string> ReadLines(string path)
    {
        var lines = new List<string>();
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    private static List<string> ReadEmbeddedPtcCsv()
    {
        const string resourceName = "Jsr4Z8.Core.Ptc.Embedded.ptc_z8.csv";
        using Stream stream = typeof(PtcTable).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
            if (line.TrimEnd('\r').Length > 0)
                lines.Add(line.TrimEnd('\r'));
        return lines;
    }
}
