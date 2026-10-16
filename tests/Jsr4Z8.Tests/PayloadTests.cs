using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Jsr4Z8.Core.Payload;
using Jsr4Z8.Tools;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Task 10: payload pack → extract → verify → self-heal pipeline, using the SAME
/// PayloadPacker code the release packer tool (tools/pack-payload) uses. The tests
/// build a minimal fake payload in temp dirs — the real ~393 MB backend payload is
/// never committed or embedded into test builds (Task 15 packs it at release time).
/// Windows-host tests: the manager writes/renames/deletes real directories.
/// Task 1 (2026-10-16 预解包): the fake engine is a synthetic mini PyInstaller onefile
/// exe (CArchive fixture builder below); every pack pre-extracts it through
/// EnginePreextractor (tool project — referenced from the test csproj) exactly like
/// tools/pack-payload does, so the manifest carries the resources\jsr_backend_runtime\
/// tree instead of the onefile exe itself.
/// </summary>
public class PayloadTests : IClassFixture<TestTemp>
{
    private readonly TestTemp _temp;

    public PayloadTests(TestTemp temp) => _temp = temp;

    private sealed record FakePayload(string SrcDir, string StagingDir, string OutDir, PayloadManifest Manifest)
    {
        public string PayloadBin => Path.Combine(OutDir, PayloadPacker.PayloadFileName);
        public string ManifestJson => Path.Combine(OutDir, PayloadPacker.ManifestFileName);
    }

    private sealed class CollectingProgress : IProgress<double>
    {
        public List<double> Reports { get; } = new();
        public void Report(double value) => Reports.Add(value);
    }

    // ---- Task 1（2026-10-16 预解包）: 合成微型 CArchive 夹具 ----

    /// <summary>载荷内预解包引擎树前缀（与 <see cref="PayloadPacker.EngineRuntimeDir"/> 契约一致的 OS 分隔符拼写）。</summary>
    private const string RuntimeTreePrefix = "resources\\jsr_backend_runtime\\";

    private static readonly byte[] BodyPython = Encoding.ASCII.GetBytes("python310 runtime dll body - deterministic fixture payload");
    private static readonly byte[] BodyNestedBackslash = Encoding.ASCII.GetBytes("nested dll body reached via backslash TOC separator");
    private static readonly byte[] BodyNestedForward = Encoding.ASCII.GetBytes("nested data body reached via forward TOC separator");
    private static readonly byte[] BodyPlain = Encoding.ASCII.GetBytes("uncompressed body stored raw - cmprsFlag 0");

    /// <summary>标准微型档案：4 个 b 条目（根、'\' 子目录、'/' 子目录、原样体）+ z/m/s/o 各 1（留在 exe 内）。</summary>
    private static List<MiniArchiveEntry> StandardMiniEntries() => new()
    {
        new("b", "python310.dll", BodyPython),
        new("b", "torch\\lib\\fun.dll", BodyNestedBackslash),
        new("b", "cv2/x.dat", BodyNestedForward, Compressed: false, PadBytes: 3),
        new("b", "plain.dat", BodyPlain, Compressed: false),
        new("z", "PYZ-00.pyz", Encoding.ASCII.GetBytes("pyz archive body")),
        new("m", "PIL._imaging", Encoding.ASCII.GetBytes("embedded pyd module body")),
        new("s", "pyi_rth_inspect", Encoding.ASCII.GetBytes("bootstrap script body")),
        new("o", "pyi-contents-directory _internal", Encoding.ASCII.GetBytes("runtime option value"), Compressed: false),
    };

    /// <summary>微型 CArchive 条目：类型码（b/z/m/s/o）+ NUL 结尾名（名内 '\' 为 Windows 构建分隔符）+ 体 + zlib/原样 + TOC 对齐填充。</summary>
    private sealed record MiniArchiveEntry(string Type, string Name, byte[] Body, bool Compressed = true, int PadBytes = 0);

    private static byte[] ZlibCompress(byte[] raw)
    {
        using MemoryStream buffer = new();
        using (Stream zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);
        return buffer.ToArray();
    }

    /// <summary>
    /// 构造微型 PyInstaller onefile "exe"：PE 桩 + 条目体 + 大端 TOC + MEI cookie（+可选尾部附录），
    /// 逐字段对齐实验解包器读取的 CArchive 布局（docs/superpowers/notes/2026-10-16-meipass2-experiment.md §2：
    /// cookie 末 8 KB、大端 magic/pkgLen/tocOff/tocLen/pyver/pylib(64)、TOC 恰止于 cookie、
    /// 18 B 大端条目头 structLen/pos/csize/usize/cmprsFlag/type + NUL 结尾名）。
    /// </summary>
    private static byte[] BuildMiniOnefile(IEnumerable<MiniArchiveEntry> entries, int stubLength = 64, int trailerLength = 0)
    {
        using MemoryStream bodies = new();
        var tocEntries = new List<byte[]>();
        foreach (MiniArchiveEntry entry in entries)
        {
            byte[] stored = entry.Compressed ? ZlibCompress(entry.Body) : entry.Body;
            uint pos = (uint)bodies.Position;
            bodies.Write(stored);

            using MemoryStream toc = new();
            byte[] name = Encoding.UTF8.GetBytes(entry.Name);
            WriteUInt32BE(toc, (uint)(18 + name.Length + 1 + entry.PadBytes)); // structLen：头+名+NUL+填充
            WriteUInt32BE(toc, pos);
            WriteUInt32BE(toc, (uint)stored.Length);
            WriteUInt32BE(toc, (uint)entry.Body.Length);
            toc.WriteByte((byte)(entry.Compressed ? 1 : 0));
            toc.WriteByte((byte)entry.Type[0]);
            toc.Write(name);
            toc.WriteByte(0);
            toc.Write(new byte[entry.PadBytes]);
            tocEntries.Add(toc.ToArray());
        }

        byte[] tocBytes = tocEntries.SelectMany(b => b).ToArray();
        using MemoryStream file = new();
        file.Write(new byte[stubLength]);           // PE 桩：archive_start = 桩末
        bodies.WriteTo(file);
        file.Write(tocBytes);                        // TOC 恰止于 cookie 起点
        file.Write(new byte[] { (byte)'M', (byte)'E', (byte)'I', 0x0C, 0x0B, 0x0A, 0x0B, 0x0E });
        WriteUInt32BE(file, (uint)(bodies.Length + tocBytes.Length + 88)); // pkgLen
        WriteUInt32BE(file, (uint)bodies.Length);    // tocOff（相对 archive_start）
        WriteUInt32BE(file, (uint)tocBytes.Length);  // tocLen
        WriteUInt32BE(file, 310);                    // pyver
        byte[] pylib = new byte[64];                 // pylib 名，NUL 填充到 64 B
        Encoding.ASCII.GetBytes("python310.dll").CopyTo(pylib, 0);
        file.Write(pylib);
        file.Write(new byte[trailerLength]);         // cookie 后附录（签名/填充）
        return file.ToArray();
    }

    private static void WriteUInt32BE(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    /// <summary>
    /// Fake backend distribution: a synthetic mini onefile engine
    /// (resources/jsr_backend.exe — pre-extracted at pack time into
    /// resources\jsr_backend_runtime\) and one nested resources file (exercises recursion +
    /// subdirectory creation) — plus excluded payload artifacts (the top-level Bridge
    /// shell, K14 engines, Tcl data, cv2 ffmpeg DLL, z8_presets preset, top-level
    /// notices) and junk files the packer must NOT include (README, 使用说明,
    /// FILES-SHA256.json, ThirdPartySources/).
    /// </summary>
    private string MakeFakeTree()
    {
        string root = _temp.NewDir();
        Directory.CreateDirectory(Path.Combine(root, "resources", "licenses"));
        Directory.CreateDirectory(Path.Combine(root, "resources", "_internal", "_tcl_data"));
        Directory.CreateDirectory(Path.Combine(root, "resources", "_internal", "cv2"));
        Directory.CreateDirectory(Path.Combine(root, "resources", "z8_presets"));
        Directory.CreateDirectory(Path.Combine(root, "ThirdPartySources"));

        // 预解包夹具（Task1，2026-10-16）：合成微型 onefile 引擎（4 个 b 条目 + z/m/s/o 各 1）——
        // PackTree 经 EnginePreextractor 解出 resources\jsr_backend_runtime\ 树入载荷。
        File.WriteAllBytes(Path.Combine(root, "resources", "jsr_backend.exe"), BuildMiniOnefile(StandardMiniEntries()));
        File.WriteAllText(Path.Combine(root, "resources", "licenses", "notice.txt"), "fake third-party license text");
        File.WriteAllText(Path.Combine(root, "THIRD_PARTY_NOTICES.md"), "# fake notices");

        // Excluded from the payload by the packer's exclusion list (user ruling 2026-10-15):
        // present in the fake tree so every Pack call exercises the filter.
        File.WriteAllText(Path.Combine(root, "resources", "jsr_v98_k14_backend.exe"), "k14 engine");
        File.WriteAllText(Path.Combine(root, "resources", "_internal", "_tcl_data", "x.enc"), "tcl data");
        File.WriteAllText(Path.Combine(root, "resources", "_internal", "cv2", "opencv_videoio_ffmpeg500_64.dll"), "video ffmpeg dll");
        File.WriteAllText(Path.Combine(root, "resources", "z8_presets", "Z8_0_ISO000064.dng"), "preset-dark dng");

        // Everything below must be excluded from the payload.
        // The retired top-level Bridge shell: excluded by ExactPath (直调后顶层壳不再
        // 携带——用户裁定 2026-10-15); present in the fake tree so every Pack call
        // exercises the anti-regression filter.
        File.WriteAllText(Path.Combine(root, "Jiangtherapee Bridge.exe"), "the retired Bridge shell");
        File.WriteAllText(Path.Combine(root, "README.md"), "readme");
        File.WriteAllText(Path.Combine(root, "DeepSeek助手使用说明.md"), "说明");
        File.WriteAllText(Path.Combine(root, "FILES-SHA256.json"), "{}");
        File.WriteAllText(Path.Combine(root, "ThirdPartySources", "src.zip"), "PK");

        return root;
    }

    private string TempDir() => _temp.NewDir();

    private FakePayload PackFake() => PackTree(MakeFakeTree());

    /// <summary>
    /// Packs an already-built source tree (e.g. a mutated variant of the <see cref="MakeFakeTree"/>
    /// output) into a fresh out dir, mirroring tools/pack-payload step for step: pre-extract
    /// resources\jsr_backend.exe into a fresh staging dir via EnginePreextractor, then
    /// <see cref="PayloadPacker.Pack"/> with that tree (→ resources\jsr_backend_runtime\).
    /// </summary>
    private FakePayload PackTree(string srcDir)
    {
        string staging = TempDir();
        EnginePreextractor.Extract(Path.Combine(srcDir, "resources", "jsr_backend.exe"), staging);
        string outDir = TempDir();
        PayloadPackResult result = PayloadPacker.Pack(srcDir, outDir, staging);
        using FileStream manifestStream = File.OpenRead(result.ManifestPath);
        return new FakePayload(srcDir, staging, outDir, PayloadPacker.ReadManifestJson(manifestStream));
    }

    /// <summary>manifest 条目的源侧字节来源：jsr_backend_runtime\ 条目来自 staging 树，其余来自源树。</summary>
    private static string SourceFile(FakePayload payload, string relPath) =>
        relPath.StartsWith(RuntimeTreePrefix, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(payload.StagingDir, relPath[RuntimeTreePrefix.Length..])
            : Path.Combine(payload.SrcDir, relPath);

    /// <summary>Fresh PayloadManager over the fake payload pair (simulates a new process launch — no session cache).</summary>
    private static PayloadManager NewManager(string baseRoot, FakePayload payload) =>
        new(baseRoot,
            () => File.OpenRead(payload.PayloadBin),
            () =>
            {
                using FileStream s = File.OpenRead(payload.ManifestJson);
                return PayloadPacker.ReadManifestJson(s);
            });

    [Fact]
    public void Pack_ScansOnlyBackendSources_AndWritesPayloadPair()
    {
        FakePayload payload = PackFake();

        Assert.True(File.Exists(payload.PayloadBin), "payload.bin written");
        Assert.True(File.Exists(payload.ManifestJson), "payload.manifest.json written");

        // 预解包形态（Task1，2026-10-16 用户裁定）：onefile exe 不再整体入载荷，取而代之的是
        // 预解包树 resources\jsr_backend_runtime\（4 个 b 条目 + 引擎 exe 本体拷贝）+ notice.txt。
        Assert.Equal(6, payload.Manifest.Files.Count);
        Assert.Equal(
            new[]
            {
                "resources\\jsr_backend_runtime\\cv2\\x.dat",
                "resources\\jsr_backend_runtime\\jsr_backend.exe",
                "resources\\jsr_backend_runtime\\plain.dat",
                "resources\\jsr_backend_runtime\\python310.dll",
                "resources\\jsr_backend_runtime\\torch\\lib\\fun.dll",
                "resources\\licenses\\notice.txt",
            }.OrderBy(p => p, StringComparer.Ordinal),
            payload.Manifest.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal));
        // 源 onefile exe 必须出载荷（ExactPath 排除；树内已有逐字节一致的引擎本体拷贝）。
        Assert.DoesNotContain(
            payload.Manifest.Files,
            f => f.Path.Replace('\\', '/') == "resources/jsr_backend.exe");
        // The top-level Bridge shell no longer ships (user ruling 2026-10-15): the GUI
        // invokes the engine directly, so only the resources/ tree is packed.
        Assert.DoesNotContain(
            payload.Manifest.Files,
            f => f.Path.Replace('\\', '/') == "Jiangtherapee Bridge.exe");
        // The notices are no longer packed (user ruling 2026-10-15): licenses ship inside resources/.
        Assert.DoesNotContain(
            payload.Manifest.Files,
            f => f.Path.Replace('\\', '/') == "THIRD_PARTY_NOTICES.md");

        Assert.Matches("^[0-9a-f]{64}$", payload.Manifest.Hash);
        foreach (PayloadManifestEntry entry in payload.Manifest.Files)
        {
            Assert.Matches("^[0-9a-f]{64}$", entry.Sha256);
            Assert.Equal(new FileInfo(SourceFile(payload, entry.Path)).Length, entry.Length);
        }
    }

    [Fact]
    public void Pack_ExcludesUnneededBackendFiles()
    {
        string root = MakeFakeTree();
        // The base tree already carries jsr_v98_k14_backend.exe, _tcl_data/, the cv2 ffmpeg
        // DLL, z8_presets/ and THIRD_PARTY_NOTICES.md; add the remaining exclusion-list entries.
        Directory.CreateDirectory(Path.Combine(root, "resources", "_internal", "_tk_data"));
        Directory.CreateDirectory(Path.Combine(root, "resources", "_internal", "wgpu", "__pyinstaller"));
        File.WriteAllText(Path.Combine(root, "resources", "jsr_fast_k14_backend.exe"), "fast k14 engine");
        File.WriteAllText(Path.Combine(root, "resources", "_internal", "tcl86t.dll"), "tcl");
        File.WriteAllText(Path.Combine(root, "resources", "_internal", "tk86t.dll"), "tk");
        File.WriteAllText(Path.Combine(root, "resources", "_internal", "_tk_data", "y.enc"), "tk data");
        File.WriteAllText(Path.Combine(root, "resources", "_internal", "wgpu", "__pyinstaller", "conftest.py"), "# test residue");
        // _internal 整树排除的夹具实体（无“保留兄弟”例外）：两文件随树一并被排除。
        File.WriteAllText(Path.Combine(root, "resources", "_internal", "cv2", "__init__.py"), "cv2 package");
        File.WriteAllText(Path.Combine(root, "resources", "_internal", "keep.dll"), "_internal residue");
        // Post-trim payload shape (probe 2026-10-16-payload-trim-probe.md §4): the whole
        // on-disk _internal tree goes (onefile self-extraction), together with the
        // never-spawned tool exes and their libjxl/mingw DLL family — while the engine's
        // own two metadata jsons stay (KEEP list: 0-literal refs but tiny and vendor-shipped).
        File.WriteAllText(Path.Combine(root, "resources", "jsr_backend_manifest.json"), "{} manifest");
        File.WriteAllText(Path.Combine(root, "resources", "jsr_options.json"), "{} options");
        File.WriteAllText(Path.Combine(root, "resources", "raw_bridge.exe"), "bridge tool");
        File.WriteAllText(Path.Combine(root, "resources", "jxl_worker.exe"), "jxl tool");
        foreach (string dll in new[]
        {
            "libjxl.dll", "libjxl_cms.dll", "libjxl_threads.dll",
            "libhwy.dll", "libbrotlicommon.dll", "libbrotlidec.dll", "libbrotlienc.dll",
            "libstdc++-6.dll", "libgcc_s_seh-1.dll", "libwinpthread-1.dll", "liblcms2-2.dll",
        })
            File.WriteAllText(Path.Combine(root, "resources", dll), "dll");

        FakePayload payload = PackTree(root);
        string[] paths = payload.Manifest.Files.Select(f => f.Path.Replace('\\', '/')).ToArray();

        string[] excluded =
        {
            "Jiangtherapee Bridge.exe",
            "THIRD_PARTY_NOTICES.md",
            // 预解包替代（Task1，2026-10-16 用户裁定）：源 onefile exe 出载荷。
            "resources/jsr_backend.exe",
            "resources/jsr_v98_k14_backend.exe",
            "resources/jsr_fast_k14_backend.exe",
            "resources/_internal/_tcl_data/x.enc",
            "resources/_internal/_tk_data/y.enc",
            "resources/_internal/tcl86t.dll",
            "resources/_internal/tk86t.dll",
            "resources/_internal/cv2/opencv_videoio_ffmpeg500_64.dll",
            "resources/_internal/wgpu/__pyinstaller/conftest.py",
            "resources/z8_presets/Z8_0_ISO000064.dng",
            // _internal 整树排除——cv2 包与任意驻留文件不再有“保留兄弟”例外。
            "resources/_internal/cv2/__init__.py",
            "resources/_internal/keep.dll",
            // 从未 spawn 的工具与其 libjxl/mingw DLL 家族。
            "resources/raw_bridge.exe",
            "resources/jxl_worker.exe",
            "resources/libjxl.dll",
            "resources/libjxl_cms.dll",
            "resources/libjxl_threads.dll",
            "resources/libhwy.dll",
            "resources/libbrotlicommon.dll",
            "resources/libbrotlidec.dll",
            "resources/libbrotlienc.dll",
            "resources/libstdc++-6.dll",
            "resources/libgcc_s_seh-1.dll",
            "resources/libwinpthread-1.dll",
            "resources/liblcms2-2.dll",
        };
        foreach (string path in excluded)
            Assert.False(paths.Contains(path), $"{path} 必须被排除出载荷");

        string[] kept =
        {
            "resources/jsr_backend_runtime/cv2/x.dat",
            "resources/jsr_backend_runtime/jsr_backend.exe",
            "resources/jsr_backend_runtime/plain.dat",
            "resources/jsr_backend_runtime/python310.dll",
            "resources/jsr_backend_runtime/torch/lib/fun.dll",
            "resources/licenses/notice.txt",
            "resources/jsr_backend_manifest.json",
            "resources/jsr_options.json",
        };
        Assert.Equal(kept.Length, paths.Length); // nothing else snuck in or out
        foreach (string path in kept)
            Assert.Contains(path, paths);

        // payload.bin mirrors the manifest: excluded bodies have no headers either.
        using FileStream bin = File.OpenRead(payload.PayloadBin);
        Assert.Equal(
            paths.OrderBy(p => p, StringComparer.Ordinal),
            PayloadPacker.ReadArchiveIndex(bin).Select(e => e.Path.Replace('\\', '/')).ToArray());

        // Seam contract: either separator style, case-insensitive match (Windows source trees).
        Assert.True(PayloadPacker.IsExcluded("resources\\_internal\\_Tcl_Data\\x.enc"));
        // _internal 整树规则：不再有逐文件例外——任意 _internal 内容均被前缀排除。
        Assert.True(PayloadPacker.IsExcluded("resources/_internal/cv2/opencv_world4700.dll"));
        Assert.True(PayloadPacker.IsExcluded("resources\\_internal\\numpy\\core\\_multiarray.pyd"));
        // 预解包替代（Task1）：onefile exe 两种分隔符拼写均被 ExactPath 挡住。
        Assert.True(PayloadPacker.IsExcluded("resources/jsr_backend.exe"));
        Assert.True(PayloadPacker.IsExcluded("resources\\jsr_backend.exe"));
    }

    [Fact]
    public void Pack_IsDeterministic_SameInputSameBytes()
    {
        // 两次独立 pack（各自重新预解包 staging）必须产出逐字节一致的载荷对——
        // 预解包树内容只由 onefile exe 字节决定，staging 重建不引入任何噪声。
        string src = MakeFakeTree();
        FakePayload first = PackTree(src);
        FakePayload second = PackTree(src);

        Assert.Equal(
            File.ReadAllBytes(first.PayloadBin),
            File.ReadAllBytes(second.PayloadBin));
        Assert.Equal(
            File.ReadAllBytes(first.ManifestJson),
            File.ReadAllBytes(second.ManifestJson));
    }

    // ---- Task 1（2026-10-16 预解包）: EnginePreextractor × 合成微型 CArchive ----

    [Fact]
    public void Extract_MiniCArchive_WritesBEntriesTreeAndEngineCopy()
    {
        string exe = Path.Combine(TempDir(), "jsr_backend.exe");
        File.WriteAllBytes(exe, BuildMiniOnefile(StandardMiniEntries()));
        string dest = TempDir();

        int extracted = EnginePreextractor.Extract(exe, dest);

        Assert.Equal(4, extracted); // 仅 b 条目；z/m/s/o 留在 exe 内（引导器内存读取）
        Assert.Equal(
            new[] { "cv2\\x.dat", "jsr_backend.exe", "plain.dat", "python310.dll", "torch\\lib\\fun.dll" }
                .OrderBy(p => p, StringComparer.Ordinal),
            Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(dest, p))
                .OrderBy(p => p, StringComparer.Ordinal));
        // 引擎 exe 本体逐字节原样拷贝进树根（实验配方：_PYI_ARCHIVE_FILE=<树>\jsr_backend.exe 直跑）。
        Assert.Equal(File.ReadAllBytes(exe), File.ReadAllBytes(Path.Combine(dest, "jsr_backend.exe")));
        // b 条目内容逐字节等于档案内的未压缩体（zlib 与 cmprsFlag=0 两路、'\' 与 '/' 两种名内分隔符）。
        Assert.Equal(BodyPython, File.ReadAllBytes(Path.Combine(dest, "python310.dll")));
        Assert.Equal(BodyNestedBackslash, File.ReadAllBytes(Path.Combine(dest, "torch", "lib", "fun.dll")));
        Assert.Equal(BodyNestedForward, File.ReadAllBytes(Path.Combine(dest, "cv2", "x.dat")));
        Assert.Equal(BodyPlain, File.ReadAllBytes(Path.Combine(dest, "plain.dat")));
    }

    [Fact]
    public void Extract_CookieScan_TrailerTolerated_GarbageRejected()
    {
        // cookie 之后带附录字节（签名/填充）：末 8 KB 倒序扫描仍锁定真 cookie。
        string exe = Path.Combine(TempDir(), "jsr_backend.exe");
        File.WriteAllBytes(exe, BuildMiniOnefile(StandardMiniEntries(), trailerLength: 512));
        string dest = TempDir();
        Assert.Equal(4, EnginePreextractor.Extract(exe, dest));
        Assert.True(File.Exists(Path.Combine(dest, "python310.dll")));

        // 无 MEI cookie 的普通文件 → 明确拒绝（不是 PyInstaller onefile）。
        string junk = Path.Combine(TempDir(), "jsr_backend.exe");
        File.WriteAllBytes(junk, new byte[4096]);
        Assert.Throws<InvalidDataException>(() => EnginePreextractor.Extract(junk, TempDir()));
    }

    [Fact]
    public void Extract_TocSelfCheckViolated_Rejected()
    {
        // 篡改 cookie 的 tocLen 字段 → TOC 不再恰止于 cookie → 拒绝（实验脚本自洽断言的移植）。
        byte[] archive = BuildMiniOnefile(StandardMiniEntries());
        archive[archive.Length - 88 + 16] ^= 0xFF; // cookie 内 tocLen 高字节（cookie 结构 88 B）
        string exe = Path.Combine(TempDir(), "jsr_backend.exe");
        File.WriteAllBytes(exe, archive);
        Assert.Throws<InvalidDataException>(() => EnginePreextractor.Extract(exe, TempDir()));
    }

    [Fact]
    public void Extract_CorruptedZlibBody_Rejected()
    {
        // 打坏 b 条目的 zlib 流：解压失败或长度不符 → 拒绝（usize 不变量）。
        byte[] archive = BuildMiniOnefile(StandardMiniEntries());
        archive[70] ^= 0xFF; // 默认 64 B PE 桩之后即第一个条目体（python310.dll 的 zlib 流）
        string exe = Path.Combine(TempDir(), "jsr_backend.exe");
        File.WriteAllBytes(exe, archive);
        Assert.Throws<InvalidDataException>(() => EnginePreextractor.Extract(exe, TempDir()));
    }

    /// <summary>
    /// 升级链语义（Task1）：源引擎 onefile 内容变 → 预解包树变 → manifest 哈希变（EmbeddedUpdater
    /// 依 manifest 字节比对自决换入）；同引擎重打包 → 哈希不变（staging 确定性）。
    /// </summary>
    [Fact]
    public void Pack_ChangedEngine_ChangesManifestHash_UnchangedEngineDoesNot()
    {
        string src = MakeFakeTree();
        string hash1 = PackTree(src).Manifest.Hash;

        // 同引擎再打包：哈希稳定。
        Assert.Equal(hash1, PackTree(src).Manifest.Hash);

        // 引擎内容变（zlib 体变 → 树字节变）：哈希必变。
        File.WriteAllBytes(
            Path.Combine(src, "resources", "jsr_backend.exe"),
            BuildMiniOnefile(StandardMiniEntries().Select(e =>
                e.Name == "python310.dll"
                    ? e with { Body = Encoding.ASCII.GetBytes("python310 runtime dll body - v2") }
                    : e).ToList()));
        Assert.NotEqual(hash1, PackTree(src).Manifest.Hash);
    }

    [Fact]
    public void EnsureExtracted_RoundTrip_AllFilesByteIdentical()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();

        string backend = NewManager(baseRoot, payload).EnsureExtracted();

        // Target dir is the backend root: base\<manifest hash first 16 hex chars>.
        Assert.Equal(Path.Combine(baseRoot, payload.Manifest.Hash[..16]), backend);
        // 预解包形态：引擎本体在树内 resources\jsr_backend_runtime\jsr_backend.exe。
        Assert.True(File.Exists(Path.Combine(backend, "resources", "jsr_backend_runtime", "jsr_backend.exe")));
        Assert.False(File.Exists(Path.Combine(backend, PayloadManager.IncompleteMarkerName)), "marker removed after successful extraction");
        foreach (PayloadManifestEntry entry in payload.Manifest.Files)
        {
            Assert.Equal(
                File.ReadAllBytes(SourceFile(payload, entry.Path)),
                File.ReadAllBytes(Path.Combine(backend, entry.Path)));
        }
    }

    [Fact]
    public void EnsureExtracted_RenameTransientlyLocked_RetriesThenSucceeds()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();
        var manager = NewManager(baseRoot, payload);
        int attempts = 0;
        manager.DirectoryMoveHook = (source, dest) =>
        {
            attempts++;
            if (attempts == 1) // AV scanner / indexer briefly holds the freshly-written tmp dir
                throw new IOException("The process cannot access the file because it is being used by another process.");
            Directory.Move(source, dest);
        };

        string backend = manager.EnsureExtracted();

        Assert.Equal(2, attempts); // one transient failure, the retry landed the rename
        Assert.Equal(Path.Combine(baseRoot, payload.Manifest.Hash[..16]), backend);
        Assert.False(Directory.Exists(Path.Combine(baseRoot, payload.Manifest.Hash[..16] + ".tmp-" + Environment.ProcessId)), "tmp dir renamed away");
        foreach (PayloadManifestEntry entry in payload.Manifest.Files)
            Assert.Equal(
                File.ReadAllBytes(SourceFile(payload, entry.Path)),
                File.ReadAllBytes(Path.Combine(backend, entry.Path)));
    }

    [Fact]
    public void EnsureExtracted_RenameLockedForGood_ThrowsAfterRetryBudget()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();
        var manager = NewManager(baseRoot, payload);
        int attempts = 0;
        manager.DirectoryMoveHook = (_, _) =>
        {
            attempts++;
            throw new UnauthorizedAccessException("locked for good");
        };

        Assert.Throws<UnauthorizedAccessException>(() => manager.EnsureExtracted());
        Assert.Equal(PayloadManager.FsLockRetryAttempts, attempts); // full budget spent before giving up
    }

    [Fact]
    public void EnsureExtracted_MissingFile_SelfHealsFully()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();
        string backend = NewManager(baseRoot, payload).EnsureExtracted();

        File.Delete(Path.Combine(backend, "resources", "licenses", "notice.txt"));

        // Fresh instance = next launch: must detect the gap, wipe the dir and re-extract.
        var second = NewManager(baseRoot, payload);
        string healed = second.EnsureExtracted();
        Assert.Equal(backend, healed);
        Assert.Equal(1, second.ManifestVerifyPasses); // the failed validation did run a full pass
        foreach (PayloadManifestEntry entry in payload.Manifest.Files)
            Assert.Equal(
                File.ReadAllBytes(SourceFile(payload, entry.Path)),
                File.ReadAllBytes(Path.Combine(backend, entry.Path)));
    }

    [Fact]
    public void EnsureExtracted_CorruptedFileByte_SelfHealsFully()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();
        string backend = NewManager(baseRoot, payload).EnsureExtracted();

        // Flip one byte, keep the length, so only the SHA-256 check can catch it
        // (树内 b 文件：预解包形态的引擎本体拷贝之外的普通载荷条目)。
        string victim = Path.Combine(backend, "resources", "jsr_backend_runtime", "python310.dll");
        byte[] bytes = File.ReadAllBytes(victim);
        bytes[10] ^= 0xFF;
        File.WriteAllBytes(victim, bytes);

        string healed = NewManager(baseRoot, payload).EnsureExtracted();
        Assert.Equal(backend, healed);
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(payload.StagingDir, "python310.dll")),
            File.ReadAllBytes(victim));
    }

    [Fact]
    public void EnsureExtracted_SecondCallInSameSession_SkipsFullRehash()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();
        var manager = NewManager(baseRoot, payload);

        string first = manager.EnsureExtracted();
        Assert.Equal(0, manager.ManifestVerifyPasses); // first call extracts, no existing-dir pass

        // Same instance (same process): session cache hit, no re-hash at all.
        Assert.Equal(first, manager.EnsureExtracted());
        Assert.Equal(0, manager.ManifestVerifyPasses);

        // Fresh instance (next launch): one full verify pass, succeeds, returns the same dir.
        var nextLaunch = NewManager(baseRoot, payload);
        Assert.Equal(first, nextLaunch.EnsureExtracted());
        Assert.Equal(1, nextLaunch.ManifestVerifyPasses);
        // And that instance caches too: a further call must not re-hash.
        Assert.Equal(first, nextLaunch.EnsureExtracted());
        Assert.Equal(1, nextLaunch.ManifestVerifyPasses);
    }

    [Fact]
    public void EnsureExtracted_StaleIncompleteMarker_ForcesFullReextract()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();
        string backend = NewManager(baseRoot, payload).EnsureExtracted();

        // Simulate a crash that left the target dir with an .incomplete marker:
        // never trusted regardless of file contents.
        File.WriteAllText(Path.Combine(backend, PayloadManager.IncompleteMarkerName), "");

        string healed = NewManager(baseRoot, payload).EnsureExtracted();
        Assert.Equal(backend, healed);
        Assert.False(File.Exists(Path.Combine(backend, PayloadManager.IncompleteMarkerName)), "self-heal wiped the dir incl. marker");
        foreach (PayloadManifestEntry entry in payload.Manifest.Files)
            Assert.Equal(
                File.ReadAllBytes(SourceFile(payload, entry.Path)),
                File.ReadAllBytes(Path.Combine(backend, entry.Path)));
    }

    [Fact]
    public void EnsureExtracted_RemovesCrashedTmpDirs()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();

        // Leftover from a crashed extraction of the same payload version.
        string stale = Path.Combine(baseRoot, payload.Manifest.Hash[..16] + ".tmp-999999");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "junk.bin"), "half-written");

        string backend = NewManager(baseRoot, payload).EnsureExtracted();

        Assert.False(Directory.Exists(stale), "crashed tmp dir cleaned up");
        Assert.True(File.Exists(Path.Combine(backend, "resources", "jsr_backend_runtime", "jsr_backend.exe")));
    }

    [Fact]
    public void EnsureExtracted_PrunesStaleSiblingVersions()
    {
        FakePayload payloadA = PackFake();
        string baseRoot = TempDir();
        string dirA = NewManager(baseRoot, payloadA).EnsureExtracted();

        // Payload B: same fake tree with one file changed → different manifest hash → different extraction dir.
        File.WriteAllText(Path.Combine(payloadA.SrcDir, "resources", "licenses", "notice.txt"), "fake third-party license text, version 2");
        FakePayload payloadB = PackTree(payloadA.SrcDir);

        // Crashed tmp leftover of the OLD version — any sibling != current hashDir must go too.
        string oldTmp = Path.Combine(baseRoot, payloadA.Manifest.Hash[..16] + ".tmp-424242");
        Directory.CreateDirectory(oldTmp);

        string dirB = NewManager(baseRoot, payloadB).EnsureExtracted();

        Assert.Equal(Path.Combine(baseRoot, payloadB.Manifest.Hash[..16]), dirB);
        Assert.NotEqual(dirA, dirB);
        Assert.False(Directory.Exists(dirA), "previous-version dir pruned after the new version is live");
        Assert.False(Directory.Exists(oldTmp), "old-version crashed tmp leftover pruned too");
        Assert.True(File.Exists(Path.Combine(dirB, "resources", "jsr_backend_runtime", "jsr_backend.exe")), "current version intact after pruning");
    }

    [Fact]
    public void EnsureExtracted_ReportsMonotonicProgressReachingOne()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();
        var progress = new CollectingProgress();

        NewManager(baseRoot, payload).EnsureExtracted(progress);

        Assert.NotEmpty(progress.Reports);
        for (int i = 1; i < progress.Reports.Count; i++)
            Assert.True(progress.Reports[i] >= progress.Reports[i - 1] - 1e-9, $"progress went backwards at {i}");
        Assert.Equal(1.0, progress.Reports[^1], precision: 6);
    }

    // ---- EmbeddedUpdater: pack-payload's swap-in decision (manifest byte-compare) ----

    /// <summary>Writes a fake embedded pair — opaque bytes, because <see cref="EmbeddedUpdater"/>
    /// only byte-compares the manifest and copies the pair; it never parses them.</summary>
    private static void WritePair(string dir, byte[] manifest, byte[] bin)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, PayloadPacker.ManifestFileName), manifest);
        File.WriteAllBytes(Path.Combine(dir, PayloadPacker.PayloadFileName), bin);
    }

    [Fact]
    public void Pack_UnchangedManifest_KeepsExistingEmbedded()
    {
        byte[] manifest = { 0x7B, 0x22, 0x76, 0x65, 0x72, 0x73, 0x69, 0x6F, 0x6E, 0x22 }; // {"version"
        byte[] bin = { 1, 2, 3 };
        string embedded = TempDir();
        WritePair(embedded, manifest, bin);

        // Fresh pack of the SAME source: identical manifest bytes (a deterministic packer
        // then also yields an identical bin; the differing bin here documents that the
        // keep/replace decision is manifest-driven, not bin-driven).
        string temp = TempDir();
        WritePair(temp, manifest, new byte[] { 9, 9, 9, 9 });

        bool replaced = EmbeddedUpdater.Apply(embedded, temp);

        Assert.False(replaced);
        Assert.False(Directory.Exists(temp), "keep deletes the temp pack dir");
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(embedded, PayloadPacker.ManifestFileName)));
        Assert.Equal(bin, File.ReadAllBytes(Path.Combine(embedded, PayloadPacker.PayloadFileName)));
    }

    [Fact]
    public void Pack_UnchangedManifest_ManifestOnlyEmbedded_Replaces()
    {
        // Incomplete embedded pair — manifest kept, bin deleted for fast dev builds
        // (exactly the state build.bat's tail NOTE leaves behind). A same-manifest
        // pack must repair it, never bless the half pair with a keep decision.
        byte[] manifest = { 0x7B, 0x22, 0x76, 0x65, 0x72, 0x73, 0x69, 0x6F, 0x6E, 0x22 }; // {"version"
        byte[] bin = { 4, 5, 6 };
        string embedded = TempDir();
        Directory.CreateDirectory(embedded);
        // Manifest-only remnant: no payload.bin in the embedded dir.
        File.WriteAllBytes(Path.Combine(embedded, PayloadPacker.ManifestFileName), manifest);

        string temp = TempDir();
        WritePair(temp, manifest, bin);

        bool replaced = EmbeddedUpdater.Apply(embedded, temp);

        Assert.True(replaced, "manifest-only remnant must go through replace");
        Assert.False(Directory.Exists(temp), "replace consumes the temp pack dir");
        Assert.Equal(bin, File.ReadAllBytes(Path.Combine(embedded, PayloadPacker.PayloadFileName)));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(embedded, PayloadPacker.ManifestFileName)));
    }

    [Fact]
    public void Pack_ChangedManifest_ReplacesEmbedded()
    {
        string embedded = TempDir();
        WritePair(embedded, new byte[] { 1, 1, 1 }, new byte[] { 1, 2, 3 });

        string temp = TempDir();
        byte[] newManifest = { 2, 2, 2, 2 };
        byte[] newBin = { 7, 7, 7 };
        WritePair(temp, newManifest, newBin);

        bool replaced = EmbeddedUpdater.Apply(embedded, temp);

        Assert.True(replaced);
        Assert.False(Directory.Exists(temp), "replace consumes the temp pack dir");
        Assert.Equal(newManifest, File.ReadAllBytes(Path.Combine(embedded, PayloadPacker.ManifestFileName)));
        Assert.Equal(newBin, File.ReadAllBytes(Path.Combine(embedded, PayloadPacker.PayloadFileName)));

        // Absent embedded pair (fresh clone, or deleted for fast dev builds) → replace too.
        string fresh = TempDir();
        string temp2 = TempDir();
        WritePair(temp2, newManifest, newBin);
        Assert.True(EmbeddedUpdater.Apply(fresh, temp2));
        Assert.True(File.Exists(Path.Combine(fresh, PayloadPacker.PayloadFileName)));
    }
}