// regen-dark-table: regenerates the embedded preset dark table
// (src/Jsr4Z8.Core/Dark/Embedded/dark_z8.csv) offline from the backend's own
// z8_presets measurement library —
// reference/Jiangtherapee-Bridge-2.3.27-Windows-x64/resources/z8_presets/manifest.json
// (schema JSR_Z8_DARK_LIBRARY_V1, 34 ISOs × 3 compressions). The z8_presets
// export is the data source of record (AGENT.md §5 决策 3).
//
//   dotnet run --project tools\regen-dark-table -- <manifest.json> [out.csv]
//
// Derivation: black_dn written as the nominal integer pedestal; read noise =
// √(mean of the profile's four per-phase temporal_variance_dn2), 2 decimals;
// backend compression 0(lossless)/3(HE*)/4(HE) → NEFCompression 3/14/13; rows
// sorted by (nef_compression, iso) ascending. The output is byte-stable — the
// header comment pins source path, SHA-256, schema, formula and mapping, with no
// timestamp, so re-running on the same manifest reproduces identical bytes.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("用法: regen-dark-table <manifest.json> [out.csv]");
    Console.Error.WriteLine("  manifest.json  后端 z8_presets 测量库 (JSR_Z8_DARK_LIBRARY_V1)");
    Console.Error.WriteLine("  out.csv        输出 CSV (默认 src/Jsr4Z8.Core/Dark/Embedded/dark_z8.csv，相对当前目录)");
    return 2;
}

string manifestPath = args[0];
string outPath = args.Length == 2
    ? args[1]
    : Path.Combine("src", "Jsr4Z8.Core", "Dark", "Embedded", "dark_z8.csv");

try
{
    int rowCount = DarkTableGenerator.Generate(manifestPath, outPath);
    string csvSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(outPath)))[..12].ToLowerInvariant();
    Console.WriteLine($"regen {rowCount} rows -> {outPath} (sha256 {csvSha}...)");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"regen failed: {ex.Message}");
    return 1;
}

/// <summary>Offline export of the backend z8_presets measurement library
/// (JSR_Z8_DARK_LIBRARY_V1) into the embedded dark preset CSV. Internal to the
/// tool; the tests exercise it end-to-end through the CLI (process mode).</summary>
internal static class DarkTableGenerator
{
    private const string LibrarySchema = "JSR_Z8_DARK_LIBRARY_V1";

    /// <summary>Backend compression code → MakerNote NEFCompression:
    /// 0 lossless → 3, 3 HE* → 14, 4 HE → 13.</summary>
    private static readonly IReadOnlyDictionary<int, int> CompressionToNef =
        new Dictionary<int, int> { [0] = 3, [3] = 14, [4] = 13 };

    /// <summary>Reads the manifest, derives the table, writes the CSV (UTF-8 without
    /// BOM, LF line endings); returns the data-row count.</summary>
    public static int Generate(string manifestPath, string outPath)
    {
        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        string sha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        (string csv, int rowCount) = BuildCsv(manifestBytes, manifestPath.Replace('\\', '/'), sha256);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(outPath, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return rowCount;
    }

    private static (string Csv, int RowCount) BuildCsv(byte[] manifestBytes, string sourcePath, string sha256)
    {
        using JsonDocument document = JsonDocument.Parse(manifestBytes);
        JsonElement root = document.RootElement;

        string schema = RequiredString(root, "schema");
        if (schema != LibrarySchema)
            throw new InvalidOperationException($"unsupported manifest schema '{schema}' (expected {LibrarySchema})");

        var rows = new List<(int Nef, int Iso, int Black, double Rn)>();
        foreach (JsonElement profile in RequiredArray(root, "profiles").EnumerateArray())
        {
            int iso = RequiredInt32(profile, "iso");
            int compression = RequiredInt32(profile, "compression");
            if (!CompressionToNef.TryGetValue(compression, out int nef))
                throw new InvalidOperationException(
                    $"profile iso={iso}: unknown backend compression {compression} (known: {string.Join("/", CompressionToNef.Keys.OrderBy(k => k))})");

            double blackDn = RequiredDouble(profile, "black_dn");
            if (blackDn < 0 || Math.Abs(blackDn - Math.Round(blackDn)) > 1e-9)
                throw new InvalidOperationException(
                    $"profile iso={iso} comp={compression}: black_dn must be a whole non-negative DN (got {blackDn})");

            JsonElement perPhase = RequiredArray(profile, "per_phase");
            int phaseCount = perPhase.GetArrayLength();
            if (phaseCount != 4)
                throw new InvalidOperationException(
                    $"profile iso={iso} comp={compression}: per_phase must hold 4 CFA phases (got {phaseCount})");
            double meanVariance = 0;
            foreach (JsonElement phase in perPhase.EnumerateArray())
            {
                double variance = RequiredDouble(phase, "temporal_variance_dn2");
                if (variance < 0)
                    throw new InvalidOperationException(
                        $"profile iso={iso} comp={compression}: negative temporal_variance_dn2 ({variance})");
                meanVariance += variance;
            }

            double readNoise = Math.Round(
                Math.Sqrt(meanVariance / phaseCount), 2, MidpointRounding.AwayFromZero);
            if (readNoise <= 0)
                throw new InvalidOperationException(
                    $"profile iso={iso} comp={compression}: read noise rounds to {readNoise} — DarkTable rejects rn <= 0");

            rows.Add((nef, iso, (int)Math.Round(blackDn), readNoise));
        }

        if (rows.Count == 0)
            throw new InvalidOperationException("manifest contains no profiles");
        if (root.TryGetProperty("count", out JsonElement countElement)
            && countElement.ValueKind == JsonValueKind.Number
            && countElement.TryGetInt32(out int declaredCount)
            && declaredCount != rows.Count)
            throw new InvalidOperationException(
                $"manifest declares count={declaredCount} but holds {rows.Count} profiles");

        var seen = new HashSet<(int Nef, int Iso)>();
        foreach ((int nef, int iso, _, _) in rows)
            if (!seen.Add((nef, iso)))
                throw new InvalidOperationException($"duplicate (nef_compression, iso) = ({nef}, {iso})");
        rows.Sort((a, b) => a.Nef != b.Nef ? a.Nef.CompareTo(b.Nef) : a.Iso.CompareTo(b.Iso));

        int isoCount = rows.Select(r => r.Iso).Distinct().Count();
        var sb = new StringBuilder();
        sb.Append("# Nikon Z8 预置黑场参数（RAW 压缩类型 + ISO 双键自动匹配用）。\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"# 来源：后端测量库 {sourcePath}（schema {schema}）离线导出，SHA-256: {sha256}。\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"# 共 {rows.Count} 行 = {isoCount} 档 ISO（Lo 32/40/50 … Hi 102400）× 3 压缩。\n");
        sb.Append("# 公式：black_dn = 标称基座（整数）；read_noise_dn = √(四相位 temporal_variance_dn2 均值)，保留 2 位。\n");
        sb.Append("# 压缩映射（后端→NEFCompression）：0→3（无损）、3→14（HE*）、4→13（HE）。\n");
        sb.Append("# 匹配：仅 (压缩, ISO) 精确匹配、不插值 —— ISO 500 是双转换增益(DCG)切换点（读噪不连续）；\n");
        sb.Append("#   仅适用 14bit 读出（black_dn=1008/white_dn=16383）；画幅不参与（后端画像=FX 实测，任意画幅取 FX 行）。\n");
        sb.Append("# Lo 档 32/40/50 为完备性纳入：LibRaw 对 Lo 扩展 NEF 报 ISO 64（数据≡ISO64，自动命中 64 行）。\n");
        sb.Append("# Hi 档键 102400 = LibRaw 对 EXIF 65535 的展开值（与 PTC 表上限一致）。\n");
        sb.Append("nef_compression,iso,black_dn,read_noise_dn\n");
        foreach ((int nef, int iso, int black, double rn) in rows)
            sb.Append(CultureInfo.InvariantCulture,
                $"{nef},{iso},{black},{rn.ToString("F2", CultureInfo.InvariantCulture)}\n");
        return (sb.ToString(), rows.Count);
    }

    private static string RequiredString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidOperationException($"manifest field '{name}' missing or not a string");

    private static int RequiredInt32(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
                ? number
                : throw new InvalidOperationException($"manifest field '{name}' missing or not an integer");

    private static double RequiredDouble(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double number)
                ? number
                : throw new InvalidOperationException($"manifest field '{name}' missing or not a number");

    private static JsonElement RequiredArray(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value
            : throw new InvalidOperationException($"manifest field '{name}' missing or not an array");
}
