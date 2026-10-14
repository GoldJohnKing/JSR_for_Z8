using Jsr4Z8.Core.Payload;

// pack-payload: packs the ORIGINAL backend distribution into the embedded payload pair
// (payload.bin + payload.manifest.json) that PayloadManager extracts at runtime.
// The release script (Task 15) invokes:
//   pack-payload <srcDir> <outDir>
// with outDir = src/Jsr4Z8.Core/Payload/Embedded/ — the pair is NOT committed
// (payload.bin is ~500 MB); the Core csproj embeds both files as resources when present.

if (args.Length != 2)
{
    Console.Error.WriteLine("用法: pack-payload <srcDir> <outDir>");
    Console.Error.WriteLine("  srcDir  原始后端包目录 (须含 Jiangtherapee Bridge.exe、resources/、THIRD_PARTY_NOTICES.md)");
    Console.Error.WriteLine("  outDir  输出目录 (payload.bin + payload.manifest.json)");
    return 2;
}

try
{
    PayloadPackResult result = PayloadPacker.Pack(args[0], args[1]);
    Console.WriteLine(
        $"打包 {result.FileCount} 个文件: {result.RawBytes / 1024.0 / 1024.0:F1} MB → " +
        $"{result.PackedBytes / 1024.0 / 1024.0:F1} MB ({result.PackedBytes * 100.0 / Math.Max(1, result.RawBytes):F1}%)");
    Console.WriteLine($"  payload:  {result.PayloadPath}");
    Console.WriteLine($"  manifest: {result.ManifestPath} (hash {result.ManifestHash[..16]}…)");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"打包失败: {ex.Message}");
    return 1;
}
