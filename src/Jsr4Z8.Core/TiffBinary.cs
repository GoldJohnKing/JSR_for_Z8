namespace Jsr4Z8.Core;

/// <summary>Shared integer readers for byte-level TIFF/NEF/DNG walking:
/// little-endian by default (TIFF "II" byte order), plus a big-endian variant
/// for the BE-tagged blobs Nikon emits (opcode lists, NEFInfo).</summary>
internal static class TiffBinary
{
    public static ushort U16(byte[] d, uint o) => (ushort)(d[o] | (d[o + 1] << 8));
    public static ushort U16(byte[] d, int o) => (ushort)(d[o] | (d[o + 1] << 8));
    public static ushort U16(ReadOnlySpan<byte> b, int o) => (ushort)(b[o] | (b[o + 1] << 8));

    public static uint U32(byte[] d, uint o) =>
        (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
    public static uint U32(byte[] d, int o) =>
        (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
    public static uint U32(ReadOnlySpan<byte> b, int o) =>
        (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    public static uint U32BE(byte[] d, uint o) =>
        (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
    public static uint U32BE(byte[] d, int o) =>
        (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
}
