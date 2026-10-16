using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Jsr4Z8.Core.Dark;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Contract of the embedded preset dark table (the (RAW compression, ISO) →
/// black/read-noise lookup that replaces the user's dark file): 102 rows = 34 ISOs
/// (Lo 32/40/50 … Hi 102400) × three compression types (NEFCompression 3 = lossless,
/// 13 = HE, 14 = HE*), regenerated offline by tools/regen-dark-table from the
/// backend 2.3.27 z8_presets measurement library manifest — black = nominal
/// black_dn, read noise = √mean of the four per-phase temporal_variance_dn2,
/// compression mapping 0→3 / 3→14 / 4→13. Exact-match only — ISO 500 is the
/// dual-conversion-gain switch where interpolation would be physically wrong.
/// Crop is not a key: the library is FX-measured and read noise is
/// crop-independent, so every crop window takes these FX values.
/// </summary>
public class DarkTableTests
{
    [Fact]
    public void BackendPresetTable_Covers34Stops()
    {
        var table = DarkTable.LoadDefault();

        Assert.Equal(102, table.Rows.Count);
        Assert.Equal(34, table.Rows.Count(r => r.NefCompression == 3));
        Assert.Equal(34, table.Rows.Count(r => r.NefCompression == 13));
        Assert.Equal(34, table.Rows.Count(r => r.NefCompression == 14));
        Assert.All(table.Rows, r => Assert.InRange(r.Iso, 32, 102400));
        for (int i = 1; i < table.Rows.Count; i++)
        {
            DarkRow a = table.Rows[i - 1], b = table.Rows[i];
            Assert.True(b.NefCompression > a.NefCompression || b.Iso > a.Iso, "rows must strictly ascend");
        }

        // Lo 32/40/50 are completeness-only rows: LibRaw normalizes Lo-extended NEFs
        // to ISO 64 (their data ≡ ISO 64), so real files hit the 64 row; the Lo rows
        // are only reachable when a caller feeds the nominal Lo ISO directly.
        Assert.NotNull(table.Lookup(32, 3));
        Assert.NotNull(table.Lookup(40, 3));
        Assert.NotNull(table.Lookup(50, 3));
    }

    [Fact]
    public void BackendPresetTable_Iso3200Values()
    {
        var table = DarkTable.LoadDefault();

        // Reference values derived from the backend manifest:
        // black = nominal black_dn; RN = √mean(the 4 per-phase temporal_variance_dn2), 2 decimals.
        AssertRow(table, 3, 3200, 1008, 14.07);
        AssertRow(table, 13, 3200, 1008, 15.10);
        AssertRow(table, 14, 3200, 1008, 14.18);

        static void AssertRow(DarkTable table, int nef, double iso, double black, double rn)
        {
            DarkMatch? match = table.Lookup(iso, nef);
            Assert.NotNull(match);
            Assert.Equal(black, match!.BlackDn, 2);
            Assert.Equal(rn, match.ReadNoiseDn, 2);
            Assert.Equal("preset", match.Source);
        }
    }

    [Fact]
    public void BackendPresetTable_HeaderRecordsProvenance()
    {
        string[] lines = ReadEmbeddedCsv().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        string[] comments = lines.Where(l => l.StartsWith('#')).ToArray();

        // The table is machine-generated and must never be hand-edited: the header
        // comment pins where the numbers came from so the table can be re-derived
        // and audited against the backend release it was exported from.
        Assert.Contains(comments, l => l.Contains("z8_presets"));                  // source manifest
        Assert.Contains(comments, l => l.Contains("JSR_Z8_DARK_LIBRARY_V1"));      // its schema
        Assert.Contains(comments, l => Regex.IsMatch(l, "SHA-256.*[0-9a-f]{64}")); // its content hash
        Assert.Contains(comments, l => l.Contains("temporal_variance_dn2"));       // the formula
        Assert.Contains(comments, l => l.Contains("0→3") && l.Contains("3→14") && l.Contains("4→13")); // mapping
        Assert.Contains("nef_compression,iso,black_dn,read_noise_dn", string.Join('\n', lines)); // format unchanged
    }

    [Theory]
    [InlineData(3, 64)]      // lossless base
    [InlineData(3, 320)]     // low-gain segment tail
    [InlineData(3, 500)]     // DCG switch — must never be interpolated
    [InlineData(3, 3200)]    // the lossless burst's ISO
    [InlineData(3, 102400)]  // Hi step (LibRaw expansion of EXIF 65535)
    [InlineData(13, 64)]     // HE base
    [InlineData(13, 3200)]   // the HE burst's ISO
    [InlineData(14, 3200)]
    [InlineData(14, 102400)]
    public void Lookup_TableKey_ReturnsRow(int nef, double iso)
    {
        DarkMatch? match = DarkTable.LoadDefault().Lookup(iso, nef);

        Assert.NotNull(match);
        Assert.Equal(iso, match!.Iso);
        Assert.InRange(match.BlackDn, 900, 1100);    // nominal black 1008 (backend black_dn)
        Assert.InRange(match.ReadNoiseDn, 1.0, 500); // 1.87 DN at Lo 32 lossless → 466 at Hi
        Assert.Equal("preset", match.Source);
    }

    [Theory]
    [InlineData(3, 51.5)]   // inside PTC bounds but never a Z8 step
    [InlineData(3, 450)]    // between rows on one side of the DCG switch
    [InlineData(13, 550)]   // HE side of the switch — no interpolation either
    [InlineData(3, 35)]     // Lo band half-step — never a real Z8 ISO
    [InlineData(9, 3200)]   // an unmeasured compression code
    public void Lookup_OffTableKey_ReturnsNull_NoInterpolation(int nef, double iso)
    {
        Assert.Null(DarkTable.LoadDefault().Lookup(iso, nef));
    }

    [Fact]
    public void Lookup_DcgNeighbours_LosslessReadNoiseIsDiscontinuous()
    {
        var table = DarkTable.LoadDefault();

        // ISO 400 → 500 is the dual-conversion-gain switch: read noise DROPS by ~4×
        // while ISO rises. Locking the direction guards against a swapped-column regen.
        DarkRow iso400 = table.Rows.First(r => r.NefCompression == 3 && r.Iso == 400);
        DarkRow iso500 = table.Rows.First(r => r.NefCompression == 3 && r.Iso == 500);
        Assert.True(iso500.ReadNoiseDn < iso400.ReadNoiseDn / 2,
            $"DCG switch must drop read noise: {iso400.ReadNoiseDn} → {iso500.ReadNoiseDn}");
    }

    [Fact]
    public void Table_HeSlightlyAboveLossless_NoEncoderNoiseFloor()
    {
        var table = DarkTable.LoadDefault();

        // Backend-library facts: lossy HE coding adds +5~+13% to the measured read
        // noise (peaking at +13% in the Lo band, +6~8% at the working ISOs), HE*
        // tracks lossless within −1.5~+3%, and every profile stays in the hundreds
        // of DN at high ISO (no encoder-imposed noise floor).
        foreach (double iso in new[] { 3200d, 6400d, 102400d })
        {
            double fx = table.Rows.First(r => r.NefCompression == 3 && r.Iso == iso).ReadNoiseDn;
            Assert.True(fx > 12.75, $"FX lossless read noise at ISO {iso} must sit in the physically expected range: {fx}");
            foreach (DarkRow other in new[]
                     {
                         table.Rows.First(r => r.NefCompression == 13 && r.Iso == iso),
                         table.Rows.First(r => r.NefCompression == 14 && r.Iso == iso),
                     })
            {
                Assert.InRange(other.ReadNoiseDn / fx, 0.95, 1.15); // ±15% of lossless
            }
        }
    }

    /// <summary>
    /// The regenerator is exercised as the real CLI (process mode):
    /// a mini manifest goes in, the exact CSV rows come out — compression mapping
    /// 0→3 / 3→14 / 4→13, RN = √mean(per-phase temporal_variance_dn2) at 2 decimals,
    /// rows sorted by (nef_compression, iso), header records the manifest's SHA-256.
    /// </summary>
    [Fact]
    public void Regen_WritesExpectedCsv()
    {
        string repoRoot = FindRepoRoot();
        string outDir = Path.Combine(Path.GetTempPath(), "jsr-regen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        string manifestPath = Path.Combine(outDir, "manifest.json");
        string csvPath = Path.Combine(outDir, "dark_z8.csv");
        // RN expectations: √((100+100+100+200)/4) = √125 = 11.1803… → 11.18 (rounding
        // case); √225 = 15.00; √196 = 14.00. Profile order is deliberately unsorted.
        File.WriteAllText(manifestPath, """
            {
              "schema": "JSR_Z8_DARK_LIBRARY_V1",
              "profiles": [
                { "iso": 3200, "compression": 4, "black_dn": 1008,
                  "per_phase": [ { "temporal_variance_dn2": 225.0 }, { "temporal_variance_dn2": 225.0 },
                                 { "temporal_variance_dn2": 225.0 }, { "temporal_variance_dn2": 225.0 } ] },
                { "iso": 32, "compression": 0, "black_dn": 1008,
                  "per_phase": [ { "temporal_variance_dn2": 100.0 }, { "temporal_variance_dn2": 100.0 },
                                 { "temporal_variance_dn2": 100.0 }, { "temporal_variance_dn2": 200.0 } ] },
                { "iso": 3200, "compression": 3, "black_dn": 1008,
                  "per_phase": [ { "temporal_variance_dn2": 196.0 }, { "temporal_variance_dn2": 196.0 },
                                 { "temporal_variance_dn2": 196.0 }, { "temporal_variance_dn2": 196.0 } ] }
              ]
            }
            """);

        string output = RunRegenTool(repoRoot, $"{manifestPath} {csvPath}");

        string[] lines = File.ReadAllLines(csvPath);
        // Sorted by (nef_compression, iso): backend 0(lossless)→3, 4(HE)→13, 3(HE*)→14.
        Assert.Equal("3,32,1008,11.18", lines[^3]);
        Assert.Equal("13,3200,1008,15.00", lines[^2]);
        Assert.Equal("14,3200,1008,14.00", lines[^1]);
        Assert.Contains("nef_compression,iso,black_dn,read_noise_dn", File.ReadAllText(csvPath));
        Assert.Contains(lines, l => l.Contains("JSR_Z8_DARK_LIBRARY_V1"));
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath))).ToLowerInvariant();
        Assert.Contains(lines, l => l.Contains(sha));
        Assert.Contains("3 rows", output); // the CLI reports the regenerated row count
    }

    private static string ReadEmbeddedCsv()
    {
        using Stream stream = typeof(DarkTable).Assembly
            .GetManifestResourceStream("Jsr4Z8.Core.Dark.Embedded.dark_z8.csv")
            ?? throw new InvalidOperationException("embedded dark_z8.csv resource not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Runs the regenerator the way a maintainer does: dotnet run from the
    /// repo root (the tool project is part of the solution, so the build is warm).</summary>
    private static string RunRegenTool(string repoRoot, string args)
    {
        var psi = new ProcessStartInfo("dotnet", $"run --project tools/regen-dark-table -- {args}")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(180_000))
        {
            process.Kill();
            Assert.Fail("regen-dark-table did not exit within 180s (dotnet run).");
        }
        Assert.True(process.ExitCode == 0, $"regen-dark-table exited {process.ExitCode}: {output}");
        return output;
    }

    private static string FindRepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "JSR_for_Z8.sln")))
                return dir;
        throw new InvalidOperationException($"JSR_for_Z8.sln not found above {AppContext.BaseDirectory}.");
    }
}
