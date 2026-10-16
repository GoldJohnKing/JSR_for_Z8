namespace Jsr4Z8.Core.Dng;

/// <summary>
/// Low-level DNG composition: one <see cref="RawPayload"/> (replaceable pixel
/// source) + the frame's <see cref="RawMetadata"/> + the NEF's preserved
/// <see cref="NefAssets"/> → a complete DNG file (Save_Z8 DngWriter topology).
/// The output domain is set per call via <see cref="DngWriteOptions"/>: CFA
/// products store either the native 14-bit domain (compressed: BL=1008/WL=15892)
/// or the ×4 16-bit domain (uncompressed: BL=4032/WL=63568, samples &lt;&lt;2 — two
/// equivalent domains, never mixed); LinearRaw RGB products store BL=0/WL=65535.
/// </summary>
public static class DngComposer
{
    /// <summary>NEF IFD0 sits directly behind the 8-byte TIFF header (Nikon II layout).</summary>
    private const uint NefIfd0Base = 8;

    /// <summary>
    /// Full-control composition path: <paramref name="options"/> is the complete
    /// output profile — domain (BL/WL), photometric/samples, compression,
    /// IFD0 attribution (Software/OmitIfd0Artist), AsShotNeutral/ColorMatrix1
    /// and geometry overrides. The LinearRaw RGB fusion entry: the backend
    /// product's payload + the reference NEF's metadata blocks.
    /// Diagnostic seam: <see cref="DngWriteOptions.RawStreams"/>
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

    /// <summary>
    /// Rebuilds the parsed IFD surface the writer consumes (Save_Z8's
    /// TiffIfdExtractor model) from the NEF asset blocks. The blocks are
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

                ushort tag = TiffBinary.U16(region, p);
                ushort type = TiffBinary.U16(region, p + 2);
                uint cnt = TiffBinary.U32(region, p + 4);
                uint val = TiffBinary.U32(region, p + 8);

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
    }
}
