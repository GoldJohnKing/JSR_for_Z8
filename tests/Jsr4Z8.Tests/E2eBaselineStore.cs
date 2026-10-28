using System.Security.Cryptography;
using System.Text.Json;

namespace Jsr4Z8.Tests;

/// <summary>
/// E2E 基线哈希库：四格式 FullRun 的最终 JSR_*.dng 全文件 SHA-256 逐字节断言——
/// 「我们的构建输出」必须与「原引擎输出基线」哈希一致（引擎运行间确定性已实证：
/// 同版本双跑 backend DNG 哈希相同，docs/superpowers/notes/2026-10-28-cpu-alignment-gpu-port-eval.md）。
/// 基线文件 tests/Jsr4Z8.Tests/fixtures/e2e/baselines.json 随仓库；哈希只在同一批样本 NEF
/// 与同一引擎构建上有意义——引擎/算法/标定发生**有意识**变更后重建基线
/// （set JSR4Z8_E2E_REGEN_BASELINES=1 跑 E2E，随变更一并提交基线 diff）；
/// 无意识漂移（并行化改造、依赖升级、payload 变化）会被默认断言模式拦下。
/// 缺基线键 = 失败（防新样本组静默跳过断言）。
/// </summary>
internal static class E2eBaselineStore
{
    /// <summary>断言（默认）或重建（regen）一组基线。返回日志行（供 E2E 证据流记录）。</summary>
    public static string AssertOrRegen(string baselinePath, string group, string dngPath, bool regen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselinePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(dngPath);

        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dngPath))).ToLowerInvariant();
        Dictionary<string, string> map = Load(baselinePath);

        if (regen)
        {
            bool changed = !map.TryGetValue(group, out string? old) || old != sha;
            map[group] = sha;
            Save(baselinePath, map);
            return changed
                ? $"E2E 基线已重建[{group}]: {sha}"
                : $"E2E 基线重建后不变[{group}]: {sha}";
        }

        if (!map.TryGetValue(group, out string? expected))
            throw new Xunit.Sdk.XunitException(
                $"E2E 基线缺失[{group}] —— 有意重建基线: set JSR4Z8_E2E_REGEN_BASELINES=1 ({baselinePath})");
        if (expected != sha)
            throw new Xunit.Sdk.XunitException(
                $"E2E 产物哈希与基线不符[{group}]: 基线={expected} 实测={sha} " +
                "（引擎/算法/标定的有意识变更须以 regen 模式重建基线并随变更提交；其余漂移即回归）");
        return $"E2E 哈希与基线一致[{group}]: {sha}";
    }

    /// <summary>加载基线 JSON（group → 64 位小写十六进制 SHA-256）。文件缺失 = 空库；损坏即抛。</summary>
    private static Dictionary<string, string> Load(string baselinePath)
    {
        if (!File.Exists(baselinePath))
            return new Dictionary<string, string>();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(baselinePath));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"E2E 基线文件须为 JSON 对象: {baselinePath}");
        var map = new Dictionary<string, string>();
        foreach (JsonProperty property in doc.RootElement.EnumerateObject())
        {
            string? hex = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            if (hex is not { Length: 64 } || !hex.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
                throw new InvalidOperationException($"E2E 基线条目非法[{property.Name}]: {hex} ({baselinePath})");
            map[property.Name] = hex;
        }
        return map;
    }

    private static void Save(string baselinePath, IReadOnlyDictionary<string, string> map) =>
        File.WriteAllText(
            baselinePath,
            JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
}
