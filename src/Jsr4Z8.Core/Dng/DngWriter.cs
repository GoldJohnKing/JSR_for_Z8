using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jsr4Z8.Core.Dng;

/// <summary>
/// Writes uncompressed 16-bit DNG files with original metadata preserved.
///
/// Topology (replicates Adobe DNG Converter so ACR renders identically):
///   IFD0  = thumbnail (NewSubfileType=1) + baseline/color/exposure tags + EXIF/GPS
///   SubIFDs(0x014A) → raw IFD (NewSubfileType=0, CFA, opcodes, ActiveArea) + previews
///   IFD1  = absent (IFD0 itself is the thumbnail for generic TIFF/WIC readers)
///
/// Rendering-parity values calibrated against ADC 18.6 output for the Z8:
///   WhiteLevel 63568 (= 15892×4), BaselineExposure +0.20 EV, BaselineNoise 0.8,
///   BaselineSharpness 1.0, AsShotNeutral = 1/cam_mul (per shot, from LibRaw).
///
/// Ported from Save_Z8 src/SaveZ8.Core/DngWriter.cs. JSR4Z8 adaptation at the
/// entry point only (internals byte-faithful): the output domain
/// (black/white level, photometric, samples-per-pixel, uncompressed bit shift,
/// CFA phase) arrives via <see cref="DngWriteOptions"/> so Task 9's LinearRaw
/// RGB payloads reuse the same engine; the metadata model is
/// <see cref="NefAssetExtractor.ExtractedMetadata"/>; the CFA pattern comes
/// pre-mapped to DNG colors (0/1/2).
/// </summary>
public class DngWriter
{
    // TIFF/DNG tags
    private const ushort TagNewSubfileType = 254;
    private const ushort TagImageWidth = 256;
    private const ushort TagImageLength = 257;
    private const ushort TagBitsPerSample = 258;
    private const ushort TagCompression = 259;
    private const ushort TagPhotometricInterpretation = 262;
    private const ushort TagMake = 271;
    private const ushort TagModel = 272;
    private const ushort TagStripOffsets = 273;
    private const ushort TagSamplesPerPixel = 277;
    private const ushort TagRowsPerStrip = 278;
    private const ushort TagStripByteCounts = 279;
    private const ushort TagPlanarConfiguration = 284;
    private const ushort TagTileWidth = 322;
    private const ushort TagTileLength = 323;
    private const ushort TagTileOffsets = 324;
    private const ushort TagTileByteCounts = 325;
    private const ushort TagJpegInterchangeFormat = 513;
    private const ushort TagJpegInterchangeFormatLength = 514;
    private const ushort TagCFARepeatPatternDim = 33421;
    private const ushort TagCFAPattern = 33422;
    private const ushort TagSubIFDs = 0x014A;
    private const ushort TagDNGVersion = 50706;
    private const ushort TagDNGBackwardVersion = 50707;
    private const ushort TagUniqueCameraModel = 50708;
    private const ushort TagBlackLevelRepeatDim = 50713;
    private const ushort TagBlackLevel = 50714;
    private const ushort TagWhiteLevel = 50717;
    private const ushort TagColorMatrix1 = 50721;
    private const ushort TagCalibrationIlluminant1 = 50778;
    private const ushort TagDefaultScale = 50718;
    private const ushort TagDefaultCropOrigin = 50719;
    private const ushort TagDefaultCropSize = 50720;
    private const ushort TagActiveArea = 50829;
    private const ushort TagOpcodeList2 = 51009;
    private const ushort TagOpcodeList3 = 51022;
    private const ushort TagSoftware = 305;
    private const ushort TagArtist = 315;
    private const ushort TagBaselineExposure = 50730;
    private const ushort TagBaselineNoise = 50731;
    private const ushort TagBaselineSharpness = 50732;
    private const ushort TagAsShotNeutral = 50728;
    private const ushort TagExifIFD = 0x8769;
    private const ushort TagGpsIFD = 0x8825;

    // TIFF field types
    private const ushort TypeByte = 1;
    private const ushort TypeAscii = 2;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const ushort TypeRational = 5;
    private const ushort TypeSRational = 10;

    // Rendering-parity constants (formerly the DngOptions class — no caller ever
    // overrode any of them, so they are fixed private constants now). The black/
    // white-level domain pair (compressed 1008/15892, uncompressed 4032/63568 and
    // the ×4 pixel shift) moved to DngComposer/DngWriteOptions with the Task 9
    // LinearRaw payload in view.
    private const string CameraModelName = "Nikon Z 8";
    private const int CalibrationIlluminant1Value = 21;    // D65
    private static readonly double[] ColorMatrix1Values =
        { 1.1423, -0.4564, -0.1123, -0.4816, 1.2895, 0.2119, -0.021, 0.1061, 0.7282 };

    // IFD0 tags safe to copy verbatim from the source file (scalar/string/blobs only).
    private static readonly HashSet<ushort> CopyableIfd0Tags = new()
        { 270, 274, 282, 283, 296, 305, 306, 315, 700, 33432 };

    // Zero bytes for explicitly filling TIFF alignment gaps during serialization.
    private static readonly byte[] AlignmentZeros = new byte[16];

    // ---------- output model ----------

    private sealed class Entry
    {
        public ushort Tag, Type;
        public uint Count;
        public byte[]? Inline;      // ≤4 bytes, little-endian, final
        public byte[]? Blob;        // >4 bytes, little-endian, final (Inline gets blob offset)
        public IfdBlock? SubBlock;  // tag value = offset of this block
        public List<IfdBlock>? SubBlockList; // tag value = offset of uint[] array (SubIFDs)
        public List<byte[]>? StreamData;     // compressed: value = offset of stream-offset array (tiles or strips); filled at layout phase 3
        public bool IsPixelData;    // tag value = pixel data offset
    }

    private sealed class IfdBlock
    {
        public List<Entry> Entries { get; } = new();
        public uint Offset;             // assigned during layout
    }

    public static void Write(string outputPath, ushort[] pixels, int width, int height,
        DngWriteOptions options, NefAssetExtractor.ExtractedMetadata? originalMetadata,
        float[]? cameraWb = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        bool compress = options.Compress;
        int samples = options.SamplesPerPixel;
        bool isCfa = options.CfaPattern != null;
        bool strips = options.Layout == RawLayout.Strips;
        if (isCfa && options.CfaPattern!.Length != 4)
            throw new ArgumentException("CFA pattern must hold 4 colors (2×2)", nameof(options));
        if (strips && (!compress || isCfa))
            throw new ArgumentException(
                "RawLayout.Strips is a compressed LinearRaw profile (backend tifffile parity): " +
                $"requires Compress=true and a non-CFA payload (got Compress={compress}, CFA={isCfa})",
                nameof(options));

        // ================= lossless-JPEG streams (Compression=7, ADC -c profile) =====
        // 256x256 tiles, each an independent SOF3 stream: 16-bit, interleaved
        // components (CFA mosaic column pairs feed the interleaver as 2
        // components with PSV=1 — the ADC profile; LinearRaw RGB as 3 components
        // with PSV=4 Ra+Rb-Rc, parity with the backend's tifffile encoding),
        // per-stream optimized Huffman tables (encoder side). Edge tiles are
        // zero-padded to full geometry (decoder crops via TIFF tile grid).
        // Strips (LinearRaw only, RawLayout.Strips): 128-row bands, each one
        // SOF3 stream over the full image width — backend parity (tifffile writes
        // ceil(H/128) strips, final band = actual remaining rows: 11040 rows → 87
        // strips, last = 32 rows). Strip geometry is exact, no edge padding.
        // Stream offsets follow the ADC layout: sequential, each start 2-byte
        // aligned.
        const int TileSide = 256;
        const int StripRows = 128;
        uint predictor = isCfa ? 1u : 4u;
        List<byte[]>? streams = null;    // tiles or strips: independent LJ streams
        if (compress)
        {
            if (strips)
            {
            // Encoder geometry: one strip = full image width × band rows, the
            // interleaved samples as channels (RGB: width × 3).
            uint encCols = (uint)width;
            uint encChannels = (uint)samples;
            int n = (height + StripRows - 1) / StripRows;
            var stripArr = new byte[n][];
            // Per-thread scratch buffers: band rows (interleaved samples) and the
            // encoded-stream capacity (2× input bytes, the tile convention).
            // Encoding is pure/deterministic per strip, so parallel scheduling
            // yields byte-identical output to sequential.
            var srcBuf = new ThreadLocal<ushort[]>(() => new ushort[(long)width * StripRows * samples]);
            var outBuf = new ThreadLocal<byte[]>(() => new byte[(long)width * StripRows * samples * 4]);
            Parallel.For(0, n, i =>
            {
                int rows = Math.Min(StripRows, height - i * StripRows);   // final band = remainder
                var stripSrc = srcBuf.Value!;
                var stripOut = outBuf.Value!;
                Array.Copy(pixels, (long)i * StripRows * width * samples, stripSrc, 0,
                    (long)rows * width * samples);
                long encoded;
                unsafe
                {
                    fixed (ushort* sp = stripSrc)
                    fixed (byte* op = stripOut)
                    {
                        encoded = Native.DngLjpegNative.lje_encode_tile(sp, (uint)rows, encCols,
                            encChannels, 16, op, (nuint)stripOut.Length, predictor);
                    }
                }
                if (encoded <= 0)
                    throw new IOException($"lossless JPEG strip encode failed (strip {i}, code {encoded})");
                stripArr[i] = stripOut[..(int)encoded];
            });
            srcBuf.Dispose();
            outBuf.Dispose();
            streams = new List<byte[]>(stripArr);
            }
            else
            {
            // Encoder geometry: per-component width × component count must equal
            // the samples per tile row (CFA: TileSide/2 × 2; RGB: TileSide × 3).
            uint encCols = isCfa ? (uint)TileSide / 2 : (uint)TileSide;
            uint encChannels = isCfa ? 2 : (uint)samples;
            int tilesX = (width + TileSide - 1) / TileSide;
            int tilesY = (height + TileSide - 1) / TileSide;
            int n = tilesX * tilesY;
            var tileArr = new byte[n][];
            // Per-thread scratch buffers: tile rows (interleaved samples) and the
            // encoded-stream capacity. Encoding is pure/deterministic per tile,
            // so parallel scheduling yields byte-identical output to sequential.
            var srcBuf = new ThreadLocal<ushort[]>(() => new ushort[TileSide * TileSide * samples]);
            var outBuf = new ThreadLocal<byte[]>(() => new byte[256 * 1024 * samples]);
            Parallel.For(0, n, i =>
            {
                int tx = i % tilesX, ty = i / tilesX;
                var tileSrc = srcBuf.Value!;
                var tileOut = outBuf.Value!;
                Array.Clear(tileSrc, 0, tileSrc.Length);
                int rowSamples = TileSide * samples;
                int copyW = Math.Min(TileSide, width - tx * TileSide) * samples;
                for (int r = 0; r < TileSide; r++)
                {
                    int y = ty * TileSide + r;
                    if (y >= height) break;
                    long dst = (long)r * rowSamples;
                    Array.Copy(pixels, (long)(y * width + tx * TileSide) * samples, tileSrc, dst, copyW);
                    // Compressed tiles keep the payload's native domain (CFA: 14-bit,
                    // BL/WL tags 1008/15892 accordingly — same ADC convention).
                }
                long encoded;
                unsafe
                {
                    fixed (ushort* sp = tileSrc)
                    fixed (byte* op = tileOut)
                    {
                        encoded = Native.DngLjpegNative.lje_encode_tile(sp, TileSide,
                            encCols, encChannels, 16, op, (nuint)tileOut.Length, predictor);
                    }
                }
                if (encoded <= 0)
                    throw new IOException($"lossless JPEG tile encode failed (code {encoded})");
                tileArr[i] = tileOut[..(int)encoded];
            });
            srcBuf.Dispose();
            outBuf.Dispose();
            streams = new List<byte[]>(tileArr);
            }
        }

        // ================= raw IFD =================
        var rawBlock = new IfdBlock();

        void RawShort(ushort tag, ushort value) =>
            rawBlock.Entries.Add(new Entry { Tag = tag, Type = TypeShort, Count = 1, Inline = Pad(BitConverter.GetBytes(value)) });
        void RawLong(ushort tag, uint value) =>
            rawBlock.Entries.Add(new Entry { Tag = tag, Type = TypeLong, Count = 1, Inline = Pad(BitConverter.GetBytes(value)) });
        void RawShorts(ushort tag, ushort[] values)
        {
            var bytes = new byte[values.Length * 2];
            for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(bytes, i * 2);
            rawBlock.Entries.Add(bytes.Length <= 4
                ? new Entry { Tag = tag, Type = TypeShort, Count = (uint)values.Length, Inline = Pad(bytes) }
                : new Entry { Tag = tag, Type = TypeShort, Count = (uint)values.Length, Blob = bytes });
        }
        void RawLongs(ushort tag, uint[] values)
        {
            var bytes = new byte[values.Length * 4];
            for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(bytes, i * 4);
            rawBlock.Entries.Add(new Entry { Tag = tag, Type = TypeLong, Count = (uint)values.Length, Blob = bytes });
        }
        void RawRationals(ushort tag, (int num, int den)[] values)
        {
            var bytes = new byte[values.Length * 8];
            for (int i = 0; i < values.Length; i++)
            {
                BitConverter.GetBytes(values[i].num).CopyTo(bytes, i * 8);
                BitConverter.GetBytes(values[i].den).CopyTo(bytes, i * 8 + 4);
            }
            rawBlock.Entries.Add(new Entry { Tag = tag, Type = TypeRational, Count = (uint)values.Length, Blob = bytes });
        }

        int bl = options.BlackLevel;
        int wl = options.WhiteLevel;

        RawLong(TagNewSubfileType, 0);
        RawLong(TagImageWidth, (uint)width);
        RawLong(TagImageLength, (uint)height);
        RawShorts(TagBitsPerSample, Enumerable.Repeat((ushort)16, samples).ToArray());
        RawShort(TagCompression, compress ? (ushort)7 : (ushort)1); // 7 = lossless JPEG (ADC -c), 1 = uncompressed
        RawShort(TagPhotometricInterpretation, options.PhotometricInterpretation); // 32803 CFA / 34892 LinearRaw
        RawShort(TagSamplesPerPixel, (ushort)samples);
        if (compress)
        {
            if (strips)
            {
                // Strip layout: RowsPerStrip=128 (backend parity), no tile tags.
                RawLong(TagRowsPerStrip, StripRows);
            }
            else
            {
                RawShort(TagTileWidth, TileSide);
                RawShort(TagTileLength, TileSide);
            }
        }
        else
        {
            RawLong(TagRowsPerStrip, (uint)height);
        }
        RawShort(TagPlanarConfiguration, 1);
        if (isCfa)
        {
            RawShorts(TagCFARepeatPatternDim, new ushort[] { 2, 2 });
            rawBlock.Entries.Add(new Entry { Tag = TagCFAPattern, Type = TypeByte, Count = 4, Inline = Pad(options.CfaPattern!) });
            RawShorts(TagBlackLevelRepeatDim, new ushort[] { 2, 2 });
            RawShorts(TagBlackLevel, new[] { (ushort)bl, (ushort)bl, (ushort)bl, (ushort)bl });
        }
        else
        {
            RawShort(TagBlackLevel, (ushort)bl);
        }
        RawLong(TagWhiteLevel, (uint)wl);

        // ActiveArea: full stored area — replicates ADC's Z8 geometry. The warp/vignette
        // opcodes normalize radius over the active area, so this must match ADC's
        // convention (full 8280x5520) or the correction strength shifts. Task 9
        // overrides this with the ×2-scaled sensor active area (options.ActiveArea).
        if (options.ActiveArea is { Length: 4 } aa)
            RawLongs(TagActiveArea, aa);
        else
            RawLongs(TagActiveArea, new[] { 0u, 0u, (uint)height, (uint)width });

        // DefaultCrop: ADC pairs the full ActiveArea with DefaultScale/Crop tags so the
        // displayed image matches the NEF's framing (Z8: origin 12,8, size 8256x5504).
        // Derived from the largest embedded preview (= camera's active crop), origin
        // centered and CFA-phase-even. Task 9 overrides the pair explicitly
        // (options.DefaultCrop) for the backend's ×2 geometry.
        if (options.DefaultCrop is { } crop)
        {
            RawRationals(TagDefaultScale, new[] { (1, 1), (1, 1) });
            RawRationals(TagDefaultCropOrigin, new[] { ((int)crop.X, 1), ((int)crop.Y, 1) });
            RawRationals(TagDefaultCropSize, new[] { ((int)crop.W, 1), ((int)crop.H, 1) });
        }
        else
        {
            var previews0 = originalMetadata?.Previews;
            if (previews0 is { Count: > 0 })
            {
                var active = previews0[^1];
                if (active.Width <= width && active.Height <= height &&
                    (active.Width < width || active.Height < height))
                {
                    uint ox = (uint)(((width - active.Width) / 2) & ~1);
                    uint oy = (uint)(((height - active.Height) / 2) & ~1);
                    RawRationals(TagDefaultScale, new[] { (1, 1), (1, 1) });
                    RawRationals(TagDefaultCropOrigin, new[] { ((int)ox, 1), ((int)oy, 1) });
                    RawRationals(TagDefaultCropSize, new[] { (active.Width, 1), (active.Height, 1) });
                }
            }
        }

        // Strip data location — real entries, values resolved by the layout pass
        if (compress)
        {
            // Value field points to a u32 array of stream offsets (tiles or
            // strips); array content is resolved at layout phase 3 (depends on
            // pixelOffset + prefix sizes).
            rawBlock.Entries.Add(new Entry { Tag = strips ? TagStripOffsets : TagTileOffsets,
                Type = TypeLong, Count = (uint)streams!.Count, Blob = new byte[streams.Count * 4],
                StreamData = streams });
            RawLongs(strips ? TagStripByteCounts : TagTileByteCounts,
                streams.Select(s => (uint)s.Length).ToArray());
        }
        else
        {
            rawBlock.Entries.Add(new Entry { Tag = TagStripOffsets, Type = TypeLong, Count = 1, IsPixelData = true });
            RawLong(TagStripByteCounts, (uint)(pixels.Length * 2));
        }

        // ---- lens correction opcodes from Nikon NEFInfo (0xC7D5) ----
        var correction = NikonLensCorrection.Decode(originalMetadata?.NefInfoData);
        if (correction?.Warp != null)
        {
            var opcodes = NikonLensCorrection.SerializeOpcodeList3(correction.Warp);
            rawBlock.Entries.Add(new Entry { Tag = TagOpcodeList3, Type = 7,
                Count = (uint)opcodes.Length, Blob = opcodes });
        }
        if (correction?.Vignette != null)
        {
            var opcodes = NikonLensCorrection.SerializeOpcodeList2(correction.Vignette);
            rawBlock.Entries.Add(new Entry { Tag = TagOpcodeList2, Type = 7,
                Count = (uint)opcodes.Length, Blob = opcodes });
        }
        rawBlock.Entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        // ================= preview IFDs =================
        static IfdBlock PreviewBlock(NefAssetExtractor.PreviewInfo p)
        {
            var block = new IfdBlock();
            block.Entries.Add(new Entry { Tag = TagNewSubfileType, Type = TypeLong, Count = 1,
                Inline = Pad(BitConverter.GetBytes(1u)) });
            block.Entries.Add(new Entry { Tag = TagImageWidth, Type = TypeLong, Count = 1,
                Inline = Pad(BitConverter.GetBytes((uint)p.Width)) });
            block.Entries.Add(new Entry { Tag = TagImageLength, Type = TypeLong, Count = 1,
                Inline = Pad(BitConverter.GetBytes((uint)p.Height)) });
            block.Entries.Add(new Entry { Tag = TagBitsPerSample, Type = TypeShort, Count = 3,
                Blob = new byte[] { 8, 0, 8, 0, 8, 0 } });
            block.Entries.Add(new Entry { Tag = TagCompression, Type = TypeShort, Count = 1,
                Inline = Pad(BitConverter.GetBytes((ushort)6)) });          // old-style JPEG
            block.Entries.Add(new Entry { Tag = TagPhotometricInterpretation, Type = TypeShort, Count = 1,
                Inline = Pad(BitConverter.GetBytes((ushort)2)) });          // RGB
            block.Entries.Add(new Entry { Tag = TagJpegInterchangeFormat, Type = TypeLong, Count = 1,
                Blob = p.Jpeg });                                            // value = jpeg offset
            block.Entries.Add(new Entry { Tag = TagSamplesPerPixel, Type = TypeShort, Count = 1,
                Inline = Pad(BitConverter.GetBytes((ushort)3)) });
            block.Entries.Add(new Entry { Tag = TagRowsPerStrip, Type = TypeLong, Count = 1,
                Inline = Pad(BitConverter.GetBytes((uint)p.Height)) });
            block.Entries.Add(new Entry { Tag = TagJpegInterchangeFormatLength, Type = TypeLong, Count = 1,
                Inline = Pad(BitConverter.GetBytes((uint)p.Jpeg.Length)) });
            return block;
        }

        // ================= IFD0 =================
        // With previews: IFD0 = thumbnail (ADC topology), raw + larger previews in SubIFDs.
        // Without previews: IFD0 = raw directly (still valid DNG).
        var previews = originalMetadata?.Previews;
        var ifd0 = new IfdBlock();
        var subIfdTargets = new List<IfdBlock>();

        if (previews is { Count: > 0 })
        {
            // IFD0 becomes the smallest preview (thumbnail); prepend its image tags
            foreach (var e in PreviewBlock(previews[0]).Entries)
                ifd0.Entries.Add(e);

            // SubIFDs: raw first (like ADC), then the remaining previews largest→smallest
            subIfdTargets.Add(rawBlock);
            for (int i = previews.Count - 1; i >= 1; i--)
                subIfdTargets.Add(PreviewBlock(previews[i]));
        }
        else
        {
            // No previews: IFD0 = raw (move all entries)
            foreach (var e in rawBlock.Entries)
                ifd0.Entries.Add(e);
        }

        void AddAscii(ushort tag, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value + "\0");
            ifd0.Entries.Add(bytes.Length <= 4
                ? new Entry { Tag = tag, Type = TypeAscii, Count = (uint)bytes.Length, Inline = Pad(bytes) }
                : new Entry { Tag = tag, Type = TypeAscii, Count = (uint)bytes.Length, Blob = bytes });
        }
        void AddShort(ushort tag, ushort value) =>
            ifd0.Entries.Add(new Entry { Tag = tag, Type = TypeShort, Count = 1, Inline = Pad(BitConverter.GetBytes(value)) });
        void AddRationals(ushort tag, int type, (int num, int den)[] values)
        {
            var bytes = new byte[values.Length * 8];
            for (int i = 0; i < values.Length; i++)
            {
                BitConverter.GetBytes(values[i].num).CopyTo(bytes, i * 8);
                BitConverter.GetBytes(values[i].den).CopyTo(bytes, i * 8 + 4);
            }
            ifd0.Entries.Add(new Entry { Tag = tag, Type = (ushort)type, Count = (uint)values.Length, Blob = bytes });
        }

        AddAscii(TagMake, "Nikon");
        AddAscii(TagModel, CameraModelName);

        ifd0.Entries.Add(new Entry { Tag = TagDNGVersion, Type = TypeByte, Count = 4,
            Inline = Pad(new byte[] { 1, 6, 0, 0 }) });
        ifd0.Entries.Add(new Entry { Tag = TagDNGBackwardVersion, Type = TypeByte, Count = 4,
            Inline = Pad(new byte[] { 1, 4, 0, 0 }) });
        AddAscii(TagUniqueCameraModel, CameraModelName);

        var cmValues = options.ColorMatrix1 ?? ColorMatrix1Values;
        if (cmValues.Length != 9)
            throw new ArgumentException("ColorMatrix1 requires 9 values", nameof(options));
        var cm = new byte[cmValues.Length * 8];
        for (int i = 0; i < cmValues.Length; i++)
        {
            BitConverter.GetBytes((int)Math.Round(cmValues[i] * 10000)).CopyTo(cm, i * 8);
            BitConverter.GetBytes(10000).CopyTo(cm, i * 8 + 4);
        }
        ifd0.Entries.Add(new Entry { Tag = TagColorMatrix1, Type = TypeSRational,
            Count = (uint)cmValues.Length, Blob = cm });
        AddShort(TagCalibrationIlluminant1, (ushort)CalibrationIlluminant1Value);

        // Rendering parity with Adobe's Z8 defaults (measured from ADC 18.6 output)
        AddRationals(TagBaselineExposure, TypeSRational, new[] { (20, 100) });     // +0.20 EV
        AddRationals(TagBaselineNoise, TypeRational, new[] { (80, 100) });
        AddRationals(TagBaselineSharpness, TypeRational, new[] { (100, 100) });

        // AsShotNeutral: per-shot camera white balance (inverse multipliers, like ADC).
        // options.AsShotNeutral (backend product value, Task 9) wins over cameraWb.
        var explicitNeutral = options.AsShotNeutral;
        if (explicitNeutral is { Length: >= 3 } &&
            explicitNeutral[0] > 0 && explicitNeutral[1] > 0 && explicitNeutral[2] > 0)
        {
            AddRationals(TagAsShotNeutral, TypeRational, new[]
            {
                ((int)Math.Round(explicitNeutral[0] * 1e6), 1000000),
                ((int)Math.Round(explicitNeutral[1] * 1e6), 1000000),
                ((int)Math.Round(explicitNeutral[2] * 1e6), 1000000),
            });
        }
        else if (cameraWb is { Length: >= 3 } && cameraWb[0] > 0 && cameraWb[1] > 0 && cameraWb[2] > 0)
        {
            AddRationals(TagAsShotNeutral, TypeRational, new[]
            {
                ((int)Math.Round(1e6 / cameraWb[0]), 1000000),
                ((int)Math.Round(1e6 / cameraWb[1]), 1000000),
                ((int)Math.Round(1e6 / cameraWb[2]), 1000000),
            });
        }

        // ---- preserve original metadata (EXIF → Interop, GPS, safe IFD0 tags) ----
        IfdBlock? exifBlock = null, gpsBlock = null;
        if (originalMetadata != null && originalMetadata.AllIfds.Count > 0)
        {
            var srcIfd0 = originalMetadata.AllIfds[0];

            foreach (var tag in CopyableIfd0Tags)
            {
                // Task 9 attribution policy (fact #9): the final product names
                // itself via options.Software and carries no IFD0 Artist tag;
                // NEF EXIF-region photographer metadata migrates untouched.
                if (tag == TagSoftware && options.Software != null) continue;
                if (tag == TagArtist && options.OmitIfd0Artist) continue;
                var e = srcIfd0.Entries.Find(x => x.Tag == tag);
                if (e != null) CopyEntry(ifd0, e, originalMetadata.IsLittleEndian);
            }

            exifBlock = FindSubIfd(originalMetadata, 0x8769, out var srcExif) ? ReEmitIfd(srcExif, originalMetadata) : null;
            gpsBlock = FindSubIfd(originalMetadata, 0x8825, out var srcGps) ? ReEmitIfd(srcGps, originalMetadata) : null;

            if (exifBlock != null)
                ifd0.Entries.Add(new Entry { Tag = TagExifIFD, Type = TypeLong, Count = 1, SubBlock = exifBlock });
            if (gpsBlock != null)
                ifd0.Entries.Add(new Entry { Tag = TagGpsIFD, Type = TypeLong, Count = 1, SubBlock = gpsBlock });
        }

        if (options.Software != null)
            AddAscii(TagSoftware, options.Software);

        // SubIFDs pointer (raw + previews) — ADC topology
        if (subIfdTargets.Count == 1)
            ifd0.Entries.Add(new Entry { Tag = TagSubIFDs, Type = TypeLong, Count = 1, SubBlock = subIfdTargets[0] });
        else if (subIfdTargets.Count > 1)
            ifd0.Entries.Add(new Entry { Tag = TagSubIFDs, Type = TypeLong,
                Count = (uint)subIfdTargets.Count, SubBlockList = subIfdTargets });

        ifd0.Entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        // ---- layout phase 1: BFS over blocks, assign IFD offsets and plain blobs ----
        uint cursor = 8;
        var pending = new List<IfdBlock> { ifd0 };
        var orderedBlocks = new List<IfdBlock>();
        var multiPointers = new List<Entry>();
        while (pending.Count > 0)
        {
            var block = pending[0]; pending.RemoveAt(0);
            if (orderedBlocks.Contains(block)) continue;
            orderedBlocks.Add(block);
            block.Offset = cursor;
            cursor += (uint)(2 + block.Entries.Count * 12 + 4);
            foreach (var e in block.Entries)
            {
                if (e.Blob != null && e.SubBlockList == null)
                {
                    cursor = Align(cursor, 4);
                    e.Inline = BitConverter.GetBytes(cursor);
                    cursor += (uint)e.Blob.Length;
                }
                if (e.SubBlock != null) pending.Add(e.SubBlock);
                if (e.SubBlockList != null)
                {
                    multiPointers.Add(e);
                    foreach (var sub in e.SubBlockList) pending.Add(sub);
                }
            }
        }

        // ---- layout phase 2: SubIFD pointer arrays appended after all blocks ----
        foreach (var e in multiPointers)
        {
            var arr = new byte[e.SubBlockList!.Count * 4];
            for (int i = 0; i < e.SubBlockList.Count; i++)
                BitConverter.GetBytes(e.SubBlockList[i].Offset).CopyTo(arr, i * 4);
            e.Blob = arr;
            cursor = Align(cursor, 4);
            e.Inline = BitConverter.GetBytes(cursor);
            cursor += (uint)arr.Length;
        }

        uint pixelOffset = Align(cursor, 16);

        // ---- layout phase 3: resolve pointer-valued entries ----
        foreach (var block in orderedBlocks)
            foreach (var e in block.Entries)
                if (e.IsPixelData) e.Inline = BitConverter.GetBytes(pixelOffset);
                else if (e.SubBlock != null) e.Inline = BitConverter.GetBytes(e.SubBlock.Offset);
                else if (e.StreamData != null)
                {
                    // Stream offsets (tiles or strips): sequential from pixelOffset,
                    // each start 2-byte aligned (matches the serializer's packing
                    // rule below).
                    uint off = pixelOffset;
                    for (int i = 0; i < e.StreamData.Count; i++)
                    {
                        BitConverter.GetBytes(off).CopyTo(e.Blob!, i * 4);
                        off += (uint)e.StreamData[i].Length;
                        off += (off & 1); // pad to even
                    }
                }

        // ---- serialize: single pass directly to file (no full-image intermediate copies) ----
        // M1 (independent review): serialize to a temp sibling first — on any failure
        // the previous good output at outputPath stays intact and no half-written
        // DNG is left behind; swap in atomically on success.
        string tmpPath = outputPath + ".tmp";
        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true))
            {
            bw.Write((byte)0x49); bw.Write((byte)0x49); bw.Write((ushort)42); bw.Write((uint)8);

            foreach (var block in orderedBlocks)
            {
                fs.Position = block.Offset;
                bw.Write((ushort)block.Entries.Count);
                foreach (var e in block.Entries)
                {
                    bw.Write(e.Tag); bw.Write(e.Type); bw.Write(e.Count);
                    var inline = new byte[4]; // TIFF value field is fixed 4 bytes
                    if (e.Inline != null) Array.Copy(e.Inline, inline, Math.Min(4, e.Inline.Length));
                    bw.Write(inline);
                }
                bw.Write(0u); // next IFD: none (IFD chains are modeled as SubIFDs)
                foreach (var e in block.Entries)
                    if (e.Blob != null && e.SubBlockList == null)
                        WriteBlob4Aligned(bw, fs, e.Blob);
            }
            foreach (var e in multiPointers)
                WriteBlob4Aligned(bw, fs, e.Blob!);

            // Pixel data: domain expansion (CFA uncompressed: ×4, clamped to u16) streamed
            // straight to the file in bounded 128 KB chunks — replaces full-image
            // intermediate copies. Compressed payloads keep native values (shift 0).
            long pos = fs.Position;
            // L3 (independent review): guard the write-position == layout-cursor invariant.
            if (pos < pixelOffset)
            {
                Debug.Assert(pixelOffset - pos <= AlignmentZeros.Length, "alignment gap exceeds zero buffer");
                bw.Write(AlignmentZeros, 0, (int)(pixelOffset - pos));
            }
            else
            {
                // A backward seek here would silently overwrite metadata — layout bug.
                Debug.Assert(fs.Position == pixelOffset, "serialization position drifted from layout cursor");
                fs.Position = pixelOffset;
            }
            if (compress)
            {
                // Tile/strip streams are pre-encoded; write sequentially, each
                // start 2-byte aligned (same rule as the phase-3 offset
                // computation).
                foreach (var stream in streams!)
                {
                    if ((fs.Position & 1) == 1) bw.Write((byte)0);
                    bw.Write(stream);
                }
            }
            else
            {
                const int ChunkPixels = 1 << 16;
                var chunk = new byte[ChunkPixels * 2];
                for (int i = 0; i < pixels.Length; i += ChunkPixels)
                {
                    int n = Math.Min(ChunkPixels, pixels.Length - i);
                    var src = pixels.AsSpan(i, n);
                    int off = 0;
                    for (int k = 0; k < src.Length; k++)
                    {
                        ushort v = (ushort)Math.Min(src[k] << options.PixelBitShift, 65535);
                        chunk[off++] = (byte)v;
                        chunk[off++] = (byte)(v >> 8);
                    }
                    bw.Write(chunk, 0, n * 2);
                }
            }
            }
            File.Move(tmpPath, outputPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { /* best-effort cleanup */ }
            throw;
        }
    }

    // ---------- metadata relocation helpers ----------

    /// <summary>Finds the sub-IFD referenced from IFD0 by pointer tag (0x8769 EXIF / 0x8825 GPS).</summary>
    private static bool FindSubIfd(NefAssetExtractor.ExtractedMetadata meta, ushort pointerTag,
        out NefAssetExtractor.TiffIfd sub)
    {
        sub = new NefAssetExtractor.TiffIfd();
        if (meta.AllIfds.Count == 0) return false;
        var srcIfd0 = meta.AllIfds[0];
        var ptr = srcIfd0.Entries.Find(x => x.Tag == pointerTag);
        if (ptr == null) return false;
        var match = srcIfd0.SubIfds.FirstOrDefault(s => s.Offset == ptr.ValueOrOffset);
        if (match == null || match.Entries.Count == 0) return false;
        sub = match;
        return true;
    }

    /// <summary>
    /// Re-emits a source IFD (EXIF or GPS) into the output: every entry is copied with its
    /// data relocated; nested IFD pointers (Interop 0xA005) become relocated sub-blocks.
    /// MakerNote is copied verbatim as an opaque blob (Nikon MakerNotes are self-relative).
    /// </summary>
    private static IfdBlock ReEmitIfd(NefAssetExtractor.TiffIfd src, NefAssetExtractor.ExtractedMetadata meta)
    {
        var block = new IfdBlock();
        bool srcLE = meta.IsLittleEndian;

        foreach (var e in src.Entries)
        {
            if (e.Tag == 0xA005) // InteropIFD pointer → relocated sub-block
            {
                var interop = src.SubIfds.FirstOrDefault(s => s.Offset == e.ValueOrOffset);
                if (interop != null && interop.Entries.Count > 0)
                    block.Entries.Add(new Entry { Tag = e.Tag, Type = TypeLong, Count = 1, SubBlock = ReEmitIfd(interop, meta) });
                continue;
            }
            CopyEntry(block, e, srcLE);
        }

        block.Entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        return block;
    }

    /// <summary>Copies one source entry, converting byte order when the source is big-endian.</summary>
    private static void CopyEntry(IfdBlock target, NefAssetExtractor.TiffEntry e, bool srcLE)
    {
        int typeSize = TiffConstants.SizeOfType(e.Type);
        long size = (long)typeSize * e.Count;

        if (size <= 4)
        {
            var raw = new byte[size];
            if (srcLE)
                for (int i = 0; i < size; i++) raw[i] = (byte)(e.ValueOrOffset >> (8 * i));
            else
                for (int i = 0; i < size; i++) raw[i] = (byte)(e.ValueOrOffset >> (8 * (3 - i)));
            target.Entries.Add(new Entry { Tag = e.Tag, Type = e.Type, Count = e.Count,
                Inline = Pad(ConvertByteOrder(raw, e.Type, srcLE)) });
        }
        else if (e.Data != null)
        {
            target.Entries.Add(new Entry { Tag = e.Tag, Type = e.Type, Count = e.Count,
                Blob = ConvertByteOrder(e.Data, e.Type, srcLE) });
        }
        // entries with out-of-range data offsets are skipped (malformed in source)
    }

    /// <summary>Swaps element byte order for multi-byte numeric types when source ≠ little-endian.</summary>
    private static byte[] ConvertByteOrder(byte[] data, ushort type, bool srcLE)
    {
        if (srcLE) return data;
        int es = TiffConstants.SizeOfType(type);
        if (es <= 1 || data.Length % es != 0) return data;
        var r = new byte[data.Length];
        for (int i = 0; i + es <= data.Length; i += es)
            for (int j = 0; j < es; j++)
                r[i + j] = data[i + es - 1 - j];
        return r;
    }

    /// <summary>Writes a blob at the next 4-byte boundary, explicitly zero-filling the
    /// ≤3-byte alignment gap so the emitted bytes are identical to the previous
    /// MemoryStream-based layout (which zero-filled seek gaps implicitly).</summary>
    private static void WriteBlob4Aligned(BinaryWriter bw, FileStream fs, byte[] blob)
    {
        long pos = fs.Position;
        int pad = (int)(((pos + 3) & ~3L) - pos);
        if (pad > 0) bw.Write(AlignmentZeros, 0, pad);
        bw.Write(blob);
    }

    private static byte[] Pad(byte[] b)
    {
        var p = new byte[4];
        Array.Copy(b, p, b.Length);
        return p;
    }

    private static uint Align(uint v, int alignment) => (uint)((v + alignment - 1) & ~(alignment - 1));
}
