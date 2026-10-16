using Jsr4Z8.Core.Payload;
using Jsr4Z8.Tools;

// pack-payload: packs the ORIGINAL backend distribution into the embedded payload pair
// (payload.bin + payload.manifest.json) that PayloadManager extracts at runtime.
// The release script (build.bat Step 4) invokes:
//   pack-payload <srcDir> <embeddedDir>
// with embeddedDir = src/Jsr4Z8.Core/Payload/Embedded/ — the pair is NOT committed
// (payload.bin is ~202 MB); the Core csproj embeds both files as resources when present.
//
// Pack flow: pre-extract the onefile engine (resources\jsr_backend.exe) via
// EnginePreextractor into a temp staging dir, then PayloadPacker.Pack maps that tree
// under resources\jsr_backend_runtime\ (thinned jsr_backend.exe copy included) — the
// onefile exe itself is ExactPath-excluded, so the payload ships the runtime tree
// instead of the whole onefile exe.
//
// The pack always runs into a temp sibling (<EmbeddedDir>.tmp-pack-<pid>) first; the
// freshly generated manifest is then byte-compared against the existing embedded one
// (EmbeddedUpdater.Apply): unchanged → the existing pair is kept, changed/absent → the
// new pair is swapped in. Callers therefore run this tool unconditionally — a new
// backend version in reference/ is picked up without manually deleting payload.bin.

if (args.Length != 2)
{
    Console.Error.WriteLine("用法: pack-payload <srcDir> <embeddedDir>");
    Console.Error.WriteLine("  srcDir      原始后端包目录 (须含 resources\\jsr_backend.exe；预解包引擎树入载荷 resources\\jsr_backend_runtime\\，源 onefile exe 不再整体携带；仅打包 resources/ 树减排除清单，顶层 Bridge.exe 不携带)");
    Console.Error.WriteLine("  embeddedDir 嵌入目录 (payload.bin + payload.manifest.json；manifest 哈希比对自决换入)");
    return 2;
}

try
{
    string embeddedDir = args[1].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    string tempDir = $"{embeddedDir}.tmp-pack-{Environment.ProcessId}";
    string stagingDir = $"{embeddedDir}.tmp-preextract-{Environment.ProcessId}";
    if (Directory.Exists(tempDir))
        Directory.Delete(tempDir, recursive: true);
    if (Directory.Exists(stagingDir))
        Directory.Delete(stagingDir, recursive: true);

    try
    {
        // 预解包：onefile exe → CArchive 树（b 条目 + exe 本体拷贝）→ staging；
        // Pack 把树整树映射进 resources\jsr_backend_runtime\（确定性：树内容只由 exe 字节决定）。
        int treeFiles = EnginePreextractor.Extract(Path.Combine(args[0], "resources", "jsr_backend.exe"), stagingDir);
        Console.WriteLine($"预解包引擎树: {treeFiles} 个 CArchive 文件 + exe 本体 → resources\\jsr_backend_runtime\\");

        PayloadPackResult result = PayloadPacker.Pack(args[0], tempDir, stagingDir);
        Console.WriteLine(
            $"打包 {result.FileCount} 个文件: {result.RawBytes / 1024.0 / 1024.0:F1} MB → " +
            $"{result.PackedBytes / 1024.0 / 1024.0:F1} MB ({result.PackedBytes * 100.0 / Math.Max(1, result.RawBytes):F1}%)");
        Console.WriteLine(
            result.BackendVersion is { } v
                ? $"后端版本: v{v}（已写入 manifest backendVersion，UI 右上角随构建自动更新）"
                : "后端版本: 未能从源目录名解析（UI 右上角 Jiangtherapee Bridge 行将隐藏）");

        if (EmbeddedUpdater.Apply(embeddedDir, tempDir))
        {
            Console.WriteLine($"[4/6] payload 已换新 (hash {result.ManifestHash[..16]}…)");
            Console.WriteLine($"  payload:  {Path.Combine(embeddedDir, PayloadPacker.PayloadFileName)}");
            Console.WriteLine($"  manifest: {Path.Combine(embeddedDir, PayloadPacker.ManifestFileName)}");
        }
        else
        {
            Console.WriteLine($"[4/6] payload 未变（哈希一致），保留现有嵌入 (hash {result.ManifestHash[..16]}…)");
        }
    }
    finally
    {
        Directory.Delete(stagingDir, recursive: true);
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"打包失败: {ex.Message}");
    return 1;
}
