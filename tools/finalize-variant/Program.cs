// tools/finalize-variant — ACR-compatibility bisection harness (post-release
// fix 3): runs the product finalizer (JsrDngFinalizer) on a real backend DNG
// with a chosen raw storage layout and prints a structural summary of the
// written file, so the two variants (S1 strips / S2 tiles) can be diffed and
// ACR-tested. Not part of the solution/UI chain.
//
// Usage: finalize-variant <backendDng> <referenceNef> <outDir> <strips|tiles>

using System.Globalization;
using Jsr4Z8.Core.Dng;

if (args.Length != 4)
{
    Console.Error.WriteLine("usage: finalize-variant <backendDng> <referenceNef> <outDir> <strips|tiles>");
    return 2;
}

RawLayout? layout = args[3].ToLowerInvariant() switch
{
    "strips" => RawLayout.Strips,
    "tiles" => RawLayout.Tiles,
    _ => null,
};
if (layout is null)
{
    Console.Error.WriteLine($"unknown layout '{args[3]}' (expected strips|tiles)");
    return 2;
}

Console.WriteLine($"backend:  {args[0]}");
Console.WriteLine($"ref NEF:  {args[1]}");
Console.WriteLine($"layout:   {layout.Value}");

string output = JsrDngFinalizer.Finalize(args[0], args[1], args[2], layout: layout.Value);
Console.WriteLine($"output:   {output}");
Console.WriteLine($"size:     {new FileInfo(output).Length.ToString("N0", CultureInfo.InvariantCulture)} bytes");

Summarize(output);
return 0;

/// <summary>Random-access TIFF walk of the raw SubIFD (IFD0 → SubIFDs[0]):
/// geometry tags + strip (273/278/279) or tile (322-325) inventory.</summary>
static void Summarize(string path)
{
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    ushort U16(long o) { fs.Position = o; return (ushort)(fs.ReadByte() | (fs.ReadByte() << 8)); }
    uint U32(long o) { fs.Position = o; return (uint)(fs.ReadByte() | (fs.ReadByte() << 8)
        | (fs.ReadByte() << 16) | (fs.ReadByte() << 24)); }

    Dictionary<ushort, (ushort Type, int Cnt, uint Val)> ParseIfd(long offset)
    {
        var tags = new Dictionary<ushort, (ushort, int, uint)>();
        int count = U16(offset);
        for (int i = 0; i < count; i++)
        {
            long e = offset + 2 + i * 12;
            tags[U16(e)] = (U16(e + 2), (int)U32(e + 4), U32(e + 8));
        }
        return tags;
    }

    // First value of a LONG array entry: inline when count=1, else blob[0].
    uint First((ushort Type, int Cnt, uint Val) e) => e.Cnt == 1 ? e.Val : U32(e.Val);

    // Sum of a LONG array entry (byte counts): inline when count=1, else blob.
    long SumLongs((ushort Type, int Cnt, uint Val) e)
    {
        if (e.Cnt == 1) return e.Val;
        long total = 0;
        for (int i = 0; i < e.Cnt; i++) total += U32(e.Val);
        return total;
    }

    if (U16(0) != 0x4949 || U16(2) != 42)
    {
        Console.WriteLine("summary:  not a little-endian TIFF?!");
        return;
    }

    var raw = ParseIfd(U32(4));
    if (raw.TryGetValue(330, out var sub))              // SubIFDs: raw first
        raw = ParseIfd(First(sub));

    // Scalar display value of an entry: SHORTs are packed two-per-u32 inline,
    // arrays larger than 4 bytes live in a blob at the value-field offset.
    uint U(ushort tag)
    {
        if (!raw.TryGetValue(tag, out var e)) return 0;
        if (e.Type == 3)
            return e.Cnt == 1 ? e.Val & 0xFFFF : U16(e.Val);
        return e.Cnt == 1 ? e.Val : U32(e.Val);
    }

    Console.WriteLine($"raw IFD:  {U(256)}x{U(257)}  compression={U(259)}  photometric={U(262)}  " +
        $"samples={U(277)}  bps={U(258)}");

    void SoiAt(long offset)
    {
        fs.Position = offset;
        Console.WriteLine($"stream0:  SOI={fs.ReadByte():X2} {fs.ReadByte():X2}");
    }

    if (raw.TryGetValue(324, out var tileOff))          // TileOffsets → tiled layout
    {
        Console.WriteLine($"layout:   TILED  {U(322)}x{U(323)}  tiles={tileOff.Cnt}");
        Console.WriteLine($"payload:  {SumLongs(raw[325]).ToString("N0", CultureInfo.InvariantCulture)} bytes of LJ tile streams");
        SoiAt(First(tileOff));
    }
    else if (raw.TryGetValue(273, out var stripOff))    // StripOffsets → strip layout
    {
        uint rps = U(278), h = U(257);
        long lastRows = stripOff.Cnt > 0 ? h - (long)(stripOff.Cnt - 1) * rps : 0;
        Console.WriteLine($"layout:   STRIPS RowsPerStrip={rps}  strips={stripOff.Cnt}  lastStripRows={lastRows}");
        Console.WriteLine($"payload:  {SumLongs(raw[279]).ToString("N0", CultureInfo.InvariantCulture)} bytes of LJ strip streams");
        SoiAt(First(stripOff));
    }
    else
    {
        Console.WriteLine("layout:   neither strip nor tile tags found");
    }
}
