using System.Runtime.InteropServices;

namespace Jsr4Z8.Core.Native;

/// <summary>P/Invoke into dng_ljpeg_wrapper.dll (libjpeg-turbo lossless JPEG tile encoder).
/// Ported from Save_Z8 src/SaveZ8.Core/Native/DngLjpegNative.cs (extern C signatures identical).</summary>
internal static class DngLjpegNative
{
    private const string DllName = "dng_ljpeg";

    /// <summary>
    /// Encodes one tile as an independent lossless JPEG stream (SOF3, PSV=1,
    /// Pt=0, 16-bit, interleaved channels). Input rows hold cols*channels u16
    /// samples; edge tiles must be pre-padded to full tile geometry.
    /// Returns the encoded byte count, negative on error.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static unsafe extern long lje_encode_tile(ushort* src, uint rows, uint cols,
        uint channels, uint bitDepth, byte* outBuf, nuint cap);
}
