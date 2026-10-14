using System.Globalization;
using System.Reflection;

namespace Jsr4Z8.Core.Dark;

/// <summary>One row of the embedded Nikon Z8 dark preset table: crop window (raw
/// sensor dims), RAW compression type (MakerNote NEFCompression), nominal ISO, and
/// the two quantities a dark file contributes to the reconstruction — the measured
/// black pedestal (DN, 4-CFA-phase mean) and the read-noise floor (DN, 2×2 Haar
/// variance median across phases, converted back to DN). Every profile is measured
/// separately: lossy HE/HE* coding and the DX high-speed readout both flatten the
/// measured noise floor dramatically at high ISO, so rows are not interchangeable.</summary>
public sealed record DarkRow(int Width, int Height, int NefCompression, double Iso, double BlackDn, double ReadNoiseDn);

/// <summary>Preset dark lookup result: the backend manual-calibration pair
/// (black → --black-level-dn, read noise → --read-noise-dn) for one (crop, compression, ISO).</summary>
public sealed record DarkMatch(double Iso, double BlackDn, double ReadNoiseDn, string Source);

/// <summary>
/// Nikon Z8 preset dark parameter table: the 124 rows extracted from
/// samples/dark_frames into the embedded resource Jsr4Z8.Core.Dark.Embedded.dark_z8.csv
/// — 31 ISOs × four measured profiles (FX lossless/HE/HE* + DX lossless), keyed by
/// raw dims + NEFCompression + ISO. Lookup is exact-match on all three keys (1e-6
/// relative ISO tolerance) — deliberately NO interpolation, because ISO 500 is the
/// sensor's dual conversion gain switch where the read-noise curve is discontinuous;
/// the values are only valid for the 14-bit readout they were measured on, which the
/// caller gates on before trusting a match.
/// </summary>
public sealed class DarkTable
{
    private const double ExactTolerance = 1e-6;

    private static readonly DarkTable DefaultInstance = FromCsv(ReadEmbeddedCsv());

    private DarkTable(IReadOnlyList<DarkRow> rows) => Rows = rows;

    /// <summary>Table rows sorted by ascending (width, height, compression, ISO).</summary>
    public IReadOnlyList<DarkRow> Rows { get; }

    public static DarkTable LoadDefault() => DefaultInstance;

    /// <summary>Every crop window (raw sensor dims) the table has measured rows for.</summary>
    public IReadOnlySet<(int Width, int Height)> CropWindows =>
        new HashSet<(int, int)>(Rows.Select(r => (r.Width, r.Height)));

    /// <summary>(crop, compression, ISO) in the table → its preset row; anything else →
    /// null (no fit, no extrapolation — the DCG discontinuity at ISO 500 makes
    /// interpolation invalid, and every profile needs its own measurement).</summary>
    public DarkMatch? Lookup(double iso, int nefCompression, int width, int height)
    {
        foreach (DarkRow row in Rows)
        {
            if (row.Width != width || row.Height != height || row.NefCompression != nefCompression)
                continue;
            if (Math.Abs(row.Iso - iso) <= ExactTolerance * Math.Max(1.0, Math.Abs(row.Iso)))
                return new DarkMatch(row.Iso, row.BlackDn, row.ReadNoiseDn, "preset");
        }

        return null;
    }

    /// <summary>Parses the embedded CSV: '#' comment lines and blanks are skipped, the
    /// header must be exactly {width, height, nef_compression, iso, black_dn, read_noise_dn}.</summary>
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

            int width = int.Parse(fields[Column(header, "width")], CultureInfo.InvariantCulture);
            int height = int.Parse(fields[Column(header, "height")], CultureInfo.InvariantCulture);
            int nef = int.Parse(fields[Column(header, "nef_compression")], CultureInfo.InvariantCulture);
            double iso = double.Parse(fields[Column(header, "iso")], CultureInfo.InvariantCulture);
            double black = double.Parse(fields[Column(header, "black_dn")], CultureInfo.InvariantCulture);
            double rn = double.Parse(fields[Column(header, "read_noise_dn")], CultureInfo.InvariantCulture);
            if (width <= 0 || height <= 0 || black < 0 || rn <= 0)
                throw new InvalidOperationException($"dark_z8.csv row has invalid values: {line}");
            rows.Add(new DarkRow(width, height, nef, iso, black, rn));
        }

        if (rows.Count < 2)
            throw new InvalidOperationException("dark_z8.csv contains no data rows.");
        rows.Sort((x, y) => x.Width != y.Width ? x.Width.CompareTo(y.Width)
            : x.Height != y.Height ? x.Height.CompareTo(y.Height)
            : x.NefCompression != y.NefCompression ? x.NefCompression.CompareTo(y.NefCompression)
            : x.Iso.CompareTo(y.Iso));
        return new DarkTable(rows);
    }

    private static int Column(string[] header, string name) =>
        Array.IndexOf(header, name) is int i && i >= 0
            ? i
            : throw new InvalidOperationException($"dark_z8.csv column not found: {name}");

    private static string ReadEmbeddedCsv()
    {
        const string resourceName = "Jsr4Z8.Core.Dark.Embedded.dark_z8.csv";
        Assembly assembly = typeof(DarkTable).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
