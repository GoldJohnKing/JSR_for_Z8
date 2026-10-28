using System.Security.Cryptography;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// E2E 基线哈希库（E2eBaselineStore）的纯逻辑单测：匹配放行、缺键/错配/损坏即失败、
/// regen 写回后断言转绿。不触 GPU/样本——只锁 store 行为（真基线由 JSR4Z8_E2E=1 的
/// FullRun Theory 消费，见 E2eTests）。
/// </summary>
public class E2eBaselineStoreTests : IClassFixture<TestTemp>
{
    private readonly TestTemp _temp;

    public E2eBaselineStoreTests(TestTemp temp) => _temp = temp;
    [Fact]
    public void Assert_MatchingBaseline_Passes_AndLogs()
    {
        string dng = WriteDng(_temp.NewDir(), "product bytes");
        string baseline = BaselinePath(_temp.NewDir(), dng);

        string log = E2eBaselineStore.AssertOrRegen(baseline, "lossless", dng, regen: false);

        Assert.Contains("一致[lossless]", log);
        Assert.Contains(Sha(dng), log);
    }

    [Fact]
    public void Assert_MissingKey_Fails_WithRegenHint()
    {
        string dng = WriteDng(_temp.NewDir(), "product bytes");
        string baseline = BaselinePath(_temp.NewDir());

        var ex = Assert.Throws<Xunit.Sdk.XunitException>(
            () => E2eBaselineStore.AssertOrRegen(baseline, "he_compressed", dng, regen: false));
        Assert.Contains("基线缺失[he_compressed]", ex.Message);
        Assert.Contains("JSR4Z8_E2E_REGEN_BASELINES=1", ex.Message);
    }

    [Fact]
    public void Assert_Mismatch_Fails_WithBothHashes()
    {
        string dir = _temp.NewDir();
        string dng = WriteDng(dir, "product bytes");
        string baseline = BaselinePath(dir);
        File.WriteAllText(baseline, $$"""{ "lossless": "{{new string('0', 64)}}" }""" + Environment.NewLine);

        var ex = Assert.Throws<Xunit.Sdk.XunitException>(
            () => E2eBaselineStore.AssertOrRegen(baseline, "lossless", dng, regen: false));
        Assert.Contains(new string('0', 64), ex.Message);
        Assert.Contains(Sha(dng), ex.Message);
    }

    [Fact]
    public void Regen_WritesBaseline_ThenAssertPasses()
    {
        string dir = _temp.NewDir();
        string dng = WriteDng(dir, "product bytes");
        string baseline = BaselinePath(dir);

        string regenLog = E2eBaselineStore.AssertOrRegen(baseline, "lossless", dng, regen: true);
        Assert.Contains("基线已重建[lossless]", regenLog);
        Assert.Contains($"\"lossless\": \"{Sha(dng)}\"", File.ReadAllText(baseline));

        // 重建后再跑默认断言 → 放行（regen 模式重写相同值 → 报“不变”）。
        Assert.Contains("一致", E2eBaselineStore.AssertOrRegen(baseline, "lossless", dng, regen: false));
        Assert.Contains("不变", E2eBaselineStore.AssertOrRegen(baseline, "lossless", dng, regen: true));
    }

    [Fact]
    public void CorruptBaseline_Throws()
    {
        string dir = _temp.NewDir();
        string dng = WriteDng(dir, "product bytes");
        string baseline = BaselinePath(dir);
        File.WriteAllText(baseline, """{ "lossless": "nothex" }""" + Environment.NewLine);

        Assert.Throws<InvalidOperationException>(
            () => E2eBaselineStore.AssertOrRegen(baseline, "lossless", dng, regen: false));
    }

    // ---- helpers -----------------------------------------------------------

    private static string WriteDng(string dir, string content)
    {
        string path = Path.Combine(dir, "JSR_DSC_9999.dng");
        File.WriteAllText(path, content);
        return path;
    }

    private static string BaselinePath(string dir) => Path.Combine(dir, "baselines.json");

    private static string BaselinePath(string dir, string dng) // 预填当前哈希的基线
    {
        string path = BaselinePath(dir);
        File.WriteAllText(path, $$"""{ "lossless": "{{Sha(dng)}}" }""" + Environment.NewLine);
        return path;
    }

    private static string Sha(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
