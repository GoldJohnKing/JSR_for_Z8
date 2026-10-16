using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jsr4Z8.Core.Payload;

/// <summary>One file of the payload manifest: relative path (within the backend root), lowercase-hex SHA-256 of the raw bytes, raw byte length.</summary>
public sealed record PayloadManifestEntry(string Path, string Sha256, long Length);

/// <summary>
/// Manifest of the packed backend payload. <see cref="Hash"/> is the SHA-256 over the
/// ordinal-path-sorted concatenation of "path\nsha256\n" lines; <see cref="PayloadManager"/>
/// extracts into a directory named by its first 16 hex chars, so a new payload version
/// never collides with an old extraction.
/// </summary>
public sealed record PayloadManifest(int Version, string Hash, IReadOnlyList<PayloadManifestEntry> Files);

/// <summary>One entry of the payload.bin index.</summary>
public sealed record PayloadArchiveEntry(string Path, long RawLength, long CompressedLength);

/// <summary>Summary of one <see cref="PayloadPacker.Pack"/> run.</summary>
public sealed record PayloadPackResult(
    int FileCount, long RawBytes, long PackedBytes, string ManifestHash, string ManifestPath);

/// <summary>
/// Packs the backend distribution into the payload pair (payload.bin +
/// payload.manifest.json) and provides the manifest/archive readers used by
/// <see cref="PayloadManager"/> for extraction. Shared library code: the release
/// packer tool (tools/pack-payload, invoked by build.bat) and the tests (fake
/// payloads in temp dirs) both go through it — the real ~202 MB payload is never
/// committed or embedded into test builds.
///
/// Payload contents: the resources/ tree of &lt;srcDir&gt; (byte-exact) minus the
/// exclusion list (<see cref="IsExcluded"/> — rationale per entry there), plus
/// the pre-extracted engine runtime tree mapped under resources\jsr_backend_runtime\
/// (tools/pack-payload's EnginePreextractor output, including the thinned engine
/// exe copy at the tree root; runtime contract: the _PYI_* triple runs the
/// tree's exe against the tree — zero %TEMP% _MEI; see
/// docs/superpowers/notes/2026-10-16-meipass2-experiment.md). Everything else in
/// the source package (README.md, DeepSeek助手使用说明.md, FILES-SHA256.json,
/// ThirdPartySources/, THIRD_PARTY_NOTICES.md — licenses ship in resources/licenses
/// — and the Bridge shell) is not part of the payload.
///
/// payload.bin layout (all integers little-endian):
///   u32  magic 0x3142504A (bytes spell "JPB1")
///   u32  fileCount
///   fileCount headers, in ordinal-path-sorted order:
///     u32 pathLen; pathLen bytes UTF-8 relative path; u64 rawLen; u64 compressedLen
///   fileCount bodies in the same order: raw DEFLATE streams (System.IO.Compression
///   DeflateStream — no zlib/gzip wrapper), each exactly compressedLen bytes.
/// </summary>
public static class PayloadPacker
{
    public const string PayloadFileName = "payload.bin";
    public const string ManifestFileName = "payload.manifest.json";
    public const int CurrentManifestVersion = 1;

    /// <summary>
    /// 载荷内预解包引擎树的目录前缀（相对后端根；正斜杠拼写与排除清单约定一致，组路径时按 OS 分隔符）。
    /// <see cref="Pack"/> 把调用方预解包好的引擎树（EnginePreextractor.Extract 输出）整树映射到其下，
    /// 含树根薄化的 jsr_backend.exe 本体拷贝（b 体清零）。运行时契约：
    /// _PYI_ARCHIVE_FILE=&lt;释放根&gt;\resources\jsr_backend_runtime\jsr_backend.exe、
    /// _PYI_APPLICATION_HOME_DIR=&lt;释放根&gt;\resources\jsr_backend_runtime。
    /// </summary>
    public const string EngineRuntimeDir = "resources/jsr_backend_runtime";

    /// <summary>OS 分隔符形态的引擎树前缀（manifest 路径解析用；枚举组路径见 <see cref="EnumerateSourceFiles"/>）。</summary>
    private static readonly string EngineRuntimeOSDir = EngineRuntimeDir.Replace('/', Path.DirectorySeparatorChar);

    private const uint Magic = 0x3142_504A; // little-endian bytes spell "JPB1"
    private const int MaxFiles = 1_000_000;
    private const int MaxPathBytes = 4096;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>How one <see cref="ExcludedPayloadEntries"/> pattern matches a normalized ('/'-separated) relative path.</summary>
    private enum ExclusionKind
    {
        /// <summary>Path equals the pattern (case-insensitive).</summary>
        ExactPath,

        /// <summary>Path starts with the pattern, which always ends in '/' — the entire directory subtree.</summary>
        DirectoryPrefix,
    }

    /// <summary>
    /// Payload exclusion list (paths relative to the backend source root, '/' separators,
    /// matched case-insensitively — Windows source trees). Rationale per entry:
    /// </summary>
    private static readonly (ExclusionKind Kind, string Pattern)[] ExcludedPayloadEntries =
    {
        // onefile exe 不整体入载荷：打包期 EnginePreextractor 把其内嵌 CArchive 解成
        // 文件树（含薄化 exe 本体拷贝：b 体清零，长度不变，打包期变换；原件=reference/
        // 树 onefile exe 未动）经 resources\jsr_backend_runtime\ 前缀入载荷，运行时
        // _PYI_* 三元组直跑树内引擎——零 %TEMP% _MEI。本 ExactPath 挡住 onefile 整体重嵌回潮。
        // （机制实证 docs/superpowers/notes/2026-10-16-meipass2-experiment.md、
        // 2026-10-16-thin-archive-probe.md）
        (ExclusionKind.ExactPath, "resources/jsr_backend.exe"),
        // Top-level Bridge shell: the GUI invokes the engine directly,
        // so the shell is dead weight — this ExactPath entry keeps a future whitelist
        // regression from smuggling it back in. 直调后顶层壳不携带。
        (ExclusionKind.ExactPath, "Jiangtherapee Bridge.exe"),
        // K14 engines: the 7-frame compute path only ever spawns jsr_backend.exe —
        // dead weight here.
        (ExclusionKind.ExactPath, "resources/jsr_v98_k14_backend.exe"),
        (ExclusionKind.ExactPath, "resources/jsr_fast_k14_backend.exe"),
        // The ENTIRE on-disk _internal tree (subsumes the earlier Tcl/Tk, cv2-ffmpeg-DLL
        // and wgpu-test-residue entries): jsr_backend.exe is a PyInstaller onefile build
        // that self-extracts its full embedded archive to %TEMP%\_MEI* on every launch
        // and never reads the on-disk tree (r03 numpy negative control + 16/16 _MEI
        // observations + r10 whole-tree smoke; probe 2026-10-16-payload-trim-probe.md §3).
        (ExclusionKind.DirectoryPrefix, "resources/_internal/"),
        // Never-spawned tools and their libjxl/mingw DLL family: zero literal references
        // in the engine's PYZ, our C# side never spawns the exes, and the DLL family's
        // only importers were these two tool exes (payload-trim-probe §2/§4).
        (ExclusionKind.ExactPath, "resources/raw_bridge.exe"),
        (ExclusionKind.ExactPath, "resources/jxl_worker.exe"),
        (ExclusionKind.ExactPath, "resources/libjxl.dll"),
        (ExclusionKind.ExactPath, "resources/libjxl_cms.dll"),
        (ExclusionKind.ExactPath, "resources/libjxl_threads.dll"),
        (ExclusionKind.ExactPath, "resources/libhwy.dll"),
        (ExclusionKind.ExactPath, "resources/libbrotlicommon.dll"),
        (ExclusionKind.ExactPath, "resources/libbrotlidec.dll"),
        (ExclusionKind.ExactPath, "resources/libbrotlienc.dll"),
        (ExclusionKind.ExactPath, "resources/libstdc++-6.dll"),
        (ExclusionKind.ExactPath, "resources/libgcc_s_seh-1.dll"),
        (ExclusionKind.ExactPath, "resources/libwinpthread-1.dll"),
        (ExclusionKind.ExactPath, "resources/liblcms2-2.dll"),
        // The 205 MB preset-dark DNGs are only consulted by the backend's own
        // auto-matching, which manual-calibration requests never trigger.
        (ExclusionKind.DirectoryPrefix, "resources/z8_presets/"),
    };

    /// <summary>
    /// True if <paramref name="relativePath"/> (relative to the backend source root, '/' or '\\'
    /// separators) hits the exclusion list. THIRD_PARTY_NOTICES.md is not an exclusion entry:
    /// it is simply not whitelisted (only the resources/ tree minus exclusions is scanned).
    /// </summary>
    internal static bool IsExcluded(string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        string normalized = relativePath.Replace('\\', '/');
        foreach ((ExclusionKind kind, string pattern) in ExcludedPayloadEntries)
        {
            switch (kind)
            {
                case ExclusionKind.ExactPath:
                    if (string.Equals(normalized, pattern, StringComparison.OrdinalIgnoreCase))
                        return true;
                    break;
                case ExclusionKind.DirectoryPrefix:
                    if (normalized.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                        return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>
    /// The payload's source file list: every file under resources/ (recursive) except the
    /// exclusion list (<see cref="IsExcluded"/>), plus the pre-extracted engine runtime tree
    /// (<paramref name="engineTreeDir"/> — EnginePreextractor.Extract output) mapped under
    /// resources\jsr_backend_runtime\. Throws if the extraction source
    /// (resources\jsr_backend.exe), the staged tree, or the staged engine copy
    /// (resources\jsr_backend_runtime\jsr_backend.exe — the runtime contract entry point)
    /// is missing. THIRD_PARTY_NOTICES.md and the top-level Bridge shell are not packed
    /// (third-party licenses ship inside resources/licenses/).
    /// </summary>
    public static IReadOnlyList<string> EnumerateSourceFiles(string srcDir, string engineTreeDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(srcDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineTreeDir);
        string resources = Path.Combine(srcDir, "resources");
        string engine = Path.Combine(resources, "jsr_backend.exe");
        if (!Directory.Exists(resources))
            throw new DirectoryNotFoundException($"后端源目录缺少必需目录: {resources}");
        if (!File.Exists(engine))
            throw new DirectoryNotFoundException($"后端源目录缺少预解包源引擎: {engine}");
        if (!Directory.Exists(engineTreeDir))
            throw new DirectoryNotFoundException($"预解包引擎树目录不存在: {engineTreeDir}");
        string stagedEngine = Path.Combine(engineTreeDir, "jsr_backend.exe");
        if (!File.Exists(stagedEngine))
            throw new FileNotFoundException($"预解包引擎树缺少引擎本体拷贝（运行时契约入口）: {stagedEngine}");

        var files = new List<string>();
        foreach (string file in Directory.EnumerateFiles(resources, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(srcDir, file);
            // 保留命名空间：引擎树映射前缀只允许来自 engineTreeDir——源树同名路径会被
            // ResolveSourceFile 误读到 staging，此处直接拒绝而不是静默错字节。
            if (rel.StartsWith(EngineRuntimeOSDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"源树占用预解包保留前缀 {EngineRuntimeOSDir}\\: {rel}");
            if (!IsExcluded(rel))
                files.Add(rel);
        }
        foreach (string file in Directory.EnumerateFiles(engineTreeDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.Combine(EngineRuntimeOSDir, Path.GetRelativePath(engineTreeDir, file));
            if (!IsExcluded(rel))
                files.Add(rel);
        }
        return files;
    }

    /// <summary>
    /// Packs <paramref name="srcDir"/> plus the pre-extracted engine tree
    /// <paramref name="engineTreeDir"/> (mapped under resources\jsr_backend_runtime\) into
    /// payload.bin + payload.manifest.json under <paramref name="outDir"/>. The staged tree
    /// is produced by the caller (tools/pack-payload: EnginePreextractor.Extract on
    /// resources\jsr_backend.exe) — same exe bytes ⇒ same staged tree ⇒ same manifest
    /// bytes (deterministic).
    /// </summary>
    public static PayloadPackResult Pack(string srcDir, string outDir, string engineTreeDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(srcDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(outDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineTreeDir);
        Directory.CreateDirectory(outDir);

        IReadOnlyList<string> files = EnumerateSourceFiles(srcDir, engineTreeDir)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        // Pass 1: per-file SHA-256 of the raw bytes → manifest.
        var entries = new List<PayloadManifestEntry>(files.Count);
        foreach (string rel in files)
        {
            using FileStream src = File.OpenRead(ResolveSourceFile(srcDir, engineTreeDir, rel));
            entries.Add(new PayloadManifestEntry(
                rel,
                Convert.ToHexString(SHA256.HashData(src)).ToLowerInvariant(),
                src.Length));
        }
        PayloadManifest manifest = BuildManifest(entries);

        // Pass 2: compress bodies to a spill file, capturing each compressedLen
        // (headers precede bodies, so lengths must be known before payload.bin is written).
        string payloadPath = Path.Combine(outDir, PayloadFileName);
        string spillPath = payloadPath + ".bodies.tmp";
        try
        {
            var compressed = new long[files.Count];
            using (FileStream bodies = File.Create(spillPath))
            {
                for (int i = 0; i < files.Count; i++)
                {
                    long start = bodies.Position;
                    // using BLOCKS (not declarations): the delta must be read after the
                    // DeflateStream is disposed and has flushed its internal buffer.
                    using (FileStream src = File.OpenRead(ResolveSourceFile(srcDir, engineTreeDir, files[i])))
                    using (Stream deflate = new DeflateStream(bodies, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        src.CopyTo(deflate);
                    }
                    compressed[i] = bodies.Position - start;
                }
            }

            // Pass 3: payload.bin = magic + count + headers + bodies.
            using (FileStream payload = File.Create(payloadPath))
            {
                Span<byte> head = stackalloc byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(head[..4], Magic);
                BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)files.Count);
                payload.Write(head);

                for (int i = 0; i < files.Count; i++)
                {
                    byte[] path = Encoding.UTF8.GetBytes(files[i]);
                    if (path.Length > MaxPathBytes)
                        throw new InvalidDataException($"载荷路径过长: {files[i]}");
                    WriteUInt32(payload, (uint)path.Length);
                    payload.Write(path);
                    WriteUInt64(payload, (ulong)entries[i].Length);
                    WriteUInt64(payload, (ulong)compressed[i]);
                }

                using FileStream spill = File.OpenRead(spillPath);
                spill.CopyTo(payload);
            }
        }
        finally
        {
            File.Delete(spillPath);
        }

        string manifestPath = Path.Combine(outDir, ManifestFileName);
        WriteManifestJson(manifest, manifestPath);
        return new PayloadPackResult(
            files.Count,
            entries.Sum(e => e.Length),
            new FileInfo(payloadPath).Length,
            manifest.Hash,
            manifestPath);
    }

    /// <summary>manifest 相对路径 → 打包源文件：jsr_backend_runtime\ 前缀条目取自引擎树，其余取自源树。</summary>
    private static string ResolveSourceFile(string srcDir, string engineTreeDir, string relPath) =>
        relPath.StartsWith(EngineRuntimeOSDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(engineTreeDir, relPath[(EngineRuntimeOSDir.Length + 1)..])
            : Path.Combine(srcDir, relPath);

    /// <summary>Builds the manifest from entries: files ordinal-sorted by path, hash computed per the format contract.</summary>
    private static PayloadManifest BuildManifest(IEnumerable<PayloadManifestEntry> entries)
    {
        PayloadManifestEntry[] sorted = entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        return new PayloadManifest(CurrentManifestVersion, ComputeManifestHash(sorted), sorted);
    }

    /// <summary>SHA-256 over the ordinal-path-sorted concatenation of "path\nsha256\n" lines (lowercase hex).</summary>
    private static string ComputeManifestHash(IEnumerable<PayloadManifestEntry> entries)
    {
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (PayloadManifestEntry entry in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
            sha.AppendData(Encoding.UTF8.GetBytes($"{entry.Path}\n{entry.Sha256}\n"));
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private static void WriteManifestJson(PayloadManifest manifest, string path)
    {
        using FileStream stream = File.Create(path);
        JsonSerializer.Serialize(stream, ManifestDto.From(manifest), ManifestJsonOptions);
    }

    /// <summary>Reads and validates a payload.manifest.json stream.</summary>
    public static PayloadManifest ReadManifestJson(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ManifestDto dto = JsonSerializer.Deserialize<ManifestDto>(stream, ManifestJsonOptions)
            ?? throw new InvalidDataException("payload.manifest.json 解析为 null");
        if (dto.Version != CurrentManifestVersion)
            throw new InvalidDataException($"manifest 版本不符: {dto.Version} (期望 {CurrentManifestVersion})");
        if (dto.Files is null || dto.Files.Count == 0)
            throw new InvalidDataException("manifest 无文件条目");
        if (dto.Hash?.Length != 64 || !dto.Hash.All(IsHex))
            throw new InvalidDataException("manifest hash 不是 64 位十六进制");
        foreach (ManifestFileDto file in dto.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
                throw new InvalidDataException("manifest 条目缺路径");
            if (file.Sha256?.Length != 64 || !file.Sha256.All(IsHex))
                throw new InvalidDataException($"manifest 条目哈希非法: {file.Path}");
            if (file.Length < 0)
                throw new InvalidDataException($"manifest 条目长度非法: {file.Path}");
        }
        return new PayloadManifest(dto.Version, dto.Hash!, dto.Files!
            .Select(f => new PayloadManifestEntry(f.Path, f.Sha256!, f.Length))
            .ToArray());
    }

    private static bool IsHex(char c) => Uri.IsHexDigit(c);

    /// <summary>
    /// Reads the payload.bin index (magic + count + headers). On return the stream is
    /// positioned exactly at the first compressed body.
    /// </summary>
    public static List<PayloadArchiveEntry> ReadArchiveIndex(Stream payload)
    {
        using var reader = new BinaryReader(payload, Encoding.UTF8, leaveOpen: true);
        uint magic = reader.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException($"payload.bin 魔数不符: 0x{magic:X8} (期望 0x{Magic:X8})");
        uint count = reader.ReadUInt32();
        if (count == 0 || count > MaxFiles)
            throw new InvalidDataException($"payload.bin 文件数异常: {count}");
        var entries = new List<PayloadArchiveEntry>((int)count);
        for (int i = 0; i < count; i++)
        {
            uint pathLen = reader.ReadUInt32();
            if (pathLen == 0 || pathLen > MaxPathBytes)
                throw new InvalidDataException($"payload.bin 条目 {i} 路径长度异常: {pathLen}");
            byte[] pathBytes = reader.ReadBytes((int)pathLen);
            if (pathBytes.Length != pathLen || pathBytes.Contains((byte)0))
                throw new InvalidDataException($"payload.bin 条目 {i} 路径损坏");
            long rawLen = reader.ReadInt64();
            long compressedLen = reader.ReadInt64();
            if (rawLen < 0 || compressedLen < 0)
                throw new InvalidDataException($"payload.bin 条目 {i} 长度为负");
            entries.Add(new PayloadArchiveEntry(Encoding.UTF8.GetString(pathBytes), rawLen, compressedLen));
        }
        return entries;
    }

    /// <summary>
    /// Opens a decompressing stream over the next <paramref name="compressedLength"/> bytes of
    /// <paramref name="payload"/> (raw DEFLATE). Disposing the returned stream advances the payload
    /// stream to the end of this body's window, ready for the next <see cref="OpenArchiveBody"/> call.
    /// </summary>
    public static Stream OpenArchiveBody(Stream payload, long compressedLength)
    {
        if (compressedLength < 0)
            throw new ArgumentOutOfRangeException(nameof(compressedLength));
        var window = new WindowStream(payload, compressedLength);
        return new DeflateBodyStream(new DeflateStream(window, CompressionMode.Decompress, leaveOpen: true), window);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        stream.Write(buf);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
        stream.Write(buf);
    }

    /// <summary>Read-only forward window over the next <c>length</c> bytes of an inner stream; the inner stream never advances past the window.</summary>
    private sealed class WindowStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public WindowStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0)
                return 0;
            int read = _inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        /// <summary>Consumes whatever is left of the window so the inner stream sits exactly at the next body.</summary>
        public void DrainToEnd()
        {
            Span<byte> scratch = stackalloc byte[8192];
            while (Read(scratch) > 0)
            {
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>DeflateStream over a <see cref="WindowStream"/>; on dispose drains the window so the payload stream is left exactly after this body.</summary>
    private sealed class DeflateBodyStream : Stream
    {
        private readonly DeflateStream _deflate;
        private readonly WindowStream _window;

        public DeflateBodyStream(DeflateStream deflate, WindowStream window)
        {
            _deflate = deflate;
            _window = window;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _deflate.Read(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _deflate.Dispose();
                _window.DrainToEnd();
            }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ManifestDto
    {
        public int Version { get; set; }
        public string? Hash { get; set; }
        public List<ManifestFileDto>? Files { get; set; }

        public static ManifestDto From(PayloadManifest manifest) => new()
        {
            Version = manifest.Version,
            Hash = manifest.Hash,
            Files = manifest.Files.Select(ManifestFileDto.From).ToList(),
        };
    }

    private sealed class ManifestFileDto
    {
        public string Path { get; set; } = "";
        public string? Sha256 { get; set; }
        public long Length { get; set; }

        public static ManifestFileDto From(PayloadManifestEntry entry) => new()
        {
            Path = entry.Path,
            Sha256 = entry.Sha256,
            Length = entry.Length,
        };
    }
}
