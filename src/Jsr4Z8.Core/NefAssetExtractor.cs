using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jsr4Z8.Core;

/// <summary>
/// Extracts TIFF IFD trees (EXIF, GPS, Makernote, custom tags) from original NEF files
/// for byte-level preservation into the output DNG.
/// Ported from Save_Z8 src/SaveZ8.Core/TiffIfdExtractor.cs (class renamed;
/// IFD parsing behavior identical). <see cref="Extract(string)"/> additionally
/// builds the <see cref="NefAssets"/> block surface consumed by the DNG tasks.
/// </summary>
public class NefAssetExtractor
{
    public class TiffIfd
    {
        public uint Offset { get; init; }
        public List<TiffEntry> Entries { get; } = new();
        public List<TiffIfd> SubIfds { get; } = new();
    }

    public class TiffEntry
    {
        public ushort Tag { get; init; }
        public ushort Type { get; init; }
        public uint Count { get; init; }
        public uint ValueOrOffset { get; init; }
        public byte[]? Data { get; set; } // if data is > 4 bytes, stored here
    }

    public class ExtractedMetadata
    {
        public bool IsLittleEndian { get; init; }
        public List<TiffIfd> AllIfds { get; } = new();

        /// <summary>Embedded JPEG previews found across all IFDs, smallest first.</summary>
        public List<PreviewInfo> Previews { get; } = new();

        /// <summary>NEFInfo blob (SubIFD tag 0xC7D5): Nikon Z lens correction data
        /// (self-contained "Nikon\0" TIFF: block 5 = distortion, block 6 = vignette).</summary>
        public byte[]? NefInfoData { get; internal set; }
    }

    public class PreviewInfo
    {
        public byte[] Jpeg { get; init; } = Array.Empty<byte>();
        public int Width { get; init; }
        public int Height { get; init; }
        public long Length => Jpeg.Length;
    }

    /// <summary>
    /// Extracts the IFD tree, embedded previews and NEFInfo from an NEF file and
    /// assembles the source blocks (<see cref="NefAssets"/>) for DNG re-embedding.
    /// </summary>
    public static NefAssets Extract(string nefPath)
    {
        var data = File.ReadAllBytes(nefPath);
        var meta = Parse(data);
        return BuildAssets(meta, data);
    }

    /// <summary>
    /// Extract all IFD structures from an NEF file for later re-embedding.
    /// </summary>
    public static ExtractedMetadata Parse(string nefPath)
    {
        var data = File.ReadAllBytes(nefPath);
        return Parse(data);
    }

    public static ExtractedMetadata Parse(byte[] data)
    {
        if (data.Length < 8)
            throw new InvalidDataException("File too small for TIFF");

        bool le = data[0] == 0x49 && data[1] == 0x49; // II = little endian
        if (!le && !(data[0] == 0x4D && data[1] == 0x4D))
            throw new InvalidDataException("Not a TIFF file");

        var result = new ExtractedMetadata { IsLittleEndian = le };

        // Walk IFD chain
        uint ifd0Offset = le ? ReadUInt32(data, 4) : ReadUInt32BE(data, 4);
        var visited = new HashSet<uint>();

        var ifd0 = ParseIfd(data, ifd0Offset, le, visited, result.AllIfds, depth: 0);

        // Extract embedded JPEG previews (IFD0 thumbnail + SubIFD full/medium previews)
        ExtractPreviews(result, data);

        // Extract Nikon Z lens correction blob (SubIFD tag 0xC7D5 in the raw SubIFD)
        foreach (var ifd in result.AllIfds)
        {
            var ne = ifd.Entries.Find(e => e.Tag == 0xC7D5);
            if (ne?.Data is { Length: > 16 })
            {
                result.NefInfoData = ne.Data;
                break;
            }
        }

        return result;
    }

    private static NefAssets BuildAssets(ExtractedMetadata meta, byte[] data)
    {
        var ifd0 = meta.AllIfds.FirstOrDefault() ?? new TiffIfd();
        var exifIfd = FindSubIfdByPointerTag(ifd0, 0x8769); // EXIF
        var gpsIfd = FindSubIfdByPointerTag(ifd0, 0x8825);  // GPS
        var interopIfd = exifIfd != null ? FindSubIfdByPointerTag(exifIfd, 0xA005) : null; // Interop

        // EXIF block: contiguous region IFD0 → end of EXIF IFD data (incl. MakerNote
        // and nested Interop IFD; excludes the SubIFD previews/raw strips).
        byte[] exifBlock = Array.Empty<byte>();
        if (exifIfd != null && exifIfd.Entries.Count > 0)
        {
            long end = Math.Max(IfdEntriesEnd(ifd0), IfdRegionEnd(exifIfd));
            exifBlock = Slice(data, ifd0.Offset, end);
        }

        return new NefAssets
        {
            ExifBlock = exifBlock,
            GpsBlock = gpsIfd != null && gpsIfd.Entries.Count > 0 ? Slice(data, gpsIfd.Offset, IfdRegionEnd(gpsIfd)) : Array.Empty<byte>(),
            InteropBlock = interopIfd != null && interopIfd.Entries.Count > 0 ? Slice(data, interopIfd.Offset, IfdRegionEnd(interopIfd)) : Array.Empty<byte>(),
            XmpBlock = ifd0.Entries.Find(e => e.Tag == 0x02BC)?.Data ?? Array.Empty<byte>(),
            Previews = meta.Previews.Take(3)
                .Select(p => new PreviewImage { W = p.Width, H = p.Height, Jpeg = p.Jpeg })
                .ToArray(),
            NefInfo = meta.NefInfoData ?? Array.Empty<byte>(),
        };
    }

    /// <summary>Finds the sub-IFD of <paramref name="parent"/> referenced by a pointer
    /// tag (0x8769 EXIF / 0x8825 GPS / 0xA005 Interop).</summary>
    private static TiffIfd? FindSubIfdByPointerTag(TiffIfd parent, ushort tag)
    {
        var ptr = parent.Entries.Find(e => e.Tag == tag);
        if (ptr == null) return null;
        return parent.SubIfds.FirstOrDefault(s => s.Offset == ptr.ValueOrOffset);
    }

    /// <summary>End offset of one IFD's own structure and out-of-line entry data
    /// (does not descend into SubIFDs).</summary>
    private static long IfdEntriesEnd(TiffIfd ifd)
    {
        long end = ifd.Offset + 2 + (long)ifd.Entries.Count * 12 + 4;
        foreach (var e in ifd.Entries)
            if (e.Data is { Length: > 0 })
                end = Math.Max(end, (long)e.ValueOrOffset + e.Data.Length);
        return end;
    }

    /// <summary>End offset of an IFD region including nested SubIFDs (EXIF → Interop).</summary>
    private static long IfdRegionEnd(TiffIfd ifd)
    {
        long end = IfdEntriesEnd(ifd);
        foreach (var sub in ifd.SubIfds)
            end = Math.Max(end, IfdRegionEnd(sub));
        return end;
    }

    private static byte[] Slice(byte[] data, uint start, long end)
    {
        long len = Math.Min(end, data.Length) - start;
        if (len <= 0) return Array.Empty<byte>();
        var slice = new byte[len];
        Array.Copy(data, start, slice, 0, (int)len);
        return slice;
    }

    private static TiffIfd ParseIfd(byte[] data, uint offset, bool le,
        HashSet<uint> visited, List<TiffIfd> allIfds, int depth)
    {
        if (visited.Contains(offset) || offset + 2 > data.Length || depth > 5)
            return new TiffIfd { Offset = offset };

        visited.Add(offset);
        var ifd = new TiffIfd { Offset = offset };
        allIfds.Add(ifd);

        int entryCount = (int)ReadUInt16(data, offset);
        if (entryCount <= 0 || entryCount > 1024) return ifd;

        for (int i = 0; i < entryCount; i++)
        {
            uint entryOffset = offset + 2 + (uint)(i * 12);
            if (entryOffset + 12 > data.Length) break;

            ushort tag = ReadUInt16(data, entryOffset);
            ushort type = ReadUInt16(data, entryOffset + 2);
            uint count = le ? ReadUInt32(data, entryOffset + 4) : ReadUInt32BE(data, entryOffset + 4);
            uint valueOrOffset = le ? ReadUInt32(data, entryOffset + 8) : ReadUInt32BE(data, entryOffset + 8);

            var entry = new TiffEntry { Tag = tag, Type = type, Count = count, ValueOrOffset = valueOrOffset };

            // Calculate data size
            int typeSize = TiffConstants.SizeOfType(type);
            long dataSize = (long)typeSize * count;

            if (dataSize > 4 && valueOrOffset + dataSize <= data.Length)
            {
                entry.Data = new byte[dataSize];
                Array.Copy(data, valueOrOffset, entry.Data, 0, (int)dataSize);
            }

            ifd.Entries.Add(entry);

            // Follow SubIFD pointers
            if (tag == 0x014A && count > 1)
            {
                // SubIFDs array: valueOrOffset points to count uint32 IFD offsets
                long arrayEnd = (long)valueOrOffset + count * 4;
                if (valueOrOffset > 0 && arrayEnd <= data.Length)
                {
                    for (uint k = 0; k < count; k++)
                    {
                        uint subOff = le ? ReadUInt32(data, valueOrOffset + k * 4)
                                         : ReadUInt32BE(data, valueOrOffset + k * 4);
                        if (subOff > 0 && subOff < data.Length)
                            ifd.SubIfds.Add(ParseIfd(data, subOff, le, visited, allIfds, depth + 1));
                    }
                }
            }
            else if (tag == 0x8769 || tag == 0x8825 || tag == 0xA005 || tag == 0x014A) // EXIF, GPS, Interop, single SubIFD
            {
                if (valueOrOffset > 0 && valueOrOffset < data.Length)
                {
                    var sub = ParseIfd(data, valueOrOffset, le, visited, allIfds, depth + 1);
                    ifd.SubIfds.Add(sub);
                }
            }
        }

        return ifd;
    }

    private static void ExtractPreviews(ExtractedMetadata meta, byte[] data)
    {
        foreach (var ifd in meta.AllIfds)
        {
            var e513 = ifd.Entries.Find(e => e.Tag == 513);  // JPEGInterchangeFormat
            var e514 = ifd.Entries.Find(e => e.Tag == 514);  // JPEGInterchangeFormatLength
            if (e513 == null || e514 == null) continue;

            // Both are inline LONG values in a well-formed preview IFD
            long off = e513.ValueOrOffset, len = e514.ValueOrOffset;
            if (off <= 0 || len < 4 || off + len > data.Length) continue;

            var jpeg = new byte[len];
            Array.Copy(data, off, jpeg, 0, (int)len);
            if (jpeg[0] != 0xFF || jpeg[1] != 0xD8 || jpeg[len - 2] != 0xFF || jpeg[len - 1] != 0xD9)
                continue; // not a complete SOI..EOI JPEG

            var (w, h) = ParseJpegSof(jpeg);
            if (w <= 0 || h <= 0) continue;
            meta.Previews.Add(new PreviewInfo { Jpeg = jpeg, Width = w, Height = h });
        }
        meta.Previews.Sort((a, b) => a.Length.CompareTo(b.Length));
    }

    /// <summary>Parses SOF0/SOF1/SOF2 for pixel dimensions from a baseline JPEG stream.</summary>
    private static (int Width, int Height) ParseJpegSof(byte[] jpeg)
    {
        int i = 2; // skip SOI
        while (i + 9 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) { i++; continue; }
            byte marker = jpeg[i + 1];
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
            if (marker is 0xC0 or 0xC1 or 0xC2) // baseline/extended/progressive SOF
                return (jpeg[i + 7] << 8 | jpeg[i + 8], jpeg[i + 5] << 8 | jpeg[i + 6]);
            if (marker is 0xDA or 0xD9) break; // SOS/EOI before SOF — no dims
            int segLen = jpeg[i + 2] << 8 | jpeg[i + 3];
            if (segLen < 2) break;
            i += 2 + segLen;
        }
        return (0, 0);
    }

    // Utility
    private static ushort ReadUInt16(byte[] d, uint off) =>
        (ushort)(d[off] | (d[off + 1] << 8));

    private static uint ReadUInt32(byte[] d, uint off) =>
        (uint)(d[off] | (d[off + 1] << 8) | (d[off + 2] << 16) | (d[off + 3] << 24));

    private static uint ReadUInt32BE(byte[] d, uint off) =>
        (uint)(d[off] << 24 | d[off + 1] << 16 | d[off + 2] << 8 | d[off + 3]);
}

/// <summary>
/// Source assets extracted from an NEF for byte-level re-embedding into an
/// output DNG. Blocks are raw source-file regions (regions may overlap where
/// Nikon packs the metadata area contiguously); empty when the NEF lacks them.
/// </summary>
public sealed record NefAssets
{
    /// <summary>Source region from IFD0 through the end of the EXIF IFD data:
    /// Make/Model strings, all EXIF tags including the MakerNote blob.</summary>
    public required byte[] ExifBlock { get; init; }

    /// <summary>GPS IFD source region (structure + data).</summary>
    public required byte[] GpsBlock { get; init; }

    /// <summary>Interop IFD source region (nested under EXIF via 0xA005).</summary>
    public required byte[] InteropBlock { get; init; }

    /// <summary>XMP packet bytes (IFD0 tag 0x02BC).</summary>
    public required byte[] XmpBlock { get; init; }

    /// <summary>Embedded JPEG previews, smallest first (at most 3).</summary>
    public required PreviewImage[] Previews { get; init; }

    /// <summary>NEFInfo blob (SubIFD tag 0xC7D5), Nikon Z lens correction data.</summary>
    public required byte[] NefInfo { get; init; }
}

/// <summary>One embedded JPEG preview with its decoded SOF pixel dimensions.</summary>
public sealed record PreviewImage
{
    public required int W { get; init; }
    public required int H { get; init; }
    public required byte[] Jpeg { get; init; }
}
