using System;
using System.IO;
using Jsr4Z8.Core.Native;

namespace Jsr4Z8.Core;

/// <summary>
/// Reads <see cref="RawMetadata"/> from an NEF via the Task 2 LibRaw handle
/// wrapper (no pixel copy), enriching it with EXIF capture info parsed from
/// the NEF's own TIFF structure. Missing, non-raw and corrupted files all
/// raise <see cref="RawReadException"/>.
/// </summary>
public static class RawMetadataReader
{
    public static RawMetadata Read(string nefPath)
    {
        if (!File.Exists(nefPath))
            throw new RawReadException($"NEF 文件不存在: {nefPath}");

        IntPtr handle = LibRawNative.OpenRaw(nefPath, out int error);
        if (handle == IntPtr.Zero)
            throw new RawReadException(
                $"LibRaw 打开/解码失败: {LibRawNative.GetErrorMessage(error)} (code={error}, file={nefPath})",
                errorCode: error);

        string make, model;
        double iso;
        int width, height, blackLevel, whiteLevel;
        double[] camMul;
        byte[] cfaPattern;
        try
        {
            make = LibRawNative.GetMake(handle);
            model = LibRawNative.GetModel(handle);
            iso = LibRawNative.GetIso(handle);
            width = LibRawNative.GetWidth(handle);
            height = LibRawNative.GetHeight(handle);
            blackLevel = LibRawNative.GetBlackLevel(handle);
            whiteLevel = LibRawNative.GetWhiteLevel(handle);
            camMul = Array.ConvertAll(LibRawNative.GetCamMul(handle), v => (double)v);
            cfaPattern = LibRawNative.GetCfaPattern(handle);

            if (width <= 0 || height <= 0)
                throw new RawReadException($"无效尺寸: {width}×{height} (file={nefPath})");
        }
        finally
        {
            LibRawNative.Close(handle);
        }

        double exposureTime, aperture;
        string dateTimeOriginal;
        try
        {
            (exposureTime, aperture, dateTimeOriginal) = ReadExifCaptureInfo(nefPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new RawReadException($"EXIF 解析失败: {ex.Message} (file={nefPath})", inner: ex);
        }

        return new RawMetadata
        {
            Make = make,
            Model = model,
            Iso = iso,
            BlackLevel = blackLevel,
            WhiteLevel = whiteLevel,
            Width = width,
            Height = height,
            CamMul = camMul,
            CfaPattern = cfaPattern,
            ExposureTime = exposureTime,
            Aperture = aperture,
            DateTimeOriginal = dateTimeOriginal,
        };
    }

    /// <summary>Reads ExposureTime (0x829A), FNumber (0x829D) and DateTimeOriginal
    /// (0x9003) from the NEF's EXIF IFD. Absent tags yield 0/0/"".</summary>
    private static (double exposureTime, double aperture, string dateTimeOriginal) ReadExifCaptureInfo(string nefPath)
    {
        var meta = NefAssetExtractor.Parse(nefPath);
        var ifd0 = meta.AllIfds.Count > 0 ? meta.AllIfds[0] : null;
        if (ifd0 == null)
            return (0, 0, "");

        var exifPtr = ifd0.Entries.Find(e => e.Tag == 0x8769);
        var exifIfd = exifPtr == null
            ? null
            : ifd0.SubIfds.Find(s => s.Offset == exifPtr.ValueOrOffset);
        if (exifIfd == null)
            return (0, 0, "");

        bool le = meta.IsLittleEndian;
        double exposure = ReadRational(exifIfd.Entries.Find(e => e.Tag == 0x829A), le);
        double aperture = ReadRational(exifIfd.Entries.Find(e => e.Tag == 0x829D), le);
        var dt = exifIfd.Entries.Find(e => e.Tag == 0x9003)?.Data;
        string dateTime = dt == null
            ? ""
            : System.Text.Encoding.ASCII.GetString(dt).TrimEnd('\0');
        return (exposure, aperture, dateTime);
    }

    private static double ReadRational(NefAssetExtractor.TiffEntry? entry, bool le)
    {
        if (entry?.Data is not { Length: >= 8 })
            return 0;
        uint num = le ? ReadUInt32(entry.Data, 0) : ReadUInt32BE(entry.Data, 0);
        uint den = le ? ReadUInt32(entry.Data, 4) : ReadUInt32BE(entry.Data, 4);
        return den == 0 ? 0 : (double)num / den;
    }

    private static uint ReadUInt32(byte[] d, int off) =>
        (uint)(d[off] | (d[off + 1] << 8) | (d[off + 2] << 16) | (d[off + 3] << 24));

    private static uint ReadUInt32BE(byte[] d, int off) =>
        (uint)(d[off] << 24 | d[off + 1] << 16 | d[off + 2] << 8 | d[off + 3]);
}
