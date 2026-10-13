using System.Runtime.InteropServices;

namespace Jsr4Z8.Core.Native;

/// <summary>P/Invoke into dng_ljpeg_wrapper.dll (libjpeg-turbo lossless JPEG tile encoder).
/// Ported from Save_Z8 src/SaveZ8.Core/Native/DngLjpegNative.cs (extern C signatures identical).</summary>
internal static class DngLjpegNative
{
    private const string DllName = "dng_ljpeg";

    /// <summary>
    /// Encodes one tile as an independent lossless JPEG stream (SOF3, PSV via
    /// <paramref name="psv"/>, Pt=0, 16-bit, interleaved channels). Input rows
    /// hold cols*channels u16 samples; edge tiles must be pre-padded to full
    /// tile geometry. Returns the encoded byte count, negative on error.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static unsafe extern long lje_encode_tile(ushort* src, uint rows, uint cols,
        uint channels, uint bitDepth, byte* outBuf, nuint cap, uint psv);

    /// <summary>
    /// Encoder self-test (dng_ljpeg.dll export): encodes a synthetic tile with
    /// the same settings as lje_encode_tile and decodes it back with
    /// libjpeg-turbo's own lossless decoder; returns the mismatching sample
    /// count (0 = pass, -1 = setup failure). Discriminates encoder-usage bugs
    /// from container/decoder issues.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long lje_selftest();

    /// <summary>
    /// Decodes one complete lossless-JPEG stream (SOF3, any predictor 1-7,
    /// 1-4 components, 16-bit) into <paramref name="dst"/>. Geometry comes
    /// from the stream's SOF header; rows are width*components interleaved
    /// u16 samples. <paramref name="outW"/>/<paramref name="outH"/>/
    /// <paramref name="outNc"/> (may be null) receive the stream geometry.
    /// Returns the decoded sample count, negative on error. Used by
    /// BackendDngReader to read backend DNG strips (tifffile LJ).
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static unsafe extern long ljd_decode_stream(byte* src, nuint cap,
        ushort* dst, nuint dstCap, uint* outW, uint* outH, uint* outNc);

    /// <summary>Safe wrapper for <see cref="ljd_decode_stream"/>: decodes a
    /// complete LJ stream into <paramref name="dst"/> at the given sample
    /// offset, reporting the stream's geometry. Returns the decoded sample
    /// count, negative on error.</summary>
    internal static long DecodeStream(byte[] src, ushort[] dst, long dstOffset,
        out uint width, out uint height, out uint components)
    {
        unsafe
        {
            uint w = 0, h = 0, c = 0;
            long decoded;
            fixed (byte* sp = src)
            fixed (ushort* dp = dst)
            {
                decoded = ljd_decode_stream(sp, (nuint)src.Length, dp + dstOffset,
                    (nuint)(dst.LongLength - dstOffset), &w, &h, &c);
            }
            width = w; height = h; components = c;
            return decoded;
        }
    }
}
