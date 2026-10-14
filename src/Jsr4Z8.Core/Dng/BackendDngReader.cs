using System;
using System.Collections.Generic;
using System.IO;
using Jsr4Z8.Core.Native;

namespace Jsr4Z8.Core.Dng;

/// <summary>One backend JSR product DNG, fully read back (Task 9 input side).</summary>
public sealed record BackendDng
{
    /// <summary>Interleaved samples, row-major; length = W·H·Samples.</summary>
    public required ushort[] Data { get; init; }

    public required int W { get; init; }

    public required int H { get; init; }

    /// <summary>Samples per pixel (3 = LinearRaw RGB backend product).</summary>
    public required int Samples { get; init; }

    /// <summary>TIFF PhotometricInterpretation (34892 = LinearRaw, 32803 = CFA).</summary>
    public required ushort Photometric { get; init; }

    public required int BlackLevel { get; init; }

    public required int WhiteLevel { get; init; }

    /// <summary>IFD0 AsShotNeutral [R,G,B]; null when the backend omitted it.</summary>
    public float[]? AsShotNeutral { get; init; }

    /// <summary>IFD0 ColorMatrix1 (9 values, row-major); null when absent.</summary>
    public double[]? ColorMatrix1 { get; init; }
}

/// <summary>
/// Reads the raw SubIFD of a backend JSR product DNG (Transformer-JSR V15.3.x,
/// written by tifffile) into a <see cref="BackendDng"/>: the decoded pixel
/// array plus BL/WL/photometric/dimensions and the IFD0 color tags the
/// finalizer carries forward. Strip storage only, both compressed
/// (lossless JPEG, any predictor 1-7 — decoded via libjpeg-turbo through
/// <see cref="DngLjpegNative.ljd_decode_stream"/>) and uncompressed.
/// <see cref="ReadStrips"/> is the no-decode variant (post-release fix 5):
/// metadata + the encoded strip bytes verbatim, for passthrough re-emission.
/// </summary>
public static class BackendDngReader
{
    private const ushort TagImageWidth = 256;
    private const ushort TagImageLength = 257;
    private const ushort TagCompression = 259;
    private const ushort TagPhotometric = 262;
    private const ushort TagStripOffsets = 273;
    private const ushort TagSamplesPerPixel = 277;
    private const ushort TagRowsPerStrip = 278;
    private const ushort TagStripByteCounts = 279;
    private const ushort TagSubIfds = 0x014A;
    private const ushort TagColorMatrix1 = 50721;
    private const ushort TagBlackLevel = 50714;
    private const ushort TagWhiteLevel = 50717;
    private const ushort TagAsShotNeutral = 50728;

    /// <summary>Reads pixels and raw-IFD domain tags from the backend DNG.</summary>
    public static BackendDng Read(string backendDngPath)
    {
        using var reader = new TiffReader(backendDngPath);
        var (ifd0, rawIfd) = OpenRawIfd(reader);
        var d = ReadDomain(reader, rawIfd);
        Validate(d);

        var data = d.Compression == 7
            ? ReadCompressedStrips(reader, rawIfd, d.W, d.H, d.Samples, d.RowsPerStrip)
            : d.Compression == 1
                ? ReadUncompressedStrips(reader, rawIfd, d.W, d.H, d.Samples, d.RowsPerStrip)
                : throw new NotSupportedException($"后端 DNG Compression={d.Compression} 不受支持（仅 1/7）");

        return new BackendDng
        {
            Data = data,
            W = d.W,
            H = d.H,
            Samples = d.Samples,
            Photometric = d.Photometric,
            BlackLevel = d.BlackLevel,
            WhiteLevel = d.WhiteLevel,
            AsShotNeutral = ReadAsShotNeutral(reader, ifd0),
            ColorMatrix1 = ReadColorMatrix1(reader, ifd0),
        };
    }

    /// <summary>
    /// Passthrough read (post-release fix 5 bisection seam): the raw SubIFD's
    /// domain tags plus every strip's encoded bytes VERBATIM — no pixel
    /// decode. Pairs with <see cref="DngWriteOptions.RawStreams"/>: the caller
    /// re-emits the backend's own entropy bytes inside the product wrapper.
    /// <see cref="BackendDng.Data"/> comes back empty (nothing was decoded).
    /// </summary>
    public static (BackendDng Info, byte[][] Streams) ReadStrips(string backendDngPath)
    {
        using var reader = new TiffReader(backendDngPath);
        var (ifd0, rawIfd) = OpenRawIfd(reader);
        var d = ReadDomain(reader, rawIfd);
        Validate(d);
        if (d.Compression != 7)
            throw new NotSupportedException($"passthrough 只支持 LJ 条带（Compression=7），遇到 {d.Compression}");

        var offsets = reader.EntryU32Array(rawIfd, TagStripOffsets);
        var counts = reader.EntryU32Array(rawIfd, TagStripByteCounts);
        int strips = Math.Min(offsets.Count, counts.Count);
        int expectedStrips = (d.H + d.RowsPerStrip - 1) / d.RowsPerStrip;
        if (strips < expectedStrips)
            throw new InvalidDataException($"条带不足: {strips} < {expectedStrips} (H={d.H}, RPS={d.RowsPerStrip})");

        var streams = new byte[expectedStrips][];
        for (int i = 0; i < expectedStrips; i++)
        {
            streams[i] = new byte[counts[i]];
            reader.ReadBytes(offsets[i], streams[i], 0, counts[i]);
        }

        return (new BackendDng
        {
            Data = Array.Empty<ushort>(),
            W = d.W,
            H = d.H,
            Samples = d.Samples,
            Photometric = d.Photometric,
            BlackLevel = d.BlackLevel,
            WhiteLevel = d.WhiteLevel,
            AsShotNeutral = ReadAsShotNeutral(reader, ifd0),
            ColorMatrix1 = ReadColorMatrix1(reader, ifd0),
        }, streams);
    }

    /// <summary>Opens the file and resolves (IFD0, raw SubIFD).</summary>
    private static (Dictionary<ushort, List<uint>> Ifd0, Dictionary<ushort, List<uint>> RawIfd) OpenRawIfd(
        TiffReader reader)
    {
        var ifd0 = reader.ParseIfd(reader.If0Offset);
        uint[] subOffsets = ifd0.TryGetValue(TagSubIfds, out var subList)
            ? subList.ToArray()
            : Array.Empty<uint>();
        return (ifd0, FindRawIfd(reader, ifd0, subOffsets));
    }

    /// <summary>Scalar domain tags of the raw SubIFD (shared by the decode and
    /// passthrough read paths).</summary>
    private sealed record RawDomain(int W, int H, ushort Compression, ushort Photometric, int Samples,
        int RowsPerStrip, int BlackLevel, int WhiteLevel);

    private static RawDomain ReadDomain(TiffReader reader, Dictionary<ushort, List<uint>> rawIfd)
    {
        int h = (int)(reader.EntryU32(rawIfd, TagImageLength, required: true) ?? 0);
        return new RawDomain(
            W: (int)(reader.EntryU32(rawIfd, TagImageWidth, required: true) ?? 0),
            H: h,
            Compression: reader.EntryU16(rawIfd, TagCompression, required: true) ?? 0,
            Photometric: reader.EntryU16(rawIfd, TagPhotometric, required: true) ?? 0,
            Samples: reader.EntryU16(rawIfd, TagSamplesPerPixel, required: false) ?? 1,
            RowsPerStrip: (int)(reader.EntryU32(rawIfd, TagRowsPerStrip, required: false) ?? (uint)h),
            BlackLevel: reader.EntryU16(rawIfd, TagBlackLevel, required: false) ?? 0,
            WhiteLevel: (int)(reader.EntryU32(rawIfd, TagWhiteLevel, required: false) ?? 65535));
    }

    private static void Validate(RawDomain d)
    {
        if (d.W <= 0 || d.H <= 0 || d.Samples is < 1 or > 4)
            throw new InvalidDataException($"后端 DNG raw IFD 无效尺寸/采样: {d.W}×{d.H}×{d.Samples}");
        if (d.RowsPerStrip <= 0)
            throw new InvalidDataException($"后端 DNG RowsPerStrip 无效: {d.RowsPerStrip}");
    }

    /// <summary>Picks the raw SubIFD: LinearRaw (34892) when present, else the
    /// largest stored image among IFD0/SubIFDs (backend layout: exactly one
    /// raw SubIFD beside the IFD0 thumbnail).</summary>
    private static Dictionary<ushort, List<uint>> FindRawIfd(TiffReader reader,
        Dictionary<ushort, List<uint>> ifd0, uint[] subOffsets)
    {
        Dictionary<ushort, List<uint>>? best = null;
        long bestArea = -1;
        bool bestLinear = false;

        void Consider(Dictionary<ushort, List<uint>> ifd)
        {
            if (!ifd.TryGetValue(TagImageWidth, out var ws) ||
                !ifd.TryGetValue(TagImageLength, out var hs))
                return;
            long area = (long)ws[0] * hs[0];
            bool linear = ifd.TryGetValue(TagPhotometric, out var pm) && pm[0] == 34892;
            if (best == null || (linear && !bestLinear) || (linear == bestLinear && area > bestArea))
            {
                best = ifd;
                bestArea = area;
                bestLinear = linear;
            }
        }

        Consider(ifd0);
        foreach (uint off in subOffsets)
            Consider(reader.ParseIfd(off));
        return best ?? throw new InvalidDataException("后端 DNG 中未找到 raw 图像 IFD");
    }

    private static ushort[] ReadCompressedStrips(TiffReader reader,
        Dictionary<ushort, List<uint>> rawIfd, int w, int h, int samples, int rowsPerStrip)
    {
        var offsets = reader.EntryU32Array(rawIfd, TagStripOffsets);
        var counts = reader.EntryU32Array(rawIfd, TagStripByteCounts);
        var pixels = new ushort[(long)w * h * samples];

        int strips = Math.Min(offsets.Count, counts.Count);
        int expectedStrips = (h + rowsPerStrip - 1) / rowsPerStrip;
        if (strips < expectedStrips)
            throw new InvalidDataException($"条带不足: {strips} < {expectedStrips} (H={h}, RPS={rowsPerStrip})");

        for (int i = 0; i < expectedStrips; i++)
        {
            int rows = Math.Min(rowsPerStrip, h - i * rowsPerStrip);
            long dstOff = (long)i * rowsPerStrip * w * samples;
            long decoded = reader.DecodeStrip(offsets[i], counts[i], pixels, dstOff,
                w, rows, samples);
            if (decoded != (long)w * rows * samples)
                throw new InvalidDataException(
                    $"条带 {i} 解码样本数 {decoded} != 期望 {w}×{rows}×{samples}");
        }
        return pixels;
    }

    private static ushort[] ReadUncompressedStrips(TiffReader reader,
        Dictionary<ushort, List<uint>> rawIfd, int w, int h, int samples, int rowsPerStrip)
    {
        var offsets = reader.EntryU32Array(rawIfd, TagStripOffsets);
        var counts = reader.EntryU32Array(rawIfd, TagStripByteCounts);
        var pixels = new byte[(long)w * h * samples * 2];

        int strips = Math.Min(offsets.Count, counts.Count);
        int expectedStrips = (h + rowsPerStrip - 1) / rowsPerStrip;
        if (strips < expectedStrips)
            throw new InvalidDataException($"条带不足: {strips} < {expectedStrips}");

        for (int i = 0; i < expectedStrips; i++)
        {
            int rows = Math.Min(rowsPerStrip, h - i * rowsPerStrip);
            long byteCount = (long)w * rows * samples * 2;
            if (counts[i] != byteCount)
                throw new InvalidDataException(
                    $"未压缩条带 {i} 字节数 {counts[i]} != 期望 {byteCount}");
            reader.ReadBytes(offsets[i], pixels, (long)i * rowsPerStrip * w * samples * 2, byteCount);
        }
        // tifffile writes little-endian; verified at header parse
        var words = new ushort[pixels.Length / 2];
        Buffer.BlockCopy(pixels, 0, words, 0, pixels.Length);
        return words;
    }

    private static float[]? ReadAsShotNeutral(TiffReader reader, Dictionary<ushort, List<uint>> ifd0)
    {
        var v = reader.EntryRationals(ifd0, TagAsShotNeutral);
        if (v is not { Count: >= 3 }) return null;
        return new[] { (float)(v[0].num / (double)v[0].den), (float)(v[1].num / (double)v[1].den),
            (float)(v[2].num / (double)v[2].den) };
    }

    private static double[]? ReadColorMatrix1(TiffReader reader, Dictionary<ushort, List<uint>> ifd0)
    {
        var v = reader.EntrySRationals(ifd0, TagColorMatrix1);
        if (v is not { Count: >= 9 }) return null;
        var m = new double[9];
        for (int i = 0; i < 9; i++) m[i] = v[i].num / (double)v[i].den;
        return m;
    }

    /// <summary>
    /// Random-access little-endian TIFF reader over a file stream (no whole-file
    /// load: full-frame backend products are hundreds of MB; tifffile output
    /// is fixed little-endian).
    /// </summary>
    private sealed class TiffReader : IDisposable
    {
        private readonly FileStream _stream;

        public uint If0Offset { get; }

        public TiffReader(string path)
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[8];
            ReadAt(0, header);
            if (header[0] != 'I' || header[1] != 'I' || (header[2] | (header[3] << 8)) != 42)
                throw new InvalidDataException(
                    "后端 DNG 不是小端 TIFF（tifffile 产物固定 II）");
            If0Offset = U32(header, 4);
        }

        /// <summary>Little-endian u32 at buffer position.</summary>
        private static uint U32(ReadOnlySpan<byte> b, int off) =>
            (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

        private void ReadAt(long offset, Span<byte> buffer)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < buffer.Length)
            {
                int n = _stream.Read(buffer[read..]);
                if (n <= 0) throw new EndOfStreamException($"TIFF 读取越界: offset={offset}, need={buffer.Length}");
                read += n;
            }
        }

        public void ReadBytes(long offset, byte[] dst, long dstOffset, long count)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(dst, (int)(dstOffset + read), (int)Math.Min(count - read, int.MaxValue));
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
        }

        private ushort U16At(long offset)
        {
            Span<byte> b = stackalloc byte[2];
            ReadAt(offset, b);
            return (ushort)(b[0] | (b[1] << 8));
        }

        private uint U32At(long offset)
        {
            Span<byte> b = stackalloc byte[4];
            ReadAt(offset, b);
            return U32(b, 0);
        }

        /// <summary>Parses one IFD into tag → raw entry values (u32 units:
        /// SHORTs zero-extended, LONGs as-is, offset arrays resolved).</summary>
        public Dictionary<ushort, List<uint>> ParseIfd(long offset)
        {
            var result = new Dictionary<ushort, List<uint>>();
            int count = U16At(offset);
            for (int i = 0; i < count; i++)
            {
                long e = offset + 2 + i * 12;
                ushort tag = U16At(e);
                ushort type = U16At(e + 2);
                uint cnt = U32At(e + 4);
                if (type is 0 or > 13 || cnt > 10_000_000) continue;   // malformed entry
                long size = (long)TiffConstants.SizeOfType(type) * cnt;
                var values = new List<uint>((int)Math.Min(cnt, 1024));
                if (size <= 4)
                {
                    uint val = U32At(e + 8);
                    if (type is 3 or 8 && cnt <= 2)
                    {
                        for (int k = 0; k < cnt; k++)
                            values.Add((val >> (16 * k)) & 0xFFFF);
                    }
                    else
                        values.Add(val);
                }
                else
                {
                    long data = U32At(e + 8);
                    for (int k = 0; k < cnt; k++)
                    {
                        int es = TiffConstants.SizeOfType(type);
                        values.Add(es switch
                        {
                            1 => ReadByteAt(data + k),
                            3 => U16At(data + (long)k * 2),
                            _ => U32At(data + (long)k * 4),
                        });
                    }
                }
                result[tag] = values;
            }
            return result;
        }

        private uint ReadByteAt(long offset)
        {
            Span<byte> b = stackalloc byte[1];
            ReadAt(offset, b);
            return b[0];
        }

        public ushort? EntryU16(Dictionary<ushort, List<uint>> ifd, ushort tag, bool required)
        {
            if (!ifd.TryGetValue(tag, out var v) || v.Count == 0)
            {
                if (required) throw new InvalidDataException($"后端 DNG 缺少必需标签 {tag}");
                return null;
            }
            return (ushort)v[0];
        }

        public uint? EntryU32(Dictionary<ushort, List<uint>> ifd, ushort tag, bool required)
        {
            if (!ifd.TryGetValue(tag, out var v) || v.Count == 0)
            {
                if (required) throw new InvalidDataException($"后端 DNG 缺少必需标签 {tag}");
                return null;
            }
            return v[0];
        }

        public List<uint> EntryU32Array(Dictionary<ushort, List<uint>> ifd, ushort tag)
        {
            if (!ifd.TryGetValue(tag, out var v) || v.Count == 0)
                throw new InvalidDataException($"后端 DNG 缺少必需标签 {tag}");
            return v;
        }

        public List<(long num, long den)>? EntryRationals(Dictionary<ushort, List<uint>> ifd, ushort tag)
        {
            if (!ifd.TryGetValue(tag, out var v)) return null;
            var r = new List<(long, long)>();
            for (int i = 0; i + 1 < v.Count; i += 2)
                r.Add((v[i], v[i + 1]));
            return r;
        }

        public List<(long num, long den)>? EntrySRationals(Dictionary<ushort, List<uint>> ifd, ushort tag)
        {
            var r = EntryRationals(ifd, tag);
            if (r == null) return null;
            for (int i = 0; i < r.Count; i++)
            {
                // reinterpret u32 pairs as i32 (sign) when the value exceeds int31
                long n = r[i].num > int.MaxValue ? r[i].num - 4294967296L : r[i].num;
                long d = r[i].den > int.MaxValue ? r[i].den - 4294967296L : r[i].den;
                r[i] = (n, d);
            }
            return r;
        }

        /// <summary>Decodes one LJ strip into <paramref name="pixels"/> at
        /// <paramref name="dstOffset"/>, validating the stream's geometry.</summary>
        public long DecodeStrip(uint stripOffset, uint stripBytes, ushort[] pixels, long dstOffset,
            int expectW, int expectRows, int samples)
        {
            if (stripBytes < 4) throw new InvalidDataException($"条带字节数无效: {stripBytes}");
            var strip = new byte[stripBytes];
            ReadBytes(stripOffset, strip, 0, stripBytes);
            long decoded = DngLjpegNative.DecodeStream(strip, pixels, dstOffset,
                out uint sw, out uint sh, out uint sc);
            if (decoded < 0)
                throw new InvalidDataException($"LJ 条带解码失败 (code {decoded}, offset {stripOffset})");
            if (sw != expectW || sh != expectRows || sc != samples)
                throw new InvalidDataException(
                    $"条带几何 {sw}×{sh}×{sc} != 期望 {expectW}×{expectRows}×{samples}");
            return decoded;
        }

        public void Dispose() => _stream.Dispose();
    }
}
