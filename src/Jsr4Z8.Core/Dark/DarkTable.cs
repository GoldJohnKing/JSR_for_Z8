using System.Globalization;

namespace Jsr4Z8.Core.Dark;

/// <summary>One row of the embedded Nikon Z8 dark preset table: RAW compression type
/// (MakerNote NEFCompression), nominal ISO, and the two quantities a dark file
/// contributes to the reconstruction — the measured black pedestal (DN, 4-CFA-phase
/// mean) and the read-noise floor (DN, √median of the 4 phase Haar variances).
/// Lossless/HE/HE* are measured separately (+3..7% quantization noise on the lossy
/// codecs); crop is deliberately NOT a key — measured crop-independence (DX vs FX
/// ≤1% at ISO ≥1000, +0.03% overall) made it a non-dimension (2026-10-15 ruling).</summary>
public sealed record DarkRow(int NefCompression, double Iso, double BlackDn, double ReadNoiseDn);

/// <summary>Preset dark lookup result: the backend manual-calibration pair
/// (black → --black-level-dn, read noise → --read-noise-dn) for one (compression, ISO).</summary>
public sealed record DarkMatch(double Iso, double BlackDn, double ReadNoiseDn, string Source);

/// <summary>
/// Nikon Z8 preset dark parameter table: the 93 rows extracted from
/// samples/dark_frames/FX/{Lossless,HE,HE Star} into the embedded resource
/// Jsr4Z8.Core.Dark.Embedded.dark_z8.csv — 31 ISOs (64 … Hi 102400) × three
/// compression types (NEFCompression 3 = lossless, 13 = HE, 14 = HE*), keyed by
/// compression + ISO. Methodology (the backend's exact estimator, v15_2.dark
/// .estimate_dark): per-CFA-phase non-overlapping 2x2 Haar h=(a-b-c+d)/2 robust
/// variance (MAD*1.4826 seed + two 4.5σ-truncation rounds), read_noise_dn =
/// √median(4 phase variances); pedestal = 4.5σ-clipped per-phase means,
/// black_dn = the 4-phase mean. Cross-checked against an independent auditor's
/// rawpy recomputation (max deviation 0.058 DN).
/// Lookup is exact-match on both keys (1e-6 relative ISO tolerance) — deliberately
/// NO interpolation, because ISO 500 is the sensor's dual conversion gain switch
/// where the read-noise curve is discontinuous. Crop-blind by ruling: any crop
/// window (FX/DX/5:4/16:9) takes these FX-measured values; the caller still gates
/// on the 14-bit readout before trusting a match.
/// </summary>
public sealed class DarkTable
{
    private const double ExactTolerance = EmbeddedCsv.ExactTolerance;

    private static readonly DarkTable DefaultInstance = FromCsv(
        EmbeddedCsv.Read("Jsr4Z8.Core.Dark.Embedded.dark_z8.csv"));

    private DarkTable(IReadOnlyList<DarkRow> rows) => Rows = rows;

    /// <summary>Table rows sorted by ascending (compression, ISO).</summary>
    public IReadOnlyList<DarkRow> Rows { get; }

    public static DarkTable LoadDefault() => DefaultInstance;

    /// <summary>(compression, ISO) in the table → its preset row; anything else → null
    /// (no fit, no extrapolation — the DCG discontinuity at ISO 500 makes
    /// interpolation invalid, and each compression type needs its own measurement).</summary>
    public DarkMatch? Lookup(double iso, int nefCompression)
    {
        foreach (DarkRow row in Rows)
        {
            if (row.NefCompression != nefCompression)
                continue;
            if (Math.Abs(row.Iso - iso) <= ExactTolerance * Math.Max(1.0, Math.Abs(row.Iso)))
                return new DarkMatch(row.Iso, row.BlackDn, row.ReadNoiseDn, "preset");
        }

        return null;
    }

    /// <summary>Parses the embedded CSV: '#' comment lines and blanks are skipped, the
    /// header must be exactly {nef_compression, iso, black_dn, read_noise_dn}.</summary>
    private static DarkTable FromCsv(string csv)
    {
        string[]? header = null;
        var rows = new List<DarkRow>();
        foreach (string rawLine in csv.Split('\n'))
        {
            string line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            string[] fields = line.Split(',').Select(f => f.Trim()).ToArray();
            if (header == null)
            {
                header = fields;
                header[0] = header[0].TrimStart('\uFEFF'); // tolerate a BOM on the header
                continue;
            }

            int nef = int.Parse(fields[Column(header, "nef_compression")], CultureInfo.InvariantCulture);
            double iso = double.Parse(fields[Column(header, "iso")], CultureInfo.InvariantCulture);
            double black = double.Parse(fields[Column(header, "black_dn")], CultureInfo.InvariantCulture);
            double rn = double.Parse(fields[Column(header, "read_noise_dn")], CultureInfo.InvariantCulture);
            if (black < 0 || rn <= 0)
                throw new InvalidOperationException($"dark_z8.csv row has invalid values: {line}");
            rows.Add(new DarkRow(nef, iso, black, rn));
        }

        if (rows.Count < 2)
            throw new InvalidOperationException("dark_z8.csv contains no data rows.");
        for (int i = 1; i < rows.Count; i++)
        {
            if (rows[i].NefCompression < rows[i - 1].NefCompression
                || (rows[i].NefCompression == rows[i - 1].NefCompression && rows[i].Iso <= rows[i - 1].Iso))
                throw new InvalidOperationException("dark_z8.csv rows must strictly ascend by (compression, iso).");
        }

        return new DarkTable(rows);
    }

    private static int Column(string[] header, string name) =>
        Array.IndexOf(header, name) is int i && i >= 0
            ? i
            : throw new InvalidOperationException($"dark_z8.csv column not found: {name}");
}
