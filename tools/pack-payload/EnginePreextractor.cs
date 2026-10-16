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
///   b 文件按 TOC 名相对路径落盘，外加 onefile exe 本体拷贝进树根——拷贝随即施加薄档案变换
///   （b 条目体区间清零，评估 docs/superpowers/notes/2026-10-16-thin-archive-probe.md F1 形态；
///   源 onefile 永不改动）——运行时 _PYI_ARCHIVE_FILE=&lt;树&gt;\jsr_backend.exe 直跑
///   （bootloader 从自身文件读 CArchive）。
/// </summary>
public static class EnginePreextractor
{
    private static readonly byte[] Magic = { (byte)'M', (byte)'E', (byte)'I', 0x0C, 0x0B, 0x0A, 0x0B, 0x0E };

    private const int CookieSize = 88;         // magic 8 + pkgLen/tocOff/tocLen/pyver 4×u32 + pylib 64
    private const int TocEntryHeaderSize = 18; // structLen/pos/csize/usize 4×u32 + cmprsFlag 1 + type 1
    private const int CookieSearchTail = 8192; // cookie 只在文件末 8 KB 内扫描（实验脚本同款）

    /// <summary>TOC 'b' 条目：体窗口定位（pos/csize）+ 解包（usize/cmprsFlag/名）所需字段。</summary>
    private sealed record BinaryEntry(long Offset, long CompressedLength, uint UncompressedLength, bool Compressed, string Name);

    /// <summary>
    /// 解包 <paramref name="onefileExe"/>（PyInstaller onefile exe）内嵌 CArchive 的全部 'b' 条目到
    /// <paramref name="destDir"/>（TOC 名即相对路径，含子目录），把 exe 本体拷贝到 destDir 根并随即
    /// 施加薄档案变换（见 <see cref="ThinTransform"/>；源 onefile 永不改动）。返回解包的 b 条目数。
    /// 同输入重跑得到逐字节相同的结果（确定性）。
    /// </summary>
    public static int Extract(string onefileExe, string destDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(onefileExe);
        ArgumentException.ThrowIfNullOrWhiteSpace(destDir);
        byte[] data = File.ReadAllBytes(onefileExe);
        (long archiveStart, _, long cookieOffset, List<BinaryEntry> binaries) =
            ParseArchive(onefileExe, data.Length, (offset, length) => data[(int)offset..(int)(offset + length)]);

        // b 条目落盘：数据窗口 [archiveStart+pos, +csize)，zlib 或原样，长度必须等于 usize
        // （从原档内存数据落盘——薄变换只作用于稍后的树内拷贝，不影响落盘内容）。
        Directory.CreateDirectory(destDir);
        foreach (BinaryEntry entry in binaries)
        {
            long source = archiveStart + entry.Offset;
            if (source + entry.CompressedLength > cookieOffset)
                throw new InvalidDataException($"b 条目数据窗口越界: {entry.Name}");
            byte[] stored = data[(int)source..(int)(source + entry.CompressedLength)];
            byte[] content = entry.Compressed ? Inflate(stored, entry.UncompressedLength, entry.Name) : stored;
            if (content.Length != entry.UncompressedLength)
                throw new InvalidDataException($"b 条目长度与 TOC usize 不符: {entry.Name} ({content.Length} != {entry.UncompressedLength})");
            string destination = Path.Combine([destDir, .. entry.Name.Split('\\', '/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, content);
        }

        // 引擎 exe 本体拷贝进树根，随即施加薄档案变换（只动树内拷贝——源 onefile 永不改动；
        // 运行契约 _PYI_ARCHIVE_FILE=<树>\jsr_backend.exe 直跑，子模式下 b 体从不被读）。
        string stagedExe = Path.Combine(destDir, Path.GetFileName(onefileExe));
        File.Copy(onefileExe, stagedExe, overwrite: true);
        ThinTransform(stagedExe);
        return binaries.Count;
    }

    /// <summary>
    /// 薄档案变换（评估 docs/superpowers/notes/2026-10-16-thin-archive-probe.md F1 形态）：
    /// 就地把 exe 内全部 'b' 条目体区间 [archiveStart+pos, +csize) 覆写为零——z/m/s/o 体、
    /// stub 头、TOC、cookie 逐字节原样，文件长度不变（所有 TOC 偏移保持有效）。子模式
    /// （_PYI_* 三元组自指）下引导器从档案文件读取的活体集不含 b 体（F1 冒烟 + E2 完整跑双实证），
    /// 故薄 exe 仅在三变量形态 spawn 下成立（评估机制事实 A）。Extract 在树内 exe 拷贝后即调用本变换；
    /// 同输入同输出字节（覆写内容恒为零，重复施加幂等）。
    /// </summary>
    public static void ThinTransform(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        using FileStream stream = File.Open(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        (long archiveStart, long tocStart, _, List<BinaryEntry> binaries) =
            ParseArchive(exePath, stream.Length, (offset, length) => ReadRange(stream, offset, length));

        // 流式覆写：按 TOC 区间 seek→写零；窗口严格限于数据区 [archiveStart, tocStart)——
        // 越界侵入 TOC/cookie 即档案损坏（拒绝而非静默越写）。
        byte[] zeros = new byte[64 * 1024];
        foreach (BinaryEntry entry in binaries)
        {
            long source = archiveStart + entry.Offset;
            if (source + entry.CompressedLength > tocStart)
                throw new InvalidDataException($"b 条目体区间越界（将侵入 TOC）: {entry.Name}");
            stream.Seek(source, SeekOrigin.Begin);
            long remaining = entry.CompressedLength;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(remaining, zeros.Length);
                stream.Write(zeros, 0, chunk);
                remaining -= chunk;
            }
        }
    }

    /// <summary>
    /// cookie + TOC 解析（Extract 与 ThinTransform 共用同一实现）：末 8 KB 定位 MEI cookie →
    /// 大端字段 → archiveStart 与 TOC 自洽校验 → 条目 structLen 步进收集全部 'b' 条目。
    /// <paramref name="readRange"/> 按需取字节区间——整档已入内存时切片，流式时按需 seek 读
    /// （薄变换只读末 8 KB + TOC 窗，不整档载入）。
    /// </summary>
    private static (long ArchiveStart, long TocStart, long CookieOffset, List<BinaryEntry> Binaries)
        ParseArchive(string path, long fileLength, Func<long, long, byte[]> readRange)
    {
        // 1) cookie 定位：末 8 KB 内最后一次出现（实验脚本 tail.rfind(MAGIC)）。
        long tailLength = Math.Min(CookieSearchTail, fileLength);
        long tailStart = fileLength - tailLength;
        byte[] tail = readRange(tailStart, tailLength);
        int cookieRelative = tail.AsSpan().LastIndexOf(Magic);
        if (cookieRelative < 0)
            throw new InvalidDataException($"EOF 前 8 KB 未找到 MEI cookie（非 PyInstaller onefile?）: {path}");
        long cookieOffset = tailStart + cookieRelative;
        if (cookieOffset + CookieSize > fileLength)
            throw new InvalidDataException($"MEI cookie 被截断（短于 {CookieSize} B 结构）: {path}");

        // 2) cookie 大端字段 → archive 起点；TOC 必须恰止于 cookie（自洽校验）。
        uint pkgLength = ReadUInt32BE(tail, cookieRelative + 8);
        uint tocOffset = ReadUInt32BE(tail, cookieRelative + 12);
        uint tocLength = ReadUInt32BE(tail, cookieRelative + 16);
        // pyver（+20）与 pylib（+24..88）仅信息性，不参与解包决策；cookie 长度已由 CookieSize 覆盖。
        long archiveStart = cookieOffset + CookieSize - pkgLength;
        long tocStart = archiveStart + tocOffset;
        long tocEnd = tocStart + tocLength;
        if (archiveStart < 0)
            throw new InvalidDataException($"CArchive pkgLength 异常: {pkgLength}");
        if (tocEnd != cookieOffset)
            throw new InvalidDataException($"TOC 自洽校验失败: TOC 末 0x{tocEnd:X} 应恰为 cookie 起点 0x{cookieOffset:X}");

        // 3) TOC 解析：18 B 大端头 + NUL 结尾名，structLen 步进；仅收集 'b' 条目。
        byte[] toc = readRange(tocStart, tocLength);
        var binaries = new List<BinaryEntry>();
        long cursor = 0; // 相对 TOC 起点（诊断消息换算回绝对偏移）
        while (cursor < tocLength)
        {
            if (cursor + TocEntryHeaderSize > tocLength)
                throw new InvalidDataException($"TOC 条目头越界 @0x{tocStart + cursor:X}");
            uint entryLength = ReadUInt32BE(toc, cursor);
            uint entryOffset = ReadUInt32BE(toc, cursor + 4);
            uint entryCompressedSize = ReadUInt32BE(toc, cursor + 8);
            uint entryUncompressedSize = ReadUInt32BE(toc, cursor + 12);
            byte compressedFlag = toc[(int)cursor + 16];
            char type = (char)toc[(int)cursor + 17];
            if (entryLength < TocEntryHeaderSize || cursor + entryLength > tocLength)
                throw new InvalidDataException($"TOC 条目 structLen 异常: {entryLength} @0x{tocStart + cursor:X}");
            string name = DecodeName(toc, cursor + TocEntryHeaderSize, cursor + entryLength);
            if (type == 'b')
                binaries.Add(new(entryOffset, entryCompressedSize, entryUncompressedSize, compressedFlag != 0, name));
            cursor += entryLength; // 头 + 名 + 对齐填充；越界由下一轮守卫兜住（循环退出即恰好 tocEnd）
        }
        return (archiveStart, tocStart, cookieOffset, binaries);
    }

    /// <summary>流式取区间：seek 到 <paramref name="offset"/> 读满 <paramref name="length"/> 字节（短读即档案损坏）。</summary>
    private static byte[] ReadRange(FileStream stream, long offset, long length)
    {
        byte[] buffer = new byte[length];
        stream.Seek(offset, SeekOrigin.Begin);
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
                throw new EndOfStreamException($"CArchive 区间读取越界 @0x{offset:X}+{length}");
            total += read;
        }
        return buffer;
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
