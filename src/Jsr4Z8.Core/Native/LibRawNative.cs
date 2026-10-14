using System;
using System.Runtime.InteropServices;

namespace Jsr4Z8.Core.Native;

/// <summary>
/// P/Invoke wrapper for libraw_wrapper.dll (native LibRaw C wrapper).
/// Ported from Save_Z8 src/SaveZ8.Core/Native/LibRawNative.cs (extern C
/// signatures identical to Save_Z8's libraw_wrapper.dll).
/// </summary>
public static class LibRawNative
{
    private const string DllName = "libraw_wrapper";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern IntPtr lr_open(string filepath, out int error);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_width(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_height(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr lr_get_raw_image(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_black_level(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_white_level(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_cfa_pattern(IntPtr handle, byte[] outPattern);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_cam_mul(IntPtr handle, float[] out4);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void lr_close(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr lr_version();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr lr_get_make(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr lr_get_model(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern float lr_get_iso(IntPtr handle);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_bits_per_sample(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_compression(IntPtr handle);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lr_get_nef_compression(IntPtr handle);

    /// <summary>Native LibRaw version string (e.g. "0.21.3-Release"), reported by the
    /// --selftest release QA path and the E2E evidence log.</summary>
    public static string GetVersion()
        => Marshal.PtrToStringAnsi(lr_version()) ?? "LibRaw (版本未知)";

    /// <summary>Opens and decodes a raw file, returning a native handle (IntPtr.Zero on error, see <paramref name="error"/>).</summary>
    public static IntPtr OpenRaw(string filepath, out int error)
        => lr_open(filepath, out error);

    /// <summary>Camera make (e.g. "NIKON CORPORATION"); valid while the handle is open.</summary>
    public static string GetMake(IntPtr handle)
        => Marshal.PtrToStringAnsi(lr_get_make(handle)) ?? string.Empty;

    /// <summary>Camera model (e.g. "NIKON Z 8"); valid while the handle is open.</summary>
    public static string GetModel(IntPtr handle)
        => Marshal.PtrToStringAnsi(lr_get_model(handle)) ?? string.Empty;

    /// <summary>As-shot ISO speed (LibRaw imgdata.other.iso_speed).</summary>
    public static float GetIso(IntPtr handle)
        => lr_get_iso(handle);

    /// <summary>Padded raw sensor width (LibRaw imgdata.sizes.raw_width).</summary>
    public static int GetWidth(IntPtr handle)
        => lr_get_width(handle);

    /// <summary>Padded raw sensor height (LibRaw imgdata.sizes.raw_height).</summary>
    public static int GetHeight(IntPtr handle)
        => lr_get_height(handle);

    /// <summary>Black level (LibRaw imgdata.color.black).</summary>
    public static int GetBlackLevel(IntPtr handle)
        => lr_get_black_level(handle);

    /// <summary>White level (LibRaw imgdata.color.maximum).</summary>
    public static int GetWhiteLevel(IntPtr handle)
        => lr_get_white_level(handle);

    /// <summary>TIFF BitsPerSample of the raw strip (14 for Z8 NEF; 12 in high-speed readout).</summary>
    public static int GetBitsPerSample(IntPtr handle)
        => lr_get_bits_per_sample(handle);

    /// <summary>TIFF Compression code of the raw strip (34713 = Nikon lossless NEF; other codes = HE/HE*/uncompressed variants).</summary>
    public static int GetCompression(IntPtr handle)
        => lr_get_compression(handle);

    /// <summary>Nikon MakerNote NEFCompression: 3 = lossless compressed, 13 = High
    /// Efficiency, 14 = High Efficiency*. HE/HE* carry the SAME TIFF Compression code
    /// (34713) as lossless — this field is the only reliable discriminator.</summary>
    public static int GetNefCompression(IntPtr handle)
        => lr_get_nef_compression(handle);

    /// <summary>2×2 CFA filter codes in row-major order (0,1,3,2 = RGGB).</summary>
    public static byte[] GetCfaPattern(IntPtr handle)
    {
        var pattern = new byte[4];
        lr_get_cfa_pattern(handle, pattern);
        return pattern;
    }

    /// <summary>As-shot white balance multipliers (R,G1,B,G2) from LibRaw cam_mul.</summary>
    public static float[] GetCamMul(IntPtr handle)
    {
        var camMul = new float[4];
        lr_get_cam_mul(handle, camMul);
        return camMul;
    }

    /// <summary>Closes a handle returned by <see cref="OpenRaw"/>.</summary>
    public static void Close(IntPtr handle)
        => lr_close(handle);

    /// <summary>
    /// Decode a NEF file into raw mosaic pixels + metadata.
    /// Returns (pixels, width, height, blackLevel, whiteLevel, cfaPattern, cameraWb).
    /// cameraWb = as-shot white balance multipliers (R,G1,B,G2) from LibRaw cam_mul.
    /// </summary>
    public static (ushort[] pixels, int width, int height, int blackLevel, int whiteLevel, byte[] cfaPattern, float[] cameraWb)
        Decode(string filepath)
    {
        int error;
        IntPtr handle = lr_open(filepath, out error);
        if (handle == IntPtr.Zero)
        {
            string msg = error switch
            {
                -1 => "内存分配失败",
                -2 => "raw_image 为空（不支持的格式）",
                _ => GetErrorMessage(error)
            };
            throw new InvalidOperationException($"LibRaw 打开/解码失败: {msg} (code={error}, file={filepath})");
        }

        try
        {
            int width = lr_get_width(handle);
            int height = lr_get_height(handle);
            int black = lr_get_black_level(handle);
            int white = lr_get_white_level(handle);

            if (width <= 0 || height <= 0)
                throw new InvalidOperationException($"无效尺寸: {width}×{height}");

            var cfa = new byte[4];
            lr_get_cfa_pattern(handle, cfa);

            var camMul = new float[4];
            lr_get_cam_mul(handle, camMul);

            IntPtr rawPtr = lr_get_raw_image(handle);
            if (rawPtr == IntPtr.Zero)
                throw new InvalidOperationException("raw_image 指针为空");

            long count = (long)width * height;
            var pixels = new ushort[count];

            // Copy native u16 data to managed array
            unsafe
            {
                fixed (ushort* dst = pixels)
                {
                    Buffer.MemoryCopy((void*)rawPtr, dst, count * 2, count * 2);
                }
            }

            return (pixels, width, height, black, white, cfa, camMul);
        }
        finally
        {
            lr_close(handle);
        }
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr lr_strerror(int code);

    /// <summary>Surfaces LibRaw's own (verbatim) error text for a return code.</summary>
    public static string GetErrorMessage(int code)
    {
        string? native = Marshal.PtrToStringAnsi(lr_strerror(code));
        return string.IsNullOrEmpty(native) ? $"LibRaw错误({code})" : $"LibRaw {native} ({code})";
    }
}
