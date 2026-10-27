using Jsr4Z8.Core.Dark;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Contract of the embedded preset dark table (the (RAW compression, ISO) →
/// black/read-noise lookup that replaces the user's dark file): 93 rows = 31 ISOs
/// × three compression types (NEFCompression 3 = lossless, 13 = HE, 14 = HE*),
/// measured on our own FX dark captures (samples/dark_frames/FX) with the
/// backend-exact estimator, exact-match only — ISO 500 is the dual-conversion-gain
/// switch where interpolation would be physically wrong. Crop is deliberately not
/// a key (2026-10-15 ruling): measured DX-vs-FX deviation is ≤1% at ISO ≥1000
/// (+0.03% overall), so every crop window takes these FX values.
/// </summary>
public class DarkTableTests
{
    [Fact]
    public void Table_Has93Rows_ThreeProfilesAscending_From64To102400()
    {
        var table = DarkTable.LoadDefault();

        Assert.Equal(93, table.Rows.Count);
        Assert.Equal(31, table.Rows.Count(r => r.NefCompression == 3));
        Assert.Equal(31, table.Rows.Count(r => r.NefCompression == 13));
        Assert.Equal(31, table.Rows.Count(r => r.NefCompression == 14));
        Assert.All(table.Rows, r => Assert.InRange(r.Iso, 64, 102400));
        for (int i = 1; i < table.Rows.Count; i++)
        {
            DarkRow a = table.Rows[i - 1], b = table.Rows[i];
            Assert.True(b.NefCompression > a.NefCompression || b.Iso > a.Iso, "rows must strictly ascend");
        }

        // No Lo-band rows: LibRaw normalizes Lo-extended NEFs to ISO 64 (their data
        // ≡ ISO 64), so real files hit the 64 row; a nominal Lo ISO fed directly is
        // off-table by design.
        Assert.Null(table.Lookup(32, 3));
        Assert.Null(table.Lookup(40, 3));
        Assert.Null(table.Lookup(50, 3));
    }

    [Fact]
    public void Table_Iso3200MeasuredValues()
    {
        var table = DarkTable.LoadDefault();

        // Spot values from our own 2026-10-15 re-measurement (backend-exact estimator,
        // cross-checked against an independent rawpy recomputation ≤0.058 DN).
        AssertRow(table, 3, 3200, 1005.89, 14.54);
        AssertRow(table, 13, 3200, 1005.76, 15.47);
        AssertRow(table, 14, 3200, 1005.89, 14.87);

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
    public void Table_HeaderRecordsMeasuredProvenance()
    {
        string[] lines = ReadEmbeddedCsv().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        string[] comments = lines.Where(l => l.StartsWith('#')).ToArray();

        // The table is our own measurement record: the header pins where the darks
        // came from and which estimator produced the numbers, so the table can be
        // re-derived and audited (the auditor's rawpy recompute agreed ≤0.058 DN).
        Assert.Contains(comments, l => l.Contains("dark_frames"));              // source captures
        Assert.Contains(comments, l => l.Contains("estimate_dark"));            // backend-exact estimator
        Assert.Contains(comments, l => l.Contains("Haar"));                     // its variance basis
        Assert.Contains(comments, l => l.Contains("0.058"));                    // cross-decoder agreement
        Assert.Contains(comments, l => l.Contains("不插值"));                    // exact-match rule
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
        Assert.InRange(match.BlackDn, 900, 1100);   // declared black 1008; pedestal drifts a few DN
        Assert.InRange(match.ReadNoiseDn, 1.0, 500); // 1.98 DN at ISO 64 lossless → 434 at Hi
        Assert.Equal("preset", match.Source);
    }

    [Theory]
    [InlineData(3, 51.5)]   // inside PTC bounds but never a Z8 step
    [InlineData(3, 450)]    // between rows on one side of the DCG switch
    [InlineData(13, 550)]   // HE side of the switch — no interpolation either
    [InlineData(3, 32)]     // Lo expansion reports ISO 64 and hits that row, never 32
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
    public void Table_HeSlightlyAboveLossless_NoFlatteningFloor()
    {
        var table = DarkTable.LoadDefault();

        // The corrected 2026-10-15 re-measurement (backend-exact estimator): lossy
        // HE/HE* coding adds only +3..+7% to the measured read noise, and every
        // profile stays in the hundreds of DN at high ISO — the earlier "8.5 DN
        // floor" flattening was a broken-estimator artifact and must never return.
        foreach (double iso in new[] { 3200d, 6400d, 102400d })
        {
            double fx = table.Rows.First(r => r.NefCompression == 3 && r.Iso == iso).ReadNoiseDn;
            Assert.True(fx > 8.5 * 1.5, $"FX lossless read noise at ISO {iso} must sit well above the dead 8.5-floor artifact: {fx}");
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

    private static string ReadEmbeddedCsv()
    {
        const string resourceName = "Jsr4Z8.Core.Dark.Embedded.dark_z8.csv";
        using Stream stream = typeof(DarkTable).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
