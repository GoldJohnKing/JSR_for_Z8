using System.Globalization;

namespace Jsr4Z8.Core.Ptc;

/// <summary>One row of the embedded Nikon Z8 PTC index: nominal ISO and gain alpha (fullwell = 1/alpha e⁻).</summary>
public sealed record PtcRow(double Iso, double Alpha);

/// <summary>Fullwell lookup result: FullwellE in e⁻; Exact=true means the 1/alpha table value.</summary>
public sealed record PtcMatch(double FullwellE, bool Exact, string Source);

/// <summary>
/// Nikon Z8 photon-transfer table: the 12 ISO rows extracted from the reference
/// ptc_index.csv into the embedded resource Jsr4Z8.Core.Ptc.Embedded.ptc_z8.csv.
/// Table ISOs (1e-6 relative tolerance) return 1/alpha exactly; ISOs strictly inside
/// [50, 102400] but off-table return a two-segment least-squares fit — ISO &lt; 500 on
/// {50, 100, 200, 400}, ISO ≥ 500 on {800 … 102400}; ISO &lt; 50 or &gt; 102400 → null
/// (no extrapolation beyond the table bounds). The fit is a straight line in log10
/// space, log10(1/alpha) = a + b·log10(iso) — the power-law FWC ∝ ISO^b that is the
/// standard PTC model: a line fit in linear space fails the plan's own leave-one-out
/// sanity check (32.7% error at ISO 1600 vs the 5% limit) and produces a negative
/// fullwell near ISO 499.
/// </summary>
public sealed class PtcTable
{
    private const double ExactTolerance = EmbeddedCsv.ExactTolerance;
    private const double SegmentSplitIso = 500.0;

    private static readonly PtcTable DefaultInstance = FromCsv(
        EmbeddedCsv.Read("Jsr4Z8.Core.Ptc.Embedded.ptc_z8.csv"));

    private readonly (double A, double B) _lowLine;   // log10(FWC) = A + B·log10(ISO)
    private readonly (double A, double B) _highLine;

    private PtcTable(IReadOnlyList<PtcRow> rows)
    {
        Rows = rows;
        _lowLine = FitLine(Rows.Where(r => r.Iso < SegmentSplitIso).ToArray());
        _highLine = FitLine(Rows.Where(r => r.Iso >= SegmentSplitIso).ToArray());
    }

    /// <summary>Table rows sorted by ascending ISO (the 12 Nikon Z8 entries).</summary>
    public IReadOnlyList<PtcRow> Rows { get; }

    public static PtcTable LoadDefault() => DefaultInstance;

    /// <summary>Exact table ISO → 1/alpha; off-table inside [50,102400] → segment fit; outside → null.</summary>
    public PtcMatch? Lookup(double iso)
    {
        if (iso < Rows[0].Iso || iso > Rows[^1].Iso)
            return null;

        foreach (PtcRow row in Rows)
        {
            if (Math.Abs(iso - row.Iso) <= ExactTolerance * row.Iso)
                return new PtcMatch(1.0 / row.Alpha, Exact: true, Source: "table");
        }

        (double a, double b) = iso < SegmentSplitIso ? _lowLine : _highLine;
        string source = iso < SegmentSplitIso ? "fit-low" : "fit-high";
        return new PtcMatch(Math.Pow(10.0, a + b * Math.Log10(iso)), Exact: false, Source: source);
    }

    private static PtcTable FromCsv(string csv)
    {
        string[]? header = null;
        var rows = new List<PtcRow>();
        foreach (string rawLine in csv.Split('\n'))
        {
            string line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0)
                continue;
            string[] fields = line.Split(',').Select(f => f.Trim()).ToArray();
            if (header == null)
            {
                header = fields;
                header[0] = header[0].TrimStart('\uFEFF'); // tolerate a BOM on the header
                continue;
            }

            string brand = fields[Column(header, "brand")];
            string model = fields[Column(header, "model")];
            if (brand != "Nikon" || model != "Z8")
                throw new InvalidOperationException($"ptc_z8.csv contains a non-Z8 row: {brand} {model}");
            double iso = double.Parse(fields[Column(header, "iso_nominal")], CultureInfo.InvariantCulture);
            double alpha = double.Parse(fields[Column(header, "alpha")], CultureInfo.InvariantCulture);
            rows.Add(new PtcRow(iso, alpha));
        }

        if (rows.Count < 2)
            throw new InvalidOperationException("ptc_z8.csv contains no data rows.");
        rows.Sort((x, y) => x.Iso.CompareTo(y.Iso));
        return new PtcTable(rows);
    }

    private static int Column(string[] header, string name) =>
        Array.IndexOf(header, name) is int i && i >= 0
            ? i
            : throw new InvalidOperationException($"ptc_z8.csv column not found: {name}");

    /// <summary>Hand-rolled least squares y = a + b·x (mean-centred) on (log10 iso, log10 1/alpha).</summary>
    private static (double A, double B) FitLine(IReadOnlyList<PtcRow> rows)
    {
        if (rows.Count < 2)
            throw new InvalidOperationException("PTC segment fit needs at least two rows.");
        double mx = 0.0, my = 0.0;
        foreach (PtcRow r in rows)
        {
            mx += Math.Log10(r.Iso);
            my += Math.Log10(1.0 / r.Alpha);
        }
        mx /= rows.Count;
        my /= rows.Count;

        double sxx = 0.0, sxy = 0.0;
        foreach (PtcRow r in rows)
        {
            double dx = Math.Log10(r.Iso) - mx;
            sxx += dx * dx;
            sxy += dx * (Math.Log10(1.0 / r.Alpha) - my);
        }
        double b = sxy / sxx;
        return (my - b * mx, b);
    }
}
