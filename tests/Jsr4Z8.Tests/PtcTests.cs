using Jsr4Z8.Core.Ptc;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Task 4: PtcTable — Nikon Z8 PTC index (12 embedded rows), exact table hits,
/// two-segment off-table fit, bounds rejection, and leave-one-out sanity.
/// Constants are computed from the CSV rows (brand=Nikon, model=Z8) and pinned
/// to 4 significant digits. The off-table fit is a least-squares straight line
/// in log10 space (log10 FWC = a + b·log10 ISO): the plan's own &lt;5%
/// leave-one-out check at ISO 1600 rules out a line fit in linear space
/// (32.7% error there); this is the standard power-law PTC model.
/// </summary>
public class PtcTests
{
    private static PtcTable Table() => PtcTable.LoadDefault();

    [Fact]
    public void Rows_AreTheTwelveNikonZ8Entries()
    {
        PtcTable t = Table();
        Assert.Equal(
            new double[] { 50, 100, 200, 400, 800, 1600, 3200, 6400, 12800, 25600, 51200, 102400 },
            t.Rows.Select(r => r.Iso).ToArray());
        Assert.Equal(1.5658450474509804e-05, t.Rows.First(r => r.Iso == 50).Alpha);
        Assert.Equal(0.00043720253960784314, t.Rows.First(r => r.Iso == 1600).Alpha);
        Assert.Equal(0.0009072403603921569, t.Rows.First(r => r.Iso == 3200).Alpha);
        Assert.Equal(0.037012435137254905, t.Rows.First(r => r.Iso == 102400).Alpha);
    }

    [Fact]
    public void TableIso_ReturnsExactReciprocalAlpha()
    {
        PtcMatch m50 = Table().Lookup(50)!;
        Assert.True(m50.Exact);
        Assert.Equal("table", m50.Source);
        Assert.Equal(63863, m50.FullwellE, 0); // 1/1.5658450474509804e-05 ≈ 63863 e⁻

        PtcMatch m3200 = Table().Lookup(3200)!;
        Assert.True(m3200.Exact);
        Assert.Equal("table", m3200.Source);
        Assert.Equal(1102.2, m3200.FullwellE, 1); // 1/0.0009072403603921569

        PtcMatch mMax = Table().Lookup(102400)!;
        Assert.True(mMax.Exact);
        Assert.Equal("table", mMax.Source);
        Assert.Equal(27.02, mMax.FullwellE, 2);
    }

    [Fact]
    public void ExactHit_Uses1eMinus6RelativeTolerance()
    {
        PtcTable t = Table();
        Assert.True(t.Lookup(3200 * (1 + 5e-7))!.Exact); // inside tolerance

        PtcMatch outside = t.Lookup(3200 * (1 + 2e-6))!; // beyond tolerance, fitted instead
        Assert.False(outside.Exact);
        Assert.Equal("fit-high", outside.Source);
    }

    [Fact]
    public void OffTableInsideInterval_UsesHighSegmentFit()
    {
        PtcMatch m = Table().Lookup(4000)!;
        Assert.False(m.Exact);
        Assert.Equal("fit-high", m.Source);
        Assert.Equal(871.9, m.FullwellE, 1);
    }

    [Fact]
    public void SegmentBoundary_499Low_500High()
    {
        PtcMatch lo = Table().Lookup(499)!;
        Assert.Equal("fit-low", lo.Source);
        Assert.False(lo.Exact);
        Assert.Equal(8079, lo.FullwellE, 0);

        PtcMatch hi = Table().Lookup(500)!;
        Assert.Equal("fit-high", hi.Source);
        Assert.False(hi.Exact);
        Assert.Equal(7918, hi.FullwellE, 0);
    }

    [Fact]
    public void OffTableOutsideBounds_ReturnsNull_NoExtrapolation()
    {
        Assert.Null(Table().Lookup(49));
        Assert.Null(Table().Lookup(102401));
    }

    [Fact]
    public void LeaveOneOut_At1600_PredictsTableValueWithin5Percent()
    {
        PtcTable t = Table();
        // High-segment log-log line rebuilt WITHOUT the ISO 1600 point must
        // predict the table value there: the segment data supports the fit.
        PtcRow[] without1600 = t.Rows.Where(r => r.Iso >= 800 && r.Iso != 1600).ToArray();
        double truth = 1.0 / t.Rows.First(r => r.Iso == 1600).Alpha; // ≈ 2287.27 e⁻
        double predicted = PredictLogLog(without1600, 1600);
        Assert.True(Math.Abs(predicted - truth) / truth < 0.05,
            $"LOO(1600): predicted {predicted:F2} vs table {truth:F2} " +
            $"= {Math.Abs(predicted - truth) / truth:P2} relative error (limit 5%)");
    }

    private static double PredictLogLog(IReadOnlyList<PtcRow> rows, double iso)
    {
        double mx = rows.Select(r => Math.Log10(r.Iso)).Average();
        double my = rows.Select(r => Math.Log10(1.0 / r.Alpha)).Average();
        double sxx = rows.Select(r => Math.Pow(Math.Log10(r.Iso) - mx, 2)).Sum();
        double sxy = rows.Select(r => (Math.Log10(r.Iso) - mx) * (Math.Log10(1.0 / r.Alpha) - my)).Sum();
        double b = sxy / sxx;
        double a = my - b * mx;
        return Math.Pow(10.0, a + b * Math.Log10(iso));
    }
}
