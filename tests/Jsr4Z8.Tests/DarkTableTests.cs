using Jsr4Z8.Core.Dark;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Contract of the embedded preset dark table (the (crop, RAW compression, ISO) →
/// black/read-noise lookup that replaces the user's dark file): 186 rows = 31 ISOs × six measured profiles — the full FX/DX ×
/// lossless/HE/HE* matrix (FX/{Lossless,HE,HE Star} + DX/{Lossless,HE,HE Star}), exact-match only — ISO 500 is the
/// dual-conversion-gain switch where interpolation would be physically wrong, and
/// every profile needs its own measurement: lossy coding AND the DX high-speed
/// readout both flatten the measured noise floor at high ISO.
/// </summary>
public class DarkTableTests
{
    [Fact]
    public void Table_Has186Rows_SixProfilesAscending_From64To102400()
    {
        var table = DarkTable.LoadDefault();

        Assert.Equal(186, table.Rows.Count);
        Assert.Equal(31, table.Rows.Count(r => r.NefCompression == 13 && r.Width == 5408));
        Assert.Equal(31, table.Rows.Count(r => r.NefCompression == 3 && r.Width == 8280));
        Assert.Equal(31, table.Rows.Count(r => r.NefCompression == 14 && r.Width == 8280));
        Assert.Equal(31, table.Rows.Count(r => r.NefCompression == 13 && r.Width == 8280));
        Assert.Equal(31, table.Rows.Count(r => r.NefCompression == 3 && r.Width == 5408));
        Assert.Equal(31, table.Rows.Count(r => r.NefCompression == 14 && r.Width == 5408));
        Assert.Contains((8280, 5520), table.CropWindows);
        Assert.Contains((5408, 3608), table.CropWindows);
        Assert.All(table.Rows, r => Assert.InRange(r.Iso, 64, 102400));
        for (int i = 1; i < table.Rows.Count; i++)
        {
            DarkRow a = table.Rows[i - 1], b = table.Rows[i];
            Assert.True(b.Width > a.Width || b.Height > a.Height || b.NefCompression > a.NefCompression || b.Iso > a.Iso,
                "rows must strictly ascend");
        }
    }

    [Theory]
    [InlineData(8280, 5520, 3, 64)]      // FX lossless base
    [InlineData(8280, 5520, 3, 500)]     // DCG switch — must never be interpolated
    [InlineData(8280, 5520, 3, 3200)]    // the lossless burst's ISO
    [InlineData(8280, 5520, 3, 102400)]  // Hi step (LibRaw expansion of EXIF 65535)
    [InlineData(8280, 5520, 13, 3200)]   // the HE burst's ISO
    [InlineData(8280, 5520, 14, 3200)]
    [InlineData(5408, 3608, 3, 64)]      // DX lossless base
    [InlineData(5408, 3608, 14, 64)]     // DX HE* base
    [InlineData(5408, 3608, 13, 3200)]   // DX HE — completes the matrix
    [InlineData(5408, 3608, 3, 3200)]
    [InlineData(5408, 3608, 3, 102400)]
    public void Lookup_TableKey_ReturnsRow(int w, int h, int nef, double iso)
    {
        DarkMatch? match = DarkTable.LoadDefault().Lookup(iso, nef, w, h);

        Assert.NotNull(match);
        Assert.Equal(iso, match!.Iso);
        Assert.InRange(match.BlackDn, 900, 1100);   // declared black 1008; pedestal drifts a few DN
        Assert.InRange(match.ReadNoiseDn, 1.0, 500); // 1.98 DN at ISO 64 lossless → 433.96 at Hi
        Assert.Equal("preset", match.Source);
    }

    [Theory]
    [InlineData(8280, 5520, 3, 51.5)]   // inside PTC bounds but never a Z8 step
    [InlineData(8280, 5520, 3, 450)]    // between rows on one side of the DCG switch
    [InlineData(8280, 5520, 13, 550)]   // HE side of the switch — no interpolation either
    [InlineData(5408, 3608, 3, 32)]     // Lo expansion — outside the table
    [InlineData(6900, 5520, 3, 3200)]   // 5:4 crop — no rows
    public void Lookup_OffTableKey_ReturnsNull_NoInterpolation(int w, int h, int nef, double iso)
    {
        Assert.Null(DarkTable.LoadDefault().Lookup(iso, nef, w, h));
    }

    [Fact]
    public void Lookup_DcgNeighbours_LosslessReadNoiseIsDiscontinuous()
    {
        var table = DarkTable.LoadDefault();

        // ISO 400 → 500 is the dual-conversion-gain switch: read noise DROPS by ~4×
        // while ISO rises. Locking the direction guards against a swapped-column regen.
        DarkRow iso400 = table.Rows.First(r => r.NefCompression == 3 && r.Iso == 400 && r.Width == 8280);
        DarkRow iso500 = table.Rows.First(r => r.NefCompression == 3 && r.Iso == 500 && r.Width == 8280);
        Assert.True(iso500.ReadNoiseDn < iso400.ReadNoiseDn / 2,
            $"DCG switch must drop read noise: {iso400.ReadNoiseDn} → {iso500.ReadNoiseDn}");
    }

    [Fact]
    public void Table_ProfilesAreIndependentlyMeasured_HeSlightlyAboveLossless()
    {
        var table = DarkTable.LoadDefault();

        // The corrected 2026-10-15 re-measurement (backend-exact estimator): lossy
        // HE/HE* coding adds only +3..+8% to the measured read noise (quantization
        // noise on top), DX tracks FX within a few percent, and every profile stays
        // in the hundreds of DN at high ISO — the earlier "8.5 DN floor" flattening
        // was a broken-estimator artifact and must never come back.
        foreach (double iso in new[] { 3200d, 6400d, 102400d })
        {
            double fx = table.Rows.First(r => r.NefCompression == 3 && r.Iso == iso && r.Width == 8280).ReadNoiseDn;
            Assert.True(fx > 8.5 * 1.5, $"FX lossless read noise at ISO {iso} must sit well above the dead 8.5-floor artifact: {fx}");
            foreach (DarkRow other in new[]
                     {
                         table.Rows.First(r => r.NefCompression == 13 && r.Iso == iso && r.Width == 8280),
                         table.Rows.First(r => r.NefCompression == 14 && r.Iso == iso && r.Width == 8280),
                         table.Rows.First(r => r.NefCompression == 3 && r.Iso == iso && r.Width == 5408),
                         table.Rows.First(r => r.NefCompression == 13 && r.Iso == iso && r.Width == 5408),
                         table.Rows.First(r => r.NefCompression == 14 && r.Iso == iso && r.Width == 5408),
                     })
            {
                Assert.InRange(other.ReadNoiseDn / fx, 0.95, 1.15); // ±15% of FX lossless
            }
        }
    }

}
