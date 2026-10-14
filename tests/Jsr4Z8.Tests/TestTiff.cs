using System.Text;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>Minimal little-endian TIFF IFD walker shared by DngWriterTests /
/// E2eTests / FinalizerTests (Save_Z8 TestTiff read-back pattern).</summary>
internal static class TestTiff
{
    internal sealed class Ifd
    {
        public Dictionary<ushort, (ushort Type, uint Count, byte[] Value)> Tags = new();
    }

    /// <summary>One IFD entry as stored on disk: the 12-byte fixed part plus the
    /// resolved value bytes. <see cref="ValueField"/> is the raw second word of
    /// the entry — the value itself when inline, the byte offset of the
    /// out-of-line blob otherwise (<see cref="IsOutOfLine"/>).
    /// <see cref="EntryOffset"/> is the absolute file offset of the 12-byte
    /// entry record (in-place patching support).</summary>
    internal sealed record IfdEntry(ushort Tag, ushort Type, uint Count, uint ValueField, byte[] Data,
        long EntryOffset)
    {
        public bool IsOutOfLine => Data.Length > 4;
    }

    internal static ushort U16(byte[] b, int i) => (ushort)(b[i] | (b[i + 1] << 8));

    internal static uint U32(byte[] b, int i) =>
        (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));

    internal static uint Be32(byte[] b, int i) =>
        (uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);

    internal static string Ascii(byte[] b) => Encoding.ASCII.GetString(b).TrimEnd('\0');

    internal static Ifd ParseIfd(byte[] file, uint offset)
    {
        var ifd = new Ifd();
        int count = U16(file, (int)offset);
        for (int i = 0; i < count; i++)
        {
            long p = offset + 2 + i * 12L;
            ushort tag = U16(file, (int)p);
            ushort type = U16(file, (int)p + 2);
            uint cnt = U32(file, (int)p + 4);
            uint val = U32(file, (int)p + 8);
            int typeSize = type switch
            {
                1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4,
                5 or 10 or 12 => 8, _ => 1,
            };
            int size = typeSize * (int)cnt;
            var data = new byte[Math.Max(size, 0)];
            if (size <= 4)
                for (int j = 0; j < size; j++) data[j] = (byte)(val >> (8 * j));
            else
                Array.Copy(file, val, data, 0, size);
            ifd.Tags[tag] = (type, cnt, data);
        }
        return ifd;
    }

    /// <summary>Ordered IFD entry walk (spec-invariant checks need the on-disk
    /// order and the raw value-offset word, which <see cref="ParseIfd">'s tag
    /// dictionary discards). Also returns the next-IFD word in
    /// <paramref name="nextIfdOffset"/> so callers can pin single-rooted chains.</summary>
    internal static List<IfdEntry> ParseIfdEntries(byte[] file, uint offset, out uint nextIfdOffset)
    {
        var entries = new List<IfdEntry>();
        int count = U16(file, (int)offset);
        for (int i = 0; i < count; i++)
        {
            long p = offset + 2 + i * 12L;
            ushort tag = U16(file, (int)p);
            ushort type = U16(file, (int)p + 2);
            uint cnt = U32(file, (int)p + 4);
            uint val = U32(file, (int)p + 8);
            int typeSize = type switch
            {
                1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4,
                5 or 10 or 12 => 8, _ => 1,
            };
            long size = (long)typeSize * cnt;
            var data = new byte[Math.Max(size, 0)];
            if (size <= 4)
                for (int j = 0; j < size; j++) data[j] = (byte)(val >> (8 * j));
            else
                Array.Copy(file, val, data, 0, size);
            entries.Add(new IfdEntry(tag, type, cnt, val, data, p));
        }
        nextIfdOffset = U32(file, (int)(offset + 2 + count * 12L));
        return entries;
    }

    /// <summary>Counts a JPEG preview IFD (JPEGInterchangeFormat tags 513/514 pointing
    /// at a SOI marker) into <paramref name="count"/>.</summary>
    internal static void CountJpegPreviews(byte[] file, Ifd ifd, ref int count)
    {
        if (!ifd.Tags.TryGetValue(513, out var jif) || !ifd.Tags.TryGetValue(514, out var jlen))
            return;
        uint off = U32(jif.Value, 0), len = U32(jlen.Value, 0);
        if (len < 4 || off + len > file.Length) return;
        if (file[off] == 0xFF && file[off + 1] == 0xD8) count++;
    }
}
