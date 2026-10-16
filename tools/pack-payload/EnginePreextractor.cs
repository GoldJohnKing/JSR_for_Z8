using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Jsr4Z8.Tools;

/// <summary>
/// PyInstaller CArchive 预解包器（仅打包期使用——刻意不进 Jsr4Z8.Core：产品运行时从不解析 CArchive）。
/// 逐逻辑移植实验验证过的 Python 解包器
/// （.superpowers/sdd/2026-10-16-meipass2-experiment/ws/extract_carchive.py；证据
/// docs/superpowers/notes/2026-10-16-meipass2-experiment.md §2——真档案对账 1,416 个 b 文件 /
/// 386,096,731 B，与引导器自解压目录逐文件 sha256 相同）。格式事实：
/// - MEI cookie（魔数 "MEI\x0c\x0b\x0a\x0b\x0e"）位于文件末 8 KB 内，取最后一次出现；
/// - cookie 大端字段 magic(8) pkgLen(u32) tocOff(u32) tocLen(u32) pyver(u32) pylib(64)＝88 B；
///   archiveStart = cookie 末 − pkgLen；TOC 必须恰止于 cookie 起点（自洽校验）；
/// - TOC 条目 18 B 大端头 structLen(u32) pos(u32) csize(u32) usize(u32) cmprsFlag(u8) type(1B)
///   + NUL 结尾名（Windows 构建名内分隔符 '\'，'/' 兼容采纳）；条目按 structLen 步进，
///   走完必须恰好落在 TOC 末；
/// - 仅 'b'（binary）条目解包落盘——m/s/z/o 留在 exe 内由引导器内存读取；cmprsFlag=1 时
///   zlib（RFC 1950）解压，解压输出长度必须等于 TOC usize；
/// - 目标布局 = 实验验证的运行时契约（计划 docs/superpowers/plans/2026-10-16-preextract-payload.md）：
///   b 文件按 TOC 名相对路径落盘，外加 onefile exe 本体逐字节拷贝进树根——运行时
///   _PYI_ARCHIVE_FILE=&lt;树&gt;\jsr_backend.exe 直跑（bootloader 从自身文件读 CArchive）。
/// </summary>
public static class EnginePreextractor
{
    private static readonly byte[] Magic = { (byte)'M', (byte)'E', (byte)'I', 0x0C, 0x0B, 0x0A, 0x0B, 0x0E };

    private const int CookieSize = 88;         // magic 8 + pkgLen/tocOff/tocLen/pyver 4×u32 + pylib 64
    private const int TocEntryHeaderSize = 18; // structLen/pos/csize/usize 4×u32 + cmprsFlag 1 + type 1
    private const int CookieSearchTail = 8192; // cookie 只在文件末 8 KB 内扫描（实验脚本同款）

    /// <summary>
    /// 解包 <paramref name="onefileExe"/>（PyInstaller onefile exe）内嵌 CArchive 的全部 'b' 条目到
    /// <paramref name="destDir"/>（TOC 名即相对路径，含子目录），并把 exe 本体逐字节拷贝到
    /// destDir 根。返回解包的 b 条目数。同输入重跑得到逐字节相同的结果（确定性）。
    /// </summary>
    public static int Extract(string onefileExe, string destDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(onefileExe);
        ArgumentException.ThrowIfNullOrWhiteSpace(destDir);
        byte[] data = File.ReadAllBytes(onefileExe);

        // 1) cookie 定位：末 8 KB 内最后一次出现（实验脚本 tail.rfind(MAGIC)）。
        int tailLength = (int)Math.Min(CookieSearchTail, data.Length);
        int tailStart = data.Length - tailLength;
        int cookieRelative = data.AsSpan(tailStart, tailLength).LastIndexOf(Magic);
        if (cookieRelative < 0)
            throw new InvalidDataException($"EOF 前 8 KB 未找到 MEI cookie（非 PyInstaller onefile?）: {onefileExe}");
        long cookieOffset = tailStart + cookieRelative;
        if (cookieOffset + CookieSize > data.Length)
            throw new InvalidDataException($"MEI cookie 被截断（短于 {CookieSize} B 结构）: {onefileExe}");

        // 2) cookie 大端字段 → archive 起点；TOC 必须恰止于 cookie（自洽校验）。
        uint pkgLength = ReadUInt32BE(data, cookieOffset + 8);
        uint tocOffset = ReadUInt32BE(data, cookieOffset + 12);
        uint tocLength = ReadUInt32BE(data, cookieOffset + 16);
        // pyver（+20）与 pylib（+24..88）仅信息性，不参与解包决策；cookie 长度已由 CookieSize 覆盖。
        long archiveStart = cookieOffset + CookieSize - pkgLength;
        long tocStart = archiveStart + tocOffset;
        long tocEnd = tocStart + tocLength;
        if (archiveStart < 0)
            throw new InvalidDataException($"CArchive pkgLength 异常: {pkgLength}");
        if (tocEnd != cookieOffset)
            throw new InvalidDataException($"TOC 自洽校验失败: TOC 末 0x{tocEnd:X} 应恰为 cookie 起点 0x{cookieOffset:X}");

        // 3) TOC 解析：18 B 大端头 + NUL 结尾名，structLen 步进；仅收集 'b' 条目。
        var binaries = new List<(long Offset, long CompressedLength, uint UncompressedLength, bool Compressed, string Name)>();
        long cursor = tocStart;
        while (cursor < tocEnd)
        {
            if (cursor + TocEntryHeaderSize > tocEnd)
                throw new InvalidDataException($"TOC 条目头越界 @0x{cursor:X}");
            uint entryLength = ReadUInt32BE(data, cursor);
            uint entryOffset = ReadUInt32BE(data, cursor + 4);
            uint entryCompressedSize = ReadUInt32BE(data, cursor + 8);
            uint entryUncompressedSize = ReadUInt32BE(data, cursor + 12);
            byte compressedFlag = data[(int)cursor + 16];
            char type = (char)data[(int)cursor + 17];
            if (entryLength < TocEntryHeaderSize || cursor + entryLength > tocEnd)
                throw new InvalidDataException($"TOC 条目 structLen 异常: {entryLength} @0x{cursor:X}");
            string name = DecodeName(data, cursor + TocEntryHeaderSize, cursor + entryLength);
            if (type == 'b')
                binaries.Add((entryOffset, entryCompressedSize, entryUncompressedSize, compressedFlag != 0, name));
            cursor += entryLength; // 头 + 名 + 对齐填充；越界由下一轮守卫兜住（循环退出即恰好 tocEnd）
        }

        // 4) b 条目落盘：数据窗口 [archiveStart+pos, +csize)，zlib 或原样，长度必须等于 usize。
        Directory.CreateDirectory(destDir);
        foreach ((long offset, long compressedLength, uint uncompressedLength, bool compressed, string name) in binaries)
        {
            long source = archiveStart + offset;
            if (source + compressedLength > cookieOffset)
                throw new InvalidDataException($"b 条目数据窗口越界: {name}");
            byte[] stored = data[(int)source..(int)(source + compressedLength)];
            byte[] content = compressed ? Inflate(stored, uncompressedLength, name) : stored;
            if (content.Length != uncompressedLength)
                throw new InvalidDataException($"b 条目长度与 TOC usize 不符: {name} ({content.Length} != {uncompressedLength})");
            string destination = Path.Combine([destDir, .. name.Split('\\', '/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, content);
        }

        // 5) 引擎 exe 本体逐字节拷贝进树根（实验配方：源 onefile 原样即引导器读取的 CArchive 宿主，
        //    运行时契约 _PYI_ARCHIVE_FILE=<树>\jsr_backend.exe）。
        File.Copy(onefileExe, Path.Combine(destDir, Path.GetFileName(onefileExe)), overwrite: true);
        return binaries.Count;
    }

    /// <summary>zlib（RFC 1950）解压，输出必须恰好 <paramref name="uncompressedLength"/> 字节——不足/超长都是档案损坏。</summary>
    private static byte[] Inflate(byte[] stored, uint uncompressedLength, string name)
    {
        if (uncompressedLength > Array.MaxLength)
            throw new InvalidDataException($"b 条目 usize 异常: {name} ({uncompressedLength})");
        using var zlib = new ZLibStream(new MemoryStream(stored), CompressionMode.Decompress);
        byte[] content = new byte[uncompressedLength];
        int total = 0;
        while (total < content.Length)
        {
            int read = zlib.Read(content, total, content.Length - total);
            if (read == 0)
                break;
            total += read;
        }
        Span<byte> probe = stackalloc byte[1];
        if (total != content.Length || zlib.Read(probe) != 0)
            throw new InvalidDataException($"b 条目 zlib 解压长度与 usize 不符: {name}");
        return content;
    }

    private static uint ReadUInt32BE(byte[] data, long offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)offset));

    /// <summary>TOC 条目名：[headerEnd, entryEnd) 内首个 NUL 前的字节，UTF-8 解码（无效序列 U+FFFD 替换——实验脚本同款）。</summary>
    private static string DecodeName(byte[] data, long from, long to)
    {
        ReadOnlySpan<byte> raw = data.AsSpan((int)from, (int)(to - from));
        int nul = raw.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul >= 0 ? raw[..nul] : raw);
    }
}
