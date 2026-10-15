namespace Jsr4Z8.Core.Payload;

/// <summary>
/// pack-payload 的换入裁决：把临时目录里刚打好的载荷对与 Core 项目当前嵌入对
/// （src/Jsr4Z8.Core/Payload/Embedded）比对，manifest 字节一致则保留现有嵌入，
/// 否则换入新对。发布脚本因此可以无条件调用打包器（build.bat Step 4），不再有
/// “只看存在性跳过 → 换新 reference/ 后仍带旧载荷”的陷阱。
/// </summary>
public static class EmbeddedUpdater
{
    /// <summary>
    /// 若 &lt;tempDir&gt;\payload.manifest.json 与 &lt;embeddedDir&gt; 里的现有 manifest
    /// 逐字节不同（或 embeddedDir 尚无 manifest）：先拷 manifest、再拷 payload.bin 进
    /// embeddedDir，删除 tempDir，返回 true（已换新）。字节一致：仅删除 tempDir、
    /// 保留现有对，返回 false（未变）。
    /// manifest 与 bin 两次拷贝之间的短暂非原子窗口可接受：该目录对 gitignored、
    /// 仅构建期存在，且确定性打包保证 manifest 一致 ⇒ bin 一致。
    /// </summary>
    public static bool Apply(string embeddedDir, string tempDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddedDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempDir);

        string tempManifest = Path.Combine(tempDir, PayloadPacker.ManifestFileName);
        if (!File.Exists(tempManifest))
            throw new FileNotFoundException($"临时打包目录缺少 manifest: {tempManifest}");

        string embeddedManifest = Path.Combine(embeddedDir, PayloadPacker.ManifestFileName);
        if (File.Exists(embeddedManifest)
            && File.ReadAllBytes(tempManifest).AsSpan().SequenceEqual(File.ReadAllBytes(embeddedManifest)))
        {
            Directory.Delete(tempDir, recursive: true);
            return false;
        }

        Directory.CreateDirectory(embeddedDir);
        // 先 manifest 后 bin：见类注释——间隙窗口为构建期专用、可接受。
        File.Copy(tempManifest, embeddedManifest, overwrite: true);
        File.Copy(
            Path.Combine(tempDir, PayloadPacker.PayloadFileName),
            Path.Combine(embeddedDir, PayloadPacker.PayloadFileName),
            overwrite: true);
        Directory.Delete(tempDir, recursive: true);
        return true;
    }
}
