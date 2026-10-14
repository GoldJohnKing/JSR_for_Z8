using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jsr4Z8.Core.Dng;

/// <summary>
/// Low-level DNG composition: one <see cref="RawPayload"/> (replaceable pixel
/// source) + the frame's <see cref="RawMetadata"/> + the NEF's preserved
/// <see cref="NefAssets"/> → a complete DNG file (Save_Z8 DngWriter topology).
///
/// Output-domain policy (Save_Z8/ADC parity, AGENTS: 未压缩 DNG 存 x&lt;&lt;2
/// (BL=4032/WL=63568)；压缩 DNG 存 14-bit 原值 (BL=1008/WL=15892) — 两套域等价，
/// 勿混用): CFA payloads default to the compressed native 14-bit domain — NOT
/// <see cref="RawMetadata.WhiteLevel"/>, which is LibRaw's declared 14-bit
/// maximum (16383). LinearRaw RGB payloads default to BL=0/WL=65535 (Task 9
/// backend product). Both levels are overridable per call.
/// </summary>
public static class DngComposer
{
    /// <summary>Z8 14-bit native domain (compressed output) — ADC parity constants.</summary>
    private const int CfaBlackLevel14 = 1008;
    private const int CfaWhiteLevel14 = 15892;

    /// <summary>×4 16-bit domain (uncompressed baseline): 1008×4 / 15892×4.</summary>
    private const int CfaBlackLevel16 = 4032;
    private const int CfaWhiteLevel16 = 63568;
    private const int CfaPixelBitShift = 2;

    /// <summary>NEF IFD0 sits directly behind the 8-byte TIFF header (Nikon II layout).</summary>
    private const uint NefIfd0Base = 8;

    /// <summary>Composes the complete DNG (raw SubIFD + previews + EXIF/GPS + opcodes).</summary>
    /// <param name="payload">Pixel source: CFA mosaic (Samples=1) or LinearRaw RGB (Samples=3).</param>
    /// <param name="meta">Frame metadata (identity, WB → AsShotNeutral).</param>
    /// <param name="assets">Preserved NEF blocks (EXIF/GPS regions, previews, NEFInfo).</param>
    /// <param name="destDngPath">Output file path.</param>
    /// <param name="blackLevel">Overrides the domain default BlackLevel.</param>
    /// <param name="whiteLevel">Overrides the domain default WhiteLevel.</param>
    /// <param name="compress">true = lossless-JPEG tiles (Compression=7); false = uncompressed.</param>
    public static void Compose(RawPayload payload, RawMetadata meta, NefAssets assets, string destDngPath,
        int? blackLevel = null, int? whiteLevel = null, bool compress = true)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(assets);
        if (payload.Data.Length != (long)payload.W * payload.H * payload.Samples)
            throw new ArgumentException(
                $"payload.Data.Length {payload.Data.Length} != W·H·Samples {payload.W}×{payload.H}×{payload.Samples}");

        int bl, wl, shift;
        if (payload.IsCfa)
        {
            bl = blackLevel ?? (compress ? CfaBlackLevel14 : CfaBlackLevel16);
            wl = whiteLevel ?? (compress ? CfaWhiteLevel14 : CfaWhiteLevel16);
            shift = compress ? 0 : CfaPixelBitShift;
        }
        else
        {
            // LinearRaw payloads are already in their final 16-bit domain.
            bl = blackLevel ?? 0;
            wl = whiteLevel ?? 65535;
            shift = 0;
        }

        var options = new DngWriteOptions
        {
            Compress = compress,
            SamplesPerPixel = payload.Samples,
            PhotometricInterpretation = (ushort)(payload.IsCfa ? 32803 : 34892),
            BlackLevel = bl,
            WhiteLevel = wl,
            PixelBitShift = shift,
            CfaPattern = payload.IsCfa ? MapCfaPattern(payload.CfaPattern) : null,
        };

        // AsShotNeutral = 1/cam_mul per component (Save_Z8/ADC parity).
        float[]? cameraWb = meta.CamMul is { Length: >= 3 }
            ? Array.ConvertAll(meta.CamMul, v => (float)v)
            : null;

        var sourceMetadata = ParseAssets(assets);
        DngWriter.Write(destDngPath, payload.Data, payload.W, payload.H, options, sourceMetadata, cameraWb);
    }

    /// <summary>
    /// Full-control composition path: <paramref name="options"/> is the complete
    /// output profile — domain (BL/WL), photometric/samples, compression,
    /// IFD0 attribution (Software/OmitIfd0Artist), AsShotNeutral/ColorMatrix1
    /// and geometry overrides. Task 9's LinearRaw RGB fusion entry: the backend
    /// product's payload + the reference NEF's metadata blocks.
    /// Diagnostic seam (post-release fix 5): <see cref="DngWriteOptions.RawStreams"/>
    /// set ⇒ the payload's pixel array is bypassed (may be empty) and the
    /// pre-encoded strip streams are emitted verbatim.
    /// </summary>
    public static void Compose(RawPayload payload, RawMetadata meta, NefAssets assets, string destDngPath,
        DngWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(options);
        if (options.SamplesPerPixel < 1)
            throw new ArgumentException("SamplesPerPixel must be ≥ 1", nameof(options));
        if ((options.CfaPattern != null) != payload.IsCfa)
            throw new ArgumentException(
                $"payload.IsCfa={payload.IsCfa} 与 CfaPattern {(options.CfaPattern == null ? "缺失" : "多出")}不一致");
        if (options.RawStreams is not { Count: > 0 } &&
            payload.Data.Length != (long)payload.W * payload.H * options.SamplesPerPixel)
            throw new ArgumentException(
                $"payload.Data.Length {payload.Data.Length} != W·H·Samples {payload.W}×{payload.H}×{options.SamplesPerPixel}");

        // AsShotNeutral = 1/cam_mul per component (fallback when options leaves
        // it unset; Save_Z8/ADC parity).
        float[]? cameraWb = meta.CamMul is { Length: >= 3 }
            ? Array.ConvertAll(meta.CamMul, v => (float)v)
            : null;

        DngWriter.Write(destDngPath, payload.Data, payload.W, payload.H, options,
            ParseAssets(assets), cameraWb);
    }

    /// <summary>2×2 phase string ("RGGB", chars from {R,G,B}) → DNG CFAPattern colors (0=R,1=G,2=B).</summary>
    private static byte[] MapCfaPattern(string? phase)
    {
        if (phase is not { Length: 4 })
            throw new ArgumentException($"CFA payload requires a 4-char CfaPattern (e.g. \"RGGB\"), got \"{phase}\"");
        var colors = new byte[4];
        for (int i = 0; i < 4; i++)
            colors[i] = phase[i] switch
            {
                'R' => 0,
                'G' => 1,
                'B' => 2,
                _ => throw new ArgumentException($"CfaPattern char '{phase[i]}' not in {{R,G,B}}"),
            };
        return colors;
    }

    /// <summary>
    /// Rebuilds the parsed IFD surface the writer consumes (Save_Z8's
    /// TiffIfdExtractor model) from the Task 3 asset blocks. The blocks are
    /// verbatim mid-file source regions whose entry values are absolute file
    /// offsets, so each region is parsed against its original file base: the
    /// ExifBlock region (IFD0 → end of EXIF data, MakerNote and Interop
    /// included, XMP overlapping) against base 8; the separate GpsBlock region
    /// against the IFD0 0x8825 pointer value. Previews and NEFInfo are carried
    /// as-is. The regions are NOT standalone TIFFs (no header) and are never
    /// blob-copied: every entry is re-emitted with relocated data.
    /// </summary>
    private static NefAssetExtractor.ExtractedMetadata ParseAssets(NefAssets assets)
    {
        var meta = new NefAssetExtractor.ExtractedMetadata { IsLittleEndian = true };

        if (assets.ExifBlock is { Length: > 8 })
        {
            var ifd0 = RegionIfd.Parse(assets.ExifBlock, 0, NefIfd0Base, meta.AllIfds);
            if (ifd0.Entries.Count == 0)
                throw new InvalidDataException("NEF ExifBlock 区域解析失败（IFD0 无条目）");

            // GPS usually lives outside the ExifBlock region, in its own block —
            // reattach it parsed against the pointer value as its file base.
            var gpsPtr = ifd0.Entries.Find(e => e.Tag == 0x8825);
            if (gpsPtr != null && FindSub(ifd0, gpsPtr.ValueOrOffset) == null &&
                assets.GpsBlock is { Length: > 2 })
            {
                var gps = RegionIfd.Parse(assets.GpsBlock, 0, gpsPtr.ValueOrOffset, meta.AllIfds);
                if (gps.Entries.Count > 0)
                    ifd0.SubIfds.Add(gps);
            }
        }

        foreach (var p in assets.Previews)
            meta.Previews.Add(new NefAssetExtractor.PreviewInfo { Jpeg = p.Jpeg, Width = p.W, Height = p.H });
        if (assets.NefInfo is { Length: > 0 })
            meta.NefInfoData = assets.NefInfo;

        return meta;
    }

    private static NefAssetExtractor.TiffIfd? FindSub(NefAssetExtractor.TiffIfd parent, uint offset) =>
        parent.SubIfds.FirstOrDefault(s => s.Offset == offset);

    /// <summary>
    /// Parses one little-endian IFD out of a verbatim source region. Entry data
    /// larger than 4 bytes is sliced at (value − baseAbs) when it lies inside the
    /// region; entries whose data falls outside keep no Data (the writer's
    /// CopyEntry skips them, mirroring Save_Z8). Pointer tags 0x8769/0x8825/0xA005
    /// are followed when their target lies inside the same region.
    /// </summary>
    private static class RegionIfd
    {
        public static NefAssetExtractor.TiffIfd Parse(byte[] region, uint relOffset, uint baseAbs,
            List<NefAssetExtractor.TiffIfd> allIfds, int depth = 0)
        {
            var ifd = new NefAssetExtractor.TiffIfd { Offset = baseAbs + relOffset };
            if (depth > 5 || relOffset + 2 > region.Length)
            {
                allIfds.Add(ifd);
                return ifd;
            }
            allIfds.Add(ifd);

            int count = region[relOffset] | (region[relOffset + 1] << 8);
            if (count <= 0 || count > 1024) return ifd;

            for (int i = 0; i < count; i++)
            {
                uint p = relOffset + 2 + (uint)(i * 12);
                if (p + 12 > region.Length) break;

                ushort tag = U16(region, p);
                ushort type = U16(region, p + 2);
                uint cnt = U32(region, p + 4);
                uint val = U32(region, p + 8);

                var e = new NefAssetExtractor.TiffEntry { Tag = tag, Type = type, Count = cnt, ValueOrOffset = val };
                long size = (long)TiffConstants.SizeOfType(type) * cnt;
                if (size > 4)
                {
                    long dataOff = (long)val - baseAbs;
                    if (dataOff >= 0 && dataOff + size <= region.Length)
                    {
                        e.Data = new byte[size];
                        Array.Copy(region, dataOff, e.Data, 0, (int)size);
                    }
                }
                ifd.Entries.Add(e);

                if (tag == 0x8769 || tag == 0x8825 || tag == 0xA005)
                {
                    long rel = (long)val - baseAbs;
                    if (rel > 0 && rel + 2 <= region.Length)
                    {
                        var sub = Parse(region, (uint)rel, baseAbs, allIfds, depth + 1);
                        if (sub.Entries.Count > 0)
                            ifd.SubIfds.Add(sub);
                    }
                    // targets outside this region (GPS in its own block) are
                    // reattached by the caller, which knows the block's base
                }
            }
            return ifd;
        }

        private static ushort U16(byte[] d, uint o) => (ushort)(d[o] | (d[o + 1] << 8));

        private static uint U32(byte[] d, uint o) =>
            (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
    }
}
