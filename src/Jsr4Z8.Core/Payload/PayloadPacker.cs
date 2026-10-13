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
    int FileCount, long RawBytes, long PackedBytes, string ManifestHash, string PayloadPath, string ManifestPath);

/// <summary>
/// Packs the backend distribution into the payload pair (payload.bin +
/// payload.manifest.json) and provides the manifest/archive readers used by
/// <see cref="PayloadManager"/> for extraction. Shared library code: the release packer
/// tool (tools/pack-payload, invoked by the release script in Task 15) and the tests
/// (fake 3-file payloads in temp dirs) both go through it — the real ~200 MB payload is
/// never committed or embedded into test builds.
///
/// Scanned from &lt;srcDir&gt; (byte-exact constraint): "Jiangtherapee Bridge.exe", the
/// ENTIRE resources/ tree (every file, recursive, no exclusions) and THIRD_PARTY_NOTICES.md.
/// Everything else in the source package (README.md, DeepSeek助手使用说明.md,
/// FILES-SHA256.json, ThirdPartySources/) is not part of the payload.
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

    private const uint Magic = 0x3142_504A; // little-endian bytes spell "JPB1"
    private const int MaxFiles = 1_000_000;
    private const int MaxPathBytes = 4096;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// The payload's source file list: the bridge exe, every file under resources/
    /// (recursive, byte-exact, no exclusions) and THIRD_PARTY_NOTICES.md, each as a path
    /// relative to <paramref name="srcDir"/>. Throws if any of the three required sources is missing.
    /// </summary>
    public static IReadOnlyList<string> EnumerateSourceFiles(string srcDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(srcDir);
        string exe = Path.Combine(srcDir, "Jiangtherapee Bridge.exe");
        string notices = Path.Combine(srcDir, "THIRD_PARTY_NOTICES.md");
        string resources = Path.Combine(srcDir, "resources");
        if (!File.Exists(exe))
            throw new DirectoryNotFoundException($"后端源目录缺少必需文件: {exe}");
        if (!File.Exists(notices))
            throw new DirectoryNotFoundException($"后端源目录缺少必需文件: {notices}");
        if (!Directory.Exists(resources))
            throw new DirectoryNotFoundException($"后端源目录缺少必需目录: {resources}");

        var files = new List<string> { Path.GetRelativePath(srcDir, exe) };
        foreach (string file in Directory.EnumerateFiles(resources, "*", SearchOption.AllDirectories))
            files.Add(Path.GetRelativePath(srcDir, file));
        files.Add(Path.GetRelativePath(srcDir, notices));
        return files;
    }

    /// <summary>Packs <paramref name="srcDir"/> into payload.bin + payload.manifest.json under <paramref name="outDir"/>.</summary>
    public static PayloadPackResult Pack(string srcDir, string outDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(srcDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(outDir);
        Directory.CreateDirectory(outDir);

        IReadOnlyList<string> files = EnumerateSourceFiles(srcDir)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        // Pass 1: per-file SHA-256 of the raw bytes → manifest.
        var entries = new List<PayloadManifestEntry>(files.Count);
        foreach (string rel in files)
        {
            using FileStream src = File.OpenRead(Path.Combine(srcDir, rel));
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
                    using (FileStream src = File.OpenRead(Path.Combine(srcDir, files[i])))
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

                Span<byte> num = stackalloc byte[8];
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
            payloadPath,
            manifestPath);
    }

    /// <summary>Builds the manifest from entries: files ordinal-sorted by path, hash computed per the format contract.</summary>
    public static PayloadManifest BuildManifest(IEnumerable<PayloadManifestEntry> entries)
    {
        PayloadManifestEntry[] sorted = entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        return new PayloadManifest(CurrentManifestVersion, ComputeManifestHash(sorted), sorted);
    }

    /// <summary>SHA-256 over the ordinal-path-sorted concatenation of "path\nsha256\n" lines (lowercase hex).</summary>
    public static string ComputeManifestHash(IEnumerable<PayloadManifestEntry> entries)
    {
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (PayloadManifestEntry entry in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
            sha.AppendData(Encoding.UTF8.GetBytes($"{entry.Path}\n{entry.Sha256}\n"));
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    public static void WriteManifestJson(PayloadManifest manifest, string path)
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
